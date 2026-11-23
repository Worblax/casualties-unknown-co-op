using System;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Networking;
using UnityEngine;

namespace CasualtiesCoop.Lobby;

internal sealed class SessionReturnRequest
{
    private readonly SteamLobby lobby;
    private Guid session;
    private Guid attempt;
    private float nextSend;

    internal SessionReturnRequest(SteamLobby lobby) => this.lobby = lobby;

    internal void Tick()
    {
        if (session != lobby.ReturningTo)
        {
            session = lobby.ReturningTo;
            attempt = Guid.NewGuid();
            nextSend = 0;
        }

        float now = Time.unscaledTime;
        if (session != Guid.Empty && now >= nextSend && lobby.Link.GetState(lobby.Owner) == RoomLinkState.Verified)
        {
            nextSend = now + 1f;
            lobby.Link.Send(lobby.Owner, SessionRejoinWire.WriteReference(MessageKind.SessionRejoinRequest, session, attempt));
        }
    }

    internal bool ReadState(byte[] message, out SessionRejoinState state) =>
        SessionRejoinWire.TryReadState(message, lobby.ReturningTo, out state) && state.Attempt == attempt;

    internal void Reset() => session = Guid.Empty;
}
