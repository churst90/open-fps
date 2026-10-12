---
name: your-own-footsteps-reverberated-outdoors
description: TargetRegionId defaults to -1 (outdoors), so footsteps fed the outdoor bus and every room had the same tail
metadata:
  type: project
---

`SpatialEmitter.TargetRegionId` defaults to **-1, which IS the outdoors**, and `SubmitFootstep` never
set it — a footstep is built on the game thread between updates, where nothing has computed a region.
So every footfall sent its reverberation to the OUTDOOR bus, and the room the player was standing in
received only the small **cross-send** (`ReverbCrossSendScale`, a quarter) that exists so a sound in
the NEXT room reverberates faintly in this one.

Heard as: *"the tail on the reverb is the same no matter where I am in the stairs, corridor or parking
garage... the walls might be farther away but the decay is short."* Same tail because it was the same
bus. The garage's 5 s and the stairwell's 2 s were measured, written to their own buses, and barely fed.

**This is why the measurements and the ear disagreed for a whole session and BOTH were right.** The
rooms were surveyed correctly and the decays applied correctly; the footsteps were going elsewhere.
It is also why `--room-walk` sounded fine throughout — that spike sets `TargetRegionId` itself.

Fixed 2026-09-19: footsteps, landings and step reflections carry `_listenerRegion`, which
`ClientAudioSystem.Update` keeps from the region it already computes. And a source already in the
listener's room no longer takes the cross-send on top, which was sending to one bus twice.

**Why:** a default of -1 meaning "outdoors" rather than "unknown" makes a missing value into a wrong
answer that never throws.
**How to apply:** when the model measures right and the ear says otherwise, check the ROUTING before
re-deriving the model. See [[never-normalise-the-room-away]], [[instrument-must-walk-like-the-game]].
