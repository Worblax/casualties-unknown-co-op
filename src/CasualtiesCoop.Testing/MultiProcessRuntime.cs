using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using CasualtiesCoop.Core;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Gameplay.Terrain;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Runtime;
using CasualtiesCoop.Session;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CasualtiesCoop.Testing;

internal sealed class MultiProcessScenario
{
    internal readonly string Name;
    internal readonly int Seed;
    internal readonly int TimeoutMs;
    internal readonly JObject[] Steps;

    private MultiProcessScenario(string name, int seed, int timeoutMs, JObject[] steps)
    {
        Name = name;
        Seed = seed;
        TimeoutMs = timeoutMs;
        Steps = steps;
    }

    internal static MultiProcessScenario Load(string path)
    {
        JObject root = ScenarioDocument.Read(path, 65536);
        return new MultiProcessScenario(
            ScenarioDocument.String(root, "name"),
            ScenarioDocument.Integer(root, "seed", int.MinValue, int.MaxValue),
            ScenarioDocument.Integer(root, "timeoutMs", 500, 300000),
            ScenarioDocument.Commands(root, "steps"));
    }
}

internal sealed class MultiProcessManifest
{
    internal string RunId = string.Empty;
    internal bool IsHost;
    internal string DataRoot = string.Empty;
    internal Guid SessionId;
    internal ulong LocalId;
    internal ulong HostId;
    internal byte Character;
    internal string RemoteHost = "127.0.0.1";
    internal int TcpPort;
    internal int LocalUdpPort;
    internal double Latency;
    internal double Jitter;
    internal double UnreliableLoss;
    internal LaunchPlayer[] Players = Array.Empty<LaunchPlayer>();
    internal Dictionary<ulong, int> RemoteUdpPorts = new();

    internal ulong RemoteId => Players.First(value => value.SteamId != LocalId).SteamId;

    internal static MultiProcessManifest Load(string path)
    {
        JObject root = ScenarioDocument.Read(path, 65536);
        var manifest = new MultiProcessManifest
        {
            RunId = ScenarioDocument.String(root, "runId"),
            IsHost = ScenarioDocument.String(root, "role") == "host",
            DataRoot = root.Value<string>("dataRoot") ?? throw new FormatException("Missing dataRoot."),
            SessionId = Guid.Parse(ScenarioDocument.String(root, "sessionId")),
            HostId = root.Value<ulong>("hostId"),
            LocalId = root.Value<ulong>("localId"),
            Character = (byte)ScenarioDocument.Integer(root, "character", 0, 3)
        };
        if (manifest.HostId == 0 || manifest.LocalId == 0) throw new FormatException("Invalid participant id.");
        JObject transport = ScenarioDocument.Object(root, "transport");
        manifest.RemoteHost = ScenarioDocument.String(transport, "host");
        manifest.TcpPort = ScenarioDocument.Integer(transport, "tcpPort", 1, 65535);
        manifest.LocalUdpPort = ScenarioDocument.Integer(transport, "localUdpPort", 1, 65535);
        manifest.Latency = transport.Value<float?>("latencyMs") is float latency ? Math.Max(0, latency) / 1000f : 0f;
        manifest.Jitter = transport.Value<float?>("jitterMs") is float jitter ? Math.Max(0, jitter) / 1000f : 0f;
        manifest.UnreliableLoss = transport.Value<float?>("unreliableLoss") is float loss
            ? Math.Clamp(loss, 0f, 1f) : 0f;
        JArray peers = root["peers"] as JArray ?? throw new FormatException("Missing peers.");
        var players = new List<LaunchPlayer>();
        foreach (JObject peer in peers.OfType<JObject>())
        {
            ulong id = peer.Value<ulong>("id");
            byte character = (byte)ScenarioDocument.Integer(peer, "character", 0, 3);
            int port = ScenarioDocument.Integer(peer, "udpPort", 1, 65535);
            if (id == 0 || players.Any(value => value.SteamId == id)) throw new FormatException("Invalid peer id.");
            players.Add(new LaunchPlayer(id, character));
            manifest.RemoteUdpPorts[id] = port;
        }
        manifest.Players = players.ToArray();
        if (manifest.Players.Length != 2 || manifest.Players.All(value => value.SteamId != manifest.LocalId) ||
            manifest.Players.All(value => value.SteamId != manifest.HostId))
            throw new FormatException("Multiprocess manifest requires the local peer and host.");
        return manifest;
    }
}

internal sealed class MultiProcessRuntime : IDisposable
{
    private const string HeldOfferLabel = "coop-held-offer";
    private readonly MultiProcessManifest manifest;
    private readonly MultiProcessScenario scenario;
    private readonly ManualLogSource log;
    private readonly ReportWriter report;
    private readonly TestWorldController scene;
    private readonly TerrainImpactFixture terrainImpact = new();
    private readonly ConsoleSpawnFixture consoleSpawn = new();
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly List<object> assertions = new();
    private MultiProcessTransport? transport;
    private SessionPeers? peers;
    private WorldSession? world;
    private int step;
    private int waitFrames;
    private float stepStarted;
    private bool finished;
    private bool disposed;
    private bool observedRecovery;
    private Item? heldFixture;
    private byte heldFixtureSlot;
    private float? heldFixtureAngle;
    private bool? heldFacingRight;
    private bool heldPickupRequested;
    private ItemDecision? heldPickupDecision;
    private uint heldPickupRevision;
    private ulong observedHeldItem;
    private int synchronizedStep = -1;
    private int nativeSlotActionStep = -1;
    private readonly Dictionary<string, (ulong Id, Item Item, uint Revision)> observedSlotItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2> releaseOrigins = new(StringComparer.Ordinal);

    internal MultiProcessRuntime(string path, ManualLogSource log)
    {
        this.log = log;
        manifest = MultiProcessManifest.Load(path);
        report = new ReportWriter(path);
        scenario = MultiProcessScenario.Load(report.ScenarioPath);
        UnityEngine.Random.InitState(scenario.Seed);
        scene = new TestWorldController();
        stepStarted = Time.realtimeSinceStartup;
    }

    internal void Tick()
    {
        if (finished) return;
        try
        {
            if (world is null)
            {
                if (!scene.ReadyFor(manifest.Character)) return;
                CreateWorld();
                return;
            }

            transport!.Tick();
            world!.Tick();
            observedRecovery |= world.WaitingForRecovery;
        }
        catch (Exception error)
        {
            Finish("FAIL", error.ToString());
        }
    }

    internal void TickPresentation()
    {
        if (finished || world is null) return;
        try
        {
            if (heldFixture && heldFixtureAngle is float angle && scene.Body.GetItem(heldFixtureSlot) == heldFixture)
            {
                if (heldFacingRight is bool facing && scene.Body.isRight != facing) scene.Body.SwitchDir();
                // Drive only the native item transform. Production samples and projects it through the normal pose path.
                heldFixture!.transform.rotation = Quaternion.Euler(0f, 0f, angle);
            }
            world.TickPresentation();
            CheckSlottedFixturesRemainAttached();
            RunScenario();
            if (watch.ElapsedMilliseconds > scenario.TimeoutMs) Finish("FAIL", "Scenario timeout.");
        }
        catch (Exception error)
        {
            Finish("FAIL", error.ToString());
        }
    }

    private void CreateWorld()
    {
        if (scene.Font is null) throw new InvalidOperationException("Sandbox font is unavailable.");
        var plan = new RoomLaunchPlan(manifest.SessionId, Guid.NewGuid(), "normal", RoomOptions.Sandbox,
            manifest.Players);
        transport = new MultiProcessTransport(manifest.SessionId, manifest.LocalId, manifest.HostId,
            manifest.IsHost, manifest.TcpPort, manifest.LocalUdpPort, manifest.RemoteHost,
            manifest.RemoteUdpPorts, manifest.Latency, manifest.Jitter, manifest.UnreliableLoss);
        peers = new SessionPeers(transport, plan, manifest.LocalId, manifest.HostId);
        world = new WorldSession(peers, plan, scene.Font, log, new UnityMonotonicClock());
        SharedItemHooks.BindTestItems(world.Items);
        CharacterSpeechHooks.BindTestCapture(world.CaptureSpeech);
        TerrainDamageReplication.BindTestTerrain(world.Terrain);
        if (world.Items.TestGuest is { } guest) guest.ResultAccepted += ObserveHeldPickup;
        log.LogInfo($"Multiprocess {manifest.RunId} ready as {(manifest.IsHost ? "host" : "guest")} " +
            $"{manifest.LocalId}; tcp={manifest.TcpPort} udp={manifest.LocalUdpPort}.");
    }

    private void RunScenario()
    {
        while (step < scenario.Steps.Length)
        {
            JObject command = scenario.Steps[step];
            if (command.Value<string>("role") is { } role && role != (manifest.IsHost ? "host" : "guest"))
            {
                step++;
                waitFrames = 0;
                stepStarted = Time.realtimeSinceStartup;
                continue;
            }
            if (!Execute(command)) return;
            step++;
            waitFrames = 0;
            stepStarted = Time.realtimeSinceStartup;
        }
        Finish("PASS", null);
    }

    private bool Execute(JObject command)
    {
        string op = ScenarioDocument.String(command, "op");
        switch (op)
        {
            case "wait.ready":
                return Ready() || TimedOut(command);
            case "wait.connected":
                return transport!.VerifiedPeerCount >= 1 || TimedOut(command);
            case "wait.remotePose":
                return world!.TryGetRemotePose(manifest.RemoteId, out _) || TimedOut(command);
            case "wait.frames":
                waitFrames++;
                return waitFrames >= ScenarioDocument.Integer(command, "count", 1, 600);
            case "wait.ms":
                return (Time.realtimeSinceStartup - stepStarted) * 1000f >=
                    ScenarioDocument.Integer(command, "ms", 1, 30000);
            case "character.place":
                Place(command);
                return true;
            case "assert.connected":
                Check(transport!.VerifiedPeerCount >= 1, "transport.connected", 1, transport.VerifiedPeerCount);
                return true;
            case "assert.remoteVisible":
                RemoteCharacterView? view = world!.RemoteViewFor(manifest.RemoteId);
                return WaitOrFail(view is { Visible: true }, command, "remote.visible", 1,
                    view is { Visible: true } ? 1 : 0);
            case "terrain.rememberAudio":
                terrainImpact.RememberExisting();
                return true;
            case "terrain.nativeImpact":
                if (world!.WaitingForWorld) return TimedOut(command);
                bool localImpact = terrainImpact.TryNativeGuestImpact(scene.Body, out object localAudio);
                Check(localImpact, "terrain.guestLocalAudio", true, localImpact, localAudio);
                return true;
            case "assert.terrainImpact":
                if (!world!.TryGetRemotePose(manifest.RemoteId, out CharacterPose actor)) return TimedOut(command);
                bool remoteImpact = terrainImpact.TryObserveHostImpact(new Vector2(actor.X, actor.Y), out object remoteAudio);
                return WaitOrFail(remoteImpact, command, "terrain.hostAudioAndDamage", true, remoteImpact, remoteAudio);
            case "assert.terrainImpactNoEcho":
                bool singleImpact = terrainImpact.ConfirmSingleGuestImpact(out object singleAudio);
                Check(singleImpact, "terrain.guestImpactExactlyOnce", true, singleImpact, singleAudio);
                return true;
            case "item.consoleSpawn":
                if (world!.WaitingForWorld || world.Items.TestGuest is not { Synced: true }) return TimedOut(command);
                consoleSpawn.Spawn(scene.Body);
                return true;
            case "assert.consoleSpawn":
                bool spawnObserved = consoleSpawn.Observe(world!.Items, manifest.IsHost, out object spawnEvidence);
                return WaitOrFail(spawnObserved, command, "item.consoleSpawnIdentityAndCondition", true, spawnObserved, spawnEvidence);
            case "assert.remotePoseNear":
                return AssertRemotePoseNear(command);
            case "assert.isolation":
                bool isolated = scene.VerifyIsolation(out object evidence);
                Check(isolated, "process.isolated", 1, isolated ? 1 : 0, evidence);
                return true;
            case "assert.remoteSpriteCount":
                RemoteCharacterView? counted = world!.RemoteViewFor(manifest.RemoteId);
                int expected = ScenarioDocument.Integer(command, "expected", 0, 512);
                int actual = counted?.SpriteRendererCount ?? -1;
                return WaitOrFail(actual == expected, command, "remote.spriteCount", expected, actual);
            case "assert.remoteVisualsMatchSource":
                AssertRemoteVisualsMatchSource();
                return true;
            case "held.spawn":
                SpawnHeldFixture(command);
                return true;
            case "held.pickup":
                return PickUpHeldFixture(command);
            case "held.offer":
                return OfferHeldFixture(command);
            case "held.findOffer":
                return FindHeldOffer(command);
            case "held.power":
                PowerHeldFixture();
                return true;
            case "assert.localHeldBeam":
                return AssertLocalHeldBeam(command);
            case "held.angle":
                heldFixtureAngle = ScenarioDocument.Scalar(command, "angle", 0f, 360f);
                return true;
            case "held.sweep":
                float from = ScenarioDocument.Scalar(command, "from", 0f, 360f);
                float to = ScenarioDocument.Scalar(command, "angle", 0f, 360f);
                int duration = ScenarioDocument.Integer(command, "ms", 200, 2000);
                float progress = Mathf.Clamp01((Time.realtimeSinceStartup - stepStarted) * 1000f / duration);
                heldFixtureAngle = Mathf.LerpAngle(from, to, progress);
                return progress == 1f;
            case "held.face":
                heldFacingRight = command.Value<bool>("right");
                return true;
            case "held.ragdoll":
                scene.Body.Ragdoll();
                return true;
            case "assert.remoteHeld":
                return AssertRemoteHeld(command);
            case "assert.remoteHeldBasis":
                return AssertRemoteHeldBasis(command);
            case "held.drop":
                scene.Body.DropItem(heldFixture ?? throw new InvalidOperationException("Missing held fixture."));
                return true;
            case "assert.heldBeamReleased":
                return AssertHeldBeamReleased(command);
            case "wait.peerStep":
                return WaitForPeerStep(command);
            case "slots.spawn":
                return SpawnSlotFixture(command);
            case "slots.pickup":
                return PickUpSlotFixture(command);
            case "slots.swap":
                return SwapNativeSlots(command);
            case "slots.switchHands":
                if (world!.Items.TestGuest is { CanSubmit: false }) return TimedOut(command);
                scene.Body.SwitchHands();
                return true;
            case "assert.slots":
                return AssertNativeOrRemoteSlots(command);
            case "release.prepare":
                PrepareRelease(command);
                return true;
            case "release.native":
                return ReleaseNativeItem(command);
            case "assert.released":
                return AssertWorldRelease(command);
            case "speech.talk":
                string line = ScenarioDocument.String(command, "text");
                scene.Body.talker.Talk(line, force: true);
                return true;
            case "assert.remoteSpeech":
                return AssertRemoteSpeech(command);
            case "assert.remoteSpeechCleared":
                string rendered = world!.RemoteViewFor(manifest.RemoteId)?.RenderedSpeech ?? string.Empty;
                return WaitOrFail(rendered.Length == 0, command, "speech.cleared", "", rendered);
            case "transport.disconnect":
                observedRecovery = false;
                transport!.DisconnectFor(ScenarioDocument.Scalar(command, "seconds", 0.25f, 5f));
                return true;
            case "wait.rejoined":
                return WaitOrFail(observedRecovery && world is { WaitingForRecovery: false } &&
                    transport!.VerifiedPeerCount == 1, command, "session.rejoined", true, observedRecovery,
                    new { waiting = world!.WaitingForRecovery, connected = transport?.VerifiedPeerCount });
            default:
                throw new FormatException($"Unknown multiprocess command {op}.");
        }
    }

    private bool Ready() =>
        world is { SceneReady: true } && transport!.VerifiedPeerCount >= 1 &&
        world.TryGetRemotePose(manifest.RemoteId, out _);

    private bool AssertRemoteSpeech(JObject command)
    {
        string expected = ScenarioDocument.String(command, "text");
        RemoteCharacterView? view = world!.RemoteViewFor(manifest.RemoteId);
        string actual = view?.RenderedSpeech ?? string.Empty;
        float distance = view is { Visible: true } ? Vector2.Distance(view.SpeechWorldPosition, view.WorldPosition) : -1f;
        return WaitOrFail(actual == expected && distance is > 1f and < 8f, command, "speech.nativeText",
            expected, actual, new { distance });
    }

    private bool TimedOut(JObject command)
    {
        int timeout = ScenarioDocument.OptionalInt(command, "timeoutMs", 1, 60000, 15000);
        if ((Time.realtimeSinceStartup - stepStarted) * 1000f < timeout) return false;
        throw new InvalidOperationException($"Timed out waiting for {ScenarioDocument.String(command, "op")}.");
    }

    private bool WaitForPeerStep(JObject command)
    {
        // A marker gates test input only. Item placement and observations still come from each Unity world.
        string directory = Path.GetDirectoryName(report.DirectoryPath)!;
        string prefix = $"{manifest.SessionId:N}-step-{step}";
        if (synchronizedStep != step)
        {
            string role = manifest.IsHost ? "host" : "guest";
            File.WriteAllText(Path.Combine(directory, $"{prefix}-{role}.ready"), report.RunId);
            synchronizedStep = step;
        }
        string other = manifest.IsHost ? "guest" : "host";
        return File.Exists(Path.Combine(directory, $"{prefix}-{other}.ready")) || TimedOut(command);
    }

    private ulong SlotActor(JObject command) => ScenarioDocument.String(command, "actor") switch
    {
        "host" => manifest.HostId,
        "guest" => manifest.Players.Single(member => member.SteamId != manifest.HostId).SteamId,
        string actor => throw new FormatException($"Unknown slot test actor {actor}.")
    };

    private static string SlotLabel(string alias) => $"coop-slot-{alias}";

    private bool SpawnSlotFixture(JObject command)
    {
        if (!manifest.IsHost)
        {
            throw new InvalidOperationException("Only the host can spawn a shared slot fixture.");
        }
        Vector2 position = scene.Body.transform.position;
        if (SlotActor(command) != manifest.LocalId)
        {
            if (!world!.TryGetRemotePose(manifest.RemoteId, out CharacterPose pose)) return TimedOut(command);
            position = new Vector2(pose.X, pose.Y);
        }
        Item fixture = CreateHeldFixture(command, position + Vector2.up * 0.5f);
        fixture.label = SlotLabel(ScenarioDocument.String(command, "item"));
        return true;
    }

    private bool PickUpSlotFixture(JObject command)
    {
        string label = SlotLabel(ScenarioDocument.String(command, "item"));
        Body body = scene.Body;
        int slot = ScenarioDocument.Integer(command, "slot", 0, body.slots.Length - 1);
        SharedItemReplication items = world!.Items;
        if (nativeSlotActionStep != step)
        {
            Item? fixture = Item.allItems.FirstOrDefault(item => item && item.gameObject.activeInHierarchy &&
                item.label == label && !item.transform.parent && items.TryGetState(item, out var state) &&
                state.Placement == ItemPlacement.World);
            if (!fixture || (items.TestGuest is not null && !items.TestGuest.CanSubmit) ||
                !body.DoPickupCheck(fixture!, noAlerts: true)) return TimedOut(command);
            if (body.GetItem(slot)) throw new InvalidOperationException($"Slot {slot} must be empty for the pickup fixture.");
            body.PickUpItem(fixture!, slot, force: false);
            nativeSlotActionStep = step;
        }
        return true;
    }

    private bool SwapNativeSlots(JObject command)
    {
        Body body = scene.Body;
        int first = ScenarioDocument.Integer(command, "first", 0, body.slots.Length - 1);
        int second = ScenarioDocument.Integer(command, "second", 0, body.slots.Length - 1);
        if (first == second || (!body.GetItem(first) && !body.GetItem(second)))
        {
            throw new InvalidOperationException("A native slot swap requires distinct slots with at least one held item.");
        }
        if (world!.Items.TestGuest is { CanSubmit: false }) return TimedOut(command);
        body.SwapSlots(first, second);
        return true;
    }

    private bool AssertNativeOrRemoteSlots(JObject command)
    {
        ulong actor = SlotActor(command);
        bool local = actor == manifest.LocalId;
        RemoteCharacterView? view = local ? null : world!.RemoteViewFor(actor);
        if (!local && view is not { Visible: true }) return TimedOut(command);
        Body body = scene.Body;
        if (command["slots"] is not JObject slots || !slots.HasValues) throw new FormatException("Missing slot assertions.");

        var evidence = new List<object>();
        bool matches = true;
        foreach (JProperty entry in slots.Properties())
        {
            if (!byte.TryParse(entry.Name, out byte slot) || slot >= body.slots.Length ||
                entry.Value.Type is not (JTokenType.String or JTokenType.Null))
                throw new FormatException($"Invalid expected slot {entry.Name}.");

            string? alias = entry.Value.Value<string>();
            Item? item;
            ulong id = 0;
            if (local)
            {
                item = body.GetItem(slot);
                if (item) world!.Items.TryGetId(item!, out id);
            }
            else
            {
                view!.TryGetHeldItem(slot, out id, out item);
            }

            bool occupied = item;
            bool hasAuthority = world!.Items.TryGetById(id, out var state) && id != 0 &&
                state.Owner == peers!.Identify(actor) && state.Placement == ItemPlacement.Inventory && state.Slot == slot;
            bool inHierarchy = occupied && (local
                ? item!.transform.parent == body.slots[slot].transform
                : item!.transform.IsChildOf(view!.VisualRoot));
            if (alias is null)
            {
                matches &= !occupied;
            }
            else if (inHierarchy && hasAuthority && item!.label == SlotLabel(alias) && item.gameObject.activeInHierarchy)
            {
                if (observedSlotItems.TryGetValue(alias, out var known) && (id != known.Id || item != known.Item))
                    throw new InvalidOperationException($"The slot move replaced the native instance or network ID of {alias}.");
                observedSlotItems[alias] = (id, item, state.Revision);
            }
            else
            {
                matches = false;
            }
            evidence.Add(new { slot, alias, itemId = id, label = occupied ? item!.label : null,
                instanceId = occupied ? (int?)item!.GetInstanceID() : null, inHierarchy, hasAuthority });
        }
        return WaitOrFail(matches, command, "slots.nativeAndAuthority", true, matches,
            new { actor, viewpoint = local ? "native" : "remote", slots = evidence });
    }

    private void CheckSlottedFixturesRemainAttached()
    {
        foreach (var entry in observedSlotItems)
        {
            if (releaseOrigins.ContainsKey(entry.Key)) continue;
            (ulong id, Item original, _) = entry.Value;
            if (world!.Items.TryGetById(id, out var state) && state.Placement != ItemPlacement.Inventory)
                throw new InvalidOperationException($"Slot move produced a non-inventory record for {entry.Key} ({id}).");
            string label = SlotLabel(entry.Key);
            foreach (Item item in Item.allItems)
            {
                if (item && item.gameObject.activeInHierarchy && item.label == label &&
                    (item != original || !item.transform.parent))
                    throw new InvalidOperationException($"Slot move left a detached or duplicate active object for {entry.Key} ({id}).");
            }
        }
    }

    private void PrepareRelease(JObject command)
    {
        string alias = ScenarioDocument.String(command, "item");
        if (!observedSlotItems.TryGetValue(alias, out var fixture) || !fixture.Item)
            throw new InvalidOperationException($"Release fixture {alias} was not observed in inventory.");
        releaseOrigins.Add(alias, fixture.Item.transform.position);
    }

    private bool ReleaseNativeItem(JObject command)
    {
        string alias = ScenarioDocument.String(command, "item");
        Body body = scene.Body;
        int slot = ScenarioDocument.Integer(command, "slot", 0, body.slots.Length - 1);
        if (!observedSlotItems.TryGetValue(alias, out var fixture) || body.GetItem(slot) != fixture.Item)
            throw new InvalidOperationException($"Native slot {slot} does not contain the release fixture {alias}.");
        if (world!.Items.TestGuest is { CanSubmit: false }) return TimedOut(command);
        if (command.Value<bool?>("throw") == true)
        {
            body.handSlot = slot;
            body.targetLookPos = fixture.Item.transform.position + new Vector3(14f, 8f, 0f);
            body.ThrowItem(0.85f);
        }
        else
        {
            body.DropItem(slot);
        }
        return true;
    }

    private bool AssertWorldRelease(JObject command)
    {
        string alias = ScenarioDocument.String(command, "item");
        if (!observedSlotItems.TryGetValue(alias, out var fixture) || !releaseOrigins.TryGetValue(alias, out Vector2 origin))
            throw new InvalidOperationException($"World release fixture {alias} has no initial inventory observation.");
        float minimumTravel = command["minimumTravel"] is null ? 0f : ScenarioDocument.Scalar(command, "minimumTravel", 0.1f, 20f);
        SharedItemReplication items = world!.Items;
        bool hasWorldState = items.TryGetById(fixture.Id, out var state) && !state.Removed && state.Owner.IsNone &&
            state.Parent == 0 && state.Placement == ItemPlacement.World && state.Revision > fixture.Revision;
        Item? item = manifest.IsHost ? items.TestHostScene!.FindTracked(fixture.Id) : items.FindReplica(fixture.Id);
        bool worldRoot = item && item!.gameObject.activeInHierarchy && !item.transform.parent && item == fixture.Item;
        bool hasCollider = worldRoot && item!.GetComponentsInChildren<Collider2D>(false).Any(collider => collider.enabled);
        bool simulated = worldRoot && item!.rb.simulated;
        RigidbodyType2D expectedType = manifest.IsHost ? RigidbodyType2D.Dynamic : RigidbodyType2D.Kinematic;
        bool physicsMode = worldRoot && item!.rb.bodyType == expectedType;
        float travel = worldRoot ? item!.transform.position.x - origin.x : 0f;
        ulong actor = SlotActor(command);
        bool noInventory = actor == manifest.LocalId
            ? !scene.Body.HoldingItem(fixture.Item)
            : world!.RemoteViewFor(actor) is { } view && !view.TryGetHeldItem(fixture.Id, out _);
        string label = SlotLabel(alias);
        int active = Item.allItems.Count(candidate => candidate && candidate.gameObject.activeInHierarchy && candidate.label == label);
        bool matches = hasWorldState && worldRoot && noInventory && active == 1 &&
            hasCollider && simulated && physicsMode && (minimumTravel == 0f || travel >= minimumTravel);
        return WaitOrFail(matches, command, "release.sameActiveWorldItem", true, matches,
            new { actor, alias, itemId = fixture.Id, revision = state.Revision, worldRoot, noInventory, active,
                hasCollider, simulated, physicsMode, travel, minimumTravel,
                position = item ? new[] { item!.transform.position.x, item.transform.position.y } : null });
    }

    private void Place(JObject command)
    {
        (float x, float y) = ScenarioDocument.Position(command);
        Body body = scene.Body;
        body.transform.position = new Vector3(x, y, body.transform.position.z);
        if (body.rb) body.rb.velocity = Vector2.zero;
    }

    private void SpawnHeldFixture(JObject command)
    {
        if (!manifest.IsHost || heldFixture)
        {
            throw new InvalidOperationException("The held-item fixture requires a single host spawn.");
        }

        heldFixtureSlot = EmptyHandSlot();
        heldFixture = CreateHeldFixture(command, scene.Body.transform.position);
    }

    private byte EmptyHandSlot()
    {
        int slot = Array.FindIndex(scene.Body.slots, candidate => candidate.isHand && candidate.transform.childCount == 0);
        if (slot < 0)
        {
            throw new InvalidOperationException("The native player has no empty hand for the held-item fixture.");
        }
        return checked((byte)slot);
    }

    private static Item CreateHeldFixture(JObject command, Vector2 position)
    {
        var catalog = new TestResourceCatalog(WorldGeneration.world);
        string resource = catalog.ItemResource(ScenarioDocument.String(command, "definition"), networked: true);
        return Utils.Create(resource, position, 0f).GetComponent<Item>();
    }

    private bool PickUpHeldFixture(JObject command)
    {
        Item item = heldFixture ?? throw new InvalidOperationException("The held-item fixture has not been spawned.");
        SharedItemReplication items = world!.Items;
        if (!items.TryGetState(item, out var state))
        {
            return TimedOut(command);
        }

        Body body = scene.Body;
        if (heldPickupDecision == ItemDecision.Unavailable && state.Revision != heldPickupRevision)
        {
            heldPickupRequested = false;
            heldPickupDecision = null;
        }
        if (!heldPickupRequested && state.Placement == ItemPlacement.World &&
            (manifest.IsHost || items.TestGuest is { CanSubmit: true }) && body.DoPickupCheck(item, noAlerts: true))
        {
            body.PickUpItem(item, heldFixtureSlot, force: false);
            heldPickupRequested = true;
            heldPickupRevision = state.Revision;
        }
        bool nativeHeld = body.GetItem(heldFixtureSlot) == item &&
            state.Owner == items.LocalParticipant && state.Placement == ItemPlacement.Inventory;
        return WaitOrFail(nativeHeld, command, "held.nativePickup", true, nativeHeld,
            new { itemId = state.Id, instanceId = item.GetInstanceID(), slot = heldFixtureSlot, requested = heldPickupRequested,
                decision = heldPickupDecision?.ToString(),
                requestedRevision = heldPickupRevision, currentRevision = state.Revision,
                placement = state.Placement.ToString(), itemPosition = new[] { item.transform.position.x, item.transform.position.y },
                bodyPosition = new[] { body.transform.position.x, body.transform.position.y } });
    }

    private void ObserveHeldPickup(SharedItemRequest request, ItemDecision decision)
    {
        if (request.Action == ItemAction.PickUp && heldFixture &&
            world!.Items.TryGetId(heldFixture!, out ulong id) && request.ItemId == id)
        {
            heldPickupDecision = decision;
        }
    }

    private bool OfferHeldFixture(JObject command)
    {
        if (!manifest.IsHost)
        {
            throw new InvalidOperationException("Only the host can offer a world fixture.");
        }
        if (!world!.TryGetRemotePose(manifest.RemoteId, out CharacterPose pose))
        {
            return TimedOut(command);
        }
        Item offer = CreateHeldFixture(command, new Vector2(pose.X, pose.Y + 0.5f));
        // Hold the aiming fixture above the floor until native pickup. Release restores prefab physics.
        offer.rb.gravityScale = 0f;
        offer.label = HeldOfferLabel;
        world.Items.Observe(offer);
        return true;
    }

    private bool FindHeldOffer(JObject command)
    {
        string definition = ScenarioDocument.String(command, "definition");
        Item? offered = Item.allItems.FirstOrDefault(item => item && item.gameObject.activeInHierarchy &&
            item.id == definition && item.label == HeldOfferLabel && !item.transform.parent &&
            world!.Items.TryGetState(item, out var state) && state.Placement == ItemPlacement.World);
        if (!offered)
        {
            return TimedOut(command);
        }
        heldFixtureSlot = EmptyHandSlot();
        heldFixture = offered;
        return true;
    }

    private void PowerHeldFixture()
    {
        Item item = heldFixture ?? throw new InvalidOperationException("The held-item fixture has not been spawned.");
        if (item.id != "flashlight" || !item.battery || !item.TryGetComponent(out CustomItemBehaviour behavior))
        {
            throw new InvalidOperationException("The powered fixture requires a native flashlight and battery component.");
        }

        if (!item.battery.hasBattery)
        {
            Item battery = Utils.Create("smallbattery", item.transform.position, 0f).GetComponent<Item>();
            item.battery.LoadBattery(battery);
        }
        item.condition = 1f;
        behavior.state = 1;
    }

    private bool AssertLocalHeldBeam(JObject command)
    {
        Item item = heldFixture ?? throw new InvalidOperationException("The held-item fixture has not been spawned.");
        Light2D? beam = item.transform.childCount > 0 ? item.transform.GetChild(0).GetComponent<Light2D>() : null;
        if (!beam) throw new InvalidOperationException("The native flashlight has no emitter child.");
        bool lit = item.battery && item.battery.hasCharge && beam!.isActiveAndEnabled && beam.intensity > 0.05f;
        return WaitOrFail(lit, command, "held.nativeBeamPowered", true, lit,
            new { condition = item.condition, hasBattery = item.battery && item.battery.hasBattery,
                behaviorState = item.GetComponent<CustomItemBehaviour>().state,
                active = beam!.isActiveAndEnabled, intensity = beam.intensity });
    }

    private bool AssertRemoteHeld(JObject command)
    {
        string definition = ScenarioDocument.String(command, "definition");
        float expectedAngle = ScenarioDocument.Scalar(command, "angle", 0f, 360f);
        float tolerance = ScenarioDocument.Scalar(command, "tolerance", 0.1f, 30f);
        float? wrapFrom = command["wrapFrom"] is null ? null : ScenarioDocument.Scalar(command, "wrapFrom", 0f, 360f);
        bool assertBeam = command.Value<bool?>("beam") == true;
        RemoteCharacterView? view = world!.RemoteViewFor(manifest.RemoteId);
        if (view is not { Visible: true } || !world.TryGetRemotePose(manifest.RemoteId, out CharacterPose pose))
        {
            return TimedOut(command);
        }

        foreach (HeldItemPose sample in pose.HeldItems)
        {
            if (!world.Items.TryGetById(sample.ItemId, out var state) || state.Definition != definition)
            {
                continue;
            }
            if (observedHeldItem != 0 && sample.ItemId != observedHeldItem)
            {
                throw new InvalidOperationException($"Held fixture changed network identity from {observedHeldItem} to {sample.ItemId}.");
            }
            bool placementMatches = state.Owner == peers!.Identify(manifest.RemoteId) &&
                state.Placement == ItemPlacement.Inventory && state.Slot == sample.Slot;
            if (!placementMatches || !view.TryGetHeldItem(sample.ItemId, out Item? mappedItem) || !mappedItem)
            {
                return TimedOut(command);
            }

            Item item = mappedItem!;
            Item[] active = view.VisualRoot.GetComponentsInChildren<Item>(false)
                .Where(candidate => candidate.id == definition).ToArray();
            float angle = item.transform.eulerAngles.z;
            bool angleMatches = Mathf.Abs(Mathf.DeltaAngle(angle, expectedAngle)) <= tolerance;
            if (wrapFrom is float from)
            {
                float travel = Mathf.Abs(Mathf.DeltaAngle(from, expectedAngle)) + tolerance;
                float fromCurrent = Mathf.Abs(Mathf.DeltaAngle(from, angle));
                if (fromCurrent > travel)
                {
                    Check(false, "held.shortestWrapPath", travel, fromCurrent);
                }
            }

            Item[] sameId = Item.allItems.Where(candidate => candidate && candidate.gameObject.activeInHierarchy &&
                world.Items.TryGetId(candidate, out ulong id) && id == sample.ItemId).ToArray();
            object evidence = new { itemId = sample.ItemId, slot = sample.Slot, instanceId = item.GetInstanceID(),
                path = TemplatePath(view.VisualRoot, item.transform), poseAngle = sample.Angle, projectedAngle = angle,
                activeRegisteredInstances = sameId.Select(candidate => candidate.GetInstanceID()).ToArray(),
                activeObjects = active.Select(candidate => new { instanceId = candidate.GetInstanceID(),
                    path = TemplatePath(view.VisualRoot, candidate.transform) }).ToArray() };
            if (!angleMatches)
            {
                return WaitOrFail(false, command, "held.worldAngle", expectedAngle, angle, evidence);
            }
            bool checkPower = command.Value<bool?>("powerState") == true;
            int power = checkPower ? item.GetComponent<CustomItemBehaviour>()?.state ?? -1 : 0;
            if (checkPower && power != 1)
            {
                return WaitOrFail(false, command, "held.authoritativePowerState", 1, power,
                    new { itemId = sample.ItemId, state.Revision });
            }
            if (assertBeam && !AssertRemoteHeldBeam(command, sample.ItemId, item))
            {
                return false;
            }
            if (checkPower) Check(true, "held.authoritativePowerState", 1, power, new { itemId = sample.ItemId, state.Revision });
            Check(true, "held.worldAngle", expectedAngle, angle, evidence);
            if (wrapFrom is float start)
            {
                float travel = Mathf.Abs(Mathf.DeltaAngle(start, expectedAngle)) + tolerance;
                Check(true, "held.shortestWrapPath", travel, Mathf.Abs(Mathf.DeltaAngle(start, angle)), evidence);
            }
            SpriteRenderer? sprite = item.GetComponent<SpriteRenderer>();
            bool visible = sprite && sprite!.enabled && sprite.sprite;
            bool passive = !item.rb.simulated && !item.GetComponentsInChildren<Collider2D>(true).Any(collider => collider.enabled);
            Check(active.Length == 1 && active[0] == item && sameId.All(candidate => candidate == item) && visible,
                "held.singleVisibleItem", 1, active.Length, evidence);
            Check(passive, "held.visualOnly", true, passive, evidence);
            observedHeldItem = sample.ItemId;
            return true;
        }

        return TimedOut(command);
    }

    private bool AssertRemoteHeldBeam(JObject command, ulong id, Item item)
    {
        Light2D? native = item.transform.childCount > 0 ? item.transform.GetChild(0).GetComponent<Light2D>() : null;
        if (!native)
        {
            throw new InvalidOperationException("The remote flashlight has no native emitter child.");
        }
        bool found = world!.TryGetHeldBeam(manifest.RemoteId, id, 0, out Light2D? beam, out bool markerVisible);
        float? angularError = null;
        float? positionError = null;
        bool lit = false;
        if (found && beam)
        {
            angularError = Vector2.Angle(beam!.transform.TransformVector(Vector3.up), native!.transform.TransformVector(Vector3.up));
            positionError = Vector2.Distance(beam.transform.position, native.transform.position);
            lit = beam.isActiveAndEnabled && beam.intensity > 0.05f && beam != native;
        }
        bool nativeDisabled = !native!.enabled;
        return WaitOrFail(lit && nativeDisabled && !markerVisible && angularError <= 1f && positionError <= 0.03f,
            command, "held.singleAlignedBeam", true, lit && nativeDisabled && !markerVisible,
            new { itemId = id, projected = found, lit, nativeDisabled, markerVisible, angularError, positionError });
    }

    private bool AssertRemoteHeldBasis(JObject command)
    {
        WorldSession session = world!;
        RemoteCharacterView? view = session.RemoteViewFor(manifest.RemoteId);
        if (view is not { Visible: true } || !session.TryGetRemotePose(manifest.RemoteId, out CharacterPose pose) ||
            !view.TryGetHeldItem(observedHeldItem, out Item? item) || !item)
        {
            return TimedOut(command);
        }

        HeldItemPose sample = Array.Find(pose.HeldItems, held => held.ItemId == observedHeldItem);
        if (sample.ItemId == 0) return TimedOut(command);
        Vector2 right = item!.transform.TransformVector(Vector3.right);
        Vector2 up = item.transform.TransformVector(Vector3.up);
        bool mirrored = right.x * up.y < right.y * up.x;
        Vector2 target = Quaternion.Euler(0f, 0f, sample.Angle) * Vector3.right;
        float error = Vector2.Angle(mirrored ? -right : right, target);
        bool rightFacing = command.Value<bool>("right");
        bool standing = command.Value<bool>("standing");
        bool matches = pose.FacingRight == rightFacing && pose.Standing == standing && mirrored == sample.Mirrored && error <= 3f;
        return WaitOrFail(matches, command, "held.worldBasis", true, matches,
            new { facingRight = pose.FacingRight, pose.Standing, mirrored, sentMirrored = sample.Mirrored, angularError = error });
    }

    private bool AssertHeldBeamReleased(JObject command)
    {
        WorldSession session = world!;
        bool released = observedHeldItem != 0 && session.Items.TryGetById(observedHeldItem, out var state) &&
            state.Placement == ItemPlacement.World && state.Owner.IsNone;
        if (!released) return TimedOut(command);

        bool stillLit = session.TryGetHeldBeam(manifest.RemoteId, observedHeldItem, 0, out Light2D? beam, out _) &&
            beam && beam!.isActiveAndEnabled;
        Check(!stillLit, "held.releasedBeamCleared", false, stillLit, new { itemId = observedHeldItem });
        return true;
    }

    private bool AssertRemotePoseNear(JObject command)
    {
        (float x, float y) = ScenarioDocument.Position(command);
        float tolerance = command["tolerance"] is null ? 0.25f : ScenarioDocument.Scalar(command, "tolerance", 0f, 64f);
        if (!world!.TryGetRemotePose(manifest.RemoteId, out CharacterPose pose))
        {
            if (TimedOut(command)) return true;
            return false;
        }
        float dx = Math.Abs(pose.X - x);
        float dy = Math.Abs(pose.Y - y);
        bool ok = dx <= tolerance && dy <= tolerance;
        return WaitOrFail(ok, command, "remote.poseNear", $"{x:F2},{y:F2}", $"{pose.X:F2},{pose.Y:F2}");
    }

    private void Check(bool ok, string query, object expected, object actual, object? evidence = null)
    {
        assertions.Add(new { query, expected, actual, evidence, role = manifest.IsHost ? "host" : "guest" });
        if (!ok) throw new InvalidOperationException($"{query} expected {expected}, got {actual}.");
    }

    private void AssertRemoteVisualsMatchSource()
    {
        ulong remoteId = manifest.RemoteId;
        LaunchPlayer member = manifest.Players.First(value => value.SteamId == remoteId);
        Body? template = RemoteCharacterView.LoadCharacter(member.Character);
        RemoteCharacterView? view = world!.RemoteViewFor(remoteId);
        if (!template || view is not { Visible: true })
        {
            throw new InvalidOperationException("The source prefab and a visible remote character are required.");
        }

        Transform root = view.VisualRoot;
        SpriteRenderer[] remote = root.GetComponentsInChildren<SpriteRenderer>(true);
        report.Write("remote-visuals.json", new
        {
            source = DumpTemplate(template),
            remote = new
            {
                visible = view.Visible,
                renderers = remote.Select(renderer => new
                {
                    path = TemplatePath(root, renderer.transform),
                    active = renderer.gameObject.activeInHierarchy,
                    enabled = renderer.enabled,
                    sprite = renderer.sprite ? renderer.sprite.name : null,
                    sortingOrder = renderer.sortingOrder,
                    position = new[] { renderer.transform.localPosition.x, renderer.transform.localPosition.y },
                    scale = new[] { renderer.transform.localScale.x, renderer.transform.localScale.y }
                }).ToArray()
            }
        });

        FacialExpression? expression = template!.GetComponentInChildren<FacialExpression>(true);
        CharacterFace? face = expression ? new CharacterFace(expression!) : null;
        var dynamicSprites = new HashSet<string>(StringComparer.Ordinal);
        var expected = new List<string>();
        foreach (SpriteRenderer renderer in template.GetComponentsInChildren<SpriteRenderer>(true))
        {
            Limb? limb = renderer.GetComponentInParent<Limb>(true);
            if (!limb || !renderer.enabled || !ActiveBelowRoot(renderer.transform, template.transform))
            {
                continue;
            }

            string path = $"{limb!.transform.name}/{renderer.name}";
            if (face is not null && (renderer == face.Head || renderer == face.Eyes))
            {
                dynamicSprites.Add(path);
            }
            expected.Add(RendererKey(path, renderer, dynamicSprites));
        }

        string[] actual = remote.Where(renderer => renderer.gameObject.activeInHierarchy && renderer.enabled)
            .Select(renderer => RendererKey(TemplatePath(root, renderer.transform), renderer, dynamicSprites))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        expected.Sort(StringComparer.Ordinal);
        Check(expected.Count > 0 && expected.SequenceEqual(actual), "remote.visualsMatchSource",
            string.Join(", ", expected), string.Join(", ", actual));
    }

    private static string RendererKey(string path, SpriteRenderer renderer, HashSet<string> dynamicSprites)
    {
        // The current expression can substitute the prefab's head and eye sprites.
        string sprite = dynamicSprites.Contains(path) && renderer.sprite ? "<expression>" : renderer.sprite ? renderer.sprite.name : "<empty>";
        return $"{path} [{sprite}, {renderer.sortingLayerID}:{renderer.sortingOrder}, {renderer.flipX}:{renderer.flipY}]";
    }

    private static object? DumpTemplate(Body? template)
    {
        if (template is null || !template) return null;
        return new
        {
            rootActive = template.gameObject.activeInHierarchy,
            renderers = template.GetComponentsInChildren<SpriteRenderer>(true).Select(renderer => new
            {
                path = TemplatePath(template.transform, renderer.transform),
                active = renderer.gameObject.activeInHierarchy,
                activeSelf = renderer.gameObject.activeSelf,
                branchActive = ActiveBelowRoot(renderer.transform, template.transform),
                enabled = renderer.enabled,
                sprite = renderer.sprite ? renderer.sprite.name : null,
                sortingOrder = renderer.sortingOrder,
                limb = renderer.GetComponentInParent<Limb>(true)?.name
            }).ToArray()
        };
    }

    private static string TemplatePath(Transform root, Transform target)
    {
        var parts = new List<string>();
        for (Transform? current = target; current && current != root; current = current.parent) parts.Add(current.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static bool ActiveBelowRoot(Transform target, Transform root)
    {
        for (Transform? current = target; current && current != root; current = current.parent)
        {
            if (!current.gameObject.activeSelf) return false;
        }
        return true;
    }

    private bool WaitOrFail(bool ok, JObject command, string query, object expected, object actual,
        object? evidence = null)
    {
        if (ok)
        {
            Check(true, query, expected, actual, evidence);
            return true;
        }
        int timeout = ScenarioDocument.OptionalInt(command, "timeoutMs", 1, 60000, 15000);
        if ((Time.realtimeSinceStartup - stepStarted) * 1000f < timeout) return false;
        Check(false, query, expected, actual, evidence);
        return false;
    }

    private void Finish(string status, string? failure)
    {
        if (finished) return;
        finished = true;
        report.Write("result.json", new
        {
            runId = manifest.RunId,
            role = manifest.IsHost ? "host" : "guest",
            pid = Process.GetCurrentProcess().Id,
            scenario = scenario.Name,
            seed = scenario.Seed,
            status,
            durationMs = watch.ElapsedMilliseconds,
            executedSteps = step,
            activeOperation = step < scenario.Steps.Length ? scenario.Steps[step].Value<string>("op") : null,
            assertions,
            failure,
            transport = new { tcp = manifest.TcpPort, udp = manifest.LocalUdpPort, stats = transport?.Stats,
                latencyMs = manifest.Latency * 1000f, jitterMs = manifest.Jitter * 1000f, loss = manifest.UnreliableLoss,
                poseInbound = world?.PoseInbound ?? 0, poseFrames = world?.PoseFrames ?? 0, poseAccepted = world?.PoseAccepted ?? 0,
                poseInvalid = world?.PoseInvalid ?? 0, poseSourceRejected = world?.PoseSourceRejected ?? 0,
                poseStale = world?.PoseStale ?? 0, remotes = world?.RemoteCount ?? 0 }
        });
        log.LogInfo($"Multiprocess transport {transport?.Stats ?? "none"}.");
        log.LogInfo($"Multiprocess {manifest.RunId} {status}{(failure is null ? "" : ": " + failure)}.");
        Dispose();
        Application.Quit(status == "PASS" ? 0 : 1);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (world is not null && ReferenceEquals(SharedItemHooks.TestItems, world.Items))
        {
            SharedItemHooks.BindTestItems(null);
            CharacterSpeechHooks.BindTestCapture(null);
            TerrainDamageReplication.BindTestTerrain(null);
        }
        world?.Dispose();
        terrainImpact.Dispose();
        peers?.Dispose();
        transport?.Dispose();
        scene.Dispose();
        report.Dispose();
    }
}
