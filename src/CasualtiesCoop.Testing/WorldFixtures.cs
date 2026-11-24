using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class WorldFixtures : IDisposable
{
    internal FixtureSpace Space { get; }
    internal TestResourceCatalog Catalog { get; }
    internal CharacterFixtures Characters { get; }
    internal TerrainFixtures Terrain { get; }
    internal FluidFixtures Fluids { get; }
    internal MobFixtures Mobs { get; }
    internal readonly HashSet<string> CoveredPaths = new(StringComparer.Ordinal);

    internal WorldFixtures(Body local, ScenarioDocument scenario)
    {
        Space = new FixtureSpace(local);
        Catalog = new TestResourceCatalog(Space.World);
        Characters = new CharacterFixtures(local, Space, Catalog, scenario.BodyLimit);
        Terrain = new TerrainFixtures(Space, Catalog);
        Fluids = new FluidFixtures(Space, Catalog);
        Mobs = new MobFixtures(Space, Catalog, scenario.MobLimit);
    }

    internal object Snapshot(VirtualSessionCluster cluster) => new
    {
        bodies = Characters.Snapshot(cluster), tiles = Terrain.Snapshot(), liquids = Fluids.Snapshot(), mobs = Mobs.Snapshot(Characters)
    };

    public void Dispose()
    {
        Mobs.Dispose();
        Characters.Dispose();
    }
}
