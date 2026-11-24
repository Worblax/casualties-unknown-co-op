using System;

namespace CasualtiesCoop.Core.Session;

// An ordinal in the frozen launch roster. Zero represents no owner.
internal readonly struct ParticipantId : IEquatable<ParticipantId>
{
    internal readonly int Seat;

    internal ParticipantId(int seat) => Seat = seat;

    internal bool IsNone => Seat == 0;

    public bool Equals(ParticipantId other) => Seat == other.Seat;
    public override bool Equals(object? obj) => obj is ParticipantId other && Equals(other);
    public override int GetHashCode() => Seat;
    public static bool operator ==(ParticipantId left, ParticipantId right) => left.Equals(right);
    public static bool operator !=(ParticipantId left, ParticipantId right) => !left.Equals(right);
}
