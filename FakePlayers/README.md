# FakePlayers

<p align="center"><img src="icon.png" width="160"></p>

**Version 1.0.0**

A **testing tool**: add **fake players** to your game and set how they are (**alive or dead, health, radiation, where they stand**), so you can try things that need several players without a second person. **Off by default.**

They're the game's own dummy players: everyone in your game sees them as players. They don't do anything on their own, except follow you if you ask them to.

**Only the host needs it.** It does nothing in someone else's game.

## Install
Use the [installer](../Installer/) (Windows) or `python3 linux/mimesis-patches.py install FakePlayers` on Linux (see the [Linux tool](../linux/)), then turn it on in **Patches > FakePlayers**.

## Use
Host a game, then press **F7** to open the panel (**F7** closes it; it also closes when you change place, e.g. as a level ends). The mouse is free while it's open.

| | |
|---|---|
| **Add 1 / Add 3** | New fake players next to you ("Bot 1", "Bot 2"...; 9 at most) |
| **Kill / Revive** | Dead or alive (revived next to you, full health, no radiation) |
| **Radiation** | Slider 0-99 %, or 0 / 50 / 60 / 90 % |
| **Health** | Slider 1-100 %, or Full |
| **Bring** | Next to you, at once |
| **Follow / Stay** | Walks after you along the level's paths (comes over at once if it's far or stuck), or stops |
| **Talk 5 s** | Its voice "talks" for 5 seconds (monsters that hear voices hear it) |
| **Remove / Remove all** | Gone |

The **You** row does the same for yourself: kill or revive yourself, set your own radiation and health. **All fake players** at the bottom applies one change to every fake player at once.

### What you can try with them
- **Revive**: kill a fake player, stand next to its body.
- **Spectating**: kill yourself and watch them (SpectatorCam, the spectator lists).
- **BiggerLobby**: more than 4 players in the HUD and lists.
- **HudPercent**, the minimap's player dots, anything that shows other players' health or radiation.
- **[Hallucinations](../Hallucinations/)**: turn its **Debug keys** on, give a fake player more radiation than the threshold, kill yourself, watch it and press **E**: you steer a hallucination at it, alone. Your game plays the fake player's side (where it appears, whether its attack lands).
- **[Traitor](../Traitor/)** and **[Deathmatch](../Deathmatch/)**: turn their **Fake players take part** option on, and their destinations show up on the tram with fewer than 3 people; add fake players in the level (someone for the monsters to chase, a death that pays the team).

The game kills a player who joins a level after it started (3 s later); fake players are exempt, so you can add them at any time.

## Limits
- Only in your own game (you host).
- They stay behind when the team changes level (tram, next level): add new ones there.
- They count as players: a level isn't lost while one is alive, and "everyone in the tram" waits for them. Kill or remove them when you're done.
- Without [BiggerLobby](../BiggerLobby/), more than 3 fake players may not fit the game's 4-player HUD.

## Uninstall
Use the installer's **Uninstall selected**, or delete `FakePlayersRuntime.dll` from the game's `Mods` folder.

## AI disclosure
This mod was made with the help of an AI coding assistant (Claude, by Anthropic): code, documentation and icons.
