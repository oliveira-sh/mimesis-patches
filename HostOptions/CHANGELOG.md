# HostOptions changelog

## 1.5.0
- Updated for MIMESIS EA 0.4.1, Linux (Proton) support; installs and updates from oliveira-sh/mimesis-patches
- Game update EA 0.4.0: the lobby's Infinite Stamina and Starting money controls moved, with the game's public match and password settings, from the Esc menu to the host's TRAM Settings window (F1)
- Patches chip: shows on the EA 0.4.0 main menu again (the game renamed and hid the version text it looked for)
- Patches window on Linux (Proton): Update shows the linux/mimesis-patches.py command to run, with a Copy button, instead of starting the Windows installer

## 1.4.4
- In-game Update reuses the installer already on your PC, so updating only downloads the small patch files (much faster)

## 1.4.3
- Thunderstore version: no updater code at all (mod managers handle updates). The GitHub / installer version keeps the in-game Update button

## 1.4.2
- AI disclosure in the README and the DLL
- Installed from a mod manager (Gale, r2modman): updates come from the mod manager; no self-updating
- README: uninstalling from a mod manager

## 1.4.1
- Installed from a mod manager (r2modman, Gale): updates come from the mod manager; the Patches window no longer checks GitHub or updates itself.
- AI disclosure added to the README and the DLL.

## 1.4.0
- Now a MelonLoader mod, the loader MIMESIS mods on Thunderstore use. Installer 5.0 sets up MelonLoader and moves existing installs over.

## 1.3.0
- Now a BepInEx 5 plugin: the game files are no longer modified. Installer 4.0 sets up BepInEx and moves existing installs over.

## 1.2.2
- Patches window: move it by the title bar and resize it from any edge or corner (remembered). Download progress stays on one line.

## 1.2.1
- Patches window: Update all in the title bar; updates ask in a popup and show download progress, speed and time left.

## 1.2.0
- Difficulty presets (Easy, Normal, Hard) and sliders for quota/repair cost, shop prices, sell value and monsters.
- Patches window: update patches from inside the game, scrollable options, section headings, resizable from the bottom-right corner.
- Choose whether they also apply to loaded saves or only to new games.

## 1.1.1
- Shorter, clearer option text in the Patches window.

## 1.0.3
- Fixed: patches failed to load (Harmony class split), the stamina checkbox had no label, and changing starting money in the lobby didn't update the funds.

## 1.0.0
- Host-only lobby options under "Use Entry Password": **Infinite Stamina** and **Starting money**. Both run on the host, so other players don't need the patch.
