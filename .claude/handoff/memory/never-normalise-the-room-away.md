---
name: never-normalise-the-room-away
description: A metering loop held the reverb unit at unity gain — which cancelled exactly how live each room was
metadata:
  type: project
---

`ApplySimulatedReverb` metered the reverb DSP's input and output and trimmed its wet level until the
gain was unity, reasoning that the per-source send carries the level so the unit should add no
arbitrary gain. **The gain it was measuring was not arbitrary.** A reverberation unit accumulates
energy in proportion to its decay time, so a long tail measures a higher output — and the loop pulled
it down by exactly as much as the room was live:

| decay | trim the loop settled on |
|---|---|
| 400-800 ms (a corridor) | −6.1 dB |
| 2800-3200 ms (a tunnel) | −8.1 dB |
| 4800-5200 ms (a car park) | −10.5 dB |
| 7200-7600 ms | −13.0 dB |

Twelve times the tail, handed back seven decibels quieter. Reported, after every other cause had been
chased out of the way: *"all of those areas sound the same other than the tunnel and street... the
only difference being how far away the walls are"* — the walls' distance being the EARLY REFLECTIONS,
which do not pass through that loop and so were the only part of a room still varying.

Fixed 2026-09-19: `AcousticConstants.ReverbUnitWetDb` is a constant (−6 dB, where the loop settled for
a mid-sized room). **Three things carry a room and each has one job**: the SEND says how much
reverberant field a source raises (the room equation, with distance and absorption in it), the DECAY
says how long it rings, and the UNIT only rings. Its one genuinely arbitrary gain is FMOD's internal
scaling, which is a property of the DSP and not of the place.

**Why:** an automatic gain control anywhere in an acoustic chain will find and cancel the signal you
are trying to convey. The measurement looked principled and was measuring the wrong thing.
**How to apply:** if places stop sounding different, look for something normalising them — and read
the applied value against the measured one before assuming the model is wrong.
See [[the-room-is-not-a-cube]], [[a-reflection-is-the-room-answering]], [[reverb-needs-enclosure]].
