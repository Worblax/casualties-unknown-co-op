using CasualtiesCoop.Networking;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static CasualtiesCoop.Testing.ScenarioDocument;

namespace CasualtiesCoop.Testing;

internal static class ScenarioQueries
{
    private sealed class Query
    {
        internal Func<TestRuntime, JObject, int> Read { get; }
        internal Action<JObject> Validate { get; }

        internal Query(Func<TestRuntime, JObject, int> read, Action<JObject> validate)
        {
            Read = read;
            Validate = validate;
        }
    }

    private static readonly Dictionary<string, Query> Registry = new(StringComparer.Ordinal)
    {
        ["runtime.unhandledExceptions"] = NoTarget((r, _) => r.Exceptions),
        ["network.pendingOperations"] = NoTarget((r, _) => r.Items.Cluster.Guests.Count(a => a.Guest!.Pending.HasValue)),
        ["world.ready"] = NoTarget((r, _) => Bool(r.Items.Ready)),
        ["peer.guestCount"] = NoTarget((r, _) => r.Items.Cluster.Guests.Count()),
        ["peer.synced"] = ForTarget((r, t) => Bool(r.Items.Cluster.Actors[t].Guest?.Synced == true)),
        ["character.nativeCount"] = NoTarget((r, _) => r.Fixtures.Characters.All.Count(pair => pair.Value)),
        ["character.localCameraPreserved"] = NoTarget((r, _) => Bool(PlayerCamera.main.body == r.Fixtures.Characters.Local)),
        ["character.ragdolled"] = BodyQuery(body => Bool(!body.standing)),
        ["character.grounded"] = BodyQuery(body => Bool(body.grounded)),
        ["character.jumped"] = BodyQuery(body => Bool(body.rb.velocity.y > 0.5f && !body.grounded)),
        ["character.crouched"] = BodyQuery(body => Bool(body.crouchAmount > 0.6f)),
        ["character.speed"] = BodyQuery(body => Mathf.RoundToInt(body.maxSpeed)),
        ["character.hunger"] = BodyQuery(body => Mathf.RoundToInt(body.hunger)),
        ["character.brainHealth"] = BodyQuery(body => Mathf.RoundToInt(body.brainHealth)),
        ["character.moved"] = new((r, c) => r.Fixtures.Characters.Moved(Alias(c), Distance(c, 0.7f)), c => { Alias(c); Distance(c, 0.7f); }),
        ["character.nearItem"] = new((r, c) => Bool(r.Items.Near(String(c, "item"), r.Fixtures.Characters.Get(String(c, "actor")), Distance(c, 3))),
            c => { String(c, "item"); String(c, "actor"); Distance(c, 3); }),
        ["character.holdsItem"] = new((r, c) => Bool(r.Items.Holds(r.Fixtures.Characters.Get(String(c, "actor")), String(c, "item"))),
            c => { String(c, "item"); String(c, "actor"); }),
        ["world.solidCount"] = ForTarget((r, t) => r.Fixtures.Terrain.SolidCount(t)),
        ["world.firstBlock"] = ForTarget((r, t) => r.Fixtures.Terrain.Block(r.Fixtures.Terrain.Cell(t))),
        ["fluid.cell"] = ForTarget((r, t) => r.Fixtures.Fluids.Cell(t)),
        ["fluid.wetCount"] = ForTarget((r, t) => r.Fixtures.Fluids.WetCount(t)),
        ["mob.alive"] = ForTarget((r, t) => Bool(r.Fixtures.Mobs.Alive(t))),
        ["mob.hasNativeTarget"] = new((r, c) =>
        {
            r.Fixtures.CoveredPaths.Add("SpiderHandler.Update/Physics2D.OverlapCircle");
            return Bool(r.Fixtures.Mobs.Targets(Alias(c), r.Fixtures.Characters.Get(c.Value<string>("actor") ?? "local")));
        }, c => { Alias(c); if (c["actor"] is not null) String(c, "actor"); }),
        ["terrain.syncRequests"] = ForTarget((r, t) => r.Items.Cluster.Actors[t].TerrainSyncRequests),
        ["terrain.guestMatch"] = new((r, c) => Bool(r.Items.Cluster.TerrainMatches(r.Fixtures.Terrain.Cell(String(c, "target")), String(c, "actor"))),
            c => { String(c, "target"); String(c, "actor"); }),
        ["terrain.cellLiquid"] = ForTarget((r, t) =>
        {
            var cell = r.Fixtures.Terrain.Cell(t);
            return FluidManager.main.GetLiquid(cell.x, cell.y);
        }),
        ["terrain.gateBeforeWater"] = new((r, c) =>
        {
            var gate = r.Fixtures.Terrain.Cell(String(c, "target"));
            var drain = r.Fixtures.Terrain.Cell(String(c, "item"));
            return Bool(r.Items.Cluster.GateBeforeWater(gate, drain));
        }, c => { String(c, "target"); String(c, "item"); }),
        ["terrain.sameChunk"] = new((r, c) =>
        {
            var a = r.Fixtures.Terrain.Cell(String(c, "target"));
            var b = r.Fixtures.Terrain.Cell(String(c, "item"));
            return Bool(WorldChunkKey.At(a.x, a.y).Equals(WorldChunkKey.At(b.x, b.y)));
        }, c => { String(c, "target"); String(c, "item"); }),
        ["terrain.hostDamage"] = ForTarget((r, t) => Mathf.RoundToInt(r.Fixtures.Terrain.Damage(r.Fixtures.Terrain.Cell(t)))),
        ["terrain.guestsMatch"] = ForTarget((r, t) =>
        {
            var cell = r.Fixtures.Terrain.Cell(t);
            return Bool(r.Items.Cluster.TerrainMatches(cell));
        }),
        ["effect.clawVisible"] = NoTarget((_, _) => Bool(GameObject.Find("ClawAnim(Clone)") is { } fx && fx && fx.GetComponentInChildren<SpriteRenderer>())),
        ["debug.cameraFollows"] = ForTarget((r, t) => Bool(r.CameraFollows(t))),
        ["debug.pauseCyclePassed"] = NoTarget((r, _) => Bool(r.PauseCyclePassed)),
        ["items.movedWithFlow"] = ForTarget((r, t) => Bool(r.Items.MovedWithFlow(t)))
    };

    static ScenarioQueries()
    {
        Registry.Add("items.activeWorldCount", ForTarget((r, t) => Bool(r.Items.IsInWorld(t))));
        foreach (string name in ItemTestAdapter.NetworkQueryNames)
            Registry.Add(name, ForTarget((r, t) => r.Items.ReadNetworkQuery(name, t)));
    }

    internal static void Validate(JObject command, string key)
    {
        Get(command, key).Validate(command);
        if (command["expected"] is not null) Integer(command, "expected", 0, 65536);
        else if (key == "query") throw new FormatException("assert.equal requires expected.");
    }

    internal static int Read(TestRuntime runtime, JObject command, string key) => Get(command, key).Read(runtime, command);
    private static Query Get(JObject c, string key) => Registry.TryGetValue(String(c, key), out var query) ? query :
        throw new FormatException($"Unknown query {String(c, key)}.");

    private static Query BodyQuery(Func<Body, int> read) => ForTarget((r, alias) => read(r.Fixtures.Characters.Get(alias)));
    private static Query ForTarget(Func<TestRuntime, string, int> read) => new((r, c) => read(r, Alias(c)), c => Alias(c));
    private static Query NoTarget(Func<TestRuntime, JObject, int> read) => new(read, _ => { });
    private static int Bool(bool value) => value ? 1 : 0;
    private static string Alias(JObject c) => String(c, c["item"] is not null ? "item" : "target");
    private static float Distance(JObject c, float fallback) => c["distance"] is not null ? Scalar(c, "distance", 0.1f, 32) : fallback;
}
