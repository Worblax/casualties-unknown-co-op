using System;
using System.Globalization;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop;

internal sealed class SteamLobby : IDisposable
{
    internal const int Capacity = 4;
    private const string ReadyKey = "cu_ready";
    private const string ProtocolKey = "cu_protocol";
    private const string ProtocolVersion = "2";
    private const string SettingsKey = "cu_settings";
    private const string CreatorKey = "cu_creator";
    private readonly CallResult<LobbyCreated_t> lobbyCreated;
    private readonly CallResult<LobbyEnter_t> lobbyEntered;
    private readonly Callback<GameLobbyJoinRequested_t> invitation;
    private readonly Callback<LobbyChatUpdate_t> membershipChanged;
    private CSteamID pendingLobby;
    private bool busy;
    private int selectedCharacter;

    internal CSteamID Current { get; private set; }
    internal CSteamID LocalUser { get; } = SteamUser.GetSteamID();
    internal bool InRoom => Current != CSteamID.Nil;
    internal bool IsBusy => busy;
    internal bool IsHost => InRoom && SteamMatchmaking.GetLobbyOwner(Current) == LocalUser;
    internal string? Status { get; private set; }
    internal int SelectedCharacter => selectedCharacter;
    internal string Difficulty => TryGetSettings(out string difficulty, out _) ? difficulty : "normal";

    internal SteamLobby()
    {
        lobbyCreated = CallResult<LobbyCreated_t>.Create(OnCreated);
        lobbyEntered = CallResult<LobbyEnter_t>.Create(OnEntered);
        invitation = Callback<GameLobbyJoinRequested_t>.Create(request => Join(request.m_steamIDLobby));
        membershipChanged = Callback<LobbyChatUpdate_t>.Create(OnMembershipChanged);
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

    internal bool IsReady(CSteamID player) => TryGetReadyCharacter(player, out _);

    internal bool TryGetReadyCharacter(CSteamID player, out int character)
    {
        character = 0;
        if (!TryGetSettings(out _, out string revision))
        {
            return false;
        }

        string selection = SteamMatchmaking.GetLobbyMemberData(Current, player, ReadyKey);
        string prefix = revision + ":";
        return selection.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(selection.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out character) &&
            character >= 0 && character < CharacterCount;
    }

    internal static int CharacterCount => PreRunScript.instance ? PreRunScript.instance.charImages.Count : 0;

    internal static bool IsCharacterUnlocked(int character) =>
        character >= 0 && character < CharacterCount &&
        (character == 0 || PlayerPrefs.GetInt("wonchar" + (character - 1)) > 0);

    internal void SelectCharacter(int character)
    {
        if (InRoom && selectedCharacter != character && IsCharacterUnlocked(character))
        {
            selectedCharacter = character;
            SteamMatchmaking.SetLobbyMemberData(Current, ReadyKey, "0");
        }
    }

    internal void ChangeDifficulty(string difficulty)
    {
        if (IsHost && difficulty != Difficulty && RunSettings.presets.Exists(preset => preset.presetName == difficulty))
        {
            PublishSettings(difficulty);
        }
    }

    internal void ToggleReady()
    {
        if (IsCharacterUnlocked(selectedCharacter) && TryGetSettings(out _, out string revision))
        {
            string ready = revision + ":" + selectedCharacter.ToString(CultureInfo.InvariantCulture);
            SteamMatchmaking.SetLobbyMemberData(Current, ReadyKey, IsReady(LocalUser) ? "0" : ready);
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
        if (!SteamMatchmaking.SetLobbyData(Current, CreatorKey, LocalUser.m_SteamID.ToString(CultureInfo.InvariantCulture)) ||
            !PublishSettings("normal") ||
            !SteamMatchmaking.SetLobbyData(Current, ProtocolKey, ProtocolVersion))
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
        selectedCharacter = 0;
        SteamMatchmaking.SetLobbyMemberData(Current, ReadyKey, "0");
        Status = null;
    }

    private bool PublishSettings(string difficulty)
    {
        bool published = SteamMatchmaking.SetLobbyData(Current, SettingsKey, difficulty + ":" + Guid.NewGuid().ToString("N"));
        if (!published)
        {
            Status = "Could not update room settings.";
        }

        return published;
    }

    private bool TryGetSettings(out string difficulty, out string revision)
    {
        difficulty = "";
        revision = "";
        if (!InRoom)
        {
            return false;
        }

        string settings = SteamMatchmaking.GetLobbyData(Current, SettingsKey);
        int separator = settings.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        difficulty = settings.Substring(0, separator);
        revision = settings.Substring(separator + 1);
        return Guid.TryParseExact(revision, "N", out _);
    }

    private void OnMembershipChanged(LobbyChatUpdate_t update)
    {
        if (!InRoom || update.m_ulSteamIDLobby != Current.m_SteamID)
        {
            return;
        }

        CSteamID owner = SteamMatchmaking.GetLobbyOwner(Current);
        if (SteamMatchmaking.GetLobbyData(Current, CreatorKey) != owner.m_SteamID.ToString(CultureInfo.InvariantCulture))
        {
            Leave();
            Status = "The host left the room.";
        }
        else if (IsHost && update.m_ulSteamIDUserChanged != LocalUser.m_SteamID)
        {
            PublishSettings(Difficulty);
        }
    }

    public void Dispose()
    {
        Leave();
        membershipChanged.Dispose();
        invitation.Dispose();
        lobbyCreated.Dispose();
        lobbyEntered.Dispose();
    }
}
