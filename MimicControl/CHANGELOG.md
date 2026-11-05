# MimicControl changelog

## 1.0.0
- Dead players control the mimic they possess: walk, run, look and grab living players until it dies. No time limit and no cooldown.
- Host options: on/off, attack cooldown, attack strength, one mimic per death, delay after death, block after a self-inflicted death. Published to players in the lobby data.
- Only the host and the possessing player need it; with a vanilla host or a vanilla possessor, possession works as in the game.
- Fixes for possession leaks that would leave a mimic frozen: being revived or leaving the game while controlling a mimic releases it.
