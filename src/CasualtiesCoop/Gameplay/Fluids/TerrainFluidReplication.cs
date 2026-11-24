using System;
using CasualtiesCoop.Core;
using System.Collections.Generic;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Fluids;

internal sealed class TerrainFluidReplication : IDisposable
{
    private const int ZoneSize = 128;
    private const int StripHeight = 16;
    private const float NetworkInterval = 0.12f;
    private const int Size = FluidWire.ChunkSize;

    private sealed class Chunk
    {
        internal readonly byte[] Cells = new byte[FluidWire.PackedSize];
        internal uint Version = 1;
        internal uint ScanRound;
    }

    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly PlayerPositionLookup playerPosition;
    private readonly bool host;
    private readonly HashSet<Vector2Int> uniqueZones = new();
    private readonly List<Vector2Int> zones = new();
    private readonly Dictionary<(int X, int Y), Chunk> chunks = new();
    private readonly FluidChunkSchedule schedule = new();
    private readonly FluidChunkProgress progress = new();
    private readonly Func<(int X, int Y), uint> captureRevision;
    private readonly Dictionary<Vector2Int, (Vector2Int Cell, byte Direction)> flow = new();
    private readonly List<GameObject> receivedPushers = new();
    private readonly FluidDrinkAuthority drinks;
    private byte[,]? cells;
    private WorldGeneration? world;
    private int strip;
    private uint scanRound;
    private float nextNetwork;
    private ulong drinkNumber;
    private (ulong Operation, Vector2Int Cell, byte Liquid)? pendingDrink;
    private float nextDrinkRetry;
    private bool applyingDrink;

    internal TerrainFluidReplication(SessionPeers peers, PlayerPositionLookup playerPosition, IMonotonicClock clock)
    {
        this.peers = peers;
        this.clock = clock;
        drinks = new FluidDrinkAuthority(clock);
        captureRevision = CaptureRevision;
        this.playerPosition = playerPosition;
        host = peers.IsHost;
        peers.ConnectionChanged += OnPeerChanged;
    }

    internal bool IsGuest => !host && IsBound(FluidManager.main);

    internal ulong LastDrink(ulong player) => drinks.LastOperation(player);

    internal void BeginRecovery(ulong confirmedDrink)
    {
        drinkNumber = Math.Max(drinkNumber, confirmedDrink);
        pendingDrink = null;
        progress.RequireFreshSnapshots();
    }

    internal void ResendNearby(ulong player)
    {
        schedule.Forget(player);
        nextNetwork = 0;
    }

    internal bool HasSnapshotNear(Vector2 position)
    {
        if (!IsBound(FluidManager.main))
        {
            return false;
        }

        Vector2Int at = world!.WorldToBlockPos(position);
        if (!InBounds(at))
        {
            return true;
        }

        int minX = Math.Max(0, at.x - 2) / Size;
        int minY = Math.Max(0, at.y - 2) / Size;
        int maxX = Math.Min(cells!.GetLength(0) - 1, at.x + 2) / Size;
        int maxY = Math.Min(cells.GetLength(1) - 1, at.y + 2) / Size;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!progress.HasCurrent((x, y)))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private bool IsBound(FluidManager? manager) => manager && cells is not null &&
        ReferenceEquals(manager!.fluid, cells) && world && world == WorldGeneration.world &&
        world!.worldExists && !world.generatingWorld;

    internal void Tick()
    {
        if (!Bind() || !IsBound(FluidManager.main))
        {
            return;
        }

        float now = clock.Now;
        if (!host)
        {
            if (pendingDrink is { } && now >= nextDrinkRetry)
            {
                SendDrink();
            }
            return;
        }

        if (now < nextNetwork)
        {
            return;
        }

        nextNetwork = now + NetworkInterval;
        scanRound++;
        foreach (LaunchPlayer player in peers.Players)
        {
            ulong peer = player.SteamId;
            if (peer != peers.LocalPlayer && peers.IsVerified(peer) &&
                playerPosition(player.SteamId, out Vector2 position))
            {
                SendNearby(peer, world!.WorldToBlockPos(position), now);
                SendFlow(peer, position);
            }
        }
        flow.Clear();
    }

    private bool Bind()
    {
        WorldGeneration currentWorld = WorldGeneration.world;
        FluidManager manager = FluidManager.main;
        if (IsBound(manager))
        {
            return true;
        }

        // Changing worlds requires a new session identity. A replacement map in the same world keeps its revisions.
        if (!currentWorld || !currentWorld.worldExists || currentWorld.generatingWorld || !manager ||
            (world is not null && !ReferenceEquals(world, currentWorld)) || manager.fluid is not { } map ||
            map.GetLength(0) is < 3 or > ushort.MaxValue || map.GetLength(1) is < 3 or > ushort.MaxValue)
        {
            return false;
        }

        if (cells is not null)
        {
            schedule.Clear();
            progress.RequireFreshSnapshots();
            flow.Clear();
            DestroyPushers();
            strip = 0;
            nextNetwork = 0;
        }
        world = currentWorld;
        cells = map;
        if (CoopDiagnostics.Enabled)
        {
            CoopDiagnostics.Trace("Fluids", $"Bound {map.GetLength(0)}×{map.GetLength(1)} terrain grid as {(host ? "host" : "guest")}.");
        }
        if (!host)
        {
            Array.Clear(map, 0, map.Length);
        }
        return true;
    }


    internal bool TryStep(FluidManager manager)
    {
        if (!IsBound(manager))
        {
            return false;
        }

        if (!host)
        {
            return true;
        }

        uniqueZones.Clear();
        zones.Clear();
        foreach (LaunchPlayer player in peers.Players)
        {
            if (!playerPosition(player.SteamId, out Vector2 position))
            {
                continue;
            }

            Vector2Int center = world!.WorldToBlockPos(position);
            int minX = Math.Max(1, center.x - 64) / ZoneSize;
            int minY = Math.Max(1, center.y - 64) / ZoneSize;
            int maxX = Math.Min(cells!.GetLength(0) - 2, center.x + 64) / ZoneSize;
            int maxY = Math.Min(cells.GetLength(1) - 2, center.y + 64) / ZoneSize;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Vector2Int zone = new(x, y);
                    if (uniqueZones.Add(zone))
                    {
                        zones.Add(zone);
                    }
                }
            }
        }

        zones.Sort(CompareZones);
        foreach (Vector2Int zone in zones)
        {
            int fromX = Math.Max(1, zone.x * ZoneSize);
            int toX = Math.Min(cells!.GetLength(0) - 1, (zone.x + 1) * ZoneSize);
            int fromY = Math.Max(1, zone.y * ZoneSize + strip);
            int toY = Math.Min(cells.GetLength(1) - 1, zone.y * ZoneSize + strip + StripHeight);
            if (fromX < toX && fromY < toY)
            {
                TerrainFluidHooks.Step(manager, new RangeI(fromX, toX), new RangeI(fromY, toY));
            }
        }

        strip = (strip + StripHeight) % ZoneSize;
        return true;
    }

    private static int CompareZones(Vector2Int a, Vector2Int b)
    {
        int y = a.y.CompareTo(b.y);
        return y != 0 ? y : a.x.CompareTo(b.x);
    }

    private void SendNearby(ulong peer, Vector2Int center, float now)
    {
        var updates = schedule.Plan(peer, center.x, center.y, cells!.GetLength(0), cells.GetLength(1), now, captureRevision);
        for (int index = 0; index < updates.Count; index++)
        {
            FluidChunkSchedule.Update update = updates[index];
            var key = update.Key;
            byte[] frame = FluidWire.WriteChunk(peers.SessionId, key.X, key.Y, update.Revision, chunks[key].Cells);
            if (peers.Send(peer, frame, update.Reliable))
            {
                schedule.RecordAccepted(peer, update, now);
            }
        }
    }

    private uint CaptureRevision((int X, int Y) key) => Capture(key).Version;


    private Chunk Capture((int X, int Y) key)
    {
        if (!chunks.TryGetValue(key, out Chunk chunk))
        {
            chunk = new Chunk();
            chunks.Add(key, chunk);
        }
        if (chunk.ScanRound == scanRound)
        {
            return chunk;
        }

        Span<byte> packed = stackalloc byte[FluidWire.PackedSize];
        packed.Clear();
        int startX = key.X * Size;
        int startY = key.Y * Size;
        int width = Math.Min(Size, cells!.GetLength(0) - startX);
        int height = Math.Min(Size, cells.GetLength(1) - startY);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                FluidWire.Pack(packed, y * Size + x, cells[startX + x, startY + y]);
            }
        }

        if (!packed.SequenceEqual(chunk.Cells))
        {
            packed.CopyTo(chunk.Cells);
            chunk.Version++;
        }
        chunk.ScanRound = scanRound;
        return chunk;
    }

    internal bool Receive(ulong sender, byte[] frame)
    {
        MessageKind kind = (MessageKind)frame[0];
        if (host)
        {
            if (kind != MessageKind.FluidDrinkRequest || !peers.TryGetPlayer(sender, out _))
            {
                return false;
            }

            if (IsBound(FluidManager.main) && FluidWire.TryReadDrink(frame, kind, peers.SessionId,
                    out ulong id, out int x, out int y, out byte expected))
            {
                RespondToDrink(sender, id, new Vector2Int(x, y), expected);
            }
            return true;
        }

        if (sender != peers.Host)
        {
            return false;
        }

        // The host starts streaming after this guest has reported an in-world position.
        if (!Bind() || !IsBound(FluidManager.main))
        {
            return true;
        }

        switch (kind)
        {
            case MessageKind.FluidChunk:
                if (FluidWire.TryReadChunk(frame, peers.SessionId, out int chunkX, out int chunkY, out uint version, out int offset))
                {
                    ApplyChunk((chunkX, chunkY), version, frame.AsSpan(offset));
                }
                return true;
            case MessageKind.FluidPush:
                if (FluidWire.TryReadPush(frame, peers.SessionId, out int flowX, out int flowY, out byte direction))
                {
                    ApplyPush(new Vector2Int(flowX, flowY), direction);
                }
                return true;
            case MessageKind.FluidDrinkResult:
                if (FluidWire.TryReadDrink(frame, kind, peers.SessionId, out ulong id, out int x, out int y, out byte liquid))
                {
                    ApplyDrink(id, new Vector2Int(x, y), liquid);
                }
                return true;
            default:
                return false;
        }
    }

    private void ApplyChunk((int X, int Y) key, uint revision, ReadOnlySpan<byte> packed)
    {
        int startX = key.X * Size;
        int startY = key.Y * Size;
        if (startX >= cells!.GetLength(0) || startY >= cells.GetLength(1) ||
            !progress.TryObserve(key, revision))
        {
            return;
        }

        int width = Math.Min(Size, cells.GetLength(0) - startX);
        int height = Math.Min(Size, cells.GetLength(1) - startY);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                cells[startX + x, startY + y] = FluidWire.Unpack(packed, y * Size + x);
            }
        }
    }

    internal void NoteFlow(FluidManager manager, Vector2 direction, Vector2Int cell)
    {
        if (!host || !IsBound(manager) || !InBounds(cell) || flow.Count >= 128)
        {
            return;
        }

        Vector2Int nearby = new(cell.x / 3, cell.y / 3);
        byte axis = direction.x < 0 ? (byte)0 : direction.x > 0 ? (byte)1 : (byte)2;
        flow[nearby] = (cell, axis);
    }

    private void SendFlow(ulong peer, Vector2 position)
    {
        int sent = 0;
        foreach (var push in flow.Values)
        {
            if ((world!.BlockToWorldPos(push.Cell) - position).sqrMagnitude <= 18f * 18f)
            {
                peers.Send(peer, FluidWire.WritePush(peers.SessionId, push.Cell.x, push.Cell.y, push.Direction), reliable: false);
                if (++sent >= 6)
                {
                    return;
                }
            }
        }
    }

    private void ApplyPush(Vector2Int cell, byte direction)
    {
        if (!InBounds(cell) || !PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Vector2 at = world!.BlockToWorldPos(cell);
        if ((at - (Vector2)PlayerCamera.main.body.transform.position).sqrMagnitude > 18f * 18f)
        {
            return;
        }

        receivedPushers.RemoveAll(pusher => !pusher);
        if (receivedPushers.Count >= 48)
        {
            return;
        }

        GameObject push = new("Co-op current", typeof(CircleCollider2D), typeof(WaterPusher));
        push.transform.position = at;
        CircleCollider2D trigger = push.GetComponent<CircleCollider2D>();
        trigger.isTrigger = true;
        trigger.radius = 1.5f;
        push.GetComponent<WaterPusher>().direction = direction == 0 ? Vector2.left : direction == 1 ? Vector2.right : Vector2.down;
        receivedPushers.Add(push);
        UnityEngine.Object.Destroy(push, 0.75f);
    }

    internal bool AllowDrink(Vector2Int cell, Body body)
    {
        if (!IsGuest || applyingDrink)
        {
            return true;
        }

        if (PlayerCamera.main && body == PlayerCamera.main.body && pendingDrink is null && InBounds(cell) &&
            cells![cell.x, cell.y] is > 0 and <= 7 && peers.IsVerified(peers.Host))
        {
            pendingDrink = (++drinkNumber, cell, cells[cell.x, cell.y]);
            SendDrink();
        }
        return false;
    }

    private void SendDrink()
    {
        if (pendingDrink is { } drink)
        {
            nextDrinkRetry = clock.Now + 1f;
            peers.Send(peers.Host, FluidWire.WriteDrink(MessageKind.FluidDrinkRequest, peers.SessionId,
                drink.Operation, drink.Cell.x, drink.Cell.y, drink.Liquid));
        }
    }

    private void RespondToDrink(ulong peer, ulong operation, Vector2Int cell, byte expected)
    {
        if (!drinks.TryDecide(peer, operation, cell.x, cell.y, expected, () => TryConsume(peer, cell, expected),
            out FluidDrinkAuthority.Receipt receipt, out bool executed))
        {
            return;
        }

        CoopDiagnostics.Count("Fluids", executed ? receipt.Liquid == 0 ? "Drink rejected" : "Drink confirmed" : "Drink replay");
        peers.Send(peer, FluidWire.WriteDrink(MessageKind.FluidDrinkResult, peers.SessionId,
            operation, receipt.X, receipt.Y, receipt.Liquid));
    }

    private bool TryConsume(ulong peer, Vector2Int cell, byte expected)
    {
        if (!InBounds(cell) || cells![cell.x, cell.y] != expected || !playerPosition(peer, out Vector2 at) ||
            (at - world!.BlockToWorldPos(cell)).sqrMagnitude > 4f * 4f)
        {
            return false;
        }

        if (expected != 3 && expected != 6)
        {
            cells[cell.x, cell.y] = 0;
            nextNetwork = 0;
        }
        return true;
    }


    private void ApplyDrink(ulong operation, Vector2Int cell, byte liquid)
    {
        if (pendingDrink is not { } request || request.Operation != operation || request.Cell != cell)
        {
            return;
        }

        pendingDrink = null;
        if (liquid != request.Liquid || !InBounds(cell) || !PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        byte previous = cells![cell.x, cell.y];
        applyingDrink = true;
        try
        {
            cells[cell.x, cell.y] = liquid;
            FluidManager.main.DrinkLiquid(cell, PlayerCamera.main.body);
        }
        finally
        {
            cells[cell.x, cell.y] = previous;
            applyingDrink = false;
        }
    }

    private bool InBounds(Vector2Int cell) => cells is not null && cell.x >= 0 && cell.y >= 0 &&
        cell.x < cells.GetLength(0) && cell.y < cells.GetLength(1);

    private void OnPeerChanged(ulong peer, bool connected)
    {
        if (connected)
        {
            ResendNearby(peer);
        }
        else
        {
            schedule.Forget(peer);
        }
    }

    public void Dispose()
    {
        peers.ConnectionChanged -= OnPeerChanged;
        DestroyPushers();
    }

    private void DestroyPushers()
    {
        foreach (GameObject push in receivedPushers)
        {
            if (push)
            {
                UnityEngine.Object.Destroy(push);
            }
        }
        receivedPushers.Clear();
    }
}
