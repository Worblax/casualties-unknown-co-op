using System;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal static class SandboxAlcoholHook
{
    private static readonly Action<ConsoleScript> ClearCommand =
        AccessTools.MethodDelegate<Action<ConsoleScript>>(
            AccessTools.Method(typeof(ConsoleScript), "AddCommandToLogAndClearInput"));

    internal static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(ConsoleScript), "TryExecuteCommand", new[] { typeof(string[]), typeof(bool) }),
        prefix: new HarmonyMethod(typeof(SandboxAlcoholHook), nameof(BeforeCommand)));

    private static bool BeforeCommand(ConsoleScript __instance, string[] args, bool addToLog)
    {
        if (args.Length < 2 || args[0] != "spawn" || args[1] != "alcohol" ||
            CoopRuntime.ActiveWorld is not { } session || !WorldGeneration.world ||
            WorldGeneration.world.biomeOverride != WorldGeneration.OverrideSceneType.Tutorial)
        {
            return true;
        }
        if (addToLog) ClearCommand(__instance);
        if (args.Length > 3 || (args.Length == 3 && args[2] is not ("player" or "cursor")))
        {
            __instance.Alert("Use spawn alcohol player or spawn alcohol cursor in a shared sandbox.");
            return false;
        }
        if (session.WaitingForRecovery || !PlayerCamera.main || !PlayerCamera.main.body || !Camera.main)
        {
            return false;
        }

        Vector2 at = args.Length == 3 && args[2] == "player"
            ? PlayerCamera.main.body.transform.position
            : Camera.main.ScreenToWorldPoint(Input.mousePosition);
        if (!session.Items.RequestAlcohol(at))
        {
            __instance.Alert("The shared bottle could not be spawned. Try closer to the character.");
        }
        return false;
    }
}
