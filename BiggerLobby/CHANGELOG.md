# BiggerLobby changelog

## 1.6.0
- Updated for MIMESIS EA 0.4.1, Linux (Proton) support; installs and updates from oliveira-sh/mimesis-patches
- Updated for MIMESIS EA 0.4.0, whose new pause menu, result screens and spectator list had broken the patch
- Pause/lobby menu: works with the game's new menu, with a row for every player (up to 10): volume, mute, profile, kick and ping. Like the game's own list, it only lists joined players (no "Empty slot" rows)
- Result screens: end of level, death match and the new session report show every player, in rows of up to 5
- Spectator list (while dead) works with the game's new list
- Public room lists (main menu and tram terminal) keep listing rooms with 4 or more players and show their real limit (for players who have BiggerLobby too)
- If a game update breaks one patch, the others (above all the lobby size) still apply. Each result screen is patched on its own, so one broken screen doesn't take the other two with it
- Patches chip: shows on the EA 0.4.0 main menu again (the game renamed and hid the version text it looked for)
- Patches window on Linux (Proton): Update shows the linux/mimesis-patches.py command to run, with a Copy button, instead of starting the Windows installer

## 1.5.7
- New icon: 10 players

## 1.5.6
- In-game Update reuses the installer already on your PC, so updating only downloads the small patch files (much faster)

## 1.5.5
- Spectator list: extra rows placed reliably below the first four; logs the row layout once to help diagnose

## 1.5.4
- Spectator player list (while dead) shows every player, not just the first 4

## 1.5.3
- Thunderstore version: no updater code at all (mod managers handle updates). The GitHub / installer version keeps the in-game Update button

## 1.5.2
- AI disclosure in the README and the DLL
- Installed from a mod manager (Gale, r2modman): updates come from the mod manager; no self-updating
- README: uninstalling from a mod manager

## 1.5.1
- Installed from a mod manager (r2modman, Gale): updates come from the mod manager; the Patches window no longer checks GitHub or updates itself.
- AI disclosure added to the README and the DLL.

## 1.5.0
- Now a MelonLoader mod, the loader MIMESIS mods on Thunderstore use. Installer 5.0 sets up MelonLoader and moves existing installs over.

## 1.4.0
- Now a BepInEx 5 plugin: the game files are no longer modified. Installer 4.0 sets up BepInEx and moves existing installs over.

## 1.3.4
- Patches window: move it by the title bar and resize it from any edge or corner (remembered). Download progress stays on one line.

## 1.3.3
- Patches window: Update all in the title bar; updates ask in a popup and show download progress, speed and time left.

## 1.3.2
- Patches window: update patches from inside the game, scrollable options, section headings, roomier layout, resizable from the bottom-right corner.

## 1.3.1
- Shorter, clearer option text in the Patches window.

## 1.2.0
- Main menu chip turns orange when a newer release is available.

## 1.1.0
- Shows a "BiggerLobby v1.1.0" chip in the bottom-left of the main menu; clicking it opens this repo.
- Lobby list: "Copy invite code" centred under the player slots.

## 1.0.0
- Lobbies hold 10 players (fixed; the Steam lobby and all 4 join checks raised).
- Pause/lobby player list: 10 numbered slots with backgrounds; free slots shown as "Empty slot". Volume, mute, profile, kick and ping work for every player.
- End-of-level and death match result screens show up to 10 players (2 × 5 grid).
- Difficulty scales above 4 players: quota, shop prices, monster budget and mimic count.
