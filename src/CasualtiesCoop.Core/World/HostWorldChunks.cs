using System;
using System.Collections.Generic;
using System.Diagnostics;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core.World;

internal sealed class HostWorldChunks
{
    private const int Window = 32;
    private const float ReceiptTimeout = 3f;
    private const int MaxFrames = 12;
    private const int MaxBytes = MaxFrames * 192;
    private const int ScansPerTick = 72;
    private const float TickInterval = 0.05f;
    private const int BytesPerSecond = 24 * 1024;
    private const float RepairInterval = 0.2f;
    private const float StreamChangeInterval = 0.5f;
    private const float CameraChangeInterval = 0.75f;

    private sealed class Delivery
    {
        internal uint Sequence;
        internal ulong Fingerprint;
        internal bool Force;
    }

    private readonly struct Pending
    {
        internal readonly uint Sequence;
        internal readonly float SentAt;
        internal readonly WorldChunkKey Key;
        internal Pending(uint sequence, float sentAt, WorldChunkKey key) { Sequence = sequence; SentAt = sentAt; Key = key; }
    }

    private sealed class Subscription
    {
        internal readonly WorldInterest Interest;
        internal readonly Dictionary<WorldChunkKey, Delivery> Delivery = new();
        internal readonly HashSet<WorldChunkKey> Dirty = new();
        internal readonly Queue<Pending> InFlight = new();
        internal Guid Stream;
        internal uint Sequence;
        internal uint Applied;
        internal float Opened, NextRepair, NextCameraChange;
        internal int HalfWidth, HalfHeight, Cursor;
        internal bool InitialComplete;

        internal Subscription(IWorldChunkGrid grid, WorldInterestOptions options, Guid stream, float opened)
        {
            Interest = new WorldInterest(grid.Width, grid.Height, options);
            Stream = stream;
            Opened = opened;
        }
    }

    private readonly IWorldChunkGrid grid;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly WorldInterestOptions options;
    private readonly Dictionary<ParticipantId, Subscription> subscriptions = new();
    private readonly List<WorldChunkKey> expired = new();
    private readonly HashSet<WorldChunkKey> union = new();
    private readonly Dictionary<WorldChunkKey, (ulong Hash, byte[] Frame)> encodedThisTick = new();
    private readonly ushort[] blocks = new ushort[WorldChunkWire.CellCount];
    private readonly float[] damage = new float[WorldChunkWire.CellCount];
    private readonly byte[] liquid = new byte[WorldChunkWire.CellCount];
    private int peerCursor;
    private bool countsChanged;
    private float nextTick, lastBudget;
    private float tokens = MaxBytes;

    internal HostWorldChunks(SessionPeers peers, IMonotonicClock clock, IWorldChunkGrid grid, WorldInterestOptions options)
    {
        this.grid = grid;
        this.peers = peers;
        this.clock = clock;
        this.options = options;
    }

    internal WorldChunkMetrics Metrics { get; } = new();
    internal WorldInterest? Interest(ParticipantId peer) => subscriptions.TryGetValue(peer, out var state) ? state.Interest : null;

    internal bool IsAcknowledged(ParticipantId peer, WorldChunkKey key) =>
        subscriptions.TryGetValue(peer, out var state) && state.Delivery.TryGetValue(key, out var delivery) &&
        delivery.Sequence <= state.Applied && !delivery.Force && !state.Dirty.Contains(key);

    internal void Forget(ulong address) => countsChanged |= subscriptions.Remove(peers.Identify(address));

    internal void MarkDirty(WorldChunkKey key)
    {
        foreach (Subscription state in subscriptions.Values)
            if (state.Interest.Contains(key)) state.Dirty.Add(key);
    }

    internal bool Receive(ulong sender, byte[] frame, Guid requiredStream)
    {
        ParticipantId participant = peers.Identify(sender);
        if (participant.IsNone || participant == peers.LocalParticipant || !peers.IsVerified(sender)) return false;
        if (!WorldChunkWire.TryReadControl(frame, peers.SessionId, out Guid stream, out uint applied,
            out int halfWidth, out int halfHeight, out var repair) || (requiredStream != Guid.Empty && stream != requiredStream)) return true;

        float now = clock.Now;
        if (!subscriptions.TryGetValue(participant, out var state) || state.Stream != stream)
        {
            if (applied != 0 || (state is not null && requiredStream == Guid.Empty && now - state.Opened < StreamChangeInterval)) return true;
            state = new Subscription(grid, options, stream, now);
            subscriptions[participant] = state;
            Metrics.Resets++;
            countsChanged = true;
        }

        if (applied > state.Sequence) return true;
        if (now >= state.NextCameraChange)
        {
            state.HalfWidth = halfWidth;
            state.HalfHeight = halfHeight;
            state.NextCameraChange = now + CameraChangeInterval;
        }
        if (applied > state.Applied)
        {
            state.Applied = applied;
            while (state.InFlight.Count != 0 && state.InFlight.Peek().Sequence <= applied)
            {
                var sent = state.InFlight.Dequeue();
                Metrics.LastAcknowledgedSeconds = now - sent.SentAt;
            }
        }

        if (repair is { } key && state.Interest.Contains(key) && now >= state.NextRepair)
        {
            state.NextRepair = now + RepairInterval;
            if (state.Delivery.TryGetValue(key, out var delivery)) delivery.Force = true;
            state.Dirty.Add(key);
            Metrics.Repairs++;
        }
        return true;
    }

    internal delegate bool ChunkCenter(ulong address, out int x, out int y);

    internal void Tick(ChunkCenter center)
    {
        float now = clock.Now;
        if (now < nextTick) return;
        nextTick = now + TickInterval;
        encodedThisTick.Clear();
        tokens = Math.Min(MaxBytes * 2, tokens + Math.Max(0f, now - lastBudget) * BytesPerSecond);
        lastBudget = now;
        int bytes = Math.Min(MaxBytes, (int)tokens), frames = MaxFrames, scans = ScansPerTick;
        int count = peers.Players.Count;
        bool rebuild = countsChanged;
        countsChanged = false;
        int start = peerCursor++ % count;
        for (int offset = 0; offset < count; offset++)
        {
            ulong address = peers.Players[(start + offset) % count].SteamId;
            if (!subscriptions.TryGetValue(peers.Identify(address), out var state) || !peers.IsVerified(address) ||
                !center(address, out int x, out int y)) continue;
            var orderedBefore = state.Interest.Ordered;
            WorldChunkKey? nextKey = orderedBefore.Count > 0 ? orderedBefore[state.Cursor] : null;
            if (state.Interest.Update(x, y, state.HalfWidth, state.HalfHeight))
            {
                Trim(state);
                state.Cursor = CursorAfterMovement(state.Interest.Ordered, nextKey);
                Metrics.Rebuilds++;
                rebuild = true;
            }
            RetryExpired(state, now);

            // Reserve some progress for the normal sweep even while edits arrive continually.
            int peerFrames = Math.Min(frames, count > 2 ? 4 : 8);
            int fastRemaining = Math.Max(0, peerFrames - 1);
            while (fastRemaining-- > 0 && scans > 0 && state.Dirty.Count != 0 && state.InFlight.Count < Window)
            {
                if (!TryFirstDirty(state, out WorldChunkKey key) ||
                    !TryService(address, state, key, now, ref frames, ref bytes, ref scans)) break;
                state.Dirty.Remove(key);
                peerFrames--;
            }

            var ordered = state.Interest.Ordered;
            int scanLimit = Math.Min(scans, Math.Min(ordered.Count, count > 2 ? 24 : 48));
            while (peerFrames > 0 && scanLimit-- > 0 && frames > 0 && bytes >= 56 && state.InFlight.Count < Window)
            {
                WorldChunkKey key = ordered[state.Cursor];
                int before = frames;
                if (!TryService(address, state, key, now, ref frames, ref bytes, ref scans)) break;
                state.Cursor = (state.Cursor + 1) % ordered.Count;
                if (frames != before) peerFrames--;
            }
            if (!state.InitialComplete && state.Delivery.Count == ordered.Count && state.Applied >= state.Sequence)
            {
                state.InitialComplete = true;
                Metrics.InitialSyncSeconds = now - state.Opened;
            }
        }
        RefreshCounts(rebuild);
    }

    private static int CursorAfterMovement(IReadOnlyList<WorldChunkKey> ordered, WorldChunkKey? next)
    {
        if (next is not { } key) return 0;
        for (int i = 0; i < ordered.Count; i++)
            if (ordered[i].Equals(key)) return i;
        return 0;
    }

    private static bool InFlight(Subscription state, WorldChunkKey key) =>
        state.Delivery.TryGetValue(key, out var sent) && sent.Sequence > state.Applied && !sent.Force;

    private static bool TryFirstDirty(Subscription state, out WorldChunkKey key)
    {
        foreach (WorldChunkKey candidate in state.Dirty)
        {
            if (InFlight(state, candidate)) continue;
            key = candidate;
            return true;
        }
        key = default;
        return false;
    }

    private bool TryService(ulong address, Subscription state, WorldChunkKey key, float now,
        ref int frames, ref int bytes, ref int scans)
    {
        if (frames == 0 || bytes < 56 || scans == 0 || state.Sequence == uint.MaxValue) return false;
        scans--;
        if (InFlight(state, key)) return true;
        bool cached = encodedThisTick.TryGetValue(key, out var capture);
        int width = 0, height = 0;
        ulong hash;
        if (cached) hash = capture.Hash;
        else
        {
            long captureStart = Stopwatch.GetTimestamp();
            if (!WorldChunkGrid.Extents(grid, key, out width, out height) || !grid.Read(key, blocks, damage, liquid)) return true;
            hash = WorldChunkWire.Fingerprint(blocks, damage, liquid);
            Metrics.CaptureTicks += Stopwatch.GetTimestamp() - captureStart;
            Metrics.Captures++;
        }
        if (state.Delivery.TryGetValue(key, out var previous) && previous.Fingerprint == hash && !previous.Force) return true;

        long start = Stopwatch.GetTimestamp();
        uint sequence = state.Sequence + 1;
        byte[] frame = cached ? WorldChunkWire.CopyForRecipient(capture.Frame!, state.Stream, sequence) :
            WorldChunkWire.WriteSnapshot(peers.SessionId, new(state.Stream, sequence, key, width, height, hash), blocks, damage, liquid);
        Metrics.EncodeTicks += Stopwatch.GetTimestamp() - start;
        if (!cached) encodedThisTick.Add(key, (hash, frame));
        if (frame.Length > bytes) return false;
        if (!peers.Send(address, frame))
        {
            Metrics.Backpressure++;
            return false;
        }

        frames--;
        bytes -= frame.Length;
        tokens -= frame.Length;
        Metrics.Frames++;
        Metrics.Bytes += frame.Length;
        state.Sequence = sequence;
        var delivery = previous ?? new Delivery();
        delivery.Force = false;
        delivery.Sequence = sequence;
        delivery.Fingerprint = hash;
        state.Delivery[key] = delivery;
        state.InFlight.Enqueue(new Pending(sequence, now, key));
        return true;
    }

    private void RetryExpired(Subscription state, float now)
    {
        while (state.InFlight.Count != 0 && now - state.InFlight.Peek().SentAt >= ReceiptTimeout)
        {
            var expiredDelivery = state.InFlight.Dequeue();
            if (!state.Delivery.TryGetValue(expiredDelivery.Key, out var sent) || sent.Sequence != expiredDelivery.Sequence) continue;
            sent.Force = true;
            state.Dirty.Add(expiredDelivery.Key);
            Metrics.Repairs++;
        }
    }

    private void Trim(Subscription state)
    {
        expired.Clear();
        foreach (var key in state.Delivery.Keys)
            if (!state.Interest.Contains(key)) expired.Add(key);
        foreach (var key in expired) state.Delivery.Remove(key);
        state.Dirty.RemoveWhere(key => !state.Interest.Contains(key));
    }

    private void RefreshCounts(bool rebuild)
    {
        Metrics.Pending = 0;
        if (rebuild) { union.Clear(); Metrics.Subscriptions = 0; }
        foreach (var state in subscriptions.Values)
        {
            Metrics.Pending += state.Interest.Ordered.Count - state.Delivery.Count + state.InFlight.Count + state.Dirty.Count;
            if (!rebuild) continue;
            Metrics.Subscriptions += state.Interest.Ordered.Count;
            foreach (var key in state.Interest.Ordered) union.Add(key);
        }
        if (rebuild) Metrics.ActiveChunks = union.Count;
    }
}
