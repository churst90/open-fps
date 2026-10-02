# To do

Planned work in priority order. Finished work is in [changes.md](changes.md) and `git log`.
Updated 2026-09-30.

## Now

In this order.

### 1. Walls, what is left (the panel model went in 2026-09-30, unheard)
- Listen: through a wall the lows and the rumble should come through and the top should not.
- A source just behind a building corner made of two boxes gets no diffraction route (the 5 cm joint
  padding in `RouteIsClear` rejects the corner), so it drops to the wall alone. Fix without reopening
  the shut-door crack leak.
- Each floor is two overlapping 25 cm slabs, and carpet on a floor counts as a barrier: both are
  panels in series, so upstairs is about 15 dB too quiet in the lows. Merge layers in contact.
- No cavity resonances or air leaks: sealed glazing is about 10 dB optimistic in the mids, and door
  gaps and seals are not modelled (wood door 41 dB at 2 kHz against about 32 measured).
- Steam Audio counts walls in a row as (2n+1)/3 of one; the tracer counts them exactly.
- Diffraction is evaluated at 200/1250/8000 Hz, not the band averages transmission uses.

### 2. Bodies and wheels (per-wheel physics in progress from 2026-10-01)
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
- The fitted octave-band noise model (branch `worktree-agent-a6e2f6e0e3da748cd`) was rejected by ear
  on 2026-10-01: scratchy and grainy, and wood and steel sounded the same. Not merged.
- Next approach to be agreed with Cody: recorded takes played as a bank, or a contact and modal
  model in which wood and steel differ by their own resonances.
- The knock (`DoorKnock`) may be about 20 dB short at 1-2 kHz against both knock recordings.

### 4. Mutation testing
Results so far are in [docs/MUTATION_2026-09-24.md](docs/MUTATION_2026-09-24.md).
- Running from 2026-09-30: the code changed since 2026-09-25 (acoustics, engine, roads), and the
  whole server. Then kill the survivors: a test for each, or a note on why it is equivalent.
- Shared maths: 74.6%, survivors done. Engine code: 93.7%, survivors done (`EngineMutationTests`).
- Client (`VoiceManager`, `VehicleShadow`, `BeaconAids`, `BirdLife`): 39.2%, survivors not yet done.

### 5. Cleansing, what is left
Done 2026-09-30 (see changes.md). Left:
- The two client heads' startup handlers are near copies; about 25 lab spikes each find ASSETS
  their own way.
- `WeaponSynth.CompositeBlast`, `RecordedBlend` and `FiringTakeIndex` are used only by `BattleSpike`;
  the game plays no recorded gunfire.
- `ClientGameSession.cs`, `WorldAudioPlayer` and `AsyncAcousticWorker` still have dated comments.
- The full test suite takes about 40 minutes for 1,415 tests; the readme says 18 minutes and 940.

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
- A command to change a password (your own, and an admin resetting someone's). `/setrole` exists
  since 2026-10-02; passwords still need `OPENFPS_ADMIN_PASSWORD` (admin only) or editing `openfps.db`.
- `/kick` and a ban list (by name and by address). docs/SERVER_SECURITY.md lists what exists.
- Roles beyond Player/Dev/Admin (Cody, 2026-10-02): map creators who build only on their own maps,
  teleporter items instead of `/tp` for players, and whether `/move` stays staff-only. Today every
  building verb needs Dev.
- `/profile` has no level, rank or game stats because none exist yet; it shows role, real name,
  online/away/idle and the map. Add them to the profile when there is something to count.
- Name locks and rate-limit counts are in memory and reset on restart.
- The Windows client hides "Where is" from players only from the next build; the friend's current
  build still offers it and the server refuses it.
- The default admin account is `admin` / `admin123` unless `OPENFPS_ADMIN_PASSWORD` is set (the
  server warns at start): make the first run ask for a password.
- A failed port bind still logs "started". Fail loudly instead.
- `/savemap` rewrites a map as plain JSON and loses its comments.
- `/restart` and `/reloadmap` for admins.
- The MUD interface is plain TCP on all interfaces, with passwords in the clear, and the game port
  does not check the client's connection key. Both are capped per address and close a connection
  that does not log in within 2 minutes (2026-10-02). Decide whether a public server should listen
  for the MUD at all, or only on localhost.

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
