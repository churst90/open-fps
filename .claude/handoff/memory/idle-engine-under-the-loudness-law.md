---
name: idle-engine-under-the-loudness-law
description: Loudness.Place compresses the DECLARED level (x0.45); an engine idling 25 dB under its full-load declaration got no lift and rendered 11 dB under a 59 dB AC unit
metadata:
  type: project
---

2026-09-22, "I start the car, get out, and it doesn't sound like it's running". Client telemetry
proved it idled at ~750 rpm the whole time. Worked through: hatchback declared 94 dB, idles 65-68;
Place(94) gives gain 0.36 widened to 0.136; an AC window unit (59 dB) gets gain 0.059 but its ring
is at its own declared level. At 3 m the AC rendered -48.6 dBFS and the idling car -59.4.

Fix: `EngineVoiceState.CompensateLevel` (live voices only — provider sets it; offline renders and
tests stay in true pascals) lifts by (1-0.45) x (declared - current level), smoothed 0.5 s, capped
20 dB. Measured: idle lifted 14.4 dB (law says 16).

**How to apply:** any source whose LIVE level wanders far below its declared level (idling engines,
anything with a duty cycle) is under-placed by the mix. Check the placement arithmetic before
blaming the synth — see [[audibility-ranking]], [[the-mixer-does-not-use-the-loudness-law]].
