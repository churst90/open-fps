---
name: the-reverb-unit-was-rendering-no-tail
description: FMOD EARLYLATEMIX 0 means early reflections ONLY, not "early off" — every room had no tail
metadata:
  type: project
---

FMOD's SFXREVERB **EARLYLATEMIX** is the blend of **LATE REVERB TO EARLY REFLECTIONS**: 0 is all
early, 100 is all late. `AcousticConstants` set it to **0**, written to mean "the unit's own early
reflections are off". It means early reflections and **nothing else** — no tail, in any room, however
carefully that room's decay was surveyed, written to its own bus, and fed.

Heard as *"just sounds like a metallic box, short reflections are not in a parking garage"* and *"all
the reflections sound the same, there's no real variation at all"* — which is an exact description of
a fixed stamped early-reflection pattern with the room taken out from behind it.

Measured with `--tailcheck` (written for this), one footstep, room configured for 6000 ms:

| EARLYLATEMIX | mix 2 s after the step |
|---|---|
| 0 (shipped) | −102 dB — the noise floor |
| 50 | −32 dB |
| 100 | −28 dB |

Fixed 2026-09-19: `ReverbLateToEarlyMixPercent = 100`, **named after the parameter it writes, because
the trap is the name**. Pinned by `ReverbTailTests`.

**Why:** three separate correct fixes upstream (region routing, per-bus decay, measured surface area)
all landed on a unit that had been told to render no tail, so none of them could be heard — and each
one measured correct, which is what made it take so long.
**How to apply:** when a parameter's NAME states an intent and the API states a scale, trust the
scale and measure the output. See [[your-own-footsteps-reverberated-outdoors]],
[[fmod-must-not-virtualise]], [[audio-lab-instruments]].
