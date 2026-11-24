using CasualtiesCoop.Gameplay.Items;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class TestResourceCatalog
{
    // WorldGeneration.GetBlockInfo returns null for unknown codes.
    private const int MaterialProbeLimit = 256;
    private readonly SharedItemObjects sharedItems = new();
    private readonly HashSet<string> confirmedItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> confirmedMobs = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> materials = new();
    private readonly List<object> probes = new();
    private readonly Dictionary<int, string> liquids = new() { [0] = "empty" };
    private readonly Dictionary<int, string> characters = new();
    private readonly HashSet<string> itemIds;

    internal TestResourceCatalog(WorldGeneration world)
    {
        itemIds = new HashSet<string>(Item.GlobalItems.Keys, StringComparer.Ordinal);
        for (int id = 0; id < MaterialProbeLimit; id++)
            if (world.GetBlockInfo((ushort)id) is { } info) materials.Add(id, info.name);
        foreach (var liquid in FluidManager.WorldFluidToLiquidID)
        {
            if (liquid.Key <= 0 || liquid.Key >= FluidManager.main.LiquidParticlePrefabs.Count + 1) continue;
            liquids.Add(liquid.Key, liquid.Key switch { 1 => "water", 6 => "magma", _ => liquid.Value });
        }

        // SpawnCharacter uses 0 / 1 / fallback; the prefabs live under this dedicated resource directory.
        foreach (GameObject prefab in Resources.LoadAll<GameObject>("Special/Characters"))
            if (prefab.GetComponentInChildren<Body>(true) is { charType: >= 0 and <= 2 } body)
                characters.TryAdd(body.charType, prefab.name);

        // The vanilla CaveTickSpawner uses a root resource path. Detect loaded AI prefabs and allow explicit
        // path probes for other mob families instead of loading every GameObject resource in the build.
        foreach (BuildingEntity entity in Resources.FindObjectsOfTypeAll<BuildingEntity>())
            if (entity && IsMob(entity.gameObject) && !entity.gameObject.scene.IsValid())
                confirmedMobs.Add(entity.gameObject.name);
        TryMob("cavetick");
    }

    internal int Character(int id) => characters.ContainsKey(id) ? id :
        throw new TestBlocked($"No confirmed SpawnCharacter binding for {id}.");

    internal ushort Material(int id) => materials.ContainsKey(id) ? (ushort)id :
        throw new TestBlocked($"No native terrain material {id}.");

    internal byte Liquid(int id) => liquids.ContainsKey(id) ? (byte)id :
        throw new TestBlocked($"No native fluid grid type {id}.");

    internal string ItemResource(string id, bool networked)
    {
        if (id == "$fixture.pickupableItem") id = "stick";
        if (!itemIds.Contains(id) || Resources.Load<GameObject>(id) is not { } prefab || !prefab ||
            !prefab.TryGetComponent<Item>(out Item item) || !prefab.GetComponent<Rigidbody2D>() ||
            !prefab.GetComponent<Collider2D>() || (networked && !sharedItems.Supports(item)))
        {
            throw new TestBlocked($"Item resource {id} does not have the required {(networked ? "production replication" : "native physics")} binding.");
        }
        confirmedItems.Add(id);
        return id;
    }

    internal string MobResource(string id)
    {
        if (!TryMob(id)) throw new TestBlocked($"No confirmed mob resource {id}.");
        return id;
    }

    private bool TryMob(string id)
    {
        GameObject prefab = Resources.Load<GameObject>(id);
        if (!prefab || !prefab.GetComponent<BuildingEntity>() || !IsMob(prefab)) return false;
        confirmedMobs.Add(id);
        return true;
    }

    internal void Probe(IEnumerable<string> items, IEnumerable<string> mobs)
    {
        foreach (string id in items)
        {
            try
            {
                string resource = ItemResource(id, networked: false);
                bool networked = sharedItems.Supports(Resources.Load<GameObject>(resource).GetComponent<Item>());
                probes.Add(new { kind = "item", id, native = true, networked });
            }
            catch (TestBlocked error)
            {
                probes.Add(new { kind = "item", id, native = false, reason = error.Message });
            }
        }
        foreach (string id in mobs) probes.Add(new { kind = "mob", id, native = TryMob(id) });
    }

    private static bool IsMob(GameObject prefab) => prefab.GetComponent<SpiderHandler>() ||
        prefab.GetComponent<CrystalEnemy>() || prefab.GetComponent<GuardScript>();

    internal object Describe(Body body) => new
    {
        level = "native resources",
        nativeBody = body.name,
        slots = body.slots.Select((slot, index) => new { index, hand = slot.isHand }).ToArray(),
        items = new { declared = itemIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            prefabChecked = confirmedItems.OrderBy(id => id, StringComparer.Ordinal).ToArray() },
        mobs = confirmedMobs.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        materials, liquids, characters, probes,
        blocked = new[] { "Independent Unity worlds", "Steam E2E", "Mob replication" }
    };
}
