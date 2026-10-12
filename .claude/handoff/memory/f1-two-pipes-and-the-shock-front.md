---
name: f1-two-pipes-and-the-shock-front
description: "Why the F1 is a siren — the two tailpipes summed at one point cancel the bank fundamental 12-16 dB; and why it breaks above ~12,300 rpm — the steepened shock front vs the sample grid, not aliasing of firings (2026-09-18, measured, not fixed)"
metadata: 
  node_type: memory
  type: project
  originSessionId: f889a6f5-7730-449f-8ecf-50a871eb8591
  modified: 2026-09-18T18:28:53.324Z
---

Cody: "no low end, too high pitched, when it revs it sounds like a siren". Measured 2026-09-18.

**1. The two tailpipes are summed coherently at one point** (`ExhaustNetwork.Step`: `Radiated = sum of
branches`). An even-firing V10's banks are exactly anti-phase at order 2.5 (the BANK firing rate, the
true fundamental of each pipe), so the sum cancels it and leaves order 5. Measured with a temporary
`OPENFPS_ONE_BRANCH` build (one pipe x2): order 2.5 reads **0 dB** against order 5 on one pipe and
**-12 dB (12,000 rpm) / -19 dB (8,000)** on the sum; level 141 vs 137 dB. That is the octave the ear
is missing: the perceived pitch of {2.5, 5, 7.5} is order 2.5; of {5} alone it is order 5. A real car's
pipes are ~0.8 m apart, so for any listener off the centre line the path difference is a good part of a
wavelength at 500-650 Hz and they do NOT cancel. The V8s are barely affected (their bank patterns have
no such symmetry; NASCAR half/whole is +8 dB). **FIXED the same day:** `ExhaustSpec.TailpipeExitsMetres`
+ `ExhaustNetwork.SetListener(machineFrame)` — per-branch path delay through AIR (343, not gas), slewed
0.5 %/sample, spreading ratio only inside 1 m; the game feeds it from the emitter heading in
`FmodAudioProvider.ListenerInMachineFrame` (local +x = up cross forward, which is what
`Vector3.Transform(slot, rotation)` uses). Null exits / no listener = the old sum bit for bit, so
declared levels did not move. Lab: `azimuth= dist= pipe=`. Cody heard the six-clip bracket
(before / behind / 30 / 60 / left pipe / right pipe) and approved: "definitely the newer model sounds better".
That approved sound is the new baseline for the F1 (see [[dont-respec-what-was-approved]]). Uncommitted as of
the verdict; commit only when asked.
Open from it: the V8 true duals could declare rear-corner exits (a listening decision, not a fix);
the bench default is still the centre line, which is the cancellation null for symmetric pipes.

**2. Above ~12,300 rpm the render is erratic** — `structure` 50.8 at 12,159, 20 at 12,466, 15 at
12,751, 46 at 13,056, 18 at 13,170, 30-34 from 14,000 up; port peak 0.9 -> 2.0-2.4 bar past 14,500.
The FormulaCar is geared to 330 km/h on the limiter, so on the speedway straights it lives in this band.
RPM is dead steady in the window (12,758 +-0), so it is real between-order energy, not drift.
Eliminated one at a time: aliasing of firings (`--engine-alias` at 12,000: 50/54/**-7** dB at
44.1/88.2/176.4 kHz — oversampling makes it WORSE, so the preset comment claiming 176 kHz "gains
nothing" was wrong), jet noise (`jet=0`), crank inertia, torque/heat (non-monotonic), valve size, primary
diameter, junction flow loss (`flowloss=0.4`: 16), cam duration (`exdur=270`: 16), the exhaust
volume-velocity cap (a scratch `OPENFPS_EXH_CAP` build: 0.6 -> 1.2 unchanged).
What moves it: `wall=3` 15 -> 41.5, `wall=6` -> 42.7; `steep=0` -> 37. Both act inside `WaveLine`.
**Done the same day (partial):** `WaveLine.Write` now merges crossed arrivals into one equal-area jump
(Whitham) at the mean of the arrivals they wanted, rewrites the slots behind it, splits the jump across
one slot. Repairs 12,300–13,300 (15 -> 26, 18 -> 41 dB) and leaves every approved preset within 0.1 dB;
15,000 is WORSE (32 -> 19). `OPENFPS_SHOCK_FRONT` is gone. The alias bench is band-limited to 20 kHz now
and still says the model is unconverged (49 / 52 / −13 dB at 44.1 / 88.2 / 176.4 kHz at 12,000 rpm):
something integrates differently at 4x the rate, and that is the lead for the top end — find what changes
between 88.2 and 176.4 where 44.1 is clean.

**3. Two declared parameters the synthesis never reads:** `ExhaustSpec.PortNoiseLevel` and
`EngineProfile.EvoTemperatureK`. Bisecting on them proves nothing.

Instruments used: `--engine-orders <p> rpm=a,b,c [knobs]`, `--engine-alias <p> rpm=N`, `--engine-trace
<p> rpm=N thr=1 sec=14`. See [[engine-bench-truth]], [[idle-limit-cycle]], [[dont-respec-what-was-approved]].
