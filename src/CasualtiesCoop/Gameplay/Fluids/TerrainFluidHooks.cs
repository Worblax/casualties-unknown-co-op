using System;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Fluids;

internal static class TerrainFluidHooks
{
    private static (RangeI X, RangeI Y)? activeStrip;

    internal static void Install(Harmony harmony)
    {
        Prefix(harmony, typeof(FluidManager), "FixedUpdate", nameof(Simulation));
        Prefix(harmony, typeof(FluidManager), nameof(FluidManager.SimulationRangeIndex), nameof(SelectStrip));
        Prefix(harmony, typeof(FluidManager), nameof(FluidManager.SetLiquid), nameof(AllowWorldWrite), typeof(int), typeof(int), typeof(byte));
        Prefix(harmony, typeof(FluidManager), nameof(FluidManager.SetLiquid), nameof(AllowWorldWrite), typeof(Vector2), typeof(byte));
        Prefix(harmony, typeof(FluidManager), nameof(FluidManager.StartFill), nameof(AllowWorldWrite), typeof(Vector2Int), typeof(byte), typeof(int));
        Prefix(harmony, typeof(FluidManager), nameof(FluidManager.Clear), nameof(AllowWorldWrite));
        Prefix(harmony, typeof(OilPipeScript), "Update", nameof(AllowWorldWrite));
        Prefix(harmony, typeof(GeyserScript), "Update", nameof(AllowWorldWrite));
        Prefix(harmony, typeof(Body), nameof(Body.UseItem), nameof(AllowFluidTool), typeof(Item));
        Prefix(harmony, typeof(FluidManager), nameof(FluidManager.DrinkLiquid), nameof(Drink), typeof(Vector2Int), typeof(Body));
        Prefix(harmony, typeof(WaterContainerItem), "Update", nameof(ContainerUpdate));
        Prefix(harmony, typeof(WaterPusher), nameof(WaterPusher.OnTriggerStay2D), nameof(ApplyPush), typeof(Collider2D));
        harmony.Patch(AccessTools.Method(typeof(FluidManager), nameof(FluidManager.IncrMove)), postfix: Patch(nameof(FlowCreated)));
    }

    private static HarmonyMethod Patch(string name) => new(typeof(TerrainFluidHooks), name);

    private static void Prefix(Harmony harmony, Type type, string method, string patch, params Type[] types) =>
        harmony.Patch(AccessTools.Method(type, method, types), prefix: Patch(patch));

    internal static void Step(FluidManager manager, RangeI x, RangeI y)
    {
        activeStrip = (x, y);
        try { manager.SimulationStep(); }
        finally { activeStrip = null; }
    }

    private static bool SelectStrip(ref (RangeI, RangeI) __result)
    {
        if (activeStrip is not { } strip)
        {
            return true;
        }

        __result = strip;
        return false;
    }

    private static bool Simulation(FluidManager __instance) => CoopRuntime.ActiveWorld?.Fluids.TryStep(__instance) != true;

    private static bool AllowWorldWrite() => CoopRuntime.ActiveWorld?.Fluids.IsGuest != true;

    private static bool AllowFluidTool(Item item) => !item || item.id != "spacedrain" || AllowWorldWrite();

    private static bool Drink(Vector2Int pos, Body body) => CoopRuntime.ActiveWorld?.Fluids.AllowDrink(pos, body) ?? true;

    private static bool ContainerUpdate(WaterContainerItem __instance, SpriteRenderer ___fillRenderer, SpriteRenderer ____renderer)
    {
        if (AllowWorldWrite())
        {
            return true;
        }

        // Preserve vanilla fill rendering, while the host remains the only terrain-fluid writer.
        if (__instance.hasSprite && ___fillRenderer && ____renderer)
        {
            ___fillRenderer.enabled = ____renderer.enabled;
        }
        return false;
    }

    private static void FlowCreated(FluidManager __instance, Vector2 direction, Vector2Int pos, int ___waterMoveCount)
    {
        if (__instance.liquidPushing && ___waterMoveCount == 0)
        {
            CoopRuntime.ActiveWorld?.Fluids.NoteFlow(__instance, direction, pos);
        }
    }

    private static bool ApplyPush(Collider2D collision)
    {
        if (AllowWorldWrite())
        {
            return true;
        }

        if (!PlayerCamera.main || !PlayerCamera.main.body || !collision.attachedRigidbody)
        {
            return false;
        }

        GameObject target = collision.attachedRigidbody.gameObject;
        Body body = PlayerCamera.main.body;
        return target == body.gameObject || (target.TryGetComponent<Limb>(out Limb limb) && limb.body == body);
    }
}
