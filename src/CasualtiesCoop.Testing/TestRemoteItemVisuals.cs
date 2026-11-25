using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Gameplay.Items;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class TestRemoteItemVisuals : IRemoteItemVisuals, IDisposable
{
    private readonly Dictionary<ParticipantId, RemoteCharacterView> views = new();
    private readonly Dictionary<ParticipantId, Vector2> positions = new();
    internal int AttachAttempts { get; private set; }
    internal int AttachSuccesses { get; private set; }

    internal TestRemoteItemVisuals(IEnumerable<ParticipantId> participants)
    {
        foreach (ParticipantId participant in participants)
        {
            views[participant] = new RemoteCharacterView(0, "remote " + participant.Seat);
            positions[participant] = Vector2.zero;
        }
    }

    internal void SetPosition(ParticipantId participant, Vector2 position) => positions[participant] = position;

    internal void Tick()
    {
        foreach (var pair in views)
            if (positions.TryGetValue(pair.Key, out Vector2 position)) pair.Value.ShowAt(position);
    }

    public bool AttachHeldItem(ParticipantId owner, ulong id, byte slot, Item item)
    {
        bool ok = views.TryGetValue(owner, out RemoteCharacterView? view) && view.AttachHeldItem(id, slot, item);
        AttachAttempts++;
        if (ok) AttachSuccesses++;
        if (!ok) Debug.Log($"test remote attach failed owner={owner.Seat} slot={slot} item={(item ? item.id : "null")}");
        return ok;
    }

    public bool AttachWornItem(ParticipantId owner, Item item) =>
        views.TryGetValue(owner, out RemoteCharacterView? view) && view.AttachWornItem(item);

    public void DetachHeldItem(Item item)
    {
        foreach (RemoteCharacterView view in views.Values) view.DetachHeldItem(item);
    }

    internal int ChildItemCount(ParticipantId owner) =>
        views.TryGetValue(owner, out RemoteCharacterView? view) ? view.ChildItemCount() : -1;

    internal int EnabledChildColliderCount(ParticipantId owner) =>
        views.TryGetValue(owner, out RemoteCharacterView? view) ? view.EnabledChildColliderCount() : -1;

    internal bool HasFiniteChildItemTransform(ParticipantId owner) =>
        views.TryGetValue(owner, out RemoteCharacterView? view) && view.HasFiniteChildItemTransform();

    public void Dispose()
    {
        foreach (RemoteCharacterView view in views.Values) view.Dispose();
        views.Clear();
    }
}
