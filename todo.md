# To do

Planned work in priority order. Finished work is in [changes.md](changes.md) and `git log`.
Updated 2026-09-27.

## Now

In this order.

### 1. Acoustics before moving on
- Blocked sources jump between two bearings. A siren 150-300 m away behind buildings turned more
  than 30 degrees between updates 68 times in 11 minutes (log of 2026-09-27 05:02) while the
  listener stood still. The bearing alternates between the diffracting edge (exact) and Steam
  Audio's pathing probes, which are 23.4 m apart on the city map.
- Reverb per surface: in the default traced mode every room's tail is traced by Steam Audio from
  the material of each surface, so it is already per surface. Not yet checked: the traced decay of
  the tunnel and the garage against real figures for spaces like them, and the three-band
  absorption of each material in the registry. The enclosure estimate (tunnel and garage too long,
  no area weighting) only applies under `/reverb room`.
- Sounds played from recordings (speech, footsteps, one-off world sounds) have no ground
  reflection of their own. Only synthesised voices (engines, machines, sirens) carry one.
- Birds stalled audio placement for up to 882 ms (29 stall warnings in the same log).
- Listen in the game to the traced reverb without the open ground (`/reverb traced`). The lab passes;
  `/reverb room` was confirmed by ear.

### 2. Mutation testing
Results so far are in [docs/MUTATION_2026-09-24.md](docs/MUTATION_2026-09-24.md).
- Shared maths (`Loudness`, `Enclosure`, `ImageSource`, `EarlyReflections`, `TyreFriction`,
  `Honk`): 74.6%, survivors killed or recorded as equivalent.
- Client (`VoiceManager`, `VehicleShadow`, `BeaconAids`, `BirdLife`): 39.2%, survivors not yet done.
  `EchoDiffuser` was never mutated (its line range went stale).
- Server and whole-Common runs (35.5% and 47.0%): not yet triaged.
- Engine code (`Engine/*.cs`, `VehicleSynth`, `Pneumatics`, 2,827 mutants): running since
  2026-09-25 12:17 on a copy of that day's tree. Map its survivors onto the current code.
- For every surviving mutant: write the test that kills it, or record why it is equivalent.
- Each test process leaves an `openfps-test-config-<pid>` folder in /tmp. Remove it on exit.

### 3. Cleansing pass, the rest
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

### 4. Tests for the untested audio code
From [docs/COVERAGE_2026-09-24.md](docs/COVERAGE_2026-09-24.md):
- `ClientAudioSystem`: which vehicles get a live voice and the level each is placed at (9% covered),
  through the fake audio provider.
  Include which voices receive an acoustic path: the borrowed distant-car voices never did until
  2026-09-24, and nothing could have caught it.
- `VehicleShadow.Apply` and `EngineReflections`.
- One test per DSP callback processor.
- `AsyncAcousticWorker` paths that do not need Steam Audio.

### 5. Vehicle consistency audit
Every vehicle configured the same way, so its loudness is predictable.
- One table for every preset: declared level, live level at 7.5 m pass-by and at idle, engine bay
  leakage, extent, level lift, air system, horn. Fix outliers in the configuration, not with trims.
- The motorbikes (`single`, `sportbike`) have never been measured on `--voice-levels`: they never
  reach full load there.
- The intake has no level anchor: the i4 and V6 intakes measure louder than their exhausts.
- Buses are about 5 dB under real life (95 dB at 1 m against 98-102).
- The 2.8 turbo diesel is jet-heavy (89 dB total against 74 dB of engine).
- `PortNoiseLevel` and `EvoTemperatureK` are declared and never read.
- A big cam's idle lope: since the airflow fix (2026-09-24) a 308-degree cam idles no rougher
  than a stock one, so `BigCam_IdlesRougherThanStockCam` is skipped. Burnt gas pushed back up the
  runners still vanishes instead of mixing into the plenum (tracking it properly over-dilutes
  every idle, so the model's reversion flow is too large); fix that and the lope comes back from
  the physics.

### 6. Gunfire
As realistic as possible.
- Source: close dry recordings of each weapon (`inbox/weapons`).
- After the muzzle: distance loss and air absorption, forward directivity of the blast, the ground
  reflection, the supersonic crack arriving before the report downrange, then the existing
  reflections and reverb.
- Calibrate on the NIJ / Cadre gunshot dataset (20 firearms, 20 positions, 20 m and 40 m).
- Render WAVs to judge before anything goes into the game.
- Also: a shotgun, an impact sound per material, casings that land and bounce where they fall,
  and a proper fire message in the protocol.

### 7. Documentation
- readme, todo and changes: rewritten 2026-09-24, brought up to date 2026-09-27.
- User manual, one document in two parts (Playing; Running a server): `docs/MANUAL.md`.

## Next

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
- Player-owned maps (`MapPublishRequest` is a stub).
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
