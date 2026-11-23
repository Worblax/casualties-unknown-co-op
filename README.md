# Casualties Co-op

Standalone Steam co-op mod for the Windows Mono build of Casualties: Unknown. See [the co-op specification](docs/coop-plan.md) and [development handoff](docs/development-handoff.md).

From `mode/`, build the first plugin with the installed .NET SDK.

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' build src/CasualtiesCoop -c Release
```

The project references the workspace copy of the game and BepInEx 5.4.23.5 in the Steam installation. Override `GameManagedDir` or `BepInExCoreDir` with MSBuild properties if the files are elsewhere. See [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

Use `scripts/Install-TestPlugin.ps1` to copy the Release DLL into the Steam copy's BepInEx plugins directory, then launch from Steam. Look for `Casualties Co-op 0.1.11 initialized.` and `Steam lobby integration ready.` in `BepInEx/LogOutput.log`. Dismiss the opening warning with a left click, then skip the intro with **Esc**. Open **Co-op** in the upper-right of the main menu, create a Steam room, invite friends, choose an unlocked character and toggle readiness. Each player's character appears in the roster as soon as they choose it.

The host can change the difficulty in the lobby and open **Room options** for the other settings. Guests can see the current difficulty and all option values in gray. Changing any shared setting resets everyone's readiness. Use the same mod version on both Steam accounts.

When you are alone in a room, mark yourself ready and choose **Start** to enter a solo preview. The preview uses the selected character and difficulty, and it disables writing the vanilla checkpoint. Room toggles are shared through Steam metadata. Their gameplay effects, named co-op saves, and shared runs are later milestones.
