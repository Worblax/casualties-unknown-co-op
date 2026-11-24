using System;
using System.Collections.Generic;
using CasualtiesCoop.Core.World;
using CasualtiesCoop.Networking;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Terrain;

internal sealed class UnityWorldChunkGrid : IWorldChunkGrid
{
    private readonly WorldGeneration world;
    private byte[,] liquidCells;
    private readonly HashSet<Vector2Int> redraw = new();

    internal UnityWorldChunkGrid(WorldGeneration world, byte[,] liquidCells)
    {
        this.world = world;
        this.liquidCells = liquidCells;
    }

    public int Width => liquidCells.GetLength(0);
    public int Height => liquidCells.GetLength(1);
    internal bool Applying { get; private set; }

    internal static bool TryBind(out WorldGeneration world, out byte[,] cells)
    {
        world = WorldGeneration.world;
        cells = FluidManager.main ? FluidManager.main.fluid : null!;
        return world && world.worldExists && !world.generatingWorld && cells is not null &&
            cells.GetLength(0) == world.width && cells.GetLength(1) == world.height &&
            world.width is > 0 and <= ushort.MaxValue && world.height is > 0 and <= ushort.MaxValue;
    }

    internal bool MatchesWorld(WorldGeneration candidate, byte[,] cells) => world == candidate &&
        Width == cells.GetLength(0) && Height == cells.GetLength(1);

    internal bool Rebind(byte[,] cells)
    {
        if (ReferenceEquals(liquidCells, cells)) return false;
        liquidCells = cells;
        return true;
    }

    public bool Read(WorldChunkKey key, Span<ushort> blocks, Span<float> damage, Span<byte> liquid)
    {
        if (!WorldChunkGrid.Extents(this, key, out int width, out int height)) return false;
        blocks.Clear();
        damage.Clear();
        liquid.Clear();
        const int side = WorldChunkWire.Side;

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int worldX = key.StartX + x, worldY = key.StartY + y;
            int i = y * side + x;
            blocks[i] = world.GetBlock(new Vector2Int(worldX, worldY));
            byte value = liquidCells[worldX, worldY];
            if (value > 7) return false;
            liquid[i] = value;
        }

        foreach (BlockDamage crack in world.blockDamages)
        {
            int x = crack.pos.x - key.StartX, y = crack.pos.y - key.StartY;
            if (x >= 0 && x < width && y >= 0 && y < height && blocks[y * side + x] != 0 &&
                float.IsFinite(crack.damage) && crack.damage > 0)
                damage[y * side + x] = Math.Min(crack.damage, 100000f);
        }
        return true;
    }

    public bool Apply(WorldChunkHeader header, ReadOnlySpan<ushort> blocks, ReadOnlySpan<float> damage, ReadOnlySpan<byte> liquid)
    {
        WorldChunkKey key = header.Key;
        if (!WorldChunkGrid.Extents(this, key, out int width, out int height) ||
            width != header.Width || height != header.Height || world.tiles is null) return false;
        for (int i = 0; i < WorldChunkWire.CellCount; i++)
            if (blocks[i] >= world.tiles.Length || (damage[i] > 0 && world.GetBlockInfo(blocks[i]) is null)) return false;

        Applying = true;
        try
        {
            RemoveOldCracks(key, blocks, damage);
            redraw.Clear();
            const int side = WorldChunkWire.Side;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * side + x;
                var at = new Vector2Int(key.StartX + x, key.StartY + y);
                if (world.GetBlock(at) != blocks[i]) SetMaterial(at, blocks[i]);
                if (damage[i] > 0) SetDamage(at, damage[i]);
            }
            foreach (Vector2Int chunk in redraw) world.UpdateChunk(chunk);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                liquidCells[key.StartX + x, key.StartY + y] = liquid[y * side + x];
        }
        finally
        {
            Applying = false;
        }
        return true;
    }

    private void SetMaterial(Vector2Int at, ushort block)
    {
        // SetBlockNoUpdate clamps the outermost row/column; preserve the exact edge coordinate.
        if (at.x >= Width - 1 || at.y >= Height - 1) world.SetBlock(at, block);
        else
        {
            world.SetBlockNoUpdate(at, block);
            redraw.Add(world.BlockToChunkPos(at));
        }
    }

    private void RemoveOldCracks(WorldChunkKey key, ReadOnlySpan<ushort> blocks, ReadOnlySpan<float> damage)
    {
        const int side = WorldChunkWire.Side;
        for (int i = world.blockDamages.Count - 1; i >= 0; i--)
        {
            BlockDamage old = world.blockDamages[i];
            int x = old.pos.x - key.StartX, y = old.pos.y - key.StartY;
            if (x is < 0 or >= side || y is < 0 or >= side) continue;
            int cell = y * side + x;
            if (damage[cell] > 0 && world.GetBlock(old.pos) == blocks[cell]) continue;
            old.DestroySprite();
            world.blockDamages.RemoveAt(i);
        }
    }

    private void SetDamage(Vector2Int at, float damage)
    {
        BlockDamage? crack = null;
        foreach (BlockDamage existing in world.blockDamages)
            if (existing.pos == at) { crack = existing; break; }
        if (crack is null)
        {
            // Old cracks outside the subscription can survive until revisited. Preserve the native bound.
            if (world.blockDamages.Count >= 128)
            {
                world.blockDamages[0].DestroySprite();
                world.blockDamages.RemoveAt(0);
            }
            crack = new BlockDamage { pos = at };
            world.blockDamages.Add(crack);
        }
        if (crack.damage == damage) return;
        crack.damage = damage;
        crack.UpdateSprite();
    }
}
