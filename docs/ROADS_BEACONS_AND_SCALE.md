# Roads, beacons, doors and scale

Answers to Cody's list of 2026-09-22, written down before any of it is built. Each section says what
exists today, what is missing, and the shape the missing part should take. Nothing here is
implemented yet; where a claim is about the current code it names the file it was checked in.

---

## 1. The driving line

### What it is now

`RaceLine` is not a racing line in any sense that matters: it is a polyline a vehicle follows, with a
speed law from curvature, a lateral offset, banking, and (since `TrackData.Stops`) places to stop. The
name is historical. A city street, a six-lane highway and the speedway are the same object to it.

What it lacks for a road is **direction and lanes**. A track is one closed loop and everything on it
goes the same way round. Two-way traffic today is two loops laid side by side (`downtown_cw`,
`downtown_ccw`), which works for NPCs and says nothing a player could use.

### What a road should be

A road is data: a centreline, a type, and a lane list.

```
Road    { Id, Type, Centreline[], Lanes[] }
Lane    { Index, Direction (+1 / -1 along the centreline), WidthMetres, SpeedLimitKmh }
Junction{ Position, Connections: (fromRoad, fromLane) -> (toRoad, toLane, Turn: left/straight/right) }
```

Each lane is a **directed** line, derived from the centreline and its offset, so it cannot drift from
the road it belongs to (the same rule as crossings: declare the point, derive the rest). NPC traffic
follows lanes instead of loops, and two-way traffic falls out of lanes having a direction. The
junction table is what answers "which lane am I turning into": a lane knows which lanes it connects
to, so ahead of a junction the game can say *"left lane turns left onto Calder Avenue"*.

### Sonifying it

The same data drives what the player hears, for any vehicle on any road:

| Cue | What it tells you | Source |
|---|---|---|
| **Lane-mark ticks** | Speed: a tick per dash (3 m dash, 9 m gap on a US road), so the rate is your speed | Lane geometry |
| **Tick pan** | How far off the lane centre you are: the ticks come from the painted line, left or right | Offset from the lane line |
| **Edge tones** | Distance to the kerb (one timbre) and to the centre line (a different one), rising as you close | Road edge / opposing lane |
| **Rumble strip** | You have left the lane; a real surface texture, not a beep | Road type declares it |
| **Junction call-out** | Which lane goes where, a set distance before the junction | Junction table |

Two edge timbres, not one, because in a city the two sides of a lane mean different things: the kerb
is a kerb, and the centre line has oncoming traffic on the far side of it. Forza's edge tones assume a
circuit where both edges are the same kind of danger.

The ticks are the important one. A dashed line is already a speedometer and a lane-keeping aid; it
only has to be made audible.

### What is built (2026-09-22)

A first version, working from the asphalt box under the car rather than from road data, which does
not exist yet (`OpenFPS.Common/LaneGuide.cs`, `OpenFPS.Client.Core/DrivingAids.cs`):

- **Dash ticks** from the nearest broken line, one per 12.19 m dash period (the US 10 ft / 30 ft
  pattern), placed on the line beside you and louder the nearer it is. On a road with one lane each
  way, the centre line ticks.
- **Edge tones:** a low triangle (196 Hz) for the kerb and a higher sine (523 Hz) for the centre
  line, starting a metre from the side of the car and rising as you close. Over the line, the note
  wobbles.
- **Driver only:** the server now tells the client whether its seat drives (`RidingControls`).
- A road 3 m per lane, two-way from two lanes up. Junction call-outs wait for the road graph.

Also built for driving: parked cars in the garage (`vehicle:<profile>` shells), steering that turns
at the pace of hands, cab sound through the body, and buses you can board at a stop.

### Aircraft

Real aviation already has audio guidance, and it is the right thing to copy rather than invent:

- **Marker beacons** on the approach: outer 400 Hz dashes, middle 1,300 Hz dot-dash, inner 3,000 Hz
  dots, at fixed distances from the runway threshold.
- **Localiser and glideslope** as a tone: left/right as pan, high/low as pitch, centred as a steady
  tone. This is the audio equivalent of an ILS needle.
- **Altitude call-outs** from the ground ("five hundred, one hundred, fifty, forty, thirty, twenty,
  ten"), which every airliner makes.
- **Stall warning** and **gear warning**, both real sounds.

Taxiing uses the road system: a taxiway is a road with lanes of one.

### Trains

A train cannot be steered, so lane-keeping is not the job. Speed and stopping are:

- Signal aspects ahead as a call-out and a tone.
- Line-speed changes ahead.
- A countdown to the platform stopping mark, which `TrackData.Stops` already carries.

---

## 2. Doors

**Exists:** building doors — `DoorComponent`, open/close on the server, a synthesised latch through
`WorldAudioPlayer`, and a door leaf that sound bends round.

**Missing:** vehicle doors, locks, the key-fob chirp and car alarms.

- **Vehicle doors** are a body panel on a hinge, a latch and a seal, so they share the building door's
  latch model and differ in the panel: thin steel on a car, heavier and longer on a truck, and a
  sliding door on a van or bus (the bus already vents air for its doors).
- **Locks** are a solenoid clunk per door, all at once on central locking.
- **Key fob** chirp and **alarm** are the same piezo disc the bus door chime already is
  (`DoorChimeSpec`: a piezo at its own resonance with a square on it). An alarm is that disc, or a
  siren driver (`ElectronicSiren`), with a pattern.
- They wait on **keys**: an item the player carries that names a vehicle, and a vehicle that knows it
  is locked.

---

## 3. Beacons

**Exists:** `EntityType.Beacon`, `BeaconComponent`, and `chirp_beacon` in `docs/AUTHORING.md`. A
beacon is an entity with an emitter and nothing more.

**Missing:** control over them. The shape:

- A beacon has a **category**: `door`, `exit`, `stairs`, `item`, `vehicle`, `waypoint`.
- The **map** sets a policy per category: `forced on`, `default on`, `default off`, `forbidden`. A
  competitive map can forbid item beacons; a tutorial map can force door beacons on.
- The **player** toggles each category that the map has not locked. That choice lives on the player's
  own machine and never goes to the server, because it changes only what one client plays.
- The sound is a **short blip**, one per category, distinct enough to learn, and placed in the world
  like any other source so it is occluded and localised properly.

Doors are the obvious first category: a blip on the door leaf, heard only when the category is on.

---

## 4. Roads, infrastructure and placement

### Roads as a category

Roads should be a **type catalogue**, not hand-placed boxes. A type sets lanes, lane width, speed
limit, markings, kerb and whether it has a rumble strip:

| Type | Lanes | Limit | Notes |
|---|---|---|---|
| Residential | 1 each way | 40 km/h | No centre line on the quietest |
| Collector | 1-2 each way | 50 km/h | |
| Arterial | 2-3 each way | 60-70 km/h | Signals at junctions |
| Highway | 2-4 each way, divided | 100-120 km/h | Ramps, no junctions, rumble strips |

From the type and a centreline, the generator builds the surface, the kerbs, the lane lines (which
are also the sonification above) and the NPC lanes. **Traffic** is then a set of features a map maker
ticks: NPC vehicles, signals, stop signs, pedestrian crossings, parked cars. Each of those is a
separate system reading the same road data, which is how the level crossing already works — it reads
the tracks and writes to the road, and nothing else has to know about it.

`docs/NEXT_THE_CITY.md` already names "roads as data + a lane follower" as the largest single piece
of the city plan. This section is the design for it.

### Placing things

Maps are JSON today, and the city is generated by `tools/gen_city.py`. That will not scale to other
people building maps. The practical order is:

1. **Placement commands in game**, admin only: stand somewhere, face a direction, `place road
   arterial`, walk, `place road end`. The game knows where you are and which way you face, which is
   exactly what a sighted editor's mouse provides.
2. **Snapping**: to the road grid, to an existing road's end, to a 15-degree heading, so a blind
   author can build straight and joined without measuring.
3. **Read-back**: every placement announces what it touched ("joins Calder Avenue, crosses the rail
   line").

### Buildings at any angle

**Feasible, and most of it already works.** Checked in the code:

- **Collision** transforms the player into each box's own frame (`SharedMovementEngine`), so a rotated
  wall is solid where it is.
- **Occlusion and diffraction** test the rotated box (`GeometryUtils.LineIntersectsOBB`, used in
  `Diffraction.cs`).
- **Acoustic regions** are written into the octree as rotated boxes (`SetRegionOBB`).
- **Ground height** tests the footprint in its own rotation (`PhysicsUtils`).

**The one gap:** the room survey (`CompositeAcoustics`) sizes a rotated piece by the axis-aligned box
that contains it (`AxisAlignedHalfExtents`). A building turned 45 degrees would be surveyed as a
bigger room than it is, so its reverb would be wrong. That needs fixing before angled buildings are
more than props.

Turning about the vertical axis — a building rotated 16 degrees off the street grid — is the case
that matters and is the one above. **Tilting** a building
(a ramp, a sloped roof you walk on) is different: ground height treats a box's top as flat, so
walking on a tilted surface needs its own work.

---

## 5. How big a map can be

### Could a map be 3,000 km wide?

**Not as things stand.** Three limits, in the order they bite:

**Precision.** Positions are 32-bit floats (`System.Numerics.Vector3`), about seven significant
digits. The spacing between representable positions grows with distance from the origin:

| Distance from origin | Smallest step |
|---|---|
| 1 km | 0.06 mm |
| 16 km | 1 mm |
| 130 km | 8 mm |
| 1,000 km | 6 cm |
| 1,500 km (edge of a 3,000 km map) | 12.5 cm |

At the edge, a 15 cm movement tap would snap to a 12.5 cm grid, collision would be coarse, and
anything moving would jitter in position, which Doppler and the dead-reckoning of voices would hear.
The standard fix is a **floating origin**: keep the world centred on the player and shift everything
when they move far enough. It is well understood, but it touches physics, networking and audio.

**Content.** The 1 km² city is 4,874 entities in a 1.3 MB file. A 3,000 km square is nine million
times that area. It would have to be generated and loaded in tiles around each player, never held
whole.

**Simulation.** The client only hears what is near it, so it copes. The server is the limit: it runs
every vehicle, train and aircraft on the map, everywhere, all the time. A map that size needs traffic
that exists only near players, which is the same idea as tiling.

### What is realistic

With the code as it is, **up to about 16 km across** stays at millimetre precision. With a floating
origin, **tens of kilometres** is reasonable. Anything bigger means tiling, streaming and simulating
only near players, which is a project in its own right.

---

## 6. Coordinates: what the player hears and types

**Done 2026-09-22.** Players read and type **x east-west, y north-south, z height**: pressing C says
"150.0, 27.0, 0.1", and `/tp 150 27 0.1` puts you back there. The swap lives in one place,
`OpenFPS.Common/PlayerCoordinates.cs`, and everything that speaks a position (C, `/tp`'s reply,
`/spawn`'s reply) or reads one from a player (`/tp`, `/move`) goes through it.

**Inside the engine nothing changed:** +X east, +Y up, +Z north. That is FMOD's native layout
(left-handed, +X right, +Y up, +Z forward), and `FmodAudioProvider` relies on it — setting FMOD's
right-handed flag was tried once and swapped left and right. Maps, prefabs and code keep the engine
order; a map file's `"Y"` is still height.

Both conventions are mainstream — Y-up in Unity, Minecraft, FMOD, Direct3D, OpenGL, Godot; Z-up in
Unreal, Blender, 3ds Max, Source, maths and surveying — so the choice is about the audience. Players
think of the ground as a map, and a map is x and y.

Waypoint lists in `docs/` were rewritten into the player order when the change was made.
