using System;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

internal struct ItemState
{
    internal ulong Id;
    internal uint Revision;
    internal uint Sample;
    internal ulong Sequence;
    internal string Definition;
    internal ParticipantId Owner;
    internal ulong Parent;
    internal byte Slot;
    internal ulong ClientKey;
    internal ItemPlacement Placement;
    internal bool Removed;
    internal float Condition;
    internal ItemLocation Location;
    internal byte[] Payload;

    internal SharedItemState ToWire(SessionPeers peers) => new()
    {
        Id = Id, Revision = Revision, Sample = Sample, Definition = Definition, Owner = peers.AddressOf(Owner),
        Parent = Parent, Slot = Slot, ClientKey = ClientKey, Placement = Placement, Removed = Removed,
        Condition = Condition, Location = Location, Payload = Payload ?? Array.Empty<byte>()
    };

    internal SharedItemState ToMotionWire(SessionPeers peers)
    {
        SharedItemState wire = ToWire(peers);
        wire.Payload = Array.Empty<byte>();
        return wire;
    }

    internal static bool TryFromWire(SharedItemState wire, SessionPeers peers, out ItemState state)
    {
        ParticipantId owner = wire.Placement is ItemPlacement.Inventory or ItemPlacement.Worn
            ? peers.Identify(wire.Owner) : default;
        state = new ItemState
        {
            Id = wire.Id, Revision = wire.Revision, Sample = wire.Sample, Definition = wire.Definition, Owner = owner,
            Parent = wire.Parent, Slot = wire.Slot, ClientKey = wire.ClientKey, Placement = wire.Placement,
            Removed = wire.Removed, Condition = wire.Condition, Location = wire.Location,
            Payload = wire.Payload ?? Array.Empty<byte>()
        };
        return (wire.Owner == 0 || !owner.IsNone) && wire.Payload is { Length: <= ItemWire.MaxPayloadLength };
    }
}
