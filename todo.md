# To do

Planned work. Finished work is in [changes.md](changes.md), `git log` and
[docs/DONE_2026-10.md](docs/DONE_2026-10.md). "(Cody)" marks what waits on Cody's ear or decision.
Updated 2026-10-11: every item checked against the commits since the last push (d1013eb6 to 6102931c),
changes.md and Cody's approvals; what was done went to docs/DONE_2026-10.md ("Audit of 2026-10-11").

## Next, in order

The order agreed with Cody on 2026-10-11. Cody's list of 2026-10-08 and where each of its twelve items
stands is in [docs/CODY_ASKS_2026-10-08.md](docs/CODY_ASKS_2026-10-08.md).

Shipping is outside the order: push, the VPS and the Windows zip go out together, once the work in flight
has landed, and only when Cody says so (each time). The VPS and the Windows zip are behind: the last deploy
was 3fc38430512b on 2026-10-08. Since then OpenFPS.Common and the wire changed many times (WorldLoading,
EntityState.SpeedRate and Turn, EditorMenuItem's members, MapSettingsUpdate's play area, UserRole.Owner,
ChatMessage.Team and Title, SoundEmitterComponent.Loudspeaker and Quench, the ground water field, geometry
stage 4's two messages, the rooms and openings moved into OpenFPS.Acoustics, the weather files in
`WorldWeather`), and the wire hash covers the library projects, so a client and a server from either side
refuse each other at login. Restart the server and update the clients first; what goes out is the "Landed
since the last deploy" list below.

1. Regions become places, steps 3 to 5: DONE 2026-10-11 (docs/GEOMETRY.md 12.6, "Regions become places").
   Step 3 (openings framed from where the floods meet them) approved by ear 2026-10-11 with step 2 (Cody:
   "complete, success everything sounds good"). Step 4 (the readers of a room's box use its flood) and step 5
   (nothing of a room authored: no RoomSize, RoomMaterials or ReverbScale in a prefab, no face materials in
   the editor) changed no sound. Before them, all approved by ear: rooms from walls switched on (2026-10-10,
   "everything sounds good"), rooms measured from their floods with the ground graded under the places'
   floors (2026-10-10, "everything sounds great. no complaints"), step 1 (no ambience beds) and step 2 (rooms
   flood from their points). The 21 rooms that seemed to have the ground through their floors were points in
   their walls' cells past the footprint; over the rooms' air there are none. Left on the box: which tiles a
   room is sent with, and the indoor flag, until the flood can decide them at load (12.6, step 4).
2. The special-cases audit's parts (docs/SPECIAL_CASES_AUDIT.md, 2026-10-11: 32 findings, parts P0 to P8),
   in this order:
   - P0, wider replay guards (tests only): DONE 2026-10-11. The replay faced moving vehicles and the
     listener backwards, which it found; fixed the same day (e5e3d0f6), all eight streams regenerated.
   - P1, the pops and the worker's answers: DONE 2026-10-11, unheard. The Main Street pavement's pops were
     the way over the top giving up after four boxes (41-56 a minute to 0). The source pool's faults (S4)
     never fired and are closed anyway; every answer says where it was traced from, and every asker takes
     one by one rule. S5 done: a siren that is off asks for no path.
   - P2, one table of model kinds (`ModelKindTable`): DONE 2026-10-11, no stream changed. Two questions for
     Cody (Decisions, below). Left for later: S26's two caches, S27's model fallbacks, and the server's own
     prefix spellings outside P2's list.
   - P8, laws in one place: DONE 2026-10-11, unheard, except S21 (`VehicleShadow` on the shared
     `Diffraction`), waiting on the barrier model: the FMOD fallback voice's distance law and Doppler, one
     occlusion-floor default, one law and one reference for the speed of sound, the world's speed where a
     heard law wrote 343. Gravity, air density, brakes, rolling resistance and the two winds are physics
     stage 2's.
   - P5, names and materials to properties: DONE 2026-10-11, streams unchanged, unheard (the audit's "P5 as
     built"): one material table, declared glazing, roofs and entrances from geometry and what openings join,
     one door model table, each map's door prewarm from the map (a manifest field: restart the server, new
     Windows zip). Waiting on Cody: whether a bus shelter's canopy is "a roof" to the scope, and which doors
     Alex's haunts take as front entrances. Left: storeys in `MapTracker.Floors` from the building; the glass
     door's sound key reading the prefab's glazing.
   - Under way: the barrier model's way round one thing that passes through another (Acoustics, below),
     which P1 found in Marlow Tower's floor 1 stairwell; the compass words in one function and P2's
     leftovers.
   - Then P3a to P3d, the voice registry: one list of voices, one writer a frame, one way to start a voice.
     It is the one path pass for every voice of a moving thing that Cody asked for on 2026-10-11 ("shouldn't
     all mobile sound sources like vehicles, trains, sirens, etc use the same path so there's
     consistency?"), widened to every voice range. It replaces the horn, siren, distant and spread branches
     in `ClientAudioSystem` and the trains' separate writer (`ClientAudioSystem.Trains.cs`), gated by the
     emitter-stream replay: no change except where a voice was stale. Then library stage 5's `AudioClock`
     slice (`IAudioClock`, its `WorldTime` from `WindField.Now()`; docs/SOUND_LIBRARY_BOUNDARY.md 18.3).
   - Then P4 (one ranking), P6 (one path model for every asker), P7 (machine state from the server: a wire
     change, shipped with a deploy).
3. The physics boundary survey: DONE 2026-10-11 (docs/PHYSICS_BOUNDARY.md). Cody asked for it the same day:
   "all parts of this game engine need to be separated in their own packages and all work cohesively
   together". The boundary of an `OpenFPS.Physics` on Geometry, 24 missing pieces ranked, stages 0 to 6 at
   20 to 30 sessions. Cody's decisions the same day: all ten as recommended; soft bodies as well as rigid
   ones; gravity a property of the world; the engine Jolt Physics through JoltPhysicsSharp. This replaces the
   2026-10-06 placement of vehicle physics in Sound. The rest of the engine follows the same rule: each part
   (geometry, acoustics, sound, physics, the world and its generators, networking, the client UI) its own
   project with stated dependencies and no reach into the host.
4. The physics stages, with geometry stage 6 (vehicles and bodies in the acoustic scene: docs/GEOMETRY.md
   section 7, "Bodies and wheels" stage 2 below). Stage 0 DONE 2026-10-11 (PHYSICS_BOUNDARY.md section 14):
   the guards (a ratchet, 19 movement replays server against client, driving, traffic and rail tracks, the
   ballistics table, `--physics-parity`) and the Jolt trial (a city tile as one mesh in 11 to 19 ms, 500
   bodies at 0.21 ms a step, the soft ball and cloth rest and drape). Found: standing on a parked car the
   server's floor is the roof and the client's the road (a 40 mm known gap); JoltPhysicsSharp 2.22.0's
   `BodyCreationSettings.MotionQuality` setter does not reach the native side. Next: stage 1. Physics
   stage 4 (rigid and soft bodies) goes with geometry stage 6, so each body's shape is made once for
   collision and for the acoustic scene (PHYSICS_BOUNDARY.md section 8). Air moved by passing things, leaf
   litter and impacts that pass on momentum ("Next", below) are physics stages 4 and 5.
5. Sound library stages 6 to 8 on the clean voices: the world input and `OpenFPS.Audio`, splitting
   `ClientAudioSystem`, `WorldAudioPlayer` and `BirdLife`, versions and Resonance.
6. Geometry stage 5, diggable ground with strata: held until it is wanted.
7. Then detail, in this order:
   - weather as a system and wind acting on things (docs/MATTER.md 5.3 and its order of work, steps 5
     and 6; CODY_ASKS item 12), with real weather for the real places (docs/REAL_WEATHER.md);
   - footsteps through the structure ("Walls", below);
   - the struck sounds, from Cody's notes ("Bump sounds", below);
   - cars in full detail ("Cars in full detail", below);
   - the world: lots and addresses, traffic on the world's roads, kerbs and bridges on them
     (docs/WORLD_STREAMING.md, "Left after stage 2");
   - enemies, Dinosaur World, melee and NPC inventories (CODY_ASKS items 6 and 11).

In every play session, clear the "Waiting on Cody's ear" list so heard work merges before it piles up.

### Landed since the last deploy (2026-10-09 to 11, all merged on main)

Details in changes.md and the docs named. "Unheard" means nobody has listened in the game yet. The wire
hash changed, so the server and the Windows zip go out together.
- The one world: terrain from 3DEP (T1, T2), the world store and one world in tiles (W1, W2); built
  ahead behind a loading screen, tiles in order of time to reach; Magnolia and Albany copied in at 2 m on
  the UTM grid; 7.8 m ground in the far ring with the seam closed; a login back to where you left;
  land cover from ESA WorldCover; roads and woods from OpenStreetMap; buildings from Overture with
  driveways. Unheard (docs/WORLD_STREAMING.md).
- Geometry stage 4: the shape library, real footprints and roofs on the places, kerbs, glTF and OBJ
  import, shapes in the quick build. Unheard (docs/GEOMETRY.md 12).
- Rooms from walls (docs/GEOMETRY.md 12.6): the flood at a quarter metre, switched on for server and
  client alike, rooms measured from their floods, server and client agreeing on every room point, homes
  under a hectare, the ground graded under the places' floors: approved by ear 2026-10-10. Regions become
  places, steps 1 to 5 (no ambience beds; rooms flood from their points; openings framed from the floods;
  the readers on the flood; nothing of a room authored): steps 2 and 3 approved by ear 2026-10-11, the
  others changed no sound.
- Sound library stages 3 (OpenFPS.Sound and OpenFPS.Acoustics; Acoustics without MemoryPack, Cody's
  decision 4), 4 (rooms and openings as values in Acoustics) and 5's first slice (traced reverb per
  provider, the weather per world in `WorldWeather`). No sound changed.
- Marlow Tower's leaks (train voices take their current path, rain from its surfaces, sample points kept in a
  source's own air, a fire door on every tower storey's stair): approved by ear 2026-10-11.
- The play fixes of 2026-10-11: E opens the door you face, every room of the real places walkable
  (counters, landings and steps, lawns dug down, side doors), checked /tp spots, `--walk-pair`, the
  tracker's "behind a wall". Unheard.
- Connecting: the Connecting window holds focus, Escape cancels, a server that is not up yet is tried for
  30 s; the in-game reconnect the same way. Unheard.
- The special-cases audit's P0 (replay guards), P1 (the pavement pops, answer provenance, one freshness
  rule; unheard) and P2 (`ModelKindTable`), and the replay harness facing things as the game does.
- State kept by map as well as entity id (11 stores: driven cars' wheels, the traffic's wheels and tyre
  demand, sirens and horns, seat headings, door announcements, parked drivers' doors, junction conflicts,
  respawn reminders, the resend queue): unheard.
- The ambience recordings and the bed stage removed (the Windows zip 90 MB smaller).
- Build warnings cleared; SQLite's native library 3.50.3 (CVE-2025-6965).
- Water over the ground and fire by fuel, stage 1: approved by ear 2026-10-10.
- The loudspeaker model (PA and megaphone): approved by ear 2026-10-10.
- The gas hob, rounds 1 and 2: round 2 approved by ear 2026-10-10. Not on any map.
- The shut glass door leak fix: approved by ear 2026-10-10.
- Floors: layers in contact as one panel, then one 15 cm slab and the end-of-voice clicks fixed. Round 2
  unheard (inbox/floors-2026-10-10).
- The material table and struck things (bumps, Shift+E and /tap on things): heard 2026-10-10, notes kept
  under "Bump sounds".
- NPC doors: the door's mechanism first, closers start back 1 s after the doorway clears, the rule the
  same whoever is about. Unheard in play.
- Distant updates (far things 5 times a second, with the keep-alive repeat fix): passed the acceptance
  test; unheard in play.
- Engine CPU: the render pool 12 % (bit-identical), then the valve solver, far engines at half rate, the
  per-engine cycle cache and mowers (heard; merged). The traced reverb's convolution 38 % cheaper (heard:
  "the reverb seems to work great now").
- The editor: the typed-value box, Control+B, the F12 dialog, Buildings and Vehicles, the placed list,
  phase 3 (changed things, versions and baking, roads, paths and railways, people), shapes. Orca works
  (Cody, 2026-10-10); NVDA untried.
- Server: the protected Owner role and names in chat, bans, `/setmapsize`; E picks what you face; Alex
  waits clear of the doorway; `/weather` says "Clearing" and a cleared front's rain stops; a front every
  five minutes on average; the trace threads niced; a gradient backdrop behind the menus.
- Designs, no code: the real weather (docs/REAL_WEATHER.md), the physics boundary (docs/PHYSICS_BOUNDARY.md),
  the special-cases audit (docs/SPECIAL_CASES_AUDIT.md).

### Decided (Cody, 2026-10-09 to 11)

- Reflections in their own process: dropped. The cost was the reverb's convolution, not tracing; the
  convolution was made 38 % cheaper instead.
- The world: no faster tile fetching for now (a car above about 65 m/s still stops short of unbuilt
  ground); the places laid on UTM grid north are fine; placed tiles stay pinned against the store's cap,
  and player-built tiles will follow the same rule; the store's cap starts at 20 GB.
- Weather: a new front every five minutes on average (it was about one).
- NPC doors: the rule holds whoever is about (no player exceptions); closers start back 1 s after the
  doorway clears (was 3 s).
- Sound library decision 4: `AcousticMap` and the octree lose `[MemoryPackable]` and Acoustics its
  MemoryPack package; `TransientSound` keeps it in Sound.
- Ambience: no beds at all, region beds included (2026-10-10); the recordings removed too (2026-10-11:
  "remove those ambiences completely we simply don't need them").
- Physics as its own project, `OpenFPS.Physics` on Geometry, and every part of the engine its own package
  (2026-10-11). The survey's ten recommendations approved; soft bodies wanted as well as rigid ones;
  gravity a property of the world; the engine Jolt Physics through JoltPhysicsSharp.
- Real weather: the world's clock is the real local time at real places (2026-10-11).
- Tests: two test runs at once on this machine, both on the last twelve cores (2026-10-11).
- The order of work above (2026-10-11).

### Matter
Cody, 2026-10-10: every material interacts with every other as in real life; wind moves things
physically and its sound comes only from what it moves; a built thing sounds by its material, size and
shape. The design and its order of work are in docs/MATTER.md. Steps 1 (the material table), 2 (struck
things) and 4 (water over the ground) are built, and fire by fuel (step 3) has its stage 1; next are
weather as a system and wind on things (item 7 above), then gases and chemistry.

### Loudspeakers
The PA horn and the megaphone as amplifier, driver and horn: approved by ear 2026-10-10
(inbox/loudspeaker-2026-10-10). The sound is settled; open, without changing it: a column speaker and a
ceiling speaker as presets (a datasheet each; the Baffled mounting is built); the traced echoes are fed
the speaker's on-axis spectrum at its radiated level, not band by band; one-off world sounds
(WorldSound) cannot name a loudspeaker yet; the first play of a recording not yet loaded is started by
the provider after the budget has let it go, and its placement goes stale for the line (seen once on the
old PA in --loudspeaker game).

## Waiting on Cody's ear

Approved 2026-10-09 to 11, so off this list: the hull's blows in time; the cheaper reverb tail; the
shut glass door leak fix (inbox/pa-leak-2026-10-09); the loudspeaker (inbox/loudspeaker-2026-10-10); the
gas hob round 2 (inbox/gas-stove-2026-10-10/round2, "sounds great now"); the four probable-bug
fixes of 2026-10-09 with recorded sounds' echoes smeared off rough walls (inbox/probable-bugs-2026-10-09,
"the probable bugs sound good"); water over the ground
(inbox/water-over-terrain-2026-10-10); fire by fuel, stage 1 (inbox/fire-fuel-2026-10-10); rooms from
walls switched on (2026-10-10, "everything sounds good"); rooms measured from their floods with the ground
graded under the places' floors (2026-10-10, "everything sounds great. no complaints"); regions become
places, steps 2 and 3 (2026-10-11, "complete, success everything sounds good"); Marlow Tower's leak fixes
(2026-10-11).
Heard, with notes and nothing to listen to again: the engine CPU renders (inbox/engine-cpu-2026-10-09;
the phased pass-by that did not sweep was the lab's panned capture, not the game); struck things and
bumps (inbox/struck-things-2026-10-10; notes under "Bump sounds", low priority).

Renders not yet heard:
- Floors, round 2 (inbox/floors-2026-10-10; merged 8406509a): one 15 cm slab, a shout overhead 12.5 dB
  louder and clean, footsteps without the end-of-voice clicks. Round 1 (inbox/floors-2026-10-09) was
  heard 10-10: silent at game level, the +40 dB copies fuzzy (16-bit capture) and cutting out (the
  clicks).
- The probable-bug fixes of 2026-10-07, before and after (inbox/probable-bugs-2026-10-07).
- Driving cues and horns: H horn, U siren, J and L indicators, the brake cue, line rumble, the speed
  limit, rails, gates, aircraft roll-out (inbox/driving-2026-10-06). The brake cue's notes, the rail
  strike level and the gate motor and clunk are assumptions.
- Trains' own horn, whistle and bell; air conditioners cycling with the weather
  (inbox/fault-fixes-2026-10-06).
- Downpipes, round 2: the flange should be gone (inbox/water-smoothing-2026-10-06/round2).

With a screen reader: the editor (the F12 dialog, Control+B and its Shape kind, the typed-value box,
phase 3's controls) works with Orca (Cody, 2026-10-10); NVDA on the Windows client is untried. Direct
keys (`/editorkeys on`) stay off until tried with Orca, then NVDA (docs/WORLD_EDITOR.md 12).

## Now

### Seen, not changed (from the probable bugs of 2026-10-07)
The rest were fixed and merged (246a4e1c, 7a9b6c85).
- A recorded loop's copy starts at its source's play position only to the nearest mixer block, so its
  delay off one wall came out 41 ms in one run and 57 ms in another.
- A pocket's ring on a hull starts one block (2.7 ms) after the pocket itself (`HullPlate.Ring` is
  queued after the block's drives were laid down).

### Listen in the game (Cody)
Built and measured, not heard in the game. Restart the server and update the client first. The /tp spots
here are checked to stand on (docs/LISTENING_SPOTS.md).
- The play fixes of 2026-10-11. E between two doors: in Marlow Tower's floor 3 corridor (`/tp -19.95
  -101.5 9.29`) the flats' doors face each other; E opens the one you face. Walking into every room on
  Magnolia and Albany: the back door on Bobcat Lane at Magnolia's spawn (`/join magnolia tx`), clear of the
  counter, and steps at the outside doors; 19617 Turtle Dove Lane (`/tp -301.4 -268.7 5.2`). The tracker:
  from the barn at 19415 Turtle Dove Lane (`/tp -88.8 -253.0 3.9`), comma or period to the house's doors
  says "behind a wall".
- Connecting: start the client before the server. The Connecting window takes focus, says "Escape to
  cancel", and keeps trying for 30 seconds ("Still trying, N seconds."); Escape says "Cancelled." and puts
  you back on the menu item. Drop the connection in game: "Reconnecting. Escape to cancel."
- The pops (audit P1): on the Main Street pavement before Marlow Tower's entrance (`/tp -6.4 -103.5
  0.12`) with traffic, no pops from the cars on Wharf Avenue over the car park and the tower. Marlow
  Tower's floor 1 stairwell (`/tp -10.8 -104.2 3.28`) still has 4 to 11 a minute (the barrier model, under
  way).
- State by map: with someone on another map at the same time (the speedway while you drive on the city),
  nothing of theirs turns up on yours: no siren on a car nobody switched on, no tyres or skids on the wrong
  thing, the seat's heading steady while you ride.
- The world (`/join world magnolia`, `/join world address 401 Market St, Tomball, TX`): the loading
  screen ("Building the world: 12 of 41 tiles."), the ground's land cover, the roads, woods, buildings
  and driveways outside the places, no crack where far ground meets near ground, and logging out and
  back in to the same spot.
- Shapes: walk round a house on Magnolia (its real outline) and onto its roof; kerbs on Albany;
  `/edit build stairs 14 steps up north`, a roof over a floor, Control+B's Shape kind.
- NPC doors: Alex and the drivers from parked cars shutting doors behind them; closers starting back a
  second after the doorway clears.
- Far things sent less often: the city's far traffic, trains and the airliner (no audible difference by
  the acceptance test).
- Far engines at half rate and their replayed cycles on a busy street (`/enginedetail off` to compare).
- Fire by fuel in the game (`/spawn fire PRESET` by trees, `/spawn fire lightning`, `/spawn fire
  water`), water over the ground on Magnolia in rain, the PA and the megaphone in the city: approved as
  renders, not yet heard in play.
- The gas hob: a `gas_hob` placed in a kitchen and lit with the interact key (approved as renders; not on
  any map).
- The editor: Place's Buildings and Vehicles, Placed on this map, Changed on this map, map versions,
  laying a road or railway by walking it, people and walkers.
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
The open questions for Cody, in one place. A few that belong to one item stay with it, marked (Cody).
- Real weather (docs/REAL_WEATHER.md section 12), six decisions open, each with a recommendation:
  Open-Meteo's free tier for a game with no ads; the User-Agent and fetching on demand; made-up maps keep
  simulated weather (a map may name a real place to follow); real lightning (GLM) later or not; the NWS API
  not at first; when the data stops, the forecast for six hours, then the simulation. (The clock was
  decided 2026-10-11: real local time at real places.)
- The scope and the push mower (audit P2): should the scope call somebody pushing a push mower a person?
  Today, as before, it picks out no machine.
- The editor and sound ids (audit P2): should the editor edit trains and woods through their sound ids?
- Compass words: seven functions turn a bearing into compass words, each its own way. Recommended: all
  of them call one 16-point function, which lands in OpenFPS.Astronomy's `SkyWords` with the sun and moon
  readout (docs/SKY.md).
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
  regeneration. Recommended: yes to all three. (Decision 4, MemoryPack, was taken 2026-10-10.)
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
- Stale code comment: RainField.cs 33 lists gutters, downpipes and run-off as missing (built 2026-10-06).
- `OpenFPS.Sound/Machines.cs` 87: the TODO on a part's `ExtentMetres` still names ClientAudioSystem's
  car-sized `MathF.Max(reference, 3f)`, which is gone (engines are placed by their outlet separation
  through `Loudness.Place`). Check whether anything reads a part's extent, then wire it or drop the TODO.

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
- The terminal's road-side steel door, automatic if it is the public way in (Cody). The towers' stairwells
  have their push-bar fire doors since 2026-10-11 (one from every storey's corridor).

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
1. Rate by distance (5 Hz beyond 150 m): merged 2026-10-10, 48-54 % measured (docs/WORLD_STREAMING.md,
   "Far things less often").
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
- Floors: one 15 cm slab between storeys in the city's towers (37.9/54/80 dB against a lab slab's
  40/54/83) is merged (8406509a, unheard). Other generators (houses, the terminal, the garage) were not
  checked for doubled slabs.
- Footsteps through the structure (Next, in order, item 7). A neighbour's steps are mostly impact into
  the slab; the game has only the airborne part, about 10 dB short on a carpeted floor and the wrong
  shape (a click, not a thud). Plan: the slab's normalised impact level from its own airborne loss by Ver's relation,
  Ln(f) = 38 + 30 lg f - R(f) (homogeneous slab; checks against a bare 152 mm slab's Ln,w 79, IIC 27);
  a resilient covering's improvement 40 lg(f/f0) above f0 = (1/2 pi) sqrt(s/m) from its dynamic
  stiffness (a material property: carpet on underlay 20-35 dB, IIC 26 bare to 60-67 carpeted,
  McGunnigle 2002); a footfall's force against the tapping machine per footwear (heels near it above
  100 Hz, socks and bare feet more below 100 Hz: Kylliainen et al. 2015, Acta Acustica 101(5)); the
  result radiated from the ceiling under the foot into the room below as a second voice per step.
  Needs footwear in the game (the bank has Carpet/barefoot; nothing chooses it).
- No cavity resonances or air leaks: sealed glazing is about 10 dB optimistic in the mids; door gaps
  and seals are not modelled.
- Shut glass front doors let more in than steel doors: 17/28/43 dB against 13/48/58 (`--wall-tl`).
- Steam Audio counts walls in a row as (2n+1)/3 of one; the tracer counts them exactly.
- Diffraction is evaluated at 200/1250/8000 Hz, not the band averages transmission uses.

### Acoustics
- The room answering at once (early tail): clap in a main-street lobby and in flat 01F. Is 20-100 ms
  too strong? Speech and steady sounds get no copies; a per-source early part would fix it (Cody).
- Pops on the Main Street pavement and in Selby House and Marlow Tower; is the city too busy with cars
  heard over the roofs (Cody)? The pavement's are gone (2026-10-11: the way over the car park and the tower
  gave up after four boxes; 41-56 a minute to 0). Left: Marlow Tower's floor 1 stairwell, 4 to 11 a minute
  (`--pop-hunt ear=-11.05,4.9,-100.84`, `/tp -10.8 -104.2 3.28`), a car on Dock Street from -57/-83/-100 to
  -35/-52/-80 dB for 4 m of road, x -19.5 to -15.5. Still sources do it: the line crosses the slab's edge all
  along and, either side of that stretch, the stair's 1.07 m concrete guard. The way over the guard is 3 cm,
  but its leg runs on through the brick front, so it is not believed and the guard is charged as a whole
  wall. The barrier model needs a way round one thing that passes through another, paid as the diffraction
  times what the leg's walls let through per band, competing with the straight line as now. That changes
  what is heard behind walls everywhere (louder where a near low wall stands in front of a heavier one), so
  a part of its own, measured on the three maps (docs/GEOMETRY.md 12.6, "Marlow Tower's leaks"); under way
  since 2026-10-11. Older finds of `--pop-hunt`: a car's straight line through one wall as it passes a gap (-80 to -62 dB for 200-400 ms),
  and up to 19 dB between over the wall and over the roof under the covered hall west of Main Street.
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
- The trace threads (TracedReverb, TracedEchoes, LateField) run at nice +10 since 2026-10-09. Unchecked:
  whether Steam Audio's second worker (numThreads 2) inherits it; `ps -L -o tid,ni,comm -p PID` while
  playing shows each thread's nice.

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
- Water over the ground: approved by ear 2026-10-10 (inbox/water-over-terrain-2026-10-10, "water over
  terrain sounds good"); keep how it sounds. Next (docs/RUNNING_WATER.md 13.9): the shallow-water model
  near players; poured water heard on the lines; soil groups by place (SSURGO) instead of B everywhere; culverts as falls; the world's
  50 m drainage margin agrees with the whole on only 57 % of Magnolia's big-line cells (250 m: 88 %), so a
  wider window or Barnes's tiled Priority-Flood next; base flow from a climate.
- Magnolia keeps 2,078 ponds (262,000 m³) of 10,566 hollows: check against the aerial photos that the
  big ones are real (stock ponds, flatwood hollows) and not grading under roads and drives.
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
  a bus or a crowd blocks sound like a wall of its size and material (geometry stage 6 with physics
  stage 4; Next, in order, item 4).
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
- Today, indoors, the zone said is the room whose flood holds you (since 2026-10-10: a room is a name and a
  point, its air flooded from its walls); outdoors a zone is a named box in the map, and the smallest box
  you stand in is the one said.
- Proposal: a building names its own zone from its rooms; a free-standing zone (park, plaza, car park)
  stays a box; a zone inside a zone is said as the inner one, the outer on the where-am-I key.

### Bump sounds
Built and merged 2026-10-10 (fb9868f9; docs/MATTER.md 7.5): a bump is a body (palm, boot toe, shoulder) striking what
you walked into by modal synthesis, with the near-field radiation below coincidence, the palm and knuckle
through their pad to the bone, and the level anchored to the footstep takes (-15.8 dB). Renders in
inbox/struck-things-2026-10-10. Still open: the plaster stud wall bump is 10-15 dB short at 125 Hz-1 kHz
against a recorded body slam into plaster; the car's rattle is busier than recorded ones; bumping a
person is still the old impact.
Heard by Cody 2026-10-10 (low priority, after geometry and the library reorganisation; Next, in order,
item 7): the aluminium bar sounds like a bright bell; running into the car sounds crunchy and staticky;
the fence and the glass door sound the same tonally, and the fence does not sound like a fence; the
aluminium sheet is far too sustained and tonal. Kept in the game meanwhile.

## Next

### Air moved by things, leaves, impacts (asked 2026-10-11; physics stages 4 and 5)
- A passing bus moves no air today. Wind is the weather's field (`WindField`) and the ear wind reads it; a
  vehicle only blocks sound (`VehicleShadow`). Wanted: a moving body pushes air (a pressure pulse ahead, a
  turbulent wake behind that decays with distance and time) into the same local air the ear wind, foliage
  and loose things read, so a bus at close range is felt at the ears and stirs what lies about.
- Leaves on the ground: leaf litter exists only as a ground material (`LandCover` forest floor, footsteps in
  leaves). Proposed: a litter layer as data per ground cell (cover and depth, like the puddles and run-off),
  lifted and skittered when the local air passes a threshold speed, heard as texture events at that rate,
  read by footsteps; not one object per leaf.
- Impacts: a vehicle that hits something stops and the impact is heard (`ImpactAcoustics`), but no momentum
  passes on (`DrivingSystem.cs:388`, "TODO: the impulse and the damage"). Wanted: bodies with mass and
  velocity, momentum exchanged on contact with restitution, a struck person or object thrown and landing
  (`BodyFall` for the sound), damage from the closing speed.
- Physics as one project: surveyed and decided 2026-10-11 (docs/PHYSICS_BOUNDARY.md; Next, in order, items
  3 and 4). The bodies and contacts are physics stage 4 (Jolt through JoltPhysicsSharp), the moving air and
  loose layers (leaf litter) stage 5.

### Doors as live models (filed 2026-10-05, not started)
The door models are physical but rendered once per key, slower than real time, and replayed from the
handle; the leaf does not swing in the world. Cody wants the squeak in the hinge and the leaf's sound
from the swinging leaf. Needs: the leaf swinging on the server from how it was pushed; the model faster
than real time (find what is slow first: probably the leaf's modes and the contacts); taps at each
hinge, the latch edge and the leaf. Knob door first, the approved renders as the yardstick. Large.

### Real places from open data
tools/gen_osm.py; docs/MAPS_FROM_REAL_PLACES.md. Magnolia and Albany are built, stream by tile and are
copied into the one world. The world's own tiles are made by the server on demand from 3DEP (ground),
ESA WorldCover (land cover), OpenStreetMap (roads and woods) and Overture (buildings and driveways);
docs/WORLD_STREAMING.md, stage 2 as built.
- Addresses (Overture's addresses theme) and lots on the world's tiles, then the world's roads as RoadData
  for traffic (Next, in order, item 7). Overture release 2026-09-23.1 will be withdrawn around the end of
  November 2026: moving to a newer one is a new generator version.
- Real weather for real places: designed (docs/REAL_WEATHER.md, 2026-10-10): Open-Meteo for the conditions,
  METAR to correct them near the ground; the clock decided 2026-10-11 (real local time); six decisions open
  (Decisions, above). With weather as a system (Next, in order, item 7).
- The sun and the moon for the player (Cody, 2026-10-11; docs/SKY.md): the calculations and the sentence
  are built (`OpenFPS.Astronomy`). The readout key and `/sky` come with the weather's clock, in
  docs/REAL_WEATHER.md stage 1: they need the world's real local time and each place's zone.
- Traffic volumes from published counts (average daily traffic by road), with time-of-day curves.

### Map editor
Phases 1 to 3 are built (docs/WORLD_EDITOR.md). Left from phase 3 (section 18, "What the existing road
and rail data cannot carry"): joining an editor road to the others with junctions and putting traffic on
it; a railway with two ends; crossings' bells live, and the bed cut where a road crosses; underground
stations with platforms and stairs, and a tunnel as a named place; more than one kind of character life.
Also missing: copy, rename, delete and publish maps; checks ("this room has no door"); groups as one
composite (only if building houses needs it); a preview of a wall being struck. A slip in the build
phrases: in `/edit build column 0.3 by 3 ahead 2` the 2 is read as a size and the phrase refused
(`WorldEditor.TryShapePhrase` adds "ahead" and counts the number with the sizes); "distance 2" works.

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
Fire at any size is built and approved (docs/FIRE.md). Fire that burns what is there, stage 1, is built
(docs/FIRE.md section 12, 2026-10-10; approved by ear 2026-10-10, inbox/fire-fuel-2026-10-10): a fire over a
shape, fuel as a property of things, catching by heat and brands, lightning, rain and water. Stage 2, in order:
- aggregation by audibility, so a forest burning is tens of emitters (FIRE.md 12.8);
- surface fire over the ground: fronts of marker points at Rothermel's rate where land cover and zones say
  there is fuel (12.5);
- structures: a house as a zone with its fuel load, rooms, windows and roof as parts; fences and floors;
- the fire's own wind round a big fire, heard (12.6);
- the roar's places following the flame height as a fire grows; slope on terrain;
- litter and grass fitted to the prescribed burns among the recordings;
- water as a substance (hoses, buckets, water over the ground) through FireSpread.AddWater; a grease fire.
  Water over the ground is built (`GroundWaterSystem.WaterReaching`), but FireSystem does not ask it yet.
Also open:
- the crown fire against a real crown-fire recording (none found);
- the car against more than two recordings;
- smoke explosions in a closed building.

### The gas hob, the rest (2026-10-10)
Built, measured against fifteen recordings; round 2 approved by ear 2026-10-10 (docs/GAS_HOB.md
sections 10 and 11). Open:
- a gas hob in the city's flats and houses (tools/gen_city.py, beside the kitchen sinks);
- changing the heat in the game: the knob's settings are in the state and the model, the interact key
  only lights and turns off;
- a pan on the burner (it shields the cap and the sparks), an oven and a grill; a North American range's
  own mains, pressure and injectors (its switching and no flame safety are in: `hob4`, `hob4_reignite`);
- if the tick is still too present: the hob top's image is idealised (fully coherent), and its 2 kHz octave
  is 6.6 dB over the recordings' median (docs/GAS_HOB.md section 4).

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

### Cars in full detail (Cody, 2026-10-08)
Cody: "I want cars to be very detailed and we need to model the parts that matter." From the AK Engine
Synth video (inbox, transcribed): ours is the more physical model; these are what to take and what to add.
- Pre-computed cycles for distant cars (agreed): our own engine model renders each engine type's cycles over
  a grid of rpm and load, denser at low rpm where the sound changes fastest, several cycles per point so the
  variation survives. Made at first launch and kept on disk per build, like the door renders, and shipped in
  the Windows zip. Near and driven cars stay live; distant and borrowed cars play the stored cycles.
  Done per engine (2026-10-09, EngineSynth.Detail.cs, CycleCache): a far engine running steadily replays its
  own last six cycles, and runs live at half rate otherwise; a cruising far car replays about 60 % of the
  time. Left: the grid per type, so a far car accelerating, braking or hunting at idle replays too (the
  live half-rate engine covers those now), with the crank's speed from the grid's mean torque.
- Cycle-to-cycle combustion variation: peak pressure, timing and burn rate vary from cycle to cycle and
  cylinder to cylinder, more at idle, light load and with big cam overlap; occasional misfires. The lopey,
  choppy idle of a cammed V8 comes from this.
- Suspension: springs, dampers, bump stops and bushes per wheel from the per-wheel physics; strut knock,
  top-mount clunk, bottoming out, creaks, and body panels and loose trim rattling, each driven by the real
  impulse at that wheel (as the AK video does for collisions and bumps, but modelled, not sampled).
- Drivetrain: gear whine at the mesh frequency (teeth times shaft speed), helical against straight-cut,
  differential whine, clutch engagement, driveline clunk on load reversal, a dog-box's clunk, the synchro.
- Tyres: tread-block noise from the pattern, the tyre cavity's air resonance (about 200-250 Hz), stones
  picked up and thrown, the surface under each wheel (the rail grooves' clack is approved by ear 2026-10-08).
- Check our pipes against published muffler and exhaust measurements.
- F1 above about 12,300 rpm: model the pulses steepening at high pressure (a small 1D gas-flow solver in the
  primaries), the fault the convolution approach cannot fix either.

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
Geometry stages 1 to 4 are built and merged: the triangle world, wedges, stairs and arches (stage 2),
terrain (stage 3), and stage 4 (eb529bfe, 2026-10-10, unheard; docs/GEOMETRY.md section 12): the shape
library (round, outlined, roofed, swept), real footprints and roofs on Magnolia and Albany, round trunks,
kerbs, glTF and OBJ import with mesh assets on the wire, shapes in the quick build. Rooms derived from
seeds by flood were switched on 2026-10-10 and approved by ear, and regions became places in five steps by
2026-10-11 (12.6; Next, in order, item 1). Left of stage 4 (GEOMETRY.md 12.6):
- Sound points on models (MATTER.md 8): where a model's sound parts are, read by machine parts and emitters.
  Begun: struck sounds read the shape a part has (a pitched roof struck as its deck, a flight as one step).
- Facets as polygons for the engine echoes; per-surface materials in the image sources.
- Doors' remaining misses on Albany (16; GEOMETRY.md 12.6 item 4), not rechecked since the floods' openings
  (step 3), which ended the openings found in flooded rooms' faces (item 5).
Then stage 6, vehicles and bodies in the acoustic scene, with the physics stages (Next, in order, item 4);
stage 5, diggable ground
with strata, is held until wanted; forests, ocean maps and boats.

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
sources placed by the generator; an inner lobby door in the towers. Asked 2026-10-05: gas stove
burners (built 2026-10-10 as the gas hob, not yet on a map), cookware (sizzle, boiling, a lid rattling),
HVAC with ducts and vents, and electricity with a power grid (substations and lines as data; an outage
silences fridges, fans and lights). Taps, sinks and showers are built.

### A map's own sounds
A map or server carries a sound pack: named cues (login, chat, menus, beacons), each a short
recording or a synthesiser recipe, cached by the client, normalised on import, and switchable off by
the player. Anything missing uses the built-in sound.

### The new city
Plan: [docs/NEXT_CITY_10KM.md](docs/NEXT_CITY_10KM.md): a 10 x 10 km city, shapes other than boxes,
and building a map in the game. Streaming stage 2, real terrain and the shape library it waited on are
built.

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
- Bans by address (account bans are built: `/ban`, `/unban`, `/bans`, 2026-10-09).
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
- Check the SQLite package for security updates again before each release (3.50.3 since 2026-10-11, for
  CVE-2025-6965).

## Ideas

- Curved kerbs along a whole road (a swept profile per run, not per 20 m piece).
- A real mourning dove; wing flaps when a flock is startled.
- Walking speed: 4.5 m/s is a jog.
- Mac client; a web version.
- Engine braking, rev-matched downshifts, gear whine.
