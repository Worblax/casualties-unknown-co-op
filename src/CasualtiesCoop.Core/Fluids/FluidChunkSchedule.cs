using System;
using System.Collections.Generic;
using CasualtiesCoop.Gameplay.Fluids;

namespace CasualtiesCoop.Core;

internal sealed class FluidChunkSchedule
{
    private const int InterestRadius = 72;
    private const int ChunkBudget = 12;
    private const float RepairInterval = 2.5f;
    private const int Size = FluidWire.ChunkSize;

    private readonly Dictionary<ulong, Dictionary<(int X, int Y), Stamp>> subscriptions = new();
    private readonly List<(int X, int Y)> expired = new();
    private readonly List<Update> candidates = new();

    private readonly struct Stamp
    {
        internal readonly uint Revision;
        internal readonly float LastSent;
        internal readonly float LastFull;

        internal Stamp(uint revision, float lastSent, float lastFull)
        {
            Revision = revision;
            LastSent = lastSent;
            LastFull = lastFull;
        }
    }

    internal readonly struct Update
    {
        internal readonly (int X, int Y) Key;
        internal readonly uint Revision;
        internal readonly bool Reliable;
        internal readonly float LastSent;
        internal readonly int Distance;

        internal Update((int X, int Y) key, uint revision, bool reliable, float lastSent, int distance)
        {
            Key = key;
            Revision = revision;
            Reliable = reliable;
            LastSent = lastSent;
            Distance = distance;
        }
    }

    internal IReadOnlyList<Update> Plan(ulong peer, int centerX, int centerY, int width, int height, float now,
        Func<(int X, int Y), uint> captureRevision)
    {
        if (!subscriptions.TryGetValue(peer, out var sent))
        {
            sent = new Dictionary<(int X, int Y), Stamp>();
            subscriptions.Add(peer, sent);
        }

        int minX = Math.Max(0, centerX - InterestRadius) / Size;
        int minY = Math.Max(0, centerY - InterestRadius) / Size;
        int maxX = Math.Min(width - 1, centerX + InterestRadius) / Size;
        int maxY = Math.Min(height - 1, centerY + InterestRadius) / Size;
        expired.Clear();
        foreach (var key in sent.Keys)
        {
            if (key.X < minX || key.X > maxX || key.Y < minY || key.Y > maxY)
            {
                expired.Add(key);
            }
        }
        foreach (var key in expired)
        {
            sent.Remove(key);
        }

        candidates.Clear();
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var key = (x, y);
                uint revision = captureRevision(key);
                bool known = sent.TryGetValue(key, out Stamp previous);
                bool reliable = !known || now - previous.LastFull >= RepairInterval;
                if (reliable || previous.Revision != revision)
                {
                    int dx = x - centerX / Size;
                    int dy = y - centerY / Size;
                    candidates.Add(new Update(key, revision, reliable, known ? previous.LastSent : -1f, dx * dx + dy * dy));
                }
            }
        }

        candidates.Sort(Compare);
        if (candidates.Count > ChunkBudget)
        {
            candidates.RemoveRange(ChunkBudget, candidates.Count - ChunkBudget);
        }
        return candidates;
    }

    internal void RecordAccepted(ulong peer, Update update, float now)
    {
        var sent = subscriptions[peer];
        float lastFull = update.Reliable ? now : sent[update.Key].LastFull;
        sent[update.Key] = new Stamp(update.Revision, now, lastFull);
    }

    internal void Forget(ulong peer) => subscriptions.Remove(peer);

    internal void Clear() => subscriptions.Clear();

    private static int Compare(Update first, Update second)
    {
        int order = first.LastSent.CompareTo(second.LastSent);
        return order != 0 ? order : first.Distance.CompareTo(second.Distance);
    }
}
