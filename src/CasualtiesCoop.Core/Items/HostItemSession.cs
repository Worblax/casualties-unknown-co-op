using System;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core.Items;

internal sealed class HostItemSession : IDisposable
{
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly AuthoritativeItemRegistry items = new();
    private readonly HostItemOperations operations;
    private readonly HostItemSnapshots snapshots;

    internal HostItemSession(SessionPeers peers, IItemWorld world, IMonotonicClock clock)
    {
        this.peers = peers;
        this.clock = clock;
        operations = new HostItemOperations(items, world, clock);
        snapshots = new HostItemSnapshots(items, peers);
        peers.ConnectionChanged += OnPeerChanged;
    }

    internal event Action<SharedItemRequest, ItemDecision, bool>? OperationResponded;

    internal bool HasCapacity => items.HasCapacity;
    internal bool TryGet(ulong item, out ItemState state) => items.TryGet(item, out state);
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
                if (worldReady && ItemWire.TryReadRequest(frame, peers.SessionId, out SharedItemRequest request) &&
                    operations.TryHandle(player, request, out var receipt, out var change, out bool replay))
                {
                    PublishChange(change);
                    if (items.TryGet(receipt.ItemId, out ItemState state))
                    {
                        peers.Send(sender, Encode(state, reliable: true));
                    }
                    peers.Send(sender, ItemWire.WriteResult(peers.SessionId, receipt.Operation, receipt.Decision));
                    OperationResponded?.Invoke(request, receipt.Decision, replay);
                }
                return true;
            case MessageKind.ItemSyncRequest:
                if (ItemSnapshotWire.TryReadRequest(frame, peers.SessionId, out ulong snapshotId))
                {
                    snapshots.Request(player, snapshotId);
                }
                return true;
            default:
                return false;
        }
    }

    internal void TickSnapshots() => snapshots.SendAvailable();

    private void OnPeerChanged(ulong address, bool verified)
    {
        if (!verified)
        {
            snapshots.Forget(peers.Identify(address));
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

    private void PublishDelta(ItemState state) => peers.Broadcast(
        ItemSnapshotWire.WriteState(MessageKind.ItemDelta, peers.SessionId, items.Sequence, state.ToWire(peers)), reliable: true);

    private void Publish(ItemState state, bool reliable) => peers.Broadcast(Encode(state, reliable), reliable);

    private byte[] Encode(ItemState state, bool reliable) =>
        ItemWire.WriteState(reliable ? MessageKind.ItemState : MessageKind.ItemMotion, peers.SessionId, state.ToWire(peers));
}
