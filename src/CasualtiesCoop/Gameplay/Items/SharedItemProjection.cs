using System;
using System.Collections.Generic;
using System.Linq;
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
        internal int CreatedFrame;
        internal uint AppliedRevision;
        internal byte[]? AppliedPayload;
        internal float NextReplicaAttempt;
        internal ItemMotionBuffer? Motion;
        internal bool InWorld;
        internal uint MotionRevision;
        internal float MotionTime;
    }

    private readonly SharedItemInventory inventory;
    private readonly SharedItemObjects objects;
    private readonly IMonotonicClock clock;
    private readonly ParticipantId local;
    private readonly IRemoteItemVisuals? remoteVisuals;
    private readonly Func<ulong, bool>? localPlacementPending;
    private readonly Dictionary<ulong, View> views = new();
    private readonly Dictionary<int, ulong> byObject = new();
    private static readonly HashSet<int> ReplicaIds = new();
    private readonly List<ulong> released = new();
    private readonly HashSet<ulong> suppressed = new();
    private bool restoringFreshBody;

    internal SharedItemProjection(SharedItemInventory inventory, SharedItemObjects objects, IMonotonicClock clock,
        ParticipantId local, IRemoteItemVisuals? remoteVisuals = null,
        Func<ulong, bool>? localPlacementPending = null)
    {
        this.inventory = inventory;
        this.objects = objects;
        this.clock = clock;
        this.local = local;
        this.remoteVisuals = remoteVisuals;
        this.localPlacementPending = localPlacementPending;
    }

    internal bool TryGetId(Item item, out ulong id) => byObject.TryGetValue(item.GetInstanceID(), out id);
    internal static bool IsReplica(Item item) => item && ReplicaIds.Contains(item.GetInstanceID());
    internal static void MarkReplica(Item? item)
    {
        if (item) ReplicaIds.Add(item.GetInstanceID());
    }

    internal Sprite? SpriteFor(ulong id) => views.TryGetValue(id, out View view) && view.Item
        ? view.Item!.GetComponent<SpriteRenderer>()?.sprite : null;

    internal Item? FindReplica(ulong id) => views.TryGetValue(id, out View view) ? view.Item : null;

    internal bool PlacementMatches(ItemState state) => views.TryGetValue(state.Id, out View view) &&
        !NeedsProjection(state, view);

    internal void BeginRecovery(bool freshBody) => restoringFreshBody = freshBody;
    internal void Suppress(ulong id, bool value)
    {
        if (value) suppressed.Add(id); else suppressed.Remove(id);
    }

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
                (!views.TryGetValue(state.Id, out View view) || NeedsProjection(state, view)))
            {
                return false;
            }
        }

        restoringFreshBody = false;
        return true;
    }

    internal void OnStateAccepted(ItemState? _, ItemState state)
    {
        if (state.Placement == ItemPlacement.World && state.Owner.IsNone && !state.Removed &&
            views.TryGetValue(state.Id, out View view) && view.InWorld)
        {
            TrackMotion(view, state, clock.Now);
        }
    }

    internal void BindCreated(ulong id, Item item)
    {
        if (!item)
        {
            return;
        }
        if (views.TryGetValue(id, out View existing) && existing.Item && existing.Item != item)
        {
            Destroy(existing);
        }
        var view = new View { Item = item, UnityId = item.GetInstanceID(), CreatedFrame = Time.frameCount - 1 };
        views[id] = view;
        byObject[view.UnityId] = id;
        ReplicaIds.Add(view.UnityId);
    }

    internal bool RestoreItem(ulong id, string definition, byte[] payload, float condition,
        ItemPlacement placement, ulong parent, byte slot, ItemLocation location)
    {
        if (!views.TryGetValue(id, out View view))
        {
            view = new View();
            views[id] = view;
        }
        if (!view.Item)
        {
            view.Item = objects.CreateReplica(definition, location);
            if (!view.Item) return false;
            view.UnityId = view.Item.GetInstanceID();
            ReplicaIds.Add(view.UnityId);
            view.CreatedFrame = Time.frameCount - 1;
            byObject[view.UnityId] = id;
        }

        Item replica = view.Item;
        if (!objects.Apply(replica, payload, condition, placement)) return false;
        remoteVisuals?.DetachHeldItem(replica);
        Detach(replica);
        switch (placement)
        {
            case ItemPlacement.World:
                objects.PutInWorld(replica, location, authoritative: false);
                view.Motion ??= new ItemMotionBuffer();
                view.Motion.Reset(location, clock.Now);
                view.InWorld = true;
                view.MotionRevision = 0;
                view.MotionTime = 0f;
                return true;
            case ItemPlacement.Inventory:
                if (ApplyInventory(new ItemState { Slot = slot }, replica)) return true;
                replica.gameObject.SetActive(false);
                return false;
            case ItemPlacement.Worn:
                if (inventory.TryWear(replica)) return true;
                replica.gameObject.SetActive(false);
                return false;
            case ItemPlacement.Container:
                if (views.TryGetValue(parent, out View parentView) && parentView.Item &&
                    inventory.TryAttach(parentView.Item, replica)) return true;
                replica.gameObject.SetActive(false);
                return false;
            default:
                return false;
        }
    }

    internal void Reapply(ulong id, ItemState state, ItemReplicaStore replicas)
    {
        if (!views.TryGetValue(id, out View view) || !view.Item) return;
        if (!Apply(state, view, replicas)) return;
        view.AppliedRevision = state.Revision;
        view.AppliedPayload = state.Payload;
    }

    internal void Tick(ItemReplicaStore replicas, bool synced)
    {
        RemoveRetiredViews(replicas);
        float now = clock.Now;
        IReadOnlyList<ItemState> ordered = OrderRelevant(replicas);
        foreach (ItemState state in ordered)
        {
            if (!views.TryGetValue(state.Id, out View view))
            {
                view = new View();
                views[state.Id] = view;
            }

            if (!state.Owner.IsNone && state.Owner != local)
            {
                view.InWorld = false;
                if (state.Placement is ItemPlacement.Inventory or ItemPlacement.Worn && remoteVisuals is not null)
                {
                    if (!view.Item && !Create(state, view, now)) continue;
                    if (view.AppliedRevision != state.Revision || view.AppliedPayload is null ||
                        !view.AppliedPayload.SequenceEqual(state.Payload))
                    {
                        if (!objects.Apply(view.Item!, state.Payload, state.Condition, state.Placement)) continue;
                        view.AppliedRevision = state.Revision;
                        view.AppliedPayload = state.Payload;
                    }
                    bool attached = state.Placement == ItemPlacement.Worn
                        ? remoteVisuals.AttachWornItem(state.Owner, view.Item!)
                        : remoteVisuals.AttachHeldItem(state.Owner, state.Id, state.Slot, view.Item!);
                    if (attached)
                    {
                        view.Item!.rb.simulated = false;
                        view.Item.condition = state.Condition;
                        continue;
                    }
                }
                if (view.Item) remoteVisuals?.DetachHeldItem(view.Item!);
                Hide(view);
                view.AppliedRevision = state.Revision;
                view.AppliedPayload = state.Payload;
                continue;
            }

            if (!view.Item && !Create(state, view, now))
            {
                continue;
            }
            if (!view.Item || Time.frameCount <= view.CreatedFrame)
            {
                continue;
            }

            if (view.AppliedRevision != state.Revision || view.AppliedPayload is null ||
                !view.AppliedPayload.SequenceEqual(state.Payload) || NeedsProjection(state, view))
            {
                if (state.Owner == local && (state.Placement is ItemPlacement.Inventory or ItemPlacement.Worn) &&
                    localPlacementPending?.Invoke(state.Id) == true &&
                    NeedsProjection(state, view))
                {
                    continue;
                }
                if (!Apply(state, view, replicas))
                {
                    continue;
                }
                view.AppliedRevision = state.Revision;
                view.AppliedPayload = state.Payload;
            }

            if (state.Placement == ItemPlacement.World && view.Item)
            {
                // Snapshot merge can replace the last sample without a live StateAccepted event.
                TrackMotion(view, state, now);
                ItemLocation at = view.Motion!.Evaluate(now);
                Item projected = view.Item!;
                // Update the collision body's pose so the native hover query sees the displayed item.
                projected.rb.position = new Vector2(at.X, at.Y);
                projected.rb.rotation = at.Angle;
                projected.condition = state.Condition;
            }
        }
    }

    private IReadOnlyList<ItemState> OrderRelevant(ItemReplicaStore replicas)
    {
        var ordered = new List<ItemState>();
        var depths = new Dictionary<ulong, int>();
        foreach (ItemState state in replicas.States)
        {
            if (IsRelevant(state, replicas, depths, 0))
            {
                ordered.Add(state);
            }
        }
        ordered.Sort((left, right) => Depth(left, replicas, depths, 0).CompareTo(Depth(right, replicas, depths, 0)));
        return ordered;
    }

    private bool IsRelevant(ItemState state, ItemReplicaStore replicas, Dictionary<ulong, int> depths, int depth)
    {
        if (depth > 32 || state.Removed)
        {
            return false;
        }
        if (state.Owner == local || (state.Owner.IsNone && state.Parent == 0))
        {
            return true;
        }
        // Other players' held and worn items are visual-only replicas. They must be projected
        // through the remote character anchors instead of being simulated or ignored.
        if (!state.Owner.IsNone && (state.Placement is ItemPlacement.Inventory or ItemPlacement.Worn))
        {
            return true;
        }
        if (!state.Owner.IsNone || state.Parent == 0)
        {
            return false;
        }
        return replicas.TryGet(state.Parent, out ItemState parent) && IsRelevant(parent, replicas, depths, depth + 1);
    }

    private static int Depth(ItemState state, ItemReplicaStore replicas, Dictionary<ulong, int> depths, int depth)
    {
        if (depths.TryGetValue(state.Id, out int known))
        {
            return known;
        }
        if (depth > 32 || state.Parent == 0 || !replicas.TryGet(state.Parent, out ItemState parent))
        {
            return depths[state.Id] = depth;
        }
        return depths[state.Id] = Depth(parent, replicas, depths, depth + 1);
    }

    private void RemoveRetiredViews(ItemReplicaStore replicas)
    {
        foreach (var pair in views)
        {
            View view = pair.Value;
            if (!replicas.TryGet(pair.Key, out _) && (!view.Item || Detach(view.Item!)))
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

    private bool NeedsProjection(ItemState state, View view)
    {
        Body? body = inventory.Body;
        if (!view.Item)
        {
            return true;
        }
        if (state.Placement == ItemPlacement.Inventory)
        {
            return !body || state.Slot >= body!.slots.Length || body.GetItem(state.Slot) != view.Item;
        }
        if (state.Placement == ItemPlacement.Worn)
        {
            return !body || body!.GetWearableBySlotID(view.Item!.Stats.wearSlotId) != view.Item;
        }
        if (state.Placement == ItemPlacement.Container)
        {
            return !view.Item!.TryGetParentContainer(out Container container) ||
                !views.TryGetValue(state.Parent, out View parent) || parent.Item != container.GetComponent<Item>();
        }
        return view.Item!.transform.parent;
    }

    private bool Apply(ItemState state, View view, ItemReplicaStore replicas)
    {
        Item replica = view.Item!;
        if (!objects.Apply(replica, state.Payload, state.Condition, state.Placement))
        {
            CoopDiagnostics.Count("Items", "State payload rejected");
            return false;
        }

        if (state.Placement == ItemPlacement.World && view.InWorld && !replica.transform.parent &&
            replica.gameObject.activeSelf)
        {
            return true;
        }
        view.InWorld = false;
        remoteVisuals?.DetachHeldItem(replica);
        if (state.Placement == ItemPlacement.Inventory && state.Owner == local)
        {
            return ApplyInventory(state, replica, replicas);
        }
        Detach(replica);
        switch (state.Placement)
        {
            case ItemPlacement.World:
                objects.PutInWorld(replica, state.Location, authoritative: false);
                view.Motion ??= new ItemMotionBuffer();
                view.Motion.Reset(state.Location, clock.Now);
                view.InWorld = true;
                view.MotionRevision = state.Revision;
                view.MotionTime = 0f;
                TrackMotion(view, state, clock.Now);
                return true;
            case ItemPlacement.Inventory:
                if (state.Owner != local && remoteVisuals is not null &&
                    remoteVisuals.AttachHeldItem(state.Owner, state.Id, state.Slot, replica)) return true;
                replica.gameObject.SetActive(false);
                return false;
            case ItemPlacement.Worn:
                if (state.Owner != local && remoteVisuals is not null && remoteVisuals.AttachWornItem(state.Owner, replica))
                    return true;
                return inventory.TryWear(replica);
            case ItemPlacement.Container:
                if (!views.TryGetValue(state.Parent, out View parent) || !parent.Item || !inventory.TryAttach(parent.Item!, replica))
                {
                    replica.gameObject.SetActive(false);
                    return false;
                }
                return true;
            default:
                return false;
        }
    }

    private static void TrackMotion(View view, ItemState state, float now)
    {
        if (state.Revision != view.MotionRevision && state.SampleTime == 0f)
        {
            ItemLocation current = view.Motion!.Evaluate(now);
            Vector2 difference = new(state.Location.X - current.X, state.Location.Y - current.Y);
            if (difference.sqrMagnitude > 16f)
            {
                view.Motion.Reset(state.Location, now);
                view.MotionTime = 0f;
            }
        }
        view.MotionRevision = state.Revision;
        if (state.SampleTime > view.MotionTime)
        {
            view.Motion!.Push(state, now);
            view.MotionTime = state.SampleTime;
        }
    }

    private bool Detach(Item item)
    {
        if (item.TryGetParentContainer(out Container container))
        {
            container.UnloadItem(item);
        }
        return inventory.TryDrop(item);
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
                CoopDiagnostics.Count("Items", "Replica unavailable");
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
        ReplicaIds.Add(view.UnityId);
        view.CreatedFrame = Time.frameCount;
        byObject[view.UnityId] = state.Id;
        return true;
    }

    private bool ApplyInventory(ItemState state, Item replica, ItemReplicaStore? replicas = null)
    {
        Body? body = inventory.Body;
        if (!body || state.Slot >= body!.slots.Length)
        {
            return false;
        }

        Item? occupant = body.GetItem(state.Slot);
        if (occupant == replica) return true;
        if (restoringFreshBody && occupant && occupant != replica && !TryGetId(occupant!, out _))
        {
            if (!inventory.TryDrop(occupant!))
            {
                return false;
            }
            occupant!.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(occupant.gameObject);
            occupant = null;
        }
        if (body.HoldingItem(replica))
        {
            int source = body.SlotOf(replica);
            if (occupant && (replicas is null || !TryGetId(occupant!, out ulong id) || !replicas.TryGet(id, out ItemState other) ||
                other.Owner != local || other.Placement != ItemPlacement.Inventory || other.Slot != source))
            {
                return false;
            }
            return inventory.TrySwapSlots(source, state.Slot);
        }
        if (!occupant && Detach(replica) && inventory.TryPickUp(replica, state.Slot))
        {
            return true;
        }

        if (!body.HoldingItem(replica))
        {
            replica.gameObject.SetActive(false);
        }
        return false;
    }

    private void Hide(View view)
    {
        if (!view.Item)
        {
            return;
        }
        Detach(view.Item!);
        view.Item!.rb.simulated = false;
        view.Item.gameObject.SetActive(false);
    }

    private void Destroy(View view)
    {
        if (view.UnityId != 0)
        {
            byObject.Remove(view.UnityId);
            ReplicaIds.Remove(view.UnityId);
        }
        if (view.Item)
        {
            remoteVisuals?.DetachHeldItem(view.Item!);
            view.Item!.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(view.Item.gameObject);
        }
        view.Item = null;
        view.UnityId = 0;
        view.InWorld = false;
    }

    public void Dispose()
    {
        foreach (View view in views.Values)
        {
            if (view.Item)
            {
                Detach(view.Item!);
            }
            Destroy(view);
        }
        views.Clear();
    }
}
