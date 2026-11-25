using System;
using System.Collections.Generic;
using CasualtiesCoop.Networking;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemObjects
{
    private readonly Dictionary<string, GameObject?> prefabs = new(StringComparer.Ordinal);

    internal bool Supports(Item item) => GetPrefab(item.id);
    internal bool SupportsDefinition(string definition) => GetPrefab(definition);

    private GameObject? GetPrefab(string definition)
    {
        if (!ItemWire.IsDefinition(definition) || !Item.GlobalItems.TryGetValue(definition, out _))
        {
            return null;
        }

        if (prefabs.TryGetValue(definition, out GameObject? cached))
        {
            return cached;
        }

        GameObject prefab = Resources.Load<GameObject>(definition);
        if (!prefab || !prefab.GetComponent<Item>())
        {
            return prefabs[definition] = null;
        }

        return prefabs[definition] = prefab;
    }

    internal Item? CreateReplica(string definition, ItemLocation at)
    {
        GameObject? prefab = GetPrefab(definition);
        if (!prefab)
        {
            return null;
        }

        GameObject instance = UnityEngine.Object.Instantiate(prefab!, new Vector3(at.X, at.Y, 0),
            Quaternion.Euler(0, 0, at.Angle));
        Item item = instance.GetComponent<Item>();
        if (!item.rb)
        {
            UnityEngine.Object.Destroy(instance);
            return null;
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

    internal bool Apply(Item item, byte[] payload, float condition, ItemPlacement placement) =>
        ItemStateCodec.TryApply(item, payload, condition, placement);

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
        RestoreWorldPresentation(item);
        if (GetPrefab(item.id) is { } prefab && item.TryGetComponent<SpriteRenderer>(out SpriteRenderer sprite) &&
            prefab.TryGetComponent<SpriteRenderer>(out SpriteRenderer source))
        {
            sprite.sortingOrder = source.sortingOrder;
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

    private void RestoreWorldPresentation(Item item)
    {
        GameObject? prefab = GetPrefab(item.id);
        if (!prefab || !item.rb)
        {
            return;
        }

        if (prefab.TryGetComponent<Rigidbody2D>(out Rigidbody2D sourceBody))
        {
            item.rb.bodyType = sourceBody.bodyType;
            item.rb.gravityScale = sourceBody.gravityScale;
            item.rb.constraints = sourceBody.constraints;
            item.rb.collisionDetectionMode = sourceBody.collisionDetectionMode;
            item.rb.sleepMode = sourceBody.sleepMode;
            item.rb.interpolation = sourceBody.interpolation;
        }
        if (item.TryGetComponent<SpriteRenderer>(out SpriteRenderer sprite))
        {
            sprite.enabled = true;
        }

        Collider2D[] sourceColliders = prefab.GetComponents<Collider2D>();
        Collider2D[] targetColliders = item.GetComponents<Collider2D>();
        for (int index = 0; index < targetColliders.Length; index++)
        {
            targetColliders[index].enabled = sourceColliders.Length > index ? sourceColliders[index].enabled : true;
            if (sourceColliders.Length > index)
            {
                targetColliders[index].isTrigger = sourceColliders[index].isTrigger;
            }
        }
        item.gameObject.layer = prefab.layer;
    }
}
