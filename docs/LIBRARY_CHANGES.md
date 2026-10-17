# The sound library's versions

What each tagged version of open-fps's sound library holds, and what a host using it must do. Newest first. The
library is a set of projects inside this repository; a host takes the repository as a git submodule pinned to a
tag (docs/SOUND_LIBRARY_BOUNDARY.md, stage 8 and decision 8) and references the projects it needs.

## Unreleased

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
