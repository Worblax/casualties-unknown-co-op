using System.Diagnostics;
using BepInEx;
using HarmonyLib;
using CasualtiesCoop.Runtime;
using UnityEngine;

namespace CasualtiesCoop.Testing;

[BepInPlugin("casualtiesunknown.coop.testing", "Casualties Co-op Testing", "0.1.0")]
[BepInDependency("casualtiesunknown.coop")]
internal sealed class TestingPlugin : BaseUnityPlugin
{
    private static TestingPlugin? active;
    private TestRuntime? test;
    private Harmony? harmony;

    private void Awake()
    {
        if (!CoopRuntime.IsTestMode)
        {
            enabled = false;
            return;
        }

        try
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--coop-test");
            string manifest = args[index + 1];
            harmony = new Harmony("casualtiesunknown.coop.testing");
            CharacterFixtures.Install(harmony);
            MobFixtures.Install(harmony);
            FluidItemProbe.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(PlayerCamera), "HandleCameraPosition"),
                postfix: new HarmonyMethod(typeof(TestingPlugin), nameof(AfterNativeCamera)));
            test = new TestRuntime(manifest);
            active = this;
            Logger.LogInfo($"Game test started in process {Process.GetCurrentProcess().Id}.");
        }
        catch (Exception error)
        {
            Logger.LogError(error);
            Application.Quit(1);
        }
    }

    private void Update() => test?.Tick();

    private void OnGUI() => test?.Draw();

    private void FixedUpdate() => test?.PhysicsTick();

    private void LateUpdate() => test?.ApplyPhysicsScale();

    private static void AfterNativeCamera(PlayerCamera __instance) => active?.test?.ApplyCamera(__instance);

    private void OnDestroy()
    {
        active = null;
        test?.Dispose();
        harmony?.UnpatchSelf();
    }
}
