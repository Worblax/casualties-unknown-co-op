# P0 real-client defects plan

Base commit: `6f721f2`.

Restart entry point: read `HANDOFF_P0_REAL_DEFECTS.md` first.

This file is the working checklist for the real two-player defects. Steps are executed in order;
a later step is not marked complete until its production fix and regression evidence exist.

## Step 1 - two-process test stand

Status: complete. `tests/multiprocess/pose-bidirectional.json` passes in both independent
processes with real production `WorldSession` and `RemoteCharacterView` instances.

- Add a local TCP+UDP `ISessionTransport` that emulates reliable ordered and unreliable ordered
  channels, network identity, latency, jitter and unreliable loss.
- Add `--coop-mp <manifest>` to the testing plugin. Each process enters the real sandbox, owns
  one real `Body`, runs the production `WorldSession`, production Harmony hooks and real
  `RemoteCharacterView` instances.
- Add a minimal orchestrator that launches two isolated game-process copies, allocates ports,
  passes the same session plan to both and checks independent reports.
- Patch only test-process data roots; do not replace gameplay logic or switch the local character
  inside one process.
- Keep the existing single-process scenarios as the fast regression layer.

Exit criteria: both processes connect through the new transport, exchange production character
poses in both directions, create the remote view, and report PASS independently.

## Step 2 - empty remote character visuals

Status: complete. `DownTorso/Cargo` / `blueHeartEmbed` is inactive in the source prefab but was
cloned active. Filtering by the source branch's active state fixes the clone. Both processes in
run `1b8b9cf5146a4ec6b023b01f9335a8d5` passed the source-to-remote renderer assertion and retained
the prefab and projection dumps. The assertion compares paths, static sprites, sorting and flips.
The head and eye sprite names can change with the live expression.

- Dump the source character prefab and the generated remote hierarchy.
- Identify the extra blue/yellow sprite by source path, active state and renderer state.
- Preserve source hierarchy/active state when cloning sprites; do not hide by arbitrary names.
- Add a visual-equivalence regression for an empty remote character, with screenshots when a
  headless assertion cannot prove the defect.

## Step 3 - single held item visual and dynamic rotation

Status: verified. Pose snapshots carry the world angle and reflection by item ID and slot. The
remote view interpolates the angle and the correlated light follows its attached emitter. The
wrap, sweep, facing and ragdoll scenario passed in `4c75f36908734ae7a201d6f2cd61676b`. The powered
fixture and stale-beam check after dropping passed in `e8885d7af22a420dad0d8557a080ed5e`.

- Trace the duplicate by network item ID, Unity instance ID, renderer and hierarchy path.
- Establish one visual owner per network item and remove the stale second representation.
- Replicate the native held-item angle from the character anchor, including gaze, limb rotation,
  sprite reflection and ragdoll.
- Send continuous angle updates over the unreliable ordered channel through the existing pose
  snapshot stream where possible; reserve reliable messages for item/slot changes.
- Interpolate shortest-path angles across 0/360 and drive the flashlight beam from the same angle.
- Test slow/fast rotation, direction changes, character turns, wrap-around, latency and loss.

## Step 4 - slot movement

Status: verified. The native two-process slot scenario passed in `fe7ca94c9a774da1aef06c485c4d2a78`.
Network checks cover atomic commit, retry, stale preconditions, craft reservations, and snapshot
recovery for a missing delta.

- Trace `SharedItemProjection.Apply`, `Detach`, `ApplyInventory`, `localPlacementPending` and
  `ItemStateReport`.
- Use native `Body.SwapSlots` semantics for ordinary slot moves; do not turn a swap into a
  drop/pickup pair.
- Resolve the race between local placement and an older authoritative state.
- Test empty/occupied slots across hands, belt and back.

## Step 5 - guest throws and drops

Status: verified. Both directions of native drop and throw passed in
`5ae0c2d046df41abaeec239e5f9ff089`. Both processes checked authoritative placement and object
identity. The host checked active colliders, physics and throw travel.

- Trace the vanilla `DropItem`/`ThrowItem` path through active state, parent, colliders, physics,
  ownership, slot, revision and network messages.
- Ensure the host creates and activates the authoritative world object instead of only updating
  the registry.
- Test both directions and a long throw with network delay.

## Step 6 - snapshot interpolation for physics

Status: implemented and measured. At the production 10 Hz sample rate, fixed 40 and 150 ms delays
gave a maximum jump of 0.064 units at 120 Hz playback. A seeded 40–150 ms jitter and packet-loss
profile gave a maximum jump of 0.132 units, a mean error of 0.143 units and a maximum lag error of
1.494 units during capped extrapolation. Unity validates movement through the throw and drop
scenario. The numeric measurements use the standalone network checks.

- Replace exponential position chasing with a timestamped snapshot buffer.
- Interpolate position, angle and velocity; use angular velocity for rotation.
- Allow bounded extrapolation on loss; snap on teleport, ownership change and world/inventory
  transitions.
- Measure jumps and playback error under 40-150 ms delay, jitter and unreliable loss.

## Step 7 - native character speech

Status: verified in two processes. Run `0f0d2461a8d34c33a794fe65542f1fbd` passed simultaneous
host and guest speech, a Unicode line, a real TCP disconnect and rejoin, clearing visible history,
and a new line after rejoin under 90 ms latency, 25 ms jitter and 8% unreliable loss.

- Inspect the original speech text creation, localization, placement, animation and lifetime.
- Add one reliable event per native speech event, with deduplication and no Harmony echo loop.
- Render through the original text mechanism on the remote character so it follows the avatar.
- Test host/guest speech, simultaneous speakers, sequences, latency and reconnect history.

## Step 8 - final verification

Status: complete on the local test stand. `Verify.ps1 -GameTests` passed the Release build, protocol,
architecture and network checks, plus all 44 eligible single-process Unity scenarios. The combined
log is `artifacts/p0-final-verified.log`.

Both processes passed each final scenario. Run IDs are relative to `artifacts/multiprocess/`.

| Scenario | Run ID |
| --- | --- |
| Bidirectional pose and source renderer comparison | `62cde20caa4e496fac7c94af71f84756` |
| Held angle, sweep, facing and ragdoll | `259d9daf7dcd4e94909413352a491701` |
| Bidirectional held beam and powered drop | `6ba8088ee6f6437ba0aa028434f38ca7` |
| Native slot moves | `d7c9560da1d648aebb59452568d550ef` |
| Native releases and throw motion on host and guest | `b5bfb4dc5e644d899bebb67fe8d105ee` |
| Native speech and disconnect/rejoin | `dcba9d1522ce442082649a267307fa84` |

Angle, beam and speech used 90 ms latency, 25 ms jitter and 8% unreliable loss. The remaining
scenarios used 40 ms latency, 5 ms jitter and 2% loss. Steam peer-to-peer and graphical rendering
require separate verification.

- Run the fast single-process suite and the two-process scenarios.
- Record exact commands, results, artifacts and remaining limitations.
- Do not claim a visual fix from registry counts alone.
