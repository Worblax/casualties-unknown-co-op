using System;
using System.Collections.Generic;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Diagnostics;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemProjection : IDisposable
{
    private const float ReplicaRetryInterval = 1f;

    private sealed class View
    {
        internal Item? Item;
        internal int UnityId;
        internal uint AppliedRevision;
        internal float NextReplicaAttempt;
        internal bool SnapNextMotion;
    }

    private readonly SharedItemInventory inventory;
    private readonly SharedItemObjects objects;
    private readonly IMonotonicClock clock;
    private readonly ParticipantId local;
    private readonly Dictionary<ulong, View> views = new();
    private readonly Dictionary<int, ulong> byObject = new();
    private readonly List<ulong> released = new();
    private bool restoringFreshBody;

    internal SharedItemProjection(SharedItemInventory inventory, SharedItemObjects objects, IMonotonicClock clock, ParticipantId local)
    {
        this.inventory = inventory;
        this.objects = objects;
        this.clock = clock;
        this.local = local;
    }

    internal bool TryGetId(Item item, out ulong id) => byObject.TryGetValue(item.GetInstanceID(), out id);

    internal Sprite? SpriteFor(ulong id) => views.TryGetValue(id, out View view) && view.Item
        ? view.Item!.GetComponent<SpriteRenderer>()?.sprite : null;

    internal Item? FindReplica(ulong id) => views.TryGetValue(id, out View view) ? view.Item : null;

    internal void BeginRecovery(bool freshBody) => restoringFreshBody = freshBody;

    internal bool InventoryReady(ItemReplicaStore replicas)
    {
        if (!inventory.Body)
        {
            return false;
        }
        foreach (var pair in views)
        {
            if (pair.Value.Item && inventory.Body!.HoldingItem(pair.Value.Item!) &&
                (!replicas.TryGet(pair.Key, out ItemState state) || state.Owner != local))
            {
                return false;
            }
        }

        foreach (ItemState state in replicas.States)
        {
            if (state.Owner == local &&
                (!views.TryGetValue(state.Id, out View view) || NeedsInventoryProjection(state, view)))
            {
                return false;
            }
        }

        restoringFreshBody = false;
        return true;
    }

    internal void OnStateAccepted(ItemState? previous, ItemState state)
    {
        if (previous is { } old && old.Owner.IsNone && state.Owner.IsNone && old.Revision == state.Revision &&
            views.TryGetValue(state.Id, out View view))
        {
            view.SnapNextMotion |= old.Location.VelocityX * state.Location.VelocityX +
                old.Location.VelocityY * state.Location.VelocityY < -1f;
        }
    }

    internal void Tick(ItemReplicaStore replicas, bool synced)
    {
        RemoveRetiredViews(replicas);
        float now = clock.Now;
        foreach (ItemState state in replicas.States)
        {
            if (!views.TryGetValue(state.Id, out View view))
            {
                if (!state.Owner.IsNone && state.Owner != local)
                {
                    continue;
                }
                view = new View();
                views.Add(state.Id, view);
            }

            if (view.AppliedRevision != state.Revision || (synced && NeedsInventoryProjection(state, view)))
            {
                Apply(state, view, now);
            }
            if (state.Owner.IsNone && view.Item)
            {
                Interpolate(view.Item!, state.Location, view.SnapNextMotion);
                view.SnapNextMotion = false;
                view.Item!.condition = state.Condition;
            }
        }
    }

    private void RemoveRetiredViews(ItemReplicaStore replicas)
    {
        foreach (var pair in views)
        {
            View view = pair.Value;
            if (!replicas.TryGet(pair.Key, out _) && (!view.Item || inventory.TryDrop(view.Item!)))
            {
                Destroy(view);
                released.Add(pair.Key);
            }
        }
        foreach (ulong id in released)
        {
            views.Remove(id);
        }
        released.Clear();
    }

    private bool NeedsInventoryProjection(ItemState state, View view)
    {
        Body? body = inventory.Body;
        return state.Owner == local && (view.AppliedRevision != state.Revision ||
            !view.Item || !body || state.Slot >= body!.slots.Length || body.GetItem(state.Slot) != view.Item);
    }

    private void Apply(ItemState state, View view, float now)
    {
        if (!state.Owner.IsNone && state.Owner != local)
        {
            if (view.Item && !inventory.TryDrop(view.Item!))
            {
                return;
            }
            if (view.Item)
            {
                view.Item!.rb.simulated = false;
                view.Item.gameObject.SetActive(false);
            }
        }
        else
        {
            if (!view.Item && !Create(state, view, now))
            {
                return;
            }

            Item replica = view.Item!;
            replica.condition = state.Condition;
            if (state.Owner == local)
            {
                if (!ApplyInventory(state, replica))
                {
                    return;
                }
            }
            else
            {
                if (!inventory.TryDrop(replica))
                {
                    return;
                }
                objects.PutInWorld(replica, state.Location, authoritative: false);
            }
        }
        view.AppliedRevision = state.Revision;
    }

    private bool Create(ItemState state, View view, float now)
    {
        if (now < view.NextReplicaAttempt)
        {
            return false;
        }
        Item? item = objects.CreateReplica(state.Definition, state.Location);
        if (!item)
        {
            if (state.Owner == local)
            {
                view.NextReplicaAttempt = now + ReplicaRetryInterval;
                CoopDiagnostics.Count("Items", "Inventory projection unavailable");
            }
            else
            {
                view.AppliedRevision = state.Revision;
            }
            return false;
        }

        if (view.UnityId != 0)
        {
            byObject.Remove(view.UnityId);
        }
        view.NextReplicaAttempt = 0;
        view.Item = item;
        view.UnityId = item!.GetInstanceID();
        byObject[view.UnityId] = state.Id;
        return true;
    }

    private bool ApplyInventory(ItemState state, Item replica)
    {
        Body? body = inventory.Body;
        if (!body || state.Slot >= body!.slots.Length)
        {
            return false;
        }

        Item? occupant = body.GetItem(state.Slot);
        if (restoringFreshBody && occupant && occupant != replica && !TryGetId(occupant!, out _))
        {
            if (!inventory.TryDrop(occupant!))
            {
                return false;
            }
            occupant!.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(occupant.gameObject);
        }
        if (body.GetItem(state.Slot) != replica && body.HoldingItem(replica) &&
            (!SharedItemInventory.CanPlaceInSlot(body, replica, state.Slot, force: true) || !inventory.TryDrop(replica)))
        {
            return false;
        }
        if (inventory.TryPickUp(replica, state.Slot))
        {
            return true;
        }

        if (!body.HoldingItem(replica))
        {
            replica.gameObject.SetActive(false);
        }
        return false;
    }

    private void Destroy(View view)
    {
        byObject.Remove(view.UnityId);
        if (view.Item)
        {
            view.Item!.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(view.Item.gameObject);
        }
    }

    private static void Interpolate(Item item, ItemLocation location, bool snap)
    {
        Vector2 target = new(location.X, location.Y);
        float ease = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
        if (snap || ((Vector2)item.transform.position - target).sqrMagnitude > 16f)
        {
            item.transform.SetPositionAndRotation(target, Quaternion.Euler(0, 0, location.Angle));
            return;
        }

        item.transform.position = Vector2.Lerp(item.transform.position, target, ease);
        item.transform.rotation = Quaternion.Euler(0, 0, Mathf.LerpAngle(item.transform.eulerAngles.z, location.Angle, ease));
    }

    public void Dispose()
    {
        foreach (View view in views.Values)
        {
            if (view.Item)
            {
                inventory.TryDrop(view.Item!);
            }
            Destroy(view);
        }
        views.Clear();
    }
}
