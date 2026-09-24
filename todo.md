# To do

Planned work in priority order. Finished work is in [changes.md](changes.md) and `git log`.
Updated 2026-09-24.

## Now

In this order.

### 1. Mutation testing
- Stryker.NET is running on the core audio and acoustics maths (`Loudness`, `Enclosure`,
  `ImageSource`, `EarlyReflections`, `TyreFriction`, `Honk`, `VoiceManager`, `VehicleShadow`,
  `BeaconAids`, `BirdLife`, `EchoDiffuser`).
- For every surviving mutant: write the test that kills it, or record why it is equivalent.

### 2. Cleansing pass, the rest
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

### 3. Tests for the untested audio code
From [docs/COVERAGE_2026-09-24.md](docs/COVERAGE_2026-09-24.md):
- `ClientAudioSystem`: which vehicles get a live voice and the level each is placed at (9% covered),
  through the fake audio provider.
  Include which voices receive an acoustic path: the borrowed distant-car voices never did until
  2026-09-24, and nothing could have caught it.
- `VehicleShadow.Apply` and `EngineReflections`.
- One test per DSP callback processor.
- `AsyncAcousticWorker` paths that do not need Steam Audio.

### 4. Vehicle consistency audit
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

### 5. Gunfire
As realistic as possible.
- Source: close dry recordings of each weapon (`inbox/weapons`).
- After the muzzle: distance loss and air absorption, forward directivity of the blast, the ground
  reflection, the supersonic crack arriving before the report downrange, then the existing
  reflections and reverb.
- Calibrate on the NIJ / Cadre gunshot dataset (20 firearms, 20 positions, 20 m and 40 m).
- Render WAVs to judge before anything goes into the game.
- Also: a shotgun, an impact sound per material, casings that land and bounce where they fall,
  and a proper fire message in the protocol.

### 6. Documentation
- readme, todo and changes: rewritten 2026-09-24.
- User manual, one document in two parts (Playing; Running a server): `docs/MANUAL.md`.

## Next

### Listen and confirm
Built but never heard in the game. Each needs a listen before it counts as done.
- Street life: honks, hard stops, cars parking.
- Engine echoes through the diffuser.
- Vehicles blocking each other's sound.
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

### Acoustics
- The room equation uses the enclosure measure, not measured absorption: the tunnel and garage run
  long.
- Area-weighted absorption (the bus shelter would drop to about 0.4 s). Changes approved rooms, so
  it needs a listen.

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
- Pedestrians and crowds; gunfire as occasional world events.
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
- Recorded voices for crowds and people (cheers, gasps, babble).
- A real mourning dove; wing flaps when a flock is startled.
- Walking speed: 4.5 m/s is a jog.
- Mac client; a web version.
- Map authoring commands such as `/createmap`.
- Engine braking, rev-matched downshifts, gear whine.
