# To do

Planned work. Finished work is in [changes.md](changes.md), `git log` and
[docs/DONE_2026-10.md](docs/DONE_2026-10.md). "(Cody)" marks what waits on Cody's ear or decision.
Updated 2026-10-07: every item checked against the code, the git history, changes.md and Cody's approvals.

## Next, in order

1. Finish this batch: the honest tests and the lab argument bugs; the probable bugs (below), on their
   own branch, heard by Cody before they merge.
2. One full test run on the final main (`tools/test-local.sh`).
3. Push, then the VPS, then the Windows zip. Cody: only after the whole batch. The VPS runs
   f1a0421123d3 (2026-10-05); the wire has changed since, so client and server go together.
4. Geometry stage 3, terrain from real elevation (docs/GEOMETRY.md section 7), with world streaming
   stage 2, one world in UTM tiles generated on demand (docs/WORLD_STREAMING.md).
5. Sound library stage 3 alongside: `OpenFPS.Sound` and the first half of `OpenFPS.Acoustics`
   (docs/SOUND_LIBRARY_BOUNDARY.md section 8).
6. World editor phase 3: people, roads and routes, map versions, baking an overlay into a map file
   (docs/WORLD_EDITOR.md section 9).

Waiting on Cody's ear:
- Driving cues and horns: H horn, U siren, J and L indicators, the brake cue, line rumble, the speed
  limit, rails, gates, aircraft roll-out (inbox/driving-2026-10-06). The brake cue's notes, the rail
  strike level and the gate motor and clunk are assumptions.
- Trains' own horn, whistle and bell; air conditioners cycling with the weather
  (inbox/fault-fixes-2026-10-06).
- Downpipes, round 2: the flange should be gone (inbox/water-smoothing-2026-10-06/round2).
- The world editor in the game (F12), and `/editorkeys on` with Orca, then NVDA.
- The probable-bug fixes, before and after (inbox/probable-bugs-2026-10-07).

## Now

### Probable bugs
Found by the housekeeping on 2026-10-07; not fixed on main. Fixes go on their own branch.
- `WorldAudioPlayer.Clear` is never called on `/join`: one-shots and thunder carry over to the next map.
- Every emitter gets `ApplyGround`: `physicalKey != null` is always true (ClientAudioSystem.cs 2864;
  probably meant `Length > 0`).
- A landing plays a footstep take, never the LANDING bank (`SubmitLanding` passes force 0).
- `SetSimulatedReverbDecay` takes the hf and lf decay ratios and does not use them, so a room's colour
  never reaches the reverb (FmodAudioProvider.cs 1110).
- The bell's clapper damping comes back at the end of the contact, 0.1-0.3 ms, not the 12 ms `Step`
  allows (StruckBell.cs 132).
- A hull's blow lands at the start of its block, up to 2.7 ms early (`ShoreSynth.HullPlate.Blow` does
  not read `at`).
- The fire's fizz is scaled by `CracklePart` (FireSynth.cs 870).
- `Footsteps.Key` writes twice the speed, so 1.4 m/s reads back as a run (AudioLab only).
- Not heard: `AggressiveOptimization` sits on `EngineProcessor.QueueStrikes`, meant for `Synthesize`;
  `AcousticPathData.ReflectionId` and `Scattering` are written and never read.
- The traced reverb stage averages its stereo input (TracedReverbDsp `v /= inCh`), so since the
  binaural input fix of 2026-10-06 the tail is 3 dB drier than the -6 Cody set by ear. Fix the input or
  move the trim (Cody).
- A new engine donor is held for 2.5 s inside the budget, so one budget car is let go once and rebuilt
  2.5 s later (docs/COVERAGE_2026-10-06.md).

### Listen in the game (Cody)
Built and measured, not heard in the game. Restart the server and update the client first.
- Doors: push and pull sides, the tower front doors locked from the street (key, then pull), nothing
  shutting on a person. The glass front door, glass shop door and lift door models
  (inbox/door-models-2026-10-05).
- Bodies and carrying: carrying slows you to 1 m/s against a 4.5 m/s walk. Is the pace right?
- The trees in the wind, rounds 2 and 3 (inbox/nature-round2/3-2026-10-05). Round 2: "almost there".
- The six Resonance-port fixes (tyres at the device rate, the bus compressor at the back, rail axles from
  the model file, machines on a base, the air horn, the bell's headroom) and the diesel locomotives.
- After a map change sounds keep their direction: travel city, speedway, city.
- Car fronts: the engine bay and radiator fans, re-rendered at game level (inbox/car-fronts-2026-10-05).
- Thunder round 2 and lightning (`/weather storm`; inbox/thunder-round2-2026-10-05).
- Magnolia and Albany (`/join magnolia tx`, `/join albany or`), streamed as you move.
- The cab heard from where each sound comes in (inbox/cabin-2026-10-06); wet roads
  (inbox/wet-roads-2026-10-06); distant woods; Alex on the city; ramps, stairs and arches.
- The ear model's effects (idles louder, birds quieter) and `/listening`; the 48 kHz mixer and limiter.
- Older: arriving speech ("You're in <map>, at <zone>", no "Under Shelter" at doorways); street life
  (honks, hard stops, parking); pedestrian and driver speech; driving aids in the head; the car starter
  (inbox/starter-2026-10-02); the street through an open lobby door, and two rooms in; bus air brakes;
  the airliner's whine; walls (lows through, top not).

### Decisions (Cody)
- Reflections following a moving talker, and himself: he will check again.
- Loudness law: anything over about 126 dB peak at 1 m (gunshots, glass, a thin plate) plays at full
  scale out to the 40 m clamp, so 5 m and 20 m sound the same (`Loudness.Place`). Explosions wait on it.
- City temperature: city.json's 42 is an offset over the seasonal curve, so the city runs 17 C in
  January and over 50 C in summer, and every stopped car's fan runs. Intended?
- Car fans: real pickups and vans mostly have a belt-driven viscous-clutch fan (not modelled); the car
  fan's 205 Hz hum is 23 dB over its noise; a 3.4 kHz block mode is 12 dB louder from the front.
- Lightning: a spoken or tone cue at the flash, or only the thunder?
- The scope hint ("Nobody in view..."): parked by Cody 2026-10-05.
- The commuter cars at a cruise are mostly tyre noise on one shared tyre, 11-20 dB over their engines
  ("too reverby, all the same", inbox/vehicles-2026-10-05).
- The Elm Park fountain's basin overflow: +4.6 dB on its side; not connected.
- A tap and its plug on one key, or two?
- Steam Audio pathing never runs (`enablePathing: false`); routes come from OpeningRoutes. Turn it on, or
  remove it.
- Server: `/move NAME` is staff-only, so an owner cannot use it on their own map. Intended?
- `/savemap` on a generated map (the city, the real places) rewrites the generated file. Refuse it?
- Hardening limits in play: chat 6 then one every 2 s; commands 20 then 5/s; editor changes 20 then
  4/s; `/join` 3 then one every 10 s; chat 512 characters, commands 1,024; no cap on model versions.
  Recommended: keep.
- MemoryPack's string-length overflow (one packet crashed the server) is not fixed upstream: report it
  to Cysharp?
- Sound library: `ColliderShape` stays in Common; CI compares renders by tolerance; 430 KB per stream
  regeneration. Recommended: yes to all three.
- World editor: carry a map entry's `Form` through Remake and duplicate (recommended).
- Streaming: a menu control for `/detail`; tree counts and cost of distant woods (+0.5 core); the cab's
  own reverb adds up to 20 dB at the bottom, so it is unchanged.

### Housekeeping finds
Left by the 2026-10-07 housekeeping; none changes sound.
- `ReachingFrom`'s two branches return the same point; `OccupancyService.Moving` stops getting in
  above 2.0 m/s for a driven car and 1.0 m/s for a map's bus; on tiled maps, confirm how the broadcast
  forgets dynamic things a client knew (Program.cs).
- `SharedMovementEngine`'s `stepped = true` is never read; `WindModel` and `WindField` overlap;
  `TransientSound` lacks its APPEND ONLY marker; `PhysicsUtils.GroundMaterial` returns its input.
- `ElevatorDoor.faceAtFoot` and `PushBarDoor.railHit` are kept only for the random sequence: retire them
  with a render regeneration.
- Nullable warnings: CS8602 in Spawning and MovementSystem, CA2014 in SolidContact, CS8714 in
  SpatialGrid.
- Stale code comment: RainField.cs 33 lists gutters, downpipes and run-off as missing (built 2026-10-06).
- `Machines.cs` 87: the declared extent is read by nothing; it should replace the car-sized
  `MathF.Max(reference, 3f)` in ClientAudioSystem, with audibility ranking.

### Doors
- The lift's close is soft: no bump and no rebounds.
- The glass front door's bar push is about 5 dB over the steel door's.
- The knob door's push is about 9 dB over its pull; turn clicks about 30 dB under the release; soft
  knocks after a close; the hard close 17-20 dB over the normal one.
- Not made: the lift reversing, the closer's sweep, a knock on the outside lever, the shop door's
  roller catch.
- A door leaf swings through furniture and walls.
- Footsteps, car doors and the knock declare a level against their peak, as doors did before
  2026-10-04 (14-29 dB low). The knock (`DoorKnock`) may also be about 20 dB short at 1-2 kHz.
- Push-bar fire doors on the towers' stairwells (they change every tower's acoustics); the terminal's
  road-side steel door, automatic if it is the public way in (Cody).

### Roles, bodies, admin gun, keys
- `/undo` covers `/put` only: not `/place` or `/spawn`, and not after a reconnect (`/edit undo` covers
  the editor).
- Spawned walkers get no crosswalks.
- Old single grants of "tp" were dropped. Moderators need a teleporter.
- Walkers have no names of their own ("body of a pedestrian"); only Alex is named.
- Trains cannot be frozen.
- Who holds which keys (Cody, later). Keyed doors work, and everyone can open them now.

### Network (Cody)
1.75 Mbit/s for three players after the 2026-10-05 trim. The rest, with measured savings, is in
docs/PLAN_2026-10-05.md section 6:
1. Rate by distance (5 Hz beyond 150 m): 50-70 %. Needs per-thing interpolation, and Doppler and tyre
   demand from the server.
2. A byte budget per packet filled by priority: caps the worst case.
3. Only the change, against what the client confirmed: about 30 %.
4. Traffic run on the client from routes, with corrections from the server: most of what remains.
5. Parts placed from their parent: about 7 %.
6. Voice: cull by distance, send nothing in silence, 32 kbit/s.
7. LZ4 compression: 10-15 %.

Sean's voice stopped being heard after he changed his audio device (2026-10-04). Needs his client log
and the server's voice line.

### Walls
- A source just behind a building corner made of two boxes gets no diffraction route (the 5 cm joint
  padding in `RouteIsClear`). Fix without reopening the shut-door crack leak.
- Each floor is two overlapping 25 cm slabs, and carpet counts as a barrier: upstairs is about 15 dB
  too quiet in the lows. Merge layers in contact.
- No cavity resonances or air leaks: sealed glazing is about 10 dB optimistic in the mids; door gaps
  and seals are not modelled.
- Shut glass front doors let more in than steel doors: 17/28/43 dB against 13/48/58 (`--wall-tl`).
- Steam Audio counts walls in a row as (2n+1)/3 of one; the tracer counts them exactly.
- Diffraction is evaluated at 200/1250/8000 Hz, not the band averages transmission uses.

### Acoustics
- The room answering at once (early tail): clap in a main-street lobby and in flat 01F. Is 20-100 ms
  too strong? Speech and steady sounds get no copies; a per-source early part would fix it (Cody).
- Pops on the Main Street pavement and in Selby House and Marlow Tower; is the city too busy with cars
  heard over the roofs (Cody)? `--pop-hunt` still finds a car's straight line through one wall as it
  passes a gap (-80 to -62 dB for 200-400 ms), and up to 19 dB between over the wall and over the roof
  under the covered hall west of Main Street.
- The late tail: boomy below 500 Hz, narrow above 2 kHz? A second noise per ear above 1 kHz would cost
  25 MB and twice the convolution (Cody).
- Below 120 Hz the tail of a room you are not in is identical in both ears.
- A voice's ground reflection flanges, so voices have none. Next: the ground as it answers (Next,
  below), and a lab render with head and body movement on both ends.
- Blocked sources come from one edge at a time. If a siren behind buildings wanders again, blend the
  routes by energy.
- The traced echoes convolve at 1,024 samples and arrive about 20 ms late.
- Furnished flats: curtains and shelves (a sofa and a bed per flat since 2026-09-29).
- The traced decay of the tunnel and the garage against real figures.
- Beacons: door range 12 m to 6 m, and lifting beacons when a louder sound is near (proposed, Cody).

### Water
- A big river's bank in a calm is silent: an eddy's wave never breaks (`breaks = !eddy`).
- Shingle reaches the master limiter at 3 m: the plunging breakers (90.9 dB), not the stones.
- Boats too clicky; lake lows 15-25 dB under the recordings (which carry mic wind).
- The fountain's rim jets are heard from the rocks' taps, 2.3 m from its middle, though they land near
  the kerb.
- Showers 11-22 dB darker than the recordings at 4-16 kHz; the dripping tap rings steel where real
  drips plink into water; the violent-rain gutter outlet 4-6 dB heavy at 250-500 Hz.
- Not built: toilet flush and cistern refill; the plug as its own control; NPCs using taps; pipe walls
  radiating; trap seals; snowmelt.
- AudioLab `--waves levels heard=` ignores `parts=`.

### Gunfire
Synthesis to spec; recordings are the yardstick only.
- The shotgun has no recording to fit to.
- Casings that land and bounce where they fall.
- Dry fire: five guns share one "hammer falls" contact (Cody: fine for now). The revolver's empty and
  partial reloads are listed twice in the render.
- A proper fire message in the protocol (fire is a text command).
- AudioLab: `GunSpecSpike` does not read its angle; every angle renders on axis.

### Bodies and wheels
Plan: [docs/NEXT_BODIES_WHEELS_ROADS.md](docs/NEXT_BODIES_WHEELS_ROADS.md). Roads as data and per-wheel
physics are done.
- Stage 2: moving bodies in the acoustic scene (vehicles from their panels, people as soft bodies), so
  a bus or a crowd blocks sound like a wall of its size and material (geometry stage 6).
- Stage 4: rolling noise per wheel outside the car (inside it is per wheel since 2026-10-06), left and
  right as separate directions, the surface under each, joints struck by each axle.
- Turning paths cut over the kerb on the 7 m estate roads (a junction connector needs a kerb-aware
  radius).
- No physical value hard-coded in a model or map; presets move to data (engines and vehicles are
  editable library kinds since 2026-10-07).
- Stage 5: traffic lights and accessible pedestrian signals.

### Left from the 2026-10-01 mutation triage
[docs/MUTATION_2026-10-01.md](docs/MUTATION_2026-10-01.md)
- A car stopped for a crossing on a turn can have a front corner up to 0.36 m inside the walkers'
  strip. Work each stop out from the body's corners.
- A car the deadlock breaker lets go creeps at about 0.3 m/s for 6-9 s before it enters.
- When the smoothed lap runs ahead, a car in a junction can count as on the next lane.
- Test gaps: `/scan` output; door swing time and sound sets; composites walled solid, ghost
  collision, ungroup; parking door claims; your own car into a wall; pairs talking and reactions;
  crowd cooldown and radius; passenger view and velocity; the game clock at midnight.

### Tests
- Mutation testing is on hold (Cody 2026-10-02). When it resumes: Common in three sections, then
  Client.Core; server sections only for files changed since (`--since`). Client (`VoiceManager`,
  `VehicleShadow`, `BeaconAids`, `BirdLife`): 39.2 %, survivors not yet done. Harness
  `~/.cache/openfps-stryker`; results docs/MUTATION_2026-09-24.md and docs/MUTATION_2026-10-01.md.
- Untested in ClientAudioSystem: the starvation path, `PlayReferenceVoice`, `SubmitLanding`.

### Zones (Cody)
- Today a zone is a named box in the map; the smallest box you stand in is the one said.
- Proposal: a room or building names its own zone from its walls; a free-standing zone (park, plaza,
  car park) stays a box; a zone inside a zone is said as the inner one, the outer on the where-am-I key.

### Bump sounds
Walking into something (`--bumps`) is always the same woofy, hollow thunk. Missing: radiation
efficiency below a panel's critical frequency, and a hard contact of about a millisecond. Then anchor
the level to the footstep takes.

## Next

### Doors as live models (filed 2026-10-05, not started)
The door models are physical but rendered once per key, slower than real time, and replayed from the
handle; the leaf does not swing in the world. Cody wants the squeak in the hinge and the leaf's sound
from the swinging leaf. Needs: the leaf swinging on the server from how it was pushed; the model faster
than real time (find what is slow first: probably the leaf's modes and the contacts); taps at each
hinge, the latch edge and the leaf. Knob door first, the approved renders as the yardstick. Large.

### Real places from open data
tools/gen_osm.py; docs/MAPS_FROM_REAL_PLACES.md. Magnolia and Albany are built and stream by tile.
- Whole towns: world streaming stage 2 (Next, in order).
- Real weather for real places (Open-Meteo or NOAA by latitude and longitude).
- Traffic volumes from published counts (average daily traffic by road), with time-of-day curves.
- Ground height from USGS 3DEP: geometry stage 3 (Next, in order).

### Map editor
Phases 1 and 2 are built (docs/WORLD_EDITOR.md). Phase 3 is in Next, in order. Also missing: copy,
rename, delete and publish maps; checks ("this room has no door"); groups as one composite (only if
building houses needs it); a preview of a wall being struck.

### Weather, the rest
- Snow and ice underfoot: measure before synthesising (synthetic footsteps failed three times);
  recordings may be the answer.
- Wind tilting the rain onto walls and windows (a vertical pane takes no drops today); ear-wind shelter
  that depends on the wind's direction (a wall upwind).
- Grip on snow and ice from the weather (0.2 and 0.1 exist in `RoadSurfaces`, from map materials only).
- A more physical weather model: fronts, clouds, fog, a random walk instead of four fixed states.
- Ear wind assumptions to check: the per-angle figures, a helmet's 12 dB, the open-window cabin airflow.
- Wet roads, not modelled: wipers, ruts, porous asphalt, puddles away from kerbs, spray in the air
  (docs/WET_ROADS.md section 6). In a car the wet tyres are about 12 dB up, against a recalled 2-5.
- Rain: in a car at moderate rain the 10 ms kurtosis is 4.5, just over the recordings; sleet and hail
  stay sharp ticks (no recording to compare).

### The ground reflection, done properly (discussed 2026-10-06)
The ground is the strongest reflection outdoors, so it stays, but as the ground really answers:
filtered by the surface's impedance (Delany-Bazley or Miki), with the coherent share falling with
frequency, distance, roughness and turbulence (Clifford and Lataitis; Nord2000, Harmonoise), and the
rest scattered. A perfect mirror copy is what flanged, which is why speech has none today. Moving
bodies go into the acoustic scene with Bodies and wheels stage 2.

### Fire, the rest
Fire at any size is built and approved (docs/FIRE.md). Open:
- the crown fire against a real crown-fire recording (none found);
- the car against more than two recordings;
- smoke explosions in a closed building;
- spotting ahead of a crown fire;
- fire spreading from one thing on a map to the next.

### Water you are in or on (asked 2026-10-06)
After the triangle geometry and real terrain (geometry stage 3), which give water a surface and depth:
- Under water: sound about 4.3 times faster, so direction is mostly lost; little from the air above;
  muffled, partly bone-conducted hearing; rain on the surface overhead, snapping shrimp; your own breath
  bubbles. Needs swimming and diving.
- Boats as floating vehicles that pitch and roll, the hull heard from inside, wavelets slapping it.
- Sea surf and shingle are on no map yet.

### Explosions
Charge as TNT equivalent; peak overpressure and positive-phase duration from Kingery-Bulmash scaled
distance; the Friedlander waveform near, a low boom far; ground reflection; the city's echoes; glass
that breaks above an overpressure. Needs the loudness law fixed for very loud sounds first.

### Vehicles: what is still simple
- Bodies: a driven car is about ten boxes; traffic cars are one box; no vehicle is in the acoustic
  scene, so nothing reflects off a car.
- Suspension: a driven car pitches and rolls on four wheel rays, but has no springs; traffic has one
  ground sample under the middle.
- Brakes: ideal sharing by load, no brake torque, no wheel inertia, no ABS.
- The server shifts instantly; the clutch exists only in the sound.
- No belt or alternator whine of its own.
- Fuel: a tank per vehicle, use from power, sputter and stall when dry, gas stations with pumps, the
  nozzle click and the fill.
- Road classes: the city has no highway. Arterials at 40-45 mph and highways at 65 mph, with lane
  changes, on and off ramps.

### Vehicles
- Crossing gates are sounds only: no arm in the world, nothing stops a player's car.
- From the driver's seat a horn's first 0.15-0.3 s is about 16 dB louder than the rest (street-life
  honks too): find what lets a loud close source through late.
- The city's runway is 588 m: only the light single can land and take off on it.
- In a car seat, cranking carries sub-20 Hz pressure 13-16 dB over everything audible. Decide with the
  cabin model.
- Cabin: 250 Hz-1 kHz and idle are still more alike in the two ears than a real cabin; the tread tone
  is one tone in phase on every tyre.
- The airliner's squeal note (430 Hz): a squeal or a groan? (Cody)
- From the 2026-09-30 set-aside (they sound good):
  - `Steepening` is capped at 1.5 (ExhaustNetwork.cs 122): the sports bike's 1.6 and the V-twin's 2.0
    do nothing.
  - `WallLossMultiplier` and the pickups' pipe size and steepening have no anchor.
  - A narrow tailpipe radiates less bass (`Waveguide.cs`); not checked.
  - `single` and `vtwin_stock` never measured on `--voice-levels`.
  - The 2.8 turbo diesel is jet-heavy.
  - Motorbike exhausts too long ("farting into a bottle"); about 0.5 m.
  - The diesel pickup that sounds backwards (turbo lag, fuelling, gear changes).
  - A V10 and a V12 from sourced firing orders and headers.
  - One table for every preset: declared level, live level, bay leakage, extent.
  - Buses about 5 dB under real life; a big cam's idle lope; the sports bike's pull-away surge.

### Shapes other than boxes
Wedges, stairs and arches are built (geometry stage 2). Next is terrain (geometry stage 3); then the
shape library and import (stage 4); diggable ground with strata (stage 5). Then forests, ocean maps and
boats.

### Experience and badges
Rules, categories and tiers in docs/PLAN_2026-10-05.md section 3: scored by difficulty and variety,
judged by the server, five tiers per category. Marksmanship, driving, exploration and community can
start now. `/profile` then shows level and badges.

### The wider world
In order, each needing the one before: money (a ledger), banks, shops and selling, mail, NPC lives
(home, job, a day's schedule). Then: what a body is for (selling it, once there is money).

### The sword and melee
An "Iron Sword" item exists; nothing swings it. Model it like the doors: the blade's modes, the draw
along the scabbard, the swing's air, strikes per material. Melee itself (swing, reach, block, hit).

### Flying a helicopter
Helicopters can be given and spawned but only stand. Flying needs its own controls (collective,
cyclic, pedals on keys). Giving jets later; the only jet is the airliner.

### People, phones and characters
- Still to generate: the characters (gang crew, delivery driver, police and the rest) and the
  vocalisations, from `~/npc-lines-2026-09-28`; conversations then get their laughs.
- The footstep bank's labels need a check by ear (`inbox/footstep-file-review-2026-09-28`, 193 clips)
  (Cody). Then jog and run by speed, scuffs on turns, a shoe per person.
- A phone model: ringtones through a small speaker, vibration on a table and in a pocket, the far side
  heard from the earpiece up close, coverage from cell towers later.
- Two people walking together, sharing a line from `pairs.py`.
- Characters: an east-end crew that warns a stranger in stages and shoots at the last; a delivery
  driver who knocks and calls out.
- Alex: no shops on the city for him yet; cars do not yield to him.
- Machines to model: elevators, garage doors, leaf blowers, fridges, smartphones, a piano that plays
  MIDI. What to record: `~/Desktop/openfps-sounds-to-source.md`.

### Building services (Cody: after the core sounds)
Corridor ventilation, fridges, extractor fans, pipes, lift machinery and electrical hum as physical
sources placed by the generator; an inner lobby door in the towers. Asked 2026-10-05: gas stove burners
(the piezo click, the gas, the flame), cookware (sizzle, boiling, a lid rattling), HVAC with ducts and
vents, and electricity with a power grid (substations and lines as data; an outage silences fridges,
fans and lights). Taps, sinks and showers are built.

### A map's own sounds
A map or server carries a sound pack: named cues (login, chat, menus, beacons), each a short
recording or a synthesiser recipe, cached by the client, normalised on import, and switchable off by
the player. Anything missing uses the built-in sound.

### The new city
Plan: [docs/NEXT_CITY_10KM.md](docs/NEXT_CITY_10KM.md): a 10 x 10 km city, shapes other than boxes,
and building a map in the game. After streaming stage 2 and real terrain.

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
- The default admin is `admin` / `admin123` unless `OPENFPS_ADMIN_PASSWORD` is set: make the first run
  ask.
- A failed port bind still logs "started" (`NetworkService.Start` ignores the result). Fail loudly.
- `/savemap` rewrites a map as plain JSON and loses its comments.
- Name locks and rate-limit counts are in memory and reset on restart.
- The MUD listens on all interfaces in plain text. Decide whether a public server should listen for it
  at all, or only on localhost (Cody).
- Saving world state across restarts (vaporized and spawned things come back or vanish).

### Client
- The client sends interact only within 3 m; the server allows 5 m.

### Sound synthesis to come
- Refraction past 150 m; wind and temperature against height; turbulence.

## Later

### Acoustics
- Synthetic footsteps (parked 2026-09-28 after three failures by ear). If tried again: fit only the
  dry first 30-60 ms, give the heel the body's weight, and give each loose surface its own mechanism.
- Aggregation for traffic and crowds: many distant sources heard as one extended source (woods done).
- Fused early reflections (inside 50 ms) by convolution or delay taps.
- One acoustic path per machine: a wall between you and one end of a bus is not modelled; machines
  with several radiators are still one point.
- A check that the HRTF voice count holds through a crowd reaction.
- Masking (partial loudness), designed in docs/EAR_MODEL.md.

### Engines and vehicles
- Idle hunting on the NASCAR, muscle car and V10.
- F1 above 12,300 rpm, and its airbox as a Helmholtz volume.
- Exhaust pipe delays that follow gas temperature.
- Tyres on gravel.
- Fit the body `Coupling` and `ShellLevel` to recordings.
- Collision damage (`DrivingSystem` has no impulse or damage).
- Walking about inside a moving bus.
- Getting into traffic cars.

### World and gameplay
- The siren switches off at every short stop; real crews keep it on through a junction.
- Elevators: the lift doors are modelled, but there is no car, shaft or machine, and none on the city.
  Signal sounds in `inbox/elevator sounds`.
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

- Curved kerbs, round columns, trees as shapes, and acoustics that handle them.
- A real mourning dove; wing flaps when a flock is startled.
- Walking speed: 4.5 m/s is a jog.
- Mac client; a web version.
- Engine braking, rev-matched downshifts, gear whine.
