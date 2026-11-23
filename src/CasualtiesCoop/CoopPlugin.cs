using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CasualtiesCoop;

[BepInPlugin(PluginId, "Casualties Co-op", PluginVersion)]
internal sealed class CoopPlugin : BaseUnityPlugin
{
    internal const string PluginId = "casualtiesunknown.coop";
    internal const string PluginVersion = "0.1.4";

    private static readonly FieldInfo? SteamInitialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private static CoopPlugin? instance;
    private Harmony? harmony;
    private SteamLobby? lobby;
    private bool lobbyInitializationFailed;
    private bool previousRunInBackground;

    internal static SteamLobby? Lobby => instance?.lobby;
    internal static string SteamStatus { get; private set; } = "Waiting for Steam.";

    private void Awake()
    {
        instance = this;
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        Logger.LogInfo($"Casualties Co-op {PluginVersion} initialized.");

        harmony = new Harmony(PluginId);
        MethodInfo attach = AccessTools.Method(typeof(CoopPlugin), nameof(AttachMenu));
        HarmonyMethod postfix = new(attach);
        harmony.Patch(AccessTools.Method(typeof(PreRunScript), "Awake"), postfix: postfix);
        harmony.Patch(AccessTools.Method(typeof(PreRunScript), "Update"), postfix: postfix);
        SceneManager.sceneLoaded += OnSceneLoaded;
        AttachMenu(PreRunScript.instance);
        Logger.LogInfo("Native co-op menu hooks installed.");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        instance?.Logger.LogDebug($"Checking scene {scene.name} for the co-op menu.");
        AttachMenu(PreRunScript.instance);
    }

    private static void AttachMenu(PreRunScript? __instance)
    {
        if (!__instance || !__instance!.mainCanvas || __instance.TryGetComponent<CoopMenuView>(out _))
        {
            return;
        }

        try
        {
            CoopMenuView view = __instance.gameObject.AddComponent<CoopMenuView>();
            view.Install(__instance);
            instance?.Logger.LogInfo("Native Co-op button attached to the main menu canvas.");
        }
        catch (Exception ex)
        {
            instance?.Logger.LogError($"Co-op menu attachment failed: {ex}");
        }
    }

    internal static void TickMenu()
    {
        if (instance is { lobby: null, lobbyInitializationFailed: false } plugin)
        {
            plugin.TryInitializeLobby();
        }
    }

    private void TryInitializeLobby()
    {
        try
        {
            SteamManager manager = SteamManager.instance;
            if (SteamInitialized is null || !manager || SteamInitialized.GetValue(manager) is not true)
            {
                SetSteamStatus("Steam is unavailable. Launch from Steam.");
                return;
            }

            if (!SteamUser.BLoggedOn())
            {
                SetSteamStatus("Sign in to Steam.");
                return;
            }

            lobby = new SteamLobby();
            Logger.LogInfo("Steam lobby integration ready.");
            lobby.TryJoinLaunchInvitation();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Steam lobby initialization failed: {ex}");
            SetSteamStatus("Steam lobby error. See BepInEx log.");
            lobby?.Dispose();
            lobby = null;
            lobbyInitializationFailed = true;
        }
    }

    private void SetSteamStatus(string status)
    {
        if (SteamStatus != status)
        {
            SteamStatus = status;
            Logger.LogInfo(status);
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        harmony?.UnpatchSelf();
        Application.runInBackground = previousRunInBackground;
        lobby?.Dispose();
        lobby = null;
        instance = null;
    }
}
