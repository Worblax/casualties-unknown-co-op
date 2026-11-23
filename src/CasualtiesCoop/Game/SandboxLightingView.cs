using System;
using System.Reflection;
using CasualtiesCoop.Networking;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CasualtiesCoop.Game;

internal sealed class SandboxLightCatalog
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

internal sealed class ProjectedSandboxLight : IDisposable
{
    private static readonly FieldInfo? SortingLayersField = AccessTools.Field(typeof(Light2D), "m_ApplyToSortingLayers");
    private static readonly FieldInfo?[] ShapeFields = Array.ConvertAll(new[]
    {
        "m_ShapeLightParametricSides", "m_ShapeLightParametricRadius", "m_ShapeLightParametricAngleOffset",
        "m_ShapeLightFalloffOffset", "m_NormalMapDistance", "m_NormalMapQuality"
    }, name => AccessTools.Field(typeof(Light2D), name));
    private readonly Light2D light;
    private readonly SpriteRenderer glint;
    private LightSample target;
    private float phaseAt;
    private float receivedAt;
    private bool hasPose;

    internal Light2D Light => light;
    internal bool Matches(byte definition, byte component) => target.Definition == definition && target.Component == component;

    internal ProjectedSandboxLight(Light2D template, Sprite marker)
    {
        GameObject root = new("Co-op light emitter");
        root.SetActive(false);
        light = root.AddComponent<Light2D>();
        light.lightType = template.lightType;
        light.blendStyleIndex = template.blendStyleIndex;
        light.lightOrder = template.lightOrder;
        light.falloffIntensity = template.falloffIntensity;
        light.overlapOperation = template.overlapOperation;
        light.pointLightInnerAngle = template.pointLightInnerAngle;
        light.pointLightOuterAngle = template.pointLightOuterAngle;
        light.pointLightInnerRadius = template.pointLightInnerRadius;
        light.pointLightOuterRadius = template.pointLightOuterRadius;
        light.shapeLightFalloffSize = template.shapeLightFalloffSize;
        light.lightCookieSprite = template.lightCookieSprite;
        light.volumeIntensity = template.volumeIntensity;
        light.volumeIntensityEnabled = template.volumeIntensityEnabled;
        light.shadowsEnabled = true;
        light.shadowIntensity = 1f;
        light.volumetricShadowsEnabled = template.volumetricShadowsEnabled;
        light.shadowVolumeIntensity = template.shadowVolumeIntensity;
        if (SortingLayersField?.GetValue(template) is int[] layers)
        {
            SortingLayersField.SetValue(light, (int[])layers.Clone());
        }
        foreach (FieldInfo? field in ShapeFields)
        {
            field?.SetValue(light, field.GetValue(template));
        }
        if (template.lightType == Light2D.LightType.Freeform && template.shapePath is { Length: >= 3 } shape)
        {
            light.SetShapePath((Vector3[])shape.Clone());
        }

        GameObject emitter = new("Visible light element");
        emitter.transform.SetParent(root.transform, false);
        glint = emitter.AddComponent<SpriteRenderer>();
        glint.sprite = marker;
        glint.sortingOrder = 1900;
        glint.enabled = false;
    }

    internal void Receive(LightSample sample, float now)
    {
        if (!target.Blink && sample.Blink)
        {
            phaseAt = now - sample.BlinkPhase / 255f * (2f / 18f);
        }
        target = sample;
        receivedAt = now;
    }

    internal void Tick(Vector2 observer, WorldGeneration world)
    {
        float now = Time.unscaledTime;
        if (now - receivedAt > 1.1f || (observer - target.Position).sqrMagnitude > 125f * 125f)
        {
            light.gameObject.SetActive(false);
            return;
        }

        Transform at = light.transform;
        bool snap = !hasPose || !light.gameObject.activeSelf || (target.Position - (Vector2)at.position).sqrMagnitude > 36f;
        float ease = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
        at.position = snap ? target.Position : Vector2.Lerp(at.position, target.Position, ease);
        float angle = snap ? target.Angle : Mathf.LerpAngle(at.eulerAngles.z, target.Angle, ease);
        at.rotation = Quaternion.Euler(0f, 0f, angle);
        at.localScale = new Vector3(target.Scale.x, target.Scale.y, 1f);
        glint.transform.localScale = new Vector3(1f / Mathf.Max(0.02f, Mathf.Abs(target.Scale.x)),
            1f / Mathf.Max(0.02f, Mathf.Abs(target.Scale.y)), 1f);
        light.color = target.Color;
        float intensity = target.Intensity;
        if (target.Blink && Mathf.FloorToInt((now - phaseAt) * 18f) % 2 != 0)
        {
            intensity = 0f;
        }
        light.intensity = intensity;
        light.gameObject.SetActive(true);
        hasPose = true;

        Vector2 origin = at.position;
        glint.enabled = intensity > 0.02f && SandboxLightVisibility.CanSee(world, observer, origin);
        glint.color = new Color(light.color.r, light.color.g, light.color.b, Mathf.Min(0.75f, intensity));
    }

    public void Dispose()
    {
        light.gameObject.SetActive(false);
        UnityEngine.Object.Destroy(light.gameObject);
    }
}

internal sealed class SandboxLightVisibility
{
    private const float ViewDistance = 50f;
    private const float VisibleLight = 0.055f;

    private Light2D[] lights = Array.Empty<Light2D>();
    private float nextRefresh;

    internal void Refresh()
    {
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 0.18f;
            lights = UnityEngine.Object.FindObjectsByType<Light2D>(FindObjectsSortMode.None);
        }
    }

    internal bool BodyVisible(WorldGeneration world, Vector2 observer, Vector2 point)
    {
        if (!CanSee(world, observer, point))
        {
            return false;
        }

        float illumination = world.ambientLight && world.ambientLight.isActiveAndEnabled ? world.ambientLight.intensity : 0f;
        if (illumination >= VisibleLight)
        {
            return true;
        }

        foreach (Light2D light in lights)
        {
            if (!light || !light.isActiveAndEnabled || light.lightType == Light2D.LightType.Global || light.intensity <= 0.01f)
            {
                continue;
            }

            float strength = StrengthAt(light, point);
            if (strength <= 0f || !RayClear(world, light.transform.position, point))
            {
                continue;
            }

            illumination += strength;
            if (illumination >= VisibleLight)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool CanSee(WorldGeneration world, Vector2 observer, Vector2 point) =>
        (point - observer).sqrMagnitude <= ViewDistance * ViewDistance && RayClear(world, observer, point);

    private static bool RayClear(WorldGeneration world, Vector2 start, Vector2 point)
    {
        Vector2 delta = point - start;
        float length = delta.magnitude;
        if (length < 0.1f)
        {
            return true;
        }

        // Trace individual tiles so thin walls and vanilla transparent tiles both keep their meaning.
        Vector2 from = start + new Vector2(world.halfWidth, world.halfHeight);
        Vector2 travel = delta * ((length - 0.08f) / length);
        Vector2 to = from + travel;
        int x = Mathf.FloorToInt(from.x);
        int y = Mathf.FloorToInt(from.y);
        int endX = Mathf.FloorToInt(to.x);
        int endY = Mathf.FloorToInt(to.y);
        int stepX = travel.x > 0f ? 1 : -1;
        int stepY = travel.y > 0f ? 1 : -1;
        float strideX = travel.x == 0 ? float.PositiveInfinity : 1f / Mathf.Abs(travel.x);
        float strideY = travel.y == 0 ? float.PositiveInfinity : 1f / Mathf.Abs(travel.y);
        float nextX = travel.x == 0 ? float.PositiveInfinity : (stepX > 0 ? x + 1f - from.x : from.x - x) * strideX;
        float nextY = travel.y == 0 ? float.PositiveInfinity : (stepY > 0 ? y + 1f - from.y : from.y - y) * strideY;
        while (x != endX || y != endY)
        {
            if (y == endY || (x != endX && nextX < nextY))
            {
                x += stepX;
                nextX += strideX;
            }
            else
            {
                y += stepY;
                nextY += strideY;
            }

            if (x < 0 || y < 0 || x >= world.width || y >= world.height || world.GetBlock(new Vector2Int(x, y)) is not (0 or 7))
            {
                return false;
            }
        }
        return true;
    }

    private static float StrengthAt(Light2D light, Vector2 point)
    {
        Vector2 delta = point - (Vector2)light.transform.position;
        if (light.lightType == Light2D.LightType.Point)
        {
            float distance = delta.magnitude;
            if (distance >= light.pointLightOuterRadius)
            {
                return 0;
            }

            float radial = 1f - Mathf.InverseLerp(light.pointLightInnerRadius, light.pointLightOuterRadius, distance);
            float outer = light.pointLightOuterAngle * 0.5f;
            float angle = distance > 0.05f ? Vector2.Angle(light.transform.up, delta) : 0f;
            if (outer < 179.9f && angle > outer)
            {
                return 0;
            }

            float angular = outer >= 179.9f ? 1f :
                1f - Mathf.InverseLerp(light.pointLightInnerAngle * 0.5f, outer, angle);
            return radial * angular * light.intensity;
        }

        Vector2 local = light.transform.InverseTransformPoint(point);
        if (light.lightType == Light2D.LightType.Freeform && light.shapePath is { Length: >= 3 } polygon)
        {
            return Contains(polygon, local) ? light.intensity : 0f;
        }
        if (light.lightType == Light2D.LightType.Sprite && light.lightCookieSprite)
        {
            Bounds bounds = light.lightCookieSprite.bounds;
            return local.x >= bounds.min.x && local.x <= bounds.max.x && local.y >= bounds.min.y && local.y <= bounds.max.y
                ? light.intensity : 0f;
        }

        return local.sqrMagnitude < light.shapeLightParametricRadius * light.shapeLightParametricRadius ? light.intensity : 0f;
    }

    private static bool Contains(Vector3[] polygon, Vector2 point)
    {
        bool inside = false;
        for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
        {
            Vector3 a = polygon[previous];
            Vector3 b = polygon[index];
            if ((a.y > point.y) != (b.y > point.y) && point.x < a.x + (b.x - a.x) * (point.y - a.y) / (b.y - a.y))
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
