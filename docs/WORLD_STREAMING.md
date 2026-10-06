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

Recommended: **UTM zones, 250 m tiles.** A world tile key is (zone, hemisphere, floor(E/250),
floor(N/250)), written `15N/1023/13345`. Generation projects every feature into the tile's zone. A
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

Elevation: the ground is flat today and stays flat in stage 2. Each place keeps its 3DEP heights for
when terrain exists.

## Detail layers

Three levels per tile, per client:

| Level | What is sent |
|---|---|
| Full | everything |
| Coarse | the ground, roads, building shells (outer walls, floors, roofs, ceilings), rail, water. No doors, rooms, interiors, yards, drives, paths, verges, trees, props or named places |
| None | nothing |

Coarse is what keeps far buildings occluding and reflecting sound, and roads under far traffic, at a
fraction of the entities. A coarse building has a gap where its front door would be; with no room
behind it, that is right for a sound heard from 400 m.

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
says it has that version: four bytes an entity instead of about 140. Not in stage 1: a 300 m radius
on Magnolia is about a megabyte, and the join takes seconds either way.

Persistent runtime state (a door left open, a dropped item, a broken window) is kept per tile as a
small delta beside the tile, applied when the tile is loaded.

## Wire messages

Added (append-only):

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

The scene follows the acoustic map: each swap bumps a geometry version on the world snapshot, and the
acoustic worker rebuilds the scene and the routes through openings off its thread when the version
changes, exactly as it does for a door, and swaps them in. The old scene is released five seconds
later. Nothing waits on it: sources keep their last occlusion until the new scene is in.

Stage 3: one Steam Audio instanced mesh per tile (`iplInstancedMeshCreate`), added to and removed
from the top-level scene with a commit, instead of rebuilding the whole scene. That makes a tile load
cost the tile, not the radius.

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
  3's instanced meshes. Measured below for stage 1.
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

See the 2026-10-06 entry in changes.md for the list of changes and the measurements.
