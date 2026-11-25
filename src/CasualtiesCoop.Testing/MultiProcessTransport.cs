using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CasualtiesCoop.Core;

namespace CasualtiesCoop.Testing;

internal sealed class MultiProcessTransport : ISessionTransport, IDisposable
{
    private const int MaxFrame = 1024 * 1024;
    private const int MaxReliableFrames = 4096;
    private const int MaxReliableBytes = 16 * 1024 * 1024;
    private const int MaxUnreliableFrames = 512;
    private const int DeliveryBudget = 512;
    private const int RetryMilliseconds = 100;
    private const int HandshakeTimeoutMilliseconds = 2000;
    private static readonly byte[] ReliableMagic = { (byte)'C', (byte)'U', (byte)'M', (byte)'P', (byte)'2' };
    private static readonly byte[] UnreliableMagic = { (byte)'C', (byte)'U', (byte)'M', (byte)'P', (byte)'U', (byte)'2' };
    private static readonly int DatagramHeaderSize = UnreliableMagic.Length + 16 + 8 + 8 + 8 + 4;

    private sealed class Link(TcpClient client, NetworkStream stream, ulong generation)
    {
        internal TcpClient Client { get; } = client;
        internal NetworkStream Stream { get; } = stream;
        internal ulong Generation { get; } = generation;
        internal uint? DeliveredSequence;
    }

    private readonly record struct Inbound(byte[] Frame, double Due, uint Sequence = 0);
    private readonly Guid session;
    private readonly ulong local;
    private readonly ulong peer;
    private readonly bool isHost;
    private readonly IPEndPoint remoteTcp;
    private readonly IPEndPoint remoteUdp;
    private readonly double latency;
    private readonly double jitter;
    private readonly double unreliableLoss;
    private readonly Random random = new(0x4d554c54);
    private readonly object gate = new();
    private readonly Queue<byte[]> outgoingReliable = new();
    private readonly Queue<byte[]> outgoingUnreliable = new();
    private readonly Queue<Inbound> incomingReliable = new();
    private readonly List<Inbound> incomingUnreliable = new();
    private readonly List<byte[]> delivery = new(DeliveryBudget);
    private readonly TcpListener? listener;
    private readonly UdpClient udp;
    private readonly Thread tcpThread;
    private readonly Thread udpThread;
    private readonly ManualResetEventSlim stopping = new(false);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Link? link;
    private Link? reportedLink;
    private bool everConnected;
    private double reconnectAt;
    private ulong nextGeneration;
    private uint nextSequence;
    private int incomingReliableBytes;
    private int outgoingReliableBytes;
    private long reliableSent;
    private long reliableReceived;
    private long unreliableSent;
    private long unreliableReceived;
    private long unreliableDropped;
    private long sendErrors;

    internal MultiProcessTransport(Guid session, ulong local, ulong host, bool isHost, int tcpPort,
        int localUdpPort, string remoteHost, IReadOnlyDictionary<ulong, int> remoteUdpPorts,
        double latency, double jitter, double unreliableLoss)
    {
        this.session = session;
        this.local = local;
        this.isHost = isHost;
        this.latency = Math.Max(0, latency);
        this.jitter = Math.Max(0, jitter);
        this.unreliableLoss = Math.Clamp(unreliableLoss, 0, 1);
        int remoteCount = 0;
        foreach (var address in remoteUdpPorts)
        {
            if (address.Key == local) continue;
            peer = address.Key;
            remoteUdp = new IPEndPoint(IPAddress.Parse(remoteHost), address.Value);
            remoteCount++;
        }
        if (remoteCount != 1 || peer == 0 || (isHost ? local != host : peer != host))
        {
            throw new ArgumentException("The local transport requires exactly one remote participant.");
        }
        remoteTcp = new IPEndPoint(remoteUdp!.Address, tcpPort);
        udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, localUdpPort));
        udp.Client.ReceiveTimeout = 250;
        if (isHost)
        {
            listener = new TcpListener(IPAddress.Loopback, tcpPort);
            listener.Start();
        }
        udpThread = new Thread(UdpReceiveLoop) { IsBackground = true, Name = "coop-mp-udp" };
        tcpThread = new Thread(TcpLoop) { IsBackground = true, Name = "coop-mp-tcp" };
        udpThread.Start();
        tcpThread.Start();
    }

    public event Action<ulong, bool>? ConnectionChanged;
    public Func<ulong, byte[], bool>? ReceiveFrame { get; set; }

    public bool IsVerified(ulong address)
    {
        lock (gate) return address == peer && reportedLink is not null;
    }

    internal int VerifiedPeerCount => IsVerified(peer) ? 1 : 0;

    internal string Stats => $"reliableSent={Interlocked.Read(ref reliableSent)} " +
        $"reliableReceived={Interlocked.Read(ref reliableReceived)} unreliableSent={Interlocked.Read(ref unreliableSent)} " +
        $"unreliableReceived={Interlocked.Read(ref unreliableReceived)} " +
        $"unreliableDropped={Interlocked.Read(ref unreliableDropped)} sendErrors={Interlocked.Read(ref sendErrors)}";

    public bool TrySend(ulong address, byte[] frame, bool reliable)
    {
        if (address != peer || frame.Length == 0 || frame.Length > MaxFrame) return false;
        lock (gate)
        {
            if (stopping.IsSet) return false;
            if (reliable)
            {
                if ((everConnected && (link is null || link != reportedLink)) ||
                    outgoingReliable.Count == MaxReliableFrames || outgoingReliableBytes + frame.Length > MaxReliableBytes)
                {
                    Interlocked.Increment(ref sendErrors);
                    return false;
                }
                outgoingReliable.Enqueue(frame);
                outgoingReliableBytes += frame.Length;
            }
            else
            {
                if (link is null || link != reportedLink || frame.Length > 65507 - DatagramHeaderSize) return false;
                if (outgoingUnreliable.Count >= MaxUnreliableFrames - 1 || random.NextDouble() < unreliableLoss)
                {
                    Interlocked.Increment(ref unreliableDropped);
                    return true;
                }
                byte[] datagram = CreateDatagram(frame, link.Generation, ++nextSequence);
                outgoingUnreliable.Enqueue(datagram);
                if (random.NextDouble() < 0.01) outgoingUnreliable.Enqueue(datagram);
            }
            Monitor.PulseAll(gate);
            return true;
        }
    }

    internal void DisconnectFor(float seconds)
    {
        if (isHost || !float.IsFinite(seconds) || seconds <= 0)
        {
            throw new ArgumentException("A simulated disconnect requires the guest and a positive duration.");
        }
        Link current;
        lock (gate)
        {
            current = reportedLink ?? throw new InvalidOperationException("The guest is not connected.");
            reconnectAt = clock.Elapsed.TotalSeconds + seconds;
        }
        Close(current);
    }

    internal void Tick()
    {
        Link? previous;
        Link? current;
        lock (gate)
        {
            previous = reportedLink;
            current = link;
            if (previous != current) reportedLink = null;
        }
        if (previous is not null && previous != current) ConnectionChanged?.Invoke(peer, false);
        if (current is null) return;

        bool reportReconnect = false;
        lock (gate)
        {
            if (link != current) return;
            if (reportedLink != current)
            {
                reportedLink = current;
                // The first verification in Steam occurs before WorldSession subscribes.
                reportReconnect = everConnected;
                everConnected = true;
                Monitor.PulseAll(gate);
            }
        }
        if (reportReconnect) ConnectionChanged?.Invoke(peer, true);

        CollectDue(current);
        foreach (byte[] frame in delivery)
        {
            lock (gate)
            {
                if (link != current) break;
            }
            ReceiveFrame?.Invoke(peer, frame);
        }
        delivery.Clear();
    }

    private void CollectDue(Link current)
    {
        double now = clock.Elapsed.TotalSeconds;
        lock (gate)
        {
            if (link != current) return;
            while (delivery.Count < DeliveryBudget && incomingReliable.TryPeek(out Inbound next) && next.Due <= now)
            {
                incomingReliable.Dequeue();
                incomingReliableBytes -= next.Frame.Length;
                delivery.Add(next.Frame);
            }

            // TCP waits for its head even when jitter gives a later frame an earlier deadline.
            // UDP instead resolves freshness in delivery order after the simulated delay.
            incomingUnreliable.Sort(static (a, b) => a.Due.CompareTo(b.Due));
            int consumed = 0;
            while (delivery.Count < DeliveryBudget && consumed < incomingUnreliable.Count &&
                incomingUnreliable[consumed].Due <= now)
            {
                Inbound next = incomingUnreliable[consumed++];
                if (current.DeliveredSequence is uint previous && unchecked((int)(next.Sequence - previous)) <= 0)
                {
                    Interlocked.Increment(ref unreliableDropped);
                    continue;
                }
                current.DeliveredSequence = next.Sequence;
                delivery.Add(next.Frame);
            }
            if (consumed != 0) incomingUnreliable.RemoveRange(0, consumed);
            Monitor.PulseAll(gate);
        }
    }

    private void TcpLoop()
    {
        while (!stopping.IsSet)
        {
            try
            {
                if (isHost)
                {
                    using TcpClient client = listener!.AcceptTcpClient();
                    Accept(client);
                }
                else
                {
                    bool reconnect;
                    lock (gate) reconnect = clock.Elapsed.TotalSeconds >= reconnectAt;
                    if (reconnect)
                    {
                        using TcpClient client = new();
                        client.Connect(remoteTcp.Address, remoteTcp.Port);
                        Connect(client);
                    }
                }
            }
            catch (SocketException)
            {
                // Loopback listener might not have started yet or the peer may be reconnecting.
            }
            catch (IOException)
            {
                // A closed stream ends the current attempt.
            }
            catch (ObjectDisposedException)
            {
                if (stopping.IsSet) return;
            }
            if (!isHost) stopping.Wait(RetryMilliseconds);
        }
    }

    private void Accept(TcpClient client)
    {
        using NetworkStream stream = OpenHandshake(client);
        byte[] hello = new byte[ReliableMagic.Length + 16 + 8 + 8];
        if (!ReadExact(stream, hello) || !hello.AsSpan(0, ReliableMagic.Length).SequenceEqual(ReliableMagic) ||
            new Guid(hello.AsSpan(ReliableMagic.Length, 16)) != session ||
            BitConverter.ToUInt64(hello, ReliableMagic.Length + 16) != peer)
        {
            return;
        }
        ulong generation = BitConverter.ToUInt64(hello, ReliableMagic.Length + 24);
        stream.Write(hello, 0, hello.Length);
        RunLink(new Link(client, stream, generation));
    }

    private void Connect(TcpClient client)
    {
        using NetworkStream stream = OpenHandshake(client);
        byte[] hello = new byte[ReliableMagic.Length + 16 + 8 + 8];
        ReliableMagic.CopyTo(hello, 0);
        session.ToByteArray().CopyTo(hello, ReliableMagic.Length);
        BitConverter.GetBytes(local).CopyTo(hello, ReliableMagic.Length + 16);
        ulong generation = ++nextGeneration;
        BitConverter.GetBytes(generation).CopyTo(hello, ReliableMagic.Length + 24);
        stream.Write(hello, 0, hello.Length);
        byte[] reply = new byte[hello.Length];
        if (ReadExact(stream, reply) && reply.AsSpan().SequenceEqual(hello))
        {
            RunLink(new Link(client, stream, generation));
        }
    }

    private static NetworkStream OpenHandshake(TcpClient client)
    {
        client.NoDelay = true;
        client.ReceiveTimeout = HandshakeTimeoutMilliseconds;
        client.SendTimeout = HandshakeTimeoutMilliseconds;
        return client.GetStream();
    }

    private void RunLink(Link current)
    {
        current.Client.ReceiveTimeout = 0;
        lock (gate)
        {
            if (stopping.IsSet) return;
            link = current;
        }
        var writer = new Thread(() => WriteLoop(current)) { IsBackground = true, Name = "coop-mp-send" };
        writer.Start();
        try
        {
            byte[] lengthBytes = new byte[4];
            while (ReadExact(current.Stream, lengthBytes))
            {
                int length = BitConverter.ToInt32(lengthBytes);
                if (length <= 0 || length > MaxFrame) break;
                byte[] frame = new byte[length];
                if (!ReadExact(current.Stream, frame) || !QueueReliable(current, frame)) break;
            }
        }
        finally
        {
            Close(current);
            writer.Join();
        }
    }

    private bool QueueReliable(Link current, byte[] frame)
    {
        lock (gate)
        {
            while (link == current && (incomingReliable.Count == MaxReliableFrames ||
                incomingReliableBytes + frame.Length > MaxReliableBytes))
            {
                Monitor.Wait(gate);
            }
            if (link != current) return false;
            incomingReliable.Enqueue(new Inbound(frame, NextDue()));
            incomingReliableBytes += frame.Length;
            Interlocked.Increment(ref reliableReceived);
            return true;
        }
    }

    private void WriteLoop(Link current)
    {
        try
        {
            while (TryTakeOutbound(current, out byte[] frame, out bool reliable))
            {
                if (reliable)
                {
                    byte[] length = BitConverter.GetBytes(frame.Length);
                    current.Stream.Write(length, 0, length.Length);
                    current.Stream.Write(frame, 0, frame.Length);
                    Interlocked.Increment(ref reliableSent);
                }
                else
                {
                    udp.Send(frame, frame.Length, remoteUdp);
                    Interlocked.Increment(ref unreliableSent);
                }
            }
        }
        catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException)
        {
            lock (gate)
            {
                if (link == current) Interlocked.Increment(ref sendErrors);
            }
            Close(current);
        }
    }

    private bool TryTakeOutbound(Link current, out byte[] frame, out bool reliable)
    {
        lock (gate)
        {
            while (link == current)
            {
                if (outgoingReliable.TryDequeue(out frame!))
                {
                    outgoingReliableBytes -= frame.Length;
                    reliable = true;
                    return true;
                }
                if (outgoingUnreliable.TryDequeue(out frame!))
                {
                    reliable = false;
                    return true;
                }
                Monitor.Wait(gate);
            }
        }
        frame = Array.Empty<byte>();
        reliable = false;
        return false;
    }

    private byte[] CreateDatagram(byte[] frame, ulong generation, uint sequence)
    {
        byte[] datagram = new byte[DatagramHeaderSize + frame.Length];
        UnreliableMagic.CopyTo(datagram, 0);
        int offset = UnreliableMagic.Length;
        session.ToByteArray().CopyTo(datagram, offset);
        BitConverter.GetBytes(local).CopyTo(datagram, offset + 16);
        BitConverter.GetBytes(peer).CopyTo(datagram, offset + 24);
        BitConverter.GetBytes(generation).CopyTo(datagram, offset + 32);
        BitConverter.GetBytes(sequence).CopyTo(datagram, offset + 40);
        frame.CopyTo(datagram, DatagramHeaderSize);
        return datagram;
    }

    private void UdpReceiveLoop()
    {
        while (!stopping.IsSet)
        {
            IPEndPoint endpoint = new(IPAddress.Any, 0);
            byte[] datagram;
            try { datagram = udp.Receive(ref endpoint); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            int offset = UnreliableMagic.Length;
            if (!endpoint.Equals(remoteUdp) || datagram.Length <= DatagramHeaderSize ||
                !datagram.AsSpan(0, offset).SequenceEqual(UnreliableMagic) ||
                new Guid(datagram.AsSpan(offset, 16)) != session ||
                BitConverter.ToUInt64(datagram, offset + 16) != peer ||
                BitConverter.ToUInt64(datagram, offset + 24) != local)
            {
                continue;
            }
            ulong generation = BitConverter.ToUInt64(datagram, offset + 32);
            uint sequence = BitConverter.ToUInt32(datagram, offset + 40);
            lock (gate)
            {
                if (link?.Generation != generation || incomingUnreliable.Count == MaxUnreliableFrames)
                {
                    Interlocked.Increment(ref unreliableDropped);
                    continue;
                }
                incomingUnreliable.Add(new Inbound(datagram.AsSpan(DatagramHeaderSize).ToArray(), NextDue(), sequence));
                Interlocked.Increment(ref unreliableReceived);
            }
        }
    }

    private double NextDue() =>
        clock.Elapsed.TotalSeconds + Math.Max(0, latency + (random.NextDouble() * 2 - 1) * jitter);

    private void Close(Link current)
    {
        lock (gate)
        {
            if (link != current) return;
            link = null;
            incomingReliable.Clear();
            outgoingReliable.Clear();
            incomingUnreliable.Clear();
            outgoingUnreliable.Clear();
            incomingReliableBytes = outgoingReliableBytes = 0;
            Monitor.PulseAll(gate);
        }
        current.Client.Close();
    }

    private static bool ReadExact(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        Link? current;
        lock (gate)
        {
            if (stopping.IsSet) return;
            stopping.Set();
            current = link;
            reportedLink = null;
        }
        listener?.Stop();
        if (current is not null) Close(current);
        udp.Close();
        tcpThread.Join(HandshakeTimeoutMilliseconds + RetryMilliseconds);
        udpThread.Join(500);
    }
}
