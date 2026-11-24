using System;
using CasualtiesCoop.Core;
using Steamworks;

namespace CasualtiesCoop.Networking;

internal sealed class SteamSessionTransport : ISessionTransport, IDisposable
{
    private readonly SteamRoomLink link;

    internal SteamSessionTransport(SteamRoomLink link)
    {
        this.link = link;
        link.PeerChanged += OnPeerChanged;
    }

    public event Action<ulong, bool>? ConnectionChanged;
    public Func<ulong, byte[], bool>? ReceiveFrame { get; set; }

    public bool IsVerified(ulong peer) => link.GetState(new CSteamID(peer)) == RoomLinkState.Verified;

    public bool TrySend(ulong peer, byte[] frame, bool reliable) => link.Send(new CSteamID(peer), frame, reliable);

    internal bool DispatchVerified(CSteamID peer, byte[] frame) => ReceiveFrame?.Invoke(peer.m_SteamID, frame) == true;

    private void OnPeerChanged(CSteamID peer, bool verified) => ConnectionChanged?.Invoke(peer.m_SteamID, verified);

    public void Dispose()
    {
        link.PeerChanged -= OnPeerChanged;
        ReceiveFrame = null;
    }
}
