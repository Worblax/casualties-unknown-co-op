using System;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core;

internal sealed class GuestRecovery
{
    private const float RetryInterval = 1f;
    private readonly IMonotonicClock clock;
    private float nextSend;
    private bool acknowledged;

    internal GuestRecovery(IMonotonicClock clock) => this.clock = clock;

    internal Guid Attempt { get; private set; }
    internal SessionRejoinState? Snapshot { get; private set; }
    internal bool Waiting => Attempt != Guid.Empty;

    internal void Begin(Guid attempt)
    {
        Attempt = attempt;
        Snapshot = null;
        acknowledged = false;
        nextSend = 0;
    }

    internal bool ExpectsState(Guid attempt) => Waiting && Attempt == attempt && Snapshot is null;

    internal void RestoreFrom(SessionRejoinState state)
    {
        Begin(state.Attempt);
        Snapshot = state;
    }

    internal void Tick(bool snapshotApplied, Func<MessageKind, Guid, bool> trySend)
    {
        float now = clock.Now;
        if (!Waiting || now < nextSend || (Snapshot.HasValue && !snapshotApplied))
        {
            return;
        }

        nextSend = now + RetryInterval;
        if (Snapshot.HasValue)
        {
            acknowledged |= trySend(MessageKind.SessionRejoinApplied, Attempt);
        }
        else
        {
            trySend(MessageKind.SessionRejoinRequest, Attempt);
        }
    }

    internal bool TryCommit(Guid attempt)
    {
        if (!Waiting || Attempt != attempt || !acknowledged)
        {
            return false;
        }

        Begin(Guid.Empty);
        return true;
    }
}
