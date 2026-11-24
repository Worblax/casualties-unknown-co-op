using System.Buffers.Binary;
using CasualtiesCoop.Core.World;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

internal static class WorldChunkChecks
{
    private static readonly Guid Launch = new("ba57b556-79de-47c2-a682-986f82579db2");
    private static readonly Guid FirstStream = new("0ba86d33-4a36-4d86-8f22-45aeea7d1681");
    private static readonly Guid ReturnStream = new("3e1008fe-1b1b-435e-b310-7c9ddd48b433");
    private const int Width = 289, Height = 275;

    internal static void Run()
    {
        InterestAndHysteresis();
        SubscribeEditAndReenter();
        MovingBudgetMaintainsProgress();
        BackpressureAndThreeGuests();
        RecoverAfterManyChanges();
        RetryUntilApplied();
        InvalidCoordinatesAndRetry();
    }

    private static void InterestAndHysteresis()
    {
        var options = new WorldInterestOptions(24, 80, 64);
        var interest = new WorldInterest(273, 257, options);
        Check.That(interest.Update(63, 56, 45, 30) && interest.Ordered[0].Equals(WorldChunkKey.At(63, 56)),
            "Initial chunk priorities are nearest first.");
        int size = interest.Ordered.Count;
        for (int i = 0; i < 30; i++)
            Check.That(!interest.Update(63 + i % 2, 56, 45, 30) && interest.Ordered.Count == size,
                "Crossing a chunk edge within the spatial deadband does not rebuild.");
        Check.That(interest.Contains(WorldChunkKey.At(124, 56)), "Interest includes camera margin.");
        Check.That(interest.Update(207, 56, 45, 30) && !interest.Contains(WorldChunkKey.At(9, 56)),
            "Old area expires and new area is prefetched.");
        Check.That(interest.Update(272, 256, 512, 512) && interest.Contains(WorldChunkKey.At(272, 256)) &&
            interest.Ordered.All(k => k.StartX < 273 && k.StartY < 257 && k.X >= 0 && k.Y >= 0),
            "Camera hints are bounded and interest handles incomplete world edge.");
    }

    private static void SubscribeEditAndReenter()
    {
        using var run = new Scenario(1, new NetworkProfile(0.18f, 0.02f));
        run.WatchedKey = WorldChunkKey.At(31, 36);
        run.GuestGrid[0].Set(31, 36, 42, 36, 7);
        run.HostGrid.Set(31, 36, 1, 7, 2);
        run.Until(() => run.WatchedFrame is not null);
        Check.That(run.Value(0, 31, 36) == (42, 36f, 7), "The initial snapshot is still in flight when the host edits its exact chunk.");
        run.HostGrid.Set(31, 36, 3, 21, 1);
        run.Host.MarkDirty(WorldChunkKey.At(31, 36));
        run.Until(() => run.Matches(0, 31, 36));
        Check.That(run.Value(0, 31, 36) == (3, 21f, 1), "A newer full chunk replaces material, damage and liquid.");
        run.HostGrid.Set(31, 36, 0);
        run.Until(() => run.Matches(0, 31, 36));
        Check.That(run.Value(0, 31, 36) == (0, 0f, 0), "The sweep repairs removals and dries cells.");
        run.HostGrid.Set(31, 36, 5);
        run.Host.MarkDirty(WorldChunkKey.At(31, 36));
        run.Until(() => run.Matches(0, 31, 36));
        run.GuestGrid[0].Set(31, 36, 7, 33, 5);
        run.Until(() => run.Matches(0, 31, 36));
        Check.That(run.Host.Metrics.Repairs > 0, "Guest checksum comparison repairs local corruption without an edit on host.");

        run.Position[0] = (245, 224);
        run.HostGrid.Set(245, 224, 7, 0, 3);
        run.Until(() => run.Matches(0, 245, 224));
        Check.That(!run.Host.Interest(run.Peers[0].LocalParticipant)!.Contains(WorldChunkKey.At(31, 36)),
            "Far area has no old subscription.");
        run.HostGrid.Set(31, 36, 11, 2, 4);
        run.Position[0] = (32, 36);
        run.Until(() => run.Matches(0, 31, 36));
        run.HostPeers.Send(run.Peers[0].LocalPlayer, run.WatchedFrame!);
        run.Turn(20);
        Check.That(run.Value(0, 31, 36) == (11, 2f, 4), "An old sequence after resubscription cannot roll back a cell.");
    }

    private static void MovingBudgetMaintainsProgress()
    {
        using var run = new Scenario(1, new NetworkProfile(0.03f));
        run.Until(() => run.Host.Metrics.Subscriptions > 0 && run.Host.Metrics.Pending == 0);
        run.HostGrid.Set(82, 93, 2, liquid: 1);
        for (int step = 0; step < 64 && !run.Matches(0, 82, 93); step++)
        {
            run.Position[0] = (32 + 16 * (step / 2 % 2), 36);
            run.Turn(1);
        }
        Check.That(run.Matches(0, 82, 93), "Frequent anchor changes must not restart the sweep ahead of background edits.");
    }

    private static void BackpressureAndThreeGuests()
    {
        var cases = new[]
        {
            (Name: "idle-one", Guests: 1, Cross: false, Profile: new NetworkProfile(0.04f), Moves: false),
            (Name: "moving-one", Guests: 1, Cross: false, Profile: new NetworkProfile(0.04f), Moves: true),
            (Name: "idle-two-overlap", Guests: 2, Cross: true, Profile: new NetworkProfile(0.04f), Moves: false),
            (Name: "idle-three-overlap", Guests: 3, Cross: true, Profile: new NetworkProfile(0.04f), Moves: false),
            (Name: "moving-three-separated", Guests: 3, Cross: false, Profile: new NetworkProfile(0.04f), Moves: true),
            (Name: "three-restricted", Guests: 3, Cross: false, Profile: new NetworkProfile(0.25f, 0.1f,
                BytesPerSecond: 4800, QueueLimit: 18), Moves: true)
        };
        foreach (var item in cases)
        {
            using var run = new Scenario(item.Guests, item.Profile);
            for (int i = 1; i < item.Guests; i++)
                run.Position[i] = item.Cross ? (32 + i * 4, 36) : i == 1 ? (245, 224) : (239, 32);
            run.Until(() => run.Host.Metrics.Subscriptions > 0 && run.Host.Metrics.Pending == 0, seconds: 150);
            float initial = run.Network.Clock.Now;
            long initialBytes = run.Host.Metrics.Bytes;
            long initialFrames = run.Host.Metrics.Frames;
            int union = run.Host.Metrics.ActiveChunks;
            Check.That(item.Guests == 1 || run.Host.Metrics.Subscriptions >= union, "Only active interest contributes to union.");
            run.Turn(100);
            Check.That(run.Host.Metrics.Bytes == initialBytes, "Idle confirmed world produces no snapshot traffic.");
            if (item.Moves)
            {
                BrushMassDestruction(run);
                for (int tick = 0; tick < 60; tick++)
                {
                    for (int i = 0; i < item.Guests; i++)
                    {
                        (int x, int y) = run.Position[i];
                        if (tick % 15 == 0) run.Position[i] = (x + (i == 0 ? 8 : -8), y);
                        // Fluid layers churn while the host handles block edits.
                        for (int offset = 0; offset < 12; offset++)
                        {
                            run.HostGrid.Set(x + offset, y + 1, 0, liquid: (byte)(tick % 8));
                            run.HostGrid.Set(x + offset, y, tick % 5 == 0 ? (ushort)1 : (ushort)0);
                            if (tick % 5 == 0) run.Host.MarkDirty(WorldChunkKey.At(x + offset, y));
                        }
                    }
                    run.Turn(1);
                }
                run.Until(() => Enumerable.Range(0, item.Guests).All(i =>
                    run.Matches(i, run.Position[i].X, run.Position[i].Y) &&
                    run.Matches(i, run.Position[i].X, run.Position[i].Y + 1)) && run.Host.Metrics.Pending == 0, 140);
            }
            var stats = run.Host.Metrics;
            Console.WriteLine($"World chunks {item.Name} seed 81 | initial {initial:F2}s {initialBytes} B {initialFrames} frames | total {run.Network.Clock.Now:F2}s {stats.Bytes} B {stats.Frames} frames | union {union} | capture {Ms(stats.CaptureTicks):F1}ms encode {Ms(stats.EncodeTicks):F1}ms apply {Ms(run.Guests.Sum(g => g.Metrics.ApplyTicks)):F1}ms | rejected {stats.Backpressure}");
            if (item.Name == "three-restricted")
                Check.That(stats.Backpressure > 0 && run.Network.Clock.Now > initial, "Backpressure exercises eventual latest-state delivery.");
        }
    }

    private static void BrushMassDestruction(Scenario run)
    {
        // Distinct nonempty terrain is streamed first. Then 4097 cells are removed over bounded ticks.
        int left = 19, bottom = 23;
        for (int i = 0; i < 4097; i++)
            run.HostGrid.Set(left + i % 64, bottom + i / 64, 1, 1);
        run.Until(() => run.Matches(0, left, bottom) && run.Matches(0, left, bottom + 64), 100);
        for (int i = 0; i < 4097; i++)
        {
            int x = left + i % 64, y = bottom + i / 64;
            run.HostGrid.Set(x, y, 0);
            run.Host.MarkDirty(WorldChunkKey.At(x, y));
            if (i % 64 == 0) run.Turn(1);
        }
        run.Until(() => run.Matches(0, left, bottom + 64) && run.Host.Metrics.Pending == 0, 140);
    }

    private static double Ms(long ticks) => 1000d * ticks / System.Diagnostics.Stopwatch.Frequency;

    private static void RecoverAfterManyChanges()
    {
        using var run = new Scenario(1, new NetworkProfile(0.1f, 0.02f));
        run.Turn(8);
        Check.That(run.Host.Metrics.Frames > 0 && run.Host.Metrics.Pending > 0, "Disconnect during initial stream.");
        run.Connection(0, false);
        for (int index = 0; index < 4097; index++)
        {
            int x = 92 + index % 64, y = 102 + index / 64;
            run.HostGrid.Set(x, y, 0, liquid: 3);
            run.Host.MarkDirty(WorldChunkKey.At(x, y));
        }
        run.Position[0] = (92, 166);
        run.GuestGrid[0].Set(92, 166, 6, 10, 1);
        run.Guests[0].Begin(ReturnStream, recovery: true);
        run.Connection(0, true);
        run.ReplayFirst(0);
        Check.That(!run.Guests[0].HasNear(92, 166, 2), "Old stream cannot release recovery.");
        run.Until(() => run.Guests[0].HasNear(92, 166, 2) && run.Matches(0, 92, 166));
        Check.That(run.Value(0, 92, 166) == (0, 0f, 3), "The 4097th edit and liquid arrive together after rejoin.");
    }

    private static void RetryUntilApplied()
    {
        using var run = new Scenario(1, new NetworkProfile(0.06f));
        run.SuppressAcks = true;
        run.Turn(82);
        Check.That(run.GuestGrid[0].Applies > 0 && run.Host.Metrics.Repairs > 0 && run.Host.Metrics.Pending > 0,
            "Without application receipts the host retains work and retries current snapshots.");
        run.SuppressAcks = false;
        run.Until(() => run.Host.Metrics.Pending == 0);
        Check.That(run.Host.IsAcknowledged(run.Peers[0].LocalParticipant, WorldChunkKey.At(32, 36)),
            "A cumulative receipt settles bounded in-flight snapshots.");
    }

    private static void InvalidCoordinatesAndRetry()
    {
        using var run = new Scenario(1, new NetworkProfile(0.03f));
        run.GuestGrid[0].OnApply = _ => false;
        run.Turn(20);
        Check.That(run.GuestGrid[0].Applies == 0 && run.Host.Metrics.Pending != 0, "Accepted Send is not evidence of application.");
        run.GuestGrid[0].OnApply = null;
        run.Until(() => run.Guests[0].HasNear(32, 36, 2));
        Check.That(run.Host.Metrics.Resets > 1, "A rejected projection causes a fresh stream.");

        var blocks = new ushort[64];
        var damage = new float[64];
        var liquid = new byte[64];
        run.Until(() => run.Host.Metrics.Pending == 0);
        var guest = run.Guests[0];
        Guid stream = guest.Stream;
        bool appliedInvalid = false;
        run.GuestGrid[0].OnApply = header =>
        {
            if (header.Key.X >= 1000) appliedInvalid = true;
            return !appliedInvalid;
        };
        var invalid = WorldChunkWire.WriteSnapshot(Launch, new(stream, guest.AppliedSequence + 1, new(1000, 1000), 8, 8,
            WorldChunkWire.Fingerprint(blocks, damage, liquid)), blocks, damage, liquid);
        run.HostPeers.Send(run.Peers[0].LocalPlayer, invalid);
        run.Until(() => guest.Stream != stream && guest.HasNear(32, 36, 2));
        Check.That(!appliedInvalid, "Invalid coordinates do not reach the world adapter.");
    }

    private sealed class Scenario : IDisposable
    {
        internal readonly TestChunkGrid HostGrid = new(Width, Height);
        internal readonly TestChunkGrid[] GuestGrid;
        internal readonly SimulatedNetwork Network;
        internal readonly SessionPeers HostPeers;
        internal readonly SessionPeers[] Peers;
        internal readonly HostWorldChunks Host;
        internal readonly GuestWorldChunks[] Guests;
        internal readonly (int X, int Y)[] Position;
        private readonly byte[]?[] firstFrames;
        internal bool SuppressAcks;
        internal WorldChunkKey? WatchedKey;
        internal byte[]? WatchedFrame;

        internal Scenario(int count, NetworkProfile profile)
        {
            Network = new SimulatedNetwork(81, profile);
            Network.Trace += record =>
            {
                var data = record.Frame.Data.Span;
                if (record.Event != "enqueue" || data.Length < 53 || data[0] != (byte)MessageKind.WorldChunkSnapshot ||
                    WatchedFrame is not null || WatchedKey is not { } key) return;
                if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(37)) == key.X &&
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(39)) == key.Y) WatchedFrame = data.ToArray();
            };
            var plan = new RoomLaunchPlan(Launch, FirstStream, "normal", RoomOptions.Sandbox,
                Enumerable.Range(1, count + 1).Select(id => new LaunchPlayer((ulong)id, 0)).ToArray());
            HostPeers = new SessionPeers(Network.AddPeer(1), plan, 1, 1);
            Host = new HostWorldChunks(HostPeers, Network.Clock, HostGrid, WorldInterestOptions.Sandbox);
            HostPeers.AttachReceiver((sender, bytes) =>
            {
                if (SuppressAcks && WorldChunkWire.TryReadControl(bytes, Launch, out _, out uint applied, out _, out _, out _) &&
                    applied > 0) return true;
                return Host.Receive(sender, bytes, Guid.Empty);
            });
            Peers = new SessionPeers[count];
            GuestGrid = new TestChunkGrid[count];
            Guests = new GuestWorldChunks[count];
            Position = new (int, int)[count];
            firstFrames = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                ulong id = (ulong)i + 2;
                Position[i] = (32, 36);
                Peers[i] = new SessionPeers(Network.AddPeer(id), plan, id, 1);
                GuestGrid[i] = new TestChunkGrid(Width, Height);
                var guest = new GuestWorldChunks(Peers[i], Network.Clock, GuestGrid[i], WorldInterestOptions.Sandbox);
                Guests[i] = guest;
                guest.Begin(FirstStream, recovery: false);
                int index = i;
                Peers[i].AttachReceiver((sender, bytes) =>
                {
                    firstFrames[index] ??= bytes;
                    return guest.Receive(sender, bytes);
                });
                Connection(i, true);
            }
        }

        internal void Connection(int i, bool connected)
        {
            Network.SetConnected(1, Peers[i].LocalPlayer, connected);
            if (!connected) Host.Forget(Peers[i].LocalPlayer);
        }

        internal (ushort Block, float Damage, byte Liquid) Value(int i, int x, int y) =>
            GuestGrid[i].TryGet(x, y, out var value) ? value : throw new InvalidOperationException("Guest chunk missing.");

        internal bool Matches(int i, int x, int y) =>
            GuestGrid[i].TryGet(x, y, out var value) && value ==
            (HostGrid.TryGet(x, y, out var host) ? host : default);

        internal void ReplayFirst(int i)
        {
            if (firstFrames[i] is { } frame) HostPeers.Send(Peers[i].LocalPlayer, frame);
        }

        internal void Until(Func<bool> predicate, float seconds = 32)
        {
            float until = Network.Clock.Now + seconds;
            while (Network.Clock.Now < until && !predicate()) Turn(1);
            Check.That(predicate(), $"Chunk convergence exceeded {seconds}s at {Network.Clock.Now:F2}s. Pending {Host.Metrics.Pending}.");
        }

        internal void Turn(int steps)
        {
            for (int step = 0; step < steps; step++)
            {
                for (int i = 0; i < Guests.Length; i++) Guests[i].Tick(Position[i].X, Position[i].Y, 42, 24);
                Host.Tick(Center);
                Network.Advance(0.05f);
            }
        }

        private bool Center(ulong address, out int x, out int y)
        {
            (x, y) = Position[(int)address - 2];
            return true;
        }

        public void Dispose()
        {
            foreach (var peer in Peers) peer.Dispose();
            HostPeers.Dispose();
        }
    }
}
