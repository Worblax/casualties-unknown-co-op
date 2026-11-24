using CasualtiesCoop.Core;

internal sealed class ManualClock : IMonotonicClock
{
    public float Now { get; private set; }

    internal void AdvanceTo(float time)
    {
        if (!float.IsFinite(time) || time < Now)
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }
        Now = time;
    }
}
