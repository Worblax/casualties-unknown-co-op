using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal static class FluidItemProbe
{
    private sealed class Observation
    {
        internal Vector2 At;
        internal Vector2 Direction;
    }

    private static readonly Dictionary<Item, Observation?> Tracked = new();

    internal static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(WaterPusher), nameof(WaterPusher.OnTriggerStay2D)),
        prefix: new HarmonyMethod(typeof(FluidItemProbe), nameof(BeforePush)),
        postfix: new HarmonyMethod(typeof(FluidItemProbe), nameof(AfterPush)));

    private static void BeforePush(Collider2D collision, out (Item? Item, Vector2 Velocity) __state)
    {
        __state = default;
        Rigidbody2D rb = collision.attachedRigidbody;
        if (rb && rb.TryGetComponent<Item>(out Item item) && Tracked.ContainsKey(item))
            __state = (item, rb.velocity);
    }

    private static void AfterPush(WaterPusher __instance, (Item? Item, Vector2 Velocity) __state)
    {
        if (__state.Item is not { } item || Vector2.Dot(item.rb.velocity - __state.Velocity, __instance.direction) < 0.05f) return;
        if (Tracked[item] is null)
            Tracked[item] = new Observation { At = item.transform.position, Direction = __instance.direction };
    }

    internal static void Track(Item item) => Tracked.Add(item, null);
    internal static void Forget(Item item) => Tracked.Remove(item);
    internal static bool Moved(Item item) => Tracked.TryGetValue(item, out var observation) && observation is not null &&
        Vector2.Dot((Vector2)item.transform.position - observation.At, observation.Direction) > 0.2f;
}
