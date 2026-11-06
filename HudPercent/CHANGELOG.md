# HudPercent changelog

## 1.4.0
- Updated for MIMESIS EA 0.4.1, Linux (Proton) support; installs and updates from oliveira-sh/mimesis-patches
- Works with the EA 0.4 HUD (new health graph and radiation gauge): the percentages sit right of each gauge, in the HUD's pixel font and amber color
- Health shows 0% while you are dead (was 100%)
- Patches chip: shows on the EA 0.4.0 main menu again (the game renamed and hid the version text it looked for)
- Patches window on Linux (Proton): Update shows the linux/mimesis-patches.py command to run, with a Copy button, instead of starting the Windows installer

## 1.3.4
- In-game Update reuses the installer already on your PC, so updating only downloads the small patch files (much faster)

## 1.3.3
- Thunderstore version: no updater code at all (mod managers handle updates). The GitHub / installer version keeps the in-game Update button

## 1.3.2
- AI disclosure in the README and the DLL
- Installed from a mod manager (Gale, r2modman): updates come from the mod manager; no self-updating
- README: uninstalling from a mod manager

## 1.3.1
- Installed from a mod manager (r2modman, Gale): updates come from the mod manager; the Patches window no longer checks GitHub or updates itself.
- AI disclosure added to the README and the DLL.

## 1.3.0
- Now a MelonLoader mod, the loader MIMESIS mods on Thunderstore use. Installer 5.0 sets up MelonLoader and moves existing installs over.

## 1.2.0
- Now a BepInEx 5 plugin: the game files are no longer modified. Installer 4.0 sets up BepInEx and moves existing installs over.

## 1.1.6
- Patches window: move it by the title bar and resize it from any edge or corner (remembered). Download progress stays on one line.

## 1.1.5
- Patches window: Update all in the title bar; updates ask in a popup and show download progress, speed and time left.

## 1.1.4
- Patches window: update patches from inside the game, scrollable options, section headings, roomier layout, resizable from the bottom-right corner.

## 1.1.3
- Shorter, clearer option text in the Patches window.

## 1.1.2
- Same gap after both bars (radiation % no longer touches its bar).

## 1.1.1
- Radiation % now sits next to its bar.

## 1.0.0
- Health and radiation percentages next to the top-left HUD bars.
