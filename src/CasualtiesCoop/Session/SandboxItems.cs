using System;
using System.Collections.Generic;
using CasualtiesCoop.Game;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Session;

internal sealed class SandboxItems : IDisposable
{
    private const int ItemLimit = 256;
    private const float UpdateInterval = 0.10f;
    private const float RetryInterval = 1f;
    private const float ReconcileInterval = 8f;
    private const float PickupRange = 10f;

    internal delegate bool PlayerPosition(ulong player, out Vector2 position);

    private sealed class Entry
    {
        internal SharedItemState State;
        internal Item? Item;
        internal int UnityId;
        internal uint AppliedRevision;
        internal bool WasMoving;
        internal float NextRefresh;
        internal Vector2 PreviousWorldPosition;
        internal float PreviousWorldSampleAt;
        internal bool SnapNextMotion;
    }

    private readonly SteamLobby lobby;
    private readonly RoomLaunchPlan plan;
    private readonly PlayerPosition playerPosition;
    private readonly SandboxItemObjects objects = new();
    private readonly bool host;
    private readonly Dictionary<ulong, Entry> entries = new();
    private readonly Dictionary<int, Entry> sceneItems = new();
    private readonly Dictionary<ulong, (ulong Operation, ItemDecision Decision)> receipts = new();
    private Body? body;
    private SharedItemRequest? pending;
    private ulong nextItemId;
    private ulong nextOperation;
    private float nextUpdate;
    private float nextResend;
    private float nextSync;
    private bool synced;
    private bool applying;

    internal SandboxItems(SteamLobby lobby, RoomLaunchPlan plan, PlayerPosition playerPosition)
    {
        this.lobby = lobby;
        this.plan = plan;
        this.playerPosition = playerPosition;
        host = lobby.IsHost;
        lobby.Link.PeerChanged += OnPeerChanged;
    }

    private ulong LocalId => lobby.LocalUser.m_SteamID;

    internal void Tick(Body localBody)
    {
        body = localBody;
        float now = Time.unscaledTime;
        if (lobby.IsHost)
        {
            if (now >= nextUpdate)
            {
                nextUpdate = now + UpdateInterval;
                RefreshHostItems(now);
            }

            return;
        }

        foreach (Entry entry in entries.Values)
        {
            if (entry.AppliedRevision != entry.State.Revision)
            {
                ApplyReplica(entry);
            }
            if (!entry.State.Removed && entry.State.Owner == 0 && entry.Item)
            {
                Interpolate(entry.Item!, entry.State.Location, entry.SnapNextMotion);
                entry.SnapNextMotion = false;
                entry.Item!.condition = entry.State.Condition;
            }
        }

        if (now >= nextSync)
        {
            nextSync = now + (synced ? ReconcileInterval : RetryInterval);
            lobby.Link.Send(lobby.Owner, SandboxWire.NewFrame(RoomFrame.ItemSyncRequest, plan.Id, 17));
        }

        if (synced && pending is { } request && now >= nextResend)
        {
            SendRequest(request);
        }
    }

    internal bool IsTracked(Item? item) => item && sceneItems.ContainsKey(item!.GetInstanceID());

    internal bool CanUse(Item? item) => applying || !IsTracked(item);
    internal bool ApplyingAuthoritativeChange => applying;

    internal bool MayPickup(Body actor, Item? item, int slot, bool force)
    {
        if (applying || !body || actor != body || !item)
        {
            return true;
        }

        if (!TryGetEntry(item!, out Entry entry))
        {
            return !IsReserved(slot);
        }

        if (entry.State.Owner == 0 && CanPlaceInSlot(actor, item!, slot, force) &&
            actor.DoPickupCheck(item!) && CanRequest())
        {
            Submit(entry, ItemAction.PickUp, slot, default);
        }
        return false;
    }

    internal bool MayDrop(Body actor, Item? item, bool thrown, float force = 0)
    {
        if (applying || !body || actor != body || !item || !TryGetEntry(item!, out Entry entry))
        {
            return true;
        }

        if (entry.State.Owner == LocalId && actor.HoldingItem(item!) && CanRequest())
        {
            Vector2 velocity = thrown ? actor.ThrowVelocity(item!, Mathf.Clamp01(force)) : actor.rb.velocity;
            Vector2 position = item!.transform.position;
            var at = new ItemLocation(position.x, position.y, item.transform.eulerAngles.z, velocity.x, velocity.y);
            Submit(entry, thrown ? ItemAction.Throw : ItemAction.Drop, entry.State.Slot, at);
        }
        return false;
    }

    internal bool IsReserved(int slot) => pending is { Action: ItemAction.PickUp } pickup && pickup.Slot == slot;

    internal Sprite? PendingSprite(int slot)
    {
        if (!IsReserved(slot) || pending is not { } request || !entries.TryGetValue(request.ItemId, out Entry entry) || !entry.Item)
        {
            return null;
        }

        return entry.Item!.GetComponent<SpriteRenderer>()?.sprite;
    }

    internal bool HandleWorldUpdate(Item item)
    {
        if (!lobby.IsHost || !TryGetEntry(item, out Entry entry) || entry.State.Owner != 0 || entry.State.Removed)
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

    private bool IsNearAPlayer(Vector2 at)
    {
        foreach (LaunchPlayer player in plan.Players)
        {
            if (playerPosition(player.SteamId, out Vector2 position) && (position - at).sqrMagnitude <= 60f * 60f)
            {
                return true;
            }
        }
        return false;
    }

    private static bool CanPlaceInSlot(Body actor, Item item, int slot, bool force) =>
        slot >= 0 && slot < actor.slots.Length && slot < ItemWire.NoSlot && !actor.HoldingItem(slot) &&
        (actor.slots[slot].canPickUp || force) && (!item.Stats.onlyHoldInHands || actor.slots[slot].isHand);

    private bool CanRequest()
    {
        if (lobby.IsHost || (synced && pending is null && lobby.Link.GetState(lobby.Owner) == RoomLinkState.Verified))
        {
            return true;
        }
        Alert("Waiting for the host to confirm shared items.");
        return false;
    }

    private void Submit(Entry item, ItemAction action, int slot, ItemLocation at)
    {
        SharedItemRequest request = new()
        {
            Operation = ++nextOperation,
            ItemId = item.State.Id,
            Revision = item.State.Revision,
            Action = action,
            Slot = (byte)slot,
            Location = at
        };
        if (lobby.IsHost)
        {
            FinishLocal(request, Execute(LocalId, request));
        }
        else
        {
            pending = request;
            SendRequest(request);
        }
    }

    private void SendRequest(SharedItemRequest request)
    {
        nextResend = Time.unscaledTime + RetryInterval;
        lobby.Link.Send(lobby.Owner, ItemWire.WriteRequest(plan.Id, request));
    }

    internal bool Receive(CSteamID sender, byte[] bytes)
    {
        RoomFrame frame = (RoomFrame)bytes[0];
        if (lobby.IsHost)
        {
            if (!plan.TryGetPlayer(sender.m_SteamID, out _))
            {
                return false;
            }

            switch (frame)
            {
                case RoomFrame.ItemSyncRequest:
                    if (body && bytes.Length == 17 && SandboxWire.HasHeader(bytes, frame, plan.Id))
                    {
                        SendSnapshot(sender);
                    }
                    return true;

                case RoomFrame.ItemRequest:
                    if (body && ItemWire.TryReadRequest(bytes, plan.Id, out SharedItemRequest request))
                    {
                        Respond(sender, request);
                    }
                    return true;

                default:
                    return false;
            }
        }

        if (sender != lobby.Owner)
        {
            return false;
        }

        switch (frame)
        {
            case RoomFrame.ItemState:
            case RoomFrame.ItemMotion:
                if (ItemWire.TryReadState(bytes, frame, plan.Id, out SharedItemState state))
                {
                    ReceiveState(frame, state);
                }
                return true;

            case RoomFrame.ItemResult:
                if (ItemWire.TryReadResult(bytes, plan.Id, out ulong operation, out ItemDecision decision) &&
                    pending is { } local && local.Operation == operation)
                {
                    pending = null;
                    FinishLocal(local, decision);
                }
                return true;

            case RoomFrame.ItemSyncDone:
                if (bytes.Length == 17 && SandboxWire.HasHeader(bytes, frame, plan.Id))
                {
                    synced = true;
                }
                return true;

            default:
                return false;
        }
    }

    private void SendSnapshot(CSteamID player)
    {
        foreach (Entry item in entries.Values)
        {
            if (!lobby.Link.Send(player, ItemWire.WriteState(RoomFrame.ItemState, plan.Id, item.State)))
            {
                return;
            }
        }
        lobby.Link.Send(player, SandboxWire.NewFrame(RoomFrame.ItemSyncDone, plan.Id, 17));
    }

    private void Respond(CSteamID sender, SharedItemRequest request)
    {
        ulong player = sender.m_SteamID;
        if (!receipts.TryGetValue(player, out var receipt) || request.Operation > receipt.Operation)
        {
            receipt = (request.Operation, Execute(player, request));
            receipts[player] = receipt;
        }

        if (receipt.Operation == request.Operation)
        {
            if (entries.TryGetValue(request.ItemId, out Entry item))
            {
                lobby.Link.Send(sender, ItemWire.WriteState(RoomFrame.ItemState, plan.Id, item.State));
            }
            lobby.Link.Send(sender, ItemWire.WriteResult(plan.Id, request.Operation, receipt.Decision));
        }
    }

    private ItemDecision Execute(ulong player, SharedItemRequest request)
    {
        if (!entries.TryGetValue(request.ItemId, out Entry entry) || !entry.Item || entry.State.Removed ||
            entry.State.Revision != request.Revision || !playerPosition(player, out Vector2 actorPosition))
        {
            return ItemDecision.Unavailable;
        }

        return request.Action == ItemAction.PickUp
            ? PickUp(player, actorPosition, request, entry)
            : Drop(player, actorPosition, request, entry);
    }

    private ItemDecision PickUp(ulong player, Vector2 position, SharedItemRequest request, Entry entry)
    {
        if (entry.State.Owner != 0)
        {
            return ItemDecision.Unavailable;
        }

        Item item = entry.Item!;
        Bounds bounds = item.GetComponent<Collider2D>().bounds;
        Vector2 target = bounds.size != Vector3.zero ? bounds.center : item.transform.position;
        int ground = LayerMask.GetMask("Ground");
        float distance = PickupRange + (player == LocalId ? 0f : 1.5f);
        bool currentReachable = CanReach(position, target, distance, ground);
        bool previousReachable = player != LocalId && entry.PreviousWorldSampleAt > 0 &&
            Time.unscaledTime - entry.PreviousWorldSampleAt <= 0.22f &&
            CanReach(position, entry.PreviousWorldPosition, distance, ground);
        if (!currentReachable && !previousReachable)
        {
            return ItemDecision.OutOfReach;
        }

        if (!plan.TryGetPlayer(player, out LaunchPlayer member) ||
            RemoteBodyView.LoadCharacter(member.Character) is not { } template || request.Slot >= template.slots.Length ||
            (item.Stats.onlyHoldInHands && !template.slots[request.Slot].isHand) || IsOccupied(player, request.Slot))
        {
            return ItemDecision.SlotOccupied;
        }
        if (player == LocalId)
        {
            if (!NativePickup(item, request.Slot))
            {
                return ItemDecision.SlotOccupied;
            }
        }
        else
        {
            item.rb.simulated = false;
            item.gameObject.SetActive(false);
        }

        entry.State.Owner = player;
        entry.State.Slot = request.Slot;
        entry.State.Condition = item.condition;
        Commit(entry);
        return ItemDecision.Accepted;
    }

    private bool IsOccupied(ulong player, byte slot)
    {
        foreach (Entry entry in entries.Values)
        {
            if (entry.State.Owner == player && entry.State.Slot == slot && !entry.State.Removed)
            {
                return true;
            }
        }
        return false;
    }

    private static bool CanReach(Vector2 actor, Vector2 item, float distance, int ground) =>
        (actor - item).sqrMagnitude <= distance * distance &&
        (!Physics2D.Linecast(actor, item, ground) || Physics2D.OverlapPoint(item, ground));

    private ItemDecision Drop(ulong player, Vector2 position, SharedItemRequest request, Entry entry)
    {
        if (entry.State.Owner != player || entry.State.Slot != request.Slot)
        {
            return ItemDecision.Unavailable;
        }

        ItemLocation requested = request.Location;
        Vector2 at = new(requested.X, requested.Y);
        if ((position - at).sqrMagnitude > 8f * 8f)
        {
            return ItemDecision.OutOfReach;
        }

        Vector2 velocity = Vector2.ClampMagnitude(new Vector2(requested.VelocityX, requested.VelocityY), 100f);
        float spin = request.Action == ItemAction.Throw ? UnityEngine.Random.Range(-800f, 800f) : 0f;
        PutInWorld(entry, new ItemLocation(at.x, at.y, requested.Angle, velocity.x, velocity.y, spin));
        return ItemDecision.Accepted;
    }

    private void PutInWorld(Entry entry, ItemLocation at)
    {
        Item item = entry.Item!;
        NativeDrop(item);
        objects.PutInWorld(item, at, authoritative: true);
        entry.State.Owner = 0;
        entry.State.Slot = ItemWire.NoSlot;
        entry.State.Condition = item.condition;
        entry.State.Location = at;
        entry.WasMoving = true;
        Commit(entry);
    }

    private bool NativePickup(Item item, int slot)
    {
        if (!body || slot < 0 || slot >= body!.slots.Length)
        {
            return false;
        }

        if (body.GetItem(slot) == item)
        {
            return true;
        }

        if (!CanPlaceInSlot(body, item, slot, true))
        {
            return false;
        }

        item.gameObject.SetActive(true);
        item.rb.simulated = false;
        applying = true;
        try
        {
            body!.PickUpItem(item, slot, force: true);
            return body.GetItem(slot) == item;
        }
        finally
        {
            applying = false;
        }
    }

    private void NativeDrop(Item item)
    {
        if (!body || !body!.HoldingItem(item))
        {
            return;
        }

        applying = true;
        try { body.DropItem(item); }
        finally { applying = false; }
    }

    private void Commit(Entry entry)
    {
        entry.State.Revision++;
        entry.State.Sample = 0;
        entry.PreviousWorldSampleAt = 0;
        Publish(entry.State, reliable: true);
    }

    private void RefreshHostItems(float now)
    {
        if (entries.Count < ItemLimit)
        {
            foreach (Item item in Item.allItems)
            {
                if (!item || item.transform.parent || !item.rb || !item.GetComponent<Collider2D>() ||
                    sceneItems.ContainsKey(item.GetInstanceID()) || !objects.Supports(item))
                {
                    continue;
                }

                Entry entry = new()
                {
                    State = new SharedItemState
                    {
                        Id = ++nextItemId,
                        Definition = item.id,
                        Slot = ItemWire.NoSlot,
                        Condition = item.condition,
                        Location = SandboxItemObjects.Read(item)
                    }
                };
                Track(entry, item);
                entries.Add(entry.State.Id, entry);
                Commit(entry);
                if (entries.Count >= ItemLimit)
                {
                    break;
                }
            }
        }

        foreach (Entry entry in entries.Values)
        {
            if (entry.State.Removed)
            {
                continue;
            }

            if (!entry.Item)
            {
                sceneItems.Remove(entry.UnityId);
                entry.State.Owner = 0;
                entry.State.Slot = ItemWire.NoSlot;
                entry.State.Removed = true;
                Commit(entry);
            }
            else if (entry.State.Owner == 0)
            {
                SampleWorldItem(entry, now);
            }
            else if (entry.State.Owner != LocalId && !lobby.HasMember(new CSteamID(entry.State.Owner)))
            {
                ItemLocation last = entry.State.Location;
                if (playerPosition(entry.State.Owner, out Vector2 at))
                {
                    last = new ItemLocation(at.x, at.y, last.Angle);
                }
                PutInWorld(entry, last);
            }
        }
    }

    private void SampleWorldItem(Entry entry, float now)
    {
        Item item = entry.Item!;
        ItemLocation old = entry.State.Location;
        ItemLocation at = SandboxItemObjects.Read(item);
        bool moving = new Vector2(at.VelocityX, at.VelocityY).sqrMagnitude > 0.0025f || Mathf.Abs(at.Spin) > 0.5f;
        bool stopped = entry.WasMoving && !moving;
        bool changed = new Vector2(at.X - old.X, at.Y - old.Y).sqrMagnitude > 0.0001f ||
            Mathf.Abs(Mathf.DeltaAngle(at.Angle, old.Angle)) > 0.25f;
        bool refresh = now >= entry.NextRefresh;
        if (moving || stopped || changed || refresh)
        {
            entry.PreviousWorldPosition = new Vector2(old.X, old.Y);
            entry.PreviousWorldSampleAt = now;
            entry.State.Location = at;
            entry.State.Condition = item.condition;
            entry.State.Sample++;
            Publish(entry.State, reliable: stopped || refresh);
            if (stopped || refresh)
            {
                entry.NextRefresh = now + 3f;
            }
        }
        entry.WasMoving = moving;
    }

    private void Publish(SharedItemState state, bool reliable)
    {
        byte[] message = ItemWire.WriteState(reliable ? RoomFrame.ItemState : RoomFrame.ItemMotion, plan.Id, state);
        foreach (LaunchPlayer participant in plan.Players)
        {
            if (participant.SteamId != LocalId)
            {
                lobby.Link.Send(new CSteamID(participant.SteamId), message, reliable);
            }
        }
    }

    private void Track(Entry entry, Item item)
    {
        entry.Item = item;
        entry.UnityId = item.GetInstanceID();
        sceneItems[entry.UnityId] = entry;
    }

    private bool TryGetEntry(Item item, out Entry entry) => sceneItems.TryGetValue(item.GetInstanceID(), out entry);

    private void ReceiveState(RoomFrame frame, SharedItemState state)
    {
        if (state.Owner != 0 && !plan.TryGetPlayer(state.Owner, out _))
        {
            return;
        }

        if (!entries.TryGetValue(state.Id, out Entry entry))
        {
            if (frame == RoomFrame.ItemState && entries.Count < ItemLimit)
            {
                entries.Add(state.Id, new Entry { State = state });
            }
            return;
        }

        if (entry.State.Definition != state.Definition || state.Revision < entry.State.Revision ||
            (state.Revision == entry.State.Revision && state.Sample < entry.State.Sample) ||
            (frame == RoomFrame.ItemMotion && (state.Revision != entry.State.Revision || entry.State.Owner != 0 ||
                state.Owner != 0 || state.Removed || entry.State.Removed)))
        {
            return;
        }

        if (state.Owner == 0 && entry.State.Owner == 0 && state.Revision == entry.State.Revision)
        {
            ItemLocation old = entry.State.Location;
            ItemLocation next = state.Location;
            entry.SnapNextMotion |= old.VelocityX * next.VelocityX + old.VelocityY * next.VelocityY < -1f;
        }
        entry.State = state;
    }

    private void ApplyReplica(Entry entry)
    {
        if (entry.State.Removed)
        {
            DestroyReplica(entry);
        }
        else if (entry.State.Owner != 0 && entry.State.Owner != LocalId)
        {
            if (entry.Item)
            {
                NativeDrop(entry.Item!);
                entry.Item!.rb.simulated = false;
                entry.Item.gameObject.SetActive(false);
            }
        }
        else
        {
            if (!entry.Item)
            {
                Item? item = objects.CreateReplica(entry.State);
                if (!item)
                {
                    entry.AppliedRevision = entry.State.Revision;
                    return;
                }
                Track(entry, item!);
            }

            Item replica = entry.Item!;
            replica.condition = entry.State.Condition;
            if (entry.State.Owner == LocalId)
            {
                if (!NativePickup(replica, entry.State.Slot))
                {
                    replica.gameObject.SetActive(false);
                    return;
                }
            }
            else
            {
                NativeDrop(replica);
                objects.PutInWorld(replica, entry.State.Location, authoritative: false);
            }
        }
        entry.AppliedRevision = entry.State.Revision;
    }

    private void DestroyReplica(Entry entry)
    {
        sceneItems.Remove(entry.UnityId);
        if (entry.Item)
        {
            NativeDrop(entry.Item!);
            entry.Item!.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(entry.Item!.gameObject);
        }
        entry.Item = null;
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

    private void FinishLocal(SharedItemRequest request, ItemDecision decision)
    {
        if (decision == ItemDecision.Accepted)
        {
            if (request.Action == ItemAction.Throw && body)
            {
                body!.armsAnimator.Play("ArmsSwing", -1, 0f);
                body.stamina -= 3.5f;
                Sound.Play("BSSwing" + UnityEngine.Random.Range(1, 5), body.transform.position, follow: body.transform);
            }
            return;
        }

        Alert(decision switch
        {
            ItemDecision.OutOfReach => "The shared item is out of reach.",
            ItemDecision.SlotOccupied => "Use an empty slot for a shared item.",
            _ => "That shared item is no longer available."
        });
    }

    private static void Alert(string message)
    {
        if (PlayerCamera.main)
        {
            PlayerCamera.main.DoAlert(message);
        }
    }

    private void OnPeerChanged(CSteamID player, bool _)
    {
        if (player == lobby.Owner && !lobby.IsHost)
        {
            synced = false;
            nextSync = 0;
        }
    }

    public void Dispose()
    {
        lobby.Link.PeerChanged -= OnPeerChanged;
        if (!host)
        {
            foreach (Entry entry in entries.Values)
            {
                DestroyReplica(entry);
            }
        }
    }
}
