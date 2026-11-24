using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

internal sealed class AuthoritativeItemRegistry
{
    internal const int Limit = 256;
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

    internal bool HasCapacity => items.Count < Limit && nextId != ulong.MaxValue;
    internal ulong Sequence { get; private set; }

    internal bool TryRegister(string definition, ItemLocation location, float condition, out ItemState state)
    {
        state = default;
        if (!HasCapacity || !ItemWire.IsDefinition(definition))
        {
            return false;
        }

        state = new ItemState { Id = ++nextId, Revision = 1, Definition = definition, Slot = ItemWire.NoSlot,
            Condition = condition, Location = location };
        items.Add(state.Id, new Record { State = state });
        Sequence++;
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

    internal ItemLocation? PreviousWorldPosition(ulong id, float now)
    {
        Record record = items[id];
        return record.PreviousAt > 0 && now - record.PreviousAt <= PreviousPositionWindow ? record.Previous : null;
    }

    internal ItemState Commit(ulong id, ParticipantId owner, byte slot, ItemLocation location, float condition)
    {
        Record record = items[id];
        record.State.Owner = owner;
        record.State.Slot = slot;
        record.State.Location = location;
        record.State.Condition = condition;
        if (owner.IsNone)
        {
            record.WasMoving = true;
        }
        return Advance(record);
    }

    internal bool TryRemove(ulong id, out ItemState state)
    {
        state = default;
        if (!items.TryGetValue(id, out Record record))
        {
            return false;
        }

        record.State.Owner = default;
        record.State.Slot = ItemWire.NoSlot;
        record.State.Removed = true;
        state = Advance(record);
        items.Remove(id);
        return true;
    }

    internal bool TrySample(ulong id, ItemLocation at, float condition, float now, out ItemState state, out bool reliable)
    {
        state = default;
        reliable = false;
        if (!items.TryGetValue(id, out Record record) || record.State.Removed || !record.State.Owner.IsNone)
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

    private ItemState Advance(Record record)
    {
        Sequence++;
        record.State.Revision++;
        record.State.Sample = 0;
        record.PreviousAt = 0;
        return record.State;
    }
}
