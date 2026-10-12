---
name: diffracted-bearing
description: A blocked source is heard from the edge it bent over — not from a Steam Audio pathing probe; that grid was the "car stopping in front of me" fault
metadata:
  type: project
---

Session 14 (2026-09-18). Cody on the speedway: *"I'm getting that car stopping in front of me problem
at close range."* Nothing stopped. `AsyncAcousticWorker.RunSteamAudio` gave the arrival direction of
ANY source with `Visibility < 0.5` to Steam Audio's pathing stage, whose probes are a **uniform floor
grid sized to the map** — his own log said `2185 probe(s) at 7.3 m spacing`. A 7.3 m-quantised bearing
is 4 degrees of error at 100 m and nearly 30 at 15, and it HOLDS while the car crosses a cell, then
jumps. Worst exactly at close range, which is what he reported.

**The rule now: an edge is a secondary source at a place, so the crossing point IS the bearing.**
`Diffraction.PathDifferenceAroundBox` already searched the silhouette for the level (the knee-high-wall
fix); it now also reports the **listener-side** crossing — the last leg is the one arriving at the ear,
and on a deep box the entry crossing is a different corner. Measured: 0.9 m pit wall → **0.2 deg** of
shift (the car does not move); 12 m grandstand → 26 deg (you hear the stand); door leaf → the jamb.
One rule, no thresholds, continuous as the source moves, no grid anywhere in it.

Two things that are separate on purpose and must stay separate:

- **The detour (level) is an estimate; the bearing is not.** `BarrierPathDifference` returns the worst
  detour ALWAYS, so a source behind a doorway keeps its relief and does not sound like it is coming
  through the wall. `edgeVerified` is a separate out-param and gates only the redirect.
- **Both legs are checked against every OTHER box** (`RouteIsClear`). Round the west end of a doorway's
  west leaf is the shortest way past THAT leaf and runs straight into the room's west wall — the leg
  that fails is the SOURCE-side one. Unverified → fall back to pathing, which knows the whole scene.
  That is the right division: exact edges outdoors, probes indoors (the rooms map bakes at 2 m).

Also: `BarrierDelta` now rides on `SaResult` so `RunSteamAudio` and `BuildSimPath` cannot disagree —
a source whose level said "over the wall" while its bearing said "through a door" was the fault.

`DiffractedBearingTests` (5). See [[audio-ghosts-and-stutters]] (the same words, a different cause —
that one was a stale timestamp), [[outdoor-acoustics-map]], [[no-special-cases]].
