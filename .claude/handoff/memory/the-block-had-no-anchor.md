---
name: the-block-had-no-anchor
description: 2026-09-20 — the engine block was 7.5 dB low across every preset because it was the one source with no declared level; fixing it is +6 dB on a bus and nothing on a petrol car
metadata:
  type: project
---

"I can hardly hear the engines on those diesels." Found with `--voice-levels parts`, which takes the
live voice apart: the three outlets metered where they are made, the rest by muting and differencing.

**Two faults, both structural.**

1. **The block had two routes out and one of them was undeclared.** The front tap carried
   `Block * 0.4 * FrontMix` = 0.14 — a fixed 17 dB of attenuation applied to every machine — and
   the bay leak carried it again at the vehicle's own declared figure. One mechanism, two paths.
   Removed: the block now reaches the street only through `EngineBayLeakage`, which every vehicle
   declares from what is actually around its engine (car 0.15, pickup 0.30, truck/bus 0.80).

2. **The block had no anchor at all.** Every other source declares one — tyres `SquealDb`, a blade
   row `ReferenceDb`, a jet its Lighthill trim — and the block was four scale factors and wherever
   the arithmetic landed. Measured, it landed **7.5 dB low at both ends of the range**: a 13 L truck
   six at 90.6 dB at 1 m against published heavy-duty engine-surface figures of 97-100, and a 1.6 L
   petrol four at 76.1 against 82-86. The DISPLACEMENT scaling between them was already right
   (+3.7 dB over 4.6x the swept volume vs the +4.4 a two-thirds surface law gives), which is what
   says it is one anchor wrong for everybody, not a preset needing a number. `BlockRadiationGain`.

**Why nobody noticed:** it is worth nothing on a petrol car — a muscle car's block is thirty
decibels under its exhaust either way — and it is most of a bus. After the fix the bus's block is
97.7 dB against its silenced tailpipe's 83, and the bay is worth 8.4 dB of the machine.

Re-declared from the live voice: school_bus 85→95, school_bus_na 98→95, diesel_truck 93→100.
Approved petrol cars all moved ≤0.1 dB (v8_muscle 121.8, police_v8 131.9, f1_v10, nascar unchanged).
788 tests pass.

**Still open:** a bus is now 95 dB at 1 m where a real transit bus at full load is 98-102, and the
petrol intakes look wrong the other way — i4_economy's intake measures 94.7 against an 86.4 exhaust
and the v6's 110.7 against 99.3, which is backwards for a silenced road car. `FrontMix = 0.35` is
still a taste constant throwing 9 dB away on the intake; do not raise it until the intake balance
is understood, or the v6 becomes an airbox with a car attached.

See [[live-voice-is-not-the-bench]], [[audio-lab-instruments]].
