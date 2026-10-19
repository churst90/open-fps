# The sound library's versions

What each tagged version of open-fps's sound library holds, and what a host using it must do. Newest first. The
library is a set of projects inside this repository; a host takes the repository as a git submodule pinned to a
tag (docs/SOUND_LIBRARY_BOUNDARY.md, stage 8 and decision 8) and references the projects it needs.

## Unreleased

- The scene view says more about each body, for the accessibility aids (open-fps's engine packages stage 9):
  `AcousticBody` gains `Name`, `Prefab`, `Beacon`, `Team`, `Kind` (the new `BodyKind`), `Named`, `Announce`,
  `HasWheels`, `SynthRunning` and, for a vehicle, every wheel's contact (`Wheels`, a
  `ReadOnlyMemory<WheelRoadContact>`, each wheel's `Surface` and `WaterMm`; empty without wheels) instead of a
  single worst wheel, all `init` properties that default to empty, false or 0. `IAcousticWorld`
  gains `Markers(List<AcousticBody>)`, the fixed named markers that are not solid. A host that implements
  `IAcousticWorld` must add `Markers` (clearing the list is enough if it has none); it fills `Wheels` from its
  wheel states if it wants the driving cues to plan braking on the least grip under every wheel, and keeps that
  memory valid while the view is held. The body also says where the host's triangle world holds it (`Geometry`,
  the new `BodyGeometry` enum: `None`, `Static`, `SightOnly`, `Mover` or `Unindexed`), its own region's name and
  whether it is indoors (`RegionName`, `RegionIndoor`), and the two regions a portal joins (`RegionAId`,
  `RegionBId`), all `init` properties that default to `BodyGeometry.None`, empty or 0. A host that implements
  `IAcousticWorld` may leave them unfilled: the aids then see every fixed thing as a box and no regions, as
  before. `EntityAcoustics` fills them for open-fps. The audio reads none of the new fields, so nothing heard
  changes.
- A fire and a wood are cut at the ear like running water (changes.md, 2026-10-18; docs/FIRE.md 12.13): new
  `ExtendedSources` members for it (`CutPoints`, `PlacePoints`, `FireOutline`, `FlamesFor` and their kin),
  `ExtendedSources.CutArea` gains optional `fire`, `flames`, `rise`, `origin` and `turn` parameters (a recompile
  binds them; a host calling it for an open water changes nothing), and `WideSources` gains `IsFire`, `IsWood`,
  `IsArea` and `IsCut`. A host using `AudioWorld` need do nothing.
- A 16-bit buffer registered with `RegisterSynthesisedSound` and played as a loop loops; it played once. A host need
  do nothing.
- Speakers sharing a program keep one track clock (Resonance's 3.3; changes.md, 2026-10-17). A loudspeaker's program
  (its recording and repeat interval) is timed on the audio clock's world time (`IWorldClock.WorldTime`), so every
  speaker of it is where that clock has it, each heard after its own flight; a repeating line leaves every horn at
  once, and a voice coming back joins mid-line or mid-loop, faded in over 80 ms. `SpatialEmitter` gains
  `ProgramStart` at its end (the world time its program's top left the speaker; 0 for every other voice). A host
  using `AudioWorld` and the FMOD mixer need do nothing, as long as its `IAudioClock.WorldTime` is one its clients
  share (`StopwatchAudioClock`'s is: UTC). A host with its own `IAudioProvider` starts a voice with a `ProgramStart`
  where `SharedPrograms.Join` says, with that delay in place of `DelayMs`, or every speaker of a program starts from
  its top as before. A repeating sound that is not a loudspeaker is staggered by its id as before.
- A door with open air on either side takes the outdoor seal (0.3 mm) by default, the open air being the outdoors or
  any place whose `Room.IsIndoor` is false, whatever surfaces it has (`OpeningRoutes.IsOpenAir`, new). A host that
  sets `IsIndoor` on its rooms need do nothing. A host that leaves it false on rooms with surfaces (it is false
  unless set) finds the doors between them sealed as if to the outdoors, 20 to 25 dB tighter in the middle band:
  set `IsIndoor` true on every room that is indoors, or give its doors their own gaps.
- `OpeningRoutes.FieldAt` steps out of each doorway on the side `ASide` says, not on the assumption that `NodeA`
  lies along `Normal`. A host need do nothing; one whose openings name their places against the normal (most of
  open-fps's) finds the point a field is heard from (`inField`) outside the listener's room now, where it was inside
  it, and the fields routed on through a room a little changed. Without a `regionAt` (`ASide` 0) nothing changes.
- A one-off sound that moves as it plays has its echoes found again and moved with it (`OneOffSounds`; Resonance's
  2.6, which Resonance marks `SpatialVoice.Moves`). A host need do nothing new: a one-off moves when it is sent with
  `TransientSound.MovesTo` and `MoveSeconds` (its place at the end of the move, and how long it takes), as open-fps's
  sliding and lift doors are. A host that marks its own moving sounds sends them so, and their copies follow; a sound
  that stays costs nothing more.
- A machine's front outlet or cabin path restarted under its id no longer leaves the engine carrying its share
  beside the new one, and a voice reading another's synth follows the live voice of its id (Resonance's 3.2;
  changes.md, 2026-10-17). A host that drives `EngineVoiceState` itself: `SetCabinTapLive` is gone, and setting
  `SplitVoices` no longer stands for a tap. Make the tap (`new EngineTapState(engine)` or `(engine, path)`) and call
  its `Take()` and `HandBack()`; `SplitVoices = true` is left for an offline render that plays the front itself.
  `EngineTapState.Source` and `EngineEchoState.Source` are properties now (`Rebind` moves them). A host using
  `AudioWorld` and the mixer has nothing to do.
- A shut door lets through its gaps (`OpenFPS.Acoustics.DoorGaps`, added in `OpeningRoutes`). `DoorLeaf` and
  `OpeningRoutes.Declared` take two more fields at their ends, `GapMetres` and `UndercutMetres`, both optional. A host
  need do nothing: zero gives every door the default for where it hangs (3 mm round and a 10 mm undercut between two
  rooms, 0.3 mm to the outdoors). A host whose doors are sealed, or looser, fills them per door. A host that
  measured levels through shut doors will find them 10 to 40 dB louder, most in the high band. `Air` gains
  `Viscosity`, `Prandtl` and `HeatCapacityRatio`.
- A doorway route leaves from the wall's face on its own room's side (Resonance's 1.2). A door's
  `OpeningRoutes.Opening.Centre` is the middle of the wall now, not of the leaf, and `HalfDepth` half the wall from
  face to face; a leaf hung at one face of its wall is no longer heard through the jamb. `Opening` gains `ASide`, the
  side of the opening its `NodeA` lies on (+1 along `Normal`, -1 against, 0 unchecked), set when the graph is given
  a `regionAt`. A host need do nothing; one that reads a door opening's `Centre` finds it moved by however far the
  leaf hangs off the wall's middle.
- A map entity (`OpenFPS.Server.Repositories.EntityData`, in OpenFPS.World) gains `DoorGapMetres` and
  `DoorUndercutMetres`, a door's gaps where it is placed, over its prefab's. A host need do nothing; one that loads
  open-fps maps with its own loader may read them into its `DoorLeaf`.

## library/v0.1.0 (2026-10-17)

The first version. Below 1.0 because the host interfaces may still change between versions; each change will be
said here with what a host must do.

### What is in it

The stack, each project referencing only those below it, none referencing open-fps's game:

- `OpenFPS.Audio`: the runtime. `AudioWorld` places and ranks every sound; the FMOD mixer and voices; Steam Audio
  (binaural, traced reverb and echoes); the binaural pool; the acoustic worker; one-off sounds; birds; rain on the
  head; the door render cache.
- `OpenFPS.Sound`: what the sounds are. Engines, exhausts and intakes, tyres, doors, glass, water, fire, wind, sirens
  and horns, trains, aircraft, footsteps; the hearing and loudness model; the mix settings.
- `OpenFPS.Acoustics`: how sound travels. Reflections, wall transmission, openings, rooms from their floods, the
  ground by its impedance, bending by wind and the air's layers.
- `OpenFPS.Environment`: weather, rain, lightning, runoff and ground water, road water, fire spread, the air's
  surface layer.
- `OpenFPS.Models`: the catalogue of presets by id.
- `OpenFPS.Physics`: bodies on foot, the vehicle step, tyres, the mechanical halves of engines and vehicles,
  contacts.
- `OpenFPS.Materials`: the material table, air, road surfaces.
- `OpenFPS.Geometry`: the triangle world, BVH, shapes, tiles.
- `OpenFPS.Native`: loading FMOD and Steam Audio; subnormal flushing on render threads.
- `OpenFPS.Schema`: field descriptions for an editor.

Optional, also separated: `OpenFPS.World` (maps, prefabs, roads, OpenStreetMap tiles), `OpenFPS.Agents` (traffic and
rail laws), `OpenFPS.Accessibility` (keys, speech output, beacons, driving aids), `OpenFPS.Editor`, `OpenFPS.Words`,
`OpenFPS.Astronomy`.

### What a host gives it

- An `AudioEngineFacade`, made with the host's `AudioDataPaths(Sounds, RenderCache, ShippedRenderCache)`: where the
  sound bank and the door render caches are.
- `new AudioWorld(facade, recordings, host, clock, manualAcoustics, seed, prewarm)`, then `Tick(world, sources)`
  once a frame.
- `IAcousticWorld` (in Acoustics): the triangle world and its version, the rooms, the air, every thing with a box,
  the moving things, door leaves and their doorways, the terrain solids. It must be an immutable view per frame:
  the acoustic worker keeps the one it was handed.
- `IAudioSources` (in Audio, `Data/AudioSources.cs`): every sounding thing by id, each a `SourceSpec` (its emitter,
  field by field, including its own range, minimum distance and, for a line source, its `Line`) and a `SourceState`
  (pose, velocity, tyre demand, wheels, horn and siren bits, lever); the audible and moving things in the host's
  order; the sample time; the woods; what is falling; rain rate; road water; the outdoor air.
- `IAudioHost`: the listener now, the floor under a point, and two hooks (the listener placed, the rooms set).
- `IRecordings`: the file for a sound id, and the category of a foot or a blow on a material.
- `AudioWorldOptions`: the shipped options, or `AudioWorldOptions.FromEnvironment()` to read the `OPENFPS_*`
  variables (voice budgets, wide sources, cabin paths, traces).

### Native libraries

FMOD and Steam Audio's native libraries are in `lib/` (`libfmod.so`, `libfmodstudio.so`, `libphonon.so`;
`fmod.dll`, `phonon.dll` on Windows). A host copies them beside its program as open-fps's builds do. FMOD is
licensed by Firelight Technologies: a host shipping it needs its own FMOD licence.

### Moving from a copy of the code (Resonance)

Namespaces now follow their projects (docs/SOUND_LIBRARY_BOUNDARY.md section 21). Replace `using OpenFPS.Common;` and
the old client namespaces with what the code uses:

| Old | New |
|---|---|
| `OpenFPS.Common` (geometry types), `OpenFPS.Common.Geometry` | `OpenFPS.Geometry` |
| `OpenFPS.Common`, `OpenFPS.Common.Systems` (acoustics) | `OpenFPS.Acoustics`, `OpenFPS.Acoustics.Systems` |
| `OpenFPS.Common`, `OpenFPS.Common.Hearing`, `OpenFPS.Client.AudioEngine.Core.*` (sound models) | `OpenFPS.Sound`, `OpenFPS.Sound.Hearing`, `OpenFPS.Sound.Core` and `.Engine`, `.Nature`, `.Signals`, `.Rail`, `.Aircraft`, `.Pneumatics`, `.Yard`, `.Stove` |
| `OpenFPS.Client.Core.Platform`, `...AudioEngine.SteamAudio` (native) | `OpenFPS.Native.Platform`, `OpenFPS.Native.SteamAudio` |
| `OpenFPS.Client.Core`, `OpenFPS.Client.AudioEngine.{Core,Fmod,Acoustics,Data}` | `OpenFPS.Audio`, `OpenFPS.Audio.Core`, `.Fmod`, `.SteamAudio`, `.Acoustics`, `.Data` |
| `OpenFPS.Common` (materials, air, road surfaces) | `OpenFPS.Materials` |
| `OpenFPS.Common`, `OpenFPS.Common.Networking` (`WheelState`) | `OpenFPS.Physics` |
| `OpenFPS.Common` (weather), `.Components` (`WeatherType`), `OpenFPS.Server.Water` (drainage) | `OpenFPS.Environment`, `OpenFPS.Environment.Water` |
| `OpenFPS.Common` (catalogue, `Speech`), `OpenFPS.Common.Editing` (`ModelKinds`) | `OpenFPS.Models`, `OpenFPS.Models.Editing` |
| `OpenFPS.Common.Editing` (`TunableAttribute`, `FieldDescriptor`) | `OpenFPS.Schema` |

Traps:
- Inside a host's own `OpenFPS.*` namespaces, bare `Environment`, `Sound` and `Geometry` now mean open-fps
  namespaces. Write `System.Environment`, `FMOD.Sound` and `FMOD.Geometry`.
- The voices' embedded resources are now named `OpenFPS.Models.Speech.voices.csv` and `.scripts.json`.
- No saved data changes: MemoryPack bytes and JSON are as they were, and old maps, models and presets load.

What changed in behaviour since Resonance's copy was last brought up to date (2026-10-14), each said in open-fps's
changes.md with its measurements:
- The binaural pool goes by how loud a voice is at the ear, not by who asked first; reverb heads have a reserve.
- A room's own reverb stays binaural when its bus is made with the pool dry.
- No voice stops with a click: every stop fades to silence in 80 ms.
- Running water, shores and water features are heard from their whole extent, cut at the ear into pieces.
- The ground by its impedance; sound bent by wind and the air's layers; the air's stir from its surface layer; the
  wind's direction from the weather.
- Render threads flush subnormal numbers.
- Global settings became instances owned by the mixer: `Atmosphere`, `HearingSettings`, `MixSettings`, `LabLevers`.

### Known open items in this version

From open-fps's todo.md, all inside the library:
- A tap restarted within about 250 ms of its stop leaves its engine carrying its share too (a doubled front or cabin
  path, 4.3 dB at 20 m/s), and a fire's outer places can outlive a restarted fire. Being fixed first.
- A doorway route leaves from inside a thick wall when the leaf hangs at one face of it.
- Speakers sharing a program have no shared track clock.
