using System.Diagnostics;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class TestRuntime : IDisposable
{
    private readonly ReportWriter report;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<object> assertions = new();
    private readonly List<string> loggedErrors = new();
    private readonly TestObservation observation;
    private ScenarioDocument? scenario;
    private TestWorldController? world;
    private ItemTestAdapter? items;
    private WorldFixtures? fixtures;
    private uint fixtureTerrainRevision;
    private int tick;
    private int physicsTicks;
    private int step;
    private int exceptions;
    private int waitFrom = -1;
    private float conditionStart = -1;
    private float resultAt = -1;
    private bool disposed;
    private string status = "RUNNING";

    internal ScenarioDocument Scenario => scenario!;
    internal ItemTestAdapter Items => items!;
    internal WorldFixtures Fixtures => fixtures!;
    internal int Exceptions => exceptions;
    internal bool PauseCyclePassed => observation.CyclePassed;
    internal bool CameraFollows(string alias) => observation.Follows(Fixtures.Characters.Get(alias));
    internal void ObserveActor(string alias)
    {
        _ = Fixtures.Characters.Get(alias);
        observation.Observe(alias);
    }

    internal TestRuntime(string manifest)
    {
        report = new ReportWriter(manifest);
        observation = new TestObservation(wall);
        Application.logMessageReceived += OnLog;
        try
        {
            scenario = new ScenarioDocument(report.ScenarioPath, report.Seed);
            if (scenario.Level is not ("UNITY_HOST" or "UNITY_GUEST" or "UNITY_ACTORS"))
                throw new TestBlocked($"Runner does not support level {scenario.Level}.");
            world = new TestWorldController();
        }
        catch (TestBlocked error)
        {
            Finish("BLOCKED", error.Message);
        }
        catch (Exception error)
        {
            Finish("FAIL", error.ToString());
        }
    }

    internal void Tick()
    {
        if (resultAt >= 0)
        {
            if (Time.realtimeSinceStartup >= resultAt) Quit();
            return;
        }

        try
        {
            observation.AdvanceCycle(tick, physicsTicks);
            if (observation.ActiveMilliseconds > Scenario.TimeoutMs)
            {
                if (items is null) throw new TestBlocked("Vanilla SandboxCourse did not become ready before the scenario deadline.");
                throw new TimeoutException($"Scenario timed out at step {step}.");
            }
            if (!observation.ConsumeScenarioTick()) return;

            tick++;
            if (items is null)
            {
                if (!world!.Ready) return;
                UnityEngine.Random.InitState(Scenario.Seed);
                fixtures = new WorldFixtures(world.Body, Scenario);
                foreach (JObject command in Scenario.Setup) ScenarioCommands.Execute(this, command);
                items = new ItemTestAdapter(Scenario, Fixtures, entry => report.Trace(entry, tick));
                CaptureCatalog();
                return;
            }

            Fixtures.Characters.ApplyPreparedState();
            items.Tick();
            if (step < Scenario.Steps.Length && ScenarioCommands.Execute(this, Scenario.Steps[step]))
            {
                step++;
                waitFrom = -1;
                conditionStart = -1;
            }
            if (step == Scenario.Steps.Length)
                Finish(assertions.Count == 0 || exceptions != 0 ? "FAIL" : "PASS",
                    assertions.Count == 0 || exceptions != 0 ? "The scenario performed no assertions or Unity reported an exception." : null);
        }
        catch (TestBlocked error)
        {
            Finish("BLOCKED", error.Message);
        }
        catch (Exception error)
        {
            Finish("FAIL", error.ToString());
        }
    }

    internal void PhysicsTick()
    {
        physicsTicks++;
        observation.PhysicsTick();
    }

    internal void PublishTerrainFixture(JObject command)
    {
        if (Scenario.Level != "UNITY_GUEST") throw new FormatException("Terrain fixture input requires UNITY_GUEST.");
        Items.Cluster.PublishTerrainFixture(Fixtures.Terrain.Cell(ScenarioDocument.String(command, "target")),
            Fixtures.Catalog.Material(ScenarioDocument.Integer(command, "material", 0, ushort.MaxValue)),
            ScenarioDocument.Integer(command, "damage", 0, 99),
            (byte)ScenarioDocument.OptionalInt(command, "liquid", 0, 7, 0), ++fixtureTerrainRevision);
        Fixtures.CoveredPaths.Add("GuestWorldChunks.Receive/UnityWorldChunkGrid.Apply");
    }

    internal bool WaitUnits(int count, bool physics)
    {
        int now = physics ? physicsTicks : tick;
        if (waitFrom < 0) waitFrom = now;
        return now - waitFrom >= count;
    }

    internal void Capture(string name) =>
        report.Write($"snapshots/{name}.json", new { tick, physicsTicks, simulation = Items.Cluster.Network.Clock.Now,
            items = Items.Snapshot(), world = Fixtures.Snapshot(Items.Cluster), worldStreaming = Items.Cluster.WorldMetrics() });

    internal void CaptureCatalog()
    {
        if (!catalogCaptured)
        {
            Fixtures.Catalog.Probe(report.ProbeItems, report.ProbeMobs);
            report.Write("capabilities.json", Fixtures.Catalog.Describe(Fixtures.Characters.Local));
            catalogCaptured = true;
        }
        if (report.CatalogOnly && assertions.Count == 0)
        {
            bool available = Item.GlobalItems.Count > 0 && WorldGeneration.world && FluidManager.main;
            assertions.Add(new { query = "catalog.nativeRegistries", expected = 1, actual = available ? 1 : 0, tick });
            if (!available) throw new TestBlocked("Native resource registries are unavailable.");
        }
    }

    private bool catalogCaptured;

    internal bool PauseCycle()
    {
        if (!report.Visible) throw new TestBlocked("The pause self-check requires -Visible.");
        return observation.StartCycle(tick, physicsTicks);
    }

    internal bool Observe(JObject command, bool always)
    {
        float now = Items.Cluster.Network.Clock.Now;
        if (conditionStart < 0) conditionStart = now;
        bool deadline = now - conditionStart >= ScenarioDocument.Integer(command, "timeoutMs", 1, 60000) / 1000f;
        JObject condition = ScenarioDocument.Object(command, "condition");
        int actual = ScenarioQueries.Read(this, condition, "op");
        int expected = Expected(condition);
        bool completed = always ? actual != expected || deadline : actual == expected || deadline;
        return completed && RecordAssertion(condition, "op", actual, expected);
    }

    internal bool Assert(JObject command, string key) =>
        RecordAssertion(command, key, ScenarioQueries.Read(this, command, key), Expected(command));

    private static int Expected(JObject command) => ScenarioDocument.OptionalInt(command, "expected", 0, 65536, 1);

    private bool RecordAssertion(JObject command, string key, int actual, int expected)
    {
        string query = ScenarioDocument.String(command, key);
        assertions.Add(new { query, item = command.Value<string>("item"), target = command.Value<string>("target"),
            actor = command.Value<string>("actor"), expected, actual, tick, source = query });
        if (actual != expected) throw new InvalidOperationException($"{query} expected {expected}, got {actual}.");
        return true;
    }

    internal void Draw()
    {
        if (!report.Visible) return;
        observation.Draw(Scenario, items, fixtures, status, step, assertions.Count, resultAt >= 0, Quit);
    }

    internal void ApplyPhysicsScale() => observation.ApplyPhysicsScale();

    internal void ApplyCamera(PlayerCamera camera) => observation.ApplyCamera(camera, fixtures?.Characters);

    private void OnLog(string message, string stack, LogType type)
    {
        if (type == LogType.Exception) exceptions++;
        if ((type == LogType.Exception || type == LogType.Error) && loggedErrors.Count < 128)
            loggedErrors.Add(message + "\n" + stack);
    }

    private void Finish(string final, string? message)
    {
        if (resultAt >= 0) return;
        if (world is not null)
        {
            bool isolated = world.VerifyIsolation(out object storage);
            report.Write("isolation.json", storage);
            if (!isolated)
            {
                final = "FAIL";
                message = "The save, settings or scoped PlayerPrefs changed during the test.";
            }
        }
        status = final;
        bool hold = final != "PASS" && report.HoldOnFailure && report.Visible;
        resultAt = hold && report.HoldSeconds == 0 ? float.PositiveInfinity :
            Time.realtimeSinceStartup + (hold ? report.HoldSeconds : 0.15f);
        if (items is not null) Capture("final");
        File.WriteAllLines(Path.Combine(report.DirectoryPath, "game.log"), loggedErrors);
        float simulation = items?.Cluster.Network.Clock.Now ?? 0;
        string[] covered = items is null ? Array.Empty<string>() : Scenario.Level == "UNITY_GUEST"
            ? new[] { "SandboxScene", "SharedItemHostScene", "HostItemSession", "GuestItemSession",
                "SharedItemProjection", "SharedItemInventory", "Body.GetItem" }
            : Scenario.StandaloneItems ? new[] { "SandboxScene", "native fixtures" }
            : new[] { "SandboxScene", "SharedItemHostScene", "HostItemSession", "GuestItemSession", "WorldChunkReplication.Tick" };
        if (fixtures is not null) covered = covered.Concat(fixtures.CoveredPaths).ToArray();
        if (!hold) DisposeWorld();
        report.Finish(scenario, final, message, wall.ElapsedMilliseconds, simulation, step, assertions, covered);
    }

    private void Quit() => Application.Quit(status == "PASS" ? 0 : 1);

    private void DisposeWorld()
    {
        observation.Restore();
        items?.Dispose();
        items = null;
        fixtures?.Dispose();
        fixtures = null;
        world?.Dispose();
        world = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Application.logMessageReceived -= OnLog;
        DisposeWorld();
        report.Dispose();
    }
}
