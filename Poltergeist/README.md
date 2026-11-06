# Poltergeist

<p align="center"><img src="icon.png" width="160"></p>

**Version 1.0.0**

**The dungeons are haunted.** Random hauntings while you explore, to make the game feel like a horror game and not only a scrap hunt:

- **Whispers** in your teammates' voices, right behind you (the game's own radiation whisper).
- **Eerie sounds** around you (the game's own radiation ambience).
- **Flickering lights**: the lights around you, your flashlight included, go out and come back, sometimes leaving you in the dark for a second.
- **A figure in the distance**, staring at you from the end of a corridor. It's gone the moment you look straight at it or walk up to it.
- **Something running past** the edge of your view.
- **Jumpscares**: something waits right behind you, keeps up with you, and lunges when you turn around.

**Harmless and fair.** Nothing touches your health, radiation, items or the server. Nothing happens while a real monster is close: a real threat never looks like a haunting, and a haunting never hides a real threat. Nothing happens during a [Deathmatch](../Deathmatch/) either: a fight between players stays fair (no figure to waste a shell on, no lights going out for you only).

**Only you need it**, and only you see and hear your own hauntings (everyone with the patch gets their own). It works in any game: no host needed.

## Install
Use the [installer](../Installer/) (Windows) or `python3 linux/mimesis-patches.py install Poltergeist` on Linux (see the [Linux tool](../linux/)). On by default.

## Settings
In **Patches > Poltergeist**:

| | |
|---|---|
| **Intensity** | **Calm** (about every 2.5 minutes), **Spooky** (about every 1.5 minutes), **Terror** (about every 50 seconds) |
| **Whispers and sounds** | On / off |
| **Flickering lights** | On / off |
| **Figures in the distance** | On / off |
| **Jumpscares** | On / off |
| **Debug mode** | Off by default. **F8** in a level opens a window to start any haunting at once (below) |

The first haunting comes 40-65 seconds into a level.

### Debug mode
To see and hear each haunting without waiting: turn **Debug mode** on, go into a level and press **F8** (**F8** closes the window, and it closes as the level ends; the mouse is free while it's open). One button per haunting (**Whisper**, **Eerie sound**, **Flickering lights**, **Figure far away**, **Runner**, **Jumpscare**), **Random** (picked as it would on its own, from what's on) and **Stop what's on**. It ignores the schedule and the switches above, and works with Poltergeist off too. The rest holds, as for any haunting: a figure needs open floor where it would stand (the jumpscare 2-3 m behind you) and no monster near, a whisper a teammate's recorded voice, and nothing happens during a deathmatch or while the level isn't being played (arriving, the result screen). The window says why when one doesn't fit. A figure closes the window as it starts, so you can look around.

## Limits
- Whispers need recorded voices: early in a game, before anyone has said much, there may be none to whisper.
- Lights flicker only where there are lights near you.

## Uninstall
Use the installer's **Uninstall selected**, or delete `PoltergeistRuntime.dll` from the game's `Mods` folder.

## AI disclosure
This mod was made with the help of an AI coding assistant (Claude, by Anthropic): code, documentation and icons.
