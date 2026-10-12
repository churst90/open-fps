---
name: player-coordinates
description: Players read/type x east, y north, z HEIGHT (C readout and /tp) — the engine is still Y-up; swap only in PlayerCoordinates
metadata:
  type: project
---

2026-09-22, at players' request: C says "150.0, 27.0, 0.1" = east, north, height, and `/tp x y z`
takes the same order. `OpenFPS.Common/PlayerCoordinates.cs` (Format / ToWorld) is the ONLY place the
swap happens; the engine, maps and prefabs stay +X east, +Y up, +Z north (FMOD's native layout).

**How to apply:** any `/tp` I give Cody puts height LAST. Waypoints in older memories (e.g.
[[ear-test-harness]], [[city-block-map]]) are written as engine x y z with height in the MIDDLE —
swap the last two before handing one over. docs/ waypoint lists were already converted.
Any new command that speaks or reads a position must go through PlayerCoordinates.
