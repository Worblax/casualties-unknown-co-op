using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesCoop.Core.Items;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemCrafting
{
    private sealed class PendingCraft
    {
        internal ulong Operation;
        internal Recipe Recipe = null!;
        internal List<Item> Ingredients = new();
        internal uint[] Revisions = Array.Empty<uint>();
        internal Original[] Originals = Array.Empty<Original>();
        internal bool Commit;
    }

    private readonly struct Original
    {
        internal readonly ulong Id;
        internal readonly string Definition;
        internal readonly float Condition;
        internal readonly byte[] Payload;
        internal readonly ItemPlacement Placement;
        internal readonly byte Slot;

        internal Original(ulong id, string definition, float condition, byte[] payload, ItemPlacement placement, byte slot)
        {
            Id = id;
            Definition = definition;
            Condition = condition;
            Payload = payload;
            Placement = placement;
            Slot = slot;
        }
    }

    private readonly SharedItemReplication items;
    private readonly SharedItemObjects objects;
    private PendingCraft? pending;
    private bool executing;

    internal SharedItemCrafting(SharedItemReplication items, SharedItemObjects objects)
    {
        this.items = items;
        this.objects = objects;
    }

    internal bool IsReserved(ulong id) => pending is { } craft && craft.Originals.Any(value => value.Id == id);

    internal bool TryBegin(Recipe recipe)
    {
        if (executing) return true;
        if (pending is not null)
        {
            Alert("Another craft is waiting for the host.");
            return false;
        }
        Body? body = items.LocalBody;
        if (!items.CanCraft)
        {
            Alert("Waiting for the host to confirm shared items.");
            return false;
        }

        List<Item>? ingredients = recipe.GetItemsForRecipe();
        if (ingredients is null) return true;
        var ids = new ulong[ingredients.Count];
        var revisions = new uint[ingredients.Count];
        var originals = new Original[ingredients.Count];
        for (int index = 0; index < ingredients.Count; index++)
        {
            Item item = ingredients[index];
            if (!item || !items.TryGetId(item, out ids[index]) || !items.TryGetById(ids[index], out ItemState state) ||
                !items.CanCraftFrom(state))
            {
                Alert("A crafting ingredient is not ready for co-op.");
                return false;
            }
            revisions[index] = state.Revision;
            originals[index] = new Original(state.Id, item.id, item.condition, ItemStateCodec.Capture(item),
                state.Placement, state.Slot);
        }

        if (!items.RequestCraft(recipe.index, ids, revisions, out ulong operation))
        {
            Alert("The host did not accept the crafting request.");
            return false;
        }
        pending = new PendingCraft
        {
            Operation = operation, Recipe = recipe, Ingredients = ingredients, Revisions = revisions,
            Originals = originals
        };
        return false;
    }

    internal bool OverrideIngredients(Recipe recipe, out List<Item>? ingredients)
    {
        ingredients = pending is { Commit: false } craft && ReferenceEquals(craft.Recipe, recipe)
            ? craft.Ingredients : null;
        return ingredients is not null;
    }

    internal void OnResult(ulong operation, int recipe, ItemDecision decision)
    {
        PendingCraft? craft = pending;
        if (craft is null || craft.Operation != operation || craft.Recipe.index != recipe) return;
        if (decision != ItemDecision.Accepted)
        {
            if (craft.Commit) Rollback(craft);
            pending = null;
            Alert(craft.Commit ? "The host rejected the crafting result; ingredients were restored."
                : "That crafting request is no longer valid.");
            return;
        }
        if (craft.Commit)
        {
            pending = null;
            return;
        }

        craft.Commit = true;
        ItemCraftCommit commit = Execute(craft);
        if (!items.CommitCraft(commit))
        {
            Rollback(craft);
            pending = null;
            Alert("The host did not acknowledge the crafting result.");
        }
    }

    private ItemCraftCommit Execute(PendingCraft craft)
    {
        var before = new HashSet<int>(ItemsForScan().Select(item => item.GetInstanceID()));
        executing = true;
        try
        {
            craft.Recipe.TryMake();
        }
        finally
        {
            executing = false;
        }

        var commit = new ItemCraftCommit { Operation = craft.Operation, Recipe = craft.Recipe.index };
        var selected = new HashSet<int>(craft.Ingredients.Where(item => item).Select(item => item.GetInstanceID()));
        for (int index = 0; index < craft.Ingredients.Count; index++)
        {
            Item item = craft.Ingredients[index];
            RecipeItem requirement = craft.Recipe.items[index];
            bool consumed = requirement.destroyItem && !requirement.isLiquid;
            if (!item || consumed)
            {
                commit.Removed.Add(craft.Originals[index].Id);
                items.Suppress(craft.Originals[index].Id, true);
                continue;
            }
            if (items.TryGetId(item, out ulong id))
            {
                commit.Updated.Add(new CraftUpdatedItem
                {
                    Id = id, Revision = craft.Revisions[index], Condition = item.condition,
                    Payload = ItemStateCodec.Capture(item)
                });
            }
        }

        foreach (Item item in ItemsForScan())
        {
            if (!item || before.Contains(item.GetInstanceID()) || selected.Contains(item.GetInstanceID()) ||
                items.TryGetId(item, out _))
            {
                continue;
            }
            (ItemPlacement placement, byte slot) = Placement(item);
            ulong clientKey = items.ReserveClientKey();
            items.ExpectRegistration(clientKey, item);
            commit.Created.Add(new CraftCreatedItem
            {
                ClientKey = clientKey, Definition = item.id, Placement = placement, Parent = 0, Slot = slot,
                Location = SharedItemObjects.Read(item), Payload = ItemStateCodec.Capture(item)
            });
        }
        if (commit.Removed.Count == 0 && commit.Updated.Count == 0 && commit.Created.Count == 0 && craft.Ingredients.Count > 0)
        {
            Item first = craft.Ingredients[0];
            if (items.TryGetId(first, out ulong id))
            {
                commit.Updated.Add(new CraftUpdatedItem
                {
                    Id = id, Revision = craft.Revisions[0], Condition = first.condition,
                    Payload = ItemStateCodec.Capture(first)
                });
            }
        }
        return commit;
    }

    private static IEnumerable<Item> ItemsForScan()
    {
        var seen = new HashSet<int>();
        foreach (Item item in Item.allItems)
        {
            if (item && seen.Add(item.GetInstanceID())) yield return item;
        }
        foreach (Item item in UnityEngine.Object.FindObjectsByType<Item>(FindObjectsSortMode.None))
        {
            if (item && seen.Add(item.GetInstanceID())) yield return item;
        }
    }

    private void Rollback(PendingCraft craft)
    {
        Body? body = items.LocalBody;
        foreach (Original original in craft.Originals)
        {
            items.Suppress(original.Id, false);
            Item? existing = items.FindReplica(original.Id);
            if (existing)
            {
                ItemStateCodec.TryApply(existing!, original.Payload, original.Condition, original.Placement);
                continue;
            }
            Vector2 at = body ? body!.transform.position : Vector2.zero;
            Item? restored = objects.CreateReplica(original.Definition, new ItemLocation(at.x, at.y, 0f));
            if (!restored || !objects.Apply(restored!, original.Payload, original.Condition, original.Placement)) continue;
            items.BindRestored(original.Id, restored!);
            if (original.Placement == ItemPlacement.Inventory && body) items.AttachToSlot(restored!, original.Slot);
            else objects.PutInWorld(restored!, SharedItemObjects.Read(restored!), authoritative: false);
        }
    }

    private (ItemPlacement Placement, byte Slot) Placement(Item item)
    {
        Body? body = items.LocalBody;
        if (body)
        {
            for (int slot = 0; slot < body!.slots.Length; slot++)
            {
                if (body.GetItem(slot) == item) return (ItemPlacement.Inventory, (byte)slot);
            }
            if (body.GetAllWearables().Contains(item)) return (ItemPlacement.Worn, ItemWire.NoSlot);
        }
        return (ItemPlacement.World, ItemWire.NoSlot);
    }

    private static void Alert(string message)
    {
        if (PlayerCamera.main) PlayerCamera.main.DoAlert(message);
    }
}
