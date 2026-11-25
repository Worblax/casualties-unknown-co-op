using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

// A remote visual must never participate in physics or world interaction. Collider state is
// restored when the item leaves the remote character and becomes a world/inventory object again.
internal sealed class HeldItemVisualState : MonoBehaviour
{
    private bool[] colliderState = System.Array.Empty<bool>();

    internal static void Disable(Item item)
    {
        if (!item) return;
        HeldItemVisualState state = item.GetComponent<HeldItemVisualState>() ??
            item.gameObject.AddComponent<HeldItemVisualState>();
        state.Capture();
    }

    internal static void Restore(Item item)
    {
        if (!item) return;
        if (item.TryGetComponent<HeldItemVisualState>(out HeldItemVisualState state)) state.RestoreAndRemove();
    }

    private void Capture()
    {
        Collider2D[] colliders = GetComponentsInChildren<Collider2D>(true);
        if (colliderState.Length != colliders.Length) colliderState = new bool[colliders.Length];
        for (int index = 0; index < colliders.Length; index++)
        {
            colliderState[index] = colliders[index].enabled;
            colliders[index].enabled = false;
        }
    }

    private void RestoreAndRemove()
    {
        Collider2D[] colliders = GetComponentsInChildren<Collider2D>(true);
        for (int index = 0; index < colliders.Length && index < colliderState.Length; index++)
        {
            colliders[index].enabled = colliderState[index];
        }
        Destroy(this);
    }
}
