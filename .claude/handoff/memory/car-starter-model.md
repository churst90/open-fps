---
name: car-starter-model
description: "10-02 starter rebuilt over 6 rounds; round 5/6 approved by renders; what each of Cody's words turned out to mean; unheard in game"
metadata:
  node_type: memory
  type: project
  originSessionId: 30d35a67-deba-4f21-b6ec-8486f88764ed
  modified: 2026-10-02T22:37:59.629Z
---

Car starter (EngineSynth `CrankWithStarter`, `StarterSound`), 2026-10-02. Round 5 approved by Cody
("much much better"), round 6 = noise 5 dB lower, now the game's. Not yet heard IN GAME.

What his words meant (see [[synthesis-by-listening]]):
- "way too high pitched / squeaking" = pure-sine gear mesh sweeping ~2 octaves per compression
  (crank speed swung 140-520 rpm) plus a 15 % pulse-train brush comb. NOT the octave.
- "the very last note is very high" = pinion tone dragged to idle speed while the key was held.
  A real one-way clutch overruns; gears unload; motor winds DOWN.
- "cranking too fast" = engines ran at ~500 rpm against 250 declared (stall torque >> drag).
- "puffs of white noise, no tone" = round 4 all noise; real starts have lines 8-14 dB over the
  noise in a 5 Hz bin (ring mesh 380-830 Hz, armature slots ~2-2.5 kHz).
- "v8 can't hear from curb" = starter summed into the block and leaked through the bay (-16.5 dB).

Model now: armature inertia + one-way clutch, pink noise 250 Hz-6 kHz, mesh + 11-slot whine,
engage click/clunk, CrankingRpm 200, key off at 1.5x cranking, under-car path 0.5, seat path
18 dB low-passed 150 Hz. Mowers: magneto (1 rev), 600/300 rpm.
References: inbox/starter-2026-10-02/round4/real/ (6 Freesound starts). Instrument:
`StarterProbe` (STARTER_WAV_DIR, STARTER_TONE env). See [[render-check-before-sending]].
