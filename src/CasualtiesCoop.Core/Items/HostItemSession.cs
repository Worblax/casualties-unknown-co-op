using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core.Items;

internal sealed class HostItemSession : IDisposable
{
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly IItemWorld world;
    private readonly AuthoritativeItemRegistry items = new();
    private readonly HostItemOperations operations;
    private readonly HostItemSnapshots snapshots;
    private readonly HostCraftReservations crafts;
    private readonly Dictionary<ParticipantId, (ulong ClientKey, ulong Item, uint Revision)> registrations = new();

    internal HostItemSession(SessionPeers peers, IItemWorld world, IMonotonicClock clock)
    {
        this.peers = peers;
        this.clock = clock;
        this.world = world;
        operations = new HostItemOperations(items, world, clock);
        snapshots = new HostItemSnapshots(items, peers);
        crafts = new HostCraftReservations(items, clock);
        peers.ConnectionChanged += OnPeerChanged;
    }

    internal event Action<SharedItemRequest, ItemDecision, bool>? OperationResponded;
    internal event Action? CraftStateChanged;

    internal bool HasCapacity => items.HasCapacity;
    internal bool TryGet(ulong item, out ItemState state) => items.TryGet(item, out state);
    internal ItemState[] Snapshot() => items.CaptureSnapshot(out _);
    internal ulong RootId(ulong id) => items.RootId(id);
    internal bool IsCraftReserved(ulong id) => crafts.IsReserved(id);
    internal ulong LastOperation(ulong address) => operations.LastOperation(peers.Identify(address));

    internal bool TryRegister(string definition, ItemLocation location, float condition, out ulong id)
    {
        id = 0;
        if (!items.TryRegister(definition, location, condition, out ItemState state))
        {
            return false;
        }

        id = state.Id;
        PublishDelta(state);
        return true;
    }

    internal bool TryRegister(string definition, ItemPlacement placement, ParticipantId owner, ulong parent, byte slot,
        ItemLocation location, float condition, byte[] payload, ulong clientKey, out ItemState state)
    {
        state = default;
        if (!items.TryRegister(definition, placement, owner, parent, slot, location, condition, payload, clientKey, out state))
        {
            return false;
        }
        PublishDelta(state);
        return true;
    }

    internal bool TryCommitPlacement(ulong item, ItemPlacement placement, ParticipantId owner, ulong parent, byte slot,
        ItemLocation location, float condition, byte[] payload, out ItemState state)
    {
        if (!items.TryGet(item, out _))
        {
            state = default;
            return false;
        }
        state = items.CommitPlacement(item, placement, owner, parent, slot, location, condition, payload);
        PublishDelta(state);
        return true;
    }

    internal void Sample(ulong item, ItemLocation location, float condition)
    {
        if (items.TrySample(item, location, condition, clock.Now, out ItemState state, out bool reliable))
        {
            Publish(state, reliable);
        }
    }

    internal void Remove(ulong item)
    {
        if (items.TryRemove(item, out ItemState state))
        {
            PublishDelta(state);
        }
    }

    internal bool TryCommitPayload(ulong item, uint revision, float condition, byte[] payload, out ItemState state)
    {
        if (!items.TryCommitPayload(item, revision, condition, payload, out state))
        {
            return false;
        }
        PublishDelta(state);
        return true;
    }

    internal ItemDecision ExecuteLocal(SharedItemRequest request)
    {
        return ExecuteForParticipant(peers.LocalParticipant, request);
    }

    internal ItemDecision ExecuteForParticipant(ParticipantId player, SharedItemRequest request)
    {
        ItemDecision decision = operations.Execute(player, request, out ItemState? change);
        PublishChange(change);
        return decision;
    }

    internal bool Receive(ulong sender, byte[] frame, bool worldReady)
    {
        ParticipantId player = peers.Identify(sender);
        if (player.IsNone || player == peers.LocalParticipant || frame.Length == 0)
        {
            return false;
        }

        MessageKind kind = (MessageKind)frame[0];
        switch (kind)
        {
            case MessageKind.ItemRequest:
                if (worldReady && ItemWire.TryReadRequest(frame, peers.SessionId, out SharedItemRequest request))
                {
                    if (crafts.IsReserved(request.ItemId))
                    {
                        peers.Send(sender, ItemWire.WriteResult(peers.SessionId, request.Operation, ItemDecision.Conflict));
                        return true;
                    }
                    if (operations.TryHandle(player, request, out var receipt, out var change, out bool replay))
                    {
                        PublishChange(change);
                        if (items.TryGet(receipt.ItemId, out ItemState state))
                        {
                            peers.Send(sender, Encode(state, reliable: true));
                        }
                        peers.Send(sender, ItemWire.WriteResult(peers.SessionId, receipt.Operation, receipt.Decision));
                        OperationResponded?.Invoke(request, receipt.Decision, replay);
                    }
                }
                return true;
            case MessageKind.ItemSyncRequest:
                if (ItemSnapshotWire.TryReadRequest(frame, peers.SessionId, out ulong snapshotId))
                {
                    snapshots.Request(player, snapshotId);
                }
                return true;
            case MessageKind.ItemRegister:
                ReceiveRegistration(sender, player, frame);
                return true;
            case MessageKind.ItemStateReport:
                ReceiveStateReport(sender, player, frame);
                return true;
            case MessageKind.ItemCraftRequest:
                ReceiveCraftRequest(sender, player, frame);
                return true;
            case MessageKind.ItemCraftCommit:
                ReceiveCraftCommit(sender, player, frame);
                return true;
            default:
                return false;
        }
    }
    internal void TickSnapshots()
    {
        crafts.Tick();
        snapshots.SendAvailable();
    }

    private void OnPeerChanged(ulong address, bool verified)
    {
        if (!verified)
        {
            snapshots.Forget(peers.Identify(address));
            crafts.Forget(peers.Identify(address));
        }
    }

    public void Dispose() => peers.ConnectionChanged -= OnPeerChanged;

    private void PublishChange(ItemState? change)
    {
        if (change is { } state)
        {
            PublishDelta(state);
        }
    }

    private void PublishDelta(ItemState state)
    {
        peers.Broadcast(
        ItemSnapshotWire.WriteState(MessageKind.ItemDelta, peers.SessionId, state.Sequence, state.ToWire(peers)), reliable: true);
    }

    private void Publish(ItemState state, bool reliable) => peers.Broadcast(Encode(state, reliable), reliable);

    private byte[] Encode(ItemState state, bool reliable) =>
        ItemWire.WriteState(reliable ? MessageKind.ItemState : MessageKind.ItemMotion, peers.SessionId,
            reliable ? state.ToWire(peers) : state.ToMotionWire(peers));

    private void ReceiveCraftRequest(ulong sender, ParticipantId player, byte[] frame)
    {
        if (!ItemCraftWire.TryReadRequest(frame, peers.SessionId, out ItemCraftRequest request))
        {
            return;
        }
        crafts.TryReserve(player, request, world.HasRecipe, world.CheckCraftIngredient, out ItemDecision decision);
        CraftStateChanged?.Invoke();
        peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, request.Operation, request.Recipe, decision));
    }

    private void ReceiveCraftCommit(ulong sender, ParticipantId player, byte[] frame)
    {
        if (!ItemCraftWire.TryReadCommit(frame, peers.SessionId, out ItemCraftCommit commit))
        {
            return;
        }
        if (!crafts.TryValidateCommit(player, commit, out _) ||
            items.Count + commit.Created.Count - commit.Removed.Count > AuthoritativeItemRegistry.Limit)
        {
            crafts.Complete(player, commit.Operation, ItemDecision.Conflict);
            peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
                ItemDecision.Conflict));
            return;
        }

        var changes = new List<ItemState>();
        var created = new List<(ulong ClientKey, ItemState State)>();
        foreach (ulong id in commit.Removed)
        {
            if (items.TryRemove(id, out ItemState removed))
            {
                changes.Add(removed);
                world.RemoveItem(id);
            }
        }
        foreach (CraftUpdatedItem update in commit.Updated)
        {
            if (items.TryCommitPayload(update.Id, update.Revision, update.Condition, update.Payload, out ItemState state))
            {
                changes.Add(state);
            }
        }
        foreach (CraftCreatedItem item in commit.Created)
        {
            ParticipantId owner = item.Placement is ItemPlacement.Inventory or ItemPlacement.Worn ? player : default;
            if (items.TryRegister(item.Definition, item.Placement, owner, item.Parent, item.Slot, item.Location,
                1f, item.Payload, item.ClientKey, out ItemState state))
            {
                changes.Add(state);
                created.Add((item.ClientKey, state));
            }
        }
        foreach (ItemState state in changes) PublishDelta(state);
        foreach (var item in created)
        {
            peers.Send(sender, ItemWire.WriteRegistered(peers.SessionId, commit.Operation, item.ClientKey,
                item.State.Id, item.State.Revision));
        }
        crafts.Complete(player, commit.Operation, ItemDecision.Accepted);
        CraftStateChanged?.Invoke();
        peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
            ItemDecision.Accepted));
    }
    private void ReceiveRegistration(ulong sender, ParticipantId player, byte[] frame)
    {
        if (!ItemWire.TryReadRegistration(frame, peers.SessionId, out SharedItemRegistration registration))
        {
            return;
        }
        if (registrations.TryGetValue(player, out var previous) && registration.ClientKey == previous.ClientKey)
        {
            peers.Send(sender, ItemWire.WriteRegistered(peers.SessionId, registration.Operation, registration.ClientKey,
                previous.Item, previous.Revision));
            return;
        }
        if (registration.Parent != 0 && (!items.TryGet(registration.Parent, out ItemState parent) || parent.Removed))
        {
            return;
        }

        ParticipantId owner = registration.Placement is ItemPlacement.Inventory or ItemPlacement.Worn ? player : default;
        if (!items.TryRegister(registration.Definition, registration.Placement, owner, registration.Parent,
            registration.Slot, registration.Location, 1f, registration.Payload, registration.ClientKey, out ItemState state))
        {
            return;
        }
        registrations[player] = (registration.ClientKey, state.Id, state.Revision);
        peers.Send(sender, ItemWire.WriteRegistered(peers.SessionId, registration.Operation, registration.ClientKey,
            state.Id, state.Revision));
        PublishDelta(state);
    }

    private void ReceiveStateReport(ulong sender, ParticipantId player, byte[] frame)
    {
        if (!ItemWire.TryReadReport(frame, peers.SessionId, out SharedItemStateReport report) ||
            crafts.IsReserved(report.ItemId) || !items.TryGet(report.ItemId, out ItemState state) || state.Removed)
        {
            return;
        }
        if (state.Owner != player)
        {
            return;
        }
        if (!TryCommitPayload(report.ItemId, report.Revision, report.Condition, report.Payload, out _) &&
            items.TryGet(report.ItemId, out ItemState current))
        {
            peers.Send(sender, Encode(current, reliable: true));
        }
    }
}
