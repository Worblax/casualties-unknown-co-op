using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay.Items;



internal sealed class RecipeCatalog
{
    internal static readonly CraftRecipePlan Rope = Plan(0, "rope", new[]
    {
        new CraftIngredientPlan(true, false, string.Empty),
        new CraftIngredientPlan(true, false, string.Empty)
    });

    internal static CraftRecipePlan Plan(int index, string definition, CraftIngredientPlan[] ingredients,
        int amount = 1, bool liquid = false) => new()
    {
        Index = index, ResultDefinition = definition, ResultAmount = amount, ResultIsLiquid = liquid,
        Ingredients = ingredients
    };

    internal static bool TryGet(int recipe, out CraftRecipePlan? plan)
    {
        plan = recipe switch
        {
            0 => Plan(0, "knife", new[] { new CraftIngredientPlan(true, false, string.Empty) }),
            7 => Rope,
            1 => Plan(1, "knife", new[] { new CraftIngredientPlan(true, false, string.Empty) }),
    11 => Plan(11, "rope", new[] { new CraftIngredientPlan(true, false, string.Empty) }),
    12 => Plan(12, "rope", new[] { new CraftIngredientPlan(false, false, string.Empty) }),
            2 => Plan(2, "knife", new[] { new CraftIngredientPlan(false, false, string.Empty) }),
            _ => null
        };
        return plan is not null;
    }
}

internal sealed class NoOpCraftTransaction : ICraftWorldTransaction
{
    public bool TryApply() => true;
    public bool Bind(IReadOnlyList<CraftCreatedResult> results, IReadOnlyList<CraftCreatedItem> specs) => true;
    public void Confirm() { }
    public void Dispose() { }
}

internal sealed class CraftWorld : IItemWorld, ICraftWorldTransaction
{
    internal readonly List<ulong> Removed = new();
    internal readonly List<ulong> Updated = new();
    internal readonly List<CraftCreatedItem> Created = new();
    internal bool RejectApply;
    internal bool Prepared;

    public ItemDecision CheckPickup(ParticipantId player, ulong item, byte slot, ItemLocation? previousPosition) =>
        ItemDecision.Accepted;
    public bool TryPickUp(ParticipantId player, ulong item, byte slot, out float condition) { condition = 1; return true; }
    public ItemDecision Drop(ParticipantId player, ulong item, SharedItemRequest request, out ItemLocation location,
        out float condition) { location = request.Location; condition = 1; return ItemDecision.Accepted; }
    public bool TryApplyState(ulong item, byte[] payload) => true;
    public bool TryGetRecipe(int recipe, out CraftRecipePlan? plan) => RecipeCatalog.TryGet(recipe, out plan);
    public ItemDecision CheckCraftIngredient(ParticipantId player, ulong item) => ItemDecision.Accepted;
    public bool IsKnownDefinition(string definition) => definition is "stick" or "rock" or "knife" or "rope" or "flask";
    public ItemDecision CheckRegistration(ParticipantId player, SharedItemRegistration registration, ItemState? parent)
    {
        if (registration.Placement != ItemPlacement.Container) return ItemDecision.Accepted;
        return parent is not null && (parent.Value.Owner == player || parent.Value.Owner.IsNone)
            ? ItemDecision.Accepted : ItemDecision.Unavailable;
    }
    public void RemoveItem(ulong item)
    {
    }

    public ICraftWorldTransaction? BeginCraft(ItemCraftCommit commit, CraftRecipePlan plan,
        IReadOnlyList<ItemState> ingredients)
    {
        Prepared = true;
        Removed.Clear();
        Removed.AddRange(commit.Removed);
        Updated.Clear();
        foreach (CraftUpdatedItem update in commit.Updated) Updated.Add(update.Id);
        Created.Clear();
        Created.AddRange(commit.Created);
        return this;
    }

    public bool TryApply() => !RejectApply;
    public bool Bind(IReadOnlyList<CraftCreatedResult> results, IReadOnlyList<CraftCreatedItem> specs) => true;
    public void Confirm() { }
    public void Dispose() { }
}
