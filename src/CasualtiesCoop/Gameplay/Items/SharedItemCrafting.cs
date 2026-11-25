using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesCoop.Core.Items;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

// Guest-side craft execution. Vanilla still performs the recipe, but the host reservation gates
// it. The actual UseItem/SpawnResult outcome is observed and sent as one commit. A commit is
// retried with the same operation number until the host result arrives, so a lost UDP frame or
// reconnect cannot execute the recipe twice.
internal sealed class SharedItemCrafting
{
    private const float RetryInterval = 1f;

    private sealed class PendingCraft
    {
        internal ulong Operation;
        internal Recipe Recipe = null!;
        internal List<Item> Ingredients = new();
        internal uint[] Revisions = Array.Empty<uint>();
        internal Original[] Originals = Array.Empty<Original>();
        internal List<Original> Descendants = new();
        internal CraftExecutionScope Scope = null!;
        internal bool Executed;
        internal bool Executing;
        internal bool HostAccepted;
        internal ItemCraftCommit? Commit;
        internal float NextSend;
        internal List<(ulong ClientKey, Item Item)> Results = new();
    }

    private readonly struct Original
    {
        internal readonly ulong Id;
        internal readonly string Definition;
        internal readonly float Condition;
        internal readonly byte[] Payload;
        internal readonly ItemPlacement Placement;
        internal readonly ulong Parent;
        internal readonly byte Slot;
        internal readonly ItemLocation Location;

        internal Original(ulong id, string definition, float condition, byte[] payload, ItemPlacement placement,
            ulong parent, byte slot, ItemLocation location)
        {
            Id = id;
            Definition = definition;
            Condition = condition;
            Payload = payload;
            Placement = placement;
            Parent = parent;
            Slot = slot;
            Location = location;
        }
    }

    private readonly SharedItemReplication items;
    private readonly SharedItemObjects objects;
    private PendingCraft? pending;
    private bool executing;
    internal int LastCreatedCount { get; private set; }
    internal int LastUpdatedCount { get; private set; }
    internal int LastRemovedCount { get; private set; }
    internal int LastCompletion { get; private set; }
    internal int LastScopeUsed { get; private set; }
    internal int LastScopeCreated { get; private set; }
    internal int LastLiquidTargets { get; private set; }

    internal SharedItemCrafting(SharedItemReplication items, SharedItemObjects objects)
    {
        this.items = items;
        this.objects = objects;
    }

    internal bool IsReserved(ulong id) => pending is { } craft && craft.Originals.Any(value => value.Id == id);
    internal bool HasPending => pending is not null;

    internal bool TryBegin(Recipe recipe)
    {
        if (executing) return true;
        if (pending is { } current)
        {
            if (!current.Executed && current.HostAccepted && ReferenceEquals(current.Recipe, recipe)) return false;
            Alert(current.Executed ? "The previous craft is still waiting for the host." :
                "Another craft is waiting for the host.");
            return false;
        }
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
        var descendants = new List<Original>();
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
            (ItemPlacement placement, ulong parent, byte slot) = Placement(item);
            originals[index] = new Original(state.Id, item.id, item.condition, ItemStateCodec.Capture(item), placement,
                parent, slot, SharedItemObjects.Read(item));
        }
        var ingredientIds = new HashSet<ulong>(ids);
        var seen = new HashSet<ulong>(ids);
        foreach (ItemState replica in items.ReplicaStates)
        {
            if (replica.Removed || !seen.Add(replica.Id) || !HasAncestor(replica, ingredientIds)) continue;
            descendants.Add(new Original(replica.Id, replica.Definition, replica.Condition, replica.Payload,
                replica.Placement, replica.Parent, replica.Slot, replica.Location));
        }

        if (!items.RequestCraft(recipe.index, ids, revisions, out ulong operation))
        {
            Alert("The host did not accept the crafting request.");
            return false;
        }
        pending = new PendingCraft
        {
            Operation = operation, Recipe = recipe, Ingredients = ingredients, Revisions = revisions,
            Originals = originals, Descendants = descendants, NextSend = Time.unscaledTime + RetryInterval
        };
        return false;
    }

    internal bool OverrideIngredients(Recipe recipe, out List<Item>? ingredients)
    {
        ingredients = pending is { Executing: true } craft && ReferenceEquals(craft.Recipe, recipe)
            ? craft.Ingredients : null;
        return ingredients is not null;
    }

    internal void Tick()
    {
        PendingCraft? craft = pending;
        if (craft is null || Time.unscaledTime < craft.NextSend) return;
        if (!craft.Executed)
        {
            if (craft.HostAccepted)
            {
                Finish(craft.Recipe);
            }
            else
            {
                items.ResendCraftRequest(craft.Operation, craft.Recipe.index,
                    craft.Originals.Select(value => value.Id).ToArray(), craft.Revisions);
                craft.NextSend = Time.unscaledTime + RetryInterval;
            }
            return;
        }
        if (craft.Commit is null) return;
        items.CommitCraft(craft.Commit);
        craft.NextSend = Time.unscaledTime + RetryInterval;
    }

    internal void OnResult(ulong operation, int recipe, ItemDecision decision)
    {
        PendingCraft? craft = pending;
        if (craft is null || craft.Operation != operation || craft.Recipe.index != recipe) return;
        if (decision == ItemDecision.Accepted)
        {
            if (!craft.Executed)
            {
                craft.HostAccepted = true;
                craft.NextSend = 0;
            }
            else
            {
                pending = null;
                LastCompletion = 5;
            }
            return;
        }

        if (craft.Executed)
        {
            LastCompletion = 6;
            Reject(craft, decision, restoreLocal: decision != ItemDecision.Conflict);
            pending = null;
            Alert(decision == ItemDecision.Conflict
                ? "The host state changed; the local craft was rolled back and will be resynchronized."
                : "The host rejected the crafting result; the previous state was restored.");
        }
        else
        {
            pending = null;
            Alert("That crafting request is no longer valid.");
        }
    }

    internal void Finish(Recipe recipe)
    {
        PendingCraft? craft = pending;
        if (craft is null || craft.Executed || !ReferenceEquals(craft.Recipe, recipe) || !craft.HostAccepted) return;
        craft.Executed = true;
        craft.Executing = true;
        try
        {
            craft.Scope = CraftExecutionScope.Begin(craft.Ingredients);
            executing = true;
            craft.Recipe.TryMake();
        }
        catch (Exception)
        {
            LastCompletion = 2;
            Debug.LogWarning($"Craft {craft.Operation} ({craft.Recipe.index}) failed during local execution.");
            Reject(craft, ItemDecision.Unavailable, restoreLocal: true);
            pending = null;
            items.CancelCraft(craft.Operation, craft.Recipe.index);
            Alert("Crafting failed locally and its reservation was cancelled.");
            return;
        }
        finally
        {
            executing = false;
            craft.Executing = false;
            craft.Scope?.End();
        }

        ItemCraftCommit commit;
        try
        {
            commit = BuildCommit(craft);
        }
        catch (Exception)
        {
            LastCompletion = 4;
            Debug.LogWarning($"Craft {craft.Operation} ({craft.Recipe.index}) could not be serialized.");
            Reject(craft, ItemDecision.Unavailable, restoreLocal: true);
            pending = null;
            items.CancelCraft(craft.Operation, craft.Recipe.index);
            Alert("The crafting state could not be serialized and was restored.");
            return;
        }
        LastScopeUsed = craft.Scope.Used.Count;
        LastScopeCreated = craft.Scope.Created.Count;
        LastLiquidTargets = craft.Scope.LiquidTargets.Count;
        if (!ValidateOutcome(craft, commit))
        {
            LastCompletion = 3;
            Debug.LogWarning($"Craft {craft.Operation} ({craft.Recipe.index}) produced no valid outcome: " +
                $"used={craft.Scope.Used.Count}/{craft.Originals.Length}, created={commit.Created.Count}, " +
                $"expected={Math.Max(1, craft.Recipe.result.amount)}, liquid={craft.Recipe.result.isLiquid}, " +
                $"liquidTargets={craft.Scope.LiquidTargets.Count}.");
            Reject(craft, ItemDecision.Unavailable, restoreLocal: true);
            pending = null;
            items.CancelCraft(craft.Operation, craft.Recipe.index);
            Alert("The recipe did not complete; no commit was sent.");
            return;
        }

        craft.Commit = commit;
        LastCompletion = 1;
        items.CommitCraft(commit);
        craft.NextSend = Time.unscaledTime + RetryInterval;
    }

    private ItemCraftCommit BuildCommit(PendingCraft craft)
    {
        var commit = new ItemCraftCommit { Operation = craft.Operation, Recipe = craft.Recipe.index };
        var destroyed = new HashSet<ulong>();
        foreach (CraftExecutionScope.UsedIngredient used in craft.Scope.Used)
        {
            if (used.Destroyed) destroyed.Add(craft.Originals[used.Index].Id);
        }

        var removed = new HashSet<ulong>();
        var updated = new HashSet<ulong>();
        for (int index = 0; index < craft.Originals.Length; index++)
        {
            Original original = craft.Originals[index];
            if (destroyed.Contains(original.Id))
            {
                if (removed.Add(original.Id))
                {
                    commit.Removed.Add(original.Id);
                    items.Suppress(original.Id, true);
                }
                continue;
            }
            if (!updated.Add(original.Id)) continue;
            Item? item = items.FindReplica(original.Id);
            if (!item) throw new InvalidOperationException();
            commit.Updated.Add(UpdateItem(item, original.Id, craft.Revisions[index]));
        }

        foreach (Original descendant in craft.Descendants)
        {
            if (updated.Contains(descendant.Id) || !items.TryGetState(descendant.Id, out ItemState state) ||
                !HasAncestor(state, destroyed)) continue;
            Item? item = items.FindReplica(descendant.Id);
            if (!item) continue;
            if (updated.Add(descendant.Id)) commit.Updated.Add(UpdateItem(item, descendant.Id, state.Revision));
        }

        foreach (Item item in craft.Scope.Created)
        {
            if (!item) continue;
            (ItemPlacement placement, ulong parent, byte slot) = Placement(item);
            ulong clientKey = items.ReserveClientKey();
            items.ExpectRegistration(clientKey, item);
            craft.Results.Add((clientKey, item));
            commit.Created.Add(new CraftCreatedItem
            {
                ClientKey = clientKey, Definition = item.id, Placement = placement, Parent = parent, Slot = slot,
                Condition = item.condition, Location = SharedItemObjects.Read(item), Payload = ItemStateCodec.Capture(item)
            });
        }

        // A liquid result can be added to an existing container without creating a new item.
        foreach (WaterContainerItem container in craft.Scope.LiquidTargets)
        {
            Item? target = container.GetComponent<Item>();
            if (!target || !items.TryGetId(target, out ulong id) || commit.Removed.Contains(id) ||
                commit.Updated.Any(value => value.Id == id) || !items.TryGetState(id, out ItemState state)) continue;
            commit.Updated.Add(UpdateItem(target, id, state.Revision));
        }
        LastCreatedCount = commit.Created.Count;
        LastUpdatedCount = commit.Updated.Count;
        LastRemovedCount = commit.Removed.Count;
        return commit;
    }

    private CraftUpdatedItem UpdateItem(Item item, ulong id, uint revision)
    {
        (ItemPlacement placement, ulong parent, byte slot) = Placement(item);
        return new CraftUpdatedItem
        {
            Id = id, Revision = revision, Condition = item.condition, HasPlacement = true,
            Placement = placement, Parent = parent, Slot = slot, Location = SharedItemObjects.Read(item),
            Payload = ItemStateCodec.Capture(item)
        };
    }

    private static bool ValidateOutcome(PendingCraft craft, ItemCraftCommit commit)
    {
        if (craft.Scope.Used.Count != craft.Originals.Length) return false;
        int expected = Math.Max(1, craft.Recipe.result.amount);
        if (craft.Recipe.result.isLiquid)
        {
            if (commit.Created.Count == 0) return craft.Scope.LiquidTargets.Count > 0;
            return commit.Created.Count == expected && commit.Created.All(value =>
                value.Definition == "craftingbottle" && value.Condition >= 0f && value.Condition <= 1f);
        }
        return commit.Created.Count == expected && commit.Created.All(value =>
            value.Definition == craft.Recipe.result.id && value.Condition >= 0f && value.Condition <= 1f);
    }

    private void Reject(PendingCraft craft, ItemDecision decision, bool restoreLocal)
    {
        foreach (var result in craft.Results)
        {
            items.ForgetPending(result.ClientKey, result.Item);
            if (!result.Item) continue;
            result.Item.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(result.Item.gameObject);
        }
        if (!restoreLocal)
        {
            items.InvalidateItemSnapshot();
            foreach (Original original in craft.Originals) items.Suppress(original.Id, false);
            return;
        }

        foreach (Original original in craft.Originals)
        {
            if (original.Id == 0) continue;
            items.Suppress(original.Id, false);
            items.RestoreItem(original.Id, original.Definition, original.Payload, original.Condition,
                original.Placement, original.Parent, original.Slot, original.Location);
        }
        foreach (Original descendant in craft.Descendants.OrderBy(value => DescendantDepth(value, craft.Descendants)))
        {
            if (descendant.Id == 0) continue;
            items.Suppress(descendant.Id, false);
            items.RestoreItem(descendant.Id, descendant.Definition, descendant.Payload, descendant.Condition,
                descendant.Placement, descendant.Parent, descendant.Slot, descendant.Location);
        }
    }

    private static int DescendantDepth(Original value, List<Original> all)
    {
        int depth = 0;
        ulong parent = value.Parent;
        var seen = new HashSet<ulong>();
        while (parent != 0 && seen.Add(parent))
        {
            Original match = all.FirstOrDefault(candidate => candidate.Id == parent);
            if (match.Id == 0) break;
            depth++;
            parent = match.Parent;
        }
        return depth;
    }

    private (ItemPlacement Placement, ulong Parent, byte Slot) Placement(Item item)
    {
        Body? body = items.LocalBody;
        if (body)
        {
            for (int slot = 0; slot < body!.slots.Length; slot++)
            {
                if (body.GetItem(slot) == item) return (ItemPlacement.Inventory, 0, (byte)slot);
            }
            if (body.GetAllWearables().Contains(item)) return (ItemPlacement.Worn, 0, ItemWire.NoSlot);
        }
        if (item.TryGetParentContainer(out Container container) && container.TryGetComponent<Item>(out Item containerItem) &&
            items.TryGetId(containerItem, out ulong parent))
        {
            return (ItemPlacement.Container, parent, ItemWire.NoSlot);
        }
        return (ItemPlacement.World, 0, ItemWire.NoSlot);
    }

    private bool HasAncestor(ItemState state, HashSet<ulong> ancestors)
    {
        for (int depth = 0; depth < 32 && state.Parent != 0; depth++)
        {
            if (ancestors.Contains(state.Parent)) return true;
            if (!items.TryGetState(state.Parent, out state)) return false;
        }
        return false;
    }

    private static void Alert(string message)
    {
        if (PlayerCamera.main) PlayerCamera.main.DoAlert(message);
    }
}
