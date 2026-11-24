using System.Diagnostics;
using CasualtiesCoop.Networking;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class TestObservation
{
    private readonly Stopwatch wall;
    private bool paused;
    private bool scenarioStep;
    private bool physicsPaused;
    private bool physicsStep;
    private long pausedAt;
    private long pausedMilliseconds;
    private float restoredScale = 1f;
    private float previousMaxDelta;
    private bool steppingLimit;
    private string? cameraTarget;
    private int observedPeer = -1;
    private int cycle;
    private int initialTick;
    private int initialPhysics;
    private long initialActive;
    private long cycleAt;

    internal TestObservation(Stopwatch wall) => this.wall = wall;
    internal long ActiveMilliseconds => (paused ? pausedAt : wall.ElapsedMilliseconds) - pausedMilliseconds;
    internal bool CyclePassed => cycle == 3;

    private void PauseScenario(bool state)
    {
        if (paused == state) return;
        if (state) pausedAt = wall.ElapsedMilliseconds;
        else pausedMilliseconds += wall.ElapsedMilliseconds - pausedAt;
        paused = state;
    }

    private void PausePhysics(bool state)
    {
        if (physicsPaused == state) return;
        if (state && Time.timeScale > 0) restoredScale = Time.timeScale;
        physicsPaused = state;
        Time.timeScale = state ? 0 : restoredScale;
    }

    private void Step()
    {
        if (paused) scenarioStep = true;
        if (!physicsPaused || physicsStep) return;
        physicsStep = true;
        previousMaxDelta = Time.maximumDeltaTime;
        steppingLimit = true;
        Time.maximumDeltaTime = Time.fixedDeltaTime;
        Time.timeScale = 1f;
    }

    internal bool ConsumeScenarioTick()
    {
        if (paused && (!scenarioStep || physicsStep)) return false;
        scenarioStep = false;
        return true;
    }

    internal void PhysicsTick()
    {
        if (!physicsStep) return;
        physicsStep = false;
        Time.timeScale = 0;
    }

    internal void ApplyPhysicsScale()
    {
        if (physicsPaused && !physicsStep) Time.timeScale = 0;
        if (steppingLimit && !physicsStep)
        {
            Time.maximumDeltaTime = previousMaxDelta;
            steppingLimit = false;
        }
    }

    internal bool StartCycle(int tick, int physics)
    {
        if (cycle != 0) return CyclePassed;
        PauseScenario(true);
        PausePhysics(true);
        initialTick = tick;
        initialPhysics = physics;
        initialActive = ActiveMilliseconds;
        cycleAt = wall.ElapsedMilliseconds;
        cycle = 1;
        return false;
    }

    internal void AdvanceCycle(int tick, int physics)
    {
        if (cycle == 1 && wall.ElapsedMilliseconds - cycleAt >= 350)
        {
            if (tick != initialTick || physics != initialPhysics || ActiveMilliseconds != initialActive)
                throw new InvalidOperationException("Scenario, physics or scenario deadline advanced during pause.");
            Step();
            cycle = 2;
        }
        else if (cycle == 2 && tick == initialTick + 1 && !physicsStep)
        {
            if (physics != initialPhysics + 1) throw new InvalidOperationException("Single-step ran a different number of physics ticks.");
            PausePhysics(false);
            PauseScenario(false);
            cycle = 3;
        }
    }

    internal void Draw(ScenarioDocument? scenario, ItemTestAdapter? items, WorldFixtures? fixtures,
        string status, int step, int assertions, bool finished, Action quit)
    {
        string peers = items is null ? "" : string.Join(" | ", items.Cluster.Actors.Values.Select(actor =>
            $"{actor.Name} #{actor.Participant.Seat} {actor.Mode} {(actor.Guest?.Synced == true ? "ready" : "")}"));
        GUI.Box(new Rect(12, 12, 700, 120),
            $"{scenario?.Name}  {status}\nstep {step + 1}  seed {scenario?.Seed}  queue {items?.Cluster.Network.Queued ?? 0}  assertions {assertions}\n{peers}");
        if (finished)
        {
            if (GUI.Button(new Rect(24, 90, 138, 24), "End observation")) quit();
        }
        else
        {
            if (GUI.Button(new Rect(24, 82, 138, 24), paused ? "Resume scenario" : "Pause scenario")) PauseScenario(!paused);
            if (GUI.Button(new Rect(168, 82, 138, 24), physicsPaused ? "Resume physics" : "Pause physics")) PausePhysics(!physicsPaused);
            if (GUI.Button(new Rect(312, 82, 76, 24), "Step")) Step();
            if (GUI.Button(new Rect(394, 82, 86, 24), restoredScale < 0.75f ? "Normal" : "Half speed"))
            {
                restoredScale = restoredScale < 0.75f ? 1f : 0.5f;
                if (!physicsPaused) Time.timeScale = restoredScale;
            }
        }
        if (fixtures is null || items is null) return;
        if (GUI.Button(new Rect(486, 82, 209, 24), $"Camera: {cameraTarget ?? "vanilla"}"))
        {
            string[] aliases = fixtures.Characters.All.Select(pair => pair.Key).ToArray();
            int next = cameraTarget is null ? 0 : Array.IndexOf(aliases, cameraTarget) + 1;
            cameraTarget = next < aliases.Length ? aliases[next] : null;
        }

        DrawChunks(items.Cluster);
        foreach (var pair in fixtures.Characters.All)
        {
            if (!pair.Value) continue;
            var peer = items.Cluster.Actors.Values.FirstOrDefault(actor => actor.NativeBody == pair.Value);
            Label(pair.Value.transform.position + Vector3.up * 1.6f, $"{pair.Key}  {peer?.Name ?? "unbound"} #{peer?.Participant.Seat}");
        }
        foreach (var pair in items.All)
        {
            if (!pair.Value) continue;
            var (id, state) = items.StateForLabel(pair.Key);
            string label = $"{pair.Key} #{id}  owner {state.Owner.Seat}  rev {state.Revision}";
            if (pair.Value.gameObject.activeInHierarchy)
                Label(pair.Value.transform.position + Vector3.up * 0.6f, label);
            else if (items.Cluster.Actors.Values.FirstOrDefault(actor => actor.Participant == state.Owner)?.NativeBody is { } owner && owner)
                Label(owner.transform.position + Vector3.up * 2.15f, label);
        }
    }

    private void DrawChunks(VirtualSessionCluster cluster)
    {
        var guests = cluster.Guests.ToArray();
        if (guests.Length == 0) return;
        string alias = observedPeer >= 0 && observedPeer < guests.Length ? guests[observedPeer].Name : "";
        if (GUI.Button(new Rect(12, 138, 180, 22), alias.Length == 0 ? "Show chunks" : $"Chunks: {alias}"))
            observedPeer = observedPeer + 1 < guests.Length ? observedPeer + 1 : -1;
        if (alias.Length == 0 || Event.current.type != EventType.Repaint || !Camera.main || !WorldGeneration.world ||
            cluster.Interest(alias) is not { } interest) return;
        var camera = Camera.main;
        var world = WorldGeneration.world;
        Color before = GUI.color;
        foreach (WorldChunkKey key in interest.Ordered)
        {
            Vector2 at = world.BlockToWorldPos(new Vector2Int(key.StartX, key.StartY)) - Vector2.one * 0.5f;
            Vector2 size = new(Math.Min(WorldChunkWire.Side, (int)world.width - key.StartX),
                Math.Min(WorldChunkWire.Side, (int)world.height - key.StartY));
            Vector3 lo = camera.WorldToScreenPoint(at);
            Vector3 hi = camera.WorldToScreenPoint(at + size);
            if (hi.x < 0 || lo.x > Screen.width || hi.y < 0 || lo.y > Screen.height) continue;
            GUI.color = cluster.ChunkAcknowledged(alias, key) ? new Color(0.2f, 0.9f, 0.65f, 0.65f) :
                new Color(1f, 0.6f, 0.15f, 0.8f);
            float width = hi.x - lo.x, height = hi.y - lo.y;
            float top = Screen.height - hi.y;
            GUI.DrawTexture(new Rect(lo.x, top, width, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(lo.x, top + height, width, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(lo.x, top, 1, height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(hi.x, top, 1, height), Texture2D.whiteTexture);
        }
        GUI.color = before;
    }

    private static void Label(Vector3 at, string value)
    {
        if (!Camera.main) return;
        Vector3 screen = Camera.main.WorldToScreenPoint(at);
        if (screen.z > 0) GUI.Box(new Rect(screen.x - 100, Screen.height - screen.y, 200, 22), value);
    }

    internal void Observe(string alias) => cameraTarget = alias;

    internal bool Follows(Body body) => Camera.main &&
        ((Vector2)(Camera.main.transform.position - body.transform.position)).sqrMagnitude < 0.25f;

    internal void ApplyCamera(PlayerCamera controller, CharacterFixtures? characters)
    {
        if (controller != PlayerCamera.main || cameraTarget is null || characters is null || !Camera.main) return;
        Vector3 position = characters.Get(cameraTarget).transform.position;
        Transform camera = Camera.main.transform;
        camera.position = new Vector3(position.x, position.y, camera.position.z);
    }

    internal void Restore()
    {
        if (physicsPaused || restoredScale < 0.75f) Time.timeScale = 1;
        if (steppingLimit) Time.maximumDeltaTime = previousMaxDelta;
        steppingLimit = false;
        physicsPaused = false;
        physicsStep = false;
    }
}
