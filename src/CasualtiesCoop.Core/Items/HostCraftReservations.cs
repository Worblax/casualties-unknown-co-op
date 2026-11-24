using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

internal sealed class HostCraftReservations
{
    private const float ReservationTime = 12f;
    private sealed class Reservation
    {
        internal ulong Operation;
        internal int Recipe;
        internal readonly HashSet<ulong> Items = new();
        internal float ExpiresAt;
    }

    private readonly AuthoritativeItemRegistry items;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<ParticipantId, Reservation> active = new();
    private readonly Dictionary<ParticipantId, (ulong Operation, ItemDecision Decision)> results = new();

    internal HostCraftReservations(AuthoritativeItemRegistry items, IMonotonicClock clock)
    {
        this.items = items;
        this.clock = clock;
    }

    internal bool TryReserve(ParticipantId player, ItemCraftRequest request, Func<int, bool> recipeExists,
        Func<ParticipantId, ulong, ItemDecision> canUse, out ItemDecision decision)
    {
        if (results.TryGetValue(player, out var previous) && request.Operation <= previous.Operation)
        {
            decision = request.Operation == previous.Operation ? previous.Decision : ItemDecision.Conflict;
            return false;
        }
        if (!recipeExists(request.Recipe) || request.ItemIds.Length is < 1 or > ItemCraftWire.MaxIngredients)
        {
            return Record(player, request.Operation, ItemDecision.Unavailable, out decision);
        }
        foreach (ulong id in request.ItemIds)
        {
            if (!items.TryGet(id, out ItemState state) || state.Removed || IsReserved(id) ||
                !items.OwnedBy(id, player) && canUse(player, id) != ItemDecision.Accepted)
            {
                return Record(player, request.Operation, ItemDecision.Unavailable, out decision);
            }
        }
        for (int index = 0; index < request.ItemIds.Length; index++)
        {
            if (!items.TryGet(request.ItemIds[index], out ItemState state) || state.Revision != request.Revisions[index])
            {
                return Record(player, request.Operation, ItemDecision.Conflict, out decision);
            }
        }
        if (active.TryGetValue(player, out Reservation? existing))
        {
            if (existing.Operation == request.Operation)
            {
                decision = ItemDecision.Accepted;
                return false;
            }
            return Record(player, request.Operation, ItemDecision.Conflict, out decision);
        }

        var reservation = new Reservation
        {
            Operation = request.Operation, Recipe = request.Recipe, ExpiresAt = clock.Now + ReservationTime
        };
        foreach (ulong id in request.ItemIds) reservation.Items.Add(id);
        active[player] = reservation;
        results[player] = (request.Operation, ItemDecision.Accepted);
        decision = ItemDecision.Accepted;
        return true;
    }
    internal bool IsReserved(ulong id)
    {
        foreach (Reservation reservation in active.Values)
        {
            if (reservation.Items.Contains(id)) return true;
        }
        return false;
    }

    internal bool TryValidateCommit(ParticipantId player, ItemCraftCommit commit, out ItemDecision decision)
    {
        decision = ItemDecision.Conflict;
        if (!active.TryGetValue(player, out Reservation? reservation) || reservation.Operation != commit.Operation ||
            reservation.Recipe != commit.Recipe)
        {
            return false;
        }
        foreach (ulong id in commit.Removed)
        {
            if (!reservation.Items.Contains(id) || !items.TryGet(id, out _)) return false;
        }
        foreach (CraftUpdatedItem update in commit.Updated)
        {
            if (!reservation.Items.Contains(update.Id) || !items.TryGet(update.Id, out ItemState state) ||
                state.Revision != update.Revision || !ItemWire.IsPayload(update.Payload))
            {
                return false;
            }
        }
        foreach (CraftCreatedItem created in commit.Created)
        {
            if (created.ClientKey == 0 || !ItemWire.IsDefinition(created.Definition) ||
                !ItemWire.IsPayload(created.Payload) || created.Parent != 0 && !items.OwnedBy(created.Parent, player))
            {
                return false;
            }
        }
        decision = ItemDecision.Accepted;
        return true;
    }

    internal void Complete(ParticipantId player, ulong operation, ItemDecision decision)
    {
        if (active.TryGetValue(player, out Reservation? reservation) && reservation.Operation == operation)
        {
            active.Remove(player);
        }
        results[player] = (operation, decision);
    }

    internal void Forget(ParticipantId player)
    {
        active.Remove(player);
        results.Remove(player);
    }

    internal void Tick()
    {
        float now = clock.Now;
        foreach (var pair in new List<KeyValuePair<ParticipantId, Reservation>>(active))
        {
            if (pair.Value.ExpiresAt <= now) active.Remove(pair.Key);
        }
    }

    private bool Record(ParticipantId player, ulong operation, ItemDecision decision, out ItemDecision result)
    {
        result = decision;
        results[player] = (operation, decision);
        return false;
    }
}