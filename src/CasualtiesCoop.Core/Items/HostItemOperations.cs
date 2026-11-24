using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

// The operation reports success only after the native inventory or world has the required result.
internal interface IItemWorld
{
    ItemDecision CheckPickup(ParticipantId player, ulong item, byte slot, ItemLocation? previousPosition);
    bool TryPickUp(ParticipantId player, ulong item, byte slot, out float condition);
    ItemDecision Drop(ParticipantId player, ulong item, SharedItemRequest request, out ItemLocation location,
        out float condition);
    bool TryApplyState(ulong item, byte[] payload);
    bool TryGetRecipe(int recipe, out CraftRecipePlan? plan);
    ItemDecision CheckCraftIngredient(ParticipantId player, ulong item);
    ICraftWorldTransaction? BeginCraft(ItemCraftCommit commit, CraftRecipePlan plan,
        IReadOnlyList<ItemState> ingredients);
    bool IsKnownDefinition(string definition);
    ItemDecision CheckRegistration(ParticipantId player, SharedItemRegistration registration, ItemState? parent);
    void RemoveItem(ulong item);
}

// Native changes for one craft commit. BeginCraft must validate and snapshot without mutating the
// world. TryApply must apply everything or leave the world unchanged.
internal interface ICraftWorldTransaction : IDisposable
{
    bool TryApply();
    bool Bind(IReadOnlyList<CraftCreatedResult> results, IReadOnlyList<CraftCreatedItem> specs);
    void Confirm();
}

internal sealed class HostItemOperations
{
    private readonly AuthoritativeItemRegistry items;
    private readonly IItemWorld world;
    private readonly IMonotonicClock clock;
    private readonly ItemOperationReceipts receipts = new();
    private bool executing;

    internal HostItemOperations(AuthoritativeItemRegistry items, IItemWorld world, IMonotonicClock clock)
    {
        this.items = items;
        this.world = world;
        this.clock = clock;
    }

    internal ulong LastOperation(ParticipantId player) => receipts.TryGet(player, out var receipt) ? receipt.Operation : 0;

    internal bool TryHandle(ParticipantId player, SharedItemRequest request, out ItemOperationReceipts.Receipt receipt,
        out ItemState? change, out bool replay)
    {
        change = null;
        receipt = default;
        replay = false;
        if (player.IsNone || executing || request.Operation == 0)
        {
            return false;
        }
        if (receipts.TryGet(player, out receipt) && request.Operation <= receipt.Operation)
        {
            replay = request.Operation == receipt.Operation;
            return replay;
        }

        var decision = Execute(player, request, out change);
        receipt = new ItemOperationReceipts.Receipt(request, decision);
        receipts.Record(player, receipt);
        return true;
    }

    internal ItemDecision Execute(ParticipantId player, SharedItemRequest request, out ItemState? change)
    {
        change = null;
        if (executing || player.IsNone || request.Slot == ItemWire.NoSlot ||
            !items.TryGet(request.ItemId, out ItemState state) || state.Removed ||
            state.Revision != request.Revision)
        {
            return ItemDecision.Unavailable;
        }

        executing = true;
        try
        {
            ItemDecision decision;
            float condition;
            if (request.Action == ItemAction.PickUp && state.Owner.IsNone)
            {
                decision = world.CheckPickup(player, state.Id, request.Slot,
                    items.PreviousWorldPosition(state.Id, clock.Now));
                if (decision != ItemDecision.Accepted)
                {
                    return decision;
                }
                if (items.Occupied(player, request.Slot) ||
                    !world.TryPickUp(player, state.Id, request.Slot, out condition))
                {
                    return ItemDecision.SlotOccupied;
                }

                change = items.Commit(state.Id, player, request.Slot, state.Location, condition, state.Payload);
                return ItemDecision.Accepted;
            }

            if (request.Action is not (ItemAction.Drop or ItemAction.Throw) || state.Owner != player ||
                state.Slot != request.Slot)
            {
                return ItemDecision.Unavailable;
            }

            byte[] payload = request.Payload.Length > 0 ? request.Payload : state.Payload;
            if (!ItemWire.IsPayload(payload))
            {
                return ItemDecision.Unavailable;
            }
            if (payload.Length > 0 && !world.TryApplyState(state.Id, payload))
            {
                return ItemDecision.Unavailable;
            }

            decision = world.Drop(player, state.Id, request, out ItemLocation location, out condition);
            if (decision == ItemDecision.Accepted)
            {
                change = items.Commit(state.Id, default, location, condition, payload);
            }
            return decision;
        }
        finally
        {
            executing = false;
        }
    }
}
