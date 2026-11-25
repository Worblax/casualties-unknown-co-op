using TMPro;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Characters;

internal sealed class NativeCharacterSpeechView
{
    private readonly Talker native;
    private CharacterSpeech? pending;

    private NativeCharacterSpeechView(Talker native) => this.native = native;

    internal string RenderedText => native.text.enabled && native.text.gameObject.activeInHierarchy ? native.text.text : "";
    internal Vector3 WorldPosition => native.text.transform.position;

    internal static NativeCharacterSpeechView? Create(Body? template, Transform parent)
    {
        Talker? source = template ? template!.GetComponent<Talker>() : null;
        if (!source) return null;
        GameObject? prefab = source!.textPrefab ? source.textPrefab : source.text ? source.text.gameObject : null;
        if (!prefab) return null;

        GameObject anchor = new("Co-op speech");
        anchor.SetActive(false);
        anchor.transform.SetParent(parent, false);
        GameObject textObject = Object.Instantiate(prefab!, anchor.transform, false);
        TextMeshPro text = textObject.GetComponent<TextMeshPro>();
        if (!text)
        {
            Object.Destroy(anchor);
            return null;
        }

        text.text = "";
        text.richText = false;
        textObject.SetActive(true);
        Talker talker = anchor.AddComponent<Talker>();
        talker.text = text;
        // The body-less native Talker animates the prefab silently. Voice clips are networked independently.
        anchor.SetActive(true);
        return new NativeCharacterSpeechView(talker);
    }

    internal void Present(CharacterSpeech line) => pending = line;

    internal void Tick(Vector3 headWorldPosition)
    {
        // Body-less LateUpdate places the native text three units above the anchor, one less than a Body's head offset.
        native.transform.position = headWorldPosition + Vector3.up;
        if (pending is not { } line || CharacterSpeechHooks.CurrentLine(native) is null) return;

        pending = null;
        native.talkTimeMult = line.TimeMultiplier - 1f;
        CharacterSpeechHooks.CurrentLine(native) = line.Text;
        CharacterSpeechHooks.Timer(native) = 0f;
        native.text.text = "";
    }

    internal void Clear()
    {
        pending = null;
        if (CharacterSpeechHooks.CurrentLine(native) is not null)
        {
            CharacterSpeechHooks.CurrentLine(native) = "";
            native.text.text = "";
        }
    }
}
