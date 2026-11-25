using System;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core.Items;

internal sealed class GuestItemSession : IDisposable
{
    private const float RetryInterval = 1f;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly ItemSnapshotSync snapshot;
    private readonly Func<ulong, bool> requestSnapshot;
    private ulong lastOperation;
    private ulong lastRegistrationOperation;
    private ulong nextClientKey;
    private ulong lastCraftOperation;
    private float nextResend;

    internal GuestItemSession(SessionPeers peers, IMonotonicClock clock)
    {
        this.peers = peers;
        this.clock = clock;
        snapshot = new ItemSnapshotSync(clock);
        requestSnapshot = RequestSnapshot;
        peers.ConnectionChanged += OnPeerChanged;
    }

    internal ItemReplicaStore Replicas { get; } = new();
    internal SharedItemRequest? Pending { get; private set; }
    internal bool Synced => snapshot.Synced;
    internal bool CanSubmit => Synced && Pending is null && lastOperation != ulong.MaxValue && peers.IsVerified(peers.Host);

    internal event Action<ItemState?, ItemState>? StateAccepted;
    internal event Action<SharedItemRequest, ItemDecision>? ResultAccepted;
    internal event Action<ulong, ulong, uint>? RegistrationAccepted;
    internal event Action<ulong, int, ItemDecision>? CraftResultAccepted;

    internal bool IsReserved(int slot) => Pending is { Action: ItemAction.PickUp } pickup && pickup.Slot == slot;

    internal bool TrySubmit(ulong item, ItemAction action, byte slot, ItemLocation at) =>
        TrySubmit(item, action, slot, at, Array.Empty<byte>());

    internal bool TrySubmit(ulong item, ItemAction action, byte slot, ItemLocation at, byte[] payload)
    {
        if (!CanSubmit || slot == ItemWire.NoSlot || !Replicas.TryGet(item, out ItemState state) || state.Removed ||
            !ItemWire.IsPayload(payload) ||
            (action == ItemAction.PickUp
                ? !state.Owner.IsNone
                : action is not (ItemAction.Drop or ItemAction.Throw) || state.Owner != peers.LocalParticipant || state.Slot != slot))
        {
            return false;
        }

        var request = new SharedItemRequest
        {
            Operation = ++lastOperation, ItemId = item, Revision = state.Revision, Action = action, Slot = slot,
            Location = at, Payload = payload
        };
        Pending = request;
        SendRequest(request);
        return true;
    }

    internal bool TryReportState(ulong item, uint revision, float condition, byte[] payload,
        ItemPlacement placement, ulong parent, byte slot, ItemLocation location)
    {
        if (!Synced || Pending is not null || revision == 0 || !ItemWire.IsPayload(payload) ||
            !Replicas.TryGet(item, out ItemState state) ||
            state.Removed || !EffectiveOwnedBy(item, peers.LocalParticipant))
        {
            return false;
        }
        var report = new SharedItemStateReport
        {
            Operation = ++lastRegistrationOperation, ItemId = item, Revision = revision, Condition = condition,
            Placement = placement, Parent = parent, Slot = slot, Location = location, Payload = payload
        };
        return peers.Send(peers.Host, ItemWire.WriteReport(peers.SessionId, report));
    }

    private bool EffectiveOwnedBy(ulong id, ParticipantId player)
    {
        for (int depth = 0; depth < 32 && Replicas.TryGet(id, out ItemState state); depth++)
        {
            if (state.Owner == player) return true;
            if (!state.Owner.IsNone || state.Parent == 0) return false;
            id = state.Parent;
        }
        return false;
    }

    internal ulong ReserveClientKey() => ++nextClientKey;

    internal bool TryRequestCraft(int recipe, ulong[] ids, uint[] revisions, out ulong operation)
    {
        operation = 0;
        if (!CanSubmit || recipe < 0 || ids is null || revisions is null || ids.Length != revisions.Length)
        {
            return false;
        }
        operation = ++lastCraftOperation;
        return peers.Send(peers.Host, ItemCraftWire.WriteRequest(peers.SessionId, new ItemCraftRequest
        {
            Operation = operation, Recipe = recipe, ItemIds = ids, Revisions = revisions
        }));
    }

    internal bool TryCommitCraft(ItemCraftCommit commit) =>
        Synced && commit.Operation != 0 && peers.Send(peers.Host, ItemCraftWire.WriteCommit(peers.SessionId, commit));

    internal bool TryResendCraft(ulong operation, int recipe, ulong[] ids, uint[] revisions)
    {
        if (!Synced || operation == 0 || operation != lastCraftOperation || recipe < 0 || ids is null ||
            revisions is null || ids.Length != revisions.Length)
            return false;
        return peers.Send(peers.Host, ItemCraftWire.WriteRequest(peers.SessionId, new ItemCraftRequest
        {
            Operation = operation, Recipe = recipe, ItemIds = ids, Revisions = revisions
        }));
    }

    internal bool TryCancelCraft(ulong operation, int recipe) =>
        Synced && operation != 0 && recipe >= 0 && peers.IsVerified(peers.Host) &&
        peers.Send(peers.Host, ItemCraftWire.WriteCancel(peers.SessionId, operation, recipe));
    internal bool TryRegister(SharedItemRegistration registration, out ulong clientKey)
    {
        clientKey = 0;
        if (!Synced || registration.ClientKey != 0 || !ItemWire.IsDefinition(registration.Definition) ||
            !ItemWire.IsPayload(registration.Payload) || nextClientKey == ulong.MaxValue)
        {
            return false;
        }
        registration.Operation = ++lastRegistrationOperation;
        registration.ClientKey = clientKey = ++nextClientKey;
        return peers.Send(peers.Host, ItemWire.WriteRegistration(peers.SessionId, registration));
    }

    internal bool TryResendRegistration(SharedItemRegistration registration)
    {
        if (!Synced || registration.ClientKey == 0 || lastRegistrationOperation == ulong.MaxValue) return false;
        registration.Operation = ++lastRegistrationOperation;
        return peers.Send(peers.Host, ItemWire.WriteRegistration(peers.SessionId, registration));
    }

    internal void Tick()
    {
        snapshot.Tick(requestSnapshot);
        if (snapshot.Pending == 0)
        {
            Replicas.CancelSnapshot();
        }
        if (Synced && Pending is { } request && clock.Now >= nextResend)
        {
            SendRequest(request);
        }
    }

    internal void InvalidateSnapshot() => snapshot.MarkOutOfSync();

    internal void SuspendForRecovery()
    {
        Pending = null;
        snapshot.Suspend();
        Replicas.CancelSnapshot();
    }

    internal void ResumeAfterOffer(ulong confirmedOperation, ulong confirmedCraft = 0,
        ulong confirmedRegistration = 0, ulong confirmedClientKey = 0)
    {
        lastOperation = Math.Max(lastOperation, confirmedOperation);
        lastCraftOperation = Math.Max(lastCraftOperation, confirmedCraft);
        lastRegistrationOperation = Math.Max(lastRegistrationOperation, confirmedRegistration);
        nextClientKey = Math.Max(nextClientKey, confirmedClientKey);
        Pending = null;
        snapshot.Resume();
        Replicas.CancelSnapshot();
    }

    internal bool Receive(ulong sender, byte[] frame)
    {
        if (sender != peers.Host || frame.Length == 0)
        {
            return false;
        }

        var kind = (MessageKind)frame[0];
        switch (kind)
        {
            case MessageKind.ItemState:
            case MessageKind.ItemMotion:
                if (ItemWire.TryReadState(frame, kind, peers.SessionId, out SharedItemState wire) &&
                    ItemState.TryFromWire(wire, peers, out ItemState state) &&
                    Replicas.ApplySample(state, kind == MessageKind.ItemMotion, out ItemState? previous))
                {
                    StateAccepted?.Invoke(previous, state);
                }
                return true;
            case MessageKind.ItemDelta:
                ReceiveDelta(frame);
                return true;
            case MessageKind.ItemSnapshotEntry:
                if (ItemSnapshotWire.TryReadState(frame, kind, peers.SessionId, out ulong id, out SharedItemState entry) &&
                    snapshot.Accepts(id) && ItemState.TryFromWire(entry, peers, out ItemState staged) && Replicas.Stage(staged))
                {
                    snapshot.RecordProgress();
                }
                return true;
            case MessageKind.ItemResult:
                if (ItemWire.TryReadResult(frame, peers.SessionId, out ulong operation, out ItemDecision decision) &&
                    Pending is { } pending && pending.Operation == operation)
                {
                    Pending = null;
                    ResultAccepted?.Invoke(pending, decision);
                }
                return true;
            case MessageKind.ItemCraftResult:
                if (ItemCraftWire.TryReadResult(frame, peers.SessionId, out ulong craft, out int recipe,
                    out ItemDecision craftDecision))
                {
                    CraftResultAccepted?.Invoke(craft, recipe, craftDecision);
                }
                return true;
            case MessageKind.ItemRegistered:
                if (ItemWire.TryReadRegistered(frame, peers.SessionId, out _, out ulong clientKey, out ulong item,
                    out uint revision))
                {
                    RegistrationAccepted?.Invoke(clientKey, item, revision);
                }
                return true;
            case MessageKind.ItemSyncDone:
                if (ItemSnapshotWire.TryReadDone(frame, peers.SessionId, out ulong snapshotId, out ulong sequence, out ushort count) &&
                    snapshot.Accepts(snapshotId))
                {
                    snapshot.Finish(Replicas.CompleteSnapshot(sequence, count));
                }
                return true;
            default:
                return false;
        }
    }

    private void ReceiveDelta(byte[] frame)
    {
        if (!ItemSnapshotWire.TryReadState(frame, MessageKind.ItemDelta, peers.SessionId, out ulong sequence, out SharedItemState wire) ||
            !ItemState.TryFromWire(wire, peers, out ItemState state))
        {
            return;
        }

        if (Replicas.ApplyDelta(sequence, state, out ItemState? previous, out bool gap))
        {
            StateAccepted?.Invoke(previous, state);
        }
        if (gap)
        {
            snapshot.MarkOutOfSync();
        }
    }

    private bool RequestSnapshot(ulong id)
    {
        if (!peers.Send(peers.Host, ItemSnapshotWire.WriteRequest(peers.SessionId, id)))
        {
            return false;
        }
        Replicas.BeginSnapshot();
        return true;
    }

    private void SendRequest(SharedItemRequest request)
    {
        nextResend = clock.Now + RetryInterval;
        peers.Send(peers.Host, ItemWire.WriteRequest(peers.SessionId, request));
    }

    private void OnPeerChanged(ulong address, bool verified)
    {
        if (address == peers.Host && !verified)
        {
            SuspendForRecovery();
        }
    }

    public void Dispose() => peers.ConnectionChanged -= OnPeerChanged;
}
