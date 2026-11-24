# Universal item sync handoff

Protocol: 13. Plugin version: 0.1.17.

## Implemented

- One stable network identity per `Item` across world, inventory, worn and container placements.
- Compact binary payload (`ItemStateCodec`) for base `Item` fields, every `[Saveable]` component, `CustomItemBehaviour.state/data`, and `LightItem`.
- Full payload on create/transfer/recovery; unreliable `ItemMotion` carries only physical samples.
- Registration of guest-created items with `ClientKey`; host-issued IDs.
- Container children carry `ParentId`; containers are restored before children.
- State reports from the owning guest are revision-checked; stale reports cannot overwrite newer state.
- Craft reservations use actual vanilla-selected ingredient IDs/revisions. Only the player's own items or reachable floor items are accepted; another player's inventory is rejected.
- One atomic craft commit applies removed/updated/created items. Duplicate commits are no-ops; rejected commits restore the local pre-craft snapshot.
- Open vanilla crafting menu refreshes on network item changes.
- Host-side vanilla crafting still sees tracked shared items. Ingredient filtering to the host-approved set is now guest-only.
- Guest crafting includes tracked projection replicas in vanilla recipe selection even though replica colliders are triggers.
- Consumed ingredients are committed as removed based on the recipe's `destroyItem` flag, not on deferred Unity destruction.
- Newly created craft results are detected through scene objects before `Item.Start` populates `Item.allItems`.
- `ItemStateCodec` can restore nested classes without a parameterless constructor, such as `LiquidStack`.
- Projection replicas not owned by the active test view are excluded from native `Item.Update`; this prevents cross-projection update errors without changing real one-guest behavior.

## Verification actually run

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Verify.ps1` - PASS: Release build, protocol, architecture and virtual-network suites.
- Unity scenarios:
  - `items.concurrent-craft`: [result.json](artifacts/game-tests/8b546a6e75304693bb9b6df39bdcd5c5/result.json)
  - `items.universal-state-transfer`: [result.json](artifacts/game-tests/a2a16c13c27a45b3a0955c8015c1a887/result.json)
  - `items.chunk-state-return`: [result.json](artifacts/game-tests/4eb21c5dc628446592898e6ab4e48e4c/result.json)
  - `items.craft-world-and-belt`: [result.json](artifacts/game-tests/a505a5c31376470091f2c949bba1bb8b/result.json)
  - `items.guest-projection`: [result.json](artifacts/game-tests/0d042ddf4eee4812a5024814d9ac4bb7/result.json)
- `CasualtiesCoop.NetworkChecks` covers the virtual-network item scenarios, including concurrent floor craft, duplicate commit rejection, stale reports, guest registration and foreign inventory rejection.
- The only build warning is NU1900 because the NuGet vulnerability index is unavailable in this environment.

## Unity scenarios

- `tests/scenarios/items/concurrent-craft.json`: two native guest bodies request recipe 0 against the same two floor foliage items in the same tick. Exactly one request is accepted, one is rejected, the authority has exactly one rope and zero foliage, both guest replica stores converge to the same result, and the local camera body is restored after each guest-specific craft operation.
- `tests/scenarios/items/universal-state-transfer.json`: flashlight condition/wet/on-state payload, pistol ammunition/chamber, bottle liquid amount, repeated pickup/drop between two guests.
- `tests/scenarios/items/chunk-state-return.json`: modified flashlight moved far away and back, then checked for state convergence.
- `tests/scenarios/items/craft-world-and-belt.json`: vanilla recipe 0, one ingredient in hand, one on the floor, distance guard for the floor ingredient, both consumed, rope produced, inventory-slot removal checked.

## Test harness changes

- `item.craft` accepts an optional `peer`; guest crafting runs through production `SharedItemReplication` and `SharedItemCrafting` with the guest Body temporarily selected.
- `items.unownedConverged` waits for the authoritative item and every guest replica to agree after a drop.
- `items.craftAcceptedCount`, `items.craftRejectedCount` and `items.craftPeerCount` expose craft decisions observed by the virtual guests.
- `items.authoritativeDefinitionCount`, `items.replicaDefinitionCount` and `items.definitionConverged` verify no duplicated authority state and replica agreement.
- `items.nativeDefinitionCount` and `items.nativeDefinitionInSlots` count native items by definition.
- Final snapshots tolerate a consumed item that no longer has a replica, so a successful scenario cannot lose its result report.

## Known limits

- Steam peer-to-peer was not exercised; Unity runs use virtual transports in one player process.
- The binary codec covers all `[Saveable]` fields plus explicit runtime adapters; unknown non-`[Saveable]` components with mutable state need an adapter.
- Craft commit must fit the 16 KiB frame budget.
- Remote visual simulation remains local; authoritative item state comes from the host.
