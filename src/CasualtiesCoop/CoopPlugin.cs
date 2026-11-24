using BepInEx;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Gameplay.Terrain;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CasualtiesCoop;

[BepInPlugin(PluginId, "Casualties Co-op", PluginVersion)]
internal sealed class CoopPlugin : BaseUnityPlugin
{
    private const string PluginId = "casualtiesunknown.coop";
    private const string PluginVersion = "0.1.17";
    private CoopRuntime? runtime;
    private Harmony? harmony;
    private bool previousRunInBackground;

    private void Awake()
    {
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;

        CoopDiagnostics.Install(Config, Logger);
        WorldChunkReplication.Configure(Config);
        harmony = new Harmony(PluginId);
        CoopPatches.Install(harmony);
        if (!CoopRuntime.IsTestMode)
        {
            runtime = new CoopRuntime(Logger);
            SceneManager.sceneLoaded += runtime.SceneLoaded;
        }
        Logger.LogInfo($"Casualties Co-op {PluginVersion} initialized.");
    }

    private void Update() => runtime?.Tick();

    private void LateUpdate() => runtime?.TickPresentation();

    private void OnDestroy()
    {
        if (runtime is not null)
        {
            SceneManager.sceneLoaded -= runtime.SceneLoaded;
            runtime.Dispose();
        }

        harmony?.UnpatchSelf();
        CoopDiagnostics.Close();
        Application.runInBackground = previousRunInBackground;
    }
}
