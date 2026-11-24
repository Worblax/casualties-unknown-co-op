using CasualtiesCoop.Core.World;
using CasualtiesCoop.Networking;

internal sealed class TestChunkGrid : IWorldChunkGrid
{
    private sealed class Chunk
    {
        internal readonly ushort[] Blocks = new ushort[WorldChunkWire.CellCount];
        internal readonly float[] Damage = new float[WorldChunkWire.CellCount];
        internal readonly byte[] Liquid = new byte[WorldChunkWire.CellCount];
    }

    private readonly Dictionary<WorldChunkKey, Chunk> cells = new();
    public int Width { get; }
    public int Height { get; }
    internal int Applies { get; private set; }
    internal int Reads { get; private set; }
    internal Func<WorldChunkHeader, bool>? OnApply { get; set; }

    internal TestChunkGrid(int width, int height) { Width = width; Height = height; }

    public bool Read(WorldChunkKey key, Span<ushort> blocks, Span<float> damage, Span<byte> liquid)
    {
        if (!WorldChunkGrid.Extents(this, key, out _, out _)) return false;
        Reads++;
        if (cells.TryGetValue(key, out var saved))
        {
            saved.Blocks.AsSpan().CopyTo(blocks);
            saved.Damage.AsSpan().CopyTo(damage);
            saved.Liquid.AsSpan().CopyTo(liquid);
        }
        else { blocks.Clear(); damage.Clear(); liquid.Clear(); }
        return true;
    }

    public bool Apply(WorldChunkHeader header, ReadOnlySpan<ushort> blocks, ReadOnlySpan<float> damage, ReadOnlySpan<byte> liquid)
    {
        if (OnApply?.Invoke(header) == false) return false;
        var chunk = GetOrCreate(header.Key);
        blocks.CopyTo(chunk.Blocks);
        damage.CopyTo(chunk.Damage);
        liquid.CopyTo(chunk.Liquid);
        Applies++;
        return true;
    }

    internal void Set(int x, int y, ushort block, float damage = 0, byte liquid = 0)
    {
        var chunk = GetOrCreate(WorldChunkKey.At(x, y));
        int i = y % WorldChunkWire.Side * WorldChunkWire.Side + x % WorldChunkWire.Side;
        chunk.Blocks[i] = block;
        chunk.Damage[i] = damage;
        chunk.Liquid[i] = liquid;
    }

    internal bool TryGet(int x, int y, out (ushort Block, float Damage, byte Liquid) value)
    {
        value = default;
        if (!cells.TryGetValue(WorldChunkKey.At(x, y), out var chunk)) return false;
        int i = y % WorldChunkWire.Side * WorldChunkWire.Side + x % WorldChunkWire.Side;
        value = (chunk.Blocks[i], chunk.Damage[i], chunk.Liquid[i]);
        return true;
    }

    private Chunk GetOrCreate(WorldChunkKey key)
    {
        if (!cells.TryGetValue(key, out var chunk)) cells.Add(key, chunk = new Chunk());
        return chunk;
    }
}
