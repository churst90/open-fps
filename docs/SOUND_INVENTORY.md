# Sound inventory

_First written 2026-09-15. Brought up to date with the code 2026-10-05._

Every sound in the game, what makes it, and what still needs a recording.

The test for whether a sound is recorded or modelled:

> Does the sound carry information that varies with the thing, and does the engine already know
> what varies?

If yes, model it. A recording throws the variation away. Record only what is a fixed artefact of
one object or one person: a voice, a named signal, music.

Two routes carry almost everything:

- **One-off sounds** travel as a `TransientSound` from the server. If it has a `SynthKey`, the client
  renders it in `WorldAudioPlayer.RenderOne` by prefix. Otherwise `TransientSynth` renders it from
  its generic fields (character, pitch, decay, noisiness).
- **Running sources** are entity emitters with a `PhysicalKey` ("machine:", "aircraft:", "rail:",
  "siren:", "bell:", "horn:", "water:", "fire:", "foliage:"). `FmodAudioProvider` turns the key into
  a voice state. Vehicles have their own voice, `EngineVoiceState` (`EngineProcessor.cs`).

---

## Modelled

### Vehicles

| Sound | Model | Notes |
|---|---|---|
| Engine, intake, exhaust, block | `EngineSynth` (`AudioEngine/Core/Engine`) from an `EngineProfile` | Turbo, fan clutch (`CoolingSystem`) and starter are part of the engine model. |
| Tyres and squeal | `TyreFriction` (Common), inside `VehicleSynth` | Rendered at the device rate. |
| Brake squeal | `BrakeSqueal` | |
| Air brakes, kneeling, compressor | `AirSystem` (`Core/Pneumatics`) | Set by the vehicle's `AirSystem` name. |
| Bus door chime | `DoorChimeSpec.TransitBus`, in `EngineVoiceState` | |
| Power windows | `CarWindow` | Key from `WindowSystem` on the server. |
| Car door | `CarDoor` | Octave bands fitted to a recording (tools/car_door_fit). Fired by `OccupancyService`. |
| Collisions | `ImpactAcoustics.Between` | From `DrivingSystem`. |

### Signals, rail, aircraft, machines

| Sound | Model | Voice |
|---|---|---|
| Road and rail horns, whistles | `ElectricHorn`, `ChimeHorn`, `SteamWhistle` (`Core/Signals`); the hand's rhythm from `Honk` | `HornVoiceState` |
| Sirens | `ElectronicSiren` | `SirenVoiceState` |
| Crossing bells | `StruckBell` | `BellVoiceState` |
| Trains | `TrainSynth` with `RailNoise`, `ElectricDrive`, `SteamFrontEnd` (`Core/Rail`) | `RailVoice` (one synth per train, a tap per source) |
| Aircraft | `AircraftSynth` | `AircraftVoiceState` |
| Mowers, air conditioners | `SmallMachineSynth` (`Core/Yard`) | `MachineVoiceState` |

### Nature

All three read the one wind field (`WindField`, `Wind.cs`), so a gust reaches each source in turn.
Models are in `AudioEngine/Core/Nature`; voices in `AudioEngine/Fmod/NatureVoices.cs`; presets in
`NatureModels.cs`.

| Sound | Model | Voice | Prefab and preset |
|---|---|---|---|
| Fountain | `FallingWaterSynth` | `WaterVoiceState`; per tap `WaterTapState` over one `WaterFeatureVoice` | `water_fountain`, "water:park_fountain"; on the city `elm_fountain_water_0..4`, "water:park_fountain/elm_park/0..4" |
| Wood fire | `FireSynth` | `FireVoiceState` | `fire_pit`, "fire:fire_pit" |
| Wind in a tree | `FoliageSynth` | `FoliageVoiceState` | `tree_crown`, "foliage:park_tree" (also "pine") |

`EventSum` and `Resonator` are the shared building blocks.

Also modelled since 2026-10-05, each with its own doc:

- Rain, sleet, snow and hail on what they land on: `RainSynth`, `RainPatch` (Nature).
- Thunder from the lightning channel: `Thunder.cs` (Core).
- Wind at your ears: `EarWindSynth` (Nature).
- Running water (creeks, gutters, drains, downpipes, taps and sinks): `RunningWaterSynth`;
  docs/RUNNING_WATER.md.
- Waves at a shore, a wall or a hull: `ShoreSynth`; docs/WAVES_AND_SHORES.md.
- Fire at any size, from a campfire to a crown fire (`/spawn fire PRESET`): `FireSynth`; docs/FIRE.md.
- Wet tyres, spray and puddles: `WetTyres.cs` (Core); docs/WET_ROADS.md.

### Doors, keys, knocks

The server picks the model in `DoorSystem` by `DoorKind`. Every event and which model plays it is in
docs/DOOR_TYPES_EVENTS.md.

| Door | Model |
|---|---|
| Hinged (knob or lever) | `KnobDoor` |
| Steel push bar | `PushBarDoor` |
| Glass push bar, glass pull | `GlassDoor` |
| Patio and automatic sliding | `SlidingDoor` |
| Lift landing doors | `ElevatorDoor` |
| A key in a lock | `LockCylinder` |
| Anything else | `DoorAcoustics` (`Doors.cs`), the generic panel model |
| Knock (Shift+E) | `DoorKnock`, fitted to a recording |

### Weapons and bullets

| Sound | Model |
|---|---|
| Report | `WeaponSynth.MuzzleBlast` ("weapon:" key). No recorded layer. |
| Reload, dry fire, fire selector | `WeaponHandling` |
| Crack and whizz going past | `BulletFlyby` |
| Strike and debris | `BulletImpact` |
| Ricochet whine | `Ricochet` |
| Window shot out | `GlassFracture`; `GlassBreak` (`Glass.cs`) decides what breaks |

### Designed sounds

Not real objects, so designed rather than modelled. Built from `DesignedSoundKit`, rendered by
`AdminGunSynth.TryRender`.

| Sound | Source | Fired by |
|---|---|---|
| Admin gun report, modes, hits ("admingun:") | `AdminGun` | `CombatService.AdminGun.cs` |
| Teleporter ("teleporter:") | `TeleporterSounds` | `Teleporter.cs` |
| Something handed to you ("give:") | `HandOverSounds` | `CommandHandler.Scope.cs` |

### Bodies and impacts

| Sound | Model | Fired by |
|---|---|---|
| Breathing | `Breathing`, rendered by `TransientSynth` | `LocalPlayerController`, `OtherBodies` |
| Walking into a wall | `ImpactAcoustics.Between` | `WallBumps` (client) |
| A dropped item landing | `ImpactAcoustics.Between` | `HandsService` |
| A body falling on death | `ImpactAcoustics.Between` | `CombatService` |
| Clap, crowd applause | `Applause` | `/clap`; `CrowdSystem` for crowds |

### Interface and aids

All synthesised in code. Interface sounds play in the head, not in the world; beacons and lane
ticks are world sounds at the thing.

| Sound | Source |
|---|---|
| Menu, chat, presence cues | `UiSounds` |
| Scope sounds | `ScopeSounds` |
| Beacons (door, item, vehicle, stairs, player, teammate) | `BeaconAids` |
| Lane guide, kerb, turn and aligned ticks | `DrivingAids` |

---

## Recorded

Folders are under `OpenFPS.Client/ASSETS/SOUNDS`.

| Sound | Where | Notes |
|---|---|---|
| Footsteps and landings | `FOOTSTEPS`, `LANDING` | Folder by material (`SoundMappingService`). `TakeLevels` evens out each take. Kept as recordings by measurement: the impact model was 31 dB off on wood. |
| Speech | `VOICES` | Cody's TTS lines. `Speech` keys from `PedestrianSpeech` and drivers (`VehicleSystem.Drivers.cs`). |
| Birds | `BIRDS` | One call per file; `BirdLife` places them by habitat. |
| Announcements | `ANNOUNCE` | The `pa_speaker` prefab. |
| Authored beacons | `BEACONS` | Prefabs such as `chirp_beacon`, `space_megaphone`. |
| Music | `music` | |

Live audio, not files:

- **Voice chat**: other players' Opus streams, through a jitter buffer (`TalkerVoice.cs`), played at
  their mouth.
- **Your own voice**: your microphone, heard only as your room answers it, never dry (`OwnVoice.cs`).

---

## Not built yet

- **Clothing and carried gear.** Rustle and knock, driven by the gait.
- **Water you touch.** Footsteps in it, things dropped into it. `FallingWaterSynth` has the bubble
  model to start from.

## Not wanted

- **Ambience beds.** No recorded loops over a map. The code still has a map `AmbienceId`; every
  shipped map leaves it empty.
- **Recorded gunshots.** The takes in `WEAPONS` are the spec the synthesis is measured
  against. Nothing in the game plays them.

## Reference recordings still worth having

Not to play. To measure a model against.

- A car crash or any heavy metal impact. Collisions have no measurement behind them.
- Breathing: at rest, after a jog, after a hard sprint.
