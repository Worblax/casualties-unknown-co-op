using System;
using System.Collections.Generic;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core.World;

internal readonly struct WorldInterestOptions
{
    internal const int NativeSimulationRadius = 64;
    internal readonly int Margin;
    internal readonly int MaxCameraHalfExtent;
    internal readonly int MinimumRadius;

    internal WorldInterestOptions(int margin, int maxCameraHalfExtent, int minimumRadius)
    {
        if (margin is < 8 or > 64 || maxCameraHalfExtent is < 24 or > 160 ||
            minimumRadius is < 8 or > 128) throw new ArgumentOutOfRangeException(nameof(margin));
        Margin = margin;
        MaxCameraHalfExtent = maxCameraHalfExtent;
        MinimumRadius = minimumRadius;
    }

    internal static WorldInterestOptions Sandbox => new(24, 112, NativeSimulationRadius);
    internal int Radius(int camera) => Math.Max(MinimumRadius, Math.Clamp(camera, 16, MaxCameraHalfExtent) + Margin);
}

internal readonly struct ChunkRectangle
{
    internal readonly int MinX, MinY, MaxX, MaxY;
    internal ChunkRectangle(int minX, int minY, int maxX, int maxY)
    {
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    internal bool Contains(WorldChunkKey key) => key.X >= MinX && key.X <= MaxX && key.Y >= MinY && key.Y <= MaxY;

    internal static ChunkRectangle Around(int x, int y, int rx, int ry, int width, int height) =>
        new(Math.Max(0, x - rx) / WorldChunkWire.Side, Math.Max(0, y - ry) / WorldChunkWire.Side,
            Math.Min(width - 1, x + rx) / WorldChunkWire.Side, Math.Min(height - 1, y + ry) / WorldChunkWire.Side);
}

internal sealed class WorldInterest
{
    private const int Hysteresis = 3;
    private const int ExitPadding = WorldChunkWire.Side;
    private readonly WorldInterestOptions options;
    private readonly int width, height;
    private readonly HashSet<WorldChunkKey> keys = new();
    private readonly List<WorldChunkKey> ordered = new();
    private int anchorX, anchorY, radiusX, radiusY;
    private bool initialized;

    internal WorldInterest(int width, int height, WorldInterestOptions options)
    {
        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width));
        this.width = width;
        this.height = height;
        this.options = options;
    }

    internal IReadOnlyList<WorldChunkKey> Ordered => ordered;
    internal bool Contains(WorldChunkKey key) => keys.Contains(key);

    internal bool Update(int x, int y, int cameraX, int cameraY)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var center = WorldChunkKey.At(x, y);
        int rx = options.Radius(cameraX), ry = options.Radius(cameraY);
        if (initialized && rx == radiusX && ry == radiusY &&
            x >= anchorX - Hysteresis && x < anchorX + WorldChunkWire.Side + Hysteresis &&
            y >= anchorY - Hysteresis && y < anchorY + WorldChunkWire.Side + Hysteresis) return false;

        anchorX = center.StartX;
        anchorY = center.StartY;
        radiusX = rx;
        radiusY = ry;
        initialized = true;
        // Include all camera centers that still lie inside the deadband.
        const int reach = WorldChunkWire.Side + Hysteresis;
        var enter = ChunkRectangle.Around(anchorX, anchorY, rx + reach, ry + reach, width, height);
        var exit = ChunkRectangle.Around(anchorX, anchorY, rx + reach + ExitPadding, ry + reach + ExitPadding, width, height);
        keys.RemoveWhere(key => !exit.Contains(key));
        for (int row = enter.MinY; row <= enter.MaxY; row++)
        for (int column = enter.MinX; column <= enter.MaxX; column++) keys.Add(new WorldChunkKey(column, row));

        ordered.Clear();
        ordered.AddRange(keys);
        ordered.Sort((a, b) =>
        {
            int order = Distance(a, center).CompareTo(Distance(b, center));
            if (order != 0) return order;
            order = a.Y.CompareTo(b.Y);
            return order != 0 ? order : a.X.CompareTo(b.X);
        });
        return true;
    }

    private static int Distance(WorldChunkKey key, WorldChunkKey center)
    {
        int dx = center.X - key.X, dy = center.Y - key.Y;
        return dx * dx + dy * dy;
    }
}
