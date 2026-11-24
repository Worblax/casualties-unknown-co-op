using System.Collections.Generic;

namespace CasualtiesCoop.Core.Items;

internal sealed class ItemReplicaStore
{
    private const int DeltaBufferLimit = 4 * AuthoritativeItemRegistry.Limit;

    private sealed class Snapshot
    {
        internal readonly Dictionary<ulong, ItemState> States = new();
        internal readonly SortedDictionary<ulong, ItemState> Deltas = new();
        internal readonly ulong StartSequence;

        internal Snapshot(ulong startSequence) => StartSequence = startSequence;
    }

    private Dictionary<ulong, ItemState> replicas = new();
    private Snapshot? snapshot;
    private ulong sequence;
    private ulong highestSeen;

    internal Dictionary<ulong, ItemState>.ValueCollection States => replicas.Values;

    internal bool TryGet(ulong id, out ItemState state) => replicas.TryGetValue(id, out state);

    internal void BeginSnapshot() => snapshot = new Snapshot(sequence);

    internal void CancelSnapshot() => snapshot = null;

    internal bool Stage(ItemState state) => snapshot is { } pending && !state.Removed &&
        pending.States.Count < AuthoritativeItemRegistry.Limit && pending.States.TryAdd(state.Id, state);

    internal bool CompleteSnapshot(ulong atSequence, ushort count)
    {
        Snapshot? candidate = snapshot;
        snapshot = null;
        if (candidate is null || count != candidate.States.Count || atSequence < candidate.StartSequence)
        {
            return false;
        }

        foreach (var delta in candidate.Deltas)
        {
            if (delta.Key <= atSequence)
            {
                continue;
            }
            if (delta.Key != atSequence + 1 || !ApplyStructural(candidate.States, delta.Value))
            {
                return false;
            }
            atSequence = delta.Key;
        }
        if (atSequence < highestSeen)
        {
            return false;
        }

        foreach (ItemState current in replicas.Values)
        {
            if (candidate.States.TryGetValue(current.Id, out ItemState saved) && SameStructure(saved, current) &&
                current.Sample > saved.Sample)
            {
                candidate.States[current.Id] = current;
            }
        }
        replicas = candidate.States;
        highestSeen = sequence = atSequence;
        return true;
    }

    internal bool ApplyDelta(ulong number, ItemState state, out ItemState? previous, out bool gap)
    {
        previous = null;
        gap = false;
        if (number <= sequence)
        {
            return false;
        }
        if (number > highestSeen)
        {
            highestSeen = number;
        }

        if (snapshot is { } pending && !pending.Deltas.ContainsKey(number))
        {
            if (pending.Deltas.Count == DeltaBufferLimit)
            {
                snapshot = null;
            }
            else
            {
                pending.Deltas.Add(number, state);
            }
        }

        if (replicas.TryGetValue(state.Id, out ItemState current))
        {
            previous = current;
        }
        if (number != sequence + 1 || !ApplyStructural(replicas, state))
        {
            gap = true;
            return false;
        }

        sequence = number;
        return true;
    }
    internal bool ApplySample(ItemState incoming, bool motion, out ItemState? previous)
    {
        previous = null;
        if (!replicas.TryGetValue(incoming.Id, out ItemState current) || !SameStructure(current, incoming) ||
            incoming.Sample < current.Sample || (motion && !incoming.Owner.IsNone))
        {
            return false;
        }

        if (incoming.Payload.Length == 0)
        {
            incoming.Payload = current.Payload;
            incoming.ClientKey = current.ClientKey;
        }
        previous = current;
        replicas[incoming.Id] = incoming;
        return true;
    }

    private static bool ApplyStructural(Dictionary<ulong, ItemState> target, ItemState incoming)
    {
        if (target.TryGetValue(incoming.Id, out ItemState current))
        {
            if (incoming.Definition != current.Definition || incoming.Revision != current.Revision + 1)
            {
                return false;
            }
            if (incoming.Removed)
            {
                return target.Remove(incoming.Id);
            }
        }
        else if (incoming.Removed || incoming.Revision != 1 || target.Count == AuthoritativeItemRegistry.Limit)
        {
            return false;
        }

        target[incoming.Id] = incoming;
        return true;
    }

    private static bool SameStructure(ItemState current, ItemState incoming) =>
        !incoming.Removed && current.Definition == incoming.Definition && current.Revision == incoming.Revision &&
        current.Owner == incoming.Owner && current.Parent == incoming.Parent && current.Slot == incoming.Slot &&
        current.Placement == incoming.Placement;
}
