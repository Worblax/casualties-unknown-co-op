using Newtonsoft.Json.Linq;
using UnityEngine;
using static CasualtiesCoop.Testing.ScenarioDocument;

namespace CasualtiesCoop.Testing;

internal sealed class TerrainFixtures
{
    private readonly FixtureSpace space;
    private readonly TestResourceCatalog catalog;
    private readonly Dictionary<string, Vector2Int[]> layouts = new(StringComparer.Ordinal);
    private WorldGeneration World => space.World;
    private Vector2Int historyOrigin;
    private int historyCell = -1;

    internal TerrainFixtures(FixtureSpace space, TestResourceCatalog catalog)
    {
        this.space = space;
        this.catalog = catalog;
    }

    internal void Load(JObject command)
    {
        Vector2Int origin = space.Cell(command);
        var rows = (JArray)command["rows"]!;
        JObject legend = Object(command, "legend");
        var cells = new List<(Vector2Int Cell, ushort Material)>();
        for (int y = 0; y < rows.Count; y++)
        {
            string row = rows[y]!.Value<string>()!;
            for (int x = 0; x < row.Length; x++)
            {
                var cell = origin + new Vector2Int(x, rows.Count - 1 - y);
                space.RequireCell(cell);
                cells.Add((cell, catalog.Material(Integer(legend, row[x].ToString(), 0, ushort.MaxValue))));
            }
        }
        layouts.Add(String(command, "id"), cells.Select(entry => entry.Cell).ToArray());
        foreach (var entry in cells) World.SetBlock(entry.Cell, entry.Material);
    }

    internal Vector2Int Cell(string alias) => layouts[alias][0];
    internal ushort Block(Vector2Int cell) => World.GetBlock(cell);
    internal float Damage(Vector2Int cell) => World.GetBlockDamage(cell)?.damage ?? 0;
    internal int SolidCount(string alias) => layouts[alias].Count(cell => Block(cell) != 0);

    internal void SetNative(string alias, int material) => World.SetBlock(Cell(alias), catalog.Material(material));

    internal bool BreakHistoryProbe(JObject command, VirtualSessionCluster cluster)
    {
        const int count = 4097;
        const int width = 64;
        if (historyCell < 0)
        {
            historyOrigin = space.Cell(command);
            Vector2Int last = historyOrigin + new Vector2Int(0, (count - 1) / width);
            space.RequireCell(historyOrigin);
            space.RequireCell(last + new Vector2Int(width - 1, 0));
            layouts.Add("history-first", new[] { historyOrigin });
            layouts.Add("history-overflow", new[] { last });
            historyCell = 0;
        }
        int stop = Math.Min(historyCell + 32, count);
        for (; historyCell < stop; historyCell++)
        {
            Vector2Int cell = historyOrigin + new Vector2Int(historyCell % width, historyCell / width);
            World.SetBlock(cell, 1);
            cluster.ApplyHostTerrainHit(cell, 100);
        }
        return historyCell == count;
    }

    internal object Snapshot() => layouts.Select(pair => new
    {
        name = pair.Key, cells = pair.Value.Select(cell => new { x = cell.x, y = cell.y, material = Block(cell), damage = Damage(cell) }).ToArray()
    }).ToArray();
}
