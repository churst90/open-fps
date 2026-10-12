---
name: diesel-combustion-model
description: What makes a diesel sound like one in this model — ignition delay, the premixed fraction, and bore-determined knock
metadata:
  type: project
---

Rebuilt 2026-09-16 from research, after measuring that the model's diesels had NO clatter at idle
(truck idle was -40 dB at 0.8-3 kHz, where a real diesel at idle is almost entirely that band).

**Ignition delay is the parameter a diesel is built around.** Combustion noise comes from the
PREMIXED phase: fuel sprayed in has to break up, evaporate, mix and reach autoignition temperature,
and everything injected during that wait goes off at once. The model had no delay at all —
combustion started the instant the injector was commanded. `IgnitionDelayDegrees` is the usual
Arrhenius form (inverse in pressure, exponential in reciprocal temperature); note the delay in
DEGREES grows with rpm even though the delay in time shrinks.

**The premixed fraction was the constant `0.22f`.** It is `delay / (delay + injectionDuration)` now,
and that single ratio gives the defining diesel behaviour free: clatters at idle (injector open a few
degrees, delay longer than that, nearly all premixed), smooth and hard under load (injector open
forty degrees into hot boosted air). Nothing declares it.

**Knock is the gas's transverse modes, not a high-pass.** Draper's Bessel roots — 1.841, 3.054,
3.832 times c/(pi D) — so a 102 mm Cummins knocks at 5.2 kHz and a 116 mm bus at 4.5: the big engine
knocks DEEPER and no preset says so. Two traps, both caught by ear: ONE mode struck at the firing
rate is a NOTE and not a knock (use three, incommensurate), and a FIXED frequency is also a note —
real knock chirps down a third of its frequency as the gas cools 2,500 K -> 1,200 K within the ring.

**Throttle turbulence is a PART-THROTTLE sound** and must be scaled by the pressure drop that makes
the jet, or a diesel — which has no plate — gets the turbulence of a throttle it does not have.

See [[turbo-is-missing-from-both-gas-paths]], [[engine-bench-truth]].

**2026-09-25 (commit after 17ab05c): clatter rings the BLOCK.** Cody: bus "zippy, too harsh, fake".
Measured: knock peaked 2-4 kHz (gas modes), valve clicks rang their own pole at 3.1-4.4 kHz louder than
the knock. Research (Priede structure attenuation, AVL curve, Sandia OSTI 1123537): radiated clatter
peaks ~1 kHz, -5 @2k, -18 @4k, -45 @8k; gas mode is a faint zing. Now knock + valve impacts drive one
structure-mode bank (620/800/1300/1800/2700 Hz at 125 mm bore, 6 ms), 2-pole LP 4.5 kHz, gas at 0.05.
Knock gain anchored at FULL LOAD (DeclaredSourceLevel test); valves -6 dB. `--tap-balance knock` dumps
engine/noknock/novalves; scratchpad octdiff.c gives the octave levels of the difference. Unheard.
