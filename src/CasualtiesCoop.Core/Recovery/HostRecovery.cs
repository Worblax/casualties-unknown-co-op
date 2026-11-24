using System;
using System.Collections.Generic;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core;

internal sealed class HostRecovery
{
    private readonly HashSet<ulong> returning = new();
    private readonly Dictionary<ulong, (SessionRejoinState State, byte[] Frame)> offers = new();

    internal void MarkReturning(ulong peer)
    {
        returning.Add(peer);
        offers.Remove(peer);
    }

    internal bool IsReturning(ulong peer) => returning.Contains(peer);

    internal bool AllowsGameplay(ulong peer, MessageKind kind) =>
        !IsReturning(peer) || (kind == MessageKind.ItemSyncRequest && offers.ContainsKey(peer));

    internal SessionRejoinState? GetOffer(ulong peer) => offers.TryGetValue(peer, out var offer) ? offer.State : null;

    internal byte[]? Offer(ulong peer, Guid attempt, Func<SessionRejoinState> capture, out bool created)
    {
        created = false;
        if (attempt == Guid.Empty || !IsReturning(peer))
        {
            return null;
        }

        if (offers.TryGetValue(peer, out var existing))
        {
            return existing.State.Attempt == attempt ? existing.Frame : null;
        }

        SessionRejoinState state = capture();
        if (SessionRejoinWire.WriteState(state) is not { } frame)
        {
            return null;
        }

        offers.Add(peer, (state, frame));
        created = true;
        return frame;
    }

    internal bool TryCommit(ulong peer, Guid attempt, Func<ulong, bool> sendConfirmation)
    {
        return offers.TryGetValue(peer, out var offer) && offer.State.Attempt == attempt &&
            sendConfirmation(peer) && returning.Remove(peer);
    }
}
