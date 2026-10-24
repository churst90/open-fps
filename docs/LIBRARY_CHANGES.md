# The sound library's versions

What each tagged version of open-fps's sound library holds, and what a host using it must do. Newest first. The
library is a set of projects inside this repository; a host takes the repository as a git submodule pinned to a
tag (docs/SOUND_LIBRARY_BOUNDARY.md, stage 8 and decision 8) and references the projects it needs.

## Unreleased

- A host can play its own flat (non-spatial) sounds, such as music and menus, through one door, and set the master and
  the world's volume while running. The library adds no player and no music logic. `AudioEngineFacade.FmodSystem` (FMOD's
  system, to create the host's sounds with) and `HostGroup` (a channel group under the master, beside the interface's) are
  valid from the mixer's start to its `Dispose`; a sound played into the group passes the master fader, the limiter and the
  dither like every other, and the world fade, the world volume and the muffle do not touch it. The host must not release
  the group, remove it from the master, add units to the master, set the master's volume (use `SetMasterVolumeDb`) or the
  group's own volume (the library holds that against the world fade; set each channel's instead), and it stops its channels
  before the dispose. `SetMasterVolumeDb(db)` is the level of everything that leaves the mixer, on top of
  `OPENFPS_MASTER_DB` (which stays the run's starting trim) and of the world fade, ahead of the limiter and the dither, and
  it scales the interface and the host's sounds too. `SetWorldVolumeDb(db)` is the level of everything heard in the world
  (voices, reflections, reverb, the wind at the ears) and leaves the interface and the host's sounds alone; it multiplies
  `SetWorldFade`, which stays the game's own 0 to 1 fade for the load screen. Both go from -80 to +12 dB (0 is as the run
  began, a number that is not a number is 0) and FMOD ramps the change over one mixer block, about 21 ms. Neither is touched
  unless called, and the mix of a host that calls neither is as it was; every world sound now sits under one `World` group
  under the master (a steady placed tone measured the same, to 0.001 dB, with and without the group).

- A host can put a low-pass over the whole world, such as a head under water: `AudioEngineFacade.SetWorldMuffle(cutoffHz)`.
  It filters the world's mix (voices, reflections, reverb, the wind at the ears) and not the interface or the host's flat
  sounds. 0, a negative or a non-finite number is off, and off is the default, in which the unit copies its input so the
  game's mix is as it was. It is one two-pole filter on the world group; the cutoff glides in octaves over about 40 ms, so
  a change never steps. It is apart from the cabin's enclosure (which is per voice) and the two stack. In the headless
  test a 1 kHz cutoff took a 6 kHz tone down by more than 25 dB and left a 200 Hz tone within 1.5 dB.

- A host can sound a horn of a given model at a place with no vehicle: `AudioWorld.PlayHorn(horn, rhythm, at, facing = null)`,
  where `horn` is "electric:disc_pair", "air:truck_dual" and so on, `rhythm` is seconds on, off, on (a tap is one
  number, each from 0 to 20) and `facing` is a rotation whose forward the horn radiates along (+Z when null). It is the
  same synthesis, level and placing as a horn a car sounds on the street (`StartHorn`), from `at` as the horn's own point,
  and it ends when its rhythm has. The handle is a `PlacedSound`: `MoveTo`, `Stop`, `IsPlaying`; its `GainDb` does not
  apply, a horn's level being its model's. A bad horn, rhythm or place throws `ArgumentException`.

- The loader reads sounds under the facade's `Paths.Sounds`, where it read only under `ASSETS/SOUNDS` beside the
  program, so the bank and the loader agree (`FmodAudioProvider.Sounds.Folder`, set by the facade's constructor and by
  `InitializeSteppedByHand(soundsPath)`). A host whose sounds folder is elsewhere need only give `AudioDataPaths` that
  folder, and can drop any link it made under the program for development. An id that leaves the folder or names a
  network share (`\\` or `//`) is still refused and reads as missing; an id naming `ASSETS` is still a path from the
  program's own folder. The loader does not fall back to the program's folder when a file is not under `Paths.Sounds`.
- A host can hand the library a sound's bytes or a stream under an id (`AudioEngineFacade.AddSound(id, byte[])`,
  `AddSound(id, Stream)`, `RemoveSound(id)`; `SoundSource` underneath), for a game that keeps its sounds in an
  encrypted package in memory. The sound is played by that id like a file's, loaded as a decoded sample without
  blocking, and wins over a file of the same id; handed in again under an id it replaces the sound for the plays that
  start after, and a voice already sounding carries on. The library copies the bytes at the call, on the host's thread
  (a host must not call it from the thread that places sounds), and frees its copy at `Dispose`, after every sound
  made from it is released; the host may free its own at once. Hand a sound in before its first play. A sound of
  bytes is not in the sound bank's index (`AudioBank`), which lists files.
- A host can play a recording at a place and keep a handle to it: `AudioWorld.Play(sound, at, gainDb, loop, minDistance,
  range)` returns a `PlacedSound` with `MoveTo(position, velocity)`, `GainDb`, `Stop()` and `IsPlaying`. `sound` is an id
  under `Paths.Sounds` or one handed in as bytes; the gain is decibels over the recording as authored, heard at
  `minDistance` (the unit `PlayReferenceVoice` takes). It is a source of the world beside the host's own, so the
  world's frame places it by the one path every recording takes: occlusion, routes through openings, reflections,
  reverb sends, HRTF, the voice budget, and the registry's voice (`VoiceKind.Recording`). It is heard only while the
  host ticks the world (`Tick`), from the first tick after the call; with an empty `IAcousticWorld` it is heard dry at
  the place, with the distance, air and HRTF every voice has. A change is heard from the next tick. `Stop()` fades the
  voice over the mixer's usual short fade; a once sound ends by itself and its handle then reads as finished (as it
  does if it was never heard within `HostedSounds.StartGraceSeconds`, 10 s, or the world is left). A once sound the
  voice budget cannot hold ends too, and is not started over. The entity ids are given from `HostedSounds.FirstId`
  (2,000,000,000) up, so a host's own entity ids must stay under it. Safe to call from any thread.

- Five materials a host can name: `Canvas`, `HollowDoor`, `Rock`, `SheetMetal` and `TimberFloor` (resonance indices 59
  to 63), each field with a source or a marked estimate on its line in `AcousticRegistry.AddMatter`. Until now a host
  had to play them as the nearest material (Fabric, Wood, Granite, Metal, Wood). A built thing is the single stuff that
  weighs and bends like it at its usual thickness, so `MaterialProperties` gains `BuiltThicknessMetres` and
  `SurfaceMassKgM2` (density times thickness: Canvas 0.41, HollowDoor 10, SheetMetal 7.85, TimberFloor 35 kg/m2; zero
  for a stuff). A host must do nothing: no map uses them yet and no existing row changes. To use one, make the box that
  thick (`BuiltThicknessMetres`), and give a floor its ceiling as the second leaf through `WallBuild` or an assembly,
  which the material cannot carry. The material table is part of the wire contract and of the door model's source, so
  the wire hash and the door fingerprint change: the server and the clients deploy together, and the door renders are
  made again before they are used.

- The mixer checks that every recording leaves its first sample, as Resonance found 58 in about 58,000 that did not:
  a recording a tenth of a second past its start on the parent's clock, not paused or virtual and still where it
  began, is counted and logged once (`[NEVER-STARTED]`, with its sound, delay, pitch and fade points), the counts are
  read from `FmodAudioProvider.StartCheck`, and the mixer says the totals as it closes. No sound changes, and the
  check costs two FMOD reads once per recording. open-fps did not show the fault: none of 63,000 footsteps started
  across the clock's edge (with and without pitch and fade points, binaural on and off) stuck, nor any of 2,244 judged
  on the busy street and at the city's login. A host must do nothing; one that restarts stuck recordings of its own
  can read this count to see whether it still needs to.
- A sound behind one barrier is heard from both its sides, each by its own strength (docs/CLIENT_NOTES.md, "The bearing
  follows the level"): `OpeningRoutes.BarrierGains` adds the shortest way round the other side of the box that governs,
  by energy, and its `edge` is one point in the weighted direction of the two (over the middle of a 3 m deck, straight
  towards the source), where it was the one nearer edge and flipped between two steps. Levels over the middle rise by up
  to 3 dB; near one edge by under 1.5 dB. A host that places a voice from `edge` need do nothing; it
  gets a point, not a width, so a host that wants the wide image of a footbridge must widen the voice itself.
- One bad source can no longer take the mix with it. `SourceSpec` now starts as an emitter does (cone 360, 360, 1 and
  `SynthRunning` true) when made with `new SourceSpec()`; a host that filled one from `default(SourceSpec)` should use
  `new SourceSpec()` or set the fields. A source whose level, range, size, cone or aim is not a finite number is not
  placed (one log line, by source id), a synthesised source naming a model that did not resolve is silent instead of
  playing as a plain oscillator, and a voice whose cone lets nothing out is logged once and sends nothing to the reverb.
  A host must do nothing; an emitter that relied on the old plain oscillator for an unresolved model id will be silent.
- A switched-off engine no longer leaves a steady level (a DC offset) in the mix: the live engine voice
  (`EngineVoiceState`) fades out the block tap's slow part, the cylinders' trapped gas, over half a second once the
  engine stops. `EngineSynth` and its stored renders are unchanged. The model kinds gain `ModelKind.DeclaresLevel`.
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
- `AudioEngineFacade.Dispose()` now stops and joins everything the audio started, and `AudioWorld` is `IDisposable`.
  In order: the facade's public calls become no-ops (nothing new is queued), the audio thread and every owned world's
  acoustic worker, door prewarm, thunder and survey tasks are told to stop and waited for (10 s each, a late one logged
  by name), the FMOD voices, DSPs and system close, and then the Steam Audio traces, scenes and context the mixer thread
  read are freed. A second call, or a call before `Initialize`, does nothing, and `Initialize` after `Dispose` starts
  nothing. A world made on a facade is stopped and freed by the facade's dispose; `AudioWorld.Dispose()` alone stops its
  threads and, while the mixer runs, leaves the shared Steam Audio objects to the facade. If a thread does not stop, what
  it could be using is left to the process and logged rather than freed under it. Mixer sounds still loading are waited
  for (3 s) before release. No sound changes. A host must call `facade.Dispose()` when it is done; one that disposed
  `AsyncAcousticWorker` itself before the facade should stop doing that, since the traces are read until the device closes.
- Headless mode, three public pieces. `new FmodAudioProvider { NoSound = true }` (or `OPENFPS_FMOD_OUTPUT=nosound`) uses
  FMOD's NOSOUND output: no sound card is needed and the mixer thread still mixes in real time. (NOSOUND_NRT was not
  chosen: it mixes inside `System.update`, faster than real time, so a sound would start at other times than on a
  device.) `facade.InitializeSteppedByHand()` brings the facade up without its audio thread and `facade.StepByHand()`
  runs one audio frame on the caller's thread; they replace the internal `InitializeForTest` and `PumpForTest`, which
  are gone. `facade.MixPeakDb` reads the highest true peak of the mix in dBFS (NaN where the mixer has no meter), so a
  host can check that its mix is not silent. A `WavPath` still wins over `NoSound`. `IAudioProvider` gains
  `MixPeakDb` with a default of NaN, so providers already written need no change.

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
