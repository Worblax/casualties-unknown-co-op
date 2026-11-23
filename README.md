# Casualties Co-op

Standalone Steam co-op mod for the Windows Mono build of Casualties: Unknown. See [the co-op specification](docs/coop-plan.md) and [development handoff](docs/development-handoff.md).

From `mode/`, build the first plugin with the installed .NET SDK.

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' build src/CasualtiesCoop -c Release
```

The project references the workspace copy of the game and BepInEx 5.4.23.5 in the Steam installation. Override `GameManagedDir` or `BepInExCoreDir` with MSBuild properties if the files are elsewhere. See [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

Use `scripts/Install-TestPlugin.ps1` to copy the Release DLL into the Steam copy's BepInEx plugins directory, then launch from Steam. Look for `Casualties Co-op 0.1.12 initialized.` and `Steam lobby integration ready.` in `BepInEx/LogOutput.log`. Dismiss the opening warning with a left click, then skip the intro with **Esc**. Open **Co-op** in the upper-right of the main menu, create a Steam room, invite friends, choose an unlocked character and toggle readiness. Each player's character appears in the roster as soon as they choose it.

The host can change the difficulty in the lobby and open **Room options** for the other settings. Guests can see the current difficulty and all option values in gray. Changing any shared setting resets everyone's readiness. Use the same game build and the same mod DLL on all Steam accounts. The room verifies both builds over Steam P2P before enabling guest readiness.

Enable **Sandbox** in **Room options**, then mark each player ready. Once the team confirms the roster and settings, the host can choose **Start**. Each player enters the native sandbox with their selected character, sees the other players' position markers, and gets their own terminal and console. `talk` speaks through the local character. The sandbox currently synchronizes player positions. Shared bodies, world changes and items are later milestones. In a one-player room, **Start** can also open the regular solo preview. Both previews disable the vanilla checkpoint write.

The plugin entry point wires together `Lobby/` for Steam room state, `Networking/` for Steam connections and message codecs, `Session/` for launch confirmation and sandbox position sync, `Game/` for Unity scene integration, and `UI/` for the lobby panels.

Run the protocol checks from `mode/`. They use the .NET 10 SDK and run without the game or Steam.

```powershell
dotnet run --project tests/CasualtiesCoop.ProtocolChecks -c Release
```
