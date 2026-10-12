---
name: first-hearing-was-dropped
description: WorldAudioPlayer used to DROP the first occurrence of any new one-off sound while rendering it — why the 7 steel doors were silent; fixed 09-23
metadata:
  type: project
---

"The steel doors make no sound, no metallic latch." Server made full Metal door sounds; the client
rendered a never-heard sound on a worker and threw that event away, with 4 seed variants each, so
rare sounds (7 steel tower doors) were almost never heard. Now the first hearing waits in
`_awaitingRender` and plays unless the render is > 0.12 s late. Test: SteelDoorSoundTests.
Same night: the steel door was a SOLID 80 mm slab (2.4 t, ~17 dB too loud a slam). Now
`DoorSkinMetres: 0.0012` (prefab -> DoorComponent.SkinMetres, appended last): mass from two skins +
150 kg/m^3 core (~64 kg), ring from the sandwich's equivalent thickness (316 Hz).
**Do NOT use ShellThickness/IsHollow for a door**: SpatialService treats hollow as a 2-skin shell and
a shut door would let almost everything through.

2026-09-24: `DoorAcoustics.Opening` now takes the leaf's mass (like Closing). The leaf answers the
latch (140 Hz knock, level -10·log10(mass/49 kg) around 74 dB), rings by the Closing rule (steel
0.7 s, wood 0.14), and a dry hinge sings at 476/sqrt(mass/49 kg) Hz. Game doors keep hingeDryness 0.
Renders: inbox/doors-2026-09-24 (`--door-opening`). APPROVED by ear 09-24.
