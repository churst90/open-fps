---
name: birds
description: BirdLife — birds found from habitat (foliage/roofs), individual calls not flock loops; Cody's samples cut 2026-09-23; not yet heard
metadata:
  type: project
---

Cody's samples (inbox/birds, 09-23) cut into single calls under OpenFPS.Client/ASSETS/SOUNDS/BIRDS:
SPARROW 44 (one cheeping sparrow; the source file is largely one recording reused, blocks correlate
0.88), CROW 2, GOOSE 16, PIGEON 5 (low-passed 2.2 k), DOVE 6 (a collared-type dove, NOT the mourning
dove Cody wants — low-passed 1.8 k, one clip with another bird in it dropped). Cutter:
/tmp/claude-1000/birds/cut.py (scratch; recreate if needed).

`OpenFPS.Common/Birds.cs` (BirdSpecies: CallDb, bout model, contagion, StartleDb, ShyMetres) and
`OpenFPS.Client.Core/BirdLife.cs` (client-only, like footsteps). Habitat from the map, never placed:
Foliage material -> sparrows in 1 hedge in 6, a dove in 1 in 12; roof = solid box top > 10 m, area
> 60 m^2, 60% of a 4x4 grid open to the sky -> pigeons / a crow. City: 176 sparrows, 33 doves, 17
pigeons, 1 crow. Geese skein every ~7 min, 60-110 m up. Loud WorldAudioEvent at a group -> quiet
8-30 s; listener within ShyMetres -> quiet.
**Contagion trap:** pulling resting birds on every call made a hedge chatter 10/s. Only a bird
STARTING a bout pulls, and only birds rested >= BoutGapMin. Measured 0.33 calls/s/bird.
Wanted still: a real mourning dove recording; wing-flap takeoff sounds for a flush.
See [[no-ambience-beds]], [[no-special-cases]].
