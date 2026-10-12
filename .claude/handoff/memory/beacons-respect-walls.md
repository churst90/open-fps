---
name: beacons-respect-walls
description: Beacons go through the acoustic path and are skipped when occluded; their sounds must not resemble crossing chirps
metadata:
  node_type: memory
  type: project
  originSessionId: 0567fe39-d6c7-4ad7-95a6-f379f6c28251
  modified: 2026-09-23T14:08:30.593Z
---

Since 2026-09-23 `BeaconAids.Blip` runs `SpatialAcoustics.CalculateAcousticPath` and drops a blip whose occlusion > 0.5 (Cody heard door beacons of other rooms through walls). `BeaconAids` must be constructed AFTER `_acoustics` in `ClientAudioSystem`; before this it received null.

Sounds: door = knock (420 + 1150 Hz damped, 90 ms), item = bell (1318 + 3350 Hz), vehicle = low double beep 330 Hz. The old door chirp sounded like a pedestrian crossing signal; anything high and chirpy will be confused with the crossings.

Also this round: the tower/terminal/estate doors were refitted in city.json (71 doors turned side-on to their walls or narrower than the opening); `EstateDoorTests.YouCanWalkThroughAnOpenDoor` covers Union, the terminal and an estate. Engine `CompensateLevel` now lifts only the engine, not the air hiss or chime.

**2026-09-23 evening, "I don't hear the beacons for doors now":** occlusion > 0.5 alone silenced a
door in plain view at an angle (its own jamb reads 0.4-0.6): 96 of 938 in-view spots. Now a beacon
blips if its face is in sight (one ray from 0.35 m off its thinnest face, `BeaconAids.InSight`) OR
occlusion <= 0.5. Test `ADoorInViewBlipsFromAnAngle`. A test ear 4 m out from a corridor door is
inside the flat opposite — place test ears only where a ray says they can see the door.
