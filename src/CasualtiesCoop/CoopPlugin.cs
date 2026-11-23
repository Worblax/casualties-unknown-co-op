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
    internal const string PluginVersion = "0.1.15";

    private static readonly FieldInfo? SteamInitialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private static CoopPlugin? instance;
    private Harmony? harmony;
    private SteamLobby? lobby;
    private RoomLaunchPreparation? launchPreparation;
    private SandboxScene? sandboxScene;
    private SandboxPresence? sandboxPresence;
    private bool sandboxRejected;
    private Guid coldReturnSession;
    private Guid coldReturnAttempt;
    private float nextColdReturn;
    private SandboxReturnPanel? returnPanel;
    private CoopMenuView? view;
    private bool lobbyInitializationFailed;
    private bool previousRunInBackground;
    private bool soloPreview;
    private float nextMenuProbe;

    internal static SteamLobby? Lobby => instance?.lobby;
    internal static SandboxPresence? ActiveSandbox => instance?.sandboxPresence is { Ready: true } active ? active : null;
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
        SandboxAudioHooks.Install(harmony);
        SandboxItemHooks.Install(harmony);
        SandboxFluidHooks.Install(harmony);
        SandboxRecoveryHooks.Install(harmony);
        SceneManager.sceneLoaded += OnSceneLoaded;
        AttachMenu(PreRunScript.instance);
        Logger.LogInfo($"Casualties Co-op {PluginVersion} initialized.");
    }

    private static HarmonyMethod OwnPatch(string method) => new(AccessTools.Method(typeof(CoopPlugin), method));

    private void Update()
    {
        lobby?.TickReturnInvitations();
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
                if (room.ReturningTo == Guid.Empty)
                {
                    launchPreparation?.Tick();
                }
                else
                {
                    TickColdReturn(room);
                }
            }
        }
        else
        {
            coldReturnSession = Guid.Empty;
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
                    if (sandboxPresence is not null)
                    {
                        Logger.LogInfo("Shared sandbox movement, audio, items, fluids and lighting active.");
                    }
                }

                sandboxPresence?.Tick();
                if (!returnPanel && sandboxPresence is { Ready: true } shared && lobby is { IsHost: true } host && PauseHandler.main)
                {
                    returnPanel = SandboxReturnPanel.Attach(PauseHandler.main, host, shared.Plan, sandboxScene.Font);
                }
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
                    room.BeginSandboxLaunch(plan);
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

            case RoomFrame.SandboxResumeState:
                return sandboxPresence?.ReceiveRecovery(sender, message) ?? AcceptColdReturn(sender, message);

            case RoomFrame.SandboxResumeRequest:
            case RoomFrame.SandboxResumeApplied:
            case RoomFrame.SandboxResumeCommitted:
                return sandboxPresence?.ReceiveRecovery(sender, message) ?? false;

            case RoomFrame.SandboxPosition:
            case RoomFrame.SandboxSound:
            case RoomFrame.ItemRequest:
            case RoomFrame.ItemState:
            case RoomFrame.ItemResult:
            case RoomFrame.ItemMotion:
            case RoomFrame.ItemSyncRequest:
            case RoomFrame.ItemSyncDone:
            case RoomFrame.FluidChunk:
            case RoomFrame.FluidPush:
            case RoomFrame.FluidDrinkRequest:
            case RoomFrame.FluidDrinkResult:
            case RoomFrame.SandboxLights:
                return sandboxPresence?.Receive(sender, message) ?? true;

            default:
                return launchPreparation?.Receive(sender, message) == true;
        }
    }

    private void TickColdReturn(SteamLobby room)
    {
        if (coldReturnSession != room.ReturningTo)
        {
            coldReturnSession = room.ReturningTo;
            coldReturnAttempt = Guid.NewGuid();
            nextColdReturn = 0;
        }

        float now = Time.unscaledTime;
        if (now >= nextColdReturn && view && view!.MenuAvailable &&
            room.Link.GetState(room.Owner) == RoomLinkState.Verified && SandboxScene.CanEnter(PreRunScript.instance))
        {
            nextColdReturn = now + 1f;
            room.Link.Send(room.Owner, SandboxResumeWire.WriteReference(RoomFrame.SandboxResumeRequest, coldReturnSession, coldReturnAttempt));
        }
    }

    private bool AcceptColdReturn(CSteamID sender, byte[] message)
    {
        if (lobby is not { IsHost: false } room || sender != room.Owner || room.ReturningTo == Guid.Empty)
        {
            return false;
        }

        PreRunScript menu = PreRunScript.instance;
        if (SandboxResumeWire.TryReadState(message, room.ReturningTo, out SandboxResumeState state) &&
            state.Attempt == coldReturnAttempt && (state.Plan.Options & RoomOptions.Sandbox) != 0 &&
            state.Plan.TryGetPlayer(room.Owner.m_SteamID, out _) && state.Plan.TryGetPlayer(room.LocalUser.m_SteamID, out LaunchPlayer player) &&
            player.Character < SteamLobby.CharacterCount && RunSettings.presets.Exists(preset => preset.presetName == state.Plan.Difficulty) &&
            view && view!.MenuAvailable && SandboxScene.CanEnter(menu) && EnterSandbox(menu, player.Character, state.Plan))
        {
            room.BeginSandboxLaunch(state.Plan);
            sandboxPresence!.RestoreFrom(state);
            Logger.LogInfo("Returning to the existing shared sandbox and restoring the reserved player seat.");
        }
        return true;
    }

    private bool EnterSandbox(PreRunScript menu, int character, RoomLaunchPlan? plan)
    {
        try
        {
            sandboxScene = SandboxScene.Enter(menu, character, plan?.Difficulty ?? lobby!.Difficulty);
            if (plan is not null)
            {
                sandboxPresence = new SandboxPresence(lobby!, plan, sandboxScene.Font, Logger);
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
        if (returnPanel)
        {
            Destroy(returnPanel!.gameObject);
        }
        returnPanel = null;
        coldReturnSession = Guid.Empty;
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
