using System;
using System.Buffers.Binary;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Gameplay.Items;

internal static class ItemSnapshotWire
{
    private const int HeaderSize = 17;
    private const int TaggedSize = HeaderSize + sizeof(ulong);
    private const int DoneSize = TaggedSize + sizeof(ulong) + sizeof(ushort);

    internal static byte[] WriteRequest(Guid session, ulong snapshotId) =>
        WriteTagged(MessageKind.ItemSyncRequest, session, snapshotId, TaggedSize);

    internal static bool TryReadRequest(ReadOnlySpan<byte> frame, Guid session, out ulong snapshotId)
    {
        snapshotId = 0;
        return frame.Length == TaggedSize && TryReadTag(frame, MessageKind.ItemSyncRequest, session, out snapshotId);
    }

    internal static byte[] WriteDone(Guid session, ulong snapshotId, ulong sequence, ushort count)
    {
        byte[] frame = WriteTagged(MessageKind.ItemSyncDone, session, snapshotId, DoneSize);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(TaggedSize), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(TaggedSize + sizeof(ulong)), count);
        return frame;
    }

    internal static bool TryReadDone(ReadOnlySpan<byte> frame, Guid session, out ulong snapshotId, out ulong sequence,
        out ushort count)
    {
        snapshotId = sequence = 0;
        count = 0;
        if (frame.Length != DoneSize || !TryReadTag(frame, MessageKind.ItemSyncDone, session, out snapshotId))
        {
            return false;
        }

        sequence = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(TaggedSize));
        count = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(TaggedSize + sizeof(ulong)));
        return true;
    }

    internal static byte[] WriteState(MessageKind kind, Guid session, ulong tag, SharedItemState state)
    {
        byte[] payload = ItemWire.WriteState(MessageKind.ItemState, session, state);
        byte[] frame = WriteTagged(kind, session, tag, TaggedSize + payload.Length);
        payload.CopyTo(frame, TaggedSize);
        return frame;
    }

    internal static bool TryReadState(ReadOnlySpan<byte> frame, MessageKind kind, Guid session, out ulong tag,
        out SharedItemState state)
    {
        state = default;
        return TryReadTag(frame, kind, session, out tag) &&
            ItemWire.TryReadState(frame.Slice(TaggedSize), MessageKind.ItemState, session, out state);
    }

    private static byte[] WriteTagged(MessageKind kind, Guid session, ulong tag, int size)
    {
        byte[] frame = GameplayWire.NewFrame(kind, session, size);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(HeaderSize), tag);
        return frame;
    }

    private static bool TryReadTag(ReadOnlySpan<byte> frame, MessageKind kind, Guid session, out ulong tag)
    {
        tag = 0;
        if (frame.Length < TaggedSize || !GameplayWire.HasHeader(frame, kind, session))
        {
            return false;
        }
        tag = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(HeaderSize));
        return tag != 0;
    }
}
