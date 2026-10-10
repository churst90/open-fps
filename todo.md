# To do

Planned work. Finished work is in [changes.md](changes.md), `git log` and
[docs/DONE_2026-10.md](docs/DONE_2026-10.md). "(Cody)" marks what waits on Cody's ear or decision.
Updated 2026-10-07: every item checked against the code, the git history, changes.md and Cody's approvals.

## Next, in order

Cody's list of 2026-10-08 (one world, server-built tiles, distant updates, sound from geometry, enemies and
Dinosaur World, railways and editor words, chat names and roles, NPC doors, weather as a system, cars and fighting)
is in [docs/CODY_ASKS_2026-10-08.md](docs/CODY_ASKS_2026-10-08.md), with its suggested order. The 10-07 batch,
the honest tests, the probable bugs and the trains fix are merged and shipped (VPS 3fc38430512b).

1-3. Done: the batch, its test run, and push, VPS and Windows zip (2026-10-07/08).
4. Geometry stage 3, terrain from real elevation (docs/GEOMETRY.md section 7), with world streaming
   stage 2, one world in UTM tiles generated on demand (docs/WORLD_STREAMING.md). T1, T2, W1, W2 merged;
   2026-10-09 (unheard): the world built before you get there, Magnolia and Albany copied into it, 8 m
   ground in the far ring, the places' elevation at 2 m on the UTM grid, a login back to where you left.
   2026-10-10 (unheard, unmerged branch): the ground's materials from ESA WorldCover; roads and woods on
   the world's tiles outside the places. Next on this item: buildings on the world's tiles (needs Cody's
   choice of footprint source: Parquet.Net in the server, or the Python downloader on the VPS; plan in
   docs/WORLD_STREAMING.md "Buildings: the plan"), then the world's roads as RoadData for traffic.
5. Sound library stage 3 alongside: `OpenFPS.Sound` and the first half of `OpenFPS.Acoustics`
   (docs/SOUND_LIBRARY_BOUNDARY.md section 8).
6. World editor phase 3: people, roads and routes, map versions, baking an overlay into a map file
   (docs/WORLD_EDITOR.md section 9).

Before items 4-6: the two cheap reflection steps (measure "Mixer load" with echoes on and off; nice
the trace threads). See "Reflections in their own process". The three bugs of 2026-10-08 are fixed
(2026-10-09, unheard): E weighs a shut door against a car by facing; Alex waits in a lobby clear of
the doorway, which had held the closer off; /weather says "Clearing" while a cleared front's rain
still falls, and that rain now stops. A new front is drawn every five minutes on average (Cody,
2026-10-09; it was about once a minute).
NPC doors (CODY_ASKS section 9) done 2026-10-10, unheard: Alex and the drivers from parked cars let
a closer shut its door, shut an outside door without one behind them, leave an inside door as found
going in and shut it going out, out of the doorway and never on anybody in it (DoorManners,
docs/DOOR_TYPES_EVENTS.md "What the server's people do with a door"). Cody 2026-10-10: the rule
holds whoever is about (no player exceptions), and closers start back 1 s after the doorway clears
(was 3 s).
Cody's world decisions of 2026-10-09: no faster tile fetching for now (a car above about 65 m/s still
stops short of unbuilt ground); the places laid on UTM grid north are fine; placed tiles stay pinned
against the store's cap, and player-built tiles will follow the same rule; close the crack between
coarse and full ground (agent running). The engine CPU savings that change the sound: approved, agent
running, renders to inbox/engine-cpu-2026-10-09.

After items 1-6 (agreed with Cody 2026-10-08): performance and distant updates come before new content,
because the one world costs CPU and bandwidth first.
7. Dropped 2026-10-09: reflections in their own process. The measurement showed the cost is the
   reverb's convolution, not tracing; the convolution was made 38 % cheaper instead (heard, merged).
8. Distant updates (CODY_ASKS item 4): built and merged 2026-10-10. Far moving
   things go at most 5 times a second, with the server's own speed rate and turn, and early the tick the
   client's prediction would stray; the acceptance test passes on a home connection (bearing within 0.010
   degree, pitch within 0.122 %, no step bigger than every tick makes) and saves 48-54 % of the broadcast
   (1.33 to 0.62-0.69 Mbit/s). docs/WORLD_STREAMING.md, "Far things less often". Wire change (Common): new
   Windows zip and VPS update when merged. Cody to listen to the city before it ships (Cody).
9. Floors at 15 dB and sound from geometry (item 5), on top of geometry stage 3. First step done
   2026-10-09 (layers in contact are one panel, unheard: inbox/floors-2026-10-09); next the map's
   double slab (Walls, below), then rooms from geometry, then sound through structure.
10. Chat names and roles (item 8): done 2026-10-09 (protected Owner role, "admin [Mafia] Owner: hi";
    Common changed, so a new Windows zip and VPS build go out together). The editor (item 7): the
    typed-value text box, Control+B and the F12 dialog (tabs Place, Edit, Build, World; nothing
    without permission) are built, untried with Orca and NVDA.
11. Weather as a system (item 12): rain, wet roads, wind and fire tied together.
12. Cars in full detail: the distant-car cycle cache first (also a performance win), then lopey idle,
    suspension, drivetrain, tyres and F1 steepening. 2026-10-09, waiting on Cody's ear
    (inbox/engine-cpu-2026-10-09): each far engine replays its own last cycles while steady (the cache
    per engine); the shared grid per engine type, below, is what is left of it.
13. Enemies, Dinosaur World, melee and NPC inventories (items 6 and 11).

Matter (Cody, 2026-10-10): every material interacts with every other as in real life; wind moves things
physically and its sound comes only from what it moves; a built thing sounds by its material, size and shape.
The design and its order of work are in docs/MATTER.md (material table, struck things by modal synthesis,
bumps, fire by fuel, water over terrain, weather as a system, wind on things, gases, chemistry).

In every play session, clear the "Waiting on Cody's ear" list so heard work merges before it piles up.

Loudspeakers (the PA horn and the megaphone as amplifier, driver and horn; approved by ear 2026-10-10,
inbox/loudspeaker-2026-10-10). The sound is settled; open, without changing it: a column speaker and a
ceiling speaker as presets (a datasheet each; the Baffled mounting is built); the traced echoes are fed
the speaker's on-axis spectrum at its radiated level, not band by band; one-off world sounds
(WorldSound) cannot name a loudspeaker yet; the first play of a recording not yet loaded is started by
the provider after the budget has let it go, and its placement goes stale for the line (seen once on the
old PA in --loudspeaker game).

Waiting on Cody's ear:
- Driving cues and horns: H horn, U siren, J and L indicators, the brake cue, line rumble, the speed
  limit, rails, gates, aircraft roll-out (inbox/driving-2026-10-06). The brake cue's notes, the rail
  strike level and the gate motor and clunk are assumptions.
- Trains' own horn, whistle and bell; air conditioners cycling with the weather
  (inbox/fault-fixes-2026-10-06).
- Downpipes, round 2: the flange should be gone (inbox/water-smoothing-2026-10-06/round2).
- The world editor dialog in the game (F12: tabs, labels, Control+Tab), and `/editorkeys on` with
  Orca, then NVDA.
- The probable-bug fixes, before and after (inbox/probable-bugs-2026-10-07).
- (Approved 2026-10-09: the hull's blows in time.)
- Recorded sounds' echoes smeared off rough walls: your steps and a PA (inbox/probable-bugs-2026-10-09/4-scattering).
- (Approved 2026-10-10: the shut glass door leak fix, inbox/pa-leak-2026-10-09.)
- Floors, heard 2026-10-10 (inbox/floors-2026-10-09): nothing audible at game level above or below; the
  +40 dB copies are fuzzy and the sound cuts out, not a clean transfer. Being worked on.
- Engine CPU (inbox/engine-cpu-2026-10-09), heard 2026-10-10: passby_i4_midsize_50kmh_3m sounded phased,
  did not sweep right to left and did not sound 3 m away (the before file too). Being checked: the lab
  capture is panned, not binaural. Not merged until heard again.

## Now

### Probable bugs
Found by the housekeeping on 2026-10-07. The rest were fixed and merged in 246a4e1c (heard by Cody).
The last four were handled on 2026-10-09, on their own branch (renders in inbox/probable-bugs-2026-10-09):
- Fixed, waiting on Cody's ear: a hull's blow now lands at its own sample in the block. It landed at the
  block's start, 0 to 2.7 ms early (1.2 ms on average).
- Fixed, no sound change in the game: the fire's fizz has its own part (`FireSynth.FizzPart`, lab
  `parts=fizz`). It was muted with the crackles in the lab. In the game every part is 1, so the game's
  fire is the same to the bit.
- `AcousticPathData.ReflectionId` is read (the reflection slots, fixed in 0e0e5ae6). `Scattering` is now
  read too (Cody asked, 2026-10-09): a recording's copy off a rough wall keeps its mirror share clean and
  smears the scattered share through the engines' EchoDiffuser. Your footsteps get the wash they lacked.
  Waiting on Cody's ear (inbox/probable-bugs-2026-10-09/4-scattering).
- Seen, not changed: a recorded loop's copy starts at its source's play position only to the nearest
  mixer block, so its delay off one wall came out 41 ms in one run and 57 ms in another.
- Fixed: a new engine donor is no longer held for 2.5 s inside the budget, so the car at the budget's edge
  is not let go and rebuilt when a donor arrives.
- Seen, not changed: a pocket's ring on a hull starts one block (2.7 ms) after the pocket itself
  (`HullPlate.Ring` is queued after the block's drives were laid down).

### Listen in the game (Cody)
Built and measured, not heard in the game. Restart the server and update the client first.
- The gas hob (docs/GAS_HOB.md, inbox/gas-stove-2026-10-10): heard 2026-10-10, liked; the spark tick is
  a little too present and loud, and the sparks kept going long after the burner lit. Being fixed. Then a
  `gas_hob` placed in a kitchen and lit with the interact key. Not yet on any map.
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
1. Rate by distance (5 Hz beyond 150 m): built, 48-54 % measured (todo item 8, waiting to merge).
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
- Done 2026-10-09 (branch, unheard): layers in contact are one panel (changes.md). The city floor is
  still 10/19/14 dB heavier than a lab-tested 15 cm slab because gen_city.py lays two 25 cm slabs
  between storeys (each storey's floor and its ceiling). One slab of 15-20 cm per storey is a map
  change for Cody to decide; it moves floor heights, stairs and the openings tests.
- Upstairs footsteps need sound through the structure (impact into the slab); airborne, they are
  silent through any real floor, before and after.
- From upstairs, a shout in the flat below comes through about 14 dB louder than the same shout the
  other way round, with the floor measuring the same both ways (inbox/floors-2026-10-09). Not found yet.
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

### Reflections in their own process (Cody, 2026-10-08; discuss Friday 2026-10-09)
Tracing (TracedReverb, TracedEchoes, LateField, Steam Audio's own threads) runs in the client
process. Its `ThreadPriority.BelowNormal` does nothing on Linux, and none of those threads is niced.
Convolution runs on the FMOD mixer thread.
- Measured 2026-10-09 (city street, standing still, /tmp/openfps-client.log 11:36-11:40): the cost is
  convolution, not tracing. Mixer load 70 % with echoes on, 62 % off. Of the mixer's time, the traced
  reverb's convolution is 26.5 % either way and the traced echoes' 10.4 %; binaural 13 %, everything
  else under 3 % each. The governor took voices back 17 times at 60-62 %. The trace threads themselves
  are off the mixer (TracedReverb about a third of a core, LateField 5 %, both now nice 10). So the split
  below would not lower Mixer load; the reverb's convolution is what to make cheaper. Also seen: the 12
  EngineRender threads take about half a core each (6 cores), and the acoustic worker 70 % of one.
- Done 2026-10-09: `BackgroundPriority.LowerThisThread` (+10) at the top of TracedReverb, TracedEchoes
  and LateField's loops. Unchecked: whether Steam Audio's second worker (numThreads 2) inherits it;
  `ps -L -o tid,ni,comm -p PID` while playing shows each thread's nice.
- Recommended next: the split itself, if the first two do not fix it:
  - A helper (`OpenFPS.AcousticsHost`) started by the client; it dies with the client (death signal
    on Linux, job object on Windows) and is niced to +19 or set below normal, Steam Audio's threads
    included.
  - The scene goes to the helper: triangles and materials at load, then doors, tile swaps and cabins.
  - The client sends positions. The helper sends back each response as samples, extracted the way
    `TracedReverb.ExtractLate` does it.
  - Responses travel through double-buffered shared memory, about 50-70 MB/s (1.2 MB per order-1
    response).
  - The client convolves them with its own convolver (`LateTail`), because Steam Audio's responses
    are opaque.
  - If the helper crashes, the game falls back to the room reverb with no echoes, then restarts it.
- What the split solves: tracing cannot take the mixer's CPU, collect garbage in the game's heap,
  hold a lock the audio waits on, or crash the game.
- What it does not solve: convolution cost (Mixer load), total CPU, or the 125-250 ms refresh.

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

### The gas hob, the rest (2026-10-10)
Built, measured against fifteen recordings, not heard (docs/GAS_HOB.md section 10). Open:
- a gas hob in the city's flats and houses (tools/gen_city.py, beside the kitchen sinks);
- changing the heat in the game: the knob's settings are in the state and the model, the interact key
  only lights and turns off;
- a pan on the burner, an oven and a grill, an American range (re-ignition module, no flame safety).

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
