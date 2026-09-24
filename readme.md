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
- Walk, run and explore by sound. Footsteps change with the ground and your speed.
- Maps: a generated city with streets, traffic, buses, a light rail loop, level crossings, an
  airport, parks, birds and buildings you can enter; a speedway with a race; and a rooms map for
  trying doors and materials.
- Drive: get into a parked car and drive it, with lane tick and edge tones, a guide beep, parking
  sensor tones and spoken road names. Ride the bus: it stops at bus stops and you can take a seat.
- Beacons: sounds that mark doors, items and vehicles near you (a map can add exits, stairs and
  waypoints). Choose which kinds you hear. Beacons behind a wall are not played.
- Chat: map, all, private and server channels, each with its own sound. Voice chat on the Windows
  client (not yet on Linux).
- F-key lists of players, maps and friends, which you can act on. Travel between maps.
- Saved servers and settings.

### The sound engine
- Binaural 3D sound (Steam Audio HRTF) mixed by FMOD.
- Occlusion, diffraction around edges, transmission through walls, and a moving vehicle blocking
  another vehicle's sound.
- Early reflections and second- and third-order echoes from nearby surfaces.
- Reverb from each room's measured size, enclosure and materials, with rooms inside rooms (a bus
  shelter inside a street, a garage inside a car park).
- Doppler, air absorption over distance, and horn directivity.
- Physical synthesis of: petrol and diesel engines with their exhaust and intake systems, turbos,
  tyres, electric and air horns, sirens, trains and their horns and bells, air brakes, aircraft
  (propellers, jets, helicopters), small machines, doors, footsteps, applause and crowds.

### Running a server
- One server can host several maps at once; players travel between them.
- Maps are JSON files; objects are prefabs. Materials decide how things sound.
- Accounts, staff roles, admin commands, a message of the day, and a text (MUD) interface for
  testing a server without a client.

## Platforms

- **Server:** Linux. It is plain .NET 10, so other platforms should work but are not tested.
- **Client:** the Linux GTK client is the current one. The Windows client works but is behind: it
  does not yet have saved servers, the settings menu or the F-key lists.
- Speech: speech-dispatcher (Orca, espeak-ng) on Linux; NVDA or SAPI on Windows.

## Getting started

- `./run-server.sh` starts a server (players land on the speedway); `./run-server.sh city` lands
  them on the city. Every map is loaded either way.
- `./run-gtk-client.sh` starts the Linux client.
- The user manual is in [docs/MANUAL.md](docs/MANUAL.md): playing, and running a server.

## For contributors

- C# on .NET 10. Networking: LiteNetLib. Entities: Arch ECS. Serialisation: MemoryPack.
  Audio: FMOD Core and Steam Audio (phonon). UI: GTK 4 (GirCore).
- Projects: `OpenFPS.Common` (shared simulation, acoustics and sound models), `OpenFPS.Server`,
  `OpenFPS.Client.Core` (client logic and the audio engine), `OpenFPS.Client.Gtk` (Linux client),
  `OpenFPS.Client` (Windows client), `OpenFPS.AudioLab` (measurement and rendering tools),
  `OpenFPS.Tests`.
- Build with `--artifacts-path` pointing at a local disk (see the run scripts).
- Tests: `dotnet test OpenFPS.Tests` (about 18 minutes, about 940 tests).
- Map and prefab authoring: [docs/AUTHORING.md](docs/AUTHORING.md).
- Planned work: [todo.md](todo.md). Recent changes: [changes.md](changes.md).
