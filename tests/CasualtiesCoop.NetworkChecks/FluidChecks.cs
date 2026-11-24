using CasualtiesCoop.Core;
using CasualtiesCoop.Gameplay.Fluids;
using CasualtiesCoop.Networking;

internal static class FluidChecks
{
    private const ulong Host = 1;
    private const ulong Guest = 2;
    private static readonly Guid Session = new("14a8e43f-b3eb-46da-91f4-fede655e7630");

    internal static void Run()
    {
        LostUpdateAndRepair();
        InterestChanges();
        ConcurrentDrinkAndReplay();
    }

    private static void LostUpdateAndRepair()
    {
        var network = new SimulatedNetwork(67, new NetworkProfile(UnreliableLoss: 1));
        var host = network.AddPeer(Host);
        var guest = network.AddPeer(Guest);
        var schedule = new FluidChunkSchedule();
        var progress = new FluidChunkProgress();
        var key = (X: 1, Y: 1);
        uint revision = 1;
        byte hostLiquid = 2;
        byte replicaLiquid = 0;
        guest.ReceiveFrame = (_, frame) =>
        {
            if (FluidWire.TryReadChunk(frame, Session, out int x, out int y, out uint revision, out int offset) &&
                progress.TryObserve((x, y), revision) && (x, y) == key)
            {
                replicaLiquid = FluidWire.Unpack(frame.AsSpan(offset), 0);
            }
            return true;
        };
        network.SetConnected(Host, Guest, true);

        SendPlanned();
        Check.That(replicaLiquid == 2 && progress.HasCurrent(key), "The first fluid snapshot is reliable.");
        network.Advance(0.25f);
        revision++;
        hostLiquid = 3;
        SendPlanned();
        Check.That(replicaLiquid == 2, "The intermediate fluid update can be lost.");

        network.Advance(2.25f);
        SendPlanned();
        Check.That(replicaLiquid == 3, "A reliable full update repairs the lost chunk.");
        progress.RequireFreshSnapshots();
        Check.That(!progress.HasCurrent(key), "Recovery requires a newly observed snapshot.");
        SendChunk(key, 1, 2, reliable: true);
        network.Advance(0);
        Check.That(replicaLiquid == 3 && !progress.HasCurrent(key), "Old revisions neither replace state nor satisfy recovery.");

        schedule.Forget(Guest);
        SendPlanned();
        Check.That(replicaLiquid == 3 && progress.HasCurrent(key), "A current revision can satisfy a fresh recovery snapshot.");

        void SendPlanned()
        {
            float now = network.Clock.Now;
            foreach (FluidChunkSchedule.Update update in schedule.Plan(Guest, 48, 48, 96, 96, now,
                current => current == key ? revision : 1))
            {
                if (SendChunk(update.Key, update.Revision, update.Key == key ? hostLiquid : (byte)0, update.Reliable))
                {
                    schedule.RecordAccepted(Guest, update, now);
                }
            }
            network.Advance(0);
        }

        bool SendChunk((int X, int Y) at, uint version, byte value, bool reliable)
        {
            byte[] packed = new byte[FluidWire.PackedSize];
            FluidWire.Pack(packed, 0, value);
            return host.TrySend(Guest, FluidWire.WriteChunk(Session, at.X, at.Y, version, packed), reliable);
        }
    }

    private static void InterestChanges()
    {
        var schedule = new FluidChunkSchedule();
        var key = (X: 1, Y: 1);
        var initial = schedule.Plan(Guest, 48, 48, 512, 128, 0, _ => 1);
        Check.That(initial.Count == 12 && initial[0].Key == key && initial.All(update => update.Reliable),
            "Interest starts with the nearest chunk and respects the reliable send budget.");
        foreach (var update in initial)
        {
            if (update.Key != key)
            {
                schedule.RecordAccepted(Guest, update, 0);
            }
        }

        var retry = schedule.Plan(Guest, 48, 48, 512, 128, 0.1f, _ => 1);
        Check.That(retry.Any(update => update.Key == key && update.Reliable), "An unaccepted send remains eligible.");
        foreach (var update in retry)
        {
            schedule.RecordAccepted(Guest, update, 0.1f);
        }
        Check.That(!schedule.Plan(Guest, 48, 48, 512, 128, 0.2f, _ => 1).Any(update => update.Key == key),
            "An accepted unchanged chunk does not consume bandwidth again before repair.");
        schedule.Plan(Guest, 460, 48, 512, 128, 0.3f, _ => 1);
        Check.That(schedule.Plan(Guest, 48, 48, 512, 128, 0.4f, _ => 1).Any(update => update.Key == key && update.Reliable),
            "Returning to an expired area makes its first update reliable again.");
    }

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
