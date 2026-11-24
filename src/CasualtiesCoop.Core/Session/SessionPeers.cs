using System;
using System.Collections.Generic;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Session;

namespace CasualtiesCoop.Gameplay;

internal sealed class SessionPeers : IDisposable
{
    private readonly ISessionTransport transport;
    private readonly RoomLaunchPlan plan;
    private Func<ulong, byte[], bool>? receiver;

    internal Guid SessionId => plan.Id;
    internal ulong LocalPlayer { get; }
    internal ulong Host { get; }
    internal bool IsHost => LocalPlayer == Host;
    internal IReadOnlyList<LaunchPlayer> Players => plan.Players;
    internal event Action<ulong, bool>? ConnectionChanged;

    internal SessionPeers(ISessionTransport transport, RoomLaunchPlan plan, ulong localPlayer, ulong host)
    {
        this.transport = transport;
        this.plan = plan;
        LocalPlayer = localPlayer;
        Host = host;
        LocalParticipant = Identify(localPlayer);
        transport.ConnectionChanged += NotifyConnectionChanged;
    }

    internal bool TryGetPlayer(ulong id, out LaunchPlayer player) => plan.TryGetPlayer(id, out player);

    internal ParticipantId LocalParticipant { get; }

    internal ParticipantId Identify(ulong address)
    {
        for (int index = 0; index < plan.Players.Count; index++)
        {
            if (plan.Players[index].SteamId == address)
            {
                return new ParticipantId(index + 1);
            }
        }
        return default;
    }

    internal ulong AddressOf(ParticipantId participant) =>
        participant.Seat > 0 && participant.Seat <= plan.Players.Count ? plan.Players[participant.Seat - 1].SteamId : 0;

    internal bool IsVerified(ulong player) => transport.IsVerified(player);

    internal bool Send(ulong player, byte[] frame, bool reliable = true) => transport.TrySend(player, frame, reliable);

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

    internal void AttachReceiver(Func<ulong, byte[], bool> receive)
    {
        if (transport.ReceiveFrame is not null)
        {
            throw new InvalidOperationException("The session transport already has a receiver.");
        }

        receiver = receive;
        transport.ReceiveFrame = receive;
    }

    private void NotifyConnectionChanged(ulong player, bool connected) => ConnectionChanged?.Invoke(player, connected);

    public void Dispose()
    {
        if (receiver is not null && transport.ReceiveFrame == receiver)
        {
            transport.ReceiveFrame = null;
        }
        receiver = null;
        transport.ConnectionChanged -= NotifyConnectionChanged;
    }
}
