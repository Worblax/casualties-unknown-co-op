using System;
using System.Reflection;
using BepInEx.Logging;
using CasualtiesCoop.Core;
using CasualtiesCoop.Lobby;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Runtime;

internal sealed class SteamLobbyStartup : IDisposable
{
    private static readonly FieldInfo? Initialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private readonly ManualLogSource log;
    private readonly IMonotonicClock clock;
    private bool failed;
    private float nextProbe;

    internal SteamLobby? Lobby { get; private set; }
    internal string Status { get; private set; } = "Waiting for Steam.";

    internal SteamLobbyStartup(ManualLogSource log, IMonotonicClock clock)
    {
        this.log = log;
        this.clock = clock;
    }

    internal SteamLobby? TryOpen()
    {
        if (Lobby is not null || failed || clock.Now < nextProbe)
        {
            return Lobby;
        }
        nextProbe = clock.Now + 0.35f;

        try
        {
            SteamManager manager = SteamManager.instance;
            if (!manager || Initialized?.GetValue(manager) is not true)
            {
                SetStatus("Steam is unavailable. Launch from Steam.");
                return null;
            }
            if (!SteamUser.BLoggedOn())
            {
                SetStatus("Sign in to Steam.");
                return null;
            }

            Lobby = new SteamLobby(log, clock);
            log.LogInfo("Steam lobby integration ready.");
            return Lobby;
        }
        catch (Exception exception)
        {
            failed = true;
            log.LogError($"Steam lobby initialization failed: {exception}");
            SetStatus("Steam lobby error. See BepInEx log.");
            return null;
        }
    }

    private void SetStatus(string status)
    {
        if (Status != status)
        {
            Status = status;
            log.LogInfo(status);
        }
    }

    public void Dispose() => Lobby?.Dispose();
}
