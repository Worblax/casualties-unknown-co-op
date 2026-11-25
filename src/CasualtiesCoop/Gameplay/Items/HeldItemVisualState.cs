using UnityEngine;
using UnityEngine.Rendering.Universal;
using CasualtiesCoop.Gameplay.Lighting;

namespace CasualtiesCoop.Gameplay.Items;

// A remote visual must never participate in physics or world interaction. Collider state is
// restored when the item leaves the remote character and becomes a world/inventory object again.
internal sealed class HeldItemVisualState : MonoBehaviour
{
    private (Collider2D Component, bool Enabled)[]? colliders;
    private (Light2D Component, bool Enabled)[]? lights;

    internal static void Disable(Item item, bool suppressNetworkLights = false)
    {
        if (!item) return;
        HeldItemVisualState state = item.GetComponent<HeldItemVisualState>() ??
            item.gameObject.AddComponent<HeldItemVisualState>();
        state.DisableComponents(suppressNetworkLights && LightPrefabCatalog.DefinitionOf(item) != 0);
    }

    internal static void Restore(Item item)
    {
        if (!item) return;
        if (item.TryGetComponent<HeldItemVisualState>(out HeldItemVisualState state)) state.RestoreComponents();
    }

    private void DisableComponents(bool suppressLights)
    {
        if (colliders is null)
        {
            Collider2D[] components = GetComponentsInChildren<Collider2D>(true);
            colliders = new (Collider2D, bool)[components.Length];
            for (int index = 0; index < components.Length; index++)
            {
                colliders[index] = (components[index], components[index].enabled);
            }
        }
        foreach (var collider in colliders)
        {
            if (collider.Component) collider.Component.enabled = false;
        }

        if (!suppressLights) return;
        if (lights is null)
        {
            Light2D[] components = GetComponentsInChildren<Light2D>(true);
            lights = new (Light2D, bool)[System.Math.Min(components.Length, LightPrefabCatalog.MaxComponents)];
            for (int index = 0; index < lights.Length; index++)
            {
                Light2D light = components[index];
                if (light.lightType != Light2D.LightType.Global) lights[index] = (light, light.enabled);
            }
        }
        foreach (var light in lights)
        {
            if (light.Component) light.Component.enabled = false;
        }
    }

    private void RestoreComponents()
    {
        if (colliders is not null)
        {
            foreach (var collider in colliders)
            {
                if (collider.Component) collider.Component.enabled = collider.Enabled;
            }
            colliders = null;
        }
        if (lights is not null)
        {
            foreach (var light in lights)
            {
                if (light.Component) light.Component.enabled = light.Enabled;
            }
            lights = null;
        }
    }
}
