using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Gameplay.Items;

internal static class ItemMotionChecks
{
    internal static void Run()
    {
        InterpolatesUnderNetworkDelay(40, 40, loss: false);
        InterpolatesUnderNetworkDelay(150, 150, loss: false);
        InterpolatesUnderNetworkDelay(40, 150, loss: true);
        RotationExtrapolationAndDiscontinuities();
        SnapshotKeepsNewestSampleTime();
    }

    private static void InterpolatesUnderNetworkDelay(int minDelayMs, int maxDelayMs, bool loss)
    {
        const float HostStart = 4270f;
        const float LocalStart = 45f;
        const int SendRate = 10;
        const int RenderRate = 120;
        const int Duration = 8;
        var random = new Random(720);
        var arrivals = new List<(float At, uint Order, ItemState State)>();
        for (uint packet = 0; packet < Duration * SendRate; packet++)
        {
            float elapsed = packet / (float)SendRate;
            if (loss && (random.Next(4) == 0 || packet % 20 is 8 or 9))
            {
                continue;
            }
            float delay = random.Next(minDelayMs, maxDelayMs + 1) / 1000f;
            arrivals.Add((LocalStart + elapsed + delay, packet + 1, Sample(HostStart + elapsed, Trajectory(elapsed))));
        }
        arrivals.Sort((left, right) => left.At.CompareTo(right.At));

        var buffer = new ItemMotionBuffer();
        buffer.Reset(Trajectory(0), LocalStart);
        int next = 0;
        uint received = 0;
        float maxJump = 0f;
        float maxError = 0f;
        float totalError = 0f;
        int measured = 0;
        ItemLocation previous = default;
        for (int frame = 0; frame < Duration * RenderRate; frame++)
        {
            float elapsed = frame / (float)RenderRate;
            float now = LocalStart + elapsed;
            while (next < arrivals.Count && arrivals[next].At <= now)
            {
                var packet = arrivals[next++];
                if (packet.Order > received)
                {
                    buffer.Push(packet.State, packet.At);
                    received = packet.Order;
                }
            }
            ItemLocation rendered = buffer.Evaluate(now);
            if (elapsed >= 2f)
            {
                ItemLocation expected = Trajectory(elapsed - minDelayMs / 1000f - ItemMotionBuffer.PlaybackDelay);
                maxJump = Math.Max(maxJump, Distance(rendered, previous));
                float error = Distance(rendered, expected);
                maxError = Math.Max(maxError, error);
                totalError += error;
                measured++;
            }
            previous = rendered;
        }

        float averageError = totalError / measured;
        Console.WriteLine($"Item motion {minDelayMs}–{maxDelayMs} ms, loss={loss}, max frame jump {maxJump:F3}, " +
            $"max playback error {maxError:F3}, mean error {averageError:F3}");
        Check.That(maxJump <= (loss ? 0.27f : 0.10f) && maxError <= (loss ? 1.75f : 0.015f) &&
            averageError <= (loss ? 0.50f : 0.01f),
            "Buffered playback bounds frame jumps and position error at 120 Hz under local network delay and loss.");
    }

    private static ItemLocation Trajectory(float seconds) =>
        new(7f * seconds, 1.6f * MathF.Sin(2f * seconds), (350f + 900f * seconds) % 360f, 7f,
            3.2f * MathF.Cos(2f * seconds), 900f);

    private static void RotationExtrapolationAndDiscontinuities()
    {
        var buffer = new ItemMotionBuffer();
        var start = new ItemLocation(0, 0, 350, 4, 0, 1000);
        buffer.Reset(start, 20);
        buffer.Push(Sample(10, start), 20);
        buffer.Push(Sample(10.25f, new ItemLocation(1, 0, 240, 4, 2, 1000)), 20.25f);
        ItemLocation middle = buffer.Evaluate(20.125f + ItemMotionBuffer.PlaybackDelay);
        Check.That(Math.Abs(middle.X - 0.5f) < 0.001f && Math.Abs(middle.VelocityY - 1f) < 0.001f &&
            Math.Abs(middle.Angle - 115f) < 0.01f && middle.Spin == 1000f,
            "Interpolation crosses a full turn boundary following measured spin, including gaps longer than half a rotation.");

        ItemLocation late = buffer.Evaluate(42f);
        Check.That(Math.Abs(late.X - 1.4f) < 0.001f && Math.Abs(late.Y - 0.2f) < 0.001f &&
            Math.Abs(late.Angle - 340f) < 0.01f && late.VelocityX == 0f && late.Spin == 0f &&
            buffer.Evaluate(50f).X == late.X, "Packet loss cannot extrapolate beyond 100 ms.");

        var destination = new ItemLocation(100, -50, 45);
        buffer.Push(Sample(10.3f, destination), 20.3f);
        buffer.Push(Sample(10.28f, start), 20.35f);
        Check.That(Distance(buffer.Evaluate(20.35f), destination) == 0,
            "A teleport snaps immediately and a stale sample cannot bring back the old trajectory.");
        buffer.Reset(start, 21);
        Check.That(Distance(buffer.Evaluate(21), start) == 0,
            "An explicit placement reset clears the previous world-motion buffer.");
    }

    private static void SnapshotKeepsNewestSampleTime()
    {
        var host = new AuthoritativeItemRegistry();
        var guest = new ItemReplicaStore();
        Check.That(host.TryRegister("rock", default, 1f, out var origin) &&
            guest.ApplyDelta(origin.Sequence, origin, out _, out _), "World object has an initial structural state.");
        Check.That(host.TrySample(origin.Id, new ItemLocation(1, 0, 0, 1), 1f, 10f, out var saved, out _) &&
            saved.SampleTime == 10f, "Authoritative motion samples have a monotonic timestamp.");
        guest.BeginSnapshot();
        Check.That(guest.Stage(saved) && host.TrySample(origin.Id, new ItemLocation(2, 0, 0, 1), 1f, 11f, out var next, out _) &&
            guest.ApplySample(next, motion: true, out _), "A newer live motion frame arrives during the snapshot.");
        Check.That(guest.CompleteSnapshot(origin.Sequence, 1) && guest.TryGet(origin.Id, out var merged) &&
            merged.SampleTime == 11f && merged.Location.X == 2f && !guest.ApplySample(saved, motion: true, out _),
            "Snapshot merge keeps the timestamp of the newest live sample and rejects an obsolete frame.");
    }

    private static ItemState Sample(float time, ItemLocation location) => new() { SampleTime = time, Location = location };

    private static float Distance(ItemLocation from, ItemLocation to)
    {
        float x = from.X - to.X;
        float y = from.Y - to.Y;
        return MathF.Sqrt(x * x + y * y);
    }
}
