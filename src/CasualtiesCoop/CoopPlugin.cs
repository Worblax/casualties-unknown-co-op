using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop;

[BepInPlugin(PluginId, "Casualties Co-op", PluginVersion)]
internal sealed class CoopPlugin : BaseUnityPlugin
{
    internal const string PluginId = "casualtiesunknown.coop";
    internal const string PluginVersion = "0.1.3";

    private static readonly FieldInfo? SteamInitialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private static CoopPlugin? instance;
    private Harmony? harmony;
    private SteamLobby? lobby;
    private LobbyPanel? panel;
    private string steamStatus = "Waiting for Steam.";
    private bool previousRunInBackground;

    private void Awake()
    {
        instance = this;
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        Logger.LogInfo($"Casualties Co-op {PluginVersion} initialized.");

        harmony = new Harmony(PluginId);
        MethodInfo start = AccessTools.Method(typeof(PreRunScript), "Start");
        MethodInfo attach = AccessTools.Method(typeof(CoopPlugin), nameof(AttachMenu));
        harmony.Patch(start, postfix: new HarmonyMethod(attach));
        Logger.LogInfo("Co-op menu hook installed.");
    }

    private static void AttachMenu(PreRunScript __instance)
    {
        if (!__instance.TryGetComponent<CoopMenuView>(out _))
        {
            __instance.gameObject.AddComponent<CoopMenuView>();
            instance?.Logger.LogInfo("Co-op menu attached to the main menu.");
        }
    }

    internal static void TickMenu()
    {
        instance?.TryInitializeLobby();
    }

    internal static void DrawMenu()
    {
        if (instance is not { } plugin)
        {
            return;
        }

        if (plugin.panel is { } panel)
        {
            panel.Draw();
        }
        else
        {
            GUI.Box(new Rect(Mathf.Max(0, Screen.width - 300), 24, 276, 54),
                "Co-op\n" + plugin.steamStatus);
        }
    }

    private void TryInitializeLobby()
    {
        if (lobby is not null)
        {
            return;
        }

        try
        {
            SteamManager manager = SteamManager.instance;
            if (SteamInitialized is null || !manager || SteamInitialized.GetValue(manager) is not true)
            {
                steamStatus = "Steam is unavailable. Launch from Steam.";
                return;
            }

            if (!SteamUser.BLoggedOn())
            {
                steamStatus = "Sign in to Steam.";
                return;
            }

            lobby = new SteamLobby();
            panel = new LobbyPanel(lobby);
            Logger.LogInfo("Steam lobby integration ready.");
            lobby.TryJoinLaunchInvitation();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Steam lobby initialization failed: {ex}");
            steamStatus = "Steam lobby error. See BepInEx log.";
            panel?.Dispose();
            panel = null;
            lobby?.Dispose();
            lobby = null;
            lobbyInitializationFailed = true;
        }
    }

    private bool lobbyInitializationFailed;

    internal static bool CanInitialize => instance is { lobbyInitializationFailed: false };

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
        Application.runInBackground = previousRunInBackground;
        panel?.Dispose();
        lobby?.Dispose();
        instance = null;
    }
}
