using System;
using System.Linq;
using CasualtiesCoop.Gameplay.Fluids;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

internal static class WireCompatibilityChecks
{
    private const string SessionBytes = "33221100554477668899AABBCCDDEEFF";
    private const string AttemptBytes = "433221106554877698A9BACBDCEDFE0F";
    private const string OperationBytes = "0807060504030201";
    private const string ItemIdBytes = "1817161514131211";
    private const string RevisionBytes = "24232221";
    private const string LocationBytes = "0000803F000000C0000080400000003F000080BF";
    private const ulong Operation = 0x0102030405060708;
    private const ulong ItemId = 0x1112131415161718;
    private const uint Revision = 0x21222324;
    private static readonly Guid Session = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Attempt = new("10213243-5465-7687-98a9-bacbdcedfe0f");

    internal static void Run()
    {
        ItemFrames();
        SwapFrames();
        FluidFrames();
        RejoinFrames();
    }

    private static void SwapFrames()
    {
        var request = new SharedItemRequest
        {
            Operation = Operation, ItemId = ItemId, Revision = Revision, Action = ItemAction.SwapSlots,
            Slot = 2, OtherSlot = 3, OtherItemId = ItemId + 1, OtherRevision = Revision + 1
        };
        byte[] frame = ItemWire.WriteRequest(Session, request);
        Require(ItemWire.TryReadRequest(frame, Session, out SharedItemRequest copy) &&
            copy.Operation == Operation && copy.Action == ItemAction.SwapSlots && copy.ItemId == ItemId &&
            copy.Revision == Revision && copy.Slot == 2 && copy.OtherSlot == 3 && copy.OtherItemId == ItemId + 1 &&
            copy.OtherRevision == Revision + 1 && copy.Payload.Length == 0, "Occupied swap request round trips");
        Require(!ItemWire.TryReadRequest(frame.AsSpan(0, frame.Length - 1), Session, out _) &&
            !ItemWire.TryReadRequest(frame.Append((byte)0).ToArray(), Session, out _), "Swap has one exact wire size");
        frame[63] = 1;
        Require(!ItemWire.TryReadRequest(frame, Session, out _), "Swap request cannot carry a state payload");

        request.OtherItemId = 0;
        Require(!Accepts(), "Vacancy cannot specify an occupant revision");
        request.OtherRevision = 0;
        Require(Accepts(), "Vacant destination round trips");
        request.OtherItemId = ItemId + 1;
        Require(!Accepts(), "Occupied destination requires a revision");
        request.OtherRevision = Revision;
        request.OtherItemId = ItemId;
        Require(!Accepts(), "The same item cannot occupy both slots");
        request.OtherItemId = ItemId + 1;
        request.OtherSlot = request.Slot;
        Require(!Accepts(), "Swap requires two different slots");
        request.OtherSlot = ItemWire.NoSlot;
        Require(!Accepts(), "Swap destination must identify a slot");

        bool Accepts() => ItemWire.TryReadRequest(ItemWire.WriteRequest(Session, request), Session, out _);
    }

    private static void ItemFrames()
    {
        SharedItemRequest request = new()
        {
            Operation = Operation, ItemId = ItemId, Revision = Revision, Action = ItemAction.Throw,
            Slot = 2, Location = new ItemLocation(1, -2, 4, 0.5f, -1, 2), Payload = new byte[] { 1, 2, 3 }
        };
        byte[] frame = ItemWire.WriteRequest(Session, request);
        Require(ItemWire.TryReadRequest(frame, Session, out SharedItemRequest decoded) &&
            decoded.Operation == request.Operation && decoded.ItemId == request.ItemId &&
            decoded.Revision == request.Revision && decoded.Action == request.Action &&
            decoded.Slot == 2 && decoded.Location.Y == -2 && decoded.Payload.Length == 3,
            "ItemRequest with state payload round trips");
        Require(frame.Length <= RoomWire.MaxFrameSize, "ItemRequest fits the transport frame");
        Require(!ItemWire.TryReadRequest(frame, Attempt, out _), "ItemRequest rejects another session");
        Require(!ItemWire.TryReadRequest(frame.AsSpan(0, frame.Length - 1), Session, out _), "ItemRequest rejects truncation");

        SharedItemState state = new()
        {
            Id = ItemId, Revision = Revision, Sample = 3, SampleTime = 42.5f, Definition = "stick", Owner = Operation,
            Placement = ItemPlacement.Inventory, Slot = 2, Condition = 0.75f,
            Location = new ItemLocation(1, -2, 4, 0.5f, -1, 8), Payload = new byte[] { 9, 8, 7 }
        };
        frame = ItemWire.WriteState(MessageKind.ItemState, Session, state);
        Require(ItemWire.TryReadState(frame, MessageKind.ItemState, Session, out SharedItemState copy) &&
            copy.Owner == Operation && copy.Placement == ItemPlacement.Inventory && copy.Sample == 3 && copy.SampleTime == 42.5f &&
            copy.Definition == "stick" && copy.Location.Spin == 8 && copy.Payload.SequenceEqual(new byte[] { 9, 8, 7 }),
            "ItemState carries placement and opaque component state");
        state.Owner = 0;
        state.Placement = ItemPlacement.World;
        state.Slot = ItemWire.NoSlot;
        frame = ItemWire.WriteState(MessageKind.ItemMotion, Session, state);
        Require(ItemWire.TryReadState(frame, MessageKind.ItemMotion, Session, out copy) && copy.Payload.Length == 0 &&
            copy.SampleTime == 42.5f,
            "Motion frames stay compact and never repeat component state");
        frame = ItemSnapshotWire.WriteState(MessageKind.ItemSnapshotEntry, Session, Operation, state);
        Require(ItemSnapshotWire.TryReadState(frame, MessageKind.ItemSnapshotEntry, Session, out _, out copy) &&
            copy.SampleTime == 42.5f, "Snapshot items carry the latest motion timestamp");
        foreach (float invalidTime in new[] { float.NaN, float.PositiveInfinity, -0.01f })
        {
            SharedItemState invalid = state;
            invalid.SampleTime = invalidTime;
            frame = ItemWire.WriteState(MessageKind.ItemMotion, Session, invalid);
            Require(!ItemWire.TryReadState(frame, MessageKind.ItemMotion, Session, out _), "Invalid motion timestamp");
        }
        state.Placement = (ItemPlacement)9;
        Require(!ItemWire.TryReadState(ItemWire.WriteState(MessageKind.ItemState, Session, state), MessageKind.ItemState,
            Session, out _), "Unknown placement is rejected");

        var registration = new SharedItemRegistration
        {
            Operation = Operation, ClientKey = ItemId, Definition = "stick", Placement = ItemPlacement.Inventory,
            Slot = 2, Location = new ItemLocation(1, -2, 4), Payload = new byte[] { 4, 5 }
        };
        frame = ItemWire.WriteRegistration(Session, registration);
        Require(ItemWire.TryReadRegistration(frame, Session, out SharedItemRegistration registered) &&
            registered.ClientKey == ItemId && registered.Payload.Length == 2 && registered.Placement == ItemPlacement.Inventory,
            "Guest-created item registration round trips");
        frame = ItemWire.WriteRegistered(Session, Operation, ItemId, Operation + 1, Revision);
        Require(ItemWire.TryReadRegistered(frame, Session, out ulong operation, out ulong key, out ulong item, out uint revision) &&
            operation == Operation && key == ItemId && item == Operation + 1 && revision == Revision,
            "Registration acknowledgment maps the local item key");
        var report = new SharedItemStateReport { Operation = Operation, ItemId = ItemId, Revision = Revision,
            Condition = .4f, Placement = ItemPlacement.World, Slot = ItemWire.NoSlot,
            Location = new ItemLocation(1, 2, 3), Payload = new byte[] { 6 } };
        Require(ItemWire.TryReadReport(ItemWire.WriteReport(Session, report), Session, out SharedItemStateReport read) &&
            read.Condition == .4f && read.Payload.Length == 1 && read.Placement == ItemPlacement.World &&
            read.Slot == ItemWire.NoSlot, "Owner state report round trips with placement");

        var craft = new ItemCraftRequest { Operation = Operation, Recipe = 4, ItemIds = new[] { ItemId, ItemId + 1 },
            Revisions = new[] { Revision, Revision } };
        Require(ItemCraftWire.TryReadRequest(ItemCraftWire.WriteRequest(Session, craft), Session, out ItemCraftRequest craftRead) &&
            craftRead.Recipe == 4 && craftRead.ItemIds.Length == 2, "Craft reservation request round trips");
        var reusable = new ItemCraftRequest { Operation = Operation, Recipe = 4,
            ItemIds = new[] { ItemId, ItemId }, Revisions = new[] { Revision, Revision } };
        Require(ItemCraftWire.TryReadRequest(ItemCraftWire.WriteRequest(Session, reusable), Session, out ItemCraftRequest reusableRead) &&
            reusableRead.ItemIds[0] == reusableRead.ItemIds[1], "Reusable recipe requirements may name one instance twice");
        byte[] craftResult = ItemCraftWire.WriteResult(Session, Operation, 4, ItemDecision.Accepted);
        Require(ItemCraftWire.TryReadResult(craftResult, Session, out _, out int recipe, out ItemDecision result) &&
            recipe == 4 && result == ItemDecision.Accepted, "Craft result round trips");
        var commit = new ItemCraftCommit { Operation = Operation, Recipe = 4 };
        commit.Removed.Add(ItemId);
        commit.Updated.Add(new CraftUpdatedItem { Id = ItemId + 1, Revision = Revision, Condition = .5f,
            Payload = new byte[] { 2 } });
        commit.Created.Add(new CraftCreatedItem { ClientKey = ItemId + 2, Definition = "stick",
            Placement = ItemPlacement.Inventory, Slot = 2, Condition = .8f, Location = new ItemLocation(1, 2, 3),
            Payload = new byte[] { 3 } });
        Require(ItemCraftWire.TryReadCommit(ItemCraftWire.WriteCommit(Session, commit), Session, out ItemCraftCommit commitRead) &&
            commitRead.Removed.Count == 1 && commitRead.Updated.Count == 1 && commitRead.Created.Count == 1 &&
            commitRead.Created[0].Definition == "stick" && commitRead.Created[0].Condition == .8f &&
            commitRead.Updated[0].Condition == .5f, "Atomic craft commit round trips with conditions");
        Require(ItemCraftWire.TryReadCancel(ItemCraftWire.WriteCancel(Session, Operation, 4), Session,
            out ulong cancelOperation, out int cancelRecipe) && cancelOperation == Operation && cancelRecipe == 4,
            "Craft cancellation round trips");
    }
    private static void FluidFrames()
    {
        Match("FluidPush", FluidWire.WritePush(Session, 0x1234, 0x5678, 2), "11" + SessionBytes + "3412785602");
        Match("FluidDrinkRequest", FluidWire.WriteDrink(MessageKind.FluidDrinkRequest, Session, Operation, 0x1234, 0x5678, 7),
            "12" + SessionBytes + OperationBytes + "3412785607");
        Match("FluidDrinkResult", FluidWire.WriteDrink(MessageKind.FluidDrinkResult, Session, Operation, 0x1234, 0x5678, 0),
            "13" + SessionBytes + OperationBytes + "3412785600");
    }

    private static void RejoinFrames()
    {
        RoomLaunchPlan plan = new(Session, Attempt, "normal", RoomOptions.Sandbox,
            new LaunchPlayer[] { new(1, 0), new(2, 3) });
        SessionRejoinState state = new(plan, Attempt, 1, 2, 3, Operation, ItemId, 5, 6, 7, 1, -2, true,
            speechSequence: 0x12345678);
        const string planBytes = "04" + SessionBytes + AttemptBytes + "1006026E6F726D616C010000000000000000020000000000000003";
        byte[] frame = Match("SessionRejoinState", SessionRejoinWire.WriteState(state),
            "16" + SessionBytes + AttemptBytes + "010000000200000003000000" + OperationBytes + ItemIdBytes +
            "0000803F000000C001" + "05000000000000000600000000000000070000000000000078563412" + planBytes);
        Require(SessionRejoinWire.TryReadState(frame, Session, out SessionRejoinState copy) &&
            copy.Attempt == Attempt && copy.ItemOperation == Operation && copy.DrinkOperation == ItemId &&
            copy.CraftOperation == 5 && copy.RegistrationOperation == 6 && copy.LastClientKey == 7 &&
            copy.SpeechSequence == 0x12345678 &&
            copy.HasPosition && copy.Y == -2 && copy.Plan.TryGetPlayer(2, out LaunchPlayer player) && player.Character == 3,
            "SessionRejoinState fixture decodes");
        Require(!SessionRejoinWire.TryReadState(frame, Attempt, out _), "SessionRejoinState rejects another session");

        Match("SessionRejoinRequest", SessionRejoinWire.WriteReference(MessageKind.SessionRejoinRequest, Session, Attempt),
            "15" + SessionBytes + AttemptBytes);
        Match("SessionRejoinApplied", SessionRejoinWire.WriteReference(MessageKind.SessionRejoinApplied, Session, Attempt),
            "17" + SessionBytes + AttemptBytes);
        Match("SessionRejoinCommitted", SessionRejoinWire.WriteReference(MessageKind.SessionRejoinCommitted, Session, Attempt),
            "18" + SessionBytes + AttemptBytes);
    }

    private static byte[] Match(string name, byte[]? actual, string hex)
    {
        byte[] expected = Convert.FromHexString(hex);
        if (actual is null || !expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{name} changed. Expected {hex}, actual {(actual is null ? "null" : Convert.ToHexString(actual))}.");
        }
        return expected;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
