internal static class TransportChecks
{
    internal static void Run()
    {
        ScriptedFaults();
        BandwidthAndConnectionLifetime();
        var first = SeededTrace(731);
        var second = SeededTrace(731);
        Check.That(first.SequenceEqual(second), "Seed 731 must reproduce delivery times and order.");
        Check.That(!first.SequenceEqual(SeededTrace(732)), "Different seeds must vary the trace.");
    }

    private static void ScriptedFaults()
    {
        var network = new SimulatedNetwork(7, script: frame => frame.Data.Span[0] switch
        {
            1 => new NetworkFault(Drop: true, Duplicate: true, ExtraDelay: 2),
            3 => new NetworkFault(Drop: true),
            4 => new NetworkFault(ExtraDelay: 1),
            5 => new NetworkFault(Duplicate: true),
            _ => default
        });
        var sender = network.AddPeer(1);
        var target = network.AddPeer(2);
        List<byte> delivered = new();
        target.ReceiveFrame = (source, frame) =>
        {
            Check.That(source == 1, "Sender must come from the simulated connection.");
            delivered.Add(frame[0]);
            return true;
        };

        network.SetConnected(1, 2, true);
        for (byte value = 1; value <= 5; value++)
        {
            Check.That(sender.TrySend(2, new[] { value }, reliable: value <= 2), "Connection accepts frame.");
        }
        network.Advance(0);
        Check.That(delivered.SequenceEqual(new byte[] { 5, 5 }), "Unreliable traffic may overtake and duplicate.");
        network.Advance(1);
        Check.That(delivered.SequenceEqual(new byte[] { 5, 5, 4 }), "Delayed unreliable frame arrives later.");
        network.Advance(1);
        Check.That(delivered.SequenceEqual(new byte[] { 5, 5, 4, 1, 2 }), "Reliable frames survive faults and preserve FIFO.");
    }

    private static void BandwidthAndConnectionLifetime()
    {
        var network = new SimulatedNetwork(8, new NetworkProfile(BytesPerSecond: 2));
        var sender = network.AddPeer(1);
        var target = network.AddPeer(2);
        List<byte> delivered = new();
        List<bool> connections = new();
        target.ConnectionChanged += (_, connected) => connections.Add(connected);
        target.ReceiveFrame = (_, data) =>
        {
            delivered.Add(data[0]);
            return true;
        };

        network.SetConnected(1, 2, true);
        byte[] frame = { 1, 1 };
        sender.TrySend(2, frame, reliable: true);
        frame[0] = 255;
        sender.TrySend(2, new byte[] { 2, 2 }, reliable: true);
        network.Advance(0.5f);
        Check.That(delivered.Count == 0, "Bandwidth limits first delivery.");
        network.Advance(0.5f);
        Check.That(delivered.SequenceEqual(new byte[] { 1 }), "Accepted bytes are copied and arrive at the bandwidth deadline.");

        network.SetConnected(1, 2, false);
        Check.That(!sender.IsVerified(2) && !sender.TrySend(2, frame, reliable: true), "Disconnected endpoint rejects sends.");
        network.SetConnected(1, 2, true);
        sender.TrySend(2, new byte[] { 3 }, reliable: true);
        network.Advance(1f);
        Check.That(delivered.SequenceEqual(new byte[] { 1, 3 }), "Frames from the retired connection never reach the new one.");
        Check.That(connections.SequenceEqual(new[] { true, false, true }), "Verification and disconnect notifications are ordered.");
    }

    private static List<(byte Value, float Time)> SeededTrace(int seed)
    {
        var network = new SimulatedNetwork(seed, new NetworkProfile(0.2f, 0.1f, 0.25f, 0.25f, 0.5f, 32));
        var sender = network.AddPeer(1);
        var target = network.AddPeer(2);
        List<(byte, float)> trace = new();
        target.ReceiveFrame = (_, data) =>
        {
            trace.Add((data[0], network.Clock.Now));
            return true;
        };
        network.SetConnected(1, 2, true);
        for (byte value = 1; value <= 24; value++)
        {
            sender.TrySend(2, new[] { value }, reliable: false);
        }
        network.Advance(3);
        return trace;
    }
}
