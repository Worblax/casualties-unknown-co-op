using System;
using System.Collections.Generic;
using CasualtiesCoop.Session;
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
        Prefix(harmony, typeof(Body), nameof(Body.WearWearable), nameof(Use), typeof(Item));
        Prefix(harmony, typeof(Body), nameof(Body.CombineItems), nameof(Combine), typeof(Item), typeof(Item));
        Prefix(harmony, typeof(Container), nameof(Container.LoadItem), nameof(Use), typeof(Item));
        Prefix(harmony, typeof(TraderScript), nameof(TraderScript.GiveItem), nameof(Use), typeof(Item));
        Prefix(harmony, typeof(PlayerCamera), nameof(PlayerCamera.ApplyWoundItem), nameof(Use), typeof(Item));
        Prefix(harmony, typeof(PlayerCamera), "TryPerformInventoryAction", nameof(InventoryAction),
            typeof(RaycastResult), typeof(List<RaycastResult>));
        Prefix(harmony, typeof(PlayerCamera), "TryPerformWorldActions", nameof(WorldActions));
        Prefix(harmony, typeof(RecipeItem), nameof(RecipeItem.GetMatchingItem), nameof(FilterIngredients),
            typeof(List<Item>), typeof(List<Item>));
        Prefix(harmony, typeof(Item), "Update", nameof(WorldItem));
        harmony.Patch(AccessTools.Method(typeof(InvButton), nameof(InvButton.UpdateGraphic)),
            postfix: new HarmonyMethod(typeof(SharedItemHooks), nameof(DrawPending)));
    }

    private static void Prefix(Harmony harmony, Type type, string method, string patch, params Type[] types) =>
        harmony.Patch(AccessTools.Method(type, method, types), prefix: new HarmonyMethod(typeof(SharedItemHooks), patch));

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

    private static bool Swap(Body __instance, int slot1, int slot2) => Items is not { } shared ||
        (!shared.IsReserved(slot1) && !shared.IsReserved(slot2) && shared.CanUse(__instance.GetItem(slot1)) &&
            shared.CanUse(__instance.GetItem(slot2)));

    private static bool UseHand(Body __instance)
    {
        Item item = __instance.GetItem(__instance.handSlot);
        return !item || !item.Stats.usableWithLMB || (Items?.CanUse(item) ?? true);
    }

    private static bool Use(Item __0) => Items?.CanUse(__0) ?? true;

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

        if (slot.isBody && shared.IsTracked(dragged) && !__instance.body.HoldingItem(dragged) &&
            !__instance.body.HoldingItem(slot.slot))
        {
            __instance.body.PickUpItem(dragged, slot.slot);
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

    private static void FilterIngredients(ref List<Item> items)
    {
        if (Items is { } shared && items.Exists(shared.IsTracked))
        {
            List<Item> available = new(items);
            available.RemoveAll(shared.IsTracked);
            items = available;
        }
    }

    private static bool WorldItem(Item __instance) => Items?.HandleWorldUpdate(__instance) ?? true;

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
