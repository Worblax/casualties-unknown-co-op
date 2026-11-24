using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using CasualtiesCoop.Core;
using CasualtiesCoop.Lobby;
using Steamworks;

namespace CasualtiesCoop.Networking;

internal sealed class RoomReturnInvitations : IDisposable
{
    private const int Channel = 477;
    private const int PacketSize = 25;
    private const byte RequestInvite = 1;
    private const byte InviteSent = 2;
    private const byte InviteUnavailable = 3;
    private readonly Func<CSteamID, bool> canReceive;
    private readonly Func<CSteamID, CSteamID, Guid, bool> invite;
    private readonly ManualLogSource log;
    private readonly IMonotonicClock clock;
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequests;
    private readonly IntPtr[] incoming = new IntPtr[8];
    private RoomReturnAddress? pending;
    private float expiresAt;
    private bool unavailable;

    internal bool Waiting => pending.HasValue;
    internal string? Status { get; private set; }

    internal RoomReturnInvitations(Func<CSteamID, bool> canReceive, Func<CSteamID, CSteamID, Guid, bool> invite,
        ManualLogSource log, IMonotonicClock clock)
    {
        this.canReceive = canReceive;
        this.invite = invite;
        this.log = log;
        this.clock = clock;
        sessionRequests = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequested);
    }

    internal void Request(RoomReturnAddress address)
    {
        if (Waiting)
        {
            return;
        }

        try
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            if (Send(address.Host, RequestInvite, address.Room, address.Session))
            {
                pending = address;
                expiresAt = clock.Now + 15f;
                Status = "Requesting a Steam invitation from the host…";
                return;
            }
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            Disable(ex);
        }
        Status = "Could not reach the host. Try again or ask for a Steam invitation.";
    }

    internal void Tick(bool hosting)
    {
        if (unavailable || (!hosting && !Waiting))
        {
            return;
        }

        if (Waiting && clock.Now >= expiresAt)
        {
            pending = null;
            Status = "The host did not respond. Try again or ask for a Steam invitation.";
        }

        try
        {
            int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, incoming, incoming.Length);
            try
            {
                for (int index = 0; index < count; index++)
                {
                    Receive(SteamNetworkingMessage_t.FromIntPtr(incoming[index]));
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
        catch (Exception ex) when (Unavailable(ex))
        {
            Disable(ex);
        }
    }

    private void OnSessionRequested(SteamNetworkingMessagesSessionRequest_t request)
    {
        SteamNetworkingIdentity identity = request.m_identityRemote;
        if (unavailable || !canReceive(identity.GetSteamID()))
        {
            return;
        }

        try { SteamNetworkingMessages.AcceptSessionWithUser(ref identity); }
        catch (Exception ex) when (Unavailable(ex)) { Disable(ex); }
    }

    private void Receive(SteamNetworkingMessage_t message)
    {
        if (message.m_cbSize != PacketSize || message.m_pData == IntPtr.Zero)
        {
            return;
        }

        byte[] bytes = new byte[PacketSize];
        Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
        CSteamID sender = message.m_identityPeer.GetSteamID();
        CSteamID room = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(1)));
        Guid session = new(bytes.AsSpan(9));
        if (bytes[0] == RequestInvite && canReceive(sender))
        {
            Send(sender, invite(sender, room, session) ? InviteSent : InviteUnavailable, room, session);
        }
        else if (bytes[0] is InviteSent or InviteUnavailable && pending is { } address && sender == address.Host &&
            room == address.Room && session == address.Session)
        {
            pending = null;
            Status = bytes[0] == InviteSent ? "Accept the new invitation in Steam to return."
                : "The host could not invite you to this session.";
        }
    }

    private static bool Send(CSteamID peer, byte kind, CSteamID room, Guid session)
    {
        byte[] bytes = new byte[PacketSize];
        bytes[0] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1), room.m_SteamID);
        session.TryWriteBytes(bytes.AsSpan(9));
        SteamNetworkingIdentity identity = default;
        identity.SetSteamID(peer);
        GCHandle data = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return SteamNetworkingMessages.SendMessageToUser(ref identity, data.AddrOfPinnedObject(), (uint)bytes.Length,
                Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession,
                Channel) == EResult.k_EResultOK;
        }
        finally { data.Free(); }
    }

    internal void Reset()
    {
        pending = null;
        Status = null;
    }

    private static bool Unavailable(Exception ex) => ex is EntryPointNotFoundException or DllNotFoundException or InvalidOperationException;

    private void Disable(Exception ex)
    {
        unavailable = true;
        pending = null;
        Status = "Steam return requests are unavailable. Ask the host for a Steam invitation.";
        log.LogWarning($"Steam room invitation requests unavailable: {ex.Message}");
    }

    public void Dispose() => sessionRequests.Dispose();
}
