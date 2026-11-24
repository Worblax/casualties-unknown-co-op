using System;
using System.Reflection;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Audio;

internal static class CharacterAudioHooks
{
    private static int emitterDepth;
    private static int resourceDepth;

    internal static void Install(Harmony harmony)
    {
        Type[] tail = { typeof(Vector2), typeof(bool), typeof(bool), typeof(Transform), typeof(float),
            typeof(float), typeof(bool), typeof(bool) };
        Type[] signature = new Type[tail.Length + 1];
        tail.CopyTo(signature, 1);
        signature[0] = typeof(AudioClip);
        harmony.Patch(AccessTools.Method(typeof(Sound), nameof(Sound.Play), signature), postfix: Patch(nameof(ClipPlayed)));
        signature[0] = typeof(string);
        harmony.Patch(AccessTools.Method(typeof(Sound), nameof(Sound.Play), signature), prefix: Patch(nameof(EnterResource)),
            postfix: Patch(nameof(ResourcePlayed)), finalizer: Patch(nameof(LeaveResource)));

        foreach (string method in new[] { "Update", "FixedUpdate", "FootStep", "Attack", "Jump", "TryExertSound", "ScaldSound", "ThrowItem" })
        {
            MethodInfo entry = AccessTools.Method(typeof(Body), method);
            harmony.Patch(entry, prefix: Patch(nameof(EnterBody)), finalizer: Patch(nameof(LeaveEmitter)),
                postfix: method == "Update" ? Patch(nameof(BodyUpdated)) : null);
        }

        harmony.Patch(AccessTools.Method(typeof(Talker), "Update"),
            prefix: Patch(nameof(EnterTalker)), finalizer: Patch(nameof(LeaveEmitter)));
    }

    private static HarmonyMethod Patch(string name) => new(typeof(CharacterAudioHooks), name);

    private static bool IsLocal(Body? body) =>
        CoopRuntime.ActiveWorld is not null && body && PlayerCamera.main && body == PlayerCamera.main.body;

    private static void EnterBody(Body __instance, AudioSource ___slideSource, out (int Depth, bool Sliding) __state)
    {
        __state = (emitterDepth, ___slideSource && ___slideSource.isPlaying);
        if (IsLocal(__instance))
        {
            emitterDepth++;
        }
    }

    private static void BodyUpdated(Body __instance, AudioSource ___slideSource, (int Depth, bool Sliding) __state)
    {
        if (!__state.Sliding && ___slideSource && ___slideSource.isPlaying && ___slideSource.clip && IsLocal(__instance))
        {
            CoopRuntime.ActiveWorld?.CaptureSound("slide", ___slideSource.clip, __instance.transform.position, ___slideSource, followPlayer: true);
        }
    }

    private static void EnterTalker(Talker __instance, out (int Depth, bool Sliding) __state)
    {
        __state = (emitterDepth, false);
        if (IsLocal(__instance.body))
        {
            emitterDepth++;
        }
    }

    private static void LeaveEmitter((int Depth, bool Sliding) __state) => emitterDepth = __state.Depth;

    private static void EnterResource(out int __state)
    {
        __state = resourceDepth;
        resourceDepth++;
    }

    private static void LeaveResource(int __state) => resourceDepth = __state;

    private static void ResourcePlayed(string clip, Vector2 pos, Transform? follow, AudioSource? __result) =>
        Capture(clip, pos, follow, __result);

    private static void ClipPlayed(Vector2 pos, Transform? follow, AudioSource? __result)
    {
        if (resourceDepth == 0)
        {
            Capture(null, pos, follow, __result);
        }
    }

    private static void Capture(string? key, Vector2 position, Transform? follow, AudioSource? playback)
    {
        if (CoopRuntime.ActiveWorld is not { } world || !playback || !playback!.clip ||
            !PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Transform body = PlayerCamera.main.body.transform;
        bool followsLocalPlayer = follow && (follow == body || follow!.IsChildOf(body));
        if (emitterDepth > 0 || followsLocalPlayer)
        {
            world.CaptureSound(key, playback.clip, position, playback, followsLocalPlayer);
        }
    }
}
