using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal static class FluidItemProbe
{
    private readonly struct Push
    {
        internal readonly Vector2 Position;
        internal readonly Vector2 Direction;
        internal readonly float ExpiresAt;

        internal Push(Vector2 position, Vector2 direction)
        {
            Position = position;
            Direction = direction;
            ExpiresAt = Time.fixedTime + 0.75f;
        }
    }

    private sealed class Observation
    {
        internal readonly Push[] Recent = new Push[3];
        internal bool Moved;
        internal int Pushes;
        internal float Displacement;
    }

    private static readonly Dictionary<Item, Observation> Tracked = new();

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
        if (__state.Item is not { } item) return;
        Vector2 direction = __instance.direction;
        if (Vector2.Dot(item.rb.velocity - __state.Velocity, direction) < 0.05f) return;
        Observation observation = Tracked[item];
        observation.Pushes++;
        Observe(item, observation);
        // Vanilla current changes direction when it falls or spreads. Keep an independent window per direction.
        int axis = direction.x < 0 ? 0 : direction.x > 0 ? 1 : 2;
        if (observation.Recent[axis].ExpiresAt <= Time.fixedTime)
            observation.Recent[axis] = new Push(item.transform.position, direction);
    }

    internal static void Tick()
    {
        foreach (var entry in Tracked)
            if (entry.Key) Observe(entry.Key, entry.Value);
    }

    private static void Observe(Item item, Observation observation)
    {
        if (observation.Moved) return;
        foreach (Push push in observation.Recent)
        {
            if (push.ExpiresAt <= Time.fixedTime) continue;
            float distance = Vector2.Dot((Vector2)item.transform.position - push.Position, push.Direction);
            observation.Displacement = Mathf.Max(observation.Displacement, distance);
            if (distance > 0.2f) observation.Moved = true;
        }
    }

    internal static void Track(Item item) => Tracked.Add(item, new Observation());
    internal static void Forget(Item item) => Tracked.Remove(item);
    internal static bool Moved(Item item) => Tracked.TryGetValue(item, out var observation) && observation.Moved;

    internal static object? Describe(Item item) => Tracked.TryGetValue(item, out var observation)
        ? new { observation.Pushes, observation.Displacement, observation.Moved } : null;
}
