---
name: engine-bench-truth
description: The rev bench and the orders bench both lied; what they now measure and which number answers which question
metadata:
  type: project
---

Fixed 2026-09-16. Three instrument faults that made earlier timbre judgements untrustworthy:

1. **The rev governor had no anticipation** — a fixed 900 rpm band on engines that gain rpm at five
   figures a second unloaded, so every blip on `--vehicle-rev` landed near the limiter whatever it
   was asked for (5,425 -> 9,948). It now governs on PREDICTED rpm, and the delay it leads is the
   whole chain to torque: the slow link is the PLATE (EngineSynth runs the pedal through a 40 ms
   lag), not combustion. Leading only the two crank revolutions was worth almost nothing.
2. **`--engine-orders` analysed a transient.** A primary is a quarter-wave tuned by the speed of
   sound in the GAS, and the gas takes ~7 s to reach full-load temperature, so the pipe DETUNES as
   it heats — 136.8 dB falling to 121.6 at identical rpm/torque/MAP. It settles 12 s now. Any bench
   number from before this is suspect.
3. **A band balance cannot tell a harmonic from a floor.** v8_muscle puts 79 % of its energy below
   80 Hz because its half order IS 40 Hz. Use the new **`structure`** column instead: the spectrum
   sampled ON the orders and a quarter-order off. Clean engines read 50-58 dB; 45 is audibly wrong.

New instruments: **`--intake-ir`** (the intake tract's own modes, valves shut, one thump) and
**`--engine-alias`** (is a redline the engine's or the integrator's — builds the engine at several
sample rates). `EngineOrderSpike.Override` now has a knob for EVERY part of the engine
(bore/stroke/rod/cr/cams/valves/pipes/muffler/crank/induction), and `--vehicle*` takes them too, so
a bisection never needs a rebuild.

**Why:** a bench that reports the wrong rpm or a transient makes every listening test meaningless.
**How to apply:** bisect with the knobs one parameter at a time and watch `structure`. See
[[audio-lab-instruments]], [[synthesis-by-listening]].

**Added 2026-09-18:** `structure` is a Goertzel over the LAST TWO SECONDS at the mean rpm (0.5 Hz bins),
on the orders vs a quarter order off. It is honest only if rpm is steady in that window — check the
`--engine-trace` rpm column first — and it reads any incommensurate energy (a chaotic network, a
jittering shock front) as "floor". `swing` is the 30 Hz envelope's std/mean, not crank ripple: it did
not move when inertia went 0.035 -> 0.3. `wav` on `--engine-orders` writes the analysed window.
`OPENFPS_SHOCK_FRONT` >= 1 disables steepening entirely (not a thicker front) — see
[[f1-two-pipes-and-the-shock-front]].
