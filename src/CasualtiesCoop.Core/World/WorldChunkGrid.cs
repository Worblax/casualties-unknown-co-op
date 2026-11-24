using System;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core.World;

internal interface IWorldChunkGrid
{
    int Width { get; }
    int Height { get; }
    bool Read(WorldChunkKey key, Span<ushort> blocks, Span<float> damage, Span<byte> liquid);
    bool Apply(WorldChunkHeader header, ReadOnlySpan<ushort> blocks, ReadOnlySpan<float> damage, ReadOnlySpan<byte> liquid);
}

internal static class WorldChunkGrid
{
    internal static bool Extents(IWorldChunkGrid grid, WorldChunkKey key, out int width, out int height)
    {
        width = Math.Min(WorldChunkWire.Side, grid.Width - key.StartX);
        height = Math.Min(WorldChunkWire.Side, grid.Height - key.StartY);
        return key.X >= 0 && key.Y >= 0 && width > 0 && height > 0;
    }
}

internal sealed class WorldChunkMetrics
{
    internal int ActiveChunks, Subscriptions, Pending;
    internal long Frames, Bytes, Backpressure, Repairs, Rebuilds, Applied, Resets, Captures;
    internal long CaptureTicks, EncodeTicks, ApplyTicks;
    internal float LastAcknowledgedSeconds, InitialSyncSeconds;
}
