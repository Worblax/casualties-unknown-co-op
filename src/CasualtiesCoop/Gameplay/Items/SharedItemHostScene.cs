using System.Collections.Generic;
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
    private const float UpdateInterval = 0.10f;
    private const float PickupRange = 10f;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly PlayerPositionLookup playerPosition;
    private readonly SharedItemInventory inventory;
    private readonly SharedItemObjects objects;
    private readonly Dictionary<ulong, (Item Item, int UnityId)> byId = new();
    private readonly Dictionary<int, ulong> byObject = new();
    private readonly List<ulong> removed = new();
    private float nextUpdate;

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

    internal void Tick()
    {
        float now = clock.Now;
        if (now < nextUpdate)
        {
            return;
        }
        nextUpdate = now + UpdateInterval;

        if (Session.HasCapacity)
        {
            foreach (Item item in Item.allItems)
            {
                if (!item || item.transform.parent || !item.rb || !item.GetComponent<Collider2D>() ||
                    byObject.ContainsKey(item.GetInstanceID()) || !TryRegister(item, out _))
                {
                    continue;
                }

                if (!Session.HasCapacity)
                {
                    break;
                }
            }
        }

        foreach (var entry in byId)
        {
            Item item = entry.Value.Item;
            if (!item)
            {
                byObject.Remove(entry.Value.UnityId);
                Session.Remove(entry.Key);
                removed.Add(entry.Key);
            }
            else if (Session.TryGet(entry.Key, out ItemState state) && state.Owner.IsNone)
            {
                Session.Sample(entry.Key, SharedItemObjects.Read(item), item.condition);
            }
        }
        foreach (ulong id in removed)
        {
            byId.Remove(id);
        }
        removed.Clear();
    }

    private bool TryRegister(Item item, out ulong id)
    {
        id = 0;
        if (!objects.Supports(item) || !Session.TryRegister(item.id, SharedItemObjects.Read(item), item.condition, out id))
        {
            return false;
        }
        int instance = item.GetInstanceID();
        byId.Add(id, (item, instance));
        byObject.Add(instance, id);
        return true;
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
        if (!TryRegister(item, out ulong id))
        {
            UnityEngine.Object.Destroy(bottle);
            return false;
        }

        if (slot != ItemWire.NoSlot && (actor - at).sqrMagnitude <= 2.5f * 2.5f)
        {
            var pickup = new SharedItemRequest { ItemId = id, Revision = 1, Action = ItemAction.PickUp, Slot = slot };
            Session.ExecuteForParticipant(peers.Identify(player), pickup);
        }
        return true;
    }

    internal bool HandleWorldUpdate(Item item)
    {
        if (!TryGetId(item, out ulong id) || !Session.TryGet(id, out ItemState state) || !state.Owner.IsNone || state.Removed)
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
        Bounds bounds = item.GetComponent<Collider2D>().bounds;
        Vector2 target = bounds.size != Vector3.zero ? bounds.center : item.transform.position;
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
            item.gameObject.SetActive(false);
        }

        condition = item.condition;
        return true;
    }

    public ItemDecision Drop(ParticipantId player, ulong id, SharedItemRequest request, out ItemLocation location, out float condition)
    {
        location = default;
        condition = 0;
        if (!byId.TryGetValue(id, out var tracked) || !tracked.Item ||
            !playerPosition(peers.AddressOf(player), out Vector2 position))
        {
            return ItemDecision.Unavailable;
        }
        ItemLocation requested = request.Location;
        Vector2 at = new(requested.X, requested.Y);
        if ((position - at).sqrMagnitude > 8f * 8f)
        {
            return ItemDecision.OutOfReach;
        }
        bool local = player == peers.LocalParticipant;
        if (local ? !inventory.HoldsSlot(tracked.Item, request.Slot) || !inventory.TryDrop(tracked.Item)
                  : tracked.Item.transform.parent)
        {
            return ItemDecision.Unavailable;
        }

        Vector2 velocity = Vector2.ClampMagnitude(new Vector2(requested.VelocityX, requested.VelocityY), 100f);
        float spin = request.Action == ItemAction.Throw ? UnityEngine.Random.Range(-800f, 800f) : 0f;
        location = new ItemLocation(at.x, at.y, requested.Angle, velocity.x, velocity.y, spin);
        objects.PutInWorld(tracked.Item, location, authoritative: true);
        condition = tracked.Item.condition;
        return ItemDecision.Accepted;
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
