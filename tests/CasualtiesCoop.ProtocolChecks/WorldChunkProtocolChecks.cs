using System;
using CasualtiesCoop.Networking;

internal static class WorldChunkProtocolChecks
{
    internal static void Run()
    {
        Guid session = Guid.NewGuid(), stream = Guid.NewGuid();
        var key = new WorldChunkKey(135, 21);
        var blocks = new ushort[64];
        var water = new byte[64];
        var cracks = new float[64];
        for (int i = 0; i < blocks.Length; i++)
        {
            blocks[i] = (ushort)(1 + i % 15);
            water[i] = (byte)(i % 8);
            cracks[i] = 0.25f + i;
        }
        var fingerprint = WorldChunkWire.Fingerprint(blocks, cracks, water);
        byte[] frame = WorldChunkWire.WriteSnapshot(session, new(stream, 21, key, 8, 8, fingerprint), blocks, cracks, water);
        var readBlocks = new ushort[64];
        var readWater = new byte[64];
        var readCracks = new float[64];
        Assert(frame.Length == WorldChunkWire.MaxSnapshotSize && frame.Length <= RoomWire.MaxFrameSize, "Worst-case snapshot fits transport");
        Assert(Decode(frame, session, out var header) && header.Key.Equals(key) && header.Stream == stream &&
            header.Sequence == 21 && header.Fingerprint == fingerprint && blocks.AsSpan().SequenceEqual(readBlocks) &&
            water.AsSpan().SequenceEqual(readWater) && cracks.AsSpan().SequenceEqual(readCracks), "Mixed chunk roundtrip");
        Assert(!Decode(frame, Guid.NewGuid(), out _), "Reject other session");
        Assert(!Decode(frame.AsSpan(0, frame.Length - 1), session, out _), "Reject truncation");
        frame[41] = 9;
        Assert(!Decode(frame, session, out _), "Reject oversized chunk");
        frame[41] = 7;
        Assert(!Decode(frame, session, out _), "Reject nonempty edge padding");
        frame[41] = 8;
        frame[frame.Length - 5] = 0;
        Assert(!Decode(frame, session, out _), "Reject duplicate damage index");
        frame[frame.Length - 5] = 63;
        GameplayWire.WriteFloat(frame.AsSpan(frame.Length - 4), float.NaN);
        Assert(!Decode(frame, session, out _), "Reject nonfinite damage");

        Array.Clear(blocks, 0, 64); Array.Clear(water, 0, 64); Array.Clear(cracks, 0, 64);
        blocks[8] = 42;
        byte[] edge = WorldChunkWire.WriteSnapshot(session, new(stream, 22, key, 1, 2,
            WorldChunkWire.Fingerprint(blocks, cracks, water)), blocks, cracks, water);
        Assert(Decode(edge, session, out header) && header.Width == 1 && header.Height == 2 && readBlocks[8] == 42,
            "Partial edge roundtrip");
        Array.Clear(blocks, 0, 64);
        byte[] dry = WorldChunkWire.WriteSnapshot(session, new(stream, 23, key, 8, 8,
            WorldChunkWire.Fingerprint(blocks, cracks, water)), blocks, cracks, water);
        Assert(dry.Length == 56 && Decode(dry, session, out _), "Uniform empty chunk fits 56 bytes");
        dry[^1] = 7;
        Assert(!Decode(dry, session, out _), "Reject inconsistent fingerprint");

        byte[] request = WorldChunkWire.WriteControl(session, stream, 333, 72, 48, key);
        Assert(WorldChunkWire.TryReadControl(request, session, out Guid lease, out uint applied,
            out int w, out int h, out var repair) && lease == stream && applied == 333 && w == 72 && h == 48 &&
            repair.HasValue && repair.Value.Equals(key), "Chunk control roundtrip");

        bool Decode(ReadOnlySpan<byte> data, Guid launch, out WorldChunkHeader output) =>
            WorldChunkWire.TryReadSnapshot(data, launch, readBlocks, readCracks, readWater, out output);
    }

    private static void Assert(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
    }
}
