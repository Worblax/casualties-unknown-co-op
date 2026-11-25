using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

internal static class CraftChecks
{
    private const ulong HostAddress = 1;
    private const ulong First = 2;
    private const ulong Second = 3;
    private static readonly Guid Session = new("11223344-5566-7788-99aa-bbccddeeff00");
    private static readonly Guid Previous = new("21324354-6576-8798-a9ba-cbdcedfe0f10");

    internal static void Run()
    {
        ConcurrentFloorCraft();
        StaleReportsAndDuplicateCommit();
        OwnedItemRegistration();
        ForeignInventoryRejected();
    }

    private static void ConcurrentFloorCraft()
    {
        using var scenario = new Scenario();
        bool firstSent = scenario.Guest(First).TryRequestCraft(11, new[] { scenario.Stick }, new[] { 1u }, out ulong first);
        bool secondSent = scenario.Guest(Second).TryRequestCraft(11, new[] { scenario.Stick }, new[] { 1u }, out ulong second);
        Check.That(firstSent && secondSent, "Both guests can ask the host about the same floor ingredient.");
        scenario.Advance();

        Check.That(scenario.FirstCraft == ItemDecision.Accepted && scenario.SecondCraft == ItemDecision.Unavailable,
            "The host grants one reservation and rejects the competing floor craft.");
        Check.That(scenario.Host.IsCraftReserved(scenario.Stick), "The reserved world item is locked for other mutations.");

        var commit = new ItemCraftCommit { Operation = first, Recipe = 11 };
        commit.Removed.Add(scenario.Stick);
        commit.Created.Add(new CraftCreatedItem
        {
            ClientKey = 90, Definition = "rope", Placement = ItemPlacement.Inventory, Slot = 0, Condition = 1f,
            Location = new ItemLocation(1, 2, 0), Payload = new byte[] { 7, 7 }
        });
        Check.That(scenario.Guest(First).TryCommitCraft(commit), "The winner sends one atomic craft commit.");
        scenario.Advance();

        bool hostHas = scenario.Host.TryGet(scenario.Stick, out _);
        bool firstHas = scenario.Guest(First).Replicas.TryGet(scenario.Stick, out _);
        bool secondHas = scenario.Guest(Second).Replicas.TryGet(scenario.Stick, out _);
        Check.That(!hostHas && !firstHas && !secondHas,
            $"A consumed floor ingredient disappears everywhere (craft results: {string.Join(" | ", scenario.FirstCraftResults)}).");
        Check.That(scenario.Host.TryGet(scenario.Created, out ItemState crafted) && crafted.Definition == "rope" &&
            crafted.Owner == scenario.Participant(First) && crafted.Slot == 0, "The crafted result belongs to its creator.");
        Check.That(scenario.Guest(Second).Replicas.TryGet(scenario.Created, out ItemState remote) && remote.Owner == scenario.Participant(First),
            "Other clients receive the result state while it stays in the creator's inventory.");

        scenario.Guest(First).TryCommitCraft(commit);
        scenario.Advance();
        Check.That(scenario.SnapshotCount == 1, "Duplicate commit does not duplicate the result or consume another item.");
    }

    private static void ForeignInventoryRejected()
    {
        using var scenario = new Scenario();
        Check.That(scenario.Host.TryRegister("owned", ItemPlacement.Inventory, scenario.Participant(Second), 0, 4,
            default, 1f, new byte[] { 1 }, 0, out ItemState owned), "A second player's inventory item is registered.");
        Check.That(scenario.Guest(First).TryRequestCraft(0, new[] { owned.Id }, new[] { owned.Revision }, out _),
            "A craft request is sent for another player's item.");
        scenario.Advance();
        Check.That(scenario.FirstCraft == ItemDecision.Unavailable, "The host refuses crafting from another player's inventory.");
    }
    private static void StaleReportsAndDuplicateCommit()
    {
        using var scenario = new Scenario();
        Check.That(scenario.Guest(First).TryRequestCraft(2, new[] { scenario.Stick }, new[] { 1u }, out ulong operation),
            "A craft reservation starts.");
        scenario.Advance();
        var commit = new ItemCraftCommit { Operation = operation, Recipe = 2 };
        commit.Updated.Add(new CraftUpdatedItem
        {
            Id = scenario.Stick, Revision = 1, Condition = .5f, Payload = new byte[] { 2, 3 }
        });
        commit.Created.Add(new CraftCreatedItem
        {
            ClientKey = 77, Definition = "knife", Placement = ItemPlacement.Inventory, Slot = 1,
            Condition = 1f, Location = new ItemLocation(2, 3, 0), Payload = new byte[] { 4 }
        });
        Check.That(scenario.Guest(First).TryCommitCraft(commit), "A partial-consumption craft commits an updated ingredient.");
        scenario.Advance();
        Check.That(scenario.Host.TryGet(scenario.Stick, out ItemState state) && state.Condition == .5f && state.Revision == 2,
            "Condition and opaque state change atomically on the host.");

        scenario.Guest(First).TryReportState(scenario.Stick, 1, 1f, new byte[] { 9 },
            ItemPlacement.World, 0, ItemWire.NoSlot, default);
        scenario.Advance();
        Check.That(scenario.Host.TryGet(scenario.Stick, out state) && state.Revision == 2 && state.Condition == .5f,
            "A stale owner report cannot overwrite the committed state.");
    }

    private static void OwnedItemRegistration()
    {
        using var scenario = new Scenario();
        Check.That(scenario.Guest(First).TryRegister(new SharedItemRegistration
        {
            Definition = "flask", Placement = ItemPlacement.Inventory, Slot = 3,
            Location = new ItemLocation(4, 5, 0), Payload = new byte[] { 4, 2, 1 }
        }, out ulong clientKey), "A guest can register a locally created item.");
        scenario.Advance();
        Check.That(scenario.Host.TryGet(scenario.Registered, out ItemState state) && clientKey != 0 &&
            state.Owner == scenario.Participant(First) && state.Payload.Length == 3,
            "The host assigns a network identity to a guest-created item.");
        Check.That(scenario.Guest(First).Replicas.TryGet(scenario.Registered, out _), "The creator receives the new identity.");
    }

    private sealed class Scenario : IDisposable
    {
        private readonly SimulatedNetwork network = new(73);
        private readonly SessionPeers hostPeers;
        private readonly SessionPeers firstPeers;
        private readonly SessionPeers secondPeers;
        private readonly ScenarioWorld world = new();
        internal readonly HostItemSession Host;
        internal readonly ulong Stick;

        internal Scenario()
        {
            var plan = new RoomLaunchPlan(Session, Previous, "normal", RoomOptions.Sandbox,
                new[] { new LaunchPlayer(HostAddress, 0), new LaunchPlayer(First, 0), new LaunchPlayer(Second, 0) });
            hostPeers = new SessionPeers(network.AddPeer(HostAddress), plan, HostAddress, HostAddress);
            firstPeers = new SessionPeers(network.AddPeer(First), plan, First, HostAddress);
            secondPeers = new SessionPeers(network.AddPeer(Second), plan, Second, HostAddress);
            Host = new HostItemSession(hostPeers, world, network.Clock);
            FirstGuest = new GuestItemSession(firstPeers, network.Clock);
            SecondGuest = new GuestItemSession(secondPeers, network.Clock);
            hostPeers.AttachReceiver((sender, frame) => Host.Receive(sender, frame, worldReady: true));
            firstPeers.AttachReceiver(FirstGuest.Receive);
            secondPeers.AttachReceiver(SecondGuest.Receive);
            FirstGuest.CraftResultAccepted += (_, _, decision) => { FirstCraft = decision; FirstCraftResults.Add(decision); };
            SecondGuest.CraftResultAccepted += (_, _, decision) => SecondCraft = decision;
            FirstGuest.RegistrationAccepted += (_, id, _) => Registered = id;
            Host.TryRegister("stick", default, 1, out Stick);
            world.Accessible.Add(Stick);
            network.SetConnected(HostAddress, First, true);
            network.SetConnected(HostAddress, Second, true);
            FirstGuest.Tick();
            SecondGuest.Tick();
            Advance();
            Host.TickSnapshots();
            Advance();
        }

        internal GuestItemSession FirstGuest { get; }
        internal GuestItemSession SecondGuest { get; }
        internal readonly List<ItemDecision> FirstCraftResults = new();
        internal ItemDecision FirstCraft { get; private set; }
        internal ItemDecision SecondCraft { get; private set; }
        internal ulong Created { get; private set; } = 9999;
        internal ulong Registered { get; private set; }
        internal int SnapshotCount => Host.Snapshot().Length;

        internal GuestItemSession Guest(ulong address) => address == First ? FirstGuest : SecondGuest;
        internal ParticipantId Participant(ulong address) => address == First ? firstPeers.LocalParticipant : secondPeers.LocalParticipant;

        internal void Advance()
        {
            while (network.Queued > 0) network.Advance(0);
            Host.TickSnapshots();
            while (network.Queued > 0) network.Advance(0);
            Created = FindCreated();
        }

        private ulong FindCreated()
        {
            foreach (ItemState state in Host.Snapshot())
            {
                if (state.Definition is "knife" or "flask" or "rope") return state.Id;
            }
            return 9999;
        }

        public void Dispose()
        {
            Host.Dispose();
            FirstGuest.Dispose();
            SecondGuest.Dispose();
            hostPeers.Dispose();
            firstPeers.Dispose();
            secondPeers.Dispose();
        }
    }

    private sealed class ScenarioWorld : IItemWorld
    {
        internal readonly HashSet<ulong> Accessible = new();
        public ItemDecision CheckPickup(ParticipantId player, ulong item, byte slot, ItemLocation? previousPosition) => ItemDecision.Accepted;
        public bool TryPickUp(ParticipantId player, ulong item, byte slot, out float condition) { condition = 1; return true; }
        public bool TrySwapSlots(ParticipantId player, ItemState first, byte destination, ItemState? second) => false;
        public ItemDecision Drop(ParticipantId player, ulong item, SharedItemRequest request, out ItemLocation location, out float condition)
        { location = request.Location; condition = 1; return ItemDecision.Accepted; }
        public bool CanApplyState(ulong item, byte[] payload) => true;
        public void ApplyRemoteState(ItemState state) { }
        public bool TryGetRecipe(int recipe, out CraftRecipePlan? plan) => RecipeCatalog.TryGet(recipe, out plan);
        public ItemDecision CheckCraftIngredient(ParticipantId player, ulong item) =>
            Accessible.Contains(item) ? ItemDecision.Accepted : ItemDecision.Unavailable;
        public void RemoveItem(ulong item) { }
        public ICraftWorldTransaction? BeginCraft(ItemCraftCommit commit, CraftRecipePlan plan,
            IReadOnlyList<ItemState> ingredients) => new NoOpCraftTransaction();
        public bool IsKnownDefinition(string definition) => true;
        public ItemDecision CheckRegistration(ParticipantId player, SharedItemRegistration registration, ItemState? parent) => ItemDecision.Accepted;
    }
}
