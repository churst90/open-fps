# To do

Planned work in priority order. Finished work is in [changes.md](changes.md) and `git log`.
Updated 2026-09-28.

## Now

In this order.

### 1. Acoustics before moving on
- Why do coherent copies want -24 dB? Cody set `/room -24` and `/echoes -24` by ear, on different
  days, and both sound right; the outdoor traced tail is accepted at its physical level. The placed
  room echoes and the traced per-source echoes are clean copies of the source from a point; the tail
  is a dense diffuse response. Next experiment: pass each placed room echo through EchoDiffuser at
  its wall's Scattering (plaster 0.15, brick 0.45, carpet 0.6), measure with `--clap-room`, then see
  how far `/room` can come back toward 0 by ear. Do not raise the default without that.
- Hear flat 01F again after 2026-09-29 (restart the CLIENT; the server is unchanged): the end walls
  are placed, other people's steps carry the wall, the traced soundfield faces the right way, and
  the late tail is a diffuse field round the head (DiffuseTail) instead of one channel. Below 300 Hz
  the tail is still the same in both ears, which is physical; if the room still gathers in front,
  try the split lower (DiffuseTail.SplitHz) and measure with `--sa-encode` first.
- The diffuse tail's level per band is within 5 dB of flat but not flat (+4.8 dB at 150-300 Hz,
  -3.5 at 2.4-4.8 kHz). A head in a diffuse field is a few dB UP at 2-5 kHz, so the top is about 5 dB
  shy. The decoder's virtual-speaker layout is Steam Audio's; if it matters by ear, weight the eight
  directions or add a measured diffuse-field equalisation.
- The generated flats are bare rooms: 8.65 by 17.86 by 2.73 m, plaster walls and ceiling, carpet,
  one sofa. Traced decay 0.7-0.8 s, heard 1.1 s. A furnished flat that size measures about 0.5 s.
  Furnishing (beds, sofas, curtains, shelves as absorbent boxes) belongs in the generator, not in a
  per-map constant.
- The tail leads in the left ear by 5 samples (0.11 ms) at 150-300 Hz in every clap of the 02:53
  capture. The two ear decorrelators sum to the same delay but not to the same group delay at low
  frequencies. Small; measure before touching it.
- Blocked sources jump between bearings. A siren 150-300 m away behind buildings turned more than
  30 degrees between updates 68 times in 11 minutes (2026-09-27) while the listener stood still. The
  bearing comes from one edge of one building (the one with the longest detour), or from Steam
  Audio's pathing probes 23.4 m apart when that edge is not in the clear, and it switches between
  them. Real sound arrives round several edges at once: blend the routes by the energy each carries
  and turn the bearing no faster than the geometry moves.
- Reverb per surface: in the default traced mode every room's tail is traced by Steam Audio from
  the material of each surface, so it is already per surface. Not yet checked: the traced decay of
  the tunnel and the garage against real figures for spaces like them, and the three-band
  absorption of each material in the registry. The enclosure estimate (tunnel and garage too long,
  no area weighting) only applies under `/reverb room`.
- A voice's ground reflection flanges, both summed into its direction and from its own direction
  below (heard 2026-09-27, `--ground-voice`), though the physics says it is strong (Acta Acustica
  2024, doi 10.1051/aacus/2024002). Voices have none until the missing part is found: whether the
  HRTF has a torso shadowing sound from below, the talker's vertical radiation, or head and body
  movement. The paper (inbox/aacus230104.pdf, read 2026-09-27) rules out "too strong": with mouth
  and ears at 1.5 m over a hard floor, the reflection is 0 to +2.6 dB against the direct sound below
  800 Hz at 3-7 m (+4.6 dB for [i] and [l]), below it at 800 Hz-1.6 kHz, and it fades only at an
  absorption of 0.4-0.6. What was rendered (-3.6 dB) was weaker than that. The paper says the
  perceptual side has not been studied, and notes that talkers make small head movements all the
  time (Munhall et al.). Next: a lab render with realistic head and body movement on both ends.
- Listen: pedestrian voices with their new low end and loudness, and the ground's answer on shots
  and doors (built 2026-09-27).
- Listen in the game to the traced reverb without the open ground (`/reverb traced`). The lab passes;
  `/reverb room` was confirmed by ear.

### 2. Bodies, wheels and roads
Plan: [docs/NEXT_BODIES_WHEELS_ROADS.md](docs/NEXT_BODIES_WHEELS_ROADS.md) (agreed 2026-09-27).
- Roads as data: lanes, junctions, crossings, surfaces; traffic follows lanes, keeps a gap, and
  gives way by gap acceptance. Pedestrians cross at the corners (2026-09-28): they wait at the kerb
  for a gap, drivers stop for anybody on a crossing, and a driver arriving at a junction lets
  somebody across who has waited 8 s. Not yet heard in the game.
- Turning paths cut over the kerb on the 7 m estate roads: a van's body passed within a metre of
  somebody standing on the corner (`CrosswalkTests`, 2026-09-28). The turn's curve should keep the
  body inside the carriageway.
- A physical body for everything: traffic cars from their panels, people as soft solid bodies,
  moving bodies in the acoustic scene.
- Per-wheel physics from the preset: load transfer, slip, wheel speed from the tyre size.
- A tyre source at each wheel, reading the surface under it.
- Traffic lights and accessible pedestrian signals.
- No physical value hard-coded in a model or map; presets move to JSON.

- Bump sounds when you walk into something (`--bumps` in the lab). Heard 2026-09-27: every one is
  the same woofy, hollow thunk; a car door sounds like a bath tub, brick and concrete too. Two things
  missing from the model: (1) radiation efficiency, since a panel below its critical frequency
  hardly radiates its low modes, which is the boom; (2) a hard contact (hand, knuckle, ring) of about
  a millisecond, which is what makes a knock on a car door or window a distinct transient. Then
  anchor the level to the footstep takes. Demo again before it goes in the game.
- Beacons as earcons: a family of bell-like chimes built on chords or intervals (root and fourth,
  major chords), pleasant and unmistakably not a world sound; a different one for doors, vehicles,
  items on the ground and general beacons; placed at the object. Render a set to choose from first.
- Doors need work. References are now in `inbox/door sounds` (a door opening, heavy knocking on
  wooden inside doors, a clean car door opening and closing); the older latch and open-close files
  are no longer in the inbox. The car door is done (2026-09-28): `CarDoor.cs`, fitted to a recording, approved by ear.
  House and steel doors still use the generic model. Model the mechanism: latch bolt riding the strike and dropping in, hinge
  stick-slip creak, the leaf swinging, the leaf striking the frame; recordings are the spec, as for
  gunfire.

### 3. Speech and sounds at login and in chat (Cody, 2026-09-28)
Done 2026-09-28 except the zone names below (loading steps silent, arrival line, /tp, chat cues).
- On connecting, say only "Logged in. You are in <map>, <zone>." Stop speaking "Receiving
  entities", "Preloading", "Geometry ready", "Acoustics ready" and the percentages
  (`ClientGameSession.cs` 783-983, `GtkClientShell.cs` 91-104). Failures are still spoken.
- `/tp` replays the whole entry into the world: the entry chord and "You have entered the world. Use
  W A S D to move" (`ClientGameSession.cs` 878-898, the PlayerSpawned case has no teleport guard). A
  teleport should only say where you are. Every command reply also plays a tick, because server text
  comes in as a chat message with no sender.
- Map chat and general chat sound the same for staff: a message from an admin plays the admin cue in
  place of the channel's (`ClientGameSession.cs` 154-160). Keep the channel's cue and mark staff
  some other way. Cody logs in as admin, so he hears this on every message.
- Zone names: every street has a sidewalk on each side, and the zone is spoken as "sidewalk", not
  "Main Street east pavement, block 2" (`tools/gen_city.py` 1101-1133). The street's name stays
  available on the where-am-I key.

### 4. Vehicles by ear (Cody, 2026-09-28)
- Some of the gruffer exhausts sound as if a hand is over the tailpipe, and one or two cars lack low
  end. Check the whole gas path on every preset: the exhaust leaves at the tailpipe exits, pointing
  the right way, at the right level. Leads: `Steepening` is silently capped at 1.5
  (`ExhaustNetwork.cs` 145), so the sports bike's 1.6 and the V-twin's 2.0 do nothing; a narrow
  tailpipe radiates less bass (`Waveguide.cs` 419); stock mufflers with resonators.
- A diesel pickup sounds backwards, like the driver jumps on the gas and then lets off, the reverse
  of what it should be; the truck sounds like the driver cannot drive. Check turbo lag and boost-
  limited fuelling against load, and the driver's gear changes. Find which preset first
  (`diesel_i4`, `diesel_cummins`, `powerstroke73`, `duramax_compound`).
- Motorbikes: the exhausts are too long, "farting into a bottle". The stock cruiser (`vtwin_stock`)
  builds 1.88 m and 2.11 m; the others 0.78-1.33 m. Cody wants about 0.5 m. Shorten the headers,
  collector, mid-pipe and muffler on the bike presets.
- A Lamborghini V10, and a V12: the aggressive idle, the revs and the exhaust, built on the
  Flowmaster exhaust work. The `v10` and `v12` presets are generic today. Take firing order, header
  layout, exhaust valves and rev limit from sources.

### 5. Mutation testing
Results so far are in [docs/MUTATION_2026-09-24.md](docs/MUTATION_2026-09-24.md).
- Shared maths (`Loudness`, `Enclosure`, `ImageSource`, `EarlyReflections`, `TyreFriction`,
  `Honk`): 74.6%, survivors killed or recorded as equivalent.
- Client (`VoiceManager`, `VehicleShadow`, `BeaconAids`, `BirdLife`): 39.2%, survivors not yet done.
  `EchoDiffuser` was never mutated (its line range went stale).
- Server and whole-Common runs (35.5% and 47.0%): not yet triaged.
- Engine code (`Engine/*.cs`, `VehicleSynth`, `Pneumatics`): finished 2026-09-28 after 2 days 19
  hours, 93.7% (1,352 killed, 1,429 timed out, 46 survived, 140 not covered; report copied to
  `~/openfps-scratch-archive/mutation-2026-09-28/`). Survivors: `Driveline` 28, `AirSystem` 18. Not covered: `EngineSynth` 56,
  `AirSystem` 31, `Driveline` 21. The run was on the tree of 2026-09-25; eight commits have changed
  these files since, and `ExhaustRadiation.cs` is new and was never mutated. Map the survivors onto
  the current code, then re-run only the changed files.
- For every surviving mutant: write the test that kills it, or record why it is equivalent.
  Engine run done 2026-09-28 (`EngineMutationTests`): the driver's pedals, launch, clutch and shifts,
  the network driver switching off, the valve solver's bracket ends, the compressor. Found a real one:
  a shift left the throttle open for its first sample. `ValveResidual` (unused and stale) deleted;
  diagnostic text marked for Stryker to skip. Next: re-run the changed engine files and
  `ExhaustRadiation.cs`.
- Each test process leaves an `openfps-test-config-<pid>` folder in /tmp. Remove it on exit.
- `BirdLifeTests.AHedgeOfSparrowsChattersAndABangShutsItUp` fails now and then: BirdLife's random
  generator is unseeded, so a minute of chirps can fall outside the test's range. Seed it in tests.

### 6. Cleansing pass, the rest
- `ChatManager` sender-prefix leftovers.
- Lab spikes nothing uses.
- The 18 `OPENFPS_*` switches: keep the ones still needed, remove the rest.
- Comments that tell history instead of what the code does. The history belongs in `changes.md`.
- Places where the same thing is done twice.
- `PhysicsAcousticBridgeSystem` is never wired up.
- Stale comments: "KNOWN GAP: no turbine" in `Engines.cs`; "no runtime map change" in
  `run-server.sh` and the server's `Program.cs` (`/join` does it now).
- `ClientWorldState.Clear` still takes grid bounds it no longer uses.
- The unused `users.json` files (the server uses `openfps.db`).

### 7. Tests for the untested audio code
From [docs/COVERAGE_2026-09-24.md](docs/COVERAGE_2026-09-24.md):
- `ClientAudioSystem`: which vehicles get a live voice and the level each is placed at (9% covered),
  through the fake audio provider.
  Include which voices receive an acoustic path: the borrowed distant-car voices never did until
  2026-09-24, and nothing could have caught it.
- `VehicleShadow.Apply` and `EngineReflections`.
- One test per DSP callback processor.
- `AsyncAcousticWorker` paths that do not need Steam Audio.

### 8. Vehicle consistency audit
- Cody 2026-09-28: "all of the exhaust everywhere is just quiet ... the bassy rumble needs to come
  down". Pickups done first (turbine). Cars next. A diesel at cruise fuel is nearly all bass on the
  bench (centroid 83 Hz at 3-10 % fuel): check the blowdown against a recording before touching it.
Every vehicle configured the same way, so its loudness is predictable.
- One table for every preset: declared level, live level at 7.5 m pass-by and at idle, engine bay
  leakage, extent, level lift, air system, horn. Fix outliers in the configuration, not with trims.
- The motorbikes (`single`, `sportbike`) have never been measured on `--voice-levels`: they never
  reach full load there.
- The parking garage has columns only round its edge and nothing inside 110 x 84 m, so a sound there
  echoes only off the floor and ceiling (4-20 ms, fused). A real garage has a column grid every
  8-16 m; offered to Cody 2026-09-28.
- The sports bike's pull-away (`--shift-trace sportbike`): for the first three seconds the revs swing
  2,800-4,200 rpm. At the clutch's least bite the idle governor opens the air against the load, and a
  200 kg bike then outpulls a 2 m/s^2 target on no throttle at all. Its 1-2 quickshift also surges
  the bike 5 km/h (the engine is still 1,300 rpm above second when the dogs engage).
- The intake has no level anchor: the i4 and V6 intakes measure louder than their exhausts.
- Buses are about 5 dB under real life (95 dB at 1 m against 98-102).
- The 2.8 turbo diesel is jet-heavy (89 dB total against 74 dB of engine).
- `PortNoiseLevel` and `EvoTemperatureK` are declared and never read.
- A big cam's idle lope: since the airflow fix (2026-09-24) a 308-degree cam idles no rougher
  than a stock one, so `BigCam_IdlesRougherThanStockCam` is skipped. Burnt gas pushed back up the
  runners still vanishes instead of mixing into the plenum (tracking it properly over-dilutes
  every idle, so the model's reversion flow is too large); fix that and the lope comes back from
  the physics.

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

### 10. Documentation
- readme, todo and changes: rewritten 2026-09-24, brought up to date 2026-09-27.
- User manual, one document in two parts (Playing; Running a server): `docs/MANUAL.md`.

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

### Zones that overlap
Done 2026-09-28: the smallest zone wins. Still open: Z could say "<inner>, in <outer>".

### Traffic faults that come and go with the mix (2026-09-28)
Any change to which cars drive reshuffles the city's traffic, and two tests fail or pass with it:
- `CrosswalkTests.Walkers_wait_for_a_gap...`: one sample in 13,090 of a stopped car overlapping a
  walker at a street corner (Hatchback 1 at (124.8, 252.9)); earlier the same day the dirt bike on
  Wharf Avenue. Probably a turn cutting the kerb corner and stopping there.
- `CarFollowingTests.No_two_vehicles_meet_inside_a_junction`: two nearly stopped cars 1.9 m apart
  side by side at a junction entry (the test exempts side by side only from 2 m).
Both passed on the mix before the four loud cars went in; neither involves those cars.

### Traced echoes are still a mixer block late
The traced REVERB runs in 256-sample pieces since 2026-09-28 (first reflection 5-8 ms, was 20-23).
The per-source traced ECHOES (TracedEchoes, FmodAudioProvider ~1714) still convolve at the mixer's
1,024 and so arrive about 20 ms late too; same change if they are heard as detached.

### The terminal is a bare concrete box (2026-09-29)
30 x 164 x 6.5 m of concrete walls and ceiling over tile: Sabine puts it near 20 s and the tracer
measures 7-10 s. A real terminal has a suspended acoustic ceiling (alpha ~0.7), which is what keeps
it to 2-3 s. Offered to Cody: an acoustic-tile ceiling material for the terminal (and the concourse
of any big public building), not a reverb trim.
