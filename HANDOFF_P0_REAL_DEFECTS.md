# Handoff for real-client P0 defects

The working checklist and per-scenario evidence are in [P0_REAL_DEFECTS_PLAN.md](P0_REAL_DEFECTS_PLAN.md). The work is in the `mode/` working tree on base `6f721f2`. The combined wire changes use lobby protocol 18.

## Production changes

- Remote avatars clone active source branches only. `DownTorso/Cargo` was inactive in the source prefab but previously rendered as an active `blueHeartEmbed` on the remote avatar.
- Pose snapshots carry the native held item angle and reflection keyed by item ID and slot. The visible held item has one attachment, and the projected beam follows its emitter. A native item update can re-enable the stock beam, so the presentation pass enforces suppression after that update.
- Inventory drags call the native slot swap. One reliable request validates both item IDs, revisions and destinations. The host commits both records before publishing changes. The guest reserves both slots through projection and runs the native swap once the new placement is coherent.
- Drop and throw validate reach before applying the payload or releasing the item. Authoritative recreated items retain their prefab colliders. Guest world replicas are kinematic and use timestamped interpolation, capped extrapolation and spin-aware angle playback.
- Native `Talker` captures the accepted, localized line and uses a reliable sequence per speaker. A remote, body-less `Talker` renders through the vanilla text prefab. Recovery resumes the sender sequence and clears displayed and pending lines.

## Verify from `mode/`

```powershell
powershell.exe -NoProfile -File ./scripts/Verify.ps1 -GameTests
powershell.exe -NoProfile -File ./scripts/Run-MultiProcessTests.ps1 -Scenario tests/multiprocess/native-speech.json -LatencyMs 90 -JitterMs 25 -UnreliableLoss 0.08
```

Run each remaining JSON in `tests/multiprocess/` serially through the same runner. It saves the exact scenario, game and build hashes, and each process's result and log. The full single-process log is `artifacts/p0-final-verified.log`.

The two-process evidence uses independent local Unity instances and TCP/UDP. Steam peer-to-peer and graphical rendering are separate verification environments.
