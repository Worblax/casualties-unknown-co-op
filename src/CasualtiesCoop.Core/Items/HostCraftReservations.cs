using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

// Host-side craft state machine. A reservation freezes the exact ingredient instances and
// revisions. The commit is checked against the runtime vanilla Recipe before any native or
// registry mutation is attempted.
internal sealed class HostCraftReservations
{
    private const float ReservationTime = 12f;
    private const int HistoryPerPlayer = 8;

    private sealed class Reservation
    {
        internal ulong Operation;
        internal int Recipe;
        internal CraftRecipePlan Plan = null!;
        internal ulong[] Items = Array.Empty<ulong>();
        internal uint[] Revisions = Array.Empty<uint>();
        internal ItemState[] Ingredients = Array.Empty<ItemState>();
        internal float ExpiresAt;
        internal CraftOperationState State;
    }

    private readonly AuthoritativeItemRegistry items;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<ParticipantId, Reservation> active = new();
    private readonly Dictionary<ParticipantId, List<CraftOperationRecord>> history = new();
    private readonly Dictionary<ParticipantId, ulong> lastOperation = new();
    internal string LastFailure { get; private set; } = string.Empty;

    internal HostCraftReservations(AuthoritativeItemRegistry items, IMonotonicClock clock)
    {
        this.items = items;
        this.clock = clock;
    }

    internal bool TryReserve(ParticipantId player, ItemCraftRequest request, CraftRecipePlan? plan,
        Func<ParticipantId, ulong, ItemDecision> canUse, Func<ulong, bool> isConflicting, out ItemDecision decision)
    {
        if (TryReplay(player, request.Operation, out CraftOperationRecord known))
        {
            decision = known.Decision;
            return false;
        }

        if (active.TryGetValue(player, out Reservation? existing))
        {
            if (existing.Operation == request.Operation)
            {
                decision = ItemDecision.Accepted;
                return false;
            }
            return Record(player, request, CraftOperationState.Rejected, ItemDecision.Conflict, out decision);
        }

        // The terminal cache is bounded. Once an operation is older than the last processed
        // operation it must never be executed again, even if its result has been evicted.
        if (lastOperation.TryGetValue(player, out ulong last) && request.Operation <= last)
        {
            decision = ItemDecision.Conflict;
            return false;
        }

        if (plan is null || plan.Index != request.Recipe || request.ItemIds.Length is < 1 or > ItemCraftWire.MaxIngredients ||
            request.ItemIds.Length != plan.Ingredients.Length || request.ItemIds.Length != request.Revisions.Length)
        {
            return Record(player, request, CraftOperationState.Rejected, ItemDecision.Unavailable, out decision);
        }

        var revisions = new Dictionary<ulong, uint>();
        var ingredients = new ItemState[request.ItemIds.Length];
        for (int index = 0; index < request.ItemIds.Length; index++)
        {
            ulong id = request.ItemIds[index];
            if (isConflicting(id) || !items.TryGet(id, out ItemState state) || state.Removed ||
                (!items.OwnedBy(id, player) && canUse(player, id) != ItemDecision.Accepted))
            {
                return Record(player, request, CraftOperationState.Rejected, ItemDecision.Unavailable, out decision);
            }
            if (state.Revision != request.Revisions[index] || !IngredientMatches(plan.Ingredients[index], state))
            {
                return Record(player, request, CraftOperationState.Rejected, ItemDecision.Conflict, out decision);
            }
            if (revisions.TryGetValue(id, out uint previous) && previous != request.Revisions[index])
            {
                return Record(player, request, CraftOperationState.Rejected, ItemDecision.Conflict, out decision);
            }
            revisions[id] = request.Revisions[index];
            ingredients[index] = state;
        }

        var reservation = new Reservation
        {
            Operation = request.Operation, Recipe = request.Recipe, Plan = plan,
            Items = (ulong[])request.ItemIds.Clone(), Revisions = (uint[])request.Revisions.Clone(),
            Ingredients = ingredients, ExpiresAt = clock.Now + ReservationTime, State = CraftOperationState.Reserved
        };
        active[player] = reservation;
        Append(player, new CraftOperationRecord(request.Operation, request.Recipe, CraftOperationState.Reserved,
            ItemDecision.Accepted));
        decision = ItemDecision.Accepted;
        return true;
    }

    internal bool IsReserved(ulong id)
    {
        foreach (Reservation reservation in active.Values)
        {
            if (reservation.State is not (CraftOperationState.Reserved or CraftOperationState.Executing)) continue;
            foreach (ulong item in reservation.Items)
            {
                if (item == id) return true;
            }
        }
        return false;
    }

    internal bool TryGetReservation(ParticipantId player, ulong operation, out CraftRecipePlan? plan,
        out CraftOperationState state, out ItemDecision decision)
    {
        plan = null;
        decision = ItemDecision.Conflict;
        if (active.TryGetValue(player, out Reservation? reservation) && reservation.Operation == operation)
        {
            plan = reservation.Plan;
            state = reservation.State;
            decision = ItemDecision.Accepted;
            return true;
        }
        if (TryReplay(player, operation, out CraftOperationRecord known))
        {
            state = known.State;
            decision = known.Decision;
            return false;
        }
        state = CraftOperationState.Cancelled;
        return false;
    }

    internal bool TryGetIngredients(ParticipantId player, ulong operation, out ItemState[] ingredients)
    {
        ingredients = Array.Empty<ItemState>();
        if (!active.TryGetValue(player, out Reservation? reservation) || reservation.Operation != operation)
        {
            return false;
        }
        ingredients = new ItemState[reservation.Items.Length];
        for (int index = 0; index < ingredients.Length; index++)
        {
            if (!items.TryGet(reservation.Items[index], out ItemState state) || state.Removed ||
                state.Revision != reservation.Revisions[index])
            {
                return false;
            }
            ingredients[index] = state;
        }
        return true;
    }

    internal bool TryValidateCommit(ParticipantId player, ItemCraftCommit commit, CraftRecipePlan plan,
        out ItemDecision decision)
    {
        decision = ItemDecision.Conflict;
        LastFailure = "operation is not the active reservation";
        if (!active.TryGetValue(player, out Reservation? reservation) || reservation.Operation != commit.Operation ||
            reservation.Recipe != commit.Recipe || !ReferenceEquals(reservation.Plan, plan) ||
            reservation.State is not (CraftOperationState.Reserved or CraftOperationState.Executing))
        {
            return false;
        }
        if (commit.Removed.Count + commit.Updated.Count + commit.Created.Count == 0 ||
            commit.Removed.Count > ItemCraftWire.MaxCommitItems || commit.Updated.Count > ItemCraftWire.MaxCommitItems ||
            commit.Created.Count > ItemCraftWire.MaxCommitItems)
        {
            LastFailure = "commit item counts are empty or out of bounds";
            return false;
        }

        var reservationIds = new HashSet<ulong>(reservation.Items);
        var expectedRevisions = new Dictionary<ulong, uint>();
        var destroyed = new HashSet<ulong>();
        var reusable = new HashSet<ulong>();
        var liquidIngredients = new HashSet<ulong>();
        for (int index = 0; index < reservation.Items.Length; index++)
        {
            ulong id = reservation.Items[index];
            CraftIngredientPlan ingredient = plan.Ingredients[index];
            expectedRevisions[id] = reservation.Revisions[index];
            if (!items.TryGet(id, out ItemState current) || current.Removed ||
                current.Revision != reservation.Revisions[index])
            {
                LastFailure = $"ingredient {id} is missing or its revision changed";
                return false;
            }
            if (ingredient.EffectiveDestroy) destroyed.Add(id);
            else
            {
                reusable.Add(id);
                if (ingredient.IsLiquid) liquidIngredients.Add(id);
            }
        }

        var removed = new HashSet<ulong>();
        foreach (ulong id in commit.Removed)
        {
            if (!removed.Add(id) || !reservationIds.Contains(id) || !destroyed.Contains(id) ||
                !items.TryGet(id, out ItemState state) || state.Removed)
            {
                LastFailure = $"removed id {id} is invalid";
                return false;
            }
        }
        if (removed.Count != destroyed.Count || !removed.SetEquals(destroyed))
        {
            LastFailure = $"destroyed set mismatch: commit={removed.Count}, recipe={destroyed.Count}";
            return false;
        }

        var updated = new HashSet<ulong>();
        int extraLiquidTargets = 0;
        foreach (CraftUpdatedItem update in commit.Updated)
        {
            if (!updated.Add(update.Id) || removed.Contains(update.Id) ||
                !items.TryGet(update.Id, out ItemState state) || state.Removed ||
                state.Revision != update.Revision || !ItemWire.IsPayload(update.Payload) ||
                // Vanilla recipes subtract an ingredient's condition directly and may produce a
                // small negative value; the native transaction validates the actual recipe effect.
                !IsFinite(update.Condition) || update.Condition is < -1f or > 1f)
            {
                LastFailure = $"updated id {update.Id} is invalid or has a stale revision";
                return false;
            }
            if (reservationIds.Contains(update.Id))
            {
                if (!reusable.Contains(update.Id)) return false;
            }
            else
            {
                bool descendant = IsDescendantOf(update.Id, removed);
                if ((!plan.ResultIsLiquid && !descendant) || !items.OwnedBy(update.Id, player))
                {
                    LastFailure = $"extra update {update.Id} is neither a liquid target nor a detached child";
                    return false;
                }
                if (plan.ResultIsLiquid) extraLiquidTargets++;
            }
            if (!IsUpdatedPlacementValid(player, update, removed))
            {
                LastFailure = $"updated placement for {update.Id} is invalid";
                return false;
            }
        }
        foreach (ulong id in reusable)
        {
            if (!updated.Contains(id))
            {
                LastFailure = $"reusable ingredient {id} was not updated";
                return false;
            }
        }

        int expectedResults = Math.Max(1, plan.ResultAmount);
        if (plan.ResultIsLiquid)
        {
            if (commit.Created.Count is not 0 and not 1 && commit.Created.Count != expectedResults)
            {
                LastFailure = $"liquid result count {commit.Created.Count} is invalid";
                return false;
            }
            if (commit.Created.Count == 0 && extraLiquidTargets == 0 && !updated.Overlaps(liquidIngredients))
            {
                LastFailure = "liquid recipe produced neither a bottle nor a container update";
                return false;
            }
        }
        else if (commit.Created.Count != expectedResults)
        {
            LastFailure = $"result count {commit.Created.Count} != expected {expectedResults}";
            return false;
        }

        var clientKeys = new HashSet<ulong>();
        foreach (CraftCreatedItem created in commit.Created)
        {
            string expectedDefinition = plan.ResultIsLiquid ? plan.ResultContainerDefinition : plan.ResultDefinition;
            if (created.ClientKey == 0 || !clientKeys.Add(created.ClientKey) ||
                !ItemWire.IsDefinition(created.Definition) || created.Definition != expectedDefinition ||
                !ItemWire.IsPayload(created.Payload) || created.Placement > ItemPlacement.Worn ||
                !IsFinite(created.Condition) || created.Condition is < 0f or > 1f ||
                !ItemWire.IsLocationValid(created.Location))
            {
                LastFailure = $"created result {created.ClientKey} is invalid or uses the wrong definition";
                return false;
            }
            if (!IsCreatedPlacementValid(player, created))
            {
                LastFailure = $"created result {created.ClientKey} has an invalid placement";
                return false;
            }
        }

        decision = ItemDecision.Accepted;
        LastFailure = string.Empty;
        return true;
    }

    internal void MarkExecuting(ParticipantId player, ulong operation)
    {
        if (active.TryGetValue(player, out Reservation? reservation) && reservation.Operation == operation)
        {
            reservation.State = CraftOperationState.Executing;
            Update(player, operation, CraftOperationState.Executing, ItemDecision.Accepted);
        }
    }

    internal void Complete(ParticipantId player, ulong operation, ItemDecision decision,
        IEnumerable<CraftCreatedResult>? created = null)
    {
        CraftOperationState state = decision == ItemDecision.Accepted
            ? CraftOperationState.Committed : CraftOperationState.Rejected;
        if (active.TryGetValue(player, out Reservation? reservation) && reservation.Operation == operation)
        {
            active.Remove(player);
        }
        CraftOperationRecord? record = Find(player, operation);
        if (record is null)
        {
            CraftOperationRecord appended = new(operation, -1, state, decision);
            if (created is not null) appended.Created.AddRange(created);
            Append(player, appended);
            return;
        }
        record.State = state;
        record.Decision = decision;
        record.Created.Clear();
        if (created is not null) record.Created.AddRange(created);
    }

    internal void Cancel(ParticipantId player, ulong operation, int recipe)
    {
        if (!active.TryGetValue(player, out Reservation? reservation) || reservation.Operation != operation ||
            reservation.Recipe != recipe)
        {
            return;
        }
        active.Remove(player);
        Update(player, operation, CraftOperationState.Cancelled, ItemDecision.Unavailable);
    }

    internal bool TryGetResult(ParticipantId player, ulong operation, out CraftOperationRecord record)
    {
        CraftOperationRecord? found = Find(player, operation);
        record = found!;
        return found is not null && found.Terminal;
    }

    internal ulong LastOperation(ParticipantId player) =>
        lastOperation.TryGetValue(player, out ulong value) ? value : 0;

    // Terminal history is retained across a reconnect so a retried commit can replay its result.
    // Only the live reservation is cancelled when the connection is lost.
    internal void Forget(ParticipantId player)
    {
        if (active.TryGetValue(player, out Reservation? reservation))
        {
            active.Remove(player);
            Update(player, reservation.Operation, CraftOperationState.Cancelled, ItemDecision.Unavailable);
        }
    }

    internal void Tick()
    {
        float now = clock.Now;
        var expired = new List<(ParticipantId Player, ulong Operation)>();
        foreach (var pair in active)
        {
            if (pair.Value.ExpiresAt <= now) expired.Add((pair.Key, pair.Value.Operation));
        }
        foreach (var item in expired)
        {
            active.Remove(item.Player);
            Update(item.Player, item.Operation, CraftOperationState.Expired, ItemDecision.Conflict);
        }
    }

    private bool IsCreatedPlacementValid(ParticipantId player, CraftCreatedItem created)
    {
        if (created.Parent == 0)
        {
            return created.Placement switch
            {
                ItemPlacement.World => created.Slot == ItemWire.NoSlot,
                ItemPlacement.Inventory => created.Slot != ItemWire.NoSlot,
                ItemPlacement.Worn => created.Slot == ItemWire.NoSlot,
                _ => false
            };
        }
        if (created.Placement != ItemPlacement.Container || created.Slot != ItemWire.NoSlot ||
            !items.TryGet(created.Parent, out ItemState parent) || parent.Removed)
        {
            return false;
        }
        return items.OwnedBy(created.Parent, player) || parent.Owner.IsNone;
    }

    private bool IsUpdatedPlacementValid(ParticipantId player, CraftUpdatedItem update, HashSet<ulong> removed)
    {
        if (!update.HasPlacement) return true;
        if (update.Placement == ItemPlacement.Container)
        {
            if (update.Parent == 0 || update.Parent == update.Id || update.Slot != ItemWire.NoSlot ||
                removed.Contains(update.Parent) || !items.TryGet(update.Parent, out ItemState parent) || parent.Removed ||
                (!items.OwnedBy(update.Parent, player) && !parent.Owner.IsNone) || WouldCycle(update.Id, update.Parent))
                return false;
            return true;
        }
        if (update.Parent != 0) return false;
        return update.Placement switch
        {
            ItemPlacement.World => update.Slot == ItemWire.NoSlot,
            ItemPlacement.Inventory => update.Slot != ItemWire.NoSlot,
            ItemPlacement.Worn => update.Slot == ItemWire.NoSlot,
            _ => false
        };
    }

    private bool IsDescendantOf(ulong id, HashSet<ulong> parents)
    {
        for (int depth = 0; depth < 32 && items.TryGet(id, out ItemState state); depth++)
        {
            if (state.Parent == 0) return false;
            if (parents.Contains(state.Parent)) return true;
            id = state.Parent;
        }
        return false;
    }

    private bool WouldCycle(ulong id, ulong parent)
    {
        for (int depth = 0; depth < 32 && parent != 0 && items.TryGet(parent, out ItemState state); depth++)
        {
            if (parent == id) return true;
            parent = state.Parent;
        }
        return parent == id;
    }

    private static bool IngredientMatches(CraftIngredientPlan plan, ItemState state)
    {
        if (plan.IsLiquid) return true;
        if (state.Condition < plan.MinimumCondition) return false;
        if (plan.NoMaxCondition && state.Condition >= 0.991f) return false;
        return !plan.Specific || state.Definition == plan.SpecificId;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private bool TryReplay(ParticipantId player, ulong operation, out CraftOperationRecord record)
    {
        CraftOperationRecord? found = Find(player, operation);
        record = found!;
        return found is not null && found.Terminal;
    }

    private CraftOperationRecord? Find(ParticipantId player, ulong operation)
    {
        if (!history.TryGetValue(player, out List<CraftOperationRecord>? records)) return null;
        foreach (CraftOperationRecord record in records)
        {
            if (record.Operation == operation) return record;
        }
        return null;
    }

    private void Update(ParticipantId player, ulong operation, CraftOperationState state, ItemDecision decision)
    {
        Advance(player, operation);
        CraftOperationRecord? record = Find(player, operation);
        if (record is null)
        {
            Append(player, new CraftOperationRecord(operation, -1, state, decision));
            return;
        }
        record.State = state;
        record.Decision = decision;
    }

    private void Append(ParticipantId player, CraftOperationRecord record)
    {
        Advance(player, record.Operation);
        if (!history.TryGetValue(player, out List<CraftOperationRecord>? records))
        {
            history[player] = records = new List<CraftOperationRecord>();
        }
        int previous = records.FindIndex(value => value.Operation == record.Operation);
        if (previous >= 0)
        {
            records[previous] = record;
            return;
        }
        records.Add(record);
        while (records.Count > HistoryPerPlayer) records.RemoveAt(0);
    }

    private bool Record(ParticipantId player, ItemCraftRequest request, CraftOperationState state,
        ItemDecision decision, out ItemDecision result)
    {
        result = decision;
        Append(player, new CraftOperationRecord(request.Operation, request.Recipe, state, decision));
        return false;
    }

    private void Advance(ParticipantId player, ulong operation)
    {
        if (!lastOperation.TryGetValue(player, out ulong previous) || operation > previous)
        {
            lastOperation[player] = operation;
        }
    }
}
