using System;
using CasualtiesCoop.Networking;
using TMPro;
using UnityEngine;

namespace CasualtiesCoop.Game;

internal sealed class RemoteBodyView : IDisposable
{
    private readonly GameObject root;
    private readonly GameObject[] limbs;
    private readonly TextMeshPro name;
    private bool facingRight;
    private bool standing;

    internal RemoteBodyView(byte character, string displayName, TMP_FontAsset font)
    {
        root = new GameObject("Co-op remote character");
        Body? template = LoadCharacter(character);
        limbs = template ? new GameObject[Math.Min(template!.limbs.Length, SandboxWire.MaxLimbs)] : Array.Empty<GameObject>();
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

    internal void Apply(SandboxPosition pose, float age)
    {
        float prediction = Mathf.Clamp(age - 0.067f, 0f, 0.1f);
        Vector2 target = new(pose.X + pose.VelocityX * prediction, pose.Y + pose.VelocityY * prediction);
        bool snap = !root.activeSelf || (target - (Vector2)root.transform.position).sqrMagnitude > 64f;
        float ease = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
        root.transform.position = snap ? target : Vector2.Lerp(root.transform.position, target, ease);
        bool snapLimbs = snap || facingRight != pose.FacingRight || standing != pose.Standing;
        facingRight = pose.FacingRight;
        standing = pose.Standing;

        for (int index = 0; index < limbs.Length; index++)
        {
            GameObject limb = limbs[index];
            if (index >= pose.Limbs.Length || !pose.Limbs[index].Visible)
            {
                limb.SetActive(false);
                continue;
            }

            SandboxLimb targetLimb = pose.Limbs[index];
            Transform transform = limb.transform;
            bool snapThisLimb = snapLimbs || !limb.activeSelf;
            Vector3 offset = new(targetLimb.X, targetLimb.Y, 0f);
            transform.localPosition = snapThisLimb ? offset : Vector3.Lerp(transform.localPosition, offset, ease);
            float angle = snapThisLimb ? targetLimb.Angle : Mathf.LerpAngle(transform.localEulerAngles.z, targetLimb.Angle, ease);
            transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            transform.localScale = new Vector3(facingRight ? targetLimb.ScaleX : -targetLimb.ScaleX, targetLimb.ScaleY, 1f);
            limb.SetActive(true);
        }

        Vector3 head = limbs.Length > 0 && limbs[0].activeSelf ? limbs[0].transform.localPosition : Vector3.zero;
        name.transform.localPosition = head + new Vector3(0, 2.1f, -0.1f);
        root.SetActive(true);
    }

    internal void Hide() => root.SetActive(false);

    public void Dispose() => UnityEngine.Object.Destroy(root);
}
