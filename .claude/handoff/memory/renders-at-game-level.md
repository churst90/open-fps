---
name: renders-at-game-level
description: "Listening renders for Cody must be at the level the game plays them (loudness law, default /levels 45 %, master), never raw physical scale"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 568c4779-136f-4fbd-b7f7-35b87bd6f916
  modified: 2026-10-06T02:28:11.649Z
---

2026-10-05: Cody heard the car-front renders and said he could "hardly hear it at 2 metres from the
tail pipes", even the police interceptor. They were written at raw scale (small-car idle -55 to
-65 dBFS RMS). The rain agent's renders mapped 94 dB SPL to -6 dBFS (street rain about -55 dBFS) —
caught before he heard them.

**Why:** he judges by ear on headphones; a raw-scale file sounds broken and wastes a listening round,
and he then suspects the game's loudness law.
**How to apply:** every brief for an agent that renders for Cody says: render through the game's
loudness law and default /levels with the master, one consistent gain across a set, stated in the
README. Before telling Cody a folder is ready, measure the files' RMS/peaks myself (numpy venv
/home/cody/drumsynth/venv/bin/python) and check for clipping and runs of exact silence (the thunder
"breaks up" was hundreds of >=5 ms zero gaps). See [[render-check-before-sending]].
