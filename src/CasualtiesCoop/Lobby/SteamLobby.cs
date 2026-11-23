using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Lobby;

internal sealed class SteamLobby : IDisposable
{
    private const string MemberKey = "cu_member";
    private const string ProtocolKey = "cu_protocol";
    private const string ProtocolVersion = "8";
    private const string SettingsKey = "cu_settings";
    private const string CreatorKey = "cu_creator";
    private const string VerifiedMembersKey = "cu_verified";
    private const string SandboxSessionKey = "cu_sandbox";
    private readonly CallResult<LobbyCreated_t> lobbyCreated;
    private readonly CallResult<LobbyEnter_t> lobbyEntered;
    private readonly Callback<GameLobbyJoinRequested_t> invitation;
    private readonly Callback<LobbyChatUpdate_t> membershipChanged;
    private readonly RoomReturnRecord returnRecord;
    private readonly RoomReturnInvitations returnInvitations;
    private readonly Dictionary<CSteamID, float> invitedAt = new();
    private RoomLaunchPlan? runningSandbox;
    private CSteamID pendingLobby;
    private bool busy;
    private int selectedCharacter;

    internal event Action? LaunchInputsChanged;
    internal SteamRoomLink Link { get; }

    internal CSteamID Current { get; private set; }
    internal CSteamID Owner { get; private set; }
    internal CSteamID LocalUser { get; } = SteamUser.GetSteamID();
    internal bool InRoom => Current != CSteamID.Nil;
    internal bool IsBusy => busy;
    internal bool IsLaunching { get; private set; }
    internal Guid ReturningTo { get; private set; }
    internal bool IsHost => InRoom && Owner == LocalUser && SteamMatchmaking.GetLobbyOwner(Current) == LocalUser;
    internal string? Status { get; private set; }
    internal string? ConnectionStatus => Link.Status;
    internal string? ReturnStatus => returnInvitations.Status;
    internal bool HasReturnAddress => returnRecord.Address.HasValue;
    internal bool CanRequestReturn => HasReturnAddress && !InRoom && !busy && !returnInvitations.Waiting;
    internal int SelectedCharacter => selectedCharacter;
    internal string Difficulty => TryGetSettings(out string difficulty, out _, out _) ? difficulty : "normal";
    internal RoomOptions Options => TryGetSettings(out _, out RoomOptions options, out _) ? options : RoomOptions.Defaults;
    internal int MemberCount => InRoom ? SteamMatchmaking.GetNumLobbyMembers(Current) : 0;
    internal bool CanReady => InRoom && !IsLaunching && GetLinkState(LocalUser) == RoomLinkState.Verified &&
        IsCharacterUnlocked(selectedCharacter) && TryGetSettings(out _, out _, out _);
    internal bool CanStartSolo => IsHost && MemberCount == 1 && CanReady && IsReady(LocalUser);

    internal SteamLobby(ManualLogSource log)
    {
        lobbyCreated = CallResult<LobbyCreated_t>.Create(OnCreated);
        lobbyEntered = CallResult<LobbyEnter_t>.Create(OnEntered);
        invitation = Callback<GameLobbyJoinRequested_t>.Create(request => Join(request.m_steamIDLobby));
        membershipChanged = Callback<LobbyChatUpdate_t>.Create(OnMembershipChanged);
        returnRecord = new RoomReturnRecord(LocalUser, log);
        returnInvitations = new RoomReturnInvitations(CanInviteReturningPlayer, InviteReturningPlayer, log);
        Link = new SteamRoomLink(LocalUser, IsRoomPeer, typeof(WorldGeneration).Assembly.ManifestModule.ModuleVersionId,
            typeof(SteamLobby).Assembly.ManifestModule.ModuleVersionId, log);
        Link.PeerChanged += OnPeerChanged;
        Link.HostEnded += OnHostEnded;
    }

    internal CSteamID MemberAt(int index) => SteamMatchmaking.GetLobbyMemberByIndex(Current, index);

    private bool IsRoomPeer(CSteamID id) => HasMember(id) &&
        (runningSandbox is null || runningSandbox.TryGetPlayer(id.m_SteamID, out _));

    internal void TickReturnInvitations() => returnInvitations.Tick(runningSandbox is not null && IsHost);

    internal void RequestReturn()
    {
        if (CanRequestReturn && returnRecord.Address is { } address)
        {
            Status = null;
            returnInvitations.Request(address);
        }
    }

    private bool CanInviteReturningPlayer(CSteamID player) => IsHost && player != LocalUser &&
        runningSandbox is not null && runningSandbox.TryGetPlayer(player.m_SteamID, out _);

    internal bool CanResendInvitation(CSteamID player) => CanInviteReturningPlayer(player) &&
        (!HasMember(player) || Link.GetState(player) != RoomLinkState.Verified) &&
        (!invitedAt.TryGetValue(player, out float previous) || Time.unscaledTime - previous >= 8f);

    internal bool ResendInvitation(CSteamID player) => runningSandbox is { } plan && InviteReturningPlayer(player, Current, plan.Id);

    private bool InviteReturningPlayer(CSteamID player, CSteamID room, Guid session)
    {
        if (room != Current || runningSandbox?.Id != session || !CanInviteReturningPlayer(player) ||
            (HasMember(player) && Link.GetState(player) == RoomLinkState.Verified))
        {
            return false;
        }

        if (!CanResendInvitation(player))
        {
            return true; // A recent request already sent this player a Steam invitation.
        }

        if (!SteamMatchmaking.InviteUserToLobby(Current, player))
        {
            return false;
        }

        invitedAt[player] = Time.unscaledTime;
        return true;
    }

    internal bool HasMember(CSteamID id)
    {
        int count = MemberCount;
        for (int index = 0; index < count; index++)
        {
            if (MemberAt(index) == id)
            {
                return true;
            }
        }

        return false;
    }

    internal RoomLinkState GetLinkState(CSteamID player)
    {
        if (IsHost)
        {
            return player == LocalUser ? RoomLinkState.Verified : Link.GetState(player);
        }

        RoomLinkState hostState = Link.GetState(Owner);
        if (hostState != RoomLinkState.Verified || player == Owner || player == LocalUser)
        {
            return hostState;
        }

        string verified = SteamMatchmaking.GetLobbyData(Current, VerifiedMembersKey);
        return !string.IsNullOrEmpty(verified) && verified.Contains("|" + SteamIdText(player) + "|")
            ? RoomLinkState.Verified : RoomLinkState.Connecting;
    }

    internal void Create()
    {
        if (busy || InRoom)
        {
            return;
        }

        returnInvitations.Reset();
        busy = true;
        Status = "Creating room…";
        lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, RoomRules.Capacity));
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
        if (IsHost && !IsLaunching)
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
        if (string.IsNullOrEmpty(selection))
        {
            return false;
        }

        int separator = selection.IndexOf(':');
        if (separator < 0 ||
            !int.TryParse(selection.Substring(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out character) ||
            character >= CharacterCount)
        {
            return false;
        }

        ready = GetLinkState(player) == RoomLinkState.Verified &&
            TryGetSettings(out _, out _, out string revision) &&
            string.Equals(selection.Substring(separator + 1), revision, StringComparison.Ordinal);
        return true;
    }

    internal static int CharacterCount => PreRunScript.instance ? PreRunScript.instance.charImages.Count : 0;

    internal static bool IsCharacterUnlocked(int character) =>
        character >= 0 && character < CharacterCount &&
        (character == 0 || PlayerPrefs.GetInt("wonchar" + (character - 1)) > 0);

    internal void SelectCharacter(int character)
    {
        if (InRoom && !IsLaunching && selectedCharacter != character && IsCharacterUnlocked(character))
        {
            selectedCharacter = character;
            PublishSelection();
        }
    }

    internal void ChangeDifficulty(string difficulty)
    {
        if (IsHost && !IsLaunching && difficulty != Difficulty && RunSettings.presets.Exists(preset => preset.presetName == difficulty))
        {
            PublishSettings(difficulty, Options);
        }
    }

    internal void ChangeOption(RoomOptions option, bool enabled)
    {
        if (IsHost && !IsLaunching && TryGetSettings(out string difficulty, out RoomOptions options, out _))
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
        if (CanReady && TryGetSettings(out _, out _, out string revision))
        {
            PublishSelection(IsReady(LocalUser) ? "" : revision);
        }
    }

    internal void Leave(bool keepReturnAddress = false)
    {
        if (InRoom)
        {
            if (!keepReturnAddress)
            {
                returnRecord.Forget(Current);
            }
            LaunchInputsChanged?.Invoke();
            Link.Close();
            SteamMatchmaking.LeaveLobby(Current);
            Current = CSteamID.Nil;
            Owner = CSteamID.Nil;
            IsLaunching = false;
            ReturningTo = Guid.Empty;
            runningSandbox = null;
            invitedAt.Clear();
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

    internal bool ReserveSandbox(RoomLaunchPlan plan)
    {
        if (IsHost)
        {
            List<string> fields = new() { plan.Id.ToString("N") };
            foreach (LaunchPlayer player in plan.Players)
            {
                fields.Add(SteamIdText(new CSteamID(player.SteamId)));
            }
            if (SteamMatchmaking.SetLobbyData(Current, SandboxSessionKey, string.Join("|", fields)))
            {
                runningSandbox = plan;
                IsLaunching = true;
                return true;
            }
        }

        Status = "Could not reserve the team for the sandbox launch.";
        return false;
    }

    internal void BeginSandboxLaunch(RoomLaunchPlan plan)
    {
        IsLaunching = true;
        ReturningTo = Guid.Empty;
        if (Owner != LocalUser)
        {
            returnRecord.Remember(new RoomReturnAddress(Current, Owner, plan.Id));
        }
    }

    internal void EndFailedLaunch()
    {
        Leave();
        Status = "Could not launch the sandbox for everyone. Create a new room to retry.";
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

        returnInvitations.Reset();
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
        Owner = LocalUser;
        if (!SteamMatchmaking.SetLobbyData(Current, CreatorKey, SteamIdText(LocalUser)) ||
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
        Owner = SteamMatchmaking.GetLobbyOwner(Current);
        if (Current != expected || SteamMatchmaking.GetLobbyData(Current, ProtocolKey) != ProtocolVersion)
        {
            Leave();
            Status = "This room uses a different mod protocol.";
            return;
        }

        if (Owner == CSteamID.Nil || Owner == LocalUser || SteamMatchmaking.GetLobbyData(Current, CreatorKey) != SteamIdText(Owner))
        {
            Leave();
            Status = "The room host is no longer available.";
            return;
        }

        string reservation = SteamMatchmaking.GetLobbyData(Current, SandboxSessionKey);
        if (!string.IsNullOrEmpty(reservation))
        {
            string[] fields = reservation.Split('|');
            if (!Guid.TryParseExact(fields[0], "N", out Guid session) || session == Guid.Empty ||
                Array.IndexOf(fields, SteamIdText(LocalUser), 1) < 0)
            {
                Leave();
                Status = "This sandbox is reserved for its original team.";
                return;
            }
            ReturningTo = session;
            IsLaunching = true;
        }
        else if (returnRecord.Address is { } address && address.Room == Current && address.Host == Owner)
        {
            // Use the address while Steam finishes propagating the host's lobby metadata.
            ReturningTo = address.Session;
            IsLaunching = true;
        }
        InitializeMembership();
    }

    private void InitializeMembership()
    {
        selectedCharacter = 0;
        if (!IsLaunching)
        {
            PublishSelection();
        }
        Link.Open(Current, Owner);
        Status = null;
    }

    private void PublishSelection(string readyRevision = "")
    {
        string selection = selectedCharacter.ToString(CultureInfo.InvariantCulture) + ":" + readyRevision;
        SteamMatchmaking.SetLobbyMemberData(Current, MemberKey, selection);
        LaunchInputsChanged?.Invoke();
    }

    private bool PublishSettings(string difficulty, RoomOptions options)
    {
        string settings = difficulty + ":" + ((int)options).ToString(CultureInfo.InvariantCulture) +
            ":" + Guid.NewGuid().ToString("N");
        bool published = SteamMatchmaking.SetLobbyData(Current, SettingsKey, settings);
        Status = published ? null : "Could not update room settings.";
        if (published)
        {
            LaunchInputsChanged?.Invoke();
        }

        return published;
    }

    internal bool TryGetSettings(out string difficulty, out RoomOptions options, out string revision)
    {
        difficulty = "";
        options = RoomOptions.Defaults;
        revision = "";
        if (!InRoom)
        {
            return false;
        }

        string[] fields = (SteamMatchmaking.GetLobbyData(Current, SettingsKey) ?? "").Split(':');
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

        EChatMemberStateChange change = (EChatMemberStateChange)update.m_rgfChatMemberStateChange;
        CSteamID currentOwner = SteamMatchmaking.GetLobbyOwner(Current);
        if (currentOwner != CSteamID.Nil && currentOwner != Owner)
        {
            Leave();
            Status = "The host left the room.";
        }
        else if (update.m_ulSteamIDUserChanged == LocalUser.m_SteamID &&
            (change & (EChatMemberStateChange.k_EChatMemberStateChangeDisconnected | EChatMemberStateChange.k_EChatMemberStateChangeLeft)) != 0)
        {
            Leave(keepReturnAddress: true);
            Status = "Steam disconnected you from the room.";
        }
        else if (IsHost && update.m_ulSteamIDUserChanged != LocalUser.m_SteamID)
        {
            if ((change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) == 0)
            {
                Link.ForgetMember(new CSteamID(update.m_ulSteamIDUserChanged));
            }

            if (!IsLaunching)
            {
                PublishSettings(Difficulty, Options);
            }
        }
    }

    private void OnPeerChanged(CSteamID _, bool connected)
    {
        if (IsHost)
        {
            int count = MemberCount;
            List<string> verified = new();
            for (int index = 0; index < count; index++)
            {
                CSteamID player = MemberAt(index);
                if (player != LocalUser && Link.GetState(player) == RoomLinkState.Verified)
                {
                    verified.Add(SteamIdText(player));
                }
            }

            SteamMatchmaking.SetLobbyData(Current, VerifiedMembersKey, "|" + string.Join("|", verified) + "|");
            if (!connected && !IsLaunching)
            {
                PublishSettings(Difficulty, Options);
            }
        }
        else if (!connected && InRoom && !IsLaunching)
        {
            PublishSelection();
        }
    }

    private void OnHostEnded()
    {
        Leave();
        Status = "The host closed the room.";
    }

    private static string SteamIdText(CSteamID player) => player.m_SteamID.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Leave(keepReturnAddress: true);
        Link.HostEnded -= OnHostEnded;
        Link.Dispose();
        returnInvitations.Dispose();
        membershipChanged.Dispose();
        invitation.Dispose();
        lobbyCreated.Dispose();
        lobbyEntered.Dispose();
    }
}
