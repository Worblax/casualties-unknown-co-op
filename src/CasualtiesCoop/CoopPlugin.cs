using BepInEx;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CasualtiesCoop;

[BepInPlugin(PluginId, "Casualties Co-op", PluginVersion)]
internal sealed class CoopPlugin : BaseUnityPlugin
{
    private const string PluginId = "casualtiesunknown.coop";
    private const string PluginVersion = "0.1.16";
    private CoopRuntime? runtime;
    private Harmony? harmony;
    private bool previousRunInBackground;

    private void Awake()
    {
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;

        runtime = new CoopRuntime(Logger);
        harmony = new Harmony(PluginId);
        CoopPatches.Install(harmony);
        SceneManager.sceneLoaded += runtime.SceneLoaded;
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
        Application.runInBackground = previousRunInBackground;
    }
}
