# To do

Planned work in priority order. Finished work is in [changes.md](changes.md) and `git log`.
Updated 2026-10-05 (evening). Today's plan, agreed with Cody: [docs/PLAN_2026-10-05.md](docs/PLAN_2026-10-05.md).

## Now

In this order.

### 1. Listen in the game
Built, rendered or measured, but not heard in the game. Each needs Cody's ear before it counts as done.
Restart the server and update the client first.
- Doors: push and pull sides, the tower front doors locked from the street (key, then pull), and
  nothing shutting on a person.
- The glass front door, the glass shop door and the lift door models (inbox/door-models-2026-10-05).
- The teleporter: the charge, the thump where you leave, the pop where you arrive.
- Hand-over sounds when something is given.
- Bodies and carrying. Carrying a body slows you to 1 m/s; walking is 4.5 m/s. Is the pace right?
- Elm Park's fountain, the fire pit at 58 Alder Street, and the trees in the wind
  (inbox/nature-round1-2026-10-05).
- The six Resonance-port fixes: tyres at the device's rate, the bus's air compressor at the back,
  rail axles from the model file, machines built on a base, the road vehicles' air horn, the bell's
  headroom. Also the diesel locomotives (crank, then the governor holds the notch).
- After a map change, sounds keep their direction (the fountain moves as you turn; the speedway's
  cars are placed). Fixed 2026-10-05; travel city -> speedway -> city and listen.
- The fountain and the trees, rounds 2 and 3 (inbox/nature-round2-2026-10-05, nature-round3).
  Cody on round 2: "almost there", the water still a little grainy, the hiss could be smoother.
- Wind at the ears and the weather's wind in the trees (inbox/weather-wind-2026-10-05). Try
  `/weather wind 8 north west gusty`, `/weather storm`, `/weather auto`. Is walking in still air
  faint enough? Is a storm (about -19 LUFS, a busy street) too loud? `OPENFPS_EAR_WIND=0` for an A/B.
- Car fronts: engine noise out of the bay, radiator fans (inbox/car-fronts-2026-10-05). Cody's first
  listen: the renders were far too quiet (written at raw scale, -55 to -65 dBFS), and some fronts
  louder than the tailpipes. Being redone.
- Rain on surfaces, thunder and lightning (inbox/rain-2026-10-05, inbox/thunder-2026-10-05), when built.
- The Magnolia and Albany maps, when built (`/join magnolia tx`, `/join albany or`).

### 2. Cody to say
- The admin gun's report: which of the five (`/admingun report 1-5`, inbox/admin-gun-2026-10-05).
  Default is 1; he said the sounds are fine.
- Do the reflections follow a moving talker, and follow himself? He will check again.
- The loudness law places a sound by its peak. Anything over about 126 dB peak at 1 m (gunshots, a
  window breaking, a thin steel plate) plays at full scale out to the 40 m clamp, so 5 m and 20 m
  sound the same (`Loudness.Place`).
- City temperature: city.json authors 42 as an OFFSET over the server's seasonal curve (+22 C), so
  the city runs 17 C in January and over 50 C in summer, and every stopped car's fan runs. Intended?
- Car fans: real pickups and vans mostly have a belt-driven viscous-clutch fan (none modelled yet);
  the car fan's 205 Hz blade hum stands 23 dB over its noise (level derived from the truck fan); a
  3.4 kHz block mode is 12 dB louder from the front now and may be heard as a whistle.
- Lightning: a spoken or tone cue at the flash ("lightning, north east"), or only the thunder?
- The scope hint ("Nobody in view. Nearest person 60 metres, below and to the left"): parked by Cody
  2026-10-05. At 4 power the scope sees a 6 degree cone, so from a roof looking level the street
  below is out of view.

### 3. Door models: gaps
- The lift's close is soft: no bump and no rebounds.
- The glass front door's bar push is about 5 dB over the steel door's.
- The knob door's push is about 9 dB over its pull. From round 4: turn clicks about 30 dB under the
  release, soft knocks after a close, the hard close 17-20 dB over the normal one.
- The client renders about 20 glass door sounds at start, 10-40 s of CPU each. Keep rendered door
  sounds in a disk cache.
- Not made yet: the lift reversing, the closer's sweep, a knock on the outside lever, the shop
  door's roller catch.
- A door leaf swings through furniture and walls.
- Footsteps, car doors and the knock declare a level against their peak the way doors did before
  2026-10-04 (14-29 dB low). Fix after the doors are heard. The knock (`DoorKnock`) may also be about
  20 dB short at 1-2 kHz against the recordings.
- Doors waiting on Cody: push-bar fire doors on the towers' stairwells (they would change every
  tower's acoustics); the terminal's road-side steel door, automatic if it is the public way in.

### 4. Roles: follow-ups
- `/undo` covers `/put` only: not `/place` or `/spawn`, and not after a reconnect.
- A parked aircraft from `/spawn` is not saved with the map.
- Spawned walkers get no crosswalks.
- Walkers and trains spawned on a shipped map are written into it by `/savemap`. That breaks the
  generated city.json.
- Old single grants of "tp" were dropped. Moderators need a teleporter.
- A map owner cannot make others editors; invite only lets them in.
- `CommandHandler.cs` is still 2,500 lines; split it by area.

### 5. Bodies: follow-ups
- Logging out while dead skips the 60 s wait.
- Walkers have no names of their own ("body of a pedestrian").
- Things given to a player while dead stay with them.
- Later: what a body is for (selling it, once there is money).

### 6. Admin gun: follow-ups
- A frozen player's client still predicts movement.
- Trains cannot be frozen.

### 7. Keys
Keyed doors work, and everyone can open them now. Who holds which keys is Cody's decision, later.

### 8. Network: next steps (waiting on Cody)
Resting states, packed positions and stats on change are done: 5.8 to 1.75 Mbit/s for three
players. The rest, with measured savings, is in docs/PLAN_2026-10-05.md section 6:
1. Rate by distance (5 Hz beyond 150 m): 50-70 %. Needs per-thing interpolation, and Doppler and
   tyre demand from the server.
2. A byte budget per packet filled by priority: caps the worst case.
3. Only the change, against what the client confirmed: about 30 %.
4. Traffic run on the client from routes, with corrections from the server: most of what remains.
5. Parts placed from their parent: about 7 %.
6. Voice: cull by distance, send nothing in silence, 32 kbit/s.
7. LZ4 compression: 10-15 %.

Also: Sean's voice stopped being heard after he changed his audio device (2026-10-04). Undiagnosed;
needs his client log and the server's voice line.

### 9. Walls, what is left (the panel model went in 2026-09-30, unheard)
- Listen: through a wall the lows and the rumble should come through and the top should not.
- A source just behind a building corner made of two boxes gets no diffraction route (the 5 cm joint
  padding in `RouteIsClear`). Fix without reopening the shut-door crack leak.
- Each floor is two overlapping 25 cm slabs, and carpet counts as a barrier: upstairs is about 15 dB
  too quiet in the lows. Merge layers in contact.
- No cavity resonances or air leaks: sealed glazing is about 10 dB optimistic in the mids; door gaps
  and seals are not modelled.
- The glass front doors let more in when shut than steel doors: 17/28/43 dB against 13/48/58
  (`--wall-tl`).
- Steam Audio counts walls in a row as (2n+1)/3 of one; the tracer counts them exactly.
- Diffraction is evaluated at 200/1250/8000 Hz, not the band averages transmission uses.

### 10. Acoustics still open
- If the game goes silent again, read the log for `[NONFINITE]`: the first line names the unit.
- Listen for the room answering at once (early-tail): clap in a main-street lobby and in flat 01F.
  Is 20-100 ms too strong? Speech and steady sounds get no copies, so they lose that share of the
  first 20 ms; a per-source early part would fix it.
- Listen for pops on the Main Street pavement and in Selby House and Marlow Tower. Cars behind
  buildings are now heard over the roofs: is the city too busy? `--pop-hunt` still finds:
  - indoors, a car's straight line through one wall as it passes a gap: -80 to -62 dB for 200-400 ms;
  - a pier on a wall: 3 dB down for about 1 m; under the covered hall west of Main Street, up to
    19 dB between over the wall and over the roof.
- Listen to the late tail (`SmoothTail`, `DiffuseLate`): does it pulse? Is it boomy below 500 Hz?
  Narrow above 2 kHz? A second noise per ear above 1 kHz would cost 25 MB and twice the convolution.
- Below 120 Hz the tail of a room you are not in is identical in both ears.
- A voice's ground reflection flanges, so voices have none. Next: a lab render with realistic head
  and body movement on both ends.
- Blocked sources come from one edge at a time. If a siren behind buildings wanders again, blend the
  routes by energy.
- The traced echoes convolve at 1,024 samples and arrive about 20 ms late.
- Furnished flats: beds, sofas, curtains and shelves placed by the generator.
- The traced decay of the tunnel and the garage against real figures.
- Beacons: door range 12 m to 6 m, and lifting beacons when a louder sound is near (proposed, not
  asked for).

### 11. Gunfire
Synthesis to spec; recordings are the yardstick only.
- In the mix a Glock at 0.5 m carries 3.7 dB less energy than a clap over 300 ms, because one-shots
  are 16-bit. A float path for registered sounds would let them reach the master limiter. Ear first.
- The shotgun has no recording to fit to.
- Casings that land and bounce where they fall.
- Dry fire: five guns share one "hammer falls" contact. Give each action its own mechanism. Cody:
  fine for now. The revolver's empty and partial reloads are listed twice in the render.
- A proper fire message in the protocol (fire is a text command).

### 12. Bodies and wheels
Plan: [docs/NEXT_BODIES_WHEELS_ROADS.md](docs/NEXT_BODIES_WHEELS_ROADS.md). Roads as data and per-wheel
physics are done (the second unheard).
- Stage 2: moving bodies in the acoustic scene (vehicles from their panels, people as soft bodies),
  so a bus or a crowd blocks sound like a wall of its size and material.
- Stage 4: rolling noise per wheel, left and right as separate directions, the surface under each,
  joints struck by each axle.
- Turning paths cut over the kerb on the 7 m estate roads (a junction connector needs a kerb-aware
  radius).
- No physical value hard-coded in a model or map; presets move to JSON.
- Stage 5: traffic lights and accessible pedestrian signals.

### 13. Left from the 2026-10-01 mutation triage
[docs/MUTATION_2026-10-01.md](docs/MUTATION_2026-10-01.md)
- A car stopped for a crossing on a turn can have a front corner up to 0.36 m inside the walkers'
  strip. Work each stop out from the body's corners.
- A car the deadlock breaker lets go creeps at about 0.3 m/s for 6-9 s before it enters.
- When the smoothed lap runs ahead, a car in a junction can count as on the next lane.
- Phone stories ("this morning") have no time filter. Muttered remarks bypass `AnyTime`.
- A huge finite look turn drives yaw without bound; wrap it.
- Test gaps: `/scan` output; door swing time and sound sets; composites walled solid, ghost
  collision, ungroup; parking door claims; your own car into a wall; pairs talking and reactions;
  crowd cooldown and radius; passenger view and velocity; the game clock at midnight.

### 14. Mutation testing (on hold, Cody 2026-10-02)
Results: [docs/MUTATION_2026-09-24.md](docs/MUTATION_2026-09-24.md),
[docs/MUTATION_2026-10-01.md](docs/MUTATION_2026-10-01.md). Harness: `~/.cache/openfps-stryker`
(8 GB heap cap, a watchdog, sections; see its README).
- When it resumes: Common in three sections, then Client.Core. Re-run server sections only for files
  changed since (`--since`).
- Client (`VoiceManager`, `VehicleShadow`, `BeaconAids`, `BirdLife`): 39.2 %, survivors not yet done.

### 15. Tests for the untested audio code
From [docs/COVERAGE_2026-09-24.md](docs/COVERAGE_2026-09-24.md):
- `ClientAudioSystem`: which vehicles get a live voice, at what level, and which get an acoustic path.
- `VehicleShadow.Apply` and `EngineReflections`.
- One test per DSP callback processor.
- `AsyncAcousticWorker` paths that do not need Steam Audio.

### 16. Zones (waiting on Cody)
- Today a zone is a named box in the map; the smallest box you stand in is the one said.
- Proposal: a room or building names its own zone from its walls; a free-standing zone (park, plaza,
  car park) stays a box; a zone inside a zone is said as the inner one, the outer on the where-am-I
  key.

### 17. Bump sounds
Walking into something (`--bumps`) is always the same woofy, hollow thunk. Missing: radiation
efficiency below a panel's critical frequency, and a hard contact of about a millisecond. Then anchor
the level to the footstep takes.

## Next

### Doors as live models (filed 2026-10-05, not started)
The door models are physical (hinges with their own friction and wear, the leaf's modes, the latch,
the stop), but each is rendered once per key, far slower than real time, and replayed from one point
by the handle; the leaf does not swing in the world. Cody wants the squeak in the hinge and the
leaf's sound from the swinging leaf. Needs: the leaf swinging on the server from how it was pushed;
the model reworked to run faster than real time (find what is slow first: probably the leaf's modes
and the contacts); taps at each hinge, the latch edge and the leaf, as trains and cars already do.
Knob door first, the approved renders as the yardstick. Large.

### Real places from open data
Maps from OpenStreetMap plus public records (tools/gen_osm.py; docs/MAPS_FROM_REAL_PLACES.md). Magnolia
TX (31907 Bobcat Lane) and Albany OR (1042 Belmont Ave SW), 3 km each, in progress 2026-10-05.
- Whole towns (a city's boundary, a ZIP area) need streaming: the map in tiles, full detail near the
  player and a coarse layer (shells, roads) beyond, loaded as you move. The same work serves the 10 km
  city and joined maps. A client detail setting (low, medium, high) then sets how far full detail
  reaches. The importer already tags tiles and has detail levels.
- Real weather for real places (Open-Meteo or NOAA by latitude and longitude).
- Traffic volumes from published counts (average daily traffic by road), with time-of-day curves.
- Ground height from USGS 3DEP once there is terrain.

### Map editor
In the game, typed commands and F-key menus with speech, building on `/map new`, `/put`, `/group`,
`/saveas`, `/place` and the build cursor. Missing: copy, rename, delete and publish maps; choose things
(nearest, by name, in an area) with each spoken back; move, turn and resize by numbers or small steps
with snapping; duplicate and repeat; undo and redo for everything; a property menu (material, sound,
model settings); a prefab browser that plays a prefab before placing it; ungroup; edit a prefab and
update every copy; a road tool (from, to, class, lanes, speed limit, junctions made automatically);
checks ("this room has no door"); "what is around me". Undo, choosing and moving first.

### Weather, the rest
Wind is done (2026-10-05); rain and thunder are being built. Still to do:
- Wet roads: tyre hiss and spray; wetness that lasts after the rain; puddles.
- Gutters, downpipes and run-off.
- Snow, sleet, hail and freezing rain; ice. Snow and ice underfoot: measure before synthesising
  (synthetic footsteps failed three times); recordings may be the answer.
- Wind tilting the rain; ear-wind shelter that depends on the wind's direction (a wall upwind).
- Grip from the weather (snow 0.2, ice 0.1 exist in `RoadSurfaces` but only from map materials).
- A more physical weather model: fronts, clouds, fog, a random walk instead of four fixed states.
- Ear wind assumptions to check: the per-angle figures, a helmet's 12 dB, the open-window cabin airflow.

### The ground reflection, done properly (discussed 2026-10-06)
The ground is the strongest reflection outdoors, so it stays, but as the ground really answers:
filtered by the surface's impedance (grass and soil absorb the top and shift the phase; Delany-Bazley
or Miki), with the coherent share falling with frequency, distance, roughness and turbulence
(Clifford and Lataitis; Nord2000 and Harmonoise do this), and the rest scattered. A perfect mirror
copy is what flanged, which is why speech has none today; done this way speech can have it back.
Moving bodies (cars, buses, aircraft, people) go into the acoustic scene so sound reflects off and
is shadowed by them (Bodies and wheels, stage 2).

### Tests on GitHub (asked 2026-10-06)
Every test and build runs on Cody's machine today. GitHub Actions is free for a public repo: a
workflow sharded across parallel runners (4 cores each; the suite is 48 minutes on 24 cores here).
The Linux FMOD and Steam Audio libraries are not in the repo: Steam Audio is Apache-2.0 and can be
fetched in the workflow; FMOD's licence does not allow publishing its SDK, so tests that need FMOD
either skip in CI or get the library from a private store.

### Fire at any size
The fire model scales (a wider fire is lower and slower; more heat, a louder roar and more crackles)
but is one point source with a capped crackle stream. A blaze needs sources over the burning area,
the turbulent roar of a large flame driven by the wind, trees flaring up, and falling branches.
Medium.

### Water bodies
Lakes and ponds lapping at a shore, creeks running, surf. The importer brings them in as zones; none
has a sound model.

### Explosions
Charge as TNT equivalent; peak overpressure and positive-phase duration from Kingery-Bulmash scaled
distance; the Friedlander waveform near, a low boom far; ground reflection; the city's echoes; glass
that breaks above an overpressure. Needs the loudness law fixed for very loud sounds first.

### Vehicles: what is still simple
- Bodies: a driven car is about ten boxes; traffic cars are one box; no vehicle is in the acoustic
  scene, so nothing reflects off a car.
- No suspension: one ground sample under the middle, no pitch or roll, a kerb lifts the whole car.
- Brakes: ideal sharing by load, no brake torque, no wheel inertia, no ABS.
- The server shifts instantly; the clutch exists only in the sound.
- No belt or alternator whine of its own.
- Fuel: a tank per vehicle, use from power (brake-specific fuel consumption), sputter and stall when
  dry, gas stations with pumps, the nozzle click and the fill.
- Road classes: the city has no highway. Arterials at 40-45 mph and highways at 65 mph, with lane
  changes, on and off ramps (the ramps need shapes other than boxes).

### Shapes other than boxes
Plan in docs/NEXT_CITY_10KM.md. Order: a wedge ramp and stairs from steps (medium); a height grid for
terrain; full meshes later (the largest engine job so far). Ground in layers you can dig into is a
voxel ground, a separate structure. Then forests, ocean maps and boats.

### Experience and badges
Rules, categories and tiers in docs/PLAN_2026-10-05.md section 3: scored by difficulty and variety,
judged by the server, five tiers per category. Marksmanship, driving, exploration and community can
start now. `/profile` then shows level and badges.

### The wider world
In order, each needing the one before: money (a ledger), banks, shops and selling, mail, NPC lives
(home, job, a day's schedule).

### The sword and melee
An "Iron Sword" item exists; nothing swings it. Model it like the doors: the blade's modes, the draw
along the scabbard, the swing's air, strikes per material. Melee itself (swing, reach, block, hit)
with it.

### Flying a helicopter
Helicopters can be given and spawned but only stand. Flying needs its own controls (collective,
cyclic, pedals on keys). Giving jets later; the only jet is the airliner.

### People, phones and characters
- Still to generate: the characters (gang crew, delivery driver, police and the rest) and the
  vocalisations, from `~/npc-lines-2026-09-28`; conversations then get their laughs.
- The footstep bank's labels need a check by ear (`inbox/footstep-file-review-2026-09-28`, 193 clips).
  Then jog and run by speed, scuffs on turns, a shoe per person.
- A phone model: ringtones through a small speaker, vibration on a table and in a pocket, the far side
  heard from the earpiece up close, coverage from cell towers later.
- Two people walking together, sharing a line from `pairs.py`.
- Characters: an east-end crew that warns a stranger in stages and shoots at the last; a delivery
  driver who knocks and calls out.
- Machines to model: elevators, garage doors, leaf blowers, fridges, smartphones, a piano that plays
  MIDI. What to record: `~/Desktop/openfps-sounds-to-source.md`.

### Building services (Cody: after the core sounds)
Corridor ventilation, fridges, extractor fans, pipes, lift machinery and electrical hum as physical
sources placed by the generator. Also an inner lobby door in the towers.
Also asked 2026-10-05: gas stove burners (the piezo click, the gas, the flame), cookware (sizzle,
boiling, a lid rattling), HVAC with ducts and vents, running water and sinks, and electricity with a
power grid (substations and lines as data; an outage silences fridges, fans and lights).

### A map's own sounds
A map or server carries a sound pack: named cues (login, chat, menus, beacons), each a short
recording or a synthesiser recipe, cached by the client, normalised on import, and switchable off by
the player. Anything missing uses the built-in sound.

### The new city
Plan: [docs/NEXT_CITY_10KM.md](docs/NEXT_CITY_10KM.md): a 10 x 10 km city, shapes other than boxes,
and building a map in the game. Needs the roads and bodies work first.

### Listen and confirm (older)
- Arriving: "You're in <map>, at <zone>."; no "Under Shelter" at doorways.
- Street life: honks, hard stops, cars parking.
- Pedestrian and driver speech since the fix for the room-like copy.
- Driving aids, now in the head.
- The car starter in the game (inbox/starter-2026-10-02/).
- The street washing into a lobby through an open door, and two rooms in.
- Bus air brakes; the airliner's whine.

### Load and stuck-voice checks
Some may already be fixed; confirm before fixing again.
- The speedway's 40-car load has never been measured.
- Two field-wide silences of about 150 ms.
- A voice placed at a position 1.9 s old.
- The PA announcement that never decodes.
- Vehicles stopping close in front of the player.
- The VPS map load time with batched definitions (1.7 s on the loopback). If long, cache maps by
  `MapManifest.Checksum`.

### Server
- A command to change a password (your own, and an admin resetting someone's).
- A ban list, by name and by address.
- `/restart` and `/reloadmap` for admins.
- The default admin is `admin` / `admin123` unless `OPENFPS_ADMIN_PASSWORD` is set: make the first
  run ask.
- A failed port bind still logs "started". Fail loudly.
- `/savemap` rewrites a map as plain JSON and loses its comments.
- Name locks and rate-limit counts are in memory and reset on restart.
- The MUD listens on all interfaces in plain text. Decide whether a public server should listen for
  it at all, or only on localhost.
- Saving world state across restarts (vaporized and spawned things come back or vanish).

### Vehicles
- No horn key and no siren key when driving.
- Driving cues need a design: Cody overshoots the road; something like Forza's.
- Aircraft roll out after landing instead of reversing at the end of the runway.
- Level crossings: tyre thump over the rails, and gates.
- In a car seat, cranking carries sub-20 Hz pressure 13-16 dB over everything audible. Decide with
  the cabin model.
- From the 2026-09-30 set-aside (they sound good):
  - `Steepening` is capped at 1.5 (`ExhaustNetwork.cs` 145): the sports bike's 1.6 and the V-twin's
    2.0 do nothing.
  - `WallLossMultiplier` and the pickups' pipe size and steepening have no anchor.
  - A narrow tailpipe radiates less bass (`Waveguide.cs`); not checked.
  - `single` and `vtwin_stock` never measured on `--voice-levels`.
  - The 2.8 turbo diesel is jet-heavy.
  - Motorbike exhausts too long ("farting into a bottle"); about 0.5 m.
  - The diesel pickup that sounds backwards (turbo lag, fuelling, gear changes).
  - A V10 and a V12 from sourced firing orders and headers.
  - One table for every preset: declared level, live level, bay leakage, extent.
  - Buses about 5 dB under real life; a big cam's idle lope; the sports bike's pull-away surge.

### Client
- The client sends interact only within 3 m; the server allows 5 m.

### Sound synthesis to come
- Rain on surfaces is being built (2026-10-05); wetness that lasts is not.
- Wet roads: tyres +4-7 dB above 2 kHz.
- Streams and surf.
- Explosions (see Explosions above).
- Refraction past 150 m; wind and temperature against height; turbulence.

## Later

### Acoustics
- Synthetic footsteps (parked 2026-09-28 after three failures by ear). If tried again: fit only the
  dry first 30-60 ms, give the heel the body's weight, and give each loose surface its own mechanism.
- Aggregation: many distant sources heard as one extended source.
- Fused early reflections (inside 50 ms) by convolution or delay taps.
- One acoustic path per machine: a wall between you and one end of a bus is not modelled.
- A check that the HRTF voice count holds through a crowd reaction.

### Engines and vehicles
- Idle hunting on the NASCAR, muscle car and V10.
- F1 above 12,300 rpm, and its airbox as a Helmholtz volume.
- Exhaust pipe delays that follow gas temperature.
- Tyres on gravel and wet roads.
- Fit the body `Coupling` and `ShellLevel` to recordings.
- Collision damage.
- Walking about inside a moving bus.
- Getting into traffic cars.

### World and gameplay
- The siren switches off at every short stop; real crews keep it on through a junction.
- Elevators: the lift doors are modelled, but there is no car, shaft or machine, and none on the
  city. Signal sounds in `inbox/elevator sounds`.
- Crowds; gunfire as occasional world events.
- A crowd murmur from the recorded chatter (8 or more talkers), for the grandstand and busy places.
- Crowd reaction lines to generate: cheers, gasps, laughter, groans.
- Speedway crowd: three stand blocks never react; the first reaction of each kind is silent; the clap
  balance; crowd reflections.
- Held items that rattle, a sound for jumping off, footstep loudness that follows speed.

### Platform and server
- A stress test with 50 or more bots.
- Check the SQLite package for security updates.

## Ideas

- Shapes other than boxes: curved kerbs, round columns, trees, and acoustics that handle them.
- Airport take-offs and landings.
- A real mourning dove; wing flaps when a flock is startled.
- Walking speed: 4.5 m/s is a jog.
- Mac client; a web version.
- Engine braking, rev-matched downshifts, gear whine.
