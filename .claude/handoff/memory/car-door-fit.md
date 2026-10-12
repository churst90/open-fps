---
name: car-door-fit
description: 09-28 car door model fitted to Cody's recording in Python (tools/car_door_fit); cabin standing waves are the boom; awaiting his ear before porting to C#
metadata:
  type: project
---

Reference: `inbox/door sounds/Car Door Open and Close - Sound Effect (HD).mp3` (Cody: "fairly clean").
Measured: close = sub-100 Hz air swell peaking ~70 ms BEFORE impact (air pushed into cabin, or mic
wind — unknown, so bracketed), a cluster of hits over ~60 ms, then a 69 Hz boom = cabin's first
standing wave c/2L (2.5 m), -20 dB at 135 ms. Opening = handle/rod taps, unlatch clunk with short
low thump (no cabin boom), check-strap detent ~0.34 s later.
The game's model (DoorAcoustics generic knock/ring/hiss) is 20.1 dB RMS off after the impact (only a
41 Hz panel ring survives: the "bath tub"); the Python fit is 3.4 dB. Prototype + fitted params in
`tools/car_door_fit/`; listening set + play.sh in `inbox/door sounds/synth-2026-09-28/`.
**Next:** Cody listens (02 no air vs 03 air), then port as a SynthKey model like Applause
("cardoor:open|close:<cabin L,W,H,...>" from the vehicle preset) and route in WorldAudioPlayer.RenderOne.
The measuring trap: align windows on the impact ONSET (1-4 kHz within 20 dB of peak) for closing, on
the loudest frame for opening, or the windows compare different events.
See [[synthesis-by-listening]], [[first-hearing-was-dropped]].

**DONE 09-28:** v1 (resonators) rejected "sounds like an instrument, too tonal"; v2 octave-band noise
approved "perfect, use those". In game: `OpenFPS.Common/CarDoor.cs` (true Butterworth band-pass +
0.1 s filter pre-roll, both needed to match the Python fit), SynthKey `cardoor:open|close` routed in
WorldAudioPlayer.RenderOne; OccupancyService.CarDoorSounds keeps DoorAcoustics' levels. Lab
`--car-door out= seed=`. Measure TONALITY (tools/car_door_fit/tonal.py), not only band levels.
