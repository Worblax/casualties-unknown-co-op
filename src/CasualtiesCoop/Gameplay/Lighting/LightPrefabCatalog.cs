using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CasualtiesCoop.Gameplay.Lighting;

internal sealed class LightPrefabCatalog
{
    private static readonly string[] ItemIds =
        { "", "flashlight", "emergencylight", "headlamp", "makeshiftheadlamp", "roselight", "lantern", "lightbulb", "torch", "campfire" };

    private readonly Light2D[]?[] prefabs = new Light2D[ItemIds.Length][];

    internal static byte DefinitionOf(Item item)
    {
        int index = Array.IndexOf(ItemIds, item.id);
        return index > 0 ? (byte)index : (byte)0;
    }

    internal Light2D? Template(byte definition, byte component)
    {
        if (definition == 0 || definition >= ItemIds.Length)
        {
            return null;
        }

        if (prefabs[definition] is not { } lights)
        {
            GameObject prefab = Resources.Load<GameObject>(ItemIds[definition]);
            lights = prefab ? prefab.GetComponentsInChildren<Light2D>(true) : Array.Empty<Light2D>();
            prefabs[definition] = lights;
        }

        return component < lights.Length && lights[component] && lights[component].lightType != Light2D.LightType.Global
            ? lights[component] : null;
    }
}
