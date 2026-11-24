using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemReplication : IDisposable
{
    private const float GuestStateInterval = 0.35f;
    private const float RegistrationScanInterval = 0.5f;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<ulong, (uint Operation, float At)> alcoholRequests = new();
    private readonly Dictionary<ulong, Item> pendingRegistrations = new();
    private readonly HashSet<int> registrationObjects = new();
    private readonly Dictionary<ulong, (uint Revision, byte[] Payload, float Condition)> reported = new();
    private uint alcoholOperation;
    private float nextGuestState;
    private float nextRecipeRefresh;
    private float nextRegistrationScan;
    private bool recipeRefresh;
    private readonly SharedItemInventory inventory = new();
    private readonly SharedItemHostScene? hostScene;
    private readonly GuestItemSession? guest;
    private readonly SharedItemProjection? projection;
    private readonly SharedItemCrafting? crafting;

    internal SharedItemReplication(SessionPeers peers, PlayerPositionLookup playerPosition, IMonotonicClock clock)
    {
        this.peers = peers;
        this.clock = clock;
        var objects = new SharedItemObjects();
        if (peers.IsHost)
        {
            hostScene = new SharedItemHostScene(peers, playerPosition, clock, inventory, objects);
            hostScene.Session.OperationResponded += (_, _, _) => recipeRefresh = true;
            hostScene.Session.CraftStateChanged += () => recipeRefresh = true;
        }
        else
        {
            guest = new GuestItemSession(peers, clock);
            projection = new SharedItemProjection(inventory, objects, clock, peers.LocalParticipant);
            guest.StateAccepted += OnStateChanged;
            guest.ResultAccepted += OnResult;
            guest.RegistrationAccepted += OnRegistrationAccepted;
            guest.CraftResultAccepted += OnCraftResult;
            crafting = new SharedItemCrafting(this, objects);
        }
    }

    internal SharedItemHostScene? TestHostScene => hostScene;
    internal GuestItemSession? TestGuest => guest;
    internal ulong LastOperation(ulong player) => hostScene?.Session.LastOperation(player) ?? 0;

    internal void SuspendForRecovery() => guest?.SuspendForRecovery();

    internal void BeginRecovery(ulong confirmedOperation, bool freshBody)
    {
        guest?.ResumeAfterOffer(confirmedOperation);
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

        projection!.Tick(guest!.Replicas, guest.Synced);
        guest.Tick();
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

    internal bool CanUse(Item? item) => inventory.Applying || !IsTracked(item);
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
    internal bool CanCraft => guest is { Synced: true };
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

    internal Sprite? PendingSprite(int slot) =>
        guest is { Pending: { } request } && guest.IsReserved(slot) ? projection!.SpriteFor(request.ItemId) : null;

    internal bool HandleWorldUpdate(Item item)
    {
        if (hostScene is not null)
        {
            return hostScene.HandleWorldUpdate(item);
        }
        return !TryGetState(item, out ItemState state) || state.Placement != ItemPlacement.World ||
            !state.Owner.IsNone || inventory.Applying;
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
        if (hostScene is not null || guest!.CanSubmit)
        {
            return true;
        }

        Alert("Waiting for the host to confirm shared items.");
        return false;
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
            if (state.Removed || state.Owner != peers.LocalParticipant)
            {
                continue;
            }
            Item? item = projection!.FindReplica(state.Id);
            if (!item)
            {
                continue;
            }
            if (crafting?.IsReserved(state.Id) == true) continue;
            if (reported.TryGetValue(state.Id, out var previous) && previous.Revision == state.Revision)
            {
                continue;
            }
            byte[] payload = ItemStateCodec.Capture(item!);
            if (state.Payload.SequenceEqual(payload) && Mathf.Abs(state.Condition - item!.condition) < 0.001f)
            {
                continue;
            }
            if (guest.TryReportState(state.Id, state.Revision, item!.condition, payload))
            {
                reported[state.Id] = (state.Revision, payload, item.condition);
            }
        }
    }

    private void TickLocalRegistrations(Body body)
    {
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

        ulong parent = 0;
        byte slot = ItemWire.NoSlot;
        ItemPlacement placement = ItemPlacement.World;
        if (item.TryGetParentContainer(out Container container) && container.TryGetComponent<Item>(out Item containerItem))
        {
            if (!projection!.TryGetId(containerItem, out parent)) return false;
            placement = ItemPlacement.Container;
        }
        else
        {
            for (int index = 0; index < body!.slots.Length; index++)
            {
                if (body.GetItem(index) == item)
                {
                    placement = ItemPlacement.Inventory;
                    slot = (byte)index;
                    break;
                }
            }
            if (placement == ItemPlacement.World && body.GetAllWearables().Contains(item))
            {
                placement = ItemPlacement.Worn;
            }
        }
        if (placement == ItemPlacement.World || registrationObjects.Count >= 32)
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
        pendingRegistrations[clientKey] = item;
        registrationObjects.Add(item.GetInstanceID());
        return true;
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

    internal bool RequestCraft(int recipe, ulong[] ids, uint[] revisions, out ulong operation) =>
        guest!.TryRequestCraft(recipe, ids, revisions, out operation);

    internal bool CommitCraft(ItemCraftCommit commit) => guest!.TryCommitCraft(commit);

    internal ulong ReserveClientKey() => guest!.ReserveClientKey();

    internal void ExpectRegistration(ulong clientKey, Item item)
    {
        pendingRegistrations[clientKey] = item;
        registrationObjects.Add(item.GetInstanceID());
    }

    internal void Suppress(ulong id, bool value) => projection?.Suppress(id, value);
    internal Item? FindReplica(ulong id) => projection!.FindReplica(id);

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
        if (!pendingRegistrations.TryGetValue(clientKey, out Item item) || !item)
        {
            return;
        }
        pendingRegistrations.Remove(clientKey);
        registrationObjects.Remove(item.GetInstanceID());
        projection!.BindCreated(itemId, item);
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
