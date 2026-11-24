using System.Collections.Generic;

namespace CasualtiesCoop.Core;

internal sealed class FluidChunkProgress
{
    private readonly Dictionary<(int X, int Y), (uint Revision, ulong Generation)> chunks = new();
    private ulong generation;

    internal void RequireFreshSnapshots() => generation++;

    internal bool HasCurrent((int X, int Y) key) =>
        chunks.TryGetValue(key, out var last) && last.Generation == generation;

    internal bool TryObserve((int X, int Y) key, uint revision)
    {
        if (revision == 0 || (chunks.TryGetValue(key, out var last) && revision < last.Revision))
        {
            return false;
        }

        chunks[key] = (revision, generation);
        return true;
    }
}
