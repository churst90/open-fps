---
name: per-wheel-physics-wip
description: 10-01 stage 3 per-wheel physics paused (agent stopped for Cody's usage); branch worktree-agent-aa7562d5d1b3be292 has 3 commits on OLD base 1e7a57af; full suite never run; resume = test then merge
metadata:
  type: project
---

Cody 2026-10-01: start per-wheel physics (docs/NEXT_BODIES_WHEELS_ROADS.md stage 3). An agent built it in
worktree .claude/worktrees/agent-aa7562d5d1b3be292, branch worktree-agent-aa7562d5d1b3be292:
bf9b9969 per-wheel model + every preset's running gear with sources (Chassis.cs, RunningGear.cs; NHTSA
Heydinger SAE 1999-01-1336 class figures; lock from published turning circles), e54f5d22 traffic and the
player's car on their wheels (LineFollower.cs, Stanley law) + each wheel on the wire, 50114568 changes/plan.
STOPPED before the full test suite, at Cody's request (usage). It built.
Resume: (1) run the full suite on the branch (~50 min; traffic tests Crosswalk/CarFollowing already fail
on main); (2) branch base is 1e7a57af, BEFORE the 09-30 merges (walls 7d966d98, cleanup, mixer safety,
pathing off, arrival speech) — merge main into it, expect conflicts in ClientAudioSystem, FmodAudioProvider,
EngineProcessor, ClientWorldState (Clear signature changed), VehicleSynth; (3) wire format changed:
rebuild client+server together and restart the server. Then stage 4 (tyre source per wheel).

**MERGED into main 10-01 (0dbdcfca), unheard.** Full suite on the merged branch: 1452 pass; failures only
load-timing (EnclosureCost, EngineProcessor budget — fail identically on main under Stryker load) and
CrosswalkTests (fails on main too). CarFollowing junction test now PASSES. BrakeSquealTests updated: the
school bus (Blue Bird Vision) has hydraulic discs per its spec sheet, so its squeal is disc-pitched now.
Server restart + client rebuild needed (wire format). Next: stage 4, a tyre source per wheel.

**10-01 later, merged 966e8ae7 (unheard):** "skidding / slamming brakes" was CORNERING, not braking: cars took
junction turns at 0.46-0.6 g, fronts at 0.8-0.95 of grip (client squeals >0.78). Fix: AASHTO Green Book
comfortable side friction (LineFollower ComfortTurnSpeed), BendSpeedWithin reads the tighter end, braking+
cornering share an ellipse, ACC replaces IDM braking. Probe: OpenFPS.Tests/TrafficBrakingProbe.cs
(OPENFPS_PROBE_OUT). Traffic ~5 % slower. Per-wheel squeal (VehicleSynth.WheelSqueal, brush model sliding
share) out through the existing front/rear taps, no extra voice; rolling noise still per axle; L/R only
level. Listening set inbox/tyres-2026-10-01. Flag: in a fast turn the INSIDE front squeals first (load
sensitivity) — Cody to judge. BusBoardingTests loosened to accept "still moving" refusal.
10-01: Cody: wheelspin demo "sounded like white noise, not skidding". Past FullSlide the squeal was handed to
broadband noise on every surface. Fixed c882b5fe: RoadSurfaces.StickSlipOf (asphalt/concrete 1 = keeps the
stick-slip note when fully sliding; gravel/dirt/snow/ice 0 = noise), per wheel. Wheelspin flatness 0.25->0.04.
Binaural demos: AudioLab --wheel-squeal binaural (pavement 7.5 m, SA HRTF, no ground/room). Python with numpy:
/home/cody/drumsynth/venv/bin/python (system python has none).
**APPROVED by ear 10-01:** the stick-slip squeal (wheelspin + hard stop, binaural set) "sounds much better now". Fast-turn inside-front-first not commented on.
