using System;
using System.Collections.Generic;
using System.Diagnostics;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core.World;

internal sealed class GuestWorldChunks
{
    private const float Heartbeat = 8f;
    private const float AckInterval = 0.12f;
    private const float VerifyInterval = 0.1f;
    private const int VerifyPerTick = 8;
    private readonly IWorldChunkGrid grid;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly WorldInterest interest;
    private readonly Dictionary<WorldChunkKey, ulong> applied = new();
    private readonly List<WorldChunkKey> expired = new();
    private readonly ushort[] blocks = new ushort[WorldChunkWire.CellCount];
    private readonly float[] damage = new float[WorldChunkWire.CellCount];
    private readonly byte[] liquid = new byte[WorldChunkWire.CellCount];
    private Guid stream;
    private bool recovery, gap, announced, initialMeasured;
    private uint sequence, advertised;
    private float nextControl, nextVerify, opened;
    private int seenX, seenY, cursor;

    internal GuestWorldChunks(SessionPeers peers, IMonotonicClock clock, IWorldChunkGrid grid, WorldInterestOptions options)
    {
        this.peers = peers;
        this.clock = clock;
        this.grid = grid;
        interest = new WorldInterest(grid.Width, grid.Height, options);
    }

    internal WorldChunkMetrics Metrics { get; } = new();
    internal Guid Stream => stream;
    internal uint AppliedSequence => sequence;
    internal WorldInterest Interest => interest;

    internal void Begin(Guid id, bool recovery)
    {
        this.recovery = recovery;
        stream = id;
        gap = announced = initialMeasured = false;
        applied.Clear();
        sequence = advertised = 0;
        nextControl = nextVerify = 0;
        opened = clock.Now;
        Metrics.Resets++;
    }

    internal void CompleteRecovery() => recovery = false;

    internal bool HasNear(int x, int y, int radius)
    {
        if (stream == Guid.Empty || gap || x < 0 || x >= grid.Width || y < 0 || y >= grid.Height) return false;
        var required = ChunkRectangle.Around(x, y, radius, radius, grid.Width, grid.Height);
        for (int row = required.MinY; row <= required.MaxY; row++)
        for (int col = required.MinX; col <= required.MaxX; col++)
            if (!applied.ContainsKey(new WorldChunkKey(col, row))) return false;
        return true;
    }

    internal bool Receive(ulong sender, byte[] frame)
    {
        if (sender != peers.Host || !peers.IsVerified(sender)) return false;
        if (stream == Guid.Empty || gap ||
            !WorldChunkWire.TryReadSnapshot(frame, peers.SessionId, blocks, damage, liquid, out var header) ||
            header.Stream != stream || header.Sequence <= sequence) return true;
        if (header.Sequence != sequence + 1 || !WorldChunkGrid.Extents(grid, header.Key, out int width, out int height) ||
            width != header.Width || height != header.Height)
        {
            gap = true;
            return true;
        }
        long start = Stopwatch.GetTimestamp();
        if (!grid.Apply(header, blocks, damage, liquid))
        {
            gap = true;
            return true;
        }
        Metrics.ApplyTicks += Stopwatch.GetTimestamp() - start;
        Metrics.Applied++;
        Metrics.Frames++;
        Metrics.Bytes += frame.Length;
        sequence = header.Sequence;
        applied[header.Key] = header.Fingerprint;
        return true;
    }

    internal void Tick(int x, int y, int halfWidth, int halfHeight)
    {
        if (stream == Guid.Empty || !peers.IsVerified(peers.Host)) return;
        if (gap && !recovery) Begin(Guid.NewGuid(), recovery: false);
        if (gap) return;
        if (interest.Update(x, y, halfWidth, halfHeight))
        {
            expired.Clear();
            foreach (var key in applied.Keys)
            {
                // Host calculates interest using an older accepted pose. Keep a small guard around our local view.
                var keep = ChunkRectangle.Around(x, y, 168, 168, grid.Width, grid.Height);
                if (!interest.Contains(key) && !keep.Contains(key)) expired.Add(key);
            }
            foreach (var key in expired) applied.Remove(key);
            cursor = 0;
        }

        float now = clock.Now;
        WorldChunkKey? repair = null;
        if (now >= nextVerify && interest.Ordered.Count != 0)
        {
            nextVerify = now + VerifyInterval;
            for (int i = 0; i < Math.Min(VerifyPerTick, interest.Ordered.Count); i++)
            {
                WorldChunkKey candidate = interest.Ordered[cursor++ % interest.Ordered.Count];
                if (applied.TryGetValue(candidate, out ulong known) && grid.Read(candidate, blocks, damage, liquid) &&
                    WorldChunkWire.Fingerprint(blocks, damage, liquid) != known)
                {
                    repair = candidate;
                    break;
                }
            }
        }

        if (now < nextControl && repair is null) return;
        if (announced && advertised == sequence && halfWidth == seenX && halfHeight == seenY && repair is null &&
            now < nextControl + (sequence == 0 ? 0.5f : Heartbeat) - AckInterval) return;

        byte[] frame = WorldChunkWire.WriteControl(peers.SessionId, stream, sequence, halfWidth, halfHeight, repair);
        nextControl = now + AckInterval;
        if (!peers.Send(peers.Host, frame)) return;
        if (repair.HasValue) Metrics.Repairs++;
        advertised = sequence;
        seenX = halfWidth;
        seenY = halfHeight;
        announced = true;
        if (!initialMeasured && HasNear(x, y, 2))
        {
            initialMeasured = true;
            Metrics.InitialSyncSeconds = now - opened;
        }
    }
}
