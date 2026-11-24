using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using CasualtiesCoop.Core;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Lobby;
using Steamworks;

namespace CasualtiesCoop.Networking;

internal enum RoomLinkState
{
    Connecting,
    Verified,
    Incompatible
}

internal sealed class SteamRoomLink : IDisposable
{
    private const int VirtualPort = 477;
    private const int RoomEnded = 1001;
    private const int HostClosedRoom = 1002;
    private const int BuildMismatch = 2001;
    private const int InvalidFrame = 2002;
    private const int LinkFailed = 2003;
    private const float RetryDelay = 4f;
    private const float HandshakeTimeout = 20f;
    private static readonly SteamNetworkingConfigValue_t[] NoOptions = Array.Empty<SteamNetworkingConfigValue_t>();

    private sealed class Peer
    {
        internal readonly CSteamID Id;
        internal readonly HSteamNetConnection Connection;
        internal readonly float Deadline;
        internal MessageKind Expected;
        internal bool Connected;
        internal bool Verified => Expected == MessageKind.None;

        internal Peer(CSteamID id, HSteamNetConnection connection, MessageKind expected, float deadline)
        {
            Id = id;
            Connection = connection;
            Expected = expected;
            Deadline = deadline;
        }
    }

    private readonly CSteamID localUser;
    private readonly Func<CSteamID, bool> isRoomMember;
    private readonly Guid gameBuild;
    private readonly Guid modBuild;
    private readonly ManualLogSource log;
    private readonly IMonotonicClock clock;
    private readonly Callback<SteamNetConnectionStatusChangedCallback_t> connections;
    private readonly List<Peer> peers = new(RoomRules.Capacity - 1);
    private readonly HashSet<CSteamID> incompatible = new();
    private readonly IntPtr[] incoming = new IntPtr[16];
    private HSteamListenSocket listener = HSteamListenSocket.Invalid;
    private CSteamID room;
    private CSteamID host;
    private float retryAt;
    private bool relayInitialized;
    private bool unavailable;

    internal event Action<CSteamID, bool>? PeerChanged;
    internal event Action? HostEnded;
    internal Func<CSteamID, byte[], bool>? HandleFrame { get; set; }
    internal string? Status { get; private set; }
    private bool Hosting => host == localUser;

    internal SteamRoomLink(CSteamID localUser, Func<CSteamID, bool> isRoomMember, Guid gameBuild, Guid modBuild, ManualLogSource log, IMonotonicClock clock)
    {
        this.localUser = localUser;
        this.isRoomMember = isRoomMember;
        this.gameBuild = gameBuild;
        this.modBuild = modBuild;
        this.log = log;
        this.clock = clock;
        connections = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionChanged);
    }

    internal void Open(CSteamID roomId, CSteamID owner)
    {
        Close();
        room = roomId;
        host = owner;
    }

    internal RoomLinkState GetState(CSteamID player)
    {
        if (incompatible.Contains(player))
        {
            return RoomLinkState.Incompatible;
        }

        return Find(player) is { Verified: true } ? RoomLinkState.Verified : RoomLinkState.Connecting;
    }

    internal void Tick()
    {
        if (room == CSteamID.Nil || unavailable)
        {
            return;
        }

        float now = clock.Now;
        if (now >= retryAt && (Hosting ? listener == HSteamListenSocket.Invalid : peers.Count == 0 && !incompatible.Contains(host)))
        {
            TryOpenSocket(now);
        }

        for (int index = peers.Count - 1; index >= 0; index--)
        {
            Peer peer = peers[index];
            if (!isRoomMember(peer.Id))
            {
                Drop(peer, RoomEnded, "Player left the room.");
            }
            else if (!peer.Verified && now >= peer.Deadline)
            {
                Drop(peer, LinkFailed, "Steam connection timed out.");
            }
            else if (peer.Connected)
            {
                Receive(peer);
            }
        }
    }

    internal bool Send(CSteamID player, byte[] message, bool reliable = true) =>
        Find(player) is { Verified: true } peer && Send(peer, message, reliable);

    private void TryOpenSocket(float now)
    {
        retryAt = now + RetryDelay;
        try
        {
            if (!relayInitialized)
            {
                SteamNetworkingUtils.InitRelayNetworkAccess();
                relayInitialized = true;
            }

            if (Hosting)
            {
                listener = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, NoOptions);
                Status = listener == HSteamListenSocket.Invalid ? "Steam P2P listener unavailable. Retrying…" : null;
                if (listener != HSteamListenSocket.Invalid)
                {
                    log.LogInfo("Steam P2P listener ready.");
                }
                return;
            }

            SteamNetworkingIdentity identity = default;
            identity.SetSteamID(host);
            HSteamNetConnection connection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, NoOptions);
            if (connection != HSteamNetConnection.Invalid)
            {
                peers.Add(new Peer(host, connection, MessageKind.HostWelcome, clock.Now + HandshakeTimeout));
                CoopDiagnostics.Trace("Steam", "Opened a P2P connection to the room host.");
            }

            Status = connection == HSteamNetConnection.Invalid ? "Could not reach the host. Retrying…" : "Connecting to the host…";
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException or InvalidOperationException)
        {
            unavailable = true;
            Status = "Steam P2P is unavailable in this game session.";
            log.LogWarning($"Steam P2P initialization failed: {exception.Message}");
        }
    }

    private void OnConnectionChanged(SteamNetConnectionStatusChangedCallback_t update)
    {
        SteamNetConnectionInfo_t info = update.m_info;
        CSteamID player = info.m_identityRemote.GetSteamID();
        if (listener != HSteamListenSocket.Invalid && info.m_hListenSocket == listener &&
            info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting &&
            update.m_eOldState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None)
        {
            Accept(update.m_hConn, player);
            return;
        }

        Peer? peer = peers.Find(candidate => candidate.Connection == update.m_hConn);
        if (peer is null)
        {
            return;
        }

        switch (info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                if (player != peer.Id || !isRoomMember(player))
                {
                    Drop(peer, InvalidFrame, "Connection identity changed.");
                    return;
                }

                peer.Connected = true;
                if (!Hosting && !SendHandshake(peer, MessageKind.GuestHello))
                {
                    Drop(peer, LinkFailed, "Could not send the room handshake.");
                }
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                bool hostEnded = !Hosting && peer.Verified && info.m_eEndReason == HostClosedRoom;
                bool mismatch = info.m_eEndReason == BuildMismatch;
                Drop(peer, mismatch ? BuildMismatch : LinkFailed,
                    mismatch ? "The host and guest must use the same game and mod builds." : "Steam connection interrupted.");
                if (hostEnded)
                {
                    HostEnded?.Invoke();
                }
                break;
        }
    }

    private void Accept(HSteamNetConnection connection, CSteamID player)
    {
        if (!Hosting || player == CSteamID.Nil || player == host || !isRoomMember(player) ||
            peers.Count >= RoomRules.Capacity - 1 || Find(player) is not null)
        {
            SteamNetworkingSockets.CloseConnection(connection, RoomEnded, "Connection is not from an available room member.", false);
            return;
        }

        if (SteamNetworkingSockets.AcceptConnection(connection) != EResult.k_EResultOK)
        {
            SteamNetworkingSockets.CloseConnection(connection, LinkFailed, "Could not accept the Steam connection.", false);
            return;
        }

        incompatible.Remove(player);
        peers.Add(new Peer(player, connection, MessageKind.GuestHello, clock.Now + HandshakeTimeout));
    }

    private void Receive(Peer peer)
    {
        int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(peer.Connection, incoming, incoming.Length);
        if (count < 0)
        {
            Drop(peer, LinkFailed, "Steam connection unavailable.");
            return;
        }

        try
        {
            for (int index = 0; index < count; index++)
            {
                SteamNetworkingMessage_t native = SteamNetworkingMessage_t.FromIntPtr(incoming[index]);
                if (native.m_cbSize is < 1 or > RoomWire.MaxFrameSize || native.m_pData == IntPtr.Zero)
                {
                    Drop(peer, InvalidFrame, "Invalid room message size.");
                    return;
                }

                byte[] message = new byte[native.m_cbSize];
                Marshal.Copy(native.m_pData, message, 0, message.Length);
                CoopDiagnostics.RecordFrame((MessageKind)message[0], message.Length, sent: false);
                if (peer.Verified)
                {
                    if (HandleFrame?.Invoke(peer.Id, message) != true)
                    {
                        Drop(peer, InvalidFrame, "Unexpected room message.");
                        return;
                    }
                }
                // Unreliable gameplay updates can overtake the reliable confirmation after reconnecting.
                else if ((MessageKind)message[0] is not (MessageKind.CharacterPose or MessageKind.CharacterSound or MessageKind.ClawEffect or MessageKind.ItemMotion or
                    MessageKind.FluidPush or MessageKind.LightSnapshot) &&
                    !CompleteHandshakeStep(peer, message))
                {
                    return;
                }
            }
        }
        finally
        {
            for (int index = 0; index < count; index++)
            {
                SteamNetworkingMessage_t.Release(incoming[index]);
            }
        }
    }

    private bool CompleteHandshakeStep(Peer peer, byte[] message)
    {
        if (!RoomWire.IsHandshake(message, peer.Expected, room.m_SteamID))
        {
            Drop(peer, InvalidFrame, "Invalid Steam room handshake.");
            return false;
        }

        if (!RoomWire.HasMatchingBuild(message, gameBuild, modBuild))
        {
            Drop(peer, BuildMismatch, "The host and guest must use the same game and mod builds.");
            return false;
        }

        MessageKind response = peer.Expected switch
        {
            MessageKind.GuestHello => MessageKind.HostWelcome,
            MessageKind.HostWelcome => MessageKind.GuestConfirm,
            _ => MessageKind.None
        };
        if (response != MessageKind.None && !SendHandshake(peer, response))
        {
            Drop(peer, LinkFailed, "Could not confirm the Steam room handshake.");
            return false;
        }

        peer.Expected = peer.Expected == MessageKind.GuestHello ? MessageKind.GuestConfirm : MessageKind.None;
        if (peer.Verified)
        {
            Status = null;
            PeerChanged?.Invoke(peer.Id, true);
            log.LogInfo("Steam P2P room peer verified.");
        }

        return true;
    }

    private bool SendHandshake(Peer peer, MessageKind frame) =>
        Send(peer, RoomWire.Handshake(frame, room.m_SteamID, gameBuild, modBuild));

    private static bool Send(Peer peer, byte[] message, bool reliable = true)
    {
        GCHandle data = GCHandle.Alloc(message, GCHandleType.Pinned);
        try
        {
            int flags = reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_UnreliableNoDelay;
            bool accepted = SteamNetworkingSockets.SendMessageToConnection(peer.Connection, data.AddrOfPinnedObject(),
                (uint)message.Length, flags, out _) == EResult.k_EResultOK;
            CoopDiagnostics.RecordFrame((MessageKind)message[0], message.Length, sent: true, accepted);
            return accepted;
        }
        finally
        {
            data.Free();
        }
    }

    private Peer? Find(CSteamID player) => peers.Find(candidate => candidate.Id == player);

    private void Drop(Peer peer, int reason, string message)
    {
        if (CoopDiagnostics.Enabled)
        {
            CoopDiagnostics.Trace("Steam", $"Closing a P2P connection ({reason}). {message}");
        }
        peers.Remove(peer);
        SteamNetworkingSockets.CloseConnection(peer.Connection, reason, message, false);
        if (reason == BuildMismatch)
        {
            incompatible.Add(peer.Id);
        }

        if (!Hosting)
        {
            retryAt = clock.Now + RetryDelay;
            Status = reason == BuildMismatch ? message : message + " Retrying…";
        }

        if (peer.Verified)
        {
            PeerChanged?.Invoke(peer.Id, false);
        }
    }

    internal void ForgetMember(CSteamID player)
    {
        if (Find(player) is { } peer)
        {
            Drop(peer, RoomEnded, "Player left the room.");
        }
        incompatible.Remove(player);
    }

    internal void Close()
    {
        int reason = Hosting ? HostClosedRoom : RoomEnded;
        foreach (Peer peer in peers)
        {
            SteamNetworkingSockets.CloseConnection(peer.Connection, reason, "Left the Steam room.", false);
        }

        peers.Clear();
        incompatible.Clear();
        if (listener != HSteamListenSocket.Invalid)
        {
            SteamNetworkingSockets.CloseListenSocket(listener);
            listener = HSteamListenSocket.Invalid;
        }

        room = CSteamID.Nil;
        host = CSteamID.Nil;
        Status = null;
        unavailable = false;
        retryAt = 0;
    }

    public void Dispose()
    {
        Close();
        connections.Dispose();
    }
}
