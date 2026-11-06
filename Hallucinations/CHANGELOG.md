# Hallucinations changelog

## 1.2.0
- Steered hallucinations: fixes for walking, striking and vanishing; fake players as test targets; none during a deathmatch
- Steered attacks land: on the target's game a steered hallucination now strikes from where its dead player attacked (not from where the network delay had left it) and turns to the player when they're in reach (the steering camera looks at the hallucination, not at the player).
- Steering no longer gets stuck: a hallucination that appeared where the game's physics finds no floor under it (on some floors, slopes and stairs; about one in three in a test session) couldn't take a single step. It now walks on the navmesh where physics misses the floor, and one that's stuck where there's no floor at all is moved onto the nearest floor. If it still can't go somewhere, the log says what blocked it.
- A steered hallucination no longer vanishes when it touches the player: its dead player walks it up to them to strike. (One running on its own still does.)
- It appears on the floor for the dead players too (the target's game sends where it stands, not the raw navmesh point).
- Whether it can see or reach the player no longer counts the player's own body as something in the way (the points it aims at are inside it).
- Testing alone: with Debug keys on, fake players (the new FakePlayers patch, or the game's own dummy players) can be sent hallucinations; the host's game plays their side. Die, watch a fake player whose radiation is above the threshold and press E to steer one at it.
- None during a deathmatch (the new Deathmatch patch): a fight between players stays fair, no fake attacker for the living to waste a shell on. The dead player is told "Hallucinations are off".

## 1.1.0
- Dead players steer the hallucination they send, and every dead player sees the hallucinations
- The dead player who sends a hallucination steers it: the camera follows it, WASD / left stick move it, Shift runs, left click makes it attack (once), E lets go. If they let go, lose the camera, come back to life or drop out, it goes after the player on its own, as before.
- Every dead player sees the living players' hallucinations while spectating (living players still only see their own). Only the target's game decides whether an attack lands.
- Protocol 2: everyone in the game needs this version.

## 1.0.0
- Above the host's radiation threshold (50%), dead teammates can send you hallucinations with E while spectating you: a mimic only you see, out of your sight, that comes at you with a shotgun, a bomb or a melee weapon and vanishes when its attack lands. No damage, no radiation, nothing anyone else sees. Hitting it makes it vanish too.
- E while dead is context-based: it possesses a mimic when one can be possessed (as in the game), otherwise it sends a hallucination. The spectator HUD tip says which, with the cooldown and how your last hallucination went.
- Possession is the game's again (speaking only), but while a dead player talks through a possessed mimic, living players within 10 m of it (on the same side, indoors or outdoors) gain 1% radiation per second, never past 90%.
- Host options: on/off, radiation threshold, cooldown, lifetime, weapon pool (shotgun, bomb, melee), talk radiation rate, range and limit. Published in the lobby data; the host checks every rule.
- Debug keys for testing alone: Ctrl+Shift+H sends a hallucination to yourself, Ctrl+Shift+J adds 10% radiation.
- Replaces MimicControl (never released).
