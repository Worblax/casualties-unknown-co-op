using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static CasualtiesCoop.Testing.ScenarioDocument;

namespace CasualtiesCoop.Testing;

internal sealed class MobFixtures : IDisposable
{
    private sealed class Observation
    {
        internal Collider2D? Candidate;
        internal Body? Target;
        internal bool Scanned;
    }

    private static readonly Dictionary<SpiderHandler, Observation> Observed = new();
    private static bool overlapBound;
    private readonly Dictionary<string, GameObject> mobs = new(StringComparer.Ordinal);
    private readonly FixtureSpace space;
    private readonly TestResourceCatalog catalog;
    private readonly int limit;

    internal MobFixtures(FixtureSpace space, TestResourceCatalog catalog, int limit)
    {
        this.space = space;
        this.catalog = catalog;
        this.limit = limit;
    }

    internal static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(SpiderHandler), "Update"),
        postfix: new HarmonyMethod(typeof(MobFixtures), nameof(RecordTarget)),
        transpiler: new HarmonyMethod(typeof(MobFixtures), nameof(ObserveOverlap)));

    private static IEnumerable<CodeInstruction> ObserveOverlap(IEnumerable<CodeInstruction> input)
    {
        var overlap = AccessTools.Method(typeof(Physics2D), nameof(Physics2D.OverlapCircle),
            new[] { typeof(Vector2), typeof(float), typeof(int) });
        var observer = AccessTools.Method(typeof(MobFixtures), nameof(Overlap));
        var instructions = input.ToList();
        overlapBound = instructions.Count(code => code.Calls(overlap)) == 1;
        if (!overlapBound) return instructions;
        return instructions.SelectMany(code => code.Calls(overlap)
            ? new[] { new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code),
                new CodeInstruction(OpCodes.Call, observer) }
            : new[] { code });
    }

    private static Collider2D Overlap(Vector2 position, float radius, int layers, SpiderHandler instance)
    {
        Collider2D collider = Physics2D.OverlapCircle(position, radius, layers);
        if (Observed.TryGetValue(instance, out Observation observation))
        {
            observation.Candidate = collider;
            observation.Scanned = true;
        }
        return collider;
    }

    private static void RecordTarget(SpiderHandler __instance)
    {
        if (!Observed.TryGetValue(__instance, out Observation observation) || !observation.Scanned) return;
        observation.Scanned = false;
        Collider2D? candidate = observation.Candidate;
        observation.Target = candidate && __instance.target == (Vector2)candidate!.transform.position
            ? candidate.GetComponent<Body>() ?? candidate.GetComponent<Limb>()?.body ?? candidate.GetComponentInParent<Body>()
            : null;
    }

    internal void Spawn(JObject command)
    {
        if (mobs.Count >= limit) throw new FormatException("The configured mob fixture limit was reached.");
        string id = String(command, "id");
        if (mobs.ContainsKey(id)) throw new FormatException($"Duplicate mob {id}.");
        string path = catalog.MobResource(String(command, "catalogId"));
        GameObject instance = Utils.Create(path, space.Position(command), 0);
        if (!instance) throw new TestBlocked($"Could not instantiate native mob {path}.");
        mobs.Add(id, instance);
        if (!instance.TryGetComponent<SpiderHandler>(out var handler))
        {
            if (command["aggro"] is not null || command["seeDistance"] is not null)
                throw new TestBlocked($"Mob {path} has no SpiderHandler setup fields.");
            return;
        }
        Observed.Add(handler, new Observation());
        if (command["aggro"] is not null) handler.aggro = Scalar(command, "aggro", 0, 1);
        if (command["seeDistance"] is not null) handler.seeDistance = Scalar(command, "seeDistance", 1, 32);
    }

    internal bool Alive(string alias) => mobs[alias] && mobs[alias].activeInHierarchy &&
        mobs[alias].GetComponent<BuildingEntity>() is { health: > 0 };

    internal bool Targets(string alias, Body body)
    {
        if (!overlapBound || !mobs[alias].TryGetComponent<SpiderHandler>(out var handler) || !Observed.TryGetValue(handler, out var observed))
            throw new TestBlocked("The native SpiderHandler target binding is unavailable.");
        return observed.Target == body;
    }

    internal object Snapshot(CharacterFixtures actors) => mobs.Select(pair => new
    {
        name = pair.Key, alive = Alive(pair.Key),
        target = pair.Value && pair.Value.TryGetComponent<SpiderHandler>(out var handler) &&
            Observed.TryGetValue(handler, out var observation) && observation.Target
            ? actors.All.FirstOrDefault(body => body.Value == observation.Target).Key : null
    }).ToArray();

    public void Dispose()
    {
        foreach (GameObject mob in mobs.Values)
        {
            if (!mob) continue;
            if (mob.TryGetComponent<SpiderHandler>(out var handler)) Observed.Remove(handler);
            UnityEngine.Object.Destroy(mob);
        }
    }
}
