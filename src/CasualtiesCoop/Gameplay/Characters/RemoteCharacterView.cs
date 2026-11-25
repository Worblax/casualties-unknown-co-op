using System;
using System.Collections.Generic;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Networking;
using TMPro;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Characters;

internal sealed class RemoteCharacterView : IDisposable
{
    private readonly GameObject root;
    private readonly GameObject[] limbs;
    private readonly TextMeshPro name;
    private readonly CharacterFace? face;
    private readonly NativeCharacterSpeechView? speech;
    private SpriteRenderer? head;
    private SpriteRenderer? eyes;
    private SpriteRenderer? nose;
    private readonly struct HandAnchor
    {
        internal readonly Transform Parent;
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal readonly Vector3 Scale;
        internal readonly int SortingOrder;

        internal HandAnchor(Transform parent, Vector3 position, Quaternion rotation, Vector3 scale, int sortingOrder)
        {
            Parent = parent;
            Position = position;
            Rotation = rotation;
            Scale = scale;
            SortingOrder = sortingOrder;
        }
    }
    private readonly Dictionary<int, HandAnchor> slots = new();
    private readonly Dictionary<byte, (ulong Id, Item Item)> heldItems = new();
    private readonly Dictionary<string, Transform> limbsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly int charType;
    private readonly bool hasTemplate;

    internal RemoteCharacterView(byte character, string displayName) : this(character, displayName, null!) { }

    internal RemoteCharacterView(byte character, string displayName, TMP_FontAsset font)
    {
        root = new GameObject("Co-op remote character");
        Body? template = LoadCharacter(character);
        speech = NativeCharacterSpeechView.Create(template, root.transform);
        hasTemplate = template;
        charType = template ? template!.charType : 0;
        FacialExpression? expression = template ? template!.GetComponentInChildren<FacialExpression>(true) : null;
        face = expression ? new CharacterFace(expression!) : null;
        limbs = template ? new GameObject[Math.Min(template!.limbs.Length, CharacterPoseWire.MaxLimbs)] : Array.Empty<GameObject>();
        for (int index = 0; index < limbs.Length; index++)
        {
            Transform source = template!.limbs[index].transform;
            GameObject limb = new(source.name);
            limb.transform.SetParent(root.transform, false);
            limbs[index] = limb;
            limbsByName[source.name] = limb.transform;
            limbsByName[template.limbs[index].name] = limb.transform;
            if (!string.IsNullOrEmpty(template.limbs[index].shortName))
                limbsByName[template.limbs[index].shortName] = limb.transform;
            foreach (SpriteRenderer sprite in source.GetComponentsInChildren<SpriteRenderer>(true))
            {
                // Nested body limbs each have their own network transform.
                if (sprite.GetComponentInParent<Limb>(true) != template.limbs[index])
                {
                    continue;
                }
                if (!ActiveBelow(sprite.transform, source))
                {
                    continue;
                }

                GameObject visual = new(sprite.name);
                visual.transform.SetParent(limb.transform, false);
                visual.transform.localPosition = source.InverseTransformPoint(sprite.transform.position);
                visual.transform.localRotation = Quaternion.Inverse(source.rotation) * sprite.transform.rotation;
                Vector3 sourceScale = source.lossyScale;
                Vector3 spriteScale = sprite.transform.lossyScale;
                visual.transform.localScale = new Vector3(spriteScale.x / sourceScale.x, spriteScale.y / sourceScale.y, 1);
                SpriteRenderer renderer = visual.AddComponent<SpriteRenderer>();
                renderer.sprite = sprite.sprite;
                renderer.sharedMaterial = sprite.sharedMaterial;
                renderer.color = sprite.color;
                renderer.sortingLayerID = sprite.sortingLayerID;
                renderer.sortingOrder = sprite.sortingOrder;
                renderer.flipX = sprite.flipX;
                renderer.flipY = sprite.flipY;
                renderer.enabled = sprite.enabled;
                if (face is not null)
                {
                    if (sprite == face.Head) head = renderer;
                    if (sprite == face.Eyes) eyes = renderer;
                    if (sprite == face.Nose) nose = renderer;
                }
            }
        }
        if (template)
        {
            for (int index = 0; index < template!.slots.Length; index++)
            {
                Transform slot = template.slots[index].transform;
                int limbIndex = Array.FindIndex(template.limbs, limb => slot.IsChildOf(limb.transform));
                Transform source = limbIndex >= 0 && limbIndex < limbs.Length
                    ? limbs[limbIndex].transform : root.transform;
                Transform templateLimb = limbIndex >= 0 && limbIndex < template.limbs.Length
                    ? template.limbs[limbIndex].transform : template.transform;
                Vector3 sourceScale = templateLimb.lossyScale;
                Vector3 slotScale = slot.lossyScale;
                slots[index] = new HandAnchor(source, templateLimb.InverseTransformPoint(slot.position),
                    Quaternion.Inverse(templateLimb.rotation) * slot.rotation,
                    new Vector3(SafeScale(slotScale.x, sourceScale.x), SafeScale(slotScale.y, sourceScale.y),
                        SafeScale(slotScale.z, sourceScale.z)), template.slots[index].spriteSortOrder);
            }
        }

        GameObject label = new("Steam name");
        label.transform.SetParent(root.transform, false);
        name = label.AddComponent<TextMeshPro>();
        name.font = font;
        name.text = displayName;
        name.richText = false;
        name.fontSize = 5;
        name.alignment = TextAlignmentOptions.Center;
        name.color = new Color32(143, 216, 182, 255);
        name.rectTransform.sizeDelta = new Vector2(14, 2);
        name.enableWordWrapping = false;
        name.overflowMode = TextOverflowModes.Ellipsis;
        name.sortingOrder = 2000;
        root.SetActive(false);
    }

    internal static Body? LoadCharacter(byte character)
    {
        string path = character switch { 0 => "Experiment", 1 => "Milky", 2 => "Dune", _ => "" };
        return path.Length == 0 ? null : Resources.Load<GameObject>("Special/Characters/" + path)?.GetComponentInChildren<Body>(true);
    }

    internal void Apply(CharacterPose pose, CharacterPose previous, float age, float interval)
    {
        Vector2 target = new(pose.X, pose.Y);
        Vector2 from = new(previous.X, previous.Y);
        bool interpolate = root.activeSelf && previous.Player == pose.Player && (target - from).sqrMagnitude <= 64f;
        float progress = interpolate ? Mathf.Clamp01(age / interval) : 1f;
        float prediction = Mathf.Clamp(age - interval, 0f, 0.08f);
        root.transform.position = (interpolate ? Vector2.Lerp(from, target, progress) : target) +
            new Vector2(pose.VelocityX, pose.VelocityY) * prediction;
        bool blendLimbs = interpolate && previous.FacingRight == pose.FacingRight && previous.Standing == pose.Standing;

        for (int index = 0; index < limbs.Length; index++)
        {
            GameObject limb = limbs[index];
            if (index >= pose.Limbs.Length || !pose.Limbs[index].Visible)
            {
                limb.SetActive(false);
                continue;
            }

            LimbPose targetLimb = pose.Limbs[index];
            Transform transform = limb.transform;
            Vector3 offset = new(targetLimb.X, targetLimb.Y, 0f);
            float angle = targetLimb.Angle;
            if (blendLimbs && index < previous.Limbs.Length && previous.Limbs[index].Visible)
            {
                LimbPose old = previous.Limbs[index];
                offset = Vector3.Lerp(new Vector3(old.X, old.Y, 0f), offset, progress);
                angle = Mathf.LerpAngle(old.Angle, angle, progress);
            }
            transform.localPosition = offset;
            transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            transform.localScale = new Vector3(pose.FacingRight ? targetLimb.ScaleX : -targetLimb.ScaleX, targetLimb.ScaleY, 1f);
            limb.SetActive(true);
        }

        ApplyHeldItems(pose, previous, progress, blendLimbs);
        ApplyFace(progress >= 0.5f || !previous.Face.HasExpression ? pose.Face : previous.Face);
        Vector3 headPosition = limbs.Length > 0 && limbs[0].activeSelf ? limbs[0].transform.localPosition : Vector3.zero;
        name.transform.localPosition = headPosition + new Vector3(0, 2.1f, -0.1f);
        root.SetActive(true);
        speech?.Tick(root.transform.TransformPoint(headPosition));
    }

    private void ApplyHeldItems(CharacterPose pose, CharacterPose previous, float progress, bool interpolate)
    {
        // Native item Update can re-enable an emitter from its replicated power state.
        foreach (var attached in heldItems.Values)
        {
            if (attached.Item) HeldItemVisualState.Disable(attached.Item, suppressNetworkLights: true);
        }
        foreach (HeldItemPose held in pose.HeldItems ?? Array.Empty<HeldItemPose>())
        {
            if (!heldItems.TryGetValue(held.Slot, out var attached) || attached.Id != held.ItemId || !attached.Item ||
                !slots.TryGetValue(held.Slot, out HandAnchor anchor))
            {
                continue;
            }

            float angle = held.Angle;
            if (interpolate)
            {
                foreach (HeldItemPose old in previous.HeldItems ?? Array.Empty<HeldItemPose>())
                {
                    if (old.ItemId == held.ItemId && old.Slot == held.Slot && old.Mirrored == held.Mirrored)
                    {
                        angle = Mathf.LerpAngle(old.Angle, angle, progress);
                        break;
                    }
                }
            }

            ApplyHeldRotation(attached.Item.transform, anchor, angle, held.Mirrored);
        }
    }

    private static void ApplyHeldRotation(Transform item, HandAnchor anchor, float angle, bool mirrored)
    {
        Transform parent = anchor.Parent;
        Vector3 right = Quaternion.Euler(0f, 0f, angle) * Vector3.right;
        Vector3 up = new(-right.y, right.x, 0f);
        Vector3 localRight = parent.InverseTransformVector(right).normalized;
        Vector3 localUp = new(-localRight.y, localRight.x, 0f);
        float verticalDirection = Mathf.Sign(Vector3.Dot(parent.TransformVector(localUp), up));
        Vector3 scale = anchor.Scale;
        item.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(localRight.y, localRight.x) * Mathf.Rad2Deg);
        item.localScale = new Vector3(
            SafeScale(Mathf.Abs(scale.x), parent.TransformVector(localRight).magnitude) * (mirrored ? -1f : 1f),
            SafeScale(Mathf.Abs(scale.y), parent.TransformVector(localUp).magnitude) * verticalDirection, scale.z);
    }

    private void ApplyFace(FacePose pose)
    {
        if (!pose.HasExpression || face is null)
        {
            return;
        }

        if (head && face.Head) head!.sprite = face.SpriteAt(pose.Head, face.Head.sprite);
        if (eyes && face.Eyes) eyes!.sprite = face.SpriteAt(pose.Eyes, face.Eyes.sprite);
        if (nose)
        {
            Color color = nose!.color;
            color.a = pose.NoseAlpha / 255f;
            nose.color = color;
        }
    }

    internal void PresentSpeech(CharacterSpeech line) => speech?.Present(line);
    internal void ClearSpeech() => speech?.Clear();
    internal string RenderedSpeech => speech?.RenderedText ?? string.Empty;
    internal Vector3 SpeechWorldPosition => speech?.WorldPosition ?? root.transform.position;

    internal void Hide()
    {
        if (root.activeSelf) ClearSpeech();
        root.SetActive(false);
    }

    internal bool Visible => root.activeSelf;
    internal Vector3 WorldPosition => root.transform.position;
    internal int SpriteRendererCount => root.GetComponentsInChildren<SpriteRenderer>(true).Length;
    internal Transform VisualRoot => root.transform;

    private static bool ActiveBelow(Transform target, Transform root)
    {
        for (Transform? current = target; current && current != root; current = current.parent)
        {
            if (!current.gameObject.activeSelf) return false;
        }
        return true;
    }

    internal void ShowAt(Vector2 position)
    {
        root.transform.position = new Vector3(position.x, position.y, 0f);
        root.SetActive(true);
    }

    internal int ChildItemCount()
    {
        int count = 0;
        foreach (Item item in root.GetComponentsInChildren<Item>(true))
            if (item && item.gameObject.activeInHierarchy) count++;
        return count;
    }

    internal int EnabledChildColliderCount()
    {
        int count = 0;
        foreach (Collider2D collider in root.GetComponentsInChildren<Collider2D>(true))
            if (collider && collider.enabled) count++;
        return count;
    }

    internal bool HasFiniteChildItemTransform()
    {
        foreach (Item item in root.GetComponentsInChildren<Item>(true))
        {
            if (!item) continue;
            Vector3 p = item.transform.localPosition;
            Vector3 s = item.transform.localScale;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y) ||
                float.IsNaN(s.x) || float.IsNaN(s.y) || s.x == 0f || s.y == 0f) return false;
        }
        return true;
    }

    internal bool TryGetHeldItem(ulong id, out Item? item)
    {
        foreach (var entry in heldItems.Values)
        {
            if (entry.Id == id && entry.Item && entry.Item.transform.IsChildOf(root.transform))
            {
                item = entry.Item;
                return true;
            }
        }
        item = null;
        return false;
    }

    internal bool TryGetHeldItem(byte slot, out ulong id, out Item? item)
    {
        if (heldItems.TryGetValue(slot, out var entry) && entry.Item && entry.Item.gameObject.activeInHierarchy)
        {
            id = entry.Id;
            item = entry.Item;
            return true;
        }
        id = 0;
        item = null;
        return false;
    }

    internal bool AttachHeldItem(ulong id, byte slot, Item item)
    {
        if (!item || !slots.TryGetValue(slot, out HandAnchor anchor)) return false;
        if (heldItems.TryGetValue(slot, out var attached) && attached.Item && attached.Item != item)
        {
            DetachHeldItem(attached.Item);
        }
        ForgetHeldItem(item);
        heldItems[slot] = (id, item);
        item.transform.SetParent(anchor.Parent, false);
        item.transform.localPosition = anchor.Position;
        item.transform.localRotation = anchor.Rotation * Quaternion.Euler(0f, 0f, item.Stats.slotRotation);
        float parentScale = Mathf.Max(0.0001f, Mathf.Abs(anchor.Parent.localScale.x));
        item.transform.localScale = new Vector3(anchor.Scale.x / parentScale, anchor.Scale.y, anchor.Scale.z);
        item.rb.simulated = false;
        item.gameObject.SetActive(true);
        SpriteRenderer? sprite = item.GetComponent<SpriteRenderer>();
        if (sprite)
        {
            sprite.enabled = true;
            sprite.sortingOrder = anchor.SortingOrder;
        }
        HeldItemVisualState.Disable(item, suppressNetworkLights: true);
        return true;
    }

    internal bool AttachWornItem(Item item)
    {
        if (!item || !item.Stats.wearable || !hasTemplate) return false;
        if (ForgetHeldItem(item)) HeldItemVisualState.Restore(item);
        Transform? limb = null;
        if (!string.IsNullOrEmpty(item.Stats.desiredWearLimb))
            limbsByName.TryGetValue(item.Stats.desiredWearLimb, out limb);
        limb ??= limbs.Length > 0 ? limbs[0].transform : root.transform;
        item.transform.SetParent(limb, false);
        item.transform.localPosition = Vector3.zero;
        item.transform.localRotation = Quaternion.identity;
        item.transform.localScale = Vector3.one;
        item.rb.simulated = false;
        item.gameObject.SetActive(true);
        SpriteRenderer? sprite = item.GetComponent<SpriteRenderer>();
        if (sprite)
        {
            sprite.enabled = true;
            sprite.sortingOrder = (limb.GetComponent<SpriteRenderer>()?.sortingOrder ?? 0) + item.Stats.wearableVisualOffset;
            if (item.TryGetComponent<WearableSpriteSwap>(out WearableSpriteSwap swap) &&
                charType >= 0 && charType < swap.sprites.Count && swap.sprites[charType])
            {
                sprite.sprite = swap.sprites[charType];
                if (charType < swap.customScale.Count && swap.customScale[charType] != 0f)
                    item.transform.localScale = Vector3.one * swap.customScale[charType];
            }
        }
        HeldItemVisualState.Disable(item);
        return true;
    }

    internal void DetachHeldItem(Item item)
    {
        if (!item) return;
        ForgetHeldItem(item);
        if (!item.transform.IsChildOf(root.transform)) return;
        HeldItemVisualState.Restore(item);
        item.transform.SetParent(null, true);
        item.gameObject.SetActive(false);
    }

    private bool ForgetHeldItem(Item item)
    {
        foreach (var entry in heldItems)
        {
            if (entry.Value.Item == item) return heldItems.Remove(entry.Key);
        }
        return false;
    }

    private static float SafeScale(float source, float divisor) =>
        Mathf.Abs(divisor) < 0.0001f ? 1f : source / divisor;

    public void Dispose() => UnityEngine.Object.Destroy(root);
}
