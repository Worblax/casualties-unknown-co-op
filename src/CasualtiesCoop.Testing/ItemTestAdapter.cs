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

    internal void Throw(JObject action)
    {
        string peer = ScenarioDocument.String(action, "peer");
        ulong id = IdFor(ScenarioDocument.String(action, "item"));
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.Guest is null ||
            !actor.Guest.Replicas.TryGet(id, out ItemState replica) || replica.Owner != actor.Peers.LocalParticipant)
            throw new InvalidOperationException($"Guest {peer} does not own the item.");
        float vx = ScenarioDocument.Scalar(action, "velocityX", -100, 100);
        float vy = ScenarioDocument.Scalar(action, "velocityY", -100, 100);
        float spin = action["spin"] is null ? 0f : ScenarioDocument.Scalar(action, "spin", -2000, 2000);
        if (!actor.Guest.TrySubmit(id, ItemAction.Throw, replica.Slot,
            new ItemLocation(actor.Position.x, actor.Position.y, 0, vx, vy, spin)))
            throw new InvalidOperationException($"Guest {peer} could not submit the throw.");
    }

    internal void NativeDrop(JObject action)
    {
        string peer = ScenarioDocument.String(action, "peer");
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.NativeBody is null)
            throw new FormatException($"Native drop requires a NativeActor peer, got {peer}.");
        Item replica = ReplicaFor(peer, ScenarioDocument.String(action, "item"));
        if (!actor.NativeBody.HoldingItem(replica))
            throw new InvalidOperationException($"Guest {peer} does not hold the item replica.");
        Cluster.RunAs(actor, _ =>
        {
            actor.NativeBody!.DropItem(replica);
            return true;
        });
    }

    internal void NativeThrow(JObject action)
    {
        string peer = ScenarioDocument.String(action, "peer");
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.NativeBody is null)
            throw new FormatException($"Native throw requires a NativeActor peer, got {peer}.");
        Item replica = ReplicaFor(peer, ScenarioDocument.String(action, "item"));
        float direction = ScenarioDocument.Scalar(action, "direction", -1, 1);
        if (actor.NativeBody.GetItem(actor.NativeBody.handSlot) != replica)
            throw new InvalidOperationException($"Guest {peer} does not hold the item in the active hand.");
        Cluster.RunAs(actor, _ =>
        {
            actor.NativeBody!.targetLookPos = actor.NativeBody.transform.position + new Vector3(direction * 10, 3, 0);
            actor.NativeBody.ThrowItem(0.35f);
            return true;
        });
    }

    internal void NativePickupPeer(JObject action)
    {
        string peer = ScenarioDocument.String(action, "peer");
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.NativeBody is null)
            throw new FormatException($"Native pickup requires a NativeActor peer, got {peer}.");
        Item replica = ReplicaFor(peer, ScenarioDocument.String(action, "item"));
        int slot = Array.FindIndex(actor.NativeBody.slots,
            candidate => candidate.isHand && candidate.transform.childCount == 0);
        if (slot < 0) throw new TestBlocked("The guest Body has no empty hand slot.");
        slots[peer + "/" + ScenarioDocument.String(action, "item")] = (byte)slot;
        Cluster.RunAs(actor, _ =>
        {
            actor.NativeBody!.PickUpItem(replica, slot, force: false);
            return true;
        });
    }

    internal void NativeHostDrop(JObject action)
    {
        Item item = ItemFor(ScenarioDocument.String(action, "item"));
        if (!Cluster.Body.HoldingItem(item))
            throw new InvalidOperationException("The host does not hold the item.");
        Cluster.Body.DropItem(item);
    }

    internal void BindAlias(string alias, string definition, string? peer)
    {
        if (items.ContainsKey(alias)) throw new FormatException($"Duplicate item alias {alias}.");
        ItemState match;
        if (peer is not null && peer != "host")
        {
            if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.Guest is null)
                throw new FormatException($"Unknown guest {peer}.");
            match = actor.Guest.Replicas.States.FirstOrDefault(state => !state.Removed && state.Definition == definition);
            if (match.Id == 0) throw new TestBlocked($"Guest {peer} has no replica of {definition}.");
        }
        else
        {
            match = Cluster.Host.Session.Snapshot()
                .FirstOrDefault(state => !state.Removed && state.Definition == definition);
            if (match.Id == 0) throw new TestBlocked($"The host has no authoritative {definition}.");
        }
        if (Cluster.Host.FindTracked(match.Id) is not { } item)
            throw new TestBlocked($"The host has no native object for {definition}.");
        items[alias] = item;
        ids[alias] = match.Id;
    }

    internal void NativeHostThrow(JObject action)
    {
        Item item = ItemFor(ScenarioDocument.String(action, "item"));
        float direction = ScenarioDocument.Scalar(action, "direction", -1, 1);
        if (Cluster.Body.GetItem(Cluster.Body.handSlot) != item)
            throw new InvalidOperationException("The host does not hold the item in the active hand.");
        Cluster.Body.targetLookPos = Cluster.Body.transform.position + new Vector3(direction * 10, 3, 0);
        Cluster.Body.ThrowItem(0.35f);
    }

    internal void NativeWear(JObject action)
    {
        string peer = ScenarioDocument.String(action, "peer");
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.NativeBody is null)
            throw new FormatException($"Native wear requires a NativeActor peer, got {peer}.");
        Item replica = ReplicaFor(peer, ScenarioDocument.String(action, "item"));
        if (!replica.Stats.wearable)
            throw new TestBlocked($"{replica.id} is not wearable.");
        Cluster.RunAs(actor, _ =>
        {
            actor.NativeBody!.WearWearable(replica);
            return true;
        });
        if (actor.NativeBody.GetWearableBySlotID(replica.Stats.wearSlotId) != replica)
            throw new InvalidOperationException($"Guest {peer} could not wear {replica.id}.");
    }

    internal void ContainerLoad(JObject action)
    {
        Item containerItem = ItemFor(ScenarioDocument.String(action, "container"));
        Item child = ItemFor(ScenarioDocument.String(action, "item"));
        if (!containerItem.container) throw new TestBlocked($"{containerItem.id} is not a container.");
        containerItem.container.LoadItem(child);
        if (!child.TryGetParentContainer(out Container loaded) || loaded != containerItem.container)
            throw new InvalidOperationException($"Could not load {child.id} into {containerItem.id}.");
    }

    private Item ReplicaFor(string peer, string alias)
    {
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) ||
            actor.ItemReplication?.FindReplica(IdFor(alias)) is not { } replica || !replica)
            throw new InvalidOperationException($"Guest {peer} has no replica for {alias}.");
        return replica;
    }

    internal void Swap(JObject action)
    {
        string peer = ScenarioDocument.String(action, "peer");
        string alias = ScenarioDocument.String(action, "item");
        if (!Cluster.Actors.TryGetValue(peer, out VirtualSessionCluster.Actor? actor) || actor.Guest is null ||
            !actor.Guest.Replicas.TryGet(IdFor(alias), out ItemState replica) ||
            replica.Owner != actor.Peers.LocalParticipant)
            throw new InvalidOperationException($"Guest {peer} does not own the item.");
        Body body = actor.NativeBody ?? throw new TestBlocked("Swap requires a native Body.");
        int from = replica.Slot;
        int to = action["slot"]?.Type == JTokenType.Integer
            ? ScenarioDocument.Integer(action, "slot", 0, 254) : -1;
        if (to < 0)
        {
            for (int index = 0; index < body.slots.Length; index++)
            {
                if (index != from && body.GetItem(index) == null) { to = index; break; }
            }
        }
        if (from == ItemWire.NoSlot || to < 0 || to >= body.slots.Length || to == from)
            throw new TestBlocked("The native Body has no second empty pickup slot.");
        Cluster.RunAs(actor, _ => { body.SwapSlots(from, to); return true; });
        slots[peer + "/" + alias] = (byte)to;
    }

    internal void Place(string alias, JObject command)
    {
        var position = ScenarioDocument.Position(command);
        ItemFor(alias).transform.position = new Vector3(position.X, position.Y, 0f);
    }

    internal void SetCondition(string alias, float condition)
    {
        Item item = ItemFor(alias);
        item.condition = Mathf.Clamp01(condition);
        Cluster.Host.Observe(item);
    }

    internal void SetWet(string alias, bool wet)
    {
        Item item = ItemFor(alias);
        item.wetTime = wet ? Time.time : -1000f;
        Cluster.Host.Observe(item);
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
        Cluster.Host.Observe(item);
    }

    internal void SetLiquid(string alias, string liquid, float amount, bool clear)
    {
        Item item = ItemFor(alias);
        WaterContainerItem? water = item.GetComponent<WaterContainerItem>();
        if (!water) throw new TestBlocked($"{alias} has no liquid container.");
        if (clear) water.stack.Clear();
        water.AddLiquid(liquid, amount);
        water.UpdateCondition();
        Cluster.Host.Observe(item);
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
        ["items.worldReplicaHover"] = (r, _, id, _) => r.Cluster.WorldReplicaHover(id, out _) ? 1 : 0,
        ["items.guestHoverDescription"] = (r, _, id, _) => r.Cluster.HoverDescription(id, out _) ? 1 : 0,
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
        ["items.hostNativeCondition"] = (_, _, _, item) => item ? Mathf.RoundToInt(item.condition * 100f) : -1,
        ["items.nativeItemTotal"] = (_, _, _, _) => Item.allItems.Count(value => value),
        ["items.hostPlacement"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var state) ? (int)state.Placement : -1,
        ["items.hostSlot"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var placed) ? placed.Slot : -1,
        ["items.replicaPayloadMatches"] = (r, _, id, _) => r.Cluster.Host.Session.TryGet(id, out var authority) &&
            r.Cluster.Guests.All(actor => actor.Guest!.Replicas.TryGet(id, out var replica) && replica.Payload.SequenceEqual(authority.Payload)) ? 1 : 0,
        ["items.hostWet"] = (r, alias, _, item) => item && item.isWet ? 1 : 0,
        ["items.hostGunRounds"] = (r, alias, _, item) => item && item.TryGetComponent<GunScript>(out var gun) ? gun.roundsInMag : -1,
        ["items.hostLiquidMl"] = (r, alias, _, item) => item && item.TryGetComponent<WaterContainerItem>(out var water)
            ? Mathf.RoundToInt(water.CurrentTotal) : -1,
        ["items.hostLiquidPositive"] = (_, _, _, item) => item && item.TryGetComponent<WaterContainerItem>(out var water)
            ? (water.CurrentTotal > 0f ? 1 : 0) : 0,
        ["items.hostLiquidBiochem"] = (_, _, _, item) => item && item.TryGetComponent<WaterContainerItem>(out var water)
            ? (water.stack.Count == 1 && water.stack[0].liquidId == "biochem" ? 1 : 0) : 0,
        ["items.hostLiquidLumalgae"] = (_, _, _, item) => item && item.TryGetComponent<WaterContainerItem>(out var water)
            ? (water.HasLiquid("lumalgae") ? 1 : 0) : 0,
        ["items.isContainer"] = (_, _, _, item) => item && item.container ? 1 : 0,
        ["items.qualityHeatsource"] = (_, _, _, item) => item && item.Stats.qualities.Any(value => value.id == "heatsource") ? 1 : 0,
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

    internal string RemoveTrackedLiquidNativeImmediately(string alias)
    {
        ulong id = IdFor(alias);
        Item item = ItemFor(alias);
        if (!Cluster.Host.Session.TryGet(id, out ItemState state) || Cluster.Host.FindTracked(id) != item ||
            !item.GetComponentInChildren<WaterContainerItem>(true))
        {
            throw new InvalidOperationException($"Fixture {alias} requires a tracked native liquid container.");
        }

        // The host can retire this registry entry on its next tick. Observe the orphan before then.
        UnityEngine.Object.DestroyImmediate(item.gameObject);
        return state.Definition;
    }

    internal bool Near(string alias, Body body, float distance) =>
        ((Vector2)(ItemFor(alias).transform.position - body.transform.position)).sqrMagnitude <= distance * distance;

    internal int CountNativeDefinition(string definition) =>
        Item.allItems.Count(item => item && item.id == definition);
    internal int CountHostNativeDefinition(string definition) =>
        Cluster.Host.Session.Snapshot().Count(state => !state.Removed && state.Definition == definition &&
            Cluster.Host.FindTracked(state.Id) is { } item && item && !SharedItemProjection.IsReplica(item));
    internal int AuthoritativeLiquidState(string definition, bool biochem)
    {
        foreach (ItemState state in Cluster.Host.Session.Snapshot())
        {
            if (state.Removed || state.Definition != definition) continue;
            Item? item = Cluster.Host.FindTracked(state.Id);
            if (!item)
            {
                throw new InvalidOperationException(
                    $"Authoritative item {state.Id} ({definition}) has no tracked native object.");
            }
            WaterContainerItem? water = item.GetComponentInChildren<WaterContainerItem>(true);
            if (!water) continue;
            if (water.CurrentTotal <= 0f) return 0;
            if (!biochem) return 1;
            return water.stack.Count == 1 && water.stack[0].liquidId == "biochem" ? 1 : 0;
        }
        return -1;
    }
    internal int AuthoritativeLiquidOrphan(string definition)
    {
        foreach (ItemState state in Cluster.Host.Session.Snapshot())
        {
            if (state.Removed || state.Definition != definition) continue;
            Item? item = Cluster.Host.FindTracked(state.Id);
            if (!item || !item.GetComponentInChildren<WaterContainerItem>(true)) return 1;
        }
        return 0;
    }
    internal int RecipeIngredientsFor(string actor, int recipe)
    {
        if (recipe < 0 || recipe >= Recipes.recipes.Count ||
            !Cluster.Actors.TryGetValue(actor, out VirtualSessionCluster.Actor? member) || member.ItemReplication is null)
            return -1;
        int count = -1;
        Cluster.RunAs(member, _ =>
        {
            count = Recipes.recipes[recipe].GetItemsForRecipe()?.Count ?? 0;
            return true;
        });
        return count;
    }
    internal int CraftCandidateCount(string actor)
    {
        if (!Cluster.Actors.TryGetValue(actor, out VirtualSessionCluster.Actor? member) || member.ItemReplication is null)
            return -1;
        int count = -1;
        Cluster.RunAs(member, items => { count = items.CraftCandidates().Count(); return true; });
        return count;
    }
    internal int LiquidPayloadRoundTrip(string alias)
    {
        Item source = ItemFor(alias);
        byte[] payload = ItemStateCodec.Capture(source);
        var objects = new SharedItemObjects();
        Item? clone = objects.CreateReplica(source.id, default);
        if (!clone) return -1;
        int result = 0;
        try
        {
            if (objects.Apply(clone, payload, source.condition, ItemPlacement.World) &&
                clone.TryGetComponent<WaterContainerItem>(out WaterContainerItem water) && water.HasLiquid("lumalgae"))
                result = 1;
        }
        finally
        {
            UnityEngine.Object.Destroy(clone.gameObject);
        }
        return result;
    }
    internal int GuestDefinitionLiquid(string actor, string definition, bool biochem) =>
        Cluster.GuestDefinitionLiquid(actor, definition, biochem);
    internal int RegistrationBinds(string actor) => Cluster.Actors.TryGetValue(actor, out var member)
        ? member.ItemReplication?.RegistrationBinds ?? -1 : -1;
    internal int LastCraftCreatedCount(string actor) => Cluster.Actors.TryGetValue(actor, out var member)
        ? member.ItemReplication?.LastCraftCreatedCount ?? -1 : -1;
    internal int LastCraftCompletion(string actor) => Cluster.Actors.TryGetValue(actor, out var member)
        ? member.ItemReplication?.LastCraftCompletion ?? -1 : -1;
    internal int LastScopeUsed(string actor) => Cluster.Actors.TryGetValue(actor, out var member)
        ? member.ItemReplication?.LastScopeUsed ?? -1 : -1;
    internal int LastScopeCreated(string actor) => Cluster.Actors.TryGetValue(actor, out var member)
        ? member.ItemReplication?.LastScopeCreated ?? -1 : -1;
    internal int LastLiquidTargets(string actor) => Cluster.Actors.TryGetValue(actor, out var member)
        ? member.ItemReplication?.LastLiquidTargets ?? -1 : -1;
    internal int PayloadLiquidState(string definition, bool biochem)
    {
        foreach (ItemState state in Cluster.Host.Session.Snapshot())
        {
            if (state.Removed || state.Definition != definition) continue;
            var objects = new SharedItemObjects();
            Item? item = objects.CreateReplica(state.Definition, state.Location);
            if (!item) return -1;
            try
            {
                if (!objects.Apply(item, state.Payload, state.Condition, state.Placement)) return -2;
                WaterContainerItem? water = item.GetComponentInChildren<WaterContainerItem>(true);
                if (!water) return -3;
                if (water.CurrentTotal <= 0f) return 0;
                if (!biochem) return 1;
                return water.stack.Count == 1 && water.stack[0].liquidId == "biochem" ? 1 : 0;
            }
            finally
            {
                UnityEngine.Object.Destroy(item.gameObject);
            }
        }
        return -4;
    }
    internal int AnyLiquidBiochem()
    {
        foreach (ItemState state in Cluster.Host.Session.Snapshot())
        {
            if (state.Removed) continue;
            Item? item = Cluster.Host.FindTracked(state.Id);
            if (!item) continue;
            WaterContainerItem? water = item.GetComponentInChildren<WaterContainerItem>(true);
            if (water && water.stack.Any(value => value.liquidId == "biochem" && value.amount > 0f)) return 1;
        }
        return 0;
    }
    internal int ResourceExists(string definition) => Resources.Load<GameObject>(definition) ? 1 : 0;
    internal object RecipeMatchDetails(string actor, int recipe)
    {
        if (recipe < 0 || recipe >= Recipes.recipes.Count ||
            !Cluster.Actors.TryGetValue(actor, out VirtualSessionCluster.Actor? member) || member.ItemReplication is null)
            return Array.Empty<object>();
        object result = Array.Empty<object>();
        Cluster.RunAs(member, items =>
        {
            List<Item> candidates = items.CraftCandidates().ToList();
            Recipe source = Recipes.recipes[recipe];
            result = source.items.Select((ingredient, index) => new
            {
                index,
                quality = ingredient.quality?.id,
                liquid = ingredient.isLiquid,
                specific = ingredient.specificId,
                minimum = ingredient.minimumCondition,
                candidates = candidates.Select(candidate => new
                {
                    id = candidate.id,
                    matches = ingredient.DoesUseItemType(candidate),
                    liquid = candidate.TryGetComponent<WaterContainerItem>(out WaterContainerItem water)
                        ? water.stack.Select(value => new { value.liquidId, value.amount }).ToArray() : null,
                    qualities = candidate.Stats.qualities.Select(value => value.id).ToArray()
                }).ToArray()
            }).ToArray();
            return true;
        });
        return result;
    }

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

    internal bool HostSlotMatchesClient(string actor, string target)
    {
        if (!slots.TryGetValue(actor + "/" + target, out byte slot)) return false;
        ulong id = IdFor(target);
        if (!Cluster.Host.Session.TryGet(id, out ItemState state) || state.Slot != slot) return false;
        if (!Cluster.Actors.TryGetValue(actor, out var member) || !member.NativeBody) return true;
        Item? replica = member.ItemReplication?.FindReplica(id);
        return replica && member.NativeBody!.GetItem(slot) == replica;
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

    internal int RemoteHeldCount(string actor) =>
        Cluster.RemoteVisuals is { } visuals && Cluster.Actors.TryGetValue(actor, out var member)
            ? visuals.ChildItemCount(member.Participant) : -1;

    internal int RemoteEnabledColliders(string actor) =>
        Cluster.RemoteVisuals is { } visuals && Cluster.Actors.TryGetValue(actor, out var member)
            ? visuals.EnabledChildColliderCount(member.Participant) : -1;

    internal int RemoteFiniteTransform(string actor) =>
        Cluster.RemoteVisuals is { } visuals && Cluster.Actors.TryGetValue(actor, out var member) &&
        visuals.HasFiniteChildItemTransform(member.Participant) ? 1 : 0;

    internal int RemoteAttachAttempts() => Cluster.RemoteVisuals?.AttachAttempts ?? -1;
    internal int GuestReportsSent(string actor) => Cluster.GuestReportsSent(actor);
    internal string HoverDetails(string alias)
    {
        Cluster.HoverDescription(IdFor(alias), out string detail);
        return detail;
    }
    internal object NativeDetails() => Item.allItems.Where(item => item).Select(item => new
    {
        instance = item.GetInstanceID(), definition = item.id, replica = SharedItemProjection.IsReplica(item),
        active = item.gameObject.activeSelf, parent = item.transform.parent ? item.transform.parent.name : null,
        hostId = Cluster.Host.TryGetId(item, out ulong hostId) ? hostId : 0
    }).OrderBy(value => value.instance).ToArray();
    internal int HostReportsReceived() => Cluster.Host.Session.ReportsReceived;
    internal int GuestNativePlacement(string actor, string item) => Cluster.GuestNativePlacement(actor, IdFor(item));
    internal int GuestCraftPending(string actor) => Cluster.GuestCraftPending(actor);
    internal int GuestReplicaLiquidPositive(string actor, string item) =>
        Cluster.GuestReplicaLiquidPositive(actor, IdFor(item));
    internal int GuestReplicaLiquidMl(string actor, string item) => Cluster.GuestReplicaLiquidMl(actor, IdFor(item));
    internal int GuestReplicaLiquidLumalgae(string actor, string item) =>
        Cluster.GuestReplicaLiquidLumalgae(actor, IdFor(item));
    internal ulong IdForAlias(string alias) => IdFor(alias);
    internal int LastCraftDecision() => Cluster.CraftDetails.Count == 0 ? -1 :
        (int)Cluster.CraftDetails[^1].Decision;
    internal int LastCraftOperation() => Cluster.CraftDetails.Count == 0 ? -1 :
        (int)Cluster.CraftDetails[^1].Operation;

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
