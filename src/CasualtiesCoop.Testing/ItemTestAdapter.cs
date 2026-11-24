using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Networking;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class ItemTestAdapter : IDisposable
{
    private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
    private readonly List<(ulong Sender, SharedItemRequest Request)> deliveries = new();
    private readonly Dictionary<string, byte> slots = new(StringComparer.Ordinal);
    private readonly bool standalone;
    private readonly Dictionary<string, ulong> ids = new(StringComparer.Ordinal);

    internal readonly VirtualSessionCluster Cluster;

    internal ItemTestAdapter(ScenarioDocument scenario, WorldFixtures fixtures, Action<NetworkTrace> trace)
    {
        standalone = scenario.StandaloneItems;
        Body body = fixtures.Characters.Local;
        Vector2 origin = fixtures.Space.Origin + new Vector2(0, 0.8f);
        JObject[] roster = scenario.Setup.Where(command => Op(command) == "peer.add").ToArray();
        if (roster.Length is < 1 or > 4 || scenario.Setup.Count(command => Op(command) == "world.loadSandbox") != 1 ||
            Op(scenario.Setup[0]) != "world.loadSandbox")
        {
            throw new FormatException("Provide one sandbox setup followed by one to four peers.");
        }

        var members = roster.Select((command, index) =>
        {
            string alias = ScenarioDocument.String(command, "id");
            string role = ScenarioDocument.String(command, "role");
            if (role != (index == 0 ? "host" : "guest")) throw new FormatException("Roster must start with one host.");
            string mode = command.Value<string>("mode") ?? (index == 0 ? "NativeActor" : "VirtualOnly");
            Body? native = mode == "NativeActor" ? fixtures.Characters.Get(command.Value<string>("body") ??
                (index == 0 ? "local" : throw new FormatException("NativeActor needs a Body alias."))) : null;
            if (index == 0 && native != body || mode == "VirtualOnly" && command["body"] is not null)
                throw new FormatException("The host must bind the local Body; VirtualOnly has no Body.");
            return (alias, index == 0, command["position"] is null ? origin : origin + Offset(command), native);
        }).ToArray();
        if (members.Select(entry => entry.alias).Distinct().Count() != members.Length)
        {
            throw new FormatException("Duplicate peer alias.");
        }

        if (members.Where(member => member.native).Select(member => member.native).Distinct().Count() !=
            members.Count(member => member.native))
            throw new FormatException("Each NativeActor requires a distinct Body.");
        if (standalone && (scenario.Level != "UNITY_ACTORS" || members.Length != 1))
            throw new FormatException("Standalone native items require UNITY_ACTORS with just the local host.");

        var spawn = scenario.Setup.Where(command => Op(command) == "item.spawn").ToArray();
        if (spawn.Length > scenario.ItemLimit) throw new FormatException("The configured item fixture limit was reached.");
        var entries = spawn.Select(command =>
        {
            string id = ScenarioDocument.String(command, "id");
            string resource = fixtures.Catalog.ItemResource(ScenarioDocument.String(command, "catalogId"), networked: !standalone);
            return (id, resource, position: origin + Offset(command));
        }).ToArray();
        if (entries.Select(fixture => fixture.id).Distinct().Count() != entries.Length)
        {
            throw new FormatException("Duplicate item alias.");
        }

        Cluster = new VirtualSessionCluster(scenario, members, body, Observe);
        try
        {
            foreach (var fixture in entries)
            {
                Item item = Utils.Create(fixture.resource, fixture.position, 0f).GetComponent<Item>();
                items.Add(fixture.id, item);
                if (standalone) FluidItemProbe.Track(item);
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        void Observe(NetworkTrace record)
        {
            trace(record);
            if (record.Event == "deliver" && record.Frame.Receiver == 1 && record.Frame.Data.Length > 17 &&
                ItemWire.TryReadRequest(record.Frame.Data.Span, new Guid(record.Frame.Data.Span.Slice(1, 16)), out var request))
            {
                if (deliveries.Count < 256) deliveries.Add((record.Frame.Sender, request));
            }
        }
    }

    internal void Tick()
    {
        Cluster.Tick();
        if (standalone) FluidItemProbe.Tick();
    }
    internal bool MovedWithFlow(string alias) => FluidItemProbe.Moved(ItemFor(alias));
    internal IEnumerable<KeyValuePair<string, Item>> All => items;

    internal bool Ready => standalone || Cluster.Ready && items.Values.All(item => item && Cluster.Host.TryGetId(item, out ulong id) &&
        Cluster.Guests.All(actor => actor.Guest!.Replicas.TryGet(id, out _)));

    internal void Connect(JObject action, bool connected)
    {
        string peer = ScenarioDocument.String(action, "peer");
        if (!Cluster.Actors.TryGetValue(peer, out var actor) || actor.Guest is null)
        {
            throw new FormatException($"Unknown guest {peer}.");
        }
        Cluster.SetConnected(actor, connected);
    }

    internal void PickUp(JObject action)
    {
        string peer = ScenarioDocument.String(action, "peer");
        string alias = ScenarioDocument.String(action, "item");
        if (!Cluster.Actors.TryGetValue(peer, out var actor) || actor.Guest is null)
        {
            throw new FormatException($"Pickup requires a guest peer, got {peer}.");
        }
        Item item = ItemFor(alias);
        ulong id = IdFor(alias);
        int slot;
        if (action["slot"]?.Type == JTokenType.Integer)
        {
            slot = ScenarioDocument.Integer(action, "slot", 0, 254);
        }
        else if (ScenarioDocument.String(action, "slot") == "$fixture.emptyPickupSlot")
        {
            slot = Array.FindIndex((actor.NativeBody ?? Cluster.Body).slots, candidate => candidate.isHand && candidate.transform.childCount == 0);
            if (slot < 0) throw new TestBlocked("The native Body has no empty hand slot.");
        }
        else
        {
            throw new FormatException("Unknown slot fixture.");
        }
        slots[peer + "/" + alias] = (byte)slot;
        if (!actor.Guest.TrySubmit(id, ItemAction.PickUp, (byte)slot, default))
        {
            throw new InvalidOperationException($"Guest {peer} could not submit the pickup.");
        }
    }

    internal void NativePickup(JObject action)
    {
        string alias = ScenarioDocument.String(action, "item");
        Item item = ItemFor(alias);
        int slot = Array.FindIndex(Cluster.Body.slots, candidate => candidate.isHand && candidate.transform.childCount == 0);
        if (slot < 0) throw new TestBlocked("The native Body has no empty hand slot.");
        slots["host/" + alias] = (byte)slot;
        Cluster.Body.PickUpItem(item, slot, force: false);
    }

    internal void Drop(JObject action)
    {
        string peer = ScenarioDocument.String(action, "peer");
        ulong id = IdFor(ScenarioDocument.String(action, "item"));
        if (!Cluster.Actors.TryGetValue(peer, out var actor) || actor.Guest is null ||
            !actor.Guest.Replicas.TryGet(id, out var replica) || replica.Owner != actor.Peers.LocalParticipant)
        {
            throw new InvalidOperationException($"Guest {peer} does not own the item.");
        }
        if (!actor.Guest.TrySubmit(id, ItemAction.Throw, replica.Slot,
            new ItemLocation(actor.Position.x, actor.Position.y, 0, 3)))
        {
            throw new InvalidOperationException($"Guest {peer} could not submit the throw.");
        }
    }

    internal void Place(string alias, JObject command)
    {
        var position = ScenarioDocument.Position(command);
        ItemFor(alias).transform.position = new Vector3(position.X, position.Y, 0f);
    }

    internal void SetCondition(string alias, float condition) => ItemFor(alias).condition = Mathf.Clamp01(condition);

    internal void SetWet(string alias, bool wet)
    {
        Item item = ItemFor(alias);
        item.wetTime = wet ? Time.time : -1000f;
    }

    internal void SetGun(string alias, int rounds, int magazine, bool hasMag, bool safe, bool racked)
    {
        Item item = ItemFor(alias);
        GunScript? gun = item.GetComponent<GunScript>();
        if (!gun) throw new TestBlocked($"{alias} is not a gun.");
        gun.roundsInMag = Mathf.Clamp(rounds, 0, gun.magCapacity);
        gun.hasMag = hasMag;
        gun.safe = safe;
        gun.racked = racked;
        gun.roundInChamber = magazine switch
        {
            1 => GunScript.RoundInChamber.Round,
            2 => GunScript.RoundInChamber.Casing,
            _ => GunScript.RoundInChamber.None
        };
    }

    internal void SetLiquid(string alias, string liquid, float amount, bool clear)
    {
        Item item = ItemFor(alias);
        WaterContainerItem? water = item.GetComponent<WaterContainerItem>();
        if (!water) throw new TestBlocked($"{alias} has no liquid container.");
        if (clear) water.stack.Clear();
        water.AddLiquid(liquid, amount);
        water.UpdateCondition();
    }

    internal void Craft(int recipe, string? peer)
    {
        if (recipe < 0 || recipe >= Recipes.recipes.Count) throw new FormatException("Unknown recipe index.");
        Recipe selected = Recipes.recipes[recipe];
        if (peer is null)
        {
            selected.TryMake();
            return;
        }
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.ItemReplication is null)
            throw new TestBlocked($"Guest {peer} has no production item replication.");
        Cluster.RunAs(actor, items => items.TryCraft(selected));
    }
    private static readonly Dictionary<string, Func<ItemTestAdapter, string, ulong, Item, int>> NetworkQueries = new(StringComparer.Ordinal)
    {
        ["items.acceptedCount"] = (r, _, id, _) => r.Cluster.Results.Count(result => result.Id == id && result.Decision == ItemDecision.Accepted),
        ["items.outOfReachCount"] = (r, _, id, _) => r.Cluster.Results.Count(result => result.Id == id && result.Decision == ItemDecision.OutOfReach),
        ["items.rejectedCount"] = (r, _, id, _) => r.Cluster.Results.Count(result => result.Id == id && result.Decision != ItemDecision.Accepted),
        ["items.distinctSubmittedPeers"] = (r, _, id, _) => r.deliveries.Where(record => record.Request.ItemId == id).Select(record => record.Sender).Distinct().Count(),
        ["items.requestRevisionCount"] = (r, _, id, _) => r.deliveries.Where(record => record.Request.ItemId == id).Select(record => record.Request.Revision).Distinct().Count(),
        ["items.hostOwns"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var state) &&
            state.Owner == r.Cluster.Actors.Values.First().Participant ? 1 : 0,
        ["items.hostSlotContainsAuthority"] = (r, alias, _, item) => r.slots.TryGetValue("host/" + alias, out byte slot) &&
            r.Cluster.Body.GetItem(slot) == item && r.Cluster.Body.HoldingItem(item) ? 1 : 0,
        ["items.guestWorldReplica"] = (r, _, id, item) => r.Cluster.HasWorldReplica(id, item) ? 1 : 0,
        ["items.typeAlcohol"] = (_, _, _, item) => item && item.id == "alcohol" && item.GetComponent<WaterContainerItem>() ? 1 : 0,
        ["items.replicated"] = (r, _, id, _) => r.Replicated(id, ownedOnly: false) ? 1 : 0,
        ["items.removed"] = (r, _, id, _) => !r.Cluster.Host.Session.TryGet(id, out _) ? 1 : 0,
        ["items.unowned"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var loose) && loose.Owner.IsNone ? 1 : 0,
        ["items.unownedConverged"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var loose) &&
            loose.Owner.IsNone && r.Cluster.Guests.All(actor => actor.Guest!.Replicas.TryGet(id, out var replica) &&
                SameState(loose, replica)) ? 1 : 0,
        ["items.motion"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var moving) &&
            (Mathf.Abs(moving.Location.VelocityX) > 0.1f || Mathf.Abs(moving.Location.VelocityY) > 0.1f) ? 1 : 0,
        ["items.converged"] = (r, _, id, _) => r.Replicated(id, ownedOnly: true) ? 1 : 0,
        ["items.hostCondition"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var state) ? Mathf.RoundToInt(state.Condition * 100f) : -1,
        ["items.hostPlacement"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var state) ? (int)state.Placement : -1,
        ["items.replicaPayloadMatches"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var authority) &&
            r.Cluster.Guests.All(actor => actor.Guest!.Replicas.TryGet(id, out var replica) && replica.Payload.SequenceEqual(authority.Payload)) ? 1 : 0,
        ["items.hostWet"] = (r, alias, _, item) => item && item.isWet ? 1 : 0,
        ["items.hostGunRounds"] = (r, alias, _, item) => item && item.TryGetComponent<GunScript>(out var gun) ? gun.roundsInMag : -1,
        ["items.hostLiquidMl"] = (r, alias, _, item) => item && item.TryGetComponent<WaterContainerItem>(out var water)
            ? Mathf.RoundToInt(water.CurrentTotal) : -1,
        ["items.guestSlotContainsReplica"] = (r, alias, id, item) => r.Cluster.ProjectedActor is { } actor &&
            r.slots.TryGetValue(actor.Name + "/" + alias, out byte slot) && r.Cluster.HoldsReplica(slot, id, item) ? 1 : 0
    };

    internal static IEnumerable<string> NetworkQueryNames => NetworkQueries.Keys;

    internal int ReadNetworkQuery(string name, string alias) =>
        NetworkQueries[name](this, alias, IdFor(alias), items[alias]);

    internal bool IsInWorld(string alias)
    {
        Item item = items[alias];
        return item && item.gameObject.activeSelf && !item.transform.parent;
    }

    internal object Snapshot() => items.Select(pair =>
    {
        Item item = pair.Value;
        ulong id = item && Cluster.Host.TryGetId(item, out ulong current) ? current : ids.TryGetValue(pair.Key, out ulong known) ? known : 0;
        Cluster.Host.Session.TryGet(id, out var host);
        return new
        {
            alias = pair.Key, id, host = Describe(host), flow = standalone ? FluidItemProbe.Describe(item) : null,
            world = new { active = item && item.gameObject.activeSelf, parent = item && item.transform.parent,
                x = item ? item.transform.position.x : 0, y = item ? item.transform.position.y : 0 },
            guests = Cluster.Guests.Select(actor =>
            {
                bool found = actor.Guest!.Replicas.TryGet(id, out var replica);
                return new { actor.Name, found, state = Describe(replica),
                    pending = actor.Guest.Pending.HasValue };
            }).ToArray()
        };
    }).ToArray();

    private bool Replicated(ulong id, bool ownedOnly)
    {
        bool hasAuthority = Cluster.Host.Session.TryGet(id, out var authority) && !authority.Removed;
        if (!Cluster.Guests.Any() || (ownedOnly && (!hasAuthority || authority.Owner.IsNone))) return false;
        return Cluster.Guests.All(actor => actor.Guest!.Synced && actor.Guest.Pending is null &&
            (hasAuthority ? actor.Guest.Replicas.TryGet(id, out var replica) && SameState(authority, replica)
                : !actor.Guest.Replicas.TryGet(id, out _)));
    }

    private static bool SameState(ItemState a, ItemState b) =>
        a.Id == b.Id && a.Definition == b.Definition && a.Owner == b.Owner && a.Parent == b.Parent &&
        a.Slot == b.Slot && a.Placement == b.Placement && a.Revision == b.Revision && a.Sample == b.Sample &&
        a.Removed == b.Removed && Mathf.Abs(a.Condition - b.Condition) < 0.01f && a.Payload.SequenceEqual(b.Payload) &&
        Mathf.Abs(a.Condition - b.Condition) < 0.001f &&
        Mathf.Abs(a.Location.X - b.Location.X) < 0.01f && Mathf.Abs(a.Location.Y - b.Location.Y) < 0.01f &&
        Mathf.Abs(Mathf.DeltaAngle(a.Location.Angle, b.Location.Angle)) < 0.01f &&
        Mathf.Abs(a.Location.VelocityX - b.Location.VelocityX) < 0.01f &&
        Mathf.Abs(a.Location.VelocityY - b.Location.VelocityY) < 0.01f &&
        Mathf.Abs(a.Location.Spin - b.Location.Spin) < 0.01f;

    private static bool SameContent(ItemState a, ItemState b) =>
        a.Id == b.Id && a.Definition == b.Definition && a.Owner == b.Owner && a.Parent == b.Parent &&
        a.Slot == b.Slot && a.Placement == b.Placement && a.Revision == b.Revision &&
        a.Removed == b.Removed && Mathf.Abs(a.Condition - b.Condition) < 0.001f && a.Payload.SequenceEqual(b.Payload);

    private static object Describe(ItemState state) => new
    {
        state.Id, state.Definition, owner = state.Owner.Seat, state.Revision, state.Sample, state.Slot, state.Parent,
        placement = (int)state.Placement, payload = state.Payload?.Length ?? 0, state.Condition, state.Removed, location = new { state.Location.X, state.Location.Y, state.Location.Angle, state.Location.VelocityX,
            state.Location.VelocityY, state.Location.Spin }
    };

    internal void DivergeReplica(JObject action, ScenarioDocument scenario)
    {
        if (!scenario.Name.StartsWith("control.", StringComparison.Ordinal))
            throw new FormatException("Replica fault injection is available only to control scenarios.");
        ulong id = IdFor(ScenarioDocument.String(action, "item"));
        var actor = Cluster.Actors[ScenarioDocument.String(action, "peer")];
        var host = Cluster.Actors.Values.First().Peers;
        if (!Cluster.Host.Session.TryGet(id, out var authority) || actor.Guest is null || authority.Owner.IsNone)
            throw new InvalidOperationException("Fault injection requires a settled owned item and a guest.");
        SharedItemState wrong = authority.ToWire(host);
        wrong.Condition = wrong.Condition > 0.5f ? 0.25f : 0.75f;
        if (!host.Send(actor.Peers.LocalPlayer, ItemWire.WriteState(MessageKind.ItemState, host.SessionId, wrong)))
            throw new InvalidOperationException("Could not inject a mismatched condition frame.");
    }

    internal void Remove(string alias)
    {
        _ = IdFor(alias);
        UnityEngine.Object.Destroy(ItemFor(alias).gameObject);
    }

    internal bool Near(string alias, Body body, float distance) =>
        ((Vector2)(ItemFor(alias).transform.position - body.transform.position)).sqrMagnitude <= distance * distance;

    internal int CountNativeDefinition(string definition) =>
        Item.allItems.Count(item => item && item.id == definition);

    internal int CountAuthoritativeDefinition(string definition) =>
        Cluster.Host.Session.Snapshot().Count(state => !state.Removed && state.Definition == definition);

    internal int CountReplicaDefinition(string peer, string definition)
    {
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.Guest is null)
            throw new FormatException($"Unknown guest {peer}.");
        return actor.Guest.Replicas.States.Count(state => !state.Removed && state.Definition == definition);
    }

    internal bool DefinitionConverged(string definition)
    {
        ItemState[] authority = Cluster.Host.Session.Snapshot()
            .Where(state => !state.Removed && state.Definition == definition).OrderBy(state => state.Id).ToArray();
        return Cluster.Guests.All(actor =>
        {
            ItemState[] replicas = actor.Guest!.Replicas.States
                .Where(state => !state.Removed && state.Definition == definition).OrderBy(state => state.Id).ToArray();
            if (replicas.Length != authority.Length) return false;
            for (int index = 0; index < replicas.Length; index++)
                if (!SameContent(authority[index], replicas[index])) return false;
            return true;
        });
    }

    internal int CountNativeDefinitionInSlots(string definition)
    {
        Body body = Cluster.Body;
        return Item.allItems.Count(item => item && item.id == definition &&
            Enumerable.Range(0, body.slots.Length).Any(slot => body.GetItem(slot) == item));
    }

    internal void ActorPickup(Body body, string alias)
    {
        if (!standalone) throw new TestBlocked("Native pickups of fixture items require standaloneItems; networked scenarios submit through GuestItemSession.");
        Item item = ItemFor(alias);
        if (item.transform.parent || body.HoldingItem(body.handSlot))
            throw new InvalidOperationException("The item must be free and the selected hand empty.");
        body.PickUpItem(item, body.handSlot, force: false);
    }

    internal void ActorThrow(Body body, string alias, float direction)
    {
        if (!standalone) throw new TestBlocked("Native throws of fixture items require standaloneItems.");
        if (body.GetItem(body.handSlot) != ItemFor(alias)) throw new InvalidOperationException("The selected hand does not hold the item.");
        body.targetLookPos = body.transform.position + new Vector3(direction * 10, 3, 0);
        body.ThrowItem(0.35f);
    }

    internal bool Holds(Body body, string alias) => body.HoldingItem(ItemFor(alias));

    internal (ulong Id, ItemState State) StateForLabel(string alias)
    {
        Item item = items[alias];
        ulong id = item && Cluster.Host.TryGetId(item, out ulong found) ? found : 0;
        Cluster.Host.Session.TryGet(id, out var state);
        return (id, state);
    }

    private Item ItemFor(string name) => items.TryGetValue(name, out Item item) && item
        ? item : throw new InvalidOperationException($"Item fixture {name} is unavailable.");

    private ulong IdFor(string name)
    {
        if (ids.TryGetValue(name, out ulong id)) return id;
        if (!Cluster.Host.TryGetId(ItemFor(name), out id)) throw new InvalidOperationException($"Fixture {name} is not registered.");
        ids.Add(name, id);
        return id;
    }

    private static string Op(JObject command) => ScenarioDocument.String(command, "op");
    private static Vector2 Offset(JObject command)
    {
        var position = ScenarioDocument.Position(command);
        return new Vector2(position.X, position.Y);
    }

    public void Dispose()
    {
        Cluster.Dispose();
        foreach (Item item in items.Values)
        {
            if (standalone) FluidItemProbe.Forget(item);
            if (item) UnityEngine.Object.Destroy(item.gameObject);
        }
    }
}
