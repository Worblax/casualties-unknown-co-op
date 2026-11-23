# Casualties Co-op

Standalone Steam co-op mod for the Windows Mono build of Casualties: Unknown. See [the co-op specification](docs/coop-plan.md) and [development handoff](docs/development-handoff.md).

From `mode/`, build the first plugin with the installed .NET SDK.

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' build src/CasualtiesCoop -c Release
```

The project references the workspace copy of the game and BepInEx 5.4.23.5 in the Steam installation. Override `GameManagedDir` or `BepInExCoreDir` with MSBuild properties if the files are elsewhere. See [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

Use `scripts/Install-TestPlugin.ps1` to copy the Release DLL into the Steam copy's BepInEx plugins directory, then launch from Steam. Look for `Casualties Co-op 0.1.1 initialized.` and `Steam lobby integration ready.` in `BepInEx/LogOutput.log`. In the prototype, open **Co-op** in the main menu, create a Steam room, invite friends, choose an unlocked character and toggle readiness. The host can change the shared difficulty, which resets readiness.

The current prototype covers a Steam room and its member list. Starting a shared run and synchronizing gameplay are later milestones.
