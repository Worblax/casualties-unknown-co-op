using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Session;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal sealed class SharedItemHostScene : IItemWorld
{
    private const float MotionInterval = 0.10f;
    private const float StateInterval = 0.5f;
    private const int StateBudget = 16;
    private const float DiscoveryInterval = 1f;
    private const int DiscoveryBudget = 64;
    private const float PickupRange = 10f;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly PlayerPositionLookup playerPosition;
    private readonly SharedItemInventory inventory;
    private readonly SharedItemObjects objects;
    private readonly IRemoteItemVisuals? remoteVisuals;
    private readonly Dictionary<ulong, (Item Item, int UnityId)> byId = new();
    private readonly Dictionary<int, ulong> byObject = new();
    private readonly Dictionary<ulong, byte[]> payloads = new();
    private readonly List<ulong> removed = new();
    private readonly List<ulong> order = new();
    private int stateCursor;
    private float nextMotion;
    private float nextState;
    private float nextDiscovery;

    internal SharedItemHostScene(SessionPeers peers, PlayerPositionLookup playerPosition, IMonotonicClock clock,
        SharedItemInventory inventory, SharedItemObjects objects, IRemoteItemVisuals? remoteVisuals = null)
    {
        this.peers = peers;
        this.playerPosition = playerPosition;
        this.clock = clock;
        this.inventory = inventory;
        this.objects = objects;
        this.remoteVisuals = remoteVisuals;
        Session = new HostItemSession(peers, this, clock);
        Session.OperationResponded += TraceOperation;
        Session.CraftFailure += message =>
        {
            CoopDiagnostics.Count("Craft", "Host failure");
            CoopDiagnostics.Trace("Craft", message);
        };
    }

    internal HostItemSession Session { get; }
    internal bool TryGetId(Item item, out ulong id) => byObject.TryGetValue(item.GetInstanceID(), out id);
    internal Item? FindTracked(ulong id) => byId.TryGetValue(id, out var tracked) ? tracked.Item : null;
    internal string LastCraftPrepareFailure { get; private set; } = string.Empty;
    internal void NoteCraftPrepareFailure(string reason) => LastCraftPrepareFailure = reason;

    internal void Observe(Item? item)
    {
        if (!item || !item)
        {
            return;
        }
        if (SharedItemProjection.IsReplica(item))
        {
            return;
        }
        if (TryGetId(item!, out ulong id))
        {
            Synchronize(id, item!, force: true);
            return;
        }
        TryRegister(item!);
    }

    internal void MarkDirty(Item? item)
    {
        if (item && TryGetId(item!, out ulong id) && Session.TryGet(id, out ItemState state) &&
            (state.Owner.IsNone || state.Owner == peers.LocalParticipant))
        {
            Synchronize(id, item!, force: true);
        }
    }

    internal void Tick()
    {
        float now = clock.Now;
        if (now >= nextDiscovery)
        {
            nextDiscovery = now + DiscoveryInterval;
            Discover();
        }
        if (now >= nextMotion)
        {
            nextMotion = now + MotionInterval;
            SampleWorld();
        }
        if (now >= nextState)
        {
            nextState = now + StateInterval;
            SynchronizeLocalState();
        }
        foreach (ulong id in removed)
        {
            byId.Remove(id);
            payloads.Remove(id);
            order.Remove(id);
        }
        removed.Clear();
    }

    public bool HasRecipe(int recipe) => recipe >= 0 && recipe < Recipes.recipes.Count;

    public void RemoveItem(ulong id) => RemoveItemSafe(id);

    private void RemoveItemSafe(ulong id, bool destroy = true)
    {
        if (!byId.TryGetValue(id, out var tracked)) return;
        Untrack(id);
        if (!tracked.Item) return;
        if (!destroy)
        {
            tracked.Item.rb.simulated = false;
            try
            {
                tracked.Item.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(tracked.Item.gameObject);
            }
            catch (Exception exception)
            {
                CoopDiagnostics.Trace("Items", $"Reconciled item {id} by hiding it: {exception}");
                if (tracked.Item) tracked.Item.gameObject.SetActive(false);
            }
            return;
        }
        try
        {
            tracked.Item.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(tracked.Item.gameObject);
        }
        catch (Exception exception)
        {
            CoopDiagnostics.Trace("Items", $"Failed to destroy item {id}: {exception}");
            if (tracked.Item) tracked.Item.gameObject.SetActive(false);
        }
    }

    private void Untrack(ulong id)
    {
        if (!byId.TryGetValue(id, out var tracked)) return;
        byId.Remove(id);
        byObject.Remove(tracked.UnityId);
        payloads.Remove(id);
        order.Remove(id);
    }
    public ItemDecision CheckCraftIngredient(ParticipantId player, ulong id)
    {
        ulong root = Session.RootId(id);
        if (!Session.TryGet(root, out ItemState state) || state.Removed || !state.Owner.IsNone ||
            state.Placement != ItemPlacement.World || !byId.TryGetValue(root, out var tracked) || !tracked.Item ||
            !playerPosition(peers.AddressOf(player), out Vector2 position))
        {
            return ItemDecision.Unavailable;
        }
        Vector2 target = tracked.Item.transform.position;
        return CanReach(position, target, 10f, LayerMask.GetMask("Ground"))
            ? ItemDecision.Accepted : ItemDecision.OutOfReach;
    }
    public bool TryApplyState(ulong id, byte[] payload)
    {
        if (!Session.TryGet(id, out ItemState state))
        {
            return false;
        }
        Item? item = EnsureItem(id, state);
        return item && objects.Apply(item!, payload, state.Condition, state.Placement);
    }

    public void ApplyRemoteState(ItemState state)
    {
        if (state.Removed)
        {
            RemoveItemSafe(state.Id);
            return;
        }
        Item? item = EnsureItem(state.Id, state);
        if (!item) return;
        if (state.Placement == ItemPlacement.World && state.Owner.IsNone)
        {
            remoteVisuals?.DetachHeldItem(item);
            objects.PutInWorld(item, state.Location, authoritative: true);
            return;
        }
        if (state.Owner.IsNone || state.Owner == peers.LocalParticipant) return;

        remoteVisuals?.DetachHeldItem(item);
        if (state.Placement == ItemPlacement.Inventory && remoteVisuals?.AttachHeldItem(state.Owner, state.Slot, item) == true)
        {
            item.rb.simulated = false;
            return;
        }
        if (state.Placement == ItemPlacement.Worn && remoteVisuals?.AttachWornItem(state.Owner, item) == true)
        {
            item.rb.simulated = false;
            return;
        }
        if (state.Placement == ItemPlacement.Container && byId.TryGetValue(state.Parent, out var parent) && parent.Item &&
            inventory.TryAttach(parent.Item, item))
        {
            return;
        }
        item.gameObject.SetActive(false);
    }

    public bool TryGetRecipe(int recipe, out CraftRecipePlan? plan)
    {
        plan = null;
        if (recipe < 0 || recipe >= Recipes.recipes.Count)
        {
            return false;
        }
        Recipe source = Recipes.recipes[recipe];
        var ingredients = new CraftIngredientPlan[source.items.Count];
        for (int index = 0; index < source.items.Count; index++)
        {
            RecipeItem item = source.items[index];
            ingredients[index] = new CraftIngredientPlan(item.destroyItem, item.isLiquid, item.specificId,
                item.specific, item.minimumCondition, item.noMaxCondition);
        }
        plan = new CraftRecipePlan
        {
            Index = recipe,
            ResultDefinition = source.result.id,
            ResultContainerDefinition = source.result.isLiquid ? "craftingbottle" : source.result.id,
            ResultIsLiquid = source.result.isLiquid,
            ResultAmount = source.result.amount,
            ResultCondition = source.result.resultCondition,
            DontDrainResultLiquid = source.result.dontDrainResultLiquid,
            Ingredients = ingredients
        };
        return true;
    }

    public bool IsKnownDefinition(string definition) => objects.SupportsDefinition(definition);

    public ItemDecision CheckRegistration(ParticipantId player, SharedItemRegistration registration,
        ItemState? parent)
    {
        if (!objects.SupportsDefinition(registration.Definition))
        {
            return ItemDecision.Unavailable;
        }
        if (registration.Placement == ItemPlacement.Container)
        {
            if (parent is not { } parentState || parentState.Removed || !itemsOwnedBy(parentState.Id, player) ||
                !byId.TryGetValue(parentState.Id, out var tracked) || !tracked.Item ||
                !tracked.Item.TryGetComponent<Container>(out Container container))
            {
                return ItemDecision.Unavailable;
            }
            if (!Item.GlobalItems.TryGetValue(registration.Definition, out ItemInfo info) ||
                info.weight > container.maxWeightPerItem ||
                container.GetHoldingWeight() + info.weight > container.maxWeight)
            {
                return ItemDecision.SlotOccupied;
            }
            if (Session.RootId(parentState.Id) == parentState.Id && parentState.Owner.IsNone &&
                (!playerPosition(peers.AddressOf(player), out Vector2 actor) ||
                 !CanReach(actor, tracked.Item.transform.position, PickupRange + 2f, LayerMask.GetMask("Ground"))))
            {
                return ItemDecision.OutOfReach;
            }
            return ItemDecision.Accepted;
        }
        if (registration.Placement is ItemPlacement.Inventory or ItemPlacement.Worn)
        {
            if (player == peers.LocalParticipant)
            {
                Body? local = inventory.Body;
                if (!local) return ItemDecision.Unavailable;
                if (registration.Placement == ItemPlacement.Inventory &&
                    (registration.Slot >= local.slots.Length || local.HoldingItem(registration.Slot)))
                {
                    return ItemDecision.SlotOccupied;
                }
                return ItemDecision.Accepted;
            }
            ulong address = peers.AddressOf(player);
            if (!peers.TryGetPlayer(address, out LaunchPlayer member) ||
                RemoteCharacterView.LoadCharacter(member.Character) is not { } template)
            {
                return ItemDecision.Unavailable;
            }
            if (registration.Placement == ItemPlacement.Inventory &&
                (registration.Slot >= template.slots.Length || Session.SlotOccupied(player, registration.Slot)))
            {
                return ItemDecision.SlotOccupied;
            }
            return ItemDecision.Accepted;
        }
        if (registration.Placement != ItemPlacement.World) return ItemDecision.Unavailable;
        return playerPosition(peers.AddressOf(player), out Vector2 at) &&
            (at - new Vector2(registration.Location.X, registration.Location.Y)).sqrMagnitude <= 16f * 16f
            ? ItemDecision.Accepted : ItemDecision.OutOfReach;
    }

    public ICraftWorldTransaction? BeginCraft(ItemCraftCommit commit, CraftRecipePlan plan,
        IReadOnlyList<ItemState> ingredients)
    {
        var native = new CraftSceneTransaction(this, commit, plan, ingredients);
        if (native.Prepare())
        {
            return native;
        }
        native.Dispose();
        return null;
    }

    private bool itemsOwnedBy(ulong id, ParticipantId player) => Session.TryGet(id, out ItemState state) &&
        Session.RootId(id) == id ? state.Owner == player : Session.RootId(id) is ulong root && TryOwner(root, player);

    private bool TryOwner(ulong root, ParticipantId player) => Session.TryGet(root, out ItemState state) &&
        (state.Owner == player || state.Owner.IsNone);

    private sealed class CraftSceneTransaction : ICraftWorldTransaction
    {
        // Guests simulate the recipe on their own copy, so a condition can drift by a fraction
        // of a tick before the commit arrives. Payloads still have to match exactly.
        private const float ConditionTolerance = 0.01f;

        private sealed class Snapshot
        {
            internal Item Item = null!;
            internal ulong Id;
            internal ParticipantId Owner;
            internal byte[] Payload = Array.Empty<byte>();
            internal float Condition;
            internal ItemPlacement Placement;
            internal ulong ParentId;
            internal byte Slot;
            internal ItemLocation Location;
            internal Transform? ParentTransform;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3 LocalScale;
            internal bool ActiveSelf;
            internal bool Simulated;
            internal bool Attempted;
            internal byte[] TargetPayload = Array.Empty<byte>();
            internal float TargetCondition;
            internal ItemPlacement TargetPlacement;
        }

        private sealed class ContainerContents
        {
            internal Container Container = null!;
            internal readonly List<Content> Items = new();
        }

        private sealed class Content
        {
            internal Item Item = null!;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3 LocalScale;
            internal bool ActiveSelf;
            internal bool Simulated;
        }

        private readonly SharedItemHostScene scene;
        private readonly ItemCraftCommit commit;
        private readonly CraftRecipePlan plan;
        private readonly IReadOnlyList<ItemState> ingredients;
        private readonly List<Snapshot> updates = new();
        private readonly List<(ulong Id, Item Item)> removals = new();
        private readonly List<(CraftCreatedItem Spec, Item Item)> created = new();
        private readonly List<GameObject> temporary = new();
        private readonly List<(ulong Id, Item Item)> sceneEntries = new();
        private readonly List<ulong> bound = new();
        private readonly List<ContainerContents> containers = new();
        private bool applied;
        private bool confirmed;
        private bool finalized;
        private bool disposed;

        internal CraftSceneTransaction(SharedItemHostScene scene, ItemCraftCommit commit, CraftRecipePlan plan,
            IReadOnlyList<ItemState> ingredients)
        {
            this.scene = scene;
            this.commit = commit;
            this.plan = plan;
            this.ingredients = ingredients;
        }

        internal bool Prepare()
        {
            bool ready = false;
            scene.NoteCraftPrepareFailure(string.Empty);
            try
            {
                Recipe recipe = Recipes.recipes[commit.Recipe];
                if (recipe.items.Count != plan.Ingredients.Length || recipe.items.Count != ingredients.Count)
                {
                    scene.NoteCraftPrepareFailure("recipe ingredient count mismatch");
                    return false;
                }

                var exclusions = new List<Item>();
                for (int index = 0; index < recipe.items.Count; index++)
                {
                    ItemState state = ingredients[index];
                    Item? item = EnsureSceneItem(state);
                    Item? matched = item ? recipe.items[index].GetMatchingItem(new List<Item> { item }, exclusions) : null;
                    if (!item || matched != item)
                    {
                        scene.NoteCraftPrepareFailure($"ingredient {index} does not match the recipe");
                        return false;
                    }
                    if (recipe.items[index].destroyItem && !recipe.items[index].isLiquid) exclusions.Add(item);
                }

                foreach (ulong id in commit.Removed)
                {
                    if (!scene.byId.TryGetValue(id, out var tracked) || !tracked.Item)
                    {
                        scene.NoteCraftPrepareFailure($"removed item {id} is not tracked");
                        return false;
                    }
                    removals.Add((id, tracked.Item));
                }
                foreach (CraftUpdatedItem update in commit.Updated)
                {
                    if (!scene.Session.TryGet(update.Id, out ItemState current))
                    {
                        scene.NoteCraftPrepareFailure($"updated item {update.Id} is not authoritative");
                        return false;
                    }
                    Item? item = EnsureSceneItem(current);
                    if (!item || !ItemStateCodec.CanApply(item, update.Payload))
                    {
                        scene.NoteCraftPrepareFailure($"updated item {update.Id} payload cannot be applied");
                        return false;
                    }
                    Snapshot snapshot = Capture(item, current);
                    snapshot.TargetPayload = update.Payload;
                    snapshot.TargetCondition = update.Condition;
                    snapshot.TargetPlacement = update.HasPlacement ? update.Placement : snapshot.Placement;
                    updates.Add(snapshot);
                }

                if (!ValidateIngredientChanges(recipe))
                {
                    scene.NoteCraftPrepareFailure("ingredient changes do not match the vanilla recipe");
                    return false;
                }

                foreach (CraftCreatedItem spec in commit.Created)
                {
                    if (!scene.objects.SupportsDefinition(spec.Definition))
                    {
                        scene.NoteCraftPrepareFailure($"result definition {spec.Definition} is unavailable");
                        return false;
                    }
                    Item? item = scene.objects.CreateReplica(spec.Definition, spec.Location);
                    if (!item)
                    {
                        scene.NoteCraftPrepareFailure("result replica could not be created");
                        return false;
                    }
                    created.Add((spec, item));
                    item.gameObject.SetActive(false);
                    if (created.Count == 1) CraftFaultInjection.Hit("prepare-result-apply");
                    if (!scene.objects.Apply(item, spec.Payload, spec.Condition, spec.Placement))
                    {
                        scene.NoteCraftPrepareFailure("result payload cannot be applied");
                        return false;
                    }
                    if (created.Count == 1) CraftFaultInjection.Hit("prepare-result");
                }
                if (!ValidateResultChanges())
                {
                    if (scene.LastCraftPrepareFailure.Length == 0)
                        scene.NoteCraftPrepareFailure("result changes do not match the vanilla recipe");
                    return false;
                }
                ready = true;
                return true;
            }
            catch (Exception exception)
            {
                scene.NoteCraftPrepareFailure($"prepare exception: {exception}");
                return false;
            }
            finally
            {
                if (!ready) CleanupPrepared();
            }
        }

        public bool TryApply()
        {
            try
            {
                for (int index = 0; index < updates.Count; index++)
                {
                    Snapshot update = updates[index];
                    // Mark before mutating: Apply can fail after changing some component fields.
                    update.Attempted = true;
                    if (index == 1) CraftFaultInjection.Hit("apply-second-ingredient");
                    if (!scene.objects.Apply(update.Item, update.TargetPayload, update.TargetCondition,
                            update.TargetPlacement))
                    {
                        throw new InvalidOperationException();
                    }
                    if (index == 0) CraftFaultInjection.Hit("apply-first-ingredient");
                }
                for (int index = 0; index < created.Count; index++)
                {
                    var entry = created[index];
                    if (!scene.objects.Apply(entry.Item, entry.Spec.Payload, entry.Spec.Condition, entry.Spec.Placement))
                    {
                        throw new InvalidOperationException();
                    }
                    if (index == 0) CraftFaultInjection.Hit("apply-result");
                }
                applied = true;
                return true;
            }
            catch (Exception)
            {
                Rollback();
                return false;
            }
        }

        public bool Bind(IReadOnlyList<CraftCreatedResult> results, IReadOnlyList<CraftCreatedItem> specs)
        {
            if (results.Count != created.Count || specs.Count != created.Count) return false;
            var bindings = new List<(CraftCreatedResult Result, CraftCreatedItem Spec, Item Item)>();
            foreach (var result in results)
            {
                bool found = false;
                foreach (var entry in created)
                {
                    if (entry.Spec.ClientKey != result.ClientKey) continue;
                    if (scene.byId.ContainsKey(result.Item) || scene.byObject.ContainsKey(entry.Item.GetInstanceID()))
                        return false;
                    bindings.Add((result, entry.Spec, entry.Item));
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            var instances = new HashSet<int>();
            foreach (var binding in bindings)
            {
                int instance = binding.Item.GetInstanceID();
                if (!instances.Add(instance)) return false;
            }
            foreach (var binding in bindings)
            {
                int instance = binding.Item.GetInstanceID();
                scene.byId[binding.Result.Item] = (binding.Item, instance);
                scene.byObject[instance] = binding.Result.Item;
                scene.payloads[binding.Result.Item] = binding.Spec.Payload;
                scene.order.Add(binding.Result.Item);
                bound.Add(binding.Result.Item);
                if (bound.Count == 1) CraftFaultInjection.Hit("bind-id");
            }
            return true;
        }

        public bool Confirm()
        {
            if (!applied) return false;
            if (confirmed) return true;
            try
            {
                CraftFaultInjection.Hit("confirm-start");
                foreach (var entry in created)
                {
                    if (!entry.Item) throw new InvalidOperationException("Craft result disappeared before confirmation.");
                    if (entry.Spec.Placement == ItemPlacement.World)
                    {
                        scene.objects.PutInWorld(entry.Item, entry.Spec.Location, authoritative: true);
                    }
                    else
                    {
                        entry.Item.gameObject.SetActive(false);
                    }
                }
                CraftFaultInjection.Hit("confirm-placement");

                // Container unloading is the last fallible step. The snapshot lets Rollback
                // restore a partially unloaded container.
                foreach (var removal in removals)
                {
                    if (!scene.byId.TryGetValue(removal.Id, out var tracked) || !tracked.Item ||
                        !tracked.Item.TryGetComponent<Container>(out Container container))
                    {
                        continue;
                    }
                    containers.Add(CaptureContents(container));
                    CraftFaultInjection.Hit("confirm-unload");
                    container.UnloadAllItems();
                    CraftFaultInjection.Hit("confirm-unload-after");
                }
                confirmed = true;
                return true;
            }
            catch (Exception exception)
            {
                CoopDiagnostics.Trace("Craft", $"Native confirmation failed: {exception}");
                Rollback();
                return false;
            }
        }

        public void FinalizeCommit()
        {
            if (!confirmed || finalized) return;
            finalized = true;
            foreach (var removal in removals)
            {
                try
                {
                    CraftFaultInjection.Hit("finalize-destroy");
                    scene.RemoveItemSafe(removal.Id);
                }
                catch (Exception exception)
                {
                    // Destruction is irreversible. Reconcile by making any survivor inert and
                    // untracked so the registry and playable world cannot diverge.
                    CoopDiagnostics.Trace("Craft", $"Native destruction failed for {removal.Id}: {exception}");
                    scene.RemoveItemSafe(removal.Id, destroy: false);
                }
            }
            applied = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (confirmed && !finalized) FinalizeCommit();
            if (!confirmed) Rollback();
            CleanupPrepared();
        }

        private void Rollback()
        {
            for (int index = updates.Count - 1; index >= 0; index--)
            {
                if (!updates[index].Attempted) continue;
                Restore(updates[index]);
                updates[index].Attempted = false;
            }
            for (int index = containers.Count - 1; index >= 0; index--) RestoreContents(containers[index]);
            containers.Clear();
            foreach (ulong id in bound) scene.Untrack(id);
            bound.Clear();
            applied = false;
        }

        private Item? EnsureSceneItem(ItemState state)
        {
            bool existed = scene.byId.TryGetValue(state.Id, out var tracked) && tracked.Item;
            Item? item = scene.EnsureItem(state.Id, state);
            if (item && !existed && scene.byId.TryGetValue(state.Id, out var entry))
            {
                sceneEntries.Add((state.Id, item));
            }
            return item;
        }

        private Snapshot Capture(Item item, ItemState state)
        {
            (ItemPlacement placement, ParticipantId owner, byte slot) = scene.Placement(item);
            var snapshot = new Snapshot
            {
                Item = item,
                Id = state.Id,
                Owner = state.Owner,
                Payload = ItemStateCodec.Capture(item),
                Condition = item.condition,
                Placement = placement,
                ParentId = scene.ParentId(item),
                Slot = slot,
                Location = SharedItemObjects.Read(item),
                ParentTransform = item.transform.parent,
                LocalPosition = item.transform.localPosition,
                LocalRotation = item.transform.localRotation,
                LocalScale = item.transform.localScale,
                ActiveSelf = item.gameObject.activeSelf,
                Simulated = item.rb ? item.rb.simulated : false
            };
            return snapshot;
        }

        private void Restore(Snapshot snapshot)
        {
            Item item = snapshot.Item;
            if (!item) return;

            if (item.TryGetParentContainer(out Container oldParent)) oldParent.UnloadItem(item, null);
            scene.inventory.TryDrop(item);
            scene.objects.Apply(item, snapshot.Payload, snapshot.Condition, snapshot.Placement);

            bool placed = snapshot.Placement switch
            {
                ItemPlacement.World => RestoreWorld(snapshot),
                ItemPlacement.Inventory => RestoreInventory(snapshot),
                ItemPlacement.Worn => RestoreWorn(snapshot),
                ItemPlacement.Container => RestoreContainer(snapshot),
                _ => false
            };
            if (!placed)
            {
                if (snapshot.ParentTransform)
                {
                    item.transform.SetParent(snapshot.ParentTransform, false);
                    item.transform.localPosition = snapshot.LocalPosition;
                    item.transform.localRotation = snapshot.LocalRotation;
                    item.transform.localScale = snapshot.LocalScale;
                }
                else
                {
                    item.transform.SetParent(null, true);
                    item.transform.SetPositionAndRotation(new Vector3(snapshot.Location.X, snapshot.Location.Y, 0),
                        Quaternion.Euler(0, 0, snapshot.Location.Angle));
                    item.transform.localScale = snapshot.LocalScale;
                }
            }
            item.gameObject.SetActive(snapshot.ActiveSelf);
            if (item.rb) item.rb.simulated = snapshot.Simulated;
        }

        private bool RestoreWorld(Snapshot snapshot)
        {
            scene.objects.PutInWorld(snapshot.Item, snapshot.Location, authoritative: true);
            return true;
        }

        private bool RestoreInventory(Snapshot snapshot)
        {
            if (snapshot.Owner == scene.peers.LocalParticipant)
            {
                return scene.inventory.TryPickUp(snapshot.Item, snapshot.Slot);
            }
            snapshot.Item.transform.SetParent(null, true);
            snapshot.Item.gameObject.SetActive(false);
            if (snapshot.Item.rb) snapshot.Item.rb.simulated = false;
            return true;
        }

        private bool RestoreWorn(Snapshot snapshot) =>
            snapshot.Owner == scene.peers.LocalParticipant && scene.inventory.TryWear(snapshot.Item);

        private bool RestoreContainer(Snapshot snapshot)
        {
            if (!scene.byId.TryGetValue(snapshot.ParentId, out var parent) || !parent.Item) return false;
            return scene.inventory.TryAttach(parent.Item, snapshot.Item);
        }

        private static ContainerContents CaptureContents(Container container)
        {
            var snapshot = new ContainerContents { Container = container };
            foreach (Transform child in container.transform)
            {
                if (!child.TryGetComponent<Item>(out Item item)) continue;
                snapshot.Items.Add(new Content
                {
                    Item = item,
                    LocalPosition = item.transform.localPosition,
                    LocalRotation = item.transform.localRotation,
                    LocalScale = item.transform.localScale,
                    ActiveSelf = item.gameObject.activeSelf,
                    Simulated = item.rb ? item.rb.simulated : false
                });
            }
            return snapshot;
        }

        private void RestoreContents(ContainerContents snapshot)
        {
            if (!snapshot.Container) return;
            Item? containerItem = snapshot.Container.GetComponent<Item>();
            foreach (Content content in snapshot.Items)
            {
                if (!content.Item) continue;
                if (containerItem)
                {
                    scene.inventory.TryAttach(containerItem, content.Item);
                }
                else
                {
                    content.Item.transform.SetParent(snapshot.Container.transform, false);
                }
                content.Item.transform.localPosition = content.LocalPosition;
                content.Item.transform.localRotation = content.LocalRotation;
                content.Item.transform.localScale = content.LocalScale;
                content.Item.gameObject.SetActive(content.ActiveSelf);
                if (content.Item.rb) content.Item.rb.simulated = content.Simulated;
            }
        }

        private bool ValidateIngredientChanges(Recipe recipe)
        {
            var updatesById = new Dictionary<ulong, CraftUpdatedItem>();
            foreach (CraftUpdatedItem update in commit.Updated) updatesById[update.Id] = update;
            var reusable = new HashSet<ulong>();
            for (int index = 0; index < ingredients.Count; index++)
            {
                if (!plan.Ingredients[index].EffectiveDestroy) reusable.Add(ingredients[index].Id);
            }

            foreach (ulong id in reusable)
            {
                if (!updatesById.TryGetValue(id, out CraftUpdatedItem expected) ||
                    expected.Condition is < -ConditionTolerance or > 1f ||
                    !scene.byId.TryGetValue(id, out var tracked) || !tracked.Item)
                    return false;
                GameObject cloneObject = UnityEngine.Object.Instantiate(tracked.Item.gameObject);
                cloneObject.SetActive(false);
                cloneObject.transform.SetParent(null, true);
                temporary.Add(cloneObject);
                if (temporary.Count == 1) CraftFaultInjection.Hit("prepare-temp-copy");
                if (!cloneObject.TryGetComponent<Item>(out Item clone)) return false;
                ItemState initial = default;
                for (int index = 0; index < ingredients.Count; index++)
                {
                    if (ingredients[index].Id == id) { initial = ingredients[index]; break; }
                }
                if (!scene.objects.Apply(clone, initial.Payload, initial.Condition, initial.Placement)) return false;
                for (int index = 0; index < recipe.items.Count; index++)
                {
                    if (ingredients[index].Id == id && !plan.Ingredients[index].EffectiveDestroy)
                        recipe.items[index].UseItem(clone);
                }
                if (!clone) return false;
                byte[] actual = ItemStateCodec.Capture(clone);
                if (Math.Abs(clone.condition - expected.Condition) > ConditionTolerance ||
                    !actual.AsSpan().SequenceEqual(expected.Payload))
                    return false;
            }
            return true;
        }

        private bool ValidateResultChanges()
        {
            var reservationIds = new HashSet<ulong>(ingredients.Select(value => value.Id));
            int liquidTargets = 0;
            foreach (CraftUpdatedItem update in commit.Updated)
            {
                if (reservationIds.Contains(update.Id) || commit.Removed.Contains(update.Id)) continue;
                Snapshot? original = updates.FirstOrDefault(value => value.Id == update.Id);
                if (original is null || !original.Item) continue;
                if (original.Payload.AsSpan().SequenceEqual(update.Payload)) continue;
                WaterContainerItem? water = original.Item.GetComponent<WaterContainerItem>();
                if (!water) continue;
                if (!plan.ResultIsLiquid || ++liquidTargets > 1) return false;
                if (!ValidateLiquidTarget(original, update)) return false;
            }

            if (plan.ResultIsLiquid)
            {
                if (created.Count > 1) return false;
                if (created.Count == 0) return liquidTargets == 1;
                if (liquidTargets != 0) return false;
                var entry = created[0];
                if (!ValidateCreatedLiquid(entry.Spec, entry.Item)) return false;
            }

            foreach (var entry in created)
            {
                if (!plan.ResultIsLiquid && !ValidateCreatedSolid(entry.Spec)) return false;
            }
            return true;
        }

        private bool ValidateLiquidTarget(Snapshot original, CraftUpdatedItem update)
        {
            GameObject? cloneObject = null;
            try
            {
                cloneObject = UnityEngine.Object.Instantiate(original.Item.gameObject);
                cloneObject.SetActive(false);
                cloneObject.transform.SetParent(null, true);
                temporary.Add(cloneObject);
                if (!cloneObject.TryGetComponent<Item>(out Item clone) ||
                    !clone.TryGetComponent<WaterContainerItem>(out WaterContainerItem water)) return false;
                if (!scene.objects.Apply(clone, original.Payload, original.Condition, original.Placement)) return false;
                float before = water.CurrentTotal;
                if (!scene.objects.Apply(clone, update.Payload, update.Condition, update.Placement)) return false;
                float after = water.CurrentTotal;
                float added = after - before;
                if (!IsResultAmountValid(added)) return false;
                if (!scene.objects.Apply(clone, original.Payload, original.Condition, original.Placement)) return false;
                water.AddLiquid(plan.ResultDefinition, added);
                if (Math.Abs(clone.condition - update.Condition) > 0.0001f) return false;
                return ItemStateCodec.Capture(clone).AsSpan().SequenceEqual(update.Payload);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool ValidateCreatedLiquid(CraftCreatedItem spec, Item item)
        {
            if (spec.Definition != plan.ResultContainerDefinition ||
                float.IsNaN(spec.Condition) || spec.Condition < 0f || spec.Condition > 1f)
            {
                scene.NoteCraftPrepareFailure($"liquid result definition/condition is invalid: " +
                    $"{spec.Definition} condition={spec.Condition}");
                return false;
            }
            if (!item.TryGetComponent<WaterContainerItem>(out WaterContainerItem water) ||
                water.stack.Count != 1 || water.stack[0].liquidId != plan.ResultDefinition ||
                !IsResultAmountValid(water.CurrentTotal))
            {
                scene.NoteCraftPrepareFailure($"liquid result stack is invalid for {spec.Definition}: " +
                    $"stacks={(water ? water.stack.Count : -1)} amount={(water ? water.CurrentTotal : -1f)}");
                return false;
            }
            float amount = water.CurrentTotal;
            bool matches = MatchesFreshResult(spec, baseline => baseline.GetComponent<WaterContainerItem>()
                .AddLiquid(plan.ResultDefinition, amount));
            if (!matches)
            {
                scene.NoteCraftPrepareFailure($"liquid result payload does not match a fresh {spec.Definition}");
            }
            return matches;
        }

        private bool ValidateCreatedSolid(CraftCreatedItem spec)
        {
            if (!IsResultConditionValid(spec.Condition)) return false;
            return MatchesFreshResult(spec, baseline =>
            {
                if (baseline.TryGetComponent<BatteryItem>(out BatteryItem battery)) battery.UnloadBattery(true);
                if (!plan.DontDrainResultLiquid && baseline.TryGetComponent<WaterContainerItem>(out WaterContainerItem water))
                {
                    water.stack = new List<LiquidStack>();
                    baseline.condition = 0f;
                }
            });
        }

        private bool MatchesFreshResult(CraftCreatedItem spec, Action<Item> shape)
        {
            Item? baseline = scene.objects.CreateReplica(spec.Definition, spec.Location);
            if (!baseline) return false;
            try
            {
                baseline.condition = spec.Condition;
                shape(baseline);
                return ItemStateCodec.Capture(baseline).AsSpan().SequenceEqual(spec.Payload);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (baseline) UnityEngine.Object.Destroy(baseline.gameObject);
            }
        }

        private bool IsResultAmountValid(float amount)
        {
            if (!(amount > 0f) || float.IsNaN(amount) || float.IsInfinity(amount)) return false;
            float maximum = Math.Max(0f, plan.ResultCondition);
            return amount <= maximum + 0.0001f && (maximum <= 0.0001f || amount >= maximum * 0.19f);
        }

        private bool IsResultConditionValid(float condition)
        {
            if (float.IsNaN(condition) || condition < 0f || condition > 1f) return false;
            float maximum = Math.Max(0f, plan.ResultCondition);
            return condition <= maximum + 0.0001f &&
                (maximum <= 0.0001f || condition >= maximum * 0.19f || Math.Abs(condition - maximum) <= 0.0001f);
        }

        private void CleanupPrepared()
        {
            if (!confirmed)
            {
                foreach (ulong id in bound) scene.Untrack(id);
                bound.Clear();
                foreach (var entry in sceneEntries)
                {
                    scene.Untrack(entry.Id);
                    if (entry.Item)
                    {
                        entry.Item.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(entry.Item.gameObject);
                    }
                }
                sceneEntries.Clear();
                foreach (var entry in created)
                {
                    if (!entry.Item) continue;
                    entry.Item.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(entry.Item.gameObject);
                }
                created.Clear();
            }
            else
            {
                bound.Clear();
                sceneEntries.Clear();
                created.Clear();
            }
            foreach (GameObject clone in temporary)
            {
                if (clone) UnityEngine.Object.Destroy(clone);
            }
            temporary.Clear();
            updates.Clear();
            removals.Clear();
            containers.Clear();
        }
    }
    private void Discover()
    {
        int examined = 0;
        foreach (Item item in Item.allItems)
        {
            if (examined++ >= DiscoveryBudget || !Session.HasCapacity)
            {
                break;
            }
            if (!item || SharedItemProjection.IsReplica(item) || TryGetId(item, out _))
            {
                continue;
            }
            TryRegister(item);
        }
    }

    private bool TryRegister(Item item)
    {
        if (!objects.Supports(item))
        {
            return false;
        }

        ulong parent = 0;
        if (item.TryGetParentContainer(out Container container) && container.TryGetComponent<Item>(out Item parentItem))
        {
            if (!TryGetId(parentItem, out parent) && !TryRegister(parentItem))
            {
                return false;
            }
        }

        (ItemPlacement placement, ParticipantId owner, byte slot) = Placement(item);
        byte[] payload = ItemStateCodec.Capture(item);
        if (!Session.TryRegister(item.id, placement, owner, parent, slot, SharedItemObjects.Read(item), item.condition,
            payload, 0, out ItemState state))
        {
            return false;
        }

        int instance = item.GetInstanceID();
        byId[state.Id] = (item, instance);
        byObject[instance] = state.Id;
        payloads[state.Id] = payload;
        order.Add(state.Id);
        return true;
    }

    private void Synchronize(ulong id, Item item, bool force)
    {
        if (!Session.TryGet(id, out ItemState state) || state.Removed || !state.Owner.IsNone &&
            state.Owner != peers.LocalParticipant)
        {
            return;
        }

        byte[] payload = ItemStateCodec.Capture(item);
        (ItemPlacement placement, ParticipantId owner, byte slot) = Placement(item);
        ulong parent = ParentId(item);
        if (placement != state.Placement || owner != state.Owner || slot != state.Slot || parent != state.Parent)
        {
            if (Session.TryCommitPlacement(id, placement, owner, parent, slot, SharedItemObjects.Read(item),
                item.condition, payload, out state))
            {
                payloads[id] = payload;
            }
            return;
        }

        if ((force || !payloads.TryGetValue(id, out byte[]? previous) || !previous.SequenceEqual(payload) ||
             Math.Abs(item.condition - state.Condition) > 0.001f) &&
            Session.TryCommitPayload(id, state.Revision, item.condition, payload, out state))
        {
            payloads[id] = payload;
        }
    }

    private void SampleWorld()
    {
        foreach (var entry in byId)
        {
            Item item = entry.Value.Item;
            if (!item)
            {
                byObject.Remove(entry.Value.UnityId);
                Session.Remove(entry.Key);
                removed.Add(entry.Key);
                continue;
            }
            if (Session.TryGet(entry.Key, out ItemState state) && state.Placement == ItemPlacement.World &&
                state.Owner.IsNone)
            {
                Session.Sample(entry.Key, SharedItemObjects.Read(item), item.condition);
            }
        }
    }

    private void SynchronizeLocalState()
    {
        int inspected = 0;
        while (inspected < StateBudget && order.Count > 0)
        {
            if (stateCursor >= order.Count) stateCursor = 0;
            ulong id = order[stateCursor++];
            inspected++;
            if (!byId.TryGetValue(id, out var entry) || !entry.Item ||
                !Session.TryGet(id, out ItemState state) || state.Removed ||
                !state.Owner.IsNone && state.Owner != peers.LocalParticipant)
            {
                continue;
            }
            Synchronize(id, entry.Item, force: false);
        }
    }

    private (ItemPlacement Placement, ParticipantId Owner, byte Slot) Placement(Item item)
    {
        Body? body = inventory.Body;
        if (body)
        {
            for (int slot = 0; slot < body!.slots.Length; slot++)
            {
                if (body.GetItem(slot) == item)
                {
                    return (ItemPlacement.Inventory, peers.LocalParticipant, (byte)slot);
                }
            }
            if (body.GetAllWearables().Contains(item))
            {
                return (ItemPlacement.Worn, peers.LocalParticipant, ItemWire.NoSlot);
            }
        }
        if (item.TryGetParentContainer(out _))
        {
            return (ItemPlacement.Container, default, ItemWire.NoSlot);
        }
        return (ItemPlacement.World, default, ItemWire.NoSlot);
    }

    private ulong ParentId(Item item)
    {
        if (!item.TryGetParentContainer(out Container container) || !container.TryGetComponent<Item>(out Item parent))
        {
            return 0;
        }
        return TryGetId(parent, out ulong id) ? id : 0;
    }

    private Item? EnsureItem(ulong id, ItemState state)
    {
        if (byId.TryGetValue(id, out var tracked) && tracked.Item)
        {
            return tracked.Item;
        }
        Item? item = objects.CreateReplica(state.Definition, state.Location);
        if (!item)
        {
            return null;
        }
        if (!objects.Apply(item, state.Payload, state.Condition, state.Placement))
        {
            UnityEngine.Object.Destroy(item.gameObject);
            return null;
        }
        item!.gameObject.SetActive(false);
        int instance = item.GetInstanceID();
        byId[id] = (item, instance);
        byObject[instance] = id;
        payloads[id] = state.Payload;
        order.Add(id);
        return item;
    }

    internal bool SpawnAlcohol(ulong player, Vector2 at, byte slot)
    {
        if (!Session.HasCapacity || !Resources.Load<GameObject>("alcohol") ||
            !playerPosition(player, out Vector2 actor) || (actor - at).sqrMagnitude > 16 * 16)
        {
            return false;
        }

        GameObject bottle = Utils.Create("alcohol", at, 0);
        if (!bottle || !bottle.TryGetComponent<Item>(out Item item))
        {
            if (bottle) UnityEngine.Object.Destroy(bottle);
            return false;
        }
        item.SetCondition(1f);
        if (!TryRegister(item))
        {
            UnityEngine.Object.Destroy(bottle);
            return false;
        }

        if (slot != ItemWire.NoSlot && TryGetId(item, out ulong id) && (actor - at).sqrMagnitude <= 2.5f * 2.5f)
        {
            var pickup = new SharedItemRequest { ItemId = id, Revision = 1, Action = ItemAction.PickUp, Slot = slot };
            Session.ExecuteForParticipant(peers.Identify(player), pickup);
        }
        return true;
    }

    internal bool HandleWorldUpdate(Item item)
    {
        if (!TryGetId(item, out ulong id) || !Session.TryGet(id, out ItemState state) || !state.Owner.IsNone ||
            state.Removed || state.Placement != ItemPlacement.World)
        {
            return true;
        }

        WorldGeneration world = WorldGeneration.world;
        Vector2 position = item.transform.position;
        bool worldReady = world && world.worldExists;
        if (worldReady && world.GetClosestChunkRenderer(world.WorldToBlockPos(position)).enabled)
        {
            return true;
        }

        bool active = worldReady && Time.timeScale <= 5f && IsNearAPlayer(position);
        item.rb.simulated = active;
        if (item.affect)
        {
            item.affect.enabled = active;
        }
        if (active)
        {
            if (item.condition <= 0f && item.Stats.destroyAtZeroCondition)
            {
                UnityEngine.Object.Destroy(item.gameObject);
            }
            else
            {
                item.HandleDecay();
            }
        }
        return false;
    }

    public ItemDecision CheckPickup(ParticipantId player, ulong id, byte slot, ItemLocation? previousPosition)
    {
        ulong address = peers.AddressOf(player);
        if (!byId.TryGetValue(id, out var tracked) || !tracked.Item || tracked.Item.transform.parent ||
            !playerPosition(address, out Vector2 position))
        {
            return ItemDecision.Unavailable;
        }

        Item item = tracked.Item;
        Vector2 target = item.transform.position;
        if (item.TryGetComponent<Collider2D>(out Collider2D collider))
        {
            Bounds bounds = collider.bounds;
            if (bounds.size != Vector3.zero) target = bounds.center;
        }
        int ground = LayerMask.GetMask("Ground");
        bool local = player == peers.LocalParticipant;
        float distance = PickupRange + (local ? 0f : 1.5f);
        if (!CanReach(position, target, distance, ground) &&
            (local || previousPosition is not { } previous || !CanReach(position, new Vector2(previous.X, previous.Y), distance, ground)))
        {
            return ItemDecision.OutOfReach;
        }

        return peers.TryGetPlayer(address, out LaunchPlayer member) &&
            RemoteCharacterView.LoadCharacter(member.Character) is { } template && slot < template.slots.Length &&
            (!item.Stats.onlyHoldInHands || template.slots[slot].isHand) ? ItemDecision.Accepted : ItemDecision.SlotOccupied;
    }

    public bool TryPickUp(ParticipantId player, ulong id, byte slot, out float condition)
    {
        Item item = byId[id].Item;
        condition = item.condition;
        if (player == peers.LocalParticipant)
        {
            if (!inventory.TryPickUp(item, slot))
            {
                return false;
            }
        }
        else
        {
            item.rb.simulated = false;
            if (remoteVisuals?.AttachHeldItem(player, slot, item) != true)
            {
                item!.gameObject.SetActive(false);
            }
        }

        condition = item.condition;
        return true;
    }

    public ItemDecision Drop(ParticipantId player, ulong id, SharedItemRequest request, out ItemLocation location,
        out float condition)
    {
        location = default;
        condition = 0;
        if (!Session.TryGet(id, out ItemState state) || !playerPosition(peers.AddressOf(player), out Vector2 position))
        {
            return ItemDecision.Unavailable;
        }
        ItemLocation requested = request.Location;
        Vector2 at = new(requested.X, requested.Y);
        if ((position - at).sqrMagnitude > 8f * 8f)
        {
            return ItemDecision.OutOfReach;
        }

        Item? item = EnsureItem(id, state);
        if (!item)
        {
            return ItemDecision.Unavailable;
        }
        bool local = player == peers.LocalParticipant;
        if (local && (!inventory.HoldsSlot(item!, request.Slot) || !inventory.TryDrop(item!)))
        {
            return ItemDecision.Unavailable;
        }

        byte[] payload = request.Payload.Length > 0 ? request.Payload : state.Payload;
        remoteVisuals?.DetachHeldItem(item!);
        if (!objects.Apply(item!, payload, state.Condition, ItemPlacement.World))
        {
            return ItemDecision.Unavailable;
        }
        Vector2 velocity = Vector2.ClampMagnitude(new Vector2(requested.VelocityX, requested.VelocityY), 100f);
        float spin = request.Action == ItemAction.Throw ? UnityEngine.Random.Range(-800f, 800f) : 0f;
        location = new ItemLocation(at.x, at.y, requested.Angle, velocity.x, velocity.y, spin);
        objects.PutInWorld(item!, location, authoritative: true);
        AttachRegisteredContents(item!, id, payload);
        condition = item!.condition;
        return ItemDecision.Accepted;
    }

    private void AttachRegisteredContents(Item parent, ulong parentId, byte[] parentPayload)
    {
        foreach (ItemState child in Session.Snapshot().Where(state => state.Parent == parentId && !state.Removed))
        {
            Item? item = EnsureItem(child.Id, child);
            if (item && objects.Apply(item!, child.Payload, child.Condition, ItemPlacement.Container) &&
                inventory.TryAttach(parent, item!))
            {
                AttachRegisteredContents(item!, child.Id, child.Payload);
            }
        }
    }

    private static void TraceOperation(SharedItemRequest request, ItemDecision decision, bool replay)
    {
        if (!CoopDiagnostics.Enabled)
        {
            return;
        }

        if (replay)
        {
            CoopDiagnostics.Count("Items", "Request replay");
        }
        else
        {
            CoopDiagnostics.Trace("Items", $"{request.Action} #{request.Operation}, item {request.ItemId}, revision {request.Revision}: {decision}.");
        }
    }

    private bool IsNearAPlayer(Vector2 at)
    {
        foreach (LaunchPlayer player in peers.Players)
        {
            if (playerPosition(player.SteamId, out Vector2 position) && (position - at).sqrMagnitude <= 60f * 60f)
            {
                return true;
            }
        }
        return false;
    }

    private static bool CanReach(Vector2 actor, Vector2 item, float distance, int ground) =>
        (actor - item).sqrMagnitude <= distance * distance &&
        (!Physics2D.Linecast(actor, item, ground) || Physics2D.OverlapPoint(item, ground));
}
