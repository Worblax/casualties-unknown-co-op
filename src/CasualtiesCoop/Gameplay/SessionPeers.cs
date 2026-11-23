using System;
using System.Collections.Generic;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Gameplay;

internal delegate bool PlayerPositionLookup(ulong player, out Vector2 position);

internal sealed class SessionPeers : IDisposable
{
    private readonly SteamRoomLink link;
    private readonly RoomLaunchPlan plan;

    internal Guid SessionId => plan.Id;
    internal ulong LocalPlayer { get; }
    internal ulong Host { get; }
    internal bool IsHost => LocalPlayer == Host;
    internal IReadOnlyList<LaunchPlayer> Players => plan.Players;
    internal event Action<ulong, bool>? ConnectionChanged;

    internal SessionPeers(SteamRoomLink link, RoomLaunchPlan plan, ulong localPlayer, ulong host)
    {
        this.link = link;
        this.plan = plan;
        LocalPlayer = localPlayer;
        Host = host;
        link.PeerChanged += NotifyConnectionChanged;
    }

    internal bool TryGetPlayer(ulong id, out LaunchPlayer player) => plan.TryGetPlayer(id, out player);

    internal bool IsVerified(ulong player) => link.GetState(new CSteamID(player)) == RoomLinkState.Verified;

    internal bool Send(ulong player, byte[] frame, bool reliable = true) => link.Send(new CSteamID(player), frame, reliable);

    internal bool AuthoredBy(ulong sender, ulong player) => player != LocalPlayer && TryGetPlayer(player, out _) &&
        (IsHost ? sender == player : sender == Host);

    internal void Broadcast(byte[] frame, bool reliable, ulong except = 0)
    {
        foreach (LaunchPlayer player in Players)
        {
            if (player.SteamId != LocalPlayer && player.SteamId != except)
            {
                Send(player.SteamId, frame, reliable);
            }
        }
    }

    private void NotifyConnectionChanged(CSteamID player, bool connected) => ConnectionChanged?.Invoke(player.m_SteamID, connected);

    public void Dispose() => link.PeerChanged -= NotifyConnectionChanged;
}
