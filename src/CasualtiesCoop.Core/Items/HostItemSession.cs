using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly Dictionary<ParticipantId, Dictionary<ulong, RegistrationReceipt>> registrations = new();

    private readonly struct RegistrationReceipt
    {
        internal readonly ulong ClientKey;
        internal readonly ulong Item;
        internal readonly uint Revision;

        internal RegistrationReceipt(ulong clientKey, ulong item, uint revision)
        {
            ClientKey = clientKey;
            Item = item;
            Revision = revision;
        }
    }

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
    internal event Action<string>? CraftFailure;

    internal bool HasCapacity => items.HasCapacity;
    internal bool TryGet(ulong item, out ItemState state) => items.TryGet(item, out state);
    internal ItemState[] Snapshot() => items.CaptureSnapshot(out _);
    internal ulong RootId(ulong id) => items.RootId(id);
    internal bool IsCraftReserved(ulong id) => crafts.IsReserved(id);
    internal bool SlotOccupied(ParticipantId player, byte slot) => items.Occupied(player, slot);
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
        if (parent != 0 && IsItemConflict(parent))
        {
            return false;
        }
        if (!items.TryRegister(definition, placement, owner, parent, slot, location, condition, payload, clientKey,
                out state))
        {
            return false;
        }
        PublishDelta(state);
        return true;
    }

    internal bool TryCommitPlacement(ulong item, ItemPlacement placement, ParticipantId owner, ulong parent, byte slot,
        ItemLocation location, float condition, byte[] payload, out ItemState state)
    {
        state = default;
        if (!items.TryGet(item, out _) || IsItemConflict(item))
        {
            return false;
        }
        state = items.CommitPlacement(item, placement, owner, parent, slot, location, condition, payload);
        PublishDelta(state);
        return true;
    }

    internal void Sample(ulong item, ItemLocation location, float condition)
    {
        if (!IsItemConflict(item) && items.TrySample(item, location, condition, clock.Now, out ItemState state,
                out bool reliable))
        {
            Publish(state, reliable);
        }
    }

    internal void Remove(ulong item)
    {
        if (!IsItemConflict(item) && items.TryRemove(item, out ItemState state))
        {
            PublishDelta(state);
        }
    }

    internal bool TryCommitPayload(ulong item, uint revision, float condition, byte[] payload, out ItemState state)
    {
        state = default;
        if (IsItemConflict(item) || !items.TryCommitPayload(item, revision, condition, payload, out state))
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
        if (IsItemConflict(request.ItemId))
        {
            return ItemDecision.Conflict;
        }
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
                    if (IsItemConflict(request.ItemId))
                    {
                        peers.Send(sender, ItemWire.WriteResult(peers.SessionId, request.Operation,
                            ItemDecision.Conflict));
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
            case MessageKind.ItemCraftCancel:
                ReceiveCraftCancel(player, frame);
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
            ParticipantId player = peers.Identify(address);
            snapshots.Forget(player);
            crafts.Forget(player);
        }
    }

    public void Dispose() => peers.ConnectionChanged -= OnPeerChanged;

    private bool IsItemConflict(ulong id) => crafts.IsReserved(id) || HasReservedAncestor(id) || HasReservedDescendant(id);

    private bool HasReservedDescendant(ulong parent)
    {
        foreach (ItemState state in items.CaptureSnapshot(out _))
        {
            if (state.Parent == 0 || !crafts.IsReserved(state.Id))
            {
                continue;
            }
            ulong current = state.Parent;
            for (int depth = 0; depth < 32 && current != 0; depth++)
            {
                if (current == parent)
                {
                    return true;
                }
                current = items.TryGet(current, out ItemState ancestor) ? ancestor.Parent : 0;
            }
        }
        return false;
    }

    private bool HasReservedAncestor(ulong id)
    {
        for (int depth = 0; depth < 32 && items.TryGet(id, out ItemState state); depth++)
        {
            if (state.Parent == 0)
            {
                return false;
            }
            if (crafts.IsReserved(state.Parent))
            {
                return true;
            }
            id = state.Parent;
        }
        return false;
    }

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
            ItemSnapshotWire.WriteState(MessageKind.ItemDelta, peers.SessionId, state.Sequence, state.ToWire(peers)),
            reliable: true);
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
        CraftRecipePlan? plan = world.TryGetRecipe(request.Recipe, out CraftRecipePlan? found) ? found : null;
        crafts.TryReserve(player, request, plan, world.CheckCraftIngredient, IsItemConflict, out ItemDecision decision);
        CraftStateChanged?.Invoke();
        peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, request.Operation, request.Recipe, decision));
    }

    private void ReceiveCraftCommit(ulong sender, ParticipantId player, byte[] frame)
    {
        if (!ItemCraftWire.TryReadCommit(frame, peers.SessionId, out ItemCraftCommit commit))
        {
            return;
        }

        if (crafts.TryGetResult(player, commit.Operation, out CraftOperationRecord previous))
        {
            peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
                previous.Decision));
            foreach (CraftCreatedResult prior in previous.Created)
            {
                peers.Send(sender, ItemWire.WriteRegistered(peers.SessionId, commit.Operation, prior.ClientKey,
                    prior.Item, prior.Revision));
            }
            return;
        }

        if (!crafts.TryGetReservation(player, commit.Operation, out CraftRecipePlan? plan, out _,
                out ItemDecision reservationDecision) || plan is null)
        {
            peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
                reservationDecision));
            return;
        }
        if (!crafts.TryValidateCommit(player, commit, plan, out ItemDecision validation))
        {
            CraftFailure?.Invoke($"validation failed for operation {commit.Operation}: {crafts.LastFailure}");
            crafts.Complete(player, commit.Operation, ItemDecision.Conflict);
            CraftStateChanged?.Invoke();
            peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
                ItemDecision.Conflict));
            return;
        }

        if (!items.HasCapacityFor(commit.Created.Count) || commit.Created.Any(value => IsClientKeyUsed(player, value.ClientKey)))
        {
            CraftFailure?.Invoke($"capacity or client key failed for operation {commit.Operation}.");
            crafts.Complete(player, commit.Operation, ItemDecision.Unavailable);
            CraftStateChanged?.Invoke();
            peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
                ItemDecision.Unavailable));
            return;
        }
        if (!ValidateCreatedPlacement(player, commit) ||
            !crafts.TryGetIngredients(player, commit.Operation, out ItemState[] ingredients))
        {
            CraftFailure?.Invoke($"result placement or ingredient snapshot failed for operation {commit.Operation}.");
            crafts.Complete(player, commit.Operation, ItemDecision.Conflict);
            CraftStateChanged?.Invoke();
            peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
                ItemDecision.Conflict));
            return;
        }

        crafts.MarkExecuting(player, commit.Operation);
        var changes = new List<ItemState>();
        var created = new List<CraftCreatedResult>();
        ICraftWorldTransaction? native = null;
        AuthoritativeItemRegistry.Transaction registry = items.BeginTransaction();
        try
        {
            native = world.BeginCraft(commit, plan, ingredients);
            if (native is null) throw new InvalidOperationException();
            if (!native.TryApply()) throw new InvalidOperationException();
            foreach (ulong id in commit.Removed)
            {
                if (!items.TryRemove(id, out ItemState removed))
                {
                    throw new InvalidOperationException("Craft removal lost its authoritative item.");
                }
                changes.Add(removed);
            }
            foreach (CraftUpdatedItem update in commit.Updated)
            {
                if (!items.TryCommitCraftUpdate(update, player, out ItemState state))
                {
                    throw new InvalidOperationException("Craft update lost its authoritative revision.");
                }
                changes.Add(state);
            }
            foreach (CraftCreatedItem result in commit.Created)
            {
                ParticipantId owner = result.Placement is ItemPlacement.Inventory or ItemPlacement.Worn
                    ? player : default;
                if (!items.TryRegister(result.Definition, result.Placement, owner, result.Parent, result.Slot, result.Location,
                        result.Condition, result.Payload, result.ClientKey, out ItemState state))
                {
                    throw new InvalidOperationException("Craft result could not enter the authoritative registry.");
                }
                changes.Add(state);
                created.Add(new CraftCreatedResult(result.ClientKey, state.Id, state.Revision));
            }
            if (!native.Bind(created, commit.Created)) throw new InvalidOperationException();
            registry.Commit();
            native.Confirm();
        }
        catch (Exception exception)
        {
            CraftFailure?.Invoke($"native/registry commit failed for operation {commit.Operation}: {exception}");
            registry.Rollback();
            native?.Dispose();
            crafts.Complete(player, commit.Operation, ItemDecision.Unavailable);
            CraftStateChanged?.Invoke();
            peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
                ItemDecision.Unavailable));
            return;
        }

        native.Dispose();
        foreach (CraftCreatedResult result in created) RecordRegistration(player, result.ClientKey, result.Item, result.Revision);
        foreach (ItemState state in changes) PublishDelta(state);
        foreach (CraftCreatedResult item in created)
        {
            peers.Send(sender, ItemWire.WriteRegistered(peers.SessionId, commit.Operation, item.ClientKey, item.Item,
                item.Revision));
        }
        crafts.Complete(player, commit.Operation, ItemDecision.Accepted, created);
        CraftStateChanged?.Invoke();
        peers.Send(sender, ItemCraftWire.WriteResult(peers.SessionId, commit.Operation, commit.Recipe,
            ItemDecision.Accepted));
    }

    private void ReceiveCraftCancel(ParticipantId player, byte[] frame)
    {
        if (!ItemCraftWire.TryReadCancel(frame, peers.SessionId, out ulong operation, out int recipe)) return;
        crafts.Cancel(player, operation, recipe);
        CraftStateChanged?.Invoke();
    }

    private bool ValidateCreatedPlacement(ParticipantId player, ItemCraftCommit commit)
    {
        foreach (CraftCreatedItem created in commit.Created)
        {
            ItemState? parent = null;
            if (created.Parent != 0)
            {
                if (!items.TryGet(created.Parent, out ItemState state) || state.Removed || IsItemConflict(created.Parent))
                    return false;
                parent = state;
            }
            var registration = new SharedItemRegistration
            {
                Operation = commit.Operation, ClientKey = created.ClientKey, Definition = created.Definition,
                Placement = created.Placement, Parent = created.Parent, Slot = created.Slot,
                Location = created.Location, Condition = created.Condition, Payload = created.Payload
            };
            if (world.CheckRegistration(player, registration, parent) != ItemDecision.Accepted) return false;
        }
        return true;
    }

    private bool IsClientKeyUsed(ParticipantId player, ulong clientKey) =>
        registrations.TryGetValue(player, out Dictionary<ulong, RegistrationReceipt>? known) && known.ContainsKey(clientKey);

    private void RecordRegistration(ParticipantId player, ulong clientKey, ulong item, uint revision)
    {
        if (!registrations.TryGetValue(player, out Dictionary<ulong, RegistrationReceipt>? history))
            registrations[player] = history = new Dictionary<ulong, RegistrationReceipt>();
        if (history.Count >= 32)
        {
            ulong oldest = 0;
            foreach (ulong key in history.Keys) { oldest = key; break; }
            if (oldest != 0) history.Remove(oldest);
        }
        history[clientKey] = new RegistrationReceipt(clientKey, item, revision);
    }

    private void ReceiveRegistration(ulong sender, ParticipantId player, byte[] frame)
    {
        if (!ItemWire.TryReadRegistration(frame, peers.SessionId, out SharedItemRegistration registration))
        {
            return;
        }
        if (registrations.TryGetValue(player, out Dictionary<ulong, RegistrationReceipt>? known) &&
            known.TryGetValue(registration.ClientKey, out RegistrationReceipt previous))
        {
            peers.Send(sender, ItemWire.WriteRegistered(peers.SessionId, registration.Operation, registration.ClientKey,
                previous.Item, previous.Revision));
            return;
        }

        ItemState? parent = null;
        if (registration.Parent != 0)
        {
            if (!items.TryGet(registration.Parent, out ItemState foundParent) || foundParent.Removed ||
                IsItemConflict(registration.Parent))
            {
                return;
            }
            parent = foundParent;
        }
        if (!world.IsKnownDefinition(registration.Definition) ||
            world.CheckRegistration(player, registration, parent) != ItemDecision.Accepted)
        {
            return;
        }

        ParticipantId owner = registration.Placement is ItemPlacement.Inventory or ItemPlacement.Worn ? player : default;
        if (!items.TryRegister(registration.Definition, registration.Placement, owner, registration.Parent,
                registration.Slot, registration.Location, registration.Condition, registration.Payload,
                registration.ClientKey, out ItemState state))
        {
            return;
        }
        RecordRegistration(player, registration.ClientKey, state.Id, state.Revision);
        peers.Send(sender, ItemWire.WriteRegistered(peers.SessionId, registration.Operation, registration.ClientKey,
            state.Id, state.Revision));
        PublishDelta(state);
    }

    private void ReceiveStateReport(ulong sender, ParticipantId player, byte[] frame)
    {
        if (!ItemWire.TryReadReport(frame, peers.SessionId, out SharedItemStateReport report) ||
            IsItemConflict(report.ItemId) || !items.TryGet(report.ItemId, out ItemState state) || state.Removed)
        {
            return;
        }
        if (!items.OwnedBy(report.ItemId, player))
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
