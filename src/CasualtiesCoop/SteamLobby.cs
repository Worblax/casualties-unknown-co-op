using System;
using Steamworks;

namespace CasualtiesCoop;

internal sealed class SteamLobby : IDisposable
{
    internal const int Capacity = 4;
    internal const string ReadyKey = "cu_ready";

    private const string ProtocolKey = "cu_protocol";
    private const string ProtocolVersion = "1";
    private readonly CallResult<LobbyCreated_t> lobbyCreated;
    private readonly CallResult<LobbyEnter_t> lobbyEntered;
    private readonly Callback<GameLobbyJoinRequested_t> invitation;
    private CSteamID pendingLobby;
    private bool busy;

    internal CSteamID Current { get; private set; }
    internal CSteamID LocalUser { get; } = SteamUser.GetSteamID();
    internal bool InRoom => Current != CSteamID.Nil;
    internal bool IsBusy => busy;
    internal bool IsHost => InRoom && SteamMatchmaking.GetLobbyOwner(Current) == LocalUser;
    internal string? Status { get; private set; }

    internal SteamLobby()
    {
        lobbyCreated = CallResult<LobbyCreated_t>.Create(OnCreated);
        lobbyEntered = CallResult<LobbyEnter_t>.Create(OnEntered);
        invitation = Callback<GameLobbyJoinRequested_t>.Create(request => Join(request.m_steamIDLobby));
    }

    internal void Create()
    {
        if (busy || InRoom)
        {
            return;
        }

        busy = true;
        Status = "Creating room…";
        lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, Capacity));
    }

    internal void TryJoinLaunchInvitation()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index] == "+connect_lobby" && ulong.TryParse(arguments[index + 1], out ulong id))
            {
                Join(new CSteamID(id));
                return;
            }
        }
    }

    internal void Invite()
    {
        if (IsHost)
        {
            SteamFriends.ActivateGameOverlayInviteDialog(Current);
        }
    }

    internal bool IsReady(CSteamID player) =>
        InRoom && SteamMatchmaking.GetLobbyMemberData(Current, player, ReadyKey) == "1";

    internal void ToggleReady()
    {
        if (InRoom)
        {
            SteamMatchmaking.SetLobbyMemberData(Current, ReadyKey, IsReady(LocalUser) ? "0" : "1");
        }
    }

    internal void Leave()
    {
        if (InRoom)
        {
            SteamMatchmaking.LeaveLobby(Current);
            Current = CSteamID.Nil;
        }
    }

    private void Join(CSteamID id)
    {
        if (busy || id == CSteamID.Nil || id == Current)
        {
            return;
        }

        if (InRoom)
        {
            Status = "Leave your current room before joining another.";
            return;
        }

        busy = true;
        pendingLobby = id;
        Status = "Joining room…";
        lobbyEntered.Set(SteamMatchmaking.JoinLobby(id));
    }

    private void OnCreated(LobbyCreated_t result, bool ioFailure)
    {
        busy = false;
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
            Status = $"Could not create room ({result.m_eResult}).";
            return;
        }

        Current = new CSteamID(result.m_ulSteamIDLobby);
        if (!SteamMatchmaking.SetLobbyData(Current, ProtocolKey, ProtocolVersion))
        {
            Leave();
            Status = "Could not publish the room's mod protocol.";
            return;
        }

        InitializeMembership();
    }

    private void OnEntered(LobbyEnter_t result, bool ioFailure)
    {
        busy = false;
        CSteamID expected = pendingLobby;
        pendingLobby = CSteamID.Nil;
        if (ioFailure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            Status = $"Could not join room ({result.m_EChatRoomEnterResponse}).";
            return;
        }

        Current = new CSteamID(result.m_ulSteamIDLobby);
        if (Current != expected || SteamMatchmaking.GetLobbyData(Current, ProtocolKey) != ProtocolVersion)
        {
            Leave();
            Status = "This room uses a different mod protocol.";
            return;
        }

        InitializeMembership();
    }

    private void InitializeMembership()
    {
        SteamMatchmaking.SetLobbyMemberData(Current, ReadyKey, "0");
        Status = null;
    }

    public void Dispose()
    {
        Leave();
        invitation.Dispose();
        lobbyCreated.Dispose();
        lobbyEntered.Dispose();
    }
}
