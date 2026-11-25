using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesCoop.Runtime;
using HarmonyLib;

namespace CasualtiesCoop.Gameplay.Characters;

internal static class CharacterSpeechHooks
{
    private const string TimerField = "timeSinceTalked";
    internal static readonly AccessTools.FieldRef<Talker, string> CurrentLine =
        AccessTools.FieldRefAccess<Talker, string>("currentString");
    internal static readonly AccessTools.FieldRef<Talker, float> Timer =
        AccessTools.FieldRefAccess<Talker, float>(TimerField);
    private static Action<string, float>? testCapture;

    internal static void BindTestCapture(Action<string, float>? capture)
    {
        if (!CoopRuntime.IsTestMode)
        {
            throw new InvalidOperationException("Speech test hooks require an opt-in test process.");
        }
        testCapture = capture;
    }

    internal static void Install(Harmony harmony)
    {
        MethodInfo method = AccessTools.Method(typeof(Talker), nameof(Talker.Talk),
            new[] { typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool) });
        harmony.Patch(method, transpiler: new HarmonyMethod(typeof(CharacterSpeechHooks), nameof(ObserveCommittedSpeech)));
    }

    private static IEnumerable<CodeInstruction> ObserveCommittedSpeech(IEnumerable<CodeInstruction> input)
    {
        FieldInfo timer = AccessTools.Field(typeof(Talker), TimerField);
        MethodInfo capture = AccessTools.Method(typeof(CharacterSpeechHooks), nameof(Capture));
        foreach (CodeInstruction instruction in input)
        {
            yield return instruction;
            // Native Talk resets this clock only after selecting and transforming an accepted line.
            if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, timer))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, capture);
            }
        }
    }

    private static void Capture(Talker talker)
    {
        Body body = talker.body;
        if (!body || !PlayerCamera.main || PlayerCamera.main.body != body) return;

        float multiplier = body.consciousness * 0.01f * (1f + talker.talkTimeMult);
        multiplier = float.IsFinite(multiplier)
            ? Math.Clamp(multiplier, CharacterSpeechWire.MinMultiplier, CharacterSpeechWire.MaxMultiplier)
            : 1f;
        string text = CurrentLine(talker);
        if (CoopRuntime.IsTestMode) testCapture?.Invoke(text, multiplier);
        else CoopRuntime.ActiveWorld?.CaptureSpeech(text, multiplier);
    }
}
