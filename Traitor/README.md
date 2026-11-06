# Traitor

<p align="center"><img src="icon.png" width="160"></p>

**Version 1.0.0**

**A traitor destination on the tram.** Now and then one of the tram's two destinations is a **traitor job**, marked **TRAITOR** on the tram's screen. Take it or not, like any destination.

If you go:
1. **Everyone arrives alone**, somewhere inside the building, far from each other and clear of monsters: you appear there as the level loads, not in the tram, and the level's monsters wait until everyone has arrived. From the tram you'd see who walks out of it; spread out, a stranger in a corridor could be a friend, a mimic or the traitor.
2. **One player is secretly the traitor.** Only they are told. Everyone else learns that there is one.
3. **Monsters ignore the traitor**, mimics included: they never chase them, never attack them, and their attacks pass through them. Players can still hurt the traitor, and the traitor them.
4. **The tram is locked** for a while (2:30 by default), so the traitor can't just pull the lever and leave everyone behind. The lever springs back until then.
5. **The end:**
   - **the traitor is killed by a player**: the team gets gold (a quarter of the stage's repair cost by default);
   - **every other player is dead**: the traitor wins;
   - **the level ends with the traitor alive**: they got away.

   The traitor is revealed to everyone in every case. A player killed by another player is announced as "not the traitor".

Your role stays in the corner of the screen for the rest of the level.

**Everyone needs it**: to see the mark on the tram's screen, their role and the messages. The tram only offers a traitor job when everyone in the game has the patch, in a version that can play with the host's (the host is told who doesn't), so nobody can be a traitor without knowing it. If someone without it joins the tram after that, the lever takes everyone to a normal level instead. The host's settings count. It needs **3 players** (with fewer, the tram doesn't offer it).

## Install
Use the [installer](../Installer/) (Windows) or `python3 linux/mimesis-patches.py install Traitor` on Linux (see the [Linux tool](../linux/)).

## Settings (host)
In **Patches > Traitor**:

| | |
|---|---|
| **Traitor destinations** | **Off**, **Rare** (15 % of the tram's rolls), **Often** (35 %, default), **Always** |
| **Tram locked at the start** | **No**, **1:30**, **2:30** (default), **4:00** |
| **Reward for the traitor** | **Small** (15 % of the stage's repair cost), **Normal** (25 %), **Big** (40 %) |
| **Fake players take part** | Testing with [FakePlayers](../FakePlayers/): fake players count as players |

Works alongside [Deathmatch](../Deathmatch/): the two never mark the same destination.

## Uninstall
Use the installer's **Uninstall selected**, or delete `TraitorRuntime.dll` from the game's `Mods` folder.

## AI disclosure
This mod was made with the help of an AI coding assistant (Claude, by Anthropic): code, documentation and icons.
