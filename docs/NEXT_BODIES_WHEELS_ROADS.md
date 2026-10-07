# Bodies, wheels and roads

Cody's request of 2026-09-27: every body on a map has a physical body; nothing physical is hard-coded
in a model or a map; per-wheel vehicle physics; roads that carry their surface as data; and, as the
natural extension, traffic lights and accessible pedestrian signals.

This builds on the road design in [ROADS_BEACONS_AND_SCALE.md](ROADS_BEACONS_AND_SCALE.md) (sections 1
and 4), which was written on 2026-09-22 and never built. Each stage below says what exists today,
what it adds, and how it is checked. Numbers marked *to confirm* are taken from their source before
they go in the code.

**Status (2026-10-07).** Stage 1 (roads as data, following, junctions, crossings) built. Stage 3
(per-wheel physics) built and merged 2026-10-01. Stage 4 partly built: squeal per wheel; since
2026-10-06 wet tyre noise per wheel from its own water (docs/WET_ROADS.md), and inside a vehicle each
wheel heard through its own arch (CabinPaths); dry rolling noise outside still per axle. Stage 2 (physical bodies for traffic and people) and stage 5 (traffic lights and
accessible pedestrian signals) not started. The bodies left when somebody is killed
(`OpenFPS.Server/Core/Bodies.cs`) are a different thing, an item, not stage 2. "What exists today"
below is as of 2026-09-27.

## What exists today

- **Roads** are geometry only: `asphalt_road` boxes 5 cm tall and `concrete_floor` footways, laid by
  `tools/gen_city.py`. The server has no road network. Traffic follows closed loops (`TRACKS`), two
  laid side by side for two-way traffic; a vehicle's lane is its `LaneOffsetMetres`.
- **Junctions** are unnamed square asphalt boxes. Give-way is a fixed 3.5 s stop at a fixed point on
  the lap, with no look at cross traffic. Vehicles never react to the vehicle ahead.
- **No traffic lights, no crossings.** Walkers go straight across junction boxes on the line of the
  pavement. Drivers check only players on foot in the road, not other pedestrians.
- **Surface**: nothing reads the material under a wheel. The tyre sound uses a fixed profile per
  preset (`SportsOnAsphalt`, `TruckOnAsphalt`).
- **Vehicle motion** on the server is one-dimensional along the loop: speed from the curvature limit,
  heading from the tangent. No yaw rate, no slip, no load transfer. Player-driven cars have a bicycle
  model (`DrivingSystem`).
- **Tyres**: two tyre voices per vehicle (one per axle), sharing one slip number. The tread tone
  uses a fixed 0.337 m wheel radius (`VehicleSynth.cs:332`), not the preset's.
- **Bodies**: traffic is one solid box with no material or mass. Parked cars and the stopping bus
  are built of steel and glass panels (`VehicleCabin.Shell`). Pedestrians are not solid, have no
  material and cannot be bumped into. Moving bodies are not in the acoustic scene; `VehicleShadow`
  adds a diffraction loss over them and nothing else.
- **Presets** are C# records (`OpenFPS.Common/Vehicles.cs`). They declare mass, axle positions,
  wheel radius, size and tyre count. They do not declare track width, centre of gravity height or
  tyre width.

## Hard-coded physical values to move into data

From the survey of 2026-09-27 (line numbers as of then). Each becomes a field on the preset or the map, with today's value as
the default so nothing changes until a preset says otherwise.

| Where | Value | Becomes |
|---|---|---|
| `VehicleSynth.cs:332` | wheel radius 0.337 m | the preset's tyre size (done, stage 3) |
| `EngineProcessor.cs:545` | front tyres = 1 if 2 or fewer | the preset's axle list (done, stage 3) |
| `EngineProcessor.cs:526` | drum brakes above 5 t | the preset's brake type (done, stage 3) |
| `DrivingSystem.cs` | steering lock 0.61 rad, ground clearance 0.35 m, engine braking 0.15 | preset fields (lock done, stage 3) |
| `DrivingAids.cs:203` | wheelbase fallback 2.6 m | the preset's axles (done, stage 3) |
| `VehicleSystem.cs:183-184` | machine hull 0.6 x 1.0 x 0.9, walker 0.5 x 1.8 x 0.5 | the body's own data |
| `VehicleSystem.cs:196-197, 300, 308` | default accel/brake, speeds, turn time | preset or map fields |
| `VehicleSystem.Parking.cs:69, 323` | walk speed 1.35 m/s, driver body 0.5 x 1.75 x 0.35 | one person definition |
| `VehicleShell.cs`, `VehicleCabin.cs` | seat pitch, skin, belt line, overhang split | preset fields |
| `VehicleSystem.Drivers.cs:26-41` | driver's window offset, in-the-way width | preset and person fields |
| `gen_city.py:1288-1315` | grip and brake/accel shares per class | the preset (tyre and brakes) |
| "walker" string check (`VehicleSystem.cs:162`) | | a person preset |

Presets themselves move to JSON (the `OpenFPS.Server/machines/` overlay already exists for one
machine), so a new vehicle or person is a file, not code.

## Stage 1: roads as data

Progress: the network as data is built (2026-09-27): `OpenFPS.Common/Roads.cs`, written by
`gen_city.py`, loaded and checked by `MapManager`, tested by `RoadNetworkTests`. Car following is in
(`VehicleSystem.Following.cs`, `CarFollowingTests`). Traffic drives the lanes (`LaneRoutes.cs`,
`VehicleSystem.Routes.cs`, `RouteTrafficTests`): seeded wanders and fixed routes, give-way lines from
junction priority, road stops, level crossings, following by lane. Gap acceptance at junctions is in
(`VehicleSystem.Junctions.cs`, `CarFollowingTests.No_two_vehicles_meet_inside_a_junction`). Lane rules
(right turns from the kerb lane, left from the inner one) wait for lane changes along a block: without
them tours ran into dead ends. Pedestrians cross (2026-09-28, `VehicleSystem.Crosswalks.cs`,
`CrosswalkTests`): a crossing is wherever a walker's line passes over a carriageway, found at load;
walkers wait at the kerb for the HCM pedestrian gap (walk time plus 3 s start-up), drivers stop for
anybody on a crossing and, arriving at a junction, for somebody who has waited 8 s; after 30 s a
walker takes a gap only as long as the walk. Two faults this exposed in the junction logic are fixed:
a vehicle held near the line could be taken as already inside it (the smoothed line drifts from the
lanes), and "everybody is waiting, one goes" could pull out in front of an arriving car. Next:
stage 2, bodies.

A road network the server knows: roads, lanes, junctions, crossings and surfaces.

```
Road       { Id, Name, Type, Centreline[], Lanes[], Surface[] }
Lane       { Index, Direction (+1/-1 along the centreline), WidthMetres, SpeedLimitKmh }
Junction   { Id, Position, Approaches[], Connections (road, lane) -> (road, lane, turn), Control }
Crossing   { Road, AtMetres, WidthMetres, Kind (zebra / signalled), Signal? }
Surface    { FromMetres, ToMetres, Material, Texture (joints, cobbles, manhole covers, lines) }
```

- The generator writes this, and builds the asphalt, kerbs and lane lines from it, so the geometry
  cannot drift from the data (the rule the level crossings already follow).
- NPC traffic follows lanes through junctions instead of loops. Two-way traffic falls out of lanes
  having a direction.
- Car following: a vehicle keeps a gap to the one ahead in its lane (a standard car-following
  model, *to confirm*: the Intelligent Driver Model, Treiber et al. 2000).
- Give-way becomes gap acceptance at the junction, not a timer.
- Pedestrians walk footways and cross at crossings.
- Checked by tests on the generated city: every lane connects, no vehicle stops for ever, no two
  vehicles overlap, no pedestrian walks through a solid body (the test added 2026-09-27 stays).

## Stage 2: a physical body for everything

- Traffic cars are built from their panels, as parked cars already are (steel, glass, carpet), with
  the preset's mass.
- People are a person definition: a body of a size and mass, soft (clothing and flesh absorb, *to
  confirm* the absorption of a standing person, around 0.5 m² Sabine per person in the literature),
  and solid to walk into. The apology comes from contact, not distance.
- Moving bodies go into the acoustic scene. Steam Audio supports moving geometry (instanced meshes
  with a transform per frame, *to confirm* the cost for a few hundred bodies). Then a bus blocks, a
  car's glass lets some sound through and its side reflects, from the same material data the walls
  use. `VehicleShadow` retires once this is measured to do the same job.
- Checked by: a bus between the listener and a source loses what its size and material say; a
  person standing in a doorway takes a little off a room's answer; the existing occlusion tests
  still pass.

## Stage 3: per-wheel physics

Progress (2026-10-01): built. Every preset declares its running gear (`OpenFPS.Common/RunningGear.cs`,
one real vehicle each, sources in the comments): axles with track, tyre size code, driven, steered
and brake type, the front weight share, centre of gravity height, yaw inertia index and a lock from
the published turning circle. Where no figure is published a measured vehicle of the same class
stands in (mostly Heydinger et al. 1999, the NHTSA inertial measurements), and a few are marked to
confirm (the bus and truck centre of gravity heights, the NASCAR and F1 weight splits). The wheel model
is `OpenFPS.Common/WheelDynamics.cs`: planar body, per-wheel loads with longitudinal and lateral
transfer (lateral split by axle load: no roll stiffness is published for these vehicles), the Magic
Formula with the 205/60R15 coefficients from Pacejka's book, load sensitivity, a friction circle for
combined slip, and wheel speed from the rolling radius. Traffic on the roads is steered by a driver
(`LineFollower.cs`, the Stanley law with curvature and sideslip fed forward) and its body moves under
its tyres; its speed still comes from the old logic, now also held to the bends the line really makes
and to what keeps its tyres below the squeal onset (`WheelDynamics.SteadyTurnSpeed`). Racers on a
track stay held to their line: their grip figures (3.1 g slicks, 2.6 g for the formula car) stand in
for banking and downforce the model does not have, so their wheels report what holding the line asks
(`WheelDynamics.Hold`, banking included) and their tyre demand is the line's, as before. The player's
car runs on the same model. Each wheel goes to the client (`EntityState.Wheels`); the tyre voices take
their tread tone from the real rolling radius and share the squeal between the axles by their worst
wheels. Checked by `WheelDynamicsTests` and `TrafficWheelsTests`. Open: turning paths on the 7 m
estate roads still put a body corner over the kerb, and it is the route geometry (a body held exactly
on its line does it too); the lane connectors through a junction need a kerb-aware radius. Next:
stage 4.

On the server, for every vehicle, from the preset:

- Axles with a track width, tyre size (width, aspect, rim, so 205/55R16 gives the rolling radius),
  and which wheels drive and steer. Centre of gravity height and weight distribution.
- Each wheel's load from static weight plus longitudinal and lateral load transfer (braking moves
  weight forward, a corner moves it outward), each wheel's slip, and each wheel's speed.
- Tyre force from a published tyre model (*to confirm*: Pacejka's Magic Formula with generic
  passenger-car coefficients).
- Traffic gets lateral dynamics (yaw rate, slip angle) instead of a heading taken from the tangent.
  Player-driven cars share the same model, so a traffic car and your car behave alike.
- Sent to clients per wheel: load, slip and the surface under it (appended to the wire format, see
  the component rule).
- Checked by: at rest, loads sum to the weight; hard braking moves load to the front by
  m·a·h/wheelbase; the outside front wheel reaches the friction limit first in a steady corner.

## Stage 4: four tyre sources

Progress (2026-10-01): the squeal is per wheel. Each wheel's squeal (`VehicleSynth.WheelSqueal`)
runs from that wheel's own demand, slip velocity (u sqrt(kappa^2 + tan^2 alpha)) and load, at a level
from the frictional power of the sliding part of the patch (brush model, Pacejka 2006 section 3.2),
on the tyre's stick-slip resonance as before. It goes out through the engine voice's tap at its end of
the vehicle, weighted by the listener's distance from the wheel against the tap's, so four tyres cost
no extra voice. Not done: rolling noise per wheel (still per axle), left and right as separate
directions (only the level differs), surface data and joints. Found on the way: in a steady turn the
model's light inside front passes its peak slip angle before the loaded outside one (peak slip angle
rises with load), so it starts to sing first, quietly; whether that sounds right is for the ear.
Checked by `WheelSquealTests`; `--wheel-squeal` renders the listening set.

Also fixed on the way: traffic on its own tyres skidded at junctions. Drivers now corner at the Green
Book's comfortable side friction (`DriverSteering.ComfortSideFriction`), plan their braking with it
(`RaceLine.BendSpeedWithin`, braking room read at the tighter end of each stretch), and follow with the
ACC model. Checked by `TrafficTyreDemandTests`; `TrafficBrakingProbe` measures the city's traffic.

- A tyre source at each wheel, played through the multi-tap voice the trains use (one voice in
  the budget, a tap per wheel).
- Each wheel's rolling noise from its speed, load and the surface under it (*to confirm*: tyre-road
  noise levels by surface from close-proximity (CPX, ISO 11819-2) measurements, which rank dense
  asphalt, stone mastic, concrete and cobbles).
- Squeal and slide per wheel from its own slip, so the loaded outside front squeals first.
- Surface texture as events: a joint or a manhole cover is struck by the front axle, then the rear
  one a wheelbase later.
- The tread tone from each wheel's own rolling radius.
- Checked by the lab: a pass-by over a joint shows two thumps spaced by wheelbase / speed; the
  level ranks by surface as the CPX data does.

## Stage 5: traffic lights and accessible pedestrian signals

- A signal controller per signalled junction: phases, green, yellow (amber) and all-red times, and
  pedestrian walk and clearance intervals (*to confirm* from the MUTCD, chapters 4D and 4E: yellow
  change interval, pedestrian clearance at a walking speed of 3.5 ft/s).
- Drivers obey: stop on red, decide on yellow by whether they can stop, go on green. The "It's
  green! Go!" lines recorded for the drivers get used.
- Accessible pedestrian signals at signalled crossings (*to confirm* from MUTCD section 4E.09-4E.13
  and the US Access Board's guidance):
  - a pushbutton locator tone, repeating once a second, audible only close to the button;
  - a walk indication: a rapid percussive tick or a speech message naming the street;
  - volume that follows the ambient noise within set limits;
  - a vibrotactile arrow, which in a game becomes a controller rumble or a spoken direction.
- Pedestrians wait for the walk signal. The player can press the button (interact).
- Checked by: no two conflicting movements are green at once; every pedestrian phase gives time to
  cross at the standard walking speed; the APS tones meet the source's levels and rates.

## Order and why

1. Roads as data first: everything after it reads it (lanes, junctions, surfaces, crossings,
   signals).
2. Bodies next: they need no roads, and they change what everything else sounds like.
3. Per-wheel physics, then the four tyre sources, which need the surface data from stage 1.
4. Signals and APS last: they need junctions, crossings, car following and pedestrians that cross.

The city is regenerated at stage 1. A city redesign is planned anyway; stage 1 is where it starts.
