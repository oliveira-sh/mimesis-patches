# PSController changelog

## 1.6.0
- Updated for MIMESIS EA 0.4.1, Linux (Proton) support; installs and updates from oliveira-sh/mimesis-patches
- Current game: controller prompts (crosshair, HUD and spectator tips, mimic possession, Controls settings) now come from a new key icon that the patch didn't reach; it shows the PlayStation icons too
- A patch that no longer fits the game is logged instead of breaking the game's startup
- Patches chip: shows on the EA 0.4.0 main menu again (the game renamed and hid the version text it looked for)
- Patches window on Linux (Proton): Update shows the linux/mimesis-patches.py command to run, with a Copy button, instead of starting the Windows installer

## 1.5.4
- In-game Update reuses the installer already on your PC, so updating only downloads the small patch files (much faster)

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
- Shows a "PSController v1.1.0" chip in the bottom-left of the main menu; clicking it opens this repo.

## 1.0.0
- PlayStation button icons (✕ ○ □ △, L1/R1/L2/R2, L3/R3) for every in-game prompt.
- Prefers the XInput (Xbox) pad so DS4Windows' Xbox 360 output reads correctly.
- Optional button-press debug log (`MIMESIS_Data/PSIcons/debug.txt`).
