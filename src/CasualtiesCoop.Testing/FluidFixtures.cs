using Newtonsoft.Json.Linq;
using UnityEngine;
using static CasualtiesCoop.Testing.ScenarioDocument;

namespace CasualtiesCoop.Testing;

internal sealed class FluidFixtures
{
    private readonly FixtureSpace space;
    private readonly TestResourceCatalog catalog;
    private readonly Dictionary<string, Vector2Int[]> regions = new(StringComparer.Ordinal);

    internal FluidFixtures(FixtureSpace space, TestResourceCatalog catalog)
    {
        this.space = space;
        this.catalog = catalog;
    }

    internal void Define(JObject command, bool fill)
    {
        Vector2Int origin = space.Cell(command);
        int width = OptionalInt(command, "width", 1, 16, 1);
        int height = OptionalInt(command, "height", 1, 16, 1);
        byte type = fill ? catalog.Liquid(Integer(command, "type", 0, byte.MaxValue)) : (byte)0;
        var cells = new List<Vector2Int>();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var cell = origin + new Vector2Int(x, y);
            space.RequireCell(cell);
            cells.Add(cell);
        }
        regions.Add(String(command, "id"), cells.ToArray());
        if (!fill) return;
        foreach (Vector2Int cell in cells) FluidManager.main.SetLiquid(cell.x, cell.y, type);
    }

    internal void EnableFlow() => FluidManager.main.liquidPushing = true;

    internal int Cell(string alias) => Read(regions[alias][0]);
    internal int WetCount(string alias) => regions[alias].Count(cell => Read(cell) != 0);
    private static byte Read(Vector2Int cell) => FluidManager.main.GetLiquid(cell.x, cell.y);

    internal object Snapshot() => regions.Select(pair => new
    {
        name = pair.Key, cells = pair.Value.Select(cell => new { x = cell.x, y = cell.y, type = Read(cell) }).ToArray()
    }).ToArray();
}
