using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

internal static class GuestItemChecks
{
    private const ulong HostAddress = 472;
    private const ulong GuestAddress = 513;
    private static readonly Guid Session = new("efaaa909-91a2-4582-99db-e885ec83714f");
    private static readonly Guid PreviousSession = new("80304b13-30f6-471a-ae93-e31c748bc484");

    internal static void Run()
    {
        SnapshotWaitsAndResumes();
        SnapshotDeadlineAndStaleCompletion();
        IncompleteManifestRetries();
        GapRecoversFromSnapshot();
        OnePendingOperation();
    }

    private static void SnapshotWaitsAndResumes()
    {
        using var scenario = new Scenario();
        scenario.WorldReady = false;
        scenario.HostPeers.Send(GuestAddress, Done(Session));
        scenario.Network.Advance(0);
        Check.That(!scenario.Guest.Synced, "An unsolicited completion does not satisfy guest bootstrap.");

        scenario.Guest.Tick();
        scenario.Network.Advance(0);
        scenario.Network.Advance(3);
        scenario.Guest.Tick();
        scenario.Network.Advance(0);
        Check.That(scenario.SnapshotRequests == 1 && !scenario.Guest.Synced, "Only one accepted snapshot request is outstanding during load.");

        scenario.WorldReady = true;
        scenario.Refuse = frame => frame[0] == (byte)MessageKind.ItemSnapshotEntry && ++scenario.ItemSendAttempts > 1;
        scenario.Host.TickSnapshots();
        scenario.Network.Advance(0);
        Check.That(scenario.Guest.Replicas.States.Count == 0 && !scenario.Guest.Synced,
            "A partial snapshot does not make the guest ready.");

        // The second snapshot state is still pending. Publish a newer revision before it can resume.
        scenario.Refuse = frame => frame[0] == (byte)MessageKind.ItemSyncDone;
        scenario.Host.Remove(scenario.Second);
        scenario.Network.Advance(0);
        scenario.Host.TickSnapshots();
        scenario.Network.Advance(0);
        Check.That(!scenario.Guest.Replicas.TryGet(scenario.Second, out _) && !scenario.Guest.Synced,
            "Snapshot entries stay staged while the completion is refused.");

        scenario.Refuse = null;
        scenario.HostPeers.Send(GuestAddress, Done(PreviousSession));
        scenario.Network.Advance(0);
        Check.That(!scenario.Guest.Synced, "A completion from a previous session is ignored.");
        scenario.Host.TickSnapshots();
        scenario.Network.Advance(0);
        Check.That(scenario.Guest.Synced && scenario.Guest.Replicas.States.Count == 1 &&
            !scenario.Guest.Replicas.TryGet(scenario.Second, out _) && scenario.SnapshotRequests == 1,
            "The same request completes after backpressure, with one retained state per item.");

        scenario.HostPeers.Send(GuestAddress, Done(Session));
        scenario.Network.SetConnected(HostAddress, GuestAddress, false);
        scenario.Network.SetConnected(HostAddress, GuestAddress, true);
        scenario.Network.Advance(0);
        Check.That(!scenario.Guest.Synced, "The previous verified connection cannot complete the next snapshot.");
        scenario.Guest.ResumeAfterOffer(9);
        scenario.Sync();
        Check.That(scenario.SnapshotRequests == 2 && scenario.Guest.Synced, "Recovery starts its own snapshot after the offer.");
    }

    private static void OnePendingOperation()
    {
        using var scenario = new Scenario(delayRequests: true);
        scenario.Sync();
        Check.That(scenario.Guest.TrySubmit(scenario.First, ItemAction.PickUp, 0, default) && scenario.Guest.IsReserved(0),
            "Submitting reserves the pickup slot.");
        Check.That(!scenario.Guest.TrySubmit(scenario.Second, ItemAction.PickUp, 1, default),
            "Only one guest operation can be pending.");
        scenario.HostPeers.Send(GuestAddress, ItemWire.WriteResult(Session, 2, ItemDecision.Accepted));
        scenario.HostPeers.Send(GuestAddress, ItemWire.WriteResult(PreviousSession, 1, ItemDecision.Accepted));
        scenario.Network.Advance(0);
        Check.That(scenario.Guest.Pending is { Operation: 1 } && scenario.Results.Count == 0,
            "An unrelated operation or prior-session result cannot clear the current reservation.");

        scenario.Network.Advance(1);
        scenario.Guest.Tick();
        Check.That(scenario.OperationRequests.SequenceEqual(new[] { 1ul, 1ul }), "The same operation number is retried.");
        scenario.Network.Advance(3);
        Check.That(scenario.World.Pickups == 1 && scenario.Guest.Pending is null && scenario.Results.Count == 1 &&
            scenario.Guest.Replicas.TryGet(scenario.First, out var state) && state.Owner == scenario.GuestPeers.LocalParticipant &&
            state.Revision == 2, "Retries commit once and the matching result releases the reservation once.");

        scenario.Network.SetConnected(HostAddress, GuestAddress, false);
        scenario.Network.SetConnected(HostAddress, GuestAddress, true);
        scenario.Guest.ResumeAfterOffer(9);
        scenario.Sync();
        Check.That(scenario.Guest.TrySubmit(scenario.First, ItemAction.Drop, 0, default) &&
            scenario.Guest.Pending is { Operation: 10 }, "Recovery advances the operation number to the host's confirmed floor.");
    }

    private static void SnapshotDeadlineAndStaleCompletion()
    {
        using var scenario = new Scenario();
        scenario.Refuse = frame => frame[0] == (byte)MessageKind.ItemSyncDone;
        scenario.Sync();
        Check.That(scenario.SnapshotRequests == 1 && !scenario.Guest.Synced, "First transfer stays pending without its completion.");

        scenario.Network.Advance(ItemSnapshotSync.Timeout);
        scenario.Refuse = null;
        scenario.HostPeers.Send(GuestAddress, Done(Session, 1));
        scenario.Network.Advance(0);
        Check.That(!scenario.Guest.Synced, "An expired completion is rejected even before the next guest tick.");
        scenario.Guest.Tick();
        scenario.Network.Advance(0);
        Check.That(scenario.SnapshotRequests == 2, "The guest requests a new snapshot ID after its deadline.");

        scenario.Refuse = null;
        scenario.HostPeers.Send(GuestAddress, Done(Session, 1));
        scenario.Network.Advance(0);
        Check.That(!scenario.Guest.Synced, "The previous ID cannot complete a new request on the same connection.");
        scenario.Host.TickSnapshots();
        scenario.Network.Advance(0);
        Check.That(scenario.Guest.Synced && scenario.Guest.Replicas.States.Count == 2,
            "The new snapshot completes after the stalled transfer is superseded.");

        int replayFrames = 0;
        scenario.Refuse = _ => { replayFrames++; return false; };
        scenario.GuestPeers.Send(HostAddress, ItemSnapshotWire.WriteRequest(Session, 1));
        scenario.GuestPeers.Send(HostAddress, ItemSnapshotWire.WriteRequest(Session, 2));
        scenario.Network.Advance(0);
        scenario.Host.TickSnapshots();
        Check.That(replayFrames == 0, "Old and already completed request IDs do not start duplicate transfers.");
    }

    private static void IncompleteManifestRetries()
    {
        using var scenario = new Scenario();
        scenario.Guest.Tick();
        scenario.Network.Advance(0);
        scenario.HostPeers.Send(GuestAddress, ItemSnapshotWire.WriteDone(Session, 1, 2, 2));
        scenario.Network.Advance(0);
        Check.That(!scenario.Guest.Synced && scenario.Guest.Replicas.States.Count == 0,
            "A matching completion cannot publish an incomplete manifest.");

        scenario.Network.Advance(1);
        scenario.Sync();
        Check.That(scenario.SnapshotRequests == 2 && scenario.Guest.Synced && scenario.Guest.Replicas.States.Count == 2,
            "The guest replaces the invalid manifest with a complete snapshot.");
    }

    private static void GapRecoversFromSnapshot()
    {
        using var scenario = new Scenario();
        scenario.Sync();
        scenario.Refuse = frame => frame[0] == (byte)MessageKind.ItemDelta;
        Check.That(scenario.Host.TryRegister("rock", default, 1, out ulong missed), "Host registers the next item.");
        scenario.Refuse = null;
        scenario.Host.Remove(scenario.Second);
        scenario.Network.Advance(0);
        Check.That(!scenario.Guest.Synced && scenario.Guest.Replicas.TryGet(scenario.Second, out _),
            "A structural sequence gap triggers repair before applying later deltas.");
        scenario.Sync();
        Check.That(scenario.Guest.Synced && !scenario.Guest.Replicas.TryGet(scenario.Second, out _) &&
            scenario.Guest.Replicas.TryGet(missed, out _), "The manifest repairs the invisible creation and removes the missed deletion.");
    }

    private static byte[] Done(Guid session, ulong id = 1) => ItemSnapshotWire.WriteDone(session, id, 0, 0);

    private sealed class Scenario : IDisposable
    {
        internal SimulatedNetwork Network { get; }
        internal SessionPeers HostPeers { get; }
        internal SessionPeers GuestPeers { get; }
        internal HostItemSession Host { get; }
        internal GuestItemSession Guest { get; }
        internal HostItemChecks.ItemWorld World { get; } = new();
        internal readonly List<ulong> OperationRequests = new();
        internal readonly List<ItemDecision> Results = new();
        internal Func<byte[], bool>? Refuse;
        internal int SnapshotRequests;
        internal int ItemSendAttempts;
        internal bool WorldReady = true;
        internal ulong First { get; }
        internal ulong Second { get; }

        internal Scenario(bool delayRequests = false)
        {
            Network = new SimulatedNetwork(19, script: frame => delayRequests && frame.Data.Span[0] == (byte)MessageKind.ItemRequest
                ? new NetworkFault(ExtraDelay: 2f) : default);
            var plan = new RoomLaunchPlan(Session, PreviousSession, "normal", RoomOptions.Sandbox,
                new[] { new LaunchPlayer(GuestAddress, 0), new LaunchPlayer(HostAddress, 0) });
            HostPeers = new SessionPeers(new TracedTransport(Network.AddPeer(HostAddress), frame => Refuse?.Invoke(frame) == true),
                plan, HostAddress, HostAddress);
            GuestPeers = new SessionPeers(new TracedTransport(Network.AddPeer(GuestAddress), ObserveRequest),
                plan, GuestAddress, HostAddress);
            Host = new HostItemSession(HostPeers, World, Network.Clock);
            Guest = new GuestItemSession(GuestPeers, Network.Clock);
            HostPeers.AttachReceiver((sender, frame) => Host.Receive(sender, frame, WorldReady));
            GuestPeers.AttachReceiver(Guest.Receive);
            Guest.ResultAccepted += (_, result) => Results.Add(result);
            Host.TryRegister("stick", default, 1, out ulong first);
            Host.TryRegister("rock", default, 1, out ulong second);
            First = first;
            Second = second;
            Network.SetConnected(HostAddress, GuestAddress, true);
        }

        internal void Sync()
        {
            Guest.Tick();
            Network.Advance(0);
            Host.TickSnapshots();
            Network.Advance(0);
        }

        private bool ObserveRequest(byte[] frame)
        {
            if (frame[0] == (byte)MessageKind.ItemSyncRequest)
            {
                SnapshotRequests++;
            }
            else if (ItemWire.TryReadRequest(frame, Session, out var request))
            {
                OperationRequests.Add(request.Operation);
            }
            return false;
        }

        public void Dispose()
        {
            Host.Dispose();
            Guest.Dispose();
            HostPeers.Dispose();
            GuestPeers.Dispose();
        }
    }

    private sealed class TracedTransport(ISessionTransport inner, Func<byte[], bool> refuse) : ISessionTransport
    {
        public event Action<ulong, bool>? ConnectionChanged
        {
            add => inner.ConnectionChanged += value;
            remove => inner.ConnectionChanged -= value;
        }

        public Func<ulong, byte[], bool>? ReceiveFrame
        {
            get => inner.ReceiveFrame;
            set => inner.ReceiveFrame = value;
        }

        public bool IsVerified(ulong peer) => inner.IsVerified(peer);

        public bool TrySend(ulong peer, byte[] frame, bool reliable) =>
            !refuse(frame) && inner.TrySend(peer, frame, reliable);
    }
}
