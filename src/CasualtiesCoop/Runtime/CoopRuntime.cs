using System;
using BepInEx.Logging;
using CasualtiesCoop.Core;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Sandbox;
using CasualtiesCoop.UI;
using UnityEngine.SceneManagement;

namespace CasualtiesCoop.Runtime;

internal sealed class CoopRuntime : IDisposable
{
    private static CoopRuntime? current;
    private readonly ManualLogSource log;
    private readonly IMonotonicClock clock = new UnityMonotonicClock();
    private readonly SteamLobbyStartup steam;
    private readonly MainMenuIntegration menu;
    private SandboxSessionController? sandbox;

    internal static WorldSession? ActiveWorld => current?.sandbox?.World is { SceneReady: true } world ? world : null;
    internal static bool MenuOpen => current?.menu.IsOpen == true;
    internal static bool IsTestMode { get; } = HasArgument("--coop-test") || HasArgument("--coop-mp");
    private static bool HasArgument(string value) => Array.IndexOf(Environment.GetCommandLineArgs(), value) >= 0;
    internal static bool SuppressVanillaSave => IsTestMode || current?.sandbox?.SuppressVanillaSave == true;

    internal SteamLobby? Lobby => steam.Lobby;
    internal string SteamStatus => steam.Status;
    internal string? PreparationStatus => sandbox?.PreparationStatus;
    internal bool CanStartSharedSandbox => sandbox?.CanStart == true;

    internal CoopRuntime(ManualLogSource log)
    {
        this.log = log;
        steam = new SteamLobbyStartup(log, clock);
        menu = new MainMenuIntegration(this, log);
        current = this;
    }

    internal void Tick()
    {
        menu.Tick();
        if (sandbox is null && PreRunScript.instance && steam.TryOpen() is { } lobby)
        {
            sandbox = new SandboxSessionController(lobby, log, () => menu.IsAvailable, clock);
            lobby.TryJoinLaunchInvitation();
        }
        Lobby?.TickReturnInvitations();
        if (Lobby is { InRoom: true } room)
        {
            room.Link.Tick();
        }
        sandbox?.Tick();
        CoopDiagnostics.Tick();
    }

    internal void TickPresentation() => sandbox?.World?.TickPresentation();

    internal void StartFromLobby(PreRunScript owner) => sandbox?.Start(owner);

    internal void SceneLoaded(Scene scene, LoadSceneMode mode)
    {
        sandbox?.SceneLoaded(scene.name);
        menu.Attach();
    }

    public void Dispose()
    {
        sandbox?.Dispose();
        steam.Dispose();
        current = null;
    }
}
