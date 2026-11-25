using CasualtiesCoop.Core.Session;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal interface IRemoteItemVisuals
{
    bool AttachHeldItem(ParticipantId owner, byte slot, Item item);
    bool AttachWornItem(ParticipantId owner, Item item);
    void DetachHeldItem(Item item);
}
