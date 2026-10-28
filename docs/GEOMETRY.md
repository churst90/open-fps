# Geometry beyond boxes

The plan for a world made of triangles: every surface a triangle mesh with a material, terrain from
real elevation, editable ground as a sparse voxel layer meshed to the same triangles, and every
consumer (movement, ground, rays, bullets, the hand-built acoustics, Steam Audio, rooms) reading one
triangle world. Written 2026-10-06 against main at 5454db81, with the world streaming plan
(`docs/WORLD_STREAMING.md` on the streaming branch) in mind: geometry lives in 250 m tiles from the
start.

Agreed direction (Cody, 2026-10-06): a hybrid. Triangle meshes are the ground truth for every
surface: buildings, terrain, ramps, stairs, round shapes, kerbs, vehicles. A sparse voxel layer exists
only for ground that can be edited (digging, building underground, layered earth), and it is meshed
to triangles so physics and acoustics still see one triangle world. The acoustic voxel grid
(`AcousticMap`, `SparseAcousticOctree`, `AcousticVolumeGenerator`) becomes something derived from
the triangles, not authored.

Measurements are from the development machine (24 threads, shared with other work: load average 7
to 8 during the runs). The instruments are committed: `python3 tools/geometry_estimate.py` and
AudioLab `--geometry map=<id> [terrain=<metres>]`.

Where it stands (2026-10-17): stages 1 to 4 built (2026-10-06 to 10, sections 9 to 12), with rooms from their walls
and regions become places (12.6). Stage 6's first half, each vehicle's body as one closed mesh from its parts list,
built 2026-10-12. Left: stage 6's second half (the meshes in the Steam Audio scene, moved with their bodies, with
physics stage 4), and stage 5, held until wanted. The shell is now made once per vehicle kind and can stand in
a `BodyWorld` as a kinematic body (2026-10-27, PHYSICS_BOUNDARY.md 18.8); the Steam Audio scene does not use it yet.

## Contents

1. What exists today
2. The representation
3. Every consumer and how it changes
4. Formats: maps, prefabs, shapes, import, wire
5. Real places: elevation, footprints, roads, bridges
6. Performance: measured
7. Stages, effort and risks
8. Decisions for Cody
9. Stage 1 as built (2026-10-06)
10. Stage 2 as built (2026-10-06)
11. Stage 3 as built (2026-10-09)
12. Stage 4 as built (2026-10-10)

---

## 1. What exists today

Every entity is a box that can be rotated (`ColliderComponent { Shape, Size, IsSolid }`). The other
shapes in `ColliderShape` (Sphere, Cylinder, Cone, Polygon) are used by nothing in any map or prefab;
only players are cylinders, and `/spawn` can make the others. Polygon has no vertex data anywhere.

Geometry is queried through at least five separate spatial indexes, each built for one job:

| Index | Where | Cell | Used by |
|---|---|---|---|
| `SpatialGrid<T>` | Common; server and client | 10 m, 2D | movement, ground height, rays, beacons, driving aids, rain |
| `SightGrid` | Client | 4 m, 2D walk | the scope and sight rays at 10 Hz |
| `OpeningRoutes.SolidGrid` | Common | 4 m, 3D DDA | routes through doorways |
| `BoxColumns` | Common | 10 m | face openings, region surveys at load |
| `Enclosure.Nearby` | Common | none: a scan of every solid | the enclosure survey |

The queries, by consumer (full list with line numbers in the appendix):

- **Movement** (`SharedMovementEngine.Step`, identical on client and server): an upright cylinder
  against each nearby box in the box's own frame (`GeometryUtils.GetCylinderAABBOverlap`), three
  depenetration passes, step-up by `StepHeight` 0.4 m, a guard against being pushed deeper, then the
  map-bounds clamp.
- **Ground height** (`PhysicsUtils.GetGroundHeight`): five points (centre and four at the body's
  radius) tested against each box's footprint with its height stretched to 40 km; the highest top
  within 0.4 m above the feet wins. It cannot see a slope: a box tilted about X or Z still answers
  with the top of its axis-aligned extent. About twenty callers (movement per input, driving per
  tick, footstep material, bullets' strikes, bodies, drops, spawns, teleports, Alex's routes).
- **Rays** (`SpatialService`): occlusion with per-band wall transmission (`GetOcclusionData`, five
  rays a source through `GetMultiPointOcclusionData`), `RaycastAll` (echolocation, ground
  reflection), `RaycastMaterial`, `CastSight` (the scope, 2 km), `RaycastSingle`.
- **Bullets** (`CombatService.FirstHit`): every round is flown in 10 ms segments; boxes by slab test,
  ricochet normals worked out from which local axis of the box was hit.
- **Hand-built acoustics**: `Enclosure.Look` (192 rays, a bounce each, openness probes),
  `EarlyReflections.Find` and `ImageSource` (box faces as rectangles), `Diffraction.PathDifferenceAroundBox`,
  `TrackClearance`, `OpeningRoutes`, `FaceOpenings`, `CompositeAcoustics.SurveyBox`.
- **Steam Audio** (`SteamAudioScene.BoxesFromWorld`): every solid, static, non-emitting box becomes
  8 vertices and 12 triangles in ONE static mesh, with a material per (name, transmission). A second
  scene without the open ground is built for the listener's trace. A door that moves near the
  listener rebuilds both in the background and swaps them in.
- **Rooms**: regions are authored boxes (`acoustic_region`); their faces' materials are surveyed
  from the boxes around them at load, and openings are found as gaps in the boxes round each room's
  faces. The voxel grid (0.5 m, sparse octree) only marks which region a point is in.
- **Wall transmission** (`WallTransmission.BandGains(material, panelSize, WallBuild)`): the panel's
  thickness is the box's smallest dimension; leaf and stud spacing come from the prefab. Sheets in
  contact (facing the same way, faces touching or overlapping, sharing some face) are one construction
  (`Constructions`, `WallTransmission.LayeredBandGains`): the tracer and the legs of routes group what a
  line crosses one straight after another, and the Steam Audio scene shows a construction's outer faces
  only, cut where its layers change, each with the whole construction's figure (`LayeredFaces`).

Steam Audio already takes triangles. Everything else takes boxes.

---

## 2. The representation

### 2.1 Meshes and surfaces

A **mesh asset** is an immutable set of triangles in its own local frame:

- vertices: `float3`, local metres;
- triangles: three `int` indices;
- a **surface index** per triangle (one byte; a mesh has at most 256 surfaces);
- a **surface table**: for each surface,
  - `Material` (an `AcousticRegistry` name, as today),
  - `Construction`: what `WallTransmission` needs and a triangle does not carry: nominal panel
    thickness, `LeafMetres`, `StudSpacingMetres` (the prefab's `WallBuild`, moved to where it belongs),
  - `Layers`: which queries see it (movement, bullets, sight, acoustics, ground). Default all. A
    stair can be stepped treads for sound and footsteps but a smooth ramp for movement, if wanted;
    a chain-link fence blocks movement but hardly any sound; glass blocks movement and bullets
    differently from sight,
  - `Flags`: open ground (for the listener scene), roof, glass, porous volume (foliage), walkable;
- a **closed** flag: whether the mesh is a closed solid (watertight, outward normals). A ray through a
  closed mesh has an entry and an exit, and the chord between them is the panel thickness the
  transmission model needs, as round shapes already do today in `GetOcclusionData`.

A mesh is identified by the hash of its contents (vertices, indices, surfaces). The same hash means
the same mesh on every machine, which is what lets a client cache meshes on disk and skip
downloading them.

**Instancing.** A prefab names a mesh; every placement is an instance (mesh id, position, rotation,
scale). How instances are stored for queries is a separate decision from how they are authored:

- **Static instances are flattened** into their tile's acceleration structure at tile load: their
  triangles are transformed into the tile's frame and built into one BVH. Traversal is fastest and
  the 1,145 tree crowns of Magnolia do not each cost a level of indirection.
- **Moving instances stay instances**: door leaves, vehicles, bodies, lifts, anything with a
  transform that changes. Each is a reference to its mesh's own BVH plus a transform, and moving it
  costs nothing but writing the transform.

**Boxes become a mesh shape.** `Box` is the first entry in the shape library (2.6): a closed mesh of
12 triangles with one surface, generated from `Size`. Nothing in any map changes. The box's surface
gets its `Construction` from the prefab exactly as `WallBuild` is read today, so the transmission
through a converted wall is the same number it is now.

**Precision.** Tile meshes are stored in tile-local coordinates (relative to the tile's corner), and
the tile carries its origin. Float32 then holds sub-millimetre detail within a tile wherever the tile
is in the world, which is what the streaming plan's frames (rebasing at 8 km) need: a rebase only
changes tile origins, never vertex data.

### 2.2 The acceleration structure: a two-level BVH

One structure answers every geometric question, on the server and the client:

- **Bottom level (BLAS):** a BVH over a triangle set: one per loaded tile's static geometry, one per
  mesh asset used by a moving instance, one per voxel chunk (2.5).
- **Top level (TLAS):** a BVH over instances (BLAS + transform + world bounds). Rebuilt whenever an
  instance moves or a tile or chunk is swapped. With a few hundred to a few thousand instances this is
  well under a millisecond; it is a refit when nothing was added or removed.

Build: binned surface area heuristic (12 bins, leaves of up to 4 triangles), nodes flattened into one
array of 32-byte structs (bounds and either the left child or the first triangle and count),
triangles stored in leaf order as (v0, e1, e2, surface, owner) for Moller-Trumbore. This is what
the spike builds; its numbers are in section 6.

Queries, all allocation-free, all thread-safe for readers:

| Query | Returns | Replaces |
|---|---|---|
| `Closest(ray, maxT, layers)` | distance, triangle, normal, surface, owning entity | `RaycastMaterial`, `RaycastSingle`, `CastSight`, bullet segments |
| `Any(segment, layers)` | blocked or not | occlusion yes/no, routes through openings |
| `All(segment, layers)` | every crossing in order (entry and exit, surface) | `GetOcclusionData`'s walk through walls in a row |
| `Overlap(capsule or box, layers)` | the triangles touching it, with closest points | movement, `CheckCollision`, standing room |
| `Ground(x, z, fromY, radius)` | height, normal, surface | `GetGroundHeight` |

**Each triangle knows its owner.** The entity it belongs to is stored with it (4 bytes), so "what is
this" (the scope's description, a door's name, the beacon on a thing) is answered from the hit, as it
is from the box today.

**Own code, not a library.** Considered:

| Option | Licence | Verdict |
|---|---|---|
| Our own BVH in Common (as the spike) | ours | **recommended**: about 600 lines, no dependencies, deterministic, the same code on server and client, measured fast enough (section 6) |
| BepuPhysics v2 (C#) | Apache-2.0 | excellent mesh queries and sweeps, but a physics engine to carry for its queries; its wide SIMD (`Vector<T>`) changes width with the CPU, so a client and server on different CPUs can disagree in the last bit, and movement prediction needs them to agree |
| Jolt Physics through JoltPhysicsSharp | MIT | has a capsule character controller with stairs, slopes and sticking to the floor built in (`CharacterVirtual`), and cross-platform determinism as a build option. Native libraries on every platform. Worth a spike if our own capsule movement fights us; not the first choice, because the movement engine carries a long list of fixes Cody has heard (kerbs, ceilings, pushes, roof edges) that would all have to be found again |
| Embree directly | Apache-2.0 | fastest tracer there is, but native, and the server would need it too. Steam Audio already carries it for its own use (2.4) |
| DotRecast (Recast/Detour in C#) | MIT | not a BVH; the navmesh library for walkers later (3.11) |

### 2.3 Terrain: a heightfield tile

Terrain is a regular grid of heights per tile, a specialised mesh:

- **Storage:** heights in centimetres (16-bit, relative to the tile's base height) at a fixed
  spacing, plus a surface id per cell (grass, dirt, gravel, asphalt under a road, water edge). At 2 m
  a 250 m tile is 126 x 126 posts: 32 KB of heights and 16 KB of surfaces.
- **Queries do not use the BVH.** Ground height is a lookup and a bilinear (triangle) interpolation:
  O(1). Rays march the grid cell by cell, skipping blocks with a min/max quadtree of the heights.
  This is what keeps terrain cheap: the spike shows that putting a 2 m terrain into the triangle BVH
  costs 2.4 s to build and 308 MB for one 3 km map (section 6); as a heightfield it is 4.5 MB for the
  whole map and nothing to build.
- **Holes:** a cell can be marked empty, for tunnels, culverts, cellars and dug ground (2.5); the
  geometry there comes from meshes or the voxel layer.
- **Steam Audio** gets the heightfield as triangles (two per cell), because Steam Audio needs
  triangles. At 2 m that is 31,250 a tile; with Embree that sub-scene builds in about 13 ms
  (section 6).
- **Detail:** the full-detail ring gets the fine grid; the coarse ring (WORLD_STREAMING's levels)
  gets the same tile at 8 m (2,000 triangles), which is plenty for a hill 600 m away.

### 2.4 Steam Audio: Embree, one instanced sub-scene per tile, one per moving thing

Measured in section 6, and the results settle the design:

- Steam Audio's **default** ray tracer is slow to build (351 ms for Magnolia's 277,000 triangles, 12
  s for 5 million) and **does not cope with instancing**: with one instanced sub-scene per tile, a
  reflection trace took 24 times as long (1,200 ms against 50 ms), because it walks the instances one
  by one.
- **Embree** (bundled in the Steam Audio libraries already shipped for Linux and Windows x64; the
  log says "Initialized Embree v4.04.00") builds the same scene in about 110 ms, traces in 29 ms
  instead of 50, and handles instancing properly: with a sub-scene per tile the trace is 37 to 60 ms,
  a tile is swapped in or out in under a millisecond, and a moving 1,000-triangle body costs 0.25 to
  1.2 ms a commit.

So the scene becomes:

- the simulator and every scene use `IPL_SCENETYPE_EMBREE` with one Embree device per context;
- **one sub-scene per loaded tile** (its static meshes flattened), instanced into the top scene;
  loading or dropping a tile is building one small sub-scene on a background thread, then an
  instance add or remove and a commit;
- **two sub-scenes per tile**, "open ground" and "everything else", so the listener's ground-free
  scene (`WithoutOpenGround`) is a second top scene that instances only the second kind. Sub-scenes
  are shared between top scenes, so this costs no second copy of the geometry;
- **one sub-scene per door leaf**, instanced: a door that swings is a transform update and a commit
  (under a millisecond) instead of rebuilding the whole scene twice in the background (0.8 to 1.8 s
  on the streaming branch, and before that a frozen second of sound). This alone retires the door
  rebuild machinery (`NearDoorMoved`, `SwapInDoorBuild`, the five-second release);
- **one sub-scene per vehicle body** in stage 6, its transform updated with the body;
- commits are batched once per simulation step on the acoustic worker, between simulation runs
  (Steam Audio forbids a commit during one).

Embree's limits: Steam Audio documents its Embree option for x86 and x64 only (no ARM, so no Apple Silicon or ARM Linux client
without the default tracer as a fallback). The fallback for those is the default tracer with one
static mesh per tile in one scene (measured: a tile in 14 to 53 ms, trace 2 to 3 times the flat
scene), never the default tracer with instancing.

A **custom scene** (`IPL_SCENETYPE_CUSTOM`, Steam Audio calling back into our BVH) would mean one
structure for both, but Embree traces about twice as fast as our managed BVH does and costs us
nothing to keep. Not recommended.

### 2.5 The voxel ground layer

For ground that can be dug, built under or layered. Untouched ground stays heightfield; the voxel
layer exists only where ground has been edited or was made to be edited.

**Representation.**

- **Resolution: 0.5 m** (recommended; decision 3). Each voxel holds a signed density (8 bits) and a
  material (8 bits: a stratum or a placed fill). The surface lies where the density crosses zero,
  interpolated, so a 0.5 m grid gives a smooth surface placed to a few centimetres, not 0.5 m steps.
  0.25 m is eight times the memory and meshing for detail a spade does not need.
- **Chunks** of 16 x 16 x 16 voxels (8 m cubes), sparse: a hash map from chunk key to chunk, held per
  250 m tile. A chunk that is all solid or all air is one value. A chunk is created the first time
  something edits inside it, initialised from the heightfield and the strata ("copy on dig"). A dug
  garden is a handful of chunks; a 3 km map nobody has dug is none.
- **Strata:** each place has a column profile, depths and materials from the surface down (topsoil
  0.3 m, subsoil to 2 m, then clay or sand, weathered rock, bedrock), varied smoothly by position.
  From real soil data where it exists (USDA SSURGO soil horizons are public domain; SoilGrids gives
  depth to bedrock under CC BY 4.0), or from a place's profile in place.json (decision 4). New
  acoustic materials: Clay, Sand, Rock, Mud (the registry has Dirt, Gravel, Concrete).
- **Depth limit:** voxels exist from the surface down to a world floor (for example 30 m) per place;
  below it is bedrock that cannot be dug.

**Meshing: surface nets** (recommended).

| Method | Triangles | Sharp edges | Seams between chunks | Verdict |
|---|---|---|---|---|
| Marching cubes | 2 to 5 per surface cell, slivers | no | needs a shared boundary layer | works, wasteful |
| **Surface nets** | about 2 per surface cell, well shaped | no | one voxel of overlap | **recommended for earth** |
| Dual contouring | about 2 per surface cell | yes, from normals (QEF solve) | as surface nets, plus care | only if dug faces must be sharp |

Earth is not sharp. Anything that must be sharp underground (a cellar wall, a tunnel lining, a
concrete footing) is a placed mesh from the shape library that carves the voxels it stands in, so the
sharp faces come from meshes and the voxels stay soft. That keeps the voxel layer simple.

**Meeting the heightfield.** A tile's terrain cells that are covered by voxel chunks are marked as
holes, and the chunks' surface takes over. Where an edited chunk meets untouched heightfield, the
chunk's boundary vertices are placed from the heightfield itself (the density there was made from
it), and a short skirt below the seam closes any crack a ray could leak through.

**Edits.**

- An edit is an operation: a brush (sphere, box, or a mesh such as a spade's bite), add or remove, a
  material. The server applies it, then sends the operation (a few dozen bytes) to every client that
  has the tile. Each side applies it and re-meshes the affected chunks with the same code, so meshes
  are never sent.
- Per edited chunk: surface nets on 16^3 (well under a millisecond), the chunk's BLAS rebuilt (a few
  thousand triangles: fractions of a millisecond), the TLAS refit, the chunk's Steam Audio sub-scene
  rebuilt and swapped (a few milliseconds with Embree, on the acoustic worker, debounced so a burst of
  digging is one rebuild).
- The acoustic voxel grid (2.7) re-floods only the edited chunks and their neighbours: a cave dug
  into a hillside becomes a room because it is enclosed, with nothing authored.
- **Persistence:** per tile, the chunk data (run-length compressed) plus an edit log since the last
  snapshot, stored beside the tile as the streaming plan stores runtime state.
- **Not in scope at first:** collapse and structural support (an overhang that should fall), water
  flowing into holes. Both are possible later on the same data.

### 2.6 The shape library

Parametric shapes, generated from a few numbers by the same code on server and client, so a map
sends the numbers and never the triangles:

| Shape | Parameters | Notes |
|---|---|---|
| box | size | today's every entity |
| wedge / ramp | size, which edge is low | the kerb ramp, a loading ramp |
| stairs | width, rise, run, steps, landing | real treads; optional smooth-ramp movement layer |
| cylinder, cone, sphere, capsule | radius, height, segments | columns, tanks, bollards, trunks |
| arch | span, rise, thickness, depth | bridges, doorways |
| extruded polygon | outline (2D), height, holes | building footprints, kerbs along a curve, fences |
| wall with openings | outline segment, height, thickness, openings | building walls with doors and windows cut |
| roof | outline, pitch, kind (flat, gable, hip from the straight skeleton) | |
| lathe | profile, segments | round things: a fountain basin, a bell |
| swept profile | profile along a 3D polyline | kerbs, gutters, rails, handrails, road edges |
| terrain patch | a small heightfield | a mound, a berm |
| mesh | an imported mesh asset by hash | anything else (4.3) |

Segment counts for round shapes default to what sound can tell apart: a column of 0.3 m radius at 12
sides; a tank of 5 m at 32. Small facets scatter rather than mirror (3.4), so more sides buy
nothing audible.

### 2.7 Rooms and the acoustic voxel grid, derived from triangles

The acoustic map stays a voxel grid of regions, but it is computed, not drawn:

1. **Voxelise the solids** of the loaded tiles at 0.5 m (a conservative triangle-box overlap per
   triangle, per tile, on a background thread).
2. **Rooms are seeds.** A room is authored (or generated) as a point and a name ("living room") and,
   for a while, its old box as a hint. A flood fill from the seed through empty voxels, stopped by
   solid voxels and by door leaves and portals, gives the room's real shape: an L, a round tower, a
   sloped attic, a dug cave.
3. **Openings** are where a room's flood touches another room or the outside through a door or a
   gap: the voxel faces on the boundary, grouped by plane. `FaceOpenings` today finds the same thing
   from box faces; this finds it for any shape.
4. **Room measurements** come from the geometry, as Cody's rule already requires ("regions measure
   themselves"): volume is a count of voxels; surface area and the materials of each face from the
   triangles lining the flood (or from the enclosure survey from inside, which already integrates
   area and absorption from rays).

Seeds with no enclosure (yards, streets) never flood: they are named places, as they are now.

---

## 3. Every consumer and how it changes

### 3.1 Movement and collision (players)

The structure of `SharedMovementEngine.Step` stays: gather what is near, collide and slide in up to
three passes, step up, never end a step deeper in anything, clamp to the map. What changes is the
contact:

- **The body becomes a capsule** (radius 0.3 m, from 0.15 m above the feet to the head) instead of a
  cylinder against box footprints. The near triangles come from `Overlap`; for each, the closest
  points between the capsule's axis and the triangle give a depth and a normal.
- **Contacts are classified by their normal**: a floor (the normal within the walkable slope of up, 45
  degrees by default), a wall, a ceiling. Walls push out horizontally only, so a steep bank slides you
  down rather than lifting you. Ceilings push down only when airborne, as now ("a head in a roof
  slab"). Floors are left to the ground probe.
- **Step-up** stays as it is: blocked by a wall while grounded, try the same move lifted by
  `StepHeight`; if that is clear, take it and then settle to the ground. Stairs with real treads are
  climbed by step-up (each riser 0.15 to 0.2 m against 0.4 m).
- **Determinism:** client and server run the same code over the same triangles, built in the same
  order from the same data. Scalar `System.Numerics` arithmetic on x64 gives the same bits on both;
  that is the reason for our own code (2.2).
- The fixes in today's engine are kept as cases in the tests, re-run against the triangle path: the
  kerb that landed you, the roof slab that pushed you through a wall, the 489 m landing, the
  push-deeper guard on Kestrel House.

### 3.2 Slopes, ramps and stairs

- Ground is a surface with a normal (3.3). On a slope the body stays on it while the slope is
  walkable; walking speed is scaled by the grade (slower up, a little faster down) and the step
  length with it, which the gait model already reads from speed.
- Above the walkable slope the ground is a wall: you slide down it.
- **Stairs:** real treads by default. Footsteps then fall one per tread at the stairs' rhythm, which
  is how stairs sound. A stair can also carry a smooth movement layer (a hidden ramp) if real treads
  make the body judder; decision 9.
- Landing and falling keep today's logic, with the floor under the body found by the probe instead of
  the highest box top.

### 3.3 Ground height

`Ground(x, z, fromY, radius)`: the same five points as today (centre and four at the body's radius),
each a downward ray from `fromY + StepHeight` against the BVH and against the terrain heightfield;
the highest walkable hit wins, and it returns the height, the normal and the surface (so the
footstep material). On terrain alone it is a lookup.

Callers keep their signature at first (`GetGroundHeight` returns a height and a material); the
normal is added for movement, vehicles and the gait. The memo on the server stays, keyed on a
geometry version per tile instead of the grid's `StaticVersion`.

### 3.4 The hand-built acoustics

| Model | Today | Becomes |
|---|---|---|
| `Enclosure.Look` | 192 rays against every box within 120 m (a scan of all), openness probes, a bounce each | the same rays through the BVH; measured 14 to 21 times faster (section 6) |
| `EarlyReflections.Find`, `ImageSource` | box faces as rectangles | **facets**: coplanar connected triangles merged into planar polygons at mesh build, each with area, normal, material and scattering. A box has 6, as now. A facet smaller than the wavelengths that matter (a 12-sided column's faces) is a scatterer, not a mirror, which is what it physically is |
| `Diffraction` | path difference round a box | over the top: the vertical profile along the path, sampled by downward rays through the BVH and the heightfield, gives the highest edge (this is how ISO 9613-2 treats terrain and barriers, and it is the right model for hills); round the ends: the same horizontally. The box version stays for boxes until stage 4 |
| `TrackClearance` | track points against solid boxes | a swept box along the track through `Overlap` |
| `OpeningRoutes` | segments through a 4 m 3D grid of boxes | `Any` through the BVH; the route search itself is unchanged |
| `FaceOpenings`, `CompositeAcoustics.SurveyBox` | gaps in boxes round a room | the derived room grid (2.7) |
| `WallTransmission` | panel = the box's smallest side; sheets in contact one construction (`Constructions`, `LayeredFaces`) | panel thickness = the chord between entry and exit of a closed mesh, or the surface's nominal thickness; leaf and studs from the surface's `Construction`; solids in contact still one construction, found from the triangles' shared faces |
| `VehicleShadow` | Maekawa over a moving body's box | unchanged until stage 6 |

### 3.5 Raycasts, occlusion and line of sight (`SpatialService`)

Each method keeps its signature and answers from the BVH:

- `GetOcclusionData`: `All` along the segment returns each wall entered and left, with its surface;
  band gains multiply as now. Walls in a row, hollow shells (entry and exit of the same closed mesh)
  and round things (the chord) all come out of one code path.
- `RaycastAll`, `RaycastMaterial`, `RaycastSingle`, `CastSight`: `Closest` with the right layers
  (glass seen through by the sight layer, as `glassBlocks` does now).
- `GetRegionAt`: the derived room grid, unchanged in use.
- The grids (`SpatialGrid` for statics, `SightGrid`, `SolidGrid`, `BoxColumns`, `Enclosure.Nearby`)
  all go. Dynamic things go into the TLAS as instances each tick instead of into the dynamic grid.

### 3.6 Bullets and ricochet

- `FirstHit` is `Closest` along the 10 ms segment with the bullet layer; people stay moving cylinders
  (`ExternalBallistics.SegmentHitsBody`) because they are rewound by velocity.
- Ricochet takes the triangle's normal directly. Today's `Face` guesses it from the largest local
  axis of the box, which is wrong near edges and on anything not a box.
- Penetration through a closed mesh reads the chord, the same as transmission.
- The strike's sound reads the surface (material) from the hit triangle.

### 3.7 The scope

`InView`, `Describe`, `Range` use `CastSight` and keep working. `StandsOnRoof` matches "roof" in an
entity's name today; it becomes the `Roof` surface flag.

### 3.8 Footsteps

The ground probe returns the surface under each foot, so the footstep material is per triangle: a
gravel strip beside a lawn, a kerb's concrete edge, a wooden stair tread. The server's
`GetMaterialUnderPlayer` reads the same probe. Dug ground gives its stratum's material (clay, sand).

### 3.9 Beacons

`BeaconAids.Reaches` goes through `GetMultiPointOcclusionData` and `RaycastMaterial`, so it follows
3.5 with no change of its own. Finding beacons near you uses the entity index (a list of things by
tile), not the collision grid.

### 3.10 Doors and openings

- A door leaf is a moving instance: its own mesh (a box today, a panel with a knob later), its own
  BLAS, a transform written by `DoorSystem`. The TLAS and the Steam Audio instance follow the
  transform every tick it moves. No grid rebuilds, no scene rebuilds.
- The portal and opening stay what they are: the doorway's frame and the two rooms it joins.

### 3.11 Walkers, Alex, traffic and parking

- Alex's pavement strips come from road data (sidewalks are part of a road's cross-section) and from
  surfaces flagged as pavement, instead of slabs named "sidewalk". His clearance checks and his
  waypoints' heights use `Overlap` and `Ground`.
- Traffic follows lane lines whose heights come from the draped road (5.3), so a car climbs a hill
  because the road does. Per-wheel physics (already merged) gets four ground rays, one per wheel,
  each with its own height, normal and surface: pitch and roll on slopes, a wheel that drops off a
  kerb, gravel under one side.
- Parking spot checks become `Overlap`.
- Walkers that leave roads (a park, a field) need a navigation mesh: walkable triangles per tile,
  built with DotRecast (MIT) from the same triangles. Not before stage 3.

### 3.12 Server surveys and composites

- `SurveyRegions` at map load becomes the room flood (2.7) on the server too, so the server and client
  agree on rooms.
- Composites (`CompositeAcoustics`, `CompositeService`) work on their parts' bounds; a part that is a
  mesh has bounds, so they keep working. A composite's own surface survey moves to rays through the
  BVH in stage 4.
- `VerifySpawnPoint`, `FindStandingRoom`, `IsSafe` and the spawning commands use `Ground` and
  `Overlap`.

---

## 4. Formats

### 4.1 Maps and prefabs

A prefab or a map entity gets one new field, `Shape`, which is either a shape from the library with
its parameters or a mesh asset:

```json
{ "PrefabId": "stone_stairs", "Position": {...}, "Rotation": {...},
  "Shape": { "Kind": "stairs", "Width": 1.2, "Rise": 0.18, "Run": 0.28, "Steps": 14 } }

{ "PrefabId": "fountain_basin", "Position": {...},
  "Shape": { "Kind": "mesh", "Mesh": "b3f19c0e7d2a", "Surfaces": { "basin": "Concrete", "water": "Water" } } }
```

- No `Shape` means a box of `ColliderSize`, as now. Every existing map and prefab stays valid.
- `ColliderSize` (scaled) stays and is filled in from the shape's local bounds when the shape has
  them. Code not yet moved to triangles keeps seeing a box of the right size: a cylinder becomes its
  bounding box, which is what it is treated as today anyway.
- Surfaces are named in the shape and given materials and constructions in the prefab or the entity,
  with the prefab's `Material` as the default for every surface.
- Terrain is not an entity: a map (or a tile) carries a `Terrain` block (spacing, a heights file, a
  surfaces file).

### 4.2 Mesh assets on disk

`OpenFPS.Server/meshes/<hash>.mesh`: the asset as MemoryPack (vertices quantised to 0.1 mm relative to
the mesh's bounds, indices, surfaces, surface names), Brotli-compressed. A mesh is written once and
never changed; a new version is a new hash.

### 4.3 Import from glTF and OBJ

An offline tool (`tools/import_mesh`, a small .NET console on SharpGLTF, MIT), never the server at run
time:

- reads glTF 2.0 (`.gltf`, `.glb`) and OBJ;
- maps glTF materials (or OBJ groups) to surfaces by name, with a mapping file to set the acoustic
  material and construction of each;
- welds vertices, drops degenerate triangles, checks closure, reports the triangle count, and refuses
  anything over the budget;
- writes the `.mesh` and prints its hash.

glTF is the format of nearly every free 3D model and of Blender's exporter, so a sighted friend could
model a fountain or a church for a map. It also carries what a renderer would need later (normals,
UVs, textures), which the importer keeps in a side file for a future client that draws. Decision 5.

### 4.4 The wire

All appended, as the positional format requires:

- `ColliderShape` gains `Mesh` and `Heightfield` (new enum values at the end).
- `ColliderComponent` gains `ShapeSpec` (the shape's kind and parameters, or a mesh hash), after
  `IsSolid`.
- `AcousticComponent` gains nothing: per-surface construction lives in the shape's surface table.
- New messages (union numbers taken at implementation time; 32 to 34 and 38 to 39 are taken):
  - `MeshAssetRequest { hashes }` and `MeshAssetBatch { assets }`: the client asks for meshes it does
    not have cached, after a tile's definitions arrive. Packed with Brotli as definitions are.
  - `TerrainTile { tile, level, spacing, base height, heights, surfaces, holes }`: 32 KB of heights
    and 16 KB of surfaces raw at 2 m, a few KB packed for gentle ground. Sent with the tile.
  - `GroundEdit { tile, chunk, brush, add or remove, material, sequence }`: one per edit.
  - `GroundChunks { tile, chunks }`: the edited chunks of a tile, when the tile is loaded.
- Parametric shapes travel as their parameters inside the definition: a staircase costs a few bytes
  more than a box.
- The client keeps a mesh cache on disk keyed by hash, beside the door render cache.

### 4.5 Validation

At map and prefab load (`PrefabValidator`, `MapManager`) and in the importer:

- shape parameters in range (a stair's rise at most `StepHeight`, or it is reported as not climbable);
- a closed solid really is closed (every edge shared by two triangles), with outward normals;
  an open mesh is allowed only for surfaces marked as sheets (a fence, a canopy);
- no degenerate or sliver triangles; no feature smaller than 1 cm;
- every surface's material known to the registry (as now);
- triangles per mesh and per tile within budget (decision 6);
- solids that interpenetrate each other are reported (the speedway's track through the grandstand,
  in general);
- a mesh whose bounds disagree with its `ColliderSize` is rejected.

---

## 5. Real places

### 5.1 Elevation to terrain tiles

- **Data.** `elevation.json` holds 3DEP heights resampled to about 30 m (100 x 100 posts per map).
  Magnolia's relief is 22 m, Albany's 17 m; the steepest post-to-post grade is 36 % and 25 %, both at
  creek banks. 30 m is too coarse for a kerb or a ditch but right for the hills. The download step
  should fetch 3DEP at 1/3 arc-second (about 10 m) everywhere, and the 1 m lidar DEM where it exists
  (it covers most of the conterminous US; `fetch_place.py` should record which it got).
- **Tiles.** For each 250 m tile, the DEM is resampled (bicubic) to the tile grid (2 m, decision 1)
  and then **graded**: flattened under each building's footprint to a pad (the pad's height is the
  ground at the footprint's front door, so a house on a slope shows a foundation on the low side),
  shaped under roads to the road's cross-section (5.3), and under drives from the road edge to the
  garage pad.
- **Water.** Ponds and creeks from `water.json` cut into the terrain to a depth, with a Water surface
  over them.
- The generator writes tiles' terrain beside their entities; the 3 km ground slab disappears.

### 5.2 Buildings from footprints

- Each footprint ring (from `buildings.json`; median 4 vertices in Magnolia, 8 in Albany, up to 148)
  becomes walls extruded along the ring itself, not a union of up to four rectangles: an L-shaped house
  is an L, a curved wall is curved, a 148-sided Albany building has its 148 sides.
- Walls are the shape library's wall-with-openings: front door, back door and windows cut in, with
  their thickness and construction (brick, siding over studs).
- Roofs: flat for flat, and for pitched the straight skeleton of the footprint (hip roofs for any
  shape, gables for rectangles), with eaves.
- Floors and ceilings: the footprint polygon, triangulated.
- Interiors are the hard part. The room layouts are rectangles today. On a non-rectangular footprint
  they go in the largest rectangles that fit, and the leftover becomes hall and closets. Room seeds
  (2.7) mean a room's real shape is measured, so an odd-shaped leftover is still heard right.
- Measured (section 6): the extruded shells come to 115,000 triangles for Magnolia and 212,000 for
  Albany, against 170,000 and 245,000 for today's structure boxes. Real shapes cost no more than
  today's rectangles.

### 5.3 Roads draped on the terrain

- A road's centreline gets heights from the terrain, smoothed along its length to the grade its class
  allows (a residential street up to about 12 %, a highway 6 %), and a cross-section: crown, kerb
  (15 cm), gutter, verge, sidewalk, as roads-as-data already describes.
- The road surface is a swept-profile mesh along the centreline; junctions are polygons joined to the
  roads' edges. The terrain under the road is graded to just below the road surface. Nothing draws the
  world, so the road and the terrain need not share vertices; the road lies on graded ground and the
  ground probe finds whichever is higher.
- The road network's lanes get their heights from the same profile, so traffic and Alex follow it.
- Measured: 51 km of road in Magnolia, 41 km in Albany. With both edges and both kerb lines every
  2 m, the road and kerb meshes are about 200,000 and 160,000 triangles per map: under 2,000 for a
  typical tile.

### 5.4 Bridges, overpasses and tunnels

- OpenStreetMap's `bridge=yes` with `layer` marks the segments that are bridges. A bridge is a deck
  (swept profile) at the road's height from its ends, at least a clearance (5 m over a road, from
  `layer` or the road it crosses) over what it spans, with parapets, and piers or abutments. The terrain
  under it is not graded to it.
- `tunnel=yes` and culverts: the road or stream is a mesh, and the terrain cells over its portals are
  holes with a portal mesh; the tunnel is a room by enclosure (2.7), so its reverb is measured.
- Rail on embankments and in cuttings: the rail profile is graded into the terrain like a road.

### 5.5 Forests, water, boats

- Trunks become cylinders (the shape library), crowns stay porous volumes with the Foliage material,
  as boxes are now; a real forest's attenuation is a volume effect (ISO 9613 foliage), not a surface.
- Lakes and the sea are planar Water surfaces at their level; the shore is the terrain meeting it.
- Boats are moving bodies with a hull mesh (stage 6) and buoyancy (a separate piece of work, not
  geometry).

---

## 6. Performance: measured

### 6.1 Triangle counts

`python3 tools/geometry_estimate.py`:

| | Magnolia | Albany |
|---|---|---|
| Entities (medium detail, shipped) | 32,598 | 41,327 |
| Solid boxes today | 23,118 | 32,165 |
| Triangles today (12 a box) | 277,416 | 385,980 |
| of which structure (walls, floors, roofs) | 169,644 | 245,052 |
| Per 250 m tile today: median / 90th / max | 1,164 / 3,888 / 17,100 | 672 / 10,680 / 36,660 |
| Buildings as extruded footprints (walls both sides, floors, ceilings, roofs, eaves) | 114,716 | 211,514 |
| Roads with kerbs, draped (2 m along each edge line) | about 205,000 | about 162,000 |
| Terrain, 3 km map, at 30 / 10 / 5 / 2 / 1 m | 22 k / 195 k / 778 k / 4.85 M / 19.4 M | the same |
| Terrain per tile at 10 / 5 / 2 / 1 m | 1,250 / 5,000 / 31,250 / 125,000 | the same |

A full-detail tile as meshes, with 2 m terrain kept as a heightfield: about 5,000 to 40,000 triangles
of buildings, roads and props, plus 31,250 terrain triangles for Steam Audio only.

### 6.2 The BVH (our own, in the spike)

AudioLab `--geometry`, one thread, full JIT (`DOTNET_TieredCompilation=0`; the game reaches the
same code after warm-up):

| | Magnolia boxes | Albany boxes | Magnolia + 5 m terrain | Magnolia + 2 m terrain |
|---|---|---|---|---|
| Triangles | 277,416 | 385,980 | 1,054,916 | 5,132,112 |
| Build (one thread) | 104 ms | 155 ms | 406 ms | 2,357 ms |
| Memory (nodes + triangles) | 17 MB | 23 MB | 63 MB | 308 MB |
| Closest hit, short rays (to 60 m) | 3.3 M/s | 3.0 M/s | 2.5 M/s | 2.0 M/s |
| Closest hit, level sight rays (to 600 m) | 1.6 M/s | 1.6 M/s | 0.9 M/s | 0.9 M/s |
| Any hit, point to point (to 850 m) | 2.1 M/s | 1.8 M/s | 1.1 M/s | 0.9 M/s |
| Ground height (one downward ray) | 0.28 us | 0.33 us | 0.30 us | 0.38 us |
| Capsule overlap (0.3 m x 1.8 m) | 0.68 us, 9 candidates | 0.78 us | 0.82 us | 0.92 us |
| Short rays on all 24 threads (machine loaded) | not measured cleanly | 19.8 M/s | 18.3 M/s | 17.1 M/s |

The terrain columns are the reason terrain is a heightfield (2.3): 2 m terrain in the BVH is 23 times
the build and 18 times the memory of the buildings, for answers a grid lookup gives in O(1).

### 6.3 Against today's box path

| | Today | Through the BVH |
|---|---|---|
| `Enclosure.Look`, one survey, Magnolia (60 places near the spawn) | 1.37 ms | 0.10 ms (305 rays) |
| `Enclosure.Look`, Albany | 2.29 ms | 0.11 ms (333 rays) |

Today's survey scans every solid in the map to find the near ones, then tests every near box for each
ray. The BVH's survey also skips Enclosure's openness cache and is still 14 to 21 times faster.

Every other query today walks one of the five grids and slab-tests each box it finds; their cost
depends on how many boxes are near (a city street: dozens to hundreds per query). A BVH query is a
fraction of a microsecond plus a few triangle tests, and does not grow with what is nearby.

### 6.4 Steam Audio

Scene creation (vertices, triangles and materials to a committed scene; today's `Build` also keys
materials by transmission and builds the listener scene, which is not counted here), and a trace
with LateField's numbers (4,096 rays, 48 bounces, one thread) and a direct pass with occlusion and
transmission for 32 sources:

| | Magnolia boxes (277 k) | Albany boxes (386 k) | Magnolia + 5 m terrain (1.05 M) | Magnolia + 2 m terrain (5.1 M) |
|---|---|---|---|---|
| Default, one static mesh: build | 351 to 504 ms | 577 ms | 1,960 ms | 11,874 ms |
| trace / direct | 50 ms / 0.09 ms | 52 / 0.12 | 62 / 0.10 | 69 / 0.09 |
| **Embree, one static mesh: build** | 84 to 114 ms | 111 ms | 316 ms | 1,353 ms |
| trace / direct | 29 ms / 0.03 ms | 30 / 0.06 | 34 / 0.05 | 35 / 0.05 |
| Default, a static mesh per tile in one scene: add the biggest tile | 14 ms | 40 ms | 19 ms | 53 ms |
| trace / direct | 111 ms / 0.20 | 98 / 0.25 | 134 / 0.23 | 184 / 0.29 |
| Default, an instanced sub-scene per tile: trace | **1,077 to 1,200 ms** | 530 ms | 1,126 ms | 1,234 ms |
| Embree, a static mesh per tile in one scene: any tile change (full commit) | 121 ms | 98 ms | 198 ms | 1,227 ms |
| **Embree, an instanced sub-scene per tile: tile swap** | 0.51 ms | 0.21 ms | 0.48 ms | 0.24 ms |
| sub-scene build, median / max | 5.4 / 24 ms | 0.7 / 44 ms | 5.3 / 35 ms | 13 / 34 ms |
| moving 1,000-triangle body, per commit | 1.21 ms | 0.26 ms | 0.25 ms | 0.45 ms |
| trace / direct | 60 ms / 0.09 | 37 / 0.08 | 37 / 0.06 | 38 / 0.07 |

What it says:

- The **trace cost hardly depends on the triangle count** (Embree: 29 ms at 277,000, 35 ms at 5.1
  million). Triangles cost build time and memory, not sound quality per second of CPU.
- Default with instances is unusable; Embree with instances is close to a flat scene and makes tile
  loads, doors and moving bodies cheap. Hence 2.4.
- Today's streaming branch rebuilds the whole scene (twice) for every tile change and every door near
  the listener: 0.8 to 1.8 s on a niced thread, about once a second when driving. With Embree
  instances a tile is one small sub-scene build (median 1 to 13 ms) and a sub-millisecond swap, and a
  door is a transform.

Peak memory of the spike process at 5.1 million triangles (both our BVH and several Steam Audio scenes
alive at once) was 5.7 GB; 1.8 GB before Steam Audio. A client never holds that: it holds the tiles
within its radius (at medium, 300 m full and 800 m coarse: about 10 full tiles and 30 coarse).

### 6.5 Budget

| | Per full-detail tile | Client at medium (about 10 full + 30 coarse tiles) | Server, whole 3 km map |
|---|---|---|---|
| Meshes (buildings, roads, props) | up to 150,000 triangles (decision 6) | about 0.5 M triangles, about 30 MB of BVH | 0.3 to 0.6 M triangles, 20 to 40 MB |
| Terrain heightfield at 2 m | 48 KB | 2 MB | 4.5 MB |
| Terrain as Steam Audio triangles | 31,250 (2,000 coarse) | about 0.4 M | none (the server runs no Steam Audio) |
| Voxel chunks | a few to a few hundred where dug | small | small |
| Tile load on the client, background | BLAS build about 20 to 60 ms, Steam Audio sub-scene 5 to 15 ms, room flood | | |

---

## 7. Stages, effort and risks

A session is one working session of an agent, as in the streaming plan. Each stage leaves the game
working and boxes valid.

Where the stages stand (2026-10-17, all merged on main):

| Stage | Status |
|---|---|
| 1. Triangle world, BVH, rays, Steam Audio, static collision | built 2026-10-06 (section 9) |
| 2. Slopes, ramps and stairs | built 2026-10-06 (section 10) |
| 3. Terrain from real elevation | built 2026-10-09 with streaming stage 2 (section 11) |
| 4. Shape library, import, generators, editor | built 2026-10-10 (section 12, eb529bfe). Rooms by flood switched on 2026-10-10 (25a328b9) and measured from their floods the same day, both approved by ear 2026-10-10; regions become places, steps 1 to 5, done 2026-10-10 and 11 (steps 2 and 3 approved by ear 2026-10-11); sound points on models begun (12.6). The shapes, footprints and roofs themselves are unheard in play |
| 5. Diggable ground with strata | not started; held until wanted (Cody, 2026-10-10) |
| 6. Vehicles and bodies in the acoustic scene | first half built 2026-10-12 (each vehicle's body as one closed mesh from its parts list; nothing uses it yet). Second half left, with physics stage 4 (docs/PHYSICS_BOUNDARY.md section 8: rigid bodies go with it, so a body's shape is made once for collision and for the acoustic scene) |

The order agreed with Cody on 2026-10-11 (todo.md, "Next, in order"): regions become places, steps 3 to 5
(done); the special-cases audit's parts (docs/SPECIAL_CASES_AUDIT.md); the physics survey (done); then the
physics stages with stage 6; then sound library stages 6 to 8. Stage 5 waits until wanted.

### Stage 1: the triangle world, the BVH, rays, Steam Audio and static collision (4 to 6 sessions)

- `TriangleWorld` in Common: mesh assets, the shape library's box, BLAS and TLAS, the queries (2.2),
  per-tile BLAS. Built on the server at map load and on the client per loaded tile on a background
  thread, swapped in.
- Boxes go in as box meshes. Existing maps load unchanged.
- `SpatialService`, `GetGroundHeight`, `SharedMovementEngine` (capsule against triangles),
  `CheckCollision`, bullets and ricochet, `Enclosure`, `OpeningRoutes`, `TrackClearance`, parking
  and Alex's checks move to the BVH. The five grids are removed when their last caller is gone.
- Steam Audio moves to Embree with a sub-scene per tile and one per door leaf; the door rebuild
  machinery goes.
- **A parity harness**: for a box-only map (the city, Magnolia, Albany), the old path and the new path
  are run side by side over thousands of probes (ground heights along walking routes, occlusion bands
  between random points, bullet hits, Enclosure surveys), and every difference over a tolerance is
  listed. The new path is switched on per consumer when its list is empty or every entry is explained.
- Coexistence with streaming: tile BLAS and tile sub-scenes follow the streaming tile set; this stage
  replaces stage 3 of the streaming plan's "instanced meshes per tile" item.
- Risks: movement feel changing (the capsule rounds corners that the cylinder-against-box did not;
  every past fix needs a test); determinism between client and server (mitigated by one code path and
  a replay test); the parity harness finding differences that are bugs in the old path (they will be
  decided case by case, by ear where they are audible).

### Stage 2: walking on slopes, ramps and stairs (2 to 3 sessions)

- The wedge, stairs and arch shapes; ground normals; the walkable slope; speed and gait on grades;
  step-up on real treads; the footstep rhythm on stairs; landing.
- Vehicles: four wheel rays, per-wheel height, normal and surface.
- The `--walk` instrument over ramps and stairs, and the AudioLab walk, as the kerb fix was checked.
- Risks: stairs that judder (the ramp movement layer is the fallback); vehicles that bounce on the
  edges between triangles (smooth the wheel contact over a contact patch, not one ray).

### Stage 3: terrain from real elevation for the real places (4 to 6 sessions)

Built with streaming stage 2 (2026-10-09; section 11): the order of work was docs/WORLD_STREAMING.md,
"Stage 2 with terrain". Bridges and tunnels from OSM tags and creeks cut in are not built.

- `fetch_place.py`: 3DEP at 10 m and 1 m where available. `gen_osm.py`: graded terrain tiles,
  building pads, draped roads with kerbs and sidewalks, drives, bridges and tunnels from OSM tags,
  creeks cut in. The 3 km ground slab goes.
- The heightfield in Common (queries, holes, coarse level); `TerrainTile` on the wire; the terrain
  sub-scene in Steam Audio; the open-ground split by surface flag instead of "a thin slab at ground
  level".
- Traffic, walkers and Alex on 3D lines.
- Acoustics on uneven ground: diffraction over the terrain profile (3.4); the ground reflection reads
  the ground's real height and slope under each source.
- Risks: per-tile generation must give the same heights at tile edges from either side (the grading
  of a road crossing an edge is decided from the road, not the tile); the generator's run time on
  bigger DEMs; a house pad on steep ground making a cliff (cap the pad's step and slope the rest).

### Stage 4: the shape library, import, generators and the map editor (3 to 5 sessions)

- Every shape in 2.6, with facets for the image-source model; the extruded footprints and
  straight-skeleton roofs in gen_osm (replacing the four-rectangle decomposition); swept kerbs; round
  columns and trunks.
- The importer (glTF and OBJ), mesh assets on disk, the client's mesh cache, `MeshAssetBatch`.
- Validation (4.5), the authoring guide.
- Map-editor hooks: `/place` and `/build` take shapes with parameters ("stairs 14 steps up north"),
  and the build menu lists them.
- Rooms derived from seeds by flood (2.7) replacing authored region boxes, with the old boxes as hints
  until every generator writes seeds.
- Risks: interior layouts on irregular footprints; imported meshes that are not closed (the importer
  refuses them as solids and they can only be sheets).
- As built (2026-10-10): section 12. Rooms by flood switched on 2026-10-10 and regions became places by
  2026-10-11 (12.6 item 1); sound points on models are left (12.6).

### Stage 5: diggable ground with strata (5 to 8 sessions)

- Sparse voxel chunks, strata, surface nets, the heightfield-to-voxel seam, edits on the wire,
  persistence per tile, incremental BLAS and Steam Audio updates, room flood in edited chunks.
- A spade (inventory, an action, the bite as a brush), digging and dirt sounds by material
  (synthesised: a spade into clay is not a spade into sand), falling into a hole, climbing out.
- Building underground: placed meshes (a cellar's walls, a tunnel lining) that carve the voxels they
  occupy.
- Risks: the seam between voxels and heightfield leaking rays (the skirt); many players digging at
  once (edits are small, re-meshing is per chunk and debounced); griefing on shared maps (permissions:
  who may dig where, as who may build).

### Stage 6: vehicles and bodies in the acoustic scene (3 to 5 sessions)

- Each vehicle's body mesh from its parts list (machines as parts): the cabin shell (glass, doors,
  roof, floor) as a closed mesh, its own BLAS and its own Steam Audio sub-scene instance, moved with
  the body.
- A vehicle's own sources (exhaust, intake through the bay, tyres) sit at their real emission points
  on the outside of the shell, so the shell occludes other sources behind the car but not its own.
  A listener in the cab is inside the shell and hears the street through glass and doors with the
  same transmission model as a building. This needs no per-source exclusion, which Steam Audio does
  not have.
- `VehicleShadow` (the Maekawa model for moving bodies) is retired or kept for far vehicles only.
- Boats: hull meshes on the same path.
- Risks: commit cost with 40 moving cars (measured 0.25 to 1.2 ms a commit for one; batched, one
  commit a step for all of them: to be measured with 40); the engine voices' tuning was done with no
  body around them, so the change will be audible and must be judged by ear.

**The first half as built (2026-10-12): the mesh.** Made on Windows and sent back as a patch. Nothing uses
it yet, so nothing seen or heard changes.

- **The builder** (`OpenFPS.Geometry/Triangles/BodyShell.cs`) knows nothing of vehicles. It is given a body
  as a few numbers (`BodyShellSpec`: the width, the underside's height off the road, the waist, and the
  body's lengths from back to front with the height of each, a cabin marked as one) and makes the outside of
  it as one `MeshAsset`: closed, wound outward, each triangle's surface slot what the face is
  (`BodyFace`: panel, door, glass, roof, floor). The shell is the union of the lengths' boxes and its faces
  are cut on one grid (the two sides, the lengths' ends, every height a face begins or ends at), so every
  edge is shared by exactly two triangles whatever the heights. A saloon (boot, cabin, bonnet) is 36 triangles, a motorcycle 12.
- **A cabin's faces:** its sides are doors to the waist and glass above it, its top is the roof, its ends
  where it stands above the bonnet and the boot are the windscreen and the back window, and below the waist
  an end is a panel. Everything else is panels, and the underside is the floor.
- **The sources.** `BodyShell.Build` is given where each part that makes sound sits, and gives back where
  each comes out: where it sat when that is outside already (a tyre on the road under the body), else the
  nearest point outside the shell (`Outside`) and 5 cm on (`SourceClearance`), so a ray from it to a
  listener outside does not start in a face. A tailpipe comes out of the back, a side pipe out of the side,
  an intake at the foot of the windscreen or through the nose, whichever is nearer.
- **A vehicle's shell** (`OpenFPS.Sound/VehicleBodyShell.cs`) is made from its parts list
  (`MachineRegistry.Describe`): the cabin is `VehicleCabin.Measure`'s, the one the server builds to sit in
  and the client traces; the bonnet and the boot stand to the waist; an end under 20 cm is the cabin's own
  (a van's nose); a body with no cabin is one length as high as it is. Its sources are the exhaust part, the
  intake part and a tyre for every wheel of its running gear. A face's material is the body part's panel
  material, and glass is glass.
- **The size is the body's** (docs/PHYSICS_BOUNDARY.md, decision 13): `VehicleBodyShell.For` takes a width,
  height and length, meshes the body at them, and moves the parts with it (a tailpipe at the back of the
  preset's car is at the back of the longer one). With none given it is the preset's body. A map's vehicle
  does not carry a size of its own yet; when it does, it is handed in here.
- **One shape per body.** The acoustic scene will instance this mesh and the physics' contacts (physics
  stage 4) will meet the same one. Its hash is its contents', so the same body is the same mesh on every
  machine.
- **Tests** (`VehicleBodyShellTests`), over every vehicle the presets, the authored machines and the maps
  hold (45), each at its own size and at three given ones: every edge walked once each way, every face
  outward with the body behind it, the volume the body's; the bounds the size it was given; every source
  outside, and none nearer a way out than the one it took; a cabin's faces what they are and made of what the
  parts list says.
- **Left for the second half** (it needs Steam Audio and an ear): the meshes in the acoustic scene, each its
  own instance moved with its body; the cost with 40 cars; the sources placed at these points;
  `VehicleShadow` retired; what is heard.

### Order and why

Stage 1 is the foundation and pays for itself in speed (Enclosure, doors, streaming tiles) before
anything looks different. Stage 2 is the first thing Cody hears change (ramps, stairs, real slopes).
Stage 3 is the biggest gain for the real places. Stage 4 makes building and import possible. Stage 5
is the most new gameplay and needs 1 to 3. Stage 6 can run after stage 1 in parallel with the others.

Total: about 21 to 33 sessions.

---

## 8. Decisions for Cody

1. **Terrain resolution.** Recommended 2 m for full-detail tiles (kerbs and ditches are separate
   meshes, so 2 m is enough for the ground between them), 8 m for the coarse ring. 1 m is four times
   the Steam Audio triangles for detail the 3DEP data mostly does not have away from lidar.
2. **Elevation data.** 3DEP 1/3 arc-second (10 m) everywhere, plus the 1 m lidar DEM where it
   exists. 1 m is much bigger to download and store, and is what makes a creek bank or a road cut
   real.
3. **Voxel resolution for digging.** Recommended 0.5 m with smooth density (a few centimetres of
   surface accuracy). 0.25 m costs eight times as much.
4. **Strata.** Real soil data (USDA SSURGO, public domain; depth to bedrock from SoilGrids, CC BY 4.0)
   or a simple profile per place in place.json. Real data is more work and more true; the profile is
   enough to make clay sound like clay.
5. **glTF import.** Recommended yes, as an offline tool (models from Blender or the free libraries,
   checked and converted to mesh assets). Whether players may import meshes into their own maps is a
   separate question (size, abuse, licences of what they upload); recommended admins only at first.
6. **Triangle budget.** Recommended 150,000 triangles per full-detail tile for meshes (terrain apart),
   20,000 per coarse tile, 50,000 per imported mesh.
7. **Steam Audio on Embree.** Recommended yes. It is faster in every measurement and makes per-tile
   loading, doors and moving bodies cheap. It ties the client to x86/x64 (Windows and Linux are both
   x64 today); an ARM client would fall back to the slower layout.
8. **Our own BVH and capsule movement** rather than a physics library (Jolt is the alternative for
   movement if ours fights us).
9. **Stairs.** Real treads for movement as well as sound (recommended, and step-up already handles
   them), or a smooth ramp for movement with real treads for sound and footsteps.
10. **Rooms from seeds.** Generated rooms become a point and a name, and their shape is measured from
    the walls. This changes what gen_osm and gen_city write, and how a room is authored by hand.
11. **Live editing of geometry.** Whether map editors build and dig live while others play on the map
    (the streaming plan leaves this open for building too).

---

## 9. Stage 1 as built (2026-10-06)

Built on the branch after world streaming 1 and 1b. The world's static solid boxes are triangles in a
two-level BVH, on the server and the client, and the consumers that stage 1 names ask it. Every map loads
unchanged; `ColliderSize` is still each entity's box. `OPENFPS_TRIANGLES=0` (server or client) turns the
triangle paths off and every query answers from the boxes as before.

### 9.1 What exists

Code: `OpenFPS.Common/Geometry/` (`Surfaces.cs`: layers, flags, construction, surface, `MeshAsset`,
`ShapeLibrary.Box`; `Bvh.cs`; `GeometryPiece.cs`: the bottom level; `TriangleWorld.cs`: the top level
and the queries; `TriangleWorldBuilder.cs`: tiles, movers, `EntityGeometry`; `SolidContact.cs`: a body
against a solid; `MoverPoses.cs`; `TriangleGeometry.cs`: the switch). Server: `ServerGeometry`, kept by
`MapManager` beside each map's grid. Client: `ClientGeometry` in `ClientWorldState`, handed to every
snapshot (`WorldSnapshot.Geometry`); `AcousticGeometry` for the acoustic scene. Harness: AudioLab
`--geometry-parity`. Tests: `GeometryStage1Tests`.

- **A piece per tile.** A static solid belongs to the 250 m tile its centre is in (the streaming tiles;
  250 m on a map sent whole too); one wider than a tile (the ground under a map) is in a piece of its
  own. A piece holds its triangles in the tile's frame (its corner the origin), a tree over them (binned
  SAH, 12 bins, leaves of four), its solids with a tree over their bounds, each convex solid's face
  planes, and a surface table. The top level is a tree over the pieces' instances. Worlds are immutable:
  a change makes a new world sharing every piece that did not change, so no reader ever locks.
- **Boxes are the one shape.** Twelve triangles wound outward, closed and convex, one surface. The box's
  corners are placed with the classical matrix of the unit quaternion: maps write turns in six digits
  (0.707082, 0.707131 is not unit length), and `Vector3.Transform` with such a quaternion scales a box's
  height by its length squared, which put a gravel strip's top a float's last bit low and stepped a body
  onto a kerb the box path did not.
- **The surface table** per triangle: material (as the entity has it), construction (the box's size as
  the panel, `WallBuild`, shell thickness), absorption override, layers (movement, bullets, sight,
  ground; acoustics unless it is a sound source's own box) and flags (glass, door leaf, hollow, roof by
  name, open ground in the acoustic store).
- **Door leaves are movers**: an instance of a piece made at its own origin (leaves of one size and build
  share one), placed where the leaf's transform is. A swing is a new pose and a refit of the top tree, no
  build. On the server `DoorSystem` and `ParentSystem` count a moved leaf (`MoverPoses`) and the grid
  places its leaves again before it hands the world out; on the client each snapshot does.
- **What is not a box** (no map has one; `/spawn` can make them) and anything not a fixed object (items)
  stays on the old per-entity test, listed as unindexed beside the world.
- **Built where**: the server at map load and in `RefreshGrid` (every core; an order-free fingerprint
  skips the tile pass when no solid changed, as for an item picked up); statics spawned between rebuilds
  are tested the old way until the tick's sync takes them in. The client off the game thread
  (`ClientGeometry`): while a build runs the last one serves, and the owners whose solid changed since it
  started are handed to each snapshot as stale (not counted from the triangles) and tested the old way,
  so an answer is never wrong while the triangles catch up.
- **Determinism**: a tile's solids are sorted by owner and built alone, single-threaded per tile, in
  scalar `System.Numerics`. The server and a client holding the same solids in a tile build the same
  piece bit for bit whatever order they hold them in (tests; the harness compares 4,000 rays and ground
  probes per map, and a streamed client at the spawn, at zero bits apart).
- **Ties.** Two faces within a few millionths of the distance are the same place. Of two such, the one
  whose solid covers less ground is met, then the lower owner. The box path took whichever its list had
  first, which was not the same on the server and the client (Magnolia's open ground: Concrete on the
  server, Dirt on the client, from the loader's foundation lying flush under the map's own ground).

### 9.2 The queries and who asks them

| Query | Answers | Now asked by |
|---|---|---|
| `Closest`, `Enter` (inside means met at once, with the face it came in by) | the nearest face, its solid and owner | `RaycastSingle`, `RaycastMaterial`, `CastSight`, `RaycastAll`, bullets' static hits (`CombatService.StaticFirstHit`), the enclosure survey (front faces) |
| `All` + `Containing` | every crossing; what a point is inside | `GetOcclusionData` (walls in a row, hollow shells) |
| `Ground` | five downward probes, the height exactly on the face's plane | `PhysicsUtils.GetGroundHeight` (server and client) |
| `Overlapping` + `SolidContact` | solids near a body; the way out of each | `SharedMovementEngine` (server and client), `MovementSystem.CheckCollision` |
| `Along`, `Column` | solids a segment or a vertical plane can meet | `OpeningRoutes` (tile build) |

Moving things (players, people, vehicles) are still the grid's dynamic half and the per-entity tests,
asked beside the triangles.

**The body.** The design said a capsule; stage 1 keeps the upright cylinder the engine has always used,
met against each solid's triangles: cut by the cylinder's two ends, seen from above, out from the nearest
point (or the nearest edge, from the support of the cut corners along every edge direction), ceilings
and floors by the same rules as the box test. On boxes it is the box test's answer to rounding. A
capsule's rounded foot would change how kerbs and steps feel on every box map, which stage 1 must not;
it belongs with stage 2's slopes and stairs. Solids are gathered from the same reach the grid had (the
3 x 3 cells of 10 m round the body), because the guard against ending a step deeper in anything can only
see what is in the list, and every past fix was heard with that reach.

**Acoustics.** The acoustic scene (`BoxesFromWorld`: no sound source's own box, door leaves where they
stand) is one store, `AcousticGeometry`, incremental as the tile set was (a cheap hash finds the changed
tiles; open ground is decided again for them and their neighbours only). `TileSceneSet` makes each
tile's two Steam Audio sub-scenes from its piece, placed by an instance at the tile's corner; the worker's
enclosure survey casts against the same world. Door leaves stay in their tiles' sub-scenes (a swing
rebuilds that tile, as in 1b): instancing them in Steam Audio was not tried again.

**Routes through openings, per tile** (Cody's addition). `OpeningRoutes` builds from the acoustic store:
boxes asked of its trees instead of a grid over all of them, each tile's boxes' frames and transmissions
kept while its piece is, each opening derived again only when a tile its walls could be in changed
(`OpeningRoutes.TileCache`). A build after a change answers exactly as one from nothing (harness, 1,000
routes, legs and barriers each, zero apart). The worker checks openings' sides against the places only
when it reports them: the check only ever wrote the report. Found by the harness in main's code: the
over-the-top search swept boxes once by height, so two roof slabs side by side held each other up only in
one order; it now sweeps to a fixed point.

**Track clearance** at load asks a tree over the solids instead of every solid at every point: the same
obstructions, 3.5 ms instead of 426 on the city.

### 9.3 Still reading boxes

- `SpatialGrid`'s static half: still built, for the callers stage 1 did not move (beacons near you, rain,
  driving aids, occupancy, spawning commands, the server's broadcast). The switched queries ask only its
  dynamic half and the unindexed list.
- `SightGrid` (the scope and sight lines at 10 Hz): it also indexes announced things that are not solid,
  which the triangle world does not hold. Moving it needs a sight-only layer.
- `BoxColumns` (the server's room survey and the client's face openings at load): box faces by nature;
  replaced by the room flood (2.7, stage 4).
- `EarlyReflections`, `ImageSource`, `Diffraction` round one box, `VehicleShadow`, `CompositeAcoustics`:
  box faces and box edges, as the design keeps them until stages 4 and 6.
- Ricochet's normal (`CombatService.Face`) is still the box's largest local axis; the triangle's normal is
  there to use (decision below).

### 9.4 The harness

`--geometry-parity map=<id> [n=4000] [only=...]`: old and new asked the same thing from the same state.
On all four maps (city, speedway, Magnolia, Albany), at 4,000 probes a kind:

- **Exact or within rounding, nothing left over:** rays (single, material, sight, the fan), occlusion and
  transmission (band gains within 2e-7), bullets' first static hit, teleport checks, the step test, every
  single movement step (worst 0.3 mm), track clearance, the server and client worlds (same bits), a
  streamed client against the server.
- **Explained classes, listed as ties:**
  - two surfaces in the same place: a different material at the same height or distance (ground: the
    server's 300-1,200 a map where it now agrees with the client; inside houses, 1-4 Carpet/Tile/Wood a
    map; the survey's absorption);
  - a ray glancing along a face: a millimetre along the ray, under 0.2 mm square to the face;
  - a body touching a wall to within 0.1 mm: touching on one side only;
  - an axis exactly between two edges of a footprint: either way out.
- **Far from the origin**: 2 of 32,364 body contacts on Albany, 1.6 km out, 0.04 mm and a twentieth of a
  degree apart. The box test works in world coordinates, where a float's step is 0.12 mm that far out;
  the triangles work in their tile's frame.
- **Walks of 90 steps** part by more than a millimetre in 1-4 of 400, as often as the box path parts from
  itself when started a tenth of a millimetre aside (1-3 of 400). The worst (Albany, 60 mm) was followed to
  its step: a body sliding into a door frame touches three solids whose depths are within 7 micrometres
  of each other, and which is pushed out of first flips; 2 mm later one walk steps onto a 10 cm slab
  and the other does not.
- **Routes** against main's box grid: 0-6 of 1,000 differ, all from the six-digit quaternions (the box
  grid with its turns made unit agrees with the tiles exactly on every map, openings, routes, legs and
  barriers), and 8-17 more are within 0.3 dB on the same openings. One Albany doorway's frame is 5 cm
  apart for the same reason.

### 9.5 Measured (this machine, shared, load 7 to 14)

| | Box path | Triangles |
|---|---|---|
| Enclosure survey (city / Magnolia / Albany) | 11.4 / 3.4 / 6.7 ms | 0.33 / 0.25 / 0.23 ms |
| `RaycastSingle`, 60 m (city / Magnolia / Albany) | 68 / 31 / 69 µs | 4.6 / 7.1 / 6.0 µs |
| `CastSight`, 600 m | 63-107 µs | 1.5-3.7 µs |
| `GetOcclusionData`; the five-ray form | 25-83; 93-295 µs | 5-8; 10-28 µs |
| Ground height, server | 71-216 µs | 16-140 µs |
| A movement step, server gather and step | 19-74 µs | 16-45 µs |
| Bullet segment, static | 6-29 µs | 1.1-2.1 µs |
| Build, whole map, server (one thread / 24) | | Magnolia 169-211 / 48 ms, Albany 211-324 / 50-133 ms, city 60 / 23 ms |
| A tile's piece | | median 0.2-0.4 ms, worst 6-19 ms (a tile of 34,000 triangles) |
| Memory, whole map | | Magnolia 23 MB, Albany 32 MB, city 6.5 MB |
| Server `RefreshGrid`, one box moved (Magnolia / Albany) | grid 32-110 ms | the grid and the triangles 49-122 ms; the triangles' share: reading the solids 14-27 ms, the changed tile 4-41 ms |
| Routes, whole (Magnolia / Albany / city) | 188-244 / 252-291 / 135-161 ms | 44-47 / 40-65 / 85-86 ms from the store |
| Routes after one tile changed | 60-232 ms | 4-6 ms |
| Track check, city | 426 ms | 3.5 ms |
| `--stream-walk` Magnolia at 15 m/s: background acoustic work | 151-177 ms a second (routes 82-97) | 59-68 ms a second (routes 2) |
| ...Albany at 15 m/s | 87 ms a second (routes 55) | 45 ms a second (routes 2) |
| ...worker answer gaps while tiles change, worst | 139-198 ms | 100 ms |
| ...game thread on frames with tile messages, median / worst | 6-20 / 14-245 ms | 6-10 / 16-62 ms |
| `--tile-scenes` Magnolia, one tile changed | 23 ms (1b) | 42 ms (the tile's BVH is built too) |

The game thread's worst frame varies more from run to run than between the two (three runs each). The
Steam Audio tile update costs more than in 1b because the tile's tree is built with it; the routes,
which no longer rebuild, more than pay for it. Nothing new runs on the audio thread.

### 9.6 Left for stage 2 and after

- The capsule, slopes, stairs on real treads, ground normals and walking speed on grades.
- Dynamic things (players, vehicles) as instances in the top tree; then the grid's dynamic half goes.
- The remaining box readers (9.3); `SightGrid` needs a sight-only layer for announced things.
- Shapes other than the box, mesh assets on disk and on the wire, import (stage 4).
- Steam Audio door leaves as instances (not tried again here).
- The server's `RefreshGrid` reads every solid again on each call; tracking what changed would take the
  10-40 ms it adds off a pick-up on a big map.

### 9.7 Decisions for Cody

1. **Two surfaces in the same place**: the smaller patch is met (a drive over the ground, a rug over a
   floor, the map's ground over the loader's foundation). The client already heard it so at the ground;
   the server now agrees, so bullets and other players' steps at the edge of the city and on Magnolia's
   open ground are dirt where the server said concrete. Keep, or prefer something else?
2. **The body stays a cylinder** in stage 1 for the same feel on every box map; a capsule with stage 2.
3. **Ricochet off the triangle's normal** instead of the box's largest axis: right near edges and on any
   shape, and audible as different bounces near corners. Not switched; say if wanted.
4. **The loader's concrete foundation** lies flush under maps that have their own ground (the city,
   Magnolia, Albany): the loader injects it whenever there is no `concrete_floor` at the origin. Worth
   not injecting it when the map has a ground of its own.
5. **Six-digit quaternions**: the triangles read every turn as unit length; the box tests still in use
   read it as written. Worth normalising turns at map load for everything.

## 10. Stage 2 as built (2026-10-06)

Walking on slopes, ramps and stairs, the vehicle's wheels, the remaining box readers, and Cody's
decisions on stage 1. Every switch was gated by the parity harness (AudioLab `--geometry-parity`, the
kinds named in 10.8) on the city, the speedway, Magnolia and Albany.

### 10.1 Stage 1's decisions, carried out

- **Two surfaces in the same place**: the smaller patch is met. Kept.
- **The ground is dirt.** The loader lays no foundation under a map whose own ground covers its play
  area (the city, Magnolia, Albany each lay a dirt ground of their own; the concrete slab flush under it
  is gone, 12 triangles a map). Where a map has no ground under all of it (the speedway), the loader lays
  natural ground, dirt, under its bounds, top at 0, named Ground. Coverage is sampled at most 2 m apart
  over the play area from the solid floors at ground level, so a ground made of many slabs counts. A new
  map (`/map new`) starts as dirt, and grass, concrete or asphalt laid on it are met where they lie (the
  smaller patch). On the speedway the ground between its own surfaces is dirt instead of concrete.
- **Ricochet off the triangle's normal** (`CombatService.Face` with the map's triangle world): the face
  the round's path enters by. On the city 2,520 strikes gave no difference from the box's largest axis;
  the two disagree only within a hair of an edge.
- **Turns made unit length at load** (`MapManager.NormaliseTurns`), once, before anything reads them:
  every reader on the server and every client (sent the same floats) reads the same turn.
- **Over-the-top routes** do not depend on box order. Kept.

### 10.2 Shapes

`OpenFPS.Common/Geometry/Shapes.cs`. A map entity or a prefab names a **form** that fills its collider's
box (`Form`, not `Shape`: `Shape` already names the collider's round or square shape):

```json
{ "PrefabId": "concrete_stairs", "Position": {...}, "Form": { "Kind": "Stairs", "Steps": 16, "Landing": 0 } }
```

- `Wedge`: a ramp, height 0 along its -Z edge rising to full height along +Z.
- `Stairs`: solid steps climbing toward +Z, each rise the height over the steps, each going the length
  less the landing over the steps. Refused at load (and by the prefab validator) when a rise is over the
  step a body can take.
- `Arch`: a block with a half-elliptical opening along Z springing from the ground, `Thickness` of ring and
  piers, the curve in `Segments` straight pieces (12).
- New prefabs: `concrete_stairs` (16 risers of 17.5 cm on 28 cm goings), `wooden_stairs` (15 of 17.3 cm on
  26 cm), `concrete_ramp` (1 in 12), `brick_arch` (2 m by 3 m opening).

`ColliderComponent.Form` is on the wire (appended); `ColliderSize` stays the bounding box every box
reader sees. A shape is its outer triangles (what rays, sound and the ground meet) and, for stairs and an
arch, its convex pieces (each step's column, each piece of the arch's curve and its piers), which a body
and containment are met against. The tests check every shape closed (a ray from inside crosses out one
more time than in) and its pieces filling it exactly.

### 10.3 Ground, slopes and the grade

- **Walkable ground**: `TriangleWorld.Ground` and `FloorAt` stand only on faces within 45 degrees of
  level, and return the face's normal. A probe that meets a steeper face goes on past it to what is under
  it, so a bank too steep to walk is a wall: walked at, it is not climbed. A box's top is level, so on a map
  of boxes nothing changes.
- **Speed on a grade** (`SharedMovementEngine.GradeAlong`, `GradeSpeed`): the slope along the way the body
  is going, from the floors under its feet and a body's radius ahead and behind, the gentler half when
  both climb or both fall and nothing when they disagree. On a ramp both halves are its slope; on a flight
  each is a riser over a going, the flight's pitch; at a lone kerb one half is level, so a kerb is stepped
  as before, not slowed for. Up: 1 / (1 + 2g) (a ramp of 1 in 12 at 0.86, a stair's pitch of about 0.6 at
  0.45). Down: up to 1.05 on a gentle slope, then 1.05 / (1 + 1.2 (|g| - 0.1)), a stair down at 0.65.
  People measured on stairs go up at a little under half their level speed and down at about two thirds
  (Fruin 1971). On the server and in the client's prediction alike.

### 10.4 The body

The design asked for a capsule (3.1). It was built and measured on the four maps against the cylinder
(harness `only=capsule`): its rounded foot changed how a body meets every kerb, step and ledge. A falling
body caught ledges the cylinder fell past and rode back up a roof's edge; the step-up climbed a 56 cm ledge
the cylinder could not; 3 to 15 walks in 400 parted by metres on each map, and on the city 181 single
steps in 4,000 differed. The ground probe and the step rule are a flat foot's, a body's radius wide, so
**the body is the cylinder's foot and trunk under a rounded head** (`BodyShape.Capsule`, SolidContact):

- The trunk is the cylinder from a hand's breadth over the feet to where the head's dome begins: its
  contacts, floors and ceilings are the cylinder's.
- The head is a half sphere: an edge over the brow (a beam, a lintel, a sloped ceiling, an arch's curve) is
  met by its curve; a ceiling straight over a body on the ground (standing on a sofa under a low ceiling)
  is left alone, where the cylinder took the ceiling slab's nearest end as the way out and put the body a
  metre and a half away, through a wall; a ceiling over a body in the air pushes it down.
- Whether a body fits on a step is asked of the cylinder whatever the body (its flat bottom is what makes
  StepHeight the most a body climbs).
- Contacts are ranked by depth; a floor's lift is not a depth.

Against the cylinder, on box maps (harness, 4,000 single steps and 400 walks of 90 steps a map; walks that
part by over a centimetre):

| Map | Steps that differ | Steps slower by the grade | Walks that part | Walks explained: on a flight / a ceiling over the head / a step up a tick apart |
|---|---|---|---|---|
| city | 31 | 65 | 3 | 33 / 1 / 4 |
| speedway | 0 | 1 | 0 | 0 / 0 / 0 |
| Magnolia | 1 | 3 | 0 | 4 / 1 / 1 |
| Albany | 1 | 2 | 0 | 11 / 1 / 0 |

Of the city's 31 steps, 26 are an edge above the brow (the head meets a slab's or lintel's edge on its
curve and comes closer before it is stopped, 13 to 190 mm), 3 an edge at the knee and the brow at once,
1 below the knee, 1 other; one of the 26 is a cylinder put 7.6 m along by a slab at its head, where the
capsule moves 0.2 m. The city's three walks that part (by 75 mm, 1.5 m and 2 m) were not followed step by
step; in the two that part by metres the capsule walked about a metre further, as a head that slides past
an edge does. The cylinder from 0.1 mm aside parts from itself in none of them. A step costs about the same
(city 37 / 41 us, Magnolia 6.9 / 7.8, Albany 8.5 / 11.2, speedway 7.1 / 3.0 capsule / cylinder). The
city's box stairwells slow a body by the grade as any flight does.

### 10.5 Stairs and footfalls

Real treads, climbed by the step rule and the ground probe as box treads always were; W walks up or down.
Footfalls (the client's `StrideAccumulator`, and `PhysicsUtils.FootOnFloor` in `LocalPlayerController`
and `OtherBodies`):

- **The foot goes down on the floor under it**: under the foot if that floor is the body's height, a
  body's radius ahead (the tread a body climbing is stepping onto), or whatever is under the foot within a
  step (going down, the body is held at the tread it is leaving until its whole footprint is past it, and
  the foot is already on the one below). Its height is that floor's, its material that floor's surface.
  A body on something the triangles do not hold (a vehicle's floor) keeps its foot where it was.
- **On a flight the cadence is the treads'**: a footfall on arriving at every `TreadsPerStep` treads, the
  step the speed asks for over the flight's going, rounded, one or two (a leg spans two risers at a run,
  not three). At the game's walk a flight is taken two treads at a time. A tread arrival is counted from
  the floor the body last settled on, so the step rule's lift and settle are one tread. A kerb or a
  doorstep alone is walked over at the walk's own cadence.

AudioLab `--stair-walk [scene=shapes|city] [sprint]` walks a concrete flight up and a wooden flight down
(shapes), or a block of flats' stairwell to the top storey and back (city), through the server's movement
and the client's stride, and checks every footfall:

| Walk | Footfalls | On a tread at the foot's height, of the flight's material |
|---|---|---|
| shapes, walking | 21 | 21 (concrete up, every 2 treads, 0.27 s apart; wood down, 0.17 s apart) |
| shapes, running | 20 | 20 |
| city stairwell, up and down | 154 | 154 |

Before the foot was put on the floor under it, 8 of 21 on the shapes were 17 to 58 cm above the tread
under them, and the flight's footfalls came every 3 to 4 treads.

### 10.6 Vehicles: four wheel rays

`OpenFPS.Common/Geometry/WheelRays.cs`: a ray down at each wheel, each its own height, normal and surface
(walkable faces only), and the body's rest from them: the mean height, pitch from the axles, roll from the
sides. A driven car (DrivingSystem) sits on its wheels: a slope pitches it, a wheel up a kerb rolls it,
each wheel's surface sets that wheel's grip (gravel under one side is gravel under that side). The box
path's car sat level at the floor under its middle. Traffic still rides its lines (stage 3 drapes them).

### 10.7 The readers that read boxes

| Reader | Now |
|---|---|
| `SightGrid` (the look of the turn narration) | the triangle world: solid fixed boxes in their layers, fixed things said by name but not solid in a sight-only layer (`GeometryLayers.Announced`, in no other query's mask); the index keeps only what the triangles do not hold |
| `BoxColumns` (the room surveys at load) | a tree over the boxes' extents (the BVH builder): the same boxes in the same order |
| `EarlyReflections` (one-off echoes on the game thread, the worker's first order) | the acoustic triangle world, published by the occlusion worker: near surfaces from its tree, legs asked of it; one copy per place where two faces lie flush |
| `ImageSource` via `EngineReflections` (engine and one-off echoes) | faces near the path from a tree over them, legs asked of the acoustic triangle world |
| Ricochet's face | the triangle's normal |
| `Diffraction` round a box, `CompositeAcoustics`' faces, `VehicleShadow` | kept: every solid a map holds is a box or a shape bounded by one, a composite is built of boxes, a moving body is its box; facets and sections for shapes come with stage 4, bodies with stage 6 |
| `SpatialGrid`'s dynamic half | a tree, not cells: what moves is filed as the cells it covers and a BVH over them answers the same questions with the same things in the order they were filed |
| Server `RefreshGrid` | incremental: each fixed thing remembered as filed, only what changed filed again and its tiles built (`OPENFPS_INCREMENTAL_REFRESH=0` files whole); a test checks the grid and the triangles against a whole refresh after a wall moved, one destroyed, one given a form and stairs put up |

Moving things are not instances in the top tree: a body and a vehicle are boxes, so a box test answers
what a triangle instance would, and building the top tree again every tick for them buys nothing until
vehicles have body meshes (stage 6). The grid's dynamic half no longer has cells.

### 10.8 The harness and the measurements

`--geometry-parity map=<id> n=4000 only=rays,ground,overlap,steps,capsule,enclosure,occlusion,bullets,
collision,determinism,tracks,routes,echoes,engineechoes,sightgrid,ricochet`, on the stage 2 build. A cell
is the cases that differ, with the ties (explained classes, listed in the log) in brackets; the box path is
the old side except where a row says otherwise.

| Comparison | Cases | city | speedway | Magnolia | Albany |
|---|---|---|---|---|---|
| RaycastSingle (60 m) | 4,000 | 0 (35) | 0 (1) | 0 (36) | 0 (42) |
| RaycastMaterial (60 m) | 4,000 | 0 (22) | 0 | 0 (33) | 0 (30) |
| CastSight (600 m, glass seen through) | 4,000 | 0 (27) | 0 (4) | 1 (44) | 0 (49) |
| RaycastAll (8 rays, 60 m) | 8,000 | 0 (43) | 0 | 0 (104) | 0 (56) |
| Ground height, server / client | 4,000 each | 0 (2) / 0 (2) | 0 / 0 | 0 (1) / 0 (2) | 0 (3) / 0 (5) |
| Body overlap per solid, both ways out | 13-27,000 | 0 (2) | 0 (2) | 6 (2) | 4 |
| Body intersects per solid (the step test) | 7-14,000 | 0 | 0 | 0 | 0 |
| Movement step, cylinder, server gather | 4,000 | 0 | 0 | 0 | 0 |
| Walks of 90 steps, cylinder | 400 | 0 | 0 | 4 | 2 |
| Movement step, the body against the cylinder | 4,000 | 31 (65) | 0 (1) | 1 (3) | 1 (2) |
| Walks of 90 steps, the body against the cylinder | 400 | 3 (38) | 0 | 0 (6) | 0 (12) |
| EngineReflections, every box / the face tree | 400 | 0 | 0 | 0 | 0 |
| SightGrid.Cast, the index / the triangles | 4,000 | 0 (40) | 0 | 0 (48) | 0 (52) |
| Ricochet face, the box's axis / the triangle's normal | 1.8-2.5k | 0 | 0 | 0 | 0 |
| Echoes, first order, the list / the world | 200 | 1 (1) | 2 | 0 (4) | 0 (2) |
| Echoes, second order in the room window | 200 | 1 (2) | 3 | 0 (1) | 1 (2) |
| Echoes, third order with the flutter | 200 | 3 (2) | 2 | 1 (1) | 2 (2) |
| Echoes in woods, first / second / third | 200 each | 0 (4) / 0 (4) / 1 (5) | - | 0 (5) / 1 (2) / 4 (3) | 0 (1) / 1 / 2 (1) |
| Enclosure survey | 80 | 0 (7) | 0 | 0 (2) | 0 (3) |
| Occlusion, one ray / five rays | 4,000 / 1,000 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| Bullet segment, first static solid | 4,000 | 0 (28) | 0 (6) | 0 (23) | 0 (25) |
| CheckCollision | 4,000 | 0 | 0 | 0 | 0 |
| Server and client worlds, same bits | 4,000 | 0 | 0 | 0 | 0 |
| TrackClearance | every point | 0 | 0 | - | - |
| Routes, whole build each way (openings, routes, legs, barriers) | 1,000 each | 0 | 0 | 0 | 0 |
| Routes after one tile changed | 1,000 each | 0 | 0 | 1 leg (0.003) | 0 |
| A tile built after a change / from nothing | 1,000 each | 0 | 0 | 0 | 0 |
| A streamed client at the spawn, rays within 250 m | 4,000 | - | - | 0 | 0 |

What differs:

- **Far from the origin** (stage 1's class): Magnolia's one CastSight is 1.2 mm on a wall 1.9 km out, its
  6 and Albany's 4 body contacts are 0.02 mm and a tenth of a degree apart, 1.1 to 1.5 km out.
- **Walks of the cylinder** part by over a millimetre as often as the box path parts from itself started
  0.1 mm aside (Magnolia 4 of 400 against 4, Albany 2 against 2); the worst, 40 mm, the box path's own is
  39.9 mm.
- **Echoes**: 0 to 4 of 200 a kind, nearly all the world finding a copy the list does not: a second
  order copy off the ground, or every copy where the source stands a centimetre or two over a roof or a
  floor (the list's leg test begins inside the solid it leaves and counts it in the way). Two cases find
  the same number at slightly different points (a gain 0.069 against 0.070). The ties are a copy off a
  surface laid flush on another, which the box test lost.
- **Routes after one tile changed**: one leg on Magnolia, 0.003 dB, on the box grid's side; the tile
  built after the change is the tile built from nothing on every map.
- **The six-digit turns are gone**: with turns normalised at load, the routes against the box grid are
  exact on every map (stage 1 had 0-6 of 1,000 differ and 8-17 within 0.3 dB, all from them).
- The SightGrid's index now holds nothing on any map (it held 111 to 30,245 things): every fixed thing is
  in the triangles, solid or sight-only.

Measured besides:

- **One-off echoes on the game thread** (`EarlyReflections`, a source within 40 m of the listener):
  on Magnolia a one-off sound outdoors to third order with the flutter cost 1.47 ms on average and 17 ms at
  worst over the box list, 0.57 and 11 ms on the tree; first order 0.93 / 0.27 ms; the room window 0.35 /
  0.04 ms. On the city the worst one-off went from 55 ms to 14 ms. In Magnolia's woods (crowns and woods
  are not solid, so nothing reflects there and the search is cheap either way) 0.77 / 0.17 ms. The list
  lost a copy wherever two surfaces lie flush (each leg began on the other, which the box test counted as in
  its way); the tree keeps one.
- **Engine echoes** (`EngineReflections`, Magnolia): 2.4 ms a search with every box, 0.32 ms with the tree;
  the same reflections, 400 of 400.
- **The server's RefreshGrid** (one box moved / nothing changed): Magnolia 104 / 28 ms filed whole, 24.7 /
  19.8 ms incremental; Albany 89 / 27 to 22.9 / 17.1 ms; city 31 / 8 to 10.5 / 8.8 ms. What is left is
  reading every fixed thing to see what changed.
- **Route answers and the heap** (found by the Resonance team): a voice's kept answer held the graph of
  routes it was asked of, and the graph its scene; a stopped voice kept them alive until a thousand answers
  had piled up. A new graph now lets every answer about an old one go, and a voice the worker forgets takes
  its answer with it (`RouteAnswers`; a test checks a superseded graph is collected). Over a 2 km drive
  across Magnolia at 15 m/s with a one-off source every tenth frame (`--stream-walk churn=0.1`, heap after a
  full collection every 10 s): 203-285 MB through the drive and 160 MB at the end before, 129-278 MB and 86
  MB after; with three one-off sources a frame the thousand-answer trim kept both about the same (92 and 88
  MB at the end).
- **Loading** (server map load / the client's acoustic map at the join): Magnolia 944 ms / 361 ms, Albany
  1,209 / 544 ms, city 408 ms, within a few per cent of stage 1.

### 10.9 Left after stage 2

- Facets: coplanar triangles merged into mirrors for image sources and early reflections, so a wedge's
  slope or a stair's treads reflect as themselves; sections for diffraction round a shape (stage 4).
- Moving bodies as instances in the top tree, with their meshes (stage 6); VehicleShadow with them.
- Traffic on draped roads and the wheel rays under it (stage 3).
- Stairs in the generators: gen_city.py's flights are box treads, which walk and sound the same; a
  `Stairs` form would make each flight one entity.
- The capsule's rounded foot, if wanted: it needs a ground probe the shape of the foot and a step rule
  for it, and changes every kerb.

### 10.10 Decisions for Cody (decided: see "Stage 2 decisions" at the end)

1. **The body**: the cylinder's foot under a rounded head, measured against both the cylinder and the
   full capsule (10.4). Keep this, or go to the full capsule (every kerb and step changes; ledges caught
   while falling)?
2. **Speed on stairs and slopes**: up a flight at 0.45 of the walk, down at 0.65, a 1 in 12 ramp at 0.86.
   At the game's walk (a jog) a flight is still 2 m/s up, two treads a footfall. Slower, faster, or none
   on stairs?
3. **Footfall cadence on a flight**: two treads a footfall at the game's speeds (it never comes out one,
   since the game's walk is three times a real one). One tread a footfall would be 7 a second.
4. **A driven car pitches and rolls** on its wheels (kerbs included); its sound positions and its
   passengers turn with it. Keep?
5. **The speedway's ground is dirt** between its own surfaces (where the loader's concrete was).

### 10.11 Where the code is (2026-10-07)

The triangle world is a project of its own now, `OpenFPS.Geometry`, the lowest of the sound library's
(docs/SOUND_LIBRARY_BOUNDARY.md, section 15): `OpenFPS.Common/Geometry/*` is
`OpenFPS.Geometry/Triangles/`, and `GeometryUtils`, `SpatialGrid`, `BoxColumns`, `PerfProbe`, `TileKey`,
`WallBuild` and `BoxContainment` are beside it. Namespaces did not change. `EntityGeometry` and
`MoverPoses` stay in Common, on the host's side. New shapes, queries, terrain and the voxel layer go in
that project and take `SolidSpec`, `Surface` and plain vectors, never an entity.

## 11. Stage 3 as built (2026-10-09)

Built with streaming stage 2 (docs/WORLD_STREAMING.md, "Stage 2 with terrain"). Step T1: the terrain itself.

### 11.1 The heightfield

`OpenFPS.Geometry/Triangles/Heightfield.cs`. A tile of ground is posts on a square grid (126 a side at
2 m for a 250 m tile), a material per cell, heights in the world. Each cell is two triangles split along
the diagonal from post (i, j) to (i + 1, j + 1). On the wire and on disk the heights are whole
centimetres over the tile's base, so the server and every client turn them into the same floats.

- **Part of its tile's piece.** A `SolidSpec` can carry a `Heightfield`; the builder keys it by its
  middle, so it lands in its own tile's piece beside the boxes. Terrain triangles are numbered after the
  piece's box triangles, and under each triangle is a prism down to 20 m under the tile's lowest post,
  numbered after the boxes' solids. `SolidCount` still counts boxes only: whatever walks every solid of
  a piece (the routes through openings, the scene's box lists) is asking about boxes.
- **Queries.** Rays walk the cells under them (Amanatides and Woo), skipping cells whose corners the ray's
  height cannot reach; each cell's two triangles are tested by the same Möller-Trumbore test as a box's.
  `Closest`, `Any`, `All`, `Containing` and `Overlapping` find the ground; `Along` and `Column` (the
  routes through openings) do not. Of a face laid flush on the ground and the ground, the face is met:
  a prism counts as covering the whole tile (Ties).
- **The skirt.** A skirted tile (`Heightfield.Skirted`, only the far ring's coarse ground) also shows rays
  the outer side of each edge prism, from the edge down to the floor: two triangles a cell side, numbered
  after the surface's, each belonging to the prism it is a side of (`PrismOf`). The walk visits an edge
  cell down to the floor. It closes the crack where coarse ground meets 2 m ground (docs/WORLD_STREAMING.md,
  Coarse ground); 2 m ground has none.
- **The body.** A prism is a convex solid with eight triangles and five planes, so `SolidContact` meets
  it as it meets a wedge: a walkable slope is a floor, a bank steeper than 45 degrees a wall. The movement
  gather asks for prisms only round the body (`TriangleWorld.Overlapping` with a terrain box), not under
  the 30 m it gathers boxes from.
- **An entity.** `TerrainTileComponent` (Posts, Spacing, HeightsCm, Cells, Materials) on an entity at
  the middle of its tile, at its base height. Its collider is `ColliderShape.Terrain` (appended to the
  enum), not solid, size zero: every reader of boxes passes it by. `EntityGeometry.Classify` makes it
  static geometry. The definition carries it in `EntityDefinition.Terrain` (appended).
- **Sound.** The acoustic store and both Steam Audio paths (tile sub-scenes and the whole scene) take the
  ground as triangles of open ground with each cell's material. What counts as open ground was "a thin
  slab whose top is under a metre"; on terrain it is under a metre over the ground beneath it, so a road
  lying on a hill 8 m up is still left out of the listener's trace. The worker reads the ground from the
  snapshot's definitions (`WorldSnapshot.TerrainSolids`), never from the background geometry build, so
  it never lags them.
- With `OPENFPS_TRIANGLES=0` there is no terrain: the box path cannot stand on it.

Tests: `GeometryTerrainTests` (heights and planes, centimetres, the ground probe and its normal, cell
materials underfoot, a walk up and down 15 %, a 60 degree bank as a wall, a walk across a tile edge, a
hill in the way of a ray, inside the ground, a kerb on a slope, the server and a client making the same
bits, open ground on raised terrain).

### 11.2 The real places on real ground (step T2)

- **The survey.** `tools/places/ID/elevation.json` is 3DEP as downloaded (about 5 m; Magnolia 600 x 600
  posts, 55 to 79 m over the sea; Albany 598 x 598, 58 to 76 m), whole centimetres in base64
  (`fetch_place.py elevation`), about 0.95 MB each. 5.1 asked for 1/3 arc-second with 1 m lidar where it
  exists; the 5 m export is what the archive has, and is finer than 10 m.
- **The map carries it** (`MapData.Elevation`): the survey resampled onto a 5 m grid of the map's own
  metres, whole centimetres over a base, y = 0 the ground at the spawn address. About 1 MB of each map.
  `gen_osm.py` sets everything on it (`ground()`, the grid read bilinearly) and the server lays the 2 m
  terrain from the same grid read the same way (`MapElevation.HeightAt`), so both agree.
- **What the generator does** (`ground_all`): a building on a level pad at the ground by its front door
  (the middle of its wall nearest the street); every strip of a line (roads, verges, sidewalks, drives,
  paths, ballast, creeks) pitched along its run and level across, in pieces of at most 20 m; a road's
  heights, and its verges', sidewalks' and junctions', from the ground averaged over a 20 m square
  (`road_y`), so a road runs smooth and its parts agree; lawns tilted to the ground under their corners;
  ponds level at their lowest bank; fences and rails pitched along their length and set 5 cm in;
  anything else solid set down to the lowest ground under it; named places, rooms outside buildings and
  the woods' volumes stretched over the ground under them. Road centrelines (what traffic rides) get a
  point every 20 m at the road's height. The 3 km dirt slab is gone. Without elevation.json nothing
  changes: the flat maps come out byte for byte as before.
- **What the server does** (`TerrainBuilder`, at map load): the survey at every 2 m post over the map's
  bounds in 250 m tiles (196 a map), then graded over the whole map at once, so tile edges agree. Under
  each slab lying on the ground (fixed, solid, a box at most 0.6 m thick, a metre each way, tilted under
  30 degrees, its underside within 1.5 m over or 3 m under the ground at a post) the ground is flattened
  to the slab's underside; every post of a cell the slab covers is cut down to it, so no part of a
  triangle of ground can rise through it (both are planes over a triangle: corners under means all
  under). Round each graded patch the ground blends back to the survey over 4 m. The tiles are entities
  on the "ground" layer (coarse). A map with an elevation gets no loader's natural ground; its void plane
  is 20 m under its lowest post.
- **Measured** (`RealPlaceMapTests`): away from grading the terrain is within 0.137 m of the survey at 99
  points in a hundred and 0.43 to 0.50 m at worst (median 4 to 7 mm) over 3,000 points a place; along
  every road every 5 m (10,209 points on Magnolia, 8,035 on Albany) and at every lawn and drive the floor
  is the slab, never the ground; a walk of 750 ticks from the spawn down the drive and along the street
  both ways is on the ground every tick, its biggest step 0.12 to 0.14 m; there is ground under every
  25 m of the play area. Server load: Magnolia 1.5 s (was 1.3), Albany 2.7 s. Entities: Magnolia 36,291
  (was 32,598), Albany 46,020 (was 41,327), from the roads in 20 m pieces. A join at medium detail on
  Magnolia is 1,388 KB packed (was 530 KB): about 17 KB a tile of ground, 49 tiles.
- **Not done**: road cross-sections (crown, gutter) and draped road meshes, bridges and tunnels,
  creeks cut in, a grade limit on roads (12 %, 6 % on a highway),
  ground materials from the land cover on the places' maps (every cell is dirt, as the slab was; the world's
  own tiles take theirs from ESA WorldCover since 2026-10-10, docs/WORLD_STREAMING.md). Done since: the
  coarse ring's ground at 7.8 m (2026-10-09, docs/WORLD_STREAMING.md "Coarse ground in the far ring");
  swept kerbs on roads with sidewalks (Albany, stage 4, 12.2).

### 11.3 The places on the world's grid at 2 m (2026-10-09)

The world's ground at Bobcat Lane was 0.9 m under the map's: the map's survey was the archive's 5 m
export in degrees, a coarser product than the 1 m lidar the world's 2 m tiles get. Now both are the same
request.

- **The survey.** `fetch_place.py elevation` asks the 3DEP service itself for 2 m cells on the place's UTM
  grid, the grid the world's tiles are on, over every whole 250 m tile the map reaches (Magnolia 14 x 14
  tiles, 1,751 x 1,751 posts, 49.95 to 78.03 m; Albany 14 x 13, 1,751 x 1,626, 58.10 to 75.91 m). Each row
  is kept as its difference from the row before and deflated: 3.0 MB (Magnolia) and 2.1 MB (Albany),
  against 8.2 MB plain. Asked tile by tile as the world asks, the service gives the same heights to the
  half centimetre (three tiles of Magnolia, 47,628 posts: worst 5 mm).
- **The map is laid on the same grid.** `gen_osm.py` projects to UTM metres less the 2 m post nearest the
  place's origin (`tools/utm.py`, the same series as `OneWorld/Utm.cs`), so the map's 2 m posts are the
  survey's own: nothing is resampled, and the map is the world's tiles moved, never turned (`MapData.Utm`).
  Everything moved under 1.5 m near the spawn (Magnolia's spawn is now -11.2, 23.2; Albany's -25.3,
  -10.6) and by the grid's turn further out (1.4 degrees at Magnolia, 0.1 at Albany).
- **The map ships the file** beside it (`maps/places/ID.elevation`, the same bytes as elevation.json, so
  git stores it once) and names it (`MapElevation.File`, `Encoding` "zlib-row-delta"); the map JSON no
  longer carries the heights (Magnolia 13.7 MB, was 14.7; Albany 18.0 MB, was 19.1).
- **Road pieces overlap 5 cm** at the joins inside a run: two pieces pitched differently left a crack a
  ray straight down at the join could fall through to the ground under the road (found on Albany's College
  Park Drive once the 2 m ground was there).
- **Measured** (`RealPlaceMapTests`): away from grading the terrain is the survey to a median of 0 to 1 mm,
  99 points in a hundred within 1.7 to 2.8 cm, at worst 6.7 to 17 cm (two triangles to a cell against the
  survey read bilinearly, on the steepest banks); was 13.7 cm and 0.43 to 0.50 m. No road point, lawn or
  drive has the ground on top (10,217 points of road on Magnolia, 8,033 on Albany). The 750-tick walk from
  the spawn is on the ground every tick; its biggest step is 0.20 m on Magnolia and 0.40 m on Albany, where
  the walk runs along the side of a drive (1009 Belmont Avenue) standing over a roadside dip the 2 m survey
  shows and the 5 m one did not. There is ground under every 25 m of both; some creek banks are now steeper
  than a body can stand on, so they are walls, as GEOMETRY.md 11.1 says a steep bank is.

## 12. Stage 4 as built (2026-10-10)

The shape library, footprints and roofs in the generators, the importer and mesh assets on the wire, and shapes in
the quick build. Rooms derived from seeds by flood (2.7) and sound points on models (MATTER.md 8) are the rest of
the stage (12.6).

### 12.1 The shape library

`OpenFPS.Geometry/Triangles/Shapes.cs` (stage 2's shapes, the builder, the surfaces), `Shapes.More.cs` (the new
ones), `Polygons.cs` (outlines: simplifying, ear clipping with holes, convex pieces; sines from arithmetic),
`StraightSkeleton.cs` (roofs), `Facets.cs` (facets and `MeshCheck`). Every shape fills its collider's box, as
stage 2's do, so `ColliderSize` stays the bounding box every box reader sees; `ShapeSpec` (appended fields) says
what the size does not.

| Kind | Numbers | Made as |
|---|---|---|
| `Cylinder` | `Segments` | an upright round column, trunk or tank; an ellipse when X and Z differ |
| `Cone` | `Top`, `Segments` | a cone or frustum |
| `Sphere`, `Dome` | `Segments` | a ball; the top half of one on a flat base |
| `Prism` | `Outline`, `Holes` | an outline stood up the box's height, holes through it |
| `Roof` | `Outline`, `Style` | solid from the eaves to the slopes: `Hip` (the straight skeleton of any outline), `Gable` (a hip whose triangular ends are stood up where the ridge meets them), `Shed`, `Flat`; the pitch is what the box's height makes it |
| `Swept` | `Profile`, `Path` | a profile drawn along a 3D path, mitred at each bend |
| `Mesh` | `Mesh` | an imported asset (12.3), stretched to the box |

- **Closed edge for edge.** Every shape is a closed solid whose every edge two triangles share going opposite
  ways, facing out (`MeshCheck.Check`), with no triangle without area and no sliver; its convex pieces (each a
  closed convex solid) fill it, their volumes summing to its own; it fills its box to a tenth of a millimetre;
  a ray from inside leaves one more face than it enters and the pieces agree on what is inside
  (`GeometryShapeLibraryTests`, 25 shapes). Stage 2's stairs and arch met their treads and tops at T-junctions:
  their sides (and the arch's faces) are one polygon each now, the same surface.
- **The same bits everywhere.** Everything is worked out in doubles from the numbers and made floats once; round
  shapes take their sines from arithmetic (`DetMath`: the C library's last bit differs between Windows and
  Linux). Made shapes are kept with the spec they were made from (a roof's skeleton is made once, however many
  readers ask).
- **Sides as sound can tell them apart.** A round shape with no `Segments` takes a multiple of four: 8 under a
  quarter metre of radius, 12 at 0.3 m, 32 from about 3 m. Sides not a multiple of four are stretched to meet the
  box.
- **The straight skeleton.** Edges move in at one speed, events (an edge shrinking away, a reflex corner
  splitting the front) taken one at a time, earliest first, so a rectangle's or an L's simultaneous events are
  just events a moment apart. Nodes within a millimetre are one (a footprint a hair off square kinks its ridge
  by micrometres). The faces must cover the outline exactly or the roof is laid flat; on Magnolia none is
  (12.2). 300 random star outlines and 400 near-squares and L's moved by 10 µm to 2 cm: every one closed.
- **Surfaces.** Each shape names its surfaces (`Shapes.SurfaceNames`: stairs body and treads; a roof's slopes,
  ends and underside; a prism's sides, top and bottom ...) and `ShapeSpec.Materials` gives each a material, the
  thing's own where it says none. A triangle's surface is its own in the piece (`SolidSpec.Slots`), so a foot on a
  carpeted tread is on carpet and a ray at the riser meets concrete. A roof's slopes are roofs (the scope's "on
  a roof"); sound goes through a roof as its deck (0.2 m, or `Thickness`), not its attic (`Shapes.PanelOf`).
- **Facets.** A mesh's facets (`MeshAsset.Facets`) are its triangles that share an edge, lie in one plane (a
  millionth of a turn, a tenth of a millimetre) and are one surface, with their area, plane and the rectangle
  round them. A box has 6, a wedge 5, sixteen stairs 36, a twelve-sided column 14, a hip roof 5. The image
  sources mirror a shape from its facets: `EarlyReflections` (with the hit point checked on the facet itself,
  not only its rectangle) and `EngineReflections` (each facet's rectangle). The first answer off a ramp comes off
  its slope (test), not the top of its box.

### 12.2 The real places: footprints, roofs, trunks, kerbs

`tools/gen_osm.py` and its port `OpenFPS.Server/OneWorld/WorldBuildings.cs` (the world's buildings), kept the
same (`WorldBuildingsTests`: Magnolia's 1,506 buildings at medium detail, every entity within a centimetre, the
same turn and the same form).

- **Walls go round the footprint itself**, simplified (a corner within 10 cm of the line through its neighbours,
  or within 25 cm of the one before, does no work), one wall a side, inside the side, a reflex corner closed by
  lengthening its two walls by their thickness. A 148-sided Albany building has its sides; an L is an L. Walls stay
  boxes, so wall transmission, layers in contact, face openings and the room survey read them as before.
- **The floor is the footprint** stood up a slab thick (`Prism`), one entity (a home's ceiling too), and **the roof is the footprint's
  roof** (`Roof`): a house a hip or a gable by its seed at about 6 in 12 (a quarter of its narrow side, never above
  its measured height), a metal building a low gable, a church a steep one, a brick shop flat. At every detail
  level (the ridge was high detail only).
- **Rooms are rectangles inside the footprint** (every footprint covered from inside, a rectangle's in one), and a
  room's outward sides are grown to the inside face of the wall where it is near (the grid stands up to a cell off
  a wall at an angle, and leaves a bay or a porch too small for a rectangle of its own); the corners of the
  footprint stop them. A cell is in when its middle is in by a millimetre each way, so gen_osm and the world (each
  projecting by its own sums) choose the same cells.
- **The front door** is on the longest stretch of a street-facing side with a room behind it (within 0.7 m of the
  side), its room the one there; a planned house's doors go on the side of the ring their room is behind.
- **Solid outbuildings** (sheds, and below high detail garages, workshops, barns) are their footprints stood up.
- **Trunks are round** (`Cylinder`, eight sides), the woods' and the single trees OpenStreetMap places.
- **Kerbs**: a road whose tags give it a sidewalk has a kerb at the carriageway's edge each side it has one, a swept
  profile 15 cm wide and 15 cm over the road with its top edges taken off, in the road's own pieces (none on
  Magnolia; 537 pieces on Albany).
- **The city** is not changed: its buildings are already their real shapes, its flights box treads that walk and
  sound the same, its pavements kerbed by their own edges. Nothing there is clearly better as a shape.

Measured (`GeometryStage4MapTests`, `RealPlaceMapTests`):

| | Magnolia | Albany |
|---|---|---|
| Map JSON | 13.7 MB → 14.7 MB | 18.0 MB → 21.2 MB |
| Entities | 36,374 → 36,317 | 46,013 → 49,732 |
| Walls (entities) | 9,258 → 10,485 | 14,376 → 20,248 |
| Regions (rooms) | 1,734 → 1,810 | 2,735 → 2,864 |
| Openings found in rooms' faces at load | 1,788 → 2,476 | 2,873 → 5,463 |
| Shapes | 2,604 prisms (floors, ceilings, sheds), 502 hip, 582 gable, 6 flat roofs, 3,030 trunks | 3,106 prisms, 659 hip, 770 gable, 2 flat roofs, 1,416 trunks, 537 kerbs |
| Triangles | 239,340 of boxes + 155,026 of shapes (trunks 84,840; prisms 51,576; roofs 18,610) | 404,808 of boxes + 194,398 of shapes (prisms 99,612; roofs 44,398; trunks 39,648; kerbs 10,740) |
| Hip or gable roofs laid flat | 0 | 1 |
| Doors whose sides are not their rooms (the test's step either side) | 1 of 1,324 | 16 of 1,916 |
| Server load in the tests, old map / new (shared machine, noisy) | 5.6 s / 4.5 to 7.4 s | 5.7 s / 5.9 to 6.8 s |
| Acoustic map at the join | 1.1 s / 1.1 to 1.2 s | 2.1 s / 1.9 to 2.0 s |

The openings found in rooms' faces rose (the gaps where the room rectangles of one house meet, now that every
footprint is covered from inside, and gaps between a room's face and walls at an angle to it). Not yet looked at
one by one: worth a listen in a house on Albany, and a run of `--geometry-parity only=routes`.

### 12.3 Import, mesh assets and the wire

- **Readers of our own** (`MeshImport.cs`, no dependency): OBJ (vertices, faces cut by ears in their own plane,
  `usemtl` or the group as the surface) and glTF 2.0 (`.gltf` with data URIs or files beside it, `.glb`; the default
  scene through its nodes' matrices or TRS, a mirroring node turning its triangles round; triangle primitives
  only; Draco, quantised positions and sparse accessors refused by name). SharpGLTF (MIT) was the plan; glTF's
  part we need is a few hundred lines, and the server needs no new package.
- **Readying** (`MeshImporter.Prepare`): corners within 10 µm welded, triangles with no area and repeats dropped,
  a solid checked closed (every edge shared by two triangles going opposite ways) and turned right way out if it
  faced in, an open mesh refused unless it is taken as a sheet, the budget (50,000) kept, slivers and sub-centimetre
  features noted, the whole moved about the middle of its bounds.
- **Assets** (`MeshAssets.cs`): `MeshAssetData` (MemoryPack, then Brotli: `meshes/ID.mesh`), named by the hash of
  its triangles, its surfaces' names and the materials the import gave them, its source and licence. `MeshLibrary`
  (one a process) checks each asset's triangles hash to its name. A body meets a solid mesh as boxes filling it (a
  grid of up to 32 cells on its longest side, merged greedily, at most 512) and a sheet as its triangles 4 cm thick.
- **The tool**: `tools/import_mesh` (docs/AUTHORING.md section 5).
- **The wire**: `MeshAssetRequest` (46) and `MeshAssetBatch` (47), appended to the union; the batch carries each
  asset's file bytes as they are on disk. The server reads `meshes/` at start and refuses a map form naming a mesh
  it lacks (the thing is its box). A client asks for the meshes its definitions name once each, keeps them in
  `LocalApplicationData/OpenFPS/meshes` by id, and builds the things made of them again when they come; the
  acoustic store's tile hash counts whether the mesh is here.

### 12.4 Shapes in the quick build

The quick build (Control+B, the F12 Build tab, `/edit build`) has a Shape kind: stairs, a ramp, a round column,
a cone, a ball, a dome, an arch, a roof, made of a wall's material at the size given (docs/WORLD_EDITOR.md 19).
A shape's own fields are in use and the rest dimmed (the shape's choice lists them), in both clients alike
(`BuildForm` is shared). Said as phrases: `/edit build stairs 14 steps up north`, `/edit build column 0.3 by 3`,
`/edit build roof gable over the floor`.

### 12.5 Tests

`GeometryShapeLibraryTests` (84), `GeometryImportTests` (6), `GeometryStage4MapTests` (2), `EditorShapeBuildTests`
(5), `RoomFloodTests` (5); `WorldBuildingsTests` compares forms too (Magnolia's 1,506 medium-detail buildings the same
in every part); `RealPlaceMapTests`' entity cap is 60,000 (Albany 49,732).

### 12.6 Left of stage 4, and the plan for it

1. **Rooms derived from seeds by flood (2.7).** Built as an instrument, then switched on 2026-10-10 and approved by
   ear ("Switched on", below); regions became places in five steps after it (below).
   `OpenFPS.Geometry/Triangles/RoomFlood.cs`. Quarter-metre cells (a whole number to an acoustic voxel: the three maps' grids are 1 m). Rooms
   whose reaches overlap flood together in one dense block of cells (an array index per cell, not a hash key), and
   the blocks flood on as many threads as are given. In three steps: each room fills what of its own box it can reach
   (all at once, a cell to the first room there); then on into its box grown by a metre, into what no other room
   reached; then past that, into air the walls close in (a wing of a footprint no box holds, a bay deeper than the
   margin), as far as 4 m past the rooms' reach. A room is open where its air meets air under the open sky (a ray up
   from where it leaves the block meets nothing); air meeting another room's air is an opening between them
   (`Room.Meets`). A box is solid where a cell's middle is in it; a box thinner than a cell along an axis is taken as
   thick as a 6-connected flood needs whatever its angle (a digital plane's least thickness: a cell times the
   axis's largest component, half the "a cell's diagonal" of the first version). A shape (a floor, a ceiling, a roof)
   is its convex pieces, each face put out by the same half-thickness for its angle, each piece within its bounds
   and half a cell: sampled at seven points a cell, as first written, a roof's eave a few centimetres thick let every
   room with no ceiling under its roof out. The ground is its prisms as they are. AudioLab `--geometry-parity
   map=ID only=rooms [threads=N]` sets each room's flood against its box; `ROOM_DEBUG=ID[,ID]` traces a room's way out
   (the path from its middle to the open, and the solids where it left its box and where it met the open).

   Before (2026-10-10, half-metre cells, the region = air and its wall cells):

   | | rooms | time | open past the margin | IoU 10th / median / 90th | only the flood / only the box |
   |---|---|---|---|---|---|
   | city | 491 | 1.6 s | 49 | 0.70 / 0.86 / 0.93 | 53,581 / 20,852 m3 |
   | Magnolia | 1,810 | 17.7 s | 874 | 0.57 / 0.76 / 0.85 | 171,134 / 13,759 m3 |
   | Albany | 2,864 | 41 s | 2,106 | 0.52 / 0.72 / 0.82 | 379,945 / 52,296 m3 |

   Now (quarter-metre cells, the leaks below fixed; one thread, then eight; IoU of the air against the box, then of
   the region; only the flood / only the box for the air):

   | | rooms | time | open past the margin | IoU air | IoU region | only the flood / only the box |
   |---|---|---|---|---|---|---|
   | city | 491 | 2.5 s / 2.1 s | 7 | 0.80 / 0.88 / 0.91 | 0.82 / 0.86 / 0.93 | 17,112 / 38,868 m3 |
   | Magnolia | 1,810 | 6.4 s / 1.6 s | 3 | 0.73 / 0.84 / 0.96 | 0.69 / 0.81 / 0.92 | 34,057 / 59,025 m3 |
   | Albany | 2,864 | 13.4 s / 3.6 s | 0 | 0.68 / 0.81 / 0.92 | 0.64 / 0.78 / 0.89 | 121,383 / 214,630 m3 |

   The IoU is of the air now: a box is drawn to its walls' inner faces, so at a quarter metre the wall cells round
   the air lie outside it, and a perfect flood of a 3 by 3 m room scored 0.65 on the region. What the air does not
   reach of a box is mostly the cells that must hold its floor, its ceiling and a lining a few centimetres thick (a
   2.58 m room keeps about 2.25 m of air; a 1.29 m bathroom lined on one side 1.0 m), and boxes drawn past their
   footprint: Albany's "34414 Highway 99e, house", 640 by 193 m, is 98,153 m3 of the 214,630. What the flood holds
   beyond its box is mostly a footprint's parts no rectangle covers (430 rooms on Magnolia, 1,356 on Albany, reach
   past their margin into closed-in air).

   The leaks, traced with `ROOM_DEBUG`, and fixed where they were made:
   - **A 3 cm slot over every door on the real places.** gen_osm's doorway head was `FLOOR_TOP + 2.13` over a leaf
     2.1 m tall standing on `FLOOR_TOP`. The head now laps the leaf by `DOOR_LAP` (`head_over`, and the same in
     `WorldBuildings`), as gen_city's do. Magnolia's open rooms 570 to 469 at once.
   - **A 5 cm slot over the city towers' glass front entrances** (lintel at `floor_top + 2.15`, leaf to 2.12): the
     five ground-floor stairwells. Laps the leaf now.
   - **A 25 cm gap each side of the roof access doors** on the city: the housing's end was cut the lane wide (1.6 m)
     for a 1 m door. The doorway is the door's width now: the five roof accesses.
   - **Air under the open sky was not told from air closed in.** Most "open" rooms on the places reached past their
     margin into a part of the house no room's rectangle covers (a detail-1 house has at most two), or past the
     edge of the block of cells; told apart by a ray to the sky, that air is the room's now (Magnolia 469 to 41).
   - **Roofs with no ceiling under them.** A barn's, a workshop's, a church's, a shop's room is closed by its roof
     form, whose eave is a wedge thinning to nothing; the seven points sampled a cell missed it (Magnolia 41 to 3).
   - Before all these, in stage 4: the gap between two room rectangles' ceilings into the eaves, closed by one
     ceiling over the footprint.

   Open past the margin now, and why: on the city the three parking garage levels (open sides), the tunnel's south
   portal, the hangar (its front is an opening with no door), 58 Alder Street's back garden (walled, under the sky)
   and Main Street block 2 (the street cut down to the tunnel); on Magnolia three barns (gen_osm builds a barn's
   doorway with no leaf); on Albany none. The two "Main Street sidewalk" rooms on the city have their middles in a
   solid. Narrow rooms keep their air: every hall on Magnolia (0.98 m wide inside) has four cells across, IoU 0.69 to
   0.86 (0.00 to 0.34 before). Left alone: eight rooms on the places under 0.7 m wide (the narrowest 0.16 m, a
   rectangle `grow_side` pulled in), which the flood gives what the walls give them.

   Gated in tests: `RoomFloodTests` (a bay deeper than the margin is the room's, a hall 1.1 m wide wherever the grid
   falls, a shut leaf stops it at six wall angles, a hip roof with no ceiling closes a room, and the city: open only
   where it opens by design, median IoU over 0.8) and `RealPlaceMapTests.Rooms_flood_shut_and_as_drawn` (each place:
   only barns open, no room without a start, median IoU over 0.8). The largest block is Albany's 640 m house, 646 by
   356 m with its reach, 77 million cells (about 460 MB while it floods): a block per room's bounds, or pages, before
   the client floods a world tile.

   **Switched on (2026-10-10, approved by ear the same day: "everything sounds good").** `EntityAcoustics.RoomsByFlood` floods every indoor room of a list of
   definitions through a triangle world with each door leaf at its shut pose (`Portal.OpeningCentre/OpeningRotation`;
   both sides move leaves in their worlds), and `RoomCells` says which room's flood holds a point. The server
   (`MapManager.RoomsOf`, after every change to the fixed geometry, in the background on the game's server) and the
   client (`ClientWorldState`: in each refresh of the acoustic map, and again whenever the geometry is published) call
   it with the same definitions, so both put a point in the same room. A point's region is the room whose flood holds
   it, else the smallest box of the regions that did not flood (yards, streets, a room whose middle is in a solid),
   else the outside: the client's `SpatialService.GetRegionAt`, and the server's `MapManager.RegionAt`, which
   `DoorManners`, the editor's doors and `PlaceAt` now ask. `GenerateRegions(..., floods)` marks a flooded room's
   voxels where its flood holds the voxel's middle, not its box, and the map carries the cells (`AcousticMap.RoomCells`).
   Region ids are the entity ids, as before. The boxes serve until the first flood lands. Groups of rooms whose seeds
   and nearby pieces have not changed are not flooded again (`RoomFlood.Cache`).

   AudioLab `--geometry-parity map=ID only=agree` sets the server's lookup against a client's at 24 random points in
   and round every indoor room the client holds (the whole city; the tiles a medium detail setting holds round the
   spawn on the places):

   | | points | the same | moved against the boxes alone | server flood | client map |
   |---|---|---|---|---|---|
   | city | 11,784 | 11,784 | 1,341 | 2.5 s | 3.1 s (489 rooms) |
   | Magnolia | 10,512 | 10,510 | 979 | 2.5 s | 0.6 s (438 rooms held) |
   | Albany | 22,512 | 22,509 | 2,355 | 6.1 s | 2.2 s (938 rooms held) |

   The five that differed were in houses 400 to 520 m from the spawn. Traced (2026-10-10, `agreedebug=ID` and the
   cells holding each point, both sides), two causes, both fixed:
   - **Two floods over the same air** (Magnolia 17423 Oak Lane, Albany 1240 Alandale Avenue). Two rooms of a house
     whose boxes grown by the margin did not overlap flooded in separate groups, and each reached the other's air
     past its margin. Both held the cells; which one a lookup found depended on the order of the spans, which
     differs with how many rooms a side holds. Now groups whose air reaches into another group's reach flood again
     as one (`RoomFlood.Overlapping`, as many passes as it takes), so a cell is one room's.
   - **A house's rooms sent apart** (Albany 5790 Mike Street, 5592 Pacific Boulevard). A living room and a kitchen
     open to each other through an archway with no door are joined by no doorway, so `MapTiles` grouped them with
     nothing: across a tile's edge the client held one, and its flood took the other's air. Rooms that flood
     together (`RoomFlood.Groups`, half a metre more generous) now go together, as rooms joined by doorways did
     (`MapTiles.FloodTogether`, in `Build` and `AddPlaced`). The client now holds 947 rooms round Albany's spawn,
     not 938.

   After: the city 11,784 of 11,784 points the same, Magnolia 10,512 of 10,512, Albany 22,728 of 22,728.
   `RoomFloodTests.Rooms_that_reach_the_same_air_past_their_margins_flood_as_one` and
   `WorldStreamingTests.Every_entity_of_a_real_place_is_in_the_tiles_it_overlaps` (rooms that flood together share
   their tiles) hold them; `RoomFloodTests` holds the city to every point the same.

   **Albany's 640 by 193 m house** was OSM way 125102808, tagged `building=yes` and nothing else: an L of six points,
   643 by 355 m (13.7 ha), round the address point of 34414 Highway 99e, so gen_osm made it that address's house
   with a 640 m living room. A home is now under a hectare (`HOME_MAX_M2`; the largest in both places is 3,721 m2):
   it is a "building" now, and Albany is regenerated (one entity fewer, ids after it moved down one; Magnolia byte
   for byte the same). Its block is still 88 million cells, so floods are bounded: a group whose block is over
   `RoomFlood.MaxBlockCells` (64 million) is not flooded and its rooms keep their boxes (`Room.TooBig`, told to the
   report). A cell's claim is a 16-bit index now, 3 bytes a cell while flooding instead of 5, so the largest group
   flooded (Albany's college, 52 million cells with its reach) takes about 160 MB.

   **Measured from their floods (2026-10-10, approved by ear the same day with the floors fixed: "everything sounds
   great. no complaints").** A flooded room's volume, face areas and face materials come
   from its flood, not its box (2.7 item 4). `RoomFlood.Flood(..., measure: true)` gives each room a `Lining`: per face
   of the box's frame (floor, ceiling, north +Z, south, east +X, west), the area of each material lining it, the area
   open to the sky and the area that is another room's air, the volume, and the air's bounds. A ray goes from the air
   cell to the surface for each group of faces in a 1 m cube (one face in sixteen on a wall; one for every face was 5
   million rays and 13 s on the city): it gives the material (the carpet, not the slab under it), the surface's angle
   (a cell's face over the normal's L1 length, so a wall at 45 degrees is not its staircase) and the gap from the
   cells to the surface, which goes into the volume and grows each face by the gaps across it (a 2.7 m wall over a
   carpet's cell is 2.5 m of cells). For a room open past its margin the air under the open sky is the outdoors, not
   the room (the margin past a hangar's open front, or over a roof). A room with no air under a roof (a walled garden)
   is not measured. Only the client measures (`RoomsByFlood(..., measure: true)`); the server floods for lookups.

   `RoomAcoustics.Measured(room, lining)` puts it on the `Room` value (`Volume`, `FaceAreas`, `FaceAbsorption` per
   band and broadband, `Extent`, and `Materials` as the main lining of each face). A face stays "None" where the box's
   was (an open side, a portal's end) and becomes "None" where half of it or more is open sky. Another room's air
   absorbs nothing in the measure, because the routes count what leaves by an opening at its own S·tau (the box's open
   ends counted it twice). Readers: `RoomAcoustics.Volume`, `FaceAreas`, `AbsorptionArea`, `Reach` and `DecayMs`;
   SpatialAcoustics' room lift; `OpeningRoutes`' room absorption, face reach and declared hash.
   `AcousticVolumeGenerator.GenerateRegions(..., floods)` measures as it builds the map; the client measures again
   when the geometry is published and when a room arrives (`EntityAcoustics.RoomOf`). Rooms that do not flood keep
   their boxes.

   AudioLab `--geometry-parity map=ID only=measure [sort=rt|volume|gain|field]` sets each flooded room's measure
   against its box, with the routes' absorption before and after (the field through its openings goes as 1/A):

   | | rooms | flood / with the measure (8 threads) | volume flood/box 10th / median / 90th | surface | Sabine RT60 mid | field through openings moved over 3 dB |
   |---|---|---|---|---|---|---|
   | city | 488 | 2.6 s / 4.3 s | 0.89 / 0.95 / 0.97 | 0.99 / 0.99 / 1.03 | 0.45 / 0.92 / 1.44 | 67 (all quieter) |
   | Magnolia | 1,810 | 2.3 s / 3.1 s | 0.98 / 1.02 / 1.16 | 1.00 / 1.03 / 1.16 | 0.95 / 1.00 / 2.54 | 26 (23 louder) |
   | Albany | 2,862 | 6.8 s / 9.4 s | 0.98 / 1.07 / 1.27 | 1.00 / 1.08 / 1.27 | 0.96 / 1.12 / 2.89 | 12 (8 louder) |

   (The places' rows are after the ground was graded under their floors, below; before it Magnolia had 36 rooms over
   3 dB and Albany 43, most of them quieter for a floor of dirt.)

   What moved most, and why: the terminal concourse's ceiling is acoustic tile, not the concrete over it (Sabine 6.1 to
   1.1 s, the field through its openings 5.4 dB down); the city's houses have carpet and sofas the box survey did not
   see (1.19 to 0.45 s, 3.6 dB down); the tunnel halves lost their ends counted twice (2.5 dB up); the corridors'
   ceilings are plaster, not the carpet of the flat above (1.6 dB up); the hangar's 221 m2 front is open, its margin
   outside is not (15.6 to 9.2 s). On the places the box survey had grass and dirt for ceilings and walls (a bathroom
   "Grass, Plaster, Plaster, Plaster, Grass, Grass" is tile and plaster: 7.8 dB up).

   Found by it, and fixed: on Albany 778 rooms and on Magnolia 160 measured floors mostly of the ground. The floors of
   the places are their footprints' prisms since stage 4, and `TerrainBuilder.Slabs` took only boxes, so the ground
   was graded under no floor and came up through the slab wherever the pad (the ground at the front wall) was below
   it: on a slope, the back of every house. A footstep there was on dirt: the ground probe met the terrain in 1,253 of
   Albany's 2,864 rooms (7,624 of 25,776 probes, nine a room) and 377 of Magnolia's 1,810 (1,694 of 16,290). A prism
   is a slab of its outline now (`TerrainBuilder.Slab.Outline`), and the ground is cut to its underside as under a
   road: the world is dirt and the floor is laid on it, the dirt dug level for it where the slope rises (up to
   `MaxCut`, 3 m) and filled where it falls (`MaxFill`, 1.5 m). Raising each floor over the highest ground of its
   footprint was the other way; it would have stood houses on stilts on their low side and moved every room, door
   and pad the generator set. After: the probe meets the terrain in no room on either place
   (`AudioLab --geometry-parity only=floors`; `RealPlaceMapTests.The_ground_never_rises_through_what_lies_on_it`
   checks nine points a room). No room measures a floor mostly of the ground; four have a few square metres of it
   where their air reaches out of an open door (two barns on Magnolia, two garages on Albany). The places copied into
   the world are graded by the same slabs, and `WorldPlaces.FormatVersion` is 3, so stored copies are made again.
   `RoomFloodTests` holds a 4 by 3 m room at 0, 30 and 45 degrees: volume within 5 %, walls within
   5 % together, the carpet as the floor, and Sabine's time the box's within 10 %; and a room with an open door, its
   doorway open and the air outside it not the room's.

   Where to listen (`/tp` x east, y north, z height, on the floor; the field change is for a sound heard from the room
   through its openings):
   - city: Terminal concourse, south end, `/tp 211.2 3.3 0.3` (field 5.4 dB down); 23 Birch Street, `/tp -347.0 -34.5
     0.3` (3.6 dB down, the city's houses alike); Tunnel, south half, `/tp 0.0 -262.5 0.3` (2.5 dB up); Union Building
     corridor, floor 7, `/tp 20.3 67.0 21.3` (1.6 dB up); Hangar, `/tp 254.0 190.0 0.1` (1.5 dB down); Parking garage,
     level 0, `/tp -66.8 62.0 0.3` (1.3 dB down).
   - Magnolia (after the ground was graded under the floors): 19415 Turtle Dove Lane, barn, `/tp -88.8 -253.0 3.5`
     (8.5 dB down, its open door); 31909 Beaver Street, bathroom, `/tp 70.8 -31.4 -1.2` (7.8 dB up); 26120 Nichols
     Sawmill Road, house, `/tp -1065.9 420.7 3.5` (7.3 dB up); 19609 Turtle Dove Lane, house, `/tp -282.3 -233.4 4.6`
     (6.7 dB down).
   - Albany (the same): 34435 Highway 99e, house, `/tp 317.9 -1070.6 2.3` (6.6 dB down); 777 College Park Drive
     Southwest, building, `/tp 36.2 969.2 -1.5` (4.9 dB up); 5095 Pacific Boulevard Southwest, house, `/tp 53.0 795.7
     -0.9` (4.6 dB up); 6251 Lanier Street Southwest, bathroom, `/tp 89.1 -68.8 -0.7` (3.4 dB up).

   **Regions become places: the plan (started 2026-10-10, after part A was approved by ear).** In order, each its
   own commit:
   1. DONE (2026-10-10). The ambience beds go (and the map-level `AmbienceId` from MapData and the three generators,
      the maps regenerated with only that line gone; `AmbisonicFormat` and `AmbisonicBedDsp` stayed for the lab
      until 2026-10-11, when they went with the recordings, 0525f576): `ClientAudioSystem.SetMapAmbience` and the region bed, `PlayAmbientBed`/`StopAmbientBed`
      in `IAudioProvider`, `FmodAudioProvider` and `AudioEngineFacade`, `PrefabTemplate.AmbienceId`, the manifest's
      ambience in Program.cs and ClientGameSession.cs, `Room.AmbienceId`; `MapManifest`'s field and
      `RegionComponent.AmbienceId` stay on the wire, unread, with a one-line comment. Tests: AmbienceAndWeather,
      ClientAudioHarness, EmitterStreamReplay, SteelDoorSound, VoiceLifecycle, Speedway, PrefabSpec, BeaconAids.
   2. DONE (2026-10-10, approved by ear 2026-10-11). The flood from a point, below ("The flood from a point").
   3. DONE (2026-10-11, approved by ear 2026-10-11: "complete, success everything sounds good"). Openings framed from where the floods meet them, below ("Openings from the
      floods"). Portals derived through door leaves were not needed: every door is an entity with its two rooms,
      framed by its leaf.
   4. DONE (2026-10-11). The readers of `RoomSize` onto the flood, below ("The readers on the flood").
   5. DONE (2026-10-11). Out of authoring, below ("Nothing authored of a room").
   A box stays where nothing floods: an outdoor place (a name over an area, a yard, a street, a park), a vehicle's
   cabin (it moves; its box comes from the vehicle), a room whose block is over `MaxBlockCells`, and every room until
   the first flood lands at load (or the load waits for it).

   **The flood from a point (step 2, 2026-10-10, approved by ear 2026-10-11).** A seed is a point (`Seed.Size` is read only by
   `RoomFlood.Groups`, for `MapTiles`). Each room floods out from its point, stopped by walls, floors, ceilings, shut
   leaves, openings and air under the open sky; no box and no margin. An opening is a portal with no leaf
   (`RoomFlood.Barrier`, from `EntityAcoustics.FloodBarriers`), its cells marked a cell wider and taller than it is
   (where its side meets a jamb at an angle to the grid, a cell between the two let the flood round it). Its cells,
   like a wall's, are the first room's to reach them, so a point in a doorway is in a room. Measured, an opening to the
   outdoors is open sky; one between rooms is another room's air.
   - The block starts as far as the walls round the point (six rays along the axes, a metre past what each meets, 2 to
     120 m). A block whose air reaches its side grows that way by half its size (at least 8 m), keeping its cells and
     claims, and the flood goes on from the cells at the side: nothing is flooded twice, and each 8-cell page is
     rasterised once a flood (`PageStore`; a shape's pieces once per 64 m square). Growing by doubling was tried:
     Albany's college (33204) overran `MaxBlockCells` and lost its flood.
   - Rooms flood apart; two whose air turns out to be the same air flood again as one, a cell to the first to reach
     it. A wall's or an opening's cell that two rooms flooded apart both reach is the room's whose point is nearer
     (`RoomCells`).
   - Ordered biggest first, one at a time, so the biggest is never left behind a thread's chunk.
   - The lining's sample cubes are on the map's grid; a cube whose middle and furthest faces meet different faces of
     the room (a corner of walls at an angle to the grid) is asked face by face (on the city every face asked alone
     moves no room's measure more than 2 %).

   The generators now say where a room ends where no wall does. gen_city: an opening in the slab where each stairwell
   flight comes up through the floor (the storey below ends there) and in the roof slab under each roof access; the
   car park's open sides over the spandrels and its east side between the piers; the tunnel's quarters and its two
   mouths; the concourse's thirds (their boxes up to the suspended ceiling now, not into the void over it, where the face
   finder found a 0.6 m strip beside each opening); the hangar's front as its frame. The stairwell's doorway onto the corridor is the
   doorway's size (aperture 1.0 m, was 1.4); since 2026-10-11 it holds a fire door (below, "Marlow Tower's leaks"). Main Street block 2 stops at the tunnel's mouth (it ran 40 m in under
   the roof and flooded as a room). gen_osm and `WorldBuildings`: a thin opening in the middle of the gap between two
   touching rectangles' rooms; every part of a footprint no rectangle covers goes to the room it shares most of its
   edge with and opens onto each other room it touches; a part touching no room is a room of its own if it is 2 m2 or
   more (`WING_LEAST`; named as the building's rooms are, on a planned house a den from 12 m2, else a closet); a
   barn's doorway is an opening to the outdoors. All written after everything else, so no id moved
   (`WorldBuildings.Later`; `WorldStore.GeneratorVersion` 6). `mapgen.open_faces` lists the faces of a room's box with
   nothing behind them, as a check.

   Times (`only=rooms`, 8 threads): city 0.9 to 1.5 s (490 rooms), Magnolia 2.4 s (1,811), Albany 5.2 s (2,864).
   Albany's is its 640 m building (30832): 4.2 s flooding 10.5 million cells of air before its block would have to
   grow past `MaxBlockCells`; the rest is done by 4.8 s.

   Gates against main (`only=agree`, `only=meets`, `only=dump` set point by point against main's dump):
   - Server and client: every point the same (city 11,760, Magnolia 10,536, Albany 22,728).
   - Air meeting with nothing between: none. On the city no two rooms' air meets and none meets the sky. On Magnolia
     2 pairs and on Albany 17 meet, each two rooms of one building already joined by openings (round an opening's
     end); none meets the sky.
   - Lookups (24 points in and round each room; a room whose box changed is left out): city 54 of 11,641 moved: the tunnel's north quarter takes the 10
     that were Main Street block 2 under the roof, 10 in 58 Alder Street's walled garden (its point is in a hedge),
     points just past the tunnel's south mouth and the hangar's front now outdoors, wall cells between flats now the
     nearer flat's. Magnolia 287 of 43,440 (0.66 %), Albany 611 of 68,736 (0.89 %): wall cells between two rooms of a
     house now the nearer's, and the barns' margins outside their doorways (18 points) the outdoors.
   - Measures: city 6 rooms over 5 %: the tunnel's north quarter (908 to 1,602 m3, its mouth open) and the five roof
     accesses (32.9 to 30.5 m3, ended in the roof slab). Magnolia 245 of 1,810 and Albany 687 of 2,862 over 5 %, by
     design: the parts of a footprint no rectangle held belong to a room now (the common rooms' air up 0.4 % on
     Magnolia, 1.4 % on Albany), and two touching rooms divide their air at the opening between them instead of by
     distance. Most moved: 19617 Turtle Dove Lane, house (4.7 to 17.4 m3), the barns (19415 Turtle Dove Lane: A mid
     218 to 30 m2, its air no longer counted out of its doorway), Linn County Juvenile Detention (1,916 to 4,386 m3).
   - Routes (the client's openings, these maps against main's, this code): city, each stairwell's storey link is
     one opening the size of the hole (2.4 by 5.0 m, 12 m2), where the face finder found 0.6 m strips beside the flight
     (about 5 m2): up to 4 dB more between storeys; the car park's east side one bay framed by its piers (11.6 by 2.5
     m) where two were found (5.6 m each); the tunnel's north mouth 12 by 5.45 m to the outdoors where Main Street
     block 2 was; the concourse's thirds as they were (29.54 by 5.77 m, authored now). Magnolia: 142 openings
     between rooms of a building found in faces are the authored ones now (sizes within 5 %, 0.3 m over to the gap's
     middle), 207 new, the barns' doorways 3.00 by 2.15 m (the face finder cut them at the room's box: 3.00 by 1.65 to
     1.87). Albany: 342 found become authored, 676 new.

   Tests: `RoomFloodTests` and `RealPlaceMapTests.Rooms_flood_shut_and_as_drawn` flood with the openings, as a load
   does, and hold no room open to the sky on any of the three maps; `CityOpeningsTests` sets each opening's sides
   against the rooms' floods, as a client has them (by the boxes, the car park's west side fell in Wharf Avenue's
   sidewalk, which overlaps it by 1.5 m); `WorldBuildingsTests` holds the port's openings and leftover rooms to
   where Magnolia has them. The emitter streams and the renders are unchanged, unregenerated.

   Where to listen (`/tp` x east, y north, z mid-room; the two Marlow spots on their floors, as checked in
   `StairsTests.AListeningSpotIsWhereABodyStands`): Marlow Tower stairwell, floor 3, `/tp -14.2 -102.7 9.28`
   (between storeys); Marlow Tower roof access, `/tp -10.65 -103.9 24.25`; Tunnel, north portal, `/tp 0.0 -212.5 2.8`;
   Magnolia 19415 Turtle Dove Lane, barn, `/tp -88.8 -253.0 5.4`; 19617 Turtle Dove Lane, house, `/tp -299.2 -271.0
   6.4`; 16510 Country Star, house, `/tp -320.0 702.1 0.7` (273.7 to 470.1 m3); Albany Linn County Juvenile Detention,
   `/tp 1464.5 1274.2 1.9`; Favorite Mistake, `/tp -53.3 643.2 0.6` (900 to 1,419 m3).
   **Marlow Tower's leaks (2026-10-11, approved by ear 2026-10-11).** Cody, in the game after step 2: on floor 3's corridor "I heard the
   train clear as day go by", walking round the tower "wind or rain or something leaking in like it was coming
   through the wall" and "a siren pop through quietly"; inside a flat on floor 3 everything sounded good. Traced from
   his client log and with `--path-probe ... fields` (each other place's field at the ear, the level its reverb bus
   is set to), `--rain survey`, `--pop-hunt`, and set against builds of 448ea7e0 (before step 2). The floods were
   none of it:
   - **The train was its horn**, at 74 dB at his ear and -5/-5/-10 dBFS low/mid/high: as good as unblocked. The
     paths say -40/-61/-87 dB at the corridor's south end (one 35 cm brick wall, the end wall, toward the line) and
     under -95 by the openings, before step 2 and after. A train's voices kept the band gains each voice started
     with (their ids are no entity's, so the path pass skipped them, and a re-submission leaves a voice's tone
     alone), and the horn started on the answer the previous horn left, minutes before and from elsewhere. Since
     c719c374 (2026-10-07). Now `PlaceTrainVoice` hands each voice its path every frame, and `VehiclePath` takes
     no answer asked from another place (`AsyncAcousticWorker.TryGetResult` gives where the ear was).
   - **The rain on the stairs** (his log: -18/-18/-18 dBFS, "blocked 20 %", 6 m off, on the flight from floor 3).
     A patch behind something took the path to its middle, and the near north quarter there falls on the pavement
     11 m below and the roof 13 m above: its middle was in the stairwell's air. Each patch behind something is now
     heard by the path to each thing it falls on (`RainSurvey.Result.Sources`, the heaviest four; `RainField.PatchPath`),
     one path a frame. Under it, the hand-rolled path's five rays round a source (10 degrees, 0.15 to 0.8 m) put
     one through the floor a source sat on: rain 0.3 m over the roof, or a footstep in the flat above, had a point
     in the room below and came through any slab at -14 dB flat. Each point round a source is pulled back to the
     first solid on its way out (`SpatialService.InItsAir`). Heavy rain (Leq, the scene summed):

     | where | before | after |
     |---|---|---|
     | where Cody stood on the stairs (his log: -10.15, -104.6, feet 9.28; too near the wall for /tp) | 26.1 dB | -9.3 dB |
     | floor 3 stairwell, `/tp -10.8 -104.2 9.28` | | -8.3 dB |
     | floor 7 corridor, under the roof | 26.4 dB | -14.4 dB |
     | floor 3 corridor by the stair door | | -40.4 dB |
     | flat 31F | | -50.3 dB |

     The near north quarter on the stairs: its middle -14/-14/-14 dB, its two surfaces -70/-85/-100.
   - **The siren** came in by the front entrance (glass, -18/-28/-45 dB), up the stairwell and out of an open
     doorway on every storey. Step 2 made the storeys one space (12 m2 between them, where the face finder had 5
     m2), so this route rose 6 to 14 dB: a siren on Main Street opposite the entrance reached floor 3's corridor
     at -56.3/-56.4/-69.7 dB (-73.0/-70.8/-82.9 at 448ea7e0). An exit stair's enclosure has a self-closing fire door
     on every storey (IBC 1023); the generator left the doorway open since 2026-09-20. gen_city puts a steel push
     bar door there now, pushed from the corridor into the stair, in the portal's place so no id moves (37 doors):
     the same siren -69.4/<-100/<-100 dB, the stairwell's field in the corridor -12.6 to -60.9 dB. The flat is as it
     was (walls first). The pops (20 to 30 dB for a quarter second, many cars at once, in the stairwell) are older:
     at 448ea7e0 too, 3 to 6 a minute with 40 cars round floor 1's stairwell (`--pop-hunt ear=-11.05,4.9,-100.84`),
     each a moving source's simulated transmission jumping from -80/-100 to -35/-52/-80 dB on Dock Street. Not the
     source pool (audit P1, 2026-10-11): still sources along the lane give the same, -35.4/-52.4/-79.5 dB from x
     -19.5 to -15.5 at z -128.5 and -56.5/-82.7/-100 on either side (`--path-probe explain`). The line from the
     stairwell down to the street crosses the floor slab's edge (box 1579, 15 cm concrete) all along; either side
     of that stretch it also crosses the stair's 1.07 m concrete guard (1576 at x -13.9, 1575), and between them it
     passes the head of the flight. The way over the guard is 3 cm, but its leg to the street runs through the
     brick front (1585), so the search does not believe it and the guard is charged as a whole wall in a row with
     the front. A way round one thing that passes through another is never paid as diffraction times that other
     thing's transmission; with it, the guard would cost a few dB and this stretch would not stand out. Done since,
     for every asker (below, "Round one thing, through another"); what is left of these pops is the slab's edge, not
     the guard. The pavement's pops were the way over the top
     giving up after four boxes (`OpeningRoutes.OverTheTopPasses`): fixed, below.

     The pavement before the entrance (`--pop-hunt ear=-6.4,1.77,-103.5`, 40 cars): 41 to 56 pops a minute, each a
     car on Wharf Avenue 190-260 m off, behind the multi-storey car park and the tower, from -90/-100/-100 to
     -25/-25/-25 dB and back in a third of a second. Along the lane at x -131.5 the way over the top existed at z
     62.0-63.0, 70.5-71.5 and 76.5-79.5 and nowhere between: the car park is a parapet and a slab a storey, each one
     a leg runs into is a pass, and the search stopped at four. It goes on now until the string is clear or an end
     is under something; the lane is -25/-25/-25 at every point and the pavement has no pops (1 high-band in some
     minutes, a clear line between two buildings 235 m off). The source pool's own faults (S4) were never seen to
     fire; they are closed anyway (docs/CLIENT_NOTES.md, "The worker's answers").

   The train at the corridor's south end, through the end wall, stays what the panel model gives 35 cm of brick
   (-40/-61/-87): with the horn's path followed, a freight's rumble is what comes through.

   Where to listen (each checked by `StairsTests.AListeningSpotIsWhereABodyStands`: clear to /tp, the floor at z):
   floor 3 corridor by the stair door `/tp -19.95 -101.5 9.29`; its south end `/tp -19.95 -110.5 9.29`; floor 3
   stairwell `/tp -10.8 -104.2 9.28`; flat 31F `/tp -14 -85 9.29`; floor 7 corridor `/tp -19.95 -100 21.29`;
   before the front entrance `/tp -6.4 -103.5 0.12`; floor 1 stairwell, where the pops are left, `/tp -10.8 -104.2
   3.28`, and the pop hunt's ear there `/tp -11.05 -100.84 3.28`.
   **Round one thing, through another (2026-10-11, unheard).** The barrier model before: what got past the things
   on a line was, per band, the better of the line through all of them (Steam Audio's transmission, or the panel
   model along a route's leg) and a way round that was clear of the whole scene (`BarrierPathDifference`: round
   each box on the line, its legs bent round up to two more, or the string over the top), paid as Maekawa's loss
   for its detour and the spreading over the longer way. A way round one box whose leg ran on into a wall was
   thrown out, so a low wall before a heavier one was charged its whole transmission in a row with the heavier one.
   Now there is a second kind of way, in `OpeningRoutes.BarrierGains`, which every asker of the barrier uses: the
   worker's simulated path (in place of `BarrierPathDifference` and its own Maekawa sum), and every leg of the
   routes by the openings (`LegGains`), so one-offs, sirens, trains and anything the worker answers. The
   hand-rolled tracer (beacons, birds, rain patches, the stand-in before an answer) has no barrier search and is
   unchanged (docs/SPECIAL_CASES_AUDIT.md S14).

   - **The model.** For each box on the line, its most promising way round (shortest, by its detour times what the
     rest of the line lets through), the legs straight from the bend to the source and to the ear. Each leg pays
     every box it passes through by the panel model (`WallTransmission`, sheets in contact as one construction,
     `Constructions.Through`); a leg may instead bend round what it meets within a metre of the bend (the other
     piece of a guard, a post), the extra detour added. What arrives is Maekawa's loss for the whole detour
     (Kurze and Anderson's form of it, `Diffraction.InsertionLossDb`, 24 dB at most) times the spreading times what
     the legs let through: G = IL(δ) · d/(d+δ) · T(legs), per band. It competes with the straight line, the clear
     ways round and the openings as before, per band the better. The basis: a ray through a sequence of edges and
     walls keeps the product of what each does to it (Funkhouser et al. 2004: "the product of all the attenuation
     coefficients for the reflecting, diffracting, and transmitting surfaces along the corresponding propagation
     sequence"); a screen's loss is Maekawa's for the extra path (Maekawa 1968, Kurze and Anderson 1971, ISO 9613-2's
     screening term); a wall's is the panel model (Sharp 1978, EN 12354-1, as WallTransmission says); and the
     sound through a barrier and the sound over it are both real and both arrive (the HUD guide's rule: a barrier
     whose transmission loss is 10 dB more than its diffraction loss loses under half a decibel to transmission).
   - **When it is allowed.** Only where everything solid on the way is paid: every box a leg touches, faces
     included, at its own transmission. Not round a leaf standing in its doorway (the doorway's to charge, as
     before). Not round an edge buried within 5 cm of another box (a guard's foot in the floor it stands on: its
     legs would start inside the floor). And never more than the detour's loss times the straight line through the
     boxes the way passes through, so a wall costs the way no less than it costs the line. It can only add: on the
     three maps no pair came out quieter, and the tests hold it below what the heavier wall alone lets through.
   - **Where it is approximate.** The paths compete by the better per band, not by summing their energies (the
     rule already used between the line, the ways round and the openings): at most 3 dB under where two are equal.
     A way bent at several edges is charged one screen's loss for its whole detour; ISO 9613-2's double diffraction
     (its C3) takes up to about 5 dB more for edges far apart (the clear search does the same). The leg's wall is
     paid at field incidence whatever the leg's angle, and the edge is a ray's bend, which wants the wall several
     wavelengths from the edge (1.7 m at 200 Hz: the stairwell's guard is 2 m from the front). One box is gone round
     per way (and what stands within a metre of its edge). The ground of the real places is no box, so a way under
     a raised floor at ground level is not stopped by the earth (the clear search's rule, nothing below both ends,
     holds).
   - **The stairwell** (still sources on Dock Street at z -128.5, the worker with Steam Audio, low/mid/high dB): x
     -27 to -21, before -57/-83/-100, now -45/-67/-96 to -45/-70/-100 (over the guard, 1 to 4 cm, -5/-6/-11, then
     through the brick front 1585, -40/-62/-87); x -15.2 -56/-82/-100 to -45/-69/-100; x -19.8 to -15.6 as it was,
     -35/-52/-80; x -30 and beyond as it was, -57/-83/-100 (the way over the guard comes down through the slab's edge
     and the brick both). `--pop-hunt` (40 cars, 30 still sources, 120 s): 11 mid-band excursions before, 22 to 31
     dB; 8 after, each a car coming into the stretch from -69 (the guard's shadow) or -80 dB to -53. What is left
     is the slab's edge, not the guard: from x -19.8 to -15.6 the line goes into the front through the 15 cm edge of the floor slab (1579) between the two storeys'
     brick and out of its top, and the panel model charges that as 15 cm of concrete crossed face to face, where it
     has gone through nearly a metre of it. A sheet entered by its edge is a flanking path (EN 12354), not a panel;
     that is the panel model's (and Steam Audio's faces, `LayeredFaces`), a part of its own: done since, below
     ("A solid gone into by its edge").
   - **The pavement** (`--pop-hunt ear=-6.4,1.77,-103.5`, 120 s): 1 mid and 4 high before, 2 mid and 6 high after
     (and 0 in an earlier run of this change); every one a car on Wharf Avenue 235 m off seen clear between two
     buildings, or the clear search's own way round, not a way through anything (the traces say "over an edge" or
     visibility 1).
   - **The three maps** (`--barrier-sweep map=X n=4000`: an ear 1.6 m over a place's floor, a source in another
     place within 150 m, the line meeting something; the leg as the routes charge it against the openings, per band
     the better): louder by more than 0.5 dB, low/mid/high, city 6.9/1.9/2.1 % of pairs (median of those
     7.9/10.9/5.6 dB, most 56.2/38.6/29.6), Magnolia 20.7/9.7/0.1 % (11.8/5.0/15.5, most 52.7/46.0/18.1), Albany
     8.7/3.9/0.0 % (12.2/4.5/-, most 47.8/20.2/0); none quieter. The street heard indoors (`outdoor`: the ear
     indoors, the source 0.5 m over the ground, 2,000 pairs): city 8.8/4.0/3.0 %, Magnolia 18.8/17.1/16.1 % within
     80 m, Albany 8.5/6.5/4.7 %. The biggest, each spelled out by `pair=` and `--path-probe explain`:
     - a line that clips a corner, a slab's edge or a parapet by a few centimetres before a heavier wall, which
       paid both before and now pays Maekawa's grazing loss and the wall: Union Building flat 70B from Central
       Street, -100/-100/-100 to -45/-70/-100 (the car park's slab edge, 3.5 cm, then the flat's brick); on the
       real places a neighbour's shed or roof, then a timber-frame wall (-24/-48/-76): -100 to -29/-54/-85;
     - round a brick pier or panel and through the window beside it: Selby House flat 01F from Main Street block
       8, -100/-100/-100 to -44/-83/-100; 60 Alder Street from the next back garden, -66/-97/-100 to -24/-57/-70;
     - ways bent at several edges that the clear search's budget missed: Selby House's stairwell, floor 5 to
       floor 4 by the stair hole, -18/-18/-26 (the openings) to -7/-11/-19.
   - **The cost.** A blocked straight leg, the same pairs both ways: city 2,037 to 2,245 µs, Magnolia 1,487 to
     1,496, Albany 1,799 to 1,908 (+10 %, +1 %, +6 %). One source in the worker (`--path-probe legcost`, the
     stairwell's): the barrier search 1.0 to 1.9 ms, both kinds 1.2 to 2.1 ms. The ways tried are bounded: four
     boxes, one way each (twelve looked at for an edge in the air), four bends in all.
   - **The guards.** The eight emitter streams and the render fingerprints are unchanged: the replay runs the
     worker without Steam Audio, and none of its scenarios has a route leg the change moves. Tests:
     `OpeningRoutesBarrierTests` (a low wall before a heavier one, a guard in two pieces, never quieter).
   - **References.** Maekawa, "Noise reduction by screens", Applied Acoustics 1 (1968) 157-173,
     https://doi.org/10.1016/0003-682X(68)90020-0. Kurze and Anderson, "Sound attenuation by barriers", Applied
     Acoustics 4 (1971) 35-53, https://trid.trb.org/View/130867, the form 5 + 20 log10(√(2πN)/tanh √(2πN)) as
     restated in https://www.ct.upt.ro/buletinhidro/2013-1/004%20Muscă%20Determination%20s.pdf. ISO 9613-2's
     screening (a thin barrier one diffraction a path, a thick one two; over the top and round the sides):
     https://jcaa.caa-aca.ca/index.php/jcaa/article/download/880/597/987, and its limits (20 dB one edge, 25 dB
     two or more; each object's own and the highest kept): https://doku.datakustik.com/CadnaA/en/Industrie_ISO.html.
     Funkhouser, Tsingos, Carlbom et al., "A beam tracing method for interactive architectural acoustics", JASA 115
     (2004) 739-756, p. 747: https://people.csail.mit.edu/addy/research/funk-beamtracing-jasa04.pdf. HUD, Noise
     Guidebook, chapter 4, transmission against diffraction:
     https://housingnm.org/uploads/documents/Noise-Guidebook-Chapter-4.pdf.
   - **Where to listen** (each `--walk-pair map=X ears=...`, the server's collision: a body stands there, z the
     floor): Marlow Tower floor 1 stairwell `/tp -10.8 -104.2 3.28` and the pop hunt's ear `/tp -11.0 -100.8 3.3`
     (cars on Dock Street, south); city: Union Building flat 70B `/tp 25.0 25.8 21.4` (Central Street, east),
     Marlow Tower stairwell floor 0 `/tp -11.8 -97.0 0.1` (Main Street, north), Brandt Court flat 02B `/tp
     -27.8 190.7 0.1` (Wharf Avenue, south-west); Magnolia: 26618 North Carrol Lane `/tp -129.4 1268.1 0.4`, 16523
     Burklin Lane `/tp 1377.5 94.6 -1.0`, 26410 Country Woods Trail `/tp -822.0 708.6 4.7`; Albany: 777 College Park
     Drive Southwest `/tp 253.0 934.4 -1.9`, 6237 Chapman Place Southwest `/tp -642.8 -0.8 -2.6`, 1551 Beaver Court
     Southwest `/tp -535.0 859.2 -4.0` (the real places: what is outside, louder through the next roof or shed).
   - **What argued against it on the real places: floors that were only a finish.** Fixed, next paragraph. The
     floors explain few of the louder pairs: of the pairs louder by the model, Magnolia's rooms 1 of 829 (low band),
     the street heard indoors 3 to 6 of 269 to 374 a band; Albany's rooms none, the street indoors 1 to 5 of 96 to
     254.

   **A floor is a construction (2026-10-11, unheard).** Every home on the real places built at medium detail (the
   whole of both places outside the spawn's few hundred metres, and every home of the world's tiles) stood on its
   finish: the footprint stood up from -0.15 to 0.12 m in carpet or wood, laid on the ground, where every other
   building stands on a 25 cm concrete slab and a home at high detail has a 2 cm finish on that slab. On the maps
   before, 1,441 rooms on Magnolia and 1,959 on Albany had a floor with no structure under it (half 27 cm of
   carpet, a porous fabric whose prefab passes 0.3/0.5/0.8, half 27 cm of solid wood on the ground). The rule
   now, in gen_osm.py `build()` and `WorldBuildings.Build` alike: every building's floor is the concrete slab
   (-0.15 to 0.10 m over the pad), and a home's finish (the same carpet or boards, chosen as before) is a second
   prism of the footprint from 0.10 to 0.12. What you stand on is the same material at the same height, so
   footsteps and the rooms' measured floors do not change, and the ground is graded to the same undersides. The
   slab keeps its entity id (its prefab and height change); the finishes are written after the steps outside the
   doors (gen_osm `finishes_later`, `WorldBuildings.Later.Finishes`), so no other part moves: Magnolia 38,226
   entities to 39,199 (973 finishes), Albany 53,031 to 54,156 (1,125), every earlier entity the same but the
   slabs. `WorldStore.GeneratorVersion` 8 remakes the world's tiles; the places' copies in the world follow their
   maps' checksums. Upper floors and ceilings have no such gap: a two-storey house is one storey of rooms in a
   taller shell (no upper floor is built), and every home's ceiling is one 6 cm plaster sheet over the footprint
   under the roof; the city's flats are carpet on a 15 cm concrete slab over a plaster soffit.
   - **The test.** `RealPlaceMapTests.Every_room_s_floor_stands_on_a_structure` (both places) and the world's
     buildings in `WorldBuildingsTests` (Magnolia's footprints through the world's generator, Tomball's tiles):
     at nine points over every room's floor, the solids stacked under what a body stands on (each less than 10
     cm under the one above) must hold a structure, a slab (airtight, stone or metal, 8 cm or more) or boards
     (airtight wood, 18 mm or more) over a void of 10 cm or more, the ground read from the place's terrain
     (`FloorStructure`). Read from materials and thickness, never names. It fails on the old maps by the counts
     above and passes on the new ones (2,864 rooms on Albany, 1,811 on Magnolia, 153 and 290 of the world's).
     A gap thinner than 10 cm counts as contact: nine rooms have a neighbour's driveway, or a service road (the
     Exxon on Magnolia), lying 6 to 8 cm over their finished floor, pitched so its underside clears the finish
     by a few millimetres (1024 Belmont Avenue's living room has 1034's driveway in it). That is a separate
     fault, a drive or road laid through another building's footprint: fixed, next paragraph.

   **A road stops at a wall (2026-10-11, unheard).** Nothing laid along a line went round a building: on the maps
   before, 276 pieces on Albany and 110 on Magnolia of a road, verge, sidewalk, drive or path lay in a building's
   footprint. A made-up driveway ran straight from the road to its house at the end h01 picks, through whatever
   stood between (1034 Belmont Avenue's, a house behind 1024 on a flag lot, through 1024's living room); a mapped
   drive drawn into its garage went in; a footway mapped through the college's cafeteria ran through it; and a
   service road ran through a 23 by 8 m footprint at the Exxon on Magnolia that is most likely the forecourt's
   canopy, which Microsoft's footprints trace as a building. Three rules, in gen_osm.py and the world's port alike:
   - A building the carriageway runs into (its footprint within half the way's width of the way's line) is not
     made; the road is kept with its lanes the whole of its width. The world's tiles had this rule
     (`WorldFeatures.Site`); gen_osm.py had only its comment. Magnolia 5 (the Exxon canopy, 18673 Turtle Creek
     Lane, 19903 Turtle Dove Lane, 24597 Spencer Boulevard, a building off a private road), Albany 4 (102 41st
     Avenue Southeast, through which 41st Avenue runs, 34378 Looney Lane, two others). On the places such a
     building is still decided and built, then left out with its ids unused, so no other building's id moves; its
     lot keeps its yards, and a place named on it (the Exxon) is a name where it is, as a place without a building.
   - A made-up driveway goes in front of its house where its strip enters no other building: from the chosen end,
     a quarter metre at a time either way, no further than the lot (gen_osm `made_drive_u`; the world, which has
     no lots, 3 m past the house's ends, `WorldFeatures.DriveFor`). A house with no such way has none: Albany 25,
     Magnolia 4 (laid and left out, ids unused).
   - Every piece laid along a line stops short of every footprint it would enter, by 2 cm: the cut that keeps the
     most of it, shorter from either end or with a gap where it passes through, or narrower from either side; a
     piece under 0.3 m is not laid (gen_osm `cut_off_rings`, `WorldFeatures.CutOffRings`). A mapped drive into its
     garage stops at the wall. Magnolia 7 drives cut; Albany 8 drives, 69 paths, 2 verges (34 wholly inside, not
     laid). A piece cut in two keeps its id and its second part is written last.
   - **The test.** `RealPlaceMapTests.Nothing_laid_along_a_line_enters_a_building` (both places),
     `WorldBuildingsTests.Tomball_from_the_trimmed_extract` (the nine tiles in one frame) and
     `A_drive_stops_at_a_wall_and_goes_round_a_neighbour` (a flag lot, a drive into its garage, an aisle through a
     shop): no piece of the roads, verges, paths or drives layers overlaps an upright prism of the structure layer
     (a slab, a ceiling, a solid shed) by more than 0.005 m² (`SurfacesClear`). Layers and geometry, never names.
     Yards are left out: a yard round its own house clips its corner by a centimetre (the lot's frame is not the
     wall ring's), and one under a solid shed is under a solid.
   - **The sound** (`--barrier-sweep n=4000 seed=1` and `outdoor n=2000`, `server=` each map, the pairs both runs
     drew): between rooms, Magnolia 4 of 1,875 changed (one 17 dB quieter in the low band, three up to 5 dB
     louder), Albany 1 of 734 (0.8 dB); the street indoors, Magnolia 3 of 950 (19415 Turtle Dove Lane's barn,
     whose made-up drive ran through it, 12 dB louder from outside), Albany 1 of 359 (0.5 dB). `--path-probe`:
     Country Food Market from under where the canopy stood -58/-61/-100 to -33/-34/-59 dB (its walls are gone),
     from the road beside it unchanged; 1024 Belmont Avenue's living room from the street -48.0/-53.4/-79.0 to
     -47.3/-54.6/-79.0. Ids on the places: everything before the woods keeps its id; the woods and what is
     written after them (named places, shores, the leftover rooms, openings, steps and finishes) move, because a
     drive moved along a lot clears different trees. `WorldStore.GeneratorVersion` 9 remakes the world's tiles.

   **The city's floors (2026-10-11, unheard).** `CityFloorsTests.Every_room_s_floor_stands_on_a_structure` holds
   the city to `FloorStructure`. A room there is a place the server's survey finds indoors with something solid
   over its middle (58 Alder Street's fenced back garden is indoors to the survey and open to the sky; 489 rooms of
   490), and a floor's top is looked for up to 0.3 m over a room's underside, not 0.1: a stairwell's storey runs
   down through the slab under it (one shaft, so the opening a flight comes up through is in a place), and the
   garage's lowest deck, 25 cm of concrete, is laid on the ground under a box that starts at the ground. At 0.1 m
   those 32 stairwells and the garage read as standing on the plaster soffit below and on the dirt, which they do
   not. What failed: the tunnel, its asphalt laid on the map's dirt (now a 25 cm concrete invert under the road and
   its walls, written last), and two Main Street sidewalk places that ran on 40 m inside the tunnel's solid walls
   from the block that meets its mouth (now stopped at the mouth, as that block's carriageway already was; the
   same ids). One entity more, so the city's vehicles' ids move by one and the stored traffic and rail replays were
   rewritten for that alone (every other column the same). The sound: `--barrier-sweep`, 694 pairs between places
   and 210 from outdoors that both runs drew, one changed (1.4 dB, low band); `--path-probe` in the tunnel unchanged.
   - **The sound** (`--barrier-sweep n=4000 seed=1` and `outdoor n=2000`, the old maps against the new, the
     same pairs; `server=DIR` reads a map from elsewhere and `dump=FILE` writes every pair). Between rooms:
     Magnolia 2 pairs quieter (by 21 dB, the low band), Albany 2 louder; the street heard indoors: Magnolia 9
     quieter (median 20 dB) and 2 louder, Albany 12 quieter (median 18 dB) and 4 louder (low band; mid and high
     alike). The louder ones are a way round the new slab's underside: the finish now lies on the slab's top
     face, so the barrier search counts each top edge as buried in it (`OpeningRoutes.Buried`) and takes a bottom
     edge, which on the real places is in the earth, and the earth is no box. 1303 Tamarack Court Southwest,
     the street below, -78/-100/-100 to -29/-58/-93; 1974 Antelope Court Southwest -76/-100/-100 to
     -29/-56/-89. Two rules close it, both the barrier model's: a layer of the same construction that ends
     with the edge does not bury it, and the ground does (done since, below, "When an edge is buried").
   **A solid gone into by its edge (2026-10-11, unheard).** The last of the stairwell's pops.
   - **How a line was charged.** Every box a straight line touches paid its panel figure once: the material
     and the box's smallest side as the thickness, the other two as the face (`WallTransmission.BandGains`,
     at field incidence), however far the line ran inside it. Sheets in contact along the line were one layered
     panel (`Constructions.Through`). The same in every asker: the hand-rolled tracer (`SpatialService`), the
     legs of the routes and the barrier model's ways (`OpeningRoutes.LegGains`, `BarrierGains`, `PaidLeg`) and
     Steam Audio, whose faces each carry a figure such that the three hits a ray makes on one box give the
     box's panel (`SteamAudioScene.MaterialIndex`, and a construction's broad faces from `LayeredFaces`). So a
     line into the 15 cm edge of a floor slab and out of its top, 0.8 m later, paid 15 cm of concrete.
   - **The model.** A panel's loss is a plate driven on one broad face and radiating from the other: its
     bending, at field incidence, so a line across it at an angle pays its thickness, not its chord. A line
     into a sheet by a narrow side has not crossed a panel. The side it meets does not move as a plate face,
     and a sheet's motion in its own plane radiates next to nothing; only bending radiates (Cremer, Heckl and
     Petersson). What gets through either crosses the stuff on the line or turns into bending where the sheet
     meets another, which costs more again: EN 12354-1's flanking path between two elements is their mean
     loss plus the junction's vibration reduction index K_ij (5.7 dB or more for a rigid cross junction,
     Annex E), plus the area term (Hopkins). So a box gone into or out of by a side other than its two broad
     faces, and run through further than it is thick, is charged as the stuff the line runs through: a panel as
     thick as the run, its face the sheet's own (for the flanking share), and never less than the panel
     (`Constructions.RunInside`, `Constructions.Along`). Face to face is the whole thickness crossed at any
     angle; a line in a face or along a joint (inside by less than 2 mm) has touched the box, not entered it,
     and pays the panel as before; a corner clipped (a run no longer than the thickness) pays the panel. A
     two-leaf build (a stud wall, sealed glazing) is not one stuff: a line along it runs in its cavity, and it
     keeps its panel. Layers in contact keep their rule, the layer run along as thick as its run.
   - **Steam Audio.** A face's figure cannot know how far a ray runs inside behind it, and the three hits on a
     box are not always the same two faces, so its scene is unchanged: every face keeps its panel's figure.
     The worker multiplies Steam Audio's transmission, per band, by what the panel model's run rule does to the
     same line against the same boxes as panels (`OpeningRoutes.InPlaneCorrection`, the ratio of
     `Constructions.Through` with and without the runs): 1 unless the line goes into something by its edge.
     The barrier model's cap is the line through the boxes its way passes through, now with their runs, so a
     way round still never passes a wall for less than the line pays for it.
   - **Where it is approximate.** The run is a thin plate's mass law and coincidence carried to a block as
     thick as the run, which overstates a thick wall's high band; most runs reach the -100 dB floor in the mid
     and high bands anyway. The flanking through the junctions a sheet is built into is no path of its own
     (the panel's flanking share stands for it). At a free edge, a grazing line that moves from a broad face to
     the edge face jumps from the panel to the run; the way round that edge (Maekawa, a few dB) is louder than
     either. The real places' timber walls are solid 25 cm of wood in the data, so a line along one pays 1.7 m
     of wood (-52/-76/-100 where its panel is -24/-48/-76); a timber frame with a stud build would keep its
     panel (done since, below, "Timber walls are framed"). Steam Audio's correction is worked out on the routes'
     boxes, which are the scene's boxes as they stand.
   - **The stairwell** (still sources on Dock Street at z -128.5, the worker with Steam Audio, low/mid/high
     dB): x -19.5 to -15.5, -35/-52/-80 before, -57/-78/-100 now (the slab's run 0.8 m); either side as it was,
     -45/-69/-100 (over the guard and through the brick front); x -30 and -32 -57/-83/-100 and -50/-87/-100 to
     -81/-100/-100 and -74/-100/-100 (the lines there go into the same slab by its edge); x -14 -70/-100/-100
     to -91/-100/-100. `--pop-hunt` (40 cars, 30 still sources, 120 s, the same build with `panels` for the old
     charge, two runs): 10 and 7 mid-band excursions before, 0 and 0 after (7 to 0 against main's build). What is
     left is a dip,
     not a pop: for those 4 m of road the line through the slab is 11/9 dB under the line through the brick
     beside it. A real front radiates into the stairwell over its whole inner face, so the brick a few
     centimetres over the slab's edge would be heard there; a straight-line model has only the line, and the
     barrier model does not go round the slab's edge, which is buried in the brick (its legs would pay the
     brick twice). A way round an edge buried in another box, its legs paying that box once, closes it (done
     since, below, "When an edge is buried").
   - **The pavement** (`--pop-hunt ear=-6.4,1.77,-103.5`, 120 s, two runs): 2 mid and 4 high, then 3 and 2,
     before; 1 and 3, then 3 and 6, after. Each is a car on Wharf Avenue 229 to 327 m off: seen clear between
     two buildings (visibility 0.75 to 1), or the clear search's way over an edge (0 m against 6 m a moment
     later), some answered on another car's source. The same cars do it with either charge, and none of their
     lines goes into anything by its edge: which cars it catches moves from run to run.
   - **The three maps** (`--barrier-sweep map=X n=4000 panels`, the same pairs as above; louder or quieter by
     more than 0.5 dB, low/mid/high, as heard against the openings):
     | map | indoor pairs quieter | median of those | most | street heard inside (2,000) | median | most |
     |---|---|---|---|---|---|---|
     | city | 0.9/0.3/0.1 % | 10.7/17.9/18.7 dB | 34.8/27.9/19.5 | 1.6/0.4/0.3 % | 9.7/23.3/19.5 | 27.2/28.6/21.3 |
     | Magnolia | 0.8/0.0/0 % | 3.3/3.7/- | 28.8/3.7/- | 0.6/0.1/0.1 % | 7.7/0.8/7.1 | 27.0/3.9/7.1 |
     | Albany | 0.6/0.1/0 % | 4.6/3.7/- | 16.4/9.1/- | 2.1/0.5/0.1 % | 6.5/3.9/3.8 | 31.4/23.4/3.8 |

     The median and 90th percentile levels of all pairs did not move by more than 0.3 dB. Louder: none indoors;
     one street pair on the city by 1.8 dB in the low band (Brandt Court stairwell floor 4 from the street, -82
     to -80 dB by the openings): a leg's barrier search tries its most promising ways first, by what the line
     lets through, and with the slab's run paid a better way came within its budget. The biggest, each spelled
     out by `--path-probe explain` or the sweep's line list:
     - a line into a tower's floor slab by its edge, in every city building: Brandt Court flat 30B from Main
       Street, -35/-51/-79 to -57/-80/-100 (a 0.86 m run); Marlow Tower flat 24F from the street to
       the west, -73/-100/-100 to -100 (1.38 m); Union Building corridor floor 6 from Calder Avenue, -55/-95/-100
       to -78/-100/-100;
     - on the real places, a line along a timber or brick wall's length (in by its end, out of its face, 1 to
       2 m): 26706 Barnett Lane from outside, -47/-96/-100 to -64/-100/-100; 6229 Warwick Court's bathroom,
       -40/-89/-100 to -67/-100/-100; a line through a 6 cm patio slab or a 0.8 m road box lengthwise (24615
       Spencer Boulevard, -42/-52/-74 to -57/-52/-74; 1333 Morse Lane, the road 4.3 m, -34/-66/-100 to
       -53/-88/-100).
   - **The cost.** The worker's correction, once a tick for each source Steam Audio finds blocked, is one walk
     along the line's boxes: 4 to 16 µs on the three maps (the sweep's pairs; 8 to 18 % of lines have a run).
     The legs cost the same as before (city 1,816 and 1,801 µs, Magnolia 1,148 and 1,140, Albany 1,804 and
     1,796). A run's figure is worked out once per material, centimetre of run and decimetre of face, and kept.
   - **The guards.** RenderFingerprint, VoiceIdBand, ModelKindTable, PhysicsBoundary, MovementReplay and
     LibraryBoundary unchanged. Two emitter streams regenerated, both the hand-rolled tracer's five rays round a
     source (the replay has no Steam Audio): traffic_rain's car 305 (its engine and intake, 305 and -800305)
     passing the end of the porch's brick wall, 8 frames, at most 1.1 dB quieter and occlusion 0.003 more where one ray runs along the wall's end;
     places_and_woods' air conditioner (703) at the room's north-east corner, up to 11.8 dB quieter in frames
     54-60 where its line runs along the north wall's end piece, and the tree, the fountain and their parts
     (701, 702, -5008413 to -5008427) by at most 0.004 occlusion. Tests: `EdgeOnCrossingTests`.
   - **Where to listen** (each `--walk-pair map=X ears=...`): Marlow Tower floor 1 stairwell `/tp -10.8 -104.2
     3.28` and the pop hunt's ear `/tp -11.0 -100.8 3.3` (cars on Dock Street, south: no jump as they pass the
     stair's head, and a dip in the low band for those few metres); city: Brandt Court flat 30B `/tp -27.6
     161.6 9.4` (Main Street, south-east), Marlow Tower corridor floor 4 `/tp -20.5 -50.0 12.4` (the street to
     the west); Magnolia: 26706 Barnett Lane `/tp -357.1 1336.6 -4.2`, 24615 Spencer Boulevard `/tp -271.6
     -1085.4 2.0`; Albany: 6229 Warwick Court Southwest's bathroom `/tp -142.1 -35.1 0.0`, 1333 Morse Lane
     Southwest `/tp -377.0 204.6 -1.1` (what is outside, quieter through the wall along its length).
   - **References.** Cremer, Heckl and Petersson, Structure-Borne Sound, 3rd ed., Springer 2005,
     https://doi.org/10.1007/b137728 (wave types; radiation is by bending). Hopkins, Sound Insulation,
     Butterworth-Heinemann 2007, https://doi.org/10.4324/9780080550473 (flanking, EN 12354's paths and K_ij).
     EN 12354-1 Annex E, K_ij for rigid junctions; the heavy-junction catalogue that tests it:
     https://upcommons.upc.edu/entities/publication/1935d59a-5a5e-4350-bcc5-aa8a2102ad01/full. The panel model's
     own sources are in `WallTransmission` (Sharp 1978, EN 12354-1 Annex C).

   **When an edge is buried (2026-10-11, unheard).** The barrier model's ways round one thing and through
   another (above) skipped any edge within the 5 cm joint of another box: a guard's foot in its floor was no
   edge to go round. Two finds argued with that rule, from opposite sides: a finish laid on a slab buried every
   edge of the floor, so the way went under the slab into the earth, which is no box (1303 Tamarack Court
   Southwest from the street below, -29/-58/-94 dB where the line gives -100); and a slab's edge buried in the
   brick front built on it could not be gone round, so a straight line that went into the slab's 15 cm edge was
   11/9 dB under the brick a few centimetres higher (Marlow Tower's floor 1 stairwell over Dock Street). Three
   rules now, in `OpeningRoutes` (`Buried`, `EdgeBurial`, `WaysRound`, `LegsThrough`, `Passage`), one each for
   every asker of the barrier search: the worker's simulated path, every leg of the routes by the openings,
   one-offs, sirens and trains. The hand-rolled tracer has no barrier search and is unchanged.
   - **A layer that ends with the edge does not bury it.** A sheet in contact with the box, facing the same
     way (`LayeredFaces.InContact`, the rule the Steam Audio scene groups constructions by), whose own edge
     runs through the same point (within the joint of two of its sides), is a layer of the same construction
     ending there: the point is on the construction's edge, in the air. A 2 cm finish cut to its slab's
     outline buried the slab's top edges and the slab buried every edge of the finish (2 cm is inside the
     joint); now the finish's top edge is the floor's edge and is gone round. A layer that runs on past the
     edge (a carpet over a slab's end) still buries it. The basis: what an edge does to sound depends on the
     wedge it makes with the air, its exterior angle (Svensson, Fred and Vanderkooy 1999), not on how the
     solid behind it is layered; a construction is one panel to what crosses it (`Constructions`, EN 12354-1).
     Approximate: the edge is taken where the layer's own edge is, so the way round is short by nothing more
     than the joint; the legs still pay every box they touch, so a way round a slab's top edge, its finish
     touched at the bend, pays the finish (the finish's own top edge, the better way, is found too).
   - **The ground buries every edge at or under its surface.** The terrain is no box, so the searches saw
     nothing under a slab graded into a hill or a wall standing on the ground, and a way under its bottom
     edge went through the earth for nothing. Now every way round a box (`WaysRound`: the clear search, the
     ways round one thing and through another, and the bends of their legs) is dropped where it bends at or
     under the terrain (`OpeningRoutes.Ground`, the heightfields of the scene: from the world's tiles of ground
     for the box list, `WorldSnapshot.TerrainSolids`, and from the triangle store's pieces for the tile build).
     The basis: the ground's impedance is so far above air's that outdoor propagation treats it as a boundary
     that reflects and absorbs, never one sound crosses (Attenborough, Li and Horoshenkov); ISO 9613-2 screens
     only over and round obstacles; Steam Audio's terrain is a 20 m deep panel no sound goes through
     (`Heightfield.Depth`). Approximate: a leg between two bends in the air that dips under a hill between them
     is still not stopped (legs pay boxes, and the terrain is none): the way over the brow is the over-the-top
     string's, which also reads boxes only (done since, below, "The ground in the way").
   - **Round an edge buried in another box, that box paid once.** After the ways round edges in the air, a
     second search, of its own so it only adds: each box's most promising way that comes before any way of it in
     the air, its edge buried in other boxes. Its legs pay each of those boxes once, by the leg that runs
     furthest inside it (never bent round: it is what the way goes through), and one no leg touches is paid as
     its panel (the edge is within the joint of it). The basis: what a front lets through radiates from its
     whole face, and where that face ends at the slab built into it, the field behind the edge is the field
     through the panel diffracted at the panel's edge, as through an aperture (Kirchhoff, Born and Wolf §8.3, the
     rule the openings already use); a ray through a sequence of edges and walls keeps the product of what
     each does (Funkhouser et al. 2004). So the slab's edge is gone round through the brick: Maekawa's grazing
     loss times the brick, never more than the brick alone lets through (the cap, as before).
   - **Which face the sound passes.** Sound goes from the air into a solid, or out of it, only by a face open to
     the air (`Passage`). Where the bend is at a corner of the box it is buried in, a face against the box gone
     round passes nothing but what that box lets through (a slab's underside on the brick front it bears on is a
     junction, EN 12354-1's K_ij, Hopkins): the face is judged a few centimetres along from the corner. By a
     broad face the box is crossed as a panel (the brick over the slab's edge, from the street); by a narrow
     one the leg has gone into it by its edge and pays the run (the slab's 15 cm edge, round the top of the brick
     under it: "A solid gone into by its edge"); by none, only through the box gone round, there is no way.
     Without this rule a way round the top of a storey's brick front went into the slab on it by the slab's
     underside, which lies on the brick, and came out 40 dB louder than the line (Kestrel House flat 50B from
     the street, -76/-100/-100 to -40/-56/-85; now -76/-100/-100).
   - **Where it is approximate.** The buried way's bend is a ray's, so at the shadow boundary it takes
     Maekawa's grazing 5 dB where the field through an aperture is 6 dB down and joins the lit side smoothly;
     the line just over the edge pays the brick with no loss, so a 5 dB step is left where the dip was 11/9 dB.
     One buried way a box and four in all; their legs go straight (bent round what stands by the bend, as the
     ways in the air are, they cost three times as much and gained 6 pairs in 4,000 on the city up to 10 dB).
     Freeing an edge with rule 1 can move which way of a box is tried within the search's budget: 3 street pairs
     in 2,000 on the city came out quieter, stairwells heard from the street at -81 to -93 dB in the low band
     now -96 to -100 (flights of stacked steps, now one construction, offered a worse way of the same box first).
   - **The examples** (the worker with Steam Audio for the stairwell, the routes' legs against the openings for
     the rest, low/mid/high dB, main against this build):
     - Marlow Tower floor 1 stairwell, still sources on Dock Street at z -128.5, x -19.5 to -15.5:
       -57/-78/-100 to -45/-67/-93 (round the slab's edge through the brick front, -40/-62/-87, Maekawa's
       grazing loss); either side as it was, -45/-69/-100 to -46/-71/-100 over the guard; x -14 -91/-100/-100
       to -83/-100/-100. The stretch is now within 0.4/2.3/6.9 dB of the guard's beside it.
     - 1303 Tamarack Court Southwest from the street below: -29/-58/-94 to -100/-100/-100 (the way went under a
       neighbour's slab, 24644, into the earth; its edges are the ground's now, and the deck on it ends with
       it); 1974 Antelope Court Southwest -29/-56/-89 to -59/-87/-100.
     - `--pop-hunt` (40 cars, 30 still sources, 60 s): the stairwell 0 excursions on both builds; the pavement
       (`ear=-6.4,1.77,-103.5`) 0 on both.
   - **The three maps** (`--barrier-sweep map=X n=4000 seed=1` and `outdoor n=2000`, main's build and this one,
     the same pairs set side by side from `dump=`; louder or quieter by more than 0.5 dB, low/mid/high):
     | map | between places, louder | median, most (dB) | quieter | street heard inside, louder | median, most | quieter |
     |---|---|---|---|---|---|---|
     | city | 8.1/3.0/0.8 % | 12.9/20.9/5.4, 55.3/33.3/25.3 | none | 19.2/8.1/2.4 % | 21.2/26.0/4.6, 55.3/33.4/12.3 | 0.15/0/0 % |
     | Magnolia | 6.5/0.4/0.1 % | 10.9/6.7/8.2, 45.7/42.8/19.3 | 0.25/0/0 % | 6.9/2.9/2.2 % | 11.5/20.3/7.5, 60.8/46.6/19.3 | 1.2/1.1/1.0 % |
     | Albany | 6.0/0.3/0.0 % | 11.8/9.8/15.1, 47.5/32.2/15.1 | 0.3/0/0 % | 9.3/2.8/1.4 % | 11.2/18.4/11.7, 62.3/46.5/21.4 | 0.6/0.3/0.3 % |

     The 90th percentile levels did not move; the median of the street heard inside rose in the low band on the
     city (-77.6 to -66.2 dB) and Albany (-53.1 to -51.4), where most lines stay at -100. The biggest:
     - louder, a tower flat or stairwell and the street, the line into a floor slab by its edge, now heard
       through the front just over that edge (every city building): Brandt Court flat 33F from Main Street,
       -100/-100/-100 to -46/-71/-100; Selby House flat 31F from Main Street, -100 to -48/-76/-100; Marlow
       Tower stairwell floor 6 from the Dock Street sidewalk, -100 to -45/-67/-92. These undo the edge-on
       rule's -100 where a real front would be heard, at the brick's own figure less Maekawa's grazing loss;
     - louder, two walls meeting at a corner on the real places, the line along one wall's length into the
       other, now through the other beside the corner: 26522 South Barnett Lane, room to room, -87/-100/-100 to
       -42/-61/-92; houses on Magnolia across their gardens, -100 to -65 in the low band;
     - quieter, a way under a slab, a barn or a shed into the earth: 17902 Country Fields from outside past
       the barn to the west, -29/-55/-89 to -90/-100/-100 (by the openings); 17403 Pine Lane -43/-65/-98 to
       -91/-100/-100; 4684 Willetta Street Southwest from the house beside, -59/-100/-100 to -100; and
       Tamarack Court above.
   - **The cost.** A blocked straight leg, the same pairs both ways: city 1,913 to 2,193 µs, Magnolia 1,263
     to 1,431, Albany 1,925 to 2,277 (+15, +13 and +18 %): the second search's legs, and the ground looked up
     at every bend. The worker in the stairwell (`--pop-hunt`): 3,609 and 3,446 µs a route query, the pavement
     76 and 68 (the same within the noise).
   - **The guards.** RenderFingerprint, EmitterStreamReplay (all eight, at the default and at four
     processors), VoiceIdBand, PhysicsBoundary, the physics replays and LibraryBoundary unchanged: the replay
     runs without Steam Audio or terrain, and none of its scenarios has a route leg these rules move. The
     terrain reaches the box list's routes as a value (`OpeningGraph.Build`'s `terrains`): the worker passes the
     tiles it already read, and `SpatialAcoustics` reads the map once where it read it three times, so no
     library file gained a host reference (its allowance for `WorldSnapshot` lowered, 31 to 29). Tests: `OpeningRoutesBarrierTests` (a finish cut to its slab, a slab
     built into a front, round a wall's top into the slab it bears, a wall on the ground from the box list
     and the triangle store, the rules only adding where there is no ground or finish).
   - **Where to listen** (each `--walk-pair map=X ears=...`, z the floor): city: Marlow Tower floor 1
     stairwell, the pop hunt's ear `/tp -11.0 -100.8 3.3` (cars on Dock Street, south: no dip in the low end
     as they pass the stair's head); Brandt Court flat 33F `/tp -10.6 203.5 9.4` and Selby House flat 31F
     `/tp 12.9 183.9 9.4` (Main Street, south: through the front now, not silent); Albany: 1303 Tamarack Court
     Southwest `/tp -385.4 276.6 -1.1` and 1974 Antelope Court Southwest `/tp -848.7 790.5 -3.9` (the street
     below, quieter); Magnolia: 26522 South Barnett Lane `/tp -340.3 1060.0 -2.5` (the room to the north,
     across the corner, louder) and 17902 Country Fields `/tp -819.5 984.7 0.6` (outside to the west past the
     barn, quieter).
   - **References.** Svensson, Fred and Vanderkooy, "An analytic secondary source model of edge diffraction
     impulse responses", JASA 106 (1999) 2331-2344, https://doi.org/10.1121/1.428071 (an edge's diffraction
     from its wedge). Attenborough, Li and Horoshenkov, Predicting Outdoor Sound, Taylor and Francis 2007,
     https://www.taylorfrancis.com/books/9780419235101 (the ground as an impedance boundary). Born and Wolf,
     Principles of Optics, 7th ed., Cambridge 1999, https://doi.org/10.1017/CBO9781139644181 (Kirchhoff's
     aperture). Funkhouser et al. 2004, Maekawa 1968, Kurze and Anderson 1971, ISO 9613-2 and Hopkins 2007 as
     cited above.

   **The ground in the way (2026-10-11, unheard).** The rules above keep a way round from bending in the earth,
   but every straight segment still went through hills for nothing: the terrain is no box, and the segments
   paid boxes only.
   - **How a segment was charged.** Every asker has its straight segments charged in `OpeningRoutes`:
     - the straight line, by the worker (Steam Audio's direct stage, then `BarrierGains` for what bends round);
     - every leg of the routes by the openings (`Evaluate`, `Leg`, `LegGains`), which is also what the routes give
       one-offs, sirens and trains;
     - the legs of the ways round one box and through the rest (`LegsThrough`, `PaidLeg`) and of the clear ways
       round (`ClearedRoute`, `ClearedLeg`);
     - the string over the top (`OverTheTop`), whose legs are tested against boxes (`FirstHit`).
     Of these only the bends read the ground (`WaysRound`, "When an edge is buried"): a leg, the line or the
     string through a hill paid nothing for it. Steam Audio has the ground as triangles of the surface
     (`SceneTerrain`), each with its land cover's figure, and the porous covers' figures (`Grass` 0.4/0.6/0.8,
     `Dirt` 0.3/0.4/0.5) do not depend on thickness: through a grassy hill 4 m high, its ends 100 m apart, it
     gives -8/-4/-2 dB, bare earth -10/-8/-6, asphalt -66/-90/-115 (`OpeningRoutesTerrainTests`), the same for
     any hill. (Above, the ground was taken as a 20 m deep panel no sound goes through; measured, it is not.)
     The hand-rolled tracer (`SpatialService`, which the one-shot path uses: footsteps, birds, speech,
     beacons, and every source when Steam Audio cannot answer) does not see the terrain at all.
   - **The model.** The ground passes nothing: its impedance is so far above air's that outdoor propagation
     treats it as a boundary (Attenborough, Li and Horoshenkov). A segment through the earth is blocked, and
     what gets past is what goes over the ground's profile in the vertical plane through the two ends: ISO
     9613-2 (7.4) treats what stands in that plane by the path difference over its top edges, two or more by
     the path over the outermost; CNOSSOS-EU takes the convex hull of the profile and the edges on it, the
     path over them the "rubber band" (RIVM 2019-0023, 5.12). That string is what `OverTheTop` pulls over
     boxes already, so the ground's profile joins it: every corner of the heightfield along the plane, a
     joint (5 cm) over the ground as a box's top is a joint over the box, holding up what stands on it (a
     house on the brow). Its path difference is diffracted by the barrier model's law (Maekawa, as Kurze and
     Anderson put it, `Diffraction.BandGains`) and competes with the ways round the boxes as before. The
     ground effect (its reflection) is already in each voice and is not taken again.
   - **For every asker, once.** `OpeningRoutes.ThroughTheGround` (the profile, `Ground.Profile`) blocks:
     - a leg of the routes (`LegGains`: through the earth it passes nothing, and what comes is `BarrierGains`);
     - a leg of a way round (`PaidLeg` passes nothing through the earth; `ClearedLeg` has no way there);
     - the line (`BarrierPathDifference` pulls the string whenever the line goes through the earth, with or
       without a box on it).
     The worker multiplies Steam Audio's transmission by `SimulatorCorrection`, as by the run rule before:
     nothing where the line goes through the earth. The hand-rolled tracer's line through the earth is
     replaced by the way over the ground's profile (`OverTheGround`, `SpatialAcoustics.CalculateMainPath`),
     heard from the top it bends over, as the worker's is.
   - **Nearly level is not level.** The string over the top took a box within 2.6 degrees of level as level,
     its top the middle's. The places' roads, drives and lawns are pitched along the ground, and a 16 m road
     segment 1.1 degrees up a slope has an end 0.15 m over its middle: the string went through that end and was
     given up, and with the ground in the way that was no way at all (-100). Its top is now its top face's
     plane where the string goes in and out (`Crossing`). The city's boxes are level and do not move.
   - **What is approximated.** The heightfield is taken as it is: 2 m posts on the places, 7.8 m in the far ring,
     each cell flat in two triangles, so its corners along a line are exact for it and the profile has no
     sample spacing of its own; the survey under it is 3DEP's, resampled. Every corner is a knife edge; a
     real brow is rounded, which costs more than a knife edge (ITU-R P.526's rounded obstacle), so a broad
     hill is a little louder here than it is. Several ridges are one string (ISO's C3 term for double
     diffraction is not taken), the barrier model's 24 dB cap holds, and the ground is gone over, never round
     the end of a ridge. Within the joint is not in the way, so an end on the ground is not blocked by the
     ground it stands on. At the shadow boundary the law steps from nothing to Maekawa's 5 dB (the lit side is
     not modelled, as for boxes): CNOSSOS-EU tapers it to nothing at a path difference of -λ/20, and asks
     whether ground that small is flat (RIVM 2019-0023, 5.4, not agreed). A box on the ground over the line
     (a house on the rise) is gone over, not round: the ways round only go round boxes on the line.
   - **Steam Audio on the places.** Measuring this found that no tile scene was made on Magnolia or Albany: a
     solid of more than 64 corners read past the corners `SteamAudioScene.AddPieceMesh` keeps to share and
     threw, and every source fell back to the hand-rolled tracer, which hears no hill (fixed with this). The
     worker at seven spots behind a rise, main's build with that fix against this one (low/mid/high dB): Magnolia
     -10/-8/-6 to -6/-10/-18, -5/-7/-12 and -5/-6/-9; Albany -10/-8/-6 to -6/-9/-16, -6/-11/-18 (the creek's bank),
     -18/-24/-24 (over a car park's pad), and -17/-13/-10 (two rises) to -5/-7/-12. The one-shot path at the same
     spots was 0 to -4 dB, and is now the worker's.
   - **The sweep** (`--barrier-sweep terrain`, against the ground as no obstacle and level boxes; the routes' leg
     model, Steam Audio not in it; quieter or louder by more than 0.5 dB, low/mid/high):
     | map, pairs | quieter | median, most (dB) | louder |
     |---|---|---|---|
     | Magnolia, both out of doors (2,000, `ground`) | 5.5/6.5/6.5 % | 5.2/5.9/9.4, 18/24/24 | 0.2/0.1/0.1 % |
     | Albany, both out of doors | 4.7/5.0/5.2 % | 5.2/7.1/11.3, 18/24/24 | 0.3/0.3/0.2 % |
     | Magnolia, between places (2,000) | 0.3/0.1/0 % | 34/4/-, 52/4/- | none |
     | Albany, between places | 0.05/0/0 % | 2/-/- | none |
     | Magnolia, the street heard inside (1,500, `outdoor`) | 1.7/1.4/1.2 % | 22/16/17, 77/52/25 | none |
     | Albany, the street heard inside | 4.1/2.7/2.3 % | 28/31/19, 77/53/31 | none |
     | city (2,000 between places, 1,000 the street heard inside) | none | | none |

     Of the lines through the earth out of doors (196 on Magnolia, 189 on Albany), 94 and 64 go over the
     ground's own corners, the rest over a road, a lawn or a pad laid on it; none is left with no way. Most are
     close to grazing: a line 0.1 to 0.6 m into a rise, -5/-6/-9 dB. The most, -18/-24/-24, over a car park's
     pad graded into the slope. Quieter between places and from the street: a line through a wall and then the
     earth (an ear indoors, a source beyond a rise), -23/-48/-75 to -100, where the openings now carry it. The
     louder are lines through a porous lawn and the earth (-8/-4/-2, now -5/-5/-6 over the lawn) and pitched
     slabs the string can go over now (-100 to -10/-17/-24). The city's dumps are main's to the thousandth of a dB.
   - **A drive past a rise** (`drive=` and `ear=`, a source 0.5 m over the ground every half metre): Albany, ear
     (136.94, -4.94, 1287.72), x 170 to 235: no step before; after, 8 steps over 3 dB in 247 positions, 58 of them
     through the earth. Six are the shadow boundary, 5 to 6 dB; two are the source in a creek's tilted water
     box, which the string takes as high as its highest corner (a source no car makes). Magnolia, ear
     (-1103.93, 0.71, -1032.08), x -1260 to -1195: 2 steps of 5 dB before (boxes), 5 after.
   - **The cost.** The straight leg on the same pairs: Magnolia 1,216 to 1,060 µs, Albany 2,233 to 2,104, between
     places 1,536 to 1,489 and 2,347 to 2,276, the city 2,115 to 2,068 (a leg through the earth stops early).
     The worker's correction, once a tick for each source Steam Audio finds blocked: 7 to 26 µs on the places
     (the run rule's was 4 to 16). A segment's profile walks only the 16 m blocks of cells whose highest post
     stands over it.
   - **The guards.** RenderFingerprint, the eight emitter streams (at the default and at four processors),
     VoiceIdBand, PhysicsBoundary, the physics replays, PhysicsCrossMachine and LibraryBoundary unchanged: the
     replay has no terrain. Tests: `OpeningRoutesTerrainTests` (a hill gone over its crest, two ridges by one
     string, a line over the ground and a source on it not blocked, a leg round a wall that dips under a berm,
     the box list and the triangle store alike, no terrain no change, and with Steam Audio opted in, what it
     gives through a hill and the correction).
   - **Where to listen** (`--walk-pair ears=`, z the floor; the source at the engine point given, about the
     distance and bearing named; before, each was 0 to -4 dB by the hand-rolled tracer the places fell back
     to, or -10/-8/-6 through the hill with Steam Audio):
     - Magnolia `/tp -1103.9 -1032.1 -0.8`, a rise to the north-west, source (-1227.3, 6.7, -938.5), 155 m:
       -6/-10/-18;
     - Magnolia `/tp -1325.6 829.0 1.1`, source (-1495.6, -1.6, 758.6), 184 m to the west: -5/-7/-12;
     - Albany `/tp -320.5 1283.6 -6.7`, source (-300.4, -7.9, 1115.6), 169 m to the south: -6/-9/-16;
     - Albany `/tp 136.9 1287.7 -6.8`, the creek's bank, source (202.9, -8.9, 1393.7), 125 m: -6/-11/-18;
     - Albany `/tp -623.8 1073.6 -4.4`, over a car park's pad, source (-512.8, -8.4, 1147.1), 133 m: -18/-24/-24;
     - Albany `/tp -813.3 671.2 -7.6`, two rises, source (-913.3, -6.2, 577.6), 137 m: -17/-13/-10 to -5/-7/-12.
   - **References.** ISO 9613-2:1996 (withdrawn), https://www.iso.org/standard/20649.html, and ISO 9613-2:2024,
     https://www.iso.org/standard/74047.html (screening, 7.4 in the 1996 text: the path difference z over the top edges, e between
     the outermost two, C3 for two). Commission Directive (EU) 2015/996 (CNOSSOS-EU), https://eur-lex.europa.eu/eli/dir/2015/996/oj;
     Kok and van Beek, Amendments for CNOSSOS-EU, RIVM Letter report 2019-0023,
     https://www.rivm.nl/bibliotheek/rapporten/2019-0023.pdf (5.12, the convex hull of the edges and the path over
     them; 5.4, the attenuation zero at -λ/20 and the flatness criterion). Deygout, "Multiple knife-edge
     diffraction of microwaves", IEEE Trans. Antennas Propag. 14 (1966) 480-489,
     https://doi.org/10.1109/TAP.1966.1138719, and Epstein and Peterson, Proc. IRE 41 (1953) 595-611 (several knife
     edges taken one at a time, the alternatives to one string). Salomons, Van Maercke, Defrance and de Roo, "The
     Harmonoise sound propagation model", Acta Acustica united with Acustica 97 (2011) 62-74,
     https://www.ingentaconnect.com/contentone/dav/aaua/2011/00000097/00000001/art00008 (a terrain profile by
     segments). ITU-R P.526, https://www.itu.int/rec/R-REC-P.526 (a rounded obstacle). Maekawa 1968, Kurze and
     Anderson 1971 and Attenborough, Li and Horoshenkov as cited above.
   **Timber walls are framed (2026-10-11, unheard).** The real places' outside walls were 25 cm of solid wood or
   brick, lined with 3 cm of solid plaster in a house built room by room, and a shed was a block of wood; a line
   along one paid 1.7 m of wood. The houses of Magnolia and Albany are timber-framed, so every wall is now built as
   one: one entity a wall piece, as before, its prefab naming an assembly (`Assemblies`, in the acoustics), its
   material the face outside.
   - **The assemblies.** Lap siding (10 mm of wood averaged over the wall, lapped, so its weight on the sheathing
     and not its stiffness: `Ply.Lapped`), 11.1 mm sheathing (plywood's figures for OSB), a 2x4's 88.9 mm cavity of
     glass-fibre batts between studs at 406 mm, 12.7 mm plasterboard: 120.7 mm (`framed_siding_wall`). Brick veneer
     92.1 mm, a 25.4 mm air space only ties cross (no bridge but its edges), the same sheathing, studs and board:
     230.2 mm (`brick_veneer_wall`). The same without the board where the board is its own sheet against it, a house
     built room by room (`..._unlined` and `gypsum_board`, the rooms' plaster face). A partition is two 12.7 mm
     boards on 2x4 studs at 406 mm, 114.3 mm, the existing two-leaf build (`stud_partition`). A shed is two 15.9 mm
     plywood walls and its air (`frame_shed`). New material `GlassWool`.
   - **The model.** A layer that lets air through and is softer than the air in it (glass fibre, the air space,
     an open-cell foam: `WallTransmission.IsCavity`, E under rho c^2, 142 kPa) with airtight layers on both sides is
     a cavity: the layers before it one leaf, those after it the next, bridged by the studs it is built between
     (Sharp's double-leaf law, as the two-leaf build); soil, snow and carpet are stiffer than air and bond as before.
     An assembly is its plies laid out so, in the order a line crosses them (`Constructions.Through` and
     `LayeredFaces` read which way it faces), and a line along one runs in its cavity and keeps its panel. A wall
     whose faces differ is turned as its frame is, never folded into a half turn (gen_osm `FACED`, the port's
     `NormFull`). The build reaches the wire as `AcousticComponent.Assembly` and `WallBuild.Assembly`.
   - **What it gives** (`TimberFrameWallTests`, 4 by 2.75 m, low/mid/high): siding 17.4/39.0/46.0 dB, the brick
     veneer 42.4/68.3/100.6, the 25 cm of timber it replaces 23.1/47.5/75.3. The siding wall is the mass law of its
     24 kg/m2 at 125 Hz (21.7 dB), rises with the studs' bridge to 43 dB at 1 kHz, and dips at 2 to 3.15 kHz with
     the boards' coincidence, as Bradley and Birta measured such walls (the studs' dip at 125 Hz, the boards' at
     2.5 to 3.1 kHz). Bonded as one plate, siding and sheathing put the dip at 800 Hz to 1 kHz, hence `Lapped`.
   - **The maps.** Regenerated; every id the same (a wall's pieces are decided as far in as the plan allows,
     WALL_T, and cut to how far the wall reaches). Doors stand in the middle of their wall. A doorway's head laps
     the wall either side by a jack stud's 38 mm (`BEARING`): a hairline between abutting pieces let a flood out of
     a Magnolia house. An outer wall runs on WALL_T past an inside corner (`CORNER_REACH`), and the flood grows a
     thin solid at an angle to its grid along its other sides as much as through its thin one (`RoomFlood.Mark`):
     12 cm walls at 45 degrees left a flood a way out of three Albany corners. A room of a building is asserted
     indoors (`IsIndoor`): the server's survey read two wing rooms as outdoors once the walls were as thin as
     they are. city.json is untouched (gen_city has no timber walls). `WorldStore.GeneratorVersion` 10.
   - **The guard.** `FramedWalls` (RealPlaceMapTests `Every_wall_is_framed`, and the Tomball tiles in
     `WorldBuildingsTests`): every upright sheet of wood, plasterboard or brick of a building is a framed assembly
     with its cladding facing out (off its floor slab), a board against an unlined one, a stud partition, or a
     shed's assembly; by material and build, never by name. On the maps before: 9,497 on Magnolia and 18,522 on
     Albany failed.
   - **Left (next).** The sound change is not measured yet: `--barrier-sweep server=DIR dump=FILE` old against new
     on both places and `--path-probe` for a house from the street and room to room. The rest is in todo.
   - **References.** Bradley and Birta, Canadian Acoustics 29(3) (2001) 64-65,
     https://jcaa.caa-aca.ca/index.php/jcaa/article/download/1385/1128/1518. Brick Industry Association TN 28 (2012),
     https://gobrick.com/media/file/28-brick-veneer-wood-stud-walls.pdf. WWPA Standard Patterns (bevel siding 5 1/2
     in. dressed, 4 1/2 in. exposed). Canadian Wood Council, OSB sizes,
     https://old.cwc.ca/wp-content/uploads/2019/03/Oriented-Strand-Board-OSB-Sizes.pdf. Owens Corning batt guide
     specification, https://www.owenscorning.com/dms/10024955. NIST PS 20 (2x4 dressed sizes), IRC R602.3(5), ASTM C1396.
   **Openings from the floods (step 3, 2026-10-11, approved by ear 2026-10-11).** A flood keeps the cells of each opening its air meets
   and the cells of open sky it meets (`Room.OpeningCells`, `Room.SkyCells`). `FloodOpenings` frames them: an
   opening's cells in its own plane, joined where they touch, a frame each piece (a pier or a post parts it), as wide
   and high as the cells reach and with the area they hold (less than the frame where something stands in it: a stair
   hole with its flight). An opening cell the air met counts only if what lies past it, the way the air came, is not
   solid: a shut shutter in a car park's bay is a wall, not an opening. Air meeting the sky with no opening there is framed the same way, by the face it meets it
   through and the plane it lies in. `RoomCells.Openings` carries them; `AcousticVolumeGenerator.ApplyFloodOpenings`
   puts them on the map (an authored opening takes its biggest piece as its frame, each other piece is an opening of
   its own between the same two places, each sky patch an opening to the outdoors) whenever the map is made with
   floods, and again when the client floods the published geometry. `FaceOpenings` now searches only the faces of
   places that did not flood (a yard, a street, a roof) and finds no gap into a flooded room; `OpeningRoutes` takes a
   framed opening as it is and searches a frame round it only for a door's leaf (and for an authored opening no flood
   met). No room's air, lookup or measure moved: the three maps' dumps are byte for byte main's.

   Routes (the client's openings with the floods, against main's):
   - Albany: 1,530 flooded rooms had openings to the outdoors found in their boxes' faces, 33,342 m2 in all, where
     their floods are closed: a box's face across a wing of its own house, or past walls the face finder cannot see.
     273 m2 remain (the 640 m building, whose box serves). Between rooms 45,937 to 39,748 m2: 21 rooms over 3 dB.
   - Magnolia: 481 rooms, 9,650 to 18 m2 to the outdoors (the barns' doorways, 5.8 to 5.9 m2 each; 19315 Turtle Dove
     Lane's had 29.6 with its false faces); between rooms 21,272 to 18,363 m2, 12 rooms over 3 dB.
   - City: the stair holes as the air meets them past the flight, 9.0 to 9.6 m2 (the authored frame was 12); the concourse's
     thirds 29.3 by 5.5 m; the car park's sides a frame a bay, level 0's west side now with them (+0.9 dB of opening);
     the tunnel's north quarter loses its second opening onto Main Street block 2 (130.8 to 63.0 m2 to the outdoors,
     -3.2 dB).

   Where to listen (`--walk-pair map=ID room=ID`, a spot the server's collision says a body stands on, z the floor):
   city Tunnel, north portal `/tp 0.0 -212.5 0.1`; Parking garage, level 0 `/tp -66.8 62.0 0.3`; Marlow Tower
   stairwell, floor 3 `/tp -14.2 -102.7 9.3`. Magnolia 17523 Country Mile, house `/tp -240.6 594.9 1.5` (102 m2 of
   false openings to the street gone); 19315 Turtle Dove Lane, barn `/tp 93.3 -260.1 1.2`; 16514 Walnut Springs Lane,
   house `/tp 1423.4 40.8 -2.4` (between its rooms 17.3 to 7.9 m2). Albany Linn-Benton Community College Cafeteria
   `/tp -27.5 -345.4 0.7` (741 m2 gone); 7101 Supra Drive Southwest, house `/tp -53.1 -690.8 0.8` (289 m2); 6121
   Warwick Place Southwest, main bedroom `/tp -161.1 89.8 0.2` (between rooms 9.6 to 1.7 m2).

   **The readers on the flood (step 4, 2026-10-11).** `RoomCells` gives each flooded room's air (`TryAir`: the bounds
   of its cells and its volume) and its floor (`FloorOf`: the top of the lowest cell in most of its columns, so not a
   flight down through it). Moved onto them:
   - `StandingSpots.InRoom` (and so `/tp` to a room, `--walk-pair room=`): over the room's air from its floor, a spot
     whose body the server finds in the room. Marlow Tower's roof access is now `/tp -10.6 -105.5 24.1` (it was
     -106.3 23.8, on the top of the flight); the other listed spots are as they were.
   - `/tp` by name: a room's size is its air, and its spot is `InRoom`'s.
   - `DoorManners`: which side of a door is further in, by the volume of each room's air.
   - The client's tracker: in a room where its flood holds you, as near as its air's nearest edge otherwise.
   - `ListeningSpots` holds a spot to the room the server's lookup puts it in, not the box.
   Already on the flood, with the box only where nothing flooded: `SpatialService.GetRegionAt`, `MapManager.RegionAt`
   and the place names, the voxel marks, `RoomAcoustics` (volume, faces, absorption, Sabine time), the routes' rooms,
   `FaceOpenings` (step 3). Left on the box, and why:
   - `MapTiles`: which tiles a room is sent with, and which rooms flood together across a tile's edge
     (`RoomFlood.Groups` from `Seed.Size`). The tiles are made at load, before the first flood lands.
   - `MapManager.SurveyRegions`: the indoor flag (it decides what floods, and is sent before any flood) and the
     materials of a room that does not flood, which a client hears until its own measure lands. A ray up from each
     region's point says the same as the survey for 711 of the city's 724 regions (`only=indoor`); the 13 are the
     platforms' canopies and Foundry Street under the terminal walkway (covered, but open sided, so outdoors) and 58
     Alder Street's walled garden (the survey's indoors, under the sky). The flag stays the survey's until the flood
     can decide it at load: covered, flooded and closed.
   - `OpeningRoutes.FaceOfRoom`: only for an authored opening no flood met.
   - `CompositeService`: a room built at run time is made from its survey.
   No lookup, measure or route moved (the three maps' dumps are main's and step 3's, byte for byte).

   **Nothing authored of a room (step 5, 2026-10-11).** A prefab declares a region by `IsIndoor` alone; `RoomSize`,
   `RoomMaterials` and `ReverbScale` are gone from `PrefabTemplate`, the schema, the validator and `acoustic_region`
   (a prefab naming one is refused as any unknown field is). A region's box is the one its collider fills, scaled as
   the entity is: the place itself where nothing floods, a room's point and fallback until its flood lands. Its
   materials start blank (the load's survey fills a room that does not flood, the flood's lining measures one that
   does) and its reverb scale is 1. The editor has no face materials to set (the six "Floor" to "West" settings and
   their bake into the map file are gone); its menus and build tell a region by `IsIndoor`. `RegionComponent`'s fields
   stay on the wire. gen_city.py, gen_osm.py and gen_speedway.py never wrote these: the four maps come out byte for
   byte as they were, and so do the dumps. Left: a map entity's `RoomMaterials` (the test map's gym, and what
   `WorldPlaces` and `WorldBuildings` carry into a tile from a survey or from their prefabs, so a tile needs no survey
   of its own) and a composite part's, which carry a measure rather than author one.

   `RealPlaceMapTests.The_ground_never_rises_through_what_lies_on_it` now checks nine points over each room's air, not
   its box: down from the top of the air to what is under it, where the room's air (`RoomCells.TryAirAt`) holds the
   point half a metre over it. Tried with `TryRoomAt` first, 21 rooms (14 on Albany, 7 on Magnolia) seemed to have the
   ground through their floors. They do not: each point was 0.1 to 0.2 m outside the footprint, in a cell of the
   room's wall (a wall at an angle to the grid is rasterised as thick as a flood needs, so it holds cells whose
   middles are just past its outer face, `RoomFlood.Room.EdgeRuns`), where there is no floor, only the yard's ground
   (AudioLab `--geometry-parity only=column at=X,Z to=X,Z` says what a ray down meets and how far the footprint is).
   `RoomCells` keeps the air and the wall cells apart now (`TryAirAt`; `TryRoomAt` still holds both, so a point on a
   wall's face is in the room). Over every room's air on both places: no point with the ground on top. No generator
   or grading rule needed changing; the world's buildings are graded by the same slabs (`TerrainBuilder.Slab`, the
   footprint's outline).

2. **Sound points on models (MATTER.md 8).** A prefab's `SoundPoints` (name, position on its shape, size,
   directivity) read by machine parts and emitters. Begun: struck sounds read the shape a part has where its box
   would mislead (`StruckThings.Describe(form:)`, from a bump and a knock): a pitched roof is struck as its deck, a
   plate the deck's thickness over the slope, and a flight as one step (a block of a tread's going and a riser's
   height), not as the box round them; round things, balls and swept profiles are what their boxes say already (a
   column is long and narrow: a bar). Unheard.
3. Facets as polygons for the engine echoes (their rectangles overreach a triangle's corners); per-facet materials
   in the image sources (a shape's slots).
4. Doors' remaining misses (16 on Albany): footprints whose rooms sit far from every wall, where the cover grid's
   angle (the footprint's least rectangle) is not its walls'; the flood fixes them. Not rechecked since step 3.
5. The openings found in rooms' faces (12.2's table): done by step 3 for every room that floods (2026-10-11): the
   face finder searches only places that did not flood, and a flooded room's openings are framed from its air.

## Appendix: box-geometry consumers today

From a survey of the code (2026-10-06). Line numbers drift; the names do not.

- **Movement:** `SharedMovementEngine.Step` (Common), from `MovementSystem.Update` per input on the
  server (memoised ground, `grid.CollectInRadius(pos, 5 m)`) and `ClientPhysicsSystem.Predict` on the
  client (radius 5 m). `MovementSystem.CheckCollision` for teleport, spawn and login.
- **Ground height**, `PhysicsUtils.GetGroundHeight`: MovementSystem (per input, memoised),
  DrivingSystem.Step (per tick per driven vehicle, one probe at the body centre), Program
  GetMaterialUnderPlayer (per broadcast per session), CombatService EmitStrike, BreakGlass, BodyFall,
  Bodies.GroundUnder, HandsService.Drop, OccupancyService.FindStandingRoom, PlayerStore.IsSafe,
  CommandHandler SpawnWalker, ClearGroundBeside, StandingSpot, CharacterSystem.Ground, Haunts,
  MapManager.VerifySpawnPoint, ClientPhysicsSystem.Predict, OtherBodies (per remote footfall),
  ClientAudioSystem.OnTheWheels. Related: ClientAudioSystem.ApplyGround (two downward RaycastAll),
  Sightline.IsGround, AdminGun.IsGroundLike, CommandHandler.IsGroundOrOverhead.
- **Bullets:** CombatService Fire, Shoot, Launch, FlyBullets, Segment, FirstHit (grid, RayIntersectsOBB,
  cylinder, sphere), TryRicochet and Face (normal from the largest local axis), FlyAhead and
  PassingSounds (pre-flown path), Assist and InPlainView (aim assist in 8 m pieces).
- **Scope:** ScopeView.InView (CastSight to each person or vehicle), Describe and Range (2 km
  CastSight), StandsOnRoof (a 1.5 m downward CastSight and "roof" in the name), every 0.1 s while
  raised.
- **Footsteps:** the ground probe's material, on the client per input and on the server per
  broadcast; remote bodies per footfall.
- **Beacons:** BeaconAids.Nearest (grid by radius), Reaches (SpatialAcoustics path, five rays,
  WallTransmission), InSight (RaycastMaterial), TryDoorFace and TryVehicleSide (box faces).
- **Walkers and Alex:** Pavements.StripsOf (slabs named sidewalk or pavement), SolidsOf, Clear,
  HauntFinder; traffic follows lane lines with no world collision; VehicleSystem.Parking (segment
  against boxes).
- **Server load:** MapManager.CreateMapInstance (minimum floor, apertures), SurveyRegions
  (BoxColumns, CompositeAcoustics.SurveyBox), ValidateTracks (TrackClearance), RefreshGrid;
  CompositeService.RefreshRoom and MakeDrivable.
- **Client load:** AcousticVolumeGenerator.GenerateRegions (0.5 m voxels), AddFaceOpenings (boxes
  only); the static grid rebuilt in full on every definition change (`ClientWorldState.RebuildGrid`).
- **Doors:** DoorSystem moves leaves (no Velocity, so the server grid keeps them at the load pose); a
  15 % aperture change re-sends the definition; the client rebuilds its grid; the acoustic worker
  rebuilds both Steam Audio scenes for a leaf within 50 m that moved, at most every 0.3 s.
- **Wire:** EntityDefinition (MemoryPack, positional): EntityId, Type, Collider, Identity, Acoustics,
  Physics, Material, SoundEmitter, Region, Portal, Transform, Moves, Team, RidingEntityId.
  `ColliderComponent { Shape, Size, IsSolid }`. `WireContract.Hash` covers every Common source file.
- **Shape-specific code:** CombatService FirstHit and Face; SpatialService GetOcclusionData, RaycastAll,
  RaycastMaterial (box and sphere only), CastSight, RaycastSingle; SightGrid;
  SharedMovementEngine.StandingRotation. Box-only filters: SteamAudioScene.BoxesFromWorld,
  AcousticVolumeGenerator, RainField, MapManager, Pavements, VehicleSystem.Parking, AdminGun.

## Decisions (Cody, 2026-10-06)

- Live editing while players are on the map: yes.
- Stairs: real treads for movement and sound, with automatic step-up; the controls stay as they are
  (W walks up or down). Footfalls land on real treads, so cadence, material and height follow from
  the geometry instead of a timed stair rhythm.
- Soil layers: real soil survey data for maps of real places; a simple profile per place for made-up
  maps.
- Everything else as recommended above: terrain 2 m near, 8 m in the coarse ring; 3DEP 10 m plus 1 m
  lidar where it exists; 0.5 m voxels; glTF/OBJ import as an offline tool; triangle budgets 150k per
  full tile, 20k per coarse tile, 50k per imported mesh; Steam Audio on Embree (x86/x64 clients);
  our own BVH and movement rather than Jolt; rooms authored as seeds.

## Stage 2 decisions (Cody, 2026-10-06)

"For the 5 decisions, go with your recommendations." All five of 10.10 stay as built: the cylinder's
foot under a rounded head; stairs at 0.45 of the walk up, 0.65 down, a 1 in 12 ramp at 0.86; two
treads a footfall on a flight at the game's speeds; a driven car pitches and rolls on its wheels, its
sound positions and passengers with it; the speedway's ground is dirt between its own surfaces.
