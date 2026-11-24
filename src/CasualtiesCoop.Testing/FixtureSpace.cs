using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class FixtureSpace
{
    internal readonly WorldGeneration World = WorldGeneration.world;
    internal readonly Vector2 Origin;

    internal FixtureSpace(Body local) => Origin = local.transform.position;

    internal Vector2 Position(JObject command)
    {
        var at = ScenarioDocument.Position(command);
        return Origin + new Vector2(at.X, at.Y);
    }

    internal Vector2Int Cell(JObject command) => World.WorldToBlockPos(Position(command));

    internal void RequireCell(Vector2Int cell)
    {
        if (cell.x < 1 || cell.y < 1 || cell.x >= World.width - 1 || cell.y >= World.height - 1)
            throw new TestBlocked("Fixture falls outside the native world.");
    }
}
