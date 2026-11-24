using System;
using BepInEx.Configuration;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.World;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Networking;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Terrain;

internal sealed class WorldChunkReplication
{
    private static WorldInterestOptions options = WorldInterestOptions.Sandbox;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly PlayerPositionLookup position;
    private readonly HostWorldChunks.ChunkCenter center;
    private readonly IWorldChunkGrid? fixture;
    private UnityWorldChunkGrid? native;
    private HostWorldChunks? host;
    private GuestWorldChunks? guest;
    private Guid recoveryStream;
    private bool suspended;
    private bool ready;
    private float nextReport;
    private float initialDeadline;
    private int initialRetries;

    internal WorldChunkReplication(SessionPeers peers, IMonotonicClock clock, PlayerPositionLookup position, IWorldChunkGrid? fixture = null)
    {
        this.peers = peers;
        this.clock = clock;
        this.position = position;
        this.fixture = fixture;
        center = FindCenter;
    }

    internal static void Configure(ConfigFile config)
    {
        var margin = config.Bind("World streaming", "Prefetch cells", 24, "Extra cells past the reported camera on each side.");
        var maxView = config.Bind("World streaming", "Maximum camera half extent", 112, "Server limit for a reported camera extent in cells.");
        options = new WorldInterestOptions(Math.Clamp(margin.Value, 8, 64), Math.Clamp(maxView.Value, 24, 160),
            WorldInterestOptions.NativeSimulationRadius);
    }

    internal static bool Handles(MessageKind kind) => kind is MessageKind.WorldChunkControl or MessageKind.WorldChunkSnapshot;
    internal bool Applying => native?.Applying == true;
    internal bool Ready => peers.IsHost || ready;
    internal WorldChunkMetrics? Metrics => host?.Metrics ?? guest?.Metrics;
    internal Guid Stream => guest?.Stream ?? Guid.Empty;
    internal WorldInterest? Interest(ParticipantId participant) => host?.Interest(participant) ?? guest?.Interest;

    internal bool IsAcknowledged(ParticipantId participant, WorldChunkKey key) => host?.IsAcknowledged(participant, key) == true;

    internal void Forget(ulong address)
    {
        host?.Forget(address);
        if (!peers.IsHost && address == peers.Host) Suspend();
    }

    internal void Suspend()
    {
        suspended = true;
        ready = false;
        recoveryStream = Guid.Empty;
        guest?.Begin(Guid.Empty, recovery: true);
    }

    internal void Resume(Guid attempt)
    {
        suspended = false;
        ready = false;
        recoveryStream = attempt;
        initialRetries = 0;
        initialDeadline = clock.Now + GuestRecovery.AttemptTimeout;
        guest?.Begin(attempt == Guid.Empty ? Guid.NewGuid() : attempt, recovery: attempt != Guid.Empty);
    }

    internal void CompleteRecovery()
    {
        recoveryStream = Guid.Empty;
        guest?.CompleteRecovery();
    }

    internal bool HasNear(Vector2 at)
    {
        var world = WorldGeneration.world;
        if (!world || guest is null) return peers.IsHost;
        Vector2Int cell = world.WorldToBlockPos(at);
        return guest.HasNear(cell.x, cell.y, 2);
    }

    // Initial readiness keeps recovery/startup from waiting forever after the first useful
    // snapshot. Interactive item operations use current chunk freshness instead.
    internal bool IsCurrentNear(Vector2 at) => peers.IsHost || HasNear(at);

    internal bool IsCurrentAtPlayer()
    {
        if (peers.IsHost) return true;
        return position(peers.LocalPlayer, out Vector2 at) && IsCurrentNear(at);
    }

    internal void MarkChanged(Vector2Int cell)
    {
        if (cell.x >= 0 && cell.y >= 0) host?.MarkDirty(WorldChunkKey.At(cell.x, cell.y));
    }

    internal bool Receive(ulong sender, byte[] frame, Guid requiredStream = default)
    {
        if (!Bind()) return true;
        return peers.IsHost ? (MessageKind)frame[0] == MessageKind.WorldChunkControl && host!.Receive(sender, frame, requiredStream)
            : (MessageKind)frame[0] == MessageKind.WorldChunkSnapshot && guest!.Receive(sender, frame);
    }

    internal void Tick()
    {
        if (!Bind()) return;
        if (host is not null)
        {
            host.Tick(center);
            ReportMetrics(host.Metrics);
        }
        else if (!suspended && FindCenter(peers.LocalPlayer, out int x, out int y))
        {
            var camera = Camera.main;
            float halfHeight = camera && camera.orthographic ? camera.orthographicSize : 32f;
            float halfWidth = camera ? halfHeight * camera.aspect : 56f;
            if (!ready && recoveryStream == Guid.Empty && clock.Now >= initialDeadline)
            {
                if (initialRetries++ == 2)
                {
                    Suspend();
                    if (PlayerCamera.main) PlayerCamera.main.DoAlert("World sync timed out. Return to the menu and rejoin the room.");
                    return;
                }
                guest!.Begin(Guid.NewGuid(), recovery: false);
                initialDeadline = clock.Now + GuestRecovery.AttemptTimeout;
            }
            Guid before = guest!.Stream;
            guest.Tick(x, y, Extent(halfWidth), Extent(halfHeight));
            if (before != guest.Stream)
            {
                ready = false;
                initialDeadline = clock.Now + GuestRecovery.AttemptTimeout;
            }
            if (!ready && guest.HasNear(x, y, 2)) ready = true;
            ReportMetrics(guest.Metrics);
        }
    }

    private void ReportMetrics(WorldChunkMetrics m)
    {
        if (!CoopDiagnostics.Enabled || clock.Now < nextReport) return;
        nextReport = clock.Now + 10f;
        double ms = 1000d / System.Diagnostics.Stopwatch.Frequency;
        CoopDiagnostics.Trace("WorldChunks", $"active={m.ActiveChunks} subscriptions={m.Subscriptions} pending={m.Pending} " +
            $"frames={m.Frames} bytes={m.Bytes} backpressure={m.Backpressure} repairs={m.Repairs} streams={m.Resets} " +
            $"initial={m.InitialSyncSeconds:F2}s ack={m.LastAcknowledgedSeconds:F2}s reads={m.Captures} " +
            $"capture={m.CaptureTicks * ms:F1}ms encode={m.EncodeTicks * ms:F1}ms apply={m.ApplyTicks * ms:F1}ms");
    }

    private static int Extent(float value) => float.IsFinite(value) ? Mathf.Clamp(Mathf.CeilToInt(value), 1, 512) : 64;

    private bool FindCenter(ulong address, out int x, out int y)
    {
        x = y = 0;
        if (!position(address, out Vector2 positionInWorld)) return false;
        Vector2Int cell = WorldGeneration.world.WorldToBlockPos(positionInWorld);
        x = cell.x;
        y = cell.y;
        return true;
    }

    private bool Bind()
    {
        if (!UnityWorldChunkGrid.TryBind(out var world, out var cells)) return false;
        if (native is not null)
        {
            if (!native.MatchesWorld(world, cells)) return false;
            if (native.Rebind(cells) && !peers.IsHost && recoveryStream == Guid.Empty && !suspended) Resume(Guid.Empty);
            return true;
        }
        native = new UnityWorldChunkGrid(world, cells);
        IWorldChunkGrid grid = fixture ?? native;
        if (peers.IsHost)
            host = new HostWorldChunks(peers, clock, grid, options);
        else
        {
            ready = false;
            guest = new GuestWorldChunks(peers, clock, grid, options);
            initialDeadline = clock.Now + GuestRecovery.AttemptTimeout;
            if (!suspended) guest.Begin(recoveryStream == Guid.Empty ? Guid.NewGuid() : recoveryStream, recoveryStream != Guid.Empty);
        }
        return true;
    }
}
