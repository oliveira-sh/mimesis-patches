# Inventory

<p align="center"><img src="icon.png" width="160"></p>

**Version 1.4.4**

The host sets the inventory size to **1-8 slots**. Slots 5-8 are the game's own **bag slots** (the row a bag opens); bags still add slots on top, up to 8 in all. Fewer than 4 leaves the rest locked and hidden.

![Inventory bar with 8 slots, on EA 0.3.1 (since EA 0.4.0, slots 5-8 are the bag row)](screenshots/inventory.png)

**Every player needs it.** The host's game decides the slots and everyone sees slots 5-8, but below 4 a player without it still sees the locked slots (they stay empty), so make sure everyone installs it.

## Install
Use the [installer](../Installer/), or install [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) (x64, 0.7.x) into the game folder, run the game once, then copy `InventoryRuntime.dll` from this patch's [release](https://github.com/oliveira-sh/mimesis-patches/releases) into the game's `Mods` folder.

## Options

![Patches > Inventory settings](screenshots/settings.png)

**Patches > Inventory** on the main menu: pick the slot count used when you host. Players get it from the host automatically. It applies when characters spawn (next level, or reload the lobby). Changing it never loses items.

## Uninstall
Use the installer's **Uninstall selected**, or delete `InventoryRuntime.dll` from the game's `Mods` folder. Installed with Gale or r2modman? Remove or disable it there instead.

## AI disclosure
This mod was made with the help of an AI coding assistant (Claude, by Anthropic): code, documentation and icons.
