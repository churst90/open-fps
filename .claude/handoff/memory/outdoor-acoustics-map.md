---
name: outdoor-acoustics-map
description: "What decides outdoor reflectivity and reverb today, the two constants that are still special cases, and how regions get made (hand-authored, prefab has no materials, no static-map enclosure detection)"
metadata: 
  node_type: memory
  type: project
  originSessionId: f889a6f5-7730-449f-8ecf-50a871eb8591
  modified: 2026-09-18T18:27:47.701Z
---

Surveyed 2026-09-18 for Cody's question "outside, walls should dictate reflectivity, not just standing
outside" and "should regions be automatic or part of the prefab".

**Discrete reflections DO fall out of the walls.** `EngineReflections.SyncGeometry` builds
`ReflectingSurface`s from every solid box collider with its material (`AcousticRegistry`), faces under
1.5 m half-extent dropped, faces re-keyed by plane so a row of blocks is one wall. `ImageSource.FirstOrder`
gain = `(direct/path) * (1-absorption) * (1-scattering)`, the scattered share radiates from the face
(see [[a-surface-scatters]]). Nothing in that path asks about regions or "outdoors". Second order is
written but no client caller uses it.

**Reverb outdoors is where the special cases remain.** `RoomAcoustics.DecayMs` returns 0 for any region
with an open face, so the bus starts dry (`_dryReverbBuses`); the Steam Audio ray-traced RT60 at the
listener (8192 rays, 16 bounces, every 6th tick, 5-sample median) then opens the wet in
`FmodAudioProvider.ApplySimulatedReverb`. Two things there are constants standing in for physics:
1. **The scene has no sky.** `SteamAudioScene.BoxesFromWorld` is boxes only; the ground IS in the scene,
   the sky is not, so a trackside grandstand can give a canyon's RT60 — hence the hard cap
   `AcousticConstants.OutdoorMaxDecayMs = 1100` and the wet ramp 260 -> 1400 ms to `OutdoorMaxWetDb -16`.
2. **The send is distance-independent**: `ReverbSendMix = 0.35` post-fader, so direct-to-reverberant is
   the same at 1 m as at 100 m; no critical distance anywhere. Already in todo.md ("gunshot echoey,
   footsteps boxy") with the instruction to read the `Room:` log line before changing the model.

**Regions are hand-authored, full stop.** A region exists because a map entity uses a region prefab
(`acoustic_region`: 10x5x10, IsIndoor true, **no RoomMaterials** -> six open faces unless the map
entity names them). `AcousticVolumeGenerator` only copies and voxelises those; it detects nothing.
`CompositeAcoustics.Survey/Derive` (server) DOES detect "these parts enclose a room" — size >= 1 m,
solid fraction <= 0.6, >= 4 of 6 faces >= 35 % covered, face material = whichever part covers most —
but is called only for player-built composites (`CompositeService.RefreshRoom`) and the `/room` command,
never at static map load. The speedway generator declares track sectors, infield and deck as regions
with `IsIndoor: False` and no materials.

**How to apply:** the answer to "automatic or prefab" that matches [[no-special-cases]] and Cody's
"without the user thinking about it" is: measure at load with the survey that already exists, keep the
region entity as an override. Do not add outdoor flags. See [[region-is-not-a-room]].
