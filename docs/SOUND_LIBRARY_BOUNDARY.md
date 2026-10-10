# The sound library boundary

A read-only survey of what it takes to turn open-fps's sound engine, acoustics and geometry into a
library that open-fps and Resonance both reference. Step 1 of
`inbox/resonance-sharing-2026-10-06/README.txt`. No engine code was changed.

- Surveyed at main `26d531a2` (the geometry stage 1 merge), 2026-10-06.
- Every number below comes from `tools/sound_boundary/run.sh`. Run it again after any move; it
  rewrites the tables into `/tmp/openfps-wt-survey/out/tables.md` and every crossing line into
  `crossings.tsv`.

## Contents

1. Summary
2. Method
3. The boundary
4. Global state
5. Files, paths and environment
6. The projects
7. The host interface
8. The migration, stage by stage
9. Enforcement
10. Risks
11. Decisions for Cody
12. Appendices: every type by file, every crossing line, every static, every file read
13. Stage 0 as built: the guards, and how to regenerate them
14. Stage 1 as built: OpenFPS.Native
15. Stage 2 as built: OpenFPS.Geometry
16. Stage 3 as built: OpenFPS.Sound and the first half of OpenFPS.Acoustics

---

## 1. Summary

- 733 types are in scope: every type declared in a sound, acoustics or geometry file, or referenced
  from one. 672 go to the library (a), 27 are values both sides need (c), 34 are host-side (b).
- The library would be about 90,000 lines: Sound 48k, Audio 24k, Native 7.8k, Acoustics 6.1k,
  Geometry 3.0k. Resonance's copy is 49k lines of it.
- 600 references cross from would-be library code into host types, from 26 files. They fall into
  four kinds:
  - 394 read the world through `WorldSnapshot`, `EntitySnapshot`, `EntityDefinition` and the entity
    components (10 files). Fix: a world input the host fills.
  - 123 use `RegionComponent` and `PortalComponent` as the library's rooms and openings (8 files).
    Fix: library `Room` and `Opening` values.
  - 27 call back into open-fps classes (`WorldAudioPlayer`, `SpatialService`, `NamedPlaces`).
  - 56 are small: a speech level, gravity and walking speed, the default road surface, a tyre
    encoding, the voice codec (11 files).
- The note counted four engine files that reach into the network or session. Counting the snapshot
  types too (they live in `OpenFPS.Common`, not in `Networking`), it is ten: `AsyncAcousticWorker`,
  `SpatialAcoustics`, `OpeningGraph`, `CabinWalls`, `VehicleShadow`, `EngineReflections`,
  `SteamAudioScene`, `EarlyCopies`, `FmodAudioProvider`, `TalkerVoice`.
- Most of the library can move with small edits. After ten small fixes and three type moves inside
  the library, 72,000 of the 90,000 lines have no path to a host type: all of Geometry, Sound and
  Native, half of Acoustics and 9,400 lines of Audio. What is left is the rooms and openings (3,000
  lines) and the acoustic worker, the FMOD provider and the Steam Audio scene chain (15,000 lines).
- `ClientAudioSystem` is not a thin adapter. 67 of its 295 members read snapshots, and those 67
  span 2,480 of its 3,085 lines of members: the big methods (`Update`, `ProcessAudioEmitter`,
  `ChooseLiveEngines`, `ChooseLiveMachines`, `UpdateTalkers`) mix the world reads with the audio
  decisions. Splitting it is a refactor, and the most expensive step.
- 311 statics in the library can change at run time. 48 are written from outside their own type
  (settings, levers, world state) and 66 are reassigned by their own type (registries, the traced
  reverb set, counters). The rest are caches, tables filled once and per-thread scratch. The test
  assembly already runs serially because of them (`OpenFPS.Tests/AssemblyInfo.cs`).
- `AudioClock.Now` is read 47 times in 13 files, 15 of them in the library.
- The library reads `ASSETS/SOUNDS` from the base directory in three places, model and machine JSON
  from directories relative to the working directory, a door render cache from the user's
  application data, and 37 environment variables in 16 files.
- Proposed: five projects (Geometry, Acoustics, Sound, Native, Audio), a host interface built from
  what `IAudioProvider` and `SpatialEmitter` already are, and nine stages. Estimate: 17 to 24
  sessions, every stage guarded by the tests, a render fingerprint and a recorded emitter stream.

---

## 2. Method

`tools/sound_boundary/Program.cs` is a Roslyn reader. It parses every `.cs` file of the seven
projects, binds them against the .NET runtime and the cached NuGet packages, and writes:

- `types.tsv`: every top-level type, its file, line span and namespace;
- `edges.tsv`: every reference from one repository type to another, with file, line and the member
  it sits in (a type name, a member access, a constructor call, an extension call, a target-typed
  `new()`);
- `members.tsv`: every method, property and field with its line span;
- `statics.tsv` and `static_refs.tsv`: every static field or property whose value or contents can
  change, and every read, write or mutating call on it;
- `assets.tsv`: every use of `File`, `Directory`, `FileStream`, the base directory, the folder
  paths, the environment, manifest resources, FMOD's sound loading and Steam Audio's HRTF.

`tools/sound_boundary/classify.py` holds the sorting rules in one place (a file rule, then type
exceptions) and writes the tables. A type no rule matches is reported as UNSORTED; there are none.

Binding: `OpenFPS.Common` and `OpenFPS.Client.Core` bind with no errors. The Server, Client,
AudioLab and Tests projects have errors only where a package or WinForms is missing (xunit's
`Assert`, `System.Windows.Forms`), which does not affect references to repository types. The
MemoryPack source generator is not run; it adds no references between repository types.

What the reader cannot see: references by string (sound ids, reflection, JSON type names), and
behaviour reached through delegates handed in from elsewhere. "Lines" in the tables are the spans
of type declarations, comments included; a partial type counts all its parts.

---

## 3. The boundary

### 3.1 Groups and how they were decided

- **a, library**: everything under `OpenFPS.Client.Core/AudioEngine`, `FmodNative`, the native
  library finder and thread priority helper, `NearDrops`, `RainField` (with `RainSurvey`), and the
  Common files that are sound models, presets, the ear, weather sound, nature, vehicles as sound
  sources, materials, rooms, openings, reflections and geometry. Each goes to one of five projects
  (section 6).
- **b, host**: the network (`Messages`, `StatePacking`, `JsonConverters`, `WorldAudioEvent`), the
  entity model (`Components`), the snapshots, the session, input, player movement, traffic driving
  (`LineFollower`, `RaceLine`, `LaneRoutes`, `LaneGuide`), roads as map data, the scope, beacons,
  open-fps's recorded speech lines, and open-fps's adapters. Four integration types are host-side
  but split: `ClientAudioSystem`, `WorldAudioPlayer`, `SpatialService` and `BirdLife` each hold
  library logic that moves out (section 7.6). `EntityGeometry` (moved by geometry stage 2 to its own file, `OpenFPS.Common/EntityGeometry.cs`) is the
  adapter half of the new geometry.
- **c, a value both need**: it lives in the library and the host builds it, stores it or sends it:
  `WheelState`, `TransientSound`, `SoundCharacter`, `PlaybackMode`, `WeatherType`, `ColliderShape`,
  `TileKey`, `Precipitation`, `WindAir`, `LightningStrike`, `DoorKind`, the weapon numbers,
  `RoadWater`, `SpatialEmitter` and `AcousticPathData`.

Choices that could go either way, and why they went this way:

- Vehicle physics (`WheelDynamics`, `TyreFriction`, `Chassis`, `RunningGear`, `VehicleBody`) is
  library. The server simulates with it and the sound is made from it; Resonance put the same files
  in `Resonance.Audio/Vehicles`.
- Traffic driving is host. Resonance keeps its `LineFollower` in its game too.
- Door, glass, bullet, ricochet, weapon handling and footstep models are library: they are physical
  models with no host reads. `AdminGun`, `TeleporterSounds` and `HandOverSounds` are open-fps's own
  designed sounds; they have no host reads either, so they sit in the library for now (decision 3).
- `Speech` is host: it is open-fps's recorded lines. `Loudness` reads two of its constants (a
  crossing to fix, not a reason to move speech).
- `RainSurvey` is library: it reads entities today, but what it does (find roofs, gutters and
  ledges near the listener) is acoustics over solids; Resonance has it in `Resonance.Audio/Weather`.
  `BirdLife` is host and splits; Resonance keeps its birds in its game.

### 3.2 Counts

Types in the universe (declared in a sound, acoustics or geometry file, or referenced from one), by group and destination.

| Group | Project | Types | Lines | Files |
|---|---|---:|---:|---:|
| a | Acoustics | 23 | 6147 | 16 |
| a | Audio | 119 | 24026 | 56 |
| a | Geometry | 33 | 3035 | 12 |
| a | Native | 246 | 7757 | 9 |
| a | Sound | 251 | 47892 | 117 |
| b | - | 34 | 11313 | 23 |
| c | Audio | 4 | 351 | 2 |
| c | Geometry | 2 | 17 | 2 |
| c | Sound | 21 | 1027 | 12 |
| all | | 733 | 101565 | |

### 3.3 References that cross the boundary

600 references from library (a or c) types to host (b) types, in 26 files. Full list with every line: crossings.tsv.

#### By host type

| Host type | References | From library files |
|---|---:|---|
| Networking.EntityDefinition | 105 | Common/Systems/AcousticVolumeGenerator.cs (50), CC/RainField.cs (19), CC/AudioEngine/Acoustics/OpeningGraph.cs (15), CC/AudioEngine/SteamAudio/SteamAudioScene.cs (9), Common/AudioEmission.cs (4), CC/AudioEngine/Acoustics/VehicleShadow.cs (3) ... |
| WorldSnapshot | 105 | CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs (36), CC/AudioEngine/Acoustics/SpatialAcoustics.cs (30), CC/RainField.cs (21), CC/AudioEngine/Acoustics/OpeningGraph.cs (9), CC/AudioEngine/Acoustics/EngineReflections.cs (3), CC/AudioEngine/Acoustics/CabinWalls.cs (2) ... |
| EntitySnapshot | 97 | CC/RainField.cs (35), CC/AudioEngine/Acoustics/SpatialAcoustics.cs (15), CC/AudioEngine/Acoustics/CabinWalls.cs (12), CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs (10), Common/AudioEmission.cs (7), CC/AudioEngine/Acoustics/OpeningGraph.cs (7) ... |
| Components.RegionComponent | 72 | Common/Systems/AcousticVolumeGenerator.cs (28), Common/OpeningRoutes.cs (15), Common/RoomAcoustics.cs (15), CC/AudioEngine/Fmod/FmodAudioProvider.cs (7), CC/AudioEngine/Acoustics/SpatialAcoustics.cs (5), Common/AcousticMap.cs (1) ... |
| Components.PortalComponent | 51 | Common/Systems/AcousticVolumeGenerator.cs (26), CC/AudioEngine/Acoustics/OpeningGraph.cs (12), CC/AudioEngine/Fmod/FmodAudioProvider.cs (12), Common/AcousticMap.cs (1) |
| Components.Transform | 44 | Common/Systems/AcousticVolumeGenerator.cs (12), CC/RainField.cs (10), CC/AudioEngine/Acoustics/OpeningGraph.cs (5), Common/AudioEmission.cs (4), CC/AudioEngine/Acoustics/SpatialAcoustics.cs (4), CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs (3) ... |
| Components.ColliderComponent | 17 | CC/RainField.cs (7), Common/Systems/AcousticVolumeGenerator.cs (3), CC/AudioEngine/SteamAudio/SteamAudioScene.cs (3), CC/AudioEngine/Acoustics/OpeningGraph.cs (2), CC/AudioEngine/Acoustics/VehicleShadow.cs (2) |
| WorldAudioPlayer | 17 | CC/AudioEngine/SteamAudio/EarlyCopies.cs (17) |
| RoadData | 16 | Common/RoadWater.cs (12), Common/WheelDynamics.cs (2), CC/AudioEngine/Fmod/EngineProcessor.cs (2) |
| Platform.VoiceCodec | 16 | CC/AudioEngine/Fmod/TalkerVoice.cs (16) |
| PhysicsConstants | 12 | Common/Glass.cs (6), Common/Breathing.cs (2), Common/EarWind.cs (2), Common/ExternalBallistics.cs (2) |
| Speech | 10 | Common/Loudness.cs (8), Common/Hearing/EarModel.cs (2) |
| Components.SoundEmitterComponent | 9 | Common/AudioEmission.cs (3), CC/AudioEngine/Acoustics/CabinWalls.cs (2), CC/RainField.cs (2), Common/Systems/AcousticVolumeGenerator.cs (1), CC/AudioEngine/SteamAudio/SteamAudioScene.cs (1) |
| SpatialService | 8 | CC/AudioEngine/Acoustics/SpatialAcoustics.cs (8) |
| Components.EntityType | 8 | CC/AudioEngine/Acoustics/SpatialAcoustics.cs (4), CC/RainField.cs (4) |
| Components.AcousticComponent | 6 | CC/RainField.cs (4), CC/AudioEngine/SteamAudio/SteamAudioScene.cs (2) |
| Components.MaterialComponent | 3 | CC/RainField.cs (2), CC/AudioEngine/SteamAudio/SteamAudioScene.cs (1) |
| Networking.EntityState | 2 | Common/Messages.cs (2) |
| NamedPlaces | 2 | CC/AudioEngine/Acoustics/SpatialAcoustics.cs (2) |

#### By library file (worst first), with the lines

| Library file | Refs | To | Lines |
|---|---:|---|---|
| Common/Systems/AcousticVolumeGenerator.cs | 120 | Networking.EntityDefinition 50, Components.RegionComponent 28, Components.PortalComponent 26, Components.Transform 12, Components.ColliderComponent 3 | 27, 40, 44, 45, 49, 50, 51, 52, 53, 59, 70, 71, 73, 75 ... (58 lines) |
| CC/RainField.cs | 104 | EntitySnapshot 35, WorldSnapshot 21, Networking.EntityDefinition 19, Components.Transform 10, Components.ColliderComponent 7 | 146, 171, 628, 631, 634, 636, 640, 641, 642, 646, 648, 649, 651, 652 ... (48 lines) |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | 70 | WorldSnapshot 30, EntitySnapshot 15, SpatialService 8, Components.RegionComponent 5, Components.EntityType 4 | 19, 21, 23, 25, 33, 36, 37, 92, 95, 102, 103, 133, 137, 142 ... (40 lines) |
| CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs | 51 | WorldSnapshot 36, EntitySnapshot 10, Components.Transform 3, Components.RegionComponent 1, Networking.EntityDefinition 1 | 35, 148, 254, 280, 376, 520, 696, 785, 795, 796, 824, 847, 908, 972 ... (38 lines) |
| CC/AudioEngine/Acoustics/OpeningGraph.cs | 50 | Networking.EntityDefinition 15, Components.PortalComponent 12, WorldSnapshot 9, EntitySnapshot 7, Components.Transform 5 | 21, 22, 23, 26, 29, 30, 40, 42, 46, 55, 58, 62, 63, 67 ... (22 lines) |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | 25 | Networking.EntityDefinition 9, EntitySnapshot 5, Components.ColliderComponent 3, WorldSnapshot 2, Components.Transform 2 | 112, 116, 118, 119, 124, 127, 128, 130, 131 |
| CC/AudioEngine/Acoustics/CabinWalls.cs | 20 | EntitySnapshot 12, Networking.EntityDefinition 2, Components.SoundEmitterComponent 2, Components.Transform 2, WorldSnapshot 2 | 31, 33, 34, 36, 41, 54, 56, 70, 73, 81, 83 |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs | 19 | Components.PortalComponent 12, Components.RegionComponent 7 | 1559, 1561, 1562, 2287, 2293, 2302, 2306, 2320, 4562, 4563, 4564, 4568, 4571, 4595 |
| Common/AudioEmission.cs | 18 | EntitySnapshot 7, Networking.EntityDefinition 4, Components.Transform 4, Components.SoundEmitterComponent 3 | 40, 43, 46, 47, 72, 74, 75, 76, 91, 93 |
| CC/AudioEngine/SteamAudio/EarlyCopies.cs | 17 | WorldAudioPlayer 17 | 75, 76, 80, 81, 83, 91, 94, 98, 107, 110 |
| CC/AudioEngine/Fmod/TalkerVoice.cs | 16 | Platform.VoiceCodec 16 | 50, 55, 56, 101, 169, 170, 190 |
| Common/OpeningRoutes.cs | 15 | Components.RegionComponent 15 | 325, 326, 339, 450, 451, 456, 457, 458, 686, 691 |
| Common/RoomAcoustics.cs | 15 | Components.RegionComponent 15 | 73, 76, 87, 90, 95, 108, 109, 121, 124, 127, 129, 131, 138 |
| CC/AudioEngine/Acoustics/VehicleShadow.cs | 15 | EntitySnapshot 6, Networking.EntityDefinition 3, WorldSnapshot 2, Components.ColliderComponent 2, Components.Transform 2 | 46, 50, 52, 53, 54, 55, 57 |
| Common/RoadWater.cs | 12 | RoadData 12 | 437, 444, 446, 451, 455, 467, 468, 497, 504, 516, 525, 550 |
| Common/Loudness.cs | 8 | Speech 8 | 234, 246, 249 |
| Common/Glass.cs | 6 | PhysicsConstants 6 | 145, 154, 187 |
| CC/AudioEngine/Acoustics/EngineReflections.cs | 3 | WorldSnapshot 3 | 153, 157, 158 |
| Common/AcousticMap.cs | 2 | Components.RegionComponent 1, Components.PortalComponent 1 | 19, 22 |
| Common/Breathing.cs | 2 | PhysicsConstants 2 | 72 |
| Common/EarWind.cs | 2 | PhysicsConstants 2 | 136 |
| Common/ExternalBallistics.cs | 2 | PhysicsConstants 2 | 303 |
| Common/Hearing/EarModel.cs | 2 | Speech 2 | 42 |
| Common/Messages.cs | 2 | Networking.EntityState 2 | 480 |
| Common/WheelDynamics.cs | 2 | RoadData 2 | 290 |
| CC/AudioEngine/Fmod/EngineProcessor.cs | 2 | RoadData 2 | 1388 |

#### By the fix each needs

| Fix | References | Files |
|---|---:|---:|
| the world input (solids, sources, listener) instead of snapshots and entities | 394 | 10 |
| rooms and openings as library values instead of RegionComponent and PortalComponent | 123 | 8 |
| library code calling back into open-fps integration classes | 27 | 2 |
| small: a constant, a level or a codec passed in instead of read | 56 | 11 |

The worst edges, read in place:

- `Common/Systems/AcousticVolumeGenerator.cs` (120 references): builds the `AcousticMap` straight
  from `EntityDefinition` lists, reading regions, portals, transforms and colliders. It needs rooms,
  openings and solids as inputs.
- `CC/RainField.cs` (104): 93 in `RainSurvey`, which walks `world.Entities` for roofs, gutters and
  ledges; 11 in `RainField`, which reads the weather and the air from the snapshot.
- `CC/AudioEngine/Acoustics/SpatialAcoustics.cs` (70): the per-voice acoustic request reads
  positions out of the snapshot and calls `SpatialService` for rays.
- `CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs` (51): takes a `WorldSnapshot` per request. It
  reads only a narrow slice: the geometry and its version, the tile size, the acoustic map, the air
  and the transform of a few entities by id.
- `CC/AudioEngine/Acoustics/OpeningGraph.cs` (50): builds the graph of openings from portal
  entities.
- `CC/AudioEngine/SteamAudio/EarlyCopies.cs` (17): calls `WorldAudioPlayer.PlanRoomEchoes` and its
  constants. The echo planner belongs in the library; it is a move, not an interface.
- `CC/AudioEngine/Fmod/TalkerVoice.cs` (16): decodes Opus through `Platform.VoiceCodec`. The talker
  should take decoded PCM, or a decoder the host passes.
- Small ones: `Loudness.cs:234-249` and `EarModel.cs:42` read `Speech.NormalDb` and
  `Speech.BufferRmsDbfs`; `Glass.cs`, `Breathing.cs`, `EarWind.cs` and `ExternalBallistics.cs` read
  `PhysicsConstants` (gravity, walk and sprint speed, player height); `WheelDynamics.cs:290` and
  `EngineProcessor.cs:1388` read `RoadData.DefaultSurface`; `Messages.cs:480` (`WheelState.Encode`)
  calls `EntityState.EncodeTyreDemand`.

Every line is in appendix B.

### 3.4 Values both need

| Type | Now in | Goes to | Library refs | Host refs | Note |
|---|---|---|---:|---:|---|
| Data.AcousticPathData | CC/AudioEngine/Data/AcousticPathData.cs | Audio | 154 | 453 | the source description the host fills |
| Data.EmitterType | CC/AudioEngine/Data/SpatialEmitter.cs | Audio | 26 | 118 | the source description the host fills |
| Data.SpatialEmitter | CC/AudioEngine/Data/SpatialEmitter.cs | Audio | 554 | 1660 | the source description the host fills |
| Data.SynthWaveType | CC/AudioEngine/Data/SpatialEmitter.cs | Audio | 12 | 8 | the source description the host fills |
| AdminGunMode | Common/AdminGun.cs | Sound | 71 | 68 |  |
| Breath | Common/Breathing.cs | Sound | 2 | 25 |  |
| Components.ColliderShape | Common/Components.cs | Geometry | 6 | 344 |  |
| Components.PlaybackMode | Common/Components.cs | Sound | 25 | 187 | an enum the emitter component and the voices share |
| Components.WeatherType | Common/Components.cs | Sound | 6 | 164 |  |
| CrowdApplause | Common/Applause.cs | Sound | 16 | 51 |  |
| DoorKind | Common/Doors.cs | Sound | 29 | 114 | stored in DoorComponent |
| FireMode | Common/Weapons.cs | Sound | 26 | 52 | weapon numbers the sound reads |
| FlashKind | Common/Lightning.cs | Sound | 16 | 30 |  |
| LightningStrike | Common/Lightning.cs | Sound | 21 | 102 | the flash the server sends |
| Networking.WheelState | Common/Messages.cs | Sound | 15 | 90 | a sound value in the network namespace |
| Precipitation | Common/Precipitation.cs | Sound | 48 | 83 | weather value in the snapshot |
| PrecipitationKind | Common/Precipitation.cs | Sound | 125 | 146 |  |
| PuddleField | Common/RoadWater.cs | Sound | 0 | 34 |  |
| RoadWater | Common/RoadWater.cs | Sound | 4 | 94 | server-advanced store sent whole; the sound reads depths |
| SoundCharacter | Common/AudioEvents.cs | Sound | 56 | 136 |  |
| TileKey | Common/Tiles.cs | Geometry | 68 | 140 | a tile of the world; the tile scenes key on it |
| TransientSound | Common/AudioEvents.cs | Sound | 135 | 929 |  |
| WeaponAction | Common/Weapons.cs | Sound | 53 | 0 | weapon numbers the sound reads |
| WeaponDefinition | Common/Weapons.cs | Sound | 281 | 270 | weapon numbers the sound reads |
| WeaponFeed | Common/Weapons.cs | Sound | 15 | 4 | weapon numbers the sound reads |
| WeaponRegistry | Common/Weapons.cs | Sound | 44 | 322 | content registry both read |
| WindAir | Common/Wind.cs | Sound | 51 | 56 | the wind the server sends |

Notes:

- `TransientSound` is `[MemoryPackable]`: if it moves, the library needs the MemoryPack attribute
  package (decision 4). `WheelState`, `Precipitation`, `WindAir` and `LightningStrike` are plain
  structs and move without it.
- `PlaybackMode`, `WeatherType` and `ColliderShape` are enums inside `Components.cs`. Moving an enum
  to another assembly does not change the bytes MemoryPack writes.
- `SpatialEmitter` and `AcousticPathData` are already the host-neutral description of a source and
  of its path. They are the base of the host interface (section 7).

### 3.5 Inside the library: the layers

Layers: Geometry < Acoustics < Sound < Audio, and Native used only by Audio. Each row is a lower project using a higher one; each must be inverted or the type moved down before the projects can be split.

| From (project) | To (project) | Refs | Example lines |
|---|---|---:|---|
| GeometryUtils (Geometry) | BoxContainment (Acoustics) | 9 | Common/GeometryUtils.cs:33, Common/GeometryUtils.cs:54, Common/GeometryUtils.cs:55 |
| AudioEmission (Acoustics) | MachineRegistry (Sound) | 4 | Common/AudioEmission.cs:64 |
| Geometry.TriangleWorldBuilder (Geometry) | WallBuild (Acoustics) | 2 | Common/Geometry/TriangleWorldBuilder.cs:236 |
| Core.Aircraft.AircraftSynth (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Aircraft/AircraftSynth.cs:76 |
| Core.Engine.EngineSynth (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Engine/EngineSynth.cs:284 |
| Core.Pneumatics.AirSystem (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Pneumatics/AirSystem.cs:204 |
| Core.Rail.TrainSynth (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Rail/TrainSynth.cs:81 |
| Core.Signals.ChimeHorn (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Signals/ChimeHorn.cs:60 |
| Core.Signals.ElectricHorn (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Signals/ElectricHorn.cs:68 |
| Core.Signals.ElectronicSiren (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Signals/ElectronicSiren.cs:46 |
| Core.Signals.SteamWhistle (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Signals/SteamWhistle.cs:61 |
| Core.Signals.StruckBell (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Signals/StruckBell.cs:68 |
| Core.VehicleSynth (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/VehicleSynth.cs:51 |
| Core.Yard.SmallMachineSynth (Sound) | Fmod.MixerQuality (Audio) | 2 | CC/AudioEngine/Core/Yard/SmallMachineSynth.cs:86 |
| AudioEmission (Acoustics) | VehicleProfile (Sound) | 1 | Common/AudioEmission.cs:64 |
| Geometry.Construction (Geometry) | WallBuild (Acoustics) | 1 | Common/Geometry/Surfaces.cs:56 |

All of these are cheap: `BoxContainment` is an enum to move down, `WallBuild` is how a wall is built
and belongs with the geometry's `Construction`, `AudioEmission` uses vehicle presets and belongs in
Sound, and every synth reads `MixerQuality.DefaultRate` as a default parameter (a constant to move
down to Sound).

### 3.6 Who will use the library

References from each host project into library projects (allowed direction; this is what each host will reference).

| Host project | Geometry | Acoustics | Sound | Audio | Native |
|---|---:|---:|---:|---:|---:|
| OpenFPS.AudioLab | 385 | 947 | 6107 | 3151 | 2214 |
| OpenFPS.Client | 2 | 0 | 4 | 9 | 4 |
| OpenFPS.Client.Core (adapters/integration) | 279 | 386 | 1235 | 1361 | 10 |
| OpenFPS.Client.Gtk | 2 | 0 | 4 | 8 | 8 |
| OpenFPS.Common | 215 | 2 | 84 | 0 | 0 |
| OpenFPS.Server | 407 | 70 | 1812 | 0 | 0 |
| OpenFPS.Tests | 966 | 2460 | 10048 | 2845 | 94 |

The server uses Sound heavily (1,812 references: vehicle presets, wheel dynamics, door and glass
models, lightning, machines) and never Audio or Native. That is why Sound and Audio are separate
projects (section 6).

---

## 4. Global state

Every static field or property of a library (a or c) type whose value can be reassigned, or whose contents can change (a static readonly collection, array or object). Categories: external = written from outside its own type (settings, levers, world state); internal = reassigned only by its own type (registries, lazy state, counters); cache = a readonly collection its own type fills; never written = assignable but only initialised (often from an environment variable); table = a collection or array filled once at start; scratch = [ThreadStatic] working buffers.

| Category | Count |
|---|---:|
| external | 48 |
| internal | 66 |
| cache | 29 |
| never written | 21 |
| table | 115 |
| scratch | 32 |

### 4.1 The statics that matter, and what replaces each

The ones that make "one world, one clock, one test at a time". Counts are references (reads/writes).

| Static | Who writes it | Who reads it | Replacement |
|---|---|---|---|
| `AudioClock.Now` (a `Stopwatch` started at type load) | nobody | 47 reads in 13 files: `FmodAudioProvider` 15, `ClientAudioSystem` 12, the rest of the client 8, the server 4, the lab 7, tests 1 | `IAudioClock` passed to the audio world and the provider. open-fps passes one stopwatch clock shared with its game loop (same values as today); offline renders and tests pass a stepped clock. `ClientAudioSystem` already takes a `Func<double>` clock for tests (`ClientAudioSystem.cs:362`); in the game it starts its own `Stopwatch` (`:388`), a second monotonic clock with a different zero from `AudioClock`. The server keeps its own. |
| `EarModel.Enabled`, `EarModel.ListeningLevelDb` (environment at start) | `ClientSettings`, `ListeningCalibration`, 14 test writes | 10 in the library (engine processor, ear stage), 10 in the client | `HearingSettings` on the audio world. The client's calibration sets it on the world it owns. |
| `Loudness.DynamicRangeCompression` (environment at start) | `ClientRunner`, `GtkClientProgram`, `ClientGameSession`, 22 lab and test writes | 6 in the library, 27 in tests | Same `HearingSettings` object (the law's compression). |
| `WindField.Weather`, `MeanSpeed`, `Turbulence`, `_held` | `ClientGameSession.cs:1099` on every `WorldStateUpdate`, 20 lab writes | 12 in the library (foliage, ear wind, nature voices, rain), `ClientAudioSystem.cs:544` | A `WindField` instance per audio world; the host feeds it the `WindAir` the server sends. |
| `WindField.Now()` (`Wind.cs:208`: UTC seconds since 2026-01-01, so the server and every client agree on the gusts) | nobody | 8: the client 7, the server 1 | A second clock on `IAudioClock` (`WorldTime`), which open-fps fills with the same UTC count. |
| `Runoff` (`_rain`, `_last`, `_held`) | `ClientAudioSystem.cs:846` (`Update`), lab resets | `RunningWater`, `RoadWater`, `NatureVoices` | An instance per world. Resonance has already done this. |
| `AudioPhysics.CurrentSpeedOfSound`, `CurrentAirCelsius` | `FmodAudioProvider` when the air temperature is set | 4 library, 14 in `ClientAudioSystem` | `Atmosphere` on the audio world (temperature, humidity, pressure, speed of sound), as Resonance's `SpatialVoices.Atmosphere`. |
| `MixerQuality.MixerRate` | `FmodAudioProvider` at start | 36 in the library | The provider's mixer format, passed to whatever renders; the synths already take a rate with `DefaultRate` as the default argument. |
| `TracedReverbSet` (internal static class: context, scene, listener, echoes, late, cabin, rooms), `TracedReverb.Current`, `TracedEchoes.Current` | itself | the Steam Audio chain | Fields of the provider: one per Steam Audio context. Two worlds in one process (tests, a lab comparing two settings) cannot exist while this is static. |
| `Talkers._all`, `OwnVoiceRing.Shared` | the talker and own-voice code | the provider, the client | Provider-owned. |
| `MachineRegistry` (`_authored`, `_loadedFrom`, `_assembled`), `ModelLibrary` (`_authored`, `_loadedFrom`) | `EnsureLoaded("machines")` and `EnsureLoaded("models")` from `ClientGameSession.cs:153` and `Server/Program.cs:346`, relative to the working directory | `MachineRegistry.VehicleFor` alone has 100 call sites (server 9, client 11, library, tests 46) | A `ModelCatalog` instance: the built-in presets plus what the host loads from paths it names. Keep a default catalog behind the static facade until the call sites are moved (decision 7). |
| `AcousticRegistry._registry` | `Initialize()`, which builds it from code and is idempotent | everything that reads a material | A table, not state: make it a lazily built readonly table. If Resonance needs its own materials, a `MaterialTable` the host may extend. |
| `DoorRenderCache.Folder`, `ShippedFolder` | the lab and tests | the door voices | Paths in `AudioDataPaths` (section 7.4). |
| Live mix levers written by the client console: `FmodAudioProvider.TailDb`, `CopiesDb`, `CabinDb`, `TracedEchoesOn`, `EngineSynth.ValveJetNoise` | `ClientGameSession` | the provider and engine synth | `MixSettings` on the provider. |
| Lab levers: `EngineSynth.Debug*`, `SmoothTail.FromFiftyMs`, `CabinPaths.Enabled` and `LabOnePlace`, `ExtendedSources.*`, `TracedReverb.RawTail` and others, `OpeningRoutes.ReverseGridOrderForParity`, `EarlyReflections.FlutterTrace`, the doors' `StemFolder` and `PinTrace` (about 30) | AudioLab and tests only | the code they switch | One `LabLevers` options object passed to the world. Until then, an allowed exception kept in one class. |

### 4.2 What may stay static

A rule of "no statics" would also forbid things that are harmless. Proposed exceptions, each kept
in a list the enforcement test reads:

- tables filled once at start and never written (115: band centres, ISO tables, presets);
- `[ThreadStatic]` scratch buffers (32: the reflection, enclosure and route searches);
- caches keyed by everything they depend on (29: `EarTimbres`, `TakeLevels`, `Thunder`'s line
  responses, `VehicleProfile._cache`, `MagicCurve.Cache`), as long as they are thread-safe;
- process-wide native handles (the Embree devices) and diagnostics (`PerfProbe`, `NonFinite`,
  `DspFault`, the starve counters).

### 4.3 The clock

| File | Group | Reads |
|---|---|---:|
| CC/ClientAudioSystem.cs | b | 12 |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs | a:Audio | 11 |
| OpenFPS.AudioLab/Spikes/GameLevelsSpike.cs | b | 7 |
| CC/AudioEngine/Fmod/FmodAudioProvider.Ear.cs | a:Audio | 4 |
| OpenFPS.Server/Program.cs | b | 3 |
| CC/ClientWorldState.cs | b | 2 |
| CC/Session/ClientGameSession.cs | b | 2 |
| CC/LocalPlayerController.cs | b | 1 |
| CC/OtherBodies.cs | b | 1 |
| CC/Session/ClientGameSession.Scope.cs | b | 1 |
| CC/StrideAccumulator.cs | b | 1 |
| OpenFPS.Server/Core/CombatService.cs | b | 1 |
| OpenFPS.Tests/ReflectionsFollowTests.cs | b | 1 |

47 reads in 13 files.

Other clocks the library reads directly:

- `Environment.TickCount64` and `DateTime.UtcNow`: 34 reads in 7 library files, 23 of them in
  `AsyncAcousticWorker` (how long a simulated path is held, door and tile rebuild throttles, the
  route rebuild interval), the rest in `FmodAudioProvider` (4), `OwnVoice` (2), `TracedReverb` (2),
  `LateField`, `SpatialAcoustics` and `Wind`. These decide behaviour, not just timing, so they go
  behind the same `IAudioClock` (a monotonic `Now` and the shared `WorldTime`).
- `Stopwatch` timings in the Steam Audio and tile code only measure; they can stay.
- Unseeded randomness: `GranularProcessor.Rnd` and `SynthProcessor.Rnd` (`new Random()`) and
  `AudioBank` (`Random.Shared`) pick grains and variants. They matter for the render fingerprint
  (stage 0), not for the boundary.

The full lists are in appendix C.

---

## 5. Files, paths and environment

What the would-be library reads from disk, and from where:

| What | Where it is read | How the path is found today | Replacement |
|---|---|---|---|
| Recorded sounds (`ASSETS/SOUNDS/...`) | `FmodAudioProvider.cs:2048` and `FmodResourceManager` (`:201`, `:216`), `GranularBank.cs:124`, `AudioEngineFacade.cs:78` (the bank index) | `AppDomain.CurrentDomain.BaseDirectory` + `ASSETS/SOUNDS`, extension guessed (.wav, .ogg, .mp3); a sound id containing "ASSETS" is used as a path | `AudioDataPaths.Sounds`, one resolver used by all three |
| Take levels (`levels.json` beside a bank) | `TakeLevels.cs:45-81` | the directory of the resolved sound file | follows `Sounds` |
| Door render cache | `DoorRenderCache.cs:43-142` | `OPENFPS_RENDER_CACHE`, else the user's local application data; shipped renders in `BaseDirectory/ASSETS/rendercache/<fingerprint>` | `AudioDataPaths.RenderCache` and `ShippedRenderCache` |
| Authored machines and models (`*.json`) | `Machines.cs:192-201`, `ModelLibrary.cs:154-163` (and `:291-296` writes an export) | `EnsureLoaded("machines")`, `EnsureLoaded("models")`: relative to the working directory, not the base directory | `AudioDataPaths.Machines`, `Models`, given to the `ModelCatalog` |
| HRTF | `FmodAudioProvider.cs:1214`, `:2222` | Steam Audio's built-in default HRTF (`IPL_HRTFTYPE_DEFAULT`); no file | an optional SOFA path in `AudioDataPaths` for later |
| Native libraries | `NativeAudioLibraries.cs:53-67` | `AppContext.BaseDirectory` | stays in Native; the host may pass a directory |
| Lab stems | `KnobDoor.cs:1316`, `PushBarDoor.cs:927`, `SlidingDoor.cs:1448`, `GlassDoor.cs:1050`, `ElevatorDoor.cs:540`, `LockCylinder.cs:610` | written only when a lab sets `StemFolder` | `LabLevers` |
| FMOD debug log | `FmodDebugLog.cs:46` | `OPENFPS_FMOD_DEBUG_FILE` or the temp directory | `MixSettings` |

Not read by the library: speech (`Speech/voices.csv`, an embedded resource of Common, host-side),
bird calls (listed by `BirdLife`, host-side today), client settings.

Environment variables: 37 in 16 library files (appendix D). They are of three kinds:

- settings a player or a server owner uses (`OPENFPS_MASTER_DB`, `OPENFPS_MASTER_MAKEUP_DB`,
  `OPENFPS_LISTENING_LEVEL`, `OPENFPS_LEVEL_COMPRESSION`, `OPENFPS_MIXER_RATE`,
  `OPENFPS_RESAMPLER`, `OPENFPS_FMOD_OUTPUT`, `OPENFPS_DITHER`, `OPENFPS_LIMITER`): these become
  fields of `MixSettings` and `HearingSettings`, and open-fps's host reads the environment and fills
  them, so behaviour does not change;
- switches for parts of the model (`OPENFPS_EAR_MODEL`, `OPENFPS_EAR_WIND`, `OPENFPS_HRTF`,
  `OPENFPS_ECHOES`, `OPENFPS_WIDE_SOURCES`, `OPENFPS_CABIN_PATHS`, `OPENFPS_TRIANGLES`,
  `OPENFPS_EMBREE`, `OPENFPS_TILE_SCENES`, `OPENFPS_STEAMAUDIO_SIM`): options on the world;
- diagnostics (`OPENFPS_AUDIO_DEBUG`, `OPENFPS_AUDIO_TRACE`, `OPENFPS_AUDIO_CAPTURE*`,
  `OPENFPS_FMOD_WAV`, `OPENFPS_FMOD_DEBUG*`, `OPENFPS_PROFILE`, `OPENFPS_GLASS_*`,
  `OPENFPS_WAVES_*`, `SA_MIRROR`): a diagnostics options object.

`FmodAudioProvider.EnvDb(name)` (`:1720`) takes variable names at its call sites, so a few more
names exist than the reader lists.

---

## 6. The projects

| Project | What goes in | Uses | Lines | Native | Packages |
|---|---|---|---:|---|---|
| `OpenFPS.Geometry` | `Geometry/*` (triangles, BVH, `TriangleWorld`, builder, surfaces, shape library, solid contact), `GeometryUtils`, `MathHelper`, `SpatialGrid`, `BoxColumns`, `TileKey`, `ColliderShape`, `BoxContainment`, `WallBuild`, `PerfProbe` | nothing | 3,050 | no | none |
| `OpenFPS.Acoustics` | materials (`AcousticRegistry`, `MaterialProperties`, `PanelAcoustics`, `WallTransmission`, `AcousticBands`), `Diffraction`, `EarlyReflections`, `ImageSource`, `Enclosure`, rooms and openings (`AcousticMap`, `SparseAcousticOctree`, `RoomAcoustics`, `OpeningRoutes`, `FaceOpenings`, `AcousticVolumeGenerator`), `AcousticConstants`, `Localisation`; later `Room`, `Opening` and the ray queries now in `SpatialService` | Geometry | 6,150 | no | none, if the unused MemoryPack attributes go (decision 4) |
| `OpenFPS.Sound` | every model and preset in Common (engines, vehicles, wheels, tyres, bodies, cabins, signals, trains, aircraft, machines, small machines, pneumatics, nature, running water, waves, wind, rain, precipitation, runoff, lightning, doors, glass, bullets, weapons' acoustic numbers, footsteps, applause), the ear (`Hearing/*`), `Loudness`, `Spectrum`, `ShapedNoise`, `AudioClock` (until it is an interface), `AudioEmission`, and the synths in `AudioEngine/Core` | Geometry, Acoustics | 49,000 | no | Serilog (decision 5), MemoryPack attributes for `TransientSound` |
| `OpenFPS.Native` | `FmodNative/*` (FMOD's own C# wrapper), the `Phonon` P/Invoke surface (three files), `NativeAudioLibraries`, `BackgroundPriority` | nothing | 7,760 | libfmod, libphonon, libc | none |
| `OpenFPS.Audio` | `AudioEngine/Fmod/*`, `AudioEngine/SteamAudio/*` (not the bindings), `AudioEngine/Acoustics/*`, `AudioEngine/Data/*`, `AudioEngineFacade`, `VoiceManager`, `AudioBank`, `DoorRenderCache`, `RainField`, `RainSurvey`, `NearDrops`, and the host-neutral halves of `ClientAudioSystem`, `WorldAudioPlayer` and `BirdLife` | all four | 24,400 now, about 30,000 after the split | through Native | Serilog |

Who references what:

```
OpenFPS.Geometry   <- OpenFPS.Acoustics <- OpenFPS.Sound <- OpenFPS.Audio -> OpenFPS.Native
       ^                   ^                    ^                ^
       +---------- OpenFPS.Common (wire, entities, snapshots) ---+  (Common uses Geometry, Acoustics, Sound)
                           ^            ^
                  OpenFPS.Server    OpenFPS.Client.Core (session, adapters) -> OpenFPS.Audio
```

- The server references Common, which references Geometry, Acoustics and Sound. It never loads
  FMOD or Steam Audio.
- Client.Core references Audio and keeps the session, the snapshot adapter, `SpatialService`'s
  entity half and everything else of the game.
- Resonance references Audio (and through it the rest), and none of open-fps's game.
- Namespaces stay as they are while files move (`OpenFPS.Common`, `OpenFPS.Client.AudioEngine.*`),
  so every move is a file move and a project edit, not an edit of every `using`. A rename can come
  later as one commit, or never (decision 6).

Why five and not four: putting the mixer in Sound would make the server reference the FMOD
bindings. The P/Invoke declarations would not load anything until called, so it would work, but the
server build would carry FMOD's wrapper and the rule "the server never touches audio" would be a
convention instead of a project reference.

---

## 7. The host interface

### 7.1 Units and axes

Stated once, in the library's README and on the interface types:

- metres, seconds, kilograms, degrees Celsius, pascals for pressure where the code uses them
  (millibars where `WorldSnapshot.AirPressure` does today);
- Y up, X east, Z north (`PlayerCoordinates.cs`; Resonance uses the same: `Resonance.Content/
  Definitions.cs:10`);
- rotations as `System.Numerics.Quaternion`; FMOD's left-handed frame and Steam Audio's
  right-handed one are converted inside the library (`Phonon.MirrorZ`), never by the host;
- times from the host's `IAudioClock`, in seconds.

### 7.2 The audio world

What the host calls. It is what `ClientAudioSystem` does today, without the snapshot:

```csharp
public sealed class AudioWorld : IDisposable
{
    public AudioWorld(AudioWorldOptions options, IAcousticWorld world, IAudioProvider provider);

    // The listener: where the ears are, how they face, how fast they move, and what they ride in.
    public void SetListener(in ListenerState listener);

    // Sources. The host chooses the id (open-fps: the entity id; Resonance: its own handle).
    public void Add(int id, in SourceSpec spec);
    public void Update(int id, in SourceState state);
    public void Remove(int id, bool fade = true);

    // One-off sounds.
    public void Play(in TransientSound sound, Vector3 at, double sampledAt);
    public void PlayRecorded(string soundId, Vector3 at, float gainDb, float referenceDistance); // placed by a gain
    public void Footstep(in StepEvent step);

    // The weather and the air, as the host knows them.
    public void SetWeather(in WeatherState weather);   // precipitation, WindAir, temperature, humidity, pressure, road water

    // Once a frame.
    public void Tick(double now);
}

public readonly record struct SourceSpec(
    string SoundId,              // a recording, or a synth key ("engine:<preset>", "machine:<id>", "siren:<key>", ...)
    PlaybackMode Mode, float Volume, float Range, float MinDistance, Vector3 Offset, float ExtentMetres,
    bool IsSynth, bool IsGranular /* and the rest of SoundEmitterComponent's fields */);

public readonly record struct SourceState(
    Vector3 Position, Quaternion Rotation, Vector3 Velocity, double SampledAt,
    float TyreDemand, ReadOnlyMemory<WheelState> Wheels,
    bool Running, bool ServingStop, float WindowsOpen /* the controls a source has */);

public readonly record struct ListenerState(
    Vector3 Position, Quaternion Rotation, Vector3 Velocity, float EyeHeight,
    int RidingId, bool Indoor, int RegionHint, string FloorMaterial, Vector3 FeltWind);
```

Notes on the shapes:

- `SourceSpec` is `SoundEmitterComponent` without the wire format: the same fields, in a library
  type. open-fps's adapter copies one into the other in one function, and a test checks that every
  field of the component has a counterpart. Keeping the component in Common keeps the wire format
  (positional, append-only) out of the library.
- There is no throttle or gear in `SourceState` today, because open-fps does not send them: the
  engine voices infer load from speed and the wheels (`VirtualDriver`). A host that knows its
  throttle (Resonance's traffic does) can be given an optional `Controls` field later; it is not
  needed to move.
- `ListenerState` is what `ClientAudioSystem` reads from `LocalPlayerState` (position, eye height,
  rotation, riding id, indoor, region, room, shelter, floor material, felt wind).
- `IAudioProvider`, `SpatialEmitter` and `AcousticPathData` already exist and stay as the lower
  interface between the world and the mixer. Resonance's `WorldAudio` and `SpatialVoices` play the
  same two parts.

### 7.3 The world as input

```csharp
public interface IAcousticWorld
{
    TriangleWorld Geometry { get; }      // solids with a material per face; boxes are twelve triangles
    long GeometryVersion { get; }
    float TileMetres { get; }
    AcousticMap? Rooms { get; }          // rooms and openings as library values
    Atmosphere Air { get; }

    // Moving bodies the sound has to know about: their pose and size (vehicle shadows, cabins).
    bool TryGetBody(int id, out BodyState body);
    void ForEachBody(Action<BodyState> visit);
}

public readonly record struct Room(int Id, Vector3 Centre, Quaternion Rotation, Vector3 Size, bool Indoor,
                                   ReadOnlyMemory<int> FaceMaterials, float ReverbTimeScale, string Name);
public readonly record struct Opening(int RoomA, int RoomB, Vector3 Centre, Quaternion Rotation,
                                      float Width, float Height, float Aperture);
```

- The geometry is already host-neutral: `SolidSpec`, `Surface` and `TriangleWorldBuilder` take no
  entities. Only `EntityGeometry` (the adapter, `TriangleWorldBuilder.cs:258-313`) reads them.
  Resonance's boxes go in as `SolidSpec`s the same way open-fps's do.
- Rooms are boxes with an indoor flag and face materials, authored or worked out. `Room` and
  `Opening` carry what `RegionComponent` and `PortalComponent` carry today, so `AcousticMap`,
  `RoomAcoustics`, `OpeningRoutes`, `FaceOpenings` and `AcousticVolumeGenerator` change their
  parameter types and nothing else.
- Doors change as they move: the host updates the solids (a mover pose) and the openings'
  aperture; the world's existing rebuild path (`TriangleWorldBuilder.Move`, `Rebuild`) does the
  rest.
- The acoustic worker reads only this slice of the snapshot today (section 3.3), so `WorldSnapshot`
  can implement `IAcousticWorld` in open-fps's adapter at first, which keeps behaviour identical
  while the library stops naming it.

### 7.4 Data from where the host says

```csharp
public sealed record AudioDataPaths(
    string Sounds,                 // open-fps: <base>/ASSETS/SOUNDS
    string? Machines, string? Models,
    string? RenderCache, string? ShippedRenderCache,
    string? Hrtf = null);          // a SOFA file; null is Steam Audio's default
```

open-fps's host fills it exactly as the paths are worked out today, including the working-directory
`machines` and `models` folders, so nothing moves on disk.

### 7.5 Options where the engines differ

From item 9 of the note, and where each lives in open-fps now:

| Option | open-fps today | Resonance | Where it becomes an option |
|---|---|---|---|
| Master makeup and trim | `MasterMakeupDb` 6 dB, `MasterTrimDb`, both `static readonly` from the environment (`FmodAudioProvider.cs:756-761`) | none so far | `MixSettings.MakeupDb`, `TrimDb` |
| Rendering synthesised sources | engines render on `EngineRenderPool` off the mixer thread; other synths in their DSP callbacks | a pool per outlet with a 2.7 s ring each | an `IRenderScheduler` the provider takes; open-fps's is the default |
| Which rooms get a tail | the nearest within 50 m | only rooms with something sounding | `TailPolicy` |
| Recorded sounds placed by a gain and a reference distance | not used; sounds are placed by the law and declared levels | Sean's recordings | `PlayRecorded` and a `Placement` field on `SourceSpec` |
| Wide sources | `ExtendedSources.Enabled` (static, `OPENFPS_WIDE_SOURCES`) | not taken | `AudioWorldOptions.WideSources` |
| Voice budgets | `ClientAudioSystem` statics from the environment (engines 32, machines 10, places 36, front 6, echoes) | its own | `VoiceBudgets` |

### 7.6 How `ClientAudioSystem` splits

For each type that holds both sound logic and open-fps's world: how many of its members touch a host type (snapshot, entity, component, session, network) and how many lines those members span. Members that touch none can move into the library as they are; the rest are where the adapter is cut out.

| Type | Members | Lines | Members reading host | Their lines | Host refs | Most-read host types |
|---|---:|---:|---:|---:|---:|---|
| ClientAudioSystem | 295 | 3085 | 67 | 2480 | 596 | EntitySnapshot 144, WorldSnapshot 89, Components.SoundEmitterComponent 71, LocalPlayerState 70 |
| WorldAudioPlayer | 117 | 1115 | 15 | 647 | 86 | EntitySnapshot 22, WorldSnapshot 21, Networking.WorldAudioEvent 16, Speech 12 |
| SpatialService | 37 | 665 | 22 | 588 | 281 | Networking.EntityDefinition 71, EntitySnapshot 58, Components.ColliderComponent 50, WorldSnapshot 49 |
| BirdLife | 47 | 320 | 10 | 253 | 55 | EntitySnapshot 20, WorldSnapshot 9, Networking.EntityDefinition 9, Components.Transform 6 |
| RainSurvey | 76 | 590 | 6 | 246 | 93 | EntitySnapshot 33, Networking.EntityDefinition 19, WorldSnapshot 12, Components.Transform 10 |
| RainField | 50 | 325 | 1 | 93 | 11 | WorldSnapshot 9, EntitySnapshot 2 |
| Acoustics.AsyncAcousticWorker | 155 | 1245 | 17 | 735 | 51 | WorldSnapshot 36, EntitySnapshot 10, Components.Transform 3, Components.RegionComponent 1 |
| Acoustics.SpatialAcoustics | 31 | 230 | 14 | 194 | 70 | WorldSnapshot 30, EntitySnapshot 15, SpatialService 8, Components.RegionComponent 5 |
| Acoustics.OpeningGraph | 5 | 59 | 5 | 59 | 50 | Networking.EntityDefinition 15, Components.PortalComponent 12, WorldSnapshot 9, EntitySnapshot 7 |
| Acoustics.CabinWalls | 9 | 55 | 4 | 40 | 20 | EntitySnapshot 12, Networking.EntityDefinition 2, Components.SoundEmitterComponent 2, Components.Transform 2 |
| Acoustics.VehicleShadow | 11 | 108 | 1 | 25 | 15 | EntitySnapshot 6, Networking.EntityDefinition 3, WorldSnapshot 2, Components.ColliderComponent 2 |
| Fmod.FmodAudioProvider | 450 | 4684 | 4 | 212 | 19 | Components.PortalComponent 12, Components.RegionComponent 7 |
| SteamAudio.SteamAudioScene | 37 | 312 | 1 | 23 | 25 | Networking.EntityDefinition 9, EntitySnapshot 5, Components.ColliderComponent 3, WorldSnapshot 2 |
| Systems.AcousticVolumeGenerator | 6 | 348 | 3 | 339 | 120 | Networking.EntityDefinition 50, Components.RegionComponent 28, Components.PortalComponent 26, Components.Transform 12 |
| AudioEmission | 7 | 32 | 3 | 22 | 18 | EntitySnapshot 7, Networking.EntityDefinition 4, Components.Transform 4, Components.SoundEmitterComponent 3 |
| RoomAcoustics | 9 | 56 | 4 | 39 | 15 | Components.RegionComponent 15 |
| OpeningRoutes | 161 | 1581 | 4 | 133 | 15 | Components.RegionComponent 15 |

What goes where, by member:

- **To `AudioWorld` (library)**: the voice budgets and the ranking of live engines, machines and
  places once the candidates are known; horns and sirens (`StartHorn`, `UpdateHorns`, `SirenVoice`,
  `SirenModeFor`); front, cabin and place voices; the spreading of wide sources; footsteps, landings
  and breath (`SubmitFootstep`, `SubmitLanding`, `OnBreath`); talkers and own voice; ambience;
  ground (`ApplyGround`, `Coherence`); loop echo levels; the physical level look-ups.
- **To open-fps's adapter (host)**: the game features it constructs today (`DrivingAids`,
  `BeaconAids`, `ClientAudioSystem.cs:369-373`); walking `world.Entities` and `AudioEntityIds`; reading
  `EntityDefinition.SoundEmitter` (55 reads), `EntitySnapshot.Transform` and `Velocity`;
  `LocalPlayerState` (70 reads); region names for speech (`NameOfGap`, `NameOfPlace`,
  `OutdoorNameFor`, `DoorwayName`); `ForgetEntity` and `LeaveWorld` as `Remove` calls.
- The cut runs through the middle of the big methods. `Update` (lines 460-934) reads the snapshot,
  decides, and submits, in one pass. The way through is to first turn each snapshot read into a
  read of a per-frame source table the adapter builds (id, spec, state), inside the same class, with
  the emitter stream unchanged; then move the class body out. `WorldAudioPlayer` (86 host references
  in 15 of 117 members) and `BirdLife` (55 in 10 of 47) split the same way and are smaller.

`SpatialService` (281 host references in 22 of 37 members) is the client's ray service over
entities; the acoustics use it through `SpatialAcoustics` (8 calls). Since geometry stage 1 its rays
go to `TriangleWorld`, so the acoustic queries (occlusion with per-band transmission, the material
under a ray) can move to Acoustics over `IAcousticWorld.Geometry`, and `SpatialService` keeps the
entity lookups for the game.

---

## 8. The migration, stage by stage

Rules for every stage:

- One project or one seam per merge. `git mv` for moves, so history follows the files.
- Before and after each stage: the test suite, the render fingerprint and the emitter-stream replay
  (stage 0) give the same result. A stage that changes any of them is not finished.
- Moves do not change namespaces, so the diff of a move is file renames and project files.
- The ratchet test (section 9) is updated in the same commit: its list of allowed crossings only
  shrinks.

| Stage | What | Sessions | Main risk |
|---|---|---:|---|
| 0 | Guards: ratchet test, render fingerprint, emitter-stream replay | 1 to 2 | some renders are not deterministic |
| 1 | `OpenFPS.Native` | 0.5 to 1 | internal visibility of `Phonon` |
| 2 | `OpenFPS.Geometry` | 1 | collides with geometry stage 2 work |
| 3 | `OpenFPS.Sound` and the first half of `OpenFPS.Acoustics` | 2 to 3 | wire hash and door fingerprint miss moved files |
| 4 | Rooms and openings as values; the rest of Acoustics | 2 | entity order changes the map |
| 5 | Statics to instances | 2 to 3 | a missed reader keeps reading the old static |
| 6 | The world input; `OpenFPS.Audio` | 3 to 5 | the acoustic worker's threading |
| 7 | Split `ClientAudioSystem`, `WorldAudioPlayer`, `BirdLife` | 4 to 6 | behaviour drift in the big methods |
| 8 | Versions and how Resonance takes it | 1 | none in open-fps |
| | **Total** | **17 to 24** | |

A session here is one working session of an agent, as in docs/GEOMETRY.md.

### Stage 0: guards (1 to 2 sessions)

- **Ratchet test** (`LibraryBoundaryTests`): see section 9. Generated from this survey's
  `crossings.tsv`, it fails when a library file gains a reference to a host type.
- **Render fingerprint**: a test that renders a fixed set of offline sounds through the library
  (an engine at three speeds, each door model, rain on three surfaces, a siren, a train pass, a
  clap in the traced room, thunder), hashes the float buffers, and compares with stored hashes.
  First find which renders are deterministic (fixed seeds, no wall clock, no thread-pool order);
  the others are left out and listed.
- **Emitter-stream replay**: a recording `IAudioProvider` that writes every call
  (`PlaySpatialSound`, `UpdateSpatialAttributes`, `SetAcousticPath`, listener updates) to a list,
  and a test that drives `ClientAudioSystem` with a stored sequence of `WorldSnapshot`s (a walk, a
  drive, the city with traffic and rain) and compares the stream with a stored one. This is what
  makes stage 7 checkable without listening.
- Risk: a stored stream is brittle against intended changes elsewhere; regenerate it in the same
  commit as an intended change, with the reason.
- As built: section 13. The rule for regenerating is 13.4.

### Stage 1: `OpenFPS.Native` (0.5 to 1 session)

- Move `FmodNative/*`, `Phonon.cs`, `PhononSim.cs`, `PhononAmbisonics.cs`, `NativeAudioLibraries`,
  `BackgroundPriority`. No crossings, no layering issues.
- `Phonon` is `internal` and used by AudioLab spikes and tests: `InternalsVisibleTo` for
  `OpenFPS.Client.Core`, `OpenFPS.Audio` (later), `OpenFPS.Tests` and `OpenFPS.AudioLab`.
- The native libraries are copied from `lib/` by the executables' projects (`OpenFPS.Client`,
  `OpenFPS.Client.Gtk`, `OpenFPS.AudioLab`), not by Client.Core, so the move does not touch them.
  Resonance ships its own copies.
- As built: section 14.

### Stage 2: `OpenFPS.Geometry` (1 session)

- Done in geometry stage 2: `EntityGeometry` is now `OpenFPS.Common/EntityGeometry.cs` (host side); `Geometry/` reads no entities.
- Move `BoxContainment` (from `SparseAcousticOctree.cs`) and `WallBuild` (from
  `WallTransmission.cs`) into Geometry; move `TileKey` out of `Tiles.cs` and `ColliderShape` out of
  `Components.cs`.
- Move `Geometry/*`, `GeometryUtils.cs`, `SpatialGrid.cs`, `BoxColumns.cs`, `PerfProbe.cs`.
  Common references Geometry.
- After this stage the geometry stage 2 work lands in the Geometry project (section 9.2).
- As built: section 15. `ColliderShape` was not moved (15.2).

### Stage 3: `OpenFPS.Sound` and the first half of `OpenFPS.Acoustics` (2 to 3 sessions)

The ten small fixes first, each its own commit with the tests unchanged:

1. `Speech.NormalDb` and `Speech.BufferRmsDbfs` (read by `Loudness.cs:234-249`, `EarModel.cs:42`):
   move the two reference-voice constants into `Hearing`; `Speech` reads them from there.
2. `PhysicsConstants.Gravity`, `WalkSpeed`, `SprintSpeed`, `PlayerHeight` (read by `Glass.cs`,
   `Breathing.cs`, `EarWind.cs`, `ExternalBallistics.cs`): the library declares the body constants it
   needs; `PhysicsConstants` refers to them, so the values are one.
3. `RoadData.DefaultSurface` (`WheelDynamics.cs:290`, `EngineProcessor.cs:1388`): to
   `RoadSurfaces.Default` in `Chassis.cs`.
4. `EntityState.EncodeTyreDemand` (`Messages.cs:480`): into `WheelState`.
5. `MixerQuality.DefaultRate` (eleven synths): a constant in Sound that `MixerQuality` refers to.
6. `AudioEmission` reads `MachineRegistry` and `VehicleProfile`, so it belongs in Sound, not
   Acoustics. It also reads entity snapshots, so it stays in Common until stage 6.
7. `PuddleField` reads `RoadData` (`RoadWater.cs:437-550`): it takes the road centrelines as a list
   of points instead.
8. `TransientSound` and `SoundCharacter` out of `AudioEvents.cs`; `WorldAudioEvent` stays.
9. `WheelState` out of `Messages.cs`.
10. `PlaybackMode` and `WeatherType` out of `Components.cs`.

Then move: every Common file in Sound's list, `AudioEngine/Core/*` except the five Audio files, and
the Acoustics files with no room or opening in them (materials, walls, panels, diffraction,
reflections, image source, enclosure, octree, constants, localisation). The survey says all of
these then have no path to a host type (section 1).

Risks:

- `WireContract.Hash` hashes Common's sources only. After `TransientSound`, `WheelState`,
  `Precipitation`, `WindAir` and `LightningStrike` leave Common, a change to them would not change
  the hash, and a client and server built from different versions would misread each other without
  being refused. The hash target must also cover the wire types in the library (or their files).
- `DoorModelFingerprint` lists door model files relative to Common
  (`OpenFPS.Common.csproj`, `DoorModelSource`). The list moves with the files into Sound; if it is
  left behind the build fails (the hash task reads every listed file), which is the safe failure.
- `TransientSound` is `[MemoryPackable]`: Sound gets the MemoryPack package (decision 4).
- Work in flight on these files will conflict with the moves. At the time of the survey every named
  branch except `geometry-stage-2` is merged, but agents land audio work most days: do each move
  when no audio branch is open, and say so before starting.
- As built: section 16. Five more fixes were needed for what landed after the survey (16.2).

### Stage 4: rooms and openings as values (2 sessions)

- Add `Room` and `Opening` to Acoustics with the fields of `RegionComponent` and `PortalComponent`.
- `AcousticMap` holds them; `RoomAcoustics`, `OpeningRoutes`, `FaceOpenings`,
  `AcousticVolumeGenerator`, `OpeningGraph` and `FmodAudioProvider` (19 references) take them.
- `AcousticVolumeGenerator` takes rooms, openings and solids instead of `EntityDefinition` lists;
  the adapter (Common for the server, Client.Core for the client) builds them in the same order the
  entity lists have today, so the octree and the region ids come out the same.
- Move the rest of Acoustics.
- Risk: region ids and iteration order. Region ids are the server's entity ids, and they appear in
  logs, the client's place names and the tests; the adapter must pass them through unchanged.

### Stage 5: statics to instances (2 to 3 sessions)

In order of how much they block: `TracedReverbSet` and the `Current` statics into the provider;
`WindField` and `Runoff` into a per-world weather; `AudioClock` behind `IAudioClock`;
`AudioPhysics` and `MixerQuality.MixerRate` into the world's atmosphere and mixer format;
`EarModel` and `Loudness` settings into `HearingSettings`; `MachineRegistry` and `ModelLibrary`
into a `ModelCatalog` behind the static facade; the console levers into `MixSettings`; the lab
levers into `LabLevers`.

- Each is a mechanical change of readers, but the readers are many (`MixerQuality.MixerRate` 36,
  `AudioClock.Now` 15 in the library, `MachineRegistry.VehicleFor` 100 in all). A reader left on the
  old static still compiles if the static is kept, so each static is deleted when its last reader is
  moved, and the deletion is the check.
- Bonus: the test assembly can run in parallel again once the world state is per instance.

### Stage 6: the world input and `OpenFPS.Audio` (3 to 5 sessions)

- `IAcousticWorld` in Acoustics; `WorldSnapshot` implements it in Common at first (behaviour
  identical), then the client's adapter fills it.
- `AsyncAcousticWorker`, `SpatialAcoustics`, `OpeningGraph`, `CabinWalls`, `VehicleShadow`,
  `EngineReflections`, `SteamAudioScene` (its `BoxesFromWorld` reads entities at lines 112-131),
  `RainSurvey` and `RainField` take `IAcousticWorld`.
- `WorldAudioPlayer.PlanRoomEchoes` and its constants move into the library; `EarlyCopies` calls it
  there.
- `TalkerVoice` takes decoded PCM (or an `IVoiceDecoder`); `VoiceCodec` stays in the client.
- The acoustic queries of `SpatialService` (occlusion with per-band transmission, the material
  under a ray) move to Acoustics over the triangle world.
- Then create `OpenFPS.Audio` and move `AudioEngine/Fmod`, `SteamAudio`, `Acoustics`, `Data`, the
  five Core files, `RainField`, `NearDrops`.
- Risk: the acoustic worker runs on its own thread and holds the snapshot it was given; an
  `IAcousticWorld` must be an immutable view per request, as the snapshot is, or results drift.

### Stage 7: split the integration types (4 to 6 sessions)

- `ClientAudioSystem` first: inside the class, replace each snapshot read with a read of a source
  table the adapter fills once a frame; the emitter-stream replay must stay identical after each
  method. Then move the class body to `AudioWorld` and leave the adapter.
- Then `WorldAudioPlayer` (the `WorldAudioEvent` decoding stays, the playing moves) and `BirdLife`
  (species and calling move, the habitat survey from entities stays).
- Risk: these are the most-tuned files in the repository and the big methods interleave reading
  and deciding. The replay test is the only thing that makes this safe; listening is the second
  check.

### Stage 8: versions and Resonance (1 session)

- Tag versions of the library projects; a short `CHANGES` per version saying what a consumer must
  do. Resonance takes a git submodule pinned to a tag (decision 8), credits open-fps in its
  `NOTICE.md`, and swaps its copies one subsystem at a time.

---

## 9. Enforcement

### 9.1 The rule and the test

The rule: the library references nothing in `OpenFPS.Common` outside the library's own files,
`OpenFPS.Client*`, `OpenFPS.Server`, the `Networking` and `Components` namespaces, the snapshots or
the session.

Two tests, one for before the projects exist and one for after:

- **Before (stages 0 to 6)**: `LibraryBoundaryTests.NoNewCrossings`. The test reads a checked-in
  list of library files (the ones in appendix A marked a or c) and an allowlist of crossings
  (`file -> host type -> count`, generated from `crossings.tsv`). It scans each library file's
  source for the forbidden names (`WorldSnapshot`, `EntitySnapshot`, `EntityDefinition`,
  `OpenFPS.Common.Networking`, `OpenFPS.Common.Components` except the three moved enums,
  `OpenFPS.Client.Core.Session`, `LocalPlayerState`, `ClientWorldState`, `SpatialService`,
  `WorldAudioPlayer`, `VoiceCodec`, `Speech.`, `PhysicsConstants.`, `RoadData`) and fails if any
  count is above its allowance or any file is new and not listed. The allowance only goes down;
  the survey tool regenerates it.
  As built (13.1), it binds the sources instead of scanning for names: a name list misses a `var`
  that reads a snapshot, which is how most of these files read one. It counts what `crossings.tsv`
  counts, every host type, with no list to keep.
- **After (each project exists)**: `LibraryBoundaryTests.LibraryReferencesOnlyLibrary`. For each
  library assembly, `Assembly.GetReferencedAssemblies()` must contain only the runtime, the allowed
  packages (Serilog, MemoryPack, if decided) and lower library projects. The compiler already
  refuses a type that is not referenced; this test refuses the project reference itself, which is
  what someone would add to make an error go away.
- **Statics**: a third test lists every static field of the library assemblies by reflection and
  fails on a mutable one that is not in the exception list of section 4.2.

### 9.2 Geometry stage 2, library-first

The `geometry-stage-2` branch has one commit beyond main (324ee0b1: the ground is dirt), all in the
server's `MapManager`, `MapTemplates` and a test. Nothing in `OpenFPS.Common/Geometry` yet. The
rest of stage 2 (wedges, stairs, arches, ground normals, walkable slope, four wheel rays) will add
to `Geometry/` and to `SharedMovementEngine`.

- Do migration stage 2 (the Geometry project) before geometry stage 2 adds more, or at least split
  `EntityGeometry` out of `TriangleWorldBuilder.cs` now so `Geometry/` has no entity reads at all.
- New shapes, normals, slope and ray queries go in `Geometry/` and take `SolidSpec`, `Surface` and
  plain vectors. Nothing there names `EntityDefinition`, `Components` or `Networking`.
- Player movement (`SharedMovementEngine`, `PhysicsUtils`) stays host and calls Geometry's queries.
- Wheel rays: a Geometry query (a ray down, the hit, its normal and surface) that `WheelDynamics`
  calls through a small `IGroundProbe` it is given, so the vehicle model stays in Sound without
  knowing the world type.
- Terrain (geometry stage 3), the heightfield and the voxel layer (stage 5) are new: build them in
  Geometry from the first commit.
- The ratchet test covers `Geometry/` with an allowance of zero from the day it lands.

---

## 10. Risks

- **Silent wire mismatch**: the wire hash covers Common only (stage 3). Fix in the same commit as
  the first wire type moves. Done: it covers Geometry, Acoustics and Sound but its synthesis (16.4).
- **Stale door renders**: the door fingerprint lists Common files (stage 3). The build fails loudly
  if left; move the list with the files. Done: the list is in OpenFPS.Sound, hashed by file name (16.4).
- **Branches in flight**: each move conflicts with every branch that edits a moved file. Today only
  `geometry-stage-2` is unmerged, but audio branches open most days. `git mv` and unchanged
  namespaces keep the conflicts to renames, which git follows when the content is unchanged, but a
  branch that edits a file and a move of the same file still need a rebase.
- **Bit-identical is not fully checkable**: renders on thread pools and anything stamped by the wall
  clock cannot be fingerprinted. The emitter stream covers the integration layer; listening covers
  the rest.
- **Internal types**: `Phonon` and several Steam Audio classes are `internal` with
  `InternalsVisibleTo` for Tests and AudioLab. Every new assembly needs the same attributes, or
  the spikes stop compiling.
- **Logging**: 12 library files log through Serilog's static `Log`. A host that does not configure
  Serilog gets no audio log; Resonance has its own `AudioLog`.
- **Statics kept as facades**: a static facade over an instance (the catalogue, the clock) works
  for one world per process. Two worlds (tests, a lab comparing settings) need the instance passed;
  until then they share.
- **The AskRoutes leak** the note reports (a cached route answer keeps an old `OpeningGraph`
  alive) gets worse with per-tile graphs; fix it before stage 6 moves the graph.

---

## 11. Decisions for Cody

1. **Five projects or four.** Five keeps FMOD and Steam Audio out of the server's references
   (recommended). Four folds Audio into Sound.
2. **Vehicle physics in Sound.** Wheel dynamics, tyres, chassis and running gear go with the sound
   models, as Resonance did (recommended), or into a later `OpenFPS.Vehicles`.
3. **open-fps's designed sounds.** `AdminGun`, `TeleporterSounds`, `HandOverSounds`: leave them in
   Sound (no cost now), or move them to a game-content project of open-fps's.
4. **MemoryPack in the library.** Keep `[MemoryPackable]` on `TransientSound` and give Sound the
   attribute package (recommended: the wire bytes stay identical), or keep a wire copy in Common and
   convert. `AcousticMap` and `SparseAcousticOctree` carry the attribute too, but nothing the survey
   found serialises them (they are not in any message); the attribute can probably go.
5. **Logging.** Keep Serilog's static `Log` (recommended for now: no behaviour change), or put a
   small log sink on the world options.
6. **Namespaces.** Keep them while moving (recommended), and decide later whether to rename to
   `OpenFPS.Sound.*` in one commit.
7. **Statics.** World state becomes per-instance (clock, weather, runoff, air, traced reverb,
   talkers); settings and catalogues go behind one options object the host creates, with static
   facades kept until their last reader moves (recommended). The stricter version (no facades at
   all) costs about two more sessions.
8. **How Resonance takes it.** A git submodule pinned to tags (recommended while both engines move
   fast), or a NuGet package from a local feed.
9. **When.** Stages 0 to 2 are cheap and protect the geometry work now in progress. Stages 3 to 5
   unblock Resonance swapping its model and acoustics copies. Stages 6 and 7 are the expensive part
   and unblock swapping its world audio.
10. **Emitter spec.** A library `SourceSpec` mirroring `SoundEmitterComponent` with a field-by-field
    test (recommended), or moving the component itself into the library with its wire format.

---

## 12. Appendices

### A. Every type in scope, by file

Group a = library (with its project), b = host, c = a value both need (lives in the library). Lines are the declaration spans. A file whose types fall in more than one group has one row per group.

| File | Group | Lines | Types | Note |
|---|---|---:|---|---|
| CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs | a:Audio | 1578 | AcousticRequest, AsyncAcousticWorker |  |
| CC/AudioEngine/Acoustics/CabinWalls.cs | a:Audio | 87 | CabinWalls |  |
| CC/AudioEngine/Acoustics/EngineReflections.cs | a:Audio | 445 | EngineReflections |  |
| CC/AudioEngine/Acoustics/GroundReflection.cs | a:Audio | 94 | GroundReflection |  |
| CC/AudioEngine/Acoustics/OpeningGraph.cs | a:Audio | 78 | OpeningGraph |  |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | a:Audio | 298 | SpatialAcoustics |  |
| CC/AudioEngine/Acoustics/VehicleShadow.cs | a:Audio | 148 | VehicleShadow |  |
| CC/AudioEngine/Core/AdminGunSynth.cs | a:Sound | 26 | AdminGunSynth | synthesis |
| CC/AudioEngine/Core/Aircraft/AircraftSynth.cs | a:Sound | 618 | AircraftSynth, BladeRow, JetStream | synthesis |
| CC/AudioEngine/Core/AmbisonicFormat.cs | a:Audio | 104 | AmbisonicFormat, AmbisonicLayout |  |
| CC/AudioEngine/Core/At44k.cs | a:Sound | 21 | At44k | synthesis |
| CC/AudioEngine/Core/AudioBank.cs | a:Audio | 54 | AudioBank |  |
| CC/AudioEngine/Core/AudioEngineFacade.cs | a:Audio | 584 | AudioEngineFacade |  |
| CC/AudioEngine/Core/AudioPhysics.cs | a:Sound | 131 | AudioPhysics | synthesis |
| CC/AudioEngine/Core/BoundaryModel.cs | a:Sound | 111 | BoundaryModel, BoundaryProbe, BoundaryTap | synthesis |
| CC/AudioEngine/Core/BrakeSqueal.cs | a:Sound | 78 | BrakeSqueal | synthesis |
| CC/AudioEngine/Core/DoorRenderCache.cs | a:Audio | 124 | DoorRenderCache | disk cache of door renders |
| CC/AudioEngine/Core/EarTimbres.cs | a:Sound | 72 | EarTimbres | synthesis |
| CC/AudioEngine/Core/Engine/BandEq.cs | a:Sound | 132 | BandEq | synthesis |
| CC/AudioEngine/Core/Engine/BayRadiation.cs | a:Sound | 98 | BayRadiation | synthesis |
| CC/AudioEngine/Core/Engine/CabinPaths.cs | a:Sound | 171 | CabinPaths | synthesis |
| CC/AudioEngine/Core/Engine/CoolingSystem.cs | a:Sound | 138 | CoolingSystem | synthesis |
| CC/AudioEngine/Core/Engine/Driveline.cs | a:Sound | 513 | DriveOrder, Driveline, Driver, DriverAction, VirtualDriver | synthesis |
| CC/AudioEngine/Core/Engine/EngineSynth.cs | a:Sound | 1770 | EngineSynth | synthesis |
| CC/AudioEngine/Core/Engine/ExhaustNetwork.cs | a:Sound | 703 | ExhaustNetwork | synthesis |
| CC/AudioEngine/Core/Engine/ExhaustRadiation.cs | a:Sound | 156 | ExhaustRadiation | synthesis |
| CC/AudioEngine/Core/Engine/IntakeNetwork.cs | a:Sound | 273 | IntakeNetwork | synthesis |
| CC/AudioEngine/Core/Engine/Waveguide.cs | a:Sound | 472 | Gas, JetNoise, Junction, OnePole, OpenEnd, Pipe, WaveLine | synthesis |
| CC/AudioEngine/Core/Nature/EarWindSynth.cs | a:Sound | 286 | EarWindSynth | synthesis |
| CC/AudioEngine/Core/Nature/EventSum.cs | a:Sound | 283 | EventSum | synthesis |
| CC/AudioEngine/Core/Nature/ExtendedSources.cs | a:Sound | 188 | ExtendedSources | synthesis |
| CC/AudioEngine/Core/Nature/FallingWaterSynth.cs | a:Sound | 731 | FallingWaterSynth | synthesis |
| CC/AudioEngine/Core/Nature/FireSynth.cs | a:Sound | 459 | FireSynth | synthesis |
| CC/AudioEngine/Core/Nature/FoliageSynth.cs | a:Sound | 470 | FoliageSynth | synthesis |
| CC/AudioEngine/Core/Nature/RainPatch.cs | a:Sound | 174 | RainLayer, RainPatch | synthesis |
| CC/AudioEngine/Core/Nature/RainSynth.cs | a:Sound | 944 | RainSynth | synthesis |
| CC/AudioEngine/Core/Nature/Resonator.cs | a:Sound | 27 | Resonator | synthesis |
| CC/AudioEngine/Core/Nature/RunningWaterSynth.Basin.cs | a:Sound | 1315 | RunningWaterSynth | synthesis |
| CC/AudioEngine/Core/Nature/ShoreReferences.cs | a:Sound | 196 | ShoreReferences | synthesis |
| CC/AudioEngine/Core/Nature/ShoreSynth.cs | a:Sound | 1291 | ShoreSynth | synthesis |
| CC/AudioEngine/Core/Nature/TextureStatistics.cs | a:Sound | 480 | TextureStatistics | synthesis |
| CC/AudioEngine/Core/Pneumatics/AirSystem.cs | a:Sound | 259 | AirPort, AirSystem | synthesis |
| CC/AudioEngine/Core/Rail/ElectricDrive.cs | a:Sound | 120 | ElectricDrive | synthesis |
| CC/AudioEngine/Core/Rail/RailNoise.cs | a:Sound | 348 | BogieVoice, TrackResponse | synthesis |
| CC/AudioEngine/Core/Rail/SteamFrontEnd.cs | a:Sound | 169 | SteamFrontEnd | synthesis |
| CC/AudioEngine/Core/Rail/TrainSynth.cs | a:Sound | 354 | BodyDrum, FanNoise, TrainSynth | synthesis |
| CC/AudioEngine/Core/ReportMeasure.cs | a:Sound | 132 | ReportMeasure, ReportMeasurement | synthesis |
| CC/AudioEngine/Core/Signals/ChimeHorn.cs | a:Sound | 271 | ChimeHorn | synthesis |
| CC/AudioEngine/Core/Signals/ElectricHorn.cs | a:Sound | 398 | ElectricHorn | synthesis |
| CC/AudioEngine/Core/Signals/ElectronicSiren.cs | a:Sound | 229 | ElectronicSiren | synthesis |
| CC/AudioEngine/Core/Signals/HeadShadow.cs | a:Sound | 49 | HeadShadow | synthesis |
| CC/AudioEngine/Core/Signals/Modes.cs | a:Sound | 43 | Mode | synthesis |
| CC/AudioEngine/Core/Signals/SteamWhistle.cs | a:Sound | 180 | SteamWhistle | synthesis |
| CC/AudioEngine/Core/Signals/StruckBell.cs | a:Sound | 193 | StruckBell | synthesis |
| CC/AudioEngine/Core/Thunder.cs | a:Sound | 901 | Thunder | synthesis |
| CC/AudioEngine/Core/TransientSynth.cs | a:Sound | 263 | TransientSynth | synthesis |
| CC/AudioEngine/Core/VehicleSynth.cs | a:Sound | 617 | VehicleRender, VehicleSynth | synthesis |
| CC/AudioEngine/Core/VoiceManager.cs | a:Audio | 420 | IVoiceSink, ScoredCandidate, VoiceManager |  |
| CC/AudioEngine/Core/WeaponSynth.cs | a:Sound | 295 | WeaponProfile, WeaponSynth | synthesis |
| CC/AudioEngine/Core/WetTyres.cs | a:Sound | 323 | WetTyres | synthesis |
| CC/AudioEngine/Core/Yard/SmallMachineSynth.cs | a:Sound | 351 | SmallMachineSynth | synthesis |
| CC/AudioEngine/Data/AcousticPathData.cs | c:Audio | 52 | AcousticPathData | the source description the host fills |
| CC/AudioEngine/Data/SpatialEmitter.cs | c:Audio | 299 | EmitterType, SpatialEmitter, SynthWaveType | the source description the host fills |
| CC/AudioEngine/Fmod/BoundaryProximityProcessor.cs | a:Audio | 266 | BoundaryProximityProcessor, BoundaryVoiceState |  |
| CC/AudioEngine/Fmod/DspFault.cs | a:Audio | 131 | DspCallback, DspFault |  |
| CC/AudioEngine/Fmod/EarStage.cs | a:Audio | 164 | EarProcessor, EarVoiceState |  |
| CC/AudioEngine/Fmod/EarWindVoice.cs | a:Audio | 114 | EarWindProcessor, EarWindState |  |
| CC/AudioEngine/Fmod/EngineProcessor.cs | a:Audio | 2444 | EchoDiffuser, EchoProcessor, EngineEchoState, EngineProcessor, EngineTapState, EngineVoiceState, SoftCeiling, SourceClock, TapProcessor |  |
| CC/AudioEngine/Fmod/EngineRenderPool.cs | a:Audio | 113 | EngineRenderPool |  |
| CC/AudioEngine/Fmod/FmodAudioProvider.Ear.cs | a:Audio | 5760 | FmodAudioProvider |  |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs | a:Audio | 208 | FmodHelpers, FmodResourceManager, MathHelper, Rolloff, SoundLoadState |  |
| CC/AudioEngine/Fmod/FmodDebugLog.cs | a:Audio | 49 | FmodDebugLog |  |
| CC/AudioEngine/Fmod/GranularBank.cs | a:Audio | 183 | GranularBank |  |
| CC/AudioEngine/Fmod/GranularProcessor.cs | a:Audio | 241 | GranularProcessor, GranularVoiceState |  |
| CC/AudioEngine/Fmod/IAudioProvider.cs | a:Audio | 200 | IAudioProvider, VoiceLevel |  |
| CC/AudioEngine/Fmod/MachineProcessor.cs | a:Audio | 562 | AircraftVoiceState, BellVoiceState, HornVoiceState, IRenderedVoice, MachineProcessor, MachineVoiceState, PhysicalVoiceState, SirenVoiceState |  |
| CC/AudioEngine/Fmod/MasterDither.cs | a:Audio | 105 | MasterDither |  |
| CC/AudioEngine/Fmod/MasterLimiter.cs | a:Audio | 313 | MasterLimiter, TruePeakLimiter |  |
| CC/AudioEngine/Fmod/MasterTap.cs | a:Audio | 165 | MasterTap |  |
| CC/AudioEngine/Fmod/MixerQuality.cs | a:Audio | 151 | MixerQuality |  |
| CC/AudioEngine/Fmod/NatureVoices.cs | a:Audio | 394 | FireVoiceState, FoliageVoiceState, NaturePlaceState, NatureVoiceState, PlacedNatureVoice, WaterFeatureVoice, WaterTapState, WaterVoiceState |  |
| CC/AudioEngine/Fmod/NonFinite.cs | a:Audio | 112 | IGuardedUnit, NonFinite, NonFiniteUnit |  |
| CC/AudioEngine/Fmod/OwnVoice.cs | a:Audio | 247 | OwnVoiceProcessor, OwnVoiceRing, OwnVoiceTap |  |
| CC/AudioEngine/Fmod/RailVoice.cs | a:Audio | 106 | TrainTapState, TrainVoiceState |  |
| CC/AudioEngine/Fmod/RainVoices.cs | a:Audio | 164 | RainFeed, RainFeeds, RainVoiceState |  |
| CC/AudioEngine/Fmod/SynthProcessor.cs | a:Audio | 188 | SynthProcessor, SynthVoiceState |  |
| CC/AudioEngine/Fmod/TakeLevels.cs | a:Audio | 106 | TakeLevels |  |
| CC/AudioEngine/Fmod/TalkerVoice.cs | a:Audio | 39 | Talkers |  |
| CC/AudioEngine/Fmod/TalkerVoice.cs | a:Audio | 179 | TalkerStream | plays decoded voice-chat PCM; the codec is host-side |
| CC/AudioEngine/SteamAudio/AcousticGeometry.cs | a:Audio | 141 | AcousticGeometry |  |
| CC/AudioEngine/SteamAudio/AmbiAxes.cs | a:Audio | 46 | AmbiAxes |  |
| CC/AudioEngine/SteamAudio/AmbisonicBedDsp.cs | a:Audio | 221 | AmbisonicBedDsp, AmbisonicBedState |  |
| CC/AudioEngine/SteamAudio/DiffuseLate.cs | a:Audio | 345 | DiffuseLateConvolver, DiffuseLateIr, DiffuseLateNoise |  |
| CC/AudioEngine/SteamAudio/EarDecorrelator.cs | a:Audio | 152 | DiffuseBranch, EarDecorrelator |  |
| CC/AudioEngine/SteamAudio/EarlyCopies.cs | a:Audio | 154 | EarlyCopies |  |
| CC/AudioEngine/SteamAudio/HrtfBands.cs | a:Audio | 75 | HrtfBands |  |
| CC/AudioEngine/SteamAudio/LateField.cs | a:Audio | 276 | LateField |  |
| CC/AudioEngine/SteamAudio/LateTail.cs | a:Audio | 426 | Fft, LateTailConvolver, LateTailIr, SdmTailIr, SharedInputConvolver |  |
| CC/AudioEngine/SteamAudio/Phonon.cs | a:Native | 575 | Phonon | Steam Audio bindings |
| CC/AudioEngine/SteamAudio/SmoothTail.cs | a:Audio | 602 | SmoothTail |  |
| CC/AudioEngine/SteamAudio/SteamAudioDsp.cs | a:Audio | 374 | SteamAudioDsp, SteamAudioVoiceState |  |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | a:Audio | 441 | SteamAudioScene |  |
| CC/AudioEngine/SteamAudio/SteamAudioSimulator.cs | a:Audio | 535 | SteamAudioSimulator |  |
| CC/AudioEngine/SteamAudio/TileSceneSet.cs | a:Audio | 219 | TileSceneSet |  |
| CC/AudioEngine/SteamAudio/TracedEchoes.cs | a:Audio | 464 | TracedEchoDsp, TracedEchoRig, TracedEchoes |  |
| CC/AudioEngine/SteamAudio/TracedReverb.cs | a:Audio | 674 | TracedReverb, TracedReverbSet |  |
| CC/AudioEngine/SteamAudio/TracedReverbDsp.cs | a:Audio | 757 | DiffuseTail, PreDelay, TracedReverbDsp, TracedReverbState |  |
| CC/BeaconAids.cs | b | 561 | BeaconAids | client game |
| CC/BirdLife.cs | b | 400 | BirdLife | SPLIT: species choice and calling go to Sound/Audio; the habitat survey reads entities |
| CC/ClientAudioSystem.cs | b | 4078 | ClientAudioSystem | SPLIT: the host-neutral audio world goes to Audio, the snapshot adapter stays |
| CC/ClientGeometry.cs | b | 202 | ClientGeometry | open-fps's geometry adapter (entities to TriangleWorld) |
| CC/ClientWorldState.cs | b | 1162 | ClientWorldState | client game |
| CC/DrivingAids.cs | b | 565 | DrivingAids | client game |
| CC/FmodNative/fmod.cs | a:Native | 3886 | ADVANCEDSETTINGS, ASYNCREADINFO, ATTRIBUTES_3D, CB_3D_ROLLOFF_CALLBACK, CHANNELCONTROL_CALLBACK, CHANNELCONTROL_CALLBACK_TYPE, CHANNELCONTROL_DSP_INDEX, CHANNELCONTROL_TYPE, CHANNELMASK, CHANNELORDER, CONSTANTS, CPU_U... | FMOD bindings |
| CC/FmodNative/fmod_dsp.cs | a:Native | 901 | COMPLEX, DSP_ALLOC_FUNC, DSP_BUFFER_ARRAY, DSP_CHANNELMIX, DSP_CHANNELMIX_OUTPUT, DSP_CHORUS, DSP_COMPRESSOR, DSP_CONVOLUTION_REVERB, DSP_CREATE_CALLBACK, DSP_DELAY, DSP_DESCRIPTION, DSP_DFT_FFTREAL_FUNC, DSP_DFT_IFFT... | FMOD bindings |
| CC/FmodNative/fmod_errors.cs | a:Native | 92 | Error | FMOD bindings |
| CC/FmodNative/fmod_studio.cs | a:Native | 2187 | ADVANCEDSETTINGS, BANK_INFO, BUFFER_INFO, BUFFER_USAGE, Bank, Bus, COMMANDCAPTURE_FLAGS, COMMANDREPLAY_CREATE_INSTANCE_CALLBACK, COMMANDREPLAY_FLAGS, COMMANDREPLAY_FRAME_CALLBACK, COMMANDREPLAY_LOAD_BANK_CALLBACK, COM... | FMOD bindings |
| CC/LocalPlayerState.cs | b | 156 | LocalPlayerState | client game |
| CC/NamedPlaces.cs | b | 32 | NamedPlaces | client game |
| CC/NearDrops.cs | a:Audio | 307 | DropBank, NearDrops |  |
| CC/Platform/BackgroundPriority.cs | a:Native | 54 | BackgroundPriority | thread priority (libc) |
| CC/Platform/NativeAudioLibraries.cs | a:Native | 62 | NativeAudioLibraries | finds libfmod/libphonon |
| CC/Platform/VoiceCodec.cs | b | 27 | VoiceCodec |  |
| CC/RainField.cs | a:Audio | 401 | RainField |  |
| CC/RainField.cs | a:Audio | 698 | RainSurvey | reads entities to find roofs and gutters: becomes a reader of the world input |
| CC/Services/SoundMappingService.cs | b | 146 | SoundMappingService |  |
| CC/SpatialService.cs | b | 769 | SpatialService | SPLIT: ray and occlusion queries go to Acoustics over the geometry input; entity lookups stay |
| CC/StrideAccumulator.cs | b | 328 | StepSlope, StrideAccumulator | client game |
| CC/WorldAudioPlayer.cs | b | 1577 | WorldAudioPlayer | SPLIT: one-off sounds and echoes go to Audio, WorldAudioEvent decoding stays |
| Common/AcousticConstants.cs | a:Acoustics | 180 | AcousticConstants |  |
| Common/AcousticMap.cs | a:Acoustics | 38 | AcousticMap, OpeningFrame |  |
| Common/AcousticRegistry.cs | a:Acoustics | 299 | AcousticRegistry, MaterialProperties | materials |
| Common/AdminGun.cs | a:Sound | 447 | AdminGun | models and presets |
| Common/AdminGun.cs | c:Sound | 12 | AdminGunMode |  |
| Common/Aircraft.cs | a:Sound | 391 | AircraftPower, AircraftProfile, BladeRowSpec, GasTurbineSpec, LandingGearSpec | models and presets |
| Common/Applause.cs | a:Sound | 496 | Applause | models and presets |
| Common/Applause.cs | c:Sound | 1 | CrowdApplause |  |
| Common/AudioClock.cs | a:Sound | 7 | AudioClock | becomes an instance the host passes in |
| Common/AudioEmission.cs | a:Acoustics | 79 | AudioEmission |  |
| Common/AudioEvents.cs | b | 31 | WorldAudioEvent | network message carrying a TransientSound |
| Common/AudioEvents.cs | c:Sound | 109 | SoundCharacter, TransientSound |  |
| Common/Ballistics.cs | a:Sound | 91 | Ballistics | models and presets |
| Common/Birds.cs | a:Sound | 90 | BirdHabitat, BirdSpecies | models and presets |
| Common/BoxColumns.cs | a:Geometry | 41 | BoxColumns |  |
| Common/Breathing.cs | a:Sound | 88 | Breathing | models and presets |
| Common/Breathing.cs | c:Sound | 1 | Breath |  |
| Common/BulletFlyby.cs | a:Sound | 529 | BulletFlyby, FlightSample | models and presets |
| Common/BulletImpact.cs | a:Sound | 1007 | BulletImpact | models and presets |
| Common/CarDoor.cs | a:Sound | 184 | CarDoor | models and presets |
| Common/CarWindow.cs | a:Sound | 631 | CarWindow | models and presets |
| Common/Chassis.cs | a:Sound | 276 | AxleSpec, BrakeKind, ChassisSpec, RoadSurfaces, TyreSize | models and presets |
| Common/Components.cs | b | 286 | AcousticComponent, ColliderComponent, EntityType, IdentityComponent, MaterialComponent, PortalComponent, RegionComponent, SoundEmitterComponent, Transform | entity model |
| Common/Components.cs | c:Geometry | 1 | ColliderShape |  |
| Common/Components.cs | c:Sound | 1 | WeatherType |  |
| Common/Components.cs | c:Sound | 1 | PlaybackMode | an enum the emitter component and the voices share |
| Common/DesignedSoundKit.cs | a:Sound | 147 | DesignedSoundKit | models and presets |
| Common/Diffraction.cs | a:Acoustics | 289 | Diffraction |  |
| Common/DoorKnock.cs | a:Sound | 47 | DoorKnock | models and presets |
| Common/DoorPhysics.cs | a:Sound | 556 | DoorPhysics | models and presets |
| Common/Doors.cs | a:Sound | 302 | DoorAcoustics, DoorEvents, DoorSound, DoorSoundKind | models and presets |
| Common/Doors.cs | c:Sound | 17 | DoorKind | stored in DoorComponent |
| Common/EarWind.cs | a:Sound | 200 | EarCover, EarWind, EarWindAtEars, EarWindListener | models and presets |
| Common/EarlyReflections.cs | a:Acoustics | 716 | EarlyReflections |  |
| Common/ElevatorDoor.cs | a:Sound | 509 | ElevatorDoor | models and presets |
| Common/Enclosure.cs | a:Acoustics | 579 | Enclosure |  |
| Common/Engines.cs | a:Sound | 2346 | CamLobe, CrossoverKind, EngineLayout, EngineProfile, ExhaustSpec, FuelType, Induction, IntakeSpec, MechanicalSpec, MufflerKind, MufflerSpec, ValveSpec | models and presets |
| Common/ExternalBallistics.cs | a:Sound | 275 | Air, BulletState, ExternalBallistics, FlightPoint | bullet flight; the server flies rounds with it too |
| Common/Footsteps.cs | a:Sound | 853 | Footstep, Footsteps, Shoe | models and presets |
| Common/Geometry/Bvh.cs | a:Geometry | 176 | BvhBuilder, BvhNode |  |
| Common/Geometry/GeometryPiece.cs | a:Geometry | 530 | AcceptAll, ExceptOwners, GeometryCrossing, GeometryPiece, GeometryTriangle, IGeometryFilter, PieceColumns, RayFaces, SolidRecord, SolidRef, SolidSpec, Ties |  |
| Common/Geometry/MoverPoses.cs | a:Geometry | 9 | MoverPoses |  |
| Common/Geometry/SolidContact.cs | a:Geometry | 242 | SolidContact |  |
| Common/Geometry/Surfaces.cs | a:Geometry | 149 | Construction, GeometryLayers, MeshAsset, ShapeLibrary, Surface, SurfaceFlags |  |
| Common/Geometry/TriangleGeometry.cs | a:Geometry | 4 | TriangleGeometry |  |
| Common/Geometry/TriangleWorld.cs | a:Geometry | 643 | GeometryHit, GeometryInstance, TriangleWorld |  |
| Common/Geometry/TriangleWorldBuilder.cs | a:Geometry | 229 | GeometryRole, TriangleWorldBuilder |  |
| Common/Geometry/TriangleWorldBuilder.cs | b | 56 | EntityGeometry | open-fps's entities to SolidSpec: the adapter half of TriangleWorldBuilder.cs |
| Common/GeometryUtils.cs | a:Geometry | 580 | GeometryUtils, MathHelper |  |
| Common/Glass.cs | a:Sound | 205 | GlassBreak, GlassEvent, GlassEventKind, GlassKind, GlassPane, GlassType, GlazedPart | models and presets |
| Common/GlassDoor.cs | a:Sound | 1005 | GlassDoor | models and presets |
| Common/GlassFracture.cs | a:Sound | 1497 | GlassFracture | models and presets |
| Common/HandOverSounds.cs | a:Sound | 151 | HandOverSounds | models and presets |
| Common/Hearing/BandAnalyser.cs | a:Sound | 288 | BandAnalyser, LiveBands | the ear |
| Common/Hearing/EarModel.cs | a:Sound | 59 | EarModel | the ear |
| Common/Hearing/EqualLoudness.cs | a:Sound | 73 | EqualLoudness | the ear |
| Common/Hearing/LoudnessCompensation.cs | a:Sound | 120 | LoudnessCompensation | the ear |
| Common/Hearing/Timbre.cs | a:Sound | 257 | Timbre | the ear |
| Common/Hearing/ZwickerLoudness.cs | a:Sound | 177 | ZwickerLoudness | the ear |
| Common/Honk.cs | a:Sound | 98 | Honk | models and presets |
| Common/ImageSource.cs | a:Acoustics | 567 | ImageSource, ReflectingSurface, Reflection |  |
| Common/ImpactAcoustics.cs | a:Sound | 152 | GlassSound, ImpactAcoustics | models and presets |
| Common/KnobDoor.cs | a:Sound | 1276 | KnobDoor | models and presets |
| Common/Lightning.cs | a:Sound | 431 | LightningChannel, LightningPhysics, LightningSchedule, StormSky | models and presets |
| Common/Lightning.cs | c:Sound | 8 | FlashKind |  |
| Common/Lightning.cs | c:Sound | 39 | LightningStrike | the flash the server sends |
| Common/Localisation.cs | a:Acoustics | 43 | Localisation |  |
| Common/LockCylinder.cs | a:Sound | 580 | LockCylinder | models and presets |
| Common/Loudness.cs | a:Sound | 412 | Loudness | the loudness law |
| Common/Machines.cs | a:Sound | 620 | MachineDefinition, MachineModels, MachinePart, MachineRegistry | models and presets |
| Common/Messages.cs | b | 100 | EntityDefinition, EntityState | network |
| Common/Messages.cs | c:Sound | 40 | WheelState | a sound value in the network namespace |
| Common/ModelLibrary.cs | a:Sound | 285 | ModelLibrary | models and presets |
| Common/NatureModels.cs | a:Sound | 363 | FireSpec, FoliageSpec, LeafKind, WaterFallSpec, WaterFeatureSpec, WaterSurface, WaterTapSpec | models and presets |
| Common/OpeningRoutes.cs | a:Acoustics | 2004 | OpeningRoutes |  |
| Common/PanelAcoustics.cs | a:Acoustics | 154 | PanelAcoustics |  |
| Common/PerfProbe.cs | a:Geometry | 137 | PerfProbe | diagnostics timer; lowest project so every layer can use it |
| Common/PhysicsConstants.cs | b | 114 | PhysicsConstants | player movement |
| Common/PhysicsUtils.cs | b | 293 | PhysicsUtils | player movement |
| Common/Pneumatics.cs | a:Sound | 179 | AirPortSpec, AirSystemSpec | models and presets |
| Common/Precipitation.cs | a:Sound | 326 | Hydrometeors, ParticleSpectrum | models and presets |
| Common/Precipitation.cs | c:Sound | 13 | PrecipitationKind |  |
| Common/Precipitation.cs | c:Sound | 10 | Precipitation | weather value in the snapshot |
| Common/PushBarDoor.cs | a:Sound | 899 | PushBarDoor | models and presets |
| Common/Rain.cs | a:Sound | 274 | DropSizeTable, RainCategory, Rainfall | models and presets |
| Common/RainSurfaces.cs | a:Sound | 451 | RainPlate, RainSurfaceKind, RainSurfaces | models and presets |
| Common/Ricochet.cs | a:Sound | 544 | Ricochet, Slug | models and presets |
| Common/RoadWater.cs | a:Sound | 229 | RoadDrainageSpec, RoadTexture, RoadWaterLaw | models and presets |
| Common/RoadWater.cs | c:Sound | 157 | PuddleField |  |
| Common/RoadWater.cs | c:Sound | 134 | RoadWater | server-advanced store sent whole; the sound reads depths |
| Common/Roads.cs | b | 21 | RoadData | map data |
| Common/RoomAcoustics.cs | a:Acoustics | 106 | RoomAcoustics |  |
| Common/RunningGear.cs | a:Sound | 448 | RunningGear | models and presets |
| Common/RunningWater.cs | a:Sound | 620 | FallFeed, FlowBasin, FlowCavity, FlowChannel, FlowFall, FlowInlet, FlowLayout, FlowObstacles, FlowTap, Hydraulics, RunningWaterSpec | models and presets |
| Common/Runoff.cs | a:Sound | 76 | Runoff | models and presets |
| Common/ShapedNoise.cs | a:Sound | 89 | ShapedNoise |  |
| Common/Signals.cs | a:Sound | 850 | ChimeBellSpec, ChimeHornSpec, DoorChimeSpec, ElectricHornKind, ElectricHornSpec, ElectricHornUnitSpec, SirenController, SirenMode, SirenSpec, StruckBellSpec, WhistleBellSpec, WhistleSpec | models and presets |
| Common/SlidingDoor.cs | a:Sound | 1416 | SlidingDoor | models and presets |
| Common/SmallMachines.cs | a:Sound | 322 | CasingSpec, CompressorSpec, CuttingSpec, GovernorSpec, MowerDeckSpec, SmallMachineSpec | models and presets |
| Common/SparseAcousticOctree.cs | a:Acoustics | 131 | BoxContainment, SparseAcousticOctree |  |
| Common/SpatialGrid.cs | a:Geometry | 295 | SpatialGrid |  |
| Common/Spectrum.cs | a:Sound | 195 | Spectrum |  |
| Common/Speech/Speech.cs | b | 269 | Speech | open-fps's recorded lines; Loudness reads its levels |
| Common/Systems/AcousticVolumeGenerator.cs | a:Acoustics | 385 | AcousticVolumeGenerator | regions and openings from the solids |
| Common/Systems/FaceOpenings.cs | a:Acoustics | 392 | FaceOpenings | regions and openings from the solids |
| Common/TeleporterSounds.cs | a:Sound | 175 | TeleporterSounds | models and presets |
| Common/Tiles.cs | c:Geometry | 16 | TileKey | a tile of the world; the tile scenes key on it |
| Common/Trains.cs | a:Sound | 613 | ConsistEntry, ElectricDriveSpec, RailTraction, RailTractionSpec, RailVehicleSpec, SleeperKind, SteamLocoSpec, TrackSpec, TrainLayout, TrainProfile, WheelsetSpec | models and presets |
| Common/TyreFriction.cs | a:Sound | 75 | TyreFriction | models and presets |
| Common/UpdateThrottle.cs | b | 41 | UpdateThrottle | client loop |
| Common/VehicleBody.cs | a:Sound | 627 | BodyMode, BodyResonator, VehicleBody | models and presets |
| Common/VehicleCabin.cs | a:Sound | 98 | VehicleCabin | models and presets |
| Common/Vehicles.cs | a:Sound | 1604 | ElectricFanSpec, EngineBaySpec, FanClutchSpec, Gearbox, TyreProfile, VehicleProfile | models and presets |
| Common/WallTransmission.cs | a:Acoustics | 185 | AcousticBands, WallBuild, WallTransmission |  |
| Common/Waves.cs | a:Sound | 594 | HullSpec, ShoreFace, ShoreGeometry, ShoreSediment, ShoreSpec, WaterBody, WindWaves | models and presets |
| Common/WeaponHandling.cs | a:Sound | 537 | HandlingSpec, WeaponHandling | models and presets |
| Common/Weapons.cs | c:Sound | 209 | WeaponRegistry | content registry both read |
| Common/Weapons.cs | c:Sound | 211 | FireMode, WeaponAction, WeaponDefinition, WeaponFeed | weapon numbers the sound reads |
| Common/WheelDynamics.cs | a:Sound | 822 | MagicCurve, WheelDynamics | models and presets |
| Common/Wind.cs | a:Sound | 274 | WindField, WindWeather | models and presets |
| Common/Wind.cs | c:Sound | 64 | WindAir | the wind the server sends |
| Common/WindModel.cs | a:Sound | 51 | WindModel | models and presets |
| Common/WoodChorus.cs | a:Sound | 161 | WoodChorus | models and presets |
| Common/WorldSnapshot.cs | b | 99 | EntitySnapshot, WorldSnapshot | snapshots |

### B. Every library-to-host reference

| Library file | Host type | Refs | Lines |
|---|---|---:|---|
| CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs | WorldSnapshot | 36 | 35, 148, 254, 280, 376, 520, 696, 785, 795, 824, 847, 908, 972, 1013, 1023, 1137, 1139, 1149, 1152, 1204, 1295, 1301, 1308, 1314, 1324, 1348, 1373, 1374, 1385, 1390 |
| CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs | EntitySnapshot | 10 | 1124, 1126, 1139, 1141, 1142, 1143, 1152, 1153 |
| CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs | Components.Transform | 3 | 1126, 1142 |
| CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs | Components.RegionComponent | 1 | 796 |
| CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs | Networking.EntityDefinition | 1 | 1121 |
| CC/AudioEngine/Acoustics/CabinWalls.cs | EntitySnapshot | 12 | 31, 33, 34, 36, 41, 54, 56, 70, 73, 81, 83 |
| CC/AudioEngine/Acoustics/CabinWalls.cs | Networking.EntityDefinition | 2 | 33, 56 |
| CC/AudioEngine/Acoustics/CabinWalls.cs | Components.SoundEmitterComponent | 2 | 33, 56 |
| CC/AudioEngine/Acoustics/CabinWalls.cs | Components.Transform | 2 | 73 |
| CC/AudioEngine/Acoustics/CabinWalls.cs | WorldSnapshot | 2 | 81, 83 |
| CC/AudioEngine/Acoustics/EngineReflections.cs | WorldSnapshot | 3 | 153, 157, 158 |
| CC/AudioEngine/Acoustics/OpeningGraph.cs | Networking.EntityDefinition | 15 | 21, 22, 23, 63, 67, 68, 70, 71 |
| CC/AudioEngine/Acoustics/OpeningGraph.cs | Components.PortalComponent | 12 | 23, 67, 71, 85, 89, 90 |
| CC/AudioEngine/Acoustics/OpeningGraph.cs | WorldSnapshot | 9 | 26, 29, 40, 42, 46, 55, 58, 62, 74 |
| CC/AudioEngine/Acoustics/OpeningGraph.cs | EntitySnapshot | 7 | 29, 30, 62, 63, 69 |
| CC/AudioEngine/Acoustics/OpeningGraph.cs | Components.Transform | 5 | 68, 69 |
| CC/AudioEngine/Acoustics/OpeningGraph.cs | Components.ColliderComponent | 2 | 22, 70 |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | WorldSnapshot | 30 | 33, 36, 92, 95, 102, 103, 133, 137, 142, 146, 148, 152, 158, 169, 174, 177, 189, 219, 220, 226, 251, 302, 307, 309, 311 |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | EntitySnapshot | 15 | 36, 37, 177, 179, 180, 182, 311, 312 |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | SpatialService | 8 | 19, 21, 23, 25, 195, 253, 256, 307 |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | Components.RegionComponent | 5 | 226, 229, 231 |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | Components.EntityType | 4 | 36, 311 |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | Components.Transform | 4 | 36, 180, 311 |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | Networking.EntityDefinition | 2 | 36, 311 |
| CC/AudioEngine/Acoustics/SpatialAcoustics.cs | NamedPlaces | 2 | 303 |
| CC/AudioEngine/Acoustics/VehicleShadow.cs | EntitySnapshot | 6 | 50, 52, 53, 57 |
| CC/AudioEngine/Acoustics/VehicleShadow.cs | Networking.EntityDefinition | 3 | 53, 54, 55 |
| CC/AudioEngine/Acoustics/VehicleShadow.cs | WorldSnapshot | 2 | 46, 50 |
| CC/AudioEngine/Acoustics/VehicleShadow.cs | Components.ColliderComponent | 2 | 54, 55 |
| CC/AudioEngine/Acoustics/VehicleShadow.cs | Components.Transform | 2 | 57 |
| CC/AudioEngine/Fmod/EngineProcessor.cs | RoadData | 2 | 1388 |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs | Components.PortalComponent | 12 | 1559, 1561, 1562, 4562, 4563, 4564, 4568, 4571 |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs | Components.RegionComponent | 7 | 2287, 2293, 2302, 2306, 2320, 4595 |
| CC/AudioEngine/Fmod/TalkerVoice.cs | Platform.VoiceCodec | 16 | 50, 55, 56, 101, 169, 170, 190 |
| CC/AudioEngine/SteamAudio/EarlyCopies.cs | WorldAudioPlayer | 17 | 75, 76, 80, 81, 83, 91, 94, 98, 107, 110 |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | Networking.EntityDefinition | 9 | 118, 119, 124, 127, 128, 130, 131 |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | EntitySnapshot | 5 | 116, 118, 130, 131 |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | Components.ColliderComponent | 3 | 119, 128 |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | WorldSnapshot | 2 | 112, 116 |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | Components.Transform | 2 | 130 |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | Components.AcousticComponent | 2 | 131 |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | Components.SoundEmitterComponent | 1 | 124 |
| CC/AudioEngine/SteamAudio/SteamAudioScene.cs | Components.MaterialComponent | 1 | 130 |
| CC/RainField.cs | EntitySnapshot | 35 | 146, 636, 640, 641, 642, 651, 652, 653, 656, 657, 659, 661, 662, 664, 669, 671, 682, 683, 685, 698, 700, 709, 719, 720, 726, 728, 732, 734, 737, 796 |
| CC/RainField.cs | WorldSnapshot | 21 | 171, 628, 631, 634, 636, 640, 646, 648, 649, 651, 659, 793, 795, 796, 798, 799 |
| CC/RainField.cs | Networking.EntityDefinition | 19 | 641, 652, 653, 662, 671, 672, 673, 675, 677, 694, 700, 701, 715, 728 |
| CC/RainField.cs | Components.Transform | 10 | 642, 657, 664, 683, 685, 709, 719, 720, 732, 734 |
| CC/RainField.cs | Components.ColliderComponent | 7 | 641, 652, 672, 673, 715, 728 |
| CC/RainField.cs | Components.EntityType | 4 | 641, 652 |
| CC/RainField.cs | Components.AcousticComponent | 4 | 677, 694 |
| CC/RainField.cs | Components.MaterialComponent | 2 | 653, 675 |
| CC/RainField.cs | Components.SoundEmitterComponent | 2 | 662, 701 |
| Common/AcousticMap.cs | Components.RegionComponent | 1 | 19 |
| Common/AcousticMap.cs | Components.PortalComponent | 1 | 22 |
| Common/AudioEmission.cs | EntitySnapshot | 7 | 72, 74, 75, 76, 91, 93 |
| Common/AudioEmission.cs | Networking.EntityDefinition | 4 | 40, 43, 46, 47 |
| Common/AudioEmission.cs | Components.Transform | 4 | 75, 76, 93 |
| Common/AudioEmission.cs | Components.SoundEmitterComponent | 3 | 43, 46, 47 |
| Common/Breathing.cs | PhysicsConstants | 2 | 72 |
| Common/EarWind.cs | PhysicsConstants | 2 | 136 |
| Common/ExternalBallistics.cs | PhysicsConstants | 2 | 303 |
| Common/Glass.cs | PhysicsConstants | 6 | 145, 154, 187 |
| Common/Hearing/EarModel.cs | Speech | 2 | 42 |
| Common/Loudness.cs | Speech | 8 | 234, 246, 249 |
| Common/Messages.cs | Networking.EntityState | 2 | 480 |
| Common/OpeningRoutes.cs | Components.RegionComponent | 15 | 325, 326, 339, 450, 451, 456, 457, 458, 686, 691 |
| Common/RoadWater.cs | RoadData | 12 | 437, 444, 446, 451, 455, 467, 468, 497, 504, 516, 525, 550 |
| Common/RoomAcoustics.cs | Components.RegionComponent | 15 | 73, 76, 87, 90, 95, 108, 109, 121, 124, 127, 129, 131, 138 |
| Common/Systems/AcousticVolumeGenerator.cs | Networking.EntityDefinition | 50 | 27, 40, 44, 45, 70, 71, 73, 75, 76, 77, 78, 85, 87, 91, 111, 113, 115, 116, 123, 131, 140, 141, 146, 149, 255, 261, 263, 264, 265, 266, 268, 269, 270, 271 |
| Common/Systems/AcousticVolumeGenerator.cs | Components.RegionComponent | 28 | 40, 49, 50, 51, 52, 53, 59, 70, 71, 73, 85, 87, 91, 188, 214, 216, 264, 279, 283, 284, 317, 340, 386, 391 |
| Common/Systems/AcousticVolumeGenerator.cs | Components.PortalComponent | 26 | 113, 115, 116, 146, 147, 148, 179, 231, 268, 271, 289, 300, 301, 350 |
| Common/Systems/AcousticVolumeGenerator.cs | Components.Transform | 12 | 45, 77, 78, 91, 123, 131, 141, 149, 269, 270 |
| Common/Systems/AcousticVolumeGenerator.cs | Components.ColliderComponent | 3 | 263, 266 |
| Common/Systems/AcousticVolumeGenerator.cs | Components.SoundEmitterComponent | 1 | 265 |
| Common/WheelDynamics.cs | RoadData | 2 | 290 |

### C. Every static

#### T7a. Written from outside their own type

Reads and writes are counted per reference: lib = library code, host = host projects, test = Tests and AudioLab. "*" marks a writer in Tests or AudioLab.

| Static | Kind | Declared | Reads lib/host/test | Writes lib/host/test | Writers outside its type |
|---|---|---|---|---|---|
| Loudness.DynamicRangeCompression | settable | Common/Loudness.cs:142 | 6/2/27 | 0/3/22 | AudioLab.Spikes.GlassSpike*, AudioLab.Spikes.ThunderSpike*, AudioLab.Spikes.WeatherWindSpike*, ClientRunner, GtkClientProgram, HeardLevel... |
| WindField.Weather | settable | Common/Wind.cs:42 | 12/1/0 | 3/1/20 | AudioLab.Spikes.CabinSpike*, AudioLab.Spikes.DistantWoodsSpike*, AudioLab.Spikes.GameLevelsSpike*, AudioLab.Spikes.RunningWaterSpike*, Au... |
| Hearing.EarModel.ListeningLevelDb | settable | Common/Hearing/EarModel.cs:35 | 2/9/10 | 0/6/8 | AudioLab.Spikes.GameLevelsSpike*, ClientSettings, ListeningCalibration, Tests.EarModelTests*, Tests.ListeningCalibrationTests* |
| Core.Engine.EngineSynth.ValveJetNoise | assignable | CC/AudioEngine/Core/Engine/EngineSynth.cs:441 | 1/1/1 | 0/2/6 | Fmod.TapBalanceSpike*, Session.ClientGameSession, Tests.ValveFlowSwitch* |
| Hearing.EarModel.Enabled | settable | Common/Hearing/EarModel.cs:21 | 8/1/8 | 0/2/6 | AudioLab.Spikes.GameLevelsSpike*, ListeningCalibration, Tests.EarModelTests* |
| SteamAudio.SmoothTail.FromFiftyMs | assignable | CC/AudioEngine/SteamAudio/SmoothTail.cs:94 | 2/0/2 | 0/0/4 | Fmod.ClapRoomSpike*, Fmod.EarlyTailSpike*, Fmod.TailSteadySpike* |
| Core.Engine.CabinPaths.Enabled | assignable | CC/AudioEngine/Core/Engine/CabinPaths.cs:38 | 2/0/2 | 0/0/4 | AudioLab.Spikes.CabinSpike*, Tests.CabinPathsTests* |
| Runoff.Held | assignable | Common/Runoff.cs:38 | 1/0/0 | 0/0/5 | AudioLab.Spikes.RunningWaterSpike* |
| Core.DoorRenderCache.Folder | assignable | CC/AudioEngine/Core/DoorRenderCache.cs:43 | 2/0/1 | 0/0/3 | AudioLab.Spikes.PrerenderDoorsSpike*, Tests.DoorPrewarmTests* |
| OpeningRoutes.ReverseGridOrderForParity | assignable | Common/OpeningRoutes.cs:416 | 2/0/0 | 0/0/4 | SteamAudio.GeometryParitySpike* |
| WindField.MeanSpeed | settable | Common/Wind.cs:71 | 0/1/5 | 0/0/3 | AudioLab.Spikes.NatureSpike*, AudioLab.Spikes.TextureSpike* |
| Fmod.FmodAudioProvider.CabinDb | assignable | CC/AudioEngine/Fmod/FmodAudioProvider.cs:1725 | 1/1/0 | 0/1/1 | AudioLab.Spikes.CabinSpike*, Session.ClientGameSession |
| Fmod.FmodAudioProvider.TracedEchoesOn | assignable | CC/AudioEngine/Fmod/FmodAudioProvider.cs:1652 | 2/0/0 | 0/3/0 | Session.ClientGameSession |
| KnobDoor.StemFolder | assignable | Common/KnobDoor.cs:113 | 3/0/1 | 0/0/2 | <top-level:AudioLab/Program.cs>*, AudioLab.Spikes.KnobRefSpike* |
| PerfProbe.Enabled | assignable | Common/PerfProbe.cs:26 | 6/0/0 | 0/0/3 | Tests.HotPathTests* |
| SlidingDoor.StemFolder | assignable | Common/SlidingDoor.cs:59 | 3/0/4 | 0/0/2 | <top-level:AudioLab/Program.cs>*, AudioLab.Spikes.PatioRefSpike* |
| Core.Engine.EngineSynth.DebugSoloTailpipe | assignable | CC/AudioEngine/Core/Engine/EngineSynth.cs:1743 | 1/0/2 | 0/0/2 | Fmod.EngineOrderSpike* |
| Fmod.FmodAudioProvider.TailDb | assignable | CC/AudioEngine/Fmod/FmodAudioProvider.cs:1712 | 4/1/2 | 0/2/0 | Session.ClientGameSession |
| EarlyReflections.FlutterTrace | assignable | Common/EarlyReflections.cs:663 | 3/0/0 | 0/0/2 | Tests.FlutterTests* |
| Geometry.TriangleGeometry.Enabled | assignable | Common/Geometry/TriangleGeometry.cs:13 | 3/8/0 | 0/0/2 | SteamAudio.GeometryParitySpike* |
| KnobDoor.PinTrace | assignable | Common/KnobDoor.cs:115 | 1/0/2 | 1/0/2 | <top-level:AudioLab/Program.cs>* |
| Acoustics.AsyncAcousticWorker.TraceProvenance | assignable | CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs:163 | 3/0/0 | 0/0/1 | SteamAudio.PopHuntSpike* |
| Core.DoorRenderCache.ShippedFolder | assignable | CC/AudioEngine/Core/DoorRenderCache.cs:46 | 1/0/0 | 0/0/1 | AudioLab.Spikes.PrerenderDoorsSpike* |
| Core.Engine.CabinPaths.LabOnePlace | assignable | CC/AudioEngine/Core/Engine/CabinPaths.cs:99 | 1/0/0 | 0/0/1 | AudioLab.Spikes.CabinSpike* |
| Core.Engine.EngineSynth.DebugLegacyDiesel | assignable | CC/AudioEngine/Core/Engine/EngineSynth.cs:1734 | 2/0/0 | 0/0/1 | Fmod.VehicleSpike* |
| Core.Engine.EngineSynth.DebugRigidValves | assignable | CC/AudioEngine/Core/Engine/EngineSynth.cs:1738 | 1/0/0 | 0/0/1 | Fmod.EngineOrderSpike* |
| Core.Nature.ExtendedSources.Enabled | assignable | CC/AudioEngine/Core/Nature/ExtendedSources.cs:37 | 2/0/2 | 0/0/1 | AudioLab.Spikes.WideSourcesSpike* |
| Core.Nature.ExtendedSources.ForceSpread | assignable | CC/AudioEngine/Core/Nature/ExtendedSources.cs:67 | 2/0/2 | 0/0/1 | AudioLab.Spikes.WideSourcesSpike* |
| Core.Nature.ExtendedSources.LayoutScale | assignable | CC/AudioEngine/Core/Nature/ExtendedSources.cs:64 | 6/0/0 | 0/0/1 | AudioLab.Spikes.WideSourcesSpike* |
| Fmod.EngineVoiceState.LabAlignProbe | assignable | CC/AudioEngine/Fmod/EngineProcessor.cs:880 | 2/0/0 | 0/0/1 | AudioLab.Spikes.CabinSpike* |
| Fmod.FmodAudioProvider.CopiesDb | assignable | CC/AudioEngine/Fmod/FmodAudioProvider.cs:1715 | 2/1/2 | 0/1/0 | Session.ClientGameSession |
| SteamAudio.DiffuseTail.LateEarVelvet | assignable | CC/AudioEngine/SteamAudio/TracedReverbDsp.cs:330 | 1/0/0 | 0/0/1 | Fmod.TailSteadySpike* |
| SteamAudio.TracedReverb.LabProbeSeconds | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:125 | 2/0/0 | 0/0/1 | Fmod.ClapRoomSpike* |
| SteamAudio.TracedReverb.OneChannelLate | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:119 | 1/0/0 | 0/0/1 | Fmod.ClapRoomSpike* |
| SteamAudio.TracedReverb.RawTail | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:116 | 3/0/0 | 0/0/1 | Fmod.ClapRoomSpike* |
| SteamAudio.TracedReverb.SimulatedType | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:46 | 1/0/0 | 0/0/1 | SteamAudio.TracedReverbSpike* |
| SteamAudio.TracedReverbDsp.LabNoPreDelay | assignable | CC/AudioEngine/SteamAudio/TracedReverbDsp.cs:619 | 1/0/0 | 0/0/1 | Fmod.ClapRoomSpike* |
| RainField.RoofRidesWithHead | assignable | CC/RainField.cs:981 | 1/0/0 | 0/0/1 | AudioLab.Spikes.CabinSpike* |
| ElevatorDoor.StemFolder | assignable | Common/ElevatorDoor.cs:52 | 3/0/0 | 0/0/1 | AudioLab.Spikes.DoorModelsSpike* |
| GlassDoor.StemFolder | assignable | Common/GlassDoor.cs:90 | 3/0/3 | 0/0/1 | AudioLab.Spikes.DoorModelsSpike* |
| KnobDoor.KeeperBendsStrike | assignable | Common/KnobDoor.cs:109 | 1/0/0 | 0/0/1 | <top-level:AudioLab/Program.cs>* |
| LockCylinder.StemFolder | assignable | Common/LockCylinder.cs:55 | 3/0/0 | 0/0/1 | AudioLab.Spikes.DoorModelsSpike* |
| OpeningRoutes.DebugOverTheTop | assignable | Common/OpeningRoutes.cs:419 | 2/0/0 | 0/0/1 | SteamAudio.GeometryParitySpike* |
| PushBarDoor.StemFolder | assignable | Common/PushBarDoor.cs:49 | 3/0/0 | 0/0/1 | <top-level:AudioLab/Program.cs>* |
| WindField.Turbulence | settable | Common/Wind.cs:91 | 0/0/1 | 0/0/1 | AudioLab.Spikes.NatureSpike* |
| Core.AudioPhysics.CurrentAirCelsius | settable | CC/AudioEngine/Core/AudioPhysics.cs:28 | 1/0/0 | 1/0/0 | Fmod.FmodAudioProvider |
| Core.AudioPhysics.CurrentSpeedOfSound | settable | CC/AudioEngine/Core/AudioPhysics.cs:18 | 4/14/4 | 1/0/0 | Fmod.FmodAudioProvider |
| Fmod.MixerQuality.MixerRate | settable | CC/AudioEngine/Fmod/MixerQuality.cs:62 | 36/4/10 | 1/0/0 | Fmod.FmodAudioProvider |

#### T7b. Reassigned by their own type

Reads and writes are counted per reference: lib = library code, host = host projects, test = Tests and AudioLab. "*" marks a writer in Tests or AudioLab.

| Static | Kind | Declared | Reads lib/host/test | Writes lib/host/test | Writers outside its type |
|---|---|---|---|---|---|
| FMOD.StringHelper.encoders | assignable | CC/FmodNative/fmod.cs:3993 | 5/0/0 | 1/0/0 |  |
| Core.AudioPhysics._current | assignable | CC/AudioEngine/Core/AudioPhysics.cs:23 | 0/0/0 | 2/0/0 |  |
| Core.AudioPhysics._currentCelsius | assignable | CC/AudioEngine/Core/AudioPhysics.cs:33 | 0/0/0 | 2/0/0 |  |
| Core.DoorRenderCache._pruned | assignable | CC/AudioEngine/Core/DoorRenderCache.cs:49 | 0/0/0 | 1/0/0 |  |
| Fmod.DspFault._count | assignable | CC/AudioEngine/Fmod/DspFault.cs:133 | 0/0/0 | 2/0/0 |  |
| Fmod.DspFault._first | assignable | CC/AudioEngine/Fmod/DspFault.cs:134 | 0/0/0 | 2/0/0 |  |
| Fmod.EarProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/EarStage.cs:134 | 0/0/0 | 1/0/0 |  |
| Fmod.EarWindProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/EarWindVoice.cs:68 | 0/0/0 | 1/0/0 |  |
| Fmod.EchoProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/EngineProcessor.cs:2354 | 0/0/0 | 1/0/0 |  |
| Fmod.EngineProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/EngineProcessor.cs:2443 | 0/0/0 | 1/0/0 |  |
| Fmod.EngineTapState.AlignedBlocks | assignable | CC/AudioEngine/Fmod/EngineProcessor.cs:2150 | 0/0/1 | 1/0/0 |  |
| Fmod.EngineTapState.UnalignedBlocks | assignable | CC/AudioEngine/Fmod/EngineProcessor.cs:2150 | 0/0/1 | 1/0/0 |  |
| Fmod.EngineVoiceState.GlobalStarves | assignable | CC/AudioEngine/Fmod/EngineProcessor.cs:278 | 3/1/0 | 1/0/0 |  |
| Fmod.FmodAudioProvider._lateLawKNow | assignable | CC/AudioEngine/Fmod/FmodAudioProvider.cs:1888 | 1/0/0 | 1/0/0 |  |
| Fmod.FmodAudioProvider._sendDropsOnWrongBus | assignable | CC/AudioEngine/Fmod/FmodAudioProvider.cs:2517 | 2/0/0 | 1/0/0 |  |
| Fmod.FmodAudioProvider._tailLeanNow | assignable | CC/AudioEngine/Fmod/FmodAudioProvider.cs:1888 | 1/0/0 | 1/0/0 |  |
| Fmod.FmodAudioProvider._tracedEchoIds | assignable | CC/AudioEngine/Fmod/FmodAudioProvider.cs:1655 | 3/0/0 | 2/0/0 |  |
| Fmod.GranularProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/GranularProcessor.cs:65 | 0/0/0 | 1/0/0 |  |
| Fmod.MachineProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/MachineProcessor.cs:600 | 0/0/0 | 1/0/0 |  |
| Fmod.MasterDither._callback | assignable | CC/AudioEngine/Fmod/MasterDither.cs:29 | 1/0/0 | 1/0/0 |  |
| Fmod.MasterLimiter._callback | assignable | CC/AudioEngine/Fmod/MasterLimiter.cs:285 | 1/0/0 | 1/0/0 |  |
| Fmod.MasterTap._callback | assignable | CC/AudioEngine/Fmod/MasterTap.cs:35 | 1/0/0 | 1/0/0 |  |
| Fmod.MixerQuality._mixerRate | assignable | CC/AudioEngine/Fmod/MixerQuality.cs:63 | 1/0/0 | 1/0/0 |  |
| Fmod.NonFinite.Blocks | assignable | CC/AudioEngine/Fmod/NonFinite.cs:49 | 0/0/2 | 1/0/0 |  |
| Fmod.NonFinite._head | assignable | CC/AudioEngine/Fmod/NonFinite.cs:47 | 0/0/0 | 2/0/0 |  |
| Fmod.NonFinite._read | assignable | CC/AudioEngine/Fmod/NonFinite.cs:47 | 3/0/0 | 2/0/0 |  |
| Fmod.OwnVoiceProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/OwnVoice.cs:218 | 0/0/0 | 1/0/0 |  |
| Fmod.PhysicalVoiceState.GlobalStarves | assignable | CC/AudioEngine/Fmod/MachineProcessor.cs:238 | 1/1/0 | 1/0/0 |  |
| Fmod.SynthProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/SynthProcessor.cs:56 | 0/0/0 | 1/0/0 |  |
| Fmod.TapProcessor._nonFiniteOther | assignable | CC/AudioEngine/Fmod/EngineProcessor.cs:2266 | 0/0/0 | 1/0/0 |  |
| Fmod.FmodDebugLog._held | assignable | CC/AudioEngine/Fmod/FmodDebugLog.cs:28 | 1/0/0 | 1/0/0 |  |
| Fmod.FmodDebugLog._out | assignable | CC/AudioEngine/Fmod/FmodDebugLog.cs:29 | 2/0/0 | 1/0/0 |  |
| SteamAudio.AmbisonicBedDsp._nonFiniteOther | assignable | CC/AudioEngine/SteamAudio/AmbisonicBedDsp.cs:79 | 0/0/0 | 1/0/0 |  |
| SteamAudio.DiffuseLateNoise._shared | assignable | CC/AudioEngine/SteamAudio/DiffuseLate.cs:106 | 1/0/0 | 1/0/0 |  |
| SteamAudio.EarlyCopies._bandShare | assignable | CC/AudioEngine/SteamAudio/EarlyCopies.cs:133 | 0/0/0 | 2/0/0 |  |
| SteamAudio.Phonon._simdLevel | assignable | CC/AudioEngine/SteamAudio/Phonon.cs:92 | 2/0/0 | 1/0/0 |  |
| SteamAudio.TracedEchoDsp._nonFiniteCapture | assignable | CC/AudioEngine/SteamAudio/TracedEchoes.cs:352 | 0/0/0 | 1/0/0 |  |
| SteamAudio.TracedEchoDsp._nonFiniteMix | assignable | CC/AudioEngine/SteamAudio/TracedEchoes.cs:352 | 0/0/0 | 1/0/0 |  |
| SteamAudio.TracedEchoes.Current | assignable | CC/AudioEngine/SteamAudio/TracedEchoes.cs:45 | 2/0/0 | 2/0/0 |  |
| SteamAudio.TracedReverb.Current | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:30 | 1/0/0 | 2/0/0 |  |
| SteamAudio.TracedReverbSet._cabin | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:629 | 9/0/0 | 4/0/0 |  |
| SteamAudio.TracedReverbSet._cabinPreset | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:627 | 1/0/0 | 2/0/0 |  |
| SteamAudio.TracedReverbSet._cabinScene | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:628 | 6/0/0 | 3/0/0 |  |
| SteamAudio.TracedReverbSet._context | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:531 | 5/0/0 | 2/0/0 |  |
| SteamAudio.TracedReverbSet._echoes | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:534 | 2/0/0 | 2/0/0 |  |
| SteamAudio.TracedReverbSet._late | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:535 | 2/0/0 | 2/0/0 |  |
| SteamAudio.TracedReverbSet._listener | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:533 | 5/0/0 | 2/0/0 |  |
| SteamAudio.TracedReverbSet._reconfigure | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:580 | 2/0/0 | 1/0/0 |  |
| SteamAudio.TracedReverbSet._riding | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:630 | 1/0/0 | 3/0/0 |  |
| SteamAudio.TracedReverbSet._scene | assignable | CC/AudioEngine/SteamAudio/TracedReverb.cs:532 | 2/0/0 | 2/0/0 |  |
| AcousticRegistry._registry | assignable | Common/AcousticRegistry.cs:61 | 9/0/0 | 1/0/0 |  |
| CarWindow._down | assignable | Common/CarWindow.cs:126 | 0/0/0 | 1/0/0 |  |
| CarWindow._up | assignable | Common/CarWindow.cs:126 | 0/0/0 | 1/0/0 |  |
| Geometry.MoverPoses._version | assignable | Common/Geometry/MoverPoses.cs:13 | 0/0/0 | 2/0/0 |  |
| Geometry.TriangleWorld._versions | assignable | Common/Geometry/TriangleWorld.cs:90 | 0/0/0 | 2/0/0 |  |
| Hearing.EarModel._enabled | assignable | Common/Hearing/EarModel.cs:26 | 0/0/0 | 2/0/0 |  |
| Hearing.EarModel._listening | assignable | Common/Hearing/EarModel.cs:40 | 0/0/0 | 2/0/0 |  |
| Loudness._compression | assignable | Common/Loudness.cs:151 | 0/0/0 | 2/0/0 |  |
| MachineRegistry._authored | assignable | Common/Machines.cs:175 | 7/0/0 | 3/0/0 |  |
| MachineRegistry._loadedFrom | assignable | Common/Machines.cs:234 | 2/0/0 | 2/0/0 |  |
| ModelLibrary._authored | assignable | Common/ModelLibrary.cs:81 | 5/0/0 | 3/0/0 |  |
| ModelLibrary._loadedFrom | assignable | Common/ModelLibrary.cs:83 | 2/0/0 | 2/0/0 |  |
| PerfProbe._lastReportStamp | assignable | Common/PerfProbe.cs:37 | 0/0/0 | 3/0/0 |  |
| Runoff._last | assignable | Common/Runoff.cs:34 | 2/0/0 | 3/0/0 |  |
| Runoff._rain | assignable | Common/Runoff.cs:33 | 0/0/0 | 3/0/0 |  |
| WindField._weather | assignable | Common/Wind.cs:47 | 0/0/0 | 2/0/0 |  |

#### T7c. Caches their own type fills

Reads and writes are counted per reference: lib = library code, host = host projects, test = Tests and AudioLab. "*" marks a writer in Tests or AudioLab.

| Static | Kind | Declared | Reads lib/host/test | Writes lib/host/test | Writers outside its type |
|---|---|---|---|---|---|
| Core.EarTimbres._byFolder | contents | CC/AudioEngine/Core/EarTimbres.cs:25 | 1/0/0 | 2/0/0 |  |
| Core.EarTimbres._bySound | contents | CC/AudioEngine/Core/EarTimbres.cs:24 | 2/0/0 | 2/0/0 |  |
| Core.EarTimbres._pending | contents | CC/AudioEngine/Core/EarTimbres.cs:26 | 1/0/0 | 2/0/0 |  |
| Core.Engine.CabinPaths._layouts | contents | CC/AudioEngine/Core/Engine/CabinPaths.cs:101 | 0/0/0 | 1/0/0 |  |
| Core.Nature.EarWindSynth.NoiseTables | contents | CC/AudioEngine/Core/Nature/EarWindSynth.cs:216 | 0/0/0 | 1/0/0 |  |
| Core.Nature.ExtendedSources._layouts | contents | CC/AudioEngine/Core/Nature/ExtendedSources.cs:69 | 0/0/0 | 1/0/0 |  |
| Core.Nature.RainSynth.SprayNorms | contents | CC/AudioEngine/Core/Nature/RainSynth.cs:659 | 0/0/0 | 1/0/0 |  |
| Core.Thunder._lineResponses | contents | CC/AudioEngine/Core/Thunder.cs:676 | 0/0/0 | 1/0/0 |  |
| Core.VehicleSynth.RollingFilters._byRate | contents | CC/AudioEngine/Core/VehicleSynth.cs:302 | 0/0/0 | 1/0/0 |  |
| Fmod.NonFinite._kinds | array | CC/AudioEngine/Fmod/NonFinite.cs:44 | 2/0/0 | 1/0/0 |  |
| Fmod.NonFinite._names | array | CC/AudioEngine/Fmod/NonFinite.cs:45 | 1/0/0 | 1/0/0 |  |
| Fmod.NonFinite._regions | array | CC/AudioEngine/Fmod/NonFinite.cs:46 | 1/0/0 | 1/0/0 |  |
| Fmod.TakeLevels.Banks | contents | CC/AudioEngine/Fmod/TakeLevels.cs:32 | 0/0/0 | 1/0/0 |  |
| Fmod.TakeLevels.Gains | contents | CC/AudioEngine/Fmod/TakeLevels.cs:33 | 1/0/0 | 1/0/0 |  |
| Fmod.Talkers._all | contents | CC/AudioEngine/Fmod/TalkerVoice.cs:218 | 2/0/0 | 3/0/0 |  |
| SteamAudio.SteamAudioScene.EmbreeDevices | contents | CC/AudioEngine/SteamAudio/SteamAudioScene.cs:38 | 3/0/0 | 1/0/0 |  |
| SteamAudio.TracedReverbSet.Rooms | contents | CC/AudioEngine/SteamAudio/TracedReverb.cs:536 | 6/0/0 | 3/0/0 |  |
| SteamAudio.TracedReverbSet._retiredCabins | contents | CC/AudioEngine/SteamAudio/TracedReverb.cs:671 | 4/0/0 | 4/0/0 |  |
| AudioEmission._presetOffsets | contents | Common/AudioEmission.cs:69 | 1/0/0 | 1/0/0 |  |
| ExternalBallistics._zeroCache | contents | Common/ExternalBallistics.cs:179 | 1/0/0 | 1/0/0 |  |
| GlassFracture._kernels | contents | Common/GlassFracture.cs:1481 | 0/0/0 | 1/0/0 |  |
| Hearing.BandAnalyser._byRate | contents | Common/Hearing/BandAnalyser.cs:56 | 0/0/0 | 1/0/0 |  |
| Loudness._unknownCartridges | contents | Common/Loudness.cs:419 | 1/0/0 | 1/0/0 |  |
| MachineRegistry._assembled | contents | Common/Machines.cs:296 | 0/0/0 | 4/0/0 |  |
| MagicCurve.Cache | contents | Common/WheelDynamics.cs:21 | 0/0/0 | 1/0/0 |  |
| PerfProbe._entries | contents | Common/PerfProbe.cs:36 | 2/0/0 | 3/0/0 |  |
| VehicleProfile._cache | contents | Common/Vehicles.cs:766 | 0/0/0 | 1/0/0 |  |
| WeaponRegistry._byId | contents | Common/Weapons.cs:345 | 3/0/0 | 1/0/0 |  |
| WindField._held | contents | Common/Wind.cs:62 | 2/0/0 | 2/0/0 |  |

#### T7d. Assignable, never written after start

Reads and writes are counted per reference: lib = library code, host = host projects, test = Tests and AudioLab. "*" marks a writer in Tests or AudioLab.

| Static | Kind | Declared | Reads lib/host/test | Writes lib/host/test | Writers outside its type |
|---|---|---|---|---|---|
| Core.Nature.ExtendedSources.TreePlaces | assignable | CC/AudioEngine/Core/Nature/ExtendedSources.cs:52 | 1/0/1 | 0/0/0 |  |
| Core.WetTyres.BowDb | assignable | CC/AudioEngine/Core/WetTyres.cs:69 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.BowHighHz | assignable | CC/AudioEngine/Core/WetTyres.cs:70 | 2/0/0 | 0/0/0 |  |
| Core.WetTyres.BowLowHz | assignable | CC/AudioEngine/Core/WetTyres.cs:70 | 2/0/0 | 0/0/0 |  |
| Core.WetTyres.BrightnessExponent | assignable | CC/AudioEngine/Core/WetTyres.cs:56 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.CabinCornerHz | assignable | CC/AudioEngine/Core/WetTyres.cs:79 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.CabinDb | assignable | CC/AudioEngine/Core/WetTyres.cs:79 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.EjectionDb | assignable | CC/AudioEngine/Core/WetTyres.cs:46 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.EjectionHighHz | assignable | CC/AudioEngine/Core/WetTyres.cs:55 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.EjectionLowHz | assignable | CC/AudioEngine/Core/WetTyres.cs:52 | 2/0/0 | 0/0/0 |  |
| Core.WetTyres.FallbackDrops | assignable | CC/AudioEngine/Core/WetTyres.cs:75 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.GrooveModulation | assignable | CC/AudioEngine/Core/WetTyres.cs:58 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.ImpactDb | assignable | CC/AudioEngine/Core/WetTyres.cs:63 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.ImpactRiseSeconds | assignable | CC/AudioEngine/Core/WetTyres.cs:65 | 2/0/0 | 0/0/0 |  |
| Core.WetTyres.ImpactTailSeconds | assignable | CC/AudioEngine/Core/WetTyres.cs:65 | 2/0/0 | 0/0/0 |  |
| Core.WetTyres.ImpactsPerSecond | assignable | CC/AudioEngine/Core/WetTyres.cs:61 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.SpeedExponent | assignable | CC/AudioEngine/Core/WetTyres.cs:50 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.SplashDb | assignable | CC/AudioEngine/Core/WetTyres.cs:74 | 1/0/0 | 0/0/0 |  |
| Core.WetTyres.WaterExponent | assignable | CC/AudioEngine/Core/WetTyres.cs:48 | 1/0/0 | 0/0/0 |  |
| SteamAudio.SteamAudioScene.Defaults.SceneSettings | assignable | CC/AudioEngine/SteamAudio/SteamAudioScene.cs:455 | 0/0/0 | 0/0/0 |  |
| WindField.FromDegrees | settable | Common/Wind.cs:78 | 0/0/0 | 0/0/0 |  |

#### T7e. Tables filled once (115)

AcousticBands.HighThirdsHz, AcousticBands.LowThirdsHz, AcousticBands.MidThirdsHz, AdminGun.Chord, AdminGun.Modes, AudioClock._clock, BulletImpact._stuff, CarDoor.BoomDb, CarDoor.DetentShape, CarDoor.Edges, CarDoor.HandleShape, CarDoor.HandleSteps, CarDoor.SettleSteps, CarDoor.SlamHits, CarDoor.SlamShape, CarDoor.UnlatchShape, CarWindow.BetweenDb, CarWindow.BottomDb, CarWindow.TopDb, Core.BoundaryModel.ProbeDirections, Core.Engine.BandEq.Centres, Core.Nature.EventSum.PulseTable, Core.Nature.RainSynth.ClusterScales, Core.Nature.RainSynth.PerClass, Core.Nature.ShoreReferences.FittedKeys, Core.Nature.ShoreReferences.Octaves, Core.Nature.ShoreReferences.Rows, Core.Nature.ShoreReferences.WaveformKurtosis, Core.Nature.TextureStatistics.Keys, Core.Nature.TextureStatistics.ModulationCentres, Core.Nature.TextureStatistics.ReferenceRows, Core.Nature.TextureStatistics.Regions, Core.ReportMeasure.OctaveCentres, Core.Signals.StruckBell.Plate, Core.Thunder.ScatterBands, DoorKnock.Shape, DoorKnock.T60, DropSizeTable.ClassEdgesMm, ElevatorDoor.Sim.PeakNames, ExternalBallistics.Cd, ExternalBallistics.Mach, Fmod.OwnVoiceRing.Shared, Fmod.RainFeeds.Feed, Fmod.TruePeakLimiter.Phases, Geometry.ShapeLibrary.BoxCorners, Geometry.ShapeLibrary.BoxTriangles, Geometry.ShapeLibrary.OneSurface, Geometry.TriangleWorld.Empty, GlassDoor.JambStops, GlassDoor.Sim.PeakNames, GlassFracture.FreeBeam, GlassFracture.FreeSquare, GlassKind.Annealed, GlassKind.Doors, GlassKind.Tempered, Hearing.EqualLoudness.AlphaF, Hearing.EqualLoudness.F, Hearing.EqualLoudness.Lu, Hearing.EqualLoudness.Tf, Hearing.LoudnessCompensation.FitWeight, Hearing.LoudnessCompensation.Solve, Hearing.Timbre.AnsiNormalSpectrumLevel, Hearing.Timbre.Speech, Hearing.ZwickerLoudness.A0, Hearing.ZwickerLoudness.Centres, Hearing.ZwickerLoudness.Dcb, Hearing.ZwickerLoudness.Ddf, Hearing.ZwickerLoudness.Dll, Hearing.ZwickerLoudness.Ltq, Hearing.ZwickerLoudness.Rap, Hearing.ZwickerLoudness.Rns, Hearing.ZwickerLoudness.Usl, Hearing.ZwickerLoudness.Zup, KnobDoor.HingeHeights, KnobDoor.Sim.PeakNames, LockCylinder.Sim.PeakNames, ModelLibrary.BuiltIn, ModelLibrary.Types, PushBarDoor.CaseAirHz, PushBarDoor.SilencerHeights, PushBarDoor.Sim.PeakNames, RainPlate.SpectrumCumulative, RainPlate.SpectrumMagnitude, RainSurvey.RingEdges, Ricochet.Harmonics, Ricochet._faces, RoadDrainageSpec.Default, RoadSurfaces.ByName, RoadSurfaces.Table, RoadWaterLaw.Table, RoomAcoustics.FaceNames, Runoff.Rungs, Runoff._held, ShapedNoise.Centres, SlidingDoor.Sim.PatchPoints, SlidingDoor.Sim.PatchWeights, SlidingDoor.Sim.PeakNames, SlidingDoor.Sim.ones, Spectrum.BandEdges, SteamAudio.DiffuseTail.Dodecahedron, SteamAudio.SmoothTail.EdgesHz, SteamAudio.SmoothTail.SegmentEdges, SteamAudio.SteamAudioScene._corner, SteamAudio.SteamAudioScene._faceIdx, Systems.FaceOpenings.Faces, WeaponHandling.Centres, WeaponRegistry.Akm, WeaponRegistry.Ar15, WeaponRegistry.Glock, WeaponRegistry.M700, WeaponRegistry.Revolver357, WeaponRegistry.ServicePistol, WeaponRegistry.Shotgun, WindField.Eddies, WindWeather.Default

#### T7f. Per-thread scratch (32)

EarlyReflections._chain, EarlyReflections._flFaces, EarlyReflections._flHits, EarlyReflections._flImages, EarlyReflections._hits, EarlyReflections._images, EarlyReflections._legIndex, EarlyReflections._legSolids, EarlyReflections._local, EarlyReflections._localIndex, EarlyReflections._mirrorScratch, EarlyReflections._mirrors, EarlyReflections._pairScratch, EarlyReflections._planeList, EarlyReflections._planes, Enclosure._casts, Enclosure._nearby, Enclosure._openness, Enclosure._opennessScene, Fmod.DspCallback._cachedClock, Fmod.DspCallback._cachedClockTable, Fmod.DspCallback._cachedGet, Fmod.DspCallback._cachedTable, Geometry.TriangleWorld.Scratch._solids, Hearing.LiveBands.t_analyser, Hearing.LiveBands.t_window, MachineRegistry._assembling, OpeningRoutes.Scratch._current, OpeningRoutes.SolidGrid._threadMark, OpeningRoutes.SolidGrid._threadStamp, OpeningRoutes.SolidGrid._threadStampFor, OpeningRoutes.TileIndex._refs

### D. Files and environment

#### T9a. Files and directories

| Where | Type | Kind | Code |
|---|---|---|---|
| CC/AudioEngine/Core/AudioBank.cs:16 | Core.AudioBank | Directory.Exists | `if (!Directory.Exists(basePath)) return;` |
| CC/AudioEngine/Core/AudioBank.cs:18 | Core.AudioBank | Path.GetFullPath | `string fullBasePath = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);` |
| CC/AudioEngine/Core/AudioBank.cs:19 | Core.AudioBank | Directory.GetFiles | `var files = Directory.GetFiles(fullBasePath, "*.*", SearchOption.AllDirectories) .Where(f => f.EndsWith(".wav") \|\| f.EndsWith(".ogg") \...` |
| CC/AudioEngine/Core/AudioBank.cs:20 | Core.AudioBank | path literal | `var files = Directory.GetFiles(fullBasePath, "*.*", SearchOption.AllDirectories) .Where(f => f.EndsWith(".wav") \|\| f.EndsWith(".ogg") \...` |
| CC/AudioEngine/Core/AudioBank.cs:24 | Core.AudioBank | Path.GetFullPath | `string fullPath = Path.GetFullPath(file);` |
| CC/AudioEngine/Core/AudioEngineFacade.cs:78 | Core.AudioEngineFacade | AppDomain.BaseDirectory | `string audioPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS", "SOUNDS");` |
| CC/AudioEngine/Core/DoorRenderCache.cs:47 | Core.DoorRenderCache | AppContext.BaseDirectory | `public static string ShippedFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "ASSETS", "rendercache", Name);` |
| CC/AudioEngine/Core/DoorRenderCache.cs:56 | Core.DoorRenderCache | Environment.GetFolderPath | `string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);` |
| CC/AudioEngine/Core/DoorRenderCache.cs:79 | Core.DoorRenderCache | File.Exists | `if (!File.Exists(path)) continue;` |
| CC/AudioEngine/Core/DoorRenderCache.cs:80 | Core.DoorRenderCache | File.OpenRead | `using var r = new BinaryReader(File.OpenRead(path));` |
| CC/AudioEngine/Core/DoorRenderCache.cs:112 | Core.DoorRenderCache | Directory.CreateDirectory | `Directory.CreateDirectory(dir);` |
| CC/AudioEngine/Core/DoorRenderCache.cs:116 | Core.DoorRenderCache | File.Create | `using (var w = new BinaryWriter(File.Create(tmp))) { float peak = 0f; foreach (float v in pcm) if (float.IsFinite(v)) peak = MathF.Max(pe...` |
| CC/AudioEngine/Core/DoorRenderCache.cs:129 | Core.DoorRenderCache | File.Move | `File.Move(tmp, path, overwrite: true);` |
| CC/AudioEngine/Core/DoorRenderCache.cs:142 | Core.DoorRenderCache | Directory.GetParent | `var parent = Directory.GetParent(dir);` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:110 | Fmod.FmodResourceManager | native load: createSound | `RESULT res = _system.createSound(bytes, MODE.OPENMEMORY \| MODE.OPENRAW \| MODE._3D \| Rolloff.Mode \| MODE.LOOP_OFF, ref info, out FMOD....` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:138 | Fmod.FmodResourceManager | native load: createSound | `RESULT res = _system.createSound(pcm16Mono, MODE.OPENMEMORY \| MODE.OPENRAW \| MODE._3D \| Rolloff.Mode \| MODE.LOOP_OFF, ref info, out F...` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:201 | Fmod.FmodResourceManager | File.Exists | `if (!File.Exists(path)) { string[] extensions = { ".wav", ".ogg", ".mp3" }; bool found = false; foreach (var ext in extensions) { if (Fil...` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:203 | Fmod.FmodResourceManager | path literal | `string[] extensions = { ".wav", ".ogg", ".mp3" };` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:205 | Fmod.FmodResourceManager | File.Exists | `if (File.Exists(path + ext)) { path = path + ext; found = true; break; }` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:216 | Fmod.FmodResourceManager | native load: createSound | `RESULT res = _system.createSound(path, mode, out sound);` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:1214 | Fmod.FmodAudioProvider | native load: iplHRTFCreate | `if (Phonon.iplHRTFCreate(_saContext, ref au, ref hs, out _saHrtf) != Phonon.IPL_STATUS_SUCCESS) { Log.Warning("Steam Audio: HRTF create f...` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:2046 | Fmod.FmodAudioProvider | path literal | `if (soundId.Contains("ASSETS", StringComparison.OrdinalIgnoreCase)) return soundId;` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:2048 | Fmod.FmodAudioProvider | AppDomain.BaseDirectory | `return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS", "SOUNDS", normId);` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:2057 | Fmod.FmodAudioProvider | File.Exists | `if (!File.Exists(path)) foreach (var ext in new[] { ".wav", ".ogg", ".mp3" }) if (File.Exists(path + ext)) { path += ext; break; }` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:2058 | Fmod.FmodAudioProvider | path literal | `foreach (var ext in new[] { ".wav", ".ogg", ".mp3" }) if (File.Exists(path + ext)) { path += ext; break; }` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:2059 | Fmod.FmodAudioProvider | File.Exists | `if (File.Exists(path + ext)) { path += ext; break; }` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:2222 | Fmod.FmodAudioProvider | native load: iplHRTFCreate | `if (Phonon.iplHRTFCreate(_saContext, ref au, ref hs, out _saHrtfTraced) != Phonon.IPL_STATUS_SUCCESS) { _saHrtfTraced = IntPtr.Zero; Phon...` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:5345 | Fmod.FmodAudioProvider | native load: createSound | `if (_system.createSound(IntPtr.Zero, MODE.LOOP_NORMAL \| MODE.OPENUSER, ref info, out _recSound) != RESULT.OK) return false;` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:5443 | Fmod.FmodAudioProvider | native load: createSound | `if (_system.createSound(pcm, MODE.OPENMEMORY \| MODE.OPENRAW \| MODE.CREATESAMPLE \| MODE.LOOP_OFF \| MODE._2D, ref info, out sound) != R...` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:5496 | Fmod.FmodAudioProvider | native load: createSound | `if (_system.createSound(pcm, MODE.OPENMEMORY \| MODE.OPENRAW \| MODE.CREATESAMPLE \| MODE.LOOP_NORMAL \| MODE._2D, ref info, out sound) !...` |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs:5579 | Fmod.FmodAudioProvider | native load: createSound | `if (!FmodCheck(_system.createSound(_diagPcm, MODE.OPENMEMORY \| MODE.OPENRAW \| MODE._3D \| Rolloff.Mode \| MODE.LOOP_NORMAL, ref info, o...` |
| CC/AudioEngine/Fmod/FmodDebugLog.cs:46 | Fmod.FmodDebugLog | Path.GetTempPath | `string path = Environment.GetEnvironmentVariable("OPENFPS_FMOD_DEBUG_FILE") ?? Path.Combine(Path.GetTempPath(), "fmod-debug.log");` |
| CC/AudioEngine/Fmod/GranularBank.cs:72 | Fmod.GranularBank | native load: createSound | `RESULT res = _system.createSound(path, mode, out FMOD.Sound sound);` |
| CC/AudioEngine/Fmod/GranularBank.cs:121 | Fmod.GranularBank | path literal | `if (soundId.Contains("ASSETS", StringComparison.OrdinalIgnoreCase)) return soundId;` |
| CC/AudioEngine/Fmod/GranularBank.cs:124 | Fmod.GranularBank | AppDomain.BaseDirectory | `string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS", "SOUNDS", normId);` |
| CC/AudioEngine/Fmod/GranularBank.cs:126 | Fmod.GranularBank | File.Exists | `if (File.Exists(path)) return path;` |
| CC/AudioEngine/Fmod/GranularBank.cs:128 | Fmod.GranularBank | path literal | `string[] extensions = { ".wav", ".ogg", ".mp3" };` |
| CC/AudioEngine/Fmod/GranularBank.cs:131 | Fmod.GranularBank | File.Exists | `if (File.Exists(path + ext)) return path + ext;` |
| CC/AudioEngine/Fmod/TakeLevels.cs:45 | Fmod.TakeLevels | Directory.Exists | `if (dir != null && Directory.Exists(dir)) { var bank = Banks.GetOrAdd(dir, Measure); if (bank.Count >= 3 && bank.TryGetValue(Path.GetFile...` |
| CC/AudioEngine/Fmod/TakeLevels.cs:67 | Fmod.TakeLevels | path literal | `string sidecar = Path.Combine(dir, "levels.json");` |
| CC/AudioEngine/Fmod/TakeLevels.cs:68 | Fmod.TakeLevels | File.Exists | `if (File.Exists(sidecar)) { var read = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(sidecar));...` |
| CC/AudioEngine/Fmod/TakeLevels.cs:70 | Fmod.TakeLevels | File.ReadAllText | `var read = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(sidecar));` |
| CC/AudioEngine/Fmod/TakeLevels.cs:73 | Fmod.TakeLevels | Directory.GetFiles | `foreach (var f in Directory.GetFiles(dir, "*.wav")) if (ImpactDb(f) is float db) levels[Path.GetFileName(f)] = db;` |
| CC/AudioEngine/Fmod/TakeLevels.cs:81 | Fmod.TakeLevels | File.OpenRead | `using var br = new BinaryReader(File.OpenRead(file));` |
| CC/FmodNative/fmod.cs:1327 | FMOD.System | native load: createSound | `return createSound(name, mode, ref exinfo, out sound);` |
| CC/FmodNative/fmod.cs:1349 | FMOD.System | native load: createStream | `return createStream(name, mode, ref exinfo, out sound);` |
| CC/Platform/NativeAudioLibraries.cs:53 | Platform.NativeAudioLibraries | AppContext.BaseDirectory | `public static string BaseDirectory => AppContext.BaseDirectory;` |
| CC/Platform/NativeAudioLibraries.cs:61 | Platform.NativeAudioLibraries | File.Exists | `if (!File.Exists(Path.Combine(baseDir, lib.FileName))) missing.Add(lib);` |
| CC/Platform/NativeAudioLibraries.cs:67 | Platform.NativeAudioLibraries | File.Exists | `public static bool IsPresent(string fileName) => File.Exists(Path.Combine(BaseDirectory, fileName));` |
| Common/ElevatorDoor.cs:540 | ElevatorDoor | File.Create | `using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "lift-" + PeakNames[i] + ".raw")))) fo...` |
| Common/GlassDoor.cs:1050 | GlassDoor | File.Create | `using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "gd-" + PeakNames[i] + ".raw")))) fore...` |
| Common/KnobDoor.cs:1316 | KnobDoor | File.Create | `using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, PeakNames[i] + ".raw")))) foreach (var...` |
| Common/LockCylinder.cs:610 | LockCylinder | File.Create | `using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "key-" + PeakNames[i] + ".raw")))) for...` |
| Common/Machines.cs:192 | MachineRegistry | Directory.Exists | `if (!Directory.Exists(directory)) return 0;` |
| Common/Machines.cs:197 | MachineRegistry | Directory.EnumerateFiles | `foreach (string file in Directory.EnumerateFiles(directory, "*.json")) { try { var dto = JsonSerializer.Deserialize<MachineDto>(File.Read...` |
| Common/Machines.cs:201 | MachineRegistry | File.ReadAllText | `var dto = JsonSerializer.Deserialize<MachineDto>(File.ReadAllText(file), JsonOptions);` |
| Common/ModelLibrary.cs:154 | ModelLibrary | Directory.Exists | `if (!Directory.Exists(directory)) return 0;` |
| Common/ModelLibrary.cs:159 | ModelLibrary | Directory.EnumerateFiles | `foreach (string file in Directory.EnumerateFiles(directory, "*.json")) { try { var wrapper = JsonSerializer.Deserialize<ModelFile>(File.R...` |
| Common/ModelLibrary.cs:163 | ModelLibrary | File.ReadAllText | `var wrapper = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(file), Json);` |
| Common/ModelLibrary.cs:291 | ModelLibrary | Directory.CreateDirectory | `Directory.CreateDirectory(directory);` |
| Common/ModelLibrary.cs:296 | ModelLibrary | File.WriteAllText | `File.WriteAllText(Path.Combine(directory, $"{kind}.{id}.json"), ToJson(kind, id, make()));` |
| Common/PushBarDoor.cs:927 | PushBarDoor | File.Create | `using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "pb-" + PeakNames[i] + ".raw")))) fore...` |
| Common/SlidingDoor.cs:1448 | SlidingDoor | File.Create | `using (var f = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(StemFolder, "sd-" + PeakNames[i] + ".raw")))) fore...` |

#### T9b. Environment variables read by library code

| File | Variables |
|---|---|
| CC/AudioEngine/Acoustics/AsyncAcousticWorker.cs | OPENFPS_AUDIO_DEBUG, OPENFPS_EMBREE, OPENFPS_STEAMAUDIO_SIM, OPENFPS_TILE_SCENES |
| CC/AudioEngine/Core/DoorRenderCache.cs | OPENFPS_RENDER_CACHE |
| CC/AudioEngine/Core/Engine/CabinPaths.cs | OPENFPS_CABIN_PATHS |
| CC/AudioEngine/Core/Nature/ExtendedSources.cs | OPENFPS_WIDE_SOURCES, OPENFPS_WIDE_TREE_PLACES |
| CC/AudioEngine/Core/Nature/ShoreSynth.cs | OPENFPS_WAVES_ |
| CC/AudioEngine/Fmod/FmodAudioProvider.cs | OPENFPS_AUDIO_CAPTURE, OPENFPS_AUDIO_CAPTURE_PRE, OPENFPS_AUDIO_DEBUG, OPENFPS_AUDIO_TRACE, OPENFPS_EAR_WIND, OPENFPS_ECHOES, OPENFPS_FMOD_WAV, OPENFPS_HRTF, OPENFPS_MASTER_DB, OPENFPS_MASTER_MAKEUP_DB |
| CC/AudioEngine/Fmod/FmodDebugLog.cs | OPENFPS_FMOD_DEBUG, OPENFPS_FMOD_DEBUG_FILE |
| CC/AudioEngine/Fmod/MasterDither.cs | OPENFPS_DITHER, OPENFPS_FMOD_WAV |
| CC/AudioEngine/Fmod/MasterLimiter.cs | OPENFPS_LIMITER |
| CC/AudioEngine/Fmod/MixerQuality.cs | OPENFPS_AUDIO_CAPTURE_FLOAT, OPENFPS_FMOD_OUTPUT, OPENFPS_MIXER_RATE, OPENFPS_RESAMPLER |
| CC/AudioEngine/SteamAudio/PhononAmbisonics.cs | SA_MIRROR |
| Common/Geometry/TriangleGeometry.cs | OPENFPS_TRIANGLES |
| Common/GlassFracture.cs | OPENFPS_GLASS_ONLY, OPENFPS_GLASS_RING_SHARE, OPENFPS_GLASS_TAG |
| Common/Hearing/EarModel.cs | OPENFPS_EAR_MODEL, OPENFPS_LISTENING_LEVEL |
| Common/Loudness.cs | OPENFPS_LEVEL_COMPRESSION |
| Common/PerfProbe.cs | OPENFPS_PROFILE |

37 variables in 16 files.

## 13. Stage 0 as built (2026-10-07)

Three guards, each a test class in OpenFPS.Tests with its stored data in `OpenFPS.Tests/LibraryBoundary/`.
They run with the rest of the suite on GitHub Actions; none needs FMOD or Steam Audio.

### 13.1 The ratchet: `LibraryBoundaryTests`

- `EveryFileIsSorted`: every `.cs` file of Common, Client.Core and the library's projects is in
  `files.tsv` as library, host or mixed (a mixed file names its library types). A new file fails until
  it is sorted, and a listed file that is gone fails until the list follows it.
- `NoNewCrossings`: binds those projects' sources with Roslyn (one compilation, no source generators)
  and counts, in every library file (only the library types' declarations of a mixed file), each name
  that binds to a host type or a member of one, `var` included, exactly as the survey's reader does.
  Each (file, host type) count must equal its line in `allowed.tsv`: over is a new crossing, under is a
  fix whose allowance was not lowered in the same commit.
- `LibraryReferencesOnlyLibrary` (added with stage 1, 14.2): each library assembly references only the
  runtime, its allowed packages and lower library projects.
- Both lists come from the survey: `tools/sound_boundary/run.sh` now writes `files.tsv` and
  `allowed.tsv` beside `crossings.tsv`. At e814ed20 the test's counts equal `crossings.tsv` line for
  line: 607 references in 26 files (the survey counted 600 at 26d531a2; the rest is code added since).
- `classify.py` sorted four new Common files into the library by its default rule; they are host:
  `EntityGeometry.cs` (the entity adapter geometry stage 2 split out), `MoverPoses.cs` (the server's
  door-leaf count), `DrivingCuePlanner.cs` (a driving aid over the road network), `RoadMapData.cs`
  (the MapRoads message). Without that the allowance would have held 202 references the library does
  not have.
- Merged with main at eff5b06c: 608. The one more is the world editor's: `ModelLibrary` (library)
  reads its models with `Networking.Vector3Converter` (`JsonConverters.cs`, host). It is in the
  allowance as main has it; a converter for a vector belongs with the library's JSON, a stage 3 fix.
- Cost: about 17 s, nearly all of it binding the two projects.

### 13.2 The render fingerprint: `RenderFingerprintTests`

Seventeen renders, hashed (SHA-256 of the float buffers, in order, with their lengths) and stored in
`render-fingerprints.tsv` with each buffer's level every 4096 samples:

| Render | What |
|---|---|
| `engine.v8_muscle` | `VehicleSynth.Render`: cranking, idle, then the dyno held at 1,500, 3,000 and 4,500 rpm; exhaust, intake, tyres and block |
| `door.knob.close`, `door.knob.open` | `KnobDoor.RenderGameClose`, `RenderOpen` |
| `door.pushbar.open`, `door.sliding.close`, `door.glass.close`, `door.elevator.open`, `door.lock.unlock` | each model's `Render*` |
| `door.car.close`, `window.car.down` | `CarDoor.Render`, `CarWindow.Render` |
| `rain.asphalt`, `rain.steel`, `rain.puddle` | `RainSynth` over one square metre a metre away, 8 mm/h |
| `siren.patrol.wail` | `ElectronicSiren` |
| `train.light_rail.pass` | `TrainSynth`, every source summed |
| `thunder.ground_1500` | `Thunder.Render` on two threads at 24 kHz |
| `clap.dry` | `Applause.RenderClap` |

- Deterministic: fixed seeds, no clock, no thread-count dependence. The lab levers a render reads
  (`EngineSynth.ValveJetNoise` and the debug switches, `KnobDoor.KeeperBendsStrike`, every door's
  `StemFolder`) are held at their game values while it renders. The same bits in Debug and Release,
  and from one process to the next.
- Left out: the clap in the traced room (Steam Audio's native library, which CI does not have);
  `GlassFracture` (its worker count is the machine's core count, so its sum is too); anything rendered
  by a voice state in the mixer (`EngineVoiceState` reads `AudioClock`, `MixerQuality.MixerRate` and the
  ear settings).
- Bits depend on the maths library as well as the code. `MathF.Sin` and the rest are the platform's
  libm, and glibc 2.41 replaced many float functions with correctly rounded ones, so this machine
  (glibc 2.43) and CI's Ubuntu (2.39) round some of them differently. The file stores a probe of the
  maths (every libm function the models call, over 4,096 arguments, and the vector width). Where the
  probe matches, every render must match to the bit; where it does not, every 4096-sample block within
  40 dB of the buffer's loudest must be within 1 dB of the stored level, and the test says which it did.
  Expect CI to take the second path. Not yet seen on CI.
- Cost: about 60 s in Debug, the door models nearly all of it.

### 13.3 The emitter-stream replay: `EmitterStreamReplayTests`

A whole `ClientAudioSystem` driven through three scripted worlds, every call it makes on the mixer
written down (`StreamMixer`) and compared with `streams/<scenario>.txt.gz`:

| Scenario | Frames | What |
|---|---:|---|
| `walk` | 480 | on foot up a street between a brick building and a concrete wall, a car idling at the kerb, another driving past at 14 m/s |
| `drive` | 360 | riding east at 12 m/s along a wall, an oncoming car sounding its horn as it nears |
| `traffic_rain` | 360 | standing by a wall under a porch roof in 8 mm/h of rain, five cars on two lanes and a crossing street |

- Recorded: every voice started (the whole emitter, every field), re-placed (the fields that changed),
  stopped and faded; every acoustic path (the fields that changed); the listener, shelter, boundaries,
  ear wind, enclosure, reverb, air, ambience beds, registered sounds (a hash of the PCM). 37,000 lines
  for the rain, 430 KB compressed for all three.
- Pinned so a run is the same every time: the acoustic worker has no thread (`AsyncAcousticWorker.Manual`;
  the test steps it after each update, so an answer lands on the same frame every run) and never starts
  Steam Audio; the triangle world, the tile acoustics and the rain survey are built in place; the audio
  clock is the test's (`AudioClock.UseForTest`); birds, near drops and footsteps are seeded; the settings
  the system reads from statics (the level compression, the ear model, the speed of sound, the mixer
  rate, wide sources, cabin paths, runoff) are held at the game's defaults and the wind is still. Each
  scenario runs twice per test and must agree with itself before it is compared. Checked across three
  processes and among 170 other audio tests in one process.
- It earned its keep at once: merging main (eff5b06c) changed `traffic_rain` from line 5,064, where
  the voices began to be re-placed in a different order. That is main's ranking of voices by how loud
  the ear hears them (615b8ae3), an intended change; the stream was regenerated in the merge's
  follow-up commit, which says so. `walk` and `drive` did not change.
- Library changes this needed, none heard: `AsyncAcousticWorker.Manual` and `StepForTest` (the loop is
  unchanged for the game); a `manualAcoustics` and `seed` on `ClientAudioSystem`'s test constructor;
  `RainField`'s seed and `SurveyInPlace`; `WorldAudioPlayer`'s `prewarm` (the replay starts no door or
  thunder renders in the background: six systems a run would each have rendered the city's doors);
  `AudioClock.UseForTest` (internal; Common gives OpenFPS.Tests
  its internals). One is a fix: `DropBank` seeded each near drop's render from `string.GetHashCode`, which
  .NET randomises per process, so every client and every test run rendered different drops; it is a
  fixed hash of the key now (the same drops, chosen the same way, in every process).
- Not in the stream: the door renders every client starts in the background (`PrewarmDoors`; the
  fingerprint holds the models); one-off world events, which render on the thread pool; Steam Audio's
  answers; the provider's own DSP; footsteps (no sound bank) and birds (no habitat in these worlds).
- Under other maths the calls must be the same calls and every number within 1e-3 (relative) or 1e-4,
  compared with each voice's fields written whole. `OPENFPS_REPLAY_DUMP=<dir>` writes each run's stream
  as text, to diff two commits line by line.
- Cost: about 3 s.

Found on the way: every test that builds a `ClientAudioSystem` started rendering all of the city's
doors in the background (minutes of a core each time) and kept them in the player's own render cache
(`~/.local/share/OpenFPS/rendercache`), pruning the folders of other builds. The tests now run with a
scratch `XDG_DATA_HOME` as well as the scratch config folder (`TestConfigIsolation`), and
`ClientAudioHarness` builds its system without the prewarm (`prewarm: false` on the test constructor;
the lab's spikes keep it). With an empty cache and the prewarm still on, the renders starved
`ClientAudioSelectionTests`' rain test until it failed.

### 13.4 Regenerating the stored data

The rule: the stored hashes, streams and allowance change only in the commit that changes the sound or
the boundary on purpose, and that commit says why (in its message and in changes.md). A move, a type
split out of a file, a static made an instance: none of them may change any of the three.

| Guard | When it changes | How |
|---|---|---|
| `allowed.tsv` | a crossing fixed: lower it | `OPENFPS_BOUNDARY_WRITE=1 dotnet test OpenFPS.Tests --filter LibraryBoundaryTests` (only lowers) |
| `allowed.tsv` | a library file moved to another path | `OPENFPS_BOUNDARY_WRITE=all ...`; the diff must show only the path changing |
| `files.tsv` | a file added, moved or deleted in a sorted project | `tools/sound_boundary/run.sh`, copy its `files.tsv`; a new file's group from `classify.py` |
| `render-fingerprints.tsv` | a model's sound changed on purpose | `OPENFPS_FINGERPRINT_WRITE=1 dotnet test OpenFPS.Tests --filter RenderFingerprintTests` |
| `streams/*.txt.gz` | what the audio system tells the mixer changed on purpose | `OPENFPS_REPLAY_WRITE=1 dotnet test OpenFPS.Tests --filter EmitterStreamReplayTests` |

The maths probe is stored with the fingerprints and the streams. Regenerate them where they were made
(glibc 2.43 here) or with the same maths: written anywhere else, they hold that machine's probe, and
here every later run falls back to the tolerant comparison.

## 14. Stage 1 as built (2026-10-07): `OpenFPS.Native`

### 14.1 What moved

By `git mv`, namespaces unchanged:

| From | To |
|---|---|
| `OpenFPS.Client.Core/FmodNative/fmod.cs`, `fmod_dsp.cs`, `fmod_errors.cs`, `fmod_studio.cs` | `OpenFPS.Native/FmodNative/` |
| `OpenFPS.Client.Core/AudioEngine/SteamAudio/Phonon.cs`, `PhononSim.cs`, `PhononAmbisonics.cs` | `OpenFPS.Native/SteamAudio/` |
| `OpenFPS.Client.Core/Platform/NativeAudioLibraries.cs`, `BackgroundPriority.cs` | `OpenFPS.Native/Platform/` |

- `OpenFPS.Native` references no project and one package, Serilog (`BackgroundPriority` logs a thread
  it could not lower). `AllowUnsafeBlocks` as Client.Core had.
- `OpenFPS.Client.Core` references it. Nothing else does directly: the Windows and GTK clients, the lab
  and the tests get it through Client.Core. The server does not.
- `InternalsVisibleTo` on Native: `OpenFPS.Client.Core`, `OpenFPS.Tests`, `OpenFPS.AudioLab` (`Phonon`
  is internal). `OpenFPS.Audio` is added when that project exists (stage 6), not before: a grant to an
  assembly that does not exist is one anybody can claim by the name. Client.Core keeps its own two
  grants for its own internals.
- The native libraries are copied from `lib/` by the executables, as before; the `DllImport`s resolve
  next to the executable whichever assembly declares them, so nothing about loading changed. Checked:
  `OpenFPS.Native.dll` lands beside `fmod.dll` and the rest in the Windows client's output, and in the
  GTK client's and the lab's.

### 14.2 Checks

- Every project builds: the tests, the GTK client, the lab, the Windows client (compiled on Linux as
  always, `EnableWindowsTargeting`), the server.
- The render fingerprint and the emitter stream are the same bits as before the move; the ratchet's
  allowance is unchanged (no crossings in these files; `files.tsv` changed only by the paths).
- `LibraryBoundaryTests.LibraryReferencesOnlyLibrary`: OpenFPS.Native references only the runtime and
  Serilog.
- `tools/sound_boundary` reads the new project (Program.cs: its own compilation, its
  `InternalsVisibleTo`; classify.py: a rule per library project folder).
- CI: `.github/workflows/tests.yml` builds `OpenFPS.Tests`, which brings the new project in through
  Client.Core; `tools/ci/shard_tests.py` deals out test classes, which did not change. Nothing to edit.

### 14.3 Wire hash and door fingerprint

Not touched: none of these files is in OpenFPS.Common, so `WireContract.Hash` and
`DoorModelFingerprint` are computed over exactly the files they were.

## 15. Stage 2 as built (2026-10-07): `OpenFPS.Geometry`

### 15.1 What moved

First three types out of the files that stay behind, each into a file of its own in Common (one
commit, text unchanged): `BoxContainment` out of `SparseAcousticOctree.cs`, `WallBuild` out of
`WallTransmission.cs` (its doc's cref to `WallTransmission` became plain text: the geometry does not
see the acoustics), `TileKey` out of `Tiles.cs`. Then, by `git mv`, namespaces unchanged:

| From (OpenFPS.Common/) | To (OpenFPS.Geometry/) |
|---|---|
| `Geometry/*` (Bvh, GeometryPiece, Shapes, SolidContact, Surfaces, TriangleGeometry, TriangleWorld, TriangleWorldBuilder, WheelRays) | `Triangles/` |
| `GeometryUtils.cs` (with `MathHelper`), `SpatialGrid.cs`, `BoxColumns.cs`, `PerfProbe.cs` | the project's root |
| `BoxContainment.cs`, `WallBuild.cs`, `TileKey.cs` | the project's root |

- `OpenFPS.Geometry` references no project and one package, MemoryPack: a collider's `ShapeSpec` is
  `[MemoryPackable]` and travels in `ColliderComponent` (decision 4, applied here first). Common
  references Geometry; everything else gets it through Common. Nothing in it reads an entity: geometry
  stage 2 had already put `EntityGeometry` and `MoverPoses` on the host's side.
- No `InternalsVisibleTo`: nothing outside the project used an internal of these files.
- The layering table (3.5) lost its three Geometry rows: `GeometryUtils` and `TriangleWorldBuilder`
  /`Construction` now find `BoxContainment` and `WallBuild` beside them.

### 15.2 Not moved: `ColliderShape`

Section 8 listed `ColliderShape` for this stage. It stays in `Components.cs`, for Cody to confirm:

- nothing in the geometry uses it (the triangle world has its own `ShapeKind`; the entity adapter,
  `EntityGeometry`, reads `ColliderShape` and turns a box into a solid, on the host's side);
- it is the entity model's collider enum, on the wire in `ColliderComponent`, with members
  (`Sphere`, `Cylinder`, `Cone`, `Polygon`) the triangle world does not build;
- the three library files that read it (`SteamAudioScene`, `RainField`, `AcousticVolumeGenerator`) read
  it off `ColliderComponent`, which stage 6 replaces with the world input; after that the library needs
  no `ColliderShape` at all.

Moving it would put a type of the game's entity model into the geometry library to serve readers that
are going away. If a library type is wanted later, it is the geometry's own shape, not this enum.

### 15.3 The wire hash and the door fingerprint

- `WireContract.Hash` now covers Common's sources and the Geometry project's, hashed by their paths
  from the repository's root (`OpenFPS.Common.csproj`, `WireLibrarySource`). Geometry's files belong in
  it: `ShapeSpec` and `TileKey`'s numbers are on the wire, and the client predicts movement with the
  same geometry the server moves bodies with, so two builds whose geometry differs disagree about where
  a body is. The hash changed with the move (`5e96a5472e62` to `551d1fe5906e` here) and changes again
  with any edit to a Geometry file (checked: a comment added to `TileKey.cs` changed it, and taking it
  out changed it back). A client and a server from either side of this commit refuse each other at
  login, as they would for any edit to Common. Native is not in it: the server never loads it.
- `DoorModelFingerprint` is unchanged (`b9dfebdd55c8`): the door renders read none of the moved
  files. Followed through the survey's edges, every source a cached door render is made from (the
  models, `DoorPhysics`, `Doors`, `Glass`, `AcousticRegistry`, `VehicleCabin`, `VehicleBody`) is still in
  Common and still listed. Stage 3 moves them, and their list with them (section 8, stage 3 risks).
- `publish-windows.sh` reads both hashes from `obj/OpenFPS.Common/`, where they are still written.

### 15.4 Checks

- Every project builds: Geometry, Common, the server, Client.Core, the GTK client, the Windows client,
  the lab, the tests.
- The render fingerprint and the emitter stream: the same bits as before the move. The ratchet: the same
  607 references; `files.tsv` changed by the paths only; `LibraryReferencesOnlyLibrary`: Geometry
  references only the runtime and MemoryPack.Core.
- The test classes that touch the moved code: GeometryStage1Tests, GeometryStage2Tests,
  GeometryUtilsTests, BoxOverlapTests, SteamAudioSceneTests, SteamAudioMappingTests, TileSceneSetTests,
  WallTransmissionTests, WorldStreamingTests, DoorPrewarmTests.
- CI and the scripts: nothing to edit. The workflow builds `OpenFPS.Tests`, which brings the project in;
  `run-server.sh`, `run-gtk-client.sh`, `publish-server.sh` and `publish-windows.sh` build their
  executables' projects, which reference it through Common.

### 15.5 Left for stage 3

- The ten small fixes of section 8, then `OpenFPS.Sound` and the first half of `OpenFPS.Acoustics`.
  `DoorModelSource` moves with the door models; the wire hash's `WireLibrarySource` gains the Sound
  project's files (`TransientSound`, `WheelState`, `Precipitation`, `WindAir`, `LightningStrike` travel).
- `PerfProbe` is in Geometry as the survey placed it (the lowest project, so every layer can time
  itself); it is a process-wide static (section 4.2's diagnostics exception).
- `MoverPoses` stays host: the server's count of door leaves moved, read by `ServerGeometry` and the
  client's geometry adapter. The triangle world takes the poses as a function (`WithMoverPoses`).

---

## 16. Stage 3 as built (2026-10-10): `OpenFPS.Sound` and the first half of `OpenFPS.Acoustics`

Done while no other audio branch was open (geometry stage 4 ran in parallel and was told to follow moved
files). The survey was run again first, at 17ca6c53: 608 crossings in 77 allowance rows (76 file and host
type pairs), 53 layering references. Since the survey of 2026-10-06 the materials table, struck things,
fire by fuel, the loudspeakers, the gas hob, ground water, layered constructions and distant motion had
landed; every new file was already sorted (files.tsv matched the survey), but four of them crossed in ways
the plan did not list (16.2).

### 16.1 The ten fixes

Each its own commit, each with the render fingerprint (17 renders, bit for bit), the three emitter streams
and the ratchet unchanged except for the allowance it lowered.

| Fix | What changed | Crossings |
|---|---|---:|
| 1 | `Hearing.ReferenceVoice` holds `NormalDb`, `BufferRmsDbfs` and `LevelDb`; `Speech`'s are those constants. `Loudness`, `EarModel` and `LoudspeakerChain` read them there | 608 to 596 |
| 2 | `BodyConstants` (Gravity, PersonHeight, WalkSpeed, SprintMultiplier, SprintSpeed); `PhysicsConstants`' five are those constants. `Glass`, `Breathing`, `EarWind`, `ExternalBallistics` read them there | 596 to 584 |
| 3 | `RoadSurfaces.Default`; `RoadData.DefaultSurface` is it. `WheelDynamics` and `EngineProcessor` read it | 584 to 580 |
| 4 | `WheelState.EncodeDemand`; `EntityState.EncodeTyreDemand` calls it | 580 to 578 |
| 5 | `Core.RenderRate.Default` (48000); `MixerQuality.DefaultRate` is it. Eleven synths' default rate argument | layering 53 to 31 |
| 6 | `AudioEmission` sorted into Sound (it reads the vehicle and machine presets); it stays in Common until stage 6 (it reads snapshots) | layering 31 to 26 |
| 7 | `PuddleField` takes `Carriageway`s (id, centreline points, width); `RoadData.ToCarriageway` builds one, sharing the centreline list | 578 to 566 |
| 8 | `TransientSound` and `SoundCharacter` into `TransientSound.cs`; `AudioEvents.cs` keeps `WorldAudioEvent` and is host | 566 |
| 9 | `WheelState` into `WheelState.cs` | 566 |
| 10 | `PlaybackMode` and `WeatherType` each into a file of its own, namespace `Components` unchanged | 566 |

The door fingerprint moved once in these fixes, with fix 2 (`Glass.cs` is one of its sources:
`f9ef642251b2` to `978619539e83`); no door render changed, the fingerprint test says so.

### 16.2 Found on the way: five more fixes

What had landed since the survey, or what the survey's counts could not see, and would have stopped the
projects compiling:

- **Weapons.cs** was mixed: the weapons' numbers the sound reads (`WeaponDefinition`, `FireMode`,
  `WeaponFeed`, `WeaponAction`, `WeaponRegistry`) and three game rules (`FireSelector`, `AmmoType`,
  `Ammunition`). The rules went to `Ammunition.cs` (host); `Weapons.cs` is all library and moved.
- **Vector3Converter** (`JsonConverters.cs`, host) was read by `ModelLibrary` (13.1 had left it for
  stage 3). Into `Vector3Converter.cs`, namespace unchanged, and moved with Sound. 565.
- **The fire models** (fire by fuel, new) took the entity model's `ColliderShape` to tell round from
  square. The ratchet never counted it (the enum is sorted as a value both need), but Sound cannot see
  Common. `FireShape.Footprint`, `FireSpec.KeyForPlaced` and `FuelCatalog.ForThing` take `bool round`;
  the host says what is round (`ColliderShapes.IsRound` in `Components.cs`: a cylinder, a sphere or a
  cone, as before). `ColliderShape` stays in Common (15.2).
- **LoudspeakerChain** (new) rendered through `MixerQuality.Resample` and `RadiatorBands`, both in the
  FMOD folder though neither touches FMOD. `SincResampler` (Core) holds the resampler, and
  `MixerQuality.Resample` calls it; `RadiatorBands` is in a file of its own beside the chain, namespace
  unchanged. Layering 26 to 18.
- **Constructions and LayeredFaces** (layered constructions, new) are what a wall or a floor lets
  through as one panel, over `WallTransmission`, and read no sound model. The default rule had put them
  in Sound, which left `OpeningRoutes` (Acoustics, stage 4) using Sound in 18 places. Sorted into
  Acoustics and moved with it. Layering 18 to 0.

### 16.3 What moved

By `git mv`, namespaces unchanged (decision 6), 165 files:

| From | To | Files |
|---|---|---:|
| `OpenFPS.Common/` (every file sorted into Sound but `AudioEmission.cs`; `Hearing/` and `Editing/` keep their folders) | `OpenFPS.Sound/` | 92 |
| `OpenFPS.Client.Core/AudioEngine/Core/` (the synthesis: every file but the five Audio ones) | `OpenFPS.Sound/Core/` | 61 |
| `OpenFPS.Common/` `AcousticConstants`, `AcousticRegistry`, `Diffraction`, `EarlyReflections`, `Enclosure`, `ImageSource`, `Localisation`, `PanelAcoustics`, `SparseAcousticOctree`, `WallTransmission`, `Constructions`, `LayeredFaces` | `OpenFPS.Acoustics/` | 12 |

Left behind on purpose: the rooms and openings (`AcousticMap`, `RoomAcoustics`, `OpeningRoutes`,
`Systems/FaceOpenings`, `Systems/AcousticVolumeGenerator`) for stage 4; `AudioEmission` for stage 6; the
five Audio files of `AudioEngine/Core` (`AmbisonicFormat`, `AudioBank`, `AudioEngineFacade`,
`DoorRenderCache`, `VoiceManager`) for stage 6. OpenFPS.Common is down to 35 files.

The projects, as section 6 drew them:

```
OpenFPS.Geometry <- OpenFPS.Acoustics <- OpenFPS.Sound <- OpenFPS.Common <- Server, Client.Core (-> Native)
```

- `OpenFPS.Acoustics` references Geometry; packages MemoryPack (the octree is inside `AcousticMap`'s
  `[MemoryPackable]`, decision 4) and Serilog (`AcousticRegistry` logs an unknown material).
  `InternalsVisibleTo`: `OpenFPS.Common` (`OpeningRoutes` calls `Diffraction.MinimiseOnEdge`; goes when
  it moves in stage 4) and `OpenFPS.Tests`.
- `OpenFPS.Sound` references Geometry and Acoustics; packages MemoryPack (`TransientSound`, decision 4)
  and Serilog. `InternalsVisibleTo`: `OpenFPS.Client.Core` (the rail voice drives `BogieVoice`,
  `BodyDrum` and `AxleSchedule`, the engine processor an aircraft's `BladeRow`; this becomes
  `OpenFPS.Audio` in stage 6), `OpenFPS.Tests`, `OpenFPS.AudioLab`. No `AllowUnsafeBlocks`: the synths
  use `stackalloc` into spans only.
- `OpenFPS.Common` references Geometry, Acoustics and Sound. The server references Common only, and
  never Native: it loads no FMOD.
- Nothing else changed its references: Client.Core, the clients, the lab and the tests get the new
  projects through Common.

### 16.4 The wire hash and the door fingerprint

- `WireContract.Hash` covers Common and, by `WireLibrarySource`, all of Geometry, all of Acoustics and
  all of Sound but `Core/`. Every file of Acoustics and of Sound outside `Core/` came from Common and was
  in the hash already; `Core/` came from Client.Core, only the client runs it, and it never was. The wire
  types that moved are all in it: `TransientSound`, `SoundCharacter`, `WheelState`, `PlaybackMode`,
  `WeatherType`, `Precipitation`, `WindAir`, `LightningStrike`, `FlashKind`, `RoadWater`, `DoorKind`,
  `CrowdApplause`, `AdminGunMode`, the weapons' numbers, and the models the server and the client both
  run (vehicles, wheels, doors, glass). Checked by appending a comment and building: to `Precipitation.cs`,
  `TransientSound.cs`, `WheelState.cs`, `AcousticRegistry.cs` or `KnobDoor.cs` changes the hash, to
  `Core/VehicleSynth.cs` does not, and taking the comment out gives the hash back. The hash moved with the
  stage (`d055f5fd65eb` at 17ca6c53 to `c98f59c6ec5c`), as it does with any edit to Common: a client and
  a server from either side refuse each other at login.
- The hashing task is in one place, `tools/build/SourceHash.targets`, imported by Common (the wire) and
  Sound (the doors). An item may carry `HashName` metadata to be hashed under that name wherever it is.
- `DoorModelFingerprint` is generated by `OpenFPS.Sound` now, beside the door models, in the same
  namespace. Its list names thirteen files of Sound and `..\OpenFPS.Acoustics\AcousticRegistry.cs`, each
  hashed under its file name (`HashName`), so the move did not change it: `978619539e83` before and after,
  and every player's door render cache stays good. Checked: a comment in `KnobDoor.cs` or
  `AcousticRegistry.cs` changes it; a listed file that is missing fails the build (`HashSources` cannot
  read it).
- `publish-windows.sh` and `run-gtk-client.sh` read the door fingerprint from `obj/OpenFPS.Sound/`;
  `publish-windows.sh` clears that folder as it clears Common's, so there is one of each.

### 16.5 The ratchet

| | Before (17ca6c53) | After |
|---|---:|---:|
| Crossings (references) | 608 | 565 |
| Allowance rows (file, host type) | 76 | 64 |
| Layering references (a lower project using a higher) | 53 | 0 |
| Files in `files.tsv` | 339 | 350 |

- `allowed.tsv` only shrank: every line that changed went down or went away, in the commit that fixed it.
  The move itself changed no line: no moved file had a crossing left.
- `files.tsv` is regenerated from the survey: the move changed 165 paths, and the fixes added eleven
  files (`ReferenceVoice`, `BodyConstants`, `RenderRate`, `TransientSound`, `WheelState`, `PlaybackMode`,
  `WeatherType`, `Ammunition`, `Vector3Converter`, `SincResampler`, `RadiatorBands`); `AudioEvents`,
  `Messages` and `Weapons` stopped being mixed, and `Components` holds one library value instead of three.
- `LibraryReferencesOnlyLibrary` has two more cases: Acoustics references only the runtime, Geometry,
  MemoryPack.Core and Serilog; Sound only the runtime, Geometry, Acoustics, MemoryPack.Core and Serilog.
- The survey reads the two new projects (`Program.cs`: their compilations, references and grants; the
  door fingerprint is generated into Sound's compilation, as the build does).

### 16.6 Checks

- Every project builds: Geometry, Native, Acoustics, Sound, Common, the server, Client.Core, the GTK
  client, the Windows client (`EnableWindowsTargeting`), the lab, the tests.
- The render fingerprint: 17 renders bit for bit after every step. The emitter streams: unchanged after
  every step. The ratchet: passes after every step with the counts above. None of the three was
  regenerated.
- The broad filter after the move (the guards and every test class whose name holds WireContract, Engine,
  Vehicle, Door, Rain, Weather, Fire, Water, Wind, Footstep, Loudness, Hearing, Ear, Acoustic, Wall, Panel,
  Material, Struck, Loudspeaker, GasHob, Train, Aircraft, Siren, Horn, Machine, Bird, Speech, Glass, Gun or
  Applause): 1,753 tests, 1,748 passed, 5 skipped, 0 failed (36 minutes on twelve cores).
  A second filter (ClientAudio, Geometry, SteamAudio, NetworkTrim, Opening, Reverb): 208 tests, 205 passed,
  2 skipped, 1 failed, the fire:campfire case that fails on main too (16.7).
- CI: `.github/workflows/tests.yml` builds `OpenFPS.Tests`, which brings the new projects in;
  `tools/ci/shard_tests.py` deals out test classes, which did not change. Nothing to edit. The publish
  scripts build their executables' projects, which reference the new ones through Common.

### 16.7 Left for stage 4

- Rooms and openings as values (`Room`, `Opening`), then move `AcousticMap`, `RoomAcoustics`,
  `OpeningRoutes`, `FaceOpenings` and `AcousticVolumeGenerator` into Acoustics. They hold 152 of the 565
  references left (`AcousticVolumeGenerator` 120, `RoomAcoustics` 15, `OpeningRoutes` 15, `AcousticMap` 2);
  `OpeningGraph` (50) and `FmodAudioProvider`'s 19 take the same values. When `OpeningRoutes` moves,
  Acoustics' grant to Common goes.
- `AcousticMap` and `SparseAcousticOctree` keep `[MemoryPackable]` (decision 4 said it can probably go:
  nothing sends them). Deciding that would let Acoustics drop MemoryPack.
- `ColliderShape` stays in Common (15.2); since 16.2 no library file needs it but the three stage 6
  readers of `ColliderComponent`.
- The rest of the 565 belongs to stage 6 (the world input): `RainField` 104, `SpatialAcoustics` 73,
  `AsyncAcousticWorker` 50, `SteamAudioScene` 27, `CabinWalls` 20, `AudioEmission` 18, `EarlyCopies` 17,
  `TalkerVoice` 16, `VehicleShadow` 15, `EngineReflections` 4.
- Unrelated, seen while checking: `ClientAudioSelectionTests.EveryKindOfPhysicalSourceIsPlacedAtItsOwnDeclaredLevel("fire:campfire")`
  fails on main as here: the test places the fire on a 4 m box, `KeyForPlaced` keys it as a bigger fire
  (`fire:campfire/shape=r4x4`, 78 dB) and the test expects the preset's declared 63 dB.

---

## Decisions (Cody, 2026-10-06)

- Five projects: Geometry, Acoustics, Sound, Native (FMOD and Steam Audio bindings), Audio (the runtime and
  the host-neutral world). FMOD stays out of the server.
- Vehicle physics goes in Sound, as Resonance did, on the condition that it stays physical: no shortcut
  that trades realism for convenience moves with it.
- Start stages 0 to 2 (guards, Native, Geometry) right after geometry stage 2 lands, so the two do not
  collide.
- Resonance takes the library as a git submodule pinned to tagged versions.
- The other defaults stand: namespaces unchanged during the moves, Serilog kept, a library SourceSpec
  that mirrors SoundEmitterComponent.
- Why (Cody, 2026-10-07): "we're separating concerns in the code not just for resonance's use, but because
  it's just good coding practice." The split is judged as open-fps's own architecture: clear boundaries and
  one-way dependencies between the projects, whatever Resonance ends up consuming.
