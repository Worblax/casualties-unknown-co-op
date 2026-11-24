using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

internal static class HostItemChecks
{
    private const ulong HostAddress = 900;
    private const ulong GuestAddress = 340;
    private static readonly Guid Session = new("b4e4c313-6e70-4ef5-b1a1-b65cc53bf538");

    internal static void Run()
    {
        var network = new SimulatedNetwork(71);
        var plan = new RoomLaunchPlan(Session, Guid.NewGuid(), "normal", RoomOptions.Sandbox,
            new[] { new LaunchPlayer(GuestAddress, 0), new LaunchPlayer(HostAddress, 0) });
        using var hostPeers = new SessionPeers(network.AddPeer(HostAddress), plan, HostAddress, HostAddress);
        var guest = network.AddPeer(GuestAddress);
        var world = new ItemWorld();
        using var host = new HostItemSession(hostPeers, world, network.Clock);
        hostPeers.AttachReceiver((sender, frame) => host.Receive(sender, frame, worldReady: true));
        var states = new List<SharedItemState>();
        var receipts = new List<(ulong, ItemDecision)>();
        guest.ReceiveFrame = (sender, frame) =>
        {
            Check.That(sender == HostAddress, "Only the host publishes item results.");
            if (ItemWire.TryReadState(frame, MessageKind.ItemState, Session, out SharedItemState state) ||
                ItemSnapshotWire.TryReadState(frame, MessageKind.ItemDelta, Session, out _, out state))
            {
                states.Add(state);
            }
            if (ItemWire.TryReadResult(frame, Session, out ulong operation, out var decision))
            {
                receipts.Add((operation, decision));
            }
            return true;
        };
        network.SetConnected(GuestAddress, HostAddress, true);
        Check.That(host.TryRegister("stick", default, 1, out ulong first) &&
            host.TryRegister("rock", default, 1, out _), "Host allocates item identities.");
        Check.That(hostPeers.Identify(GuestAddress).Seat == 1 && hostPeers.LocalParticipant.Seat == 2 &&
            hostPeers.AddressOf(hostPeers.LocalParticipant) == HostAddress && hostPeers.Identify(999).IsNone,
            "Participant identity comes from the frozen roster and maps to the existing wire address.");
        network.Advance(0);
        ulong second = states.Single(state => state.Definition == "rock").Id;

        world.RejectPickup = true;
        Submit(1, first, 1, ItemAction.PickUp);
        Check.That(receipts[^1] == (1ul, ItemDecision.SlotOccupied) && world.Pickups == 1 &&
            host.TryGet(first, out var state) && state.Owner.IsNone && state.Revision == 1 &&
            states[^1].Owner == 0 && states[^1].Revision == 1, "Native pickup rejection leaves the registry and wire state unchanged.");

        world.RejectPickup = false;
        Submit(1, second, 1, ItemAction.PickUp);
        Check.That(receipts[^1] == (1ul, ItemDecision.SlotOccupied) && world.Pickups == 1 && states[^1].Id == first,
            "A repeated operation number replays the original item and decision, even with a different payload.");

        Submit(2, first, 1, ItemAction.PickUp);
        Check.That(receipts[^1] == (2ul, ItemDecision.Accepted) && host.TryGet(first, out state) &&
            state.Owner == hostPeers.Identify(GuestAddress) && state.Revision == 2 && states[^1].Owner == GuestAddress,
            "The registry commits only after native success and encodes the Steam-compatible wire owner.");

        Submit(3, second, 1, ItemAction.PickUp);
        Check.That(receipts[^1] == (3ul, ItemDecision.SlotOccupied) && world.Pickups == 2,
            "An occupied shared slot is rejected before executing another native pickup.");

        world.RejectDrop = true;
        Submit(4, first, 2, ItemAction.Drop);
        Check.That(receipts[^1] == (4ul, ItemDecision.Unavailable) && world.Drops == 1 &&
            host.TryGet(first, out state) && state.Owner == hostPeers.Identify(GuestAddress) && state.Revision == 2,
            "Native drop rejection preserves owner and revision.");

        world.RejectDrop = false;
        int before = receipts.Count;
        Submit(2, first, 2, ItemAction.Drop);
        Check.That(receipts.Count == before && world.Drops == 1, "An older operation has no response or native side effects.");
        Submit(5, first, 1, ItemAction.Drop);
        Check.That(receipts[^1] == (5ul, ItemDecision.Unavailable) && world.Drops == 1,
            "A stale revision is rejected before the native operation.");
        Submit(6, first, 2, ItemAction.Drop);
        Check.That(host.TryGet(first, out state) && state.Owner.IsNone && state.Slot == ItemWire.NoSlot &&
            state.Revision == 3 && host.LastOperation(GuestAddress) == 6, "Successful drop transfers ownership exactly once.");

        host.Remove(first);
        network.Advance(0);
        int stateFrames = states.Count;
        Submit(6, second, 1, ItemAction.Drop);
        Check.That(receipts[^1] == (6ul, ItemDecision.Accepted) && states.Count == stateFrames && world.Drops == 2,
            "Retirement does not discard the receipt or leak the replacement payload's state.");
        Submit(7, first, 3, ItemAction.PickUp);
        Check.That(receipts[^1] == (7ul, ItemDecision.Unavailable) && world.Pickups == 2,
            "A newer request targeting a retired item is rejected before vanilla execution.");

        void Submit(ulong operation, ulong id, uint revision, ItemAction action)
        {
            var request = new SharedItemRequest { ItemId = id, Revision = revision, Operation = operation, Action = action, Slot = 0 };
            Check.That(guest.TrySend(HostAddress, ItemWire.WriteRequest(Session, request), reliable: true), "Request queued.");
            network.Advance(0);
        }
    }

    internal sealed class ItemWorld : IItemWorld
    {
        internal int Pickups;
        internal int Drops;
        internal bool RejectPickup;
        internal bool RejectDrop;

        public ItemDecision CheckPickup(ParticipantId player, ulong item, byte slot, ItemLocation? previousPosition) =>
            ItemDecision.Accepted;

        public bool TryPickUp(ParticipantId player, ulong item, byte slot, out float condition)
        {
            Pickups++;
            condition = 1;
            return !RejectPickup;
        }

        public ItemDecision Drop(ParticipantId player, ulong item, SharedItemRequest request, out ItemLocation location, out float condition)
        {
            Drops++;
            location = request.Location;
            condition = 1;
            return RejectDrop ? ItemDecision.Unavailable : ItemDecision.Accepted;
        }
    }
}
