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
    private Item? boundItem;
    private Transform? boundEmitter;

    internal Light2D Light => light;
    internal ulong HeldItemId => target.HeldItemId;
    internal byte Component => target.Component;
    internal bool MarkerVisible => glint.enabled && glint.gameObject.activeInHierarchy;
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

    internal void Tick(Vector2 observer, WorldGeneration world, float now, Item? heldItem)
    {
        if (now - receivedAt > 1.1f || (observer - target.Position).sqrMagnitude > 125f * 125f)
        {
            Hide();
            return;
        }

        Transform at = light.transform;
        Transform? emitter = FindHeldEmitter(heldItem);
        if (emitter)
        {
            ApplyHeldTransform(at, emitter!);
        }
        else
        {
            bool snap = !hasPose || !light.gameObject.activeSelf || (target.Position - (Vector2)at.position).sqrMagnitude > 36f;
            float ease = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
            at.position = snap ? target.Position : Vector2.Lerp(at.position, target.Position, ease);
            float angle = snap ? target.Angle : Mathf.LerpAngle(at.eulerAngles.z, target.Angle, ease);
            at.rotation = Quaternion.Euler(0f, 0f, angle);
            at.localScale = new Vector3(target.Scale.x, target.Scale.y, 1f);
            glint.transform.localScale = new Vector3(1f / Mathf.Max(0.02f, Mathf.Abs(target.Scale.x)),
                1f / Mathf.Max(0.02f, Mathf.Abs(target.Scale.y)), 1f);
        }
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
        glint.enabled = !emitter && intensity > 0.02f && LocalLightVisibility.CanSee(world, observer, origin);
        glint.color = new Color(light.color.r, light.color.g, light.color.b, Mathf.Min(0.75f, intensity));
    }

    internal void Hide() => light.gameObject.SetActive(false);

    private static void ApplyHeldTransform(Transform target, Transform emitter)
    {
        // A reflected parent changes the beam's rendered basis independently of its quaternion.
        Vector2 up = emitter.TransformVector(Vector3.up);
        Vector2 right = emitter.TransformVector(Vector3.right);
        float scaleY = up.magnitude;
        float scaleX = scaleY > 0f ? (right.x * up.y - right.y * up.x) / scaleY : right.magnitude;
        float angle = Mathf.Atan2(-up.x, up.y) * Mathf.Rad2Deg;
        target.SetPositionAndRotation(emitter.position, Quaternion.Euler(0f, 0f, angle));
        target.localScale = new Vector3(scaleX, scaleY, emitter.lossyScale.z);
    }

    private Transform? FindHeldEmitter(Item? item)
    {
        if (item != boundItem)
        {
            boundItem = item;
            boundEmitter = null;
            if (item && LightPrefabCatalog.DefinitionOf(item!) == target.Definition)
            {
                Light2D[] emitters = item!.GetComponentsInChildren<Light2D>(true);
                if (target.Component < emitters.Length && emitters[target.Component])
                {
                    boundEmitter = emitters[target.Component].transform;
                }
            }
        }
        return item && item!.gameObject.activeInHierarchy ? boundEmitter : null;
    }

    public void Dispose()
    {
        light.gameObject.SetActive(false);
        UnityEngine.Object.Destroy(light.gameObject);
    }
}
