---
name: three-undeclared-duplicates
description: A constant attenuating a path that already had a declared one — found three times (block x0.4, FrontMix 0.35, and the intake's missing airbox). The pattern to look for whenever a layer is inaudible.
metadata:
  type: project
---

Same fault, three places, found 2026-09-20/21. **A hard-coded multiplier sitting in front of a path
that already had a declared, per-vehicle description of itself.** Each one was invisible on a loud
car and decisive on a quiet one, which is why none was caught by ear.

1. **The block**, `Block * 0.4f` inside the front tap — on top of its declared
   `EngineBayLeakage`. Two routes out of the engine for one mechanism. Removed; the block's route
   is the bay and nothing else. Worth 17 dB on a bus, nothing on a muscle car.
2. **`FrontMix = 0.35f`** on the intake — nine decibels, on top of `IntakeSpec.Level` (an escape
   fraction) and the airbox's own loss. Now 1.
3. **The airbox had no silencer at all.** The exhaust side has a muffler model, so silencing an
   exhaust works; the intake had only `Level`, and no preset set it low enough to stand in for one.
   Result: *every* car with a silenced exhaust radiated MORE from its airbox than its tailpipe
   (economy four +8 dB, V6 +11, road police +10) where a real one is 8-15 below. A car you hear
   mostly through its airbox is a car with velocity stacks on it — reported as "the police cruiser
   still sounds like a loud nascar".

**A blanket −12 dB anchor was tried for (3) and was WRONG** — it moved the race engines, which were
never the problem, and broke `TheIntakeVoiceCarriesSomethingWorthHearing`. What separates the cases
is geometry, so it is read off geometry: `IntakeSpec.AirboxLossDb` derives the expansion-chamber
transmission loss from the box volume and the snorkel bore,
`TL = 10log10[1 + ¼(m − 1/m)² sin²(kL)]` averaged over frequency, m = A_box/A_snorkel, box taken as
cubic. Gives an economy four 13 dB and a big-block with an open element 4 — and declares nothing
per preset.

Result: every preset now has its intake BELOW its exhaust. The approved loud cars moved ≤0.2 dB
(v8_muscle 121.9, police_v8 132.0, f1_v10 132.0, nascar 127.4) because they were always
exhaust-dominated. 788 tests pass.

**How to apply:** when a layer is inaudible or a quiet machine will not come up, grep the render
path for a bare float multiplying it and ask what declared quantity already says the same thing.
`--voice-levels parts` is the instrument: it meters the three outlets where they are made and the
rest by muting, so a layer that is present-but-worthless shows up as a sub-decibel column.

See [[the-block-had-no-anchor]], [[live-voice-is-not-the-bench]], [[no-special-cases]].
