using CasualtiesCoop.Core;
using CasualtiesCoop.Gameplay.Fluids;
using CasualtiesCoop.Networking;

internal static class FluidChecks
{
    private const ulong Host = 1;
    private const ulong Guest = 2;
    private static readonly Guid Session = new("14a8e43f-b3eb-46da-91f4-fede655e7630");

    internal static void Run() => ConcurrentDrinkAndReplay();

    private static void ConcurrentDrinkAndReplay()
    {
        const ulong secondGuest = 3;
        var network = new SimulatedNetwork(73);
        var host = network.AddPeer(Host);
        var first = network.AddPeer(Guest);
        var second = network.AddPeer(secondGuest);
        var authority = new FluidDrinkAuthority(network.Clock);
        byte available = 2;
        int consumed = 0;
        List<(ulong Guest, ulong Operation, byte Liquid)> replies = new();
        first.ReceiveFrame = (_, frame) => ReadResult(Guest, frame);
        second.ReceiveFrame = (_, frame) => ReadResult(secondGuest, frame);
        host.ReceiveFrame = (peer, frame) =>
        {
            if (FluidWire.TryReadDrink(frame, MessageKind.FluidDrinkRequest, Session, out ulong operation, out int x,
                    out int y, out byte expected) &&
                authority.TryDecide(peer, operation, x, y, expected, () =>
                {
                    if (available != expected)
                    {
                        return false;
                    }
                    available = 0;
                    consumed++;
                    return true;
                }, out FluidDrinkAuthority.Receipt receipt, out _))
            {
                host.TrySend(peer, FluidWire.WriteDrink(MessageKind.FluidDrinkResult, Session, receipt.Operation,
                    receipt.X, receipt.Y, receipt.Liquid), reliable: true);
            }
            return true;
        };
        network.SetConnected(Host, Guest, true);
        network.SetConnected(Host, secondGuest, true);
        byte[] request = FluidWire.WriteDrink(MessageKind.FluidDrinkRequest, Session, 1, 13, 18, 2);
        first.TrySend(Host, request, reliable: true);
        second.TrySend(Host, request, reliable: true);
        network.Advance(0);
        Check.That(consumed == 1 && replies.SequenceEqual(new[] { (Guest, 1ul, (byte)2), (secondGuest, 1ul, (byte)0) }),
            "Two guests contesting a cell obtain one accepted drink.");

        available = 2;
        first.TrySend(Host, request, reliable: true);
        first.TrySend(Host, FluidWire.WriteDrink(MessageKind.FluidDrinkRequest, Session, 2, 13, 18, 2), reliable: true);
        network.Advance(0);
        Check.That(consumed == 1 && available == 2 && replies[^2] == (Guest, 1ul, (byte)2) &&
            replies[^1] == (Guest, 2ul, (byte)0), "A repeated drink replays its receipt and cooldown protects a new operation.");
        network.Advance(0.65f);
        first.TrySend(Host, FluidWire.WriteDrink(MessageKind.FluidDrinkRequest, Session, 3, 13, 18, 2), reliable: true);
        network.Advance(0);
        Check.That(consumed == 2 && authority.LastOperation(Guest) == 3,
            "Managed time releases the drink cooldown at its deadline.");

        bool ReadResult(ulong peer, byte[] frame)
        {
            if (FluidWire.TryReadDrink(frame, MessageKind.FluidDrinkResult, Session, out ulong operation, out _, out _, out byte liquid))
            {
                replies.Add((peer, operation, liquid));
            }
            return true;
        }
    }
}
