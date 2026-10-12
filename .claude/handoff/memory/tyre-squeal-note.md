---
name: tyre-squeal-note
description: 10-07 approved tyre squeal model; rounds 1-2 rejected as white noise and gravel
metadata:
  type: project
---

Cody 2026-10-07: "tire squeals are good" (round 3, inbox/fault-fixes-2026-10-06/round3, pushed d9e6a085).
Rejected first: round 1 "white noise, not screeching like tires hitting blacktop" (aircraft gear used the car per-axle
voice past the grip limit = broadband skid only); round 2 "sounds like gravel" (squeal was noise through a resonator:
level flutter 28 % per 5 ms block). Fix: VehicleSynth.StickSlipVoice/StickSlipTone, one note + harmonics -10/-21/-28/
-32 dB (from a real car screech), 5 % pitch wander at ~6 Hz, noise scrub underneath at 1/10 power; used by every tyre.
Light single touchdown was inaudible because the client put it at 62 % power on the runway: piston aircraft idle
on the ground and in the flare (ClientAudioSystem, GroundIdleLever), jets keep GroundPower (reversers).
Airliner squeal note 430 Hz declared (truck-like); untested whether Cody hears it as a squeal or groan.
**How to apply:** a squeal is tonal and steady; measure flatness AND level flutter, not just a tonal peak. My
1.88 kHz "squeal" in the before file was the airliner's engine whine: check a sound exists before the event.
