# The physics boundary

A survey and a plan for separating open-fps's physics into its own project, `OpenFPS.Physics`. No engine
code was changed. Asked by Cody on 2026-10-11 ("all parts of this game engine need to be separated in their
own packages and all work cohesively together"), with three effects to be built on it later: a passing bus
pushing air at your ears and through the leaves, leaf litter on the ground, and impacts that pass on
momentum so a struck person or thing is thrown and lands. This replaces the 2026-10-06 decision that vehicle
physics lives in Sound (docs/SOUND_LIBRARY_BOUNDARY.md, decision 2).

- Surveyed at main `0228374a`, 2026-10-11.
- The counts come from `tools/physics_boundary/run.sh`: the sound boundary's Roslyn reader
  (`tools/sound_boundary/Program.cs`, unchanged) and a classifier of its own
  (`tools/physics_boundary/classify.py`). It writes `physics_tables.md` and `physics_crossings.tsv`; the
  crossings as surveyed are checked in beside the tool. Run it again after any move.
- The model is docs/SOUND_LIBRARY_BOUNDARY.md: survey, guards, then stages that each leave the game
  working.

Where it stands (2026-10-11): surveyed, and Cody's decisions recorded the same day (the last section: all ten
as recommended, with soft bodies wanted as well as rigid ones, gravity a property of the world, and the engine
Jolt Physics through JoltPhysicsSharp). Stage 0, the guards, is under way. Stage 4 (rigid and soft bodies)
goes with geometry stage 6 (docs/GEOMETRY.md section 7). In todo.md's order, agreed 2026-10-11, the physics
stages come after the special-cases audit's parts.

## Contents

1. Summary
2. Method
3. Physics today
4. The boundary
5. What it must be able to hold next
6. What the physics is missing
7. The stages
8. How it fits with geometry stage 6 and library stages 5 to 7
9. Enforcement
10. Risks
11. Decisions for Cody
12. What was not measured
13. Appendices: the inventory, the crossings, the presets' members
14. Stage 0 as built (2026-10-11): the guards, the Jolt trial, other machines
15. Stage 1 as built (2026-10-12): the project, the crossings, the moves

---

## 1. Summary

- Physics is in five projects today. Player movement and the ground probes are in Common
  (`SharedMovementEngine`, `PhysicsUtils`, `PhysicsConstants`); vehicles, tyres, projectiles and the wind are
  in Sound (`WheelDynamics`, `ChassisSpec`, `RunningGear`, `ExternalBallistics`, `WindField`); contact
  queries are in Geometry (`SolidContact`, `WheelRays`, `TriangleWorld`); the stepping is in the server
  (`MovementSystem`, `DrivingSystem`, `VehicleSystem`, `RailSystem`); prediction is in the client
  (`ClientPhysicsSystem`).
- 69 types were sorted (appendix A). 21 of them, 2,798 lines, move to Physics whole (14 types, 2,553 lines)
  or as values both sides read (7 types, 245 lines). 10 types, 4,685 lines, split: the mechanics go, the
  rest stays (the vehicle and engine presets are most of it). 9 stay in Geometry and Physics calls them;
  15 stay in Sound; 14 stay in the host and step Physics.
- What moves whole is nearly clean already: **62 references from 5 files** cross out of it, all four kinds
  cheap: the editor's `TunableAttribute` (43), `WeaponDefinition` in the ballistics (12), the `ColliderShape`
  enum in `Components.cs` (5), and the wet-road grip law `RoadWaterLaw` (2).
- The presets mix mechanics and sound in one record. Of `VehicleProfile`'s 47 members that anything reads,
  20 are read by both the mechanics and the sound and 22 by the sound only. Of `EngineProfile`'s 87, the
  mechanics read 5 (idle, redline, peak torque and its rpm, the name); the sound reads 84.
- Sound reads the mechanics in 807 places in 29 files of `OpenFPS.Sound`, and the client's audio in 409
  places in 28 files. Most of these are preset reads; the live state it reads is the wheels on the wire
  (`WheelState`), the speed, and its own re-derivation of everything else.
- The car is simulated twice. The server steps `WheelDynamics` with a torque curve that its own comment calls
  "not a dyno sheet" and shifts instantly (`DrivingSystem.cs:453-477`). The client's sound runs a second
  drivetrain at the audio rate, with a clutch, its own driver and its own brakes (`Driveline.cs:14`,
  `VirtualDriver`), following the server's speed. The gear rule is "written twice on purpose"
  (`DrivingSystem.cs:448-452`).
- The laws are duplicated. Gravity is written 40 times as 9.81 in 26 files and 4 times as 9.80665 in 4
  files; the map's own gravity (`ZoneComponent.Gravity`) moves players and nothing else. Air density is
  1.225, 1.21 and 1.2 in at least eleven places. There are two brake laws for one car, two rolling-resistance
  laws, and two wind laws (bullets feel `WindModel`, everything heard feels `WindField`).
- There are no rigid bodies. No type in the repository has an angular velocity or an inertia tensor.
  `PhysicsPropertyComponent` (mass, friction, restitution, drag) is authored by prefabs and sent on the wire,
  and nothing reads it (`Components.cs:406`, `EntityDefinitionFactory.cs:22`). A car that hits something
  stops dead (`DrivingSystem.cs:387-396`); a person who dies falls from two fixed heights on a timer
  (`CombatService.cs:1247`); a dropped thing lands where it is put at sqrt(2gh) (`HandsService.cs:402-424`).
- Proposed: `OpenFPS.Physics` on Geometry only; Sound references Physics for its state types and never
  steps it; the server and the client step it. Seven stages, 20 to 30 sessions, each guarded by a ratchet,
  a movement replay built from `--walk-pair`, a driving replay and the existing tests. The order: the
  audit's voice registry, then physics stages 0 to 3, then stage 4 (rigid bodies) together with geometry
  stage 6, so the body shapes are made once for collision and for the acoustic scene.

---

## 2. Method

The sound boundary's reader parses every `.cs` file of the eleven projects, binds them, and writes every
type, every reference from one repository type to another (file, line, member), every member, and every
static that can change. It is reused unchanged. Built with `tools/build-local.sh` on tmpfs; nothing in the
repository was built.

`tools/physics_boundary/classify.py` holds the sorting in one table (`RULES`): each inventory type, its
group and a note. From the reader's tables it writes:

- the inventory: each type's file, line span, callers by project, and every reference it makes to a host,
  sound or acoustics type;
- the crossings: every reference from a type bound for Physics whole (P, V) to anything but Geometry and
  Physics;
- for the five presets, every member and who reads it, mechanics or sound;
- who references what moves, by project;
- the statics in what moves.

Groups:

| Group | Meaning |
|---|---|
| P | moves to Physics whole |
| PS | splits: the mechanics move, the rest stays |
| V | a value both sides need; lives in Physics, the host sends it, Sound reads it |
| G | stays in Geometry; Physics calls it |
| S | stays in Sound or Acoustics, reads physical state |
| H | stays in the host; steps Physics or adapts the world to it |

What the reader cannot see: references by string (sound-id prefixes, JSON preset names), behaviour passed in
as delegates, and which part of a split type a reference uses (it counts by type; the preset split is by
member, from the member edges). Lines are declaration spans with comments; a partial type counts all its
parts (`VehicleSystem` is 2,679 lines over its ten files). The reader binds Server, Client and Tests with
some errors where a package or WinForms is missing; references to repository types still bind.

Gravity, air density and step height were counted with `grep` over the six engine projects, doc-comment
lines left out.

---

## 3. Physics today

Appendix A has every type with its callers and host reads. This section says what each part does and
where it is a shortcut. Coordinates in the engine are x east, y up, z north (memory: player coordinates
put the height last on `/tp`).

### 3.1 Common: the body on foot

**`SharedMovementEngine`** (`OpenFPS.Common/SharedMovementEngine.cs:12`, 540 lines). One step of a body on
foot, stateless, shared by the server and the client so that prediction agrees. Called by the server
(`MovementSystem.cs:234`, 48 references), the client (`ClientPhysicsSystem.cs:175`, 57), and the lab and tests
(563). It reads one host type, `ColliderShape` (`StandingRotation`, :549-550).

- Ground snap and step-down (:277-292), gravity (:296), a fall that would pass the floor this tick stops on it
  (:304-308).
- Horizontal velocity is set to the input direction times the speed, on the ground and in the air
  (:314-316). There is no acceleration, no momentum and full air control: a body that jumps and lets go of
  the keys stops in mid-air horizontally. The comment at :311 ("a body in the air keeps the speed it has")
  refers to the speed parameter, not the velocity.
- Collide and slide, three passes, deepest contact first, with a box path (cylinder) and a triangle path
  (capsule: the cylinder's foot and trunk under a rounded head, docs/GEOMETRY.md 10.4) (:340-430).
- Step climbing teleports the body up by `StepHeight` (0.4 m) when the step fits (:377-399).
- A step that leaves the body deeper in anything is undone (:436-444); the map's edges and unbuilt tiles
  are walls (:448-460).
- `GradeAlong` and `GradeSpeed` (:181-212): walking speed on stairs and ramps from Fruin, approved by ear.
- `GatherSolids` (:216-254) sorts the solids by owner so the server and a client meet them in the same order.

**`PhysicsUtils`** (`OpenFPS.Common/PhysicsUtils.cs:9`, 325 lines). The ground probes: `GetGroundHeight` for
the server (`World`, `SpatialGrid<Entity>`, :24-128) and for the client (`WorldSnapshot`, :180-230), and
`FootOnFloor` (:239-278), where a foot goes down. Each has a box path and a triangle path
(`TriangleGeometry.Enabled`, three times in this file). Reads 69 host references: `WorldSnapshot` 18,
`ColliderComponent` 15, `EntitySnapshot` 12, `Transform` 11, `EntityDefinition` 8, `MaterialComponent` 5.
Called by the server 44 times from 17 files (spawns, teleports, drops, bodies, the editor, `StandingSpots`).

- The step height is written again as a local 0.4 (:34, :182) beside `PhysicsConstants.StepHeight`.
- The server's probe counts moving things (`CollectDynamicInRadius`, :88); the client's does not
  ("nothing that moves", :186-187). See 3.7.
- `GroundMaterial` returns its input (:106; also in todo.md's housekeeping finds).

**`PhysicsConstants`** (`OpenFPS.Common/PhysicsConstants.cs:5`, 89 lines) mixes three things: the body
(radius 0.3, height, mass 70 kg, walk and run from `BodyConstants`, `JumpPower` 3.13, `StepHeight` 0.4,
`Gravity` from `BodyConstants`), the tick (`TickRate` 30, `FixedDeltaTime`, `MaxCatchUpSeconds`), and the
input integrity limits (`MaxInputDeltaTime`, `MaxInputsPerTick`, budgets, history). Read 1,123 times, 739 of
them by tests.

**`GroundProbeMemo`** (`OpenFPS.Common/GroundProbeMemo.cs:16`): the server's per-session memory of the floor,
handed back while the point has moved under 5 cm (:19) and the answer is under 150 ms old (:22, read from
`Environment.TickCount64`, `PhysicsUtils.cs:115`).

### 3.2 Sound: vehicles, projectiles, air

**`WheelDynamics`** (`OpenFPS.Sound/WheelDynamics.cs:110`, 756 lines) with `MagicCurve` (:10). Planar vehicle
dynamics with per-wheel loads: longitudinal and lateral velocity and yaw rate, load transfer from the centre
of gravity height and roll stiffness share (`Loads`, :394), Ackermann steering, the Magic Formula per tyre
with a friction circle, the kinematic bicycle below 3 m/s blended into the dynamic model by 6 m/s, sub-steps
up to 16 a tick (`Step`, :447). Every number on the presets is sourced. This is a physical model.

- The driver asks for an acceleration (`Step(dt, steer, wantAccel)`): the drive force is that acceleration
  times the mass, shared between the driven wheels. No engine torque reaches a wheel, no wheel has rotational
  inertia; a wheel's speed is worked out from its slip ratio (summary at :100-104).
- No vertical degree of freedom: no suspension, no pitch or roll dynamics, no wheel leaving the ground.
- Reads `RoadWaterLaw.GripFactor` from Sound (:331), the aquaplaning law.
- Instantiated by the server for driven cars and traffic (`DrivingSystem.cs:100`, `VehicleSystem.cs:255,
  406`), by `LineFollower` (`LineFollower.cs:142`), and by the client's sound to lay out its wheels
  (`EngineProcessor.cs:514`, `CabinPaths.cs:116`).

**`ChassisSpec`, `AxleSpec`, `TyreSize`, `RoadSurfaces`** (`OpenFPS.Sound/Chassis.cs`, 252 lines) and
**`RunningGear`** (`OpenFPS.Sound/RunningGear.cs:19`, 448 lines): the running gear as data, every preset one
real vehicle with its sources. Mechanics, read by Sound 263 times (the sound places its tyres and brakes by
them).

**`TyreFriction`** (`OpenFPS.Sound/TyreFriction.cs:11`): `Demand` (the friction circle, :17-22) is mechanics;
`SquealOnset`, `SlideOnset`, `FullSlide` are where the squeal begins, which is sound. `G` is 9.80665 (:13) and
is also `WheelDynamics.G`.

**`VehicleProfile`, `Gearbox`, `TyreProfile`** (`OpenFPS.Sound/Vehicles.cs`, 1,429 lines) and
**`EngineProfile`** (`OpenFPS.Sound/Engines.cs:357`, 1,757 lines): the presets. Section 4.4 splits them by
member.

**`Driveline`, `Driver`, `VirtualDriver`** (`OpenFPS.Sound/Core/Engine/Driveline.cs`, 509 lines). The car
behind the sound's engine, stepped once a sample: gearbox, clutch (one degree of freedom with the clutch up,
two with it down), mass, drag, rolling resistance and brakes (:14-60). `VirtualDriver` (:341) drives it to
follow the server's speed; `Driver` (:158) runs the lab's scripts. Its drag uses 1.225 and its rolling
resistance 9.81 as literals (:57-58); its brakes are a constant 7.5 m/s² (:35) where the server brakes at the
tyres' grip (`DrivingSystem.cs:262`).

**`ExternalBallistics`** (`OpenFPS.Sound/ExternalBallistics.cs:48`, 254 lines): a point mass under gravity and
G7 drag scaled by the round's ballistic coefficient, the air's temperature and pressure, the wind as the air's
velocity, a second-order step of 0.5 ms (:36-55). The server flies the authoritative bullet with it
(`CombatService.cs:284-296`, `Fly` :656-659) and the client's scope zeroes with it. Reads `WeaponDefinition`
12 times (`CoefficientOf` :99, `Fly` :152, `ZeroAngle` :183). Physical and good.

**`Ricochet`** (`OpenFPS.Sound/Ricochet.cs:80`, 516 lines): the bounce (`TryBounce`) and the tumbling flight
(`Advance`) are mechanics; the whine is sound. The faces are a table by material name (:89-103), not the
material table of docs/MATTER.md 2.4.

**`WindField`, `WindAir`, `WindWeather`** (`OpenFPS.Sound/Wind.cs`, 300 lines). The moving air: the log law
with height, Taylor's frozen turbulence as five eddy scales carried by the mean flow, hashed from a lattice so
every client agrees (:5-22). Horizontal only (`VelocityAt` returns a `Vector2`, :103). Nothing in it knows of
buildings or moving bodies. Its clock is `Now()`, UTC seconds since 2026-01-01 (:126).
**`WindModel`** (`OpenFPS.Sound/WindModel.cs:11`) is a second law: three sines for the gusts, used only by the
server's bullets (`CombatService.cs:597`).

**`BodyConstants`** (`OpenFPS.Sound/BodyConstants.cs:9`): person height, walk, run, gravity 9.81.
`PhysicsConstants` refers to it (decided in library stage 3, fix 2).

**`DoorPhysics`** (`OpenFPS.Sound/DoorPhysics.cs:11`, 538 lines) is not world physics. It is the parts every
simulated door's sound is built from, stepped four times per audio sample: exactly-stepped modes, plates
radiating by the Rayleigh integral, contacts, LuGre friction, small parts on mounts (:5-23). The approved door
renders depend on it bit for bit. It stays in Sound. So do the other audio-rate mechanisms: `LockCylinder`,
`KnobDoor`, `PushBarDoor`, `GlassDoor`, `SlidingDoor`, `CarWindow`, the glass shards' bounces in
`GlassFracture` (restitution and friction from the ground, :462-463), the debris in `BulletImpact`.

**`VehicleBody`** (`OpenFPS.Sound/VehicleBody.cs:20`) is the panels and cabin air the engine's sound gets
out through. Sound. **`VehicleCabin`** (`OpenFPS.Sound/VehicleCabin.cs:11`) is the body and cabin as boxes,
which the server builds the shell you sit in from (`VehicleShell`, 33 server references). Geometry stage 6
makes it a mesh; it is where a vehicle's collision shape and its acoustic shape should become one.

### 3.3 Geometry: the queries

Geometry is where Physics will stand, and it is in good shape for it.

- **`TriangleWorld`** (`OpenFPS.Geometry/Triangles/TriangleWorld.cs:77`, 688 lines): the two-level BVH of
  docs/GEOMETRY.md 2.2, our own (not Embree; Embree is Steam Audio's). Ray queries with layers, face choice
  and a filter (`Closest` :338, `Enter` :390, `Any` :423, `All` :452), point and box queries (`Containing`
  :488, `Overlapping` :514/:521), segments (`Along` :565, `Column` :604), the ground (`Ground` :661,
  `FloorAt` :700, `HeightOn` :728), moving instances (`WithMoved` :123, `WithMoverPoses` :145: door leaves).
- **`SolidContact`** (`OpenFPS.Geometry/Triangles/SolidContact.cs:36`, 473 lines): an upright cylinder and the
  stage 2 capsule against a solid or a box, with the way out and the depth. The walkable rule is here:
  `WalkableCos` 0.707, 45 degrees (:50).
- **`WheelRays`** (`OpenFPS.Geometry/Triangles/WheelRays.cs:14`): a ray down at each wheel on walkable faces
  (`Contacts` :19) and the body's rest from them: mean height, pitch from the axles, roll from the sides
  (`Rest` :36). `Rest` is mechanics standing in for suspension.
- **`Heightfield`**, **`BvhBuilder`**, **`GeometryUtils`** (boxes against cylinders, OBB tests),
  **`SpatialGrid`** (the entity grid; moving things are still boxes in it).
- What is missing for bodies: a swept query. Nothing sweeps a sphere, capsule or box along a path; every
  contact is an overlap at the end of a step. Section 6, item 12.

### 3.4 The server: who steps what

The tick is fixed at 30 Hz (`PhysicsConstants.cs:72`) and its order is in `Program.cs:908-987`: players move
(`MovementSystem`), then the road water, ground water and fire, then traffic (`VehicleSystem`), characters,
combat's due events, trains (`RailSystem`), crossings, speech and crowds, then driven cars (`DrivingSystem`),
car windows, doors (`DoorSystem`), parts placed on their roots (`ParentSystem`), and seated bodies carried
(`OccupancySystem`).

**`MovementSystem`** (`OpenFPS.Server/Systems/MovementSystem.cs:16`, 278 lines): drains each player's inputs
within a budget of simulated time, gathers the obstacles, and calls `SharedMovementEngine.Step`. Reads the
map's gravity from its `ZoneComponent` (:59-66). The teleport check `CheckCollision` uses a foot padding of
0.4 m (:254) where movement uses 0.15 (`SharedMovementEngine.cs:329`).

**`DrivingSystem`** (`OpenFPS.Server/Systems/DrivingSystem.cs:18`, 533 lines). A driven car, one tick:

- Engine pull: the gear from speed alone (`SelectGear` :453), rpm from the gear, torque from
  `TorqueFraction`, "a shallow curve about the profile's peak ... Not a dyno sheet" (:460-477). Instant shifts,
  no clutch.
- Resistances: drag with `AirDensity` 1.225 (:71, :260), rolling resistance (:261), brakes as the tyres' whole
  grip times the brake pedal (:262), engine braking as 15 % of peak torque scaled by rpm (:120, :263-266).
- Steering rate-limited and held under the angle that uses all the grip (:280-291).
- `WheelDynamics.Step` (:318); the pose from the ground probe and the four wheel rays (:342-372). The
  velocity it writes is horizontal (:408): a car never falls, flies or leaves its wheels.
- Collision: three cylinders along the car against the grid's boxes (`Blocked` :507-549); on a hit the car
  stops dead and the impact is heard (`Collision` :483-501), the struck thing's mass taken as fifty times the
  car's when it has no `Velocity` ("effectively the planet", :495) or its box volume times its density.
  `:388`: "TODO: the impulse and the damage: a car that hits something should be damaged and push it."
- State: `_running`, a static table of each world's driven cars by entity id (:95), and a thread-static map
  id (:100). Until 2026-10-11 it was one `ConcurrentDictionary` keyed by entity id alone: each map is its own
  Arch `World` with its own ids, so two driven cars with the same id on two maps shared one `WheelDynamics`
  when their presets matched, and the broadcast sent either car the other's wheels. Reproduced
  (`MapScopedStateTests`: a parked car on one world stopped the wheels of a car doing 20 m/s on another) and
  fixed: the table is a `ConditionalWeakTable` per `World`, an entry carries its entity's version, and a car
  no longer driven is dropped at the end of the world's step. The same sweep found the same fault in eight
  more places (changes.md, 2026-10-11).

**`VehicleSystem`** (`OpenFPS.Server/Systems/VehicleSystem*.cs`, ten files, 2,679 lines): traffic,
aircraft, small machines and walkers. Kinematic: a vehicle's place is a sample of its `RaceLine` and its speed
comes from the curvature and the braking pass. The tyres are told the motion afterwards
(`WheelDynamics.Hold`, :625, :863) so their loads and slips are right for the sound; they do not move the
car. Its brakes use 9.81 as a literal (`VehicleSystem.cs:813`, `.Following.cs:85`, `.StreetLife.cs:135`,
`.Crosswalks.cs:298`).

**`RailSystem`** (`OpenFPS.Server/Systems/RailSystem.cs:18`, 380 lines): a train is its sources placed along
the track behind the head. Speed rises at the map's `AccelerationMps2` (default 0.9) and falls at its
`BrakingMps2` (default 1.0) toward the line's speed (:153, :209, :260-261). No mass, tractive effort,
grade, adhesion or coupler slack.

**`OccupancySystem`** and **`OccupancyService`**: a seated body is placed on its seat after the vehicles move
and given the vehicle's velocity.

**`DoorSystem`** (`OpenFPS.Server/Systems/DoorSystem.cs:22`): a leaf's openness runs on a timer
(`SwingSeconds`, 0.9 s by default, :430), with a slower closer and a latch zone. Not a hinge with inertia and a
closer's torque; the door's sound model (3.2) is the physical one.

**`CombatService`** (`OpenFPS.Server/Core/CombatService*.cs`, 2,008 lines): bullets through
`ExternalBallistics` and `Ricochet`, glass that breaks through `GlassBreak` and a pane that fails taken out of
the world (:1154-1156). `BodyFall` (:1247-1266) is a person going down as two impacts, the legs from 0.5 m and
the trunk from 1.0 m 0.45 s later, through `ImpactAcoustics`: a script, not a fall.

**`HandsService`** (`OpenFPS.Server/Core/HandsService.cs:402-424`): a dropped thing is put on the floor 0.6 m
ahead and heard arriving at sqrt(2gh) from where it was held. No throw, bounce or roll.

**`Bodies`** (`OpenFPS.Server/Core/Bodies.cs:41`): what is left when somebody dies; placed on the ground under
them by `GroundUnder` (:157).

### 3.5 The client

**`ClientPhysicsSystem.Predict`** (`OpenFPS.Client.Core/ClientPhysicsSystem.cs:74`): the same step over the
client's snapshot. Gathers its own candidates (:91-115): the triangle world's solids, statics the triangles do
not hold, and moving solids within reach. `PredictionReconciler` replays the unacknowledged inputs from each
server state (`PredictionReconciler.cs:78-104`) and snaps past 5 m (:24). A seated player predicts nothing
(:58): the driver's own steering is heard a round trip late.

**`OtherBodies`** (`OpenFPS.Client.Core/OtherBodies.cs:16`): everybody else's feet from their interpolated
transforms. A body is grounded when its vertical speed is under 0.15 m/s (:106): the server knows
`IsGrounded` and does not send it. A thing with an engine is told from a person by its sound id prefix
(:70-78), a special case for the audit (docs/SPECIAL_CASES_AUDIT.md).

**`DistantMotion`** (`OpenFPS.Common/DistantMotion.cs`): far things carried forward between their states on
their speed's rate and their turn; the server runs the same prediction and resends when it strays. Kinematic
dead reckoning, part of the network, stays there.

### 3.6 The same law written more than once

| Law | Where | Values |
|---|---|---|
| Gravity | `BodyConstants.cs:31` (movement, glass, breath, ear wind), `Waves.cs:525`, `RunningWater.cs:619`, the door models' own `G` (`KnobDoor.cs:243`, `SlidingDoor.cs:200`, `LockCylinder.cs:125`, `CarWindow.cs:210`, `ElevatorDoor.cs:120`, `GlassFracture.cs:87`), literals in `RaceLine.cs` (4), `VehicleSystem*.cs` (4), `Driveline.cs` (2), `BulletImpact.cs` (4), `FireSynth.cs` (5), and 9 more files | 9.81: 40 times in 26 files |
| Gravity | `TyreFriction.cs:13` (= `WheelDynamics.G`), `ExternalBallistics.cs:50`, `StruckThings.cs:178`, `Aircraft.cs:432` | 9.80665: 4 times in 4 files |
| The map's gravity | `ZoneComponent.Gravity`, read by `MovementSystem.cs:64` only | drops, falls, tyres, bullets and water ignore it |
| Air density | `DrivingSystem.cs:71` and `Driveline.cs:57` (1.225); `Precipitation.cs:101`, `WallTransmission.cs:83`, `DoorPhysics.cs:13` (1.21); `BulletImpact.cs:275`, `GlassFracture.cs:87`, `ExhaustNetwork.cs:36`, `IntakeNetwork.cs:27`, `Waveguide.cs:511`, `GasHobSynth.cs:40` (1.2) | the ballistics alone work it out from the world's temperature and pressure (`Air`) |
| Step height | `PhysicsConstants.cs:57` and two locals in `PhysicsUtils.cs:34, 182` | 0.4 |
| Foot padding | `SharedMovementEngine.cs:329`, `WorldEditor.cs:28` (0.15); `MovementSystem.cs:254`, `OccupancyService.cs:376` (0.4) | two values for one body |
| Brakes | `DrivingSystem.cs:262` (all the grip) and `Driveline.cs:35` (7.5 m/s²) | one car, two laws |
| Rolling resistance and drag | `DrivingSystem.cs:260-261` and `Driveline.cs:57-58` | the same law twice |
| Gear choice | `DrivingSystem.SelectGear` and the client's engine processor | "written twice on purpose" |
| Wind | `WindField` (everything heard) and `WindModel` (the server's bullets) | two gust laws |
| Speed of sound | `AudioPhysics.SpeedOfSoundAt` (the mix, linear fit) and `Air.SpeedOfSound` (the bullets, ideal gas); 343 as a literal in the heard laws | made one by special-cases P8 (2026-10-11): `Air`'s law, one 20 °C reference, the world's speed in the heard laws |

The sound models' own gravity inside an audio-rate mechanism (a door's bolt falling, a shard's flight) is
not wrong, only unshared; it should read one constant. The ones that matter for play are the movement, the
tyres, the brakes, the bullets and the map's gravity.

### 3.7 Determinism today

The rule is that the server is the authority and the client's prediction of its own body agrees to the
millimetre (`--walk-pair`, `OpenFPS.AudioLab/Spikes/WalkPairSpike.cs`: the same walk through the server's
`MovementSystem.Update` and the client's `Predict`, every tick where they part by more than a centimetre
printed). What helps: one stateless step, solids sorted by owner, a fixed step, inputs that carry their own
`DeltaTime`. What can still part them, read in the code and not measured here:

1. The server's ground probe counts moving things (`PhysicsUtils.cs:88`); the client's does not
   (`:186-187`). Standing on a parked car or a bus, the two read different floors; the client's capsule may
   then be lifted by the bus's box in the collision step instead.
2. The server hands back a remembered floor for a point up to 5 cm away and 150 ms old
   (`GroundProbeMemo.cs:19, 22`); the client probes every time. On a ramp of 1 in 12 that is 4 mm; at a
   tread's edge it is a step.
3. The candidates are gathered differently: the server from the grid's dynamic cells within 5 m
   (`MovementSystem.cs:176`), the client from every moving snapshot within 5 m plus half its size
   (`ClientPhysicsSystem.cs:99-102`). The boxes are tested in list order and the deepest contact wins, so a
   tie between two boxes can go either way.
4. Yaw: the server turns in double and wraps (`MovementSystem.cs:50-51`), the client in float without
   wrapping (`ClientPhysicsSystem.cs:59`). Sub-millimetre per step; corrected past a threshold.
5. Wall clocks: the ground memo (`Environment.TickCount64`) and the wind (`DateTime.UtcNow`,
   `Wind.cs:126`). The wind is meant to be the same on every client by its UTC second; a physics step must not
   read it.

Bodies other than the player's are not predicted: the server sends their states and the client
interpolates or dead-reckons. So a rigid-body solver does not have to be bit-identical across machines; the
player's own movement does, and it stays the code it is.

---

## 4. The boundary

### 4.1 Layers

```
Geometry  <-  Physics  <-  Sound  <-  Audio (library stage 6)
    ^            ^           ^
    |            |           |
 Acoustics       +--- the host: Common (entities, wire), Server (steps it), Client (predicts, reads it)
```

- Physics references Geometry and nothing else of ours: no Sound, no Acoustics, no Common.
- Sound references Physics, for the state it reads (section 4.5) and for the presets' mechanical halves.
  It never steps the world's physics. Its audio-rate mechanisms (doors, locks, shards, the engine's crank)
  stay in Sound and take their inputs from Physics' state.
- The server steps Physics; the client steps the player's own body (prediction) and reads the rest.
- Acoustics does not need Physics. Geometry stage 6 puts bodies in the acoustic scene from shapes both
  read from Geometry.

### 4.2 What goes where

| Today | Goes to | Note |
|---|---|---|
| `SharedMovementEngine` (Common) | Physics | after `ColliderShape` moves to Geometry |
| `PhysicsUtils` (Common) | split | the triangle-world probes and `FootOnFloor<F>` to Physics; the `World` and `WorldSnapshot` overloads stay in Common as adapters |
| `PhysicsConstants` (Common) | split | the body and gravity to Physics (`BodyConstants`); the tick and input integrity stay with the network in Common |
| `GroundProbeMemo` (Common) | host | a server cache; must stop changing the answer (3.7 item 2) |
| `BodyConstants`, `ChassisSpec`, `AxleSpec`, `TyreSize`, `RunningGear`, `MagicCurve`, `WheelDynamics`, `Gearbox`, `VehicleChassisSpec` (Sound) | Physics | |
| `RoadSurfaces`, `BrakeKind`, `WheelState` (Sound) | Physics, as values | Sound reads them |
| `TyreFriction`, `TyreProfile` (Sound) | split | the friction circle and the Magic Formula's shape to Physics; squeal onsets, tread blocks and cavity to Sound |
| `VehicleProfile`, `EngineProfile`, `VehicleSpec` (Sound) | split | section 4.4 |
| `Driveline` (Sound) | split | the mechanics at the tick to Physics; the audio-rate coupling of crank and road stays (decision 3) |
| `VirtualDriver`, `Driver` (Sound) | Sound | until the server sends gear, clutch and throttle (stage 6) |
| `ExternalBallistics`, `FlightPoint` (Sound) | Physics | `BulletState` and `Air` as values |
| `Ricochet` (Sound) | split | `TryBounce` and the tumbling flight to Physics; the whine stays |
| `WindField`, `WindModel` (Sound) | Physics | made one law (stage 2); `WindAir`, `WindWeather` as values |
| `RoadWaterLaw.GripFactor` (Sound) | Physics | the water film between tyre and road is a contact law; the puddles and their sound stay |
| `WorldWeather`, `EarWind`, `ImpactAcoustics`, `Ballistics`, `VehicleBody`, `VehicleCabin`, `DoorPhysics` and the audio-rate mechanisms (Sound) | Sound | they read physical state |
| `SolidContact`, `WheelRays`, `TriangleWorld`, `Heightfield`, `GeometryUtils`, `SpatialGrid` (Geometry) | Geometry | `WheelRays.Rest` to Physics when suspension replaces it (stage 6) |
| `MovementSystem`, `VehicleSystem`, `RailSystem`, `OccupancySystem`, `DoorSystem`, `CombatService`, `HandsService`, `Bodies` (Server) | host | they step Physics and turn its contacts into game events |
| `DrivingSystem` (Server) | split | engine pull, resistances, the pose and `Blocked` into a Physics vehicle step; the controls, ignition, tile fences and the hearing of a collision stay |
| `ClientPhysicsSystem`, `OtherBodies`, `PredictionReconciler` (Client) | host | |
| `LineFollower`, `RaceLine` (Common) | host | drivers and planning; they read Physics' laws (one gravity) instead of 9.81 |

### 4.3 The crossings to fix

62 references from what moves whole (P, V) to what it may not reference, in 5 files (every line in
`tools/physics_boundary/physics_crossings.tsv`):

| From | To | Refs | Fix |
|---|---|---:|---|
| `VehicleChassisSpec` (`VehicleSpec.cs:11-38`) | `Editing.TunableAttribute` (Sound) | 28 | the editor's attributes go down to a project every part can reference (decision 7) |
| `Gearbox` (`Vehicles.cs:12-26`) | `Editing.TunableAttribute` | 15 | the same |
| `ExternalBallistics` (`ExternalBallistics.cs:99-296`) | `WeaponDefinition` (Sound) | 12 | take a projectile (mass, diameter, G7 coefficient, muzzle speed) instead of the weapon; `WeaponDefinition` makes one |
| `SharedMovementEngine` (`SharedMovementEngine.cs:549-550`) | `ColliderShape` (Common's `Components.cs:13`) | 5 | move the enum to Geometry (library stage 2 left it, 15.2: moving an enum does not change MemoryPack's bytes) |
| `WheelDynamics` (`WheelDynamics.cs:331`) | `RoadWaterLaw` (Sound) | 2 | move the grip law to Physics |

The split types carry their own reads, counted by type in appendix A: `PhysicsUtils` 69 host references (the
adapters, which stay in Common), `DrivingSystem` 85 to `DriveComponent` alone (the controls stay). The
presets' sound halves read Sound's own types (`ExhaustSpec` 500, `CamLobe` 310 and so on); they stay in
Sound, so those references never cross.

### 4.4 The presets: where the mechanics end and the sound begins

Every member of the five preset types that anything reads, by who reads it ("mechanics" is a type bound for
Physics or a server stepper; "sound" is Sound or the client's audio). Full lists in appendix C.

| Preset | Members read | Mechanics only | Both | Sound only |
|---|---:|---:|---:|---:|
| `VehicleProfile` | 47 | 0 | 20 | 22 |
| `EngineProfile` | 87 | 0 | 5 | 79 |
| `TyreProfile` | 24 | 9 | 3 | 12 |
| `Gearbox` | 12 | 0 | 6 | 5 |
| `ChassisSpec` | 17 | 10 | 2 | 4 |

- `VehicleProfile`: mass, drag area, rolling resistance, dimensions, axle places, running gear, tyres,
  gearbox and engine are read by both. The sound reads them because its `Driveline` re-simulates the car.
  The 22 sound-only members are the sound's (exhaust and intake places, bay, fans, siren, air system).
- `EngineProfile`: the mechanics read idle, redline, peak torque and its rpm. Everything else (cams,
  valves, exhaust, mufflers, intake, the `MechanicalSpec` of valvetrain tick and accessory whine) is the
  sound's. The engine's inertia and friction (`InertiaKgM2`, `FrictionNm`) are read by the sound's engine
  only.
- `Gearbox`: the ratios, final drive, top gear, upshift rpm and wheel radius are read by both; the shift
  behaviour (`DownshiftRpm`, `CruiseUpshiftRpm`, `LaunchRpm`, `ShiftSeconds`) by the sound only. The server
  shifts at the upshift rpm instantly; the sound's driver shifts its own way.

The line falls here. A preset becomes two halves joined by its id, as machines are parts (memory: machines as
parts):

- **Mechanics (Physics):** mass and its distribution, inertias, dimensions, the running gear, the tyres'
  grip and shape, the gearbox and the shift rules a driver uses, the clutch's capacity, brakes, drag and
  rolling resistance, and the engine's mean torque against rpm and throttle with its inertia and friction.
- **Sound (Sound):** everything that makes the pressure: the gas path, the cylinders' firing in crank
  angle, valvetrain, bay, panels, fans, tyre tread and cavity, squeal.
- **The bridge:** the server sends the engine's state (rpm, throttle, gear, clutch, load torque) and each
  wheel's; the sound's engine runs its cycle at that mean speed and load. The mean torque map comes from the
  sound's own engine model, rendered once per engine kind as the cycle cache already renders cycles
  (todo.md, "Cars in full detail"), so the server's car pulls with the torque the engine's sound makes and
  `TorqueFraction` goes.

### 4.5 The shared value types

What crosses from Physics to Sound and the host, stated as types (in Physics, plain structs, no MemoryPack
unless one is sent whole):

| Type | Fields | Today |
|---|---|---|
| `BodyState` | id, position, orientation, linear and angular velocity, mass, shape id, grounded, sleeping | `Transform` + `Velocity` components; angular velocity does not exist; `IsGrounded` is not sent |
| `ContactEvent` | the two bodies (or a body and a solid), point, normal, normal impulse, closing speed, tangential slip speed, the two surfaces, began/continued/ended | `SharedMovementEngine.Contact` (the player's wall bump), `DrivingSystem.Collision`'s arguments |
| `WheelState` | load, angular speed, slip ratio and angle, surface, demand, water (exists); then suspension travel and its speed, brake torque, brake temperature | `OpenFPS.Sound/WheelState.cs:6`, namespace `OpenFPS.Common.Networking` |
| `VehicleState` | speed, heading, yaw rate, accelerations, rpm, throttle, brake, gear, clutch, load torque, the wheels | `DriveComponent` (server only), `VehicleComponent.Speed`, `EntityState.SpeedRate`, `TyreDemand` |
| `AirSample` | velocity (3D), turbulence, pressure deviation at a point and moment | `WindField.VelocityAt` (2D, weather only) |
| `LooseLayerState` | per ground cell: cover, depth, how much is aloft and moving | none (section 5.3) |
| `ProjectileState` | position, velocity, spin, mass, diameter, coefficient | `BulletState` (no spin) |

Sound reads these and nothing that steps. The host copies them onto components for the wire. A body's shape
id names a Geometry piece, so the acoustic scene (geometry stage 6) and the contacts use one shape.

### 4.6 Who steps it

- **Server:** one `PhysicsWorld` per map, stepped once a tick in the place of today's systems, in today's
  order (players, then everything that moves, then seats). Each tick it takes the static `TriangleWorld`
  (with door leaves where they are), the moving bodies, the players' inputs and the weather, and gives back
  states and contact events. `CombatService`, `HandsService`, `DrivingSystem` and the rest turn contact
  events into game events (damage, a sound to emit). One world per map ends the static state of 3.4
  (`DrivingSystem._running`).
- **Client:** the player's own body only, through the same `SharedMovementEngine` over a view of its
  snapshot (`IPhysicsWorld`: the triangle world, the statics it does not hold, moving bodies as shapes). Both
  sides fill the same view, which is where 3.7's items 1 and 3 close.
- **Sound:** steps nothing of the world. Its audio-rate models step themselves, driven by state.

### 4.7 Shortcuts rather than physics

Cody's rule for vehicle physics was "as long as it adheres to realism". These do not, today:

| Shortcut | Where | What realism needs |
|---|---|---|
| A body on foot has no momentum and full air control | `SharedMovementEngine.cs:314-316` | at least a ballistic flight once off the ground (decision 6: the walk itself is approved and stays) |
| Step climbing teleports by up to 0.4 m | `SharedMovementEngine.cs:377-399` | acceptable for a person (a stride up is quick); keep, but the sound reads the real rise |
| The engine's torque is "not a dyno sheet" | `DrivingSystem.cs:460-477` | the engine's own mean torque (4.4) |
| Instant gear changes, no clutch on the server | `DrivingSystem.cs:226, 453` | clutch and shift time (stage 6) |
| Brakes as the tyres' whole grip; no brake torque, no lock-up | `DrivingSystem.cs:262` | brake torque per wheel against wheel inertia (stage 6) |
| No wheel inertia; wheel speed derived from slip | `WheelDynamics.cs:100-104` | a spinning wheel with its inertia: wheelspin and lock-up from torque |
| No suspension; the car rests on the mean of four rays | `WheelRays.cs:36`, `DrivingSystem.cs:344-372` | springs and dampers per wheel (stage 6) |
| A car never leaves the ground | `DrivingSystem.cs:408` | vertical motion; a car off a ledge falls |
| A car that hits something stops dead | `DrivingSystem.cs:387-396` | momentum exchanged, damage from the impulse (stage 4) |
| The struck thing's mass is fifty times the car's or its box times its density | `DrivingSystem.cs:493-496` | a body's mass from its parts and the material table |
| Traffic is kinematic on its line; the tyres are told afterwards | `VehicleSystem.cs:625, 813, 863` | traffic under the same tyres as a driven car (stage 6; the cost is the question) |
| Trains accelerate and brake at map constants | `RailSystem.cs:260-261` | mass, tractive effort, brake force, grade, adhesion, slack |
| A person falls as two impacts on a timer | `CombatService.cs:1247-1266` | a body that falls (stage 4) |
| A dropped thing lands where it is put | `HandsService.cs:402-424` | a body that falls, bounces, rolls and rests |
| A door leaf moves on a timer | `DoorSystem.cs:430` | a hinge with the leaf's inertia, the closer's spring and damper, the latch |
| Bullets and the ear feel different winds | `WindModel.cs:11`, `CombatService.cs:597` | one air field |
| Ricochet faces by material name | `Ricochet.cs:89-103` | from the material table's hardness and yield |
| Wind ignores buildings and bodies | `Wind.cs:23` | shelter behind solids, the wake of moving bodies (stage 5) |
| Gravity: the map's value moves players only | 3.6 | one gravity per world, read by everything |

---

## 5. What it must be able to hold next

None of this is to be built now. The boundary and the value types must not need to change when it is.

### 5.1 Rigid bodies and contacts

- A body: mass, centre of mass, inertia tensor (in its own frame), position, orientation, linear and angular
  velocity, a shape (a Geometry piece, a box, a capsule, a compound of them), a material (from the material
  table: density, friction, restitution, hardness), and whether it sleeps.
- Contacts against the triangle world and between bodies, with a normal impulse from restitution and a
  tangential impulse under Coulomb friction (static and kinetic), solved by sequential impulses with warm
  starting. Resting contact and small stacks without jitter; sleeping bodies woken by a contact or a moved
  support.
- Continuous collision for small fast things (a thrown bottle at 15 m/s moves 0.5 m a tick at 30 Hz): a swept
  shape against the BVH (Geometry needs the sweep, 3.3).
- Events: every contact that begins with an impulse, and every sliding or rolling contact as it continues,
  go to the host and on to `ImpactAcoustics` and the scrape and roll sounds. A sound is then a contact with a
  closing speed, two materials and two masses, which `ImpactAcoustics.Between` already takes.
- People: a capsule body for being struck, pushed and carried, with the walk on top of it when standing.
  Knocked over, the capsule becomes a few linked parts (trunk, legs, head) long enough to fall and land, then
  lies as the body item it is today (`Bodies.cs`). Not a full ragdoll at first. Superseded 2026-10-12: a
  person is eleven parts all the time (section 11, decision 11).
- What this gives: `DrivingSystem.cs:388` done; a car that strikes a person throws them and they land where
  the momentum says, heard as the impact, the flight's silence and the landing on what is there.

### 5.2 Moving air

- One air field per world: the weather's (`WindField` and the broadcast `WindAir`) plus local
  disturbances, sampled as `AirSample` at a point and moment.
- A moving body makes a disturbance from its frontal area, speed and length: a pressure pulse ahead and
  beside it, and a turbulent wake behind that decays with distance and time (a bus at 14 m/s leaves a wake
  of several metres a second at the kerb that dies away over seconds). A list of a few dozen disturbances
  near listeners, each analytic (a moving source of pressure and a decaying wake), not a fluid grid.
- Readers: the ear wind (`EarWind`, which already reads the weather), foliage, fire's lean, flags and loose
  things, and the loose layers. Sound reads `AirSample`; it does not own the field.
- Shelter: the weather's wind falls behind solids. The acoustic map already knows openness; a shelter factor
  per point from the triangle world (rays upwind) belongs with the air.
- Pressure from doors and blasts later reuses the same disturbances (section 6).

### 5.3 Loose layers

- Per ground cell, a layer as data, as the puddles (`RoadWater`), run-off and ground water already are: kind
  (leaf litter, grit, snow, sand), cover and depth, moisture, and how much of it is aloft.
- Lifted above a threshold air speed at the surface (a dry leaf moves at about 2 to 4 m/s, wet ones not at
  all), carried by the air, set down when it drops. Heard as texture events at that rate (skitter, rustle),
  read by footsteps and tyres as the surface's top layer.
- Seeded from the land cover (`LandCover` forest floor, park trees by season) and moved by wind and by the
  disturbances of 5.2.

### 5.4 Where determinism matters

| What | Who decides | Must agree |
|---|---|---|
| The player's own body | server; the client predicts | to the millimetre, every tick (`--walk-pair`) |
| Vehicles, bodies, loose things | server | not predicted; the client interpolates and dead-reckons (`DistantMotion`) |
| The driven car | server | if prediction is added (section 6, item 10), the vehicle step must be as deterministic as the walk |
| The air's weather part | every client from the broadcast and the UTC second | the same gusts at the same place on every client (as now) |
| The air's disturbances | each client from the bodies' states | close enough to be heard alike; never fed back into the server |
| Loose layers | server (what lies where), clients (what flutters) | the layer is the server's; the flutter is the client's |

A rigid-body solver therefore needs to be deterministic on the server from run to run (replays, tests), not
across machines.

---

## 6. What the physics is missing

Cody, 2026-10-11: "try and think of things my physics library is missing when it comes to physics related
functions". A gap list against what a realistic world needs, by the rule that sound falls out of physics,
materials and geometry. Ranked by what each adds for a player who hears rather than sees. Each was checked
against the code (grep and reading); "none" means nothing was found.

| # | Missing | Today | What it makes possible, and heard | Size | Where |
|---:|---|---|---|---|---|
| 1 | **Momentum exchange on contact** between moving bodies and things | none: a car stops dead (`DrivingSystem.cs:387-396`); `PhysicsPropertyComponent` unread (`Components.cs:406`) | a crash with a push and a spin; a struck bin rolling away; the impact level from the real impulse, then the slide and the settling | large | stage 4 |
| 2 | **Thrown and dropped things as bodies**: fall, bounce, roll, slide, rest | `HandsService.cs:402-424` puts a dropped thing on the floor | throwing a bottle to distract, hearing it bounce and roll down a ramp and stop against a wall; a dropped rifle clattering on stairs tread by tread | medium | stage 4 |
| 3 | **People knocked down, falling and landing** | `BodyFall` script (`CombatService.cs:1247`) | a person hit by a car thrown and landing where the momentum says; falling down stairs; a body landing on a car's bonnet and not the road | medium to large | stage 4 (capsule, few parts), articulated later |
| 4 | **The air moved by moving bodies** (pressure pulse, wake) | none; `VehicleShadow` blocks sound only | the bus felt at the ears and heard in the kerb's leaves as it passes; a train's wake on a platform; a lorry buffeting you at a crossing | medium | stage 5 |
| 5 | **Vehicle suspension per wheel** | none; rest on four rays (`WheelRays.cs:36`) | strut knock, bottoming out, top-mount clunk, the car pitching under braking heard in the cabin, kerbs and potholes struck per wheel with their real impulse (todo.md "Cars in full detail") | medium | stage 6 |
| 6 | **The drivetrain on the server**: engine torque, clutch, shift time, wheel inertia, brake torque, lock-up, ABS | `DrivingSystem.cs:224-266`; the clutch only in the sound (`Driveline.cs:14`) | gear changes heard when the car really changes gear; wheelspin and burnout from torque; a locked wheel's skid; ABS chatter; one car, not two | medium to large | stage 6 |
| 7 | **Loose layers**: leaf litter, grit, snow lying, dust | leaves as a ground material only; snow falls (`Precipitation.cs:13`) and never lies | leaves skittering in a gust and under a passing bus; walking through drifts; snow muffling footsteps and tyres; grit under a tyre | medium | stage 5 |
| 8 | **Wind loading on things** | none (docs/MATTER.md 5.3 wants it) | a gate on its latch, a door swinging in a draught, a flag, a tarp, a bin blown over: the wind heard only where something is moved | medium | stage 5 and after |
| 9 | **Collisions between people and vehicles, and people and people**, as pushes | players are solid cylinders (`Program.cs:1533`); a moving box pushes a body out by depenetration, not by momentum | being pushed aside in a crowd, shouldering a door, a car nudging a pedestrian; the apology from contact (docs/NEXT_BODIES_WHEELS_ROADS.md stage 2) | medium | stage 4 |
| 10 | **Prediction of the driven car** | none: a seated player predicts nothing (`PredictionReconciler.cs:58`) | steering and the engine answering at once instead of a round trip late; needs the vehicle step deterministic like the walk | medium | after stage 6 |
| 11 | **Hinges, springs and other joints** in the world | door leaves on a timer (`DoorSystem.cs:430`); joints exist only at the audio rate in the door models | a door that swings as far as it was pushed and is caught by its closer; a gate in the wind; trailers; a swinging sign | medium | stage 4 (hinge, spring), later the rest |
| 12 | **Swept queries** (sphere, capsule, box along a path) | none in Geometry (3.3) | no tunnelling of small fast things; bodies that meet the first thing on their path | small | stage 4 (Geometry) |
| 13 | **Contact events as a stream**, and triggers | the player's wall bump (`SharedMovementEngine.cs:59`); `EntityType.Trigger` marks derived rooms (`CompositeService.cs:580`), not a physics trigger | every scrape, roll and knock heard from one place; pressure plates, doorways that know who is in them | small | stage 3 |
| 14 | **A car leaving the ground**: vertical motion, rollover, jumps | none: horizontal velocity only (`DrivingSystem.cs:408`) | a car off a ledge falling and landing, a rollover's sequence, a speed bump taken fast | medium | stage 6 (needs 5) |
| 15 | **Buoyancy, wading and swimming** | none (grep: no buoyancy, swim or wade); water depth exists (`GroundWater`, `RoadWater`), waves are sound only (`Waves.cs`) | wading heard from the real depth; swimming; things that float and knock against a hull or a bank; a splash from what falls in | medium to large | later, after stage 4 |
| 16 | **Pressure waves**: blasts, a door slam pushing air | none; a door model feels air on its leaf (`KnobDoor.cs:898`) but pushes none into the room | a slammed door rattling another in its frame; a blast that breaks windows by overpressure | medium | later, on 5.2's disturbances |
| 17 | **Draughts and the stack effect** | none | a draught whistling under a door when a window is open across the house; doors drawn shut | medium | later, with the rooms' openings (by pressure, section 11, decision 15) |
| 18 | **Rail dynamics**: mass, tractive effort, grade, adhesion, coupler slack | map constants (`RailSystem.cs:260-261`) | the clank running down a freight train as it starts and stops (slack run-in), wheelslip and sanding on a grade, braking distances | medium | later |
| 19 | **Damage and deformation from impulse**; fracture of other materials | glass breaks well (`Glass.cs`, `GlassFracture.cs`, panes removed `CombatService.cs:1154-1156`); nothing else breaks | crumpled panels and their changed ring, wood splintering, plaster and tile chips; debris as bodies | large | later, after stage 4 |
| 20 | **Projectiles, the rest**: penetration by energy, spin drift, momentum to what is hit | G7 point mass with wind drift (good, `ExternalBallistics.cs:36-48`); ricochet (`Ricochet.cs`); glass puncture | a round heard through a door or a car's body with its impact on the far side; consistent drift with the one air field | small to medium | stage 2 (one wind), later (penetration) |
| 21 | **Footing**: slipping on ice, wet or steep ground; climbing | walkable to 45 degrees (`SolidContact.cs:50`); wet grip is for tyres only | slipping on ice and the fall; a steep bank you slide down; climbing a fence or a ladder | medium | later |
| 22 | **Heat in solids**: conduction, expansion and contraction | fire spread with radiation is good (`FireSpread.cs:28`); an exhaust's cooling ticks are a sound, not the metal cooling (docs/MATTER.md 6) | ticks from cooling metal that follow the heat; brake heat and fade; a hot hob creaking | medium | later |
| 23 | **Aerodynamics beyond drag**: crosswind force, downforce | drag only (`DrivingSystem.cs:260`); vehicles read no wind | a high-sided van pushed by a gust, heard in its tyres and steering | small | stage 6 |
| 24 | **Granular piles and debris fields** | none | gravel shifting underfoot, a rubble heap, a sand pile | large | later |

Also checked and good, so not gaps: the tyre model (Magic Formula, combined slip on a friction circle,
load sensitivity, wet grip, `WheelDynamics.cs`); the bullets' flight; the fixed tick with an input budget;
the walk on stairs and slopes (approved); fire spread; glass breaking by type.

How this changes the stages: items 12 and 13 move into stages 3 and 4 because 1 to 3 need them; item 23 joins
stage 6; item 20's one wind joins stage 2. Items 1 to 4 are why stage 4 goes before stage 6.

---

## 7. The stages

Rules for every stage, as for the sound library: one stage per merge; `git mv` for moves; namespaces
unchanged while moving (the physics types are in `OpenFPS.Common` and `OpenFPS.Common.Geometry` across
projects already); before and after each stage the guards (section 9) give the same result, and a stage that
changes one is finished only when the change is the one intended and its commit says so.

| Stage | What | Sessions | Main risk |
|---|---|---:|---|
| 0 | Guards: the ratchet, the movement replay, the driving and traffic replays, the ballistics table, the parity harness; the Jolt trial. **Built 2026-10-11, section 14** | 1.5 to 2 | the driving replay depends on statics (the thread-static map id) |
| 1 | `OpenFPS.Physics`; move what moves whole; the four crossing fixes; the wire hash covers it. **Built 2026-10-12, section 15** | 1.5 to 2 | the engine render fingerprint moves with `Gearbox` |
| 2 | Split the presets and the mixed types; one law per law | 2 to 3 | approved sound depends on the sound's half reading the same numbers |
| 3 | The state types; one world view both sides fill; the parity divergences; contact events; `DrivingSystem`'s mechanics into a vehicle step | 3 to 4 | movement parity |
| 4 | Rigid bodies and contacts, with geometry stage 6 | 5 to 8 | solver cost and stability on the server tick |
| 5 | Moving air and loose layers | 3 to 5 | the ear wind and foliage were approved on the weather alone |
| 6 | Vehicles in full: suspension, the server's drivetrain from the engine's own torque, brakes, traffic under the same model | 4 to 6 | the engine sound's tuning against its own driver |
| | **Total** | **20 to 30** | |

A session is one working session of an agent, as in docs/GEOMETRY.md.

### Stage 0: guards (1.5 to 2 sessions; built 2026-10-11, section 14)

- **Ratchet** (`PhysicsBoundaryTests`, beside `LibraryBoundaryTests` and on its machinery): a checked-in
  list of physics files and the allowance of `physics_crossings.tsv` (62 now). Fails on a new reference from
  a physics file to anything but Geometry; the allowance only goes down. After stage 1, the assembly test:
  `OpenFPS.Physics` references the runtime and `OpenFPS.Geometry` only.
- **Movement replay** (`MovementReplayTests`): `--walk-pair`'s machinery as a test. Scripted walks on the
  test maps and a cut of the city and Magnolia (held keys, taps, sprint, stairs, a ramp, a kerb, a doorway,
  a roof edge, standing on a parked car), each run through the server's `MovementSystem.Update` and the
  client's `Predict`; stored per tick, the server's position and velocity and the largest server-client gap.
  Fails if either side's path changes or a gap passes 1 mm.
- **Driving replay** (`DrivingReplayTests`): scripted inputs through `DrivingSystem.Update` on a test map
  (pull away, shift through the gears, a bend at the grip, a brake to a stop, a kerb under one side, a wet
  patch, a wall); stored per tick, the pose, speed and the wheels' bytes. A fresh `World` per run starts
  with no `_running` state.
- **Traffic replay**: `VehicleSystem` for a minute of the city's buses and cars, stored poses and wheel
  bytes; and `RailSystem` for a train through a station.
- **Ballistics table**: the flight tables `ExternalBallistics.Fly` makes for every weapon at 100 m steps,
  stored.
- **Parity harness** (`--physics-parity`, after `--geometry-parity`): the old and the new path side by side
  on real maps over thousands of probes (ground, steps, overlaps; later bodies against analytical answers),
  every difference over float rounding listed. Stage 1 and 2 moves must show none.
- Existing tests that guard it: `SharedMovementEngineTests`, `SharedMovementPhysicsTests`,
  `MovementSystemTests`, `StairsTests` (23), `TickRateAndPredictionTests`, `ClientGroundProbeTests`,
  `RoofEdgeFallTests`, `CeilingJumpTests`, `WheelDynamicsTests`, `VehicleMotionTests` (26),
  `DrivingControlsTests`, `TrafficTyreDemandTests`, `TrafficWheelsTests`, `OccupancyTests` (35),
  `RailRunTests`, `RailAndSignalTests`, `ExternalBallisticsTests`, `BulletRicochetTests`, `BodiesTests`,
  `WeatherWindTests`, and the sound library's `RenderFingerprintTests` (the engine render reads `Gearbox`,
  `VehicleProfile` and `Driveline`) and `EmitterStreamReplayTests` (its `drive` and `traffic_rain` scenarios
  read the wheels). All run on GitHub Actions (memory: tests on GitHub); locally only through
  `tools/test-local.sh`, which takes one of two slots on the machine.

### Stage 1: the project and the moves (1.5 to 2 sessions)

- Create `OpenFPS.Physics` referencing Geometry. Sound, Common, Server, Client.Core, the lab and the tests
  reference it.
- The four crossing fixes first, each its own commit with the guards unchanged: `ColliderShape` to Geometry;
  the editor's attributes down (decision 7); `ExternalBallistics` takes a projectile; `RoadWaterLaw` to
  Physics.
- Move whole: `BodyConstants`, `Chassis.cs`, `RunningGear.cs`, `WheelDynamics.cs`, `WheelState.cs`,
  `ExternalBallistics.cs`, `Wind.cs`, `WindModel.cs`, `SharedMovementEngine.cs`, and `Gearbox` out of
  `Vehicles.cs`.
- The wire hash (`OpenFPS.Common.csproj`, `WireLibrarySource`) covers `OpenFPS.Physics`: `WheelState`,
  `WindAir` and `BulletState` are on the wire. `DoorModelFingerprint` lists no file that moves.
- `files.tsv` and `allowed.tsv` of the library ratchet follow the moved files; `LibraryReferencesOnlyLibrary`
  allows Sound to reference Physics.
- Check: every project builds; the guards unregenerated; the render fingerprint bit for bit.

### Stage 2: the splits and one law per law (2 to 3 sessions)

- `VehicleProfile`, `EngineProfile`, `TyreProfile`, `VehicleSpec`: each preset's mechanical half to Physics
  (section 4.4), joined by id; the sound half keeps its name so the sound's readers change only where they
  read a mechanical member. The catalogue (`MachineRegistry`, sound library stage 5's `ModelCatalog`) hands
  out both halves.
- `TyreFriction`, `Ricochet`, `PhysicsUtils`, `PhysicsConstants`: split as in 4.2.
- One constant for each law: gravity (decision 5) with the map's gravity read by everything that falls,
  rolls or flies; air density from the world's temperature and pressure (`Air`, which the ballistics already
  use); one rolling resistance and drag law; one brake law. `WindModel` folded into `WindField`, so bullets
  feel the air the ears do.
- The sound's own audio-rate constants read the shared gravity; their renders must not change by more than
  the fingerprint's maths tolerance (decision 5).
- The split with the special-cases audit's P8 (docs/SPECIAL_CASES_AUDIT.md, 2026-10-11): P8 made the
  acoustic laws one, and this stage keeps the rest. Done in P8: one law for the speed of sound
  (`Air.SpeedOfSound`, which `AudioPhysics.SpeedOfSoundAt` reads, so the bullets and the mix hear one air),
  one reference for it (`AcousticConstants.SpeedOfSound`, 343 m/s at 20 °C), the world's current speed in
  the heard laws that wrote 343, the FMOD fallback voice's distance law and Doppler, one occlusion-floor
  default. Here: gravity, air density, brakes, rolling resistance and drag, `WindModel` and `WindField`.
  Air density includes the acoustic 1.2 and 1.21 (walls, doors, glass, pipes, hob) and the 343 written
  beside each in the sources' own mechanisms (pipe and horn resonances, Helmholtz cavities, radiation
  impedance): they take one air, density and speed together, from the world's temperature and pressure.
- Check: render fingerprint, emitter-stream replay, movement and driving replays unchanged, except where a
  constant was made one on purpose, regenerated in that commit with the reason.

### Stage 3: the state, the world view, the vehicle step (3 to 4 sessions)

- The value types of 4.5 (`BodyState`, `ContactEvent`, `VehicleState`, `AirSample`), copied onto the
  components the wire carries; `IsGrounded` sent so `OtherBodies` stops guessing it from the vertical speed.
- `IPhysicsWorld`: the triangle world, the statics it does not hold and the moving bodies as shapes, one view
  that the server fills from its `World` and grid and the client from its snapshot. The ground probe and the
  gather read the view, so both sides ask the same question. The three divergences of 3.7 closed: the moving
  things in the ground probe on both sides or neither; the memo hands back only an answer for the same point;
  one gather.
- A Physics `VehicleStep` from `DrivingSystem`'s mechanics (engine pull, resistances, steering limit, the
  wheels, the pose, the collision test), stepped per map instance; `DrivingSystem` keeps the controls,
  ignition, the tile fences and the hearing.
- Contact events from the movement and the vehicle step, a stream the host reads.
- Check: the movement replay with zero gaps over 1 mm on every scripted walk; the driving replay unchanged
  apart from the named fixes.

### Stage 4: rigid bodies and contacts, with geometry stage 6 (5 to 8 sessions)

- In Geometry: the swept queries; each vehicle's body as a closed mesh from its parts list (the same piece
  the acoustic scene instances, geometry stage 6); people as capsules.
- In Physics: bodies, contacts against the BVH and between bodies, restitution and friction from the material
  table, sleeping, continuous collision for small fast bodies, a hinge and a spring joint.
- First uses: a car that hits something exchanges momentum and is damaged by the impulse
  (`DrivingSystem.cs:388`); a struck person is thrown and lands (a capsule, then a few parts), heard by
  `BodyFall`'s impacts from the real speeds; dropped and thrown things fall, bounce and roll; door leaves as
  hinged bodies with their closers (the door's sound model takes the leaf's angle and speed).
- Contact events into `ImpactAcoustics` and the scrape and roll sounds.
- Check: analytical tests (a ball's bounce heights from its restitution, momentum and energy across a
  collision, a box resting on a slope at its friction angle and sliding past it, a body thrown off a roof
  landing where the flight says); the movement replay unchanged; the server tick's budget with 40 cars and a
  street's walkers measured.

### Stage 5: moving air and loose layers (3 to 5 sessions)

- The air field of 5.2 in Physics: the weather's wind with shelter from the triangle world, and the
  disturbances of moving bodies.
- `EarWind`, foliage and fire read `AirSample`.
- Leaf litter as the first loose layer (5.3), from the land cover, lifted and carried by the air, heard as
  texture events and read by footsteps.
- Check: a bus passing at 3 m measured at the ears against published wake measurements before it is played
  (memory: measure band balance before playing anything); the emitter-stream replay changes only where a
  voice reads the air.

### Stage 6: vehicles in full (4 to 6 sessions)

- Suspension per wheel (springs, dampers, bump stops) replacing `WheelRays.Rest`; a car that can leave the
  ground and land.
- The drivetrain on the server: the engine's mean torque from its own model (4.4), wheel inertia, the clutch,
  shift time and the gearbox's shift rules, brake torque per wheel with lock-up; crosswind force. The server
  sends gear, clutch, throttle and load; the sound's engine follows them instead of its own driver.
- Traffic under the same model where the cost allows (measured), kinematic beyond it.
- Check: the driving replay regenerated once, on purpose; the engine sound heard by Cody against the approved
  renders before it merges.

---

## 8. How it fits with geometry stage 6 and library stages 5 to 7

The order agreed with Cody on 2026-10-11 (todo.md, Next, in order): regions become places; the
audit's voice registry; this survey alongside it; then the physics stages with geometry stage 6; then
library stages 6 to 8.

- **Geometry stage 6** (docs/GEOMETRY.md section 7: vehicles and bodies in the acoustic scene) makes each
  vehicle's body a mesh from its parts and moves it with the body; bodies stage 2 (docs/NEXT_BODIES_WHEELS_ROADS.md)
  wants people as soft bodies of a size and mass. Physics stage 4 needs the same shapes for contacts. Doing
  them together makes one shape per body that both read, and one transform per body per tick (from
  `BodyState`) that both move. Done apart, the cabin shell, the collision boxes and the acoustic mesh would be
  three shapes of one car.
- **Library stage 5** (statics to instances): the rest of it is the audio clock (`IAudioClock`, whose
  `WorldTime` is `WindField.Now()`). Physics stage 1 moves `WindField` out of Sound; do stage 5's clock slice
  first or name the world time in Physics (or Geometry) so both read one clock. `WorldWeather` stays in
  Sound and holds the air field's weather from Physics.
- **Library stage 6** (the world input, `IAcousticWorld`): `VehicleShadow` (15 host references) and
  `CabinWalls` (20) read vehicles out of snapshots. After physics stage 3 they read `BodyState` with a shape
  id; doing physics stage 3 first means those two are rewritten once.
- **Library stage 7** (splitting `ClientAudioSystem`): `ChooseLiveEngines` and the engine voices read each
  vehicle's speed, wheels and preset from snapshots. Their target should be `VehicleState`, so physics stage
  3's types must exist before stage 7 starts.
- **The voice registry** (the audit's): it decides which voice belongs to which body and where on it; a body
  id from Physics is what it keys on. No conflict, but the registry should key on the entity id, which a
  body keeps.

So: voice registry, then physics 0 to 3, then physics 4 with geometry 6, then library 6 and 7 on the clean
state types, with physics 5 and 6 beside or after them.

---

## 9. Enforcement

- Before the project exists: `PhysicsBoundaryTests.NoNewCrossings`, the ratchet of stage 0, binding the
  sources as `LibraryBoundaryTests` does and counting every reference from a physics file to a type outside
  Geometry and Physics.
- After: `PhysicsReferencesOnlyGeometry`, the assembly's references: the runtime and `OpenFPS.Geometry`.
  The compiler refuses a type that is not referenced; the test refuses the reference.
- Sound's side: Sound may name Physics' value types and presets, never a stepper (`PhysicsWorld`,
  `VehicleStep`, `SharedMovementEngine.Step`). A test lists the Physics types Sound references and fails on
  a stepper.
- Statics: the physics assembly holds no mutable static outside tables filled once and per-thread scratch
  (today: `PhysicsUtils`' five thread-static scratch lists, the preset caches, `MagicCurve.Cache`,
  `ExternalBallistics._zeroCache`; appendix A's statics table). World state is per `PhysicsWorld`.

---

## 10. Risks

- **Approved sound rides on today's mechanics.** The engines were tuned against the sound's own
  `Driveline` and `VirtualDriver`; the idle limit cycles and the gear changes were approved by ear. Stages 1
  to 3 must not change a sample (the fingerprint and the replay say so); stage 6 changes what drives the
  engine and must be heard before it merges.
- **Movement is sensitive.** docs/GEOMETRY.md 10.4 shows a change of body shape parting 3 to 15 walks in
  400 by metres. Nothing in stages 1 to 3 may change the step's arithmetic or the order of the solids;
  closing 3.7's divergences changes the server or the client on purpose and is measured by the movement
  replay first.
- **The wire.** `WheelState`, `WindAir`, `BulletState` move; the hash must follow (stage 1) or a client and a
  server of different versions misread each other. `PhysicsPropertyComponent` is positional: append only.
- **Statics across maps.** The thread-static map id; `DrivingSystem._running` (3.4) is per world since
  2026-10-11. Per-world state removes the rest.
- **Cost on the server tick.** 30 Hz with 40 cars, a street's walkers and their bodies; a rigid-body solver
  adds contacts per body per sub-step. Sleeping and only waking bodies near players keep it bounded; measure
  before stage 4 merges (`PerfProbe`).
- **Two collision worlds** if a library engine is chosen (decision 1): its own copy of every tile's triangles,
  rebuilt on every door swing and streamed tile.
- **Work in flight.** Another agent is editing acoustics, the editor and the generators; physics moves touch
  `Components.cs` (the enum), the editor's attributes and the presets. Do each move when no branch is open on
  those files, and say so.
- **The audit.** `OtherBodies`' sound-id prefixes and `VehicleSystem`'s kinds by name are special cases the
  audit will rank; physics stage 3's `BodyState` gives them a kind to read instead.

---

## 11. Decisions for Cody

1. **The physics engine for rigid bodies.** Cody decided on 2026-10-06 for our own BVH and our own movement
   rather than Jolt (docs/GEOMETRY.md, decisions). That stays. The open question is the solver for rigid
   bodies (stage 4). The library facts below are recalled, not checked against their sites in this survey.

   | | Our own | BepuPhysics v2 | Jolt (through JoltPhysicsSharp) |
   |---|---|---|---|
   | Licence | ours | Apache 2.0 [recalled] | MIT, and the wrapper MIT [recalled] |
   | Language | C# | C#, no native code | C++ with a P/Invoke wrapper; native libraries per platform, as FMOD and Steam Audio are shipped now |
   | Determinism | by construction: fixed order, one thread, our floats | repeatable on one machine and build [recalled]; not promised across CPUs | cross-platform determinism as a build option, at a cost [recalled] |
   | Geometry | our `TriangleWorld` and its BVH, one copy | its own mesh shapes and broad phase: a second copy of every tile, kept in step with doors and streaming | the same |
   | Features | what we write: bodies, contacts, sleeping, CCD, a hinge and a spring, in stage 4 | bodies, many constraints, CCD, sleeping, fast; vehicles and characters only as demos [recalled] | bodies, constraints, CCD, sleeping, a character, wheeled and tracked vehicles, ragdolls, soft bodies, buoyancy [recalled] |
   | Fit | the contacts read the same materials and shapes the sound reads | good, pure .NET; the movement and vehicles stay ours | most features; the most to keep in sync |

   **Recommended: our own**, small and physical: sequential impulses with warm starting over our BVH, tens to
   a few hundred awake bodies near players. The bodies are not predicted on the client, so cross-machine
   determinism is not needed (5.4), and one copy of the geometry with the material table's friction and
   restitution keeps the sound and the contact the same thing. BepuPhysics is the fallback if stacks and
   piles (gap 24) or long chains of joints prove hard; Jolt if ragdolls and soft bodies become wanted and its
   copy of the world is accepted.

2. **The step rate.** The tick stays 30 Hz for the network, the walk and its prediction (approved, and
   parity-tested). **Recommended:** rigid bodies sub-step at 120 Hz (four a tick) with continuous collision
   for small fast things; vehicles keep their own sub-steps (up to 16, `WheelDynamics.Step`); the sound's
   audio-rate mechanisms keep theirs. 60 Hz halves the cost and doubles a thrown bottle's step.

3. **Where vehicle mechanics end and their sound begins.** **Recommended:** Physics owns everything that sets
   the motion at the tick: the mean engine torque (rendered from the engine's own model, 4.4), the gearbox,
   clutch, wheels, brakes, tyres and suspension. The server sends the engine's and wheels' state. Sound owns
   the pressure: the cycle in crank angle around that mean speed and load, the gas path, panels, tyre tread
   and squeal. The sound's `Driveline` and `VirtualDriver` stay for the lab and for far cars until stage 6
   sends gear, clutch and throttle. The alternative, the server stepping the sound's engine model itself at
   the audio rate, is 1,600 steps a tick per car at the mixer's 48 kHz (`RenderRate.cs:11`).

4. **Sound references Physics.** **Recommended:** Sound references Physics for its value types and presets
   (one-way, enforced by section 9). The alternative is a third small project of state types that both
   reference; it buys nothing while Physics depends on Geometry alone.

5. **One gravity.** 9.81 (40 places, `BodyConstants`, Cody's "the speed of gravity on earth", 2026-10-04) or
   9.80665 (the standard; the tyres and bullets use it). **Recommended:** 9.80665 as `StandardGravity` in
   Physics, the map's gravity read everywhere that falls, rolls or flies, and the sound's constants reading
   it; the render fingerprint and replays regenerated in the one commit that changes it. The 0.03 %
   difference is inaudible; the point is one number.

6. **The walk's momentum.** A body on foot has no acceleration and full air control
   (`SharedMovementEngine.cs:314-316`); Cody taps keys and one tap is 15 cm (memory). **Recommended:** keep
   the walk on the ground as it is (approved), and make the flight ballistic: once off the ground a body
   keeps its horizontal velocity, so a jump or a fall off a ledge carries on as it should. Ask before
   changing the ground walk.

7. **Where the editor's attributes go.** `TunableAttribute` and the field descriptors are in
   `OpenFPS.Sound/Editing`; Physics' presets carry them. **Recommended:** a small `OpenFPS.Schema` project
   with the attributes alone, referenced by every part (the world editor's generic schema editor reads them);
   cheaper alternative, put them in Geometry.

8. **Door leaves as hinged bodies.** **Recommended:** in stage 4, a door leaf becomes a hinged body with its
   closer as a spring and damper and its latch as a contact, and the sound's door model takes the leaf's
   angle and speed from it. The door models were approved as they are; the leaf's motion they are given
   would change, so this is heard before it merges ("Doors as live models", todo.md).

9. **Traffic.** **Recommended:** traffic under the same vehicle step as a driven car near listeners, kinematic
   on its line far away, once stage 6's cost is measured with 40 cars.

10. **When.** Stages 0 and 1 are cheap and can follow the voice registry at once. Stage 4 is the one Cody
    asked for (impacts) and waits for geometry stage 6's shapes; stage 5 is the bus's air and the leaves.

Decided 2026-10-12 (Cody), from questions asked on the Resonance side; both games take these through the
libraries:

11. **A person is their parts all the time.** Eleven parts (hips, trunk, head, two upper arms, two forearms with
    hands, two thighs, two lower legs with feet), present whether standing or down, so each can be hit, reached
    with and stood on. Standing and walking, the parts follow the walk's pose kinematically (Jolt's ragdoll
    driven to a pose); the movement controller still moves the body, since balancing on two legs is not
    simulated. Struck, the part takes the impulse and the joints' motors try to hold the pose: a light hit
    jolts a limb, a heavy one makes the body stumble. Knocked down or out, the motors let go and the body
    falls as one. This replaces "a capsule, then a few parts" in 5.1 and stage 4. Footsteps can then come
    from the foot that lands, at its own mass.
12. **The body plan is data, owned by `OpenFPS.Physics`.** Parts, sizes, masses, centres of mass and joints
    with their limits, scaled from a body's height and mass by published segment tables (Dempster; de Leva's
    adjustments) and measured ranges of motion. One format for both games; Resonance makes none of its own.
    The same format describes animals later.
13. **A vehicle's body and its sound preset are separate.** A map's vehicle carries its own length, width and
    height; the preset is the engine (and the parts the sound reads). Exhaust and intake positions come from
    the body by the vehicle's layout (a stack behind the cab, a tailpipe at the rear), not from the car the
    engine was measured in. `diesel_truck` stays a tractor's engine.
14. **A trailer is a second body joined at its pin.** Its own wheels and no engine, coupled by a constraint at
    the kingpin or hitch, so its tail swings and it can jackknife. Built in stage 6 (vehicles in full), after
    cars are on Jolt; one box until then. With it: the coupling's clunk pulling away and braking, the trailer's
    tyres scrubbing on a tight turn, an empty trailer rattling, its own air brakes.
15. **Draughts by pressure (item 17 of section 6).** Wind sets a pressure on each face of a building from which
    way the face meets it (windward pushes, leeward and side faces pull); flow through an opening follows the
    orifice law from its area and the pressure across it (Q = Cd A sqrt(2 dp / rho)). One opening alone
    passes little, a pulse with the gusts; openings on two faces give a through-draught. The flow is fastest
    in the opening and spreads and slows into the room; the ear's wind reads the air speed at the head. It
    comes through the libraries' rooms and openings, so both games get it, and it drives later doors pushed
    or slammed by the wind and the whistle at a gap.

---

## 12. What was not measured

- No walk was run: `--walk-pair` lives in the lab, and only the survey tool was built. The divergences of 3.7
  are read from the code; whether each moves a body by over a millimetre on the real maps is stage 0's first
  measurement.
- The shared state across maps in `DrivingSystem._running` (3.4) was reproduced and fixed on 2026-10-11.
- How far `TorqueFraction` is from the engine model's own torque: not rendered.
- The cost of rigid bodies, the air's disturbances or traffic under `WheelDynamics.Step` on the server tick.
- The library facts in decision 1 (licences, determinism, features) are from memory.
- References by string (sound-id prefixes that tell a car from a person, preset names in JSON) and through
  delegates are not in the counts.
- The split types' crossings are counted by type, not by the half that moves; the preset split is by member
  (appendix C), which is what the move needs.

---

## 13. Appendices

### A. The inventory

Every type sorted, from `physics_tables.md`. Callers are references by project (tests and the lab
included); the last column is every reference to a host, sound or acoustics type.

| Type | Group | File | Lines | Callers (refs by project) | Reads outside Geometry and the inventory |
|---|---|---|---:|---|---|
| SharedMovementEngine | P | OpenFPS.Common/SharedMovementEngine.cs:12 | 540 | Tests 463, AudioLab 100, Client.Core 57, Server 48, Common 4 | ColliderShape (host) 5 |
| PhysicsUtils | PS | OpenFPS.Common/PhysicsUtils.cs:9 | 325 | Tests 66, Server 44, AudioLab 32, Client.Core 10 | WorldSnapshot 18, ColliderComponent 15, EntitySnapshot 12, Transform 11, EntityDefinition 8, MaterialComponent 5 (host) |
| PhysicsConstants | PS | OpenFPS.Common/PhysicsConstants.cs:5 | 89 | Tests 739, Server 158, AudioLab 128, Client.Core 57, Common 29, Client 6, Client.Gtk 6 | |
| GroundProbeMemo | H | OpenFPS.Common/GroundProbeMemo.cs:16 | 51 | Tests 14, Server 6, Common 3 | |
| BodyConstants | P | OpenFPS.Sound/BodyConstants.cs:9 | 24 | Sound 12, Common 10 | |
| ChassisSpec | P | OpenFPS.Sound/Chassis.cs:104 | 125 | Sound 263, Client.Core 19, Tests 6, Server 4, Common 1, AudioLab 1 | |
| AxleSpec | P | OpenFPS.Sound/Chassis.cs:71 | 29 | Sound 63, Client.Core 4, Tests 3 | |
| TyreSize | P | OpenFPS.Sound/Chassis.cs:18 | 51 | Tests 12, Sound 11, Client.Core 2 | |
| BrakeKind | V | OpenFPS.Sound/Chassis.cs:11 | 1 | Sound 18, Client.Core 2 | |
| RoadSurfaces | V | OpenFPS.Sound/Chassis.cs:237 | 46 | Tests 16, Sound 12, Server 12, Client.Core 8, AudioLab 4, Common 2 | |
| MagicCurve | P | OpenFPS.Sound/WheelDynamics.cs:10 | 65 | Sound 17 | |
| WheelDynamics | P | OpenFPS.Sound/WheelDynamics.cs:110 | 756 | Tests 290, AudioLab 136, Server 112, Client.Core 30, Common 28, Sound 14 | RoadWaterLaw (sound) 2 |
| TyreFriction | PS | OpenFPS.Sound/TyreFriction.cs:11 | 59 | Tests 102, Sound 22, Server 8, Client.Core 2, AudioLab 2 | |
| RunningGear | P | OpenFPS.Sound/RunningGear.cs:19 | 448 | Sound 64 | |
| WheelState | V | OpenFPS.Sound/WheelState.cs:6 | 44 | Tests 51, AudioLab 31, Server 24, Client.Core 19, Common 6 | |
| Gearbox | P | OpenFPS.Sound/Vehicles.cs:8 | 55 | Sound 270, Tests 43, AudioLab 26, Server 11, Client.Core 9 | TunableAttribute (sound) 15 |
| TyreProfile | PS | OpenFPS.Sound/Vehicles.cs:205 | 154 | Sound 188, Tests 54, AudioLab 12, Client.Core 4, Server 1 | TunableAttribute (sound) 53 |
| VehicleProfile | PS | OpenFPS.Sound/Vehicles.cs:361 | 1069 | Tests 648, AudioLab 506, Sound 285, Client.Core 108, Server 58, Common 1 | BladeRowSpec 32, ElectricFanSpec 23, EngineBaySpec 14, FanClutchSpec 5 (sound) |
| EngineProfile | PS | OpenFPS.Sound/Engines.cs:357 | 1757 | Sound 282, AudioLab 164, Tests 140, Server 16, Client.Core 2 | ExhaustSpec 500, CamLobe 310, IntakeSpec 259, ValveSpec 232, MechanicalSpec 144, MufflerSpec 141 and 7 more (sound) |
| VehicleSpec | PS | OpenFPS.Sound/VehicleSpec.cs:71 | 75 | Sound 15, Tests 11 | MachineRegistry 10, TunableAttribute 3 (sound) |
| VehicleChassisSpec | P | OpenFPS.Sound/VehicleSpec.cs:9 | 32 | Sound 23, Tests 2 | TunableAttribute (sound) 28 |
| VehicleExhaustSpec | S | OpenFPS.Sound/VehicleSpec.cs:43 | 11 | Sound 9 | TunableAttribute 9 |
| VehicleIntakeSpec | S | OpenFPS.Sound/VehicleSpec.cs:56 | 8 | Sound 7 | TunableAttribute 6 |
| VehicleBody | S | OpenFPS.Sound/VehicleBody.cs:20 | 364 | Tests 171, Sound 136, AudioLab 56, Client.Core 29, Server 3 | TunableAttribute 33, PanelAcoustics 11, AcousticRegistry 2, MaterialProperties 1 |
| BodyResonator | S | OpenFPS.Sound/VehicleBody.cs:389 | 81 | Sound 10, Tests 8, Client.Core 7 | |
| BodyMode | S | OpenFPS.Sound/VehicleBody.cs:7 | 1 | Sound 12, AudioLab 5, Tests 4 | |
| VehicleCabin | S | OpenFPS.Sound/VehicleCabin.cs:11 | 94 | AudioLab 56, Sound 43, Server 33, Tests 31, Client.Core 15 | |
| DoorPhysics | S | OpenFPS.Sound/DoorPhysics.cs:11 | 538 | Sound 1460 | |
| Driveline | PS | OpenFPS.Sound/Core/Engine/Driveline.cs:14 | 108 | Sound 136, Tests 58, AudioLab 33, Client.Core 29 | EngineSynth (sound) 15 |
| Driver | S | OpenFPS.Sound/Core/Engine/Driveline.cs:158 | 177 | Tests 20, AudioLab 6, Sound 3 | EngineSynth 42 |
| VirtualDriver | S | OpenFPS.Sound/Core/Engine/Driveline.cs:341 | 169 | Tests 19, AudioLab 9, Client.Core 6 | EngineSynth 24 |
| DriveOrder | S | OpenFPS.Sound/Core/Engine/Driveline.cs:151 | 1 | AudioLab 70, Tests 58, Sound 29 | |
| DriverAction | S | OpenFPS.Sound/Core/Engine/Driveline.cs:124 | 24 | AudioLab 102, Tests 89, Sound 43 | |
| ExternalBallistics | P | OpenFPS.Sound/ExternalBallistics.cs:48 | 254 | Tests 78, Server 22, Client.Core 12, AudioLab 8, Sound 4 | WeaponDefinition (sound) 12 |
| BulletState | V | OpenFPS.Sound/ExternalBallistics.cs:21 | 8 | Server 75, Sound 58, AudioLab 48, Tests 41 | |
| FlightPoint | P | OpenFPS.Sound/ExternalBallistics.cs:32 | 1 | Tests 31, Sound 4 | |
| Air | V | OpenFPS.Sound/ExternalBallistics.cs:8 | 11 | Sound 37, Tests 20, AudioLab 9, Server 6 | |
| Ballistics | S | OpenFPS.Sound/Ballistics.cs:19 | 90 | Tests 106, Sound 2 | |
| Ricochet | PS | OpenFPS.Sound/Ricochet.cs:80 | 516 | Tests 50, AudioLab 27, Server 18, Client.Core 5 | Slug 44, BulletFlyby 16, TransientSound 10, ShapedNoise 5, SoundCharacter 2 (sound) |
| WindField | P | OpenFPS.Sound/Wind.cs:23 | 132 | Tests 40, Sound 30, Client.Core 18, AudioLab 10, Server 8 | |
| WindAir | V | OpenFPS.Sound/Wind.cs:160 | 61 | Sound 46, Tests 36, Server 12, AudioLab 9, Client.Core 6 | |
| WindWeather | V | OpenFPS.Sound/Wind.cs:227 | 74 | AudioLab 65, Tests 56, Sound 31, Client.Core 6, Server 2 | |
| WindModel | P | OpenFPS.Sound/WindModel.cs:11 | 41 | Tests 20, Server 2 | |
| WorldWeather | S | OpenFPS.Sound/WorldWeather.cs:16 | 50 | AudioLab 65, Tests 49, Client.Core 30, Sound 3 | Runoff 2, GroundWater 2 |
| EarWind | S | OpenFPS.Sound/EarWind.cs:67 | 173 | Tests 108, AudioLab 36, Sound 30, Client.Core 2 | EarWindListener 13, EarCover 11, Loudness 4, EarWindAtEars 3 |
| ImpactAcoustics | S | OpenFPS.Sound/ImpactAcoustics.cs:11 | 62 | Server 10, Client.Core 6, Tests 6, Sound 2, AudioLab 2 | TransientSound 18, PanelAcoustics 14, MaterialProperties 7, SoundCharacter 4 |
| SolidContact | G | OpenFPS.Geometry/Triangles/SolidContact.cs:36 | 473 | Common 23, Tests 9, AudioLab 8, Geometry 2, Server 2 | |
| BodyShape | G | OpenFPS.Geometry/Triangles/SolidContact.cs:6 | 9 | AudioLab 20, Tests 11, Common 3, Client.Core 2, Server 2 | |
| WheelRays | G | OpenFPS.Geometry/Triangles/WheelRays.cs:14 | 50 | Server 4, Tests 4 | |
| WheelContact | G | OpenFPS.Geometry/Triangles/WheelRays.cs:6 | 1 | Geometry 6, Tests 6, Server 4 | |
| TriangleWorld | G | OpenFPS.Geometry/Triangles/TriangleWorld.cs:77 | 688 | Tests 167, AudioLab 101, Geometry 68, Client.Core 60, Acoustics 31, Common 29, Server 17 | |
| BvhBuilder | G | OpenFPS.Geometry/Triangles/Bvh.cs:26 | 167 | Geometry 98, Common 4, Client.Core 4 | |
| Heightfield | G | OpenFPS.Geometry/Triangles/Heightfield.cs:19 | 388 | Tests 78, Geometry 70, Client.Core 26, Common 8, Server 5 | |
| GeometryUtils | G | OpenFPS.Geometry/GeometryUtils.cs:30 | 519 | Tests 331, Geometry 84, AudioLab 73, Common 58, Client.Core 48, Acoustics 14, Server 14, Sound 6 | |
| SpatialGrid | G | OpenFPS.Geometry/SpatialGrid.cs:10 | 343 | Server 129, Tests 124, AudioLab 27, Client.Core 13, Common 12 | |
| MovementSystem | H | OpenFPS.Server/Systems/MovementSystem.cs:16 | 278 | Tests 46, Server 24, AudioLab 10 | Transform 28, ClientInputUpdate 19, PlayerComponent 17, UserSession 13, ColliderComponent 13, Velocity 7 and 9 more |
| DrivingSystem | PS | OpenFPS.Server/Systems/DrivingSystem.cs:18 | 533 | Tests 12, Server 10, AudioLab 2 | DriveComponent 85, Transform 21, ColliderComponent 12, Velocity 8, CompositeService 8, MaterialComponent 6 and 11 more |
| VehicleSystem | H | OpenFPS.Server/Systems/VehicleSystem*.cs (10 files) | 2679 | Tests 227, Server 19, AudioLab 4 | ParkState 132, RoadNetwork 100, VehicleData 95, Transform 86, ColliderComponent 35, StreetLifeData 35 and 53 more |
| RailSystem | H | OpenFPS.Server/Systems/RailSystem.cs:18 | 380 | Tests 24, Server 19, AudioLab 12 | TrainData 24, TrainSignal 14, Transform 12, TrainLayout 12, MapManager 10, TrainProfile 8 and 14 more |
| OccupancySystem | H | OpenFPS.Server/Systems/OccupancySystem.cs:18 | 98 | Tests 13, Server 3, AudioLab 3 | Transform 24, OccupantComponent 20, Velocity 12, PlayerComponent 8, OccupancyComponent 6, Seat 2, and 1 more |
| OccupancyService | H | OpenFPS.Server/Core/OccupancyService.cs:15 | 383 | Tests 75, Server 38, AudioLab 1 | Transform 40, UserSession 35, Seat 23, TransientSound 21, OccupantComponent 20, OccupancyComponent 16 and 17 more |
| DoorSystem | H | OpenFPS.Server/Systems/DoorSystem.cs:22 | 1024 | Tests 331, Server 65, AudioLab 5 | DoorComponent 253, TransientSound 83, DoorKind 66, DoorEvents 54, GlassDoor 36, Transform 32 and 19 more |
| CombatService | H | OpenFPS.Server/Core/CombatService*.cs | 2008 | Tests 133, Server 45, AudioLab 8 | UserSession 148, WeaponDefinition 73, Transform 70, ColliderComponent 59, AdminGun 56, GameServer 43 and 62 more |
| Bodies | H | OpenFPS.Server/Core/Bodies.cs:41 | 158 | Tests 18, Server 10 | IdentityComponent 19, Transform 17, ItemComponent 11, Corpse 8, NameComponent 7, BelongingsBag 7 and 7 more |
| HandsService | H | OpenFPS.Server/Core/HandsService.cs:23 | 964 | Tests 230, Server 71, AudioLab 1 | UserSession 103, HandsComponent 90, Arms 48, Transform 42, SavedItem 39, MapManager 27 and 25 more |
| LineFollower | H | OpenFPS.Common/LineFollower.cs:82 | 98 | Tests 25, Server 9 | DriverSteering 8 |
| RaceLine | H | OpenFPS.Common/RaceLine.cs:13 | 384 | Tests 113, Server 92, AudioLab 40, Common 19 | |
| ClientPhysicsSystem | H | OpenFPS.Client.Core/ClientPhysicsSystem.cs:13 | 183 | Client.Core 27, Tests 9, AudioLab 6 | LocalPlayerState 21, WorldSnapshot 15, EntitySnapshot 13, ClientInputUpdate 9, EntityDefinition 9, ColliderComponent 7 and 4 more |
| OtherBodies | H | OpenFPS.Client.Core/OtherBodies.cs:16 | 113 | Tests 48, Client.Core 11 | EntitySnapshot 25, StrideAccumulator 9, Transform 6, EntityType 5, EntityDefinition 5, Breathing (sound) 3 and 5 more |

Lines by group:

| Group | Types | Lines |
|---|---:|---:|
| P | 14 | 2,553 |
| PS | 10 | 4,685 |
| V | 7 | 245 |
| G | 9 | 2,638 |
| S | 15 | 1,843 |
| H | 14 | 8,801 |

References into what moves to Physics (P, PS, V), by project:

| Project | Refs | Files |
|---|---:|---:|
| OpenFPS.Tests | 3,088 | 122 |
| OpenFPS.AudioLab | 1,353 | 47 |
| OpenFPS.Sound | 807 | 29 |
| OpenFPS.Server | 535 | 42 |
| OpenFPS.Client.Core | 409 | 28 |
| OpenFPS.Common | 45 | 10 |
| OpenFPS.Client, OpenFPS.Client.Gtk | 6 each | 1 each |

Sound and the client's audio reading the mechanics, by type: `VehicleProfile` 208 + 100, `EngineProfile`
189 + 2, `Driveline` 136 + 29, `TyreProfile` 90 + 1, `Gearbox` 39 + 9, `WindField` 28 + 16, `WheelDynamics`
14 + 28, `WindWeather` 25 + 1, `TyreFriction` 18 + 2, `WindAir` 15 + 3, `VehicleSpec` 15, `WheelState` 0 + 15,
`ChassisSpec` 3 + 11, `BodyConstants` 10, `RoadSurfaces` 4 + 6, `AxleSpec` 5 + 3, `Air` 6, `TyreSize` 2 + 2,
`BrakeKind` 0 + 2, `PhysicsUtils` 0 + 2 (Sound + Client.Core audio).

Statics that can change in what moves: `PhysicsUtils`' five thread-static scratch lists
(`PhysicsUtils.cs:13-17`); tables and caches filled once (`RoadSurfaces.Table` and `ByName`,
`Chassis.cs:249, 260`; `MagicCurve.Cache`, `WheelDynamics.cs:21`; `ExternalBallistics.Mach`, `Cd`,
`_zeroCache`, `ExternalBallistics.cs:65, 75, 175`; `Ricochet._faces`, `Harmonics`, `Ricochet.cs:89, 309`;
`VehicleProfile._cache`, `Vehicles.cs:633`; `WindField.Eddies`, `Wind.cs:45`; `WindWeather.Default`,
`Wind.cs:242`); and in the stepper, `DrivingSystem._running` (per world) and `_mapId`
(`DrivingSystem.cs:95, 100`). None is written from outside its type.

### B. The crossings

All 62, in `tools/physics_boundary/physics_crossings.tsv` (from type, file, line, to type, its class, the
member named, the member it is named in). By file: `OpenFPS.Sound/VehicleSpec.cs` 28, `OpenFPS.Sound/Vehicles.cs`
15, `OpenFPS.Sound/ExternalBallistics.cs` 12, `OpenFPS.Common/SharedMovementEngine.cs` 5,
`OpenFPS.Sound/WheelDynamics.cs` 2.

### C. The presets' members by who reads them

- `VehicleProfile`, read by both: Body, ByName, DoorChime, DragArea, Engine, FrontAxleZ, Gearbox,
  HeightMetres, HornFor, LengthMetres, MassKg, Name, Presets, RearAxleZ, RollingResistance, Running,
  SourceLevelDb, TyreCount, Tyres, WidthMetres. By the sound only: AirSystem, CoolingFan, ElectricFan,
  EngineAtRear, EngineBay, EngineBayLeakage, EngineKey, ExhaustAxis, ExhaustHeight, ExhaustOffset,
  ExhaustOffsetX, ExhaustOffsetZ, ExhaustSlot, FanClutch, FanDriveRatio, FrontTapHeight, FrontTapZ,
  IntakeHeight, IntakeOffsetZ, PascalsAtFullScale, PeakHeadroomDb, Siren. (Some read by both are the sound's
  for the server's own reasons: `HornFor` and `DoorChime` are read by the server to emit signals, which is
  not mechanics; they stay with the sound half.)
- `EngineProfile`, read by both: IdleRpm, Name, PeakTorqueNm, PeakTorqueRpm, RedlineRpm. The other 79 by the
  sound only.
- `TyreProfile`, by the mechanics only: CorneringStiffness, CorneringStiffnessLoad, LateralCurvature,
  LateralShape, LoadSensitivity, LongitudinalCurvature, LongitudinalShape, SlipStiffness, SlipStiffnessLoad.
  By both: InflationKPa, PeakGripG, TreadDepthMm. By the sound only: ByName, Presets, RaceSlick, ReferenceDb,
  SportBikeRoad, SportsOnAsphalt, SquealDb, SquealHz, SquealQ, SurfaceRoughness, TreadBlocks, TruckOnAsphalt.
- `Gearbox`, by both: FinalDrive, Ratios, RpmFor, TopGear, UpshiftRpm, WheelRadiusMetres. By the sound only:
  CruiseUpshiftRpm, DownshiftRpm, LaunchRpm, ShiftSeconds, SixSpeedSports.
- `ChassisSpec`, by the mechanics only: the constructor, Ackermann, CentreOfGravityHeightMetres,
  DefaultTrackOverWidth, FrontRollStiffnessShare, FrontWeightShare, Groups, LockFromTurningCircle,
  MaxSteerAngleRad, YawInertiaIndex. By both: Axles, CentreOfGravityZ. By the sound only: Default, MainBrake,
  PlacedOn, SteeredTyres.

## 14. Stage 0 as built (2026-10-11): the guards and the Jolt trial

Built on main `13ae8b27`. No game code changed: the guards are tests in OpenFPS.Tests with their stored data in
`OpenFPS.Tests/PhysicsBoundary/`, and the instruments are in AudioLab. The walks, drives, traffic, trains and
ballistics are produced by one rig, `OpenFPS.AudioLab/Spikes/PhysicsReplays*.cs`, which the tests compile in as
they do `DistantUpdatesRig`, so the lab and the tests run the same code. All of it runs on GitHub Actions with
the rest of the suite; none of it needs FMOD, Steam Audio or Jolt.

### 14.1 The ratchet: `PhysicsBoundaryTests`

- `types.tsv` is the survey's sorting of every inventory type into P, PS, V and G (from
  `tools/physics_boundary/classify.py`, `RULES`; the server's `DrivingSystem` is left out, nothing in the
  sorted projects can reach it).
- `NoNewCrossings` binds the same compilation as `LibraryBoundaryTests` (Common, Client.Core, Geometry,
  Acoustics, Sound, Native, Audio) and counts, in the declarations of every P and V type, each name that binds
  to a repository type outside P, PS, V, G and OpenFPS.Geometry, keyed by the two types. Each count must equal
  its line in `allowed.tsv`. It finds what the survey found, line for line: 62 in five pairs
  (`VehicleChassisSpec` to `TunableAttribute` 28, `Gearbox` to `TunableAttribute` 15, `ExternalBallistics` to
  `WeaponDefinition` 12, `SharedMovementEngine` to `ColliderShape` 5, `WheelDynamics` to `RoadWaterLaw` 2).
- Keyed by type rather than file, so stage 1's moves need no change to it.
- `EverySortedTypeIsFound`: a renamed or moved type cannot drop out of the count unseen.
  `TheAllowanceIsNoMoreThanTheSurveyFound`: the sum never goes above 62.
- After stage 1 it gains the assembly test of section 9 (`OpenFPS.Physics` references the runtime and
  Geometry only).
- Cost: about 3 s.

### 14.2 The movement replay: `MovementReplayTests`

Nineteen walks (`PhysicsReplays.Walks`), each a script of keys from one spot:

| Walk | Map | What |
|---|---|---|
| `city_street_held`, `city_street_sprint` | city | W held, and a sprint, along the plaza from the spawn |
| `city_street_taps`, `city_strafe_taps` | city | twelve taps of W; taps of A, D and S (one tap is one tick, 15 cm) |
| `city_kerb` | city | off the 7 cm kerb onto Foundry Street and back up it |
| `city_jump` | city | a running jump, then a standing one |
| `city_doorway_lobby` | city | in through Brandt Court's front door, across the lobby, through the stairwell door (both opened at load) |
| `city_stairs_up`, `city_stairs_down` | city | Brandt Court's first flight, nineteen steps, both ways |
| `city_roof_fall` | city | off Brandt Court's west side at roof height, eighteen metres down |
| `city_parked_car` | city | standing on a parked car (a moving box) and walking off its end |
| `city_bus_side`, `city_two_cars` | city | into the side of a standing bus and along it; along two parked cars nose to tail |
| `magnolia_spawn_held`, `magnolia_spawn_taps` | Magnolia | held and tapped over the ground from the spawn |
| `magnolia_slope_up`, `magnolia_slope_down` | Magnolia | the steepest smooth ground near the spawn (7 %, found by `--physics-replay hills`), up held, down tapped then running |
| `magnolia_house`, `magnolia_barn` | Magnolia | about the house at 19617 and the barn at 19415 Turtle Dove Lane |

How a walk is taken:

- The server's side is `MovementSystem.Update` with a real session and queued inputs, the body made as a
  login makes it (a solid cylinder, indexed), the moving things filed again in the grid each tick as
  `Program.cs` does.
- The client's side is `ClientPhysicsSystem.Predict` over the client's world as a streamed client holds it
  standing at the start: the tiles `TileStreamer.Begin` sends there, and the moving things' definitions.
- Each tick the client starts from the server's state, as the reconciler replays from it, so the gap is what
  one step on each side makes of the same body.
- The ground memo ages as it would at 30 ticks a second, whatever the test machine's speed: it is forgotten
  when the game's memo would have aged past 150 ms. A tick that took the machine over 150 ms is detected and
  the walk retried.

What is stored and checked:

- `walks/NAME.tsv`: the server's body each tick (position to 10 µm, velocity, grounded) and the gap. Compared
  to 0.1 mm in position, 1 mm/s in velocity. Plain text, so a change in movement is a readable diff.
- Every walk is taken twice and must take the same track (no wall clock, no order dependence).
- The rig carries its own copy of `MovementSystem`'s ground, gather and context; it must agree with the real
  step on every tick, or the risks below would be measured on some other step. It does, on all 19.
- The gap is held to 1 mm. A walk with a known gap carries `KnownGapMm` and its reason; the test fails if the
  gap grows past it, and asks for it to be taken off once the gap is under 1 mm.
- `walks.tsv`: one line per walk with the three risks, compared too.
- The replay classes run in a collection of their own with parallelism off: `MovementSystem` keeps its
  gather's scratch lists in statics meant for the one tick thread, so two test classes stepping it at once
  share them.
- Cost: the four replay classes together (walks, drives, traffic, trains, ballistics) took 32 to 45 s here, each run twice.

### 14.3 The three parity risks of 3.7, measured

The walks, and `--physics-parity` over 4,000 standing points each on the city and on Magnolia (14.6):

1. **The moving things in the server's ground probe: a gap today.** On the parked car the server's floor is
   the car's roof and the client's is the road 1.5 m below; from the same state the client falls 11 mm a tick
   (39 mm on the first), on all 40 ticks the body is on the car. `city_parked_car` carries
   `KnownGapMm = 40`. Over the sweep, every point on a parked box differed (396 of 396 on the city, 400 of 400
   on Magnolia), and every one of the differences came from the moving things; one step from rest on a box
   parted the two sides on 324 of 400 points on Magnolia, by up to 3.5 m. No point on a floor of the map
   differed. In play the client is pulled back each round trip and falls again: a body standing on a car, a
   bus or anything else that moves jitters by a centimetre a tick (heard as steps and landings).
2. **The ground memo's stale answers: no gap in any walk.** The memo was used on 17 to 163 ticks a walk
   (standing, tapping, pressing into a bus), and its floor never differed from a fresh probe's by over 1 mm,
   because a body that moves under 5 cm in a tick is standing still or pressed against something, and the
   floor under it has not changed. The sweep shows where it could: a point within 5 cm of another has a floor
   over 1 mm different at 32 of 3,991 city points (kerbs, treads, slab edges; worst 1.42 m at a parapet) and
   308 of 2,899 on Magnolia (the terrain's slope; worst 0.27 m), and the step from that remembered floor is
   off by up to 0.34 m. To bite, a body must creep less than 5 cm a tick across such an edge, or the floor
   must move under a still body within 150 ms (a lift, a vehicle).
3. **The two gathers: no gap.** On `city_two_cars` the sets were the same boxes in another order on every
   tick (85 of 85), and in another order at 27 of 3,991 sweep points on the city; the step with either list was
   the same to the bit everywhere (0 of 3,991 and 0 of 2,899 over 1 mm). The deepest-contact tie the survey
   feared did not occur. The orders differ because the server reads the grid's cells and the client its
   snapshot's list.

Item 1 is the one to close in stage 3 (the one world view both sides fill); 2 and 3 are recorded.

### 14.4 The driving, traffic and train replays: `DrivingReplayTests`, `RailReplayTests`

- Four drives through `DrivingSystem.Update` with a seated driver whose inputs go through `ApplyControls`, as
  `MovementSystem` hands a driver's packets on: `city_main_street` (crank, eight seconds' throttle, coast,
  brake to a stop, reverse), `city_turn` (away from a standstill and right across the Central Street junction
  at speed), `city_wall` (steered into the tunnel's west wall: the stop and the impact), `magnolia_hill` (up the
  7 % dirt slope, the pose pitching with it). Stored per tick in `drives/NAME.tsv`: position, heading, speed,
  steer, throttle, brake, the pose's quaternion, tyre demand, and each wheel's bytes as the wire carries them.
  A fresh `World` per class, so no `_running` state is carried in.
- Traffic: the city's `VehicleSystem` for 60 s on a freshly loaded map, every moving thing sampled once a
  second (188 movers: cars, buses, walkers, shuttles, aircraft), each with its wheels' bytes:
  `traffic.tsv.gz`, 11,232 rows.
- Trains: the city's `RailSystem` alone for 90 s, every source of every train once a second (24 movers):
  `rail.tsv.gz`, 2,160 rows.
- Each runs twice and must agree with itself before it is compared. Positions to 0.5 mm (drives) and 2 mm
  (movers), angles and speeds to 1e-3, the wheels' bytes to one step of their encoding.
- Traffic and trains load the city twice each, a second a load.

### 14.5 The ballistics table: `BallisticsTableTests`

`ExternalBallistics.Fly` for every weapon in `WeaponRegistry` (seven), six cases each: zeroed at 100 m on a
standard day, level, raised 5 and 30 degrees, zeroed in a 5 m/s crosswind, zeroed on a hot day at altitude
(35 C, 850 mb); at 50 m steps to 300 m for a pistol or shotgun round and 100 m steps to 1,000 m for a rifle's.
Stored in `ballistics.tsv` (326 rows): height and drift to 0.2 mm, time to 20 µs, speed to 2 cm/s. The m700
zeroed at 100 m is 14.78 m low at 1,000 m, 2.10 s out, at 307 m/s.

### 14.6 The parity harness: `--physics-parity`

`--physics-parity [map=city] [n=4000] [movers=30] [seed=1]`: standing points on floors near every kind of
solid (and one in ten on a parked box), and at each, every question a later stage moves asked of the old path
and the new (`Paths`: the server's ground probe, the client's, the server's step), every answer compared to the
bit. At stage 0 the new path is the old one and all three agree on every point, which proves the harness;
stages 1 and 2 put their moved code in the new column and must show none. The same points sweep the three
risks (14.3). Cost: 7 s on the city, about 20 s on Magnolia with its load.

### 14.7 Regenerating the stored data

The same rule as the sound library's (docs/SOUND_LIBRARY_BOUNDARY.md 13.4): the stored tracks, tables and
allowance change only in the commit that changes the physics or the boundary on purpose, and that commit says
why. A move, a split, a constant made one: none may change any of them, except stage 2's one gravity, which
regenerates them in its own commit.

| Guard | When it changes | How |
|---|---|---|
| `allowed.tsv` | a crossing fixed: lower it | `OPENFPS_BOUNDARY_WRITE=1` with the filter `PhysicsBoundaryTests` (only lowers) |
| `types.tsv` | a type renamed, or sorted into another group | by hand, with `classify.py` |
| `walks/`, `walks.tsv`, `drives/`, `traffic.tsv.gz`, `rail.tsv.gz`, `ballistics.tsv` | movement, driving, traffic, trains or ballistics changed on purpose | `OPENFPS_PHYSICS_WRITE=1` with the filters `MovementReplayTests`, `DrivingReplayTests`, `RailReplayTests`, `BallisticsTableTests` |
| a walk's `KnownGapMm` | a parity risk fixed | lower it, or take it off below 1 mm |

`--physics-replay [walks|drives|traffic|rail|ballistics|all] [name=] [track]` prints what each guard stores
without writing it; `hills map=` finds the steepest smooth ground on a map.

The tracks were written on this machine (glibc 2.43). Each store says which maths it was made on, and another
machine compares by the rule for other maths (14.9).

### 14.8 The Jolt trial: `--jolt-trial`

JoltPhysicsSharp 2.22.0 (2026-07-13; 2.23.0 of 2026-10-05 was under two weeks old), in AudioLab only.
Licence MIT, confirmed in the package (`<license type="expression">MIT</license>`); its native package,
JoltPhysics.Native 1.1.0, carries `LICENSE.MIT.txt` (Jolt Physics, Jorrit Rouwe). Both packages set
`requireLicenseAcceptance`; restore accepted it without a prompt.

**It runs on this machine's Linux x64.** The native library is `libjoltc.so` (2.0 MB,
`runtimes/linux-x64/native/`, needing only libstdc++, libm, libgcc_s and libc); `joltc.dll` for Windows x64 and
ARM64, and builds for Linux ARM64, macOS and Android, in the same package. It would ship through
`OpenFPS.Native` as libfmod and libphonon do. Initialising Jolt costs 2 MB.

Our geometry reaches it unchanged:

- One city tile (the 250 m square about the spawn cut from the city's 13 static pieces, every triangle that
  reaches into it): 29,490 triangles as one mesh shape in 11 to 19 ms, 5.8 MB.
- The whole city as one mesh: 71,100 triangles in 25 ms, 11 MB.
- Every building and box on Magnolia as one mesh: 393,382 triangles in 0.17 to 0.25 s, 77 MB (vertices
  unshared, three a triangle; sharing them would cut it). No mesh was too big for one shape.
- 64 balls dropped on the city tile's mesh all came to rest on our own floor there (`FloorAt`), 0.0 mm off.
- Magnolia's ground at the spawn, 126 by 126 posts 2 m apart, as a height field in 1 to 2 ms: its posts are
  within 3.6 mm of ours (Jolt stores samples in 8 bits a block), and 62 of 64 balls came to rest on our terrain
  to 2 cm, the other two 2 cm and 15 cm off at a cell carved under a building's slab or the tile's edge (Jolt is
  given no holes).
- Units and axes: metres, y up, and the same right-handed maths and anticlockwise front faces as our
  triangles (`Normal = E1 x E2`), so +X east, +Y up, +Z north go in as they are, with no mirror (Steam Audio
  needed one, `Phonon.World`). Gravity is set per `PhysicsSystem`, which fits gravity as a property of the
  world (9.80665 here).

Measured, single-threaded (no worker threads, as the server's tick thread would run it), pinned to the
lab's cores:

| Scene, at 120 Hz | Step |
|---|---|
| the city tile, no bodies | 0.002 ms |
| 50 bodies (boxes and balls dropped from 2 to 12 m) | 0.08 ms while falling, 0.02 to 0.09 ms after; all asleep by 8 s |
| 500 bodies | 0.21 to 0.22 ms mean while falling (p95 0.31 ms, worst 0.7 to 1.3 ms), 0.16 ms after (worst 2.8 to 3.4 ms); 129 still awake at 8 s; 5.6 to 6.1 MB |
| the same with three worker threads | 500 bodies 0.24 ms falling, 0.13 ms after: no faster at this size |

Four steps a server tick for 500 bodies is under 1 ms of the 33 ms tick.

- **Ten 0.5 m boxes stacked** settle and all sleep: after 10 s the top box's centre is at 4.7473 m (4.75 exactly
  touching; 2.7 mm of penetration over ten contacts), the most any drifted sideways 11 mm.
- **A bullet-sized sphere at a 10 cm wall** (7.8 mm across, from 0.3 m, gravity off): at 800 m/s, 6.7 m a step,
  discrete goes through and linear cast stops it; at 100 m/s, 0.83 m a step, the same; a 6 cm stone at 15 m/s
  is stopped either way. Jolt clamps a body to 500 m/s unless `MaxLinearVelocity` is raised. Our bullets stay
  `ExternalBallistics` (the decision), so this is for thrown and knocked things.
- **Soft bodies:** a soft ball 1 m across (266 vertices, 2 kPa) dropped from 1 m rests on the ground standing
  1.055 m tall and 1.047 m wide; a 2 m cloth (441 vertices) dropped on a 1 m table 0.8 m high drapes over it
  to 3.7 cm off the floor. The two together step in 0.15 to 0.47 ms mean (worst 1.8 to 9.9 ms, the first
  contacts).

Against Jolt, or to watch:

- **The binding has bugs.** In 2.22.0 `BodyCreationSettings.MotionQuality`'s setter passes the enum by
  reference to a C function that takes it by value: every body made with it got quality 244, read back, and
  continuous collision silently did nothing. The body interface's `SetMotionQuality` works. Our own types over
  it (the decision) are where such things are worked round; each binding call wants a test that reads back
  what it set.
- **A second copy of the world.** Every tile's triangles are handed over again (and the place's 393,000
  triangles took 77 MB as built here); door leaves and streamed tiles must be kept in step, as for Steam Audio.
- **Cross-machine determinism is not checked.** Jolt has a cross-platform deterministic build option; whether
  the NuGet's native build has it was not looked at. Bodies are not predicted on the client (5.4), so only
  run-to-run repeatability on the server matters, and that was not measured either.
- **The worst steps** (3.4 ms with 500 bodies, 9.9 ms for the first soft contacts) are single steps, not
  sustained, but four a tick would take a 33 ms tick past a frame's worth now and then.
- Nothing found conflicts with our geometry: no mesh too big, no axis or winding change, metres throughout.

### 14.9 Other machines: the maths line, the drift and the slip

GitHub's runner parted the traffic from its stored copy twice on 2026-10-11, with no physics changed: a heading
of pi against -pi (now compared round the circle), then a Twin-turbo Cummins 6 at second 57, z 138.089 against
138.087 stored, just past the 2 mm allowed. Its libm and CPU are not this machine's, and a minute of
integration carries the difference. The physics stores now do what the emitter-stream replay does
(docs/SOUND_LIBRARY_BOUNDARY.md 13.3):

- **The maths line.** Every store's first line is `# maths <fingerprint>` (`CrossMachine.MathsFingerprint`):
  the sound guards' libm probe (`RenderFingerprintTests.MathsProbe`: the bits of every libm function the models
  call, and the vector width), the instruction sets the JIT may use for the vector types (AVX-512, AVX2, FMA),
  and the bits of the `System.Numerics` operations the steps call (normalise, transform by a quaternion,
  slerp, cross, dot, length). Here it is `8;e39fea91ef91332c21b478ff;avx512+fma+v512;7ae9ca4cf97a9f93`.
  Adding the line changed no row of any store. On GitHub the libm probe already differs (the emitter streams
  say so in shard 5).
- **The same maths: as before.** Positions to 0.1 mm (walks), 0.5 mm (drives) and 2 mm (movers), the rest as
  in 14.2 to 14.5. A refactor that changes nothing changes no row past float rounding.
- **Other maths: the drift and the slip** (`CrossMachine`, one rule per store in `PhysicsReplayTests.cs`).
  `OPENFPS_PHYSICS_FOREIGN=1` compares as other maths would, to see the rule here.

How the two machines part, measured. A minute of traffic and a minute and a half of trains were run with every
tick's step rounded a float unit longer or shorter at random (`PhysicsCrossMachineTests`), rounding everywhere
on every tick, a harder push than a different libm:

| Store | Seconds | Worst drift, rounded every tick | GitHub |
|---|---:|---|---|
| traffic, 188 movers | 60 | 1 mm (one printed step) in position, 1e-4 rad, 1 mm/s, one wheel step | 1 to 3 mm at 57 s, 1e-4 rad |
| trains, 24 sources | 90 | 1 mm, 1e-4 rad | within 2 mm |
| four drives | 8 to 17 | 0.1 mm (one printed step), 1e-4 rad | within 0.5 mm |

The steps are contractive: drivers hold their lines, so rounding does not grow; it stays at the printed
resolution. The one thing that did grow was a decision. One walker on Foundry Street started a tick later and
stood 46 mm behind (one tick at 1.4 m/s) for three seconds, then caught up. A threshold (a start, a stop, a
gear, a give way) crossed a tick early or late moves a body along its own path by its speed times the ticks.

So the rule for other maths has two parts:

- **Drift**, on every row: the same-maths tolerance plus, per simulated second, 1 mm of position, 1 mm/s of
  speed, 1e-4 rad of heading, and a wheel step a minute. That is twenty times GitHub's worst position drift
  (about 0.05 mm/s) and fifty times its heading drift. At the end of the minute a car may be 62 mm off; a lane
  is 3.5 m.
- **Slip**, for a row past its drift: it passes only if it is where the body would be two ticks along or back
  its own path (twice the slip seen). Along its stored heading: drift plus speed times 2/30 s (1.1 m for the
  fastest car, 16 m/s). Across it: drift plus what a slip on the tightest turn (5 m) puts off the tangent,
  under 0.12 m. Height: drift plus the vertical speed (walks) or the speed (vehicles) times the slip. Speed:
  plus 10 m/s² times the slip (0.67 m/s). Heading: plus 1.5 rad/s times the slip. On a slipped row the
  wheels, controls, pose, grounded flag and gap are not compared: a tick turns a slip ratio from nothing to a
  skid. A row whose wheels or controls are past their drift while the body is where it was stored fails.
- A flight has no decisions, so the ballistics table has drift only, growing with the time of flight (1 mm of
  height and drift, 10 µs and 1 cm/s a second). `walks.tsv`, the risk counts, allows a count to move by two
  and a worst case by half a millimetre.

What it still catches, each a test in `PhysicsCrossMachineTests` on the stored traffic: a car in the next lane
(3.5 m across), half a lane over (1 m across, against a bound of 0.1 m), a car that stops (10.5 m/s off in the
next second), its wheels changed where the car is the same, and a walk a stair tread lower; and it passes a car
a tick along its way with a skid begun early, and a minute of drift at half the bound. The rounded-every-tick
traffic must pass it on every run (4 rows passed as slips here). Two deliberate changes to `VehicleSystem`,
tried with `OPENFPS_PHYSICS_FOREIGN=1` and reverted: one car's lane offset by 1 m failed at second 1 ("0.996 m
across its path, past 0.0314"); every routed car's top speed 3 % lower failed at second 2 ("0.829 m along its
path, past 0.541").

The movement walks' 1 mm gap between the server and the client is unaffected: both sides run in one process
on one machine, and the gap is checked on every run whatever the store's maths.

Regenerating on another machine writes that machine's maths line, and then this machine compares by the rule
for other maths until it is regenerated here.

Cost: `PhysicsCrossMachineTests` about 6 s, nearly all of it the rounded minute of traffic.

## 15. Stage 1 as built (2026-10-12): the project, the crossings, the moves

Made on Windows and sent back as patches, on main `91aee50e`. Nothing a player sees or hears changes: every
number is where it was, and the guards pass unregenerated.

### 15.1 Two projects

- **`OpenFPS.Physics`** references `OpenFPS.Geometry` and `OpenFPS.Schema`, and nothing else of ours.
  `OpenFPS.Sound` and `OpenFPS.Common` reference it; the server, the client, the lab and the tests reach it
  through them. Namespaces are the ones the files had.
- **`OpenFPS.Schema`** (decision 7) is the editor's attribute alone, `TunableAttribute`, in the namespace it
  had (`OpenFPS.Common.Editing`). It references nothing. The field descriptors and the model kinds that read
  the attribute stay in `OpenFPS.Sound/Editing`.

### 15.2 The four crossings

| Crossing | Refs | What was done |
|---|---:|---|
| `SharedMovementEngine` to `ColliderShape` | 5 | the enum and its `IsRound` are `OpenFPS.Geometry/ColliderShape.cs`, namespace unchanged |
| `Gearbox` and `VehicleChassisSpec` to `TunableAttribute` | 43 | the attribute is `OpenFPS.Schema/Tunable.cs` |
| `ExternalBallistics` to `WeaponDefinition` | 12 | the flight takes a `Projectile` (muzzle speed, G7 coefficient, mass, diameter), which `WeaponDefinition.Projectile` makes; the torso's width is `BodyConstants.TorsoWidth` |
| `WheelDynamics` to `RoadWaterLaw` | 2 | the wet road's grip (`RoadTexture`, the surfaces' table, `HoldsMm`, `AquaplaningKmh`, `GripFactor`) is `RoadGripLaw` in `OpenFPS.Physics/RoadGrip.cs`; the water's own laws and the squeal stay in the sound's `RoadWaterLaw` |

Each is its own commit, and the physics ratchet's allowance went 62, 57, 14, 2, 0.

### 15.3 What moved

Whole, with `git mv`: `BodyConstants.cs`, `Chassis.cs`, `RunningGear.cs`, `WheelState.cs`,
`ExternalBallistics.cs`, `Wind.cs`, `WindModel.cs` (from `OpenFPS.Sound`) and `SharedMovementEngine.cs` (from
`OpenFPS.Common`). `Gearbox` out of `Vehicles.cs` into `OpenFPS.Physics/Gearbox.cs`.

Two small seams let two of them move whole, with no number changed:

- **`Chassis.cs`** named the sound's preset in two methods. `ChassisSpec.PlacedOn` takes the two axle
  positions and `ChassisSpec.Default` the tyre count, width and mass; `VehicleProfile.Running`, their one
  caller, hands them over.
- **`SharedMovementEngine.cs`** read four of the game's constants. `PlayerRadius`, `StepHeight`,
  `CollisionSearchRadius` and `DefaultGroundCheckLimit` are `BodyConstants`' now, and `PhysicsConstants` reads
  them from there, as it already read the walk, the run and gravity.

### 15.4 Not moved: `WheelDynamics.cs`

Section 7 lists it to move whole. It cannot yet: it holds a `TyreProfile`, reads `TyreFriction.G` and
`TyreFriction.SquealOnset`, and is made from a `VehicleProfile`, three types that stage 2 splits between the
physics and the sound (4.2). It stays in `OpenFPS.Sound` until that split, `MagicCurve` with it, and reads the
running gear and the grip law from the physics. Its crossing (the road water) is fixed.

### 15.5 The guards

- `PhysicsBoundaryTests.NoNewCrossings`: the allowance is empty. A type in `OpenFPS.Physics` or
  `OpenFPS.Schema` counts as the physics' own, as one in `OpenFPS.Geometry` did. `Projectile`, `RoadGripLaw`
  and `RoadTexture` are on the list of sorted types (`types.tsv`, `tools/physics_boundary/classify.py`).
- `PhysicsBoundaryTests.PhysicsReferencesOnlyGeometry` (section 9): the assembly references the runtime,
  `OpenFPS.Geometry` and `OpenFPS.Schema`.
- `PhysicsBoundaryTests.TheSoundNeverStepsThePhysics` (section 9): nothing in `OpenFPS.Sound` names a stepper.
  One stepper today, `SharedMovementEngine`; a physics world and a vehicle step join the list when they exist.
- The library's ratchet sorts both new projects (`LibraryBoundaryTests.SortedProjects`, `files.tsv`,
  `tools/sound_boundary/classify.py`), lets the sound reference them, and holds that `OpenFPS.Schema`
  references nothing. `Components.cs` is a host file now: its one library type left it.

### 15.6 The wire hash and the door fingerprint

- `WireContract.Hash` covers `OpenFPS.Physics` and `OpenFPS.Schema` (`OpenFPS.Common.csproj`,
  `WireLibrarySource`): `WheelState`, `WindAir` and `BulletState` are on the wire. It changes, as files it
  covers moved (812896a959fb at `91aee50e`, 23112534dcd8 after). Nothing sent changed. A client and a server from either
  side of this refuse each other at login.
- `DoorModelFingerprint` is unchanged (ab2b953cc671): it lists no file that moved.

## Decisions (Cody, 2026-10-11)

- Approved as recommended, all ten (section 8), with two changes:
- **Soft bodies as well as rigid ones** ("I will want soft bodies as well as solid bodies"). This changes the
  rigid-body engine decision: our own solver was recommended for rigid bodies alone. Checked 2026-10-11 on
  the projects' own pages: Jolt Physics (MIT) lists soft bodies (a soft ball, cloth), ragdolls, wheeled and
  tracked vehicles, a full set of constraints, height-field terrain and triangle meshes, deterministic
  simulation, and x64 and ARM64 on Windows and Linux; JoltPhysicsSharp (MIT) binds it for .NET 9 and 10.
  BepuPhysics v2 (Apache-2.0, pure C#) lists no soft bodies.
- **The engine: Jolt Physics through JoltPhysicsSharp** (Cody: "yes record that"). Jolt solves rigid and soft
  bodies, joints and contacts inside `OpenFPS.Physics`, behind our own types: nothing outside Physics sees
  Jolt. Ours stays ours: the geometry (our BVH and Embree are the truth; each tile's shapes are handed to Jolt
  as they are to Steam Audio, and kept in step), the approved walk, the tyre model, ballistics, air and wind,
  fire, water, and every contact turned into a heard impact. The native library ships as FMOD's and Steam
  Audio's do (OpenFPS.Native). Stage 0 measures its cost and its soft bodies in our scenes before anything
  depends on it.
- **Gravity is a property of the world** ("adjusting the gravity as a property of the world will effect
  everything on the world"): one value per world, 9.80665 m/s² by default, read by everything that falls,
  rolls, flies, sloshes or sways (players, bodies, vehicles, projectiles, water, fire's buoyant plume,
  thrown objects), never a constant in code. Stage 2's "one constant per law" removes the 40 copies of 9.81.
