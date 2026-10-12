---
name: geometry-system
description: 10-06 agreed geometry direction: triangle meshes + BVH as ground truth, voxels only for editable ground (meshed), acoustic voxel grid derived; design agent writing docs/GEOMETRY.md, build after streaming stage 1
metadata:
  type: project
---

Cody 2026-10-06 asked for the highest-quality geometry system for his purposes. Recommended and started a
DESIGN-only agent (docs/GEOMETRY.md): triangles with per-triangle materials + BVH for collision, raycasts,
occlusion and Steam Audio (fed natively); heightfield terrain from USGS 3DEP for real-place maps; OSM
footprints extruded; glTF/OBJ import; sparse voxel ground with strata only where diggable, meshed (MC /
dual contouring) to triangles; the acoustic voxel grid derived from triangles. Voxels-only rejected
(staircased surfaces scatter/diffract wrong, memory, Steam Audio needs triangles); wedge+stairs plan is
only a stepping stone. BUILD waits for world streaming stage 1 to merge (same core files).
See [[world-streaming]], [[magnolia-map]].

**Design DONE + merged 10-06 (docs/GEOMETRY.md, da2fe40b; tools/geometry_estimate.py, AudioLab --geometry).**
Own two-level BVH (Enclosure 14-21x faster), terrain as heightfield per tile (2 m, 48 KB/tile; in-BVH costs
2.4 s/308 MB), Steam Audio on EMBREE with instanced sub-scene per tile (swap 0.2-0.5 ms; default tracer with
sub-scenes is 24x slower = unusable), door leaves as transform updates, voxels 0.5 m 16^3 chunks surface nets,
rooms from seeds. Stages ~21-33 sessions. 11 decisions for Cody (terrain 2 m, 10 m+lidar, voxel 0.5 m,
strata, glTF offline, budgets 150k/20k/50k, Embree (x86/x64 only), own BVH vs Jolt, stairs, room seeds,
live editing). Told the streaming 1b agent to use Embree.
Cody's decisions 10-06 (in docs/GEOMETRY.md): live editing yes; real stair treads (W as now, auto step-up);
real soil survey data for real places; everything else as recommended. Build after streaming 1b merges.
**Stage 1 BUILD started 10-06** (agent, after streaming 1b merged): triangle store + per-tile BVH + Embree feed +
static collision, old/new comparison harness as the gate for each consumer switch.
**10-06: Cody "go with your recommendations"** for stage 1's five: keep flush rule (smaller footprint wins); stop
the loader's foundation where a map has its own ground; ricochet triangle normal in stage 2; normalise map
rotations at load; accept over-the-top route changes. AND Cody: "a new default world should just be dirt, then you
place concrete on top" — the base layer is natural ground (dirt/soil strata), everything else layered on it; the
loader's fallback foundation becomes dirt; /map new starts as dirt (supersedes the 09-28 "one grass slab").
Stage 1 merged locally 26d531a2, broad tests running; then push + stage 2.
**Stage 2 merged locally 10-06** (branch geometry-stage-2, broad tests on merge running): ShapeSpec forms (wedge,
stairs with treads, arch) via ColliderComponent.Form (appended), walkable ground <=45 deg with normals, Fruin grade
speeds, BodyShape "Capsule" = cylinder foot+trunk under a domed head (full capsule measured and rejected: caught
ledges, climbed 56 cm), FootOnFloor + tread-counted stride (stair walk 154/154 on treads), WheelRays (driven cars
pitch/roll), SightGrid/BoxColumns/EarlyReflections/image sources/ricochet on triangles (Diffraction,
CompositeAcoustics, VehicleShadow still boxes), incremental RefreshGrid (OPENFPS_INCREMENTAL_REFRESH=0),
RouteAnswers leak fix, one-off echoes on BVH (city worst 55->14 ms). Dirt "Ground" under maps without ground
(speedway only); /map new = dirt. Pushed 3717067a. Cody 10-06: all five stage 2 decisions as recommended
(body, 0.45/0.65/0.86, 2 treads/footfall, cars pitch/roll, speedway dirt) - recorded at the end of docs/GEOMETRY.md.
/tmp FILLED 10-06 (agents' renders in MY scratchpad: 3.4 GB) -> cleaned dirs untouched 2 h; watch df /tmp.
