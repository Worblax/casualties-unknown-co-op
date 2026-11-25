using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemInventory
{
    internal Body? Body { get; set; }

    internal bool Applying { get; set; }

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

    internal static bool CanSwapInto(Body actor, Item? item, int slot) =>
        slot >= 0 && slot < actor.slots.Length && slot < ItemWire.NoSlot &&
        (!item || !item!.Stats.onlyHoldInHands || actor.slots[slot].isHand);

    internal bool TrySwapSlots(int first, int second)
    {
        Body? body = Body;
        if (!body || first == second || !CanSwapInto(body!, null, first) || !CanSwapInto(body!, null, second))
        {
            return false;
        }

        Item? left = body!.GetItem(first);
        Item? right = body.GetItem(second);
        if (!CanSwapInto(body, left, second) || !CanSwapInto(body, right, first))
        {
            return false;
        }

        bool wasApplying = Applying;
        Applying = true;
        try
        {
            body.SwapSlots(first, second);
            return body.GetItem(first) == right && body.GetItem(second) == left;
        }
        finally
        {
            Applying = wasApplying;
        }
    }

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

    internal bool TryWear(Item item)
    {
        Body? body = Body;
        if (!body || !item || !item.Stats.wearable)
        {
            return false;
        }
        if (body!.GetWearableBySlotID(item.Stats.wearSlotId) == item)
        {
            return true;
        }

        Applying = true;
        try
        {
            if (body.HoldingItem(item)) body.DropItem(item);
            if (item.TryGetParentContainer(out Container parent)) parent.UnloadItem(item);
            body.WearWearable(item);
            return body!.GetWearableBySlotID(item.Stats.wearSlotId) == item;
        }
        finally
        {
            Applying = false;
        }
    }

    internal bool TryAttach(Item containerItem, Item child)
    {
        if (!containerItem || !containerItem.container || !child)
        {
            return false;
        }
        if (child.transform.parent == containerItem.transform)
        {
            return true;
        }

        Applying = true;
        try
        {
            Container container = containerItem.container;
            child.transform.SetParent(container.transform);
            child.rb.simulated = false;
            child.transform.position = container.transform.position;
            SpriteRenderer sprite = child.GetComponent<SpriteRenderer>();
            sprite.enabled = container.itemsVisible;
            if (container.itemsVisible)
            {
                if (containerItem.TryGetComponent<SpriteRenderer>(out SpriteRenderer parentSprite))
                {
                    sprite.sortingOrder = parentSprite.sortingOrder;
                }
                child.transform.localEulerAngles = new Vector3(0f, 0f, 180f);
            }
            if (child.TryGetComponent<Wearable>(out Wearable wearable)) wearable.ClearSprites();
            Container.UpdateItemLight(child.gameObject, inContainer: !container.itemsVisible);
            return true;
        }
        finally
        {
            Applying = false;
        }
    }
}
