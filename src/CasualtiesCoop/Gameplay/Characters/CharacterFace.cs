using System.Collections.Generic;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Characters;

// The palette is built in the same order from the local character and the matching prefab.
internal sealed class CharacterFace
{
    private readonly List<Sprite?> palette = new() { null };
    private readonly Dictionary<Sprite, byte> ids = new();
    private readonly FacialExpression expression;
    internal SpriteRenderer Head { get; }
    internal SpriteRenderer Eyes => expression.eyes;
    internal SpriteRenderer Nose => expression.nosebleedSprite;

    internal CharacterFace(FacialExpression expression)
    {
        this.expression = expression;
        Head = expression.head ? expression.head : expression.GetComponent<SpriteRenderer>();
        Add(expression.defaultHead);
        Add(expression.defaultHeadMouth);
        Add(expression.defaultHeadMouthHalf);
        Add(expression.eyesGone);
        Add(expression.eyesGoneHealed);
        if (expression.eyeList is { } eyes)
        {
            foreach (Eye eye in eyes)
            {
                Add(eye.front);
                Add(eye.back);
            }
        }
        AddAll(expression.disfiguredHead);
        AddAll(expression.disfiguredHeadHeal);
    }

    internal FacePose Capture() => new(IdOf(Head), IdOf(Eyes), Nose && Nose.enabled && Nose.gameObject.activeInHierarchy
        ? (byte)Mathf.RoundToInt(Mathf.Clamp01(Nose.color.a) * 255f) : (byte)0);

    internal Sprite? SpriteAt(byte id, Sprite? fallback) => id == 0 || id >= palette.Count ? fallback : palette[id];

    private byte IdOf(SpriteRenderer renderer) => renderer && renderer.sprite && ids.TryGetValue(renderer.sprite, out byte id) ? id : (byte)0;

    private void Add(Sprite? sprite)
    {
        if (sprite && palette.Count <= byte.MaxValue && ids.TryAdd(sprite!, (byte)palette.Count))
        {
            palette.Add(sprite);
        }
    }

    private void AddAll(Sprite[]? sprites)
    {
        if (sprites is not null)
        {
            foreach (Sprite sprite in sprites)
            {
                Add(sprite);
            }
        }
    }
}
