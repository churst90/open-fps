---
name: live-voice-is-not-the-bench
description: --engine-levels measures the TAILPIPE; the game plays the whole vehicle. Where those differ, SourceLevelDb is wrong in both directions at once. Use --voice-levels.
metadata:
  type: project
---

Found 2026-09-20 chasing "the buses are quiet". `--engine-levels` renders `VehicleSynth` offline and
reports **exhaust + 0.35 × intake**, and every `VehicleProfile.SourceLevelDb` was set from it. The
game plays `EngineVoiceState`, which is that PLUS the body ringing, the engine bay, the cooling fan,
the tyres and the air system.

On a car the two agree to a decibel — a car IS its exhaust. On a **turbocharged bus** they were
**12 dB apart**: the turbine eats the exhaust, so the tailpipe is the quietest part of the machine
and the block, intake, fan and body are eleven of its twelve decibels over it. Measured:

| | tailpipe bench | live voice |
|---|---|---|
| school_bus (turbo) | 85 | 89 |
| school_bus_na | 98 | 93 |

So the bench saw 13 dB between them and the game plays 1.3 dB.

**A mis-declaration never cancels.** `SourceLevelDb` sets BOTH the placement gain and
`PascalsAtFullScale`, and they pull opposite ways: rendered ∝ live − 0.55 × declared. Declare a
vehicle Δ too LOUD and it plays 0.55Δ too QUIET. That is why the buses (declared +5) were inaudible
and the trucks (declared −4) were fine.

Corrected from the live voice: diesel_truck 93→97, school_bus 85→89, school_bus_na 98→93,
v6 100→105, diesel_i4 88→94, v12 128→124. Everything else was already within 3.5 dB, the approved
race cars inside 0.3.

**How to apply:** `--voice-levels [preset ...]` prints declared / live / diff and what the bay, fan
and tyres are each worth (by muting and differencing). `--voice-levels <p> sweep` gives the curve
against rpm plus the tailpipe alone, which is what separated "quieter voice" from "metered at the
wrong rpm". The test `DeclaredSourceLevelMatchesTheLiveVoice` now holds the line; the old offline
assertion was removed and says why. Meter only where the throttle is open — a bus's 0.9 s shift sits
inside the rpm window with the throttle shut and costs several decibels.

See [[audio-lab-instruments]], [[engine-bench-truth]], [[dont-respec-what-was-approved]].
