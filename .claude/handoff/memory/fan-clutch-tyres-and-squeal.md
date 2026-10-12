---
name: fan-clutch-tyres-and-squeal
description: "09-25 the diesel \"whoosh\" was fan + unanchored tyres; CoolingSystem clutch, tyres in pascals, squeal seed hashed; unheard"
metadata:
  node_type: memory
  type: project
  originSessionId: 53f7c168-2e82-4337-ae6c-6ef4f8dc6ae6
  modified: 2026-09-25T23:40:10.238Z
---

Cody 2026-09-25: "trucks are all fan", audible blocks away. Measured with `--tap-balance whoosh`
(band levels, per-part mutes, raw dumps via OPENFPS_WHOOSH_DUMP; scratchpad noiseshare.c/impulse.c
split harmonic vs noise and kurtosis):
- the 1-4 kHz hiss on the semi was half cooling fan, half block via the bay; jet, air, steepening,
  turbo and valves were worth ~0 dB. The block's band is impulsive clatter (kurtosis 30-47); the fan
  filled the gaps into a steady rush.
- the real reason it stood out: tyre rolling noise in the live voice was in ARBITRARY units, 10-15 dB
  under real, so no car made broadband roar.

Built (commit 60dc94b, not pushed, UNHEARD):
- `CoolingSystem` heat balance → `FanClutchSpec` (OnOff truck, Viscous bus). Cruise: disengaged at
  0.2 speed; a long full-load pull (~40 s) engages. Ambient fixed at 20 C (world.Temperature not wired).
- `TyreProfile.ReferenceDb` = ONE tyre at 20 m/s, 84 dB (truck 85, slick 82); `VehicleProfile.TyreCount`
  (bikes 2, bus 6, semi 18). Rolling roar band-limited (HP 400 Hz + two-pole LP) to CNOSSOS shape.
  Pass-by 7.5 m at 43 km/h: cars 66 dBA, semi 72.6, bus 71.5. Aircraft wheels keep the old path.
- i4_economy and diesel_i4 re-declared 94 → 99.5 (tyres at redline speed).
- Brake squeal: seed was the entity id unhashed → neighbours sang within 11 Hz; now hashed (Mix).
  Drums 60 %. The city bus (entity 5935) still doesn't squeal: dice.

**Why squeal is rarely heard:** it only sings under 4.5 m/s at 0.4-3.5 m/s² decel, i.e. the end of a
gentle stop. On the city only parking (1 per 90 s city-wide) and the one bus stop gently; hard brakes
are too hard. The real fix is cars stopping at signals — see [[street-life]], [[route-stops]].

**How to apply:** `--tap-balance squeal <keys>` and `seed=N <key>` check squeal; if Cody hears tyres as
too loud inside cabins, that path (atPanels) scales with the anchored tyres too. Related:
[[air-blocking-and-pipe-radiation]], [[the-block-had-no-anchor]].

**Later 09-25 (commit 07adbf5, unheard):** Cody's "pitch steps hard" truck was GEAR HUNTING, not the
fan: light-throttle upshift (0.85 x torque-peak rpm, 1020 on the semi) sat under the 1100 downshift.
Six presets hunted (i4_turbo 103 changes/20 s). Upshift now must land > 1.1 x downshift;
GearHuntingTests covers every preset. Telemetry "gear 0" at a steady speed = mid-shift = hunting.
Aircraft flange: twin turboprop trims ±0.5 % = two sliding pulse trains = flanger → Synchrophased;
piston prop now driven from CrankAngle (BladeRow.Driven). `--aircraft steady` = flange index.
Helicopter: main/tail harmonics near-coincide (170 vs 170.7 Hz, 9.8 dB swing). Real 206 speeds; the
flaw is the main rotor's Gaussian pulse keeping its 13th harmonic (Gutin says ~-200 dB). Fix would
change the shared BladeRow (props, truck fans, mowers; approved by ear) — ASKED Cody, not done.
BirdLifeTests.AHedgeOfSparrows flaked once under a loaded full run; passes alone.
Commit 4c0e5f0: downshifts rev-match in neutral; PlaceAtSpeed picks the cruise gear (no burst at voice
start); riding: client FollowRide turns heading with the vehicle, ears follow the rider, exit faces the
vehicle heading; StreetLife.GunfireEverySeconds (walkers fire 2-3 rounds, city 120 s — seen live);
planes removed from the city except the helicopter.
