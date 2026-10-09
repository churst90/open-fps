# Maps of real places

Two maps are made from open data about real places:

| Map id | Listed as | Where | Spawn |
|---|---|---|---|
| `magnolia_tx` | magnolia tx | A 3 km square south of Magnolia, Texas, round Bobcat Lane and Nichols Sawmill Road | 31907 Bobcat Lane, on the drive, facing the street |
| `albany_or` | albany or | A 3 km square of southwest Albany, Oregon, round Belmont Avenue Southwest | 1042 Belmont Avenue Southwest, in the front yard, facing the street |

Neither is the landing map. Go there with F6 or `/join magnolia tx` (the id works too). The map
files are `OpenFPS.Server/maps/places/*.json`; the server loads that folder with the others.
They are kept out of the test output's `maps` folder, so the tests that load every shipped map do
not load a town each; `RealPlaceMapTests` loads each one on its own.

## What is real and what is made up

Real, from the data:

- Every road's line, name, class, lanes and surface where OpenStreetMap has them, and the junctions
  where roads meet. Unnamed roads take their name from the Census address ranges along them.
- Every building's footprint and height (Microsoft's footprints traced from aerial imagery,
  OpenStreetMap's, and USGS lidar heights, all through Overture Maps).
- Addresses: OpenStreetMap's first, then the US National Address Database's points. An address goes
  on the building its point is in, or the nearest building on the same side of the same road.
- Shops, churches, the fire station, parks: OpenStreetMap and Overture places of kinds a passer-by
  would name. Businesses registered at somebody's house are left out.
- Gates, fences, walls and hedges, railways, sidewalks, ponds and creeks, single trees where mapped.
- Where the woods are: ESA WorldCover 2021, a class every 10 m.

Made up, deterministically (the same input gives the same map, byte for byte):

- Lot lines. No public parcel data may be redistributed here (Montgomery County's licence forbids
  it), so a lot runs back from the road's verge, square to its house, halfway to the next address on
  each side, and as deep as its buildings and a back yard.
- The address of a house the address points miss: interpolated from the Census address ranges of
  the road it faces, with that side's odd or even numbers, never one the street already has. These
  are approximate. Magnolia has 243, Albany 94.
- What a house is made of (brick or siding, in a proportion set per place), the rooms inside it and
  their doors, the furniture, the garage. A narrow house is a single-wide: rooms in a row.
- The individual trees: positions, spacing and species in a set proportion. Only where the woods are
  is real.
- Speed limits where none is mapped: the state's usual limits by road class (place.json).
- Driveways for houses OpenStreetMap has none for.

The ground follows the survey: USGS 3DEP at the resolution it was downloaded at (about 5 m), in each
place's `elevation.json`. The generator writes it into the map on a 5 m grid of the map's own metres
(`Elevation`) and sets everything on it: houses on level pads at the ground by their front door, roads
level across and pitched along their run in pieces of at most 20 m over the ground averaged across
20 m, lawns tilted to the ground, solid things set into it, named places stretched over it. The server
lays 2 m terrain from the same grid at load and grades it to the slabs lying on it (docs/GEOMETRY.md
5.1 and 11). y = 0 is the ground at the spawn address.

## How a place is represented

- Coordinates are local east-north metres from the place's origin (the spawn address's geocode) on
  the WGS84 ellipsoid: x east, z north, y up. /tp and the C key say x east, y north, z height.
- A footprint is cut into at most four rectangles in its own frame; walls go round the outside of
  their union, with the doors cut into them; a pitched roof is a deck with a ridge.
- Roads are road data (Roads.cs) as well as boxes, so traffic can drive them and the driving aids
  know them.
- Yards and roads are named places (named_place), not regions. They are where you are told you
  are; for sound they are the outdoors, which is what they are. As regions, the 6,500 of them took
  20 s to rasterise into the client's acoustic grid. Rooms inside buildings are regions. A yard or a
  road zone is cut back from any building with rooms, because a named place over a room renames it.
- Every entity carries `Tile` (the 250 m square it stands in, counted from the origin, "x,z") and
  `Layer` (ground, roads, drives, paths, verges, yards, zones, structure, rooms, interiors, trees,
  props, rail, water). Roads carry `Tiles`, junctions `Tile`, the map `GeoOrigin` and `TileMetres`.
  The server streams the map by tile from geometry (docs/WORLD_STREAMING.md) and reads `Layer` for
  the coarse level (docs/WORLD_STREAMING.md, Detail layers).

## Detail levels

`--detail=low|medium|high` (default from place.json). `FullDetailMetres` in place.json builds
everything within that distance of the origin as at high.

| | low | medium | high |
|---|---|---|---|
| Roads, junctions, zones, lot names | yes | yes | yes |
| Buildings | shells, one room, front door | shells, one room, front door, slab with a floor | every room, inner doors, back door, furniture |
| Garages, workshops, barns | solid | solid | buildings with a door |
| Lawns | none | front yards | front, back and side yards |
| Verges | none | near the origin | everywhere |
| Trunks | none | one per 60 m of woods (Magnolia), 12 m near the origin | one per 16 m |
| Canopy and wind in the trees | canopy | canopy, one crown per 80 m | the same |
| Traffic | none | from place.json | from place.json |

Entity counts:

| Map | low | medium (shipped) | high |
|---|---|---|---|
| magnolia_tx | 23,301 | 32,598 | 114,975 |
| albany_or | 31,152 | 41,327 | 122,400 |

Load cost of the shipped maps on the development machine, with the city for comparison (city:
7,814 entities, 2.0 MB, server load 2.2 s, acoustic map 0.8 s, Steam Audio scene 0.7 s):

| Map | JSON | Server load | Client acoustic map | Steam Audio scene |
|---|---|---|---|---|
| magnolia_tx | 12.0 MB | 0.7 s | 1.3 s | 4.0 s (23,119 solid boxes) |
| albany_or | 16.0 MB | 1.0 s | 1.9 s | 7.5 s (32,166 solid boxes) |

The scene is built on the acoustic worker, not the game thread.

## Sources and licences

Each place folder has a `SOURCES.txt` written by the download step: where each layer came from, and
its licence.

- OpenStreetMap: ODbL 1.0, (c) OpenStreetMap contributors. The maps are a produced work from it and
  carry the attribution in their description; the extracted inputs under `tools/places` are a
  derived database under the same licence.
- Overture Maps buildings: ODbL 1.0 (OpenStreetMap, Microsoft ML Buildings, USGS lidar).
- Overture Maps places: CDLA-Permissive-2.0.
- US National Address Database (through Overture's addresses theme): public domain.
- US Census TIGER/Line address ranges: public domain.
- ESA WorldCover 2021 v200: CC BY 4.0, (c) ESA WorldCover project 2021 / contains modified
  Copernicus Sentinel data (2021).
- USGS 3D Elevation Program: public domain.

Not used: county appraisal district parcels. Montgomery County's (Texas) licence forbids
redistribution without the district's authorisation; Linn and Benton counties' (Oregon) terms were
not checked.

## Making another place

1. Make `tools/places/NAME/place.json`. Copy one of the two and change:
   - `Id` (the map id) and `Name` (what the maps list says).
   - `Area`: `{"Bbox": [S, W, N, E]}`, `{"Centre": [LAT, LON], "SizeMetres": 3000}`,
     `{"OsmRelation": ID}` (a city's boundary from OpenStreetMap) or `{"Zcta": "77355"}` (a Census
     ZIP code area).
   - `Origin`: the spawn address's geocode (the US Census geocoder gives it). Full detail is built
     round it.
   - `Spawn.Address`: as the street is named on the road ("31907 Bobcat Lane").
   - `CountyFips`: the county or counties, for the Census address ranges.
   - `Archive`: a folder outside the repository for the raw downloads.
   - The place's character: `SpeedMph` (state defaults by road class), `BrickShare`, `Trees`
     (species and spacing), `VergeMetres`, `SidewalkMetres`, `Setting` ("town" names a second big
     building on a lot a building rather than a barn), `Traffic`, `StreetLife`, `Temperature` and
     `Humidity` (offsets from the server's 20 C and 0.5, as on every map).
2. Download, in a venv with `overturemaps shapely rasterio numpy pyshp`:
   `python tools/fetch_place.py download tools/places/NAME/place.json`
   (OpenStreetMap from Overpass, Overture's buildings, addresses, places and water, the WorldCover
   tile window, 3DEP elevation and the TIGER address ranges; anything already there is kept).
3. Prepare: `python tools/fetch_place.py prepare tools/places/NAME/place.json` clips everything to
   the area and writes the small JSON inputs beside place.json. `fetch_place.py elevation` writes
   only elevation.json, from the archive's 3DEP download.
4. Generate (standard library only): `python3 tools/gen_osm.py tools/places/NAME`. It prints the
   counts and the spawn. Commit the inputs and the map together; `RealPlaceMapTests` checks the map
   is what the generator makes from them.
5. Add the spawn's expected name to `RealPlaceMapTests.The_spawn_is_where_the_place_says`.

A whole town is the same steps with a bigger area. Clients are sent only the tiles near them, but
the server still loads the whole map, so keep a map to a few kilometres until stage 2 of
docs/WORLD_STREAMING.md.
