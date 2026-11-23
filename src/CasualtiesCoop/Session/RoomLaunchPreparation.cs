using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Session;

internal sealed class RoomLaunchPreparation : IDisposable
{
    private const float PollInterval = 0.25f;
    private const float ConfirmationTimeout = 12f;
    private readonly SteamLobby lobby;
    private readonly SteamRoomLink link;
    private readonly ManualLogSource log;
    private readonly HashSet<CSteamID> confirmed = new();
    private RoomLaunchPlan? plan;
    private bool guestConfirmed;
    private float nextPoll;
    private float expiresAt;

    internal RoomLaunchPreparation(SteamLobby lobby, ManualLogSource log)
    {
        this.lobby = lobby;
        this.log = log;
        link = lobby.Link;
        lobby.LaunchInputsChanged += Reset;
    }

    internal string? Status
    {
        get
        {
            if (plan is null)
            {
                return null;
            }

            if (!lobby.IsHost)
            {
                return guestConfirmed ? "Run settings sent to the host." : "Checking the host's run settings…";
            }

            if (!HasConfirmations)
            {
                return "Confirming run settings with guests…";
            }

            return (plan.Options & RoomOptions.Sandbox) != 0 ? "Team ready to enter the sandbox."
                : "Team settings confirmed. Shared world loading is in development.";
        }
    }

    internal bool CanStartSandbox => lobby.IsHost && plan is not null && (plan.Options & RoomOptions.Sandbox) != 0 &&
        HasConfirmations && MatchesPlan(plan, allPlayersReady: true);

    private bool HasConfirmations => plan is not null && confirmed.Count == plan.Players.Count - 1;

    internal RoomLaunchPlan? StartSandbox()
    {
        if (!CanStartSandbox || plan is not { } launch || !lobby.ReserveSandbox(launch))
        {
            return null;
        }

        byte[] message = RoomWire.PlanReference(MessageKind.StartSandbox, launch.Id);
        foreach (LaunchPlayer player in launch.Players)
        {
            CSteamID id = new(player.SteamId);
            if (id != lobby.LocalUser && !link.Send(id, message))
            {
                lobby.EndFailedLaunch();
                return null;
            }
        }

        return launch;
    }

    internal RoomLaunchPlan? AcceptSandbox(CSteamID sender, Guid id) =>
        !lobby.IsHost && sender == lobby.Owner && plan is not null && id == plan.Id && guestConfirmed &&
        (plan.Options & RoomOptions.Sandbox) != 0 && MatchesPlan(plan, allPlayersReady: false) ? plan : null;

    internal void Tick()
    {
        float now = Time.unscaledTime;
        if (now < nextPoll)
        {
            return;
        }

        nextPoll = now + PollInterval;
        if (lobby.IsHost)
        {
            TickHost(now);
        }
        else if (plan is not null)
        {
            if (!MatchesPlan(plan, allPlayersReady: false))
            {
                // Steam lobby data and P2P messages can arrive in either order.
                if (guestConfirmed || now >= expiresAt)
                {
                    Reset();
                }
            }
            else if (!guestConfirmed)
            {
                guestConfirmed = link.Send(lobby.Owner, RoomWire.PlanReference(MessageKind.RunPrepared, plan.Id));
                if (guestConfirmed)
                {
                    log.LogInfo("Verified settings and roster with the room host.");
                }
            }
        }
    }

    private void TickHost(float now)
    {
        if (plan is not null && MatchesPlan(plan, allPlayersReady: true) &&
            (HasConfirmations || now < expiresAt))
        {
            return;
        }

        Reset();
        RoomLaunchPlan? candidate = BuildPlan();
        if (candidate is null || RoomWire.WritePlan(candidate) is not { } message)
        {
            return;
        }

        plan = candidate;
        expiresAt = now + ConfirmationTimeout;
        foreach (LaunchPlayer player in plan.Players)
        {
            CSteamID id = new(player.SteamId);
            if (id != lobby.LocalUser)
            {
                link.Send(id, message);
            }
        }
    }

    internal bool Receive(CSteamID sender, byte[] bytes)
    {
        if (lobby.IsHost)
        {
            Guid acknowledged = RoomWire.ReadPlanReference(MessageKind.RunPrepared, bytes);
            if (acknowledged == Guid.Empty)
            {
                return false;
            }

            if (plan?.Id == acknowledged && plan.TryGetPlayer(sender.m_SteamID, out _) && confirmed.Add(sender) && HasConfirmations)
            {
                log.LogInfo("All guests confirmed the run settings.");
            }

            return true;
        }

        RoomLaunchPlan? received = sender == lobby.Owner ? RoomWire.ReadPlan(bytes) : null;
        if (received is null)
        {
            return false;
        }

        plan = received;
        guestConfirmed = false;
        expiresAt = Time.unscaledTime + ConfirmationTimeout;
        return true;
    }

    private RoomLaunchPlan? BuildPlan()
    {
        int count = lobby.MemberCount;
        if (count is < 2 or > RoomRules.Capacity || !lobby.CanReady ||
            !lobby.TryGetSettings(out string difficulty, out RoomOptions options, out string revision))
        {
            return null;
        }

        LaunchPlayer[] players = new LaunchPlayer[count];
        for (int index = 0; index < count; index++)
        {
            CSteamID id = lobby.MemberAt(index);
            if (!lobby.TryGetMemberSelection(id, out int character, out bool ready) || !ready)
            {
                return null;
            }

            players[index] = new LaunchPlayer(id.m_SteamID, (byte)character);
        }

        return new RoomLaunchPlan(Guid.NewGuid(), Guid.ParseExact(revision, "N"), difficulty, options, players);
    }

    private bool MatchesPlan(RoomLaunchPlan candidate, bool allPlayersReady)
    {
        if (!lobby.CanReady || !lobby.IsReady(lobby.LocalUser) || candidate.Players.Count != lobby.MemberCount ||
            !lobby.TryGetSettings(out string difficulty, out RoomOptions options, out string revision) ||
            difficulty != candidate.Difficulty || options != candidate.Options || Guid.ParseExact(revision, "N") != candidate.SettingsRevision ||
            !RunSettings.presets.Exists(preset => preset.presetName == difficulty))
        {
            return false;
        }

        foreach (LaunchPlayer player in candidate.Players)
        {
            CSteamID id = new(player.SteamId);
            if (!lobby.HasMember(id) || player.Character >= SteamLobby.CharacterCount ||
                (id == lobby.LocalUser && player.Character != lobby.SelectedCharacter) ||
                (allPlayersReady && (!lobby.TryGetMemberSelection(id, out int character, out bool ready) || !ready || character != player.Character)))
            {
                return false;
            }
        }

        return true;
    }

    private void Reset()
    {
        plan = null;
        confirmed.Clear();
        guestConfirmed = false;
    }

    public void Dispose() => lobby.LaunchInputsChanged -= Reset;
}
