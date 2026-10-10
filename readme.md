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
- **Acoustics from geometry.** Rooms measure themselves from the map: their size, how enclosed they
  are, and what their surfaces are made of. Reverb, echoes off facades, sound through doorways and
  sound around corners all follow from that. Nothing is set per room by hand.
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
- Doors work as real ones do: knob doors, push-bar doors with closers, keyed glass front doors,
  automatic sliding doors and patio doors. Each is pushed from one side and pulled from the other,
  and no door shuts on a person.
- Find your way: step through the nearest doors, entrances, stairs, items, people, vehicles or
  places with comma and period; spoken stair cues; spoken coordinates and facing.
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
  menu, including creating an account, voice chat and reconnecting after a dropped connection.
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
  lists them), `OpenFPS.Tests`.
- Build with `--artifacts-path` pointing off the repository's volume (see the run scripts). Do not
  use `dotnet run`: it writes `obj/` and `bin/` into the repository.
- Tests: `dotnet test OpenFPS.Tests` (about 2,800 test methods); the full suite runs on GitHub Actions.
- Map and prefab authoring: [docs/AUTHORING.md](docs/AUTHORING.md). Maps of real places, and how
  to make another: [docs/MAPS_FROM_REAL_PLACES.md](docs/MAPS_FROM_REAL_PLACES.md). Their map data is
  (c) OpenStreetMap contributors (ODbL), with buildings from Overture Maps, addresses from the US
  National Address Database and Census TIGER/Line, and tree cover from ESA WorldCover 2021 (CC BY 4.0).
  The world's own tiles take their ground from USGS 3DEP, land cover from ESA WorldCover, roads from
  OpenStreetMap and buildings from Overture Maps.
- Planned work: [todo.md](todo.md). Recent changes: [changes.md](changes.md).
