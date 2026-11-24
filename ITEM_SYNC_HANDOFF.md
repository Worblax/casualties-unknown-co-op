# Universal item sync handoff

Protocol: 15. Plugin version: 0.1.19.

## Current stopping point

P0 craft/registration paths are implemented and exercised. P1/P2 work remains; do not
describe the codec or every vanilla item as universally covered yet.

Verified with:

```powershell
powershell.exe -NoProfile -File ./scripts/Verify.ps1 -DotnetPath 'C:/Program Files/dotnet/dotnet.exe' -GameTests
```

Latest result: Release build, ProtocolChecks, ArchitectureChecks, virtual-network
NetworkChecks and every available Unity game scenario passed. Unity tests must run outside
the sandbox in this environment because they replace DLLs under the Steam installation.

## Landed P0 work

- `AuthoritativeItemRegistry.Transaction` snapshots the full registry for craft rollback.
- `HostCraftReservations` uses the runtime vanilla `Recipe` as the source of truth:
  - ordered ingredient plan, revisions and captured states,
  - states `Requested/Reserved/Executing/Committed/Rejected/Cancelled/Expired`,
  - bounded terminal history for idempotent replay,
  - duplicate/overlapping IDs, wrong results, missing consumption, stale revisions and
    inaccessible result placement are rejected,
  - a reusable non-destroying requirement may name the same instance more than once,
  - repeated detached-child updates are accepted only for a destroyed container.
- `HostItemSession` applies a craft through one native transaction and one registry
  transaction. Capacity, client keys and result placement are preflighted before mutation.
- Native craft transactions pre-validate payloads and placements, create staged replicas,
  defer ingredient destruction until confirmation, and roll back staged updates/results.
- `CraftExecutionScope` observes the actual vanilla call:
  - `RecipeItem.UseItem` boundaries,
  - the exact `UnityEngine.Object.Destroy` call for consumed ingredients,
  - `Utils.Create` results,
  - liquid additions to existing containers.
- The `RecipeResult.SpawnResult(int)` Harmony patch now binds with the correct signature.
  Missing this parameter previously prevented `Utils.Create` observation and the `Item.Update`
  guard from being installed.
- Guest craft commits are retried with the same operation number until a terminal result
  arrives. The reservation request may also be retried. `ItemCraftCancel` releases a
  reservation when vanilla crafting fails locally.
- Guest rejection removes locally created results, restores ingredient payload, condition,
  container/slot/world placement and captured descendants. `Conflict` requests a fresh
  authoritative snapshot instead of restoring an already-superseded copy.
- Registration receipts are per-`ClientKey`, so A -> B -> A is idempotent. Registration checks
  definition support, parent ownership chain, container weight, slot occupancy, world reach
  and cycles/parent validity in the registry path.
- Reservation conflicts include the item, its ancestors and descendants. Local host actions,
  guest actions, registration, state reports, sampling and removal all use the same check.
- Effective-owner (container-chain) reports are used for nested owned-item state. Stale owner
  reports are rejected and answered with current authority.
- Craft commit wire adds updated-item placement/parent/slot/location and carries result
  conditions. Protocol bumped 14 -> 15; plugin 0.1.18 -> 0.1.19.

## P0 regression coverage

`tests/CasualtiesCoop.NetworkChecks/P0CraftChecks.cs` covers concurrent reservations,
native rejection, delete/update overlap, duplicate removal IDs, wrong recipe result, missing
ingredient, local host mutation of a reserved item, reserved nested child blocking its parent,
foreign-container registration, expiry, commit replay after reconnect and A-B-A registration.

`tests/scenarios/items/concurrent-craft.json` now sets deterministic INT for both test bodies
before the recipe, because recipe 0 uses `INT` to decide whether `SpawnResult` can produce rope.
This changes a precondition, not an expected outcome.

## Known P0 limitations

1. A dedicated Unity scenario for a failed vanilla `SpawnResult` rollback still needs to be
   added. The local-rejection path is implemented and logs a warning, but the current Unity
   suite does not force a failed recipe on a real native body.
2. Guest-side rollback of nested containers is implemented from captured replica state, but
   only container unload/placement paths exercised by current scenarios are covered.
3. Hung guest registrations are retried and capped at 256 per process; there is no protocol
   acknowledgement that lets the guest prove a failed registration without a host query.
4. Commit size remains bounded by 64 changed/created entries and the 16 KiB frame limit.
   Composite operations larger than that are not implemented.
5. `tests/CasualtiesCoop.NetworkChecks/P0CraftChecks.cs.draft` is the superseded draft kept
   in the working tree; the compiled `P0CraftChecks.cs` is the active suite.

## Remaining P1

- Grouped craft commit application on guests; craft changes still arrive as separate ItemDelta
  messages.
- Placement reports for arbitrary local container/equipment moves are intentionally blocked
  for tracked items instead of being replicated. State-changing use of owned items is allowed
  and sent through effective-owner reports.
- Fluid bandwidth split, unreliable latest-state fluid updates and neighbouring structural
  dependencies are not implemented.
- World freshness is separated from initial readiness and gates item requests/crafting when
  the current chunk is not present, but predictive prefetch and full physics freshness remain.

## Remaining P2

- Event-based registration discovery instead of the periodic item scan.
- Chunk damage spatial indexing, codec allocation profiling and typed replication budgets.
- Full generated audit of every item component and every vanilla recipe in co-op.

## Do not regress

- Core and Protocol must stay UnityEngine-free (`ArchitectureChecks` enforces this).
- `Verify.ps1` and `Verify.ps1 -GameTests` must stay green.
- Craft commit must remain all-or-nothing across registry and native objects.
- Duplicate terminal commits must replay the stored result, never re-execute.
- Do not infer ingredient destruction from `destroyItem` alone; observe the actual vanilla
  `Object.Destroy` within the active craft scope.
- Use the actual vanilla `Recipe`/`RecipeItem`/`RecipeResult` definitions. Do not add a
  hand-maintained recipe table.
