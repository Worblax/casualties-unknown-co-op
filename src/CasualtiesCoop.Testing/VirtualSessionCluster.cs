using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.World;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Gameplay.Terrain;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Session;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class VirtualSessionCluster : IDisposable
{
    internal enum ActorMode { VirtualOnly, NativeActor }

    internal sealed class Actor
    {
        internal readonly string Name;
        internal Vector2 VirtualPosition;
        internal readonly Body? NativeBody;
        internal ActorMode Mode => NativeBody is null ? ActorMode.VirtualOnly : ActorMode.NativeActor;
        internal ParticipantId Participant => Peers.LocalParticipant;
        internal Vector2 Position => NativeBody ? (Vector2)NativeBody!.transform.position : VirtualPosition;
        internal WorldChunkReplication? WorldChunks;
        internal TestChunkGrid? Terrain;
        internal readonly SessionPeers Peers;
        internal GuestItemSession? Guest;
        internal uint TerrainHit;
        internal int TerrainSyncRequests;
        internal uint Claws;

        internal Actor(string name, Vector2 position, Body? body, SessionPeers peers)
        {
            Name = name;
            VirtualPosition = position;
            NativeBody = body;
            Peers = peers;
        }
    }

    internal readonly SimulatedNetwork Network;
    internal readonly SharedItemHostScene Host;
    internal readonly Dictionary<string, Actor> Actors = new(StringComparer.Ordinal);
    internal readonly List<(string Peer, ulong Id, ItemDecision Decision)> Results = new();
    internal readonly Body Body;
    internal readonly Actor? ProjectedActor;
    private SharedItemProjection? projection;
    private readonly TerrainDamageReplication terrain;
    private readonly WorldChunkReplication chunks;
    private readonly bool terrainFixture;
    private readonly Vector2 origin;
    private readonly bool standaloneItems;
    internal Body ProjectionBody => ProjectedActor?.NativeBody ?? Body;
    private SharedItemReplication? nativeItemHooks;

    internal VirtualSessionCluster(ScenarioDocument scenario, (string Name, bool Host, Vector2 Position, Body? Body)[] members, Body body,
        Action<NetworkTrace> trace)
    {
        Body = body;
        origin = body.transform.position;
        terrainFixture = scenario.Level == "UNITY_GUEST" && scenario.Steps.Any(s => s.Value<string>("op") == "terrain.state");
        standaloneItems = scenario.StandaloneItems;
        Network = new SimulatedNetwork(scenario.Seed, scenario.Profile);
        Network.Trace += trace;
        var plan = new RoomLaunchPlan(Guid.NewGuid(), Guid.NewGuid(), "normal", RoomOptions.Sandbox,
            members.Select((_, index) => new LaunchPlayer((ulong)index + 1, 0)).ToArray());
        for (int index = 0; index < members.Length; index++)
        {
            var member = members[index];
            ulong address = (ulong)index + 1;
            var peers = new SessionPeers(Network.AddPeer(address), plan, address, host: 1);
            var actor = new Actor(member.Name, member.Position, member.Body, peers);
            Actors.Add(member.Name, actor);
            if (member.Host)
            {
                continue;
            }
            var guest = new GuestItemSession(peers, Network.Clock);
            actor.Guest = guest;
            guest.ResultAccepted += (request, decision) => Results.Add((actor.Name, request.ItemId, decision));
            if (!terrainFixture)
                actor.Terrain = new TestChunkGrid((int)WorldGeneration.world.width, (int)WorldGeneration.world.height);
            actor.WorldChunks = new WorldChunkReplication(peers, Network.Clock, PositionOf, actor.Terrain);
            peers.ConnectionChanged += (sender, connected) => { if (!connected) actor.WorldChunks.Forget(sender); };
            peers.AttachReceiver((sender, bytes) =>
            {
                if (bytes.Length == 0) return false;
                var kind = (MessageKind)bytes[0];
                if (WorldChunkReplication.Handles(kind)) return actor.WorldChunks.Receive(sender, bytes);
                if (kind == MessageKind.ClawEffect && sender == peers.Host)
                {
                    return true;
                }
                return guest.Receive(sender, bytes);
            });
        }

        SessionPeers hostPeers = Actors.Values.First().Peers;
        if (scenario.Steps.Any(UsesNativePickup))
        {
            nativeItemHooks = new SharedItemReplication(hostPeers, PositionOf, Network.Clock);
            SharedItemHooks.BindTestItems(nativeItemHooks);
            Host = nativeItemHooks.TestHostScene!;
        }
        else
        {
            var hostInventory = new SharedItemInventory { Body = body };
            Host = new SharedItemHostScene(hostPeers, PositionOf, Network.Clock, hostInventory, new SharedItemObjects());
        }
        chunks = new WorldChunkReplication(hostPeers, Network.Clock, PositionOf);
        terrain = new TerrainDamageReplication(hostPeers, Network.Clock, PositionOf, chunks);
        hostPeers.ConnectionChanged += (sender, connected) =>
        {
            if (connected) return;
            terrain.ResetPeer(sender);
            chunks.Forget(sender);
        };
        var claws = new ClawEffects(hostPeers, Network.Clock, PositionOf);
        hostPeers.AttachReceiver(ReceiveHost);
        bool ReceiveHost(ulong sender, byte[] bytes)
        {
            if (bytes.Length == 0) return false;
            var kind = (MessageKind)bytes[0];
            if (kind == MessageKind.WorldChunkControl)
            {
                var requester = Actors.Values.FirstOrDefault(actor => actor.Peers.LocalPlayer == sender);
                if (requester is not null) requester.TerrainSyncRequests++;
            }
            if (WorldChunkReplication.Handles(kind)) return terrainFixture || chunks.Receive(sender, bytes);
            if (kind == MessageKind.TerrainHit) return terrain.Receive(sender, bytes);
            if (kind == MessageKind.ClawEffect) return claws.Receive(sender, bytes);
            return nativeItemHooks?.Receive(sender, bytes) ?? Host.Session.Receive(sender, bytes, worldReady: true);
        }
        foreach (Actor actor in Actors.Values)
        {
            if (actor.Guest is not null)
            {
                Network.SetConnected(1, actor.Peers.LocalPlayer, true);
            }
        }
        if (scenario.Level == "UNITY_GUEST")
        {
            ProjectedActor = Actors.Values.First(actor => actor.Guest is not null);
            projection = new SharedItemProjection(new SharedItemInventory { Body = ProjectionBody }, new SharedItemObjects(),
                Network.Clock, ProjectedActor.Peers.LocalParticipant);
            ProjectedActor.Guest!.StateAccepted += projection.OnStateAccepted;
        }
    }

    private static bool UsesNativePickup(JObject command) =>
        command.Value<string>("op") == "item.nativePickup" ||
        command["actions"] is JArray actions &&
        actions.Cast<JObject>().Any(UsesNativePickup);

    internal IEnumerable<Actor> Guests => Actors.Values.Where(actor => actor.Guest is not null);
    internal bool Ready => Guests.All(actor => actor.Guest!.Synced);

    internal void Tick()
    {
        if (nativeItemHooks is not null) nativeItemHooks.Tick(Body);
        else if (!standaloneItems) Host.Tick();
        foreach (Actor actor in Guests)
        {
            actor.Guest!.Tick();
            if (actor.Peers.IsVerified(actor.Peers.Host)) actor.WorldChunks!.Tick();
        }
        Network.Advance(Math.Min(Time.unscaledDeltaTime, 0.05f));
        if (nativeItemHooks is null) Host.Session.TickSnapshots();
        if (!terrainFixture) chunks.Tick();
        Network.Advance(0);
        if (ProjectedActor is { Guest: { } guest } && projection is not null)
        {
            projection.Tick(guest.Replicas, guest.Synced);
        }
    }

    internal bool HoldsReplica(byte slot, ulong id, Item authoritative) =>
        slot < ProjectionBody.slots.Length && ProjectionBody.GetItem(slot) is { } actual && actual &&
        actual != authoritative && ProjectionBody.HoldingItem(actual) && projection?.TryGetId(actual, out ulong actualId) == true &&
        actualId == id;

    internal void SetConnected(Actor actor, bool connected)
    {
        bool before = actor.Peers.IsVerified(1);
        Network.SetConnected(1, actor.Peers.LocalPlayer, connected);
        if (connected && !before)
        {
            actor.Guest!.ResumeAfterOffer(Host.Session.LastOperation(actor.Peers.LocalPlayer));
            actor.WorldChunks!.Resume(Guid.Empty);
        }
    }

    internal void ApplyHostTerrainHit(Vector2Int cell, float damage) =>
        terrain.ApplyHit(WorldGeneration.world, cell, damage, sound: false, metal: false, ignoreLoot: true);

    internal void HitTerrain(string peer, Vector2Int cell, ushort block, float damage)
    {
        Actor actor = Actors[peer];
        if (actor.Guest is null || block == 0 || !actor.Peers.Send(1,
            SandboxGameplayWire.WriteTerrainHit(actor.Peers.SessionId, cell.x, cell.y, block, ++actor.TerrainHit, damage)))
        {
            throw new InvalidOperationException("Could not send native terrain hit.");
        }
    }

    internal void PublishTerrainFixture(Vector2Int cell, ushort block, float damage, byte liquid, uint revision)
    {
        if (!terrainFixture || ProjectedActor?.WorldChunks is not { } guest || guest.Stream == Guid.Empty)
            throw new InvalidOperationException("Terrain projection fixture requires an open guest stream.");
        SessionPeers host = Actors.Values.First().Peers;
        var grid = new UnityWorldChunkGrid(WorldGeneration.world, FluidManager.main.fluid);
        var key = WorldChunkKey.At(cell.x, cell.y);
        Span<ushort> blocks = stackalloc ushort[WorldChunkWire.CellCount];
        Span<float> cracks = stackalloc float[WorldChunkWire.CellCount];
        Span<byte> water = stackalloc byte[WorldChunkWire.CellCount];
        if (!grid.Read(key, blocks, cracks, water) || !WorldChunkGrid.Extents(grid, key, out int width, out int height))
            throw new InvalidOperationException("Terrain fixture is out of bounds.");
        int i = (cell.y - key.StartY) * WorldChunkWire.Side + cell.x - key.StartX;
        blocks[i] = block;
        cracks[i] = damage;
        water[i] = liquid;
        host.Send(ProjectedActor.Peers.LocalPlayer, WorldChunkWire.WriteSnapshot(host.SessionId,
            new WorldChunkHeader(guest.Stream, revision, key, width, height, WorldChunkWire.Fingerprint(blocks, cracks, water)), blocks, cracks, water));
    }

    internal bool TerrainMatches(Vector2Int cell, string? peer = null)
    {
        var world = WorldGeneration.world;
        byte water = FluidManager.main.GetLiquid(cell.x, cell.y);
        var selected = Guests.Where(actor => peer is null || actor.Name == peer);
        return selected.Any() && selected.All(actor => actor.Terrain is { } grid &&
            grid.TryGet(cell.x, cell.y, out var value) && value.Block == world.GetBlock(cell) &&
            Math.Abs(value.Damage - (world.GetBlockDamage(cell)?.damage ?? 0)) < 0.01f && value.Liquid == water);
    }

    internal bool GateBeforeWater(Vector2Int gate, Vector2Int drain) => Guests.All(actor =>
        actor.Terrain is { } grid && grid.TryGet(drain.x, drain.y, out var water) && (water.Liquid == 0 ||
        (grid.TryGet(gate.x, gate.y, out var wall) && wall.Block == 0)));

    internal void MovePeer(string peer, Vector2 offset)
    {
        Actor actor = Actors[peer];
        if (actor.Mode != ActorMode.VirtualOnly) throw new FormatException("peer.move requires a VirtualOnly guest.");
        actor.VirtualPosition = origin + offset;
    }

    internal bool ChunkAcknowledged(string peer, WorldChunkKey key) => chunks.IsAcknowledged(Actors[peer].Participant, key);
    internal WorldInterest? Interest(string peer) => chunks.Interest(Actors[peer].Participant);

    internal object WorldMetrics() => new
    {
        host = Measure(chunks.Metrics),
        guests = Guests.Select(actor => new { actor.Name, counters = Measure(actor.WorldChunks?.Metrics) }).ToArray()
    };

    private static object? Measure(WorldChunkMetrics? m) => m is null ? null : new
    {
        m.ActiveChunks, m.Subscriptions, m.Pending, m.Frames, m.Bytes, m.Backpressure, m.Repairs, m.Rebuilds,
        m.Applied, m.Resets, m.Captures, m.InitialSyncSeconds, m.LastAcknowledgedSeconds,
        captureMs = 1000d * m.CaptureTicks / System.Diagnostics.Stopwatch.Frequency,
        encodeMs = 1000d * m.EncodeTicks / System.Diagnostics.Stopwatch.Frequency,
        applyMs = 1000d * m.ApplyTicks / System.Diagnostics.Stopwatch.Frequency
    };

    internal void SendClaw(string peer)
    {
        Actor actor = Actors[peer];
        if (actor.Guest is null || !actor.Peers.Send(1,
            SandboxGameplayWire.WriteClaw(actor.Peers.SessionId, actor.Peers.LocalPlayer, ++actor.Claws,
                actor.Position.x, actor.Position.y, 20, true), reliable: false))
        {
            throw new InvalidOperationException("Could not send claw effect.");
        }
    }

    internal bool HasWorldReplica(ulong id, Item authoritative) =>
        projection?.FindReplica(id) is { } item && item && item != authoritative && item.gameObject.activeSelf &&
        !item.transform.parent && !ProjectionBody.HoldingItem(item);

    private bool PositionOf(ulong address, out Vector2 position)
    {
        foreach (var actor in Actors.Values)
        {
            if (actor.Peers.LocalPlayer == address)
            {
                if (actor.Mode == ActorMode.NativeActor && !actor.NativeBody) break;
                position = actor.Position;
                return true;
            }
        }
        position = default;
        return false;
    }

    public void Dispose()
    {
        if (projection is not null)
        {
            ProjectedActor!.Guest!.StateAccepted -= projection.OnStateAccepted;
            projection.Dispose();
            projection = null;
        }
        if (nativeItemHooks is null) Host.Session.Dispose();
        else
        {
            SharedItemHooks.BindTestItems(null);
            nativeItemHooks.Dispose();
            nativeItemHooks = null;
        }
        foreach (Actor actor in Actors.Values)
        {
            actor.Guest?.Dispose();
            actor.Peers.Dispose();
        }
    }
}
