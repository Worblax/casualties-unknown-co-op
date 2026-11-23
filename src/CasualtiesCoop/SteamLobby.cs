using System;
using System.Globalization;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop;

[Flags]
internal enum RoomOptions
{
    None = 0,
    PvP = 1,
    Respawn = 2,
    KeepItems = 4,
    KeepCheckpoint = 8,
    Defaults = Respawn | KeepItems | KeepCheckpoint,
    All = PvP | Defaults
}

internal sealed class SteamLobby : IDisposable
{
    internal const int Capacity = 4;
    private const string MemberKey = "cu_member";
    private const string ProtocolKey = "cu_protocol";
    private const string ProtocolVersion = "4";
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
    internal string Difficulty => TryGetSettings(out string difficulty, out _, out _) ? difficulty : "normal";
    internal RoomOptions Options => TryGetSettings(out _, out RoomOptions options, out _) ? options : RoomOptions.Defaults;
    internal int MemberCount => InRoom ? SteamMatchmaking.GetNumLobbyMembers(Current) : 0;
    internal bool CanStartSolo => IsHost && MemberCount == 1 && IsReady(LocalUser) && IsCharacterUnlocked(selectedCharacter);

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

    internal bool IsReady(CSteamID player) => TryGetMemberSelection(player, out _, out bool ready) && ready;

    internal bool TryGetMemberSelection(CSteamID player, out int character, out bool ready)
    {
        character = 0;
        ready = false;
        if (!InRoom)
        {
            return false;
        }

        string selection = SteamMatchmaking.GetLobbyMemberData(Current, player, MemberKey);
        int separator = selection.IndexOf(':');
        if (separator < 0 ||
            !int.TryParse(selection.Substring(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out character) ||
            character >= CharacterCount)
        {
            return false;
        }

        ready = TryGetSettings(out _, out _, out string revision) &&
            string.Equals(selection.Substring(separator + 1), revision, StringComparison.Ordinal);
        return true;
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
            PublishSelection();
        }
    }

    internal void ChangeDifficulty(string difficulty)
    {
        if (IsHost && difficulty != Difficulty && RunSettings.presets.Exists(preset => preset.presetName == difficulty))
        {
            PublishSettings(difficulty, Options);
        }
    }

    internal void ChangeOption(RoomOptions option, bool enabled)
    {
        if (IsHost && TryGetSettings(out string difficulty, out RoomOptions options, out _))
        {
            RoomOptions updated = enabled ? options | option : options & ~option;
            if (updated != options)
            {
                PublishSettings(difficulty, updated);
            }
        }
    }

    internal void ToggleReady()
    {
        if (IsCharacterUnlocked(selectedCharacter) && TryGetSettings(out _, out _, out string revision))
        {
            PublishSelection(IsReady(LocalUser) ? "" : revision);
        }
    }

    internal void Leave()
    {
        if (InRoom)
        {
            SteamMatchmaking.LeaveLobby(Current);
            Current = CSteamID.Nil;
            Status = null;
        }
    }

    internal void StartSoloPreview(PreRunScript menu)
    {
        if (!CanStartSolo)
        {
            return;
        }

        if (PlayerPrefs.GetInt("didbasiccourse") == 0)
        {
            menu.StartRun(); // Use the game's tutorial prompt and leave the room open.
            return;
        }

        menu.ApplyPreset(Difficulty);
        menu.SelectCharacter(selectedCharacter);
        CoopPlugin.BeginSoloPreview();
        menu.StartRun();
        Leave();
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
            !PublishSettings("normal", RoomOptions.Defaults) ||
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
        PublishSelection();
        Status = null;
    }

    private void PublishSelection(string readyRevision = "")
    {
        string selection = selectedCharacter.ToString(CultureInfo.InvariantCulture) + ":" + readyRevision;
        SteamMatchmaking.SetLobbyMemberData(Current, MemberKey, selection);
    }

    private bool PublishSettings(string difficulty, RoomOptions options)
    {
        string settings = difficulty + ":" + ((int)options).ToString(CultureInfo.InvariantCulture) +
            ":" + Guid.NewGuid().ToString("N");
        bool published = SteamMatchmaking.SetLobbyData(Current, SettingsKey, settings);
        Status = published ? null : "Could not update room settings.";
        return published;
    }

    private bool TryGetSettings(out string difficulty, out RoomOptions options, out string revision)
    {
        difficulty = "";
        options = RoomOptions.Defaults;
        revision = "";
        if (!InRoom)
        {
            return false;
        }

        string[] fields = SteamMatchmaking.GetLobbyData(Current, SettingsKey).Split(':');
        if (fields.Length != 3 || fields[0].Length == 0 ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int flags) ||
            (flags & ~(int)RoomOptions.All) != 0 || !Guid.TryParseExact(fields[2], "N", out _))
        {
            return false;
        }

        difficulty = fields[0];
        options = (RoomOptions)flags;
        revision = fields[2];
        return true;
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
            PublishSettings(Difficulty, Options);
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
