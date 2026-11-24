using System;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
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

    internal bool IsReserved(int slot) => Pending is { Action: ItemAction.PickUp } pickup && pickup.Slot == slot;

    internal bool TrySubmit(ulong item, ItemAction action, byte slot, ItemLocation at)
    {
        if (!CanSubmit || slot == ItemWire.NoSlot || !Replicas.TryGet(item, out ItemState state) || state.Removed ||
            (action == ItemAction.PickUp
                ? !state.Owner.IsNone
                : action is not (ItemAction.Drop or ItemAction.Throw) || state.Owner != peers.LocalParticipant || state.Slot != slot))
        {
            return false;
        }

        var request = new SharedItemRequest
        {
            Operation = ++lastOperation, ItemId = item, Revision = state.Revision, Action = action, Slot = slot, Location = at
        };
        Pending = request;
        SendRequest(request);
        return true;
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

    internal void SuspendForRecovery()
    {
        Pending = null;
        snapshot.Suspend();
        Replicas.CancelSnapshot();
    }

    internal void ResumeAfterOffer(ulong confirmedOperation)
    {
        lastOperation = Math.Max(lastOperation, confirmedOperation);
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
