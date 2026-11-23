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
    internal const string PluginVersion = "0.1.11";

    private static readonly FieldInfo? SteamInitialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private static CoopPlugin? instance;
    private Harmony? harmony;
    private SteamLobby? lobby;
    private CoopMenuView? view;
    private bool lobbyInitializationFailed;
    private bool previousRunInBackground;
    private bool soloPreview;
    private float nextMenuProbe;

    internal static SteamLobby? Lobby => instance?.lobby;
    internal static string SteamStatus { get; private set; } = "Waiting for Steam.";

    private void Awake()
    {
        instance = this;
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;

        harmony = new Harmony(PluginId);
        harmony.Patch(AccessTools.PropertyGetter(typeof(AdaptiveButton), nameof(AdaptiveButton.overlayActive)),
            prefix: OwnPatch(nameof(BlockUnderlyingMenu)));
        harmony.Patch(AccessTools.Method(typeof(SaveSystem), nameof(SaveSystem.SaveGame)),
            prefix: OwnPatch(nameof(AllowSave)));
        SceneManager.sceneLoaded += OnSceneLoaded;
        AttachMenu(PreRunScript.instance);
        Logger.LogInfo($"Casualties Co-op {PluginVersion} initialized.");
    }

    private static HarmonyMethod OwnPatch(string method) => new(AccessTools.Method(typeof(CoopPlugin), method));

    private void Update()
    {
        if (!view && Time.unscaledTime >= nextMenuProbe)
        {
            nextMenuProbe = Time.unscaledTime + 0.35f;
            AttachMenu(PreRunScript.instance);
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == "PreGen")
        {
            soloPreview = false;
        }

        AttachMenu(PreRunScript.instance);
    }

    private void AttachMenu(PreRunScript? menu)
    {
        if (view || !menu || !menu!.mainCanvas)
        {
            return;
        }

        try
        {
            view = menu.gameObject.AddComponent<CoopMenuView>();
            view.Install(menu);
            Logger.LogInfo("Native Co-op button attached to the main menu canvas.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Co-op menu attachment failed: {ex}");
        }
    }

    private static bool BlockUnderlyingMenu(ref bool __result)
    {
        if (instance?.view is not { PanelOpen: true })
        {
            return true;
        }

        __result = true;
        return false;
    }

    internal static void BeginSoloPreview()
    {
        if (instance is { } plugin)
        {
            plugin.soloPreview = true;
            plugin.Logger.LogInfo("Starting a solo co-op preview with checkpoint writes disabled.");
        }
    }

    private static bool AllowSave() => instance is not { soloPreview: true };

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
