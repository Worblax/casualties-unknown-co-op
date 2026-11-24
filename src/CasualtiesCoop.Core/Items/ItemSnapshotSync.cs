using System;

namespace CasualtiesCoop.Core.Items;

internal sealed class ItemSnapshotSync
{
    private const float RetryInterval = 1f;
    private const float ReconcileInterval = 8f;
    internal const float Timeout = 10f;
    private const float MaxTransferTime = 60f;
    private readonly IMonotonicClock clock;
    private bool suspended;
    private ulong lastId;
    private float nextRequest;
    private float deadline;
    private float transferDeadline;

    internal ItemSnapshotSync(IMonotonicClock clock) => this.clock = clock;

    internal bool Synced { get; private set; }
    internal ulong Pending { get; private set; }

    internal void Suspend()
    {
        suspended = true;
        Reset();
    }

    internal void Resume()
    {
        suspended = false;
        Reset();
    }

    internal bool Accepts(ulong snapshotId) => !suspended && snapshotId != 0 && snapshotId == Pending && clock.Now < deadline;

    internal void MarkOutOfSync()
    {
        if (Synced && Pending == 0)
        {
            nextRequest = 0;
        }
        Synced = false;
    }

    internal void RecordProgress() => deadline = Math.Min(transferDeadline, clock.Now + Timeout);

    internal void Tick(Func<ulong, bool> requestSnapshot)
    {
        float now = clock.Now;
        if (Pending != 0 && now >= deadline)
        {
            Pending = 0;
            Synced = false;
        }
        if (suspended || Pending != 0 || now < nextRequest || lastId == ulong.MaxValue)
        {
            return;
        }

        ulong id = ++lastId;
        nextRequest = now + RetryInterval;
        if (requestSnapshot(id))
        {
            Pending = id;
            transferDeadline = now + MaxTransferTime;
            deadline = now + Timeout;
        }
    }

    internal void Finish(bool applied)
    {
        Pending = 0;
        Synced = applied;
        nextRequest = clock.Now + (applied ? ReconcileInterval : RetryInterval);
    }

    private void Reset()
    {
        Synced = false;
        Pending = 0;
        nextRequest = 0;
    }
}
