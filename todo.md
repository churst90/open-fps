# To do

Planned work in priority order. Finished work is in [changes.md](changes.md) and `git log`.
Updated 2026-09-30.

## Now

In this order.

### 1. Walls let the highs through
Heard 2026-09-30 after the blanket muffle was retired: through a wall the highs come through, where
a real wall passes the lows and the rumble and stops the top. Being measured: the transmission each
wall gets per band against the mass law for its material and thickness, how the three bands reach
the voice, and every path that could carry highs round or through a wall (diffraction, copies,
traced echoes, the late tail, the held Steam Audio answer). Fix by physics, not by a trim.

### 2. Bodies and wheels (Cody, 2026-09-30: next after the walls)
Plan: [docs/NEXT_BODIES_WHEELS_ROADS.md](docs/NEXT_BODIES_WHEELS_ROADS.md) (agreed 2026-09-27).
Stage 1, roads as data, is done.
- A physical body for every entity: mass and volume for vehicles (from their panels), people and NPCs
  as soft solid bodies, props and machines. Moving bodies go into the acoustic scene, so a bus or a
  crowd blocks sound like a wall of its size and material.
- Per-wheel physics from the preset: load transfer, slip, wheel speed from the tyre size.
- A tyre source at each wheel, reading the surface under it.
- No physical value hard-coded in a model or map; presets move to JSON.
- Turning paths cut over the kerb on the 7 m estate roads: a van's body passed within a metre of
  somebody standing on the corner (`CrosswalkTests`). The turn's curve should keep the body inside
  the carriageway.
- Then traffic lights and accessible pedestrian signals.

### 3. Doors from the recordings
The car door (`CarDoor.cs`) is done and approved. House and steel doors still use the generic model.
In progress 2026-09-30: a door model fitted to `inbox/door sounds/Door Opening Sound Effect.mp3` the
way the car door was (octave-band noise hits, not resonators), with the steel door from the same
model by material and mass. Renders for Cody to judge before it goes in the game.

### 4. Mutation testing
Results so far are in [docs/MUTATION_2026-09-24.md](docs/MUTATION_2026-09-24.md).
- Running from 2026-09-30: the code changed since 2026-09-25 (acoustics, engine, roads), and the
  whole server. Then kill the survivors: a test for each, or a note on why it is equivalent.
- Shared maths: 74.6%, survivors done. Engine code: 93.7%, survivors done (`EngineMutationTests`).
- Client (`VoiceManager`, `VehicleShadow`, `BeaconAids`, `BirdLife`): 39.2%, survivors not yet done.

### 5. Cleansing pass
In progress 2026-09-30: `ChatManager` sender prefixes, unused lab spikes, the `OPENFPS_*` switches,
comments that tell history instead of what the code does, duplicates, `PhysicsAcousticBridgeSystem`,
stale comments, `ClientWorldState.Clear`'s unused bounds, `users.json`, test config folders left in
/tmp, the unseeded `BirdLife` random in tests, and the docs.

### 6. Acoustics still open
- The tail and the copies sit at -6 by ear (0 is physical). Cody is happy with that.
- Below 120 Hz the tail is identical in both ears, so it sits in the head.
- A voice's ground reflection flanges (heard 2026-09-27), though the physics says it is strong
  (Acta Acustica 2024, doi 10.1051/aacus/2024002). Voices have none until the missing part is found:
  the HRTF's torso, the talker's vertical radiation, or head and body movement. Next: a lab render
  with realistic head and body movement on both ends.
- Blocked sources come from one edge of one building at a time. Heard fine on the 2026-09-30 walk;
  if a siren behind buildings wanders again, blend the routes by the energy each carries.
- The traced echoes still convolve at the mixer's 1,024 samples and arrive about 20 ms late (the
  reverb runs in 256-sample pieces).
- The generated flats are bare rooms. Furnishing (beds, sofas, curtains, shelves as absorbent boxes)
  belongs in the generator, not in a per-map constant.
- The traced decay of the tunnel and the garage against real figures for spaces like them.

### 7. Tests for the untested audio code
From [docs/COVERAGE_2026-09-24.md](docs/COVERAGE_2026-09-24.md):
- `ClientAudioSystem`: which vehicles get a live voice and the level each is placed at (9% covered),
  through the fake audio provider, including which voices receive an acoustic path.
- `VehicleShadow.Apply` and `EngineReflections`.
- One test per DSP callback processor.
- `AsyncAcousticWorker` paths that do not need Steam Audio.

### 8. Vehicles (set aside 2026-09-30: they sound good)
The louder exhaust since 2026-09-29 is mostly a real correction: the turbine was a 260 Hz low-pass
that let a third of the wave through, and is now a flat loss that scatters the top, as measured
turbines do. The rest is the reflections coming up from -24 to -6. Still open, for when vehicles are
picked up again:
- `Steepening` is silently capped at 1.5 (`ExhaustNetwork.cs` 145): the sports bike's 1.6 and the
  V-twin's 2.0 do nothing.
- The pickups' pipe size, steepening and wall loss were chosen by ear. `WallLossMultiplier` (0.8-2.5
  per preset) has no anchor.
- A narrow tailpipe radiates less bass (`Waveguide.cs` 419); not checked.
- The motorbikes `single` and `vtwin_stock` have never been measured on `--voice-levels`.
- The 2.8 turbo diesel is jet-heavy (89 dB total against 74 dB of engine).
- Motorbike exhausts too long ("farting into a bottle"); Cody wants about 0.5 m.
- The diesel pickup that sounds backwards (turbo lag, boost-limited fuelling, gear changes).
- A Lamborghini V10 and a V12 from sourced firing orders, header layouts and exhaust valves.
- One table for every preset: declared level, live level at 7.5 m and at idle, bay leakage, extent.
- Buses about 5 dB under real life; `PortNoiseLevel` and `EvoTemperatureK` never read; a big cam's
  idle lope; the sports bike's pull-away surge.

### 9. Gunfire
As realistic as possible.
- Source: close dry recordings of each weapon (`inbox/weapons`).
- After the muzzle: distance loss and air absorption, forward directivity of the blast, the ground
  reflection, the supersonic crack arriving before the report downrange, then the existing
  reflections and reverb.
- Calibrate on the NIJ / Cadre gunshot dataset (20 firearms, 20 positions, 20 m and 40 m).
- Render WAVs to judge before anything goes into the game.
- Also: a shotgun, an impact sound per material, casings that land and bounce where they fall,
  and a proper fire message in the protocol.

### 10. Zones (discussed 2026-09-30, waiting on Cody)
- Today a zone is a named box in the map (`acoustic_region`), placed by hand or by a generator; its
  materials are measured from the walls round it, but its shape is not. The smallest box you stand
  in is the one said.
- Proposal: a room or a building names its own zone from its own walls, so most zones need no box;
  a free-standing zone (a park, a plaza, a car park) stays a box in the map, named, with no entity
  behind it; a zone inside a zone is said as the inner one, with the outer one on the where-am-I key.

### 11. Bump sounds
When you walk into something (`--bumps`): every one is the same woofy, hollow thunk. Missing:
radiation efficiency (a panel below its critical frequency hardly radiates its low modes), and a
hard contact of about a millisecond. Then anchor the level to the footstep takes.

## Next

### People, phones and characters (Cody, 2026-09-28)
- Line lists for Cody to generate: `~/npc-lines-2026-09-28` (generic filler, phone calls, two people
  walking together, 38 characters, vocalisations). Generic, phone and pairs are generated and in the
  game (2026-09-28). Still to generate: the characters (gang crew, delivery driver, police and the
  rest) and the vocalisations; conversations then get their laughs (cues are a pause for now).
- The footstep bank's labels need a file-by-file check by ear: `inbox/footstep-file-review-2026-09-28`
  (193 clips). Then jog and run by speed, scuffs on J/L turns, a shoe per person.
- A phone model: ringtones as note lists through a small speaker in a plastic case, vibration on a
  table and in a pocket, the far side of a call heard from the earpiece only up close (narrowband
  300-3,400 Hz), and later coverage from cell towers (see docs/NEXT_CITY_10KM.md).
- Two people walking together: one pair of walkers sharing a line, taking turns from `pairs.py`.
- Characters: an east-end crew that warns a stranger in stages and shoots at the last; a delivery
  driver who knocks and calls out. The knock needs the hard contact from the bump-sound work.
- Machines to model: elevators, garage doors, leaf blowers, refrigerators, smartphones, a piano
  that plays MIDI files with somebody sitting at it. What to record for each is in
  `~/Desktop/openfps-sounds-to-source.md`.

### A map's own sounds (discussion, 2026-09-28)
Cody likes the synthesised UI sounds and asks whether a map's creator should be able to make their
own, synthetic or recorded, for menus, logging in, messages, chat and beacons, so a game is its
own. Proposal:
- A map (or server) carries a sound pack: a folder of named cues (`login`, `chat_map`, `chat_all`,
  `private`, `menu_move`, `beacon_door` and so on) sent to the client with the map and cached.
- Each cue is either a recording (short OGG, size-limited) or a recipe for the synthesiser: notes,
  intervals, envelopes, the same form `UiSounds.cs` builds from today. A recipe costs bytes, not
  megabytes.
- Anything not in the pack uses the built-in sound. The player can turn a server's pack off.
- Levels are normalised on import (the same loudness rule as speech), so a pack cannot be louder
  than the game.

### The new city
Plan: [docs/NEXT_CITY_10KM.md](docs/NEXT_CITY_10KM.md): a 10 x 10 km city (west end, east end,
center city, subway, airport, hospital, schools, park), shapes other than boxes, and building a map
in the game. Needs the roads and bodies work first.

### Listen and confirm
Built but never heard in the game. Each needs a listen before it counts as done.
- Arriving: "You're in <map>, at <zone>." The city's sidewalks; no "Under Shelter" at doorways.
- Street life: honks, hard stops, cars parking. (Traffic as a whole was heard 2026-09-27 and is
  fine as it is.)
- Pedestrian and driver speech since the fix for the room-like copy.
- Beacons.
- Driving aids.
- Bus air brakes; the airliner's whine.
- A walk through the city block.
- Chat, menus and saved servers.

### Load and stuck-voice checks
Some may already be fixed; confirm before fixing again.
- The speedway's 40-car load has never been measured.
- Two field-wide silences of about 150 ms.
- A voice placed at a position 1.9 s old.
- The PA announcement that never decodes.
- Vehicles stopping close in front of the player.

### Server
- Admin commands to change a password and a role. Today the only way is editing `openfps.db`.
- The default admin account is `admin` / `admin123`: make the first run ask for a password.
- A failed port bind still logs "started". Fail loudly instead.
- `/savemap` rewrites a map as plain JSON and loses its comments.
- `run-server.sh` prints port 33288 even when `--port` says otherwise; `OPENFPS_PORT` only drives
  its check.
- `/restart` and `/reloadmap` for admins.
- The MUD interface is plain TCP on all interfaces, and the game port accepts any connection
  without a key. Decide what a public server needs.

### Vehicles
- A key for the siren when driving a police car.
- Aircraft roll out after landing instead of reversing at the end of the runway.
- Level crossings: tyre thump over the rails, and gates.

### Client
- The Linux client cannot register a new account (the server and the Windows client can).
- Enter is bound to both interact and fire; fire wins. Pick one.
- There is no horn key when driving.
- The in-game help label is out of date ("P scan"; F6, F8, G, Q, R, T, B, K and Enter missing).
- The client only sends interact within 3 m; the server allows 5 m.
- Linux voice chat: V and the input device setting exist, but capture is not wired up.
- Beacon categories exit, stairs and waypoint have no sound of their own; only authored beacon
  objects play for them.
- `run-gtk-client.sh`: the usage text leaves out `foot` and `fmodlog`, and `foot` is passed on to
  the client.

## Later

### Acoustics
- Synthetic footsteps, parked 2026-09-28 after three attempts failed by ear (see changes.md and
  tools/footstep_synth_*.py): band-envelope resynthesis was "watery", contacts fitted to the
  recordings' reverberant tails were "a snare drum" with no depth, and one grain process made gravel,
  sand and snow "static, all the same". The recorded bank is used. If it is tried again: fit only the
  dry first 30-60 ms (the room is the game's job), give the heel the body's weight, and give each loose
  surface its own mechanism (stones clacking, sand compacting, snow crystals breaking).
- Aggregation: many distant sources heard as one extended source, so a whole city fits in the
  voice budget.
- Fused early reflections (arrivals inside 50 ms) by convolution or delay taps.
- One acoustic path per machine: a wall between you and one end of a bus is not modelled.
- A check that the HRTF voice count holds through a crowd reaction.

### Engines and vehicles
- Idle hunting on the NASCAR, muscle car and V10 (the combustion model's dilution cliff).
- F1 above 12,300 rpm, and its airbox as a Helmholtz volume.
- Exhaust pipe delays that follow gas temperature.
- Tyres on gravel and wet roads (the first real use of the granular engine).
- Fit the body `Coupling` and `ShellLevel` to recordings.
- Collision damage.
- Walking about inside a moving bus.
- Getting into traffic cars.
- Car glass blocking outside sound (moving parts are not in the acoustic scene).

### World and gameplay
- The siren switches off at every short stop (below 2 m/s); real crews keep it on through a junction.
- Elevators. Signal sounds are in `inbox/elevator sounds` (42 synthetic replicas of real ones:
  arrival dings and chimes, button beeps, door buzzers, alarm bells, each named by its pitch). They
  can be played as they are or rebuilt from their pitch and envelope. The machine itself (traction
  motor, rope and guide-rail rumble, door operator, door panels, latch, the car's own ride) has to
  be modelled; nothing recorded covers it.
- Traffic lights at the downtown intersections: drivers could then say "It's green! Go!" (recorded,
  unused), and the crossings could have accessible pedestrian signals.
- Crowds; gunfire as occasional world events. Pedestrians talk since 2026-09-26; not yet heard.
- A crowd murmur from the recorded chatter lines, for the grandstand and busy places: many voices
  mixed into one extended source, like the applause. Measured 2026-09-26: at 8 or more talkers the
  gaps between words are gone (envelope spread 3 dB, against 13 dB at 4 talkers).
- Crowd reactions need lines the delivered set does not have: cheers, "whoa", "come on", gasps,
  laughter, groans. Generate them with the same tool and import them the same way.
- Glass: the pane falls after it is shot out; the fragment shower on the granular engine.
- Rain that sounds different on each surface and under shelter.
- Speedway crowd: three stand blocks never react; the first reaction of each kind is silent; the
  clap balance; crowd reflections.
- Held items that rattle, a sound for jumping off, handing items between players, footstep
  loudness that follows speed.

### Platform and server
- Windows client: saved servers, settings, F-key lists, and removing the global keyboard hook.
- Saving player position, progress and world state.
- A protocol version check on connect.
- Player-owned maps (`MapPublishRequest` is a stub). Decided 2026-09-28: admins build anything;
  players make their own maps but cannot publish them; an owner can make others editors; a blank
  map is one slab of grass; placing by typed commands, relative or by coordinates. See
  docs/NEXT_CITY_10KM.md.
- A stress test with 50 or more bots.
- Check the SQLite package for security updates.

## Ideas

- Shapes other than boxes: curved kerbs, round columns, trees, and acoustics that handle them.
- Airport take-offs and landings; lifts; flats with things in them.
- Recorded crowd reactions (cheers, gasps); see Crowds under World and gameplay.
- A real mourning dove; wing flaps when a flock is startled.
- Walking speed: 4.5 m/s is a jog.
- Mac client; a web version.
- Map authoring commands such as `/createmap`.
- Engine braking, rev-matched downshifts, gear whine.

### Traffic faults that come and go with the mix (2026-09-28)
Any change to which cars drive reshuffles the city's traffic, and two tests fail or pass with it:
- `CrosswalkTests.Walkers_wait_for_a_gap...`: one sample in 13,090 of a stopped car overlapping a
  walker at a street corner (Hatchback 1 at (124.8, 252.9)); earlier the same day the dirt bike on
  Wharf Avenue. Probably a turn cutting the kerb corner and stopping there.
- `CarFollowingTests.No_two_vehicles_meet_inside_a_junction`: two nearly stopped cars 1.9 m apart
  side by side at a junction entry (the test exempts side by side only from 2 m).
Both passed on the mix before the four loud cars went in; neither involves those cars.
