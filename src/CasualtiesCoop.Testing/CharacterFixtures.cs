using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static CasualtiesCoop.Testing.ScenarioDocument;

namespace CasualtiesCoop.Testing;

internal sealed class CharacterFixtures : IDisposable
{
    private sealed class Driver
    {
        internal float Direction;
        internal bool Crouch;
        internal bool Driving;
        internal bool CrouchSet;
        internal bool ReleaseJump;
    }

    private static readonly Dictionary<Body, Driver> Drivers = new();
    private readonly Dictionary<string, Body> bodies = new(StringComparer.Ordinal);
    private readonly List<GameObject> spawned = new();
    private readonly Dictionary<Body, Vector2> starts = new();
    private readonly List<JObject> pendingState = new();
    private readonly TestResourceCatalog catalog;
    private readonly FixtureSpace space;
    private readonly int limit;
    internal Body Local { get; }

    internal CharacterFixtures(Body local, FixtureSpace space, TestResourceCatalog catalog, int limit)
    {
        Local = local;
        this.space = space;
        this.catalog = catalog;
        this.limit = limit;
        bodies.Add("local", local);
        starts.Add(local, local.transform.position);
    }

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Body), "FixedUpdate"),
            prefix: new HarmonyMethod(typeof(CharacterFixtures), nameof(DrivePhysics)));
        harmony.Patch(AccessTools.Method(typeof(Body), "Update"),
            prefix: new HarmonyMethod(typeof(CharacterFixtures), nameof(DriveCrouch)));
    }

    private static void DrivePhysics(Body __instance)
    {
        if (Drivers.TryGetValue(__instance, out Driver driver) && driver.Driving)
        {
            __instance.moveDir = new Vector2(driver.Direction, 0);
            if (driver.Direction != 0)
                __instance.targetLookPos = __instance.transform.position + Vector3.right * (driver.Direction * 10);
        }
    }

    private static void DriveCrouch(Body __instance)
    {
        if (!Drivers.TryGetValue(__instance, out Driver driver)) return;
        if (driver.CrouchSet) __instance.crouching = driver.Crouch;
        if (driver.ReleaseJump)
        {
            __instance.endedJump = true;
            driver.ReleaseJump = false;
        }
    }

    internal Body Get(string alias) => bodies.TryGetValue(alias, out Body body) && body
        ? body : throw new FormatException($"Unknown or inactive Body {alias}.");

    internal IEnumerable<KeyValuePair<string, Body>> All => bodies;

    internal void Spawn(JObject command)
    {
        if (bodies.Count >= limit) throw new FormatException("The configured Body fixture limit was reached.");
        string id = String(command, "id");
        if (bodies.ContainsKey(id)) throw new FormatException($"Duplicate Body {id}.");
        int character = catalog.Character(Integer(command, "character", 0, 255));
        GameObject instance = PlayerCamera.main.SpawnCharacter(character);
        if (!instance || !instance.GetComponentInChildren<Body>())
        {
            if (instance) UnityEngine.Object.Destroy(instance);
            throw new TestBlocked($"Character {character} did not create a native Body.");
        }
        spawned.Add(instance);
        instance.transform.position = space.Position(command);
        Body body = instance.GetComponentInChildren<Body>();
        bodies.Add(id, body);
        starts.Add(body, body.transform.position);
    }

    private static Driver For(Body body)
    {
        if (!Drivers.TryGetValue(body, out Driver driver))
        {
            driver = new Driver();
            Drivers.Add(body, driver);
        }
        return driver;
    }

    internal void Move(JObject command)
    {
        Body body = Get(String(command, "target"));
        Driver driver = For(body);
        driver.Direction = Scalar(command, "axis", -1, 1);
        driver.Driving = true;
    }

    internal void Crouch(JObject command)
    {
        Driver driver = For(Get(String(command, "target")));
        driver.Crouch = Flag(command, "enabled");
        driver.CrouchSet = true;
    }

    internal void Jump(string alias)
    {
        Body body = Get(alias);
        bool previous = PlayerCamera.main.wantsJumpInput;
        float vertical = body.rb.velocity.y;
        body.endedJump = false;
        try
        {
            body.Jump();
            if (body.rb.velocity.y > vertical + 0.25f) For(body).ReleaseJump = true;
        }
        finally
        {
            PlayerCamera.main.wantsJumpInput = previous;
        }
    }

    internal void Speed(JObject command)
    {
        Body body = Get(String(command, "target"));
        body.maxSpeed = Scalar(command, "maxSpeed", 0.5f, 20);
    }

    internal void Place(JObject command)
    {
        Body body = Get(String(command, "target"));
        body.rb.position = space.Position(command);
        body.rb.velocity = Vector2.zero;
        Physics2D.SyncTransforms();
    }

    internal void PrepareState(JObject command) => pendingState.Add(command);

    internal void ApplyPreparedState()
    {
        foreach (JObject command in pendingState) State(command);
        pendingState.Clear();
    }

    private void State(JObject command)
    {
        Body body = Get(String(command, "target"));
        if (command["brainHealth"] is not null) body.brainHealth = Scalar(command, "brainHealth", 1, 100);
        if (command["hunger"] is not null) body.hunger = Scalar(command, "hunger", 0, 100);
        if (command["thirst"] is not null) body.thirst = Scalar(command, "thirst", 0, 100);
        if (command["stamina"] is not null) body.stamina = Scalar(command, "stamina", 0, 100);
        if (command["temperature"] is not null) body.temperature = Scalar(command, "temperature", 30, 42);
        if (command["bloodVolume"] is not null) body.bloodVolume = Scalar(command, "bloodVolume", 20, 100);
        if (command["limb"] is not null)
        {
            int index = Integer(command, "limb", 0, body.limbs.Length - 1);
            Limb limb = body.limbs[index];
            if (command["skin"] is not null) limb.skinHealth = Scalar(command, "skin", 1, 100);
            if (command["muscle"] is not null) limb.muscleHealth = Scalar(command, "muscle", 1, 100);
            if (command["pain"] is not null) limb.pain = Scalar(command, "pain", 0, 100);
            if (command["bleeding"] is not null) limb.bleedAmount = Scalar(command, "bleeding", 0, 5);
        }
    }

    internal int Moved(string alias, float distance)
    {
        Body body = Get(alias);
        return Mathf.Abs(body.transform.position.x - starts[body].x) >= distance ? 1 : 0;
    }

    internal object Snapshot(VirtualSessionCluster cluster) => All.Select(pair =>
    {
        Body body = pair.Value;
        return new
        {
            name = pair.Key, active = (bool)body, participant = cluster.Actors.Values.FirstOrDefault(member => member.NativeBody == body)?.Participant.Seat,
            x = body.rb.position.x, y = body.rb.position.y, vx = body.rb.velocity.x, vy = body.rb.velocity.y,
            body.standing, body.grounded, body.crouchAmount, body.maxSpeed, body.brainHealth, body.hunger, body.thirst, body.stamina,
            body.temperature, body.bloodVolume,
            limbs = body.limbs.Select(limb => new { limb.skinHealth, limb.muscleHealth, limb.pain, limb.bleedAmount }).ToArray(),
            inventory = Enumerable.Range(0, body.slots.Length).Select(index => new
            {
                index, hand = body.slots[index].isHand,
                definition = body.GetItem(index) is { } item && item ? item.id : null
            }).ToArray()
        };
    }).ToArray();

    public void Dispose()
    {
        foreach (Body body in bodies.Values) Drivers.Remove(body);
        foreach (GameObject instance in spawned) if (instance) UnityEngine.Object.Destroy(instance);
    }
}
