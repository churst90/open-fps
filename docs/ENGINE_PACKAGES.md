# The engine packages

A survey of what is still to be separated in open-fps's engine, beyond what the sound library and the physics
surveys already plan, and the order to do it in. No engine code was changed. Asked by Cody on 2026-10-11: "all
parts of this game engine need to be separated in their own packages and all work cohesively together". The
models for method and depth are docs/SOUND_LIBRARY_BOUNDARY.md and docs/PHYSICS_BOUNDARY.md.

- Surveyed at main `6dce0ba6`, 2026-10-12, with physics stage 1 and the first half of geometry stage 6 in.
- The counts come from `tools/engine_packages/run.sh`: the sound boundary's Roslyn reader
  (`tools/sound_boundary/Program.cs`, which now also parses `OpenFPS.Words` and `OpenFPS.Astronomy`) and a
  classifier of its own (`tools/engine_packages/classify.py`). It writes `packages_tables.md`,
  `packages_files.tsv` and `packages_crossings.tsv` into the output folder, and the ratchet's four lists into
  `ratchet` there (stage 0). Run it again after any move.
- Already packages: Geometry, Acoustics, Sound, Native, Physics, Schema, Words, Astronomy; Materials since
  stage 1 was built (2026-10-12), Models since stage 2, Accessibility, its clean half, since stage 7,
  World since stage 4, Environment since stage 3 and Editor since stage 8 (2026-10-16). Planned with their
  own documents and not planned again here: `OpenFPS.Audio` and the split of `ClientAudioSystem` (sound library
  stages 6 to 8), and physics stages 2 to 6. This survey says where its packages meet them.

Where it stands (2026-10-12): surveyed; the decisions in section 13 are Cody's to take.

## Contents

1. Summary
2. Method
3. The packages
4. The layers
5. The crossings
6. Global state
7. Files, paths and environment
8. The wire hash
9. The stages
10. How it fits with the physics and library stages
11. Enforcement
12. Risks
13. Decisions for Cody
14. What was not measured
15. Appendix: every file of the new packages

---

## 1. Summary

- 534 source files in eleven projects were sorted, every one to a package or to the game. Seven new packages
  take 27,932 lines: Materials 775, Environment 5,688, Models 2,195, World 7,872, Agents 2,493,
  Accessibility 5,561, Editor 3,348. `OpenFPS.Common` keeps 2,737 lines: the wire and the entity model. The
  game keeps 51,972 lines, 21,000 of them the hosts that step a package (the server's systems, the world
  editor's commands, the map manager).
- The material table has no outbound reference at all. `MaterialProperties` and `AcousticRegistry` (729
  lines in `OpenFPS.Acoustics/AcousticRegistry.cs`) and the road surfaces' grip (`RoadSurfaces`, 46 lines in
  `OpenFPS.Physics/Chassis.cs`) can move to a bottom package that Physics and Acoustics both reference. It has
  no friction or restitution yet; physics stage 4 needs both. Beside it are another surface table in Physics
  (`RoadGripLaw`), a friction and restitution per prefab in Common that nothing reads, and four places that
  still test a material by its name.
- The model catalogue (`MachineRegistry`, `ModelLibrary`, `ModelKindTable`, the editor's `ModelKinds`, the
  voices of `Speech`) belongs above Sound: it hands out Sound's presets. Sound calls back into it 95 times from
  16 files: half are a preset's static lookup asking the library, the rest reads of a machine's parts and kind
  checks. Those turn round.
- Weather, water and fire as world state and laws (12 files of Sound, 3 of the server) form one package,
  Environment, between Physics and Sound. 38 references cross, all small. The atmosphere is written three
  times: Physics' `Air` (`ExternalBallistics.cs:8`), Thunder's own `Air` (`Thunder.cs:25`), and
  `WorldEnvironmentComponent` with `MapAtmosphere` (`Components.cs:413`, `WorldEnvironmentSystem.cs:385`).
- The world (map and prefab formats, roads, tiles, terrain, the real-world sources and the on-demand tile
  generator) is 26 files with 36 crossings, counted with `EntityType` already moved down to it. The Python
  generators (8,160 lines) are kept in step with their C# port by hand: `WorldBuildings.cs` is `gen_osm.py`'s
  `build()`, and names it 66 times.
- Traffic's laws are nearly clean (the following law, junctions, crossings, routes, street life: 22
  crossings), but they are parts of the partial class `VehicleSystem`. The people (Alex's day, haunts, door
  manners, pavements, pedestrian speech) read the entity store on almost every line and stay hosts until
  physics stage 3 gives a world view.
- The accessibility interface is the biggest job: 598 of its 703 crossings are snapshot reads from ten files
  (`MapTracker`, `SightGrid`, `BeaconAids`, `DrivingAids`, `SightWatch` and five more). They are the same six
  types the audio reads in library stage 6's way (`WorldSnapshot`, `EntitySnapshot`, `EntityDefinition`,
  `Transform`, `ColliderComponent`, `IdentityComponent`). One scene view should serve both.
- The editor's model (dialogs, kinds, overlays, versions, model store: 12 files) separates with 33
  crossings; its commands (16 partial files of `WorldEditor`, 7,926 lines) read sessions and maps and stay.
- Combat, roles, permissions, bans, teams, commands, accounts, persistence and sessions stay in the game
  (section 3.10).
- Proposed: eleven stages, 17 to 25 sessions, each guarded by a new packages ratchet, a new spoken-lines
  replay for the aids, and the existing guards. Materials and the catalogue go before physics stage 2; the
  second half of the aids waits for library stage 6's world input; the people wait for physics stage 3.

---

## 2. Method

The sound boundary's reader parses every `.cs` file of the projects, binds them, and writes every type, every
reference from one repository type to another (file, line, member), every member, every static that can
change, and every file, folder and environment use. It was given two more projects for this survey,
`OpenFPS.Words` and `OpenFPS.Astronomy`, so the server's and the client's references to the compass words
bind (Client.Core went from 4 binding errors to none, the server from 31 to 11). Unknown projects sort as host
in the other two classifiers, and both reproduce their checked-in lists unchanged
(`physics_crossings.tsv` exactly; the library's `files.tsv` and `allowed.tsv` but for a trailing tab on four
rows). Built with `tools/build-local.sh`; nothing in the repository was built.

`tools/engine_packages/classify.py` (standard library only, run with `python3 -I`) holds the sorting in
three tables:

- `ALLOWED`: what each package may reference. The layers of section 4 are computed from it, and a cycle stops
  the run.
- `FILE_RULES`: a file pattern, its package, and why. The first match wins; a file no rule matches is
  reported as unsorted (none are).
- `TYPE_RULES`: the types that do not go where their file goes (the material table out of Acoustics,
  `EntityType` out of `Components.cs`, the keys out of the client's input).

A reference is counted from the package of the file it sits in, so a partial class splits by file, as an
extraction would split it; it is counted to the package of the type it names. A crossing is a reference from a
package to one its layer does not allow; references from the game, the lab and the tests are not crossings.
Where the physics or library survey already places a file (`PhysicsUtils`, `WheelDynamics`, the audio engine),
it is sorted where that survey puts it, and its crossings are listed under that survey's stage.

Packages:

| Package | Kind | Files | Types | Lines |
|---|---|---:|---:|---:|
| Words | exists | 2 | 3 | 142 |
| Schema | exists | 2 | 3 | 225 |
| Native | exists | 9 | 246 | 8,117 |
| Geometry | exists | 28 | 70 | 8,772 |
| Materials | new | 0 (types from 2 files) | 7 | 775 |
| Astronomy | exists | 6 | 13 | 696 |
| Physics | exists, with physics stage 2's moves | 13 | 24 | 3,491 |
| Acoustics | exists | 20 | 25 | 7,783 |
| Environment | new | 15 | 46 | 5,688 |
| Sound | exists | 130 | 276 | 52,016 |
| Models | new | 7 | 18 | 2,195 |
| Audio | planned (library stage 6) | 68 | 142 | 27,416 |
| World | new | 26 | 71 | 7,872 |
| Common | exists, slimmed | 11 | 94 | 2,737 |
| Agents | new | 10 | 11 | 2,493 |
| Accessibility | new | 22 | 37 | 5,561 |
| Editor | new | 12 | 39 | 3,348 |
| game | stays in open-fps | 153 | 171 | 51,972 |

Lines are whole files under a file rule; a type moved by a type rule counts its declaration span against its
new package and is taken off its file's.

What the reader cannot see: references by string (material names, model ids, sound ids, JSON keys), delegates
handed in from elsewhere, and the Python generators. Lines are declaration spans with comments.

---

## 3. The packages

| Package | One line |
|---|---|
| Materials | What each material is: one table of physical properties, read by physics, acoustics, sound, environment and the world |
| Environment | The weather, the water on the ground and the roads, and fire, as world state and the laws that move it |
| Models | The catalogue of what things are: machines, vehicles, trains, horns, water, fire and voices by id and kind |
| World | Maps and the one world: the map and prefab formats, roads, tiles, terrain, real-world sources and the tile generator |
| Agents | What drives things and people: drivers on lines, traffic's laws, aircraft on runways, a machine's controls |
| Accessibility | How a player who cannot see is told the world: beacons, aids, sight, stairs, menus, interface sounds, speech, keys |
| Editor | The world editor's model: dialogs, the kinds it edits, overlays, versions and the model store |
| Common | The wire and the entity model, nothing else |
| game | open-fps itself: combat, roles, bans, teams, commands, accounts, sessions, and the hosts that step the packages |

### 3.1 Materials

- **What goes in.** From `OpenFPS.Acoustics/AcousticRegistry.cs` (752 lines), all six types: `MaterialProperties`,
  `AcousticRegistry`, `Underfoot`, `StrikeKind`, `StrikeBehaviour`, `RicochetFace` (729 lines). From
  `OpenFPS.Physics/Chassis.cs:243`, `RoadSurfaces` (46 lines): the surface byte a wheel sends and its grip
  against dry asphalt and stick-slip, by material name. 7 types, 775 lines.
- **Layer 0.** It references nothing: the reader finds no outbound reference from any of the seven types.
- **Who uses it.** Sound 352 references, Acoustics 135, Physics 8; the server 81, Client.Core 94, the lab
  282, the tests 771. Resonance through Acoustics and Physics.
- **Crossings to fix first.** None out of it. The work is the other tables and the name tests:

  | What | Where | What it should read |
  |---|---|---|
  | Tyre grip and stick-slip by material name, 7 rows | `RoadSurfaces`, `Chassis.cs:255` | the material's friction against rubber; the byte stays the wire's index |
  | Texture depth, Manning's n, wet grip ratio by surface index, 7 rows | `RoadGripLaw.Table`, `RoadGrip.cs:29` | fields of the material (texture depth and roughness are surface properties); the law stays in Physics |
  | Mass, friction, restitution, drag per prefab, read by nothing | `PhysicsPropertyComponent`, `Components.cs:406` | the parts' materials and volumes (physics stage 4) |
  | Fuel by material, kind and size | `FuelCatalog`, `Fuel.cs:86` | the table's fuel fields (`PyrolysisC`, `HeatOfCombustionMJKg`), which are there and empty |
  | Footstep bank folder by material | `SoundMappingService.RecordedStandIn`, `SoundMappingService.cs:35` | stays: it is a choice of recordings, not a property |
  | A material's spoken name ("sidewalk", "road", "metal grating") | `ClientAudioSystem.cs:1039-1046` | a word the material table or Words carries |
  | "Asphalt" means a road | `DrivingAids.cs:425`, `:895` | the road network, or the surface's road flag |
  | "Foliage" means a canopy | `SurfaceRaster.cs:36` | `MaterialProperties.Leaves` |
  | "Glass" means a window | `WorldEditor.Build.cs:182` | the family (`AcousticRegistry.IsGlass`) |

- **What it lacks.** Friction (static and sliding) and restitution, which physics stage 4 reads for every
  contact ("restitution and friction from the material table", docs/PHYSICS_BOUNDARY.md stage 4). Each wants a
  source, as every other column of the table has (docs/MATTER.md 2.4).
- **Statics.** The published dictionary (`AcousticRegistry._registry`, assignable, rebuilt and swapped by
  `Initialize`), the aliases, the set of names warned about, and `RoadSurfaces`' two tables. A table filled
  once; no world state.
- **Files.** None read.
- **Wire.** `AcousticRegistry.cs` is hashed today (it is in Acoustics); `RoadSurfaces`' byte is in
  `WheelState.Surface`. Materials must be in the wire hash. `DoorModelFingerprint` lists
  `..\OpenFPS.Acoustics\AcousticRegistry.cs` by path (`OpenFPS.Sound.csproj:57`), which must follow the move.
- **Risks.** The door renders are keyed on the fingerprint: a moved path renews the disk cache once. The
  render fingerprint and the emitter-stream replay must not move (`AcousticRegistryTests` pins every old row).

### 3.2 Environment

- **What goes in.** From Sound: `WeatherType`, `Precipitation` (with `Hydrometeors`, `ParticleSpectrum`),
  `Rain` (rates and drop sizes), `Lightning` (strikes, schedule, channel), `Runoff`, `GroundWater` (land
  cover, TR-55 hydrology, catchments), `RoadWater` (drainage, the road's water, puddles), `Fire`, `FireShape`,
  `FireSpread`, `Fuel`, `WorldWeather`; and by type `WindWaves` and `WaterBody` from `Waves.cs` and
  `GroundChannelKind` from `GroundChannels.cs`. From the server: `Water/Drainage.cs`, `Water/DrainageNetwork.cs`,
  `Systems/GroundWaterSystem.cs`. 15 files, 46 types, 5,688 lines.
- **Layer 2**, on Words, Schema, Geometry, Materials, Astronomy and Physics. The wind stays in Physics
  (`WindField`, physics stage 5's moving air); Environment holds the weather that drives it. Sound reads
  Environment (424 references) and never the other way.
- **Who uses it.** Sound 424, the server 561, Client.Core 184, the lab 941, the tests 1,685. Resonance has its
  own weather (`Resonance.Audio/Weather`, library survey 3.1) and would take the laws.
- **Crossings to fix first** (38):

  | From | To | Refs | Lines | Fix |
  |---|---|---:|---|---|
  | `DrainageNetwork.cs`, `GroundWaterSystem.cs` | `GroundChannels` (Sound) | 16 | `DrainageNetwork.cs:164, 463, 534, 558, 561`; `GroundWaterSystem.cs:488-489` | split `GroundChannels`: the bed's hydraulic geometry (`WidthFor`, `ReferenceFlow`) down with `GroundChannelKind`; the running-water voice spec stays in Sound |
  | `Fuel.cs`, `Fire.cs`, `FireSpread.cs`, `DrainageNetwork.cs` | the catalogue | 16 | `Fuel.cs:229, 235`; `Fire.cs:548, 583`; `FireSpread.cs:700`; `DrainageNetwork.cs:164` | kind checks ("is it a vehicle") and preset lookups turn round: the caller passes the kind or the spec |
  | `Lightning.cs` | `AcousticConstants.SpeedOfSound` | 4 | `:171`, `:180` | the speed of sound from Physics' `Air` (the one law since the audit's P8) |
  | `Precipitation.cs` | `RainSurfaces` (Sound) | 2 | `:166` | pass the surface's share in |

- **The hosts that stay.** `WorldEnvironmentSystem` (407 lines: game time, seasons, the fronts; 62 references
  to `WorldEnvironmentComponent`), `LightningSystem`, `RoadWaterSystem`, `FireSystem`, `MapDrainage`,
  `SurfaceRaster`. They step Environment over the entity store. In the first pass, with them inside, 197
  references went from them into Common.
- **One atmosphere.** Physics' `Air` (temperature, pressure), Thunder's `Air` (temperature, humidity,
  pressure, wind, gustiness) and `WorldEnvironmentComponent` (game time, day, temperature, humidity, pressure,
  air absorption multiplier, wind, gustiness, precipitation) say the same things three ways. Environment owns
  one atmosphere value; the component carries it on the wire; Physics' laws take it.
- **Real weather** (docs/REAL_WEATHER.md 8.1) plugs in at Environment's seam: "one call made by the host each
  tick or second: it hands the per-world weather the conditions". The conditions service (Open-Meteo, METAR) is
  the server's, beside World's other network sources; `WorldEnvironmentSystem`'s fronts become one more
  conditions source. Astronomy is allowed below it for the sun in the day's curve; nothing reads it yet.
- **Statics** (11 mutable): `GroundHydrology.SoilGroup` (assignable, `GroundWater.cs:45`, the audit's S31: two
  worlds share one soil), and `GroundWaterSystem._maps` and `_frames` (`GroundWaterSystem.cs:86`, `:104`): every
  map's water in one static dictionary. These become per world, as library stage 5 made the run-off and the wind.
  The rest are tables.
- **Files.** None.
- **Wire.** 12 of its 15 files are hashed today; `WeatherType`, `Precipitation`, `LightningStrike`, `RoadWater`
  and the ground water field are on the wire. Environment must be in the hash.
- **Risks.** Approved sound reads these values (rain, thunder, running water, fire). Moving them must not move a
  sample: the render fingerprint (rain on three surfaces, thunder) and the emitter-stream replay
  (`traffic_rain`) say so.

### 3.3 Models: the catalogue

- **What goes in.** `Machines.cs` (machines as parts, `MachineRegistry`), `ModelKindTable.cs` (the one table of
  kinds, audit P2), `ModelLibrary.cs` (the models a map can name), `Editing/ModelKinds.cs` (each kind's fields
  for the editor), `Speech/Speech.cs` (the voices and what each can say), `Scopes.cs` (what a scope is; the
  scope's maths and sway stay in the game), `Ammunition.cs`. 7 files, 18 types, 2,195 lines.
  `Editing/FieldDescriptor.cs` (193 lines) goes to Schema, which the editor and the catalogue both read.
- **Layer 4**, above Sound and Physics: it hands out Sound's presets (515 references into Sound:
  `VehicleProfile` 147, `VehicleBody` 57, `TyreProfile` 35, `TrainProfile` 27 and on) and, after physics stage
  2, Physics' mechanical halves.
- **Who uses it.** The server 670, Client.Core 331, the lab 351, the tests 1,009; the editor's model 104,
  Audio 100, Accessibility 26, World 12, Agents 7, Common 4.
- **Crossings to fix first.** None out of it. Sound calls into it 95 times from 16 files; these turn round:

  | Kind | Refs | Lines |
  |---|---:|---|
  | A preset's static lookup asking the library | 48 | `Engines.cs:2110`; `Trains.cs:389, 395, 400`; `TrainSynth.cs:214, 225, 237`; `Loudspeakers.cs:283-284`; `NatureModels.cs:217, 320`; `GasHob.cs:528`; `RunningWater.cs:610`; `SmallMachines.cs:402`; `Waves.cs:514`; `CabinPaths.cs:161, 164`; `Honk.cs:69` |
  | Reading a machine's parts | 35 | `VehicleBodyShell.cs:37-76` (21); `VehicleSpec.cs:91-124` (10); `Glass.cs:95-96` (4) |
  | A kind check | 12 | `CabinPaths.cs:174-176`; `Honk.cs:72-74`; `ExtendedSources.cs:78`; `TeleporterSounds.cs:55` |

  The fix for each: the caller that has the catalogue hands the preset, the parts or the kind in. The shell
  (`VehicleBodyShell`, geometry stage 6's first half) takes the machine's parts as an argument.
- **Weapons stay in Sound.** `WeaponDefinition` is read by Sound 169 times (the crack, the handling, the strikes,
  the glass). It is a preset like the engines; `WeaponRegistry` joins the catalogue when the presets split.
- **Statics** (32 mutable): the registries' loaded and authored sets (`MachineRegistry._authored`,
  `_loadedFrom`, `_assembled`, `_engineKeys`, `_seenGeneration`; `ModelLibrary._authored`, `_loadedFrom`,
  `_generation`; `ModelKindTable._bodies`, `_bodyGeneration`) and Speech's five lazy caches
  (`Speech.cs:135-214`, without locks: the audit's S31). Library stage 5 already names a `ModelCatalog`
  instance for the registries; this is where it lives.
- **Files.** `machines/*.json` and `models/*.json` relative to the working directory (`Machines.cs:183`,
  `ModelLibrary.cs:203`), loaded from `Server/Program.cs:430-431` and `ClientGameSession.cs:126-127`; the
  library writes exports (`ModelLibrary.cs:358`); `voices.csv` and `scripts.json` are embedded in Common
  (`OpenFPS.Common.csproj`). The host names the folders (library survey 7.4).
- **Wire.** All 7 files are hashed today: a server and a client must resolve a model id the same way. Models
  must be in the hash.

### 3.4 World

- **What goes in.** From Common: `Roads.cs`, `RoadMapData.cs`, `Tiles.cs`, `TrackClearance.cs`. From the server:
  `OneWorld/*` but `WorldMaps.cs` and `WorldPlaces.cs` (OpenStreetMap, Overture, USGS 3DEP elevation, ESA
  WorldCover land cover, the Census geocoder, UTM, the buildings and features of a world tile, the tile store
  and the on-demand tile service), `MapRepository.cs` (the map file format), `PrefabTemplate.cs` and
  `PrefabValidator.cs` (the prefab format), `CompositeRepository.cs` (composite templates and placements),
  `MapElevation.cs`, `MapRoadsBuilder.cs`, `TerrainBuilder.cs` (its grading). By type: `EntityType` from
  `Components.cs`, `WorldPlace` from `WorldMaps.cs`. 26 files, 71 types, 7,872 lines. The JSON vector
  converters go to Geometry.
- **Layer 5**, above Sound and Models: a prefab names a door's events, a glass type and a playback mode, and a
  map names models. Below Common, because the roads message and the tile states are World's values on the wire.
- **Who uses it.** The server 2,869, Client.Core 113, the lab 420, the tests 3,970; Agents 282, the editor's
  model 119, Common 7. Resonance: not known (section 14).
- **Crossings to fix first** (36):

  | From | To | Refs | Lines | Fix |
  |---|---|---:|---|---|
  | `TerrainBuilder.cs` | entity components, `TerrainTiles` | 23 | `:99-106`, `:144`, `:255-256` | the grading stays; making the terrain's entities moves to the host |
  | `WorldTile.cs` | `TerrainTileComponent` | 7 | `:56`, `:58` | a tile keeps its ground as a World value; the component wraps it |
  | `WorldFeatures.cs`, `WorldTileService.cs` | `SurfaceRaster` (server) | 4 | `WorldFeatures.cs:431`; `WorldTileService.cs:314` | rasterise from boxes, not entities, and move it in |
  | `MapRepository.cs` | `Transform` | 2 | `:104` | the map format's own pose |

  Before these: `EntityType` down to World (an enum: MemoryPack's bytes do not change, as `ColliderShape`'s move
  showed in physics stage 1), and `Vector3Converter` and `QuaternionConverter` to Geometry.
- **The hosts that stay.** `MapManager` (1,333 lines), `PrefabRepository` (loads the prefab files and makes
  entities: the loading half moves when the entity making is split off), `MapTiles`, `TerrainTiles`,
  `TileStreamer`, `WorldMaps` (sessions arriving), `WorldPlaces`, `MapTemplates`.
- **The generators.** Python, not parsed: `gen_osm.py` 4,210 lines, `gen_city.py` 2,525, `gen_speedway.py` 577,
  `fetch_place.py` 567, `mapgen.py` 203 (the prefabs' dimensions both read), `utm.py` 78. `WorldBuildings.cs`
  is `gen_osm.py`'s `build()` ported ("keep the two the same"; it names `gen_osm` 66 times, `WorldFeatures.cs`
  29, `WorldFeatures.Buildings.cs` 21); `Utm.cs` and `utm.py` are kept in step by hand. `city.json` and the real
  places' maps come from the generators and are never edited (memory "city-json-is-generated"); the maps
  folder is 45 MB. Decision 6.
- **Statics** (19 mutable): tables, the shared HTTP clients, and `OverpassRegions._lastAsked` (`Osm.cs:70`), when
  Overpass was last asked, process-wide on purpose (one server, one rate limit).
- **Files.** 27 literal paths and 98 file and folder calls: the tile store (`ROOT/tiles/v{generator}/...`,
  `WorldStore.cs`), `world.json` (`WorldSettings.cs`), the maps and prefabs folders, `world_places.json`. The host
  must name the roots.
- **Wire.** 4 of 26 files are hashed today (Common's). Roads, the roads message and tile states are on the wire;
  World must be in the hash.
- **Risks.** The map tests (`RealPlaceMapTests`, `MapSurveyTests`, `WorldBuildingsTests`, `MapScopedStateTests`)
  and the movement replay (terrain under walks) hold the behaviour; `city.json` must regenerate byte-identical.

### 3.5 Agents

- **What goes in.** Clean now: `LineFollower`, `RaceLine`, `LaneRoutes` (Common), `AircraftGroundRun` and
  `MachinePower` (Sound). Extracted from the partial class `VehicleSystem`: `Following` (the Intelligent Driver
  Model), `Junctions` (who goes first), `Crosswalks` (where a walker crosses), `Routes` (tours of lanes) and
  `StreetLife` (the street's random events), with the parking state (`ParkState`, `ParkPhase`, `ParkingSpot`
  from `Parking.cs`) and the street's lines (`StreetLines`, `SpeechConditions` from `PedestrianSpeech.cs`).
  10 files, 11 types, 2,493 lines.
- **Layer 6**, on World (the road network) and Physics; Sound for the machines' presets.
- **Who uses it.** The server 383, the lab 87, the tests 489; Accessibility 21 (the driving cues plan on the racing
  line). Resonance keeps its own driver (library survey 3.1).
- **Crossings to fix first** (22 from Agents, and 2 into it from Common):

  | From | To | Refs | Lines | Fix |
  |---|---|---:|---|---|
  | `StreetLife.cs`, `Crosswalks.cs`, `Following.cs` | `Transform` | 10 | `StreetLife.cs:117, 169`; `Crosswalks.cs:342-343`; `Following.cs:126, 130` | positions passed in |
  | `PedestrianSpeech.cs` (`SpeechConditions`) | `WorldEnvironmentComponent` | 4 | `:25-26` | Environment's atmosphere value |
  | `Crosswalks.cs`, `Routes.cs` | `MapManager` | 4 | `Crosswalks.cs:62, 64`; `Routes.cs:24, 26` | the road network passed in |
  | `Following.cs` | `RoadWaterSystem` | 2 | `:64` | Environment's `RoadWater` |
  | `Parking.cs` (`ParkState`) | `DoorManners` | 2 | `:46` | a door's state passed in |
  | `Messages.cs` (Common) | `MachinePower` | 2 | `:601` | the lever's byte encoding moves to `StatePacking` |

- **The hosts that stay.** `VehicleSystem.cs` (1,048 lines) with `Parking`, `Casualties`, `Drivers`,
  `AdminGun`; `CharacterSystem` (740: Alex), `CrossingSystem`, `CrowdSystem`, `DoorManners`, `Haunts`,
  `Pavements`, `PedestrianSpeech` (821 and 492), `RailSystem`. In the first pass, with them inside, the
  package had 770 crossings: 656 into Common (`Transform` 210, `ColliderComponent` 85, `Velocity` 75) and 110
  into the server (`MapManager` 55, `DoorSystem` 30). Their decisions (where Alex goes, which door is a front
  entrance, the pavement graph from slabs named as pavements) move once they read a view of people, doors and
  places instead of the store; that view is physics stage 3's `IPhysicsWorld` extended, not a second one.
- **Statics.** Two tables (`DriverSteering`).
- **Wire.** Nothing of it is on the wire but `MachinePower`'s encoding, which goes to Common. Agents need not be
  in the hash once that moves.
- **Risks.** `VehicleSystem` is one tuned class over ten files. Extraction must leave the traffic replay
  (`DrivingReplayTests`' traffic track), `RailReplayTests`, `TrafficRuleTests`, `RouteTrafficTests` and the
  emitter-stream replay's `traffic` and `traffic_rain` unchanged.

### 3.6 Accessibility

- **What goes in.** From Client.Core: `BeaconAids`, `DrivingAids`, `DrivingCueSounds`, `StairCues`,
  `TurnNarration`, `Sightline`, `SightGrid`, `SightWatch`, `NamedPlaces`, `ListMenu`, `UiSounds`, `WallBumps`,
  `ZoneSettle`, `MapTracker`, `ListeningCalibration`, and from `Platform/` the speech output (`ISpeechOutput`,
  `SpeechDispatcherOutput`, `SpeechLog`). From Common: `Beacons`, `DrivingCuePlanner`, `LaneGuide`,
  `PlayerCoordinates`. By type: `GameKey` and `KeyModifiers`, the keys the game knows (memory "screen reader
  keys are not game keys"). 22 files, 37 types, 5,561 lines. `DirectionWords` (Common) goes to Words.
- **Layer 7**, on Audio (the cues are voices), World, Agents and everything below; never on Common.
- **Who uses it.** Client.Core 678, the Windows head 145 and the GTK head 215 (the keys), the server 92
  (beacons, coordinates), the tests 1,980; the editor's model 43. Resonance is meant to (Cody's list).
- **Clean now** (0 crossings, 12 files): `DrivingCueSounds`, `TurnNarration`, `ZoneSettle`, the speech output,
  `Beacons`, `LaneGuide`, `PlayerCoordinates`, `DrivingCuePlanner`, and, once the keys and `Speech` move,
  `ListMenu` and `ListeningCalibration`.
- **Crossings** (703):

  | Kind | Refs | Files and lines | Fix |
  |---|---:|---|---|
  | Snapshot reads | 598 | `MapTracker.cs` 103 (`:103-145`); `SightGrid.cs` 96 (`:44-55`); `BeaconAids.cs` 90 (`:96-175`); `DrivingAids.cs` 74 (`:176-202`); `SightWatch.cs` 68 (`:80`, `:289-305`); `Sightline.cs` 48 (`:26-46`); `WallBumps.cs` 37 (`:76-141`); `UiSounds.cs` 30 (`:62-71`); `NamedPlaces.cs` 29 (`:21-31`); `StairCues.cs` 23 (`:84-158`) | one scene view (decision 8), with library stage 6's world input |
  | The client's contact | 30 | `WallBumps.cs:39-76` (`BodyContact`) | physics stage 3's `ContactEvent` |
  | The client's settings | 24 | `DrivingAids.cs:176-181` (`DrivingCues`) | Accessibility's own options |
  | The scope's categories | 31 | `MapTracker.cs:235-245`; `Sightline.cs:92-153` (`SightKind`, `ScopeView`) | a category source the game registers |
  | The client's queries | 10 | `SpatialService` from `Sightline.cs`, `SightGrid.cs:71`, `BeaconAids.cs:151` | Geometry's rays (library stage 6 moves the acoustic ones) |
  | Others | 10 | `LocalPlayerState` 6 (`DrivingAids.cs`), `OtherBodies` 4 (`BeaconAids.cs:108, 294, 307`) | through the view |

  `UiSounds` maps chat and presence messages to cues; the mapping stays in the session and the cue meanings move.
- **Statics.** Nine tables (timbres, brake sounds, ring orders).
- **Files.** `BeaconPreferences` (`BeaconAids.cs`) reads and writes the player's `beacons.json` in the settings
  folder (the folder a mutation run once wrote over: memory "mutation runs touched real config"). The host names
  the folder: `SettingsFolder` is the one place that says where it is, and `BeaconPreferences.Load(path)` takes
  any other file.
- **Wire.** 4 files are hashed today, only because they are in Common; none is on the wire. Accessibility need
  not be in the hash.
- **Risks.** These are the most heard and least measured parts of the client: nothing replays what is said.
  Stage 0 adds a spoken-lines replay before any of them moves.

### 3.7 Editor

- **What goes in.** From the server: `Editor/EntitySettings.cs`, `LiveState.cs`, `MapOverlay.cs`,
  `MapVersions.cs`, `ModelCatalog.cs` (the kinds the editor edits), `ModelStore.cs`, `OverlayRoute.cs`, and
  `Core/BuildSession.cs`. From Client.Core: `EditorDialog.cs` (1,438), `EditorKeys.cs`, `EditorValuePrompt.cs`,
  `BuildForm.cs`. 12 files, 39 types, 3,348 lines.
- **Layer 8**, the top of the packages: it reads Common (the editor's messages are there), World, Models,
  Schema and Accessibility (the keys).
- **Who uses it.** The server 1,610 (the commands), the two heads 232 and 248 (each draws the dialog), Client.Core
  60, the tests 799. open-fps only.
- **Crossings to fix first** (33):

  | From | To | Refs | Lines | Fix |
  |---|---|---:|---|---|
  | `ModelCatalog.cs` | `PrefabRepository` | 18 | `:86-102` | World's prefab loading, once the repository is split |
  | `MapOverlay.cs` | `WorldEditor`, `MapManager`, `MapSettings` | 9 | `:66`, `:242`, `:275`, `:289-294` | the map's data passed in |
  | `LiveState.cs` | `Pose`, `DoorSystem` | 6 | `:25`, `:27`, `:45` | `Pose` moves with it; a door's state passed in |

  `ModelStore.cs:60` read `SafeText`, which goes to Words.
- **The host that stays.** `WorldEditor`, 16 partial files, 7,926 lines: the menus and commands over sessions and
  maps (in the first pass, 536 references to `UserSession`, 212 to `MapManager`, 42 to `Permissions`).
- **Statics.** `EditorKeys.Enabled`, written from four places outside it: a setting, to become an option.
- **Files.** The map overlays and their versions (`MapOverlay.cs:121-210`, `MapVersions.cs:138-140`), model
  versions (`ModelStore`). The host names the folders.
- **Wire.** None of its files is hashed today; the editor's messages are Common's.

### 3.8 Common

What is left: `Messages`, `Components`, `StatePacking`, `AudioEvents`, `JsonConverters`, `WorldSnapshot`,
`DistantMotion`, `UpdateThrottle`, and the entity adapters (`AudioEmission`, `EntityAcoustics`, `EntityGeometry`).
11 files, 2,737 lines, no mutable static. `PhysicsUtils` and the body half of `PhysicsConstants` go in physics
stage 2; `GroundProbeMemo` and `MoverPoses` are the server's. Two package references are left with nothing to
use them: Arch is used only by `PhysicsUtils.cs`, and LiteNetLib by no file of Common (the server and the client
each reference it themselves).

### 3.9 Where the existing packages meet these

- **Schema** gains `FieldDescriptor` and `FieldType` (`Sound/Editing/FieldDescriptor.cs`): the generic field
  description the catalogue, the editor and the physics presets share.
- **Words** gains `DirectionWords` (Common) and `SafeText` (the server's rules for player text).
- **Geometry** gains the two JSON vector converters.
- **Physics** loses `RoadSurfaces` to Materials and reads friction from it.
- **Sound** loses 12 files to Environment and 4 to Models, and reads both.
- **Audio** (library stage 6) reads the same scene view as Accessibility.

### 3.10 What stays in the game

| Part | Files | Lines | Why it stays |
|---|---:|---:|---|
| Hosts that step a package (systems, the editor's commands, maps to entities, composites, seats) | 56 | 20,972 | they own the entity store and the tick; each steps a package or adapts entities to it |
| Client audio, until library stage 7 splits it | 5 | 6,396 | `ClientAudioSystem`, `WorldAudioPlayer`, `BirdLife` |
| Sessions and the network (both sides, the MUD gateway, limits, wire checks, chat, settings) | 18 | 4,908 | open-fps's accounts and transport; the wire's types are Common's |
| Combat and weapons in play (firing, wounds, bodies, hands, the teleporter, the scope, pick-up) | 13 | 4,447 | game rules; the weapons' numbers are Sound's presets and the flight is Physics' |
| The server program and the map manager | 6 | 4,295 | the composition root |
| Commands | 14 | 3,837 | open-fps's command set and help |
| The client's world, prediction and geometry | 11 | 3,806 | the snapshot store and its adapters; prediction is physics' host |
| Accounts, auth and persistence | 12 | 1,652 | the database and the files beside it |
| Roles, permissions, bans, teams | 6 | 860 | open-fps's policy |
| Client platform and input | 8 | 717 | each head's devices; the keys themselves go to Accessibility |
| Diagnostics | 4 | 238 | |

Line counts here are whole files.

---

## 4. The layers

A package's layer is one more than the highest layer it may reference. No cycles: the classifier computes the
layers from `ALLOWED` and stops on one.

| Layer | Package | May reference |
|---:|---|---|
| 0 | Words | nothing |
| 0 | Schema | nothing |
| 0 | Native | nothing |
| 0 | Geometry | nothing |
| 0 | Materials | nothing |
| 1 | Astronomy | Words |
| 1 | Physics | Schema, Geometry, Materials |
| 1 | Acoustics | Geometry, Materials |
| 2 | Environment | Words, Schema, Geometry, Materials, Astronomy, Physics |
| 3 | Sound | Schema, Geometry, Materials, Physics, Acoustics, Environment |
| 4 | Models | Schema, Geometry, Materials, Physics, Acoustics, Environment, Sound |
| 5 | Audio | Schema, Native, Geometry, Materials, Physics, Acoustics, Environment, Sound, Models |
| 5 | World | Words, Schema, Geometry, Materials, Astronomy, Physics, Acoustics, Environment, Sound, Models |
| 6 | Common | Words, Schema, Geometry, Materials, Astronomy, Physics, Acoustics, Environment, Sound, Models, World |
| 6 | Agents | Words, Schema, Geometry, Materials, Physics, Environment, Sound, Models, World |
| 7 | Accessibility | Words, Schema, Geometry, Materials, Physics, Acoustics, Environment, Sound, Models, Audio, World, Agents |
| 8 | Editor | Words, Schema, Geometry, Materials, Physics, Acoustics, Environment, Sound, Models, World, Common, Accessibility |
| 9 | the game: Server, Client.Core, the heads, the lab | anything |

- The server references everything but Native and Audio, as today: it never loads FMOD or Steam Audio.
- Physics stays on Geometry and Schema, as decided, and gains Materials.
- Resonance can take layers 0 to 5 and Accessibility without anything of open-fps's game, once Accessibility
  reads the scene view instead of Common.
- Two choices could go the other way: World above Sound (it names sound values in prefabs; the other way, the
  prefab format would carry ints), and Accessibility above Agents (the driving cues plan on the racing line; the
  other way, the planner takes a line).

---

## 5. The crossings

1,384 references cross the proposed layers. 455 are already planned by the other two surveys; 929 are this
survey's.

| From | To | Refs | Files | Whose stage |
|---|---|---:|---:|---|
| Physics | Common | 69 | 1 | physics stage 2 (`PhysicsUtils`' adapters) |
| Physics | Sound | 31 | 1 | physics stage 2 (`WheelDynamics` waits for the tyre split) |
| Physics | game | 3 | 1 | physics stage 2 (`GroundProbeMemo`) |
| Audio | Common | 297 | 8 | library stage 6 |
| Audio | game | 49 | 4 | library stages 6 and 7 |
| Audio | World | 4 | 1 | library stage 6 (`EntityType` read from snapshots) |
| Audio | Accessibility | 2 | 1 | library stage 6 (`NamedPlaces` in `SpatialAcoustics.cs:353`) |
| Environment | Sound, Models, Acoustics | 38 | 7 | this survey, 3.2 |
| Sound | Models | 95 | 16 | this survey, 3.3 |
| World | Common, game | 36 | 5 | this survey, 3.4 |
| Common | Agents | 2 | 1 | this survey, 3.5 |
| Agents | Common, game | 22 | 6 | this survey, 3.5 |
| Accessibility | Common, game | 703 | 10 | this survey, 3.6 |
| Editor | game | 33 | 3 | this survey, 3.7 |

Of this survey's 929, the 598 snapshot reads of the aids are one problem (a world view) and the other 331 are
small: a value moved down, a lookup turned round, an argument passed in. Every line is in
`packages_crossings.tsv` from the tool.

The references between packages, all of them (lab and tests left out; brackets: not allowed):

| From | Words | Schema | Native | Geometry | Materials | Physics | Acoustics | Environment | Sound | Models | Audio | World | Common | Agents | Accessibility | Editor | game |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Astronomy | 2 |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| Physics |  | 15 |  | 200 | 8 |  |  |  | (31) |  |  |  | (69) |  |  |  | (3) |
| Acoustics |  |  |  | 307 | 135 |  |  |  |  |  |  |  |  |  |  |  |  |
| Environment |  | 57 |  |  | 4 | 69 | (4) |  | (18) | (16) |  |  |  |  |  |  |  |
| Sound |  | 1286 |  | 73 | 352 | 434 | 128 | 424 |  | (95) |  |  |  |  |  |  |  |
| Models |  | 68 |  | 1 | 2 | 20 |  | 13 | 515 |  |  |  |  |  |  |  |  |
| Audio |  |  | 3414 | 308 | 58 | 78 | 493 | 143 | 1387 | 100 |  | (4) | (297) |  | (2) |  | (49) |
| World | 2 |  |  | 118 | 11 | 4 | 7 | 21 | 4 | 12 |  |  | (26) |  |  |  | (10) |
| Common |  | 3 |  | 103 | 4 | 12 | 53 | 3 | 12 | 4 |  | 7 |  | (2) |  |  |  |
| Agents |  |  |  |  | 2 | 29 |  | 21 | 68 | 7 |  | 282 | (14) |  |  |  | (8) |
| Accessibility | 4 |  |  | 36 | 8 | 28 | 3 |  | 115 | 26 | 204 | 195 | (598) | 21 |  |  | (105) |
| Editor | 2 | 222 |  | 5 | 6 |  |  | 6 |  | 104 |  | 119 | 391 |  | 43 |  | (33) |
| game | 62 | 434 | 6 | 1133 | 92 | 677 | 444 | 556 | 2408 | 748 | 1757 | 2634 | 8027 | 339 | 725 | 1670 |  |

---

## 6. Global state

Mutable statics (assignable, settable, or a collection whose contents can change), by package:

| Package | Mutable statics | Thread-static | Written from outside their type |
|---|---:|---:|---:|
| Materials | 5 | 0 | 0 |
| Environment | 11 | 0 | 0 |
| Models | 32 | 1 | 0 |
| World | 19 | 0 | 0 |
| Common | 0 | 0 | 0 |
| Agents | 2 | 0 | 0 |
| Accessibility | 9 | 0 | 0 |
| Editor | 10 | 0 | 1 |

The ones that are world state or settings, and what replaces each:

| Static | Where | What it is | Replaced by |
|---|---|---|---|
| `GroundWaterSystem._maps`, `_frames` | `GroundWaterSystem.cs:86`, `:104` | every map's ground water in one dictionary | Environment's per-world state, as library stage 5 did for run-off and the wind |
| `GroundHydrology.SoilGroup` | `GroundWater.cs:45` | one soil for every world (audit S31) | a field of the world's ground |
| The registries' sets and generations | `Machines.cs:139-552`, `ModelLibrary.cs:84-99`, `ModelKindTable.cs:210-273` | what was loaded, from where, and authored | the `ModelCatalog` instance library stage 5 names, behind the static facade until its last reader moves |
| Speech's five caches | `Speech.cs:135-214` | lazy, without locks (S31) | built once in the catalogue |
| `AcousticRegistry._registry` | `AcousticRegistry.cs:166` | the table, swapped whole by `Initialize` | stays a table filled once; the host calls `Initialize` at start |
| `EditorKeys.Enabled` | `EditorKeys.cs:14` | a setting written from four places | an option the session holds |

The rest are tables filled once, shared HTTP clients and per-thread scratch.

---

## 7. Files, paths and environment

| Package | Environment variables | Literal or working-directory paths | File and folder calls | What |
|---|---:|---:|---:|---|
| Materials | 0 | 0 | 0 | |
| Environment | 0 | 0 | 0 | |
| Models | 0 | 5 | 8 | `machines/`, `models/` from the working directory; exports |
| World | 0 | 27 | 98 | the tile store, `world.json`, maps, prefabs, `world_places.json`; five network sources |
| Agents | 0 | 0 | 0 | |
| Accessibility | 1 | 1 | 4 | the player's `beacons.json` (in the settings folder, `SettingsFolder`) |
| Editor | 0 | 5 | 22 | map overlays and versions, model versions |

The rule is the library's (its section 7.4): a package reads only the folders its host names. Models, World,
Accessibility and Editor each need one options value carrying their roots.

---

## 8. The wire hash

`WireContract.Hash` (`OpenFPS.Common.csproj`) hashes Common and `WireLibrarySource`: Geometry, Acoustics, Sound
outside `Core/`, Schema and Physics. Files hashed today, by proposed package:

| Package | Files | Hashed today | Must be hashed | Why |
|---|---:|---:|---|---|
| Materials | types from 2 files | yes (both) | yes | the surface byte, and the walls both sides hear through |
| Environment | 15 | 12 | yes | weather type, precipitation, strikes, road and ground water are sent |
| Models | 7 | 7 | yes | both sides resolve a model id the same way |
| World | 26 | 4 | yes | roads, the roads message and tile states are sent |
| Common | 11 | 11 | yes | the wire itself |
| Agents | 10 | 5 | no, once `MachinePower`'s encoding is in Common | nothing of it is sent |
| Accessibility | 22 | 4 | no | client only |
| Editor | 12 | 0 | no | its messages are Common's |

Each stage that makes a hashed package adds it to `WireLibrarySource` in the same commit; every such stage
changes the hash, so the server and the Windows client go out together (todo.md's shipping rule). Taking
Accessibility out of the hash means a change to an aid no longer refuses logins between versions.

---

## 9. The stages

Rules for every stage, as for the other two: one stage per merge; `git mv` for moves; namespaces unchanged while
moving; the guards give the same result before and after, and a stage that changes one is finished only when the
change is the one intended and its commit says so.

| Stage | What | Sessions | Main risk |
|---|---|---:|---|
| 0 | Guards: the packages ratchet and the spoken-lines replay | 1 to 1.5 | the replay depends on timing in the aids |
| 1 | Materials | 1 | the door renders' disk cache renews once |
| 2 | Models, with library stage 5's `ModelCatalog` slice | 2 to 3 | 95 lookups turned round in tuned files |
| 3 | Environment | 2 to 3 | approved rain, thunder, water and fire must not move a sample |
| 4 | World | 2 to 3 | `city.json` and the places' maps must regenerate byte-identical |
| 5 | Agents, the laws | 2 | extraction from `VehicleSystem` changes traffic |
| 6 | Common to the wire | 1 | touches every project |
| 7 | Accessibility, the clean half and the keys | 1 to 1.5 | the heads' key maps |
| 8 | Editor | 1 to 2 | the prefab repository's split |
| 9 | Accessibility on the scene view | 2 to 3 | the most heard code in the client |
| 10 | Agents, the people on a view | 2 to 3 | Alex's day and the door manners were approved by ear |
| | **Total** | **17 to 25** | |

A session is one working session of an agent, as in docs/GEOMETRY.md.

### Stage 0: guards (1 to 1.5 sessions)

- **The packages ratchet** (`EnginePackagesTests`, on the machinery of `LibraryBoundaryTests` and
  `PhysicsBoundaryTests`): a checked-in list of each package's files and the allowance of crossings by file from
  `packages_crossings.tsv` (1,384 now, 929 of them this survey's). It binds the sources and fails on a new
  crossing or an unlisted file; the allowance only goes down. After each project exists, an assembly test: its
  references are those `ALLOWED` gives (checked in as a small table).
- **The spoken-lines replay** (`SpokenLinesReplayTests`): scripted walks and drives on the test maps and a cut of
  the city, through the client's aids (beacons, stairs, sight, turns, wall bumps, the tracker, the driving cues),
  recording every line given to the speech output and every cue voice started, with its time, and comparing them
  with a stored list, as the emitter-stream replay does for voices. Nothing guards what is said today.
- Existing guards for every stage: `LibraryBoundaryTests`, `PhysicsBoundaryTests`, `RenderFingerprintTests`,
  `EmitterStreamReplayTests`, `VoiceIdBandTests`, `VoiceRegistryTests`, `ModelKindTableTests`,
  `MapScopedStateTests`, and the physics replays (`MovementReplayTests`, `DrivingReplayTests`, `RailReplayTests`),
  also run with `DOTNET_PROCESSOR_COUNT=4`. All run on GitHub Actions; locally only through `tools/test-local.sh`.

- **Built 2026-10-12, the packages ratchet** (made on Windows and sent back as a patch, on main `e47be9ac`
  with physics stage 2's fourth piece). `EnginePackagesTests` holds four lists in
  `OpenFPS.Tests/EnginePackages`, which `tools/engine_packages/run.sh` writes into its `ratchet` folder:
  `files.tsv` (540 files, each in a package or in the game), `types.tsv` (the 24 types sorted by a rule of
  their own), `layers.tsv` (what each package may reference, and its layer) and `allowed.tsv` (1,301
  crossings, by file and the package referred to, in 75 lines).
  - It binds the sources of the eleven sorted projects in one compilation and counts as the survey counts;
    its count and the survey's are the same line for line.
  - It fails on a file in no package, a listed file that is gone, a type rule that names no type, a cycle
    in the layers, a new crossing, an allowance left higher than the count (lower it with
    `OPENFPS_BOUNDARY_WRITE=1`) or raised past 1,301, and a package's project that references a project
    its layer does not allow (the eight that are projects already; a new one joins the list when it is made).
  - The survey said 1,384 crossings at `6dce0ba6`. It is 1,301 now: the tyre, the vehicle and the engine took
    theirs out, and `PhysicsUtils.cs` and `PhysicsConstants.cs` are sorted to Common (they were "Physics,
    until split"; the split is done, 16.4 of docs/PHYSICS_BOUNDARY.md), which took 72 from Physics and gave
    15: 12 to the files of Accessibility and World that read the body's numbers by the game's names, and 3
    to `PhysicsUtils.cs` itself, which names the server's `GroundProbeMemo`.
- **Built 2026-10-12, the spoken-lines replay, its walks** (made on Windows and sent back as a patch, on the
  packages ratchet). `SpokenLinesReplayTests` runs the client's own session (`ClientGameSession`: its
  bindings, its prediction, its aids and its audio system) on a piece of the city as the server sends it, with
  a script at the keys, and compares every line given to the speech output and every cue started, each with
  its tick, with a stored list in `OpenFPS.Tests/EnginePackages/spoken`.
  - Two walks, each about twenty seconds of the game: `selby_stairs` (the arrival on a named landing, the
    compass, two flights with their cues, the landings by name, quarter turns with what is ahead after each,
    a wall walked into, the look-ahead, the tracker, the zone by its key) and `main_street` (the arrival in a
    zone, along the pavement, a turn to a front door and into it, the tracker stepped and its kind changed,
    the coordinates, the narration turned off and on, off the kerb into the street's zone). The door and
    stair beacons sound through both.
  - A line is "tick say text" (said after what is being said), "tick say! text" (over it), "tick interrupt",
    "tick ui id" or "tick cue kind sound". Cues are the interface sounds and the voices of the beacons' and
    the aids' kinds. A knock on a wall walked into is a render played when a worker has made it, so its
    moment is not the same on every run: it is not in the list, and the wall's name said with it is
    (`WalkingIntoAWallKnocks` holds the knock; see the drive below).
  - **The session has a clock for it.** Three announcements waited on the wall clock (the arrival line, a
    zone holding before it is said, a stair cue speaking for its landing), and the session's audio system ran
    on a stopwatch with its acoustic worker on a thread. An internal constructor takes a replay's clock: the
    three wait on it, and the audio system is on it with the worker stepped by hand, as the emitter-stream
    replay has it. The game's constructor is as it was.
  - The same run says the same on the same tick every time (`TheSameRunSaysTheSame`), and the two lists come
    out the same run after run on the machine they were written on.
  - **Not built:** a drive (built 2026-10-16, below), other people passing, and what the menus, the editor
    and chat say.
- **Built 2026-10-16, the spoken-lines replay, its drive, and the knock** (on Linux, on main `d0116bb5`).
  `garage_drive` is a third scenario, about twenty seconds of the game: beside the hatchback parked on level 0
  of the city's garage, in by E, the engine on by T, out of the garage and right onto Main Street, Z for the
  road, the right indicator by L, slowing for the junction with Central Street and right through it, west
  along Central Street, stopping, the engine off by Shift+T and out by E. Its list holds 14 lines said and
  105 driving cues: the road's name and limit, the junction ahead with its exits, the turn the indicator
  means, the readout, the guide, the kerb sensor, the kerb's rumble strip, 12 turn clicks, the aligned
  chime, the brake cue in four bands, and the relay ticking until it cancels after the turn; and the
  server's lines for getting in, the key and getting out, the arrival, the car's offer to get in, and the
  zones (the car's cabin once out of the garage, the street on stepping out). The car's beacon sounds while
  you stand beside it.
  - **The server's half is scripted** (`DriveServer`, in `SpokenLinesReplayDrive.cs`), as the emitter-stream
    replay scripts its cars. The car and its parts are the ones the server makes (a city with its parked cars
    placed, a map manager of the drive's own so the walks are sent nothing new), sent as the broadcast sends
    them once in reach, then a `ServerStateUpdate` every tick with the car, its parts and, seated, the
    driver at the seat. E, T and Shift+T are answered in the server's words (OccupancyService.Enter and
    Exit, DrivingSystem.SetIgnition). The roads are `MapRoadsBuilder`'s, without level crossings (none is
    near). The car keeps to a line of straights and arcs; W and S only set its speed, so steering and lane
    assist are sent and not followed. No car door is sent (a render, as the knock is), no wheels (the cues
    take the road as dry) and no weather.
  - **Every heading is 0.6 degrees off the road's.** A heading on a multiple of 15 degrees sits on a turn
    click's mark, where the last bit of an arctangent decides the click: due south clicked twice on a
    straight in the first run, and another machine's maths could add or drop one.
  - **The relay is one voice, sounded again.** The test mixer never ends a one-shot, so the relay's tock
    after its tick was a new sound on a voice still held, where the game starts each afresh. A cue voice
    given another sound counts as a cue started; the walks' lists did not change for it.
  - The drive says the same twice (`TheSameDriveSaysTheSame`), and its list is the same with
    `DOTNET_PROCESSOR_COUNT=4`. Broken on purpose, it failed for each of: the junction line reworded, the
    relay's period 0.3 s for 1/3 s, the road's lines said over speech instead of after it, and the kerb
    sensor's range 1.4 m for 1.5 m.
  - **The knock** (`WalkingIntoAWallKnocks`): up Selby House's stairs into the brick wall, the wall's name
    is said and a strike starts within three ticks, of brick, with the body among its blows. The replay
    waits for each render a worker is making before the next tick (`AwaitRenders`), which the stored lists'
    runs never do. It failed with the knock not handed to the world's sounds, and with the wall's material
    taken as plaster. Before it, `WallBumpAndNarrationTests` held the bump and the strike's key but not that
    the session plays it.

### Stage 1: Materials (1 session)

- Create `OpenFPS.Materials`, referencing nothing. Move `AcousticRegistry.cs` whole; move `RoadSurfaces` out of
  `Chassis.cs`. Acoustics, Physics, Sound and Common reference it.
- `DoorModelSource` (`OpenFPS.Sound.csproj:57`) follows the file; `WireLibrarySource` gains Materials; the library
  ratchet's `files.tsv` and the physics ratchet's lists follow; `PhysicsReferencesOnlyGeometry` allows Materials.
- Then, each its own commit with the guards unchanged: the four name tests of 3.1 read properties; friction and
  restitution added as columns with sources, read by nothing yet.
- Check: every project builds at 0 warnings; the render fingerprint bit for bit; the emitter-stream replay and the
  physics replays unchanged; `AcousticRegistryTests`, `MaterialStrikeTests`.

- **Built 2026-10-12, the project and the two moves** (made on Windows and sent back as a patch, on the
  spoken-lines replay). `OpenFPS.Materials` references nothing of ours. `AcousticRegistry.cs` moved whole
  from Acoustics with `git mv`, not a line changed, and `RoadSurfaces` whole out of `Chassis.cs` into
  `RoadSurfaces.cs`. Acoustics, Physics, Sound and Common reference it; Acoustics no longer references
  Serilog itself.
  - The wire hash changes (7cd68fbff151 to ab1cd0862ca3): the hashed files moved and `WireLibrarySource`
    gained Materials. **The door models' fingerprint does not** (cfb37eeb081b): it hashes what its files
    hold, not where they are, so no kept render is made again. (The risk written in 3.1 did not come about.)
  - The guards followed: the library's ratchet (the sorted projects, `files.tsv`, each assembly's allowed
    references), the physics' (`PhysicsReferencesOnlyGeometry` allows Materials, and a type declared there
    may be named), the packages' (the seven type rules are gone, the files are sorted by project, the
    assembly is checked against its layer). The reader parses the new project and the three classifiers
    sort it.
  - In a second commit, the twelve names: `Sightline.cs`, `WallBumps.cs`, `MapRepository.cs` and
    `PrefabValidator.cs` read the body's step, radius and mass and gravity from `BodyConstants`, where
    they read them by the game's names. The allowance is 1,289.
- **Built 2026-10-16, the name tests and the contact columns** (on Linux, in two commits on main `c8386406`).
  - **The name tests.** A grep for comparisons with a material's name found five, not four; each reads a
    property of the table now (docs/MATTER.md 2.4, "What a material is to a player"):

    | What | Where | Reads now |
    |---|---|---|
    | A material's spoken name underfoot | `ClientAudioSystem.OutdoorNameFor` | `MaterialProperties.GroundName` |
    | "Asphalt" means a road | `DrivingAids.NearestRoad`, `TryRoadAt` | `MaterialProperties.Carriageway` (`IsCarriageway`) |
    | "Foliage" means a canopy | `SurfaceRaster.Of` | `Leaves` (`IsLeaves`) |
    | "Glass" means a window | `WorldEditor.Glazing` | the glass family (`IsGlass`) |
    | "Glass" means a glass surface of a shape | `Shapes.SlotSurfaces` (Geometry) | the family, passed in by `SolidSpec.OfShape`'s callers: Geometry holds no table |
    | "Fence" means a pale | `StruckThings.Describe` (Sound) | `MaterialProperties.Pales`, the pale's section, which moved into the table |

    Every row of the table answers as its name did but laminated glass, which is glass now (no map uses it), and
    an alias answers as its material ("Steel" underfoot is metal grating). `MaterialNameTests` holds both, row by
    row, and holds the fence's pale as it was. The lab's three copies (`DrivingSpike`, `BumpSpike`,
    `GeometryParitySpike`) read the same properties.
  - **The guard of section 11** (`MaterialNameTests.NoMaterialIsToldByItsName`): no file of the sorted projects
    but the table compares anything with a material's name or an alias (an `==`, an `Equals`, a switch arm, a
    `case`, an `is`), beyond four allowed: `SoundMappingService`'s wet footstep sets (a choice of recordings,
    which stays), the concrete a path is laid in (`WorldEditor.Routes`, two), and the menu a sofa is listed under
    (`WorldEditor.Menus`, "Audience"). "None" and "Generic", the table's words for no material and an unknown one,
    are not counted. It reads names as the table spells them; lower-case words are other vocabularies (the
    weather's "snow", OSM's "brick", a lock's "steel"). One name test is in lower case and so out of its sight:
    `GroundWater.OfMaterial`, the runoff class by name (grass a lawn, foliage woods, dirt open ground, anything
    else sealed). It wants a runoff cover in the table, and waits for stage 3, which moves the file.
  - **Friction and restitution** (`StaticFriction`, `KineticFriction`, `Restitution`), each the material against
    itself, so decision 2's pair rule gives a measured like-on-like pair back. 22 rows have a friction, one a
    restitution (glass); the rest are empty, not guessed, and docs/MATTER.md 2.4 lists them with the sources.
    Nothing reads them yet. `MaterialContactTests` holds every cell.
  - The wire hash changes (Materials, Geometry, Sound and Common changed), and so does the door models'
    fingerprint, which hashes `AcousticRegistry.cs`: every door render is made again, the same. Nothing heard
    changes: the render fingerprint, the emitter streams, the spoken lines and the physics replays are as they were.
    No crossing came or went (the packages survey still counts 1,289).

### Stage 2: Models (2 to 3 sessions)

- Create `OpenFPS.Models` above Sound and Physics. `FieldDescriptor` to Schema first. Move the seven files.
- Turn round the 95 calls from Sound and the 16 from Environment's files (still in Sound at this point): the
  presets' static `Get`s become the catalogue's; the shell, the vehicle spec and the glass take the parts or the
  kind as an argument.
- Library stage 5's slice in the same stage: the registries into a `ModelCatalog` instance behind the static
  facade; Speech's caches built once.
- Check: render fingerprint, emitter stream, `ModelKindTableTests`, `ModelLibraryTests`, `MachineTests`, the world
  editor's tests, `VehicleBodyShellTests`.

- **Built 2026-10-16, the catalogue** (made on Linux in four commits, begun on main `d0116bb5` and rebased onto
  `f7701b8b`, which has the ricochet, one gravity and one air, the rest of stage 1, refraction and stage 7's
  first half). Nothing a player
  hears changes: every stream and render passes unregenerated, and every model the library exports (174),
  every vehicle's parts list and its stored spec (45), every kind with its editor fields, every voice take,
  call and conversation, the scope and the rounds, written out on main and on the branch, are the same byte
  for byte.
  - **`FieldDescriptor` to Schema first** (`FieldType`, `FieldDescriptor`), moved whole.
  - **The turn round.** The 95 references from Sound's files and the 16 from Environment's are gone, but
    `VehicleSpec.cs`'s 10, which moved with the catalogue. `IPresets` (Sound) is a preset by name or key as
    the catalogue has it, a map's own first; the catalogue is one. The eight lookups that asked the library
    (`EngineProfile`, `SmallMachineSpec`, `WaterFeatureSpec`, `FoliageSpec`, `RunningWaterSpec`, `ShoreSpec`,
    `GasHobSpec` and `FireSpec.ByName`) and `Loudspeakers.TryGet` are the catalogue's, their bodies as they
    were; the game, the lab and the tests ask `ModelLibrary.Presets`. What names another preset is handed
    it: the train, aircraft and machine synths, `TrainLayout.Sources`, `Honk.LevelDb`, `CabinPaths.For`,
    `ExtendedSources.Layout`, `WoodChorus.Build`, `HobControls.Press`. The ids logged once are
    `ModelFailures` (Sound); `ModelKindTable.Fail` writes there.
  - **The project.** `OpenFPS.Models`, on Geometry, Materials, Acoustics, Physics, Schema and Sound, holds
    `Machines.cs`, `ModelKindTable.cs`, `ModelLibrary.cs`, `Editing/ModelKinds.cs`, `Speech/Speech.cs` with its
    three lists, `Ammunition.cs`, what a scope is (`Scopes.cs`; `ScopeMath` and `ScopeSway` stay in Common's),
    `VehicleSpec.cs` and `GlassKind.cs`, moved with `git mv`, not a line changed. Common references it. The
    voices' lines keep their resource names. It is in the wire hash (decision 11).
  - **Library stage 5's slice.** The registries are one instance, `Catalogue`: its halves sit beside the
    static classes in `ModelLibrary.cs`, `Machines.cs` and `ModelKindTable.cs`, and hold what was loaded,
    from where, what was assembled, the generation and a machine's body. `ModelLibrary`, `MachineRegistry`
    and the kind table are facades over `Catalogue.Shared`. Speech's five tables are built once, under a lock
    (`Lazy`), where any thread built them without one (the audit's S31).
  - **Where it went another way than written,** each a question for Cody:
    - The parts vocabulary (`MachineModels`, `MachinePart`, `MachineDefinition`) stayed in Sound, in
      `MachineParts.cs`, so the shell could take the parts list as the plan says: `VehicleBodyShell.For` takes a
      `MachineDefinition`, and `MachineRegistry.Shell(id)` makes one by id.
    - `GlassKind` and `GlazedPart` moved to the catalogue rather than taking the vehicle as an argument: the
      argument would have changed `CombatService`, which the Ricochet move holds. Taking them out of
      `Glass.cs` changes the door models' fingerprint, so the kept door renders are made once again.
    - `VehicleSpec` moved whole (Cody's decision in docs/PHYSICS_BOUNDARY.md 16.2) rather than taking the
      parts as an argument. The physics survey sorts it and `VehicleChassisSpec` to a new group, M, the
      catalogue's; its crossings fall from 43 to 15, the 28 having been `VehicleChassisSpec`'s.
    - Environment's three files (`Fire.cs`, `FireSpread.cs`, `Fuel.cs`) take a fire by name as a function, not
      `IPresets`, which is Sound's and above them once stage 3 makes Environment. `FuelCatalog.ForThing` is told
      whether the thing is a road vehicle; a fire is read from its key's prefix, as `Fire.cs` reads it. The
      server's drainage lines name their sound by `GroundChannels.SoundId`.
    - The instance is `Catalogue`, not `ModelCatalog`: the world editor's catalogue of kinds
      (`OpenFPS.Server.Editor.ModelCatalog`) has that name.
  - **The ratchet** falls from 1,289 to 1,178. The library's allowance is unchanged; its survey counts nine
    fewer statics that can change for the last commit. The catalogue's assignable statics are
    `Catalogue.Shared`, the per-thread list of vehicles being assembled and the built-in engines' names; the
    rest are tables filled once.
  - **The wire hash** changes (05c6eeaff638 on `f7701b8b`, 5c7130516f83 after; the hashes in the commits'
    messages were taken before the rebase): the server and the Windows client go out together. The door
    models' fingerprint changes once, at the second commit (4cee64a56d02 to cb56e903d42f).
  - **Left:** the readers are on the static facades, and the kind table's rows, `VehicleSpec.Build` and the
    `IPresets` places ask `Catalogue.Shared`; a second catalogue is whole only for what its own members do.
    The folders (`machines/`, `models/`) are still relative to the working directory, not named by the host
    (library survey 7.4). `WeaponRegistry` joins the catalogue when the presets split.

### Stage 3: Environment (2 to 3 sessions)

- Create `OpenFPS.Environment` on Physics, Materials, Astronomy, Geometry and Schema. Split `GroundChannels` first;
  Lightning reads Physics' `Air`. Move the 12 Sound files, the types from `Waves.cs`, and the three server files.
- One atmosphere value; `WorldEnvironmentComponent` carries it.
- `GroundWaterSystem`'s maps and `SoilGroup` per world.
- Check: render fingerprint (rain, thunder), emitter stream (`traffic_rain`), `FireSpreadTests`, `FireTests`,
  `WeatherWindTests`, `WeatherAndConvergenceTests`, `AmbienceAndWeatherTests`, `MapScopedStateTests`; the wire
  hash changes.

- **Built 2026-10-16, Environment** (on Linux, in five commits, begun on main `13e0f460` and rebased onto
  `afe5e9fb`, which has library stage 6's second half; the counts and hashes in the first four commits'
  messages were taken before the rebase). `OpenFPS.Environment` holds 20 files on Geometry, Materials, Schema
  and Physics; Sound, Models, World and Common reference it, and it references none of them. Nothing a player
  hears changes: the render fingerprints (rain on three surfaces, thunder), the stored streams (`traffic_rain`
  among them), the spoken lines and the physics replays pass unregenerated.
  - **First, the bytes.** `EnvironmentWireBytesTests` was written on `13e0f460` before any change: a map's
    weather as the broadcast fills its `WorldStateUpdate` (a seeded storm on a map with its own air, what falls,
    the water on the roads, three hours of it over the ground with two ponds overflowing), every `WeatherType`
    and `PrecipitationKind` by number, and a seeded storm's first four strikes as the sounds they are sent as,
    each by its SHA-256. It passes after every commit. Broken on purpose, it failed for the soil group B for C,
    Snow and Storm swapped, and a strike's declared level 175 dB for 176.
  - **The splits** (Environment's 25 crossings; the plan's 38 less the 16 of the catalogue, turned round by
    stage 2, and the clock's 3 counted since library stage 5):

    | What | Was | Now |
    |---|---|---|
    | A drainage line's bed | in `GroundChannels` (Sound) | `GroundChannelBed`: `GroundChannelKind`, `SegmentMetres`, `ReferenceStormMm`, `ReferenceFlow`, `WidthFor`, `ManningN`, moved as they were; the voice's spec, key and level table stay in `GroundChannels` |
    | A drainage voice's sound id | `DrainageNetwork.Voice.SoundId` | `MapDrainage.SoundId(voice)`, the host's, from `GroundChannels.SoundId` |
    | Lightning's default speed of sound | `AcousticConstants.SpeedOfSound` (Acoustics) | `Air.ReferenceSpeedOfSound` (Materials), 343 m/s, which `AcousticConstants.SpeedOfSound` now reads; thunder passes its own speed, so only the lab's and the tests' defaulted calls read it |
    | What is soft to a hailstone | `RainSurfaces.SoftBelowGPa` (Sound) | `Hydrometeors.SoftBelowGPa`, which `RainSurfaces` reads |
    | The weather's clock | `IAudioClock` (Sound) | `IWorldClock` (`WorldClock.cs`): the world time alone; `IAudioClock` is one, so the mixer still hands the weather its own clock, and until it does the weather reads `SharedWorldClock` (`WindField.Now`), as the stopwatch clock it replaces did |
    | `WaterBody`, `WindWaves` | in `Waves.cs` | `WindWaves.cs`, cut whole; the shores' specs stay in Sound |

  - **Per world.** `GroundWaterSystem` is an instance: one world's maps and frames, where every map's water was
    in two static dictionaries. The map manager holds the server's (`MapManager.GroundWater`): its maps register
    there as they load, the world's frames build there, the tick steps it, the broadcast sends it, and a road's
    puddles read its hollows (`RoadWaterSystem.Update` is handed it). A frame waits on real time its host hands
    it (`RealTime.Milliseconds`), so the library's one-place rule for the machine's clock still holds. The soil:
    `GroundHydrology.SoilGroup`, assignable and written by nobody, is `DefaultSoilGroup` (B), the laws that read
    it take a soil group, and a world's ground water has one (`GroundWaterSystem.SoilGroup`), which the networks
    it builds (`DrainageNetwork.SoilGroup`) and the water it keeps (`GroundWater.SoilGroup`) are on. Every world
    is on B, as before. `Two_worlds_keep_their_own_water_and_their_own_soil` holds both; it failed with the maps'
    dictionary static again, the water's excess share on the default soil, and the network's beds on it.
  - **The move**, each file whole and not a line changed, namespaces as they were: from Sound `WeatherType`,
    `Precipitation`, `Rain`, `Lightning`, `Runoff`, `GroundWater`, `RoadWater`, `Fire`, `FireShape`,
    `FireSpread`, `Fuel`, `WorldWeather`, `AirLayers` and the three the splits made; into `Water/`, `Drainage.cs`
    (left in Sound by stage 4 to wait for this one), `DrainageNetwork.cs` and `GroundWaterSystem.cs` (the
    server's). The plan's 15 files are 19 with the splits' three and `AirLayers`, which came after it; 20 with
    the atmosphere. The fire files moved as stage 2 left them: they take a fire by name as a function, which
    Environment can hold since `FireSpec` is in `Fire.cs`. No crossing came or went with the move.
  - **One atmosphere.** `OutdoorAir` (Environment): temperature, humidity, pressure, the wind and its gustiness,
    with its gas, `Air`, by the one law. The three ways of 3.2 are now one value and the law below it:
    - `Thunder.Air` is gone; thunder takes an `OutdoorAir` (`Thunder.StandardAir` before the weather has said,
      the same 15 °C, 0.8, 1013.25 mb, still, 0.3 as before; the four tests and the render fingerprint that
      leaned on the old default gust say 0.3).
    - `WorldEnvironmentComponent.Outdoors` and `WorldSnapshot.Outdoors` carry it: the fields stay what is sent,
      one by one (`WorldStateUpdate` is unchanged and its bytes held), and the value is read from them. The
      client's thunder (`WorldAudioPlayer`), the server's bullets (`CombatService.Weather` is a
      `Func<string, OutdoorAir>`) and a driven car's drag (`driveAir.Outdoors.Air`) read it.
    - Physics' laws take its `Air`: Physics is below Environment and reads the materials' gas law, never the
      value.
    - The audio world's `Atmosphere` (`OpenFPS.Sound/Core/Atmosphere.cs`, library stage 5) is not a fourth
      way: it is the mixer's live copy of one number of it, the temperature, with the speed of sound from
      `Air`'s law, held volatile for the mixer's thread. Its host sets it from the weather's temperature, as
      before. It stays the audio world's.
    - The acoustics' `AirState` (library stage 6's world input: temperature, humidity, pressure, absorption)
      stays as it is: Acoustics sits beside Environment and cannot read it.
    `TheWorldsAirIsOneValueOfItsFields` holds the component's value field by field; it failed with the gust
    read from the absorption multiplier.
  - **The guards followed.** The packages ratchet (Environment a sorted project, its files by one rule, its
    assembly checked against its layer; the three type rules gone with their types' own files), the library's
    (Environment is library, as its files were in Sound; its assembly may reference Geometry, Materials,
    Schema and Physics, and Sound, the catalogue and the runtime may reference it; the type rules for the
    weather's values on the wire name Environment). The reader parses the new project, and every project above
    it references it. The physics survey sorts a project it does not know as host; its 15 are unchanged.
  - **The counts.** The packages ratchet falls from 761 to 736 (main `afe5e9fb`), all 25 Environment's; the
    library's allowance stays empty and its survey counts one static fewer (`SoilGroup`). Its layering count
    rises (4,530 to about 6,500): that table knows only the library's own layers, and already counted Sound to
    Physics; it is not a ratchet.
  - **The wire hash** changes (d81f09ee05a8 at `afe5e9fb` to 60f35a9236d7): the server and the Windows client
    go out together.
  - **Found, not fixed** (the reader's compilations, other stages' lines): Accessibility is given neither Models
    nor Sound (22 binding errors in `ListeningCalibration.cs`, since stage 7 moved it), Sound's internals are not
    shown to Models (8 in `Machines.cs`), and World is not given Parquet.Net (8 in `Overture.cs`). Those
    references go unseen by every survey.
  - **Left:** `GroundWater.OfMaterial` still tells the runoff class by a material's name (stage 1's note): it
    wants a runoff cover column in the table. Real weather (docs/REAL_WEATHER.md 8.1) plugs in at
    `OutdoorAir` and `WorldWeather`; the server's `WorldEnvironmentSystem` still keeps its fields one by one.
    The ground water's soil is one per world until a soil layer gives each tile its own.

### Stage 4: World (2 to 3 sessions)

- `EntityType` to World and the converters to Geometry first (MemoryPack's bytes unchanged). Split
  `TerrainBuilder`'s entity making and `PrefabRepository`'s loading from their hosts; `SurfaceRaster` from boxes.
- Create `OpenFPS.World` above Sound and Models; move the 26 files.
- The roots (tile store, maps, prefabs, `world.json`) from one options value the server makes.
- Check: `RealPlaceMapTests`, `MapSurveyTests`, `WorldBuildingsTests`, `MapScopedStateTests`, `GeometryStage4MapTests`;
  the movement replay; `city.json` regenerated byte-identical; the wire hash changes.

- **Built 2026-10-16, World** (on Linux, in five commits, begun on main `cedb4677` and rebased onto `3736101f`,
  which has library stage 6's first half). `OpenFPS.World` holds 30 files on Words, Geometry, Materials,
  Acoustics, Schema, Physics, Sound and Models; Common and the server reference it. Nothing a player hears,
  sees or is told changes.
  - **First, below it.** `EntityType` cut whole out of `Components.cs` into a file of its own; `Vector3Converter`
    (Sound) and `QuaternionConverter` (Common's `JsonConverters.cs`) moved whole to Geometry; `SafeText` from the
    server to Words (3.9), which the composites' store reads. `WorldWireBytesTests` was written on `cedb4677`
    before any move and holds every value of `EntityType`, an `EntityDefinition` with a type and a tile of ground
    sent as a message, a `TileStreamUpdate`, and the city's roads message as `MapRoadsBuilder` writes it, each by
    its SHA-256: it passes after every commit, so no byte moved. Broken on purpose, it failed for `Item` and
    `StaticObject` swapped in the enum, a tile state's X and Z swapped, and a road's `Type` written as "Kind".
  - **The splits, before the move** (World's 36 crossings):

    | What | Was | Now |
    |---|---|---|
    | The terrain's entities | `TerrainBuilder.Slabs(World)`, `Spawn` | `TerrainTiles.Slabs`, `Spawn`, the host's; `TerrainBuilder` grades and lays, and owns the post spacing |
    | A world tile's ground | `WorldTile.TerrainData.ToComponent()` | `TerrainTiles.ToComponent`, an extension the host holds; the tile keeps `TerrainData` |
    | What covers the ground | `SurfaceRaster.Of(World, ...)` | `SurfaceRaster.Of(boxes, ...)` with `SurfaceRaster.Box`; which entities are fixed boxes is `MapDrainage.Boxes`, in the world's order |
    | A map's spawn | `Transform` | `MapPose` (Position, Rotation), the map format's own; `MapManager.GetSpawnPoint` makes the body's `Transform` |
    | The prefabs' loading | `PrefabRepository` | `PrefabLibrary` (read, checked, written, put in use); `PrefabRepository` extends it with the making of an entity, so its callers are as they were |
    | A tile's drainage | `Server/Water/Drainage.cs` | `Sound/Drainage.cs`, moved whole, beside Environment's other files until stage 3 |
    | `WorldPlace` | in `WorldMaps.cs` | `OneWorld/WorldPlace.cs` |

    A spawn read from a map is the same `Transform` as before (Scale one, not dirty: the JSON reader called
    `Transform`'s constructor). A map the server saves no longer writes `"Scale"` and `"IsDirty"` under its
    spawn; no shipped map has them. The editor's catalogue lost 11 crossings with the prefabs' loading in World,
    so the packages survey fell from 814 to 767.
  - **The move**, each file whole and not a line changed: from Common `Roads.cs`, `RoadMapData.cs`, `Tiles.cs`,
    `TrackClearance.cs`, `EntityType.cs`; from the server `MapRoadsBuilder.cs`, `TerrainBuilder.cs`,
    `SurfaceRaster.cs`, the sixteen files of `OneWorld/` but `WorldMaps.cs` and `WorldPlaces.cs`, and
    `CompositeRepository.cs`, `MapElevation.cs`, `MapRepository.cs`, `PrefabLibrary.cs`, `PrefabTemplate.cs`,
    `PrefabValidator.cs`. Parquet.Net, which only Overture's reader uses, moved with it. No crossing came or went
    with the move. The plan's 26 files are 30: `EntityType` and `WorldPlace` in files of their own, the split-off
    `PrefabLibrary` and `SurfaceRaster`.
  - **The roots.** `WorldRoots` (Maps, Prefabs, Composites, Settings, Places; `In(folder)` lays them out as the
    server's folder is, `Store(settings)` is `world.json`'s StorePath from `world.json`'s folder). The server makes
    one (`GameServer.Roots`): the names it always read from its working folder, the maps and composites also
    found under `OpenFPS.Server` from the repository's root, as `MapRepository` and `CompositeRepository` used to
    guess for themselves. `WorldSettings.Load` and `WorldMaps.LoadPlaces` are told their file. Nothing in World
    names a folder of its own now; the sources' caches are under the store the host names. `WorldRootsTests`
    holds the layout and the store; it failed with the store taken from the working folder.
  - **The generators.** `city.json` regenerates byte-identical from `tools/gen_city.py` (SHA-256 `f9411ee3...`),
    and both places' maps from `tools/gen_osm.py` (`magnolia_tx` `530e97c4...`, `albany_or` `5aeb165b...`).
    Their comments follow the moved paths.
  - **The guards followed.** The packages ratchet (World a sorted project, its files by one rule, its assembly
    checked against its layer), the library's (World sorted, all host), the reader (it parses World, every
    project above references it, and Common's compilation is given Words: without it the snapshot types bound
    as retargeted symbols and 594 references went unseen). The physics survey sorts World as host; its 15 are
    unchanged.
  - **The wire hash** changes (50fc87080b8d on `3736101f` to 57189199a720): the server and the Windows client go out
    together.
  - **What stage 5 still needs for `DrivingCuePlanner`.** It reads `RoadNetwork`, `RoadMapData`, `JunctionData`
    and `CrossingRails`, which are World's now and which Accessibility may reference; and `RaceLine`, `LaneRoutes`
    and `DriverSteering`, still in Common. Once stage 5 puts those in `OpenFPS.Agents`, Accessibility references
    World and Agents and the planner moves whole.
  - **Left:** the server's map manager, prefab repository, tiles, streamer and places stay hosts (3.4); the
    editor's catalogue still reaches `PrefabRepository` for its kinds (stage 8 reads `PrefabLibrary`). Docs other
    than this one name the old paths of the moved files.

### Stage 5: Agents, the laws (2 sessions)

- Create `OpenFPS.Agents` on World and Physics. Move `LineFollower`, `RaceLine`, `LaneRoutes`, `AircraftGroundRun`,
  `MachinePower` (its encoding to `StatePacking`).
- Extract the five partial files' laws into Agents types; `VehicleSystem` calls them with positions, the road
  network and the road's water passed in.
- Check: the traffic and rail replays, `TrafficRuleTests`, `RouteTrafficTests`, `TrafficTyreDemandTests`,
  `PedestrianSpeechTests`, the emitter stream's `traffic` and `traffic_rain`.

### Stage 6: Common to the wire (1 session)

- After stages 1 to 5 and physics stage 2: what is left in Common is section 3.8. Drop the unused Arch and
  LiteNetLib references (the server and the client keep theirs). Rename or not (decision 9).
- Check: every project builds; every guard unchanged.

### Stage 7: Accessibility, the clean half (1 to 1.5 sessions)

- Create `OpenFPS.Accessibility`. Move the twelve clean files, the keys (`GameKey`, `KeyModifiers`) and the speech
  output; `DirectionWords` to Words. The heads' key maps reference it.
- Check: the spoken-lines replay, `BeaconTests`, `DrivingAidsTests`, `EditorDialogTests`, `ListeningCalibrationTests`.

- **Built 2026-10-16, ten of the twelve, the keys and the speech output** (on Linux, in two commits on main
  `ef7c115f`). `OpenFPS.Accessibility` references nothing of ours (Serilog, as the speech-dispatcher output logs):
  14 files.
  - Moved whole with `git mv`, not a line changed: `Beacons`, `LaneGuide` and `PlayerCoordinates` from Common;
    `DrivingCueSounds`, `TurnNarration` and `ZoneSettle` from Client.Core; the speech output (`ISpeechOutput`,
    `SpeechDispatcherOutput`, `SpeechLog`) and `GameKey` from `Client.Core/Platform`. `KeyModifiers` was cut
    whole out of `InputCommandMapper.cs`. `DirectionWords` moved whole to Words. Namespaces as they were.
    Client.Core, the server and both heads reference it: the heads' key maps and speech outputs use it directly.
  - **`ListMenu` needed more than the keys.** It plays the menu cues through `UiSounds`, which plays through the
    client's mixer (`AudioEngineFacade`, Audio's, in Client.Core until library stage 6). `UiCue`, what each
    interface sound means, was cut whole out of `UiSounds.cs` into Accessibility (3.6's "the cue meanings move",
    done early), beside `IUiCues`, which plays one; `UiSounds` is an `IUiCues`, and `MenuStack` takes one (two
    lines changed). The game hands it the same `UiSounds` as before.
  - **Not moved, two of the twelve.** Each is clean by package, but a type it reads is still in Common, which no
    package's project may reference. `ListeningCalibration` reads `Speech` (Models', stage 2) and `UiSounds`: it
    moves once `Speech` is in Models, taking an `IUiCues` as the lists do. `DrivingCuePlanner` reads the roads and
    the race line (`RoadNetwork`, `RoadMapData`, `JunctionData`, `CrossingRails`, World's; `RaceLine`,
    `LaneRoutes`, `DriverSteering`, Agents'): it moves after stages 4 and 5.
  - The wire hash changes (b38f858c04f8 to f6a935f51398): `Beacons`, `LaneGuide`, `PlayerCoordinates` and
    `DirectionWords` were hashed only because they were in Common, and Accessibility and Words are not (decision
    11). The server and the Windows client go out together.
  - The guards followed: the packages ratchet (Accessibility a sorted project and an assembly checked against its
    layer; the type rules for `GameKey` and `KeyModifiers` gone with their files), the library's (Accessibility
    sorted, all host). The reader parses the new project; the packages and library classifiers sort it; the
    physics classifier sorts a project it does not know as host, which Accessibility is to it, and its crossings
    are unchanged. No crossing came or went: 1,289.
  - The spoken-lines replay passes unregenerated, line for line, and with `DOTNET_PROCESSOR_COUNT=4`. So do the
    guards, the stage's four checks and the 40 classes that name a moved type (849 tests), and the replays with
    four processors (60).
  - **`ListeningCalibration` moved after (2026-10-16), eleven of the twelve.** `git mv` to Accessibility with
    two lines changed: it takes an `IUiCues` where it took `UiSounds`, as the lists do, and the game hands it
    the same `UiSounds`. It reads `Speech` (Models) and the hearing it sets (`HearingSettings`, `EarModel`,
    `Loudness`: Sound), both under its layer, so Accessibility now references Models and Sound and nothing
    needed an interface of its own. No crossing came or went (the packages ratchet's 1,178, the library's 358,
    the physics survey's 15); the two ratchets' file lists moved the row. The wire hash does not change:
    Accessibility is not in it. `ListeningCalibrationTests` and the spoken-lines replay pass unchanged.
    `DrivingCuePlanner` still waits on stages 4 and 5.

### Stage 8: Editor (1 to 2 sessions)

- After World (stage 4) and Models (stage 2). Create `OpenFPS.Editor`; move the twelve files and `Pose`; the
  catalogue reads World's prefab loading; the overlay takes the map's data.
- Check: `WorldEditorTests` and its phase and leftovers tests, `EditorDialogTests`, `EditorFormTests`,
  `WorldEditorClientTests`.

- **Built 2026-10-16, Editor** (on Linux, in three commits on main `7d45f7f1`). `OpenFPS.Editor` holds 13 files
  on Words, Schema, Geometry, Materials, Environment, Models, World, Common and Accessibility; the server,
  Client.Core and both heads reference it. Nothing a player hears, sees or is told changes, the editor's spoken
  menus and dialogs included.
  - **The rename first.** The editor's catalogue of kinds, `OpenFPS.Server.Editor.ModelCatalog`, is `EditorKinds`
    (in `EditorKinds.cs`): it holds the kinds the editor edits (`EditorKind`), and stage 2's `Catalogue` no
    longer reads like it. The properties holding one keep their name, `Catalog`.
  - **The splits** (the editor's 22 crossings; the plan's 33 less the 11 that stage 4's `PrefabLibrary` took):

    | What | Was | Now |
    |---|---|---|
    | The prefab kind's files | `PrefabKind` on the server's `PrefabRepository` | on World's `PrefabLibrary` (read, checked, put in use); the repository extends it, so its callers hand it the same object |
    | `Pose` | in `WorldEditor.Ops.cs` | `Pose.cs`, cut whole, beside `LiveState` |
    | A door made again | `LiveState.OnTo` called `DoorSystem.Settle` | it is handed the settle law; the host hands it `DoorSystem.Settle` |
    | The things' settings after load | `ApplyAfter(MapManager, id)` asked the map manager for the world and its things | `ApplyAfter(id, world, authored)`: the map's data handed in |
    | What laying an overlay needs of the host | `MapSettings.Apply`, `WorldEditor.LayRoutes` and `WorldEditor.IsVehicleId`, called from the overlay | `OverlayLaying` (which ids are parked vehicles, how a setting is laid, how the routes become the map's data), handed to `ApplyBefore` and `Lay`; the server's is `WorldEditor.Laying`. `OverlayAddition.IsVehicle` is `WorldEditor.IsParked` |

    Each is held by a test that failed when it was broken: a parked vehicle laid as a thing
    (`WorldEditorPlacedTests`), the map's settings not laid (`WorldEditorPhase2Tests`, `WorldEditorPhase3Tests`'
    baking, `MapSizeTests`), the routes not laid (`WorldEditorPhase3Tests`' road), a door not settled after a new
    version (two of `WorldEditorLeftoversTests`), and the settings not put on the things after load
    (`WorldEditorTests`, the baking).
  - **The move**, each file whole and its history kept, namespaces as they were: from the server's `Editor/`
    `EditorKinds.cs`, `EntitySettings.cs`, `LiveState.cs`, `MapOverlay.cs`, `MapVersions.cs`, `ModelStore.cs`,
    `OverlayRoute.cs` and `Pose.cs`, from its `Core/` `BuildSession.cs`, and from Client.Core `EditorDialog.cs`,
    `EditorKeys.cs`, `EditorValuePrompt.cs` and `BuildForm.cs`. Three members the server's commands use went from
    internal to public: `LiveState`, `MapOverlayStore.Lay` and `Find`. The tests and the lab see its internals.
    No crossing came or went with the move.
  - **The guards followed.** The packages ratchet (Editor a sorted project, its files by one rule, its assembly
    checked against its layer), the library's (Editor sorted, all host). The reader parses the new project with
    no binding error and every project above it references it; the physics survey sorts it as host, its 15
    unchanged.
  - **The counts.** The packages ratchet falls from 736 to 714, all 22 the editor's: no file of its model reaches
    the game. The library's allowance stays empty.
  - **The wire hash** does not change (60f35a9236d7): none of the files was hashed, and Editor is not
    (decision 11).
  - **Left:** `EditorKeys.Enabled` is still a static written from four places (section 6: an option the session
    holds). The folders of the overlays, the map versions and the model versions are still handed in by the
    server one by one, not as one options value. The two allowed name tests (`WorldEditor.Routes`' concrete,
    `WorldEditor.Menus`' "Audience") stay in the host, untouched.

### Stage 9: Accessibility on the scene view (2 to 3 sessions)

- After library stage 6 (the world input) and physics stage 3 (contact events). The ten aids read the scene view
  (decision 8): positions, kinds, names, boxes, materials, doors, stairs and places, as the audio does. The settings
  and the scope's categories come in as options and a registered source.
- Check: the spoken-lines replay unchanged, line for line; the emitter stream (beacons are voices).

### Stage 10: Agents, the people (2 to 3 sessions)

- After physics stage 3 (`IPhysicsWorld`). Haunts, door manners, the pavement graph, Alex's day and the pedestrians'
  conditions read a view of people, doors and places; the server's systems step them.
- Check: `PedestrianSpeechTests`, the character and haunt tests, the traffic replay, and Cody's ear for Alex.

### What can run beside what

| Can run together | Why |
|---|---|
| Stage 1 and stage 7 | no shared file but the project list |
| Stage 5 and stage 8 | Agents edits `VehicleSystem`; the editor's model edits `Server/Editor` but not `WorldEditor`'s partials |
| Stage 3 and stage 7 | Sound and the server's water against Client.Core's aids |

| Must not run together | Shared files |
|---|---|
| Stage 2 and physics stage 2 | `Machines.cs`, `VehicleSpec.cs`, `Engines.cs`, `Vehicles.cs` |
| Stage 3 and stage 4 | `Server/Water/*`, `WorldTile.cs` (it keeps a tile's drainage), `SurfaceRaster.cs` |
| Stage 6 and anything | `Components.cs`, `Messages.cs`, every project file |
| Stage 9 and library stages 6 and 7 | the scene view; `ClientAudioSystem` reads the aids' voices |
| Stage 10 and physics stage 3 | the world view |

---

## 10. How it fits with the physics and library stages

The order agreed on 2026-10-11 (todo.md, Next, in order): the special-cases audit's parts, then the physics stages
with geometry stage 6, then library stages 6 to 8. Physics stage 2 is next.

- **Materials before physics stage 2.** Stage 2 makes one law per law and splits the tyres; the surface grip
  becomes the material's in the same pass if the table is already below Physics. Physics stage 4 reads friction
  and restitution for every contact; the columns must exist with their sources before it starts.
- **Models before physics stage 2.** Stage 2 splits each preset into a mechanical and a sound half joined by id,
  and "the catalogue hands out both halves" (docs/PHYSICS_BOUNDARY.md, stage 2). The catalogue should exist as a
  package, and as library stage 5's instance, before the halves are made.
- **Environment beside physics stage 5.** The moving air stays in Physics (`WindField`); Environment's weather
  drives it, and its one atmosphere is what physics stage 2's air density reads.
- **World with geometry stage 6.** The second half of geometry stage 6 puts vehicle bodies in the acoustic scene;
  it does not touch the map formats. No conflict.
- **Agents before physics stage 6.** Stage 6 puts traffic under the same vehicle step as a driven car; the drivers
  (`LineFollower`, the following law) give that step its controls, so they should be Agents' by then.
- **Accessibility after library stage 6.** Library stage 6 makes the world input (`IAcousticWorld`) for the audio.
  The aids read the same six types; one scene view made then serves both (decision 8).
- **People after physics stage 3.** Physics stage 3 makes `IPhysicsWorld`, the one view both sides fill; the
  people read an extension of it, not a second view.

So: physics 2 is preceded by stages 0, 1 and 2 of this survey (3.5 to 5.5 sessions); stages 3 to 5 and 7 and 8 go
beside physics 3 and 4 where the files allow; stage 9 follows library 6; stage 10 follows physics 3.

---

## 11. Enforcement

- **Before the projects exist:** `EnginePackagesTests.NoNewCrossings`, the ratchet of stage 0, binding the sources
  and counting every reference from a package's files to a package its layer does not allow.
- **After:** `EnginePackagesTests.EachPackageReferencesItsLayer`: each assembly's `GetReferencedAssemblies()` is the
  runtime, its allowed packages and its allowed NuGet packages (MemoryPack where a type is sent, Serilog). The
  compiler refuses a type that is not referenced; the test refuses the reference.
- **Statics:** the new packages hold no mutable static outside tables filled once and per-thread scratch. The
  registries and the ground water are per instance; the static facades go when their last reader moves.
- **Names:** the material name tests of 3.1 fail a test once fixed: no string literal of a material name outside
  the material table and the map data, checked by the ratchet's binder.

---

## 12. Risks

- **The wire.** Stages 1, 2, 3, 4 and 6 each change the wire hash. A client and a server of different builds refuse
  each other at login; they ship together. `EntityType` and `TerrainTileComponent` are positional: move, never
  reorder.
- **Approved sound.** Rain, thunder, running water, fire, engines and the doors were approved by ear on today's
  values. Moves must not move a sample; the render fingerprint and the emitter stream say so.
- **What is said.** The aids are heard all the time and guarded by nothing that records speech. Stage 0's replay
  comes before stage 7.
- **Partial classes.** `VehicleSystem` (ten files) and `WorldEditor` (sixteen) are single types; extracting laws
  from them is a refactor of tuned code.
- **Generators.** `city.json` and the places' maps are generated; a move that changes a generator's output changes
  the maps. Byte identity is the check.
- **Strings.** Model ids, material names, sound ids and JSON keys are not seen by the reader. The model kind table
  (audit P2) and the material table's aliases hold most of them; the name tests of 3.1 read properties since
  2026-10-16, and `MaterialNameTests` keeps new ones out.
- **Work in flight.** The audit's P3d step 6 and P4 came back as patches on 2026-10-12; physics stage 2 is next;
  the second half of geometry stage 6 is open. Each stage here is done when no branch is open on its files, and
  says so.

---

## 13. Decisions for Cody

Decided 2026-10-12: Cody approved all thirteen as recommended, with one change to 13. Physics stage 2 was already
under way on the other machine, so it goes ahead: each preset's mechanical half and sound half stay joined by the
preset's id, so the catalogue (stage 2 here) takes both over afterwards without renaming. Stage 0 runs beside it;
stage 1 runs beside it only where the two touch no file in common (`Chassis.cs`'s road surfaces and `TyreFriction`
are the likely ones), and otherwise after it; stage 2 follows physics stage 2.

1. **Materials as their own package.** Recommended: `OpenFPS.Materials` at the bottom, referenced by Physics,
   Acoustics, Sound, Environment and World; cost 1 session, nothing heard changes. Alternative: leave the table in
   Acoustics and let Physics reference Acoustics; Physics would then carry the acoustics with it, against the
   physics survey's rule that it references Geometry alone. Second alternative: the table in Geometry; Geometry
   would hold stuff as well as shape.
2. **Friction and restitution.** Recommended: one value of each per material, with a published source, combined
   for a pair by a stated rule (friction the geometric mean, restitution the larger, which is what Jolt does by
   default [recalled]), and a short table of measured pairs where the rule is wrong (rubber on asphalt, ice, the
   tyre surfaces `RoadSurfaces` has now). Alternative: a full table of pairs; about fifty materials make over
   1,300 pairs, and most would have no source.
3. **Where the catalogue sits.** Recommended: above Sound and Physics, a typed catalogue that hands out their
   presets; 95 calls from Sound and 16 from Environment turn round (2 to 3 sessions with library stage 5's slice).
   Alternative: a generic registry below Sound, into which Sound and Physics register their kinds; about 515
   typed references in the catalogue would be rewritten, perhaps 2 more sessions, and the catalogue would no
   longer know what a vehicle is.
4. **One Environment or three.** Recommended: one package for weather, water and fire. Fire reads the water's
   wetness and the wind; the water reads the rain. Alternative: Weather, Water and Fire each a project; three
   projects with references between all of them and nothing gained.
5. **World below Common.** Recommended: World under Common, with `EntityType` moved down, so the roads message and
   the tile states are World's values on the wire as `WheelState` is Physics'. Alternative: World above Common as a
   server package; roads and tiles stay in Common and nothing outside the server can read a map.
6. **The Python generators.** Recommended: one implementation for what is done twice. The world's tiles already
   build the real places' buildings in C# (`WorldBuildings.cs`, a port of `gen_osm.py`'s `build()`); port the rest
   of `gen_osm.py` (4,210 lines) into World behind a small command, and retire the Python, so a building rule is
   changed once. Keep `gen_city.py` and `gen_speedway.py` in Python while the city and the speedway are authored
   maps outside the world; cost 3 to 5 sessions, byte identity of the places' maps the check. Alternative: keep both
   and the rule "keep the two the same" by hand; every building change is made twice.
7. **Agents in two steps.** Recommended: the laws now (stage 5); the people after physics stage 3, on its view
   extended. Alternative: an agents' world view now; 2 to 3 more sessions, and a view physics stage 3 would define
   again.
8. **One scene view.** Recommended: library stage 6's world input made wide enough for the aids (positions, kinds,
   names, boxes, materials, doors, stairs, places), one interface both Audio and Accessibility read and the client
   fills once a frame. Alternative: two views; the client would fill both and they could disagree about what is
   where.
9. **Common's name.** Recommended: keep `OpenFPS.Common`, now only the wire and the entity model; nothing to
   change. Alternative: rename the project `OpenFPS.Protocol`; namespaces stay (library decision 6), so the cost is
   the project files and the scripts that name it.
10. **The voices and their lines.** Recommended: `Speech` (with `voices.csv` and `scripts.json`) in Models: who
    exists and what each can say. Alternative: a content package of its own, holding the lines and later the
    designed sounds (library decision 3).
11. **The wire hash.** Recommended: hash Materials, Environment, Models, World and Common; leave Agents,
    Accessibility and Editor out, so a change to an aid or the editor does not refuse logins. Alternative: hash
    everything, as Sound outside `Core/` is hashed today; safer and stricter.
12. **The transport.** Recommended: no shared network package now; the server and the client each wrap LiteNetLib
    their own way (about 600 lines), and the wire's types are Common's. Alternative: one transport package both
    use; small, and nothing would use it but open-fps.
13. **When.** Recommended: stages 0, 1 and 2 before physics stage 2 (3.5 to 5.5 sessions); stages 3 to 8 beside the
    physics stages as section 9 allows; stage 9 after library stage 6, stage 10 after physics stage 3.

---

## 14. What was not measured

- No package was built and no test was run; the sizes in sessions are estimates.
- Resonance's own code was not read: which of World, Models and Environment it would use is a guess from the
  library survey's notes.
- References by string (material names, model ids, sound ids, JSON keys) and through delegates are not in the
  counts; the four material name tests of 3.1 were found with `grep`.
- The Python generators were counted by lines and searched for their C# twins; their structure was not read.
- Split files (`Scopes.cs`, `Waves.cs`, `GroundChannels.cs`, `PedestrianSpeech.cs`, `VehicleSystem.Parking.cs`)
  are counted by type; whether each type moves cleanly with its members was not checked member by member.
- The crossings of the hosts (`VehicleSystem`, `CharacterSystem`, `WorldEditor` and the rest) were measured in a
  first pass with them inside the new packages and are quoted from it; the final counts leave them in the game.
- Jolt's default combine rules for friction and restitution (decision 2) are recalled, not checked.
- Which tests guard each aid was read from file names, not run.

---

## 15. Appendix: every file of the new packages

Every other file's package is in `packages_files.tsv` from the tool.

### Environment

| File | Lines | What |
|---|---:|---|
| `OpenFPS.Server/Systems/GroundWaterSystem.cs` | 516 | the water on the ground, by map |
| `OpenFPS.Server/Water/Drainage.cs` | 219 | one tile's drainage from its ground |
| `OpenFPS.Server/Water/DrainageNetwork.cs` | 659 | a map's drainage from its tiles' |
| `OpenFPS.Sound/Fire.cs` | 586 | what is burning: the fire's description, which its sound reads |
| `OpenFPS.Sound/FireShape.cs` | 245 | a fire's burning area |
| `OpenFPS.Sound/FireSpread.cs` | 797 | fire spreading through fuel |
| `OpenFPS.Sound/Fuel.cs` | 277 | what a thing burns as |
| `OpenFPS.Sound/GroundWater.cs` | 498 | hydrology of the ground |
| `OpenFPS.Sound/Lightning.cs` | 504 | strikes, their schedule and channel |
| `OpenFPS.Sound/Precipitation.cs` | 371 | what falls and its spectrum |
| `OpenFPS.Sound/Rain.cs` | 284 | rain rates and drop sizes |
| `OpenFPS.Sound/RoadWater.cs` | 455 | water on the road and its puddles |
| `OpenFPS.Sound/Runoff.cs` | 89 | rain on its way to the drains |
| `OpenFPS.Sound/WeatherType.cs` | 4 | the weather a map has, on the wire |
| `OpenFPS.Sound/WorldWeather.cs` | 65 | one world's weather state |
| By type: `WindWaves`, `WaterBody` (`Waves.cs`), `GroundChannelKind` (`GroundChannels.cs`) | | |

### Models

| File | Lines | What |
|---|---:|---|
| `OpenFPS.Common/Ammunition.cs` | 101 | what a round and a fire selector are |
| `OpenFPS.Common/Scopes.cs` | 257 | what a scope is (`ScopeMath` and `ScopeSway` stay in the game) |
| `OpenFPS.Common/Speech/Speech.cs` | 269 | the voices: who exists and what each can say |
| `OpenFPS.Sound/Editing/ModelKinds.cs` | 354 | each kind's fields for the editor |
| `OpenFPS.Sound/Machines.cs` | 661 | machines as parts lists, the registry |
| `OpenFPS.Sound/ModelKindTable.cs` | 363 | the one table of model kinds |
| `OpenFPS.Sound/ModelLibrary.cs` | 375 | the models a map can name |

### World

| File | Lines | What |
|---|---:|---|
| `OpenFPS.Common/RoadMapData.cs` | 73 | roads as sent to a driver |
| `OpenFPS.Common/Roads.cs` | 358 | roads as a map declares them |
| `OpenFPS.Common/Tiles.cs` | 120 | tile detail and streaming radii |
| `OpenFPS.Common/TrackClearance.cs` | 124 | a map's drive route checked at load |
| `OpenFPS.Server/Core/MapRoadsBuilder.cs` | 42 | the roads message from a map |
| `OpenFPS.Server/Core/TerrainBuilder.cs` | 260 | grading the survey (its entity making is the host's) |
| `OpenFPS.Server/OneWorld/Elevation.cs` | 183 | where a tile's heights come from |
| `OpenFPS.Server/OneWorld/Geocoder.cs` | 48 | an address to a place |
| `OpenFPS.Server/OneWorld/LandCover.cs` | 395 | what covers the ground |
| `OpenFPS.Server/OneWorld/Osm.cs` | 256 | OpenStreetMap ways |
| `OpenFPS.Server/OneWorld/Overture.cs` | 665 | building footprints |
| `OpenFPS.Server/OneWorld/Utm.cs` | 78 | the UTM grid |
| `OpenFPS.Server/OneWorld/WorldBuildings.cs` | 1,208 | a building's shell, as `gen_osm.py` builds it |
| `OpenFPS.Server/OneWorld/WorldFeatures.Buildings.cs` | 595 | the buildings of a world tile |
| `OpenFPS.Server/OneWorld/WorldFeatures.Clear.cs` | 103 | nothing laid along a line goes into a building |
| `OpenFPS.Server/OneWorld/WorldFeatures.cs` | 871 | what stands on a world tile |
| `OpenFPS.Server/OneWorld/WorldSettings.cs` | 53 | the world's settings |
| `OpenFPS.Server/OneWorld/WorldStore.cs` | 334 | the world's tiles on disk |
| `OpenFPS.Server/OneWorld/WorldTile.cs` | 105 | one tile as the store keeps it |
| `OpenFPS.Server/OneWorld/WorldTileKey.cs` | 51 | which tile |
| `OpenFPS.Server/OneWorld/WorldTileService.cs` | 331 | tiles made on demand |
| `OpenFPS.Server/Repositories/CompositeRepository.cs` | 144 | composite templates and placements |
| `OpenFPS.Server/Repositories/MapElevation.cs` | 104 | a map's ground heights |
| `OpenFPS.Server/Repositories/MapRepository.cs` | 703 | the map file format |
| `OpenFPS.Server/Repositories/PrefabTemplate.cs` | 329 | the prefab format |
| `OpenFPS.Server/Repositories/PrefabValidator.cs` | 337 | the prefab format's checks |
| By type: `EntityType` (`Components.cs`), `WorldPlace` (`WorldMaps.cs`) | | |

### Agents

| File | Lines | What |
|---|---:|---|
| `OpenFPS.Common/LaneRoutes.cs` | 259 | a vehicle's tour of lanes |
| `OpenFPS.Common/LineFollower.cs` | 179 | a driver steering to a line |
| `OpenFPS.Common/RaceLine.cs` | 396 | speeds along a line |
| `OpenFPS.Server/Systems/VehicleSystem.Crosswalks.cs` | 370 | where a walker crosses a road (extract) |
| `OpenFPS.Server/Systems/VehicleSystem.Following.cs` | 144 | the gap to the car in front (extract) |
| `OpenFPS.Server/Systems/VehicleSystem.Junctions.cs` | 276 | who goes first (extract) |
| `OpenFPS.Server/Systems/VehicleSystem.Routes.cs` | 183 | tours of lanes from the road network (extract) |
| `OpenFPS.Server/Systems/VehicleSystem.StreetLife.cs` | 185 | the street's random events (extract) |
| `OpenFPS.Sound/AircraftGroundRun.cs` | 176 | an aeroplane driven along its runway |
| `OpenFPS.Sound/MachinePower.cs` | 98 | how hard a driver works a machine |
| By type: `ParkState`, `ParkPhase`, `ParkingSpot` (`VehicleSystem.Parking.cs`), `StreetLines`, `SpeechConditions` (`PedestrianSpeech.cs`) | | |

### Accessibility

| File | Lines | Crossings |
|---|---:|---:|
| `OpenFPS.Client.Core/BeaconAids.cs` | 716 | 96 |
| `OpenFPS.Client.Core/DrivingAids.cs` | 967 | 104 |
| `OpenFPS.Client.Core/DrivingCueSounds.cs` | 144 | 0 |
| `OpenFPS.Client.Core/ListMenu.cs` | 178 | 0 once the keys move |
| `OpenFPS.Client.Core/ListeningCalibration.cs` | 187 | 0 once the keys and `Speech` move |
| `OpenFPS.Client.Core/MapTracker.cs` | 355 | 133 |
| `OpenFPS.Client.Core/NamedPlaces.cs` | 45 | 29 |
| `OpenFPS.Client.Core/Platform/ISpeechOutput.cs` | 19 | 0 |
| `OpenFPS.Client.Core/Platform/SpeechDispatcherOutput.cs` | 63 | 0 |
| `OpenFPS.Client.Core/Platform/SpeechLog.cs` | 28 | 0 |
| `OpenFPS.Client.Core/SightGrid.cs` | 276 | 98 |
| `OpenFPS.Client.Core/SightWatch.cs` | 585 | 68 |
| `OpenFPS.Client.Core/Sightline.cs` | 177 | 55 |
| `OpenFPS.Client.Core/StairCues.cs` | 278 | 23 |
| `OpenFPS.Client.Core/TurnNarration.cs` | 79 | 0 |
| `OpenFPS.Client.Core/UiSounds.cs` | 285 | 30 |
| `OpenFPS.Client.Core/WallBumps.cs` | 222 | 67 |
| `OpenFPS.Client.Core/ZoneSettle.cs` | 53 | 0 |
| `OpenFPS.Common/Beacons.cs` | 72 | 0 |
| `OpenFPS.Common/DrivingCuePlanner.cs` | 662 | 0 |
| `OpenFPS.Common/LaneGuide.cs` | 95 | 0 |
| `OpenFPS.Common/PlayerCoordinates.cs` | 28 | 0 |
| By type: `GameKey` (`Platform/GameKey.cs`), `KeyModifiers` (`Input/InputCommandMapper.cs`) | | |

### Editor

| File | Lines | Crossings |
|---|---:|---:|
| `OpenFPS.Client.Core/BuildForm.cs` | 370 | 0 once the keys move |
| `OpenFPS.Client.Core/EditorDialog.cs` | 1,438 | 0 once the keys move |
| `OpenFPS.Client.Core/EditorKeys.cs` | 41 | 0 once the keys move |
| `OpenFPS.Client.Core/EditorValuePrompt.cs` | 113 | 0 |
| `OpenFPS.Server/Core/BuildSession.cs` | 95 | 0 |
| `OpenFPS.Server/Editor/EntitySettings.cs` | 167 | 0 |
| `OpenFPS.Server/Editor/LiveState.cs` | 73 | 6 |
| `OpenFPS.Server/Editor/MapOverlay.cs` | 339 | 9 |
| `OpenFPS.Server/Editor/MapVersions.cs` | 148 | 0 |
| `OpenFPS.Server/Editor/ModelCatalog.cs` | 293 | 18 |
| `OpenFPS.Server/Editor/ModelStore.cs` | 213 | 0 once `SafeText` moves |
| `OpenFPS.Server/Editor/OverlayRoute.cs` | 58 | 0 |
