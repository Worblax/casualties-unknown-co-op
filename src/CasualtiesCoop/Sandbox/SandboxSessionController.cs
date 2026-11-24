using System;
using BepInEx.Logging;
using CasualtiesCoop.Core;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using CasualtiesCoop.UI;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Sandbox;

internal sealed class SandboxSessionController : IDisposable
{
    private readonly SteamLobby lobby;
    private readonly SteamSessionTransport transport;
    private readonly ManualLogSource log;
    private readonly IMonotonicClock clock;
    private readonly Func<bool> menuAvailable;
    private readonly RoomLaunchPreparation launch;
    private readonly SessionReturnRequest returnRequest;
    private SandboxScene? scene;
    private ReturnInvitationPanel? invitations;
    private bool launchRejected;
    private bool soloPreview;

    internal WorldSession? World { get; private set; }
    internal bool SuppressVanillaSave => soloPreview || scene is not null;
    internal bool CanStart => launch.CanStartSandbox;
    internal string? PreparationStatus => launch.Status;

    internal SandboxSessionController(SteamLobby lobby, ManualLogSource log, Func<bool> menuAvailable, IMonotonicClock clock)
    {
        this.lobby = lobby;
        this.log = log;
        this.clock = clock;
        this.menuAvailable = menuAvailable;
        launch = new RoomLaunchPreparation(lobby, log, clock);
        returnRequest = new SessionReturnRequest(lobby, clock);
        transport = new SteamSessionTransport(lobby.Link);
        lobby.Link.HandleFrame = Receive;
    }

    internal void Start(PreRunScript menu)
    {
        if (!lobby.IsHost || scene is not null || !SandboxScene.CanEnter(menu))
        {
            return;
        }

        if (lobby.CanStartSolo)
        {
            if ((lobby.Options & RoomOptions.Sandbox) != 0)
            {
                if (Enter(menu, lobby.SelectedCharacter, null))
                {
                    lobby.Leave();
                }
            }
            else
            {
                StartSoloPreview(menu);
            }
        }
        else if (launch.StartSandbox() is { } plan && !Enter(menu, lobby.SelectedCharacter, plan))
        {
            lobby.EndFailedLaunch();
        }
    }

    private void StartSoloPreview(PreRunScript menu)
    {
        if (PlayerPrefs.GetInt("didbasiccourse") == 0)
        {
            menu.StartRun(); // The native tutorial prompt keeps the room open.
            return;
        }

        menu.ApplyPreset(lobby.Difficulty);
        menu.SelectCharacter(lobby.SelectedCharacter);
        soloPreview = true;
        log.LogInfo("Starting a solo co-op preview with checkpoint writes disabled.");
        menu.StartRun();
        lobby.Leave();
    }

    internal void Tick()
    {
        if (launchRejected)
        {
            launchRejected = false;
            lobby.EndFailedLaunch();
        }

        if (!lobby.InRoom)
        {
            returnRequest.Reset();
            if (World is not null && PlayerCamera.main)
            {
                PlayerCamera.main.ToMainMenu();
                return;
            }
        }

        if (scene is null)
        {
            if (lobby.InRoom && lobby.ReturningTo == Guid.Empty)
            {
                launch.Tick();
            }
            else if (lobby.InRoom && menuAvailable() && SandboxScene.CanEnter(PreRunScript.instance))
            {
                returnRequest.Tick();
            }
            return;
        }

        bool wasLoaded = scene.CourseLoaded;
        scene.Tick();
        if (!scene.CourseLoaded)
        {
            return;
        }

        if (!wasLoaded)
        {
            log.LogInfo(World is null ? "Sandbox course loaded. Local console is available."
                : "Sandbox course loaded. Shared character, audio, item, fluid and light replication active.");
        }
        World?.Tick();
        if (!invitations && World is { SceneReady: true } world && lobby.IsHost && PauseHandler.main)
        {
            invitations = ReturnInvitationPanel.Attach(PauseHandler.main, lobby, world.Plan, scene.Font);
        }
    }

    internal void SceneLoaded(string name)
    {
        if (name == "PreGen")
        {
            soloPreview = false;
            if (World is not null)
            {
                lobby.Leave();
            }
            ClearScene();
        }
        else if (name == "SampleScene" && scene is null)
        {
            lobby.Leave();
        }
    }

    private bool Receive(CSteamID sender, byte[] message) => ((MessageKind)message[0]) switch
    {
        MessageKind.PrepareRun or MessageKind.RunPrepared => launch.Receive(sender, message),
        MessageKind.StartSandbox => ReceiveStart(sender, message),
        MessageKind.SandboxRejected => ReceiveRejection(sender, message),
        MessageKind.SessionRejoinState when World is null => ReceiveReturn(sender, message),
        _ => World is not null ? transport.DispatchVerified(sender, message) : GameplayWire.IsWorldMessage((MessageKind)message[0])
    };

    private bool ReceiveStart(CSteamID sender, byte[] message)
    {
        Guid id = RoomWire.ReadPlanReference(MessageKind.StartSandbox, message);
        if (lobby.IsHost || sender != lobby.Owner || id == Guid.Empty)
        {
            return false;
        }
        if (scene is not null)
        {
            return true;
        }

        PreRunScript menu = PreRunScript.instance;
        if (launch.AcceptSandbox(sender, id) is { } plan && SandboxScene.CanEnter(menu) &&
            plan.TryGetPlayer(lobby.LocalUser.m_SteamID, out LaunchPlayer player) && Enter(menu, player.Character, plan))
        {
            lobby.BeginSandboxLaunch(plan);
        }
        else
        {
            lobby.Link.Send(sender, RoomWire.PlanReference(MessageKind.SandboxRejected, id));
        }
        return true;
    }

    private bool ReceiveRejection(CSteamID sender, byte[] message)
    {
        Guid id = RoomWire.ReadPlanReference(MessageKind.SandboxRejected, message);
        if (!lobby.IsHost || id == Guid.Empty)
        {
            return false;
        }
        launchRejected |= World?.IsParticipant(sender.m_SteamID, id) == true;
        return true;
    }

    private bool ReceiveReturn(CSteamID sender, byte[] message)
    {
        if (lobby.IsHost || sender != lobby.Owner || lobby.ReturningTo == Guid.Empty)
        {
            return false;
        }

        PreRunScript menu = PreRunScript.instance;
        if (returnRequest.ReadState(message, out SessionRejoinState state) && (state.Plan.Options & RoomOptions.Sandbox) != 0 &&
            state.Plan.TryGetPlayer(lobby.Owner.m_SteamID, out _) && state.Plan.TryGetPlayer(lobby.LocalUser.m_SteamID, out LaunchPlayer player) &&
            player.Character < SteamLobby.CharacterCount && RunSettings.presets.Exists(preset => preset.presetName == state.Plan.Difficulty) &&
            menuAvailable() && SandboxScene.CanEnter(menu) && Enter(menu, player.Character, state.Plan))
        {
            lobby.BeginSandboxLaunch(state.Plan);
            World!.RestoreFrom(state);
            log.LogInfo("Returning to the existing sandbox and restoring the reserved player seat.");
        }
        return true;
    }

    private bool Enter(PreRunScript menu, int character, RoomLaunchPlan? plan)
    {
        SessionPeers? peers = null;
        try
        {
            scene = SandboxScene.Enter(menu, character, plan?.Difficulty ?? lobby.Difficulty);
            if (plan is not null)
            {
                peers = new SessionPeers(transport, plan, lobby.LocalUser.m_SteamID, lobby.Owner.m_SteamID);
                World = new WorldSession(peers, plan, scene.Font, log, clock);
            }

            log.LogInfo(plan is null ? "Opening sandbox preview." : "Opening shared sandbox.");
            return true;
        }
        catch (Exception exception)
        {
            if (World is null)
            {
                peers?.Dispose();
            }
            ClearScene();
            log.LogError($"Could not open the sandbox: {exception}");
            return false;
        }
    }

    private void ClearScene()
    {
        if (invitations)
        {
            UnityEngine.Object.Destroy(invitations!.gameObject);
        }
        invitations = null;
        returnRequest.Reset();
        WorldSession? previousWorld = World;
        World = null;
        previousWorld?.Dispose();
        scene?.Dispose();
        scene = null;
    }

    public void Dispose()
    {
        lobby.Link.HandleFrame = null;
        launch.Dispose();
        ClearScene();
        transport.Dispose();
    }
}
