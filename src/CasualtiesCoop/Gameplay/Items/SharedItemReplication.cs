using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Runtime;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemReplication : IDisposable
{
    private const float GuestStateInterval = 0.35f;
    private const float RegistrationScanInterval = 0.5f;
    private sealed class PendingRegistration
    {
        internal Item Item = null!;
        internal SharedItemRegistration Request;
        internal float NextSend;
    }
    private struct ReportedState
    {
        internal uint Revision;
        internal byte[] Payload;
        internal float Condition;
        internal ItemPlacement Placement;
        internal ulong Parent;
        internal byte Slot;
        internal ItemLocation Location;
        internal float NextSend;
    }
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<ulong, (uint Operation, float At)> alcoholRequests = new();
    private readonly Dictionary<ulong, PendingRegistration> pendingRegistrations = new();
    private readonly HashSet<int> registrationObjects = new();
    private readonly Dictionary<ulong, ReportedState> reported = new();
    private uint alcoholOperation;
    private float nextGuestState;
    private float nextRecipeRefresh;
    private float nextRegistrationScan;
    private bool recipeRefresh;
    internal int ReportsSent { get; private set; }
    internal int RegistrationBinds { get; private set; }
    private readonly SharedItemInventory inventory = new();
    private readonly SharedItemObjects objects = new();
    private readonly SharedItemHostScene? hostScene;
    private readonly GuestItemSession? guest;
    private readonly SharedItemProjection? projection;
    private readonly SharedItemCrafting? crafting;
    private readonly Dictionary<ulong, float> localPlacementUntil = new();

    internal SharedItemReplication(SessionPeers peers, PlayerPositionLookup playerPosition, IMonotonicClock clock,
        IRemoteItemVisuals? remoteVisuals = null)
    {
        this.peers = peers;
        this.clock = clock;

        if (peers.IsHost)
        {
            hostScene = new SharedItemHostScene(peers, playerPosition, clock, inventory, objects, remoteVisuals);
            hostScene.Session.OperationResponded += (_, _, _) => recipeRefresh = true;
            hostScene.Session.CraftStateChanged += () => recipeRefresh = true;
        }
        else
        {
            guest = new GuestItemSession(peers, clock);
            projection = new SharedItemProjection(inventory, objects, clock, peers.LocalParticipant, remoteVisuals,
                LocalPlacementPending);
            guest.StateAccepted += OnStateChanged;
            guest.ResultAccepted += OnResult;
            guest.RegistrationAccepted += OnRegistrationAccepted;
            guest.CraftResultAccepted += OnCraftResult;
            crafting = new SharedItemCrafting(this, objects);
        }
    }

    internal SharedItemHostScene? TestHostScene => hostScene;
    internal GuestItemSession? TestGuest => guest;
    internal bool CraftPending => crafting?.HasPending == true;
    internal int LastCraftCreatedCount => crafting?.LastCreatedCount ?? -1;
    internal int LastCraftCompletion => crafting?.LastCompletion ?? -1;
    internal int LastScopeUsed => crafting?.LastScopeUsed ?? -1;
    internal int LastScopeCreated => crafting?.LastScopeCreated ?? -1;
    internal int LastLiquidTargets => crafting?.LastLiquidTargets ?? -1;
    internal ulong LastOperation(ulong player) => hostScene?.Session.LastOperation(player) ?? 0;
    internal ulong LastCraftOperation(ulong player) => hostScene?.Session.LastCraftOperation(player) ?? 0;
    internal ulong LastRegistrationOperation(ulong player) =>
        hostScene?.Session.LastRegistrationOperation(player) ?? 0;
    internal ulong LastClientKey(ulong player) => hostScene?.Session.LastClientKey(player) ?? 0;

    internal void SuspendForRecovery() => guest?.SuspendForRecovery();

    internal void BeginRecovery(ulong confirmedOperation, ulong confirmedCraftOperation,
        ulong confirmedRegistrationOperation, ulong confirmedClientKey, bool freshBody)
    {
        guest?.ResumeAfterOffer(confirmedOperation, confirmedCraftOperation, confirmedRegistrationOperation,
            confirmedClientKey);
        projection?.BeginRecovery(freshBody);
    }

    internal bool RecoveryComplete() =>
        guest is { Synced: true } && projection!.InventoryReady(guest.Replicas);

    internal void Tick(Body localBody)
    {
        inventory.Body = localBody;
        if (hostScene is not null)
        {
            hostScene.Tick();
            hostScene.Session.TickSnapshots();
            return;
        }

        guest.Tick();
        crafting?.Tick();
        if (guest.Synced)
        {
            if (clock.Now >= nextRegistrationScan)
            {
                nextRegistrationScan = clock.Now + RegistrationScanInterval;
                TickLocalRegistrations(localBody);
            }
            if (clock.Now >= nextGuestState)
            {
                nextGuestState = clock.Now + GuestStateInterval;
                TickOwnedState();
            }
        }
        projection!.Tick(guest.Replicas, guest.Synced);
    }

    private void OnStateChanged(ItemState? previous, ItemState state)
    {
        projection!.OnStateAccepted(previous, state);
        recipeRefresh = true;
    }

    private void RefreshRecipeUi()
    {
        if (!recipeRefresh || nextRecipeRefresh > clock.Now || !PlayerCamera.main || !PlayerCamera.main.craftingPanel ||
            !PlayerCamera.main.craftingPanel.activeSelf) return;
        nextRecipeRefresh = clock.Now + 0.25f;
        recipeRefresh = false;
        PlayerCamera.main.RefreshRecipeList();
    }
    internal void Observe(Item? item)
    {
        if (!item || !item)
        {
            return;
        }
        if (hostScene is not null)
        {
            hostScene.Observe(item);
        }
        else if (!projection!.TryGetId(item!, out _) && !registrationObjects.Contains(item!.GetInstanceID()))
        {
            TryRegisterLocal(item!);
        }
    }

    internal void ItemStarted(Item? item)
    {
        if (!item) return;
        if (hostScene is not null)
        {
            hostScene.Observe(item);
            return;
        }
        if (projection!.TryGetId(item!, out ulong id) && guest!.Replicas.TryGet(id, out ItemState state))
        {
            projection.Reapply(id, state);
            return;
        }
        if (!registrationObjects.Contains(item!.GetInstanceID())) TryRegisterLocal(item!);
    }

    internal void MarkDirty(Item? item)
    {
        if (!item || !item)
        {
            return;
        }
        if (hostScene is not null)
        {
            hostScene.MarkDirty(item);
            return;
        }
        if (projection!.TryGetId(item!, out _))
        {
            nextGuestState = 0;
        }
        else if (!registrationObjects.Contains(item!.GetInstanceID()))
        {
            TryRegisterLocal(item!);
        }
    }

    internal bool Receive(ulong sender, byte[] bytes) =>
        hostScene?.Session.Receive(sender, bytes, inventory.Body) ?? guest!.Receive(sender, bytes);

    internal bool RequestAlcohol(Vector2 at)
    {
        if (!inventory.Body)
        {
            return false;
        }
        Body body = inventory.Body!;
        int? empty = (at - (Vector2)body.transform.position).sqrMagnitude < 4 ? body.FirstEmptySlot() : null;
        byte slot = empty is >= 0 and < ItemWire.NoSlot ? (byte)empty.Value : ItemWire.NoSlot;
        return hostScene is not null ? hostScene.SpawnAlcohol(peers.LocalPlayer, at, slot)
            : peers.Send(peers.Host, SandboxGameplayWire.WriteAlcohol(peers.SessionId, ++alcoholOperation, at.x, at.y, slot));
    }

    internal bool ReceiveAlcohol(ulong player, byte[] bytes)
    {
        if (hostScene is null || player == peers.LocalPlayer || !peers.TryGetPlayer(player, out _))
        {
            return false;
        }
        if (!SandboxGameplayWire.TryReadAlcohol(bytes, peers.SessionId, out var request))
        {
            return true;
        }
        float now = clock.Now;
        if (alcoholRequests.TryGetValue(player, out var last) &&
            (now - last.At < 0.5f || unchecked((int)(request.Operation - last.Operation)) <= 0))
        {
            return true;
        }

        alcoholRequests[player] = (request.Operation, now);
        hostScene.SpawnAlcohol(player, new Vector2(request.X, request.Y), request.Slot);
        return true;
    }

    internal void ResetPeer(ulong player)
    {
        alcoholRequests.Remove(player);
        pendingRegistrations.Clear();
        registrationObjects.Clear();
    }

    internal bool IsTracked(Item? item) => item &&
        (hostScene?.TryGetId(item!, out _) ?? projection!.TryGetId(item!, out _));

    internal bool CanUse(Item? item)
    {
        if (inventory.Applying || !item) return true;
        if (!TryGetState(item, out ItemState state)) return true;
        if (state.Owner != peers.LocalParticipant) return false;
        return hostScene is not null ? !hostScene.Session.IsCraftReserved(state.Id) : !IsReservedItem(state.Id);
    }
    internal bool CanChangePlacement(Item? item)
    {
        if (inventory.Applying || !item) return true;
        if (!TryGetState(item, out ItemState state)) return true;
        return state.Owner == peers.LocalParticipant &&
            (hostScene is null || !hostScene.Session.IsCraftReserved(state.Id));
    }
    internal bool ApplyingAuthoritativeChange => inventory.Applying;
    internal Body? LocalBody => inventory.Body;
    internal ParticipantId LocalParticipant => peers.LocalParticipant;
    internal bool TryGetById(ulong id, out ItemState state)
    {
        if (guest is not null) return guest.Replicas.TryGet(id, out state);
        if (hostScene is not null) return hostScene.Session.TryGet(id, out state);
        state = default;
        return false;
    }
    internal bool CanCraft => guest is { Synced: true } && WorldAllowsInteraction();
    internal bool CanCraftFrom(ItemState state)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            if (state.Owner == peers.LocalParticipant) return true;
            if (!state.Owner.IsNone) return false;
            if (state.Parent == 0) return true;
            if (guest is null || !guest.Replicas.TryGet(state.Parent, out state)) return false;
        }
        return false;
    }

    internal bool CanSelectCraftIngredient(Item? item) =>
        item && TryGetState(item!, out ItemState state) && CanCraftFrom(state);

    internal IEnumerable<Item> CraftCandidates()
    {
        Body? body = inventory.Body;
        if (!body) yield break;
        foreach (Item item in Item.allItems)
        {
            if (!item || !CanSelectCraftIngredient(item)) continue;
            if (((Vector2)(item.transform.position - body!.transform.position)).sqrMagnitude > 100f) continue;
            yield return item;
        }
    }

    internal bool TryGetId(Item item, out ulong id) => hostScene?.TryGetId(item, out id) ?? projection!.TryGetId(item, out id);

    internal bool MayPickup(Body actor, Item? item, int slot, bool force)
    {
        if (inventory.Applying || !inventory.Body || actor != inventory.Body || !item)
        {
            return true;
        }
        if (!TryGetState(item!, out ItemState state))
        {
            return !IsReserved(slot);
        }
        if (state.Owner.IsNone && SharedItemInventory.CanPlaceInSlot(actor, item!, slot, force) &&
            actor.DoPickupCheck(item!) && CanRequest())
        {
            Submit(state, ItemAction.PickUp, (byte)slot, default, Array.Empty<byte>());
        }
        return false;
    }

    internal bool MayDrop(Body actor, Item? item, bool thrown, float force = 0)
    {
        if (inventory.Applying || !inventory.Body || actor != inventory.Body || !item || !TryGetState(item!, out ItemState state))
        {
            return true;
        }
        if (state.Owner == peers.LocalParticipant && actor.HoldingItem(item!) && CanRequest())
        {
            Vector2 velocity = thrown ? actor.ThrowVelocity(item!, Mathf.Clamp01(force)) : actor.rb.velocity;
            Vector2 position = item!.transform.position;
            var at = new ItemLocation(position.x, position.y, item.transform.eulerAngles.z, velocity.x, velocity.y);
            Submit(state, thrown ? ItemAction.Throw : ItemAction.Drop, state.Slot, at, ItemStateCodec.Capture(item!));
        }
        return false;
    }

    internal bool IsReserved(int slot) => guest?.IsReserved(slot) == true;

    internal bool TryNativeSwap(Body body, int first, int second)
    {
        if (inventory.Applying || !inventory.Body || inventory.Body != body) return true;
        Item? left = body.GetItem(first);
        Item? right = body.GetItem(second);
        if (!IsTracked(left) && !IsTracked(right)) return true;
        if (IsReserved(first) || IsReserved(second)) return false;
        if ((left && !CanChangePlacement(left)) || (right && !CanChangePlacement(right))) return false;
        inventory.Applying = true;
        try
        {
            body.SwapSlots(first, second);
        }
        finally
        {
            inventory.Applying = false;
        }
        nextGuestState = 0;
        return false;
    }

    internal Sprite? PendingSprite(int slot) =>
        guest is { Pending: { } request } && guest.IsReserved(slot) ? projection!.SpriteFor(request.ItemId) : null;

    internal bool HandleWorldUpdate(Item item)
    {
        if (hostScene is not null)
        {
            return hostScene.HandleWorldUpdate(item);
        }
        if (!TryGetState(item, out ItemState state)) return true;
        if (!state.Owner.IsNone && state.Owner != peers.LocalParticipant) return false;
        return state.Placement != ItemPlacement.World || !state.Owner.IsNone || inventory.Applying;
    }

    internal bool TryGetState(Item item, out ItemState state)
    {
        state = default;
        if (hostScene is not null)
        {
            return hostScene.TryGetId(item, out ulong id) && hostScene.Session.TryGet(id, out state);
        }
        return projection!.TryGetId(item, out ulong replica) && guest!.Replicas.TryGet(replica, out state);
    }

    private bool CanRequest()
    {
        if (!WorldAllowsInteraction())
        {
            Alert("Waiting for the current world chunk.");
            return false;
        }
        if (hostScene is not null || guest!.CanSubmit)
        {
            return true;
        }

        Alert("Waiting for the host to confirm shared items.");
        return false;
    }

    private bool WorldAllowsInteraction()
    {
        if (hostScene is not null) return true;
        return CoopRuntime.ActiveWorld?.WorldChunks.IsCurrentAtPlayer() != false;
    }

    private void Submit(ItemState state, ItemAction action, byte slot, ItemLocation at, byte[] payload)
    {
        if (hostScene is not null)
        {
            var request = new SharedItemRequest
            {
                ItemId = state.Id, Revision = state.Revision, Action = action, Slot = slot, Location = at, Payload = payload
            };
            OnResult(request, hostScene.Session.ExecuteLocal(request));
        }
        else
        {
            guest!.TrySubmit(state.Id, action, slot, at, payload);
        }
    }

    private void TickOwnedState()
    {
        foreach (ItemState state in guest!.Replicas.States)
        {
            if (state.Removed || !EffectiveOwnedBy(state.Id, peers.LocalParticipant))
            {
                continue;
            }
            Item? item = projection!.FindReplica(state.Id);
            if (!item)
            {
                continue;
            }
            if (crafting?.IsReserved(state.Id) == true) continue;
            byte[] payload = ItemStateCodec.Capture(item!);
            (ItemPlacement placement, ulong parent, byte slot) = CurrentPlacement(item!);
            ItemLocation location = SharedItemObjects.Read(item!);
            bool authoritative = state.Payload.SequenceEqual(payload) &&
                Mathf.Abs(state.Condition - item!.condition) < 0.001f && state.Placement == placement &&
                state.Parent == parent && state.Slot == slot;
            if (authoritative)
            {
                if (reported.TryGetValue(state.Id, out ReportedState synced) && synced.Revision != state.Revision)
                    reported[state.Id] = new ReportedState { Revision = state.Revision, Payload = payload,
                        Condition = item.condition, Placement = placement, Parent = parent, Slot = slot,
                        Location = location };
                continue;
            }
            float now = clock.Now;
            if (reported.TryGetValue(state.Id, out ReportedState previous) && previous.Revision == state.Revision &&
                previous.Payload.SequenceEqual(payload) && Mathf.Abs(previous.Condition - item!.condition) < 0.001f &&
                previous.Placement == placement && previous.Parent == parent && previous.Slot == slot &&
                now < previous.NextSend)
                continue;
            if (guest.TryReportState(state.Id, state.Revision, item!.condition, payload, placement, parent, slot, location))
            {
                ReportsSent++;
                localPlacementUntil[state.Id] = now + 1.5f;
                reported[state.Id] = new ReportedState { Revision = state.Revision, Payload = payload,
                    Condition = item.condition, Placement = placement, Parent = parent, Slot = slot,
                    Location = location, NextSend = now + 1f };
            }
        }
    }

    private bool LocalPlacementPending(ulong id) =>
        localPlacementUntil.TryGetValue(id, out float until) && clock.Now < until;

    private bool EffectiveOwnedBy(ulong id, ParticipantId player)
    {
        for (int depth = 0; depth < 32 && guest!.Replicas.TryGet(id, out ItemState state); depth++)
        {
            if (state.Owner == player) return true;
            if (!state.Owner.IsNone || state.Parent == 0) return false;
            id = state.Parent;
        }
        return false;
    }

    private void TickLocalRegistrations(Body body)
    {
        TickPendingRegistrations();
        foreach (Item item in Item.allItems)
        {
            if (!item || projection!.TryGetId(item, out _) || registrationObjects.Contains(item.GetInstanceID()))
            {
                continue;
            }
            if (HasLocalPlacement(body, item))
            {
                TryRegisterLocal(item);
            }
        }
    }

    private bool TryRegisterLocal(Item item)
    {
        Body? body = inventory.Body;
        if (guest is not { Synced: true } || !body)
        {
            return false;
        }
        // Items produced by an executing recipe belong to the craft commit. Registering them
        // separately would reserve their slot before the host validates the commit.
        if (CraftExecutionScope.Current is { } scope && scope.Created.Contains(item))
        {
            return false;
        }

        (ItemPlacement placement, ulong parent, byte slot) = CurrentPlacement(item);
        if (placement == ItemPlacement.Container && parent == 0) return false;
        if (pendingRegistrations.Count >= 256)
        {
            return false;
        }

        var registration = new SharedItemRegistration
        {
            Definition = item.id, Placement = placement, Parent = parent, Slot = slot,
            Location = SharedItemObjects.Read(item), Payload = ItemStateCodec.Capture(item)
        };
        if (!guest.TryRegister(registration, out ulong clientKey))
        {
            return false;
        }
        pendingRegistrations[clientKey] = new PendingRegistration
        {
            Item = item, Request = registration, NextSend = Time.unscaledTime + 1f
        };
        registrationObjects.Add(item.GetInstanceID());
        return true;
    }

    private void TickPendingRegistrations()
    {
        if (pendingRegistrations.Count == 0) return;
        var expired = new List<ulong>();
        foreach (var pair in pendingRegistrations)
        {
            PendingRegistration pending = pair.Value;
            if (!pending.Item)
            {
                expired.Add(pair.Key);
                continue;
            }
            if (pending.Request.Operation == 0 || Time.unscaledTime < pending.NextSend) continue;
            guest!.TryResendRegistration(pending.Request);
            pending.NextSend = Time.unscaledTime + 1f;
        }
        foreach (ulong key in expired)
        {
            if (!pendingRegistrations.TryGetValue(key, out PendingRegistration pending)) continue;
            registrationObjects.Remove(pending.Item.GetInstanceID());
            pendingRegistrations.Remove(key);
        }
    }

    private static bool HasLocalPlacement(Body body, Item item)
    {
        if (item.TryGetParentContainer(out Container container) && container.TryGetComponent<Item>(out Item parent))
        {
            return body.HoldingItem(parent) || body.GetAllWearables().Contains(parent) ||
                parent.TryGetParentContainer(out _);
        }
        return body.HoldingItem(item) || body.GetAllWearables().Contains(item);
    }

    private (ItemPlacement Placement, ulong Parent, byte Slot) CurrentPlacement(Item item)
    {
        Body? body = inventory.Body;
        if (body)
        {
            for (int index = 0; index < body.slots.Length; index++)
            {
                if (body.GetItem(index) == item) return (ItemPlacement.Inventory, 0, (byte)index);
            }
            if (body.GetAllWearables().Contains(item)) return (ItemPlacement.Worn, 0, ItemWire.NoSlot);
        }
        if (item.TryGetParentContainer(out Container container) &&
            container.TryGetComponent<Item>(out Item containerItem))
        {
            return (ItemPlacement.Container,
                projection!.TryGetId(containerItem, out ulong parent) ? parent : 0, ItemWire.NoSlot);
        }
        return (ItemPlacement.World, 0, ItemWire.NoSlot);
    }

    internal bool RequestCraft(int recipe, ulong[] ids, uint[] revisions, out ulong operation) =>
        guest!.TryRequestCraft(recipe, ids, revisions, out operation);

    internal bool CommitCraft(ItemCraftCommit commit) => guest!.TryCommitCraft(commit);
    internal bool ResendCraftRequest(ulong operation, int recipe, ulong[] ids, uint[] revisions) =>
        guest!.TryResendCraft(operation, recipe, ids, revisions);
    internal void CancelCraft(ulong operation, int recipe) => guest!.TryCancelCraft(operation, recipe);

    internal void FinishCraft(Recipe recipe) => crafting?.Finish(recipe);

    internal bool TryGetState(ulong id, out ItemState state) => TryGetById(id, out state);
    internal int LocalPlacementOf(ulong id)
    {
        Item? item = hostScene is not null ? hostScene.FindTracked(id) : projection!.FindReplica(id);
        return item ? (int)CurrentPlacement(item).Placement : -1;
    }
    internal Dictionary<ulong, ItemState>.ValueCollection ReplicaStates =>
        guest?.Replicas.States ?? new Dictionary<ulong, ItemState>().Values;
    internal bool ApplyReplicaState(Item item, byte[] payload, float condition, ItemPlacement placement) =>
        objects.Apply(item, payload, condition, placement);
    internal void InvalidateItemSnapshot() => guest?.InvalidateSnapshot();
    internal void SuppressPending(ulong clientKey) { }

    internal void ForgetPending(ulong clientKey, Item item)
    {
        pendingRegistrations.Remove(clientKey);
        registrationObjects.Remove(item.GetInstanceID());
    }

    internal ulong ReserveClientKey() => guest!.ReserveClientKey();

    internal void ExpectRegistration(ulong clientKey, Item item)
    {
        pendingRegistrations[clientKey] = new PendingRegistration { Item = item };
        registrationObjects.Add(item.GetInstanceID());
    }

    internal void Suppress(ulong id, bool value) => projection?.Suppress(id, value);
    internal Item? FindReplica(ulong id) => projection!.FindReplica(id);
    internal void RestoreItem(ulong id, string definition, byte[] payload, float condition, ItemPlacement placement,
        ulong parent, byte slot, ItemLocation location) =>
        projection!.RestoreItem(id, definition, payload, condition, placement, parent, slot, location);
    private bool IsReservedItem(ulong id) => crafting?.IsReserved(id) == true;

    internal void BindRestored(ulong id, Item item) => projection!.BindCreated(id, item);

    internal void AttachToSlot(Item item, byte slot) => inventory.TryPickUp(item, slot);

    internal bool CraftOverride(Recipe recipe, out List<Item>? ingredients)
    {
        ingredients = null;
        return crafting is not null && crafting.OverrideIngredients(recipe, out ingredients);
    }

    internal bool RestrictsCraftIngredients => crafting is not null;

    internal bool TryCraft(Recipe recipe)
    {
        if (hostScene is not null)
        {
            List<Item>? selected = recipe.GetItemsForRecipe();
            if (selected is null) return true;
            foreach (Item item in selected)
            {
                if (item && hostScene.TryGetId(item, out ulong id) && hostScene.Session.IsCraftReserved(id))
                {
                    Alert("Another player is using that item for crafting.");
                    return false;
                }
            }
            return true;
        }
        return crafting?.TryBegin(recipe) ?? true;
    }

    private void OnCraftResult(ulong operation, int recipe, ItemDecision decision) => crafting?.OnResult(operation, recipe, decision);
    private void OnRegistrationAccepted(ulong clientKey, ulong itemId, uint revision)
    {
        if (!pendingRegistrations.TryGetValue(clientKey, out PendingRegistration pending) || !pending.Item)
        {
            return;
        }
        pendingRegistrations.Remove(clientKey);
        registrationObjects.Remove(pending.Item.GetInstanceID());
        projection!.BindCreated(itemId, pending.Item);
        RegistrationBinds++;
    }

    private void OnResult(SharedItemRequest request, ItemDecision decision)
    {
        if (decision == ItemDecision.Accepted)
        {
            if (request.Action == ItemAction.Throw && inventory.Body is { } body && body)
            {
                body.armsAnimator.Play("ArmsSwing", -1, 0f);
                body.stamina -= 3.5f;
                Sound.Play("BSSwing" + UnityEngine.Random.Range(1, 5), body.transform.position, follow: body.transform);
            }
            return;
        }

        Alert(decision switch
        {
            ItemDecision.OutOfReach => "The shared item is out of reach.",
            ItemDecision.SlotOccupied => "Use an empty slot for a shared item.",
            ItemDecision.Conflict => "That item changed during the operation.",
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

    public void Dispose()
    {
        hostScene?.Session.Dispose();
        if (guest is not null)
        {
            guest.StateAccepted -= OnStateChanged;
            guest.ResultAccepted -= OnResult;
            guest.RegistrationAccepted -= OnRegistrationAccepted;
            guest.CraftResultAccepted -= OnCraftResult;
            guest.Dispose();
            projection!.Dispose();
        }
    }
}
