---
name: the-empty-grid-answer
description: The client froze on the city because an EMPTY spatial-grid result was treated as a failure and fell back to the whole map
metadata:
  type: project
---

**Session 17, 2026-09-19.** Reported as *"when I load the map and begin to move around the client
crashes"*. It was not a crash — it was the physics putting the WHOLE MAP through the collision solver
on every tick.

`ClientPhysicsSystem` asked the static grid for solid geometry within `CollisionSearchRadius` (5 m)
and, if the answer was **empty**, fell back to `snapshot.Entities.Values`. `SpatialService` did the
same thing for occlusion. An empty answer is an ANSWER: it means there is nothing within five metres,
which is what standing in the middle of a twelve-metre carriageway IS. So on the city the fallback
fired continuously, every physics tick, over 5,825 entities — and it fired precisely where a listener
spends their time, outdoors with nothing close by. On the 504-entity block nobody noticed.

Only a **missing** grid — the seconds between a map arriving and the first `RebuildGrid` — is a
reason to consider everything.

Three more things found in the same log, all of them "fine at 500 entities, fatal at 6,000":
- **The moved-region check walked every entity every frame** to find the ~600 that declare a region.
  `WorldSnapshot.RegionEntityIds` now carries them, the way `AudioEntityIds` already did.
- **`PhysicalLevel` built a whole model per machine per frame.** `SmallMachineSpec.ByName` goes
  through `ModelLibrary.Get`, which CONSTRUCTS the spec every call — measured at **2.3 MB allocated
  and 5.1 ms for one second's worth** (121 machines x 60 Hz). Memoised in ClientAudioSystem.
  `VehicleProfile.ByName` is memoised for exactly this reason and says so; the small-machine and
  aircraft ones are not.
- **The sample reflection path tried to play `machine:ac_window` as a FILE**, failing every frame
  with a deferred play queued behind each. It skipped `engine:` by name; it now skips anything with
  `IsSynth`, which is the property that actually decides it.

**Why:** the map went from 504 entities to 5,825 in one session, and every O(map) loop that was
invisible became the whole frame.
**How to apply:** the audio update now reports where its time goes by stage — `listener+map, paths,
budget, emitters, mixer` on the "Audio placement" line. Read that before guessing. See
[[city-block-map]], [[physical-voice-path]].
