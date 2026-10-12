---
name: vehicles-do-not-occlude
description: Moving vehicles are not in the acoustic scene; VehicleShadow puts them back as Maekawa barriers per band — built 2026-09-23, not heard
metadata:
  type: project
---

`SteamAudioScene.BoxesFromWorld` skips anything that `Moves` (the scene is built once). Cody (09-23):
"when vehicles pass behind each other the further is occluded". Built as
`AudioEngine/Acoustics/VehicleShadow.cs`: per voiced source per frame, every solid dynamic body
> 2.5 m^3 crossing the ear->source line is a barrier; detour over roof / round end / side, Maekawa
10log(3+20N) shifted to 0 dB at grazing, capped 15 dB, applied to path EqLow/Mid/High in step 5
(and to that vehicle's horn/siren voices). Measured: bus broadside at 10 m = 2.7/8.2/13.7 dB.
