# Casualties Co-op

Standalone Steam co-op mod for the Windows Mono build of Casualties: Unknown. See [the co-op specification](docs/coop-plan.md) and [development handoff](docs/development-handoff.md).

From `mode/`, build the first plugin with the installed .NET SDK.

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' build src/CasualtiesCoop -c Release
```

The project references the workspace copy of the game and BepInEx 5.4.23.5 in the Steam installation. Override `GameManagedDir` or `BepInExCoreDir` with MSBuild properties if the files are elsewhere. See [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

Use `scripts/Install-TestPlugin.ps1` to copy the Release assemblies into the Steam copy's BepInEx plugins directory, then launch from Steam. Look for `Casualties Co-op 0.1.17 initialized.` and `Steam lobby integration ready.` in `BepInEx/LogOutput.log`. Dismiss the opening warning with a left click, then skip the intro with **Esc**. Open **Co-op** in the upper-right of the main menu, create a Steam room, invite friends, choose an unlocked character and toggle readiness. Each player's character appears in the roster as soon as they choose it.

The host can change the difficulty in the lobby and open **Room options** for the other settings. Guests can see the current difficulty and all option values in gray. Changing any shared setting resets everyone's readiness. Use the same game build and the same set of mod DLLs on all Steam accounts. The room verifies both builds over Steam P2P before enabling guest readiness. The sandbox uses lobby protocol 13 and requires an updated Release bundle on all peers.

Enable **Sandbox** in **Room options**, then mark each player ready. Once the team confirms the roster and settings, the host can choose **Start**. Players see each other's bodies, including ragdoll poses, and hear speech and character sound effects at the point where they played. Each player has their own terminal and console. `talk` speaks through the local character, and nearby players hear the speech sounds. In a one-player room, **Start** can also open the regular solo preview. Both previews disable the vanilla checkpoint write.

Shared items now use the game's own item prefabs and saveable component schema. A network item keeps one identity across pickup, drop, containers, equipment changes and chunk changes; condition, liquids, ammunition, batteries, wetness, flashlight/custom modes and supported `[Saveable]` component fields travel with that identity. Crafting asks the host to reserve the exact ingredients selected by the vanilla recipe, rejects another player's inventory items, and commits consumption plus results atomically. See [the universal item handoff](ITEM_SYNC_HANDOFF.md) and the [current project handoff](docs/development-handoff.md#текущая-точка-входа) for verification status.

The host simulates sandbox terrain liquids near the team. Each player renders the water from their own camera and sees supported lights carried by other players or lying in the host's world. Line of sight and avatar visibility are calculated for each observer. See the [two-player scenario and verification status](docs/development-handoff.md#сборка-0114).

To return to a live shared sandbox after restarting the game, open **Co-op → Rejoin last room** and accept the new Steam invitation. The host can also use **Invite again** in the pause menu. The return flow keeps the saved character, last accepted location, and confirmed shared items. See the [reconnection verification status](docs/reconnection.md#сборка-0115).

The plugin entry point wires together `Lobby/` for Steam room state, `Networking/` for Steam connections and message codecs, `Session/` for launch confirmation, `Sandbox/` for scene and session lifecycle, `Gameplay/` for Unity hooks and projections, and `UI/` for the lobby panels.

Run the Release build and automated checks from `mode/` with `powershell.exe -NoProfile -File ./scripts/Verify.ps1`. The protocol checks use the .NET 10 SDK and can also run separately without the game or Steam.

```powershell
dotnet run --project tests/CasualtiesCoop.ProtocolChecks -c Release
```
