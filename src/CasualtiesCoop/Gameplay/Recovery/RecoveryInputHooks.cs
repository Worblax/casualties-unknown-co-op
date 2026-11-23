using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Recovery;

internal static class RecoveryInputHooks
{
    internal static void Install(Harmony harmony)
    {
        Prefix(harmony, nameof(PlayerCamera.HandleInput), nameof(AllowInput));
        Prefix(harmony, "HandleDragging", nameof(AllowDragging));
        Prefix(harmony, nameof(PlayerCamera.SetTimeScale), nameof(AllowTimeScale));
    }

    private static void Prefix(Harmony harmony, string method, string prefix) => harmony.Patch(
        AccessTools.Method(typeof(PlayerCamera), method), prefix: new HarmonyMethod(typeof(RecoveryInputHooks), prefix));

    private static bool AllowInput(PlayerCamera __instance)
    {
        if (CoopRuntime.ActiveWorld is not { WaitingForRecovery: true })
        {
            return true;
        }

        __instance.body.moveDir = Vector2.zero;
        if (Input.GetKeyDown(KeyBinds.GetBind("pause")))
        {
            PauseHandler.TogglePause();
        }
        return false;
    }

    private static bool AllowDragging() => CoopRuntime.ActiveWorld is not { WaitingForRecovery: true };

    private static bool AllowTimeScale(PlayerCamera.SpeedType __0) => CoopRuntime.ActiveWorld is null ||
        !PauseHandler.paused || __0 != PlayerCamera.SpeedType.Paused;
}
