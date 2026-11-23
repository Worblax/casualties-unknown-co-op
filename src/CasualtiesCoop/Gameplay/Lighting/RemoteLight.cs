using System;
using System.Reflection;
using CasualtiesCoop.Networking;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CasualtiesCoop.Gameplay.Lighting;

internal sealed class RemoteLight : IDisposable
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

    internal RemoteLight(Light2D template, Sprite marker)
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
        glint.enabled = intensity > 0.02f && LocalLightVisibility.CanSee(world, observer, origin);
        glint.color = new Color(light.color.r, light.color.g, light.color.b, Mathf.Min(0.75f, intensity));
    }

    public void Dispose()
    {
        light.gameObject.SetActive(false);
        UnityEngine.Object.Destroy(light.gameObject);
    }
}
