---
name: door-rebuilds-and-jamb-routes
description: "09-30: door scene rebuilds froze all sound up to 1 s (Configure under TracedReverbSet.Gate); barrier search now tries every way round, shortest clear first"
metadata:
  node_type: memory
  type: project
  originSessionId: bdab4ba8-7b09-445e-8c24-e849615c1a5b
  modified: 2026-09-30T09:15:33.147Z
---

Fixed 2026-09-30 (commit 1e7a57af), unheard by Cody yet.

- **The freeze:** "Audio placement stalled ... listener+map 1006 ms" with the game loop and the audio
  thread stalling together = a lock, not GC (gen2 pauses were ~20 ms). TracedReverbSet.Configure held
  `Gate` while calling each tracer's SetScene, which waits on LateField's `_simGate` for a whole trace.
  Rebuilds now go through `ConfigureInBackground`; retired scenes wait on `Reconfiguring`.
- **Spurious rebuilds:** the old door signature hashed the doors within 50 m, so walking changed it
  (477 rebuilds/session). Now `NearDoorMoved` compares each leaf with its pose at the last build.
  Door leaf = solid + non-default Portal + !Moves (server gives every door a portal, -1/-1 if unauthored).
- **Open door did nothing off-axis:** `Diffraction.PathDifferenceAroundBox` returned one way round per
  box (for a 2.7 m wall: over the top, into the slab). Now it reports every way round (with source-side
  and listener-side crossings); the worker drops routes shorter than the worst box, tries the 6
  shortest, checks all legs with other boxes padded 5 cm (joint cracks leaked -14 dB through a SHUT door).
- **Instrument:** `--path-probe ear= src= open=R` swings leaves within R m on their hinges; the lab
  loader (SirenRouteSpike.Load) now carries map RegionAId/RegionBId. Flat 01F: ear -14,1.6,-85;
  corridor src -20,1.6,-82 → shut -54 dB, open -14/-22/-25.
- **Held Steam Audio path:** pool-full ticks reuse the last SA answer ≤1 s, shifted by the source's
  movement (otherwise a bus is heard behind itself).

Related: [[sa-source-pool]], [[diffracted-bearing]], [[blocked-bearing-flips]], [[a-fence-is-not-a-wall]]
