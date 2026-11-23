using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CasualtiesCoop.Game;

internal sealed class SandboxTerrainShadows : IDisposable
{
    private const int RegionSize = 16;
    private const int RegionLimit = 80;
    private static readonly FieldInfo? ShapePathField = AccessTools.Field(typeof(ShadowCaster2D), "m_ShapePath");

    private sealed class Region
    {
        internal readonly byte[] Solid = new byte[RegionSize * RegionSize];
        internal GameObject? Root;
    }

    private readonly Dictionary<Vector2Int, Region> regions = new();
    private readonly HashSet<Vector2Int> wanted = new();
    private readonly List<Vector2Int> sorted = new();
    private readonly List<Vector2Int> expired = new();
    private WorldGeneration? boundWorld;
    private float nextRefresh;

    internal void Tick(WorldGeneration world, Vector2 observer, List<Light2D> projected)
    {
        if (ShapePathField is null || Time.unscaledTime < nextRefresh)
        {
            return;
        }

        nextRefresh = Time.unscaledTime + 0.18f;
        if (world != boundWorld)
        {
            Dispose();
            boundWorld = world;
        }

        wanted.Clear();
        foreach (Light2D light in projected)
        {
            if (light && light.isActiveAndEnabled)
            {
                Include(world, light);
            }
        }

        Vector2Int center = world.WorldToBlockPos(observer);
        center = new Vector2Int(center.x / RegionSize, center.y / RegionSize);
        sorted.Clear();
        sorted.AddRange(wanted);
        sorted.Sort((a, b) => (a - center).sqrMagnitude.CompareTo((b - center).sqrMagnitude));
        int count = Math.Min(sorted.Count, RegionLimit);

        wanted.Clear();
        for (int index = 0; index < count; index++)
        {
            Vector2Int key = sorted[index];
            wanted.Add(key);
            UpdateRegion(world, key);
        }

        expired.Clear();
        foreach (Vector2Int key in regions.Keys)
        {
            if (!wanted.Contains(key))
            {
                expired.Add(key);
            }
        }
        foreach (Vector2Int key in expired)
        {
            DestroyRegion(regions[key]);
            regions.Remove(key);
        }
    }

    private void Include(WorldGeneration world, Light2D light)
    {
        float radius = Mathf.Clamp(Radius(light), 2f, 72f);
        Vector2Int center = world.WorldToBlockPos(light.transform.position);
        int reach = Mathf.CeilToInt(radius) + 2;
        int fromX = Math.Max(0, center.x - reach) / RegionSize;
        int fromY = Math.Max(0, center.y - reach) / RegionSize;
        int toX = Math.Min((int)world.width - 1, center.x + reach) / RegionSize;
        int toY = Math.Min((int)world.height - 1, center.y + reach) / RegionSize;
        for (int y = fromY; y <= toY; y++)
        {
            for (int x = fromX; x <= toX; x++)
            {
                wanted.Add(new Vector2Int(x, y));
            }
        }
    }

    private static float Radius(Light2D light)
    {
        if (light.lightType == Light2D.LightType.Point)
        {
            return light.pointLightOuterRadius;
        }

        float radius = light.shapeLightParametricRadius;
        if (light.lightType == Light2D.LightType.Sprite && light.lightCookieSprite)
        {
            radius = light.lightCookieSprite.bounds.extents.magnitude;
        }
        if (light.shapePath is { } shape && light.lightType == Light2D.LightType.Freeform)
        {
            foreach (Vector3 vertex in shape)
            {
                radius = Mathf.Max(radius, vertex.magnitude);
            }
        }
        Vector3 scale = light.transform.lossyScale;
        return (radius + light.shapeLightFalloffSize) * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
    }

    private void UpdateRegion(WorldGeneration world, Vector2Int key)
    {
        if (!regions.TryGetValue(key, out Region region))
        {
            region = new Region();
            regions.Add(key, region);
        }

        int originX = key.x * RegionSize;
        int originY = key.y * RegionSize;
        Span<byte> solid = stackalloc byte[RegionSize * RegionSize];
        solid.Clear();
        int width = Math.Min(RegionSize, (int)world.width - originX);
        int height = Math.Min(RegionSize, (int)world.height - originY);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort block = world.GetBlock(new Vector2Int(originX + x, originY + y));
                solid[y * RegionSize + x] = block != 0 && block != 7 ? (byte)1 : (byte)0;
            }
        }

        if (solid.SequenceEqual(region.Solid))
        {
            return;
        }

        solid.CopyTo(region.Solid);
        DestroyRegion(region);
        if (solid.IndexOf((byte)1) < 0)
        {
            return;
        }
        Vector2 at = world.BlockToWorldPos(new Vector2Int(originX, originY)) - new Vector2(0.5f, 0.5f);
        GameObject root = new("Co-op terrain shadow region");
        root.transform.position = at;
        root.SetActive(false);
        for (int y = 0; y < RegionSize; y++)
        {
            for (int x = 0; x < RegionSize; x++)
            {
                if (solid[y * RegionSize + x] == 0)
                {
                    continue;
                }

                int right = x;
                while (right < RegionSize && solid[y * RegionSize + right] != 0)
                {
                    right++;
                }
                int top = y + 1;
                while (top < RegionSize && IsFilled(solid, x, right, top))
                {
                    top++;
                }
                for (int row = y; row < top; row++)
                {
                    solid.Slice(row * RegionSize + x, right - x).Clear();
                }

                GameObject caster = new("Opaque terrain");
                caster.transform.SetParent(root.transform, false);
                ShadowCaster2D component = caster.AddComponent<ShadowCaster2D>();
                ShapePathField!.SetValue(component, new Vector3[]
                {
                    new(x, y), new(x, top), new(right, top), new(right, y)
                });
                component.useRendererSilhouette = false;
                component.castsShadows = true;
                component.selfShadows = false;
                x = right - 1;
            }
        }

        root.SetActive(true);
        region.Root = root;
    }

    private static bool IsFilled(ReadOnlySpan<byte> solid, int left, int right, int y)
    {
        for (int x = left; x < right; x++)
        {
            if (solid[y * RegionSize + x] == 0)
            {
                return false;
            }
        }
        return true;
    }

    private static void DestroyRegion(Region region)
    {
        if (region.Root)
        {
            region.Root!.SetActive(false);
            foreach (ShadowCaster2D caster in region.Root.GetComponentsInChildren<ShadowCaster2D>(true))
            {
                if (caster.mesh)
                {
                    UnityEngine.Object.Destroy(caster.mesh);
                }
            }
            UnityEngine.Object.Destroy(region.Root);
        }
        region.Root = null;
    }

    public void Dispose()
    {
        foreach (Region region in regions.Values)
        {
            DestroyRegion(region);
        }
        regions.Clear();
        boundWorld = null;
    }
}
