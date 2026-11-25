using System;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

internal sealed class ItemMotionBuffer
{
    internal const float PlaybackDelay = 0.18f;
    internal const float MaxExtrapolation = 0.10f;
    private const int Capacity = 16;
    private const float MaxSampleGap = 0.6f;
    private const float TeleportTolerance = 4f;
    private const double ClockAdjustmentRate = 0.20;
    private const double MaxCatchUpRate = 1.5;

    private readonly (float HostTime, ItemLocation Location)[] samples = new (float, ItemLocation)[Capacity];
    private ItemLocation initial;
    private int start;
    private int count;
    private double offset;
    private double lowestOffset;
    private float lastArrival;
    private float lastEvaluation;
    private double playbackTime;
    private bool hasPlaybackTime;

    internal void Reset(ItemLocation location, float localNow)
    {
        initial = location;
        lastArrival = localNow;
        start = 0;
        count = 0;
        hasPlaybackTime = false;
    }

    internal void Push(ItemState state, float localArrival)
    {
        float hostTime = state.SampleTime;
        if (hostTime <= 0f || !float.IsFinite(hostTime) || !float.IsFinite(localArrival) || localArrival < lastArrival)
        {
            return;
        }

        if (count > 0)
        {
            var last = At(count - 1);
            float duration = hostTime - last.HostTime;
            if (duration < 0f)
            {
                return;
            }
            if (duration > MaxSampleGap || IsTeleport(last.Location, state.Location, duration))
            {
                Reset(state.Location, localArrival);
            }
            else if (duration == 0f)
            {
                samples[(start + count - 1) % Capacity] = (hostTime, state.Location);
                return;
            }
        }

        double observedOffset = (double)localArrival - hostTime;
        if (count == 0)
        {
            offset = lowestOffset = observedOffset;
        }
        else
        {
            // The fastest observed delivery anchors playback. Slew toward it so one early packet
            // cannot leap the rendered timeline forward by an entire latency fluctuation.
            lowestOffset = Math.Min(lowestOffset, observedOffset);
            offset = Math.Max(lowestOffset, offset - (localArrival - lastArrival) * ClockAdjustmentRate);
        }
        lastArrival = localArrival;

        samples[(start + count) % Capacity] = (hostTime, state.Location);
        if (count == Capacity)
        {
            start = (start + 1) % Capacity;
        }
        else
        {
            count++;
        }
    }

    internal ItemLocation Evaluate(float localNow)
    {
        if (count == 0)
        {
            return initial;
        }

        double desiredTime = localNow - offset - PlaybackDelay;
        double horizon = At(count - 1).HostTime + (double)MaxExtrapolation;
        // Freeze the playback cursor when the extrapolation budget runs out, then converge to
        // normal playback speed gradually as packets return instead of jumping over the gap.
        double reachableTime = hasPlaybackTime
            ? playbackTime + Math.Max(0f, localNow - lastEvaluation) * MaxCatchUpRate
            : desiredTime;
        playbackTime = Math.Min(horizon, Math.Min(desiredTime, reachableTime));
        lastEvaluation = localNow;
        hasPlaybackTime = true;
        var previous = At(0);
        if (playbackTime <= previous.HostTime)
        {
            return count < Capacity ? initial : previous.Location;
        }
        for (int index = 1; index < count; index++)
        {
            var next = At(index);
            if (playbackTime <= next.HostTime)
            {
                float duration = next.HostTime - previous.HostTime;
                return Interpolate(previous.Location, next.Location,
                    (float)((playbackTime - previous.HostTime) / duration), duration);
            }
            previous = next;
        }

        ItemLocation latest = previous.Location;
        double ahead = playbackTime - previous.HostTime;
        float extrapolation = (float)Math.Min(ahead, MaxExtrapolation);
        bool holding = playbackTime == horizon && desiredTime > horizon;
        return new ItemLocation(latest.X + latest.VelocityX * extrapolation,
            latest.Y + latest.VelocityY * extrapolation, NormalizeAngle(latest.Angle + latest.Spin * extrapolation),
            holding ? 0f : latest.VelocityX, holding ? 0f : latest.VelocityY, holding ? 0f : latest.Spin);
    }

    private (float HostTime, ItemLocation Location) At(int index) => samples[(start + index) % Capacity];

    private static bool IsTeleport(ItemLocation from, ItemLocation to, float duration)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float fromSpeedSquared = from.VelocityX * from.VelocityX + from.VelocityY * from.VelocityY;
        float toSpeedSquared = to.VelocityX * to.VelocityX + to.VelocityY * to.VelocityY;
        float limit = TeleportTolerance + duration * MathF.Sqrt(Math.Max(fromSpeedSquared, toSpeedSquared));
        return dx * dx + dy * dy > limit * limit;
    }

    private static ItemLocation Interpolate(ItemLocation from, ItemLocation to, float fraction, float duration)
    {
        float expectedTurn = (from.Spin + to.Spin) * 0.5f * duration;
        float delta = to.Angle - from.Angle;
        delta += 360f * MathF.Round((expectedTurn - delta) / 360f);
        return new ItemLocation(Lerp(from.X, to.X, fraction), Lerp(from.Y, to.Y, fraction),
            NormalizeAngle(from.Angle + delta * fraction), Lerp(from.VelocityX, to.VelocityX, fraction),
            Lerp(from.VelocityY, to.VelocityY, fraction), Lerp(from.Spin, to.Spin, fraction));
    }

    private static float Lerp(float from, float to, float fraction) => from + (to - from) * fraction;

    private static float NormalizeAngle(float angle) => (angle % 360f + 360f) % 360f;
}
