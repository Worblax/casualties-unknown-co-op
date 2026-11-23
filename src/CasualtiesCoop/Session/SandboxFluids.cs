using System;
using System.Collections.Generic;
using CasualtiesCoop.Game;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Session;

internal sealed class SandboxFluids : IDisposable
{
    private const int ZoneSize = 128;
    private const int StripHeight = 16;
    private const int InterestRadius = 72;
    private const int ChunkBudget = 12;
    private const float NetworkInterval = 0.12f;
    private const float RepairInterval = 2.5f;
    private const int Size = FluidWire.ChunkSize;

    private sealed class Chunk
    {
        internal readonly byte[] Cells = new byte[FluidWire.PackedSize];
        internal uint Version = 1;
        internal uint ScanRound;
    }

    private sealed class SentChunk
    {
        internal uint Version;
        internal float LastSent;
        internal float LastFull;
    }

    private readonly SteamLobby lobby;
    private readonly RoomLaunchPlan plan;
    private readonly SandboxItems.PlayerPosition playerPosition;
    private readonly bool host;
    private readonly HashSet<Vector2Int> uniqueZones = new();
    private readonly List<Vector2Int> zones = new();
    private readonly Dictionary<Vector2Int, Chunk> chunks = new();
    private readonly Dictionary<CSteamID, Dictionary<Vector2Int, SentChunk>> subscriptions = new();
    private readonly Dictionary<Vector2Int, uint> applied = new();
    private readonly List<(Vector2Int Key, Chunk Chunk, SentChunk? Sent)> candidates = new();
    private readonly List<Vector2Int> expired = new();
    private readonly Dictionary<Vector2Int, (Vector2Int Cell, byte Direction)> flow = new();
    private readonly List<GameObject> receivedPushers = new();
    private readonly Dictionary<CSteamID, (ulong Operation, Vector2Int Cell, byte Liquid, float At)> drinks = new();
    private byte[,]? cells;
    private WorldGeneration? world;
    private int strip;
    private uint scanRound;
    private float nextNetwork;
    private ulong drinkNumber;
    private (ulong Operation, Vector2Int Cell, byte Liquid)? pendingDrink;
    private float nextDrinkRetry;
    private bool applyingDrink;

    internal SandboxFluids(SteamLobby lobby, RoomLaunchPlan plan, SandboxItems.PlayerPosition playerPosition)
    {
        this.lobby = lobby;
        this.plan = plan;
        this.playerPosition = playerPosition;
        host = lobby.IsHost;
        lobby.Link.PeerChanged += OnPeerChanged;
    }

    internal bool IsGuest => !host && IsBound(FluidManager.main);

    private bool IsBound(FluidManager? manager) => manager && cells is not null &&
        ReferenceEquals(manager!.fluid, cells) && world && world == WorldGeneration.world &&
        world!.worldExists && !world.generatingWorld;

    internal void Tick()
    {
        if (!Bind() || !IsBound(FluidManager.main))
        {
            return;
        }

        float now = Time.unscaledTime;
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
        foreach (LaunchPlayer player in plan.Players)
        {
            CSteamID peer = new(player.SteamId);
            if (peer != lobby.LocalUser && lobby.Link.GetState(peer) == RoomLinkState.Verified &&
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
        if (cells is not null)
        {
            return true;
        }

        WorldGeneration currentWorld = WorldGeneration.world;
        FluidManager manager = FluidManager.main;
        if (!currentWorld || !currentWorld.worldExists || currentWorld.generatingWorld || !manager ||
            manager.fluid is not { } map || map.GetLength(0) is < 3 or > ushort.MaxValue ||
            map.GetLength(1) is < 3 or > ushort.MaxValue)
        {
            return false;
        }

        world = currentWorld;
        cells = map;
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
        foreach (LaunchPlayer player in plan.Players)
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
                SandboxFluidHooks.Step(manager, new RangeI(fromX, toX), new RangeI(fromY, toY));
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

    private void SendNearby(CSteamID peer, Vector2Int center, float now)
    {
        if (!subscriptions.TryGetValue(peer, out Dictionary<Vector2Int, SentChunk> sent))
        {
            sent = new Dictionary<Vector2Int, SentChunk>();
            subscriptions.Add(peer, sent);
        }

        int minX = Math.Max(0, center.x - InterestRadius) / Size;
        int minY = Math.Max(0, center.y - InterestRadius) / Size;
        int maxX = Math.Min(cells!.GetLength(0) - 1, center.x + InterestRadius) / Size;
        int maxY = Math.Min(cells.GetLength(1) - 1, center.y + InterestRadius) / Size;
        expired.Clear();
        foreach (Vector2Int key in sent.Keys)
        {
            if (key.x < minX || key.x > maxX || key.y < minY || key.y > maxY)
            {
                expired.Add(key);
            }
        }
        foreach (Vector2Int key in expired)
        {
            sent.Remove(key);
        }

        candidates.Clear();
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2Int key = new(x, y);
                Chunk chunk = Capture(key);
                sent.TryGetValue(key, out SentChunk? previous);
                if (previous is null || previous.Version != chunk.Version || now - previous.LastFull >= RepairInterval)
                {
                    candidates.Add((key, chunk, previous));
                }
            }
        }

        Vector2Int centerChunk = new(center.x / Size, center.y / Size);
        candidates.Sort((a, b) =>
        {
            int order = (a.Sent?.LastSent ?? -1f).CompareTo(b.Sent?.LastSent ?? -1f);
            return order != 0 ? order : (a.Key - centerChunk).sqrMagnitude.CompareTo((b.Key - centerChunk).sqrMagnitude);
        });

        for (int index = 0; index < Math.Min(ChunkBudget, candidates.Count); index++)
        {
            var update = candidates[index];
            bool reliable = update.Sent is null || now - update.Sent.LastFull >= RepairInterval;
            byte[] frame = FluidWire.WriteChunk(plan.Id, update.Key.x, update.Key.y, update.Chunk.Version, update.Chunk.Cells);
            if (!lobby.Link.Send(peer, frame, reliable))
            {
                continue;
            }

            SentChunk stamp = update.Sent ?? new SentChunk();
            stamp.Version = update.Chunk.Version;
            stamp.LastSent = now;
            if (reliable)
            {
                stamp.LastFull = now;
            }
            sent[update.Key] = stamp;
        }
    }

    private Chunk Capture(Vector2Int key)
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
        int startX = key.x * Size;
        int startY = key.y * Size;
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

    internal bool Receive(CSteamID sender, byte[] frame)
    {
        RoomFrame kind = (RoomFrame)frame[0];
        if (host)
        {
            if (kind != RoomFrame.FluidDrinkRequest || !plan.TryGetPlayer(sender.m_SteamID, out _))
            {
                return false;
            }

            if (IsBound(FluidManager.main) && FluidWire.TryReadDrink(frame, kind, plan.Id,
                    out ulong id, out int x, out int y, out byte expected))
            {
                RespondToDrink(sender, id, new Vector2Int(x, y), expected);
            }
            return true;
        }

        if (sender != lobby.Owner)
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
            case RoomFrame.FluidChunk:
                if (FluidWire.TryReadChunk(frame, plan.Id, out int chunkX, out int chunkY, out uint version, out int offset))
                {
                    ApplyChunk(new Vector2Int(chunkX, chunkY), version, frame.AsSpan(offset));
                }
                return true;
            case RoomFrame.FluidPush:
                if (FluidWire.TryReadPush(frame, plan.Id, out int flowX, out int flowY, out byte direction))
                {
                    ApplyPush(new Vector2Int(flowX, flowY), direction);
                }
                return true;
            case RoomFrame.FluidDrinkResult:
                if (FluidWire.TryReadDrink(frame, kind, plan.Id, out ulong id, out int x, out int y, out byte liquid))
                {
                    ApplyDrink(id, new Vector2Int(x, y), liquid);
                }
                return true;
            default:
                return false;
        }
    }

    private void ApplyChunk(Vector2Int key, uint revision, ReadOnlySpan<byte> packed)
    {
        int startX = key.x * Size;
        int startY = key.y * Size;
        if (startX >= cells!.GetLength(0) || startY >= cells.GetLength(1) ||
            (applied.TryGetValue(key, out uint current) && revision < current))
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
        applied[key] = revision;
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

    private void SendFlow(CSteamID peer, Vector2 position)
    {
        int sent = 0;
        foreach (var push in flow.Values)
        {
            if ((world!.BlockToWorldPos(push.Cell) - position).sqrMagnitude <= 18f * 18f)
            {
                lobby.Link.Send(peer, FluidWire.WritePush(plan.Id, push.Cell.x, push.Cell.y, push.Direction), reliable: false);
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
            cells![cell.x, cell.y] is > 0 and <= 7 && lobby.Link.GetState(lobby.Owner) == RoomLinkState.Verified)
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
            nextDrinkRetry = Time.unscaledTime + 1f;
            lobby.Link.Send(lobby.Owner, FluidWire.WriteDrink(RoomFrame.FluidDrinkRequest, plan.Id,
                drink.Operation, drink.Cell.x, drink.Cell.y, drink.Liquid));
        }
    }

    private void RespondToDrink(CSteamID peer, ulong operation, Vector2Int cell, byte expected)
    {
        float now = Time.unscaledTime;
        drinks.TryGetValue(peer, out var receipt);
        if (operation > receipt.Operation)
        {
            byte liquid = 0;
            float lastAccepted = receipt.At;
            if ((receipt.Operation == 0 || now - lastAccepted >= 0.65f) && InBounds(cell) && expected != 0 &&
                cells![cell.x, cell.y] == expected && playerPosition(peer.m_SteamID, out Vector2 at) &&
                (at - world!.BlockToWorldPos(cell)).sqrMagnitude <= 4f * 4f)
            {
                liquid = expected;
                lastAccepted = now;
                if (expected != 3 && expected != 6)
                {
                    cells[cell.x, cell.y] = 0;
                    nextNetwork = 0;
                }
            }
            receipt = (operation, cell, liquid, lastAccepted);
            drinks[peer] = receipt;
        }

        if (receipt.Operation == operation)
        {
            lobby.Link.Send(peer, FluidWire.WriteDrink(RoomFrame.FluidDrinkResult, plan.Id,
                operation, receipt.Cell.x, receipt.Cell.y, receipt.Liquid));
        }
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

    private void OnPeerChanged(CSteamID peer, bool connected)
    {
        subscriptions.Remove(peer);
        if (connected)
        {
            nextNetwork = 0;
        }
    }

    public void Dispose()
    {
        lobby.Link.PeerChanged -= OnPeerChanged;
        foreach (GameObject push in receivedPushers)
        {
            if (push)
            {
                UnityEngine.Object.Destroy(push);
            }
        }
    }
}
