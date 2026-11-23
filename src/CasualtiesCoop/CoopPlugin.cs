using System;
using System.Reflection;
using BepInEx;
using CasualtiesCoop.Game;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using CasualtiesCoop.UI;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CasualtiesCoop;

[BepInPlugin(PluginId, "Casualties Co-op", PluginVersion)]
internal sealed class CoopPlugin : BaseUnityPlugin
{
    internal const string PluginId = "casualtiesunknown.coop";
    internal const string PluginVersion = "0.1.12";

    private static readonly FieldInfo? SteamInitialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private static CoopPlugin? instance;
    private Harmony? harmony;
    private SteamLobby? lobby;
    private RoomLaunchPreparation? launchPreparation;
    private SandboxScene? sandboxScene;
    private SandboxPresence? sandboxPresence;
    private bool sandboxRejected;
    private CoopMenuView? view;
    private bool lobbyInitializationFailed;
    private bool previousRunInBackground;
    private bool soloPreview;
    private float nextMenuProbe;

    internal static SteamLobby? Lobby => instance?.lobby;
    internal static string? PreparationStatus => instance?.launchPreparation?.Status;
    internal static bool CanStartSharedSandbox => instance?.launchPreparation?.CanStartSandbox == true;
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
        if (lobby is { InRoom: true } room)
        {
            room.Link.Tick();
            if (sandboxRejected)
            {
                sandboxRejected = false;
                room.EndFailedLaunch();
            }

            if (sandboxScene is null)
            {
                launchPreparation?.Tick();
            }
        }

        if (sandboxPresence is not null && lobby is not { InRoom: true } && PlayerCamera.main)
        {
            PlayerCamera.main.ToMainMenu();
            return;
        }

        if (sandboxScene is not null)
        {
            bool wasLoaded = sandboxScene.CourseLoaded;
            sandboxScene.Tick();
            if (sandboxScene.CourseLoaded)
            {
                if (!wasLoaded)
                {
                    Logger.LogInfo("Sandbox course loaded. Local console is available.");
                }

                sandboxPresence?.Tick();
            }
        }
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
            if (sandboxPresence is not null)
            {
                lobby?.Leave();
            }

            ClearSandbox();
        }
        else if (scene.name == "SampleScene" && sandboxScene is null)
        {
            lobby?.Leave();
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

    private static bool AllowSave() => instance is not { soloPreview: true } and not { sandboxScene: not null };

    internal static void StartFromLobby(PreRunScript menu)
    {
        if (instance is not { lobby: { IsHost: true } room, sandboxScene: null } plugin || !SandboxScene.CanEnter(menu))
        {
            return;
        }

        if (room.CanStartSolo)
        {
            if ((room.Options & RoomOptions.Sandbox) != 0)
            {
                if (plugin.EnterSandbox(menu, room.SelectedCharacter, null))
                {
                    room.Leave();
                }
            }
            else
            {
                room.StartSoloPreview(menu);
            }
        }
        else if (plugin.launchPreparation?.StartSandbox() is { } plan)
        {
            if (!plugin.EnterSandbox(menu, room.SelectedCharacter, plan))
            {
                room.EndFailedLaunch();
            }
        }
    }

    private bool OnRoomFrame(CSteamID sender, byte[] message)
    {
        switch ((RoomFrame)message[0])
        {
            case RoomFrame.StartSandbox:
                Guid planId = RoomWire.ReadPlanReference(RoomFrame.StartSandbox, message);
                if (lobby is not { IsHost: false } room || sender != room.Owner || planId == Guid.Empty)
                {
                    return false;
                }

                PreRunScript menu = PreRunScript.instance;
                RoomLaunchPlan? plan = launchPreparation?.AcceptSandbox(sender, planId);
                if (sandboxScene is not null)
                {
                    return true;
                }

                if (plan is not null && SandboxScene.CanEnter(menu) &&
                    plan.TryGetPlayer(room.LocalUser.m_SteamID, out LaunchPlayer player) &&
                    EnterSandbox(menu, player.Character, plan))
                {
                    room.BeginSandboxLaunch();
                }
                else
                {
                    room.Link.Send(sender, RoomWire.PlanReference(RoomFrame.SandboxRejected, planId));
                }

                return true;

            case RoomFrame.SandboxRejected:
                Guid rejected = RoomWire.ReadPlanReference(RoomFrame.SandboxRejected, message);
                if (lobby is not { IsHost: true } || rejected == Guid.Empty)
                {
                    return false;
                }

                sandboxRejected |= sandboxPresence?.IsParticipant(sender, rejected) == true;
                return true;

            case RoomFrame.SandboxPosition:
                return sandboxPresence?.Receive(sender, message) ?? true;

            default:
                return launchPreparation?.Receive(sender, message) == true;
        }
    }

    private bool EnterSandbox(PreRunScript menu, int character, RoomLaunchPlan? plan)
    {
        try
        {
            sandboxScene = SandboxScene.Enter(menu, character, plan?.Difficulty ?? lobby!.Difficulty);
            if (plan is not null)
            {
                sandboxPresence = new SandboxPresence(lobby!, plan, sandboxScene.Font);
            }

            Logger.LogInfo(plan is null ? "Opening sandbox preview." : "Opening shared sandbox.");
            return true;
        }
        catch (Exception exception)
        {
            ClearSandbox();
            Logger.LogError($"Could not open the sandbox: {exception}");
            return false;
        }
    }

    private void ClearSandbox()
    {
        sandboxPresence?.Dispose();
        sandboxPresence = null;
        sandboxScene?.Dispose();
        sandboxScene = null;
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

            lobby = new SteamLobby(Logger);
            launchPreparation = new RoomLaunchPreparation(lobby, Logger);
            lobby.Link.HandleFrame = OnRoomFrame;
            Logger.LogInfo("Steam lobby integration ready.");
            lobby.TryJoinLaunchInvitation();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Steam lobby initialization failed: {ex}");
            SetSteamStatus("Steam lobby error. See BepInEx log.");
            lobby?.Dispose();
            lobby = null;
            launchPreparation = null;
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
        ClearSandbox();
        lobby?.Dispose();
        lobby = null;
        launchPreparation = null;
        instance = null;
    }
}
