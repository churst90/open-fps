# To do

Planned work. Finished work is in [changes.md](changes.md), `git log` and
[docs/DONE_2026-10.md](docs/DONE_2026-10.md). "(Cody)" marks what waits on Cody's ear or decision.
Updated 2026-10-17: every item checked against the 130 commits since the last push (d0116bb5 to e262d69f),
changes.md and Cody's approvals of 2026-10-16 and 17; what was done went to docs/DONE_2026-10.md ("Audit of
2026-10-17").

## Next, in order

The order as of 2026-10-17. Cody's list of 2026-10-08 and where each of its twelve items stands is in
[docs/CODY_ASKS_2026-10-08.md](docs/CODY_ASKS_2026-10-08.md).

Where the plans stand (2026-10-17):
- Sound library (docs/SOUND_LIBRARY_BOUNDARY.md): stages 0 to 7 done, and the namespace rename (decision 6,
  section 21), the engine packages under the library included (21.1), and stage 8: the first version tagged,
  `library/v0.1.0`, with docs/LIBRARY_CHANGES.md (2026-10-17). Resonance takes it as a submodule pinned to the tag.
  Then `library/v0.2.0` (2026-10-24): todo 6a, the host's needs.
- Engine packages (docs/ENGINE_PACKAGES.md): stages 0 to 5, 7 and 8 done. Left: 6, 9 and 10.
- Physics (docs/PHYSICS_BOUNDARY.md): stages 0 to 3 done, with stage 2's leftovers (16.6). Left: 4 to 6.
- Geometry (docs/GEOMETRY.md): stages 1 to 4 done, and stage 6's first half (each vehicle's body as one closed
  mesh). Left: stage 6's second half, and stage 5 (held).
- A source heard from the whole of itself (docs/RUNNING_WATER.md 15): steps 0 to 4 done. Left: 5 to 8.
- The special-cases audit (docs/SPECIAL_CASES_AUDIT.md): P0 to P8 built 2026-10-11 and 12, unheard. What it
  left is under "Left from the special-cases audit", below.

Shipping is outside the order. Push, the VPS and the Windows zip go out together, and only when Cody says so,
each time. Main was pushed 2026-10-24 (8f3c038d, tag `library/v0.2.0`; CI green on all 8 shards). The last deploy
was 3fc38430512b on 2026-10-08. The wire hash has changed many times since (053dd7700918 now), so a client and a
server from either side refuse each other at login: restart the server and update the clients first. The door
fingerprint changed too (3a8a8e7b91ab), so the door renders are made again once.

1. A voice swap leaves an engine carrying its tap's share (Resonance's 3.2): DONE 2026-10-17 (changes.md). The
   engine counts its taps and each takes and gives back its share once; a tap revived mid-fade takes it again; a
   voice reading another's synth (front outlet, cabin path, echo, borrowed voice, a fire's or wood's outer place)
   follows the live voice of its id. AudioLab `--resonance-maybes outlet`: no restart delay undone (up to 200 ms
   before), the engine's own voice -40.7 dBFS after the swap against -40.5 for the control (-36.2 before), no
   orphan. Unheard: see "Listen in the game".
2. A shut door's gaps and undercut (Resonance's 1.1): DONE 2026-10-17, unheard. Each shut door's gaps are added
   to its doorway as Gomperts' slit with a narrow gap's friction (`DoorGaps`, `OpeningRoutes.GapsRound`): 3 mm
   round and a 10 mm undercut between rooms, 0.3 mm to the outdoors, or the door's own (`DoorGapMetres`,
   `DoorUndercutMetres`). Selby House's flat doors pass -14/-16/-16 dB (were -18/-27/-55), its stair doors
   -12/-17/-15 (were -13/-48/-59). docs/CLIENT_NOTES.md "A shut door leaks by its gaps". Listen: "Listen in the
   game" below. A door to a place in the open air that has surfaces (a roof) is weatherstripped too since
   2026-10-17 (`OpeningRoutes.IsOpenAir`): the city's five roof access doors, -13/-41/-37 shut (were -12/-17/-15).
3. One-off sounds that move while they play have their echoes found and moved (Resonance's 2.6): DONE 2026-10-17,
   unheard (changes.md). A one-off sent moving (`MovesTo`: the sliding and lift doors' runs) and a part of the
   thunder have their copies found again as they go (`OneOffSounds.FollowCopies`) and glided there; a copy no longer
   answered fades. AudioLab `--moving-echoes`: the patio door's west wall copy turns with the run and fades behind
   the jamb, a ring carried past Selby House's south wall takes its copy 46 degrees with it; no clicks; 0.5-2.7 ms a
   search, none for a still sound. A swinging door is heard from its shut leaf and does not move, so its copies
   stay; whether a swing should be heard moving is Cody's to say (Cody).
   Listen: "Listen in the game" below. The "click at the ring's end" the lab found, with and without the change,
   was its own measure against digital silence, not a sound (changes.md, 2026-10-17); the measure is fixed.
4. A doorway route leaves from the wall's face (Resonance's 1.2): DONE 2026-10-17 (changes.md). A door's opening
   is measured from its jambs' faces, in the middle of the wall and as deep as it, wherever the leaf hangs; a leg
   leaves it from the face on its own room's side (`Opening.ASide`), straight out. The leaf at a face of a 0.3 m wall,
   open, round the corner: -37.4/-40.1/-42.0 dB, as at the middle (was -44.0/-45.3/-58.7). A sound beside a small
   room heard in it through its open door: -10.6/-16.9/-25.1 (was -29.0/-37.6/-65.5, through its wall). No map
   hangs a leaf at a face (gen_city, gen_osm and the editor all hang it mid-wall, measured), so nothing on the maps
   changes; the stored streams pass unregenerated. `OpeningRoutes.FieldAt` took the first-named place to lie along
   the normal and most doors are named the other way: fixed with `ASide` the same day, unheard (changes.md).
5. Sound library stage 8: DONE 2026-10-17. Tag `library/v0.1.0`, docs/LIBRARY_CHANGES.md. Each later library change
   that a host must act on gets a line there and a new tag. Resonance's side (the submodule, NOTICE.md, swapping
   its copies one subsystem at a time) is Resonance's to do.
6. Sound that starts clean (docs/AUDIO_WARM_START.md; asked 2026-10-18). The city crackles and breaks up for its
   first seconds: 33 engines, 25 air conditioners and a train start in the same two seconds, each for the first
   time, the mixer reaches 104 %, placement stalls 1.3 s (a second of it unnamed) and the game loop 1.4 s. The fix
   is at the cause, not a fade: an instrument (`--city-load`) that names every millisecond; the Windows client
   compiled ahead and a warm-up of every voice kind at login; nothing heavy where sounds are placed; the mix
   opened only when every voice in earshot is warm (loads, `/join`, long `/tp`, tiles); a start budget by
   measured cost during play; no garbage collection in the first seconds; a guard test. Six to nine sessions on
   two tracks (instrument, attribution, settled start; compiled ahead, collection, start budget). Client only,
   no wire change, no sound changed.
   - Parts 1, 2 and 3 DONE 2026-10-20 (measured in the doc): the login's worst loop iteration 1.1 s to 60 ms,
     its worst placement pass 0.6 s to 25 ms. Load 2's 0.5 s hold was the load counted as a stall, fixed (44 ms).
     Part 4, the settled start, HELD 2026-10-20 (Cody): a second line of defence now that the login is clean by
     ear; it would lengthen every load. Parts 5 and 6 only if a measurement names them.
   - A candidate for part 3, from Resonance's list of 2026-10-18: a vehicle's sound is made on whatever thread
     asks for it. In Resonance's copy one vehicle takes 32 to 92 ms, the first of each preset the longest, and
     its game ticks spent up to 210 ms on it arriving in town. `VoiceBuilds.Makes` builds sirens, bells, horns,
     machines and aircraft ahead, not engines. Check it in the `--city-load` numbers and move it first if they
     name it.
6a. Library 0.2 for hosts (inbox/2026-10-18-resonance-host-needs; agreed 2026-10-19). What a host other than
   open-fps's own game needs of the library, and three faults that may be open-fps's too. Each change a host
   must act on gets a line in docs/LIBRARY_CHANGES.md, and the lot a new tag. In this order:
   - Faults first. A sound behind a barrier is heard round one edge only: `Diffraction` takes the shortest way
     round a box and `Propagation.Combine` hears that edge alone, so over the middle the direction flips and
     the turn limit makes the flip a slide. Hear both ways round, each by its own strength; guard: a source
     under the middle of a 3 m deck, the listener walking across 1 m above, the direction moving smoothly and
     wide over the middle. Needs Cody's ear.
     Done 2026-10-23 (e96ea3e5): both sides summed by energy, 5 degrees per 10 cm where it was 111. No spot on
     the city exercises it (a scan of every steady source: over the top always wins). Cody 2026-10-27: no place
     and no render for it; it stands on its guard.
   - A car switched off leaves a steady offset in the mix (Resonance measured -0.0395, -28 dBFS, for as long
     as the car stands, after bursts to -29 dBFS while it dies). Measure a parked car switched off in AudioLab
     first; fix only if open-fps has it, with a guard on the mix's mean.
     Done 2026-10-24 (54f29162): open-fps had it (a hatchback -42.7 dB of DC); the live engine voice fades the
     trapped gas's slow push once switched off, now -125 dB. An idling engine's own small offset is untouched.
   - One source can silence the whole mix: a `SourceSpec` with its cone left at 0, 0, 0 (the emitter's
     defaults are 360, 360, 1), or a synthesised source naming a model the catalogue lacks. Give `SourceSpec`
     the emitter's defaults, `SynthRunning` true among them (item 9 of the list); a missing model silent on
     its own; and find how one source takes the mix to silence (likely a NaN held in the reverb), so no
     source can.
     Done 2026-10-24 (54f29162): no whole-mix silence reproduced; defaults given, a bad number or a missing
     model silences that source alone, and a dead cone no longer feeds the reverb twenty times over.
   - Then the host's needs: one dispose that stops and joins every thread and task the audio starts (6);
     a public headless mode on FMOD's no-sound output, for tests on CI (5); one call playing a recording at a
     place with a handle to move and stop it (1); the loader reading under `Paths.Sounds` and from bytes or a
     stream (7); a door for the host's own flat sounds through the master limiter, and master and world volume
     set while running (2 and 3 as Cody cut them: no music in the library); a muffle over the whole mix, for a
     head under water (4); Rock, SheetMetal, TimberFloor, HollowDoor and Canvas in the material table (10,
     realism for open-fps's own houses too); a horn with no vehicle source (8), last.
     Done 2026-10-24: the dispose (71946d55) and headless mode (babc5f25; `FmodAudioProvider.NoSound`,
     `InitializeSteppedByHand`/`StepByHand`, `MixPeakDb`); the five materials (e3b44c94, indices 59 to 63, on no
     map yet; wire hash 053dd7700918 and a new door fingerprint).
     Done 2026-10-24: the loader under `Paths.Sounds` and from a host's bytes (c80fc14d), and `AudioWorld.Play`
     with a `PlacedSound` handle (8cc41caf).
     Done 2026-10-24: the host's group (`HostGroup`, `FmodSystem`) with `SetMasterVolumeDb` and
     `SetWorldVolumeDb` (e3988d42; every world sound now under one World group), `SetWorldMuffle` (cd96b380) and
     `AudioWorld.PlayHorn` (32291b55). The near-wall reflections moved from the master onto the World group (Cody
     2026-10-24): walls colour world sounds only, not the interface or the host's, and the muffle covers them.
   - Sean's update of 2026-10-20 and 21 (the same inbox file), taken one item at a time as Cody agrees each:
     - Recordings that never start (agreed 2026-10-21; next after the faults above). In Resonance's copy 58 of
       about 58,000 recordings in 36 minutes never left their first sample, past their delayed start, not
       paused and not virtual; footsteps cut off. open-fps starts voices the same way (`setDelay` on the
       parent's clock, `FmodAudioProvider`). First a check that changes no sound: count and log each voice
       still on its first sample once its start has passed, naming the sound. If open-fps has it, find the cause
       in the voice's start (the head DSP, its fade points, its pitch); Resonance's restart a quarter second
       late hides the fault and is a last resort only.
       Done 2026-10-24 (af3ad786): open-fps does not have it (0 of 63,000 scattered starts, 0 on the busy
       street and at login); the check logs `[NEVER-STARTED]` and stands as the guard.
     - Ground in narrow strips heard as a small room (agreed 2026-10-27: Cody, "take it if you think it is more
       realistic"; it is, since the ground under the feet is already in each voice and a second copy is a box).
       Done 2026-10-27 (073ecdcc, then the merge after it): a walkable slab whose underside rests on the ground,
       or on a broad slab laid on it, with its top within a step there, is open ground whatever its width; each
       point is judged against the ground under it. A rail on posts stays. Albany's rails on their ballast leave
       the trace; the city changes by one step on a platform; Magnolia not at all. open-fps has the same rule:
       `SteamAudioScene.IsOpenGround` leaves ground out of the listener's trace only when a piece is at least
       1 m each way and level, so kerbs, gutters, thresholds and ramps' sloped tops stay in and the ground
       under the feet returns about 9 ms late. Sean's fix: judge a piece that is stood on by what it is, not
       its width.
     - Traffic (Cody, 2026-10-21): traffic is the library's, and Sean's traffic work is taken in, slowly and one
       item at a time, each built well and fitting open-fps. Roads carry player drivers as well as NPCs, so
       nothing may work for NPC cars alone; and an effect needs a thing that exists to cause it (a breakdown
       needs a battery that runs down or a tyre that meets something on the road, never a dice roll). The
       faults Sean found that open-fps's traffic may share come first, each shown on open-fps's own roads
       before it is fixed; then lane changes and junctions that list their roads and ways; breakdowns,
       flashers and tow trucks last. None of it before the items above.
7. Engine package stage 6, Common to the wire: DONE 2026-10-18 (docs/ENGINE_PACKAGES.md, stage 6's built note).
   Left: the Arch reference, which goes with the physics views and constants; and `Scopes` stays in Common unless
   Cody wants a shared game project for it.
8. Engine package stages 9 and 10: DONE 2026-10-18 and 19 (docs/ENGINE_PACKAGES.md, their built notes). The ten
   aids read the scene view; the people's laws are Agents' and read `IPeopleWorld`. Alex's day and the door
   manners are unchanged line for line (`PeopleReplayTests`).
9. Physics, then (docs/PHYSICS_BOUNDARY.md):
   - stage 2's leftovers (16.6): one rolling resistance and drag law, one brake law (surveyed 2026-10-27, 16.8:
     the renames DONE 2026-10-27, nothing moved; nine decisions for Cody); `VehicleChassisSpec` went whole to `OpenFPS.Models` with `VehicleSpec` (engine
     packages stage 2), so nothing is left of it; the generators' 9.81 at the maps'
     next regeneration. `WindModel` folded into `WindField`: DONE 2026-10-27 (16.7), bullets feel the air the
     ears do;
   - stage 4, rigid and soft bodies, with geometry stage 6's second half (the vehicle meshes in the Steam Audio
     scene, moved with their bodies, the cost with 40 cars), so each body's shape is made once for collision
     and for the acoustic scene (section 8). Air moved by passing things, leaf litter and impacts that pass on
     momentum ("Next", below) are stages 4 and 5;
     surveyed 2026-10-27: thirteen steps, costs measured, five decisions for Cody (PHYSICS_BOUNDARY.md 18);
     steps 1 and 2 (Jolt shipped, `BodyWorld`) built 2026-10-27, unused by any system, wire hash 59dd0b1e4b31
     (PHYSICS_BOUNDARY.md 18.6); step 3 (tiles and door leaves as Jolt meshes, `PieceBodies`) and step 5 (sweeps,
     the vehicle's one shape as a kinematic body) built 2026-10-27, unused by any system, wire hash WIREHASH
     (18.7, 18.8); step 4 (analytic tests of `BodyWorld` against closed-form physics, tests only) built
     2026-10-27 (18.9);
   - stage 5, moving air and loose layers;
   - stage 6, vehicles in full.
10. A source heard from the whole of itself, steps 5 to 8 (Cody, 2026-10-16: "a river should sound like a flowing
   river the entire width and length of the river"; docs/RUNNING_WATER.md 15.9):
   - 5: fire and woods on the same cut: DONE 2026-10-18, unheard (docs/FIRE.md 12.13). Renders of the fire still to
     make, to check its approved sound stands;
   - 6: crowds, the seat blocks as geometry with decorrelated applause per piece;
   - 7: far traffic along the roads;
   - 8: a path per near piece (today every piece is heard through its source's middle's path).
   With them:
   - A river's banks as their own flow (Resonance's 2.2): DONE 2026-10-17, unheard (docs/RUNNING_WATER.md 15.10).
     Each bank the outer fifth, 6 % of the flow at half the depth over stones half the size; on Oak Creek 12 to 16
     dB under the middle and brighter. Listen: "Listen in the game" below.
   - Whitecaps over open water (Cody: "especially yes"): DONE 2026-10-17, unheard (docs/RUNNING_WATER.md 15.11).
     Each pond's open water a source over its whole surface, its crests breaking by fetch, wind and gust; none under
     3 m/s, Albany's ponds whitening from about 8 m/s. Listen: "Listen in the game" below. Left: the sea's whitecaps
     on a map with a coast; a lake's depth from data (every pond is 1.5 m deep).
11. Speakers sharing a program keep one track clock (Resonance's 3.3): DONE 2026-10-17 (changes.md; docs/CLIENT_NOTES.md
    "Speakers sharing a program"). The world's clock is the program's; the speedway's four horns say one line, and a
    speaker coming back joins its program where it is, heard at its own flight time. Waiting on the ear ("Listen in
    the game").
12. Smaller, when a session is free:
    - Stored traffic and rail tracks list movers by ordinal, not entity id, so a map edit does not churn them
      (the fountain's taps moved every id by 5 on 2026-10-17): DONE 2026-10-17 (changes.md). The tracks were
      written again once, every row the same apart from its key.
    - A real place with a river. No map has one yet, so the `river_bank` shore is untested in the game.
    - Mapped waterways in the one world's tiles keep a dry placeholder: `VoiceWaterways` runs only at a map's load,
      and the world builds its drainage per frame. One session.
    - Optional, low priority: a sound source set 5 to 20 cm from an inside corner is given the outdoors
      (Resonance's 1.4, for sources; listeners never get that close). `RoomCells.From` and `TryAt`.
13. Geometry stage 5, diggable ground with strata: held until it is wanted.
14. Then detail, in this order:
    - weather as a system and wind acting on things (docs/MATTER.md 5.3 and its order of work, steps 5
      and 6; CODY_ASKS item 12), with real weather for the real places (docs/REAL_WEATHER.md);
    - footsteps through the structure ("Walls", below);
    - the struck sounds, from Cody's notes ("Bump sounds", below);
    - cars in full detail ("Cars in full detail", below);
    - the world: lots and addresses, traffic on the world's roads, kerbs and bridges on them
      (docs/WORLD_STREAMING.md, "Left after stage 2");
    - enemies, Dinosaur World, melee and NPC inventories (CODY_ASKS items 6 and 11).

In every play session, clear the "Waiting on Cody's ear" list so heard work merges before it piles up.

### Left from the special-cases audit
Built 2026-10-11 and 12, unheard (docs/SPECIAL_CASES_AUDIT.md; the whole of P0 to P8 is in docs/DONE_2026-10.md).
- P5: storeys in `MapTracker.Floors` from the building; the glass door's sound key reading the prefab's glazing.
  (Cody) Is a bus shelter's canopy "a roof" to the scope? Which doors do Alex's haunts take as front entrances?
- P3a: S17 (ask by movement). P6: beacons (their wall test is tuned by ear) and birds asking the worker (by ear).
- P3d: (Cody) the rain's 40 dB and a bump's 85 dB starting levels.
- P4: (Cody) the path at the choosers (the worker would be asked about every contender); a budget for sirens,
  horns and talkers.
- P6, the rain: the pool not yet measured with `--pop-hunt`.
- P7: an aircraft on a circuit has no ground run, so one whose circuit came down to a runway would be at cruise.
- P8: S21, `VehicleShadow` on the shared `Diffraction`, deferred until geometry stage 6 gives vehicles bodies.
- Engine packages: the catalogue's readers onto an instance and the folders named by the host (stage 2);
  `GroundWater.OfMaterial`'s runoff class by name, a runoff cover column in the table (stage 3); the parking
  state (stage 10); a view of a vehicle for the laws (physics stage 6); `EditorKeys.Enabled` as an option the
  session holds and the editor's folders as one options value (stage 8).
- Sound library: the statics stage 5 left that are not the audio world's (19.13): the door models' levers (with
  the next door model change), the geometric acoustics' traces and A/Bs, the geometry's switches, and
  `ExtendedSources.TreePlaces`.
- Regions become places: which tiles a room is sent with, and the indoor flag, until the flood can decide them
  at load (docs/GEOMETRY.md 12.6, step 4).

### Landed since the last push (2026-10-14 to 17, on main, not pushed)

Details in changes.md and the docs named. All unheard but where said.
- Sound fixes: a place's late answers are that place's (no rush into the tail stepping out of a small room); a
  one-off's copies heard only while the way from their wall is clear; a door's jamb is not a wall (near-wall
  copies as whole as the surface); your own room's reverb stays binaural after walking in from a busy street;
  the binaural pool goes by how loud a voice is at the ear; no voice stops with a click (every stop an 80 ms
  fade).
- New sound laws: the ground by its impedance (asphalt as Nord2000's class H); sound bent by the wind and the
  air's layers; the air's stir from the surface layer; the wind's direction from the weather.
- Running water heard from the whole of itself, steps 0 to 4: the cut at the ear, the mapped creeks, drainage
  as whole runs, shores as whole outlines, the fountain from its spec. Albany's, Magnolia's and the city's maps
  regenerated.
- Physics stage 2 (the ricochet, one gravity, one air) and stage 3 (one world view; standing on a car's roof
  no longer steps; a player's grounding sent).
- Engine packages, stages 1's rest, 2 (Models), 3 (Environment), 4 (World), 5 (Agents), 7 (Accessibility)
  and 8 (Editor). Sound library stages 5's rest, 6 (the world input, `OpenFPS.Audio`) and 7 (`AudioWorld`).
  The namespaces of the library and of the packages under it follow their projects.
- Materials told by what they are, not their names; friction and restitution columns; ground names for snow,
  ice, solid timber, clay and leaves.
- Tests and guards: the spoken-lines replay drives; the ricochet table; the world's and the environment's wire
  bytes; the binaural stages, the reverb bus's panning and the voice stops; Resonance's maybes held by guards;
  the fire allocation test hardened; the Steam-Audio-opt-in tests isolated; subnormals flushed on the render
  threads.

### Landed since the last deploy (2026-10-09 to 11, pushed, not deployed)

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
- Floors as constructions on the real places and the world's tiles: a home's carpet or boards on the concrete
  slab, not 27 cm of finish on the ground (docs/GEOMETRY.md 12.6). Unheard; world tiles remade (generator 8).
- Nothing laid along a line enters a building on the real places and the world's tiles: a building the
  carriageway runs into is not made, a made-up driveway goes round a neighbour, every road, sidewalk, drive and
  path piece stops at a wall (docs/GEOMETRY.md 12.6, "A road stops at a wall"). Unheard; world tiles remade
  (generator 9). Listen: Country Food Market and the Exxon forecourt on Magnolia, 1024 Belmont Avenue Southwest.
- The city under the floor test: a concrete invert under the tunnel's road, Main Street's sidewalk places stopped
  at the tunnel's mouth (docs/GEOMETRY.md 12.6). Unheard; no sound changed in the sweep.
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
- The order of work of 2026-10-11, replaced by the order of 2026-10-17 above.

### Decided (Cody, 2026-10-16 and 17)

Cody approved every recommendation put to him on both days. The ones that set later work:
- Take from Resonance only what adds realism, nothing Resonance-specific. From the survey of 2026-10-14: the
  ground by impedance, refraction (one rule with the thunder's), then the wind's swing, in that order (all
  built). The wind follows the weather's direction, never a random meander: "wind sound should actually follow
  the wind directions, not random".
- A source sounds from the whole of itself (Cody: "a river should sound like a flowing river the entire width and
  length of the river"): each piece as loud as what it holds (the balance gain goes), mapped OSM waterways voiced
  directly, the approved sounds re-listened, not re-fitted. Whitecaps wanted ("especially yes").
- Held until heard: the ground law only takes, never adds; speech's ground as a separate change after the ground is
  heard; the ground's coherence stays 5e-6 until refraction is heard; the thunder keeps its own profile; the calm
  night's treble flicker to be listened to first; convection with real weather.
- Later, at their own time: the generators' 9.81 to 9.80665 at the next map regeneration; a map's own gravity on
  the client and in the sounds once a map sets one; the door fingerprint hashing only the columns doors read; a
  runoff cover column in the material table; a prefab category for Audience; the door models' levers with the next
  door model change; `--subnormals` on an Intel machine when one is to hand.
- Kept: `Air` in Materials; the parts vocabulary in Sound; `GlassKind`, `GlazedPart` and the whole `VehicleSpec` in
  Models; the instance named `Catalogue`; `RicochetLaw` and `WeaponDefinition.Slug`; the world input stays the
  snapshot; the bird habitat survey stays in the library; lab rigs use shipped budgets unless they pass
  `AudioWorldOptions.FromEnvironment()`.
- The binaural pool: one-shots more than 40 dB under the loudest are not started; a voice that loses its stage is
  faded, not stopped.
- The engine packages the library stands on (Materials, Physics, Environment, Models, Schema) renamed like the
  library before Resonance pins it (done); the lab's spikes in `OpenFPS.AudioLab.Spikes` (done).
- The handoff to another machine ended 2026-10-16 ("we can cut sean out of the picture now"): work is done on
  Cody's machine only, and no reply document goes to Resonance's team.

### Resonance's list of 2026-10-17: taken, dropped, not taken

From inbox/2026-10-17-resonance-audio-open-fps-lacks. Taken: items 1 to 4 and 9 to 11 of "Next, in order" (1.1,
1.2, 2.2, 2.6, 3.2, 3.3, 1.4 for sources).
- Dropped, tested 2026-10-17: open-fps does not have the fault, and a guard test holds it (AudioLab
  `--resonance-maybes`, the guards of 21f2c6a4 and ee603b93). 1.3, flush joints and a shut leaf believed a way
  round; 1.4 for listeners, a corner taken for the outdoors; 2.3, a cut line's pieces keeping an old direction;
  3.1, recordings stuck playing (1,470 one-offs in 120 s, none held past its end); 3.4, a crash closing FMOD
  (40 devices closed with 3,120 sounds opening, none).
- Not taken: 1.5, real-thickness walls (map data); 2.7, overhead and roll doors, and 4.2, 4.3 and 4.5 (the game's
  side, not the library's); 4.4, the ERA5 climate generator, to be agreed with Sean: one weather state in
  Environment fed by live or generated weather; 4.1, runoff: compare with `GroundWater` and converge.

### Resonance's list of 2026-10-18: what a host needs

From inbox/2026-10-18-resonance-host-needs. Taken as "Next, in order", item 6a, and the vehicle sound as a
candidate for item 6's part 3. Later by Cody's word of 2026-10-18: item 11 (a small machine heard from its
parts, a compressor that is not an air conditioner, a river's banks apart from its middle, a flow fed from its
ground, a water feature's taps each at its own level, a vehicle fading at the world's edge, a vehicle sized apart
from its preset), rain on the head, and geometry stage 5 for Sean's ground ("Next, in order", item 13). Sean's
questions, Claude's advice, waiting on Cody: his town at 3,700 ft rather than 3,300 (yes: a lake adds space,
not many boxes); the height said while swimming (the head's, since climbing out is judged from it).

### Matter
Cody, 2026-10-10: every material interacts with every other as in real life; wind moves things
physically and its sound comes only from what it moves; a built thing sounds by its material, size and
shape. The design and its order of work are in docs/MATTER.md. Steps 1 (the material table), 2 (struck
things) and 4 (water over the ground) are built, and fire by fuel (step 3) has its stage 1; next are
weather as a system and wind on things ("Next, in order", item 14), then gases and chemistry.

### Loudspeakers
The PA horn and the megaphone as amplifier, driver and horn: approved by ear 2026-10-10
(inbox/loudspeaker-2026-10-10). The sound is settled; open, without changing it: a column speaker and a
ceiling speaker as presets (a datasheet each; the Baffled mounting is built); the traced echoes are fed
the speaker's on-axis spectrum at its radiated level, not band by band; one-off world sounds
(WorldSound) cannot name a loudspeaker yet; the first play of a recording not yet loaded is started by
the provider after the budget has let it go, and its placement goes stale for the line (seen once on the
old PA in --loudspeaker game).

## Waiting on Cody's ear

- Everything of 2026-10-14 to 17 is unheard. It is listed with its /tp spots at the top of "Listen in the game"
  (Now, below): doorways, the ground, the wind and the air, running water, the binaural pool, stops without a
  click, your own room's reverb, a car's roof.
- Door renders that end while they ring (Cody, a decision; found 2026-10-17). A door model's render stops at a
  fixed time after its last event, not when its sound has died away, and the voice ends with the buffer, so the
  step is in the render. Measured on the 228 renders in the door cache and checked against the current code:
  122 of the 130 knob door keys end above 60 dB under their peak, the worst a close whose last 5 ms are a 33 Hz
  ring 26 dB under its peak, 81 dB SPL at a metre, cut mid-cycle (KnobDoor's hand close stops 1.0 s after the
  first blow, its open 0.6 s after the swing); 10 of the 28 glass door keys (the worst a pull door's close, a
  knock starting 40 ms before the render ends and cut 31 dB under its peak); 3 of 6 push bar closes, 56 dB under.
  The patio, automatic, lock and car window renders end 87 dB or more under their peaks (the lift door's were not
  measured). The models were approved by ear, so nothing was changed. The fix by the rule would let each model run
  on until its sound is 60 dB under its peak (the mixer's silence) and keep every sample it makes now: the approved
  part unchanged, a longer tail, every door render made again once. Not a fade: the knob door's tail is well above
  hearing.

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
- Speakers sharing a program keep one track clock (2026-10-17, docs/CLIENT_NOTES.md "Speakers sharing a program").
  On the speedway, in the infield 5 m east of the PA pole (`/tp 5.0 0.0 0.2`): the announcement is one voice from
  the pole, every 20 s, where it was four of the same line 0.9 s apart (a chant, 2.7 s from first to last). The
  first line after you arrive can take up to 20 s now (its turn is on the world's clock, the same for everyone).
  Then 60 m east (`/tp 60.0 0.0 0.2`): one line still, the horn facing you plainest, about 0.18 s after it leaves
  the pole. Two players together hear it at the same moment, each by their distance. Two speakers playing one loop
  (none on a map: place two Space Megaphones about 40 m apart with the editor, F12): both say the same moment of
  it, the far one later only by its distance, and walking between them never starts either from its top; one coming
  back fades in rather than clicking. Lab: AudioLab `--resonance-maybes track`, `old` for before.
- Whitecaps over open water (2026-10-17, docs/RUNNING_WATER.md 15.11). On Albany (`/join albany_or`), its biggest pond:
  `/weather wind 12 west` (or a stormy front), stand on its downwind shore, the east one, 2 m back
  (`/tp -729.4 613.6 -7.3`), and walk west out over the water (solid underfoot) to the point furthest from any edge
  (`/tp -945.5 592.5 -7.6`). A soft hiss of small breaking crests over the pond, more of it out on the water and
  downwind, swelling where a gust crosses; nothing at all in a light wind, and no point where it switches on: as the
  wind rises from calm it fades in from nothing, and out as it drops (`/weather wind 4 west`, then 8, then 12). Say
  whether it should be louder or brighter, and whether the onset (about 8 m/s on these narrow ponds) is right. On
  Magnolia, its pond from `/tp 563.4 -999.7 0.2` and out at `/tp 394.2 -972.2 -0.1`: plainer, with more water upwind.
  Lab: AudioLab `--waves open ramp`, `--extent-walk open,open-ramp render`.
- A wide creek's banks (2026-10-17, docs/RUNNING_WATER.md 15.10). On Albany after heavy rain (`/weather rain heavy`
  for half an hour or more), beside Oak Creek 1.5 m out from its edge (`/tp 689.4 1142.1 -6.3`), walking east along it
  to `/tp 748.6 1147.3 -6.5`: the near edge a finer, brighter babble close to you, the middle's deeper gurgle a little
  further off, the far bank fainter; the whole about 1 dB quieter than before. Lab: AudioLab `--running-water banks`,
  `--extent-walk oak,oak-out render`.
- A moving one-off's echoes move with it (2026-10-17, docs/CLIENT_NOTES.md "One-off sounds: why the echoes are as
  they are"). In 1 Elm Street's living room (`/tp -414.2 -108.0 0.2`), slide the patio door (the back door, 2.9 m
  south-west) open and shut a few times, facing north: its answer off the west wall, on your left, should go with
  the panel and thin out as the panel reaches the jamb, not stay where the run began; the front wall's, ahead of
  you, goes with it too. Small: the panel moves a metre and the walls are 4 and 6 m from you. Then on Main Street
  (`/tp 7.0 121.0 0.2`), `/weather storm`, walk along the street while thunder rolls: the facades' answers to it
  keep their direction as you walk instead of staying where you first heard them. Lab: AudioLab
  `--moving-echoes door` and `pass`, each with `before`.
- A restarted outlet no longer doubles its engine (2026-10-17, docs/CLIENT_NOTES.md "A voice that reads another's").
  On Main Street in traffic (`/tp 7.0 121.0 0.2`): as cars pass close and their fronts split off and merge again, no
  quarter-second swell or brightening of a car's engine as it does. In the parking garage (`/tp -66.0 62.0 0.3`), get
  into a parked car, out, and into another: the cabin sounds the same each time, with nothing of the first car left.
  By the fire at 58 Alder Street (`/tp -325.0 141.0 0.2`): the fire as before, and when it goes out all of it goes
  out. Lab: AudioLab `--resonance-maybes outlet`.
- A doorway route leaves from the wall's face (2026-10-17, docs/CLIENT_NOTES.md "A doorway route leaves from the
  wall's face"). Nothing on the maps should change: every door hangs at its wall's middle. In Selby House, with
  flat 00B's door open, someone talking in the corridor (`/tp 20.3 157.5 0.1`) heard in the flat (`/tp 23.5 157.5
  0.1`) as before. A map or a Resonance scene with a leaf at a face of a thick wall is where it is heard.
- The street heard indoors is traced from the street again (2026-10-17, docs/CLIENT_NOTES.md "A doorway route leaves
  from the wall's face"). In Selby House's lobby (`/tp 11.8 157.4 0.1`), with the glass front door shut and then
  open, in traffic (`/weather rain heavy` helps): the street's reverberant tail through the door is the open
  street's, coming from outside it, where it was traced from 2 m inside the lobby (the street with the lobby's
  echoes in it). The same at most doors of the city, Albany and Magnolia; the lobby's own reverb is unchanged.
- A shut door lets through its gaps (2026-10-17, docs/CLIENT_NOTES.md "A shut door leaks by its gaps"). In
  Selby House, ground floor: stand in flat 00B (`/tp 23.5 157.5 0.1`) with its door shut and have someone talk
  or fire in the corridor (`/tp 20.3 157.5 0.1`): heard through the door, muffled but clear, about 16 dB down
  and no longer dull (the corridor's top band was 55 dB down, now 16); open the door and it is about 16 dB louder.
  In the lobby (`/tp 11.8 157.4 0.1`) with the glass front door shut, the street (`/tp 8.0 157.5 0.2`) is as
  before in its body but has a little more hiss and sparkle (high band -32 dB, was -45). Standing in the lobby by
  the shut steel door to the corridor (`/tp 17.5 157.5 0.1`), the corridor beyond it (`/tp 20.3 157.5 0.1`) is
  much louder than before (mid -19 dB, was -48; it is a fire door with smoke seals, 3 mm round and under, since
  2026-10-17): say whether it should sound more sealed than that. Then the roof: at the stair head of Selby
  House's roof access (`/tp 10.6 155.5 21.3`), in `/weather rain heavy`, with the roof access door shut, the rain
  on the roof outside (`/tp 10.6 159.5 21.3`) is a dull distant patter, its hiss gone (mid -41 dB, was -17; the
  door is weatherstripped like a street door since 2026-10-17); open the door and the roof is suddenly there, 25 dB
  louder in the middle.
- Your own room's reverb stays binaural (2026-10-17, the fault Cody found 2026-10-17 00:40). On Main Street in
  traffic, `/weather rain heavy` (`/tp 7.0 121.0 0.2`), then walk into Selby House's lobby (`/tp 15.2 157.1 0.1`)
  and clap (T), and shut a door: the lobby's reflections and tail are wide round you, not the same in both
  ears. Lab: AudioLab `--clap-city busy`.
- No voice stops with a click (2026-10-17, docs/CLIENT_NOTES.md "A voice is never cut off"). On Main Street in
  traffic and rain (`/tp 7.0 121.0 0.2`, `/weather rain heavy`): as cars come and go out of earshot, as walkers'
  steps are dropped and taken up, and as the rain's patches move round you, no ticks at the ends of sounds. Walk
  into Selby House's lobby (`/tp 15.2 157.1 0.1`) and out again: no tick as the street's voices change. Then
  `/join speedway` from the city: the city fades out over half a second rather than stopping. Log out to the menu
  and quit: no click at the end of either. Each voice now takes 80 ms longer to go; a sound that used to stop at once
  (a horn let go, a siren switched off) has that much of a tail.
- The binaural pool by rank (2026-10-17, docs/CLIENT_NOTES.md "The binaural pool"). On Main Street in traffic,
  `/weather rain heavy`, at `/tp 7.0 121.0 0.2`: cars passing close are outside the head, left to right with no
  jump inside or above it; the rain is wide all round; a bus's horn and the drops at your feet always sound; no
  clicks or dropouts as cars come and go (a voice let go fades over 80 ms). Far walkers' steps you could not make
  out under the traffic are no longer played; the near ones are. The log's "Mixer load" line should read
  "0 refused for want of one".
- No rush stepping out of a doorway (2026-10-14). With traffic on Main Street, inside Marlow Tower's front
  entrance (`/tp -12.0 -103.0 0.1`), the door open, walk east out to the pavement (`/tp -7.0 -103.0 0.2`)
  and back, a few times: the street's reverb comes in as you cross, with no burst of it for a moment at the
  threshold.
- No closing-in standing in a doorway (2026-10-16). Marlow Tower's front entrance, the door open, stand in
  the doorway (`/tp -9.7 -103.0 0.12`, solid while the door is shut) and turn about: it should sound like
  standing in a doorway, not in a narrow cupboard, and no louder than a step either side. Also flat 00B's
  doorway off the ground-floor corridor (door open, `/tp -21.3 -102.7 0.1`). In rooms and along walls nothing
  should have changed, turning toward a wall included. In Selby House's lobby: from the lobby (`/tp 15.2 157.1 0.1`) walk out through the open front door to Main
  Street and back: no closing-in in the doorway and no burst of the street's reverb at the threshold.
- Running water heard from the whole of itself (2026-10-16 and 17, docs/RUNNING_WATER.md 15): a re-listen of the
  approved creek, gutter, waves, fountain and fire, not a re-fit.
  - Albany, the drainage creek of step 1: beside it at `/tp 931.4 -1274.8 1.7`, walk west along it to
    `/tp 871.9 -1284.5 1.5`, then out from it to `/tp 912.4 -1339.0 2.3`. The level no longer swells every 20 m;
    it is 2.5 dB quieter than on 10-16, and its water gathers down it (`/tp 929.8 -1272.3 1.7`).
  - Albany, Oak Creek: it sounds where the map draws it (it was silent), louder in riffles than over pools, rising
    in rain: `/tp 719.3 1143.7 -6.6`, walk east to `/tp 748.6 1147.3 -6.5`.
  - Albany, a pond: its shore one even sound all the way round, lapping on the shore the wind blows onto
    (`/tp -412.1 1373.7 -9.5`).
  - The city, Foundry Street's gutter in rain (`/weather rain heavy`): flat along its length, `/tp 44.5 122.2 0.2`
    east to `/tp 74.0 122.2 0.2`, its middle at `/tp 59.0 122.2 0.2`. With the road as dense asphalt it keeps its
    top heard along the road.
  - The city, the Elm Park fountain: the rim jets from near the kerb (`/tp -325.0 220.0 0.2`), and one voice from
    about 80 m (`/tp -325.0 140.0 0.2`).
- The ground by its impedance (2026-10-16, docs/CLIENT_NOTES.md "The ground by its impedance"). In Elm Park on the
  lawn (`/tp -350.0 240.0 0.2`): the fountain's low splashes across the grass a little thinner in the middle, the
  train beyond the park 3 to 4 dB less of its middle, a garden mower far off thinner; a car on Alder Street, heard
  off the pavement's concrete, unchanged. Close by, nothing changes.
- Sound bent by the wind and the air's layers, and the air's stir (2026-10-16, docs/CLIENT_NOTES.md). From Elm
  Park's lawn (`/tp -350.0 240.0 0.2`), `/weather wind 5 west`, on a sunny afternoon: the light rail and the traffic
  150 to 300 m upwind lose their treble first and go into a shadow; downwind they keep it. Past 100 m a far
  sound rises and falls a little with the gusts. On a calm clear night (`/weather wind calm`): far sounds steady,
  the treble barely flickering (Cody asked to hear this first). Within 50 m nothing changes. The thunder is
  unchanged. On the airport side of the rail's east leg (the train runs north and south at x = 178, dirt
  between), checked standable: `/tp 330 -100 0.1` (152 m off), `/tp 380 -100 0.1` (202 m, on the runway) and
  `/tp 470 -100 0.1` (292 m). With `/weather wind 5 east steady` and `/edit map set time 15` the passing train
  is dull and much quieter at 292 m than at 152 m, swelling as gusts move the shadow's edge; with a west wind
  or at night it is fuller, the middle carrying. `/edit map set time server` puts the clock back.
- The wind's direction (2026-10-16, docs/CLIENT_NOTES.md "Where the wind comes from"). In Elm Park among the
  trees (`/tp -375.0 195.0 0.2`), `/weather wind 5 west gusty`, stand facing north: the wind in the left ear,
  swinging a little toward the face and the back over tens of seconds, never from the right; turn to face west
  and it is in both ears. `/weather wind 5 west steady`: the same, with less swing. `/weather wind calm`: nothing
  at the ears standing still. Then `/weather storm`: `/weather` says it is turning to come from the north west
  while it comes round, and the ears follow it. By the fountain (`/tp -325.0 219.0 0.2`) and at
  `/tp -350.0 240.0 0.2`, the trees with `/weather auto`: no change of level you would notice (under 1 dB).
- Standing on a car's roof (2026-10-16, physics stage 3, docs/PHYSICS_BOUNDARY.md 17.3). From the spawn,
  `/tp 60 122 0.12`, face north and `/spawn vehicle i4_economy`; then `/tp 62.4 121.8 1.6` stands you on its roof.
  Stand still: no steps and landings (before, the client stepped and landed every round trip). Walk north or
  south off its end: the fall and landing heard once. With a second player: their jump no longer steps in
  mid-air at its top.
- The audio world split (2026-10-17, library stage 7): nothing should change. The spots in
  docs/SOUND_LIBRARY_BOUNDARY.md 20.11: Main Street before Marlow Tower, the Mill Road crossing, Elm Park by the
  fountain, Marlow Tower's floor 1 stairwell (a door's copies gone behind a shut door), Foundry Street in rain,
  and a parked car in the garage.
- Nothing standing still when a train first blows (2026-10-12). At the Mill Road crossing (`/tp -160.0
  -175.0 0.1`) with traffic about, the first train to blow and ring for it: the cars keep moving through the
  horn's first blast, the bell starts with the crossing closing. Also at the Main Street crossing (`/tp 0.0
  -180.0 0.2`). A patrol car's siren switching on, and a car horn, without the street stopping.
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
  Tower's floor 1 stairwell (`/tp -10.8 -104.2 3.28`, the pop hunt's ear `/tp -11.0 -100.8 3.3`): no pops
  from cars on Dock Street since the barrier model and the slab's edge (2026-10-11), and no dip in their
  low end as they pass the stair's head (an edge buried in the front, gone round through the brick). The
  street through a front just over a floor slab's edge, heard now: Brandt Court flat 33F `/tp -10.6 203.5
  9.4`, Selby House flat 31F `/tp 12.9 183.9 9.4` (Main Street, south). Nothing under a slab or a barn
  through the earth: Albany 1303 Tamarack Court `/tp -385.4 276.6 -1.1` (the street below), Magnolia 17902
  Country Fields `/tp -819.5 984.7 0.6` (outside to the west); two walls at a corner, through the one
  beside it: Magnolia 26522 South Barnett Lane `/tp -340.3 1060.0 -2.5`. Lines along walls and slabs
  elsewhere, quieter now: Brandt Court flat 30B `/tp -27.6 161.6 9.4` (Main Street), Marlow Tower corridor
  floor 4 `/tp -20.5 -50.0 12.4`;
  Magnolia 26706 Barnett Lane `/tp -357.1 1336.6 -4.2`; Albany 6229 Warwick Court's bathroom `/tp -142.1
  -35.1 0.0`, 1333 Morse Lane `/tp -377.0 204.6 -1.1`.
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
- A beacon behind a wall (audit P6, "P3b as built"): whether a blip sounds is decided on the hand-rolled path.
  On the simulator's answer, as every other source is heard, which beacons come through which walls would move.
  Try it, by ear?
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
- Sound library: CI compares renders by tolerance; 430 KB per stream regeneration. Recommended: yes to both.
  (`ColliderShape` went to `OpenFPS.Geometry` with the namespace rename, 2026-10-17; decision 4, MemoryPack,
  was taken 2026-10-10.)
- Streaming: a menu control for `/detail`; tree counts and cost of distant woods (+0.5 core); the cab's
  own reverb adds up to 20 dB at the bottom, so it is unchanged.

### Housekeeping finds
Left by the 2026-10-07 housekeeping; none changes sound.
- `ReachingFrom`'s two branches return the same point; `OccupancyService.Moving` stops getting in
  above 2.0 m/s for a driven car and 1.0 m/s for a map's bus; on tiled maps, confirm how the broadcast
  forgets dynamic things a client knew (Program.cs).
- `SharedMovementEngine`'s `stepped = true` is never read; `WindModel` and `WindField` overlapped (done 2026-10-27, docs/PHYSICS_BOUNDARY.md 16.7);
  `TransientSound` lacks its APPEND ONLY marker; `PhysicsUtils.GroundMaterial` returns its input.
- `ElevatorDoor.faceAtFoot` and `PushBarDoor.railHit` are kept only for the random sequence: retire them
  with a render regeneration.
- Stale code comment: `OpenFPS.Audio/RainField.cs` 38 lists gutters, downpipes and run-off as missing (built 2026-10-06).
- `OpenFPS.Sound/MachineParts.cs` 83: the TODO on a part's `ExtentMetres` still names ClientAudioSystem's
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
- Footsteps through the structure (Next, in order, item 14). A neighbour's steps are mostly impact into
  the slab; the game has only the airborne part, about 10 dB short on a carpeted floor and the wrong
  shape (a click, not a thud). Plan: the slab's normalised impact level from its own airborne loss by Ver's relation,
  Ln(f) = 38 + 30 lg f - R(f) (homogeneous slab; checks against a bare 152 mm slab's Ln,w 79, IIC 27);
  a resilient covering's improvement 40 lg(f/f0) above f0 = (1/2 pi) sqrt(s/m) from its dynamic
  stiffness (a material property: carpet on underlay 20-35 dB, IIC 26 bare to 60-67 carpeted,
  McGunnigle 2002); a footfall's force against the tapping machine per footwear (heels near it above
  100 Hz, socks and bare feet more below 100 Hz: Kylliainen et al. 2015, Acta Acustica 101(5)); the
  result radiated from the ceiling under the foot into the room below as a second voice per step.
  Needs footwear in the game (the bank has Carpet/barefoot; nothing chooses it).
- No cavity resonances or air leaks in walls: sealed glazing is about 10 dB optimistic in the mids. A shut door's
  gaps are modelled (2026-10-17, `DoorGaps`); a wall's vents and cracks are not.
- A shut steel stair door leaks as much as a wooden flat door now (-17 dB mid, by the same 3 mm and 10 mm gaps):
  if the city's stair doors are smoke-sealed fire doors, `steel_door` should declare its seals.
- Steam Audio counts walls in a row as (2n+1)/3 of one; the tracer counts them exactly.
- A line run along a sheet pays a panel as thick as its run (2026-10-11): a thin plate's law carried to a
  block, which overstates a thick wall's high band, and no flanking path of its own through the sheet's
  junctions. (The real places' walls, 25 cm of solid wood, are framed now and keep their panel: docs/GEOMETRY.md
  12.6, "Timber walls are framed".)
- Timber walls are framed (2026-10-11, unheard). Next: measure the sound change, `--barrier-sweep map=X n=4000
  seed=1` and `outdoor n=2000` with `server=DIR` (the maps before, whose prefabs are all still there) and
  `dump=FILE`, both places; `--path-probe` at 31907 Bobcat Lane (ear in bedroom 2 -18.8,1.75,20.1; the street
  -5,0.7,20.1; bedroom 3 -18.8,1.2,23.7). Left: the room's face of a medium-detail house is its cladding's
  material (one entity has one material: an assembly's inner plies have no face of their own to reflections or the
  room survey); ceilings are still a two-leaf plaster box 6 cm thick (one board on joists under an attic in a real
  house); metal buildings are 25 cm of solid steel (a steel frame clad in sheet); brick ties are no bridge (Sharp's
  point connection is not in the model); every stud is a 2x4 at 16 in. (Albany's newer houses are 2x6: no era in
  the data); a knock on a wall (StruckThings) reads its box as one stuff; a lintel too short to be a sheet is crossed
  apart from its plasterboard (Constructions.IsSheet).
- Diffraction is evaluated at 200/1250/8000 Hz, not the band averages transmission uses.

### Placing sounds
Nothing that takes longer than a frame belongs on the two threads that place sounds (PlacementThread;
models are made in VoiceBuilds, reverb stages in TracedStages, traces off TracedReverbSet's lock, cabin
path trims and other rooms' fields on the thread pool, all 2026-10-12). Left, each to be moved the same way:
- A map's birds, worked out on the first pass after the map loads (part of the load burst, Next, in order,
  item 6): 1.3-2.6 s on the city, every source still
  (`--room-change`, "birds" in the stall line). It is the map load, not a room change, but it is heard.
- The ear model's recompute (`FmodAudioProvider.RecomputeEar`, every 0.1 s a voice): 5-7 ms a voice in a
  debug build, and voices started together recompute in the same tick (40-80 ms ticks in a debug lab, 4-6 ms
  in release). Fine in the game's release build; spread or move it if it grows.
- Smaller, and within a frame or two: each new voice's first path (`FirstAnswer`, the hand-rolled tracer,
  1-4 ms, 10-20 cold), and the first frame of a session compiling the audio code (170-190 ms in the train
  lab).
- Subnormal numbers: the threads that render ahead flush them since 2026-10-16 (`FlushToZero`), as FMOD's
  mixer thread already did. Left: AudioLab `--subnormals` on an Intel machine, for the plain column the AMD
  one cannot show, and on Windows, to see that FMOD's mixer thread flushes there too. Thunder and the glass
  fracture split a render across the thread pool (Parallel.For), and only the thread that started one is
  flushed; the others are as the pool left them.

### Acoustics
- The room answering at once (early tail): clap in a main-street lobby and in flat 01F. Is 20-100 ms
  too strong? Speech and steady sounds get no copies; a per-source early part would fix it (Cody).
- Pops on the Main Street pavement and in Selby House and Marlow Tower; is the city too busy with cars
  heard over the roofs (Cody)? The pavement's are gone (2026-10-11: the way over the car park and the tower
  gave up after four boxes; 41-56 a minute to 0). Left: Marlow Tower's floor 1 stairwell, 4 to 11 a minute
  (`--pop-hunt ear=-11.05,4.9,-100.84`, `/tp -10.8 -104.2 3.28`), a car on Dock Street from -57/-83/-100 to
  -35/-52/-80 dB for 4 m of road, x -19.5 to -15.5. The guard's half is done (2026-10-11, unheard): a way
  round one thing that runs on through another is paid as the diffraction times what its legs pass through
  (docs/GEOMETRY.md 12.6, "Round one thing, through another"), so beside the stretch it is -45/-70/-100, and
  11 excursions in two minutes are 8. The stretch itself is done too (2026-10-11, unheard): a solid gone
  into by its edge pays what the line runs through of it (docs/GEOMETRY.md 12.6, "A solid gone into by its
  edge"), and the stairwell has no pops (10 to 0 in two minutes). The dip left there is filled too
  (2026-10-11, unheard): a way round an edge buried in another box pays that box once (docs/GEOMETRY.md
  12.6, "When an edge is buried"), so those 4 m of road are -45/-67/-93, within Maekawa's grazing loss of
  the brick beside them; listen. Older finds
  of `--pop-hunt`: a car's straight line through one wall as it passes a gap (-80 to -62 dB for 200-400 ms),
  and up to 19 dB between over the wall and over the roof under the covered hall west of Main Street.
- 58 Alder Street's fenced back garden is indoors to the server's survey, with nothing over it (found by the
  city's floor test, 2026-10-11).
- A made-up driveway is a straight strip: a house with none clear from the road (Albany 25, Magnolia 4, most
  behind another house) has none. A drive that bends round would give them one.
- The ground in the way (2026-10-11, unheard; docs/GEOMETRY.md 12.6, "The ground in the way"): a line or leg
  through the earth passes nothing and goes over the ground's profile; Steam Audio's ground no longer passes
  its cover's figure; the one-shot path goes over the ground too. Steam Audio made no tile scene on the places
  until the same day (a solid of more than 64 corners): what Cody heard there was the hand-rolled tracer.
  Listen at the spots in 12.6. Left: the shadow boundary steps from nothing to 5 dB (CNOSSOS-EU's lit side to
  -λ/20, and its flatness test, RIVM 2019-0023 5.4, for boxes and ground alike, a change of the law); rounded
  brows (ITU-R P.526); round the end of a ridge; a house on the rise is gone over only; a source in a creek's
  tilted water box takes the box's highest corner.
- The late tail: boomy below 500 Hz, narrow above 2 kHz? A second noise per ear above 1 kHz would cost
  25 MB and twice the convolution (Cody).
- Below 120 Hz the tail of a room you are not in is identical in both ears.
- A voice's ground reflection flanges, so voices have none. The ground as it answers is built (below);
  next, a lab render with head and body movement on both ends.
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
- Showers 11-22 dB darker than the recordings at 4-16 kHz; the dripping tap rings steel where real
  drips plink into water; the violent-rain gutter outlet 4-6 dB heavy at 250-500 Hz.
- Not built: toilet flush and cistern refill (its parts written down, docs/RUNNING_WATER.md section 14); the plug as its own control; NPCs using taps; pipe walls
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
  stage 4; Next, in order, item 9).
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
- `HotPathTests.RemovingNothingDoesNotInvalidateTheSnapshot` failed once in a big mixed run on 2026-10-16 and
  passes alone. Watch for it on GitHub's runners.

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
item 14): the aluminium bar sounds like a bright bell; running into the car sounds crunchy and staticky;
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
- Physics as one project: surveyed and decided 2026-10-11 (docs/PHYSICS_BOUNDARY.md; Next, in order, item
  9). The bodies and contacts are physics stage 4 (Jolt through JoltPhysicsSharp), the moving air and
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
  for traffic (Next, in order, item 14). Overture release 2026-09-23.1 will be withdrawn around the end of
  November 2026: moving to a newer one is a new generator version.
- Real weather for real places: designed (docs/REAL_WEATHER.md, 2026-10-10): Open-Meteo for the conditions,
  METAR to correct them near the ground; the clock decided 2026-10-11 (real local time); six decisions open
  (Decisions, above). With weather as a system (Next, in order, item 14).
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
Built 2026-10-16, unheard (docs/CLIENT_NOTES.md, "The ground by its impedance"): Delany-Bazley
impedance from Nord2000's flow resistivities, the spherical wave's reflection, Clifford and Lataitis'
coherence; it only takes from the approved copy. Asphalt is dense asphalt, Nord2000's H, since 2026-10-16
(Cody): water in a road's gutter keeps its top. Left:
- what the law would add (the far top over any ground, water and concrete at a hundred metres, 1 to 4 dB),
  held back as approved (Cody's call);
- roughness and the scattered share (the 20 dB floor stands in); the ground mixed over the Fresnel zone
  instead of one bounce point; μ² from the weather (measured 2026-10-16: the surface layer's would move a lawn's
  middle band 3.5 dB down on a calm night and 4.5 up in a 10 m/s wind at 100 m; held, Cody's call); the tyre
  share and textures seen by the law;
- speech could take the ground as band gains with no copy at all, which cannot flange.
Moving bodies go into the acoustic scene with Bodies and wheels stage 2.

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
2026-10-11 (12.6). Left of stage 4 (GEOMETRY.md 12.6):
- Sound points on models (MATTER.md 8): where a model's sound parts are, read by machine parts and emitters.
  Begun: struck sounds read the shape a part has (a pitched roof struck as its deck, a flight as one step).
- Facets as polygons for the engine echoes; per-surface materials in the image sources.
- Doors' remaining misses on Albany (16; GEOMETRY.md 12.6 item 4), not rechecked since the floods' openings
  (step 3), which ended the openings found in flooded rooms' faces (item 5).
Then stage 6's second half, vehicles and bodies in the acoustic scene, with physics stage 4 (Next, in order,
item 9); its first half, each vehicle's body as one closed mesh, was built 2026-10-12. Stage 5, diggable
ground with strata, is held until wanted; forests, ocean maps and boats.

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
- The city's load burst, measured 2026-10-17: Next, in order, item 6 (docs/AUDIO_WARM_START.md).
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
- Refraction: built 2026-10-16, unheard (docs/CLIENT_NOTES.md, "Sound bent by the wind and the air's
  layers"). Left: the thunder on the same profile and depth (it moves up to 25 dB, Cody's call); fences and
  buildings shadowing less under sound bent down; the shore (land or water by one point); a map's own latitude.
  The stir comes from the surface layer since 2026-10-16 (CLIENT_NOTES.md, "The air's stir from the surface
  layer"): still air swings nothing. Left: a calm clear night still swings the treble 2.4 to 2.9 dB past 150 m
  by the Rytov law (a question for Cody); the afternoon's convective eddies (they scale with the boundary
  layer's depth, which the weather does not have); the ground's coherence on the same stir (it moves a lawn's
  middle band up to 4.5 dB at 100 m, so it stays at 5e-6 until Cody says).
- The wind's direction: built 2026-10-16, unheard (docs/CLIENT_NOTES.md, "Where the wind comes from"). Left:
  a prevailing direction per world or real place (one for the server now, 250), and real weather's direction
  where a place has it (docs/REAL_WEATHER.md); the southern hemisphere's turns are the other way; rain does not
  read the wind in sound yet; the day's turn of the wind's strength (stronger by day) is not modelled.

## Later

### Acoustics
- Synthetic footsteps (parked 2026-09-28 after three failures by ear). If tried again: fit only the
  dry first 30-60 ms, give the heel the body's weight, and give each loose surface its own mechanism.
- Aggregation for traffic and crowds: many distant sources heard as one extended source (woods done; the rule
  is "Next, in order" item 9, steps 6 and 7).
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
