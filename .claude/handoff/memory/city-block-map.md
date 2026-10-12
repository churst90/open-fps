---
name: city-block-map
description: The city map is a whole city since session 17 — ./run-server.sh city; generated, nothing acoustic authored
metadata:
  type: project
---

`OpenFPS.Server/maps/city.json`, rebuilt as a CITY 2026-09-19 (session 17). `./run-server.sh city`.

**4,874 entities, 584 named places, 396 doors, 64 machines, about a square kilometre.** Five towers of
6-10 storeys over a grid of three avenues and four cross streets with real intersections; a three-deck
open-deck garage (spandrels on piers, NOT walls — as walls it measured a nine-second tail); a brick
plaza; a 100 m tunnel under Main Street; a 2.5 km light rail loop with three stations and a level
crossing; an airport with a 588 m runway, taxiway, apron, glazed terminal and a steel hangar; an
estate of 64 houses with gardens, hedges and mowers. 22 vehicles on five routes, five aircraft.
Spawn is in the carriageway at `0 0.1 -40` facing north. Read `docs/THE_CITY.md` for what each place
measures.

**Generated, not typed** — `tools/gen_city.py`; the dimensions at the top are the source and the map
is an output, so regenerate after changing one. It reads the prefab files for their collider sizes so
it cannot drift from them.

**Nothing on it authors an acoustic anything** — see [[regions-measure-themselves]]. Four materials
were added for it: Brick, Asphalt, Tile, Foliage.

Two traps paid for while building it: a door prefab **is already a portal** (`IsDoor` attaches one),
so a doorway wants a door carrying `RegionAId`/`RegionBId` and NOT a door plus a portal; and a door
leaf needs a **gap cut out of the wall** — the first draft had steel doors standing against unbroken
brick with no way into either building.

**Four faults found while building it, none of them a model fault — all geometry, and each found by a
different instrument.** `EveryShippedTrackIsDriveable` caught two: the estate traffic loop ran through
houses (there is a second lane now, and no plot may stand within 13 m of one), and the rail loop ran
through the tunnel walls (the line crosses north of the portal, on the flat, which is a LEVEL
CROSSING). `--enclosure` caught two more: the garage built as three solid walls and a lid measured a
**9.2 s** tail, and an open-deck car park is open by law — spandrels on piers, 5.9 s; and a bungalow
rang at 827 ms over a **2.1 s bottom** until it was lined with plaster, which is the membrane brick
is not (853/616/451 after). And reading the JSON caught a fifth: the level crossing was a 62 cm box
across Main Street, 22 cm over StepHeight — a wall, not a crossing.

Also fixed by walking the generator rather than the map: every house had its front door in the back
garden, because the front wall was built at the same end of the plot whichever way the house faced.

**Still to do:** walk it and listen. Every number is measured and none of it is heard.
**How to apply:** read `docs/THE_CITY_BLOCK.md`; drive it with [[mud-walkthrough]] on a spare port.
