---
name: turbo-is-missing-from-both-gas-paths
description: A turbo sits in both gas paths; the compressor barrier (intake) and the turbine stage (exhaust) both exist now — do not re-add either
metadata:
  type: project
---

A turbo sits in BOTH gas paths and the model had it in neither. Found 2026-09-16 chasing a fixed
tone Cody heard on every lift and gearshift.

**Intake half — FIXED.** The intake runner's quarter-wave (0.22 m -> 386 Hz on the school bus) was
radiating straight out of the snorkel. It is a FIXED frequency, so the ear hears a note, not an
engine; and it is a DIESEL problem because the term carrying plenum pulsation to the airbox is
scaled by the throttle's open area — a petrol engine at part throttle passes almost none, a diesel
is wide open for ever. On the overrun the fuel cut removes the combustion that was masking it.
`IntakeNetwork._compressorBarrier` is 20 dB of insertion loss on PULSATION only (mean flow
untouched) for any non-NA engine. That is why a turbo intake is a whoosh and a whistle rather than
induction honk — and why the whistle survives: the compressor makes it downstream of its own barrier.

**Exhaust half — DONE (verified in code 2026-09-18).** `ExhaustNetwork.Turbine` sits between each
collector and its downpipe on `Induction.Turbocharged` only (a blower has nothing in the exhaust):
one-pole at 260 Hz, passes 0.34 of a low wave, reflects 0.5 of what it stops back up the manifold,
the rest is shaft work. The Cummins preset comment still says "KNOWN GAP: no turbine" — that comment
is stale, the code is not.

**How it was found, which matters more than the fix:** six wrong suspects, and every measurement I
made agreed with me each time — I kept measuring the firing fundamental and calling it a ring. What
found it was Cody A/B-ing rendered stems by ear and noticing the tone SURVIVED muting the exhaust.
When a listener's description and the instruments disagree, mute sources one at a time and play them;
do not keep refining the measurement. See [[synthesis-by-listening]], [[engine-bench-truth]].
