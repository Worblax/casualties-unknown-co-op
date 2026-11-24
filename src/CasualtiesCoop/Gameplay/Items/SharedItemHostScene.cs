using System.Collections.Generic;
using System.Linq;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Session;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemHostScene : IItemWorld
{
    private const float MotionInterval = 0.10f;
    private const float StateInterval = 0.5f;
    private const int StateBudget = 16;
    private const float DiscoveryInterval = 1f;
    private const int DiscoveryBudget = 64;
    private const float PickupRange = 10f;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly PlayerPositionLookup playerPosition;
    private readonly SharedItemInventory inventory;
    private readonly SharedItemObjects objects;
    private readonly Dictionary<ulong, (Item Item, int UnityId)> byId = new();
    private readonly Dictionary<int, ulong> byObject = new();
    private readonly Dictionary<ulong, byte[]> payloads = new();
    private readonly List<ulong> removed = new();
    private readonly List<ulong> order = new();
    private int stateCursor;
    private float nextMotion;
    private float nextState;
    private float nextDiscovery;

    internal SharedItemHostScene(SessionPeers peers, PlayerPositionLookup playerPosition, IMonotonicClock clock,
        SharedItemInventory inventory, SharedItemObjects objects)
    {
        this.peers = peers;
        this.playerPosition = playerPosition;
        this.clock = clock;
        this.inventory = inventory;
        this.objects = objects;
        Session = new HostItemSession(peers, this, clock);
        Session.OperationResponded += TraceOperation;
    }

    internal HostItemSession Session { get; }
    internal bool TryGetId(Item item, out ulong id) => byObject.TryGetValue(item.GetInstanceID(), out id);

    internal void Observe(Item? item)
    {
        if (!item || !item)
        {
            return;
        }
        if (TryGetId(item!, out ulong id))
        {
            Synchronize(id, item!, force: true);
            return;
        }
        TryRegister(item!);
    }

    internal void MarkDirty(Item? item)
    {
        if (item && TryGetId(item!, out ulong id) && Session.TryGet(id, out ItemState state) &&
            (state.Owner.IsNone || state.Owner == peers.LocalParticipant))
        {
            Synchronize(id, item!, force: true);
        }
    }

    internal void Tick()
    {
        float now = clock.Now;
        if (now >= nextDiscovery)
        {
            nextDiscovery = now + DiscoveryInterval;
            Discover();
        }
        if (now >= nextMotion)
        {
            nextMotion = now + MotionInterval;
            SampleWorld();
        }
        if (now >= nextState)
        {
            nextState = now + StateInterval;
            SynchronizeLocalState();
        }
        foreach (ulong id in removed)
        {
            byId.Remove(id);
            payloads.Remove(id);
        }
        removed.Clear();
    }

    public bool HasRecipe(int recipe) => recipe >= 0 && recipe < Recipes.recipes.Count;    public void RemoveItem(ulong id)
    {
        if (!byId.TryGetValue(id, out var tracked)) return;
        byId.Remove(id);
        byObject.Remove(tracked.UnityId);
        payloads.Remove(id);
        if (tracked.Item)
        {
            tracked.Item.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(tracked.Item.gameObject);
        }
    }
    public ItemDecision CheckCraftIngredient(ParticipantId player, ulong id)
    {
        ulong root = Session.RootId(id);
        if (!Session.TryGet(root, out ItemState state) || state.Removed || !state.Owner.IsNone ||
            state.Placement != ItemPlacement.World || !byId.TryGetValue(root, out var tracked) || !tracked.Item ||
            !playerPosition(peers.AddressOf(player), out Vector2 position))
        {
            return ItemDecision.Unavailable;
        }
        Vector2 target = tracked.Item.transform.position;
        return CanReach(position, target, 10f, LayerMask.GetMask("Ground"))
            ? ItemDecision.Accepted : ItemDecision.OutOfReach;
    }
    public bool TryApplyState(ulong id, byte[] payload)
    {
        if (!Session.TryGet(id, out ItemState state))
        {
            return false;
        }
        Item? item = EnsureItem(id, state);
        return item && objects.Apply(item!, payload, state.Condition, state.Placement);
    }

    private void Discover()
    {
        int examined = 0;
        foreach (Item item in Item.allItems)
        {
            if (examined++ >= DiscoveryBudget || !Session.HasCapacity)
            {
                break;
            }
            if (!item || TryGetId(item, out _))
            {
                continue;
            }
            TryRegister(item);
        }
    }

    private bool TryRegister(Item item)
    {
        if (!objects.Supports(item))
        {
            return false;
        }

        ulong parent = 0;
        if (item.TryGetParentContainer(out Container container) && container.TryGetComponent<Item>(out Item parentItem))
        {
            if (!TryGetId(parentItem, out parent) && !TryRegister(parentItem))
            {
                return false;
            }
        }

        (ItemPlacement placement, ParticipantId owner, byte slot) = Placement(item);
        byte[] payload = ItemStateCodec.Capture(item);
        if (!Session.TryRegister(item.id, placement, owner, parent, slot, SharedItemObjects.Read(item), item.condition,
            payload, 0, out ItemState state))
        {
            return false;
        }

        int instance = item.GetInstanceID();
        byId[state.Id] = (item, instance);
        byObject[instance] = state.Id;
        payloads[state.Id] = payload;
        return true;
    }

    private void Synchronize(ulong id, Item item, bool force)
    {
        if (!Session.TryGet(id, out ItemState state) || state.Removed || !state.Owner.IsNone &&
            state.Owner != peers.LocalParticipant)
        {
            return;
        }

        byte[] payload = ItemStateCodec.Capture(item);
        (ItemPlacement placement, ParticipantId owner, byte slot) = Placement(item);
        ulong parent = ParentId(item);
        if (placement != state.Placement || owner != state.Owner || slot != state.Slot || parent != state.Parent)
        {
            if (Session.TryCommitPlacement(id, placement, owner, parent, slot, SharedItemObjects.Read(item),
                item.condition, payload, out state))
            {
                payloads[id] = payload;
            }
            return;
        }

        if ((force || !payloads.TryGetValue(id, out byte[]? previous) || !previous.SequenceEqual(payload)) &&
            Session.TryCommitPayload(id, state.Revision, item.condition, payload, out state))
        {
            payloads[id] = payload;
        }
    }

    private void SampleWorld()
    {
        foreach (var entry in byId)
        {
            Item item = entry.Value.Item;
            if (!item)
            {
                byObject.Remove(entry.Value.UnityId);
                Session.Remove(entry.Key);
                removed.Add(entry.Key);
                continue;
            }
            if (Session.TryGet(entry.Key, out ItemState state) && state.Placement == ItemPlacement.World &&
                state.Owner.IsNone)
            {
                Session.Sample(entry.Key, SharedItemObjects.Read(item), item.condition);
            }
        }
    }

    private void SynchronizeLocalState()
    {
        int inspected = 0;
        while (inspected < StateBudget && order.Count > 0)
        {
            if (stateCursor >= order.Count) stateCursor = 0;
            ulong id = order[stateCursor++];
            inspected++;
            if (!byId.TryGetValue(id, out var entry) || !entry.Item ||
                !Session.TryGet(id, out ItemState state) || state.Removed ||
                !state.Owner.IsNone && state.Owner != peers.LocalParticipant)
            {
                continue;
            }
            Synchronize(id, entry.Item, force: false);
        }
    }

    private (ItemPlacement Placement, ParticipantId Owner, byte Slot) Placement(Item item)
    {
        if (item.TryGetParentContainer(out _))
        {
            return (ItemPlacement.Container, default, ItemWire.NoSlot);
        }
        Body? body = inventory.Body;
        if (body)
        {
            for (int slot = 0; slot < body!.slots.Length; slot++)
            {
                if (body.GetItem(slot) == item)
                {
                    return (ItemPlacement.Inventory, peers.LocalParticipant, (byte)slot);
                }
            }
            if (body.GetAllWearables().Contains(item))
            {
                return (ItemPlacement.Worn, peers.LocalParticipant, ItemWire.NoSlot);
            }
        }
        return (ItemPlacement.World, default, ItemWire.NoSlot);
    }

    private ulong ParentId(Item item)
    {
        if (!item.TryGetParentContainer(out Container container) || !container.TryGetComponent<Item>(out Item parent))
        {
            return 0;
        }
        return TryGetId(parent, out ulong id) ? id : 0;
    }

    private Item? EnsureItem(ulong id, ItemState state)
    {
        if (byId.TryGetValue(id, out var tracked) && tracked.Item)
        {
            return tracked.Item;
        }
        Item? item = objects.CreateReplica(state.Definition, state.Location);
        if (!item)
        {
            return null;
        }
        item!.gameObject.SetActive(false);
        int instance = item.GetInstanceID();
        byId[id] = (item, instance);
        byObject[instance] = id;
        payloads[id] = state.Payload;
        return item;
    }

    internal bool SpawnAlcohol(ulong player, Vector2 at, byte slot)
    {
        if (!Session.HasCapacity || !Resources.Load<GameObject>("alcohol") ||
            !playerPosition(player, out Vector2 actor) || (actor - at).sqrMagnitude > 16 * 16)
        {
            return false;
        }

        GameObject bottle = Utils.Create("alcohol", at, 0);
        if (!bottle || !bottle.TryGetComponent<Item>(out Item item))
        {
            if (bottle) UnityEngine.Object.Destroy(bottle);
            return false;
        }
        item.SetCondition(1f);
        if (!TryRegister(item))
        {
            UnityEngine.Object.Destroy(bottle);
            return false;
        }

        if (slot != ItemWire.NoSlot && TryGetId(item, out ulong id) && (actor - at).sqrMagnitude <= 2.5f * 2.5f)
        {
            var pickup = new SharedItemRequest { ItemId = id, Revision = 1, Action = ItemAction.PickUp, Slot = slot };
            Session.ExecuteForParticipant(peers.Identify(player), pickup);
        }
        return true;
    }

    internal bool HandleWorldUpdate(Item item)
    {
        if (!TryGetId(item, out ulong id) || !Session.TryGet(id, out ItemState state) || !state.Owner.IsNone ||
            state.Removed || state.Placement != ItemPlacement.World)
        {
            return true;
        }

        WorldGeneration world = WorldGeneration.world;
        Vector2 position = item.transform.position;
        bool worldReady = world && world.worldExists;
        if (worldReady && world.GetClosestChunkRenderer(world.WorldToBlockPos(position)).enabled)
        {
            return true;
        }

        bool active = worldReady && Time.timeScale <= 5f && IsNearAPlayer(position);
        item.rb.simulated = active;
        if (item.affect)
        {
            item.affect.enabled = active;
        }
        if (active)
        {
            if (item.condition <= 0f && item.Stats.destroyAtZeroCondition)
            {
                UnityEngine.Object.Destroy(item.gameObject);
            }
            else
            {
                item.HandleDecay();
            }
        }
        return false;
    }

    public ItemDecision CheckPickup(ParticipantId player, ulong id, byte slot, ItemLocation? previousPosition)
    {
        ulong address = peers.AddressOf(player);
        if (!byId.TryGetValue(id, out var tracked) || !tracked.Item || tracked.Item.transform.parent ||
            !playerPosition(address, out Vector2 position))
        {
            return ItemDecision.Unavailable;
        }

        Item item = tracked.Item;
        Vector2 target = item.transform.position;
        if (item.TryGetComponent<Collider2D>(out Collider2D collider))
        {
            Bounds bounds = collider.bounds;
            if (bounds.size != Vector3.zero) target = bounds.center;
        }
        int ground = LayerMask.GetMask("Ground");
        bool local = player == peers.LocalParticipant;
        float distance = PickupRange + (local ? 0f : 1.5f);
        if (!CanReach(position, target, distance, ground) &&
            (local || previousPosition is not { } previous || !CanReach(position, new Vector2(previous.X, previous.Y), distance, ground)))
        {
            return ItemDecision.OutOfReach;
        }

        return peers.TryGetPlayer(address, out LaunchPlayer member) &&
            RemoteCharacterView.LoadCharacter(member.Character) is { } template && slot < template.slots.Length &&
            (!item.Stats.onlyHoldInHands || template.slots[slot].isHand) ? ItemDecision.Accepted : ItemDecision.SlotOccupied;
    }

    public bool TryPickUp(ParticipantId player, ulong id, byte slot, out float condition)
    {
        Item item = byId[id].Item;
        condition = item.condition;
        if (player == peers.LocalParticipant)
        {
            if (!inventory.TryPickUp(item, slot))
            {
                return false;
            }
        }
        else
        {
            item.rb.simulated = false;
            item!.gameObject.SetActive(false);
        }

        condition = item.condition;
        return true;
    }

    public ItemDecision Drop(ParticipantId player, ulong id, SharedItemRequest request, out ItemLocation location,
        out float condition)
    {
        location = default;
        condition = 0;
        if (!Session.TryGet(id, out ItemState state) || !playerPosition(peers.AddressOf(player), out Vector2 position))
        {
            return ItemDecision.Unavailable;
        }
        ItemLocation requested = request.Location;
        Vector2 at = new(requested.X, requested.Y);
        if ((position - at).sqrMagnitude > 8f * 8f)
        {
            return ItemDecision.OutOfReach;
        }

        Item? item = EnsureItem(id, state);
        if (!item)
        {
            return ItemDecision.Unavailable;
        }
        bool local = player == peers.LocalParticipant;
        if (local ? !inventory.HoldsSlot(item!, request.Slot) || !inventory.TryDrop(item!)
                  : item!.transform.parent)
        {
            return ItemDecision.Unavailable;
        }

        byte[] payload = request.Payload.Length > 0 ? request.Payload : state.Payload;
        if (!objects.Apply(item!, payload, state.Condition, ItemPlacement.World))
        {
            return ItemDecision.Unavailable;
        }
        Vector2 velocity = Vector2.ClampMagnitude(new Vector2(requested.VelocityX, requested.VelocityY), 100f);
        float spin = request.Action == ItemAction.Throw ? UnityEngine.Random.Range(-800f, 800f) : 0f;
        location = new ItemLocation(at.x, at.y, requested.Angle, velocity.x, velocity.y, spin);
        objects.PutInWorld(item!, location, authoritative: true);
        AttachRegisteredContents(item!, id, payload);
        condition = item!.condition;
        return ItemDecision.Accepted;
    }

    private void AttachRegisteredContents(Item parent, ulong parentId, byte[] parentPayload)
    {
        foreach (ItemState child in Session.Snapshot().Where(state => state.Parent == parentId && !state.Removed))
        {
            Item? item = EnsureItem(child.Id, child);
            if (item && objects.Apply(item!, child.Payload, child.Condition, ItemPlacement.Container) &&
                inventory.TryAttach(parent, item!))
            {
                AttachRegisteredContents(item!, child.Id, child.Payload);
            }
        }
    }

    private static void TraceOperation(SharedItemRequest request, ItemDecision decision, bool replay)
    {
        if (!CoopDiagnostics.Enabled)
        {
            return;
        }

        if (replay)
        {
            CoopDiagnostics.Count("Items", "Request replay");
        }
        else
        {
            CoopDiagnostics.Trace("Items", $"{request.Action} #{request.Operation}, item {request.ItemId}, revision {request.Revision}: {decision}.");
        }
    }

    private bool IsNearAPlayer(Vector2 at)
    {
        foreach (LaunchPlayer player in peers.Players)
        {
            if (playerPosition(player.SteamId, out Vector2 position) && (position - at).sqrMagnitude <= 60f * 60f)
            {
                return true;
            }
        }
        return false;
    }

    private static bool CanReach(Vector2 actor, Vector2 item, float distance, int ground) =>
        (actor - item).sqrMagnitude <= distance * distance &&
        (!Physics2D.Linecast(actor, item, ground) || Physics2D.OverlapPoint(item, ground));
}