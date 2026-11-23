using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CasualtiesCoop.Gameplay.Lighting;

internal sealed class LocalLightVisibility
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
