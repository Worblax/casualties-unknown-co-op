using System;
using System.Collections.Generic;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Gameplay.Characters;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Audio;

internal sealed class SpatialCharacterAudio : IDisposable
{
    private const float HearDistance = 30f;
    private const int MaxPlayback = 48;
    private readonly Dictionary<string, AudioClip> clips = new(StringComparer.Ordinal);
    private readonly Dictionary<AudioClip, string> clipKeys = new();
    private readonly List<(AudioSource Source, Vector2 Origin, float Volume, ulong Follow, Vector2 Offset)> playing = new();
    private readonly PlayerPositionLookup playerPosition;

    internal SpatialCharacterAudio(PlayerPositionLookup playerPosition) => this.playerPosition = playerPosition;

    internal string GetClipKey(AudioClip clip, Body body)
    {
        if (body.talker && body.talker.talkSoundCustom == clip)
        {
            return "@voice";
        }

        IndexFootsteps();
        return clipKeys.TryGetValue(clip, out string key) ? key : clip.name;
    }

    private void IndexFootsteps()
    {
        if (clipKeys.Count != 0 || !WorldGeneration.world || WorldGeneration.world.footstepDict is not { } footsteps)
        {
            return;
        }

        foreach (KeyValuePair<string, AudioClip[]> surface in footsteps)
        {
            for (int index = 0; index < surface.Value.Length; index++)
            {
                AudioClip clip = surface.Value[index];
                if (clip)
                {
                    string key = $"@step/{surface.Key}/{clip.name}";
                    clipKeys[clip] = key;
                    clips[key] = clip;
                }
            }
        }
    }

    internal void Play(CharacterSound sound, byte character, Vector2 emitter, Vector2 listener)
    {
        Vector2 origin = new(sound.X, sound.Y);
        if ((origin - listener).sqrMagnitude >= HearDistance * HearDistance || playing.Count >= MaxPlayback)
        {
            return;
        }

        IndexFootsteps();
        if (!clips.TryGetValue(sound.Clip, out AudioClip? clip))
        {
            if (sound.Clip == "@voice")
            {
                clip = RemoteCharacterView.LoadCharacter(character)?.GetComponent<Talker>()?.talkSoundCustom;
            }
            else if (!sound.Clip.StartsWith("@", StringComparison.Ordinal))
            {
                clip = Resources.Load<AudioClip>("Sounds/" + sound.Clip);
                if (clip)
                {
                    clips[sound.Clip] = clip;
                }
            }
        }

        if (!clip)
        {
            CoopDiagnostics.Count("Audio", "Clip unavailable locally");
            return;
        }

        AudioSource source = Sound.Play(clip, origin, twoDimensional: true, pitchShift: false,
            volume: sound.Volume * DistanceGain(origin, listener), pitch: sound.Pitch);
        if (source)
        {
            CoopDiagnostics.Count("Audio played", sound.Clip);
            SetPan(source, origin, listener);
            playing.Add((source, origin, sound.Volume, sound.FollowPlayer ? sound.Player : 0, origin - emitter));
        }
    }

    internal void Tick(Vector2 listener)
    {
        for (int index = playing.Count - 1; index >= 0; index--)
        {
            var entry = playing[index];
            if (!entry.Source)
            {
                playing.RemoveAt(index);
                continue;
            }

            if (entry.Follow != 0 && playerPosition(entry.Follow, out Vector2 at))
            {
                entry.Origin = at + entry.Offset;
                playing[index] = entry;
                entry.Source.transform.position = entry.Origin;
            }
            entry.Source.volume = entry.Volume * DistanceGain(entry.Origin, listener);
            SetPan(entry.Source, entry.Origin, listener);
        }
    }

    private static float DistanceGain(Vector2 origin, Vector2 listener) =>
        1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2f, HearDistance, Vector2.Distance(origin, listener)));

    private static void SetPan(AudioSource source, Vector2 origin, Vector2 listener) =>
        source.panStereo = Mathf.Clamp((origin.x - listener.x) / 16f, -0.85f, 0.85f);

    public void Dispose()
    {
        foreach (var entry in playing)
        {
            if (entry.Source)
            {
                UnityEngine.Object.Destroy(entry.Source.gameObject);
            }
        }
    }
}
