# Next: a 10 x 10 km city, shapes other than boxes, and building maps in the game

Cody's description of 2026-09-28, with what each part needs. Nothing here is built yet. The roads and
bodies plan ([NEXT_BODIES_WHEELS_ROADS.md](NEXT_BODIES_WHEELS_ROADS.md)) comes first: this city is
made of its roads, junctions, crossings and signals.

## The layout

The city is 10 km on a side. Main Street runs north-south through the middle and splits it into a
west end and an east end. Center city is in the middle, around Main Street.

**Main Street** is a six-lane highway, three lanes each way, with a sidewalk on each side.

**West end: the nice side.**
- Big two-storey single-family houses, two cars in each driveway, a few cars parked on the street.
- From the road outward: kerb, a 1 m grass strip, the sidewalk, then yards, driveways and trees.
- One or two good apartment buildings.
- Small shops, residential roads and stores, with cars pulling in and out of parking lots.
- Buildings: drug store, music shop, library, post office, bank, restaurant, barber shop, hair
  salon, gym and laundromat.
- Schools: elementary, middle and high schools, and a university campus. Put them where they fit;
  there can be more than one of each.
- A hospital with ambulances, and a fire station with fire trucks.
- Two or three buses on a loop. The loop goes round the west end, crosses Main Street, goes round
  the east end and comes back. School buses as well.

**East end: rougher.**
- A few high-rises and smaller run-down houses, with a couple of cars parked at the roadside.
- A trailer park.
- Potholes, cracked sidewalks and patched asphalt. These are road surface data (the Surface records
  in stage 1) and sidewalk surface data, not geometry.
- Most of the crime is here: gangs, car theft, dealing. The lines are in
  `~/npc-lines-2026-09-28/characters.py`.

**Center city.**
- High-rise offices with elevators. The elevator is a machine model, with its signals from
  `inbox/elevator sounds`.
- The subway's central stations.

**Across the city.**
- A subway: underground, fast, stopping at stations. It replaces the surface light rail as the way
  to get round the city quickly. Tunnels and platforms are the acoustic interest.
- A helipad with a helicopter that can fly. The aircraft model already does rotor blades.
- An airport: terminal, drop-off, baggage, gates, boarding, hangars and aircraft. Aeroplanes only
  for now.
- A park with walking paths, birds (BirdLife picks species from habitat), a fountain or waterfall
  and dogs.
- Street lights. They make no sound (LED) or a faint hum (sodium). They are there for the day there
  is a picture.
- Cell phone towers, for the phone model's coverage.
- Fire hydrants, benches, bins, bus shelters and manhole covers. Each is a body and a material.
- Empty land is grass fields until something meaningful goes there.

## What a city this size needs from the engine

The current city is about one square kilometre. A 10 x 10 km city is 100 times the area.

- **Generation.** `tools/gen_city.py` lays the city today, and the roads are data since stage 1. A
  city this size needs the generator to work from district rules (lot sizes, house types, what a
  block holds), not from placing each box by hand.
- **Loading.** The client receives every entity on connect ("Receiving entities"). At 100 times the
  area that is too much. The map has to be split into tiles, and each client loads the tiles within
  earshot. The spatial grid already limits what the server sends by distance.
- **Sound budget.** Hundreds of cars and walkers are heard at once only if distant sources merge
  into one extended source. This is aggregation, already on the list under Later, Acoustics.
- **Steam Audio scene.** One static mesh per map today. It becomes one per tile, loaded with the
  tile.
- **Server tick.** 310 walkers cost 1.9 ms a tick. A full city's people and traffic need to be
  simulated in less detail far from any player: the same rules, stepped less often.

## Shapes other than boxes (triangles)

Today every entity is a box, which can be rotated. The collider types Sphere, Cylinder, Cone and
Polygon are declared in `Components.cs` but collide as boxes, and Steam Audio ignores them. Ramps,
slopes, stairs, round columns and curved kerbs are not possible.

What triangle-built entities need, largest first:

1. **Collision.** Player movement against triangles, with step-up
   (`SharedMovementEngine.cs`, `GeometryUtils`). Large.
2. **The hand-built acoustic models.** Enclosure, EarlyReflections, ImageSource, Diffraction and
   TrackClearance all take boxes. They need one shared triangle store with a ray query (a BVH).
   Large.
3. **Ground height.** A downward ray against triangles. This is what gives real ramps and slopes.
   About ten callers. Medium.
4. **Raycasts and occlusion** in SpatialService: a mesh branch and the same BVH. Medium to large.
5. **Map and prefab format:** a mesh reference or a library of shapes, plus the validator. Medium.
6. **Wire format:** mesh data sent once per map, with a mesh id on the collider, appended so older
   components still read. Medium.
7. **Materials per triangle** for footsteps, occlusion and tracers. Medium.
8. **Steam Audio.** It already takes triangles with a material each; today it is fed 12 per box.
   Small to medium.
9. **Server surveys and composites:** the mesh's bounding box is enough at first. Small to medium.
10. **Generator and tools:** `mesh()`, `ramp()` and `stairs()` in the generators, or an importer
    for OBJ or glTF files; `/spawn`; the authoring guide. Medium.
11. **Tests** that assume boxes. Medium.

In all, general triangle meshes are the largest single piece of engine work so far, several
sessions. A cheaper first step covers most of what the city needs: one ramp (wedge) shape and
stairs built from box steps. That touches only collision, ground height and Steam Audio, and is a
medium piece of work. Full meshes come after the city is laid, when round and curved things are
wanted.

## 3D graphics, later

The world is already geometry with materials, positions and moving bodies, and the server does not
care whether a client draws it. A client that draws boxes in flat colours is small work. Realistic
graphics are large work: meshes (the triangle work above first), textures, lighting, animated
people and vehicles, and a renderer, probably an existing engine used as the client's picture while
the audio stays ours. None of it changes the sound or the server.

## Joined maps and phone coverage

Today maps are separate worlds, and `/join` moves a player between them. For towers on one map to
cover the next, the maps have to share one coordinate space: a world made of tiles laid side by
side, which is the same change the 10 km city needs for loading. Then coverage is simply a
property of the world. Each tower has a position, height and power. Signal at a point falls off with
distance and is blocked by buildings and terrain, using the same geometry the sound uses. A tower
near a map's edge covers the neighbouring tile because it is near it, with no special case for the
edge. A phone call can then break up in a basement, in the subway or far from a tower.

## Building a map in the game (discussed 2026-09-28)

Decided by Cody:

- **Who builds.** Admins have full permissions. A player can create their own maps but cannot
  publish them. A published map is public. The owner of a map can make other players editors of it.
- **A blank map** is a single slab of grass and nothing else. The owner builds on it and extends it
  as far as they like.
- **Placing by coordinates.** Anything can be placed at exact coordinates as well as relative to
  where you stand.

How building works, proposed:

- **Commands typed in the chat line**, the same box `/tp` uses; there is no voice input. "Speaking"
  in the first draft meant the builder answers in speech, not that you talk to it. Two ways to say
  where:
  - relative to you: `/build road 120 north` lays a road from where you stand, 120 m north;
  - by coordinates: `/place house 340 -120 facing north` or `/build road from 0 0 to 0 500`, using
    the same x (east), y (north), z (height) that `/tp` and the C key use.
- **Things, not boxes.** A road brings its kerbs, sidewalks, lanes and junctions; a house type fills
  its lot; a building has a number of floors; a bus route runs through named stops. The builder
  uses the same rules as the city generator, so a hand-built map and a generated one are the same
  kind of thing. A raw box can still be placed for anything the library lacks.
- **Answers in speech.** Each command says what it made and what it joined: "Road, 120 m north,
  joins Main Street at a new junction." One key undoes the last change.
- **A build menu** on an F key lists what can be placed, for when you do not remember the name.
- **Extending** the slab: `/extend north 500` adds 500 m of grass on the north edge. Internally the
  map is kept in tiles so a large map loads by the part you are in, but the owner never has to think
  about tiles.
- **Templates are maps.** A copy of the city or of a small town is a starting point like the blank
  slab.
- **A map's own sounds** travel with it (todo.md, "A map's own sounds").

Still to decide: whether editors build live while other players are on the map or in a separate
edit copy that is published when ready.
