using System;
using System.Buffers.Binary;

namespace CasualtiesCoop.Networking;

internal readonly struct WorldChunkKey : IEquatable<WorldChunkKey>
{
    internal readonly int X;
    internal readonly int Y;

    internal WorldChunkKey(int x, int y) { X = x; Y = y; }
    internal static WorldChunkKey At(int x, int y) => new(x / WorldChunkWire.Side, y / WorldChunkWire.Side);
    internal int StartX => X * WorldChunkWire.Side;
    internal int StartY => Y * WorldChunkWire.Side;
    public bool Equals(WorldChunkKey other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is WorldChunkKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y);
}

internal readonly struct WorldChunkHeader
{
    internal readonly Guid Stream;
    internal readonly uint Sequence;
    internal readonly WorldChunkKey Key;
    internal readonly int Width;
    internal readonly int Height;
    internal readonly ulong Fingerprint;

    internal WorldChunkHeader(Guid stream, uint sequence, WorldChunkKey key, int width, int height, ulong fingerprint)
    {
        Stream = stream;
        Sequence = sequence;
        Key = key;
        Width = width;
        Height = height;
        Fingerprint = fingerprint;
    }
}

internal static class WorldChunkWire
{
    internal const int Side = 8;
    internal const int CellCount = Side * Side;
    private const int PackedLiquids = CellCount * 3 / 8;
    private const int SnapshotHeader = 53;
    private const int ControlSize = 41;
    internal const int MaxSnapshotSize = SnapshotHeader + 2 * CellCount + 5 * CellCount + PackedLiquids;

    internal static ulong Fingerprint(ReadOnlySpan<ushort> blocks, ReadOnlySpan<float> damage, ReadOnlySpan<byte> liquids)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < CellCount; i++)
        {
            hash = Mix(hash, blocks[i]);
            hash = Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(damage[i])));
            hash = Mix(hash, liquids[i]);
        }
        return hash;
    }

    private static ulong Mix(ulong hash, uint value) => unchecked((hash ^ value) * 1099511628211UL);

    internal static byte[] WriteControl(Guid session, Guid stream, uint applied, int halfWidth, int halfHeight, WorldChunkKey? repair = null)
    {
        byte[] frame = GameplayWire.NewFrame(MessageKind.WorldChunkControl, session, ControlSize + (repair.HasValue ? 4 : 0));
        stream.TryWriteBytes(frame.AsSpan(17));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(33), applied);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(37), checked((ushort)halfWidth));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(39), checked((ushort)halfHeight));
        if (repair is { } key) WriteKey(frame.AsSpan(41), key);
        return frame;
    }

    internal static bool TryReadControl(ReadOnlySpan<byte> frame, Guid session, out Guid stream, out uint applied,
        out int halfWidth, out int halfHeight, out WorldChunkKey? repair)
    {
        stream = default;
        applied = 0;
        halfWidth = halfHeight = 0;
        repair = null;
        if ((frame.Length != ControlSize && frame.Length != ControlSize + 4) ||
            !GameplayWire.HasHeader(frame, MessageKind.WorldChunkControl, session)) return false;
        stream = new Guid(frame.Slice(17, 16));
        applied = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(33));
        halfWidth = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(37));
        halfHeight = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(39));
        if (frame.Length > ControlSize) repair = ReadKey(frame.Slice(41));
        return stream != Guid.Empty && halfWidth is > 0 and <= 512 && halfHeight is > 0 and <= 512;
    }

    internal static byte[] WriteSnapshot(Guid session, WorldChunkHeader header, ReadOnlySpan<ushort> blocks,
        ReadOnlySpan<float> damage, ReadOnlySpan<byte> liquids)
    {
        bool flatBlocks = true;
        bool flatLiquid = true;
        int cracks = 0;
        for (int index = 0; index < CellCount; index++)
        {
            flatBlocks &= blocks[index] == blocks[0];
            flatLiquid &= liquids[index] == liquids[0];
            if (damage[index] > 0) cracks++;
        }

        var frame = GameplayWire.NewFrame(MessageKind.WorldChunkSnapshot, session,
            SnapshotHeader + (flatBlocks ? 2 : CellCount * 2) + (flatLiquid ? 1 : PackedLiquids) + cracks * 5);
        header.Stream.TryWriteBytes(frame.AsSpan(17));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(33), header.Sequence);
        WriteKey(frame.AsSpan(37), header.Key);
        frame[41] = (byte)header.Width;
        frame[42] = (byte)header.Height;
        frame[43] = (byte)((flatBlocks ? 1 : 0) | (flatLiquid ? 2 : 0));
        frame[44] = (byte)cracks;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(45), header.Fingerprint);

        int cursor = SnapshotHeader;
        int blockCount = flatBlocks ? 1 : CellCount;
        for (int index = 0; index < blockCount; index++, cursor += 2)
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(cursor), blocks[index]);

        if (flatLiquid) frame[cursor++] = liquids[0];
        else
        {
            var packed = frame.AsSpan(cursor, PackedLiquids);
            for (int index = 0; index < CellCount; index++)
                Pack(packed, index, liquids[index]);
            cursor += PackedLiquids;
        }

        for (int index = 0; index < CellCount; index++)
        {
            if (damage[index] <= 0) continue;
            frame[cursor++] = (byte)index;
            GameplayWire.WriteFloat(frame.AsSpan(cursor), damage[index]);
            cursor += 4;
        }
        return frame;
    }

    internal static byte[] CopyForRecipient(byte[] snapshot, Guid stream, uint sequence)
    {
        var copy = (byte[])snapshot.Clone();
        stream.TryWriteBytes(copy.AsSpan(17));
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(33), sequence);
        return copy;
    }

    internal static bool TryReadSnapshot(ReadOnlySpan<byte> frame, Guid session, Span<ushort> blocks, Span<float> damage,
        Span<byte> liquids, out WorldChunkHeader header)
    {
        header = default;
        if (frame.Length < SnapshotHeader || frame.Length > MaxSnapshotSize ||
            !GameplayWire.HasHeader(frame, MessageKind.WorldChunkSnapshot, session)) return false;
        var stream = new Guid(frame.Slice(17, 16));
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(33));
        int width = frame[41], height = frame[42], flags = frame[43], cracks = frame[44];
        if (stream == Guid.Empty || sequence == 0 || width is < 1 or > Side || height is < 1 or > Side ||
            flags > 3 || cracks > CellCount) return false;

        bool flatBlocks = (flags & 1) != 0;
        bool flatLiquid = (flags & 2) != 0;
        int blockCount = flatBlocks ? 1 : CellCount;
        int liquidSize = flatLiquid ? 1 : PackedLiquids;
        if (frame.Length != SnapshotHeader + blockCount * 2 + liquidSize + cracks * 5) return false;

        int cursor = SnapshotHeader;
        if (flatBlocks) blocks.Fill(BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(cursor)));
        else
            for (int i = 0; i < CellCount; i++)
                blocks[i] = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(cursor + i * 2));
        cursor += blockCount * 2;

        if (flatLiquid)
        {
            if (frame[cursor] > 7) return false;
            liquids.Fill(frame[cursor]);
        }
        else
        {
            var packed = frame.Slice(cursor, PackedLiquids);
            for (int i = 0; i < CellCount; i++) liquids[i] = Unpack(packed, i);
        }
        cursor += liquidSize;
        damage.Clear();
        int previous = -1;
        for (int i = 0; i < cracks; i++)
        {
            int index = frame[cursor++];
            float value = GameplayWire.ReadFloat(frame.Slice(cursor));
            cursor += 4;
            if (index <= previous || index >= CellCount || blocks[index] == 0 || !(value is > 0 and <= 100000f)) return false;
            damage[index] = value;
            previous = index;
        }
        for (int i = 0; i < CellCount; i++)
            if ((i % Side >= width || i / Side >= height) && (blocks[i] != 0 || liquids[i] != 0 || damage[i] != 0)) return false;

        ulong fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(45));
        if (fingerprint != Fingerprint(blocks, damage, liquids)) return false;
        header = new WorldChunkHeader(stream, sequence, ReadKey(frame.Slice(37)), width, height, fingerprint);
        return true;
    }

    private static void Pack(Span<byte> packed, int index, byte value)
    {
        int bit = index * 3;
        int shift = bit & 7;
        packed[bit >> 3] |= (byte)((value & 7) << shift);
        if (shift > 5)
        {
            packed[(bit >> 3) + 1] |= (byte)((value & 7) >> (8 - shift));
        }
    }

    private static byte Unpack(ReadOnlySpan<byte> packed, int index)
    {
        int bit = index * 3;
        int shift = bit & 7;
        int value = packed[bit >> 3] >> shift;
        if (shift > 5)
        {
            value |= packed[(bit >> 3) + 1] << (8 - shift);
        }
        return (byte)(value & 7);
    }

    private static void WriteKey(Span<byte> destination, WorldChunkKey key)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, checked((ushort)key.X));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), checked((ushort)key.Y));
    }

    private static WorldChunkKey ReadKey(ReadOnlySpan<byte> source) =>
        new(BinaryPrimitives.ReadUInt16LittleEndian(source), BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2)));
}
