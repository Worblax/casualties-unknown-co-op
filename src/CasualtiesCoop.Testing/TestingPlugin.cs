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
    private MultiProcessRuntime? multi;
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
            harmony = new Harmony("casualtiesunknown.coop.testing");
            CharacterFixtures.Install(harmony);
            MobFixtures.Install(harmony);
            FluidItemProbe.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(PlayerCamera), "HandleCameraPosition"),
                postfix: new HarmonyMethod(typeof(TestingPlugin), nameof(AfterNativeCamera)));
            int multiIndex = Array.IndexOf(args, "--coop-mp");
            if (multiIndex >= 0)
            {
                string manifest = args[multiIndex + 1];
                MultiProcessManifest config = MultiProcessManifest.Load(manifest);
                MultiProcessDataIsolation.Initialize(config.DataRoot);
                MultiProcessDataIsolation.Patch(harmony);
                multi = new MultiProcessRuntime(manifest, Logger);
            }
            else
            {
                int index = Array.IndexOf(args, "--coop-test");
                if (index < 0) throw new FormatException("Missing test manifest argument.");
                test = new TestRuntime(args[index + 1]);
            }
            active = this;
            Logger.LogInfo($"Game test started in process {Process.GetCurrentProcess().Id}.");
        }
        catch (Exception error)
        {
            Logger.LogError(error);
            Application.Quit(1);
        }
    }

    private void Update()
    {
        test?.Tick();
        multi?.Tick();
    }

    private void OnGUI() => test?.Draw();

    private void FixedUpdate() => test?.PhysicsTick();

    private void LateUpdate()
    {
        test?.ApplyPhysicsScale();
        multi?.TickPresentation();
    }

    private static void AfterNativeCamera(PlayerCamera __instance) => active?.test?.ApplyCamera(__instance);

    private void OnDestroy()
    {
        active = null;
        test?.Dispose();
        multi?.Dispose();
        harmony?.UnpatchSelf();
    }
}
