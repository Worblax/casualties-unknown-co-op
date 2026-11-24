namespace CasualtiesCoop.Gameplay.Items;

// Derived from the vanilla Recipe/RecipeItem definitions at runtime. Core validates commits against
// this projection so the host never needs a hand-maintained recipe table.
internal sealed class CraftRecipePlan
{
    internal int Index;
    internal string ResultDefinition = string.Empty;
    internal string ResultContainerDefinition = string.Empty;
    internal bool ResultIsLiquid;
    internal int ResultAmount;
    internal CraftIngredientPlan[] Ingredients = System.Array.Empty<CraftIngredientPlan>();
}

internal readonly struct CraftIngredientPlan
{
    internal readonly bool DestroyItem;
    internal readonly bool IsLiquid;
    internal readonly bool Specific;
    internal readonly string SpecificId;
    internal readonly float MinimumCondition;
    internal readonly bool NoMaxCondition;

    internal CraftIngredientPlan(bool destroyItem, bool isLiquid, string specificId, bool specific = false,
        float minimumCondition = 0.9f, bool noMaxCondition = false)
    {
        DestroyItem = destroyItem;
        IsLiquid = isLiquid;
        Specific = specific;
        SpecificId = specificId ?? string.Empty;
        MinimumCondition = minimumCondition;
        NoMaxCondition = noMaxCondition;
    }

    internal bool EffectiveDestroy => DestroyItem && !IsLiquid;
}
