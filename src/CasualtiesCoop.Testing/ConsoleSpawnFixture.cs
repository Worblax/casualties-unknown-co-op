using System;
using System.Linq;
using CasualtiesCoop.Gameplay.Items;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class ConsoleSpawnFixture
{
    private const string Label = "Co-op fixture: console spawn";
    private const string Definition = "stick";
    private const float Condition = 0.63f;
    private Item? native;
    private Item? observed;
    private ulong observedId;

    internal void Spawn(Body body)
    {
        ConsoleScript.CheckForConsole();
        if (!ConsoleScript.instance) throw new InvalidOperationException("The native console is unavailable.");

        var before = UnityEngine.Object.FindObjectsOfType<Item>().ToHashSet();
        Vector2 at = body.transform.position + new Vector3(3f, 0.5f);
        ConsoleScript.instance.ExecuteCommand(FormattableString.Invariant($"spawn {Definition} {at.x:R},{at.y:R} {Condition:R}"));
        native = UnityEngine.Object.FindObjectsOfType<Item>()
            .SingleOrDefault(item => item.id == Definition && !before.Contains(item));
        if (!native || native!.transform.parent)
        {
            throw new InvalidOperationException("The native console did not create a world item.");
        }
        native.label = Label;
    }

    internal bool Observe(SharedItemReplication items, bool isHost, out object evidence)
    {
        Item[] matches = Item.allItems.Where(item => item && item.gameObject.activeInHierarchy &&
            item.id == Definition && item.label == Label).ToArray();
        Item? item = matches.Length == 1 ? matches[0] : null;
        if (!item || item!.transform.parent || !items.TryGetState(item, out var state))
        {
            evidence = new { active = matches.Length, known = false };
            return false;
        }

        Item? tracked = isHost ? items.TestHostScene!.FindTracked(state.Id) : items.FindReplica(state.Id);
        bool sameNative = isHost || item == native;
        bool stable = observedId == 0 || (state.Id == observedId && observed == item);
        bool valid = tracked == item && sameNative && stable && state.Owner.IsNone && state.Parent == 0 &&
            state.Placement == ItemPlacement.World && Mathf.Abs(state.Condition - Condition) < 0.01f &&
            Mathf.Abs(item.condition - Condition) < 0.01f;
        if (valid)
        {
            observed = item;
            observedId = state.Id;
        }

        evidence = new { active = matches.Length, itemId = state.Id, state.Revision, state.Condition,
            nativeCondition = item.condition, instance = item.GetInstanceID(), sameNative, stable, tracked = tracked == item };
        return valid;
    }
}
