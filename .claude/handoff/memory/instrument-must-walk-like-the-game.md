---
name: instrument-must-walk-like-the-game
description: --room-walk had its own private gait and its own private send number; both hid real faults
metadata:
  type: feedback
---

Three instruments in this engine were found stating something other than what the game does, and each
cost a session:

- **`--room-walk` walked at 1.4 m/s stepping every 0.55 s** — a real human gait, and one the game has
  never taken (4.5 m/s, nine steps a second at the time). It exists to settle "it pops when I walk",
  so a fault that only happens at nine steps a second could not appear in it however long it ran.
  It now runs the game's own `StrideAccumulator` at the game's own speed and pins its footsteps to
  the listener the way `OnOwnFootstep` does.
- **The `Room:` log printed `AcousticConstants.ReverbSendMix`** ("each voice sends 35 %") — a constant
  the send stopped being when it became `Enclosure.ReverberantToDirectPower`. The real figure at a
  metre and a half is 98 %. Now reports the wettest send actually given, and at what distance.
- **Every footstep reported "placed at a position N ms old"** because a voice pinned to the listener
  has no attribute update to be waiting for. A false positive on the commonest voice in the game
  buried the one warning worth reading.

**Why:** an instrument with its own idea of the thing it measures is worse than no instrument — it
produces confident wrong answers, and the next person believes them.
**How to apply:** when writing or trusting a spike, check it drives the SAME model the game does, and
that every number it prints is measured rather than restated. See [[audio-lab-instruments]],
[[gait-from-the-body]], [[synthesis-by-listening]].
