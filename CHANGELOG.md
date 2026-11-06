# Changelog

Each patch has its own changelog:

- [BetterMicrophone](BetterMicrophone/CHANGELOG.md)
- [BetterMimics](BetterMimics/CHANGELOG.md)
- [BiggerLobby](BiggerLobby/CHANGELOG.md)
- [Deathmatch](Deathmatch/CHANGELOG.md)
- [FakePlayers](FakePlayers/CHANGELOG.md)
- [FpsCounter](FpsCounter/CHANGELOG.md)
- [Fov](Fov/CHANGELOG.md)
- [Hallucinations](Hallucinations/CHANGELOG.md)
- [HostOptions](HostOptions/CHANGELOG.md)
- [HudPercent](HudPercent/CHANGELOG.md)
- [Installer](Installer/CHANGELOG.md)
- [Inventory](Inventory/CHANGELOG.md)
- [LateJoin](LateJoin/CHANGELOG.md)
- [Minimap](Minimap/CHANGELOG.md)
- [Poltergeist](Poltergeist/CHANGELOG.md)
- [PSController](PSController/CHANGELOG.md)
- [Revive](Revive/CHANGELOG.md)
- [SpectatorCam](SpectatorCam/CHANGELOG.md)
- [Traitor](Traitor/CHANGELOG.md)

## Unreleased
- **New patch: BetterMimics** (host only): mimics act like the people they copy. One voice per mimic for the whole level, the same person whose habits it copies (the game mixed everyone's), quiet while that person is near it, no talking over people, reply-like answers after a human pause; standing mimics look at whoever talks or stares at them; wandering mimics pick up scrap they spot (never from the tram; it's dropped, never lost) and put it down later; they join players' dances; they walk instead of flip-flopping into a run. Mimics stay exactly as dangerous. See [BetterMimics](BetterMimics/README.md).
- **New patch: Poltergeist** (each player's own, no host needed): random harmless hauntings, whispers in teammates' voices, eerie sounds, flickering lights, figures in the distance, and something behind you when you turn around. Debug mode: F8 in a level opens a window to start any haunting at once. See [Poltergeist](Poltergeist/README.md).
- **New patch: Traitor** (host and every player): the tram can offer a traitor job (marked on its screen). Everyone arrives alone inside the building (not in the tram), one player is secretly the traitor and monsters ignore them; catch them for gold. See [Traitor](Traitor/README.md).
- **New patch: Deathmatch** (host and every player): the tram can offer a deathmatch (marked on its screen). Everyone arrives alone inside the building, only mimics, a loaded double-barrel shotgun lent to everyone (taken back at the end), every death pays the team, the dead lose what they carry (the tram's contents are safe). See [Deathmatch](Deathmatch/README.md).
- **New patch: BetterMicrophone** (anyone, for their own voice): the game's built-in neural noise remover (RNNoise, off in the game) turned on, then a low cut, an optional hum filter, silence between words, even volume and a limiter, and the voice sent at 24 or 32 kbps instead of the game's 10. Mic test on the main menu (record, hear the original and the cleaned version). See [BetterMicrophone](BetterMicrophone/README.md).
- **New patch: FakePlayers** (host, off by default): a testing tool. While hosting, F7 adds fake players (the game's own dummy players) and sets them: alive or dead, health, radiation, next to you or following you, talking. With Hallucinations' Debug keys on, a dead host can steer a hallucination at one, alone. See [FakePlayers](FakePlayers/README.md).
- **Hallucinations**: steering no longer gets stuck where the game's physics misses the floor, a steered hallucination no longer vanishes when it reaches the player, steered attacks land, and fake players can be targets for testing. See its CHANGELOG.
- **New patch: Hallucinations** (host and every player): above 50% radiation a living player can be sent hallucinations by dead teammates (E while spectating them): fake mimics only that player sees, which come at them with a shotgun, a bomb or a melee weapon and vanish the moment the attack lands, doing no damage. Possession stays as in the game (the dead player only speaks through the mimic), but while they talk through it, living players within 10 m gain radiation (1% per second, never past 90%). Host settings are published in the lobby data. See [Hallucinations](Hallucinations/README.md). It replaces MimicControl (dead players driving a possessed mimic), which was never released and is removed: delete `MimicControlRuntime.dll` from the game's `Mods` folder if you installed a build of it (the Linux tool's `install` and `uninstall` now do that for you).
- **Linux (Steam with Proton)**: [`linux/mimesis-patches.py`](linux/README.md) installs, updates, builds and removes the patches, like the Windows installer: MelonLoader, the Wine DLL override in the game's Proton prefix, and the patches built against your game.
- **Game update EA 0.4.0**: BiggerLobby, HostOptions, HudPercent, Inventory, LateJoin and PSController are ported to the new game; Fov and SpectatorCam fixed for its new features; Revive fixed; Minimap and FpsCounter checked. Details in each patch's CHANGELOG.
- **Game update EA 0.4.1** (Steam build 26447295): all patches checked against it, none needed changes. They build, every hook and transpiler still matches the game code, and the game data they rely on is unchanged.
- All patches: the Patches window's list scrolls when the patches don't all fit (with many installed, the buttons got squeezed), and keeps the selected one in view. Its footer link shows the address it opens (the repository the patches are released from).
- Linux tool: its tables line up with the longer patch names (BetterMicrophone).
- Installer 5.3.0: lists the new patches (an installed 5.2.0 updates itself to it).
- Releasing: `tools/release.py --first` releases new patches at the version they have (1.0.0) instead of bumping them first.
- All patches: the Patches chip shows on the EA 0.4.0 main menu again. Under Proton the window's Update buttons show the Linux command to run (with a Copy button, for the same source and checkout as the install) instead of downloading and starting the Windows installer. Other Wine setups (CrossOver or Whisky on macOS) keep the installer.
- Building: `dotnet build` finds the game on Linux too (`MIMESIS_DIR`, or the default Steam, Flatpak or Snap Steam library) and says clearly when it can't. The patches build with the .NET SDK 8 or newer.

## All patches (BiggerLobby 1.3.0, PSController 1.3.0, SpectatorCam 1.1.0, Minimap 1.4.0, HostOptions 1.1.0, HudPercent 1.1.0)
- One **Patches** chip on the main menu replaces the per-patch chips. It opens a window: installed patches on the left, the selected patch's options on the right, with update links.
- New options: BiggerLobby difficulty scaling on/off; PSController icons on/off; SpectatorCam, HudPercent and Minimap on/off; HostOptions stamina and starting money; Minimap settings moved here from its own window.

