using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

internal static class P0CraftChecks
{
    private const ulong HostAddress = 10;
    private const ulong First = 11;
    private const ulong Second = 12;
    private static readonly Guid Session = new("22334455-6677-8899-aabb-ccddeeff0011");
    private static readonly Guid Previous = new("33445566-7788-99aa-bbcc-ddeeff001122");

    internal static void Run()
    {
        ConcurrentReservationHasSingleWinner();
        RejectedWorldTransactionLeavesIngredientsUntouched();
        DeleteAndUpdateOverlapIsRejected();
        DuplicateCommitIdsAreRejected();
        WrongResultAndMissingIngredientsAreRejected();
        HostCannotMutateReservedItems();
        ReservedNestedItemBlocksParent();
        ForeignContainerRegistrationIsRejected();
        PlacementReportMovesOwnedItem();
        ExpiredReservationCannotCommit();
        DuplicateCommitReplaysAfterReconnect();
        RegistrationReplayAfterInterleavedRegistrationIsIdempotent();
        AcceptedCommitCompletesAfterNativeFailures();
        EvictedClientKeyCannotBeReusedAfterReconnect();
    }

    private static void ConcurrentReservationHasSingleWinner()
    {
        using var scenario = new Scenario();
        Check.That(scenario.Guest(First).TryRequestCraft(11, new[] { scenario.Item0 }, new[] { 1u }, out _) &&
            scenario.Guest(Second).TryRequestCraft(11, new[] { scenario.Item0 }, new[] { 1u }, out _),
            "Both participants request the same shared ingredient.");
        scenario.AdvanceReady();
        Check.That(scenario.FirstCraftResults.Last() == ItemDecision.Accepted &&
            scenario.SecondCraftResults.Last() == ItemDecision.Unavailable && scenario.Host.IsCraftReserved(scenario.Item0),
            "Only the first reservation is granted and the ingredient remains locked.");
    }

    private static void RejectedWorldTransactionLeavesIngredientsUntouched()
    {
        using var scenario = new Scenario();
        ulong operation = scenario.ReserveFirst();
        scenario.World.RejectApply = true;
        Check.That(scenario.FirstGuest.TryCommitCraft(Commit(operation, 11, new[] { scenario.Item0 }, Result(80))),
            "The participant sends a valid recipe-11 ingredient removal and result registration.");
        scenario.AdvanceReady();
        bool firstPresent = scenario.Host.TryGet(scenario.Item0, out ItemState first);
        bool secondPresent = scenario.Host.TryGet(scenario.Item1, out ItemState second);
        int snapshotCount = scenario.Host.Snapshot().Length;
        bool reserved = scenario.Host.IsCraftReserved(scenario.Item0);
        Check.That(scenario.FirstCraftResults.Last() == ItemDecision.Unavailable && firstPresent && first.Revision == 1 &&
            secondPresent && second.Revision == 1 && snapshotCount == 2 && !reserved && scenario.World.Prepared,
            $"Rollback invariants: result={scenario.FirstCraftResults.Last()}, first={firstPresent}/{first.Revision}, " +
            $"second={secondPresent}/{second.Revision}, count={snapshotCount}, reserved={reserved}, " +
            $"transactionPrepared={scenario.World.Prepared}.");
    }

    private static void DeleteAndUpdateOverlapIsRejected()
    {
        using var scenario = new Scenario();
        ulong operation = scenario.ReserveFirst();
        var commit = Commit(operation, 11, new[] { scenario.Item0 }, Result(81));
        commit.Updated.Add(new CraftUpdatedItem
        {
            Id = scenario.Item0, Revision = 1, Condition = .5f, Payload = new byte[] { 9 }
        });
        Check.That(scenario.FirstGuest.TryCommitCraft(commit), "The participant sends a delete/update-overlapping commit.");
        scenario.AdvanceReady();
        Check.That(scenario.FirstCraftResults.Last() == ItemDecision.Conflict &&
            scenario.Host.TryGet(scenario.Item0, out ItemState first) && first.Revision == 1 && first.Condition == 1f &&
            scenario.Host.TryGet(scenario.Item1, out ItemState second) && second.Revision == 1 &&
            scenario.Host.Snapshot().Length == 2,
            "Delete/update overlap rejects atomically without changing either ingredient.");
    }

    private static void DuplicateCommitIdsAreRejected()
    {
        using var scenario = new Scenario();
        ulong operation = scenario.ReserveFirst();
        var commit = Commit(operation, 11, new[] { scenario.Item0, scenario.Item0 }, Result(82));
        Check.That(scenario.FirstGuest.TryCommitCraft(commit), "The participant sends a commit with a repeated removed ID.");
        scenario.AdvanceReady();
        Check.That(scenario.FirstCraftResults.Last() == ItemDecision.Conflict &&
            scenario.Host.TryGet(scenario.Item0, out ItemState first) && first.Revision == 1 &&
            scenario.Host.TryGet(scenario.Item1, out ItemState second) && second.Revision == 1 &&
            scenario.Host.Snapshot().Length == 2,
            "Repeated removed IDs reject the whole commit without changing either ingredient.");
    }

    private static void WrongResultAndMissingIngredientsAreRejected()
    {
        using var wrong = new Scenario();
        ulong wrongOperation = wrong.ReserveFirst();
        var wrongResult = Commit(wrongOperation, 11, new[] { wrong.Item0 }, Result(90, "knife"));
        Check.That(wrong.FirstGuest.TryCommitCraft(wrongResult), "The participant sends a result from another recipe.");
        wrong.AdvanceReady();
        Check.That(wrong.FirstCraftResults.Last() == ItemDecision.Conflict && wrong.Host.TryGet(wrong.Item0, out ItemState wrongState) &&
            wrongState.Revision == 1 && wrong.Host.Snapshot().Length == 2,
            "A result from a different recipe is rejected atomically.");

        using var missing = new Scenario();
        ulong missingOperation = missing.ReserveFirst();
        var missingIngredient = new ItemCraftCommit { Operation = missingOperation, Recipe = 11 };
        missingIngredient.Created.Add(Result(91));
        Check.That(missing.FirstGuest.TryCommitCraft(missingIngredient), "The participant sends a result without consuming its ingredient.");
        missing.AdvanceReady();
        Check.That(missing.FirstCraftResults.Last() == ItemDecision.Conflict && missing.Host.TryGet(missing.Item0, out ItemState missingState) &&
            missingState.Revision == 1 && missing.Host.Snapshot().Length == 2,
            "A result without the required ingredient is rejected atomically.");
    }

    private static void HostCannotMutateReservedItems()
    {
        using var scenario = new Scenario();
        scenario.ReserveFirst();
        Check.That(scenario.Host.ExecuteForParticipant(scenario.HostParticipant, new SharedItemRequest
        {
            ItemId = scenario.Item0, Revision = 1, Operation = 900, Action = ItemAction.PickUp, Slot = 0
        }) == ItemDecision.Conflict, "A local host pickup observes the guest craft reservation.");
    }

    private static void ReservedNestedItemBlocksParent()
    {
        using var scenario = new Scenario();
        Check.That(scenario.Host.TryRegister("box", ItemPlacement.World, default, 0, ItemWire.NoSlot,
            new ItemLocation(2, 2, 0), 1f, new byte[] { 1 }, 0, out ItemState box), "A world container is registered.");
        Check.That(scenario.Host.TryRegister("stick", ItemPlacement.Container, default, box.Id, ItemWire.NoSlot,
            default, 1f, new byte[] { 2 }, 0, out ItemState child), "A nested ingredient is registered.");
        scenario.AdvanceReady();
        Check.That(scenario.FirstGuest.TryRequestCraft(11, new[] { child.Id }, new[] { child.Revision }, out _),
            "The guest reserves the nested ingredient.");
        scenario.AdvanceReady();
        Check.That(scenario.FirstCraftResults.Last() == ItemDecision.Accepted && scenario.Host.IsCraftReserved(child.Id),
            "The nested reservation is accepted.");
        Check.That(scenario.Host.ExecuteForParticipant(scenario.HostParticipant, new SharedItemRequest
        {
            ItemId = box.Id, Revision = box.Revision, Operation = 901, Action = ItemAction.PickUp, Slot = 0
        }) == ItemDecision.Conflict, "Moving the parent cannot bypass a reserved child.");
    }

    private static void ForeignContainerRegistrationIsRejected()
    {
        using var scenario = new Scenario();
        Check.That(scenario.Host.TryRegister("box", ItemPlacement.Inventory, scenario.ParticipantOf(Second), 0, 3,
            default, 1f, new byte[] { 1 }, 0, out ItemState foreign), "Another participant owns a registered container.");
        int before = scenario.Host.Snapshot().Length;
        Check.That(scenario.FirstGuest.TryRegister(Registration(foreign.Id), out _),
            "The first participant attempts to register inside the foreign container.");
        scenario.AdvanceReady();
        Check.That(scenario.Host.Snapshot().Length == before,
            "A registration inside another participant's container is rejected.");
    }

    private static void PlacementReportMovesOwnedItem()
    {
        using var scenario = new Scenario();
        ParticipantId owner = scenario.ParticipantOf(First);
        Check.That(scenario.Host.TryRegister("stick", ItemPlacement.Inventory, owner, 0, 2, default, 1f,
            new byte[] { 5 }, 0, out ItemState item), "The host registers an owned inventory item.");
        scenario.AdvanceReady();
        Check.That(scenario.FirstGuest.Replicas.TryGet(item.Id, out ItemState before) &&
            scenario.SecondGuest.Replicas.TryGet(item.Id, out _), "Both clients receive the owned item.");
        Check.That(scenario.FirstGuest.TryReportState(item.Id, before.Revision, before.Condition, before.Payload,
            ItemPlacement.Inventory, 0, 3, before.Location), "The owner reports a slot move.");
        scenario.AdvanceReady();
        Check.That(scenario.Host.TryGet(item.Id, out ItemState moved) && moved.Slot == 3 &&
            scenario.SecondGuest.Replicas.TryGet(item.Id, out ItemState observed) && observed.Slot == 3,
            "The authoritative slot and the observer replica follow the placement report.");

        Check.That(scenario.Host.TryRegister("rock", ItemPlacement.Inventory, owner, 0, 4, default, 1f,
            new byte[] { 6 }, 0, out _), "A second slot is occupied.");
        scenario.AdvanceReady();
        Check.That(scenario.FirstGuest.TryReportState(item.Id, moved.Revision, moved.Condition, moved.Payload,
            ItemPlacement.Inventory, 0, 4, moved.Location), "The owner tries to overwrite an occupied slot.");
        scenario.AdvanceReady();
        Check.That(scenario.Host.TryGet(item.Id, out ItemState retained) && retained.Slot == 3 &&
            retained.Revision == moved.Revision, "An occupied destination cannot corrupt the authoritative placement.");
    }

    private static void ExpiredReservationCannotCommit()
    {
        using var scenario = new Scenario();
        ulong operation = scenario.ReserveFirst();
        scenario.Network.Advance(13f);
        scenario.Host.TickSnapshots();
        Check.That(!scenario.Host.IsCraftReserved(scenario.Item0), "The reservation expires after its deterministic deadline.");
        Check.That(scenario.FirstGuest.TryCommitCraft(Commit(operation, 11, new[] { scenario.Item0 }, Result(83))),
            "The participant sends a commit after reservation expiry.");
        scenario.AdvanceReady();
        Check.That(scenario.FirstCraftResults.Last() == ItemDecision.Conflict &&
            scenario.Host.TryGet(scenario.Item0, out ItemState first) && first.Revision == 1 &&
            scenario.Host.TryGet(scenario.Item1, out ItemState second) && second.Revision == 1 &&
            scenario.Host.Snapshot().Length == 2,
            "An expired operation cannot consume ingredients or create a result.");
    }

    private static void DuplicateCommitReplaysAfterReconnect()
    {
        int craftResults = 0;
        var trace = new List<NetworkTrace>();
        using var scenario = new Scenario(frame =>
        {
            if (frame.Sender == HostAddress && frame.Receiver == First && frame.Data.Length > 0 &&
                frame.Data.Span[0] == (byte)MessageKind.ItemCraftResult)
            {
                craftResults++;
                if (craftResults == 2) return new NetworkFault(ExtraDelay: 20f);
            }
            return default;
        });
        scenario.Network.Trace += trace.Add;
        ulong operation = scenario.ReserveFirst();
        Check.That(scenario.FirstGuest.TryCommitCraft(Commit(operation, 11, new[] { scenario.Item0 }, Result(84))),
            "The participant sends a valid commit before losing its connection.");
        scenario.Network.Advance(0);
        Check.That(scenario.Host.Snapshot().Length == 2 && scenario.FirstCraftResults.Count == 1,
            "The host commits once while the result packet is delayed on the old connection.");

        scenario.Network.SetConnected(HostAddress, First, false);
        scenario.Network.SetConnected(HostAddress, First, true);
        scenario.FirstGuest.ResumeAfterOffer(operation);
        scenario.FirstGuest.Tick();
        scenario.Network.Advance(0);
        scenario.Host.TickSnapshots();
        scenario.Network.Advance(0);
        Check.That(scenario.FirstGuest.Synced, "The participant completes a fresh item snapshot after reconnecting.");

        Check.That(scenario.FirstGuest.TryCommitCraft(Commit(operation, 11, new[] { scenario.Item0 }, Result(84))),
            "The participant retries the original commit after reconnecting.");
        scenario.Network.Advance(0);
        Check.That(scenario.FirstCraftResults.Count == 2 && scenario.FirstCraftResults.All(value => value == ItemDecision.Accepted) &&
            scenario.Host.Snapshot().Length == 2 && scenario.Host.Snapshot().Any(value => value.Definition == "rope"),
            "The host replays the terminal result without consuming or creating items twice.");

        scenario.Network.Advance(20f);
        Check.That(trace.Exists(item => item.Event == "retired") && scenario.FirstCraftResults.Count == 2 &&
            scenario.Host.Snapshot().Length == 2,
            "The delayed old-connection result is retired without a second commit or response.");
    }

    private static void RegistrationReplayAfterInterleavedRegistrationIsIdempotent()
    {
        using var scenario = new Scenario();
        var accepted = new List<(ulong ClientKey, ulong Item, uint Revision)>();
        scenario.FirstGuest.RegistrationAccepted += (key, item, revision) => accepted.Add((key, item, revision));
        var registrationA = Registration(scenario.Item0);
        var registrationB = Registration(scenario.Item0);
        Check.That(scenario.FirstGuest.TryRegister(registrationA, out ulong keyA),
            "The participant registers item A.");
        Check.That(scenario.FirstGuest.TryRegister(registrationB, out ulong keyB) && keyA != keyB,
            "The participant registers two distinct container items.");
        scenario.AdvanceReady();
        Check.That(accepted.Count == 2, "The host acknowledges both initial registrations.");

        registrationA.Operation = 1;
        registrationA.ClientKey = keyA;
        Check.That(scenario.FirstPeers.Send(HostAddress, ItemWire.WriteRegistration(Session, registrationA)),
            "The participant replays registration A after registration B.");
        scenario.AdvanceReady();
        Check.That(accepted.Count == 3 && accepted[0].ClientKey == keyA && accepted[2].ClientKey == keyA &&
            accepted[0].Item == accepted[2].Item && accepted[0].Revision == accepted[2].Revision &&
            accepted[1].ClientKey == keyB && accepted[1].Item != accepted[0].Item &&
            scenario.Host.Snapshot().Length == 4,
            "The A-B-A sequence replays A's receipt without allocating another item.");
    }

    private static void AcceptedCommitCompletesAfterNativeFailures()
    {
        using var scenario = new Scenario();
        var accepted = new List<ulong>();
        scenario.FirstGuest.RegistrationAccepted += (key, _, _) => accepted.Add(key);
        ulong operation = scenario.ReserveFirst();
        scenario.World.RejectRemoteState = 1;
        scenario.World.FailFinalize = 1;
        scenario.World.FailDispose = 1;
        Check.That(scenario.FirstGuest.TryCommitCraft(Commit(operation, 11, new[] { scenario.Item0 }, Result(85))),
            "The participant sends a valid commit before native reconciliation fails.");
        scenario.AdvanceReady();
        bool created = scenario.Host.Snapshot().Any(value => value.Definition == "rope");
        Check.That(scenario.FirstCraftResults.Last() == ItemDecision.Accepted && created &&
            !scenario.Host.TryGet(scenario.Item0, out _) && accepted.Count == 1 &&
            scenario.World.RemoteStateCalls >= 2 && scenario.World.FailFinalize == 0 &&
            scenario.World.FailDispose == 0 && scenario.World.RejectRemoteState == 0,
            $"Accepted operation completion: result={scenario.FirstCraftResults.Last()}, created={created}, " +
            $"acks={accepted.Count}, projections={scenario.World.RemoteStateCalls}.");
    }

    private static void EvictedClientKeyCannotBeReusedAfterReconnect()
    {
        using var scenario = new Scenario();
        var accepted = new List<(ulong ClientKey, ulong Item)>();
        scenario.FirstGuest.RegistrationAccepted += (key, item, _) => accepted.Add((key, item));
        var first = Registration(scenario.Item0);
        Check.That(scenario.FirstGuest.TryRegister(first, out ulong firstKey), "The participant registers the first item.");
        for (int index = 1; index < 40; index++)
        {
            Check.That(scenario.FirstGuest.TryRegister(Registration(scenario.Item0), out _),
                $"The participant registers item {index + 1}.");
        }
        scenario.AdvanceReady();
        int before = scenario.Host.Snapshot().Length;
        int acknowledged = accepted.Count;
        ulong lastOperation = scenario.Host.LastRegistrationOperation(First);
        ulong lastKey = scenario.Host.LastClientKey(First);
        Check.That(before == 42 && acknowledged == 40 && lastKey == 40,
            $"Forty registrations settle: items={before}, acks={acknowledged}, lastKey={lastKey}.");

        var stale = Registration(scenario.Item0);
        stale.Operation = lastOperation + 1;
        stale.ClientKey = firstKey;
        Check.That(scenario.FirstPeers.Send(HostAddress, ItemWire.WriteRegistration(Session, stale)),
            "A stale evicted client key is replayed with a fresh operation.");
        scenario.AdvanceReady();
        Check.That(scenario.Host.Snapshot().Length == before && accepted.Count == acknowledged &&
            scenario.Host.LastClientKey(First) == lastKey &&
            scenario.Host.LastRegistrationOperation(First) == lastOperation,
            "The stale key is rejected without allocating an item or advancing receipts.");

        scenario.Network.SetConnected(HostAddress, First, false);
        scenario.Network.SetConnected(HostAddress, First, true);
        scenario.FirstGuest.ResumeAfterOffer(0, 0, lastOperation, lastKey);
        for (int frame = 0; frame < 32 && !scenario.FirstGuest.Synced; frame++)
        {
            scenario.FirstGuest.Tick();
            scenario.Network.Advance(0);
            scenario.Host.TickSnapshots();
            scenario.Network.Advance(0);
        }
        Check.That(scenario.FirstGuest.Synced, "The participant resynchronizes after reconnecting.");

        Check.That(scenario.FirstGuest.TryRegister(Registration(scenario.Item0), out ulong nextKey) &&
            nextKey > lastKey, "A fresh registration resumes above the confirmed high-water mark.");
        scenario.AdvanceReady();
        Check.That(accepted.Count == acknowledged + 1 && scenario.Host.Snapshot().Length == before + 1 &&
            scenario.Host.LastClientKey(First) == nextKey,
            "Reconnect keeps client keys unique across the evicted history window.");
    }

    private static ItemCraftCommit Commit(ulong operation, int recipe, IReadOnlyList<ulong> removed, CraftCreatedItem created)
    {
        var commit = new ItemCraftCommit { Operation = operation, Recipe = recipe };
        foreach (ulong item in removed) commit.Removed.Add(item);
        commit.Created.Add(created);
        return commit;
    }

    private static CraftCreatedItem Result(ulong clientKey, string definition = "rope") => new()
    {
        ClientKey = clientKey, Definition = definition, Placement = ItemPlacement.Inventory, Slot = 0,
        Condition = 1f, Location = new ItemLocation(1, 2, 0), Payload = new byte[] { 7 }
    };

    private static SharedItemRegistration Registration(ulong parent) => new()
    {
        Definition = "rope", Placement = ItemPlacement.Container, Parent = parent, Slot = ItemWire.NoSlot,
        Condition = 1f, Location = new ItemLocation(2, 3, 0), Payload = new byte[] { 4 }
    };

    private sealed class Scenario : IDisposable
    {
        private readonly SessionPeers hostPeers;
        private readonly SessionPeers secondPeers;

        internal Scenario(Func<SimulatedFrame, NetworkFault>? script = null)
        {
            Network = new SimulatedNetwork(223, script: script);
            var plan = new RoomLaunchPlan(Session, Previous, "normal", RoomOptions.Sandbox,
                new[] { new LaunchPlayer(HostAddress, 0), new LaunchPlayer(First, 0), new LaunchPlayer(Second, 0) });
            hostPeers = new SessionPeers(Network.AddPeer(HostAddress), plan, HostAddress, HostAddress);
            FirstPeers = new SessionPeers(Network.AddPeer(First), plan, First, HostAddress);
            secondPeers = new SessionPeers(Network.AddPeer(Second), plan, Second, HostAddress);
            World = new CraftWorld();
            Host = new HostItemSession(hostPeers, World, Network.Clock);
            FirstGuest = new GuestItemSession(FirstPeers, Network.Clock);
            SecondGuest = new GuestItemSession(secondPeers, Network.Clock);
            hostPeers.AttachReceiver((sender, frame) => Host.Receive(sender, frame, worldReady: true));
            FirstPeers.AttachReceiver(FirstGuest.Receive);
            secondPeers.AttachReceiver(SecondGuest.Receive);
            FirstGuest.CraftResultAccepted += (_, _, decision) => FirstCraftResults.Add(decision);
            SecondGuest.CraftResultAccepted += (_, _, decision) => SecondCraftResults.Add(decision);
            Check.That(Host.TryRegister("stick", default, 1f, out ulong first), "The host registers ingredient A.");
            Check.That(Host.TryRegister("stick", default, 1f, out ulong second), "The host registers ingredient B.");
            Item0 = first;
            Item1 = second;
            Network.SetConnected(HostAddress, First, true);
            Network.SetConnected(HostAddress, Second, true);
            FirstGuest.Tick();
            SecondGuest.Tick();
            Network.Advance(0);
            Host.TickSnapshots();
            Network.Advance(0);
            Check.That(FirstGuest.Synced && SecondGuest.Synced && FirstGuest.Replicas.TryGet(Item0, out _) &&
                FirstGuest.Replicas.TryGet(Item1, out _), "Both guests complete the initial item snapshot.");
        }

        internal SimulatedNetwork Network { get; }
        internal CraftWorld World { get; }
        internal HostItemSession Host { get; }
        internal SessionPeers FirstPeers { get; }
        internal GuestItemSession FirstGuest { get; }
        internal GuestItemSession SecondGuest { get; }
        internal ulong Item0 { get; }
        internal ulong Item1 { get; }
        internal List<ItemDecision> FirstCraftResults { get; } = new();
        internal List<ItemDecision> SecondCraftResults { get; } = new();
        internal ParticipantId HostParticipant => hostPeers.LocalParticipant;

        internal GuestItemSession Guest(ulong address) => address == First ? FirstGuest : SecondGuest;
        internal ParticipantId ParticipantOf(ulong address) =>
            address == First ? FirstPeers.LocalParticipant : secondPeers.LocalParticipant;

        internal ulong ReserveFirst()
        {
            const int recipe = 11;
            ulong[] ingredients = { Item0 };
            uint[] revisions = ingredients.Select(_ => 1u).ToArray();
            Check.That(FirstGuest.TryRequestCraft(recipe, ingredients, revisions, out ulong operation),
                "The participant sends a craft reservation request.");
            AdvanceReady();
            Check.That(FirstCraftResults.Last() == ItemDecision.Accepted,
                "The host accepts the requested craft reservation.");
            Check.That(ingredients.All(Host.IsCraftReserved),
                "The accepted craft reservation locks every requested ingredient.");
            return operation;
        }

        internal void AdvanceReady() => Network.Advance(0);

        public void Dispose()
        {
            Host.Dispose();
            FirstGuest.Dispose();
            SecondGuest.Dispose();
            hostPeers.Dispose();
            FirstPeers.Dispose();
            secondPeers.Dispose();
        }
    }
}
