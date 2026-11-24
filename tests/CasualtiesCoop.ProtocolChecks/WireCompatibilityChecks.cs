using System;
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
        FluidFrames();
        RejoinFrames();
    }

    private static void ItemFrames()
    {
        SharedItemRequest request = new()
        {
            Operation = Operation, ItemId = ItemId, Revision = Revision, Action = ItemAction.Throw,
            Slot = 2, Location = new ItemLocation(1, -2, 4, 0.5f, -1)
        };
        byte[] frame = Match("ItemRequest", ItemWire.WriteRequest(Session, request),
            "0A" + SessionBytes + OperationBytes + ItemIdBytes + RevisionBytes + "0302" + LocationBytes);
        Require(ItemWire.TryReadRequest(frame, Session, out SharedItemRequest decoded) &&
            decoded.Operation == request.Operation && decoded.ItemId == request.ItemId &&
            decoded.Revision == request.Revision && decoded.Action == request.Action &&
            decoded.Slot == 2 && decoded.Location.Y == -2, "ItemRequest fixture decodes");
        Require(!ItemWire.TryReadRequest(frame, Attempt, out _), "ItemRequest rejects another session");
        Require(!ItemWire.TryReadRequest(frame.AsSpan(0, frame.Length - 1), Session, out _), "ItemRequest rejects truncation");

        SharedItemState state = new()
        {
            Id = ItemId, Revision = Revision, Sample = 3, Definition = "stick", Owner = Operation,
            Slot = 2, Condition = 0.75f, Location = new ItemLocation(1, -2, 4, 0.5f, -1, 8)
        };
        const string stateTail = "05000000403F" + LocationBytes + "00000041737469636B";
        frame = Match("ItemState", ItemWire.WriteState(MessageKind.ItemState, Session, state),
            "0B" + SessionBytes + ItemIdBytes + RevisionBytes + "03000000" + OperationBytes + "02" + stateTail);
        Require(ItemWire.TryReadState(frame, MessageKind.ItemState, Session, out SharedItemState copy) &&
            copy.Owner == Operation && copy.Sample == 3 && copy.Definition == "stick" && copy.Location.Spin == 8,
            "ItemState fixture decodes");
        state.Owner = 0;
        state.Slot = ItemWire.NoSlot;
        Match("ItemMotion", ItemWire.WriteState(MessageKind.ItemMotion, Session, state),
            "0D" + SessionBytes + ItemIdBytes + RevisionBytes + "030000000000000000000000FF" + stateTail);

        frame = Match("ItemResult", ItemWire.WriteResult(Session, Operation, ItemDecision.Accepted),
            "0C" + SessionBytes + OperationBytes + "01");
        Require(ItemWire.TryReadResult(frame, Session, out ulong accepted, out ItemDecision decision) &&
            accepted == Operation && decision == ItemDecision.Accepted, "ItemResult fixture decodes");
        byte[] sync = Match("ItemSyncRequest", ItemSnapshotWire.WriteRequest(Session, Operation), "0E" + SessionBytes + OperationBytes);
        Require(ItemSnapshotWire.TryReadRequest(sync, Session, out ulong requestId) && requestId == Operation &&
            !ItemSnapshotWire.TryReadRequest(sync.AsSpan(0, 17), Session, out _), "v10 request requires its snapshot ID");
        byte[] done = Match("ItemSyncDone", ItemSnapshotWire.WriteDone(Session, Operation, ItemId, 256),
            "0F" + SessionBytes + OperationBytes + ItemIdBytes + "0001");
        Require(ItemSnapshotWire.TryReadDone(done, Session, out ulong snapshotId, out ulong sequence, out ushort count) &&
            snapshotId == Operation && sequence == ItemId && count == 256 &&
            !ItemSnapshotWire.TryReadDone(done.AsSpan(0, 17), Session, out _, out _, out _), "v10 completion carries its boundary");
        string payload = "0B" + SessionBytes + ItemIdBytes + RevisionBytes + "030000000000000000000000FF" + stateTail;
        Match("ItemDelta", ItemSnapshotWire.WriteState(MessageKind.ItemDelta, Session, Operation, state),
            "19" + SessionBytes + OperationBytes + payload);
        byte[] entry = Match("ItemSnapshotEntry", ItemSnapshotWire.WriteState(MessageKind.ItemSnapshotEntry, Session, Operation, state),
            "1A" + SessionBytes + OperationBytes + payload);
        Require(ItemSnapshotWire.TryReadState(entry, MessageKind.ItemSnapshotEntry, Session, out ulong entryId, out _) &&
            entryId == Operation && !ItemSnapshotWire.TryReadState(entry, MessageKind.ItemDelta, Session, out _, out _),
            "Snapshot entries and live deltas cannot be confused");
    }

    private static void FluidFrames()
    {
        byte[] cells = new byte[FluidWire.PackedSize];
        FluidWire.Pack(cells, 0, 1);
        FluidWire.Pack(cells, 2, 7);
        FluidWire.Pack(cells, 3, 5);
        FluidWire.Pack(cells, 1023, 4);

        byte[] frame = Match("FluidChunk", FluidWire.WriteChunk(Session, 0x1234, 0x5678, Revision, cells),
            "10" + SessionBytes + "34127856" + RevisionBytes + "C10B" + new string('0', 762) + "80");
        Require(FluidWire.TryReadChunk(frame, Session, out int x, out int y, out uint revision, out int offset) &&
            x == 0x1234 && y == 0x5678 && revision == Revision && offset == 25 &&
            FluidWire.Unpack(frame.AsSpan(offset), 2) == 7 && FluidWire.Unpack(frame.AsSpan(offset), 1023) == 4,
            "FluidChunk fixture decodes");
        Require(!FluidWire.TryReadChunk(frame, Attempt, out _, out _, out _, out _), "FluidChunk rejects another session");

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
        SessionRejoinState state = new(plan, Attempt, 1, 2, 3, Operation, ItemId, 1, -2, true);
        const string planBytes = "04" + SessionBytes + AttemptBytes + "1006026E6F726D616C010000000000000000020000000000000003";
        byte[] frame = Match("SessionRejoinState", SessionRejoinWire.WriteState(state),
            "16" + SessionBytes + AttemptBytes + "010000000200000003000000" + OperationBytes + ItemIdBytes +
            "0000803F000000C001" + planBytes);
        Require(SessionRejoinWire.TryReadState(frame, Session, out SessionRejoinState copy) &&
            copy.Attempt == Attempt && copy.ItemOperation == Operation && copy.DrinkOperation == ItemId &&
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
