# Hallucinations changelog

## Unreleased
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
