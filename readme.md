# OpenFPS

OpenFPS is a multiplayer game engine played by ear. It is built for blind and visually impaired
players first: there are no graphics to rely on, so everything the world does is heard, and the
interface talks through your screen reader.

The world is simulated on a server and heard on each player's client. Every sound is placed in 3D
around your head (binaural), and every sound travels through the world the way real sound does: it
gets quieter with distance, is blocked and bent by walls, reflects off buildings and fills rooms with
reverb that comes from the room's actual size and materials.

## What makes it different

- **Sound from physics, not from sound files.** Engines, tyres, horns, sirens, trains, aircraft,
  lawn mowers, air conditioners, doors, bells and footsteps are synthesised from how the real thing
  works: cylinders firing into exhaust pipes, a reed on a horn, a wheel on a rail. Change a car's
  exhaust or a door's material and it sounds different without anyone recording anything.
- **Acoustics from geometry.** Rooms are found from their walls: a room is a name and a point, and its
  air is flooded out from that point to the walls, floors, ceilings and shut doors round it. Its volume,
  its openings and what lines each side of it are measured from them. Reverb, echoes off facades, sound
  through doorways and sound around corners all follow from that. Nothing is set per room by hand, and
  no recorded ambience is played over any place.
- **Measured, not guessed.** Levels are anchored to real figures (a horn's legal loudness, a car's
  pass-by level) and checked by tests. Sounds are compared against recordings where recordings exist.
- **Accessible by design.** Speech through your screen reader, keys chosen so they do not clash with
  screen reader keys, menus with distinct sounds, spoken coordinates, and audio aids for finding your
  way, crossing roads and driving.

## Features

### Playing
- Walk, run and explore by sound. Footsteps change with the ground and your speed. Walking into
  something strikes it, so it sounds of its material, size and shape, and names it; you can knock on
  or tap what is in front of you. The game can say what is ahead as you turn and move.
- Maps: a generated city with streets, traffic, buses, a light rail loop, level crossings, an
  airport, houses with gardens, a park with a fountain, birds, and buildings you can enter; a
  speedway with a race; a rooms map for trying doors and materials; and two real places made from
  open data, Magnolia, Texas and Albany, Oregon, with their real streets, buildings and addresses
  (listed as "magnolia tx" and "albany or").
- The world: one continuous world on real ground (USGS 3DEP elevation), built by the server tile by
  tile the first time anyone goes near a place, with land cover, roads, woods and buildings from open
  data. Arrive by place or street address (`/join world magnolia`, `/join world address ...`); the
  world round you is built behind a loading screen before you get there.
- Doors work as real ones do: knob doors, push-bar doors with closers (a fire door onto the stairs on
  every storey of the city's towers), keyed glass front doors, automatic sliding doors and patio
  doors. Each is pushed from one side and pulled from the other, and no door shuts on a person. E
  opens the door you face.
- Find your way: step through the nearest doors, entrances, stairs, items, people, vehicles or
  places with comma and period (a door a wall keeps from you is said to be "behind a wall"); spoken
  stair cues; spoken coordinates and facing.
- Drive: get into a parked car and drive it, with lane and kerb tones, a guide beep, turn clicks,
  lane assist and spoken road names. Ride the bus: it stops at bus stops and you can take a seat.
- Weapons with real handling: fire selectors, reloads that take the real time, a rifle scope on the
  keypad, and aim assistance from the hip. A player who dies leaves a body and a bag of their
  belongings, and comes back after 60 seconds.
- People: the city's pedestrians greet you, apologise when you bump into them and talk on the
  phone; drivers yell when something goes wrong. Recorded lines, placed in the world like any sound.
- Beacons: sounds that mark doors, items, vehicles, stairs and other players near you (a map can add
  exits and waypoints). Choose which kinds you hear. Beacons behind a wall are not played.
- Chat: map, all, private, team and server channels, each with its own sound. Voice chat, heard from
  where each player stands and muffled by walls.
- Teams, friends and profiles. F-key lists of players, maps and friends, which you can act on.
  Travel between maps with F6 or `/join`, or carry a teleporter.
- Maps of your own: any player can make a map, build on it with the world editor (F12, and Control+B
  to build quickly: floors, walls, roofs, doors, stairs, columns and other shapes), lay roads and
  railways, and invite people.
- Saved servers and settings. When you log out, your place and what you carry are kept.

### The sound engine
- Binaural 3D sound (Steam Audio HRTF) mixed by FMOD.
- Occlusion, diffraction around edges, transmission through walls and doors, and a moving vehicle
  blocking another vehicle's sound.
- Reflections from nearby surfaces, played from the walls they come off.
- Reverb traced from the real geometry and materials around you, arriving from the directions it
  comes from. Rooms inside rooms work (a bus shelter inside a street, a garage inside a car park).
- Doppler, air absorption over distance, and horn directivity.
- Physical synthesis of: petrol and diesel engines with their exhaust and intake systems, turbos,
  tyres, electric and air horns, sirens, trains and their horns and bells, air brakes, aircraft
  (propellers, jets, helicopters), small machines, doors and locks, footsteps, gunfire, water
  (including rain running off the ground into ditches and creeks), fire that spreads by what is there
  to burn, a gas hob, loudspeakers (a PA horn, a megaphone), things struck by hand or body by their
  material and shape, wind in trees, applause and crowds.

### Running a server
- One server can host several maps at once; players travel between them.
- Maps are JSON files; objects are prefabs. Materials decide how things sound.
- Accounts with roles (player, moderator, developer, administrator and a protected owner), custom
  roles and single permissions; bans. On a map they own, every player can build.
- An admin gun for the administrator: kill, vaporize, freeze or inspect what it hits.
- A message of the day, and a text (MUD) interface for testing a server without a client.
- The network sends only what changed: things at rest are not sent again. Clients must be built
  from the same version as the server, and are told so at login if not.

## Platforms

- **Server:** Linux. It is plain .NET 10, so other platforms should work but are not tested.
- **Client:** the Linux GTK client and the Windows client share the game, the keys and the main
  menu, including creating an account, voice chat, a Connecting window that keeps trying a server
  that is not up yet for 30 seconds (Escape cancels), and reconnecting after a dropped connection.
  The Windows client is built from Linux (`./publish-windows.sh`) and is less tested. See
  [docs/WINDOWS_AND_SERVER.md](docs/WINDOWS_AND_SERVER.md).
- `./publish-server.sh` packages the server for a VPS.
- Speech: speech-dispatcher (Orca, espeak-ng) on Linux; NVDA or SAPI on Windows.

## Getting started

- `./run-server.sh` starts a server (players land on the speedway); `./run-server.sh city` lands
  them on the city. Every map is loaded either way.
- `./run-gtk-client.sh` starts the Linux client.
- The user manual is in [docs/MANUAL.md](docs/MANUAL.md): playing, and running a server.

## For contributors

- C# on .NET 10. Networking: LiteNetLib. Entities: Arch ECS. Serialisation: MemoryPack.
  Audio: FMOD Core and Steam Audio (phonon). UI: GTK 4 (GirCore).
- Projects: the sound library (`OpenFPS.Geometry`, the triangle world; `OpenFPS.Acoustics`, materials,
  walls and reflections; `OpenFPS.Sound`, the sound models and synthesis; `OpenFPS.Native`, the FMOD and
  Steam Audio bindings), `OpenFPS.Common` (shared simulation and the wire), `OpenFPS.Server`,
  `OpenFPS.Client.Core` (client logic and the audio engine), `OpenFPS.Client.Gtk` (Linux client),
  `OpenFPS.Client` (Windows client), `OpenFPS.AudioLab` (measurement and rendering tools; `--help`
  lists them), `OpenFPS.Astronomy` (where the sun and the moon are), `OpenFPS.Words` (the compass points,
  said one way everywhere), `OpenFPS.Tests`.
- Build with `--artifacts-path` pointing outside the repository (see the run scripts). Do not use
  `dotnet run`: it writes `obj/` and `bin/` into the repository. The repository is on btrfs on the
  NVMe now, not ntfs3 (the run scripts' comments still say ntfs3).
- On the development machine builds go through `tools/build-local.sh` and test runs through
  `tools/test-local.sh` (a filter is required). Both are pinned to the last twelve cores at low
  priority; the test script has two slots, both on those twelve cores, so a third run waits.
- Tests: about 2,800 test methods in `OpenFPS.Tests`; the full suite runs on GitHub Actions (eight
  shards).
- Map and prefab authoring: [docs/AUTHORING.md](docs/AUTHORING.md). Maps of real places, and how
  to make another: [docs/MAPS_FROM_REAL_PLACES.md](docs/MAPS_FROM_REAL_PLACES.md). Their map data is
  (c) OpenStreetMap contributors (ODbL), with buildings from Overture Maps, addresses from the US
  National Address Database and Census TIGER/Line, and tree cover from ESA WorldCover 2021 (CC BY 4.0).
  The world's own tiles take their ground from USGS 3DEP, land cover from ESA WorldCover, roads from
  OpenStreetMap and buildings from Overture Maps.
- Planned work: [todo.md](todo.md). Recent changes: [changes.md](changes.md). Done items taken out of
  the todo: [docs/DONE_2026-10.md](docs/DONE_2026-10.md).
- SQLite's native library (under EF Core) is pinned to 3.50.3 for CVE-2025-6965.

## Documentation

Everything is in `docs/`. Plans and designs say where their stages stand near the top.

For players and server owners:
- [MANUAL.md](docs/MANUAL.md): the player's manual, and running a server.
- [WINDOWS_AND_SERVER.md](docs/WINDOWS_AND_SERVER.md): giving someone the Windows client, and a server
  they can reach. [WINDOWS_README.txt](docs/WINDOWS_README.txt) ships in the Windows zip.
- [SERVER_SECURITY.md](docs/SERVER_SECURITY.md): roles, logins, limits and what the server records.
- [LISTENING_SPOTS.md](docs/LISTENING_SPOTS.md): `/tp` spots to hear rooms, doors and the outdoors,
  checked to stand on, and what is still to be heard.

Building maps:
- [AUTHORING.md](docs/AUTHORING.md): the map and prefab format the loader reads, and importing meshes.
- [MAPS_FROM_REAL_PLACES.md](docs/MAPS_FROM_REAL_PLACES.md): Magnolia and Albany from open data, and
  how to make another.
- [WORLD_EDITOR.md](docs/WORLD_EDITOR.md): the in-game editor (F12, Control+B), its design and how it
  is built.
- [THE_CITY.md](docs/THE_CITY.md): the city map, as it is now. [THE_CITY_BLOCK.md](docs/THE_CITY_BLOCK.md):
  the first city block (2026-09-19), kept as a record.

Plans and designs:
- [GEOMETRY.md](docs/GEOMETRY.md): the triangle world, terrain, shapes, and rooms found from their walls.
- [WORLD_STREAMING.md](docs/WORLD_STREAMING.md): one world in tiles, generated on demand from open data.
- [SOUND_LIBRARY_BOUNDARY.md](docs/SOUND_LIBRARY_BOUNDARY.md): splitting the sound engine, acoustics
  and geometry into a library shared with Resonance, stage by stage.
- [PHYSICS_BOUNDARY.md](docs/PHYSICS_BOUNDARY.md): the survey and plan for `OpenFPS.Physics` (Jolt
  Physics through JoltPhysicsSharp, soft bodies, gravity a property of the world).
- [SPECIAL_CASES_AUDIT.md](docs/SPECIAL_CASES_AUDIT.md): where the audio code tells sounds apart by id
  ranges, names or prefixes, and the parts that fix it.
- [MATTER.md](docs/MATTER.md): how materials act on each other; wind, water, fire and struck things.
- [REAL_WEATHER.md](docs/REAL_WEATHER.md): real weather and real local time for the world's real places.
- `docs/SKY.md`: where the sun and the moon are, for the player.
- [NEXT_BODIES_WHEELS_ROADS.md](docs/NEXT_BODIES_WHEELS_ROADS.md): physical bodies, per-wheel physics,
  roads as data, traffic signals.
- [NEXT_CITY_10KM.md](docs/NEXT_CITY_10KM.md): a 10 by 10 km city and building maps in the game.
- [ROADS_BEACONS_AND_SCALE.md](docs/ROADS_BEACONS_AND_SCALE.md): answers of 2026-09-22 on roads, beacons,
  doors and scale.
- [PLAN_2026-10-05.md](docs/PLAN_2026-10-05.md): roles, items, progression, bodies and the network.
- [CODY_ASKS_2026-10-08.md](docs/CODY_ASKS_2026-10-08.md): Cody's twelve asks of 2026-10-08 and where
  each stands.
- [RESEARCH_2026-10-02.md](docs/RESEARCH_2026-10-02.md): answers to the questions of 2026-10-02.
- [HOUSEKEEPING.md](docs/HOUSEKEEPING.md): the rules for removing dead code and writing comments.

Sound models (research, measurements and how each is built):
- [ENGINE_SYNTHESIS.md](docs/ENGINE_SYNTHESIS.md): combustion engines from the mechanism.
- [AIRCRAFT.md](docs/AIRCRAFT.md): turbines, turboprops, pistons and rotors.
- [TRAINS.md](docs/TRAINS.md): trains, air brakes, horns, whistles and bells.
- [YARD_MACHINES.md](docs/YARD_MACHINES.md): mowers and air conditioners.
- [DOOR_TYPES.md](docs/DOOR_TYPES.md): door sounds measured from 27 recordings.
  [DOOR_TYPES_EVENTS.md](docs/DOOR_TYPES_EVENTS.md): door kinds and the events each sends.
- [GUNFIRE.md](docs/GUNFIRE.md): gunfire, synthesised to the recordings' measurements.
- [FIRE.md](docs/FIRE.md): fire at any size, and fire that burns what is there.
- [GAS_HOB.md](docs/GAS_HOB.md): the gas hob.
- [RUNNING_WATER.md](docs/RUNNING_WATER.md): creeks, gutters, drains, taps and water over the ground.
- [WAVES_AND_SHORES.md](docs/WAVES_AND_SHORES.md): waves on rivers, lakes and the sea.
- [WET_ROADS.md](docs/WET_ROADS.md): tyres and grip on wet roads.
- [DRIVING_AIDS.md](docs/DRIVING_AIDS.md): horn and siren keys, driving cues, level crossings.
- [EAR_MODEL.md](docs/EAR_MODEL.md): loudness as ears hear it, and how the game uses it.
- [FOOTSTEP_SYNTHESIS_RESEARCH.md](docs/FOOTSTEP_SYNTHESIS_RESEARCH.md): what the literature says about
  synthetic footsteps (the game uses recordings).
- [SOUND_INVENTORY.md](docs/SOUND_INVENTORY.md): every sound in the game and what makes it.
- [WHO_RENDERS_WHAT.md](docs/WHO_RENDERS_WHAT.md): one mechanism per part of what reaches the ear.

Notes behind the code:
- [CLIENT_NOTES.md](docs/CLIENT_NOTES.md), [COMMON_NOTES.md](docs/COMMON_NOTES.md),
  [SERVER_NOTES.md](docs/SERVER_NOTES.md), [TEST_NOTES.md](docs/TEST_NOTES.md),
  [AUDIOLAB_NOTES.md](docs/AUDIOLAB_NOTES.md): the reasoning the code points to.

Diagnoses and reports, kept as written:
- [AUDIO_GHOSTS_AND_STUTTERS.md](docs/AUDIO_GHOSTS_AND_STUTTERS.md), [AUDIO_LOAD_DROPOUTS.md](docs/AUDIO_LOAD_DROPOUTS.md),
  [REPEATS_AND_POPS.md](docs/REPEATS_AND_POPS.md), [THE_MIXER_THREAD_CRASH.md](docs/THE_MIXER_THREAD_CRASH.md),
  [AUDIO_QUALITY_2026-10-06.md](docs/AUDIO_QUALITY_2026-10-06.md): faults found and fixed.
- [COVERAGE_2026-09-24.md](docs/COVERAGE_2026-09-24.md), [COVERAGE_2026-10-06.md](docs/COVERAGE_2026-10-06.md):
  test coverage. [MUTATION_2026-09-24.md](docs/MUTATION_2026-09-24.md),
  [MUTATION_2026-10-01.md](docs/MUTATION_2026-10-01.md): mutation testing.
