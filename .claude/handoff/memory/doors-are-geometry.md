---
name: doors-are-geometry
description: 09-30 the SA scene rebuilds with door leaves where they are (background, ~120 ms city); blanket InsulationFactor muffle retired; patch 5 applied (walls lose full mass law)
metadata:
  type: project
---

2026-09-30 (b830453b), unheard. Cody: "I press E, open the door but I don't hear the outside world flow
inside ... a clear boundary", unlike the tunnel/bus shelter.

Causes: (1) SteamAudioScene built once per map, door leaves at load pose (shut), never updated — to
occlusion/diffraction/traces an open door was a wall; (2) FmodAudioProvider.InsulationFactor took
shelter x40 dB off the top of every outdoor sound whenever the listener's region was an enclosure,
door open or shut (tunnel/shelter aren't enclosures, so no boundary there).

Fix: AsyncAcousticWorker.RebuildSceneIfNeeded hashes door leaves (portal + solid box) within 50 m;
on change builds both scenes on a Task (--scene-cost: 5220 boxes ~120 ms), swaps on the worker,
releases old scenes after 5 s; at most every 0.3 s; no probe rebake. InsulationFactor deleted (rain/
wind keep the sky shelter). Patch 5: transmission per face ^(2/3). Server leaf transform is real
(DoorSystem swings it), Moves=false for doors.
Same day: SA materials interpolated to SA's band centres 400/2500/15000 (table is 200/1250/8000).
If closed rooms now sound too leaky or too bright through walls, look at MaxWallLossDb (55 flat cap)
and flanking, not at re-adding a blanket. See [[shelter-is-not-insulation]], [[room-was-too-wet]].
