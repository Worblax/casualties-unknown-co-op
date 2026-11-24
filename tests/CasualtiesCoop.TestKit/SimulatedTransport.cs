using CasualtiesCoop.Core;

internal readonly record struct NetworkProfile(float Latency = 0, float Jitter = 0, float UnreliableLoss = 0,
    float UnreliableDuplication = 0, float UnreliableReorderDelay = 0, int BytesPerSecond = 0, int QueueLimit = 4096);

internal readonly record struct SimulatedFrame(ulong Sender, ulong Receiver, ReadOnlyMemory<byte> Data, bool Reliable);

internal readonly record struct NetworkFault(bool Drop = false, bool Duplicate = false, float ExtraDelay = 0);

internal readonly record struct NetworkTrace(float Time, string Event, SimulatedFrame Frame);

internal sealed class SimulatedTransport : ISessionTransport
{
    private readonly SimulatedNetwork network;

    internal SimulatedTransport(SimulatedNetwork network, ulong peer)
    {
        this.network = network;
        Peer = peer;
    }

    internal ulong Peer { get; }
    public event Action<ulong, bool>? ConnectionChanged;
    public Func<ulong, byte[], bool>? ReceiveFrame { get; set; }

    public bool IsVerified(ulong peer) => network.IsConnected(Peer, peer);

    public bool TrySend(ulong peer, byte[] frame, bool reliable) =>
        network.TrySend(new SimulatedFrame(Peer, peer, frame, reliable));

    internal void Notify(ulong peer, bool connected) => ConnectionChanged?.Invoke(peer, connected);

    internal bool Deliver(ulong sender, byte[] frame) => ReceiveFrame?.Invoke(sender, frame) == true;
}

internal sealed class SimulatedNetwork
{
    private sealed class Direction
    {
        internal float WireAvailableAt;
        internal float LastReliableArrival;
    }

    private sealed class Link
    {
        internal readonly Direction FromLower = new();
        internal readonly Direction FromHigher = new();
    }

    private sealed record Packet(ulong Sender, ulong Receiver, byte[] Data, bool Reliable, Link Connection);

    private readonly Random random;
    private NetworkProfile profile;
    private readonly Func<SimulatedFrame, NetworkFault>? script;
    private readonly Dictionary<ulong, SimulatedTransport> endpoints = new();
    private readonly Dictionary<(ulong Lower, ulong Higher), Link> links = new();
    private readonly SortedSet<(float Time, long Order, Packet Packet)> pending =
        new(Comparer<(float Time, long Order, Packet Packet)>.Create((a, b) =>
            a.Time == b.Time ? a.Order.CompareTo(b.Order) : a.Time.CompareTo(b.Time)));
    private long sequence;

    internal SimulatedNetwork(int seed, NetworkProfile? profile = null, Func<SimulatedFrame, NetworkFault>? script = null)
    {
        random = new Random(seed);
        this.profile = profile ?? new NetworkProfile(QueueLimit: 4096);
        Validate(this.profile);
        this.script = script;
    }

    internal ManualClock Clock { get; } = new();
    internal int Queued => pending.Count;

    internal void SetProfile(NetworkProfile next)
    {
        Validate(next);
        profile = next;
    }
    internal event Action<NetworkTrace>? Trace;

    internal SimulatedTransport AddPeer(ulong peer)
    {
        var endpoint = new SimulatedTransport(this, peer);
        endpoints.Add(peer, endpoint);
        return endpoint;
    }

    internal bool IsConnected(ulong first, ulong second) => links.ContainsKey(Pair(first, second));

    internal void SetConnected(ulong first, ulong second, bool connected)
    {
        if (first == second)
        {
            throw new ArgumentException("Endpoints must be different.");
        }

        var firstEndpoint = endpoints[first];
        var secondEndpoint = endpoints[second];
        var pair = Pair(first, second);
        bool changed = connected ? links.TryAdd(pair, new Link()) : links.Remove(pair);
        if (changed)
        {
            Record(connected ? "connect" : "disconnect", new SimulatedFrame(first, second, ReadOnlyMemory<byte>.Empty, true));
            firstEndpoint.Notify(second, connected);
            secondEndpoint.Notify(first, connected);
        }
    }

    internal bool TrySend(SimulatedFrame frame)
    {
        if (!links.TryGetValue(Pair(frame.Sender, frame.Receiver), out Link? link) || pending.Count >= profile.QueueLimit)
        {
            Record("reject", frame);
            return false;
        }

        NetworkFault fault = script?.Invoke(frame) ?? default;
        if (!frame.Reliable && (fault.Drop || (float)random.NextDouble() < profile.UnreliableLoss))
        {
            Record("drop", frame);
            return true;
        }

        Direction path = frame.Sender < frame.Receiver ? link.FromLower : link.FromHigher;
        float delay = Math.Max(0, profile.Latency + ((float)random.NextDouble() * 2 - 1) * profile.Jitter + fault.ExtraDelay);
        if (!frame.Reliable)
        {
            delay += (float)random.NextDouble() * profile.UnreliableReorderDelay;
        }
        Schedule(frame, link, path, delay);
        if (!frame.Reliable && pending.Count < profile.QueueLimit &&
            (fault.Duplicate || (float)random.NextDouble() < profile.UnreliableDuplication))
        {
            Schedule(frame, link, path, delay);
        }
        return true;
    }

    internal void Advance(float seconds)
    {
        float target = Clock.Now + seconds;
        if (!float.IsFinite(target) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        int delivered = 0;
        while (pending.Count != 0 && pending.Min.Time <= target && delivered < 4096)
        {
            var next = pending.Min;
            pending.Remove(next);
            Clock.AdvanceTo(next.Time);
            Packet packet = next.Packet;
            var frame = new SimulatedFrame(packet.Sender, packet.Receiver, packet.Data, packet.Reliable);
            delivered++;
            if (!links.TryGetValue(Pair(packet.Sender, packet.Receiver), out Link? link) || link != packet.Connection)
            {
                Record("retired", frame);
                continue;
            }
            Record("deliver", frame);
            if (!endpoints[packet.Receiver].Deliver(packet.Sender, packet.Data))
            {
                Record("reject-handler", frame);
                SetConnected(packet.Sender, packet.Receiver, false);
            }
        }
        if (pending.Count == 0 || pending.Min.Time > target)
        {
            Clock.AdvanceTo(target);
        }
    }

    private void Schedule(SimulatedFrame frame, Link link, Direction path, float delay)
    {
        float sentAt = Math.Max(Clock.Now, path.WireAvailableAt);
        if (profile.BytesPerSecond > 0)
        {
            sentAt += (float)frame.Data.Length / profile.BytesPerSecond;
            path.WireAvailableAt = sentAt;
        }

        float due = sentAt + delay;
        if (frame.Reliable)
        {
            due = Math.Max(due, path.LastReliableArrival);
            path.LastReliableArrival = due;
        }
        pending.Add((due, sequence++, new Packet(frame.Sender, frame.Receiver, frame.Data.ToArray(), frame.Reliable, link)));
        Record("enqueue", frame);
    }

    private void Record(string status, SimulatedFrame frame) => Trace?.Invoke(new NetworkTrace(Clock.Now, status, frame));

    private static void Validate(NetworkProfile profile)
    {
        if (!Valid(profile.Latency) || !Valid(profile.Jitter) || !Valid(profile.UnreliableReorderDelay) ||
            !Valid(profile.UnreliableLoss) || profile.UnreliableLoss > 1 ||
            !Valid(profile.UnreliableDuplication) || profile.UnreliableDuplication > 1 ||
            profile.BytesPerSecond < 0 || profile.QueueLimit is < 1 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }

    private static bool Valid(float value) => float.IsFinite(value) && value >= 0;

    private static (ulong Lower, ulong Higher) Pair(ulong first, ulong second) =>
        first < second ? (first, second) : (second, first);
}
