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
        internal SharedItemReplication? ItemReplication;
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
    internal readonly List<(string Peer, int Recipe, ItemDecision Decision)> CraftResults = new();
    internal readonly List<(string Peer, ulong Operation, int Recipe, ItemDecision Decision)> CraftDetails = new();
    internal readonly Body Body;
    internal readonly Actor? ProjectedActor;
    internal TestRemoteItemVisuals? RemoteVisuals;
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
        bool guestCraft = scenario.Steps.Any(UsesGuestCraft) || scenario.Steps.Any(UsesGuestItemReplication);
        terrainFixture = scenario.Level == "UNITY_GUEST" && scenario.Steps.Any(s => s.Value<string>("op") == "terrain.state");
        standaloneItems = scenario.StandaloneItems;
        Network = new SimulatedNetwork(scenario.Seed, scenario.Profile);
        Network.Trace += trace;
        if (scenario.Setup.Any(command => command.Value<string>("op") == "remote.enable"))
        {
            RemoteVisuals = new TestRemoteItemVisuals(Enumerable.Range(1, members.Length)
                .Select(seat => new ParticipantId(seat)));
        }
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
            GuestItemSession guest;
            if (guestCraft)
            {
                if (member.Body is null) throw new FormatException("Guest crafting requires NativeActor guests.");
                var replication = new SharedItemReplication(peers, PositionOf, Network.Clock, RemoteVisuals);
                actor.ItemReplication = replication;
                guest = replication.TestGuest ?? throw new InvalidOperationException("Guest item replication has no session.");
            }
            else
            {
                guest = new GuestItemSession(peers, Network.Clock);
            }
            actor.Guest = guest;
            guest.ResultAccepted += (request, decision) => Results.Add((actor.Name, request.ItemId, decision));
            guest.CraftResultAccepted += (operation, recipe, decision) =>
            {
                CraftResults.Add((actor.Name, recipe, decision));
                CraftDetails.Add((actor.Name, operation, recipe, decision));
            };
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
                return actor.ItemReplication is { } replication
                    ? RunAs(actor, items => items.Receive(sender, bytes))
                    : guest.Receive(sender, bytes);
            });
        }

        SessionPeers hostPeers = Actors.Values.First().Peers;
        if (scenario.Steps.Any(UsesNativePickup))
        {
            nativeItemHooks = new SharedItemReplication(hostPeers, PositionOf, Network.Clock, RemoteVisuals);
            SharedItemHooks.BindTestItems(nativeItemHooks);
            Host = nativeItemHooks.TestHostScene!;
        }
        else
        {
            var hostInventory = new SharedItemInventory { Body = body };
            Host = new SharedItemHostScene(hostPeers, PositionOf, Network.Clock, hostInventory, new SharedItemObjects(),
                RemoteVisuals);
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
                Network.Clock, ProjectedActor.Peers.LocalParticipant, RemoteVisuals);
            ProjectedActor.Guest!.StateAccepted += projection.OnStateAccepted;
        }
    }

    private static bool UsesNativePickup(JObject command) =>
        command.Value<string>("op") is "item.nativePickup" or "item.nativeHostDrop" or "item.nativeHostThrow" ||
        command["actions"] is JArray actions &&
        actions.Cast<JObject>().Any(UsesNativePickup);

    private static bool UsesGuestCraft(JObject command) =>
        command.Value<string>("op") == "item.craft" && command["peer"] is not null ||
        command["actions"] is JArray actions &&
        actions.Cast<JObject>().Any(UsesGuestCraft);

    private static bool UsesGuestItemReplication(JObject command) =>
        command.Value<string>("op") is "item.throw" or "item.swap" or "item.nativeDrop" or "item.nativeThrow" or
            "item.nativePickupPeer" or "item.nativeWear" ||
        command.Value<string>("query") == "items.guestHoverDescription" ||
        command["condition"] is JObject condition && condition.Value<string>("op") == "items.guestHoverDescription" ||
        command["actions"] is JArray actions &&
        actions.Cast<JObject>().Any(UsesGuestItemReplication);

    internal IEnumerable<Actor> Guests => Actors.Values.Where(actor => actor.Guest is not null);
    internal bool Ready => Guests.All(actor => actor.Guest!.Synced);

    internal void Tick()
    {
        if (nativeItemHooks is not null) nativeItemHooks.Tick(Body);
        else if (!standaloneItems) Host.Tick();
        foreach (Actor actor in Guests)
        {
            if (actor.ItemReplication is not null)
                RunAs(actor, items => { items.Tick(actor.NativeBody!); return true; });
            else
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
        if (RemoteVisuals is not null)
        {
            foreach (Actor actor in Actors.Values) RemoteVisuals.SetPosition(actor.Participant, actor.Position);
            RemoteVisuals.Tick();
        }
    }

    internal bool RunAs(Actor actor, Func<SharedItemReplication, bool> action)
    {
        SharedItemReplication items = actor.ItemReplication ??
            throw new InvalidOperationException("Guest production item replication is unavailable.");
        Body body = actor.NativeBody ??
            throw new InvalidOperationException("Guest production item replication requires a native body.");
        SharedItemReplication? previousItems = SharedItemHooks.TestItems;
        PlayerCamera? camera = PlayerCamera.main;
        Body? previousBody = camera ? camera.body : null;
        SharedItemHooks.BindTestItems(items);
        if (camera) camera.body = body;
        try
        {
            return action(items);
        }
        finally
        {
            if (camera) camera.body = previousBody;
            SharedItemHooks.BindTestItems(previousItems);
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
            actor.Guest!.ResumeAfterOffer(Host.Session.LastOperation(actor.Peers.LocalPlayer),
                Host.Session.LastCraftOperation(actor.Peers.LocalPlayer),
                Host.Session.LastRegistrationOperation(actor.Peers.LocalPlayer),
                Host.Session.LastClientKey(actor.Peers.LocalPlayer));
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

    // Mirrors PlayerCamera.HandleWorldHover: a point query with the vanilla layer mask, then the
    // real vanilla tooltip builder. Headless tests cannot move Input.mousePosition, so the point
    // under test is the item itself. Both the guest replica and a shared-scene host object with
    // the same network id are accepted; transport identity, not the fixture object, is the target.
    internal bool HoverDescription(ulong id, out string detail)
    {
        detail = "no-replica";
        Item? replica = projection?.FindReplica(id) ??
            Actors.Values.FirstOrDefault(actor => actor.Guest is not null)?.ItemReplication?.FindReplica(id);
        if (!replica)
        {
            return false;
        }
        int mask = LayerMask.GetMask("Item", "Ground", "Descriptor", "Limb");
        Collider2D? hit = Physics2D.OverlapPoint(replica!.transform.position, mask);
        if (!hit)
        {
            detail = "hit=none";
            return false;
        }
        if (!hit.TryGetComponent<Item>(out Item item))
        {
            detail = $"hit={hit.gameObject.name} item=none";
            return false;
        }
        bool same = item == replica || hit.transform.IsChildOf(replica.transform);
        same |= Host.TryGetId(item, out ulong hostId) && hostId == id;
        if (!same)
        {
            detail = $"hit={hit.gameObject.name} item={item.id} id-mismatch";
            return false;
        }
        (string name, string description) = PlayerCamera.ItemHoverDescription(item);
        detail = $"ok name={name.Length} description={description.Length}";
        return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description);
    }

    internal int GuestReportsSent(string peer) => Actors.TryGetValue(peer, out Actor? actor)
        ? actor.ItemReplication?.ReportsSent ?? 0 : -1;
    internal int GuestNativePlacement(string peer, ulong id) => Actors.TryGetValue(peer, out Actor? actor)
        ? actor.ItemReplication?.LocalPlacementOf(id) ?? -1 : -1;
    internal int GuestCraftPending(string peer) => Actors.TryGetValue(peer, out Actor? actor) &&
        actor.ItemReplication?.CraftPending == true ? 1 : 0;
    internal int GuestReplicaLiquidPositive(string peer, ulong id) =>
        Actors.TryGetValue(peer, out Actor? actor) && actor.ItemReplication?.FindReplica(id) is { } item &&
        item.TryGetComponent<WaterContainerItem>(out WaterContainerItem water) && water.CurrentTotal > 0f ? 1 : 0;
    internal int GuestReplicaLiquidMl(string peer, ulong id) =>
        Actors.TryGetValue(peer, out Actor? actor) && actor.ItemReplication?.FindReplica(id) is { } item &&
        item.TryGetComponent<WaterContainerItem>(out WaterContainerItem water) ? Mathf.RoundToInt(water.CurrentTotal) : -1;
    internal int GuestReplicaLiquidLumalgae(string peer, ulong id) =>
        Actors.TryGetValue(peer, out Actor? actor) && actor.ItemReplication?.FindReplica(id) is { } item &&
        item.TryGetComponent<WaterContainerItem>(out WaterContainerItem water) && water.HasLiquid("lumalgae") ? 1 : 0;
    internal int GuestDefinitionLiquid(string peer, string definition, bool biochem)
    {
        if (!Actors.TryGetValue(peer, out Actor? actor) || actor.ItemReplication is null) return -1;
        foreach (ItemState state in actor.Guest!.Replicas.States)
        {
            if (state.Removed || state.Definition != definition) continue;
            Item? item = actor.ItemReplication.FindReplica(state.Id);
            if (!item)
            {
                item = Item.allItems.FirstOrDefault(candidate => candidate && candidate.id == definition &&
                    candidate.GetComponentInChildren<WaterContainerItem>(true));
            }
            WaterContainerItem? water = item ? item.GetComponentInChildren<WaterContainerItem>(true) : null;
            if (!item || !water) continue;
            if (water.CurrentTotal <= 0f) return 0;
            if (!biochem) return 1;
            return water.stack.Count == 1 && water.stack[0].liquidId == "biochem" ? 1 : 0;
        }
        return -1;
    }
    internal bool WorldReplicaHover(ulong id, out string detail)
    {
        detail = "no-replica";
        if (projection?.FindReplica(id) is not { } replica || !replica) return false;
        Collider2D[] colliders = replica.GetComponentsInChildren<Collider2D>(true);
        detail = $"layer={LayerMask.LayerToName(replica.gameObject.layer)} active={replica.gameObject.activeInHierarchy} " +
            $"parent={(replica.transform.parent ? replica.transform.parent.name : "none")} colliders={colliders.Length}";
        foreach (Collider2D collider in colliders)
            detail += $" [{collider.GetType().Name} enabled={collider.enabled} trigger={collider.isTrigger} " +
                $"layer={LayerMask.LayerToName(collider.gameObject.layer)}]";
        Vector2 at = replica.transform.position;
        int mask = LayerMask.GetMask("Item", "Ground", "Descriptor", "Limb");
        Collider2D hit = Physics2D.OverlapPoint(at, mask);
        if (!hit)
        {
            detail += " hit=none";
            return false;
        }
        bool same = hit.gameObject == replica.gameObject || hit.transform.IsChildOf(replica.transform);
        bool hasItem = hit.TryGetComponent<Item>(out Item hitItem) && hitItem == replica;
        detail += $" hit={hit.gameObject.name} same={same} item={hasItem}";
        return hasItem || same;
    }

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
            if (actor.ItemReplication is { } replication) replication.Dispose();
            else actor.Guest?.Dispose();
            actor.Peers.Dispose();
        }
        RemoteVisuals?.Dispose();
        RemoteVisuals = null;
    }
}
