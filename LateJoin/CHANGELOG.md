# LateJoin changelog

## 1.3.0
- Updated for MIMESIS EA 0.4.1, Linux (Proton) support; installs and updates from oliveira-sh/mimesis-patches
- Game EA 0.4.0: the game itself now lets players join mid-run, so the unused lobby-only override is gone. A player who reaches the tram while the team is away waits there again instead of being sent back to the menu
- The "team is in a level" message no longer stays on screen for good if you leave the tram while waiting (e.g. the host quits)
- Patches chip: shows on the EA 0.4.0 main menu again (the game renamed and hid the version text it looked for)
- Patches window on Linux (Proton): Update shows the linux/mimesis-patches.py command to run, with a Copy button, instead of starting the Windows installer

## 1.2.3
- In-game Update reuses the installer already on your PC, so updating only downloads the small patch files (much faster)

## 1.2.2
- Thunderstore version: no updater code at all (mod managers handle updates). The GitHub / installer version keeps the in-game Update button

## 1.2.1
- Installed from a mod manager (r2modman, Gale): updates come from the mod manager; the Patches window no longer checks GitHub or updates itself.
- AI disclosure added to the README and the DLL.

## 1.2.0
- Now a MelonLoader mod, the loader MIMESIS mods on Thunderstore use. Installer 5.0 sets up MelonLoader and moves existing installs over.

## 1.1.0
- Now a BepInEx 5 plugin: the game files are no longer modified. Installer 4.0 sets up BepInEx and moves existing installs over.

## 1.0.4
- Patches window: move it by the title bar and resize it from any edge or corner (remembered). Download progress stays on one line.

## 1.0.3
- Patches window: Update all in the title bar; updates ask in a popup and show download progress, speed and time left.

## 1.0.2
- Patches window: update patches from inside the game, scrollable options, section headings, roomier layout, resizable from the bottom-right corner.

## 1.0.1
- Shorter, clearer option text in the Patches window.

## 1.0.0
- Join a game in progress: late joiners wait, then enter at the tram when the team returns. Host and joiner need it.
