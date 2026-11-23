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
    internal const string PluginVersion = "0.1.2";

    private static readonly FieldInfo? SteamInitialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private SteamLobby? lobby;
    private LobbyPanel? panel;
    private bool reportedSteamWait;
    private bool previousRunInBackground;

    private void Awake()
    {
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        Logger.LogInfo($"Casualties Co-op {PluginVersion} initialized.");
        if (SteamInitialized is null)
        {
            Logger.LogError("The game's Steam initialization field is unavailable.");
        }
    }

    private void Update()
    {
        if (lobby is not null || SteamInitialized is null)
        {
            return;
        }

        SteamManager manager = SteamManager.instance;
        if (!manager || SteamInitialized.GetValue(manager) is not true)
        {
            ReportSteamWait(manager ? "the game did not initialize Steam" : "the game has not created SteamManager");
            return;
        }

        try
        {
            if (!SteamUser.BLoggedOn())
            {
                ReportSteamWait("the Steam client is not signed in");
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
            panel = null;
            lobby?.Dispose();
            lobby = null;
            enabled = false;
        }
    }

    private void ReportSteamWait(string reason)
    {
        if (!reportedSteamWait && Time.realtimeSinceStartup >= 15f)
        {
            reportedSteamWait = true;
            Logger.LogWarning($"Steam lobby is waiting: {reason}.");
        }
    }

    private void OnGUI()
    {
        if (PreRunScript.instance)
        {
            panel?.Draw();
        }
    }

    private void OnDestroy()
    {
        Application.runInBackground = previousRunInBackground;
        panel?.Dispose();
        lobby?.Dispose();
    }
}
