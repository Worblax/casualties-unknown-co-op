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
        PeriodicSnapshotRepairsFinalMissedDelta();
        RegistrationReceiptsReplayWithoutCreatingCopies();
        InvalidOwnerPayloadDoesNotCommit();
        OnePendingOperation();
        SwapSlotsCommitAndReplay();
        MissingSwapDeltaRepairsWithSnapshot();
    }

    private static void PeriodicSnapshotRepairsFinalMissedDelta()
    {
        using var scenario = new Scenario();
        scenario.Sync();
        scenario.Refuse = frame => frame[0] == (byte)MessageKind.ItemDelta;
        Check.That(scenario.Host.TryRegister("flask", default, .64f, out ulong spawned), "The host registers a new item.");
        scenario.Network.Advance(0);
        Check.That(scenario.Guest.Synced && !scenario.Guest.Replicas.TryGet(spawned, out _),
            "A missing final delta leaves no later event to announce the sequence gap.");

        scenario.Refuse = null;
        scenario.Network.Advance(9);
        scenario.Sync();
        Check.That(scenario.SnapshotRequests == 2 && scenario.Guest.Synced &&
            scenario.Guest.Replicas.TryGet(spawned, out ItemState recovered) && recovered.Condition == .64f,
            "Periodic reconciliation restores the missed registration and its condition.");
    }

    private static void RegistrationReceiptsReplayWithoutCreatingCopies()
    {
        using var scenario = new Scenario();
        scenario.Sync();
        var accepted = new List<(ulong Key, ulong Id)>();
        var rejected = new List<ulong>();
        scenario.Guest.RegistrationAccepted += (key, id, _) => accepted.Add((key, id));
        scenario.Guest.RegistrationRejected += rejected.Add;
        var registration = new SharedItemRegistration
        {
            Definition = "flask", Placement = ItemPlacement.World, Slot = ItemWire.NoSlot,
            Condition = .62f, Payload = Array.Empty<byte>()
        };

        var registrationPublication = new List<MessageKind>();
        scenario.Refuse = frame =>
        {
            MessageKind kind = (MessageKind)frame[0];
            if (kind is MessageKind.ItemRegistered or MessageKind.ItemDelta) registrationPublication.Add(kind);
            return kind == MessageKind.ItemRegistered;
        };
        Check.That(scenario.Guest.TryRegister(registration, out ulong key), "A verified guest registers a world item.");
        scenario.Network.Advance(0);
        Check.That(scenario.Host.Snapshot().Length == 3 && accepted.Count == 0, "The host commits despite a missed receipt.");
        Check.That(registrationPublication.SequenceEqual(new[] { MessageKind.ItemRegistered, MessageKind.ItemDelta }),
            "The host sends the receipt ahead of its delta so the native item can claim that identity on arrival.");
        registration.ClientKey = key;
        scenario.Refuse = null;
        Check.That(scenario.Guest.TryResendRegistration(registration), "The guest resends its original client key.");
        scenario.Network.Advance(0);
        Check.That(scenario.Host.Snapshot().Length == 3 && accepted.Count == 1 && accepted[0].Key == key &&
            scenario.Guest.Replicas.TryGet(accepted[0].Id, out ItemState replica) && replica.Condition == .62f,
            "Receipt replay keeps one authoritative identity and the original condition.");

        registration.ClientKey = 0;
        registration.Placement = ItemPlacement.Container;
        registration.Parent = ulong.MaxValue;
        Check.That(scenario.Guest.TryRegister(registration, out ulong invalidKey), "The request reaches world validation.");
        scenario.Network.Advance(0);
        registration.ClientKey = invalidKey;
        Check.That(scenario.Guest.TryResendRegistration(registration), "The rejected request can repeat its client key.");
        scenario.Network.Advance(0);
        Check.That(rejected.SequenceEqual(new[] { invalidKey, invalidKey }) && accepted.Count == 1 &&
            scenario.Host.Snapshot().Length == 3, $"The negative receipt is idempotent and never creates an item. " +
            $"Expected two rejections for {invalidKey}, saw [{string.Join(",", rejected)}], accepted {accepted.Count}, " +
            $"host items {scenario.Host.Snapshot().Length}.");
    }

    private static void InvalidOwnerPayloadDoesNotCommit()
    {
        using var scenario = new Scenario();
        scenario.Sync();
        Check.That(scenario.Guest.TrySubmit(scenario.First, ItemAction.PickUp, 0, default), "Guest requests ownership.");
        scenario.Network.Advance(0);
        Check.That(scenario.Guest.Replicas.TryGet(scenario.First, out ItemState current) &&
            current.Placement == ItemPlacement.Inventory, "Guest owns an item it may report.");

        scenario.World.RejectState = true;
        Check.That(scenario.Guest.TryReportState(current.Id, current.Revision, .5f, new byte[] { 1 },
            current.Placement, current.Parent, current.Slot, default), "An invalid native payload still reaches the host.");
        scenario.Network.Advance(0);
        Check.That(scenario.Host.TryGet(current.Id, out ItemState authoritative) && authoritative.Revision == current.Revision &&
            authoritative.Condition == current.Condition, "Validation failure preserves the current authoritative revision.");
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

    private static void SwapSlotsCommitAndReplay()
    {
        using var scenario = new Scenario();
        PrepareSwap(scenario);
        Check.That(!scenario.Guest.TrySubmitSwap(scenario.First, 0, 1, 0),
            "The guest cannot claim a destination its replica shows as occupied.");

        List<(ulong Sequence, SharedItemState State)> deltas = new();
        scenario.Refuse = frame =>
        {
            if (ItemSnapshotWire.TryReadState(frame, MessageKind.ItemDelta, Session, out ulong sequence, out var state))
            {
                Check.That(scenario.Host.TryGet(scenario.First, out var first) && first.Slot == 1 &&
                    scenario.Host.TryGet(scenario.Second, out var second) && second.Slot == 0,
                    "The host registry has already applied both changes when the first swap delta is sent.");
                deltas.Add((sequence, state));
            }
            return frame[0] == (byte)MessageKind.ItemResult;
        };
        Check.That(scenario.Guest.TrySubmitSwap(scenario.First, 0, 1, scenario.Second) &&
            scenario.Guest.IsReserved(0) && scenario.Guest.IsReserved(1) && !scenario.Guest.IsReserved(2),
            "An occupied-slot swap reserves both slots while the request is in flight.");
        scenario.Network.Advance(0);
        Check.That(scenario.World.Swaps == 1 && scenario.Guest.Pending is { Action: ItemAction.SwapSlots } &&
            deltas.Count == 2 && deltas[0].State.Id == scenario.First && deltas[1].State.Id == scenario.Second &&
            deltas[1].Sequence == deltas[0].Sequence + 1 && deltas.All(delta => delta.State.Revision == 3),
            "An occupied swap publishes consecutive structural changes, even when its result cannot be sent.");
        scenario.Refuse = null;
        scenario.Network.Advance(1);
        scenario.Guest.Tick();
        scenario.Network.Advance(0);
        Check.That(scenario.Guest.Pending is null && !scenario.Guest.IsReserved(0) && !scenario.Guest.IsReserved(1) &&
            scenario.Results[^1] == ItemDecision.Accepted && scenario.World.Swaps == 1 &&
            scenario.Guest.Replicas.TryGet(scenario.First, out var firstReplica) && firstReplica.Slot == 1 &&
            scenario.Guest.Replicas.TryGet(scenario.Second, out var secondReplica) && secondReplica.Slot == 0,
            "A retry replays the result without moving either item again and releases both reservations.");

        scenario.World.RejectSwap = true;
        Check.That(scenario.Guest.TrySubmitSwap(scenario.First, 1, 2, 0), "Vacant destination request succeeds locally.");
        scenario.Network.Advance(0);
        Check.That(scenario.Results[^1] == ItemDecision.Unavailable &&
            scenario.Host.TryGet(scenario.First, out var unchanged) && unchanged.Slot == 1 && unchanged.Revision == 3,
            "Native refusal preserves the source slot and its revision.");
        scenario.World.RejectSwap = false;
        Check.That(scenario.Guest.TrySubmitSwap(scenario.First, 1, 2, 0), "Vacant destination can be retried as a new operation.");
        scenario.Network.Advance(0);
        Check.That(scenario.Results[^1] == ItemDecision.Accepted &&
            scenario.Host.TryGet(scenario.First, out var moved) && moved.Slot == 2 && moved.Revision == 4 &&
            scenario.Host.TryGet(scenario.Second, out var unmoved) && unmoved.Slot == 0 && unmoved.Revision == 3,
            "A successful vacancy move advances only the item that moved.");

        var stale = new SharedItemRequest
        {
            Operation = 6, ItemId = scenario.First, Slot = 2, Revision = 4, Action = ItemAction.SwapSlots,
            OtherSlot = 0, OtherItemId = scenario.Second, OtherRevision = 2
        };
        Check.That(scenario.Host.ExecuteForParticipant(scenario.GuestPeers.LocalParticipant, stale) == ItemDecision.Unavailable &&
            scenario.World.Swaps == 3, "A stale destination revision is rejected before touching native slots.");
        stale.OtherItemId = 0;
        stale.OtherRevision = 0;
        Check.That(scenario.Host.ExecuteForParticipant(scenario.GuestPeers.LocalParticipant, stale) == ItemDecision.SlotOccupied &&
            scenario.World.Swaps == 3, "The host rejects a vacancy claim for a currently occupied slot.");
    }

    private static void MissingSwapDeltaRepairsWithSnapshot()
    {
        using var scenario = new Scenario();
        PrepareSwap(scenario);
        scenario.Refuse = frame =>
            ItemSnapshotWire.TryReadState(frame, MessageKind.ItemDelta, Session, out _, out var state) && state.Id == scenario.Second;
        Check.That(scenario.Guest.TrySubmitSwap(scenario.First, 0, 1, scenario.Second), "A real occupied swap is queued.");
        scenario.Network.Advance(0);
        Check.That(scenario.Results[^1] == ItemDecision.Accepted && !scenario.Guest.Synced && !scenario.Guest.CanSubmit,
            "An accepted swap missing the final structural delta detects the incomplete replica and requests repair.");
        scenario.Sync();
        Check.That(scenario.Guest.Synced && scenario.World.Swaps == 1 &&
            scenario.Guest.Replicas.TryGet(scenario.First, out var first) && first.Slot == 1 &&
            scenario.Guest.Replicas.TryGet(scenario.Second, out var second) && second.Slot == 0,
            "The fresh snapshot repairs both slots without a second native swap.");
    }

    private static void PrepareSwap(Scenario scenario)
    {
        scenario.Sync();
        Check.That(scenario.Guest.TrySubmit(scenario.First, ItemAction.PickUp, 0, default), "First item pickup is queued.");
        scenario.Network.Advance(0);
        Check.That(scenario.Guest.TrySubmit(scenario.Second, ItemAction.PickUp, 1, default), "Second item pickup is queued.");
        scenario.Network.Advance(0);
        Check.That(scenario.Results.SequenceEqual(new[] { ItemDecision.Accepted, ItemDecision.Accepted }),
            "The host puts both swap inputs in the guest inventory.");
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
