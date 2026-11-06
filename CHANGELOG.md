# Changelog

Each patch has its own changelog:

- [BiggerLobby](BiggerLobby/CHANGELOG.md)
- [FpsCounter](FpsCounter/CHANGELOG.md)
- [Fov](Fov/CHANGELOG.md)
- [Hallucinations](Hallucinations/CHANGELOG.md)
- [HostOptions](HostOptions/CHANGELOG.md)
- [HudPercent](HudPercent/CHANGELOG.md)
- [Installer](Installer/CHANGELOG.md)
- [Inventory](Inventory/CHANGELOG.md)
- [LateJoin](LateJoin/CHANGELOG.md)
- [Minimap](Minimap/CHANGELOG.md)
- [PSController](PSController/CHANGELOG.md)
- [Revive](Revive/CHANGELOG.md)
- [SpectatorCam](SpectatorCam/CHANGELOG.md)

## Unreleased
- **New patch: Hallucinations** (host and every player): above 50% radiation a living player can be sent hallucinations by dead teammates (E while spectating them): fake mimics only that player sees, which come at them with a shotgun, a bomb or a melee weapon and vanish the moment the attack lands, doing no damage. Possession stays as in the game (the dead player only speaks through the mimic), but while they talk through it, living players within 10 m gain radiation (1% per second, never past 90%). Host settings are published in the lobby data. See [Hallucinations](Hallucinations/README.md). It replaces MimicControl (dead players driving a possessed mimic), which was never released and is removed: delete `MimicControlRuntime.dll` from the game's `Mods` folder if you installed a build of it (the Linux tool's `install` and `uninstall` now do that for you).
- **Linux (Steam with Proton)**: [`linux/mimesis-patches.py`](linux/README.md) installs, updates, builds and removes the patches, like the Windows installer: MelonLoader, the Wine DLL override in the game's Proton prefix, and the patches built against your game.
- **Game update EA 0.4.0**: BiggerLobby, HostOptions, HudPercent, Inventory, LateJoin and PSController are ported to the new game; Fov and SpectatorCam fixed for its new features; Revive fixed; Minimap and FpsCounter checked. Details in each patch's CHANGELOG.
- **Game update EA 0.4.1** (Steam build 26447295): all patches checked against it, none needed changes. They build, every hook and transpiler still matches the game code, and the game data they rely on is unchanged.
- All patches: the Patches chip shows on the EA 0.4.0 main menu again. Under Proton the window's Update buttons show the Linux command to run (with a Copy button, for the same source and checkout as the install) instead of downloading and starting the Windows installer. Other Wine setups (CrossOver or Whisky on macOS) keep the installer.
- Building: `dotnet build` finds the game on Linux too (`MIMESIS_DIR`, or the default Steam, Flatpak or Snap Steam library) and says clearly when it can't. The patches build with the .NET SDK 8 or newer.

## All patches (BiggerLobby 1.3.0, PSController 1.3.0, SpectatorCam 1.1.0, Minimap 1.4.0, HostOptions 1.1.0, HudPercent 1.1.0)
- One **Patches** chip on the main menu replaces the per-patch chips. It opens a window: installed patches on the left, the selected patch's options on the right, with update links.
- New options: BiggerLobby difficulty scaling on/off; PSController icons on/off; SpectatorCam, HudPercent and Minimap on/off; HostOptions stamina and starting money; Minimap settings moved here from its own window.

