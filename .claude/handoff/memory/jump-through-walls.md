---
name: jump-through-walls
description: 09-28 64 Alder St: sideways-only collision pushed a head in the roof slab out through the wall; voxel grid put outside-the-wall points inside; house doors were on the garden side
metadata:
  type: project
---

Found from the session log's [FOOT] lines (wood at x -220.04 inside, then LANDED on dirt at -222.45).
1. GeometryUtils.GetCylinderAABBOverlap was XZ-only: jumping (feet to 0.83 m, top 2.71) put the head in
   the 2.6 m roof slab; centre inside its footprint → pushed 0.3 m away from the slab's centre, 3 passes
   a tick, through the wall. Now: nearest-edge MTV, and a ceiling pushes DOWN when `canGoDown`
   (= !isGrounded) — grounded, a head-height beam must stay a wall (SharedMovementPhysicsTests).
   CeilingJumpTests fails on the old code in all three cases.
2. SpatialService.GetRegionAt voxel fallback (0.5 m) returned the house for points 0-0.5 m outside its
   wall. Now a region with its own box is never returned by the voxel fallback.
3. gen_city.py house(): front/back wall choice was inverted (doorway on the garden side, a door inside the
   solid street wall) on every house; back door had no gap. Fixed; +192 entities.
Also: TracedReverb.Order 1 → 2 (0.2-0.35 ms/block), and the traced stage runs in 256-sample pieces (TracedReverb.TracedFrame): the convolution answers one of ITS blocks late, so rooms' first reflection went from ~20-23 ms to 5.6-8.3 ms. --traced-reverb SA_FRAME=N to compare.
The live client log is /tmp/openfps-sa-on.log (run-gtk-client.sh on); /tmp/openfps-sa-on-client.log was an older 1 GB file (cleared 09-28 at Cody's request).
See [[a-kerb-is-not-a-cliff]], [[region-is-not-a-room]].

09-29: "Audio placement stalled ... 680 ms" in the airport terminal = TracedReverb.Loop held _gate
through iplSimulatorRunReflections while the game thread's per-frame SetListener took the same lock.
Listener now has its own lock; TracedEchoes split into _gate (requests) and _simGate (simulator), source
adds deferred to the trace thread. Read "Audio placement stalled" + "Game loop: N Hz" first when Cody
says sounds are missing or the reverb flutters. Terminal RT60 7-10 s is the bare-concrete geometry.
