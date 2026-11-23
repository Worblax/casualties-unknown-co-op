using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Game;

internal static class SandboxRecoveryHooks
{
    internal static void Install(Harmony harmony)
    {
        Prefix(harmony, nameof(PlayerCamera.HandleInput), nameof(AllowInput));
        Prefix(harmony, "HandleDragging", nameof(AllowDragging));
        Prefix(harmony, nameof(PlayerCamera.SetTimeScale), nameof(AllowTimeScale));
    }

    private static void Prefix(Harmony harmony, string method, string prefix) => harmony.Patch(
        AccessTools.Method(typeof(PlayerCamera), method), prefix: new HarmonyMethod(typeof(SandboxRecoveryHooks), prefix));

    private static bool AllowInput(PlayerCamera __instance)
    {
        if (CoopPlugin.ActiveSandbox is not { WaitingForRecovery: true })
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

    private static bool AllowDragging() => CoopPlugin.ActiveSandbox is not { WaitingForRecovery: true };

    private static bool AllowTimeScale(PlayerCamera.SpeedType __0) => CoopPlugin.ActiveSandbox is null ||
        !PauseHandler.paused || __0 != PlayerCamera.SpeedType.Paused;
}
