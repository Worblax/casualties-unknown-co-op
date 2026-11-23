using System;
using System.Collections.Generic;
using CasualtiesCoop.Networking;
using UnityEngine;

namespace CasualtiesCoop.Game;

internal sealed class SandboxItemObjects
{
    private readonly Dictionary<string, GameObject?> prefabs = new(StringComparer.Ordinal);

    internal bool Supports(Item item) => GetPrefab(item.id);

    private GameObject? GetPrefab(string definition)
    {
        if (!ItemWire.IsDefinition(definition) || !Item.GlobalItems.TryGetValue(definition, out ItemInfo info) ||
            info.wearable || info is LiquidItemInfo)
        {
            return null;
        }

        if (prefabs.TryGetValue(definition, out GameObject? cached))
        {
            return cached;
        }

        GameObject prefab = Resources.Load<GameObject>(definition);
        if (!prefab || !prefab.GetComponent<Item>() || !prefab.GetComponent<Rigidbody2D>() ||
            !prefab.GetComponent<Collider2D>() || !prefab.GetComponent<SpriteRenderer>())
        {
            return prefabs[definition] = null;
        }

        // Items with their own behaviours need a separate serializer before they can be shared.
        foreach (MonoBehaviour behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour is not Item)
            {
                return prefabs[definition] = null;
            }
        }

        return prefabs[definition] = prefab;
    }

    internal Item? CreateReplica(SharedItemState state)
    {
        GameObject? prefab = GetPrefab(state.Definition);
        if (!prefab)
        {
            return null;
        }

        ItemLocation at = state.Location;
        GameObject instance = UnityEngine.Object.Instantiate(prefab!, new Vector3(at.X, at.Y, 0),
            Quaternion.Euler(0, 0, at.Angle));
        Item item = instance.GetComponent<Item>();
        foreach (MonoBehaviour behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
        {
            behaviour.enabled = false;
        }

        item.rb.bodyType = RigidbodyType2D.Kinematic;
        item.rb.gravityScale = 0;
        foreach (Collider2D collider in instance.GetComponentsInChildren<Collider2D>(true))
        {
            collider.isTrigger = true;
        }

        item.rb.simulated = false;
        return item;
    }

    internal static ItemLocation Read(Item item)
    {
        Vector2 position = item.transform.position;
        Vector2 velocity = item.rb.velocity;
        return new ItemLocation(position.x, position.y, item.transform.eulerAngles.z, velocity.x, velocity.y, item.rb.angularVelocity);
    }

    internal void PutInWorld(Item item, ItemLocation at, bool authoritative)
    {
        item.transform.SetParent(null, true);
        item.transform.localScale = Vector3.one;
        item.transform.SetPositionAndRotation(new Vector3(at.X, at.Y, 0), Quaternion.Euler(0, 0, at.Angle));
        if (GetPrefab(item.id) is { } prefab)
        {
            item.GetComponent<SpriteRenderer>().sortingOrder = prefab.GetComponent<SpriteRenderer>().sortingOrder;
        }
        item.gameObject.SetActive(true);
        item.rb.simulated = true;
        if (authoritative)
        {
            item.rb.velocity = new Vector2(at.VelocityX, at.VelocityY);
            item.rb.angularVelocity = at.Spin;
            item.rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        }
    }
}
