namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemInventory
{
    internal Body? Body { get; set; }

    internal bool Applying { get; private set; }

    internal static bool CanPlaceInSlot(Body actor, Item item, int slot, bool force) =>
        slot >= 0 && slot < actor.slots.Length && slot < ItemWire.NoSlot && !actor.HoldingItem(slot) &&
        (actor.slots[slot].canPickUp || force) && (!item.Stats.onlyHoldInHands || actor.slots[slot].isHand);

    internal bool TryPickUp(Item item, int slot)
    {
        Body? body = Body;
        if (!body || slot < 0 || slot >= body!.slots.Length)
        {
            return false;
        }
        if (body.GetItem(slot) == item)
        {
            return true;
        }
        if (!CanPlaceInSlot(body, item, slot, force: true) || body.HoldingItem(item))
        {
            return false;
        }

        bool active = item.gameObject.activeSelf;
        bool simulated = item.rb.simulated;
        item.gameObject.SetActive(true);
        item.rb.simulated = false;
        Applying = true;
        try
        {
            body.PickUpItem(item, slot, force: true);
            if (body.GetItem(slot) == item)
            {
                return true;
            }

            item.rb.simulated = simulated;
            item.gameObject.SetActive(active);
            return false;
        }
        finally
        {
            Applying = false;
        }
    }

    internal bool HoldsSlot(Item item, int slot) =>
        Body && slot >= 0 && slot < Body!.slots.Length && Body.GetItem(slot) == item;

    internal bool TryDrop(Item item)
    {
        Body? body = Body;
        if (!body || !body!.HoldingItem(item))
        {
            return true;
        }

        Applying = true;
        try
        {
            body.DropItem(item);
            return !body.HoldingItem(item) && !item.transform.parent;
        }
        finally
        {
            Applying = false;
        }
    }
}
