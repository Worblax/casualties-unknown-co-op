using System;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Core;

internal sealed class GuestRecovery
{
    private const float RetryInterval = 1f;
    internal const float AttemptTimeout = 25f;
    private const int MaxRetries = 2;
    private readonly IMonotonicClock clock;
    private float nextSend;
    private bool acknowledged;
    private float expiresAt;
    private int retries;

    internal GuestRecovery(IMonotonicClock clock) => this.clock = clock;

    internal Guid Attempt { get; private set; }
    internal SessionRejoinState? Snapshot { get; private set; }
    internal bool Waiting => Attempt != Guid.Empty;
    internal bool TimedOut => Waiting && clock.Now >= expiresAt;

    internal void Begin(Guid attempt)
    {
        retries = 0;
        Start(attempt);
    }

    private void Start(Guid attempt)
    {
        Attempt = attempt;
        expiresAt = clock.Now + AttemptTimeout;
        Snapshot = null;
        acknowledged = false;
        nextSend = 0;
    }

    internal bool ExpectsState(Guid attempt) => Waiting && !TimedOut && Attempt == attempt && Snapshot is null;

    internal bool TryRetry(Guid attempt)
    {
        if (!TimedOut || retries == MaxRetries) return false;
        retries++;
        Start(attempt);
        return true;
    }

    internal void RestoreFrom(SessionRejoinState state)
    {
        if (Attempt != state.Attempt) Begin(state.Attempt);
        Snapshot = state;
    }

    internal void Tick(bool snapshotApplied, Func<MessageKind, Guid, bool> trySend)
    {
        float now = clock.Now;
        if (!Waiting || TimedOut || now < nextSend || (Snapshot.HasValue && !snapshotApplied))
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
        if (!Waiting || TimedOut || Attempt != attempt || !acknowledged)
        {
            return false;
        }

        Begin(Guid.Empty);
        return true;
    }
}
