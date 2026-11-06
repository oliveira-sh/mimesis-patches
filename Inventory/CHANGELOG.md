# Inventory changelog

## 1.5.0
- Updated for MIMESIS EA 0.4.1, Linux (Proton) support; installs and updates from oliveira-sh/mimesis-patches
- Works with the game's new inventory (cosmetic and equipment slots, bags): slots 5-8 are now the game's own bag slots instead of a copied second row, bags still add slots (up to 8 in all), and fewer than 4 locks and hides the rest
- A player who joined without a Steam lobby (by address) sees all 4 basic slots instead of hiding slots by their own "Slots when hosting" number
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

## 1.2.11
- Patches window: move it by the title bar and resize it from any edge or corner (remembered). Download progress stays on one line.

## 1.2.10
- Patches window: Update all in the title bar; updates ask in a popup and show download progress, speed and time left.

## 1.2.9
- Patches window: update patches from inside the game, scrollable options, section headings, roomier layout, resizable from the bottom-right corner.

## 1.2.8
- Fixed: you could only pick up 4 items even with more slots. The host no longer reports the inventory as full at 4.

## 1.2.7
- Stack counts / durability % follow their slot when fewer than 4 slots are shown.

## 1.2.3
- The slot count is set only in Patches → Inventory (removed from the lobby menu).

## 1.2.2
- The stamina bar moves above the second slot row.

## 1.2.0
- Fixed the 5-8 slot layout: slots 1-4 stay exactly as the game draws them, slots 5-8 are a copy of that row placed above.

## 1.1.1
- Fixed: with fewer than 4 slots, the hidden slots still showed their stack counts.

## 1.1.0
- Slot count goes down to 1 (range 1-8); slot buttons in the Patches window are a 2 × 4 grid.

## 1.0.0
- Host-set inventory size (1-8 slots), 2 × 4 grid HUD, shared with players through the Steam lobby. Every player needs it.
