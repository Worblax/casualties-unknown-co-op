using System;
using System.Collections.Generic;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Items;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemReplication : IDisposable
{
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<ulong, (uint Operation, float At)> alcoholRequests = new();
    private uint alcoholOperation;
    private readonly SharedItemInventory inventory = new();
    private readonly SharedItemHostScene? hostScene;
    private readonly GuestItemSession? guest;
    private readonly SharedItemProjection? projection;

    internal SharedItemReplication(SessionPeers peers, PlayerPositionLookup playerPosition, IMonotonicClock clock)
    {
        this.peers = peers;
        this.clock = clock;
        var objects = new SharedItemObjects();
        if (peers.IsHost)
        {
            hostScene = new SharedItemHostScene(peers, playerPosition, clock, inventory, objects);
        }
        else
        {
            guest = new GuestItemSession(peers, clock);
            projection = new SharedItemProjection(inventory, objects, clock, peers.LocalParticipant);
            guest.StateAccepted += projection.OnStateAccepted;
            guest.ResultAccepted += OnResult;
        }
    }

    internal SharedItemHostScene? TestHostScene => hostScene;

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
        }
        else
        {
            projection!.Tick(guest!.Replicas, guest.Synced);
            guest.Tick();
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

    internal void ResetPeer(ulong player) => alcoholRequests.Remove(player);

    internal bool IsTracked(Item? item) => item &&
        (hostScene?.TryGetId(item!, out _) ?? projection!.TryGetId(item!, out _));

    internal bool CanUse(Item? item) => inventory.Applying || !IsTracked(item);
    internal bool ApplyingAuthoritativeChange => inventory.Applying;

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
            Submit(state, ItemAction.PickUp, (byte)slot, default);
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
            Submit(state, thrown ? ItemAction.Throw : ItemAction.Drop, state.Slot, at);
        }
        return false;
    }

    internal bool IsReserved(int slot) => guest?.IsReserved(slot) == true;

    internal Sprite? PendingSprite(int slot) =>
        guest is { Pending: { } request } && guest.IsReserved(slot) ? projection!.SpriteFor(request.ItemId) : null;

    internal bool HandleWorldUpdate(Item item) => hostScene?.HandleWorldUpdate(item) ?? true;

    private bool TryGetState(Item item, out ItemState state)
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

    private void Submit(ItemState state, ItemAction action, byte slot, ItemLocation at)
    {
        if (hostScene is not null)
        {
            var request = new SharedItemRequest { ItemId = state.Id, Revision = state.Revision, Action = action, Slot = slot, Location = at };
            OnResult(request, hostScene.Session.ExecuteLocal(request));
        }
        else
        {
            guest!.TrySubmit(state.Id, action, slot, at);
        }
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
            guest.StateAccepted -= projection!.OnStateAccepted;
            guest.ResultAccepted -= OnResult;
            guest.Dispose();
            projection.Dispose();
        }
    }
}
