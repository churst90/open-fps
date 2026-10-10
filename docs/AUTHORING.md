# Authoring maps and prefabs

This is the format the loader actually reads. It is written against two classes, and those classes are the
spec:

- **`OpenFPS.Server/Repositories/PrefabTemplate.cs`** — one prefab file.
- **`OpenFPS.Server/Repositories/MapRepository.cs`** (`MapData` and `EntityData`) — one map file.

`OpenFPS.Server/prefabs/prefab-schema.json` is generated against `PrefabTemplate` and a test
(`PrefabSpecTests.Schema_DescribesExactlyTheTemplateClass`) fails if the two ever drift apart again.

> **Why this document exists.** The schema and the old `GEMINI_MAP_STANDARD.md` described two *different*
> formats, and the loader had read neither: the schema documented nested `Collider` / `SoundEmitter` /
> `Acoustics` objects and an integer `Type`; the map standard documented per-entity components. An author
> following either produced a file that deserialized into all-defaults and spawned an invisible, silent,
> materialless cube — with no error, because `System.Text.Json` drops a key it does not recognise without a
> word. Both descriptions have been replaced by this one, and the loader now rejects a prefab it cannot
> honour instead of spawning most of it.

---

## 1. Where the files live

Everything is resolved relative to the server's working directory, which `run-server.sh` sets to
`OpenFPS.Server/`:

```
OpenFPS.Server/
  maps/<mapid>.json          one file per map, named after its Id
  prefabs/<prefabid>.json    one file per prefab, named after its Id
  prefabs/prefab-schema.json the schema (skipped by the loader)
  meshes/<id>.mesh           imported mesh assets (section 5), read at start
```

Both loaders accept `//` comments and trailing commas, so a map can be annotated in place — the rooms map,
`OpenFPS.Tests/maps/default.json`, is, heavily, and is the worked example for everything below. It is a test
fixture: the server does not serve it.

## 2. Prefabs

A prefab is a template: a name, and the components an entity spawned from it receives. A map places
*instances* of prefabs.

Only `Id` and `Name` are required. **Omit** a field you are not setting — an omitted field means "do not
attach this behaviour", and is not the same as setting it to `0`. Enums are written by **name**
(`"Type": "Beacon"`, not `4`).

```json
{
  "Id": "chirp_beacon",
  "Name": "Chirp Beacon",
  "Type": "Beacon",
  "Material": "None",
  "HasEmitter": true,
  "SoundId": "BEACONS/siren",
  "Volume": 0.9,
  "Range": 25,
  "Mode": "LoopOne"
}
```

Each block below produces one component. `PrefabTemplate.cs` documents every field individually; this is the
shape of it.

| Block | Fields | Produces |
|---|---|---|
| Identity | `Id`, `Name`, `Description`, `Announce`, `Type`, `Material` | `NameComponent`, `IdentityComponent`, `EntityType`, `MaterialComponent` |
| Collider | `ColliderSize`, `Shape`, `IsSolid` | `ColliderComponent` |
| Health | `MaxHealth` | `HealthComponent` |
| Crowd | `CrowdPeople`, `CrowdReactRadiusMetres` | `CrowdComponent` |
| Item | `IsItem`, `ItemWeight`, `Hands`, `WeaponId`, `Premium` | `ItemComponent` |
| Acoustics | `Transmission{Low,Mid,High}`, `Absorption`, `Scattering`, `ShellThickness`, `LeafMetres`, `StudSpacingMetres`, `FaceMask` / `MissingFaces` | `AcousticComponent` |
| Physics | `Mass`, `Friction`, `Restitution`, `Drag` | `PhysicsPropertyComponent` |
| Emitter | `HasEmitter` + `SoundId`, `StartSoundId`, `StopSoundId`, `Volume`, `Range`, `MinDistance`, `Mode`, `EmitterDirection`, `EmitterOffset`, `Cone*`, `RepeatIntervalSeconds` | `SoundEmitterComponent` |
| Granular | `IsGranular`, `Granular*` | (same component) |
| Synth | `IsSynth`, `Synth*` | (same component) |
| Region | `IsIndoor`, `RoomSize`, `AmbienceId`, `ReverbScale`, `RoomMaterials` | `RegionComponent` |
| Portal | `RegionAId`, `RegionBId`, `ApertureSize` | `PortalComponent` |
| Door | `IsDoor`, `DoorKind`, `HingeSide`, `PushSide`, `KeyedSide`, `SwingSeconds`, `SwingDegrees`, `DoorSkinMetres`, `Slides`, `Powered`, `SensorMetres`, `CloseAfterSeconds`, `CloseSeconds` | `DoorComponent` (and a `PortalComponent`) |
| Beacon | `BeaconCategory` | (see Beacons below) |

`ColliderSize` is **full extents in metres**, multiplied by the map instance's `Scale`. So a
`concrete_wall` sized `2 × 3 × 0.5` placed with `"Scale": { "X": 5, "Y": 1.333, "Z": 1 }` is a 10 m wall,
4 m high, half a metre thick.

### Announcing

`Announce` decides whether the client SPEAKS this thing as the player walks within three metres of it.
Omit it and it follows `Type`: **true** for `Item`, `NPC` and `Beacon` — the things a player encounters —
and **false** for `StaticObject`, `Trigger` and `Projectile`.

The default is that way round because every entity is named: the walls, the floors, the auto-injected
foundation, the acoustic region volumes and the portals all carry a `Name` so that you and the logs can
refer to them. When the announcer spoke every named entity in range, walking through a doorway read the
`portal` prefab's authoring notes aloud, mid-stride.

Set it explicitly when the default is wrong for your thing:

```json
{ "Id": "stone_stairs", "Name": "Stone Staircase", "Type": "StaticObject", "Announce": true }
```

Keep `Description` short if you set `Announce` — it is read out after the name, every time the player
walks back into range.

### Materials

`Material` and `RoomMaterials` take material **names** known to `AcousticRegistry`
(`OpenFPS.Common/AcousticRegistry.cs`):

`AcousticTile`, `Asphalt`, `Audience`, `BootRubber`, `Brick`, `Carpet`, `Concrete`, `Dirt`, `Fence`,
`Foliage`, `Generic`, `Glass`, `Grass`, `Gravel`, `Leather`, `Marble`, `Metal`, `None`, `Plaster`,
`Plastic`, `Rubber`, `Skin`, `Tile`, `Water`, `Wood`

Steel is `Metal`. The validator's error message lists the current names if this list falls behind.

A name outside that list is rejected at load. (At runtime an unknown material silently becomes `Generic`,
which is right for robustness and wrong for authoring — hence the check.)

### Sound emitters

`HasEmitter` is the gate for the whole block. Setting `SoundId` (or any other emitter field) *without* it is
rejected, because the loader would otherwise attach no emitter and the object would be permanently silent.

- `Range` is where the voice stops being submitted; `MinDistance` is where it stops getting louder as you
  approach. `MinDistance` must be less than `Range`.
- **Directivity:** `ConeInsideAngle` / `ConeOutsideAngle` (degrees, inner ≤ outer) and `ConeOutsideVolume`.
  `360 / 360` — the default — is omnidirectional. A flat cone is a fiction: for anything that is a
  loudspeaker, use `Loudspeaker` below instead.
- **Loudspeakers.** `Loudspeaker` names a `loudspeaker` model (`pa_horn`, `megaphone`,
  `megaphone_shouted`) the recording in `SoundId` is played through: the client renders it through the
  speaker's amplifier, driver and horn, places it by the level that comes out (`MinDistance` is ignored),
  and beams it per band from its mouth toward `EmitterDirection` (the cone is ignored). `pa_speaker` and
  `space_megaphone` use it. A new kind of speaker is a new `loudspeaker` model with its datasheet's
  numbers (OpenFPS.Common/Loudspeakers.cs).
- **Aim:** `EmitterDirection` is a **local** direction, rotated into the world by the map instance's
  `Rotation`. Omit it for the default forward `(0, 0, 1)` and aim the entity with `Rotation`, which is what
  the rooms map does. It has no audible effect on an omnidirectional emitter.
- **Spin-up / spin-down:** `StartSoundId` plays once before the loop starts, `StopSoundId` plays once
  instead of cutting the loop off.
- `Mode` is `Single`, `LoopOne`, `LoopFolder`, `Sequential` or `StateMachine`.
- `IsSynth` generates the signal (no `SoundId` needed); `IsGranular` cuts grains from `SoundId`. They are
  mutually exclusive, and synth/granular parameters without their flag are rejected.
- **Physical models.** An `IsSynth` emitter whose `SoundId` has a model prefix is rendered by that
  model on the client, not by the `Synth*` oscillator: `machine:` (`ac_window`, `ac_condenser`,
  `mower_push`, `mower_riding`), `water:` (`park_fountain`), `fire:` (`fire_pit`), `foliage:`
  (`park_tree`, `pine`), `bell:` and `stove:` (`hob4`, `hob4_propane`, `hob1`: a gas hob, whose key also
  carries its knobs' settings, docs/GAS_HOB.md). The part after the colon is a preset name in the model library.
  A water feature with several landing places (`WaterFeatureSpec.Taps`) can instead be placed as one
  emitter per tap, `water:<preset>/<feature>/<tap>`: one synth feeds them all, each tap plays the
  water that lands there, and `<feature>` keeps two fountains of the same preset apart.

### Regions and portals

An **acoustic region** is a room volume the listener stands *inside*; it is what supplies reverb, ambience
and the room half of portal coupling. A region must not be solid, and needs a positive `RoomSize` — its
volume and surface areas are what the reverb time is computed from.

`RoomMaterials` names the six interior surfaces, in the order the reverb math reads them:

```
Floor, Ceiling, North, South, East, West
```

That is **not** the `FaceMask` bit order (`North 1, South 2, East 4, West 8, Top 16, Bottom 32`), which is a
genuine trap; both orders are spelled out wherever they are written down.

A **portal** is an opening joining two regions. It must not be solid. `RegionAId` / `RegionBId` are the map
`EntityId`s of the two regions, where `-1` means "the outside". A reusable portal prefab leaves both at `-1`
and lets the map entity name the pair, because which rooms a doorway joins is a property of where it was
placed. `ApertureSize` is the width of the opening in metres; leave it out (or at `0`) and it is derived
from the portal's own collider at map load, and the derivation is logged.

The same entity cannot be both a region and a portal, and a region cannot be solid geometry — a solid box
that declares itself a room puts the room's voxels inside a volume the listener can never enter.

### Collider shapes

`Shape` is `Box` (default), `Sphere`, `Cylinder`, `Cone` or `Polygon`; for the round shapes
`ColliderSize.X` is the diameter and `.Y` the height.

**Use `Box` for anything that should block sound.** Movement collides against the bounding box whatever the
shape, and Steam Audio's scene is built from box colliders only — so a solid sphere is walked around, hit by
the client's raycasts, and *not heard* as an obstruction. Loading one logs a warning saying exactly that.
For a round or shaped thing that is walked into, heard and struck as its shape, give the box a `Form`.

### Forms: shapes that fill the box

A box collider can carry a `Form` (docs/GEOMETRY.md 2.6 and 12): the shape the triangle world, Steam Audio,
the echoes, the ground probe and a body all meet, made on the server and every client from a few numbers. The
shape fills the collider's box, so `ColliderSize` (times `Scale`) is its bounding box and every older reader
of boxes keeps working. On a prefab or on a map entity (the entity's wins):

```json
"Form": { "Kind": "Cylinder" }
"Form": { "Kind": "Stairs", "Steps": 16, "Landing": 1.2, "Materials": ["Concrete", "Carpet"] }
"Form": { "Kind": "Roof", "Style": "Gable", "Outline": [0,0, 12,0, 12,8, 0,8] }
"Form": { "Kind": "Prism", "Outline": [0,0, 10,0, 10,10, 0,10], "Holes": [[3,3, 3,6, 6,6, 6,3]] }
"Form": { "Kind": "Swept", "Profile": [-0.075,0, 0.075,0, 0.075,0.21, 0.055,0.23, -0.055,0.23, -0.075,0.21],
          "Path": [0,0,0, 1,0,0] }
"Form": { "Kind": "Mesh", "Mesh": "3f9a0c2b7d1e4a65" }
```

| Kind | Numbers | What it is | Surfaces (`Materials` in this order) |
|---|---|---|---|
| `Wedge` | | a ramp: 0 high along its -Z edge, the box's height along +Z | body |
| `Stairs` | `Steps`, `Landing` | solid steps climbing toward +Z; each rise at most 0.4 m | body, treads |
| `Arch` | `Thickness`, `Segments` | a block with a half-elliptical opening along Z | body |
| `Cylinder` | `Segments` | an upright round column, trunk or tank (an ellipse if X and Z differ) | side, ends |
| `Cone` | `Top`, `Segments` | an upright cone (`Top` 0) or frustum (`Top` the top's radius over the base's) | side, ends |
| `Sphere` | `Segments` | a ball | body |
| `Dome` | `Segments` | the top half of a ball on a flat base | dome, base |
| `Prism` | `Outline`, `Holes` | an outline, x and z pairs, stood up the box's height; holes inside it | sides, top, bottom |
| `Roof` | `Outline`, `Style` | a solid roof from the eaves (the box's bottom) to the ridge (its top): `Hip` (the outline's straight skeleton, any shape), `Gable` (a hip whose triangular ends stand up), `Shed` (one slope rising toward +Z), `Flat`; the pitch is whatever the box's height makes it; sound goes through it as a 0.2 m deck (`Thickness` to say otherwise) | slopes, ends, underside |
| `Swept` | `Profile`, `Path` | a profile (across and up pairs, across to the right of the way the path goes) drawn along a path (x, y, z triples), mitred at each bend: a kerb, a rail, a moulding | sides, ends |
| `Mesh` | `Mesh` | an imported mesh asset by its id (section 5), stretched to the box | its own, named at import |

- Round shapes take as many sides as sound can tell apart when `Segments` is 0: 12 for a column of 0.3 m
  radius, 32 for a tank of 5 m, 8 for anything thinner than a quarter of a metre.
- An outline is in metres about anything (the shape is fitted to the box, so only its proportions bind);
  it must not cross itself; holes must be inside it and apart.
- `Materials` gives a material to each surface in turn; a missing or empty one is the thing's own
  `Material`. A roof's slopes are roofs (the scope's "on a roof").
- A form that cannot be made (stairs too steep, an outline that crosses itself, a mesh the server does not
  have) is logged at load, and the thing is its box.
- Each shape is closed and faces out, its convex pieces fill it, and the same numbers make the same bits
  everywhere (`GeometryShapeLibraryTests`).

## 3. Maps

```json
{
  "Id": "default",
  "Size":     { "X": 100, "Y": 40, "Z": 100 },
  "MinBound": { "X": -50, "Y": 0,  "Z": -50 },
  "MaxBound": { "X": 50,  "Y": 40, "Z": 50 },
  "SpawnPoint": { "Position": { "X": 0, "Y": 1, "Z": 0 }, "Rotation": { "X": 0, "Y": 0, "Z": 0, "W": 1 } },
  "VoxelResolution": 0.5,
  "OcclusionFloor": 0.15,
  "Entities": [
    { "EntityId": 100, "PrefabId": "concrete_floor", "Position": { "X": 0, "Y": 0, "Z": 10 },
      "Scale": { "X": 4, "Y": 1, "Z": 4 } }
  ]
}
```

**Map fields:** `Id`, `Description`, `IsDefault`, `Size`, `MinBound`, `MaxBound`, `PlayMin`, `PlayMax`,
`SpawnPoint`, `MinimumY`, `VoxelResolution`, `OcclusionFloor`, `Gravity`, `Temperature`, `Humidity`,
`AirPressure`, `AirAbsorptionMultiplier`, `AmbienceId`, `BeaconPolicy`, `Entities`, `Composites`,
and for traffic and rail: `Roads`, `Junctions`, `RoadStops`, `Tracks`, `Vehicles`, `Trains`,
`Crossings`, `StreetLife`. `OwnerId` and `IsPublic` are read too, but the server's
`map_access.json` overrides them for any map it lists.

- `MinBound` / `MaxBound` are the acoustic grid's bounds. `PlayMin` / `PlayMax` are where a player and
  anything they drive can go; left out, they are the bounds. `SharedMovementEngine` clamps position
  *and* velocity against them, so the edge of the map is a surface, not a cliff.
- `IsDefault` marks the map players land on at login. Exactly one map should set it (the speedway).
- `MinimumY` is computed at load (20 m below the lowest floor surface) — anything below it has fallen out
  of the world.
- `VoxelResolution` is the acoustic grid's cell size in metres; `OcclusionFloor` is the floor on occlusion
  (sounds never drop below it through a wall). Both are streamed to the client in the map manifest.

**Entity fields:** `EntityId`, `PrefabId`, `Position`, `Rotation`, `Scale`, `Name`, and the per-instance
overrides `IsIndoor`, `RoomMaterials` (or `Materials`), `RegionAId`, `RegionBId`, `ApertureSize`, and on a
door `PushSide` and `KeyedSide`.

`Name` is what a player hears: on a region or named place, as they walk into it; on a wall or door,
when they bump into or scan it ("Brandt Court front entrance").

A map entity does **not** list components. It names a prefab and places it; the overrides above are the only
things an instance may change, because they are the only things that are properties of *where it is* rather
than *what it is*.

`EntityId` is the handle other entities refer to (a portal names the regions it joins by `EntityId`). The
conventional ranges:

| Range | Use |
|---|---|
| 1–99 | system entities (floor, sky) |
| 100–499 | static geometry and rooms |
| 500–999 | beacons and interactables |
| 1000+ | assigned by the server at runtime (players, NPCs) |

These ranges are a convention for hand-written maps. The generated maps number their entities from
1001 upward.

If no entity sits at the origin with the `concrete_floor` prefab, the loader injects an auto-scaled
foundation so the map has a floor; it logs when it does. The spawn point is then dropped onto whatever floor
is under it, and a spawn point over nothing is reported and parked at Y = 2.

### A worked room

From the rooms map (`OpenFPS.Tests/maps/default.json`) — a concrete room with a doorway and a siren inside it:

```jsonc
// four walls with a 2 m gap at x -8..-6 in the south wall
{ "EntityId": 203, "PrefabId": "concrete_wall", "Position": { "X": -10, "Y": 2, "Z": 10 }, "Scale": { "X": 2, "Y": 1.333, "Z": 1 } },
{ "EntityId": 204, "PrefabId": "concrete_wall", "Position": { "X": -4,  "Y": 2, "Z": 10 }, "Scale": { "X": 2, "Y": 1.333, "Z": 1 } },
// the room itself: a non-solid volume with six concrete surfaces
{ "EntityId": 210, "PrefabId": "acoustic_region", "Position": { "X": -7, "Y": 2, "Z": 15 }, "IsIndoor": true,
  "RoomMaterials": ["Concrete", "Concrete", "Concrete", "Concrete", "Concrete", "Concrete"] },
// the doorway, joining the room to the outside
{ "EntityId": 211, "PrefabId": "portal", "Position": { "X": -7, "Y": 1, "Z": 10 },
  "RegionAId": -1, "RegionBId": 210, "ApertureSize": 2.0 },
// something to hear from outside it
{ "EntityId": 220, "PrefabId": "chirp_beacon", "Position": { "X": -7, "Y": 1.5, "Z": 15 } }
```

Walls are geometry; the region is the acoustics; the portal is the hole. All three are separate entities on
purpose — the walls are what Steam Audio traces against, the region is what supplies the reverb, and the
portal is what tells the engine the two rooms are coupled and where the sound comes through.

## Doors

A door prefab sets `IsDoor`. It is solid while shut and is always a portal: the map entity names the
two places it joins with `RegionAId` / `RegionBId`. `DoorKind` picks the hardware and so the sound
model (`docs/DOOR_TYPES_EVENTS.md`).

| Prefab | `DoorKind` | Opens | `PushSide` | `KeyedSide` |
|---|---|---|---|---|
| `door` | `knob` | swings | +1 | — |
| `steel_door` | `pushbar` | swings | −1 | — |
| `glass_front_door` | `glass-pushbar` | swings, closer | −1 | +1 |
| `glass_pull_door` | `glass-pull` | swings, closer | −1 | — |
| `auto_sliding_door` | `auto-slide` | slides, powered, sensor | — | — |
| `patio_door` | `patio-slide` | slides | — | — |
| `elevator_door` | `elevator` | slides, powered | — | — |

**Which way it swings.** A leaf's own +Z face is its outside. `PushSide` +1 means it is pushed from
the +Z face and swings away from it; −1 means it is pushed from the other face. A room door is pushed
from the corridor and swings into the room (+1). An exit door is pushed from inside by its bar and
swings out (−1). `HingeSide` (−1 left, +1 right) says which edge it swings on, or which way it slides.

**Keyed side.** `KeyedSide` +1 or −1 is the face that needs a key; from that face a shut door is
locked, and opening it is key, then hand. 0 or absent is never locked.

A map entity may override `PushSide` and `KeyedSide` for one door. Turn the leaf with `Rotation` so
its +Z face points the right way; `tools/gen_city.py` does this for every hinged door from the room it
belongs to.

## Named places

`named_place` is a non-solid box with a `Name` and nothing else. Walking into it changes the place name
you hear; it does not make a room, so the reverb and openings stay those of the region round it. Use it
for a flight of stairs, a landing, a garden gate. Where named places overlap, the smallest wins.
`/tp` accepts a named place or a region by name.

## Nature, water and fences

| Prefab | What it is |
|---|---|
| `water_fountain` | the sound of a fountain from one point: `water:park_fountain`, not solid. Put it over the water, not inside the pedestal |
| `elm_fountain_water_0`..`_4` | the Elm Park fountain's five taps (`water:park_fountain/elm_park/0..4`): the bowl, then the north, east, south and west sides of its rocks |
| `rock_boulder` | a boulder, scaled to size; material Concrete (there is no Stone material) |
| `water_surface` | standing water: a solid box of material `Water`, a hard reflector |
| `fire_pit` | a wood fire: `fire:fire_pit`, not solid. Put it above the ring of brick round it |
| `gas_hob`, `gas_hob_flame_safety`, `gas_hob_reignition`, `gas_hob_propane`, `gas_burner` | a gas hob (four burners on natural gas or propane, or one; with flame safety valves, or an auto re-ignition module): `stove:hob4`, not solid. Put it on the worktop with its back (+Z) to the wall; the interact key lights the next burner or turns them all off (docs/GAS_HOB.md) |
| `tree_crown` | the wind in a tree, heard from the middle of the crown: `foliage:park_tree`. Pair it with a `foliage_hedge` box for the crown and a trunk box |
| `fence_timber` | a close-boarded wooden fence, 1.8 m, solid, `Wood` |
| `fence_palisade` | a steel palisade, solid, `Fence` (porous: sound passes the gaps) |

None of these is a recording. Elm Park and 58 Alder Street on the city map are the worked example.

## Items

An item sets `IsItem` and `"Type": "Item"`. `Hands` is 1 or 2. `WeaponId` makes it a weapon.

- `Premium: true` marks an item only someone with `give-premium` may give (an admin). `teleporter` is
  one: carrying it is what lets a player use `/tp`.
- `admin_gun` has `WeaponId` `admingun`. Only a player with the `admin-gun` permission may hold or
  fire it.

## Generated maps

`city.json` and `speedway.json` are written by `tools/gen_city.py` and `tools/gen_speedway.py`. Do not
edit them by hand: change the generator and run it again from the repository root
(`python3 tools/gen_city.py`). `RoadNetworkTests.The_generator_reproduces_the_shipped_city` fails if
`city.json` differs from the generator's output.

## Beacons

A beacon is a short blip that tells a player where something is. Every beacon has a **category**:
`door`, `exit`, `stairs`, `item`, `vehicle`, `waypoint`.

Most are not placed. A door prefab (`IsDoor`) is a door beacon, an `Item` is an item beacon, and a
composite you can drive is a vehicle beacon, so a map with doors in it has door beacons without its
author doing anything. A prefab of `"Type": "Beacon"` names its own category with
`"BeaconCategory": "exit"` and defaults to `waypoint`.

A map decides which categories its players may hear, with a policy per category:

```json
"BeaconPolicy": { "door": "forced_on", "item": "forbidden", "stairs": "default_off" }
```

`default_on` (the default for anything unlisted) and `default_off` leave the choice to each player;
`forced_on` and `forbidden` do not. Players switch categories with `/beacons` (lists them and says
why each is on or off), `/beacons door` (switches it) or `/beacons door on|off`. Their choices are
kept on their own machine in `~/.config/openfps/beacons.json` and never reach the server.

## 4. What is checked at load, and what happens when it fails

**Prefabs are rejected.** `PrefabValidator` runs on every file. A prefab with errors is not registered, each
error is logged with the file that caused it, and a map entity referring to it fails to spawn with "was
REJECTED at load" rather than "not found". Warnings are logged and the prefab still loads.

The rules are all the same rule — *a prefab must not be able to describe something the engine will then
quietly ignore*:

- an unknown field (the loader would drop it silently);
- a duplicate `Id`, or a missing `Id` or `Name`;
- a material name the registry does not know;
- a collider with a non-positive extent, `IsSolid` with no collider, `Shape` with no collider;
- emitter settings with `HasEmitter` false, an emitter with nothing to play, `Type: "Beacon"` with no
  emitter, an inside-out cone, `MinDistance ≥ Range`, a zero-length `EmitterDirection`;
- synth or granular parameters without their flag, or both flags at once;
- a region that is solid, has no `RoomSize`, or whose `RoomMaterials` is not six known names;
- a portal that is solid, or that links a region to itself, or an entity that is both region and portal;
- out-of-range numbers generally (transmission/absorption/scattering outside 0–1, negative mass, a face
  mask outside 0–63, `FaceMask` and `MissingFaces` both set…).

**Maps are not rejected.** An unknown field in a map file, or on one of its entities, is logged as an error
naming the file, the entity and the field — but the map still loads, because dropping the entity would
delete a wall. Unknown material names, `RoomMaterials` that is not six long, and room materials on an entity
that is not a region are reported the same way. A portal linking a region to itself is reported and ignored.

Everything above is covered by `OpenFPS.Tests/PrefabSpecTests.cs`, including a test that every shipped
prefab satisfies the spec and one that the shipped map uses only fields the loader reads.

## 5. Importing meshes (glTF and OBJ)

A shape the library has no numbers for (a fountain, a statue, a church a sighted friend modelled in
Blender) comes in as a mesh asset (docs/GEOMETRY.md 4.2 to 4.5). The importer is an offline tool, never the
server at run time; it reads glTF 2.0 (`.gltf` with its buffers, or `.glb`) and Wavefront OBJ with small
readers of our own (`OpenFPS.Geometry/Triangles/MeshImport.cs`: no third-party parser).

Build it once and run the built program from the repository's root:

```
flock /tmp/openfps-build.lock tools/build-local.sh /tmp/ofps-import tools/import_mesh/ImportMesh.csproj
~/.dotnet/dotnet /tmp/ofps-import/bin/ImportMesh/debug/ImportMesh.dll fountain.glb \
    --material Basin=Concrete --material Water=Water --source "Fountain by A. Friend, CC BY 4.0"
```

It prints what it found and writes `OpenFPS.Server/meshes/<id>.mesh`, the id being sixteen hex digits, the
hash of the triangles. Options:

| Option | What it does |
|---|---|
| `--material SURFACE=MATERIAL` | the acoustic material of a surface (a glTF material's name, an OBJ `usemtl`, or an OBJ group when the file names no materials); repeat for each |
| `--materials MAP.json` | the same from a file: `{ "Basin": "Concrete", "Water": "Water" }` |
| `--sheet` | take an open mesh (a canopy, a sail, a mesh fence) as a sheet; without it an open mesh is refused |
| `--scale S` | the file's units to metres (0.01 for centimetres; glTF is metres already) |
| `--budget N` | the most triangles (50,000 by default) |
| `--source TEXT` | where it came from and its licence, kept in the file |
| `--out DIR` | where to write (`OpenFPS.Server/meshes` by default) |
| `--check` | read and check, write nothing |

What it does to the triangles, and what it refuses:

- corners within 10 µm are welded into one; triangles with no area and repeated triangles are left out;
- glTF nodes' transforms (matrix or translation, rotation, scale) are applied, a mirroring one turning its
  triangles back round; only triangle primitives are read (strips, points and lines are said and left out);
  Draco, quantised positions and sparse accessors are refused by name;
- a solid must be closed: every edge shared by two triangles going opposite ways. One that is not is
  refused, with how many edges are open; import it with `--sheet` if it is meant to be a sheet;
- a solid whose triangles face inward is turned right way out, and the note says so;
- more triangles than the budget is refused; slivers (a triangle under a millimetre high) and features
  under a centimetre are noted;
- glTF and OBJ are y up, as the game is; the mesh is moved so its bounds' middle is its origin.

Use it on a map entity or a prefab with a box collider of the size you want it, and `Form`:

```json
{ "PrefabId": "concrete_wall", "Position": { "X": 4, "Y": 1.2, "Z": 9 }, "Scale": { "X": 6, "Y": 0.8, "Z": 6 },
  "Form": { "Kind": "Mesh", "Mesh": "3f9a0c2b7d1e4a65" } }
```

The mesh is stretched to the box. Its surfaces have the materials the import gave them; `Materials` in the
form overrides them in the order the importer printed the surfaces. A body meets a solid mesh as boxes filling
it (up to 32 across its longest side, at most 512), a sheet as each triangle 4 cm thick (a sheet of more than
512 triangles is not in a body's way at all). Rays, sound, the ground and bullets meet its triangles.

The server reads `meshes/` at start and logs any file it cannot read or whose triangles do not hash to its
name. A client asks the server for the meshes its map names (`MeshAssetRequest`, `MeshAssetBatch`), keeps them
in `LocalApplicationData/OpenFPS/meshes` by id (`OPENFPS_MESH_CACHE` moves it, `off` turns it off), and makes
the thing as its box until its mesh has come. Tests: `GeometryImportTests`.
