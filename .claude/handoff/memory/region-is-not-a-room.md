---
name: region-is-not-a-room
description: "Naming a place on a map used to put a roof over it — every open-air behaviour was keyed on the global region id; fixed 2026-09-16 (session 9) by RoomAcoustics"
metadata:
  type: project
---

Cody logged into the speedway and "everything sounds like it's in a big room and not outside". It
arrived with the zones commit (c1b5da5), and the cause was one sentence: **every open-air behaviour in
the audio engine was keyed on "the listener is in the GLOBAL region id", which is not a question about
the place — it is a question about whether the map has bothered to NAME it.** A region is a named
volume; being a room is a property of its BOUNDARY. `OpenFPS.Common/RoomAcoustics.cs` owns that
question now (`IsEnclosure`, `Enclosure`, `DecayMs`) and the id tests are gone from the reverb bus,
the simulated-reverb cap and gate, the air-absorption "indoors" flag, the small-room gain lift, and
the hear-a-room-through-its-doorway test.

Two things underneath, both general:
- **A face of material "None" is an OPENING, not a mirror.** Absorption 0, transmission 1. Sabine
  reads absorption 0 as a perfect reflector, so an unbounded 277,000 m³ infield came out as a sealed
  box — and because nothing absorbed, the total fell under a "did anything absorb?" guard and the code
  substituted a 500 ms default room. At full wet, over the whole map.
- **Sabine has a precondition**: a diffuse field in a CLOSED enclosure. One open face and the energy
  leaves once and is gone. So an unclosed region gets NO statistical estimate — its bus is built dry
  and the ray-traced RT60 (which can see a grandstand or a street's facades) is what may open it.

**How to apply:** the instrument is `--open-air-reverb` in the AudioLab — it drives the real FMOD
graph and brackets the fault: named infield 500 ms at 0 dB wet under the old rule, identical unnamed
ground one step away dry. `OpenAirReverbTests` covers the model. Still open: the grandstand should
declare its roof and back wall; region faces could be MEASURED from map geometry the way
`CompositeAcoustics.Survey` already measures a player-built room's; air absorption (4mV) is not in the
estimate. See [[no-special-cases]], [[speedway-map]], [[audio-lab-instruments]].
