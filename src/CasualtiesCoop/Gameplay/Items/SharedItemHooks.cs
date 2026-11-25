using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Session;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CasualtiesCoop.Gameplay.Items;

internal static class SharedItemHooks
{
    private static readonly Color PendingTint = new(1f, 1f, 1f, 0.5f);
    private static SharedItemReplication? testItems;
    private static SharedItemReplication? Items => CoopRuntime.IsTestMode ? testItems : CoopRuntime.ActiveWorld?.Items;
    internal static SharedItemReplication? TestItems => testItems;

    internal static void BindTestItems(SharedItemReplication? items)
    {
        if (!CoopRuntime.IsTestMode)
        {
            throw new InvalidOperationException("Item test hooks require an opt-in test process.");
        }
        testItems = items;
    }

    internal static void Install(Harmony harmony)
    {
        Prefix(harmony, typeof(Body), nameof(Body.PickUpItem), nameof(Pickup), typeof(Item), typeof(int), typeof(bool));
        Prefix(harmony, typeof(Body), nameof(Body.DoPickupCheck), nameof(AuthoritativePickupCheck), typeof(Item), typeof(bool));
        Prefix(harmony, typeof(Body), nameof(Body.DropItem), nameof(Drop), typeof(Item));
        Prefix(harmony, typeof(Body), nameof(Body.ThrowItem), nameof(Throw), typeof(float));
        Prefix(harmony, typeof(Body), nameof(Body.SwapSlots), nameof(Swap), typeof(int), typeof(int));
        Prefix(harmony, typeof(Body), nameof(Body.UseItemInHand), nameof(UseHand));
        Prefix(harmony, typeof(Body), nameof(Body.UseItem), nameof(Use), typeof(Item));
        Prefix(harmony, typeof(Body), nameof(Body.WearWearable), nameof(Placement), typeof(Item));
        Prefix(harmony, typeof(Body), nameof(Body.CombineItems), nameof(Combine), typeof(Item), typeof(Item));
        Prefix(harmony, typeof(Container), nameof(Container.LoadItem), nameof(Placement), typeof(Item));
        Prefix(harmony, typeof(TraderScript), nameof(TraderScript.GiveItem), nameof(Use), typeof(Item));
        Prefix(harmony, typeof(PlayerCamera), nameof(PlayerCamera.ApplyWoundItem), nameof(Use), typeof(Item));
        Prefix(harmony, typeof(PlayerCamera), "TryPerformInventoryAction", nameof(InventoryAction),
            typeof(RaycastResult), typeof(List<RaycastResult>));
        Prefix(harmony, typeof(PlayerCamera), "TryPerformWorldActions", nameof(WorldActions));
        Prefix(harmony, typeof(Recipe), nameof(Recipe.TryMake), nameof(Craft));
        Postfix(harmony, typeof(Recipe), nameof(Recipe.TryMake), nameof(CraftFinished));
        Prefix(harmony, typeof(Recipe), nameof(Recipe.GetItemsForRecipe), nameof(CraftIngredients));
        Prefix(harmony, typeof(RecipeItem), nameof(RecipeItem.GetMatchingItem), nameof(FilterIngredients),
            typeof(List<Item>), typeof(List<Item>));
        Prefix(harmony, typeof(RecipeItem), nameof(RecipeItem.UseItem), nameof(BeforeUseItem), typeof(Item));
        Postfix(harmony, typeof(RecipeItem), nameof(RecipeItem.UseItem), nameof(AfterUseItem), typeof(Item));
        Prefix(harmony, typeof(RecipeResult), nameof(RecipeResult.SpawnResult), nameof(BeforeSpawnResult), typeof(int));
        Postfix(harmony, typeof(RecipeResult), nameof(RecipeResult.SpawnResult), nameof(AfterSpawnResult), typeof(int));
        Prefix(harmony, typeof(Utils), nameof(Utils.Create), nameof(BeforeCreate), typeof(string), typeof(Vector2), typeof(float));
        Postfix(harmony, typeof(Utils), nameof(Utils.Create), nameof(AfterCreate), typeof(string), typeof(Vector2), typeof(float));
        harmony.Patch(AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy),
                new[] { typeof(UnityEngine.Object) }),
            prefix: new HarmonyMethod(typeof(SharedItemHooks), nameof(BeforeDestroy)));
        Prefix(harmony, typeof(Item), "Update", nameof(WorldItem));
        harmony.Patch(AccessTools.Method(typeof(Item), "Start"),
            postfix: new HarmonyMethod(typeof(SharedItemHooks), nameof(Started)));
        Postfix(harmony, typeof(Body), nameof(Body.UseItem), nameof(MarkArgument), typeof(Item));
        Postfix(harmony, typeof(Body), nameof(Body.UseItemInHand), nameof(MarkHand));
        Postfix(harmony, typeof(Body), nameof(Body.WearWearable), nameof(MarkArgument), typeof(Item));
        Postfix(harmony, typeof(Body), nameof(Body.CombineItems), nameof(MarkPair), typeof(Item), typeof(Item));
        Postfix(harmony, typeof(LightItem), "Start", nameof(ComponentStarted));
        Postfix(harmony, typeof(CustomItemBehaviour), "Start", nameof(ComponentStarted));
        Postfix(harmony, typeof(Container), nameof(Container.LoadItem), nameof(MarkContainer), typeof(Item));
        Postfix(harmony, typeof(Container), nameof(Container.UnloadItem), nameof(MarkComponent), typeof(Item), typeof(Body));
        Postfix(harmony, typeof(GunScript), nameof(GunScript.TryRack), nameof(MarkComponent));
        Postfix(harmony, typeof(GunScript), nameof(GunScript.ToggleSafety), nameof(MarkComponent));
        Postfix(harmony, typeof(GunScript), nameof(GunScript.LoadMag), nameof(MarkComponent), typeof(AmmoScript));
        Postfix(harmony, typeof(GunScript), nameof(GunScript.UnloadMag), nameof(MarkComponent));
        Postfix(harmony, typeof(AmmoScript), nameof(AmmoScript.LoadRound), nameof(MarkComponent), typeof(AmmoScript));
        Postfix(harmony, typeof(AmmoScript), nameof(AmmoScript.UnloadRound), nameof(MarkComponent));
        Postfix(harmony, typeof(BatteryItem), nameof(BatteryItem.LoadBattery), nameof(MarkComponent), typeof(Item));
        Postfix(harmony, typeof(BatteryItem), nameof(BatteryItem.UnloadBattery), nameof(MarkComponent), typeof(bool));
        Postfix(harmony, typeof(WaterContainerItem), nameof(WaterContainerItem.AddLiquid), nameof(MarkComponent),
            typeof(string), typeof(float));
        Postfix(harmony, typeof(WaterContainerItem), nameof(WaterContainerItem.Drain), nameof(MarkComponent), typeof(List<float>));
        Postfix(harmony, typeof(WaterContainerItem), nameof(WaterContainerItem.DrainAll), nameof(MarkComponent));
        Postfix(harmony, typeof(LiquidTransfer), nameof(LiquidTransfer.Finish), nameof(MarkTransfer));
        harmony.Patch(AccessTools.Method(typeof(InvButton), nameof(InvButton.UpdateGraphic)),
            postfix: new HarmonyMethod(typeof(SharedItemHooks), nameof(DrawPending)));
    }

    private static void Postfix(Harmony harmony, Type type, string method, string patch, params Type[] types) =>
        harmony.Patch(AccessTools.Method(type, method, types), postfix: new HarmonyMethod(typeof(SharedItemHooks), patch));
    private static void Prefix(Harmony harmony, Type type, string method, string patch, params Type[] types) =>
        harmony.Patch(AccessTools.Method(type, method, types), prefix: new HarmonyMethod(typeof(SharedItemHooks), patch));

    private static void Started(Item __instance) => Items?.ItemStarted(__instance);
    private static void ComponentStarted(Component __instance) => Items?.ItemStarted(__instance.GetComponent<Item>());
    private static void MarkArgument(Item __0) => Items?.MarkDirty(__0);
    private static void MarkPair(Item it1, Item it2) { Items?.MarkDirty(it1); Items?.MarkDirty(it2); }
    private static void MarkComponent(Component __instance) => Items?.MarkDirty(__instance.GetComponent<Item>());
    private static void MarkContainer(Container __instance, Item item)
    {
        Items?.MarkDirty(item);
        Items?.MarkDirty(__instance.GetComponent<Item>());
    }
    private static void MarkHand(Body __instance) => Items?.MarkDirty(__instance.GetItem(__instance.handSlot));
    private static void MarkTransfer(LiquidTransfer __instance)
    {
        Items?.MarkDirty(__instance.transferTo ? __instance.transferTo.GetComponent<Item>() : null);
        Items?.MarkDirty(__instance.transferFrom ? __instance.transferFrom.GetComponent<Item>() : null);
    }
    private static bool Pickup(Body __instance, Item item, int slot, bool force) =>
        Items?.MayPickup(__instance, item, slot, force) ?? true;

    private static bool AuthoritativePickupCheck(Item item, ref bool __result)
    {
        if (Items is not { ApplyingAuthoritativeChange: true } shared || !shared.IsTracked(item))
        {
            return true;
        }

        __result = true;
        return false;
    }

    private static bool Drop(Body __instance, Item item) => Items?.MayDrop(__instance, item, false) ?? true;

    private static bool Throw(Body __instance, float force) =>
        Items?.MayDrop(__instance, __instance.GetItem(__instance.handSlot), true, force) ?? true;

    private static bool Swap(Body __instance, int slot1, int slot2)
    {
        if (Items is not { } shared || shared.ApplyingAuthoritativeChange) return true;
        return shared.TryNativeSwap(__instance, slot1, slot2);
    }

    private static bool UseHand(Body __instance)
    {
        Item item = __instance.GetItem(__instance.handSlot);
        return !item || !item.Stats.usableWithLMB || (Items?.CanUse(item) ?? true);
    }

    private static bool Use(Item __0) => Items?.CanUse(__0) ?? true;
    private static bool Placement(Item __0) => Items?.CanChangePlacement(__0) ?? true;

    private static bool Combine(Item it1, Item it2) => Items is not { } shared || (shared.CanUse(it1) && shared.CanUse(it2));

    private static bool InventoryAction(PlayerCamera __instance, RaycastResult hit, List<RaycastResult> uiCasts, ref bool __result)
    {
        if (Items is not { } shared || !hit.gameObject.TryGetComponent<InvButton>(out InvButton slot) || !slot.Overlaps(uiCasts))
        {
            return true;
        }

        Item dragged = __instance.dragItem;
        if (!shared.IsTracked(dragged) && !shared.IsTracked(slot.GetItem()) && (!slot.isBody || !shared.IsReserved(slot.slot)))
        {
            return true;
        }

        if (slot.isBody && dragged)
        {
            Body body = __instance.body;
            if (body.HoldingItem(dragged))
            {
                int source = body.SlotOf(dragged);
                if (source >= 0 && source != slot.slot) body.SwapSlots(source, slot.slot);
            }
            else if (shared.IsTracked(dragged) && !body.HoldingItem(slot.slot))
            {
                body.PickUpItem(dragged, slot.slot);
            }
        }

        __result = true;
        return false;
    }

    private static bool WorldActions(PlayerCamera __instance)
    {
        Item item = __instance.dragItem;
        if (Items is not { } shared || !shared.IsTracked(item))
        {
            return true;
        }

        if (__instance.body.HoldingItem(item) && !__instance.woundView.activeSelf &&
            Vector2.Distance(Input.mousePosition, __instance.clickPos) > 10f)
        {
            __instance.body.DropItem(item);
        }
        return false;
    }

    private static bool Craft(Recipe __instance) => Items?.TryCraft(__instance) ?? true;

    private static void CraftFinished(Recipe __instance) => Items?.FinishCraft(__instance);

    private static void BeforeUseItem(Item item) => CraftExecutionScope.Current?.BeginUse(item);
    private static void AfterUseItem(Item item) => CraftExecutionScope.Current?.EndUse();

    private static void BeforeSpawnResult()
    {
        CoopDiagnostics.Trace("Items", $"Recipe spawn begin, used={CraftExecutionScope.Current?.Used.Count ?? -1}.");
        CraftFaultInjection.Hit("vanilla-spawn-result");
    }
    private static void AfterSpawnResult()
    {
        CoopDiagnostics.Trace("Items", $"Recipe spawn end, created={CraftExecutionScope.Current?.Created.Count ?? -1}.");
        CraftFaultInjection.Hit("vanilla-spawn-result-after");
    }

    private static void BeforeCreate() { }
    private static void AfterCreate(ref GameObject __result) => CraftExecutionScope.Current?.RecordCreated(__result);
    private static void BeforeDestroy(UnityEngine.Object obj) => CraftExecutionScope.Current?.MarkDestroyed(obj);

    private static void TraceLiquid(WaterContainerItem __instance, float __result) =>
        CraftExecutionScope.Current?.RecordLiquid(__instance, __result);

    private static bool CraftIngredients(Recipe __instance, ref List<Item> __result)
    {
        if (Items?.CraftOverride(__instance, out List<Item>? approved) != true || approved is null) return true;
        __result = approved;
        return false;
    }
    private static void FilterIngredients(ref List<Item> items)
    {
        if (Items is { RestrictsCraftIngredients: true } shared)
        {
            List<Item> available = new(items);
            available.AddRange(shared.CraftCandidates());
            var unique = new List<Item>(available.Count);
            var seen = new HashSet<int>();
            foreach (Item item in available)
            {
                if (!shared.CanSelectCraftIngredient(item) || !seen.Add(item.GetInstanceID())) continue;
                unique.Add(item);
            }
            items = unique;
        }
    }

    private static bool WorldItem(Item __instance)
    {
        // Replicas can be activated before Item.Start has added LiquidAffect. Vanilla
        // Update dereferences it unconditionally.
        WorldGeneration world = WorldGeneration.world;
        if (!world || !__instance.affect || !Item.GlobalItems.ContainsKey(__instance.id)) return false;
        if (!world.GetClosestChunkRenderer(world.WorldToBlockPos(__instance.transform.position))) return false;
        if (SharedItemProjection.IsReplica(__instance) && (Items is null || !Items.IsTracked(__instance)))
            return false;
        return Items?.HandleWorldUpdate(__instance) ?? true;
    }

    private static void DrawPending(InvButton __instance)
    {
        if (!__instance.isBody || __instance.woundView)
        {
            return;
        }

        Sprite? pending = Items?.PendingSprite(__instance.slot);
        if (pending)
        {
            __instance.itemImg.enabled = true;
            __instance.itemImg.sprite = pending;
            __instance.itemImg.color = PendingTint;
            __instance.itemImg.rectTransform.sizeDelta = PlayerCamera.ImageSizeDelta(pending!.texture, 3f, __instance.maxImageSize);
        }
        else if (__instance.itemImg.color == PendingTint)
        {
            __instance.itemImg.color = Color.white;
        }
    }
}
