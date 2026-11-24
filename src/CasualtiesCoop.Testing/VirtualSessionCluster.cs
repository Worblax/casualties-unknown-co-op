using CasualtiesCoop.Core.Items;
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
        private readonly Vector2 virtualPosition;
        internal readonly Body? NativeBody;
        internal ActorMode Mode => NativeBody is null ? ActorMode.VirtualOnly : ActorMode.NativeActor;
        internal ParticipantId Participant => Peers.LocalParticipant;
        internal Vector2 Position => NativeBody ? (Vector2)NativeBody!.transform.position : virtualPosition;
        internal TerrainDamageReplication? TerrainMechanic;
        internal readonly SessionPeers Peers;
        internal GuestItemSession? Guest;
        internal uint TerrainHit;
        internal int TerrainSyncRequests;
        internal uint Claws;
        internal readonly Dictionary<Vector2Int, (ushort Block, float Damage)> Terrain = new();

        internal Actor(string name, Vector2 position, Body? body, SessionPeers peers)
        {
            Name = name;
            virtualPosition = position;
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
    private readonly bool standaloneItems;
    internal Body ProjectionBody => ProjectedActor?.NativeBody ?? Body;
    private SharedItemReplication? nativeItemHooks;

    internal VirtualSessionCluster(ScenarioDocument scenario, (string Name, bool Host, Vector2 Position, Body? Body)[] members, Body body,
        Action<NetworkTrace> trace)
    {
        Body = body;
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
            var guestTerrain = new TerrainDamageReplication(peers, Network.Clock, PositionOf, (world, cell, block, damage) =>
            {
                actor.Terrain[cell] = (block, damage);
                if (scenario.Level == "UNITY_GUEST" && actor == ProjectedActor)
                    TerrainDamageReplication.Project(world, cell, block, damage);
            });
            actor.TerrainMechanic = guestTerrain;
            peers.ConnectionChanged += (sender, connected) => { if (!connected) guestTerrain.ResetPeer(sender); };
            peers.AttachReceiver((sender, bytes) =>
            {
                if (bytes.Length == 0) return false;
                var kind = (MessageKind)bytes[0];
                if (TerrainDamageReplication.Handles(kind)) return guestTerrain.Receive(sender, bytes);
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
        terrain = new TerrainDamageReplication(hostPeers, Network.Clock, PositionOf);
        hostPeers.ConnectionChanged += (sender, connected) => { if (!connected) terrain.ResetPeer(sender); };
        var claws = new ClawEffects(hostPeers, Network.Clock, PositionOf);
        hostPeers.AttachReceiver(ReceiveHost);
        bool ReceiveHost(ulong sender, byte[] bytes)
        {
            if (bytes.Length == 0) return false;
            var kind = (MessageKind)bytes[0];
            if (kind == MessageKind.TerrainSync)
            {
                var requester = Actors.Values.FirstOrDefault(actor => actor.Peers.LocalPlayer == sender);
                if (requester is not null) requester.TerrainSyncRequests++;
            }
            if (TerrainDamageReplication.Handles(kind)) return terrain.Receive(sender, bytes);
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
        terrain.Tick();
        foreach (Actor actor in Guests)
        {
            actor.Guest!.Tick();
            if (actor.Peers.IsVerified(actor.Peers.Host)) actor.TerrainMechanic!.Tick();
        }
        Network.Advance(Math.Min(Time.unscaledDeltaTime, 0.05f));
        if (nativeItemHooks is null) Host.Session.TickSnapshots();
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
            actor.Guest!.ResumeAfterOffer(Host.Session.LastOperation(actor.Peers.LocalPlayer));
    }

    internal void ApplyHostTerrainHit(Vector2Int cell, float damage) =>
        terrain.ApplyHit(WorldGeneration.world, cell, damage, sound: false, metal: false, ignoreLoot: true);

    internal void HitTerrain(string peer, Vector2Int cell, ushort block, float damage)
    {
        Actor actor = Actors[peer];
        if (actor.Guest is null || block == 0 || !actor.Peers.Send(1,
            SandboxGameplayWire.WriteTerrain(actor.Peers.SessionId, MessageKind.TerrainHit, cell.x, cell.y, block, ++actor.TerrainHit, damage)))
        {
            throw new InvalidOperationException("Could not send native terrain hit.");
        }
    }

    internal void PublishTerrainFixture(Vector2Int cell, ushort block, float damage, uint revision)
    {
        SessionPeers host = Actors.Values.First().Peers;
        host.Broadcast(SandboxGameplayWire.WriteTerrain(host.SessionId, MessageKind.TerrainState, cell.x, cell.y,
            block, revision, damage), reliable: true);
    }

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
