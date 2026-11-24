using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

internal struct ItemState
{
    internal ulong Id;
    internal uint Revision;
    internal uint Sample;
    internal string Definition;
    internal ParticipantId Owner;
    internal byte Slot;
    internal bool Removed;
    internal float Condition;
    internal ItemLocation Location;

    internal SharedItemState ToWire(SessionPeers peers) => new()
    {
        Id = Id, Revision = Revision, Sample = Sample, Definition = Definition, Owner = peers.AddressOf(Owner),
        Slot = Slot, Removed = Removed, Condition = Condition, Location = Location
    };

    internal static bool TryFromWire(SharedItemState wire, SessionPeers peers, out ItemState state)
    {
        var owner = peers.Identify(wire.Owner);
        state = new ItemState
        {
            Id = wire.Id, Revision = wire.Revision, Sample = wire.Sample, Definition = wire.Definition, Owner = owner,
            Slot = wire.Slot, Removed = wire.Removed, Condition = wire.Condition, Location = wire.Location
        };
        return wire.Owner == 0 || !owner.IsNone;
    }
}
