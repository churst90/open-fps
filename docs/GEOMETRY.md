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
  thickness is the box's smallest dimension; leaf and stud spacing come from the prefab.

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
| `WallTransmission` | panel = the box's smallest side | panel thickness = the chord between entry and exit of a closed mesh, or the surface's nominal thickness; leaf and studs from the surface's `Construction` |
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
