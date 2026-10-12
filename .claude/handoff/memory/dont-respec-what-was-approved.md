---
name: dont-respec-what-was-approved
description: Cody approved one change and I made twenty; bisecting proved him right. Change only what the new constraint forces.
metadata:
  type: feedback
---

2026-09-16. Cody approved the F1 V10 held at 2,000-7,750 rpm — the EXISTING engine with two numbers
overridden. I then wrote a new engine from scratch (6.0 litres, different bore/stroke, cams, valves,
pipes, compression, torque) on the argument that an engine turning half as fast should be a bigger
one, and presented it as the same decision. He rejected it: *"white noise coming through a pipe"*.

Bisecting one parameter at a time proved him right: the six-litre step ALONE cost 13 dB of structure
(58.8 -> 44.8 dB) and nothing downstream recovered it. Reverted entirely.

**Why:** when a listener approves a sound, that sound is the baseline — changing twenty things in the
name of two throws away the only fixed point the tuning has.
**How to apply:** change what the new constraint FORCES and nothing else, then measure, then play it.
If a forced change has a side effect (halving the crank speed forces a milder cam, which costs the
idle its lope — crest 4.9 -> 2.5), say so rather than fixing it with a second unforced change.
A corollary found the same day: WHICH END a change comes off matters. Cutting valve overlap from the
exhaust lobe advances exhaust opening 102 -> 75 deg ATDC, so blowdown happens at higher pressure and
the band above 3 kHz became the loudest thing in the engine. The exhaust lobe makes the sound; take
overlap off the intake. See [[synthesis-by-listening]], [[no-special-cases]], [[engine-bench-truth]].
