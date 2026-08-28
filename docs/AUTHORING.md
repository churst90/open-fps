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
  materials.json             optional per-material acoustic overrides
```

Both loaders accept `//` comments and trailing commas, so a map can be annotated in place — `maps/default.json`
is, heavily, and is the worked example for everything below.

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
| Identity | `Id`, `Name`, `Description`, `Type`, `Material` | `NameComponent`, `IdentityComponent`, `EntityType`, `MaterialComponent` |
| Collider | `ColliderSize`, `Shape`, `IsSolid` | `ColliderComponent` |
| Health | `MaxHealth` | `HealthComponent` |
| Acoustics | `Transmission{Low,Mid,High}`, `Absorption`, `Scattering`, `ShellThickness`, `FaceMask` / `MissingFaces` | `AcousticComponent` |
| Physics | `Mass`, `Friction`, `Restitution`, `Drag` | `PhysicsPropertyComponent` |
| Emitter | `HasEmitter` + `SoundId`, `StartSoundId`, `StopSoundId`, `Volume`, `Range`, `MinDistance`, `Mode`, `EmitterDirection`, `Cone*` | `SoundEmitterComponent` |
| Granular | `IsGranular`, `Granular*` | (same component) |
| Synth | `IsSynth`, `Synth*` | (same component) |
| Region | `IsIndoor`, `EnvType`, `RoomSize`, `AmbienceId`, `ReverbScale`, `RoomMaterials` | `RegionComponent` |
| Portal | `RegionAId`, `RegionBId`, `ApertureSize` | `PortalComponent` |

`ColliderSize` is **full extents in metres**, multiplied by the map instance's `Scale`. So a
`concrete_wall` sized `2 × 3 × 0.5` placed with `"Scale": { "X": 5, "Y": 1.333, "Z": 1 }` is a 10 m wall,
4 m high, half a metre thick.

### Materials

`Material` and `RoomMaterials` take material **names** known to `AcousticRegistry`:

`Generic`, `Wood`, `Metal`, `Concrete`, `Marble`, `Carpet`, `Glass`, `None`, `Plastic`, `Grass`, `Dirt`

A name outside that list is rejected at load. (At runtime an unknown material silently becomes `Generic`,
which is right for robustness and wrong for authoring — hence the check.)

### Sound emitters

`HasEmitter` is the gate for the whole block. Setting `SoundId` (or any other emitter field) *without* it is
rejected, because the loader would otherwise attach no emitter and the object would be permanently silent.

- `Range` is where the voice stops being submitted; `MinDistance` is where it stops getting louder as you
  approach. `MinDistance` must be less than `Range`.
- **Directivity:** `ConeInsideAngle` / `ConeOutsideAngle` (degrees, inner ≤ outer) and `ConeOutsideVolume`.
  `360 / 360` — the default — is omnidirectional. `space_megaphone` uses `40 / 110 / 0.05`: full volume in
  the beam, almost nothing behind it, and an off-axis timbre change in between.
- **Aim:** `EmitterDirection` is a **local** direction, rotated into the world by the map instance's
  `Rotation`. Omit it for the default forward `(0, 0, 1)` and aim the entity with `Rotation`, which is what
  `maps/default.json` does. It has no audible effect on an omnidirectional emitter.
- **Spin-up / spin-down:** `StartSoundId` plays once before the loop starts, `StopSoundId` plays once
  instead of cutting the loop off.
- `Mode` is `Single`, `LoopOne`, `LoopFolder`, `Sequential` or `StateMachine`.
- `IsSynth` generates the signal (no `SoundId` needed); `IsGranular` cuts grains from `SoundId`. They are
  mutually exclusive, and synth/granular parameters without their flag are rejected.

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

**Map fields:** `Id`, `Size`, `MinBound`, `MaxBound`, `SpawnPoint`, `MinimumY`, `Description`,
`VoxelResolution`, `OcclusionFloor`, `Gravity`, `Temperature`, `Humidity`, `AirPressure`,
`AirAbsorptionMultiplier`, `Entities`.

- `MinBound` / `MaxBound` are hard walls: `SharedMovementEngine` clamps position *and* velocity against
  them, so the edge of the map is a surface, not a cliff.
- `MinimumY` is computed at load (20 m below the lowest floor surface) — anything below it has fallen out
  of the world.
- `VoxelResolution` is the acoustic grid's cell size in metres; `OcclusionFloor` is the floor on occlusion
  (sounds never drop below it through a wall). Both are streamed to the client in the map manifest.

**Entity fields:** `EntityId`, `PrefabId`, `Position`, `Rotation`, `Scale`, and the per-instance overrides
`IsIndoor`, `RoomMaterials` (or `Materials`), `RegionAId`, `RegionBId`, `ApertureSize`.

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

If no entity sits at the origin with the `concrete_floor` prefab, the loader injects an auto-scaled
foundation so the map has a floor; it logs when it does. The spawn point is then dropped onto whatever floor
is under it, and a spawn point over nothing is reported and parked at Y = 2.

### A worked room

From `maps/default.json` — a concrete room with a doorway and a siren inside it:

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
