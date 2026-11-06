# HostOptions

<p align="center"><img src="icon.png" width="160"></p>

**Version 1.4.4**

Extra lobby options for the host: **infinite stamina**, **starting money**, and **difficulty and economy** settings.

![Infinite Stamina and Starting money in the lobby menu, on EA 0.3.1 (since EA 0.4.0 they're in TRAM Settings)](screenshots/options.png)

**Only the host needs it.** Other players don't see the controls.

## Install
Use the [installer](../Installer/), or install [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) (x64, 0.7.x) into the game folder, run the game once, then copy `HostOptionsRuntime.dll` from this patch's [release](https://github.com/oliveira-sh/mimesis-patches/releases) into the game's `Mods` folder.

## Options

![Patches > HostOptions settings](screenshots/settings.png)

In the lobby's **TRAM Settings** window (F1 by default; the game's public match and password settings), under "Use Entry Password", or in **Patches > HostOptions** on the main menu:
- **Infinite Stamina**: nobody's stamina drains. Monsters are unaffected.
- **STARTING MONEY**: the funds a run starts with (game default $15). Type a value and press Enter or click away. Empty or invalid resets to the default. Changing it before the first departure also sets your current funds.

In **Patches > HostOptions** only:
- **Difficulty**: Easy, Normal or Hard sets the four sliders below. Move any slider for a custom mix.
- **Quota / repair**: money needed to repair the tram (25%-300%).
- **Shop prices**: cost of shop items.
- **Sell value**: money you get for selling items.
- **Monsters**: how many monsters and mimics spawn in a level.
- **Apply to**: *All games* (default) also uses these on runs you continue from a save; *New games only* leaves loaded saves at normal values.

| Preset | Quota | Prices | Sell | Monsters |
|---|---|---|---|---|
| Easy | 75% | 75% | 125% | 60% |
| Normal | 100% | 100% | 100% | 100% |
| Hard | 125% | 125% | 90% | 150% |

These stack with BiggerLobby's scaling for big groups.

Settings are saved on your PC.

## Uninstall
Use the installer's **Uninstall selected**, or delete `HostOptionsRuntime.dll` from the game's `Mods` folder. Installed with Gale or r2modman? Remove or disable it there instead.

## AI disclosure
This mod was made with the help of an AI coding assistant (Claude, by Anthropic): code, documentation and icons.
