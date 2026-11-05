# SpectatorCam changelog

## Unreleased
- Free camera: stays off while the MimicControl patch has you driving a mimic (its keys move the mimic); F works again once you let go
- Free camera: its keys (E = up) no longer possess a mimic or end the possession by accident; press F to return to the normal camera first. The controller is unaffected
- Free camera: ignores input while the F9 feedback form is open, like the game's spectator camera
- Patches chip: shows on the EA 0.4.0 main menu again (the game renamed and hid the version text it looked for)
- Patches window on Linux (Proton): Update shows the linux/mimesis-patches.py command to run, with a Copy button, instead of starting the Windows installer

## 1.3.3
- In-game Update reuses the installer already on your PC, so updating only downloads the small patch files (much faster)

## 1.3.2
- Thunderstore version: no updater code at all (mod managers handle updates). The GitHub / installer version keeps the in-game Update button

## 1.3.1
- Installed from a mod manager (r2modman, Gale): updates come from the mod manager; the Patches window no longer checks GitHub or updates itself.
- AI disclosure added to the README and the DLL.

## 1.3.0
- Now a MelonLoader mod, the loader MIMESIS mods on Thunderstore use. Installer 5.0 sets up MelonLoader and moves existing installs over.

## 1.2.0
- Now a BepInEx 5 plugin: the game files are no longer modified. Installer 4.0 sets up BepInEx and moves existing installs over.

## 1.1.4
- Patches window: move it by the title bar and resize it from any edge or corner (remembered). Download progress stays on one line.

## 1.1.3
- Patches window: Update all in the title bar; updates ask in a popup and show download progress, speed and time left.

## 1.1.2
- Patches window: update patches from inside the game, scrollable options, section headings, roomier layout, resizable from the bottom-right corner.

## 1.1.1
- Shorter, clearer option text in the Patches window.

## 1.0.0
- Free camera for dead players (F / R3), within 8 m of the spectated player and never through walls.
