---
name: ground-reflection-and-flutter
description: 09-25 ground reflection lives INSIDE each voice (echo voices can't be <46 ms); flutter for outdoor impulses; outdoor impulses skip the reverb; riding heading locked to vehicle
metadata:
  type: project
---

Built 2026-09-25 (commits a8a945b, 8428b93), UNHEARD in final form:
- **GroundReflection** (Client.Core/AudioEngine/Acoustics): per voice delay line + two-band surface
  reflection, applied in EngineVoiceState.Consume, EngineTapState, PhysicalVoiceState.Consume. Game
  thread `ClientAudioSystem.ApplyGround` casts 2 static-only rays (bounce point, material). It CANNOT
  be an echo voice: EngineEchoState.MinDelayBlocks = 2 blocks (~46 ms), ImageSource drops <12 ms.
- Cody: "very nice, maybe a little too strong" → research: +6 dB bass is right; a car is NOT one
  point — tyres (2 cm) reflect in phase, only pipe/block (0.3 m) comb. Voice measures tyre share
  (NearGroundShare) → that share added undelayed. High band falls with range (asphalt grazing Q),
  Clifford-Lataitis turbulence coherence (mu2 5e-6, L 1.1 m). Whole-car notch at 10 m: few dB.
- **Flutter** (EarlyReflections.FindFlutter, `flutter: true`): outdoor impulses only (Knock,
  <=1 s). Facade faces clustered into WALLS (same normal, within 1.5 m, >=150 m² — storeys and
  shopfronts); street = nearest facing pair; orders 4..16, each crossing must land on a building;
  legs tested only against solids BETWEEN the walls (was 30-160 ms, now ~7.5 ms/shot). Main Street
  z -18..-26 east side is the open garage: no flutter there, correctly.
- Outdoor impulses no longer send to the reverb unit (room tail made shots "fired in a hall").
- Riding: heading = vehicle heading every frame (FollowRide), ReconcileYaw skipped while Riding;
  exit faces vehicle heading; proximity probes off while riding.
- **Engine flutter (sustained sources) NOT built** — offered; 09-23 ghost washes are the risk.
- 50-65 voice STARVES/s from 21:20 on 09-25 while Stryker ran on all cores (again) — suspect for
  Cody's "really bad over sampling" on one car; not confirmed.
See [[higher-order-reflections]], [[reverb-needs-enclosure]], [[fan-clutch-tyres-and-squeal]].

**Later 09-25 (5e0e819):** the V8 crunch #1 was the ground BEFORE the SoftCeiling (fixed 80ea7e3);
crunch #2 was STARVES: EngineRenderPool was cores/4 capped 6; engines cost 0.1-0.23 core each
(`--tap-balance cost`), city needed more. Pool now cores/2 capped 12; ChooseLiveEngines sheds
machines then cars when producer starves > 5/s. Emitter stage 150-250 ms (every source froze →
"reflections step away"): now logs a breakdown (echoes/ground/birds/horns+sirens) on the
"Audio placement" line — READ IT next session. Ground rays cached per voice. Master trim 6 → 2 dB,
limiter ceiling -1 → -2 dBFS (no lookahead; meter saw +1.6 dBFS), makeup 10 → 7.
**THE 8-BIT CRUNCH (fixed 09-26):** GroundReflection read at `float pos = _w - _delay` (_w = running
sample count) → exact only to 2^24 samples = 6.3 min, then the read point snapped to 2/4/8-sample steps.
Every car went crunchy "after a period". Integer whole + float fraction now; test reads 63 vs 125.5 on
old code. RULE: never put a running sample counter into a float.
