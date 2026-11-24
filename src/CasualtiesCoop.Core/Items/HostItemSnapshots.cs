using System.Collections.Generic;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core.Items;

internal sealed class HostItemSnapshots
{
    internal const int SendBudget = 12;

    private sealed class Transfer
    {
        internal ulong Id;
        internal ItemState[]? States;
        internal ulong Sequence;
        internal int Next;
        internal bool Complete;
    }

    private readonly AuthoritativeItemRegistry items;
    private readonly SessionPeers peers;
    private readonly Dictionary<ParticipantId, Transfer> transfers = new();
    private readonly bool[] blocked;
    private int nextSeat;

    internal HostItemSnapshots(AuthoritativeItemRegistry items, SessionPeers peers)
    {
        this.items = items;
        this.peers = peers;
        blocked = new bool[peers.Players.Count];
    }

    internal void Request(ParticipantId player, ulong snapshotId)
    {
        if (!transfers.TryGetValue(player, out Transfer previous) || previous.Id < snapshotId)
        {
            transfers[player] = new Transfer { Id = snapshotId };
        }
    }

    internal void SendAvailable()
    {
        System.Array.Clear(blocked, 0, blocked.Length);
        int attempts = 0;
        int idleSeats = 0;
        while (attempts < SendBudget && idleSeats < blocked.Length)
        {
            int index = nextSeat;
            nextSeat = (index + 1) % blocked.Length;
            var seat = new ParticipantId(index + 1);
            ulong address = peers.AddressOf(seat);
            if (blocked[index] || !transfers.TryGetValue(seat, out Transfer transfer) || transfer.Complete || !peers.IsVerified(address))
            {
                idleSeats++;
                continue;
            }

            attempts++;
            idleSeats = 0;
            blocked[index] = !SendNext(address, transfer);
        }
    }

    internal void Forget(ParticipantId player) => transfers.Remove(player);

    private bool SendNext(ulong address, Transfer transfer)
    {
        ItemState[] states = transfer.States ??= items.CaptureSnapshot(out transfer.Sequence);
        bool done = transfer.Next == states.Length;
        byte[] frame = done
            ? ItemSnapshotWire.WriteDone(peers.SessionId, transfer.Id, transfer.Sequence, (ushort)states.Length)
            : ItemSnapshotWire.WriteState(MessageKind.ItemSnapshotEntry, peers.SessionId, transfer.Id, states[transfer.Next].ToWire(peers));
        if (!peers.Send(address, frame))
        {
            return false;
        }

        if (done)
        {
            transfer.Complete = true;
            transfer.States = null;
        }
        else
        {
            transfer.Next++;
        }
        return true;
    }
}
