# World streaming

The plan for a world made of tiles: generated from open data the first time anyone goes near a
place, kept on the server, sent to each client a piece at a time, and later the same machinery for
maps players build. Written 2026-10-06 against main at cce0e7ca.

Four stages:

1. Streaming within one map. The server keeps the whole map; each client is sent only the tiles
   near it, and tiles are loaded and dropped as it moves. Built now (see "Stage 1 as built").
2. One world in real coordinates. Tiles keyed by where they are on the Earth, generated on demand by
   the server running the importer, stored, and streamed with stage 1's machinery.
3. Seamless travel across tiles, with other players, traffic and walkers in the same world.
4. Maps players build, stored and streamed as tiles the same way.

## What exists today

- A map is one JSON file. The server loads all of it into one Arch ECS world per map
  (`MapManager.CreateMapInstance`). Entity ids are Arch's runtime ids, allocated in file order at
  load, so they are stable for the life of the server process and nothing else. The ids written in
  the file (`EntityData.EntityId`) are only used to link portals to regions at load.
- Joining a map: the server sends `MapManifest`; the client clears its world and answers with
  `MapDataRequest`; the server sends every static entity (no `Velocity`, no `PlayerComponent`) as
  `EntityDefinitionBatch` messages of 256, then `MapLoadComplete`; the client builds its acoustic
  map (`AcousticVolumeGenerator.GenerateRegions`) on a niced thread, sends "ready", and the server
  spawns the body (`PlayerSpawned`). Magnolia is 32,598 definitions sent in one tick.
- After that, `GameServer.BroadcastWorldState` runs every tick for every session: anything with a
  collider or an item, within the map's earshot, that is dynamic, dirty or not yet known to the
  client, is sent (a definition the first time, then states). Dynamic entities that leave the area
  go out as `EntityRemoved`. Static geometry is never removed, because the client's acoustic map
  was built from the whole map.
- `session.KnownEntities` is the server's record of what each client has been sent a definition for.
- The client keeps every definition in `ClientWorldState`. Its static collision grid is rebuilt from
  all definitions when any changes. A definition that declares a region or a portal after the bake
  is tracked one at a time (`TrackRegion`, `TrackPortal`).
- The Steam Audio scene is built on the acoustic worker from every solid static box
  (`SteamAudioScene.BoxesFromWorld`) when the acoustic map object changes. A door that moves near
  the listener rebuilds the scene off the worker thread and swaps it in
  (`AsyncAcousticWorker.SwapInDoorBuild`, `TracedReverbSet.ConfigureInBackground`); the old scene is
  released a few seconds later. Building it on the worker used to freeze all sound for a second
  (memory: door rebuilds and jamb routes).
- Maps of real places (`tools/gen_osm.py`) tag every entity with `Tile` ("x,z", 250 m squares from
  the map's origin) and `Layer` (ground, roads, drives, paths, verges, yards, zones, structure,
  rooms, interiors, trees, props, rail, water). Roads carry `Tiles`, junctions `Tile`, the map
  `GeoOrigin` and `TileMetres`. Magnolia has 154 tiles (median 142 entities, most 1,660); Albany 124
  (median 69, most 3,635).
- Some entities are bigger than a tile: the ground is one 3 km slab, a named place along a road can
  be 1.2 km long, a college roof 70 m. 43 of Magnolia's 1,633 portal links and 38 of Albany's 2,565
  join a door to a room whose centre is in another tile.
- Roads (`RoadNetwork`) are server only. The client finds roads from geometry.
- Messages and components serialise positionally (MemoryPack). New fields are appended, new message
  types take new union numbers (32-34 are reserved by other work; streaming takes 38), and
  `WireContract.Hash` makes a client and server from different builds refuse each other.

## Tiles and coordinates

### Stage 1

A tile is a square of `MapData.TileMetres` (250 m on the real places) counted from the map's origin:
tile (x, z) covers x·T ≤ X < (x+1)·T and z·T ≤ Z < (z+1)·T, with X east and Z north as everywhere in
the engine. This is what gen_osm already writes, so no map needs regenerating. A map with
`TileMetres` 0 (the city, the speedway, every hand-built map) is not streamed: it is one tile, sent
whole as today.

Tile membership is worked out by the server from geometry at load, not read from the `Tile` tag:
an entity belongs to every tile its footprint (collider or room box, turned by its rotation) overlaps.
The ground then belongs to every tile and is always sent; a long named place goes with every tile it
crosses; a door on a tile edge goes with both. Entities that did not come from the map file (the
foundation the loader adds, the map's zone entity, anything spawned later) are global or placed by
where they stand.

### Stage 2: one world

Tiles need a key that means the same place for every map and every server, and metres that do not
stretch. Options:

| Grid | Fixed size in metres | Seams | Verdict |
|---|---|---|---|
| Web Mercator (slippy map) | No: a z16 tile is 611 m at the equator and 529 m at 30°N | none | not for physics |
| S2 / H3 cells | roughly; cells are not squares | none | boxes and roads do not fit hexagons or curved cells |
| Local tangent plane per place (today) | yes near the origin | between every pair of places | fine for one town, not for joining towns |
| UTM zone grid | yes (scale error under 0.04 % inside a zone) | every 6° of longitude | recommended |

**Decided (Cody, 2026-10-06): the standard UTM grid, 250 m tiles.** A world tile key is (zone,
hemisphere, floor(E/250), floor(N/250)), written `15N/1023/13345`. Generation projects every feature into the tile's zone. A
zone edge is crossed rarely (Texas has three); a player keeps their zone's frame across it and the
server reprojects the neighbour zone's tiles into that frame when it sends them (a rotation of up to
about 1.5° at 30°N and a shift; boxes stay boxes).

Precision. Positions are float32 metres on the wire and in physics. Millimetres hold to about 16 km
from the origin (docs/ROADS_BEACONS_AND_SCALE.md). UTM eastings and northings are millions of metres,
so the server keeps world positions in doubles and simulates each group of nearby players in a
**frame**: a local origin at a tile corner near them. A client is told its frame's origin with the
manifest and moves to a new frame (a rebase) when it is more than 8 km from the origin: every position
it holds is shifted by a whole number of tiles, the acoustic map and the Steam Audio scene are
translated (rebuilt in the background), and voices are moved by the same offset in one step. Rebasing
happens only between frames, so a car's Doppler never sees it.

What stage 1 already does, and what stage 2 changes, to make the move easy:

- Tile keys are already (x, z) integers of a fixed size from an origin, and a map's tile size is
  data (`TileMetres`). Stage 2 adds the zone and hemisphere to the key and takes the origin from the
  grid instead of the place.
- A real place's origin today is its spawn address, so its tiles do not line up with the UTM grid.
  Stage 2 generates places with the origin on a UTM 250 m grid corner and x and z along UTM east and
  north (not true north): then a place's tile (x, z) is world tile (zone, origin E/250 + x, origin
  N/250 + z), and the maps made now can be regenerated into the world without moving anything by hand.
  `GeoOrigin` stays on the map, as the origin's latitude and longitude.
- Nothing on the wire names a tile by its world key yet. `TileStreamUpdate` carries (x, z) of the
  map's own grid; stage 2 appends the zone and the frame's origin (two doubles) to the manifest and
  keeps (x, z) relative to the frame.

Elevation: the ground is flat today and stays flat in stage 2. Each place keeps its 3DEP heights for
when terrain exists.

## Detail layers

Three levels per tile, per client:

| Level | What is sent |
|---|---|
| Full | everything |
| Coarse | what sound notices from past the full radius (below) |
| None | nothing |

The coarse layer is decided by acoustics (Cody, 2026-10-06: keep whatever sound would notice at that
distance, leave out only what it cannot):

- **In:** the ground, roads, building shells (outer walls, floors, roofs, ceilings) with their front
  doors, rail, water, the woods (canopy volumes that scatter and absorb) and tree trunks, and any
  other solid thing that stands at least 0.8 m tall and runs at least 2 m: fences, hedges, garden
  walls, guard rails. Each is a barrier to a source near the ground, a surface that reflects, or (the
  woods) the scattering that makes a far road behind trees sound far. A static parked vehicle would
  qualify the same way; the real places have none (their vehicles move and are sent as moving things).
- **Out:** rooms and named places, the inside of houses (inner walls, inner doors, furniture), lawns,
  drives, paths and verges (flat slabs on ground that already reflects), posts and other small
  props, and any sound source that carries less than the smallest full radius (100 m).
- **Tree crowns are in** (2026-10-06, distant woods). One tree's wind carries about 90 m, but a wood of
  N trees is the same sound N times over, 10 log N dB louder, and carries much further. Past 110 m the
  client hears each wood's trees (by 200 m square and species) as one source over the wood's extent
  (`WoodChorus`, `FoliageSynth.Trees`), handing each tree to its own voice between 110 and 70 m with
  its power split between the two, so nothing steps; a tree the voice budget leaves out stays in its
  wood. Measured against the trees summed one by one (AudioLab `--distant-woods`): within 0.4 dB in
  level and 0.5 dB in every octave band from 125 Hz to 8 kHz, at 300, 500 and 800 m.
- **Front doors** are in, as shut leaves in their walls. A doorway and the rooms it joins share their
  tiles as one group (a house), so a door in a full tile always comes with its rooms; a door in a coarse
  tile has no room behind it, and the client treats it as a shut leaf, not as a doorway into nothing.

Cost (medium, at the spawn): Magnolia's join went from 12,344 to 13,590 entities and 530 to 588 KB;
Albany's from 17,140 to 18,024 entities and 740 to 776 KB. About 34 entities and 1.5 KB packed more
per coarse tile on Magnolia (woods and trunks), about 24 on Albany (fences, hedges, woods).

Which level a tile gets depends on its distance from the player (nearest point of the tile):

- Full within the full-detail radius, coarse within the far radius, nothing beyond.
- Hysteresis: a tile is upgraded at the radius and kept until the player is 50 m past it, so walking
  along a tile edge does not load and drop the same tile.
- The client's world detail setting chooses the radii: low 150 m full, 500 m far; medium (default)
  300 m and 800 m; high 500 m and 1,200 m. The server clamps what it is asked for (full 100 to 600 m,
  far up to 1,500 m).

In stage 1 the coarse layer is a subset of the map, chosen by the `Layer` tag. In stage 2 the
generator writes each tile at two levels (coarse first, because it is cheaper and needed first).

## Server: interest management

Per session the server holds the level of every tile that client has (`TileInterest`), and the queue
of definitions it still owes. Each tick, for each player on a streamed map:

1. If they have moved 8 m since the last look, or changed their detail setting, work out the levels
   they should have.
2. Tiles that went down: every entity in them that is no longer wanted by any of the client's tiles
   goes out in one `EntityRemoved`, and is struck off `KnownEntities` and `SentStates`.
3. Tiles that went up: their entities are queued, nearest tile first.
4. The queue is drained a tick at a time, at most 768 definitions a tick per session, in batches of
   256. When a tile's last definition has gone, a `TileStreamUpdate` says so.

Each definition is checked again when it is drained (still wanted, not already known, still alive), so
a tile dropped while it was still being sent costs nothing more.

The broadcast changes in one place: on a streamed map, static geometry from the map is the tile
streamer's to send, never the broadcast's, and anything else (a moving thing, a dropped item, a
player, something built at run time) is sent only while it stands in a tile the client has at any
level. A car that drives out of the client's tiles is removed as anything that leaves the area is now.

Simulation. In stage 1 the server still runs everything on the whole map (the 4 cars and the walkers
on Magnolia cost what they cost today). In stage 3 the server simulates people and traffic only in
tiles some player has (a sim radius at least the far radius), freezes or retires what leaves, and
spawns what the roads' traffic volumes say should be there as a player's tiles come into range.

Cost. The per-session check is one dictionary lookup for each unknown static candidate in the
broadcast and one tile computation for each dynamic candidate. Re-evaluating tile levels is a few
dozen tiles every 8 m. The definitions themselves are what they cost today, spread over ticks.

## Entity ids

Stage 1: the server keeps the whole map, so an entity's id is the same every time its tile is sent
again during the server's life. Nothing else is needed.

Stage 2: the server loads and unloads tiles of the world, and Arch hands out ids as it creates
entities, so a tile reloaded after an unload gets new ids. That is safe as long as a tile is only
unloaded on the server when no client holds it (the server keeps a reference count per tile, from
the sessions' interests). Ids on the wire stay what they are (int, the ECS id).

Clients can cache tiles on disk to skip the download: a tile file has a version (the generator
version and the content hash), and each stored entity has an ordinal within its tile. The server
then sends `TileCached { Tile, Version, int[] EntityIds }` instead of the definitions when the client
says it has that version: four bytes an entity instead of about 40 packed (700 unpacked). Not in
stage 1: a join at medium on Magnolia is 530 KB packed.

Persistent runtime state (a door left open, a dropped item, a broken window) is kept per tile as a
small delta beside the tile, applied when the tile is loaded.

## Wire messages

Added (append-only):

- `EntityDefinitionPack` (union 39): an `EntityDefinitionBatch` compressed with Brotli. Used for
  every map load and every tile, on every map. A definition is about 700 bytes (most of it the
  prefab's description and sound settings, the same for every wall of a kind) and packs about 16 to 1.
- `TileStreamUpdate` (union 38): the tile size, the tiles whose level changed (x, z, level; level 0
  means dropped), how many definitions were sent for them and how many entities removed. Sent after
  the definitions and removals it describes, so when it arrives they have arrived.
- `MapManifest.TileMetres` (appended): 0 for a map that is not streamed. Tells the client to treat
  later definition batches as tile loads.
- `MapDataRequest.FullDetailMetres`, `MapDataRequest.FarMetres` (appended): the client's detail
  setting. 0 asks for the server's default.
- `/detail low|medium|high` is a text command, as `/aimassist` is: no new message.

Stage 2 adds the frame origin to the manifest (two doubles) and `FrameShift` (a whole-tile offset);
stage 2's cache adds `TileCached` and a client `TileHave` (tile, version) list.

## Client: loading and unloading tiles

- Definitions in a batch after `MapLoadComplete` on a streamed map are tile loads. They are filed as
  today, except that rooms and doorways are not put on the acoustic map one at a time (each of those
  copied every region table and surveyed the room's walls against every definition, on the game
  thread). They wait for the tile's `TileStreamUpdate`.
- Removals are `EntityRemoved`, as today: the voices playing on those entities stop
  (`ClientAudioSystem.ForgetEntity`), beacons have nothing to sound from, and the collision grid loses
  them.
- The static collision grid is rebuilt into a new grid and swapped in, instead of being cleared and
  refilled in place while the acoustic worker reads it.

### Acoustic map

The acoustic map is the region tables, the openings and a voxel grid of the rooms. On a
`TileStreamUpdate` the client rebuilds the whole acoustic map from the definitions it holds, on a
niced background thread, and swaps the new tables into the existing map object. The object is kept
because the mixer clears every reverb bus when the map object changes
(`FmodAudioProvider.SetAcousticMap`). Region ids are entity ids, so a room that stays loaded keeps its
id and its reverb bus. Doors that moved during the rebuild are put back as they are now, and a room
that arrived during it is added, before the swap. If more tiles arrive while it runs, it runs once more
when it finishes.

Rebuilding everything rather than one tile at a time is deliberate: openings are found from the walls
round each room, and a room on a tile edge needs the walls of both tiles. With 300 m loaded the
rebuild is a fraction of a second on a background thread.

### Steam Audio scene

Built (stage 1b, 2026-10-06), following docs/GEOMETRY.md 2.4 and its measurements:

- Every scene of the game's Steam Audio context is an **Embree** scene (`IPL_SCENETYPE_EMBREE`), and
  every simulator on that context (the occlusion worker's, the listener's traced reverb, the traced
  echoes, the late field, room traces, the cabin) is made for it (`SteamAudioScene.TypeFor`). Where
  Embree does not start (Steam Audio carries it for x86 and x64 only), or with `OPENFPS_EMBREE=0`,
  everything stays on the default tracer and the scene is rebuilt whole as before; the log says which.
  This ties the client's fast path to x86/x64, which the geometry design accepts.
- **A tile owns its geometry** (`TileSceneSet`): each tile the client holds (by where each box's centre
  is; 250 m on a map sent whole) has two sub-scenes, its open ground and everything else, built when its
  boxes change and kept while they do not. The full scene instances both; the listener's scene (no
  open ground under the listener's head) instances only the second, so the geometry is held once.
  A door leaf stays in its tile's sub-scene: a door that swings rebuilds that tile.
- **Two pairs of top scenes, used in turn.** While the simulators trace one pair, a change adds and
  removes instances in the other and commits it, and the worker hands it over as it always handed over
  a rebuilt scene. Steam Audio forbids a commit while a scene is traced, and this never does one: the
  next change waits until the last pair has reached every tracer (`TracedReverbSet.Reconfiguring`).
  Every instance is made when its sub-scene is new, before anything traces it; making one of a
  sub-scene being traced waited for the trace (up to 0.66 s measured).
- **What Steam Audio's Embree scenes do with instances** (read from Steam Audio 4.8.1's source,
  2026-10-06, after the first door that swung handed over a pair that traced as empty; Cody heard
  traffic inside Selby House and the reflections go mono). Both held by `TileSceneSetTests`:
  - An instance is in its top scene's Embree scene from the moment it is made, *enabled*, and only a
    commit that finds it on the scene's list (`iplInstancedMeshAdd`) commits it. Embree will not build a
    scene holding an enabled geometry that was never committed ("geometry not committed"; Steam Audio
    does not report it), and the scene keeps its last build, or none. Instances made for the pair in use,
    or for a pair that a replaced tile never reached, were exactly that. Each instance is now disabled
    the moment it is made (added and taken out again), and enabled only by being added for a commit.
  - Releasing an instance gives its geometry id back to the top scene, but the geometry is never
    detached from the Embree scene, so the next instance given that id cannot be attached and is
    silently not there: the third swing of a door lost the tile it stood in. An instance now lives as
    long as its top scene (disabled once its tile is replaced), and with it the replaced tile's
    sub-scenes. When a pair holds more replaced geometry than it traces (`TileSceneSet.RecycleShare`),
    it is made afresh the next time it is idle: new top scenes, an instance of every tile in use, and
    the old pair let go with everything it held. That makes instances of sub-scenes being traced, so it
    can wait: 15 to 60 ms on the city, 0.4 to 0.9 s on Magnolia while streaming, once every twenty-odd
    tile changes, on the build's own thread (sources keep their answers meanwhile).
  - Measured against the whole scene (`OPENFPS_TILE_SCENES=0`): AudioLab `--path-probe ... door=
    swings=N` (open, shut, open... each a swap; `traced` adds the traced reverb and echoes at the ear,
    binaural) and `--stream-walk ... stops=M` (stops every M metres once everything has settled and
    asks twenty sources afresh) answer the same at every swing and every stop.
- The routes through openings are made again when the openings change (a door, rooms coming or
  going) and otherwise at most every 3 s while only walls and roads change.

Tried and not kept: assembling new top scenes from scratch for each change (instances of traced
sub-scenes waited for traces, 0.07 to 1.9 s an assembly); door leaves as instances of their own at
their pose (about 500 within reach on Magnolia made each assembly 70 ms to 2.2 s). Doors as moving
instances are left for geometry stage 1.

### Regions and named places

Rooms are regions and arrive with their tiles at full detail. Named places (yards, road zones) are
full-detail only. "Where am I" answers from what is loaded, which is always the tile you stand in.

### Roads

Roads are server data; the client needs nothing. In stage 2 a road crossing tiles is stored once,
with its own id, in the tile its first point is in, and listed (`Tiles`) in every tile it crosses;
the server's `RoadNetwork` is assembled from the roads of the loaded tiles and joins them at
junctions by road id, which gen_osm already writes.

### Physics and collision

The server collides against the whole map in stage 1. The client predicts against what it has
loaded, which always includes 300 m around it, so prediction and the server agree. In stage 2 the
server only has the tiles some player is near; a player can never reach a tile the server has not
loaded, because the server loads at the far radius and moves at the full radius.

## Persistence (stage 2)

Layout on the server, beside the maps:

    world/
      generator.json                     generator version, data sources and their dates
      sources/                           regional extracts (see below), shared by all tiles
      tiles/v{generator}/{zone}{N|S}/{x}/{z}/
        coarse.json.gz                   the coarse level
        full.json.gz                     everything
        meta.json                        content hash, entity counts, sources, when made
        state.json                       runtime deltas (doors, items), optional
      tiles/v{generator}/.../{z}.lock    while a tile is being made

- Versioning by generator version: a new generator does not overwrite old tiles. A tile is
  regenerated the next time it is wanted; players' saved positions survive because they are world
  coordinates, not entity ids.
- One generation per tile at a time: the server keeps an in-process map of tile to its pending
  generation task, and a lock file created with O_EXCL guards against a second server process. A
  stale lock (older than ten minutes, no live process) is broken and logged.
- A tile is only ever written whole (written to a temporary name and renamed).

## Stage 2: generating tiles on demand

### Data

| Layer | Source | How to get it in bulk |
|---|---|---|
| Roads, names, gates, fences, rail, water, POIs | OpenStreetMap | Geofabrik regional extracts (`.osm.pbf` per US state, updated daily), clipped per tile with `osmium extract` or pyosmium. Overpass is not for bulk: its usage policy is about 10,000 queries and 1 GB a day, and the public instances are shared |
| Building footprints and heights | Overture Maps buildings | GeoParquet on S3 (`s3://overturemaps-us-west-2/release/<date>/theme=buildings`), read by bounding box with DuckDB or pyarrow (the files carry a bbox column); no account needed |
| Addresses | Overture addresses (NAD in the US) | the same, theme=addresses |
| Places | Overture places | the same, theme=places |
| Address ranges | Census TIGER/Line ADDRFEAT | one zip per county, cached per county |
| Land cover | ESA WorldCover 2021 | 3°×3° Cloud-Optimised GeoTIFFs on S3, read by window |
| Elevation | USGS 3DEP | 1 arc-second COGs, read by window |

The server keeps a regional cache under `world/sources/`: the state's OSM extract, and the Overture
and other layers fetched for a region (for example a 0.25° square) the first time any tile in it is
wanted. Generating a tile then reads only local files and takes seconds. Fetching a new region is
minutes and is done ahead of the player, at the far radius.

### The generator

gen_osm.py today makes a whole place in one run: lot lines are decided between neighbouring
addresses, address interpolation uses whole roads, woods use the whole land cover grid. Per-tile
generation must give the same answer whichever tile is generated first. The rule: every feature (a
building, its lot, a road segment, a stand of trees) has an anchor point, belongs to the tile its
anchor is in, is generated whole by that tile, and is listed in every tile its footprint overlaps.
Generating a tile reads the data for the tile plus a margin (100 m) so lots and buildings at the
edge see their neighbours. Every random choice is already seeded from the thing itself (`h01`), so
this is a restructuring, not a rewrite.

### Running it

- The server runs `python3 tools/gen_osm.py --tile=15N/1023/13345` as a child process: no network
  access in the generator (only the downloader has it), a CPU and memory limit (ulimit, or a systemd
  slice on the VPS), a timeout of two minutes, at most two at a time.
- The download step (`fetch_place.py`, needs overturemaps, shapely, rasterio, numpy) runs in its own
  venv on the server, rate-limited, one region at a time.
- Tiles are wanted from the far radius, so generation has (far − full) ÷ speed of warning: 500 m at
  30 m/s is 16 s. Coarse first, full after.

### At an edge that is not ready

The server never lets a player into a tile it does not have. If the next tile is not generated yet:

- It is asked for as soon as it enters the far radius.
- Walking or driving into it is stopped at the tile edge as by a wall that is not a wall: no impact
  sound, a short low tone, and once the words "Not built yet. Wait here, or turn back." A driven
  vehicle is braked hard to a stop before the edge.
- When the tile arrives the tone stops and nothing else needs doing.
- Tiles outside every data source (sea, another country without data) are generated as flat open
  ground with no roads, so there is no invisible wall in the middle of nowhere.

## Stage 2 with terrain: the order of work (2026-10-09)

Streaming stage 2 and geometry stage 3 (docs/GEOMETRY.md section 7) are built together, on one branch,
in four steps. Each leaves every map working and is committed with its tests.

| Step | What | What it proves |
|---|---|---|
| T1 | The heightfield in `OpenFPS.Geometry`: one terrain tile per 250 m tile, 2 m posts, heights in centimetres, a material per cell. Part of its tile's piece, so rays, the ground probe, containment and contact find it with no change to their callers; each cell's two triangles are convex prisms for the body. A terrain tile is an entity (`TerrainTileComponent`, appended to the definition) so the streamer, the client's geometry, the acoustic store and Steam Audio take it as they take boxes | Heights, slopes, a steep bank as a wall, a hill in the way of sound, footsteps on the cell's material, the server and a client building the same bits, all on a made-up terrain |
| T2 | Real elevation for Magnolia and Albany. The map carries its elevation (3DEP, 5 m, in the map's metres); the server makes the 2 m terrain tiles at load, graded to the floors and solids that rest on it. `gen_osm.py` sets everything on the ground: houses on level pads, road and drive slabs pitched along their run, lawns tilted to the ground, trees and props on it, road centrelines with heights. The 3 km dirt slab goes | The ground follows the survey within a stated tolerance away from grading; the spawn, kerbs, walks and footsteps on the real maps; both maps still load and regenerate byte for byte |
| W1 | The world store on the server: tiles keyed by UTM zone and 250 m square, versioned by generator, gzip JSON written whole, a lock per tile, and a disk cap (20 GB, a server setting). The tile generator runs in the server in the background, two at a time: terrain from 3DEP asked for in the tile's own UTM metres, nothing downloaded kept; flat open ground where there is no survey | A tile is made once and read from disk after; the cap evicts the least recently visited tiles; a second request for a tile being made waits for it |
| W2 | One world: a map called "the world" whose tiles are the store's, loaded as players near them. The frame's origin goes on the manifest (appended). Players arrive at a place by name; the maps list has two parts, the world and maps. A tile not yet built stops a player at its edge (a short low tone and once "Not built yet. Wait here, or turn back.") | Tiles are asked for from the far radius and arrive while a player walks; nobody enters a tile that is not there |

What is left after these four:

- The per-tile generator for OpenStreetMap and Overture features (roads, buildings, addresses, woods)
  that gives the same answer whichever tile is made first. Roads and woods done 2026-10-10 ("Roads and woods
  on the world's tiles"); buildings, addresses and the rest planned there. Over the two real places their
  maps are copied in ("Places in the world", 2026-10-09).
- Coarse terrain at 8 m for the far ring (done 2026-10-09, "Coarse ground in the far ring"); still to do:
  the client's tile cache, frames that rebase past 8 km, crossing a UTM zone edge.
- Draped road meshes with kerbs and sidewalks as swept profiles, bridges and tunnels, creeks cut in
  (geometry stage 4 shapes); diffraction over the terrain profile and the ground reflection reading the
  slope.
- Traffic, walkers and Alex follow the roads' heights where the roads carry them; anything that leaves a
  road keeps its old flat assumptions.

## Stage 3: seamless travel

- One world per server instead of one map per world: the server loads tiles of the world as players
  come near, keeps them while any player has them, and drops them after a grace period.
- Frames (above) for float precision; players in different frames are simulated in their own frames
  and see each other only when they share one.
- Traffic and walkers are simulated in the active tiles only; vehicles leaving the active set are
  frozen and later retired, and new ones enter at the edge of the active set from road traffic data.
- The road network is assembled from loaded tiles; a vehicle's route is planned only through loaded
  tiles and extended as tiles load.
- Steam Audio instanced meshes per tile; per-tile acoustic maps merged by region id.
- Tile caches on the client (TileCached).

## Stage 4: maps players build

A user map is a world of its own (`worlds/<map id>/tiles/...`) with the same tiles, levels and
streaming. Content comes from what the builder places (prefabs, composites, roads as data) instead of
the importer. The builder writes each change into the tiles the thing overlaps and bumps their
revision; clients that have the tile get the change as entity messages, as today; others get the new
revision when they load it. `/extend north 500` adds tiles of ground. Ownership, publishing and
invitations stay as they are (MapAccessRepository). A user map can also start as a copy of generated
tiles (a template is a set of tiles).

## Effort and risks

| Stage | Work | Estimate |
|---|---|---|
| 1 | tile index, per-session interest, broadcast filter, wire messages, client deferred acoustics, background acoustic and scene rebuilds, settings, tests | one to two sessions (this one) |
| 2 | UTM tile keys and frames, regional data cache, per-tile generator, generation service with locks and limits, tile store, the edge behaviour | five to eight sessions |
| 3 | world server instead of maps, tile load/unload on the server, simulation only near players, road network across tiles, instanced Steam Audio meshes, client tile cache, rebasing | five to eight sessions |
| 4 | builder writes tiles, revisions, templates as tile sets | two to four sessions |

Risks:

- Acoustics at the loaded edge. A room whose walls are half loaded has openings that are not there;
  the radius keeps this 300 m away, but a low setting brings it to 150 m.
- Steam Audio rebuild cost grows with the radius (a whole-scene rebuild per tile change) until stage
  3's instanced meshes. Measured below for stage 1. Stage 1b (2026-10-06, below) brought the instanced
  sub-scene per tile forward, so a tile change rebuilds only that tile.
- The server keeps the whole map in stage 1; a whole town (a ZIP area) would be hundreds of thousands
  of entities and needs stage 2's server-side unloading before it is practical.
- Per-tile generation that does not depend on order is the hardest part of stage 2: lots, address
  interpolation and woods are whole-place decisions today.
- Running Python on the server: resource limits, no network in the generator, and the VPS's memory
  (the generator holds a region's buildings in memory).
- Licences: the tile store is a derived database of OpenStreetMap (ODbL), so it has to be offered
  under the ODbL with attribution; Overture's places are CDLA-Permissive, WorldCover CC BY. County
  parcel data stays out.
- Data gaps: Overture has no addresses outside some countries; rural roads without names; buildings
  without heights (a default height).
- Bandwidth on the VPS: a join at medium detail on Magnolia, and each tile after it, measured below.
- Float precision past 16 km without frames.

## Stage 1 as built

Code: `OpenFPS.Common/Tiles.cs` (TileKey, TileDetail, StreamRadii, TileSelection),
`OpenFPS.Server/Core/MapTiles.cs` (the index, built at map load), `OpenFPS.Server/Core/TileStreamer.cs`
(per-session interest, called from `GameServer.BroadcastWorldState`), `GameServer.SendMapData`,
`ClientWorldState` (deferred rooms, `RequestAcousticRefresh`, the grid swap),
`AsyncAcousticWorker.RebuildSceneIfNeeded` (GeometryVersion), `/detail` on both sides. Tests:
`OpenFPS.Tests/WorldStreamingTests.cs`. Instrument: AudioLab `--stream-walk`.

Differences from the plan above:

- Definitions are packed (Brotli) everywhere, not only for streamed maps. It was the cheapest large
  saving: the city's join went from 5.0 MB to 186 KB.
- A tile change with no rooms or doorways in it (the coarse ring moving) does not rebuild the acoustic
  map, only the Steam Audio scene.
- The client's collision grid keeps a slab bigger than 400 cells (the 3 km ground) apart from its
  cells; filing the ground in 90,000 cells was three quarters of every grid rebuild.

Measurements (development machine):

| | Magnolia | Albany |
|---|---|---|
| Join at medium: entities of the map | 12,344 of 32,598 | 17,140 of 41,327 |
| Join bytes (packed) | 530 KB | 740 KB |
| Server time to send the join | 27-36 ms | |
| Client acoustic map at join | 0.4 s | 0.7 s |
| Walking 700 m east (test, 5 m a tick) | 41 tiles in, 2,764 definitions, 7,207 removed, 176 KB | |
| Server broadcast tick while streaming (one player) | median 1.2 ms, worst 2.9 ms | |
| Game thread on a frame with tile messages (`--stream-walk`, 15 m/s) | median 10 ms, worst 30 ms | median 15 ms, worst 33 ms |
| Acoustic map rebuild (niced thread) | 130-310 ms | 280 ms |
| Steam Audio scene rebuild (niced thread, ~11,000 boxes) | 0.8-1.1 s | up to 1.8 s |
| Worker answer gaps, quiet / while tiles change | median 33 / 33 ms, worst 175 / 172 ms | worst 399 / 462 ms |

The worker's gaps do not grow while tiles change: nothing waits on a rebuild. At driving speed the
scene is rebuilt about once a second (31 rebuilds in 40 s at 15 m/s), which is one niced core kept
busy; walking, it is a few a minute. That cost grows with the far radius and is the reason to do
stage 3's instanced meshes per tile early.

Not done in stage 1:

- The server still simulates traffic and walkers on the whole map; it only stops sending them.
- No client tile cache; no frames (stage 2).

### Stage 1b: per-tile Steam Audio scenes, the coarse layer by acoustics (2026-10-06)

Measured with `--stream-walk` (real time, with Steam Audio, on a shared 24-core machine; A and B run
back to back) and `--tile-scenes`:

| | Before (default tracer, whole scene) | After (Embree, a sub-scene per tile) |
|---|---|---|
| Steam Audio scene work, Magnolia at 15 m/s | 492-561 ms a second (32 rebuilds in 43 s) | 32-46 ms a second |
| ...walking at 1.4 m/s | 46 ms a second | 4 ms a second |
| ...Albany at 15 m/s | 583 ms a second | 35 ms a second |
| Routes through openings (both) | 85-104 ms a second at 15 m/s | the same |
| Process CPU, Magnolia at 15 m/s | 199-210 % of a core | 185-203 % |
| Worker answer gaps while tiles change | median 33 ms, worst 155-257 ms | median 33 ms, worst 179-224 ms |
| One tile changed: rebuild, then swap in | (whole scene) 0.6-0.9 s | 23 ms, then 13 ms |
| Build Magnolia's 11,761 boxes at the spawn | 197-293 ms one mesh | 360-404 ms as 64 tiles (85-125 ms as one Embree mesh) |
| A run of direct and reflections, 48 sources | 95-147 ms | 40-58 ms |

Same answers (`--tile-scenes`, 48 sources round the spawn, 18 of them mostly occluded): occlusion and
transmission identical to the default tracer's one mesh (difference 0.000); mid-band reverberation time
within 0.1-1.7 % on average of the default's, against Embree's own run-to-run spread of 0.1-1.8 %
(Embree's reflections are not bit-for-bit repeatable; the default's are). The listener's scene holds
the same 11,020 boxes as the whole-map open-ground filter.
- The settings menus have no world detail control yet; `/detail` does it.

## Stage 2 as built (2026-10-09)

Steps T1 and T2 (terrain, and the real places on it) are in docs/GEOMETRY.md section 11.

### W1: the world store and making tiles

Code: `OpenFPS.Server/OneWorld/` (`Utm`, `WorldTileKey`, `WorldTile`, `WorldStore`, `Elevation`,
`WorldTileService`, `WorldSettings`). Tests: `WorldStoreTests`.

- **Keys.** UTM on WGS84 (USGS Professional Paper 1395's series, no Norway or Svalbard exceptions), a
  tile every 250 m from each zone's own origin: `15N/943/13342` is Bobcat Lane. Round trips to a ten
  millionth of a degree.
- **The store**, under `world/` in the server's folder (gitignored):
  `tiles/v1/{zone}{N|S}/{x}/{z}/full.json.gz` per tile, `{z}.lock` while one is being made, and
  `index.json` with each tile's size and when it was last visited. A tile is gzip JSON: its ground
  (2 m posts, centimetres over a base in metres over the sea, base64), where the ground came from, and
  room for entities later. Written to a temporary name and renamed; a tile that does not read is made
  again.
- **The cap**: 20 GB unless `world.json` says otherwise (`CapGigabytes`). When a write takes the store
  over it, tiles are dropped until it is under nine tenths of the cap: an older generator's first, then
  the least recently visited (a player's tiles are touched as they are loaded, W2), never one a player
  has loaded or one being made. A dropped tile is made again when it is next wanted: the cap costs the
  next visitor a wait, never a hole.
- **Making a tile**: the 3DEP ImageServer is asked for the tile in its own UTM metres (`bboxSR` and
  `imageSR` the zone's EPSG code), 126 x 126 cells of 2 m centred on the posts, as a float TIFF read in
  memory and thrown away. At most two at a time (`MaxAtOnce`), two minutes each, a lock file against a
  second server process (broken after ten minutes). A tile the survey could not be asked for (no network)
  is not stored and is tried again after 30 s; one the survey covers none of (the sea, abroad) is flat
  open ground at sea level, stored like any other.
- **Measured**: Bobcat Lane's tile from 3DEP made in 0.95 s, 21.9 KB on disk, ground 64.7 to 72.1 m.
  Magnolia's own ground as tiles: 18.5 to 19.8 KB each, so 20 GB holds about a million tiles, every
  250 m square of 65,000 km² (Texas is 696,000 km²). Ground only; buildings and roads will add to it.
- **Settings** (`world.json`, all optional): `StorePath` ("world"), `CapGigabytes` (20), `Generate`
  (true), `MaxAtOnce` (2).

### W2: one world

Code: `OneWorld/WorldMaps` (frames, places, tiles round players, arrivals), `OneWorld/Geocoder`,
`MapTiles` (tiles that come and go: `AddTile`, `RemoveTile`, `IsReady`, `Version`), `TileStreamer`
(only tiles that are there; a tile arriving is worked in at once), `SharedMovementEngine` (the fence),
`/join world`, the two-part list. Tests: `WorldMapsTests`.

- **Frames.** The world is served as frames: maps whose (0, 0) is the south-west corner of the 250 m
  UTM square a player first arrived in, x east and z north along the grid, y metres over the sea less
  the frame's base (the ground there, to the metre). Frame tile (x, z) is world tile (origin + x, origin
  + z). A frame reaches 6 km each way; arrivals within it share it, so two players in one town meet.
  Moving a frame with its players (rebasing at 8 km) and crossing a zone edge are stage 3. A frame is
  made by the server, never written to a file (`MapData.IsWorld`), and gets no loader's ground.
- **Tiles round players**, every quarter of a second on the tick thread: the world tiles within each
  player's far radius and 100 m more are wanted; a stored one is loaded (its ground an entity on the
  "ground" layer, coarse), a missing one is made in the background. A tile nobody holds or is near is
  let go after 30 s. The store's cap never drops a loaded tile, and a loaded tile is touched.
- **Arriving.** `/join world magnolia` (a place by its words), `/join world address 1042 Belmont Ave SW,
  Albany, OR` (the Census geocoder, any US address), or for builders `/join world 30.1237, -95.7409`.
  The tile arrived in and the eight round it are wanted at once; the player is told "Building the world
  at ..." if it is not stored, and is moved into the frame, stood on the ground, as soon as it is. Two
  minutes without it: "could not be built just now".
- **The edge that is not ready.** `MovementContext.TileReady` and `TileMetres`: on the world, a step from
  a ready tile toward one that is not stops a body's radius short of the shared edge on that axis (it
  slides along the edge), and across a corner on both. The server asks its tile index; the client asks
  the tiles it holds whose ground its triangle world has built, so a foot never comes down on ground the
  client cannot stand on yet. The client plays a short low tone (196 Hz, 0.22 s) while pressing on, at
  most every 0.6 s, no knock, and says "Not built yet. Wait here, or turn back." once until it has been
  clear of an edge for 5 s. When the tile arrives the body walks on. Since "Building ahead" (below) this is a last resort, and a
  driven vehicle is braked to a stop before it.
- **The list of where to go** (F6) has two parts: "The world" (`world_places.json`, and any map of a
  real place not listed there, at its origin) and "Maps". A frame is never listed as a map. The text
  gateway lists the world's places first too.
- **Where am I.** `/map` in the world: "the world, 1.2 kilometres north east of Magnolia, Texas, 31907
  Bobcat Lane"; builders also get the grid square (`15N/943/13342`).
- **Measured** (`WorldMapsTests`, the real survey): arriving at Bobcat Lane with nothing stored took
  0.4 to 0.7 s; the 41 tiles round it 2.5 s on one run and 60 s on another (3DEP's own speed, two at a
  time); 20.6 KB a tile. The ground round the arrival is 0.9 m under Magnolia's map survey there: asked
  for 2 m cells the service mosaics the 1 m lidar, the map's export (5 m cells in degrees) a coarser
  product. Not a projection error: the UTM conversion matches PROJ to the centimetre.
- **Wire, appended:** `MapManifest.IsWorld`, `WorldZone`, `WorldNorth`, `FrameEasting`, `FrameNorthing`,
  `FrameBaseY`; `MapSummary.IsWorldPlace`. With T1's `ColliderShape.Terrain`, `TerrainTileComponent` and
  `EntityDefinition.Terrain`.

### Building ahead: nobody waits at an edge (2026-10-09)

Cody, 2026-10-09: the world should be built before a player gets there, not while they stand at its edge.
The edge stop stays, as a last resort only.

Code: `WorldTileService` (the queue), `WorldMaps` (`Interest`, `SecondsToReach`, `Hold`, `Prebuild`),
`GameServer.MapDataAsked` and `MoveToMapNow` (the held join), `DrivingSystem.TileFence`, the client's
`WorldLoading` handling. Tests: `WorldLoadAheadTests`, `OccupancyTests.ACarIsBrakedToAStopShortOfGroundNotBuiltYet`.

- **The queue.** Tiles to make wait in a queue taken in order of how soon somebody could be in each, in
  seconds: the distance to the tile's nearest point, over the player's speed toward it if they are heading
  that way, or over a run (7.2 m/s) if that is sooner (they can turn and run). The tile you are in is 0.
  Speed is the server's own: the body's velocity, or the vehicle's for somebody riding. Every quarter of a
  second the priorities are worked out again for every player. Tiles nobody is on their way to any more go
  to the back of the queue and are still made.
- **What is wanted.** Every tile within a player's far radius and 100 m more, as before, and every tile
  they could reach within 30 seconds however far that is (a car at 60 m/s: 1.8 km ahead). 30 s because
  3DEP took up to 3 s a tile on its slow run (41 tiles in 60 s, two at a time) and one answer can take
  ten times that.
- **Arriving.** `/join world PLACE` (and F6 The world) puts you in the world's frame as soon as the tile
  you arrive in is made (under a second when 3DEP is quick), which shows the loading screen. The server
  then holds your join until every tile within your far radius of where you will stand is built and
  loaded, and tells the loading screen how it goes: "Building the world: 12 of 41 tiles." shown every
  second and said every 5 seconds (message `WorldLoading`, 45). Then the join goes out with all of them,
  and you stand in a finished ring. A text session waits the same way and hears the same line. The wait
  ends early, and the edge does the rest, if the remaining tiles cannot be made now (no network, or a
  server that makes no new tiles), or after two minutes.
- **At start.** The tiles within 1,300 m of every place in `world_places.json` (the far radius at high
  detail and the margin) are queued behind anything a player wants, so the first visitor to a listed
  place waits for nothing. `world.json` `"Prebuild": false` turns it off.
- **A car at the edge.** A driven vehicle on the world looks ahead along its way for its stopping
  distance (80 % of its tyres' grip), two ticks' travel and 4 m; if a tile there is not built, it is
  braked hard whatever the driver asks, and the driver is told once "The road ahead is not built yet.
  Stopping here until it is." It can always back away, and when the tile is built it drives on. If it
  reaches the edge anyway it is held there, never driven onto nothing.
- **Logging in where you left.** A player who logged out in the world is saved, as on any map, by the
  frame's id (which names the frame's corner tile) and the place in it. At login that is turned back into
  a point of the world (`WorldMaps.WhereSaved`, `BaseYOf`) and they arrive there as above, under the height
  they left at (a floor, not the roof over it), facing the way they faced. If the ground there cannot be
  built within 30 s they land on the landing map as before. A text session lands on the landing map.

Measured (`WorldLoadAheadTests`: the real `WorldMaps` and `WorldTileService`, a survey that answers each
tile a set time after it is asked on a clock the test turns, two at a time, a straight run east from
Bobcat Lane for 5 to 5.5 km):

| | 3 s a tile (3DEP's slow run) | 6 s a tile |
|---|---|---|
| Arriving, medium detail (45 tiles) | 69 s on the loading screen | 128 s |
| Arriving, low detail | 33 s | |
| Walker 4.5 m/s, runner 7.2 m/s | never at an edge; built ground at least 825 m ahead | |
| Car 30 m/s (108 km/h) | never at an edge; at least 575 m ahead | never; down to 100 m ahead |
| Fastest straight run that never meets an edge | 60 m/s (216 km/h), medium or low detail | 30 m/s (108 km/h) |
| The first speed that meets one | 70 m/s, after 3.3 km (medium) or 2.1 km (low) | 40 m/s, after 1.8 km |

Two at a time, 3 s a tile is two thirds of a tile a second. A straight run needs a tile every 250 m on its
line and the ones beside it; past about 65 m/s the tiles beside the road, which a car could also swerve
into within seconds, come in faster than that. Then a car is braked to a stop short of the edge and goes
on when the tile is built; a walker is stopped at the edge as before. Asking 3DEP for four tiles in one
request, or three at a time, would raise the speed; neither is done.

### Places in the world: Magnolia and Albany copied into its tiles (2026-10-09)

`/join world magnolia` (or Albany) arrives at the map's own spawn, among the same houses, roads, lawns,
named places and traffic as the map, on the same ground. The maps keep working on their own as well.

Code: `OneWorld/WorldPlaces` (the copy), `WorldTileService.Placed` (the hook a placed tile is made by),
`WorldStore` (placed tiles in the index), `MapManager.SpawnCopied` (a map's things into a live map, the
map load's own path), `MapTiles.AddPlaced`, `WorldMaps.Pump`, `VehicleSystem.SpawnMap`. Tests:
`WorldPlacesTests`.

- **Laid on the world's grid.** A place's map is in UTM metres less a 2 m post near its origin
  (docs/GEOMETRY.md 11.3), so a world tile over it is the map moved, never turned (`MapData.Utm`), and the
  map's survey posts are the world's posts.
- **How a place is cut.** Each thing of the map goes to the one tile its middle is in. Rooms, the
  doorways between them and their doors go together, to the tile of their middle, so a room never
  arrives without its doorway. A thing that spans tiles is stored once, in its own tile, and is sent to a
  client with every tile it overlaps (as on a map). Rooms carry what the map's load measured (their
  materials by name, indoors or not), so a tile needs no survey when it loads.
- **Its ground** is the map's: the same posts graded to the same slabs (`TerrainBuilder`), laid over
  whole world tiles. A place's tiles never ask the survey.
- **Roads, junctions, traffic and street life are not cut**: a road network is one thing. A frame of the
  world that wholly contains a place takes them, moved into the frame, when it is made; the place's
  vehicles are spawned on it then.
- **Stored** in the world store as ordinary tile files (`full.json.gz`), made through the tile queue like
  any tile, but marked as placed in `index.json` (the place and a version: the copy's format and the
  SHA-256 of the map file). The cap never drops a placed tile: it is content, not a cache of the survey.
  If placed tiles alone pass nine tenths of the cap, the server says so in its log every time the cap is
  checked. At start, a stored tile of a place that was not copied from the map as it is now (an older
  map, or ground made from the survey before the place was copied in) is dropped and copied again when
  next wanted.
- **On the server** a stored tile is read and unpacked off the tick thread and put into its frame at most
  6 ms a tick, soonest first: a tile of a town is up to 2,000 things.

Measured (`WorldPlacesTests`, Magnolia, this machine):

| | |
|---|---|
| Copying Magnolia out of its map at start | 42 ms |
| `/join world magnolia` to standing there, nothing stored | 1.5 s (the 55 tiles round the arrival; no survey asked) |
| Fixed things of the map within 400 m of the spawn found in the world, the same, where the map has them | 6,514 of 6,514 |
| The world's ground against the map's, 4,000 points within 600 m | median 0, worst 1.7 mm |
| What a body stands on (roads, lawns and drives too), 2,000 points | median 0, worst 2.3 cm |
| Roads, junctions, traffic | 198, 206, 4 vehicles: all of the map's |
| The whole place as world tiles | 196 tiles, 36,374 things, 5.1 MB packed (26 KB a tile, the biggest 57 KB), made in 0.4 s after its ground is laid |

### Coarse ground in the far ring (2026-10-09)

A tile of ground was sent at 2 m whatever the tile's detail, which took Magnolia's join from 530 KB to
1,388 KB. Now a client is sent a tile's ground at about 8 m while it has the tile only in its far ring, and
at 2 m once the tile comes within the full radius.

- **The coarse ground** (`TerrainTileComponent.Coarse`): 32 cells a side, so posts 7.8 m apart that land
  on both edges of the tile; each post's height read off the tile's own 2 m triangles, over the same base;
  each cell the material under its middle. Two coarse tiles side by side share their edge posts (within the
  centimetre).
- **No crack where it meets full ground** (closed 2026-10-09). Between two coarse posts a coarse edge is a
  straight line and the 2 m edge beside it is not, which left a hairline crack a ray could pass through. Now
  each edge post of the coarse ground is raised (whole centimetres, rounded up) until the coarse edge is
  nowhere under the 2 m edge; the corners stay on the 2 m corners, and a post depends only on its edge's own
  2 m posts, which the neighbour shares, so two coarse tiles still meet. And coarse ground hangs a skirt: the
  outer sides of its edge prisms, from the edge down to its floor, met by rays (`Heightfield.Skirted`) and
  laid in the Steam Audio scene. A client tells coarse ground from the wire's own fields (33 posts, further
  apart than 2 m; `TerrainTileComponent.IsCoarse`), so the wire is unchanged; 2 m ground never has a skirt,
  so the server's ground and every 2 m seam are as they were. Measured: on all 364 of Magnolia's seams, one
  side at 7.8 m and the other at 2 m either way round, none of 33,274 grazing rays and 1,064 lines of sight
  that pass the seam under the ground get through (front faces only); before, 3,082 and 22 did
  (`GeometryTerrainTests.CoarseGroundBesideFullGroundLeavesNoCrack`, `WorldStreamingTests`). The raised edges
  move the 7.8 m ground from the 2 m by a median of 2.2 cm (was 2.1), 43 cm at 99 points in a hundred (was
  41) and 3.2 m at worst (was 2.1, a creek bank by a corner, where the post next to the corner carries the
  whole lift); the lines of sight changed by a swap are 187 of 19,600 (were 186).
- **What is sent** (`TileStreamer.Definition`): the same entity either way. The join and every tile
  arriving later carry a tile's ground coarse if the client has that tile at coarse; when the tile comes up
  to full, its ground is sent again whole and the client's triangle world, acoustic map and Steam Audio
  scene take the new one in place of the old. Ground already sent at 2 m is not sent again coarse when the
  tile falls back to coarse.
- **Nobody stands on it.** The tile a player is in is always full, and the full radius is at least 100 m
  (300 m at medium), so the swap happens at the full radius, never under anyone's feet. The server's own
  ground is always the 2 m one: movement, cars, bullets and the server's sound paths never see the coarse
  ground.
- **What changes for the ear**: a sound whose path grazes the ground in the tile being swapped. Over all of
  Magnolia's 196 tiles, the 7.8 m ground lies from the 2 m by a median of 2.1 cm, 41 cm at 99 points in a
  hundred and 2.1 m at worst (a creek bank); of 19,600 lines from 1 m to 1.6 m over the ground within a
  tile, 1,145 are blocked by the 2 m ground and 186 (0.95 %) change when the tile is swapped (187 with the
  raised edges above). Each tile swaps once as a player approaches (the 50 m hysteresis), so such a change
  is a single step in one far sound's occlusion, not a flutter.

Measured (`WorldStreamingTests`, `WorldPlacesTests`):

| Join at medium detail | Definitions packed | On the wire |
|---|---|---|
| Magnolia map, before terrain (stage 1) | 530 KB | |
| Magnolia map, 2 m ground everywhere (T2, 5 m survey) | 1,388 KB | |
| Magnolia map, 2 m ground everywhere (2 m survey, 11.3) | 1,513 KB | 1,750 KB |
| Magnolia map, 2 m near and 7.8 m far (12 + 37 tiles) | 942 KB | 1,179 KB |
| Albany map, 2 m near and 7.8 m far | 1,112 KB (was 1,613) | |
| The world at Magnolia (9 tiles at 2 m, 36 at 7.8 m) | | 1,077 KB |

Walking 700 m east on Magnolia now streams 429 KB (was 608 KB): 10 KB a tile.

### Land cover for the ground (2026-10-10)

Outside the real places every cell of a world tile was dirt. Now each 2 m cell takes a material from ESA
WorldCover 2021 v200, the land cover gen_osm.py already reads for a place's woods.

Code: `OneWorld/LandCover` (`EsaWorldCover`, `LandCoverMaterials`, `HttpRanges`), `WorldTileService.LandCover`,
`WorldTile.LandCover`, `world.json` `"LandCover"`. Tests: `WorldLandCoverTests`.

- **Why WorldCover and not NLCD.** WorldCover is global (the world is not only the US), 10 m (NLCD is 30 m),
  in plain latitude and longitude (NLCD is in its own Albers projection), CC BY 4.0, and the same data the
  places' woods come from, so a place and the world round it agree. NLCD's classes are richer for the US
  (developed low to high intensity, deciduous or evergreen forest, pasture apart from crops); that is a
  possible later layer over the same table, not a reason to give up the rest of the world.
- **Reading it.** WorldCover is one Cloud-Optimised GeoTIFF per 3 x 3 degrees on S3, 36,000 pixels a side,
  in deflated blocks of 1,024 pixels (about 8.5 by 9.3 km at 30 degrees north). Only the blocks a tile's
  cells fall in are fetched, by HTTP byte range: the file's header once (its list of blocks), then each
  block once. Nothing outside .NET is needed (a TIFF directory reader and `ZLibStream`). A cell's class is
  the pixel its middle is in, so it is deterministic.
- **The regional cache** (`world/sources/worldcover/` beside the tiles): each file's header as JSON, and each
  block as it came, deflated (40 KB on average over N30W096's 1,296 blocks, 122 KB at most; Bobcat Lane's is
  67 KB). A block serves the thousand-odd world tiles in it, and is read from disk ever after, offline as
  well. A file that does not exist (the open sea) is remembered by a `.none` marker. The cache is outside
  the tile store's cap: Texas is about 9,000 blocks, some 400 MB.
- **The table** (`LandCoverMaterials`), onto the registry's own materials (an unknown name would quietly be
  Generic): tree cover and mangroves Foliage (the forest floor; footsteps in leaves), grassland, wetland and
  moss Grass, shrubland, cropland, bare ground and permanent snow Dirt (the registry has no snow), built-up
  Asphalt (footsteps as cement), permanent water Water (the survey's lakes are flat at the water). A tile
  lists only the materials it has, in a fixed order, so the same cover is the same bytes.
- **When it cannot be had** (no network and nothing cached), the tile is made anyway, every cell dirt as
  before, and the log says "no land cover for tile ... its ground is dirt". Unlike the survey, a tile
  without land cover is not a hole, so it is not held back.
- **Licence**: CC BY 4.0. Each tile records the attribution (`LandCover`: "(c) ESA WorldCover project 2021 /
  Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium"), and the cache
  has a `SOURCE.txt`.
- The generator version is 2: tiles made by version 1 (all dirt) are made again when next wanted, and are
  the first the cap drops. Placed tiles (Magnolia, Albany) are copied again too; their cells stay what the
  maps have (dirt under lawns and roads).

Measured (`WorldLandCoverTests`, a recording of the four byte ranges one reading of Bobcat Lane's tile asks
S3 for, 77 KB): every one of its 15,625 cells has the class Magnolia's own landcover.json has there (read
with rasterio by fetch_place.py); the tile is 70.6 % Foliage, 29.3 % Grass, 0.1 % Asphalt. Reading a tile's
classes takes about 20 ms from the recording or the cache. The materials add almost nothing to a stored tile
(the cells were already a byte each).

### Roads and woods on the world's tiles (2026-10-10)

Outside the real places a world tile now has its roads and its woods: OpenStreetMap's roads laid as
gen_osm.py lays a place's, the ground graded under them, and the woods from the land cover. The generator
lives in the server, in C# (`OneWorld/WorldFeatures`): the server already makes tiles in-process (W1), and a
Python child per tile would need a Python, shapely and a venv on the VPS, and a second copy of the road
logic's inputs. What was ported is only what roads and woods need, and a test holds the port to gen_osm.py.

Code: `OneWorld/WorldFeatures` (the generator), `OneWorld/Osm` (`OverpassRegions`, `OsmWay`),
`WorldTileService.Features`, `IElevationSource.WindowAsync`, `TerrainBuilder.SlabOf`, `world.json`
`"OpenStreetMap"`. Tests: `WorldFeaturesTests`.

- **Data.** OpenStreetMap through the Overpass API, a region at a time: 0.05 by 0.05 degrees (about 5.5 by
  4.8 km at 30 degrees north, a few hundred tiles), every way with a highway tag, whole, with its nodes'
  places (`out body geom`). Kept in the regional cache (`world/sources/osm/highways-2026-10-01/`, gzip JSON,
  130 to 220 KB a region round Tomball) and read from disk ever after, offline too. Overpass and not Geofabrik: a region is one request
  of a few hundred kilobytes, and a state's extract is hundreds of megabytes to download and a PBF reader to
  write before the first tile; the usage policy (about 10,000 requests a day) is far above what one server
  asks, one request at a time, a second apart, backing off when Overpass is busy. A server that makes tiles
  faster would read a Geofabrik extract through the same `IOsmSource`.
- **One date for all the data.** Every region asks for the data as it stood at 2026-10-01 (Overpass's
  `date` setting), so a road through two regions fetched a month apart is one road in both, and two tiles
  that see it agree. A newer date is a new generator version.
- **The same answer whichever tile is made first.** Everything is decided from the whole way in the zone's
  metres, never from the tile: where its pieces start and end (simplified, at most 20 m each, lengthened at a
  bend, overlapping 5 cm at the joins), their heights (the ground averaged over a 20 m square, road_y), which
  nodes are junctions and which junctions are one. Each piece, zone and junction belongs to the one tile its
  middle is in, which alone stores it; every tile it reaches is graded under it, because each tile works out
  every piece that comes within its margin. The ground is asked for the tile and 50 m round it (176 posts a
  side instead of 126) and graded as a whole, then the tile is cut out: two tiles grade their shared edge
  from the same posts and the same pieces, so the edge is one line of posts.
- **What a road is** (gen_osm.py's tables): motorway to service, not drives, parking aisles, areas or one-way
  service ways; each way in its own width (its `width`, its `lanes` at 3.4 m, or its class's two-lane width)
  and surface (asphalt, concrete, brick, gravel, dirt, grass, wood); a sidewalk where the way says it has
  one; a named place over each road (walking along it is silent, stepping onto it says its name) and at each
  junction ("Main Street and Elm Street junction"). Named as OpenStreetMap names it; an unnamed way is
  "Service road", "Private road" or "Unnamed road".
- **The woods**, from the tile's own land cover: where WorldCover has tree cover every 10 m and no road is
  within 2 m, canopy volumes of foliage (rectangles of 10 m cells, at least 800 m², 5 to 18 m over the
  ground), a trunk in every 40 m square on average (where, and whether, seeded from the cell's place on the
  world's grid), and one crown for the wind in the trees in every 80 m block of woods. All within the tile,
  so no neighbour can disagree. A place's woods take their species from the place; the world's trees are
  "Tree", because the species of a wood are not known anywhere and differ from Texas to Oregon.
- **The real places first.** A tile of Magnolia or Albany is the place's copy; nothing generated reaches
  into one (a piece whose footprint touches a placed tile is not made), so the roads stop at the place's
  tiles and the place's own roads carry on.
- **When the roads cannot be had** (Overpass down and the region not cached), the tile is not made and is
  tried again after 30 s, as when the survey cannot be asked: stored without its roads it would keep that
  hole. Land cover is different (above): a tile without it is dirt and has no woods, and is stored.
- **Not yet:** buildings (below), drives, footpaths, rail, water, verges; roads are surfaces to walk and
  drive on, not yet roads the traffic routes on (`RoadData`), so the world's traffic is still only the
  places'; bridges and tunnels are laid on the ground like any road.
- **Licence.** The store is now a derived database of OpenStreetMap (ODbL 1.0): each tile with roads
  records "(c) OpenStreetMap contributors, ODbL 1.0 (Overpass API, the data as of 2026-10-01)" in
  `Features`, and the cache has a `SOURCE.txt`. A server that offers its tiles to others offers them under
  the ODbL.
- Generator version 3.

Measured:

| | |
|---|---|
| The port against Magnolia's map (the place's osm.json on the map's own survey posts, three tiles round the spawn) | 70 of 70 pieces of road and sidewalk are the map's: worst 0.03 mm across, 0.00 mm in height, the same turn (1 - dot 1.2e-7) |
| A made-up road across a tile edge, A then B against B then A | the same bytes; no piece stored twice; the 126 posts of the shared edge equal (29 of them graded to the road); no ground above any piece's underside at 1,000-odd points on either side |
| Downtown Tomball, 25 tiles from the recorded regions, offline (made-up hills, no survey wait) | 0.35 s for all 25, a tile 12 ms median and 64 ms at most (the first reads the regions); 1,389 pieces of road and sidewalk, 369 named places, 61 roads by name; 22.6 KB a tile median, 26 KB at most (20 KB of it the ground) |
| Downtown Tomball, 9 real tiles over the network (3DEP, Overpass, WorldCover, cold caches) | the first 2.8 s (two Overpass regions and a WorldCover block fetched), the rest 0.6 to 3.2 s (3DEP's own time, as before); 21 to 24 KB a tile; 48 to 110 pieces and 5 to 54 trees a tile; the regional cache 555 KB after them |
| Half a tile of woods (made-up cover, the test roads through it) | 5 canopy volumes, 17 trunks, 4 crowns; none on a road |

So a tile's roads cost about 3 KB stored and a few milliseconds of the server's time; what a tile waits for is
still the survey (and, once per region, Overpass: 3 to 10 s for a region the first time anyone goes near it,
fetched while the tile's survey is asked).

#### Buildings: the plan

Not built in this step: shells need footprints, and the footprints a place uses do not come from where the
roads do.

1. **Footprints.** OpenStreetMap's buildings are sparse in the rural US (Magnolia's map takes its 4,000-odd
   from Overture: Microsoft's footprints traced from imagery, OpenStreetMap's, USGS lidar heights). Overture is
   GeoParquet on S3, read by bounding box; C# has no Parquet reader without a new package (Parquet.Net, MIT),
   and the alternative is a downloader child process in its own venv (`fetch_place.py`'s overturemaps), as
   this doc first planned. Either fills the regional cache with a region's footprints and heights (and
   Overture's addresses, which are the National Address Database in the US, for the names). Cody's choice: a
   new package in the server, or Python on the VPS. OpenStreetMap's own `building=*` ways can come in the same
   Overpass region now, for towns that are mapped, as a first source.
2. **The shell, gen_osm.py's build() at medium detail**, ported as the roads were: the footprint covered by up
   to four rectangles (cover, largest_rect), walls round their union (exterior_runs, wall_run with the door
   cut), a floor slab and a roof deck, one room per rectangle, a front door on the longest wall facing the
   nearest road, a doorway joining the room to the outdoors; the shed rule (small outbuildings one solid box);
   classify() without parcels (house, mobile home, premises, barn, shed by size, shape, Overture's class and
   the land cover's built-up); a name from the address, else "House off Main Street".
3. **The same answer whichever tile.** A building belongs to the tile its footprint's middle is in, which
   stores all of it (rooms, doorway and door together, as the places' copies keep them); a tile grades its
   ground to the floor slab of every building within its margin (a pad, as gen_osm.py's pad_of), so a house
   across an edge sits level in both. Doors link to rooms by ids within their own tile (already how a copied
   tile is spawned).
4. **Then** drives and lots (lot lines decided between neighbouring addresses within the 100 m margin the doc
   gives), interiors at high detail, and roads as RoadData so the world's traffic can use them.

Estimate: footprints and shells one session once the data choice is made; lots, drives and addresses one
more.

### Left after stage 2 (as of 2026-10-10)

- Outside the real places, world tiles have their roads and woods (2026-10-10, above), not yet buildings,
  addresses, drives, paths, rail or water, and their roads are not yet roads the traffic routes on. The plan
  for buildings is above.
- (Done 2026-10-09: a player who logs out in the world comes back to the same spot at login, through the
  loading screen; the landing map if the ground there cannot be built within 30 s. See Building ahead.)
- Rebasing a frame past 8 km, crossing a UTM zone edge, frames that are empty for a while let go.
- (Coarse terrain at 8 m for the far ring: done, above.) The client's tile cache. (Land cover for the
  ground's materials: done 2026-10-10, above.)
- (Done 2026-10-09: a driven vehicle is braked to a stop before an edge that is not ready; see Building ahead.)

## What the broadcast chooses from

Each player used to ask the spatial grid for everything within earshot. On the city earshot is the whole
map: about 86,000 grid entries a player a tick (a wall is filed in every cell it crosses) to find some
7,500 entities, 16 ms each on a desktop. Two players overran the 33 ms tick on the VPS, the loop fell
behind every few ticks, and everything anyone heard trailed what it belonged to (2026-10-04, fixed in
60d2fc4e). The grid holds exactly the collidable entities, so one pass over the world now finds the same
set once, with no repeats, and each player filters it.

Items are gathered as well, because they have no collider: you walk over a gun on the floor and a shot
does not stop on it. A broadcast that chose only from the collidable never mentioned an item, so one made
by /give, or picked up and put down, never reached a client and its item beacon had nothing to sound from
(Cody, 2026-10-04: "when I drop items I still don't hear them"). An item is the one thing that changes
hands after the map is streamed, so it is the one colliderless thing the broadcast carries.

A carried thing is sent every tick unreliably, like a body. As a static that moved, a carried gun went out
on the reliable channel every tick its holder walked, and one lost packet held up every chat line and
definition behind it.

The stats update (health, floor material, held weapon) goes only when something in it changed. It used to
go every tick, reliably, to say the same thing thirty times a second.

## The broadcast radius

How far from a player the server tells them about things is derived, not authored. It used to be one
constant, 200 m, which suits a room and not a racetrack: a one-mile oval is about 700 m across, so cars
spent most of a lap outside it. They vanished round the back, came back at 200 m already at full
throttle, and the client tore down and rebuilt their engine synthesis every lap. What the player heard
was cars pinned to one side, silence from the other, and stuttering, none of it an audio bug.

The radius comes from the two things that decide it: how far the map's loudest emitter carries
(Loudness.AudibleRange, which every sound declares) and how big the map is, since there is no point
reaching past its corners. A map with a quiet beacon keeps a small radius; add a race engine and it grows
on its own.

The ceiling must not be below what the loudest source carries. At 1,200 m it was: an airliner is 142 dB
at a metre and AudibleRange gives it 3,000 m, so outside 1,200 m the server stopped sending it. At
228 m/s that was ten seconds of existence per pass, and the rest of the time the sky was empty ("I'm not
hearing the planes"). 3,000 m is the cap AudibleRange itself applies, so a source is broadcast exactly as
far as it can be heard.

The radius is recomputed after everything that emits has been spawned (RefreshEarshotRanges). Measured
at map load alone, the vehicles were not there yet, every racetrack came out at the 200 m floor, and
eight cars on the oval only existed along the front straight.

## Far things less often

A moving thing 150 m or more from a player goes to that player at most every sixth tick (5 times a second)
instead of every tick, and only when what the client would make of it is about to be wrong
(OpenFPS.Common/DistantMotion.cs, RestingStates.DueFar, ClientWorldState.Track). Cody's condition
(docs/CODY_ASKS_2026-10-08.md section 4): only where it costs no realism, shown by a test.

What goes with each far state is how the thing is changing, as the server has it from one tick to the next:
its speed's rate and its heading's turn (EntityState.SpeedRate and Turn, eight bytes, sent only when not
zero). Between states the client carries the thing on those numbers, so a bend at a steady speed and a
steady brake are followed exactly, and Doppler, an engine's road speed and a train's notch come from the
server's own velocity, never from differences between positions. A speed and a turn rather than a vector
acceleration: round a bend, the difference of two velocities is shorter than the arc, and read as braking
(1 m/s² at 13 m/s on a 15 m radius).

The server runs the client's prediction for every far thing and sends a state the tick it strays by more
than a fifth of what was agreed as inaudible: a bearing of 0.1 degree, a tenth of a tick's travel (so the
correction is never a step), a speed 0.1 % off (the engine's pitch follows it), a velocity 0.34 m/s off in
any direction (0.1 % of Doppler), a heading 1 degree off; and when the horn changes, or a tyre's demand,
surface or water. A state that went for a change goes once more the next tick: the stream is unreliable,
and a change lost or overtaken would be carried wrongly for a fifth of a second, where anything sent
every tick is bridged by the tick after. A steady thing goes every sixth tick.

What a player is in, drives or carries goes every tick whatever its distance (GameServer.Involved), as does
their own body. Crossing 150 m changes nothing that can be heard: near or far, the client goes from one state
to the next the same way.

The client keeps, for each moving thing, its last state at or before the playback point (A) and its next
(B), the first snapshot from 'to' on that mentions it. Before B arrives it carries the thing from A. Once
B is known it steers onto it: the velocity and heading take B's in the last tick before B, as anything
sent every tick does (B went because they changed, and they changed then); the position makes up the
prediction's miss steadily from when B arrived, because the mixer carries a position on its velocity
between steps and a miss made up faster than that would be a small jump at the next step. A tick the
buffer never got may have held a change (a change goes twice and a far moving thing goes at least every
sixth tick, so only a lost tick just before the next state, or a longer run of them within six ticks of
it, can have), and then the steering starts before it, as the line through a lost tick always has. When the track changes other than by playback reaching B (B turned up late, or a
lost tick turned up after all), the two tracks are compared at the last frame's time and the difference
is eased out: the position over 0.1 s, the velocity over a tick. For anything sent every tick all of this
is the line from one tick to the next, as before, except on a connection that loses or reorders ticks,
where it is now eased the same way.

### The test

DistantUpdatesTests.FarThingsSentLessOftenSoundTheSame, also `AudioLab --distant-updates [net=poor]
[seed=N] [trace=ID|pass-by|fly-over]` (OpenFPS.AudioLab/Spikes/DistantUpdatesRig.cs). The city runs on the
real server, its traffic, walkers and trains, plus a car shuttling past at 108 km/h 20 m off and an airliner
flying over at 120 m and 100 m/s, both spawned as /spawn would. Two players stand side by side 40 m from the
railway, one sent everything every tick and one far things less often; each one's states go through the
wire (packed, split, read back), arrive 40 to 60 ms late (the same for both), feed a client's interpolation
at the 30 Hz step, and between steps are carried on their velocity at the mixer's 250 Hz, as the mixer does.
At every one of those instants, for everything beyond 150 m, the two are compared from the second player's
ears, and each against the server's own track.

45 s of the city on 2026-10-10:

| scene | things | bearing | pitch | largest change per instant (position, bearing): every tick / less often |
|---|---|---|---|---|
| pass-by (108 km/h) | 1 | 0.000° | 0.041 % | 0.2149 / 0.2149 m, 0.0096 / 0.0096° |
| fly-over (airliner) | 1 | 0.001° | 0.029 % | 0.7121 / 0.7125 m, 0.2229 / 0.2228° |
| train (light rail) | 24 sources | 0.010° | 0.088 % | 0.0899 / 0.0896 m, 0.0206 / 0.0206° |
| walkers | 352 | 0.001° | 0.002 % | 0.0113 / 0.0116 m, 0.0029 / 0.0029° |
| traffic | 41 | 0.006° | 0.122 % | 0.1116 / 0.1116 m, 0.0358 / 0.0359° |

Pitch is the Doppler times the road speed (for walkers the Doppler alone). A train's notch, which the client
works out from the speed's change, differed on 12 of 32,400 steps, never two in a row: a notch changing one
step earlier or later. The largest per-instant changes match to the millimetre: they are the interpolation
clock's own steps, which both have. The test asserts the agreed limits against both references and allows a
step no bigger than every tick ever makes but for a hundredth of a degree, a hundredth of a percent of pitch
or a centimetre.

On a poor connection (OnAPoorConnectionFarThingsAreNoFurtherOff: 30 to 90 ms, so one tick in ten overtakes
the one before, and 2 % lost) both clients leave the server's own track by the same amounts, in bearing
0.017 degree at most and in pitch up to 23 % (the airliner slowing at the far end of its leg, at a moment
the buffer ran dry); the one sent less often is never further off, and the test holds it to that. An early
version of this work, which did not ease a change of track, put the client sent every tick 1.47 degrees off
there: an overtaken tick moved the world back and forth. The client as it was before this work was not
measured on this connection.

### What it saves

Measured by the same rig, every byte the broadcast hands each player's socket, with 29 bytes of headers a
datagram, standing still for 40 s after the first 5:

| where | every tick | less often | saving |
|---|---|---|---|
| 40 m from the railway | 1.33 Mbit/s, 128 datagrams/s, 4,780 states/s | 0.62 Mbit/s, 65 datagrams/s, 2,059 states/s | 54 % |
| the spawn point | 1.33 Mbit/s, 128 datagrams/s, 4,780 states/s | 0.69 Mbit/s, 73 datagrams/s, 2,280 states/s | 48 % |

1.33 Mbit/s is the figure the offline replay of 2026-10-05 measured for the same stream (1.35); the live
three-bot figure then, 1.75 Mbit/s, also counted LiteNetLib's acknowledgements and the reliable channel.
