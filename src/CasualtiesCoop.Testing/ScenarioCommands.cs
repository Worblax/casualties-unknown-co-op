using Newtonsoft.Json.Linq;
using UnityEngine;
using static CasualtiesCoop.Testing.ScenarioDocument;

namespace CasualtiesCoop.Testing;

[Flags]
internal enum CommandPhase { Setup = 1, Step = 2, Parallel = 4 }

internal static class ScenarioCommands
{
    private sealed class Command
    {
        internal CommandPhase Phases { get; }
        internal string Arguments { get; }
        internal Action<JObject> Validate { get; }
        internal Func<TestRuntime, JObject, bool> Run { get; }

        internal Command(CommandPhase phases, string arguments, Action<JObject> validate, Func<TestRuntime, JObject, bool> run)
        {
            Phases = phases;
            Arguments = arguments;
            Validate = validate;
            Run = run;
        }
    }

    private const CommandPhase Setup = CommandPhase.Setup;
    private const CommandPhase Step = CommandPhase.Step;
    private const CommandPhase Both = Step | CommandPhase.Parallel;
    private static readonly Dictionary<string, Command> Registry = new(StringComparer.Ordinal)
    {
        ["world.loadSandbox"] = Apply(Setup, "", None, (_, _) => { }),
        ["peer.add"] = Apply(Setup, "id role position mode body", ValidatePeer, (_, _) => { }),
        ["item.spawn"] = Apply(Setup, "id catalogId position", Spawn, (_, _) => { }),
        ["world.loadLayout"] = Apply(Setup, "id rows legend position", Layout, (r, c) =>
        {
            r.Fixtures.Terrain.Load(c);
            r.Fixtures.CoveredPaths.Add("WorldGeneration.SetBlock/GetBlock");
        }),
        ["fluid.fill"] = Apply(Setup, "id type position width height", c => Fluid(c, true), (r, c) =>
        {
            r.Fixtures.Fluids.Define(c, fill: true);
            r.Fixtures.CoveredPaths.Add("FluidManager.SetLiquid/GetLiquid");
        }),
        ["fluid.probe"] = Apply(Setup, "id position width height", c => Fluid(c, false), (r, c) => r.Fixtures.Fluids.Define(c, fill: false)),
        ["fluid.enableFlow"] = Apply(Setup, "", None, (r, _) => r.Fixtures.Fluids.EnableFlow()),
        ["character.spawn"] = Apply(Setup, "id character position", c =>
        {
            String(c, "id"); Integer(c, "character", 0, 255); Position(c);
        }, (r, c) =>
        {
            r.Fixtures.Characters.Spawn(c);
            r.Fixtures.CoveredPaths.Add("PlayerCamera.SpawnCharacter");
        }),
        ["character.place"] = Apply(Setup | Both, "target position", c => { Target(c); Position(c); },
            (r, c) => r.Fixtures.Characters.Place(c)),
        ["character.setState"] = Apply(Setup, "target brainHealth hunger thirst stamina temperature bloodVolume limb skin muscle pain bleeding",
            ValidateState, (r, c) => r.Fixtures.Characters.PrepareState(c)),
        ["character.move"] = Apply(Both, "target axis", c => { Target(c); Scalar(c, "axis", -1, 1); },
            (r, c) => { r.Fixtures.Characters.Move(c); r.Fixtures.CoveredPaths.Add("Body.FixedUpdate"); }),
        ["character.crouch"] = Apply(Both, "target enabled", c => { Target(c); Flag(c, "enabled"); },
            (r, c) => { r.Fixtures.Characters.Crouch(c); r.Fixtures.CoveredPaths.Add("Body.Update"); }),
        ["character.speed"] = Apply(Setup | Both, "target maxSpeed", c => { Target(c); Scalar(c, "maxSpeed", 0.5f, 20); },
            (r, c) => r.Fixtures.Characters.Speed(c)),
        ["character.intelligence"] = Apply(Setup | Both, "target value", c => { Target(c); Integer(c, "value", -10, 20); },
            (r, c) => r.Fixtures.Characters.Intelligence(c)),
        ["character.jump"] = Apply(Both, "target", Target, (r, c) =>
        {
            r.Fixtures.Characters.Jump(String(c, "target"));
            r.Fixtures.CoveredPaths.Add("Body.Jump");
        }),
        ["character.ragdoll"] = Apply(Both, "target", Target, (r, c) =>
        {
            r.Fixtures.Characters.Get(String(c, "target")).Ragdoll();
            r.Fixtures.CoveredPaths.Add("Body.Ragdoll");
        }),
        ["character.pickup"] = Apply(Both, "target item", TargetItem,
            (r, c) => { r.Items.ActorPickup(r.Fixtures.Characters.Get(String(c, "target")), String(c, "item")); r.Fixtures.CoveredPaths.Add("Body.PickUpItem"); }),
        ["character.throw"] = Apply(Both, "target item direction", c => { TargetItem(c); Scalar(c, "direction", -1, 1); },
            (r, c) => { r.Items.ActorThrow(r.Fixtures.Characters.Get(String(c, "target")), String(c, "item"), Scalar(c, "direction", -1, 1)); r.Fixtures.CoveredPaths.Add("Body.ThrowItem"); }),
        ["mob.spawn"] = Apply(Setup, "id catalogId position aggro seeDistance", c =>
        {
            Spawn(c);
            if (c["aggro"] is not null) Scalar(c, "aggro", 0, 1);
            if (c["seeDistance"] is not null) Scalar(c, "seeDistance", 1, 32);
        }, (r, c) => r.Fixtures.Mobs.Spawn(c)),
        ["item.nativePickup"] = Apply(Both, "item", Item, (r, c) =>
        {
            r.Items.NativePickup(c);
            r.Fixtures.CoveredPaths.Add("Body.PickUpItem/SharedItemHooks.Pickup");
        }),
        ["item.pickup"] = Apply(Both, "peer item slot", c =>
        {
            PeerItem(c);
            if (c["slot"]?.Type == JTokenType.Integer) Integer(c, "slot", 0, 254);
            else String(c, "slot");
        }, (r, c) => r.Items.PickUp(c)),
        ["item.drop"] = Apply(Both, "peer item", PeerItem, (r, c) => r.Items.Drop(c)),
        ["item.throw"] = Apply(Both, "peer item velocityX velocityY spin", c =>
        {
            PeerItem(c);
            Scalar(c, "velocityX", -100, 100);
            Scalar(c, "velocityY", -100, 100);
            if (c["spin"] is not null) Scalar(c, "spin", -2000, 2000);
        }, (r, c) => r.Items.Throw(c)),
        ["item.swap"] = Apply(Both, "peer item", PeerItem, (r, c) => r.Items.Swap(c)),
        ["item.remove"] = Apply(Both, "item", Item, (r, c) => r.Items.Remove(String(c, "item"))),
        ["item.place"] = Apply(Both, "item position", c => { Item(c); Position(c); }, (r, c) => r.Items.Place(String(c, "item"), c)),
        ["item.condition"] = Apply(Both, "item condition", c => { Item(c); Scalar(c, "condition", 0, 1); },
            (r, c) => r.Items.SetCondition(String(c, "item"), Scalar(c, "condition", 0, 1))),
        ["item.wet"] = Apply(Both, "item wet", c => { Item(c); Flag(c, "wet"); },
            (r, c) => r.Items.SetWet(String(c, "item"), Flag(c, "wet"))),
        ["item.gun"] = Apply(Both, "item rounds chamber hasMag safe racked", c =>
        {
            Item(c); Integer(c, "rounds", 0, 999); Integer(c, "chamber", 0, 2); Flag(c, "hasMag");
            Flag(c, "safe"); Flag(c, "racked");
        }, (r, c) => r.Items.SetGun(String(c, "item"), Integer(c, "rounds", 0, 999), Integer(c, "chamber", 0, 2),
            Flag(c, "hasMag"), Flag(c, "safe"), Flag(c, "racked"))),
        ["item.liquid"] = Apply(Both, "item liquid amount clear", c =>
        {
            Item(c); String(c, "liquid"); Scalar(c, "amount", 0, 5000); Flag(c, "clear");
        }, (r, c) => r.Items.SetLiquid(String(c, "item"), String(c, "liquid"), Scalar(c, "amount", 0, 5000), Flag(c, "clear"))),
        ["item.craft"] = Apply(Both, "recipe peer", c =>
        {
            Integer(c, "recipe", 0, 10000);
            if (c["peer"] is not null) Peer(c);
        }, (r, c) => r.Items.Craft(Integer(c, "recipe", 0, 10000), c.Value<string>("peer"))),
        ["terrain.hit"] = Apply(Both, "peer target damage", c => { Peer(c); Damage(c); }, (r, c) =>
        {
            var cell = r.Fixtures.Terrain.Cell(String(c, "target"));
            r.Items.Cluster.HitTerrain(String(c, "peer"), cell, r.Fixtures.Terrain.Block(cell), Integer(c, "damage", 1, 1000));
            r.Fixtures.CoveredPaths.Add("TerrainDamageReplication.Receive/WorldGeneration.DamageBlock");
        }),
        ["terrain.breakArea"] = new(Step, "position", c => Position(c), (r, c) =>
        {
            if (r.Scenario.Name != "world.terrain-history-limit") throw new FormatException("History load probe is a dedicated control.");
            return r.Fixtures.Terrain.BreakHistoryProbe(c, r.Items.Cluster);
        }),
        ["terrain.breakNative"] = Apply(Both, "target damage", Damage, (r, c) =>
        {
            r.Items.Cluster.ApplyHostTerrainHit(r.Fixtures.Terrain.Cell(String(c, "target")), Integer(c, "damage", 1, 1000));
            r.Fixtures.CoveredPaths.Add("WorldGeneration.DamageBlock");
        }),
        ["terrain.state"] = Apply(Both, "target damage material liquid", c =>
        {
            Target(c); Integer(c, "damage", 0, 99); Integer(c, "material", 0, ushort.MaxValue);
            OptionalInt(c, "liquid", 0, 7, 0);
        }, (r, c) => r.PublishTerrainFixture(c)),
        ["effect.claw"] = Apply(Both, "peer", Peer, (r, c) =>
        {
            r.Items.Cluster.SendClaw(String(c, "peer"));
            r.Fixtures.CoveredPaths.Add("ClawEffects.Receive/ClawAnim");
        }),
        ["peer.move"] = Apply(Both, "peer position", c => { Peer(c); Position(c); },
            (r, c) =>
            {
                var at = Position(c);
                r.Items.Cluster.MovePeer(String(c, "peer"), new Vector2(at.X, at.Y));
            }),
        ["fluid.setNative"] = Apply(Both, "target type", c => { Target(c); Integer(c, "type", 0, 7); }, (r, c) =>
        {
            var at = r.Fixtures.Terrain.Cell(String(c, "target"));
            FluidManager.main.SetLiquid(at.x, at.y, r.Fixtures.Catalog.Liquid(Integer(c, "type", 0, 7)));
        }),
        ["terrain.setNative"] = Apply(Both, "target material", c => { Target(c); Integer(c, "material", 0, ushort.MaxValue); },
            (r, c) => r.Fixtures.Terrain.SetNative(String(c, "target"), Integer(c, "material", 0, ushort.MaxValue))),
        ["peer.disconnect"] = Apply(Step, "peer", Peer, (r, c) => r.Items.Connect(c, false)),
        ["peer.reconnect"] = Apply(Step, "peer", Peer, (r, c) => r.Items.Connect(c, true)),
        ["network.profile"] = Apply(Step, "profile", c => ReadProfile(Object(c, "profile")), (r, c) =>
            r.Items.Cluster.Network.SetProfile(ReadProfile(Object(c, "profile")))),
        ["snapshot.capture"] = Apply(Step, "id", c => SafeName(c, "id"), (r, c) => r.Capture(String(c, "id"))),
        ["catalog.capture"] = Apply(Step, "", None, (r, _) => r.CaptureCatalog()),
        ["wait.ready"] = new(Step, "", None, (r, _) => r.Items.Ready),
        ["wait.frames"] = new(Step, "count", Count, (r, c) => r.WaitUnits(Integer(c, "count", 1, 1000), physics: false)),
        ["wait.physicsTicks"] = new(Step, "count", Count, (r, c) => r.WaitUnits(Integer(c, "count", 1, 1000), physics: true)),
        ["assert.equal"] = new(Step, "query item target actor distance expected", c => ScenarioQueries.Validate(c, "query"),
            (r, c) => r.Assert(c, "query")),
        ["assert.eventually"] = new(Step, "timeoutMs condition", Observe, (r, c) => r.Observe(c, always: false)),
        ["assert.always"] = new(Step, "timeoutMs condition", Observe, (r, c) => r.Observe(c, always: true)),
        ["parallel"] = Apply(Step, "actions", c =>
        {
            foreach (JObject action in Commands(c, "actions")) Validate(action, CommandPhase.Parallel);
        }, (r, c) =>
        {
            foreach (JObject action in Commands(c, "actions")) Execute(r, action);
        }),
        ["debug.observeActor"] = Apply(Step, "target", Target, (r, c) => r.ObserveActor(String(c, "target"))),
        ["debug.pauseCycle"] = new(Step, "", None, (r, _) => r.PauseCycle()),
        ["control.divergeReplica"] = Apply(Step, "peer item", PeerItem, (r, c) => r.Items.DivergeReplica(c, r.Scenario))
    };

    private static readonly HashSet<string> Blocked = new(StringComparer.Ordinal)
    {
        "world.fillTerrain", "world.clearTerrain", "fluid.clear", "character.interact",
        "mob.setState", "network.partition", "network.restore", "peer.connect", "wait.until"
    };

    internal static void Validate(JObject command, CommandPhase phase)
    {
        string name = String(command, "op");
        if (!Registry.TryGetValue(name, out var spec))
        {
            if (Blocked.Contains(name)) throw new TestBlocked($"Binding for {name} has not been verified.");
            throw new FormatException($"Unknown command {name}.");
        }
        if ((spec.Phases & phase) == 0) throw new FormatException($"Misplaced command {name}.");
        Fields(command, spec.Arguments.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Append("op").ToArray());
        spec.Validate(command);
    }

    internal static bool Execute(TestRuntime runtime, JObject command) => Registry[String(command, "op")].Run(runtime, command);
    private static Command Apply(CommandPhase phase, string arguments, Action<JObject> validate, Action<TestRuntime, JObject> run) =>
        new(phase, arguments, validate, (r, c) => { run(r, c); return true; });

    private static void None(JObject _) { }
    private static void Count(JObject c) => Integer(c, "count", 1, 1000);
    private static void Target(JObject c) => String(c, "target");
    private static void Item(JObject c) => String(c, "item");
    private static void Peer(JObject c) => String(c, "peer");
    private static void PeerItem(JObject c) { Peer(c); Item(c); }
    private static void TargetItem(JObject c) { Target(c); Item(c); }
    private static void Damage(JObject c) { Target(c); Integer(c, "damage", 1, 1000); }
    private static void Spawn(JObject c) { String(c, "id"); String(c, "catalogId"); Position(c); }

    private static void ValidatePeer(JObject c)
    {
        String(c, "id"); String(c, "role");
        if (c["position"] is not null) Position(c);
        if (c["mode"] is not null && String(c, "mode") is not ("VirtualOnly" or "NativeActor"))
            throw new FormatException("Unknown participant mode.");
        if (c["body"] is not null) String(c, "body");
    }

    private static void Layout(JObject c)
    {
        String(c, "id"); Position(c);
        if (c["rows"] is not JArray { Count: > 0 and <= 16 } rows ||
            rows.Any(row => row.Type != JTokenType.String || row.Value<string>()!.Length is < 1 or > 16) ||
            rows.Select(row => row.Value<string>()!.Length).Distinct().Count() != 1)
            throw new FormatException("Layout rows must form a rectangle up to 16 by 16.");
        JObject legend = Object(c, "legend");
        foreach (char symbol in rows.SelectMany(row => row.Value<string>()!).Distinct()) Integer(legend, symbol.ToString(), 0, ushort.MaxValue);
    }

    private static void Fluid(JObject c, bool fill)
    {
        String(c, "id"); Position(c);
        if (fill) Integer(c, "type", 0, byte.MaxValue);
        OptionalInt(c, "width", 1, 16, 1);
        OptionalInt(c, "height", 1, 16, 1);
    }

    private static void ValidateState(JObject c)
    {
        Target(c);
        foreach (var (field, min, max) in new (string, float, float)[]
        {
            ("brainHealth", 1, 100), ("hunger", 0, 100), ("thirst", 0, 100), ("stamina", 0, 100),
            ("temperature", 30, 42), ("bloodVolume", 20, 100), ("skin", 1, 100), ("muscle", 1, 100), ("pain", 0, 100), ("bleeding", 0, 5)
        })
            if (c[field] is not null) Scalar(c, field, min, max);
        if (c["limb"] is not null) Integer(c, "limb", 0, 40);
        else if (c["skin"] is not null || c["muscle"] is not null || c["pain"] is not null || c["bleeding"] is not null)
            throw new FormatException("Limb fields require a limb index.");
    }

    private static void Observe(JObject command)
    {
        Integer(command, "timeoutMs", 1, 60000);
        JObject condition = Object(command, "condition");
        Fields(condition, "op", "item", "target", "actor", "distance", "expected");
        ScenarioQueries.Validate(condition, "op");
    }

    private static void SafeName(JObject c, string field)
    {
        string name = String(c, field);
        if (name.Length > 32 || name.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_')) || name == "final")
            throw new FormatException("Invalid or reserved snapshot name.");
    }
}
