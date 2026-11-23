using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace CasualtiesCoop.Sandbox;

internal sealed class SandboxScene : IDisposable
{
    private static readonly FieldInfo? StartedLoading = AccessTools.Field(typeof(PreRunScript), "startedLoading");
    private readonly bool achievementsWereDisabled;

    internal TMP_FontAsset Font { get; }
    internal bool CourseLoaded { get; private set; }

    private SandboxScene(PreRunScript menu)
    {
        Font = menu.versionText.font;
        achievementsWereDisabled = SteamManager.disableAchievements;
    }

    internal static bool CanEnter(PreRunScript? menu) => menu && StartedLoading?.GetValue(menu) is false;

    internal static SandboxScene Enter(PreRunScript menu, int character, string difficulty)
    {
        SandboxScene scene = new(menu);
        try
        {
            SteamManager.disableAchievements = true;
            menu.ApplyPreset(difficulty);
            menu.SelectCharacter(character);
            menu.StartTutorial();
            // StartTutorial clears the preset before its scene-loading coroutine waits for the fade.
            WorldGeneration.runSettings = menu.runSettings;
            return scene;
        }
        catch
        {
            scene.Dispose();
            throw;
        }
    }

    internal void Tick()
    {
        if (CourseLoaded || WorldGeneration.world is not { worldExists: true, generatingWorld: false } world ||
            !world || world.biomeOverride != WorldGeneration.OverrideSceneType.Tutorial ||
            !TutorialHandler.main || !PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        TutorialHandler tutorial = TutorialHandler.main;
        if (!tutorial.activeCourse)
        {
            tutorial.StartCourse<SandboxCourse>();
            tutorial.courseSelectScreen.SetActive(false);
        }

        // Each native course creates its own terminal. Commands stay in this process's ConsoleScript.
        CourseLoaded = tutorial.activeCourse is SandboxCourse && ConsoleScript.instance &&
            tutorial.lastSpawnedObject && PlayerCamera.main.body.transform.position.y < -70f;
    }

    public void Dispose() => SteamManager.disableAchievements = achievementsWereDisabled;
}
