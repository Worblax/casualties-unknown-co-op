using System.Collections.Generic;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class GuestItemDiscovery
{
    private const int Capacity = 256;
    private readonly Queue<(Item Item, int InstanceId)> candidates = new();
    private readonly HashSet<int> queued = new();

    internal int Count => candidates.Count;

    internal void Enqueue(Item item)
    {
        if (candidates.Count >= Capacity)
        {
            return;
        }

        int id = item.GetInstanceID();
        if (queued.Add(id))
        {
            candidates.Enqueue((item, id));
        }
    }

    internal Item Dequeue()
    {
        (Item item, int id) = candidates.Dequeue();
        queued.Remove(id);
        return item;
    }
}
