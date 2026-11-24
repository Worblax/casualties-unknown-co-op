using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

internal static class ItemChecks
{
    private const ulong HostAddress = 1;
    private const ulong First = 2;
    private const ulong Second = 3;
    private static readonly Guid Session = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid OtherSession = new("10213243-5465-7687-98a9-bacbdcedfe0f");

    internal static void Run()
    {
        Race(First);
        Race(Second);
        RemovalAndLateFrames();
        CapacityAfterChurn();
        SnapshotBudget();
    }

    private static void Race(ulong firstArrival)
    {
        using var scenario = new Scenario(firstArrival);
        Check.That(scenario.Start(First, ItemAction.PickUp) && scenario.Start(Second, ItemAction.PickUp),
            "Both contenders send the same revision.");
        scenario.Network.Advance(1);

        ulong loser = firstArrival == First ? Second : First;
        var winner = scenario.Peers[firstArrival].LocalParticipant;
        Check.That(scenario.Host.TryGet(scenario.Id, out ItemState state) && state.Owner == winner && state.Revision == 2 &&
            scenario.World.Pickups == 1, "Two contenders yield one native pickup, one owner and one committed revision.");
        Check.That(scenario.Results[firstArrival].Single() == ItemDecision.Accepted &&
            scenario.Results[loser].Single() == ItemDecision.Unavailable &&
            scenario.Guests.Values.All(guest => guest.Replicas.TryGet(scenario.Id, out var copy) &&
                copy.Owner == winner && copy.Revision == 2), "Both guests converge on the host's only owner.");

        Check.That(scenario.Start(firstArrival, ItemAction.Throw), "The winner can throw the item.");
        scenario.Network.Advance(1);
        Check.That(scenario.Host.TryGet(scenario.Id, out state) && state.Owner.IsNone &&
            state.Slot == ItemWire.NoSlot && state.Revision == 3, "Throw returns that identity to the world.");

        Check.That(scenario.Start(loser, ItemAction.PickUp), "The other guest can pick up the same item.");
        scenario.Network.Advance(1);
        Check.That(scenario.Host.TryGet(scenario.Id, out state) && state.Owner == scenario.Peers[loser].LocalParticipant &&
            state.Revision == 4, "The follow-up pickup preserves identity and advances the revision.");
    }

    private static void RemovalAndLateFrames()
    {
        using var scenario = new Scenario(First);
        Check.That(scenario.Host.TryGet(scenario.Id, out ItemState item), "The registry contains the world item.");
        SharedItemState live = item.ToWire(scenario.HostPeers);
        SharedItemState unknown = live;
        unknown.Id += 1;
        scenario.Send(MessageKind.ItemMotion, unknown);
        scenario.Network.Advance(0);
        Check.That(scenario.Guests.Values.All(guest => !guest.Replicas.TryGet(unknown.Id, out _)),
            "Motion without a baseline cannot create an item.");

        scenario.Host.Remove(scenario.Id);
        scenario.Send(MessageKind.ItemState, live);
        scenario.Send(MessageKind.ItemMotion, live);
        scenario.HostPeers.Broadcast(ItemSnapshotWire.WriteState(MessageKind.ItemDelta, Session, 1, live), reliable: true);
        live.Revision++;
        scenario.Send(MessageKind.ItemState, live);
        scenario.Network.Advance(0);
        Check.That(!scenario.Host.TryGet(scenario.Id, out _) && scenario.Guests.Values.All(guest => !guest.Replicas.TryGet(scenario.Id, out _)),
            "Compacted removals stay absent after delayed samples and deltas.");
    }

    private static void CapacityAfterChurn()
    {
        using var scenario = new Scenario(First);
        scenario.Network.SetProfile(new NetworkProfile(QueueLimit: 16384));
        ulong lastId = scenario.Id;
        for (int index = 0; index < 600; index++)
        {
            Check.That(scenario.Host.TryRegister("rock", default, 1, out ulong id) && id > lastId, "Retirement reclaims capacity without reusing IDs.");
            lastId = id;
            scenario.Host.Remove(id);
            Check.That(!scenario.Host.TryGet(id, out _), "Retired IDs are absent from the authoritative registry.");
            scenario.Network.Advance(0);
        }
        Check.That(scenario.Guests.Values.All(guest => guest.Replicas.States.Count == 1 &&
            guest.Replicas.TryGet(scenario.Id, out _)), "A low live ID survives churn without accumulating client tombstones.");

        for (int index = 1; index < AuthoritativeItemRegistry.Limit; index++)
        {
            Check.That(scenario.Host.TryRegister("rock", default, 1, out _), "All live slots are usable after churn.");
        }
        Check.That(!scenario.Host.TryRegister("rock", default, 1, out _), "The cap still bounds active items.");
        scenario.Host.Remove(scenario.Id);
        Check.That(scenario.Host.TryRegister("stick", default, 1, out _), "Retirement frees an active slot immediately.");
        while (scenario.Network.Queued > 0) scenario.Network.Advance(0);
        Check.That(scenario.Guests.Values.All(guest => guest.Replicas.States.Count == AuthoritativeItemRegistry.Limit),
            "Replica capacity reflects active items only.");
    }

    private static void SnapshotBudget()
    {
        using var scenario = new Scenario(First, additionalItems: 24, finishInitialSnapshot: false);
        Check.That(scenario.SnapshotFrames.Values.All(count => count == 0), "Receiving a request does not transmit outside the snapshot tick.");
        for (int tick = 0; tick < 5; tick++)
        {
            scenario.Network.Advance(3);
            foreach (GuestItemSession guest in scenario.Guests.Values)
            {
                guest.Tick();
            }
            scenario.Network.Advance(0);
            int first = scenario.SnapshotFrames[First];
            int second = scenario.SnapshotFrames[Second];
            scenario.Host.TickSnapshots();
            scenario.Network.Advance(0);
            int firstSent = scenario.SnapshotFrames[First] - first;
            int secondSent = scenario.SnapshotFrames[Second] - second;
            Check.That(firstSent > 0 && secondSent > 0 && Math.Abs(firstSent - secondSent) <= 1 &&
                firstSent + secondSent <= HostItemSnapshots.SendBudget, "One global budget is shared fairly by both active guests.");
        }
        Check.That(scenario.Guests.Values.All(guest => guest.Synced && guest.Replicas.States.Count == 25),
            "Active progress allows both transfers to complete after more than one inactivity timeout.");
    }

    private sealed class Scenario : IDisposable
    {
        internal SimulatedNetwork Network { get; }
        internal SessionPeers HostPeers { get; }
        internal HostItemSession Host { get; }
        internal HostItemChecks.ItemWorld World { get; } = new();
        internal readonly Dictionary<ulong, SessionPeers> Peers = new();
        internal readonly Dictionary<ulong, GuestItemSession> Guests = new();
        internal readonly Dictionary<ulong, List<ItemDecision>> Results = new();
        internal readonly Dictionary<ulong, int> SnapshotFrames = new();
        internal ulong Id { get; }

        internal Scenario(ulong firstArrival, int additionalItems = 0, bool finishInitialSnapshot = true)
        {
            Network = new SimulatedNetwork(42, script: frame => new NetworkFault(ExtraDelay:
                frame.Data.Span[0] == (byte)MessageKind.ItemRequest && frame.Sender != firstArrival ? 0.25f : 0));
            var plan = new RoomLaunchPlan(Session, OtherSession, "normal", RoomOptions.Sandbox,
                new[] { new LaunchPlayer(HostAddress, 0), new LaunchPlayer(First, 0), new LaunchPlayer(Second, 0) });
            HostPeers = new SessionPeers(Network.AddPeer(HostAddress), plan, HostAddress, HostAddress);
            Host = new HostItemSession(HostPeers, World, Network.Clock);
            HostPeers.AttachReceiver((sender, frame) => Host.Receive(sender, frame, worldReady: true));
            foreach (ulong address in new[] { First, Second })
            {
                var peers = new SessionPeers(Network.AddPeer(address), plan, address, HostAddress);
                var guest = new GuestItemSession(peers, Network.Clock);
                var results = new List<ItemDecision>();
                guest.ResultAccepted += (_, result) => results.Add(result);
                SnapshotFrames.Add(address, 0);
                peers.AttachReceiver((sender, frame) =>
                {
                    if ((MessageKind)frame[0] is MessageKind.ItemSnapshotEntry or MessageKind.ItemSyncDone)
                    {
                        SnapshotFrames[address]++;
                    }
                    return guest.Receive(sender, frame);
                });
                Peers.Add(address, peers);
                Guests.Add(address, guest);
                Results.Add(address, results);
                Network.SetConnected(HostAddress, address, true);
            }
            Host.TryRegister("stick", default, 1, out ulong id);
            Id = id;
            for (int index = 0; index < additionalItems; index++)
            {
                Host.TryRegister("rock", default, 1, out _);
            }
            foreach (GuestItemSession guest in Guests.Values)
            {
                guest.Tick();
            }
            Network.Advance(0);
            if (finishInitialSnapshot)
            {
                Host.TickSnapshots();
                Network.Advance(0);
            }
        }

        internal bool Start(ulong player, ItemAction action) => Guests[player].TrySubmit(Id, action, 0, default);

        internal void Send(MessageKind kind, SharedItemState state) =>
            HostPeers.Broadcast(ItemWire.WriteState(kind, Session, state), kind != MessageKind.ItemMotion);

        public void Dispose()
        {
            foreach (GuestItemSession guest in Guests.Values)
            {
                guest.Dispose();
            }
            foreach (SessionPeers peers in Peers.Values)
            {
                peers.Dispose();
            }
            Host.Dispose();
            HostPeers.Dispose();
        }
    }
}
