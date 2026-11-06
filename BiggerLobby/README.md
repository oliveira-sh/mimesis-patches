# BiggerLobby

<p align="center"><img src="icon.png" width="160"></p>

**Version 1.5.7**

Lobbies hold **10 players**, with the UI and difficulty adjusted to match.

![Lobby screen with 10 player slots filled](screenshots/lobby.png)

**Only the host needs it** for 10-player lobbies and the difficulty scaling. Other players who want the 10-player UI (lobby menu, result screen, spectator list while dead, public room list) need it installed too; without it they can still join and play, but those screens only show 4 players, and the public room list hides rooms once they have 4 players (they can still join through a Steam invite or the room's invite code).

## Install
Use the [installer](../Installer/), or install [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) (x64, 0.7.x) into the game folder, run the game once, then copy `BiggerLobbyRuntime.dll` from this patch's [release](https://github.com/oliveira-sh/mimesis-patches/releases) into the game's `Mods` folder.

## What you get
- **Player list** (pause/lobby menu): a row for every player, up to 10, each with volume, mute, profile, kick (host) and ping.
- **Result screens** (end of level, death match, session report): with more than 4 players, everyone is shown, in rows of up to 5.
- **Spectator list** (while dead): every player.
- **Public room lists**: rooms with 4 or more players stay listed, with their real limit (e.g. 5/10).
- **Difficulty scaling** above 4 players:

| What | Formula | 6 players | 8 players | 10 players |
|---|---|---|---|---|
| Quota | × players ÷ 4 | ×1.5 | ×2 | ×2.5 |
| Crow shop / vending prices | +12.5% per player above 4 | ×1.25 | ×1.5 | ×1.75 |
| Monster threat (normal monsters) | +10% per player above 4 | ×1.2 | ×1.4 | ×1.6 |
| Mimics per stage | +1 per 3 players above 4 | +0 | +1 | +2 |

With 4 players or fewer, nothing changes. Loot isn't scaled. Saves stay correct if you load them with a different group size.

**Known limitation:** every map has only 4 player spawn points, so players 5-10 start on the same spots as players 1-6 and the game's physics pushes them apart. Nothing breaks, but if someone gets pushed into a wall or out of the tram, please report it.

## Options

![Patches > BiggerLobby settings](screenshots/settings.png)

**Patches > BiggerLobby** on the main menu: turn difficulty scaling on/off.

## Uninstall
Use the installer's **Uninstall selected**, or delete `BiggerLobbyRuntime.dll` from the game's `Mods` folder. Installed with Gale or r2modman? Remove or disable it there instead.

## Build
`dotnet build -c Release` in `Runtime`.

## AI disclosure
This mod was made with the help of an AI coding assistant (Claude, by Anthropic): code, documentation and icons.
