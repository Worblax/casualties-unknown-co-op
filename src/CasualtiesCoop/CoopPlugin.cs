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
    internal const string PluginVersion = "0.1.0";

    private static readonly FieldInfo? SteamInitialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private SteamLobby? lobby;
    private LobbyPanel? panel;

    private void Awake()
    {
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
            return;
        }

        try
        {
            if (!SteamUser.BLoggedOn())
            {
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

    private void OnGUI()
    {
        if (PreRunScript.instance)
        {
            panel?.Draw();
        }
    }

    private void OnDestroy()
    {
        panel?.Dispose();
        lobby?.Dispose();
    }
}
