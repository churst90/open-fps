---
name: idle-limit-cycle
description: "Which presets cannot idle and why — a dilution cliff in DecideCharge plus a governor slower than the crank; measured 2026-09-18, not fixed"
metadata: 
  node_type: memory
  type: project
  originSessionId: f889a6f5-7730-449f-8ecf-50a871eb8591
  modified: 2026-09-18T18:27:29.945Z
---

Measured 2026-09-18 with `--engine-trace <preset> idle sec=10`, all 16 presets, rpm sampled every
0.25 s after the first two seconds:

| preset | target | mean | min-max | sd | burn q | residual |
|---|---|---|---|---|---|---|
| nascar_v8 | 1300 | 1935 | 814-2912 | 620 | 0.74 | 0.39 |
| v8_muscle | 720 | 858 | 559-1205 | 177 | 0.56 | 0.44 |
| v10 | 750 | 767 | 492-1070 | 176 | 0.86 | 0.35 |
| f1_v10 | 4000 | 4022 | 3812-4471 | 113 | 0.70 | 0.42 |
| v8_flatplane / i4_sport / v8_sports | | | | 58-77 | ~0.9 | 0.35 |
| everything else (diesels, i4_economy, i6, v12, v6, vtwin, boxer4) | | | | 14-55 | 1.00 | 0.05-0.26 |

**The mechanism is one thing.** `DecideCharge` collapses burn quality between 33 % and 46 %
residual (`mean = 1 - 0.9((dil-0.30)/0.25)^1.5`, plus a bimodal cut under q 0.45). Every bad idler
sits ON that cliff, and the loop round it is: rpm high -> shut plate pulls MAP down -> more reversion
through the 80 deg overlap -> residual past 0.4 -> misfires -> torque negative -> rpm falls -> the
leak and bypass refill the plenum, MAP rises, residual falls -> clean burn -> +130 Nm into a
0.15 kg m^2 crank -> 2,900 rpm. Period about a second on the stock car. The reversion also FILLS the
plenum with burnt gas (MAP 0.6-0.7 with the plate shut), so MAP is high and useless at the same time.

**Knobs tried on nascar_v8:** `gov=8` no change (sd 622) — the todo's "raise IdleGovernorGain" will
not fix it; `inertia=0.35` sd 109 (1198-1638) but q still 0.66 — the crank stops overshooting, the
combustion is still on the cliff; `idlerpm=1800` sd 236. The F1 holds its rpm (governor fine) but
burns at 70 % with 42 % residual: crest 4.9, blop 36 %, chop 21 % — a misfiring idle, not a lopey one.

**How to apply:** this is a combustion-model question (how residual degrades a burn: real SI engines
tolerate ~25-30 % EGR and fall off gently, not a 1.5-power cliff) and a plant-vs-governor bandwidth
question. It is not a gain to nudge and not a per-preset number. Do not change `IdleRpm`s to hide
it. See [[engine-synthesis-state]], [[engine-bench-truth]], [[dont-respec-what-was-approved]].
