using System;
using System.Collections.Generic;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

// Records what the vanilla recipe actually did during one TryMake call. The ingredient's
// destroy flag is only used as an expectation; the recorded result depends on whether the
// UnityEngine.Object became null during RecipeItem.UseItem.
internal sealed class CraftExecutionScope
{
    private static CraftExecutionScope? active;

    private readonly Dictionary<int, int> ingredientIndexes = new();
    private readonly Dictionary<int, Item> ingredientItems = new();
    private UsedIngredient? pending;
    private int pendingIndex = -1;

    internal readonly List<UsedIngredient> Used = new();
    internal readonly List<Item> Created = new();
    internal readonly List<WaterContainerItem> LiquidTargets = new();

    internal static CraftExecutionScope? Current => active;

    internal static CraftExecutionScope Begin(IReadOnlyList<Item> ingredients)
    {
        if (active is not null)
        {
            throw new InvalidOperationException("A craft execution scope is already active.");
        }
        var scope = new CraftExecutionScope();
        for (int index = 0; index < ingredients.Count; index++)
        {
            Item item = ingredients[index];
            if (!item)
            {
                continue;
            }
            int id = item.GetInstanceID();
            scope.ingredientIndexes[id] = index;
            scope.ingredientItems[id] = item;
        }
        active = scope;
        return scope;
    }

    internal void End() => active = null;

    internal void BeginUse(Item item)
    {
        pending = null;
        pendingIndex = -1;
        if (!item)
        {
            return;
        }
        int id = item.GetInstanceID();
        if (!ingredientIndexes.TryGetValue(id, out pendingIndex))
        {
            pendingIndex = -1;
            return;
        }
        ingredientItems[id] = item;
        pending = new UsedIngredient(item, index: pendingIndex);
    }

    internal void MarkDestroyed(UnityEngine.Object item)
    {
        if (pending is not null && (pending.Item == item || pending.Item.gameObject == item))
        {
            pending.Destroyed = true;
        }
    }

    internal void EndUse()
    {
        if (pending is null)
        {
            return;
        }
        // Unity reports a destroyed object as null. This is the actual vanilla outcome,
        // not the RecipeItem.destroyItem declaration.
        pending.Destroyed |= !pending.Item;
        Used.Add(pending);
        pending = null;
        pendingIndex = -1;
    }

    internal void RecordCreated(GameObject? created)
    {
        if (created == null)
        {
            return;
        }
        Item? item = created.GetComponent<Item>();
        if (item != null && !Created.Contains(item))
        {
            Created.Add(item);
        }
    }

    internal void RecordLiquid(WaterContainerItem? container, float amount)
    {
        if (!container || amount <= 0f || !container.GetComponent<Item>())
        {
            return;
        }
        if (!LiquidTargets.Contains(container))
        {
            LiquidTargets.Add(container);
        }
    }

    internal sealed class UsedIngredient
    {
        internal readonly Item Item;
        internal readonly int Index;
        internal bool Destroyed;

        internal UsedIngredient(Item item, int index)
        {
            Item = item;
            Index = index;
        }
    }
}
