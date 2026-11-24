using System;
using System.Collections.Generic;
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
    private SpriteRenderer? head;
    private SpriteRenderer? eyes;
    private SpriteRenderer? nose;
    private readonly struct HandAnchor
    {
        internal readonly Transform Parent;
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal readonly Vector3 Scale;

        internal HandAnchor(Transform parent, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Parent = parent;
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }
    }
    private readonly Dictionary<int, HandAnchor> hands = new();

    internal RemoteCharacterView(byte character, string displayName, TMP_FontAsset font)
    {
        root = new GameObject("Co-op remote character");
        Body? template = LoadCharacter(character);
        FacialExpression? expression = template ? template!.GetComponentInChildren<FacialExpression>(true) : null;
        face = expression ? new CharacterFace(expression!) : null;
        limbs = template ? new GameObject[Math.Min(template!.limbs.Length, CharacterPoseWire.MaxLimbs)] : Array.Empty<GameObject>();
        for (int index = 0; index < limbs.Length; index++)
        {
            Transform source = template!.limbs[index].transform;
            GameObject limb = new(source.name);
            limb.transform.SetParent(root.transform, false);
            limbs[index] = limb;
            foreach (SpriteRenderer sprite in source.GetComponentsInChildren<SpriteRenderer>(true))
            {
                // Nested body limbs each have their own network transform.
                if (sprite.GetComponentInParent<Limb>(true) != template.limbs[index])
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
                if (!template.slots[index].isHand) continue;
                Transform slot = template.slots[index].transform;
                int limbIndex = Array.FindIndex(template.limbs, limb => slot.IsChildOf(limb.transform));
                Transform source = limbIndex >= 0 && limbIndex < limbs.Length
                    ? limbs[limbIndex].transform : root.transform;
                Vector3 sourceScale = source.lossyScale;
                Vector3 slotScale = slot.lossyScale;
                hands[index] = new HandAnchor(source, source.InverseTransformPoint(slot.position),
                    Quaternion.Inverse(source.rotation) * slot.rotation,
                    new Vector3(slotScale.x / sourceScale.x, slotScale.y / sourceScale.y, 1f));
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

        ApplyFace(progress >= 0.5f || !previous.Face.HasExpression ? pose.Face : previous.Face);
        Vector3 headPosition = limbs.Length > 0 && limbs[0].activeSelf ? limbs[0].transform.localPosition : Vector3.zero;
        name.transform.localPosition = headPosition + new Vector3(0, 2.1f, -0.1f);
        root.SetActive(true);
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

    internal void Hide() => root.SetActive(false);

    internal bool AttachHeldItem(byte slot, Item item)
    {
        if (!item || !hands.TryGetValue(slot, out HandAnchor anchor)) return false;
        item.transform.SetParent(anchor.Parent, false);
        item.transform.localPosition = anchor.Position;
        item.transform.localRotation = anchor.Rotation;
        item.transform.localScale = anchor.Scale;
        item.rb.simulated = false;
        item.gameObject.SetActive(true);
        SpriteRenderer? source = anchor.Parent.GetComponentInChildren<SpriteRenderer>(true);
        SpriteRenderer? sprite = item.GetComponent<SpriteRenderer>();
        if (sprite)
        {
            sprite.enabled = true;
            if (source) sprite.sortingOrder = source.sortingOrder + 1;
        }
        return true;
    }

    internal void DetachHeldItem(Item item)
    {
        if (!item || !item.transform.IsChildOf(root.transform)) return;
        item.transform.SetParent(null, true);
        item.gameObject.SetActive(false);
    }

    public void Dispose() => UnityEngine.Object.Destroy(root);
}
