using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

internal sealed class AuthoritativeItemRegistry
{
    internal const int Limit = 4096;
    private const float RefreshInterval = 3f;
    private const float PreviousPositionWindow = 0.22f;

    private sealed class Record
    {
        internal ItemState State;
        internal bool WasMoving;
        internal float NextRefresh;
        internal ItemLocation Previous;
        internal float PreviousAt;
    }

    private readonly Dictionary<ulong, Record> items = new();
    private ulong nextId;

    internal bool HasCapacity => HasCapacityFor(1);
    internal bool HasCapacityFor(int count) =>
        count >= 0 && count <= Limit - items.Count && nextId <= ulong.MaxValue - (ulong)count;
    internal int Count => items.Count;
    internal ulong Sequence { get; private set; }

    internal Transaction BeginTransaction() => new(this, Sequence, nextId);

    // Registry-only snapshot. Native Unity objects are staged by the caller and committed after this succeeds.
    internal sealed class Transaction
    {
        private readonly AuthoritativeItemRegistry registry;
        private readonly Dictionary<ulong, Record> snapshot;
        private readonly ulong nextId;
        private readonly ulong sequence;
        private bool active;

        internal Transaction(AuthoritativeItemRegistry registry, ulong sequence, ulong nextId)
        {
            this.registry = registry;
            this.sequence = sequence;
            this.nextId = nextId;
            snapshot = new Dictionary<ulong, Record>(registry.items.Count);
            foreach (var pair in registry.items)
            {
                snapshot.Add(pair.Key, Clone(pair.Value));
            }
            active = true;
        }

        internal void Commit() => active = false;

        internal void Rollback()
        {
            if (!active)
            {
                return;
            }
            active = false;
            registry.items.Clear();
            foreach (var pair in snapshot)
            {
                registry.items.Add(pair.Key, pair.Value);
            }
            registry.nextId = nextId;
            registry.Sequence = sequence;
        }

        private static Record Clone(Record source) => new()
        {
            State = source.State,
            WasMoving = source.WasMoving,
            NextRefresh = source.NextRefresh,
            Previous = source.Previous,
            PreviousAt = source.PreviousAt
        };
    }

    internal bool TryRegister(string definition, ItemLocation location, float condition, out ItemState state) =>
        TryRegister(definition, ItemPlacement.World, default, 0, ItemWire.NoSlot, location, condition,
            Array.Empty<byte>(), 0, out state);

    internal bool TryRegister(string definition, ItemPlacement placement, ParticipantId owner, ulong parent, byte slot,
        ItemLocation location, float condition, byte[] payload, ulong clientKey, out ItemState state)
    {
        state = default;
        if (!HasCapacity || !ItemWire.IsDefinition(definition) || !ItemWire.IsPayload(payload) ||
            !IsPlacementValid(placement, owner, parent, slot))
        {
            return false;
        }

        state = new ItemState
        {
            Id = ++nextId, Revision = 1, Definition = definition, Owner = owner, Parent = parent, Slot = slot,
            ClientKey = clientKey, Placement = placement, Condition = condition, Location = location, Payload = payload
        };
        Sequence++;
        state.Sequence = Sequence;
        items.Add(state.Id, new Record { State = state });
        return true;
    }

    internal bool TryGet(ulong id, out ItemState state)
    {
        bool found = items.TryGetValue(id, out Record record);
        state = found ? record.State : default;
        return found;
    }

    internal ItemState[] CaptureSnapshot(out ulong sequence)
    {
        sequence = Sequence;
        var snapshot = new ItemState[items.Count];
        int index = 0;
        foreach (Record record in items.Values)
        {
            snapshot[index++] = record.State;
        }
        return snapshot;
    }

    internal ulong RootId(ulong id)
    {
        ulong root = id;
        for (int depth = 0; depth < 32 && items.TryGetValue(root, out Record? record); depth++)
        {
            if (record.State.Parent == 0) return root;
            root = record.State.Parent;
        }
        return root;
    }
    internal bool OwnedBy(ulong id, ParticipantId player)
    {
        for (int depth = 0; depth < 32 && items.TryGetValue(id, out Record? record); depth++)
        {
            ItemState state = record.State;
            if (state.Owner == player) return true;
            if (state.Parent == 0) return false;
            id = state.Parent;
        }
        return false;
    }
    internal bool Occupied(ParticipantId player, byte slot)
    {
        foreach (Record record in items.Values)
        {
            if (record.State.Owner == player && record.State.Slot == slot && !record.State.Removed)
            {
                return true;
            }
        }
        return false;
    }

    internal bool OccupiedExcept(ParticipantId player, byte slot, ulong item)
    {
        foreach (Record record in items.Values)
        {
            if (record.State.Id != item && record.State.Owner == player && record.State.Slot == slot &&
                !record.State.Removed)
                return true;
        }
        return false;
    }

    internal ItemLocation? PreviousWorldPosition(ulong id, float now)
    {
        if (!items.TryGetValue(id, out Record? record))
        {
            return null;
        }
        return record.PreviousAt > 0 && now - record.PreviousAt <= PreviousPositionWindow ? record.Previous : null;
    }

    internal ItemState Commit(ulong id, ParticipantId owner, ItemLocation location, float condition, byte[] payload)
    {
        Record record = items[id];
        record.State.Owner = owner;
        record.State.Parent = 0;
        record.State.Slot = ItemWire.NoSlot;
        record.State.Placement = owner.IsNone ? ItemPlacement.World : ItemPlacement.Inventory;
        record.State.Location = location;
        record.State.Condition = condition;
        record.State.Payload = payload;
        if (owner.IsNone)
        {
            record.WasMoving = true;
        }
        return Advance(record);
    }

    internal ItemState Commit(ulong id, ParticipantId owner, byte slot, ItemLocation location, float condition,
        byte[] payload) => Commit(id, owner, location, condition, payload, slot, ItemPlacement.Inventory);

    internal ItemState Commit(ulong id, ParticipantId owner, ItemLocation location, float condition, byte[] payload,
        byte slot, ItemPlacement placement)
    {
        Record record = items[id];
        record.State.Owner = owner;
        record.State.Parent = 0;
        record.State.Slot = slot;
        record.State.Placement = placement;
        record.State.Location = location;
        record.State.Condition = condition;
        record.State.Payload = payload;
        if (owner.IsNone)
        {
            record.WasMoving = true;
        }
        return Advance(record);
    }

    internal ItemState CommitPlacement(ulong id, ItemPlacement placement, ParticipantId owner, ulong parent, byte slot,
        ItemLocation location, float condition, byte[] payload)
    {
        Record record = items[id];
        record.State.Placement = placement;
        record.State.Owner = owner;
        record.State.Parent = parent;
        record.State.Slot = slot;
        record.State.Location = location;
        record.State.Condition = condition;
        record.State.Payload = payload;
        record.State.ClientKey = 0;
        record.WasMoving = placement == ItemPlacement.World;
        return Advance(record);
    }

    internal bool TryCommitPayload(ulong id, uint revision, float condition, byte[] payload, out ItemState state)
    {
        state = default;
        if (!items.TryGetValue(id, out Record record) || record.State.Removed || record.State.Revision != revision ||
            !ItemWire.IsPayload(payload))
        {
            return false;
        }
        record.State.Payload = payload;
        record.State.Condition = condition;
        state = Advance(record);
        return true;
    }

    internal bool TryCommitCraftUpdate(CraftUpdatedItem update, ParticipantId player, out ItemState state)
    {
        state = default;
        if (!items.TryGetValue(update.Id, out Record record) || record.State.Removed ||
            record.State.Revision != update.Revision || !ItemWire.IsPayload(update.Payload) ||
            float.IsNaN(update.Condition) || float.IsInfinity(update.Condition))
            return false;
        if (update.HasPlacement)
        {
            bool valid = update.Placement switch
            {
                ItemPlacement.World => update.Parent == 0 && update.Slot == ItemWire.NoSlot,
                ItemPlacement.Inventory => update.Parent == 0 && update.Slot != ItemWire.NoSlot,
                ItemPlacement.Worn => update.Parent == 0 && update.Slot == ItemWire.NoSlot,
                ItemPlacement.Container => update.Parent != 0 && update.Slot == ItemWire.NoSlot,
                _ => false
            };
            if (!valid) return false;
            record.State.Placement = update.Placement;
            record.State.Owner = update.Placement is ItemPlacement.Inventory or ItemPlacement.Worn ? player : default;
            record.State.Parent = update.Parent;
            record.State.Slot = update.Slot;
            record.State.Location = update.Location;
            record.State.ClientKey = 0;
        }
        record.State.Condition = update.Condition;
        record.State.Payload = update.Payload;
        state = Advance(record);
        return true;
    }

    internal bool TryRemove(ulong id, out ItemState state)
    {
        state = default;
        if (!items.TryGetValue(id, out Record record))
        {
            return false;
        }

        record.State.Owner = default;
        record.State.Parent = 0;
        record.State.Slot = ItemWire.NoSlot;
        record.State.Placement = ItemPlacement.World;
        record.State.Removed = true;
        state = Advance(record);
        items.Remove(id);
        return true;
    }

    internal bool TrySample(ulong id, ItemLocation at, float condition, float now, out ItemState state, out bool reliable)
    {
        state = default;
        reliable = false;
        if (!items.TryGetValue(id, out Record record) || record.State.Removed || !record.State.Owner.IsNone ||
            record.State.Placement != ItemPlacement.World)
        {
            return false;
        }

        ItemLocation old = record.State.Location;
        float dx = at.X - old.X;
        float dy = at.Y - old.Y;
        bool moving = at.VelocityX * at.VelocityX + at.VelocityY * at.VelocityY > 0.0025f || Math.Abs(at.Spin) > 0.5f;
        bool stopped = record.WasMoving && !moving;
        bool changed = dx * dx + dy * dy > 0.0001f || Math.Abs(((at.Angle - old.Angle) % 360f + 540f) % 360f - 180f) > 0.25f;
        bool refresh = now >= record.NextRefresh;
        record.WasMoving = moving;
        if (!moving && !stopped && !changed && !refresh)
        {
            return false;
        }

        record.Previous = old;
        record.PreviousAt = now;
        record.State.Location = at;
        record.State.Condition = condition;
        record.State.Sample++;
        state = record.State;
        reliable = stopped || refresh;
        if (reliable)
        {
            record.NextRefresh = now + RefreshInterval;
        }
        return true;
    }

    private static bool IsPlacementValid(ItemPlacement placement, ParticipantId owner, ulong parent, byte slot) => placement switch
    {
        ItemPlacement.World => owner.IsNone && parent == 0 && slot == ItemWire.NoSlot,
        ItemPlacement.Inventory => !owner.IsNone && parent == 0 && slot != ItemWire.NoSlot,
        ItemPlacement.Worn => !owner.IsNone && parent == 0 && slot == ItemWire.NoSlot,
        ItemPlacement.Container => owner.IsNone && parent != 0 && slot == ItemWire.NoSlot,
        _ => false
    };

    private ItemState Advance(Record record)
    {
        Sequence++;
        record.State.Sequence = Sequence;
        record.State.Revision++;
        record.State.Sample = 0;
        record.PreviousAt = 0;
        return record.State;
    }
}
