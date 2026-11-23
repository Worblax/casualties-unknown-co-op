using CasualtiesCoop.Gameplay.Audio;
using CasualtiesCoop.Gameplay.Fluids;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Gameplay.Recovery;
using HarmonyLib;

namespace CasualtiesCoop.Runtime;

internal static class CoopPatches
{
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.PropertyGetter(typeof(AdaptiveButton), nameof(AdaptiveButton.overlayActive)),
            prefix: new HarmonyMethod(typeof(CoopPatches), nameof(BlockUnderlyingMenu)));
        harmony.Patch(AccessTools.Method(typeof(SaveSystem), nameof(SaveSystem.SaveGame)),
            prefix: new HarmonyMethod(typeof(CoopPatches), nameof(AllowVanillaSave)));
        CharacterAudioHooks.Install(harmony);
        SharedItemHooks.Install(harmony);
        TerrainFluidHooks.Install(harmony);
        RecoveryInputHooks.Install(harmony);
    }

    private static bool BlockUnderlyingMenu(ref bool __result)
    {
        if (!CoopRuntime.MenuOpen)
        {
            return true;
        }
        __result = true;
        return false;
    }

    private static bool AllowVanillaSave() => !CoopRuntime.SuppressVanillaSave;
}
