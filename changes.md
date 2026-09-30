# Changes

Recent work, newest first. `git log` has the rest.

## 2026-09-29

- Reflections were measured at 0 dB before anything else was changed: a clap in flat 01F put its
  placed copies at -14 dB and its traced tail at -17 dB against the direct sound, at or under what room
  acoustics predicts. So -24 was not a level the rooms wanted, and three steps replace it.
  1. Three bugs. A copy's gain used L/d, which is wrong inside the source's reference distance, so
     loud sources' copies were 8-11 dB hot (now max(L,R)/max(d,R), EarlyReflections.PlacedCopyGain).
     Far sources' traced echoes traced the open ground as well as carrying their own ground bounce,
     a comb at about the direct level. The master-bus boundary copies sat outside the trim.
  2. Two levels. `/tail` for everything traced and `/copies` for everything placed as a copy;
     `/reflections` sets both. By ear after step 3: copies 0 dB, the physical level; the tail -24
     (at 0 it was a wash that masked every direction).
  3. Copies as reflections. A surface keeps sqrt(1 - absorption) of the pressure, not 1 - absorption,
     which took twice the decibels. A copy carries only the mirror share, sqrt(1 - scattering) per
     bounce; a first-order wall's scattered share is played as its wash beside it. A room gets its
     first order and at most four second-order copies; the rest is the tail. Your own steps get the
     mirror share and the order limit, not yet the wash.
  Leaving a room no longer cuts its ring off: its bus falls at the room's own measured decay rate.
- Diffraction is the same both ways round an obstacle and exact over thin walls (open-fps-patches 7,
  with a closed-form edge search: 20 us a call). Doorways and low walls lose less.
- The material table is the only source of material values; `materials.json` is gone (a copy is in
  `docs/retired/`).

- The Windows client is brought level with the GTK one. Main menu: Connect, Saved Servers,
  Settings (output and input device, interface sounds), Open log folder, Quit, on the same
  `client.json` format (`%APPDATA%\openfps`). Keys come from the game window instead of a global
  hook, are cleared on every focus change, and Alt on its own no longer opens the system menu.
  Speech goes through NVDA, checked per line, with SAPI when NVDA is not running; the menus speak
  control names only when no screen reader is running. The connect form has Create account. It
  ships `machines/*.json`, which it lacked, so vehicles have engines. Logs go to `logs\` beside
  the exe, and a hang writes a dump there. Compiled from Linux, not yet run on Windows.
- `publish-windows.sh` builds a self-contained Release zip, `publish-server.sh` a server tarball
  for a VPS (no accounts database in it). Both build under `~/.cache/openfps-publish`, not /tmp.
  See `docs/WINDOWS_AND_SERVER.md`.
- Client and server must be built from the same `OpenFPS.Common`. Its sources are hashed at build
  time (`WireContract.Hash`), the client sends the hash with its login, and the server refuses a
  mismatch and says so. An old server cannot say so: it drops the login.
- `OPENFPS_ADMIN_PASSWORD` sets the admin password on a new database and resets it on an existing
  one. The server warns at every start while it is still admin123.
- The cabin of the vehicle you ride in plays its traced response at its traced level again. The
  reflections trim had taken it 24 dB down, and a bus ride was muffled, with the doors and the
  street gone. `/cabin <dB>` sets it for judging by ear.
- Engines are heard to start. Firing waits for the engine computer to synchronise
  (`EngineProfile.RevolutionsBeforeFiring`: three revolutions for petrol, four for diesel), and the
  driver holds the key until it catches. Every preset cranked for 0.02-0.09 s before; now 0.34-0.53 s.
- A bus lets passengers off at its stops only. Stopped at a light or a junction the doors stay shut
  and you are told so. Anything with a door chime works this way; the driver can always get out.

- One rule for every place, and the room algorithm is gone. A one-off sound's first 80 ms are
  placed voices mirrored through the surfaces round it, indoors and out, for claps and shots and
  your own footsteps alike. The listener's traced stage plays only the late tail, everywhere, as a
  diffuse field; other rooms heard through their doorways and vehicle cabins keep their whole
  traced response. Facade echoes and street flutter beyond the window stay separate events
  outdoors; in a room the copies past the window are dense and are the tail. Nothing in the audio
  path decides by "indoors" any more except that last, physical distinction.
- `/reflections -24` is the one level for every reflected sound against the direct: placed copies,
  every traced tail, and the traced echoes of far sources. `/room` and `/echoes -N` set the same
  number. It was reached three times by ear on three mechanisms, in a carpeted flat, a concrete
  tunnel and a street, which is why it is one number and not a per-place one. Zero is the physical
  level, measured. `OPENFPS_REFLECTIONS_DB` starts it; `OPENFPS_TAIL=full` keeps the whole traced
  response outdoors for an A/B against the tail-only rule.
- Retired: `/reverb room`, `OPENFPS_REVERB`, the SFXREVERB tail (the unit stays as a dry
  passthrough the traced stage is inserted at), the Sabine and enclosure estimates behind it, the
  wet-level loop, the room-equation sends, the anisotropy steering of the listener's bus, the
  first-order three-tap footstep echoes, and the labs `--open-air-reverb`, `--tailcheck` and
  `--reverb-route`. The survey's return direction and mean free path are no longer sent to the mixer.

- The reflection search only looks at the boxes that can matter. Every footstep and clap in a room
  searched the whole city (5,220 solids) and tested every leg against all of it: 73-90 ms per
  footstep on the game thread in the flat, which is five frames. A path no longer than the window
  allows lies inside an ellipsoid round the source and the ear, so the search first keeps the boxes
  within that reach and tests legs against those alone: 3.7 ms in the flat, 0.5 on the street.
  `--room-echoes` prints the cost.

- `/room -6` trims the room you are in: its placed early reflections and its late tail together,
  in decibels against the traced level. Rooms only; outdoors and open shelters are unchanged.
  `OPENFPS_ROOM_DB` starts it. The default is -24, set by ear, the same figure as the traced echoes
  outdoors ("-24 dB is where it's at, just like outdoors... everything sounds great and accurate").
  The copy-to-direct arithmetic is within 1.6 dB of physics and the flat's tail within 3 dB of the
  room equation, so the level is not where the 24 dB lives. Both trimmed paths render coherent
  copies of the source from a point; the untrimmed outdoor tail, accepted at its physical level, is
  a dense diffuse response. Next: scatter the placed copies by their wall's scattering, then see how
  far the trim can come back.

- Your own footsteps have the room's reflections again. In traced mode the step's reflections
  returned early, from when the traced response carried the early part; once the room's stage was
  cut to its late tail (parametric, silent for 50 ms) a step got a direct sound, then a tail, and
  nothing from the walls between. Walking the flat was a wash with no reflections in it. Steps now
  get what a clap gets: mirrored through the walls to third order, the twelve loudest inside 80 ms,
  each from its own wall with that wall's colour. The step-echo pool is 48 voices.
- The listener's own reverb bus is no longer re-placed at a point. In traced mode it blended a
  mono copy of the whole bus at the survey's return direction, weighted by the survey's anisotropy,
  on top of a field that is already round the head: a second, one-point room. Room mode keeps it.
- The diffuse tail shares its bass between the ears below 120 Hz, not 300: a step on carpet is
  nearly all below 300 Hz, and a tail identical in both ears there sits in the head whatever the
  rest does. `--sa-encode` at 120 Hz: correlation 0.92 / 0.23 / 0.19 / 0.55 / 0.30 / 0.29 from
  150 Hz to 4.8 kHz, level per band within 5 dB of the tail's (+4.8 at 150-300).

- The room you are in is round you, not in your head. Its late tail (Steam Audio's parametric
  reverb) is one channel, and one channel decoded is the same signal in both ears: heard inside the
  head or straight ahead, and it stayed there when the head turned ("a consolidating of reverb in
  front of me"). The tail is now rendered as the diffuse field it is: eight copies through eight
  different all-pass chains, each encoded into the soundfield from a fixed direction in the world (the
  corners of a cube round the head) and decoded through the HRTF in the listener's frame, so each ear
  hears eight directions through eight head responses and the fine structure turns with the head.
  Below 300 Hz the tail goes to both ears as it is, which is what a diffuse field is on a head there.
  Measured (`--sa-encode`): interaural correlation 0.90 / 0.22 / 0.20 / 0.56 / 0.30 / 0.29 in the
  bands from 150 Hz to 4.8 kHz, where a head in a real diffuse field measures about 0.9, 0.5, 0.2
  and near 0; level per band within 3 dB of the tail's. `OPENFPS_DIFFUSE_TAIL=0` restores the
  one-channel tail.
- Two calibrations of Steam Audio's ambisonics, measured and taken out: its encoder writes W at
  1/sqrt(4 pi) of the input (measured at creation by running noise through an encoder), and its
  binaural decoder sums its virtual loudspeakers' head responses coherently for a signal that is
  the same in all of them, which is +10 dB below 300 Hz and +8 dB to 600 Hz. The old one-channel
  tail had that on it: every room's reverb carried ten decibels of extra bass, which is the boom.
- The traced stages' Steam Audio effects and buffers are released with their buses (they leaked).

- Steam Audio's world is now a true image of the game's. The scene, every trace's source and
  listener and the probe volume were handed over with the game's z, while the listener's frame for
  decoding was handed over with z the other way (Steam Audio's forward is -z). So every traced
  response was decoded facing the wrong way along z: the wall ahead of you answered from behind, and
  with you facing west the wall to the north landed in the left ear instead of the right. Measured
  by `--sa-frame` (a wall to the left, a wall ahead): X/W -0.49 before, +0.49 after; all four
  checks pass. Everything now goes through Phonon.World. Occlusion, transmission and pathing never
  cared which way was forward, which is why it was never noticed.
- In a room, the far walls answer a sound at arm's length. A copy's audibility was judged against
  the direct sound at its true distance, so for your own clap half a metre from your ear any wall
  past a twelve-metre round trip was dropped as inaudible. In Marlow flat 01F, 8.65 by 17.86 m,
  the end walls were never placed; between the side walls' answers (25 ms) and the omnidirectional
  tail (50 ms) there was a hole, then the tail arrived in the middle of the head at a level 3-8 dB
  over the window before it, and held: "like there's a hallway in front of me". Audibility is now
  judged against the direct sound as heard, never nearer than a metre; the gains are unchanged.
  The end wall ahead is placed at 36 ms and its second and third orders fill the window to 50 ms.
  The twelve loudest are voiced, not the first twelve in surface order.
- Somebody else's footsteps start with the wall between you already on them. A step was submitted
  with no occlusion, and the worker's answer for its pooled id came a tick or two later and eased
  in — after the step was over. Every footfall outside a flat played its attack through the brick.
  The step now takes the simulator's result for the nearest source it heard a moment ago, else the
  hand-rolled tracer, at submission, and reverberates in the room the foot is in.
- Read from the 02:53 capture in flat 01F, for the record: the direct clap is equal in both ears;
  the placed reflections (5-50 ms) swing 4-9 dB between the ears with heading; the tail (50-200 ms)
  is equal in both ears to 0.1 dB at every heading, with interaural correlation near 1 below
  300 Hz falling to 0.2-0.3 above 2 kHz, which is what a diffuse field measures. Its decay is about
  1.1 s. The flat is a bare 8.65 by 17.86 by 2.73 m room with plaster walls and ceiling and one
  sofa, and the trace's decay for it is 0.7-0.8 s. That length is the geometry's.

- Reflections are placed at their true level against their source. Every copy (echo, reflection,
  flutter) was handed to the loudness placement as a quieter sound of its own. The placement keeps
  45 % of a level difference, so a reflection 14 dB down came out 6 dB down. Every reflection in
  the game was 4-8 dB too loud against what it copies. Copies now take their source's placement
  and are scaled by what the surface and the longer path kept.
- The room's tail no longer sits on the left. The ear decorrelator's right-ear chain summed to 32
  samples more delay than the left's, so the tail reached the left ear 0.7 ms first on every sound.
  Both chains now sum to 260 samples.
- Walls stop sound by their weight. Transmission through an airtight wall now follows the mass
  law from its density and the box's own thickness, per band, up to 55 dB. Porous materials
  (fences, hedges, grass, crowds, carpet, acoustic tile) keep their table figures. Each face of a
  box carries half its loss, and Steam Audio now counts up to eight surfaces, so two walls in a row
  are both paid for.
- A route round a wall only counts if it is clear. The barrier search measures one box at a time,
  and when its route ran into another wall its level was kept anyway. Now what arrives is what the
  wall lets through, from the source's own bearing. For an hour Steam Audio's pathing eq stood in
  for the level: that is the colour of the bend, near 1.0 on a 150 m route, so sirens and walkers
  behind walls played at full level from straight below (the probe grid's route), fixed in front
  of the listener whichever way they turned.
- The tower flats' doors have a wall over them and leaves that lap their jambs. The doorway cut ran
  floor to ceiling, leaving a 65 cm slot over every shut door, and the 0.9 m leaf sat in a 1.0 m
  opening. A shut flat door now passes -23/-35/-41 dB, where it passed -7/-11/-19.
- `--path-probe ear=x,y,z src=x,y,z` shows what the occlusion worker hands the mixer.
  `OPENFPS_AUDIO_DEBUG=1` adds the raw visibility, transmission and route.

- A clap's early reflections play on time. They were queued while the clap played and sent a whole
  frame later: every reflection in the log was 20-50 ms late. One due 6-25 ms after the clap
  arrived 45-65 ms after it ("the clapping breaks up").
- A sound that can only get round a building by going 156 m out of its way pays for the extra
  distance. The barrier model's 24 dB ceiling left that route at -24 dB in every band, and it beat
  the wall. Walkers outside Marlow flat 01F now come through the brick at the wall's own figures,
  -24/-30/-36 dB, instead of a flat -24. That is still too loud for 35 cm of brick: wall
  transmission comes from a table per material and does not know how thick a wall is.

- A room is its walls first, then its tail. In the room you are in, a one-off sound's early
  reflections (first to third order, the first 80 ms) now play as their own voices. Each is
  mirrored off one wall and placed there through the HRTF, so they move as you turn. The traced
  stage for that room plays only Steam Audio's parametric tail, built from the decay times the
  trace measured, which starts about 50 ms in.
  The traced response on its own is nearly omnidirectional: in Marlow flat 01F its directional
  channels sit about 20 dB under the omni one. The capture showed the two ears 85-95 % alike after a
  clap ("the room sounds narrow... I turn my head and nothing seems to move").
  Other rooms and outdoors keep the full traced response. Sustained sounds (engines, speech) get
  the tail but not the placed reflections.
- Fixed: the distance scaling added earlier today also applied to sounds entering from outside.
  A lorry down the street was sent into the flat's reverb about 19 dB hot, the mix ran at -8 LUFS
  and clipped.

- A room's answer to a sound close to you is quieter, measured through the whole mixer with a new
  lab instrument (`--clap-room`, a clap in Marlow flat 01F). The room came back 1 dB over the clap
  where physics puts it about 10 dB under. There were three causes:
  - The traced reverb decoded through an HRTF made for 1024-sample blocks while running at 256.
    That made it about 3.5 dB hot and the wrong colour. It has its own HRTF now.
  - A sound in the room you are in was sent into that room twice, once as its room and once as
    yours. It is sent once now.
  - The trace is normalised to a source one metre off, and every sound was sent as if it stood
    there. In a closed room the send is now the arriving sound times its distance. Outdoors it is
    unchanged beyond a metre. The measured enclosure blends the two.
  A clap now has the room 4.6 dB under it at game levels and 6.6 dB under with the limiter out of the
  way.
- Footsteps outside a building no longer come through the wall on their attack. A new one-shot
  started on the old hand-rolled path's guess (for the pavement outside Marlow flat 01F, a route
  through the flat's door at -4 dB) and slid to Steam Audio's answer (-24 dB through brick) after
  the loudest part had played. It now starts from Steam Audio's answer for the nearest source it
  heard a moment ago.
- Rooms no longer sound like a stadium. The ear decorrelator added on 09-28 was itself a reverberator:
  six all-passes of up to 13 ms at a feedback of 0.6 turned every click into 100 ms of build-up
  peaking 20-45 ms late. It sat on top of every reflection the room handed back, flats, the
  stairwell and the street alike. Measured on 52 claps in a capture, the room's answer peaked 48 ms
  after the clap in a flat a few metres across. The delays are now 0.16-2.2 ms at 0.5: 90 % of a
  click comes back inside 9 ms, and the ears stay apart (0.14 interaural correlation).
- The airport terminal's acoustic ceiling was being heard as carpet. AcousticTile had the same
  resonance index as Carpet, and region faces are stored by index. It has its own now, and a test
  checks that no two materials share one.

## 2026-09-28

- A room is heard round you, not in the middle of your head. The traced reverb is rebuilt from an
  energy field, and in a diffuse room that is all omnidirectional, so it reached both ears as one
  signal: measured from a capture in 64 Alder Street, 0.8-0.99 interaural correlation in the tail
  where a real room is 0.1-0.5. Each ear now gets its own all-pass chain above 300 Hz (the bottom
  stays shared, as it is in a real room): 0.07 above 1 kHz, 0.87 below 150 Hz, level unchanged.
- The time between beacon soundings is yours: `/beacons every 3`, half a second to ten, saved.
- The airport terminal has a suspended acoustic ceiling (a new AcousticTile material): about 1.2 s
  of reverb through the middle and 2.4 s at the bottom, where the bare concrete rang for 7-10 s.
- Shift+E knocks on the nearest door: three knuckles on wood, built from shaped noise and fitted to a
  recording of real knocks (within about a decibel per octave).
- New beacon tones: soft sine notes, each kind its own shape. A door is two notes rising, an item one
  small ring, a vehicle a low note twice. Exits, stairs and waypoints have designs rendered for
  listening; the map places those beacons with their own sounds for now.
- docs/LISTENING_SPOTS.md: places on the city to check rooms and reflections, with their /tp.
- The sound no longer freezes in big rooms. The room tracer held its lock for the whole of a trace,
  and the game asked it where you were every frame, so every frame waited out the trace: in the
  airport terminal (a 9-second hall) every sound stood still for 680 ms at a time, the game loop ran
  at 8 Hz, footsteps and claps came late or not at all, and the reverb stepped. Where you are is now
  handed over without waiting, for the room tracer and the per-source echo tracer both.
- Jumping indoors no longer throws you through the wall. Collision only pushed sideways, so a head in
  a house's roof slab was pushed out of the roof's footprint through the nearest wall ("I can jump
  over the edge to get out but I can't jump back in"). A body in the air now meets a ceiling and
  stops rising; standing, a beam at head height is still a wall.
- Standing right outside a building is outside it. The zone lookup fell back to a half-metre grid
  that carries a room into the first half-metre past its wall, so against a house you were inside
  it: the walkers beside you in the room, everything else muffled through walls.
- Houses have their front doors on the street. Every house on the estate had its doorway in the
  garden-side wall and a door standing inside its solid front wall; the back door now has a doorway
  too.
- A room answers when a room would: the traced reverb is convolved in 256-sample pieces, and the
  convolution is one of its own blocks late, so a room's first reflection comes 5-8 ms after the
  sound instead of 20-23 (in a car cabin too). With the mixer's 1,024-sample block every room had
  been a separate space off to one side ("reflections centred not around me"). It is also
  second-order ambisonics now (it was first), so the answer comes from round you.
- Where two named zones overlap you are in the smaller one, whichever order the map lists them in,
  for the name Z says and for the room you hear alike.
- No sports bike on the city: the 600 supersport tried today did not sound like one and is gone,
  preset and all. The litre bike stays in the registry with a stock silencer and its own tyres.
- Turbocharged exhausts are heard: a turbine takes about 6 dB off the pulses evenly and scatters only
  the top, where it had been a 260 Hz low-pass passing a third, so every turbo diesel was rumble and
  turbo whine with the exhaust's bark gone before the pipe. The twin-turbo pickups' exhaust end is
  now 21-25 dB over their engine bays at a cruise, and brighter; every turbo vehicle is louder.
- Loud cars in traffic: four of the city's ordinary cars are now a sport compact, a turbo hatch, a
  V8 pickup on Flowmasters and a mild small-block muscle car (94-104 dB at a metre cruising, against
  the stock cars' 87-89). Measured, the other cars and the buses do not have the pickups' fault:
  their exhausts are balanced, and at city speeds a stock car's tyres are as loud as its pipe.
- A horn is heard from the car's front as it is now, not from where its exhaust was a few frames ago.
- Echoes get duller with each bounce: a surface's roughness takes more of the top than the middle,
  more off brick than glass, again at every surface in a chain.
- Back gardens on the north side of Birch Street no longer run over Central Street's pavement and
  road; with the smallest zone winning, 54 steps of that pavement had become "back garden".
- The sports bike sounds like a small engine: a stock silencer (119 dB at a metre flat out to 101,
  about what a stock litre bike makes) and the engine itself heard. A petrol engine's block now
  keeps getting louder above 6,000 rpm, as measured engines do (Anderton's 50 log N); at a bike's
  eleven thousand the engine is as loud as the pipe. Nothing changes at or below 6,000.
- The twin-turbo pickups have the bigger pipes approved by ear (five-inch Duramax, six-inch Cummins)
  and exit at the side ahead of the rear wheel, kerb side. Out of the rear bumper the truck's own
  body stood between the pipe and the pavement until it had passed.
- An echo of a shot, a clap or a slam is the crack itself, coming from the wall. Every echo went
  through a diffuser, and off steel, concrete or glass (the shortest delays) that rang: a
  "processed sounding" copy. The mirror share of a wall's return now plays the sound unchanged; the
  scattered share still comes from points across the face, smeared, which is what gives the echo
  the wall's size.
- T on foot claps your hands, heard by everyone near and answered by the walls. In the driver's seat
  T is still the key.
- Lab: `--ride <preset>` renders the game's vehicle voice through a stop-go ride to a WAV, with
  exhaust and mechanism knobs for trying variants; `--shot-echoes at=x,z [shot=x,z]` lists every echo
  a shot makes on a real map and what each came off.
- The traffic driver no longer lurches. Pulling away on a light throttle it held the clutch out while
  the engine revved free, then closed it in one step: a bike jumped ten km/h in a tenth of a second.
  The clutch is now let in over half a second whenever the engine and gear turn at different speeds,
  the pull-away throttle is rolled on and eases off if the vehicle is ahead of where it should be, the
  speed loop's integral no longer winds up during an overshoot, and slowing below what first gear does
  at idle puts the clutch in instead of letting the idle drive the vehicle on. The muscle car and the
  dirt bike had the same lurch, less often. `--shift-trace <preset>` in the lab prints gear, revs,
  clutch and throttle through a stop-go drive.
- The sports bike changes up in town. On a light throttle it changed up at 85 % of its 11,000 rpm
  torque peak, so it never left first below 90 km/h. It short-shifts at 5,000 now, changes down at
  3,000 rather than 5,000, and pulls away at 2,800 (`Gearbox.CruiseUpshiftRpm`, `Gearbox.LaunchRpm`).
- A car door that sounds like a car door. Fitted to a recording and approved by ear: the slam is four
  hits over 75 ms (first touch, the latch's two catches, a rebound), the cabin answering underneath,
  and the body settling; opening is the handle, the latch letting go and the check strap's detent a
  third of a second later. Every part is noise shaped per octave band. A first version built from
  resonators matched the band levels and was rejected as sounding like an instrument, which is
  what a few fixed modes ringing for half a second are. Used for every car door, player or driver.
- Tests for what the engine mutation run found unchecked: the scripted driver's brake, gear, throttle,
  launch clutch and shifts, the network driver switching the engine off, the valve solver at the ends
  of its range, and the air compressor's knock. A gear shift now closes the throttle on its first
  sample rather than its second.
- Approved recordings and renders moved out of the inbox into `approved/`, with an index.
- The footstep bank is Cody's two complete Foley packs: fifteen surfaces, walk, jog, run, scuff and
  landing for each shoe recorded, about 14,000 takes, every source file named for what it is. The game
  plays ordinary walking and landings from it now, a hair different in pitch and level each step;
  the rest loads on first use. Grass has its own recordings.
- Quieter arrival: the loading steps (preloading, receiving entities, acoustics) are shown and no
  longer spoken; arriving says "Logged in. You are in <map>." and then the zone. A /tp no longer
  replays the arrival; command replies make no chat sound; map and general chat keep their own
  sounds when an admin talks.
- Shift+P says what is in sight, nearest first, measured to the nearest part of each thing, in one
  line; nothing behind a wall, and not the floor you stand on.
- A siren or horn heard round a building no longer flutters: where it is heard from turns at a limited
  rate instead of sliding through your head when the route round the building switches sides, and its
  level comes from how far away it really is.
- Gunshot echoes at their proper level: each echo was being muffled by the very wall it came off
  (its line from you ran through that wall to the mirror image behind it), 40-60 dB down. The echo
  search's distance limits now count the extra path over the direct one, so far shots echo too.
- The sports bike and the dirt bike rev like bikes: their gearing lacked the reduction between crank
  and gearbox, so at city speeds they lugged like a diesel. The dirt bike's exhaust is half a metre.
- Steve joins the street, and two children wait for the schools.
- More people and more to say: glenn and louis join (27 voices, 11,053 lines). A voice without the
  original named lines speaks from its own lines in the same category. People on their own now and
  then mutter, think aloud, read a text out or remark on the weather or the hour when it is true;
  people near a shot or a leant-on horn react. Half the phone calls are recorded calls played
  through, some go to voicemail; strangers passing make small talk or ask the way. About one walker
  in four on a wide pavement walks with somebody, and the two talk to each other when you are near:
  47 conversations between ten pairs of voices.

- People on foot cross the roads. Wherever a walker's line passes over a carriageway (a side street's
  mouth at a corner) is a crossing, found at load: 90 in the city. A walker stops at the kerb and
  waits for a gap as long as the walk across plus 3 s (the Highway Capacity Manual's pedestrian
  critical gap); drivers stop for anybody on a crossing, and a driver arriving at a junction stops for
  somebody who has waited at the kerb for 8 s. After 30 s a walker takes any gap as long as the walk.
  A driver waiting at a junction stands short of the crossing, not on it. Over five minutes of city
  traffic nobody out in the road had a vehicle over them, the longest wait at a kerb was 40 s and the
  longest any vehicle stood still was 30 s. The timings are map data (`StreetLife`).
- Two junction faults the crossings brought out. A long truck holding at the line was taken to be in
  the junction already, because the smoothed line drifts from the lanes by metres at corners, and
  drove on into a car. And "everybody is waiting, one goes" could pull out in front of a car about to
  arrive; it now waits for anything within 4 s.

## 2026-09-27

- Drivers take turns at junctions. A vehicle already in a junction, or too close to stop before the
  line, has it: nobody enters on a path that crosses or joins its path. A driver giving way arrives
  at walking pace to look and goes only if the priority traffic is further off than the Highway
  Capacity Manual's critical gap (6.2 s turning right, 6.5 straight across, 7.1 turning left, 4.1 for a
  left turn off the priority road across oncoming traffic), counted from when it reaches the line.
  A left turn gives way to oncoming traffic; between equals the one on the right goes first; of two
  side by side, the one further back; and if everyone is waiting for someone, one goes after a few
  seconds. Waiting drivers stand with the front bumper at the line. The fixed two-second give-way
  pause is gone. Over ten minutes of city traffic no two vehicles met inside a junction.
- The city's traffic drives the roads. Each car is a tour of lanes and the turns between them, built
  from the road network at load: a seeded wander, turning at random at each junction, so no two take
  the same way and the city is the same every time. The bus has a fixed route that passes both
  shelters on Main Street the right way and stops at them. Southgate's traffic goes round its square
  and waits at both level crossings. Vehicles give way where their road does not have priority (the
  higher class of road, then the longer one), keep to the kerb lane unless another saves distance,
  keep to each lane's speed limit, and keep a gap to the vehicle in front on the same lane whatever
  their route. The drawn downtown loops are gone; only the railway keeps a drawn track.
- Traffic keeps a gap to the vehicle in front, by the Intelligent Driver Model (Treiber, Hennecke and
  Helbing 2000): it eases off to hold a time headway and stops two metres behind a stopped vehicle.
  Before, no vehicle knew another was there, and over three minutes of city traffic 73 pairs drove
  through each other; now none do. The headway and gap are map data (`StreetLife`). Street maps only:
  the speedway still races.
- `--ground-voice --ladder` in the lab renders a line with its ground reflection at the physical
  level, 6, 12 and 20 dB below it, and with the talker and listener moving as standing people do.
- The city's roads are data. Every road is a record (centreline, type, lanes with a direction,
  width and speed limit, surface stretches) written by the same call that lays its asphalt, and the
  27 junctions are found where centrelines meet. The server builds the network at load: every lane
  cut into the stretches between junctions and where each can turn next, and logs any problem. No
  traffic uses it yet. Southgate's streets, which its traffic had always driven on bare ground, are
  laid (Mill Road, Kiln Street, Tanner Road), and its north side is now Dock Street.
- Short recorded impacts hear the ground: a shot, a door, a knock get the surface's answer from
  their mirror image below it, through an HRTF of their own, worked out from the sound's height, the
  listener's and the surface between them. Sounds made at the ground (footsteps) get none: the
  recording has it. Speech was given it too and flanged, summed into the voice's direction and
  again from below (heard 2026-09-27), so voices have none until the missing part is found (see
  todo). `--ground-voice` in the lab renders a line all three ways.
- Pedestrian voices have less low end where they had too much. Each voice's spectrum below 500 Hz is
  compared with a real talker's at the same pitch (Byrne et al. 1994) and the excess cut: tim by up
  to 11 dB below 100 Hz, linda by up to 8 dB below 160 Hz; the shouting drivers and the lighter
  voices are unchanged. `OpenFPS.Common/Speech/voicing.csv` records what each voice was given.
- Every line is matched by loudness (ITU-R BS.1770) instead of RMS. The voices were up to 2.6 dB
  apart to the ear; now they are level, at the same average as before.
- Pedestrians no longer walk inside walls. The map generator read a prefab with no `IsSolid` as not
  solid, where the server reads it as solid, so the Main Street walks ran through the tunnel's
  concrete sides and a Wharf Avenue walk through a concrete wall. From outside the tunnel you heard
  people walking inside the wall. The generator now uses the server's rule; 304 walks instead of 310.
  A test checks every walk against the server's solid entities.
- The birds no longer freeze the sound for up to a second. They looked for their homes again every
  time a pedestrian or car came into or out of range: 16 rays over every roof on the city, up to
  850 ms at once and about 150 ms every 10-30 s, and the flocks were reset each time. They now look
  again only when the scenery changes.
- C no longer says "-0.0". Walking due south moves x by a hair below zero.

## 2026-09-26

### People in the street talk
- The city's pedestrians speak, using 17 recorded voices (1,462 lines). They greet you as you pass,
  say goodbye as you part, apologise if you walk into them, ask if they can help when you stand in
  front of them, talk on the phone, and greet each other.
- Lines depend on the game clock and weather ("Good morning", "Looks like rain", "Cold out here
  today").
- A line is a world sound from the speaker's mouth, placed, blocked and reverberated like any other,
  and it moves with the speaker while they talk. Levels are the ANSI S3.5 speech levels (62 dB at
  1 m for normal speech, 68 dB raised).
- A talker is duller and quieter behind than in front (about -2, -6 and -13 dB in the low, mid and
  high bands straight behind). Speech gets no discrete echo copies, which made a person in the
  street sound as if they were in a building.
- A voice's tone, air loss and room now update while it plays and moves. Before, only its position,
  level and blocking did.
- Pedestrians greet each other only where a player could hear it (within 40 m).
- Text (MUD) players are told the words of anything said within 10 m.
- Of the three voices in the set cloned from real people, seanterry and jimdale are shipped; ben is not.
- The city has 310 pedestrians, generated from the pavements themselves: every pavement is walked,
  split wherever something solid stands on it, and given a person per 30 m, half each way.
- 25 voices from the 2026-09-27 set: 20 people and 5 angry drivers. joel, seanterry, joeb, ben,
  alec, fluke and camel are handed out twice as often. Eleven people have stories (23 in all), which
  they sometimes tell during a phone call. 40 more everyday lines per voice.
- Every car on the street has a driver with a voice. Drivers yell after a hard stop, at a car coming
  across while they wait at a give-way, at anyone standing in the road ahead (they also brake and
  honk), at a car pulling out in front of them, and when held at a level crossing for 25 s. Yells are
  at shouting level (82 dB at 1 m) from the driver's window and move with the car. The Main Street walks stop at the bus
  shelters, which the old walkers passed through.
- `tools/import_npc_voices.py` imports a new set; `--speech-lines` in the lab decodes every line and
  checks its level.

### Turbos
- A turbo spools as soon as the pedal goes down. On the compound-turbo pickups the spool's target
  was the larger of the idle freewheel and the throttle's share, and the throttle's share only
  passed the freewheel at 1,300-1,700 rpm: the whine held flat pulling away. The throttle's share
  now adds to the freewheel. Turbos with no idle freewheel are unchanged.
- The whine's tip-clearance hump is a narrow band of noise (6% wide) at the power the old sine had,
  not a single line.

### Voices sounded like they were in a room outdoors
- The outdoor traced reverb is traced from the listener's head, so it heard the ground under their
  own feet and handed every sound back 10-15 ms late, 2-7 dB under the direct sound (measured from a
  capture). A close voice with a copy that close behind is a small room. The listener's trace is now
  built without the open ground; sources keep their own ground reflection. Street tails come from the
  facades, first arriving at their real delay (48 ms in a 20 m street).
- `docs/WHO_RENDERS_WHAT.md`: which mechanism renders each part of a sound, so nothing is rendered twice.
- `--traced-reverb` in the lab fails if the listener's trace hands anything back inside 25 ms.

### Your own footsteps
- Your footsteps were drowned out on the busy city: everybody's steps shared one pool of twelve
  voices, taken in turn, and other people's steps took your slot before your step could play. Other
  people's steps now have their own pool of 64, are only made within 15 m, compete for a voice by
  level (only yours are pinned), and no longer take the pool for the echoes of your own steps.

### Sirens and traffic
- A distant siren no longer flutters. Its position was set twice a frame, from two places up to
  300 ms apart, and swung between them. `--siren-route` in the lab drives the police car's route
  past a fixed listener.
- Traffic no longer surges through corners. A vehicle reading the speed limit a braking distance
  ahead saw the corner exit before it reached the tightest point, so it accelerated and then braked
  at every bend; it now takes the lowest limit over the whole look-ahead. Trains too. Most audible on
  the diesel pickups.

## 2026-09-24

### What you hear from far away
- Cars voiced from afar (twelve on the city) now go through the same occlusion, vehicle shadowing
  and air absorption as everything else. Before, they reached the listener unblocked and bright at
  any distance: the white-noise wash heard from the edge of the map.
- A bus lying broadside between you and a car at ear height now blocks it. Before, the route round
  it was computed as a straight line through it.
- An engine's echo is darkened by the air over its own, longer path. Before, it was as bright as the
  car, which could make a passing car seem to be on the far side of the street.

### Echoes and the ground
- The echo of a gunshot, a clap or a door is smeared by the roughness of what it came off, like
  engine echoes: glass returns it almost intact, brick smears it over about 20 ms.
- Open ground returns a short wash after a sharp sound, from the ground round the bounce point (about
  -24 dB at 20 ms and -28 dB at 60 ms for a shot 20 m away over dirt). Before, open ground returned
  nothing. Surfaces are now found by their nearest point, so the city's ground and long facades count
  wherever you are.

### Gunfire
- The gunshot is synthesized to a spec measured from real recordings (the NIJ gunshot dataset): a
  pulse and a short burst that fall 20 dB in 2.5-3.5 ms. The old shot took 18-26 ms. Nothing
  recorded is played; what follows the shot comes from the place it is heard in.
- `docs/GUNFIRE.md` has the measurements and the plan.

### Engines
- Engines breathe only the air that comes past the throttle. Before, with the throttle shut, the
  cylinders drew up to 18 times more, and every engine made power on the overrun. Engine braking
  and idle are now physical; the sportbike reaches its shift point; automatic drivers change down
  when floored. Levels at full throttle are unchanged.
- The road V10 changes gear (its gearbox shifted above the engine's redline).
- Mufflers use the engine's own steepening setting, and the engine voices' soft limiter no longer
  clicks on backfires.

### Doors
- Opening a door depends on the leaf's weight and material: the leaf thumps under the latch, and a
  steel door rings where a wooden one does not.

### The map and the client
- You can no longer walk off the edge of the map. Maps can declare where players can walk
  (`PlayMin`, `PlayMax`); the city's is its built ground. The client says "Edge of the map".
- Shift with `[` and `]` switches chat buffers on Linux.
- Voice chat packets and the mic indicator are no longer cut off as they start.
- Every 5 s the client log lists the sounds reaching you loudest, with level per band and route.

### Testing
- Coverage report (`docs/COVERAGE_2026-09-24.md`) and a first mutation-testing round with Stryker.NET
  (`docs/MUTATION_2026-09-24.md`): 149 new tests, and four real defects found and fixed.

### Horns
- Truck, bus and train air horns swell in and fade out cleanly. Before, they were weak and broke up at the start and end of each blast.
- The low-pressure pitch bend is much smaller (6.5% down to 0.8%), and the valve opens in 25 ms and closes in 30 ms (truck and bus 20/25 ms).
- The horn column rings out when the air stops. Before, it was cut off about 35 dB down.
- A chord horn's bells come in one after another on every blast. Before, this never happened.
- Truck and bus horns have more body (the reed is open for less of each cycle).
- Car horns swell in over about 20 ms and ring out over about 50 ms.

### Chat and menus
- Chat messages carry their channel: map, all, private or server. Plain typing reaches your map. `/all` reaches everyone.
- Command answers are no longer labelled "System". Only messages to everyone are labelled "Server".
- Four chat rings: All, Map, Private and Server. `[` and `]` read messages. Shift with them changes ring.
- Admins can use `/announce` and `/setmotd`. Anyone can use `/motd`. `motd.txt` beside the server is read to each player on their first arrival in a session.
- Interface sounds: a tick when focus moves, a rising tone to select, a falling tone to go back, a chord on entering the world, and a sound for each kind of chat.
- F5 (players), F6 (maps) and F8 (friends) open lists. Up and Down move, Enter or Right chooses, Escape, Left or Backspace goes back, and a letter jumps. You stand still while a list is open.
- A player's menu offers private message, where is, view profile, and add or remove friend.
- Linux main menu: Connect goes to your preferred server. Saved Servers lets you add, edit, remove and set a preferred server. Settings has output device, input device and interface sound volume.

### Travelling between maps
- `/join` moves you to another loaded map without logging out. Choosing a map from F6 does the same.
- The loading screen shows during the move.

### Friends
- `/friend add` and `/friend remove`. F8 says which friends are online.
- `/where` and `/profile` give distance, clock direction and place.
- For hosts: friends are kept in `friends.json` beside the server.

### City
- Pavements no longer say "Outside". The four cross streets have their own pavement regions.

### Street life, birds and trains
- Every road vehicle has a horn that suits it. A honk is sent by the server and played on the vehicle.
- On the city: a honk somewhere about every 45 s, a hard stop with squealing tyres about every 2 min, and a car parking beside a door about every 90 s (engine off, door, driver walks in and out, engine on, drives away).
- Trains sound long-long-short-long before every level crossing. The game never played a train horn before.
- Birds are placed by habitat: sparrows in trees, doves, pigeons and crows on roofs, geese flying over. A loud noise or someone walking up makes them go quiet.

### Reverb and reflections
- Moving vehicles block sound from things behind them.
- Engine echoes are back on. Each echo is smeared by the roughness of the wall it came off, so it no longer sounds like a second copy of the car. `OPENFPS_ENGINE_ECHOES=0` turns them off.
- The room survey is about 40% cheaper (55 ms to 33 ms on Main Street).

### Fixes
- Distant cars and machines no longer stutter or cut out behind buildings. Before, they came back unblocked every five seconds.
- The acoustic simulator no longer falls back to the simpler tracer in the apartment building when too many sources are in use.
- Door beacons are no longer silent when you approach at an angle.
- Steel doors make a sound now. The first time a sound was needed it used to be dropped. The steel door is now a hollow door of about 64 kg instead of a solid 2.4 t slab.
- A police siren no longer plays where its car was twenty seconds earlier.
- Voice chat packets play in full. Before, each sound was released as soon as it started.
- Grandstands on two different maps can no longer silence each other's crowd reactions.

### For contributors
- Dead code removed across the audio and spatial code.
- `dotnet-stryker` added as a local tool for mutation testing.
- `--car-horn` takes `air=`, `bend=`, `rise=`, `fall=`, `tag=`. `--door-opening` renders doors before and after.
- `ClientSettings` is in the shared client core, one file per user.
- Planning notes and an audio engine coverage report.

## 2026-09-23

### Beacons
- A beacon is a short sound that says where something is. Each has a category: door, exit, stairs, item, vehicle or waypoint.
- Maps set a policy per category: default on, default off, forced on or forbidden.
- Players switch categories with `/beacons`, `/beacons door`, `/beacons door on|off`. Choices are kept in `~/.config/openfps/beacons.json`.
- Most beacons come from what a thing is. Every door, item and drivable vehicle is a beacon. The city has 470 door beacons without anyone placing them.
- The nearest few of each kind sound: doors within 12 m, items within 10 m, cars within 25 m.
- Beacons blocked by a wall are skipped.
- Doors knock, items ring a bell and vehicles give a low double tone, so no beacon sounds like a crossing chirp.

### Doors
- 71 doors on the city refitted. Tower entrances, terminal doors and estate front doors now sit in their walls and fill the opening.
- 64 house curtains hung across front doors were moved beside them. You can walk through the door at 24 Birch Street.
- Pressing E beside an open door with nothing to get into closes it.

### Reverb and reflections
- Sounds can reflect off two or three surfaces outdoors, so you hear the flutter between two facades. This applies to one-off sounds only, at most three echoes.
- Sustained sounds (jets, air hiss, sirens, machines) use first-order reflections only. Before, they left ghost washes of noise hanging in one place.
- One-off echoes were delayed twice. A facade's slapback now arrives at 90 ms, not 180.
- Under a bus shelter or in the tunnel, outdoor sounds are no longer muffled. The roof check now applies only inside real enclosures.
- The room survey knows where a small room ends. Bus shelter tail 2.2 s down to 0.9 s. Open-deck garage 5.9 s down to 3.2 s. Other places unchanged.
- The idle engine boost no longer lifts bus air brakes, so they do not carry across the city.

## 2026-09-22

### Driving
- Four parked cars in the garage (hatchback, sedan, pickup, muscle car). You can get in and drive.
- T starts the engine. Shift+T switches it off. The starter motor has its own sound.
- Getting in or out opens and closes the car door beside your seat.
- Steering turns at the pace of hands on a wheel, and at speed the lock is limited to what the tyres can hold.
- Driven cars squeal their tyres.
- The arrow keys work as a second set of WASD.
- `/tp` from a seat gets you out first.
- An idling car is now audible. It was about 11 dB under a window air conditioner at the same distance.
- Getting in tells you whether the engine is running. Getting out tells you if you left it running.

### Driving aids
- Guide beep: a high beep on the middle of your lane ahead. Steer until it is in front. It beeps faster at speed.
- Centre line and kerb: parking-sensor beeps from their side within 1.5 m, a steady tone once you are over. Each has its own pitch.
- The road's name is spoken when you turn onto it, with junctions ahead and their exits, road ends and "Off the road".
- Off the road, the guide beep leads back to the nearest road, and the voice says which road and how far.
- Z says road, heading, lane, speed, and how far you are pointing off the road's line.
- A soft click for every 15 degrees the car turns, and a rising chime when you are lined up with the road.
- Lane assist, on by default, K toggles. It steers to the middle of your lane when you are close to the road's line. Your own steering always wins.
- J and L do nothing in a seat. Your ears face where the vehicle faces.
- Driving cues are now actually played. Before, they were dropped anywhere more than 120 m from the middle of the map.

### Cab sound
- Inside a car you hear the engine through the body: the firing note, not the rasp. Wind noise rises with speed.
- The city outside is filtered by the windows, about 21 to 30 dB quieter.
- Your own car door is no longer filtered by its own glass.
- A car's room is its cabin, not the whole car.
- Your ears are a metre above the seat, inside the car.

### Buses
- A bus that stops at bus stops has seats. City bus 1 has 22. You get the nearest free seat.
- Nobody gets on or off above walking pace.
- On the bus you hear the door beeper, and the street comes in through the open doors.

### Vehicles are solid
- You cannot walk through cars, buses, trucks or mowers. Aircraft and pedestrians stay walk-through.
- Each vehicle has its real size. The school bus is 10.9 m long.
- A car that drives into you pushes you aside.

### Crossings, stops and machines
- Crossing bells ring. Before, the bell played as a nameless sound.
- Mowers follow their real speed, and the engine works harder under load. The verge mower now moves.
- Level crossings and route stops for buses and trains.
- Electronic sirens and a road police car.

### Coordinates
- C and `/tp` now use x east, y north, z height.

### Footsteps
- The footstep sounds are twelve walking recordings, 518 samples across twelve materials.

### Fixes
- A driver was told every tick that they were on foot, because large state updates lost the seat when split. Footsteps while driving, missing cab sound and missing lane cues all came from this.
- A driven car no longer climbs on top of its own floor.

## 2026-09-20

### Trains, mowers, walkers and buses
- Two light rail sets run the city loop.
- Push mowers move back and forth across their gardens. You hear the pusher's footsteps.
- Four people walk the pavements, heard by their footsteps.
- Buses and trucks have air brakes: release, spring brakes and doors at a stop, release when moving off.
- The airliner's whine no longer stops at spool-up.
- Your own footsteps are 8 dB louder.

### Fixes
- The city map no longer crashes the client. A reverb send was being disconnected through the wrong reverb unit. Nothing audible changed.
- Only one game window opens. Before, `/tp` could open another.

### For contributors
- New lab commands: `--foreign-disconnect`, `--send-churn [ownroom]`, `--send-window`, `--send-drift`, and `tools/read_core_dsp.py`.
- `run-gtk-client.sh fmodlog` uses FMOD's logging build.
- Region ids in client logs are server ids, not `city.json` ids.

## 2026-09-19

### The city map
- `./run-server.sh city` runs a city block: two three-storey buildings with flats, a corridor and a tiled stairwell, a street, a tunnel, a two-deck parking garage, a bus shelter and a metro platform.
- Named places measure their own materials from the walls around them when the map loads. Maps do not have to set room acoustics.
- New materials: Brick, Asphalt, Tile, Foliage, Plaster, and soft furniture.
- The flats have carpet, plasterboard and furniture, and sound furnished. The stairwell is bright and ringing.
- The city is no longer cold enough to turn footsteps into snow.

### Reverb and reflections
- Rooms now have real reverb tails. The reverb unit had been set to early reflections only. A car park now rings for seconds, a corridor for under half a second.
- Your own footsteps reverberate in the room you are in. Before, they went to the outdoor reverb, so every room sounded the same.
- The reverb level is no longer pulled down in live rooms. Before, a car park's tail was 7 dB quieter than it should be.
- Room surface area is measured, not assumed to be a cube. The garage was 9 dB too reverberant.
- Reverb fades between rooms instead of jumping, and walking past a doorway no longer pops.
- The tunnel, stairwell and metro platform get their full reverb tail back.
- Footsteps get reflections from nearby walls, heard from the wall's direction.
- Echoes arrive after the sound, not at the same moment. Before, they stacked on top of it as one bang.
- Reflections no longer feed the reverb as new sources. This removed a pop on each footstep indoors.
- The bus shelter is no longer a room.

### Movement and footsteps
- Stepping down a kerb no longer counts as a landing. The periodic bangs while standing near buildings were landings.
- No landing sounds on arrival at the spawn point.
- Step length grows with speed, like a real walk. Running no longer sounds like nine steps a second.
- Footsteps are louder: 68 dB instead of 55.
- Every tap of a movement key plays a footstep, even a very short tap.
- Asphalt, brick and foliage use the nearest recorded footstep material.

### Keys
- The trigger is Enter. Control fired the rifle, and screen reader users press Control to stop speech.
- No game action may be bound to Control or Alt. A test checks this.
- An admin firing with empty hands is no longer given a rifle. `/fire akm` still works.

### Breathing
- Breathing is no longer played. The breathing model still drives the exertion readout ("Breathing hard", "Winded").
- Air hiss (breath, door seals, air brakes) is now broad turbulence without a sharp start.

### Sounds
- Clapping matches a real recording: brighter and faster than before.
- The megaphone no longer repeats its announcement.
- Scheduled delays and fades now work. Before, every reflection played in sync with its source.
- Footsteps no longer hit the master limiter.

### Machines
- Push mowers, ride-on mowers and air conditioner units, built from their parts. A mower slows in thick grass and recovers.
- Fans make broadband blade noise as well as tones. Aircraft are unchanged.

### Reflections and sound paths
- Early reflections come from the map's own surfaces. How reverberant a place is comes from a survey of the space around you.
- A blocked sound is heard from the edge it bends around.
- Voices are no longer virtualised by FMOD, which caused popping.
- The server takes `--map <id>`.

### For contributors
- New lab commands: `--tailcheck`, `--enclosure`, `--walk`, `--room-walk`, `--breath`, `--applause compare=DIR`, `--yard`.
- `OPENFPS_AUDIO_DEBUG=1` adds `[FOOT]` and `[WAUDIO]` trace lines. `run-gtk-client.sh capture` sets it.
- `OPENFPS_WEATHER` pins the weather for listening tests.
- The map is generated by `tools/gen_city.py`.
- Planning notes.

## 2026-09-18

### Trains, horns, bells and air
- Trains built from their parts: wheel and rail rolling noise, clatter over rail joints, and curve squeal on tight curves.
- Diesel, electric and steam locomotives.
- Air horns, steam whistles and bells.
- Air brakes, doors and dryers as air escaping through a hole.
- A train is a line of sources, one per bogie, so its level plateaus as it passes and the clatter sweeps along it.
- Trains, horns, whistles, bells and air systems are data files. A map can replace one by name.

### Aircraft
- Jet and propeller aircraft, built from their parts: blades, jets, combustor rumble and whine. Piston aircraft use the car engine model with a propeller load.

### Engines
- Jet noise follows one law for every vehicle. The 2.8 diesel no longer has 15 to 20 dB of hiss at speed.
- The F1 car's two tailpipes are two sources. It no longer sounds like a siren.
- Rough behaviour between 12,300 and 13,300 rpm on the F1 repaired.
- Two F1 cars are back in the speedway field.

### For contributors
- New lab commands: `--aircraft`, `--crossing`, `--models`, `--models export=DIR`.
- `azimuth=`, `dist=` and `pipe=` on `--engine-orders` and `--engine-gallery`.

## 2026-09-17

### Footsteps
- The footstep model is calibrated against real concrete recordings. The worst band is now 9.9 dB off, down from 32. The game still plays recorded footsteps.

### For contributors
- `tools/split_footsteps.py` cuts a walking recording into one file per step, without normalising each step.

## 2026-09-16

### Speedway
- The speedway names its places: Front straight, Turns one and two, Back straight, Turns three and four, Infield and Grandstand.
- The far turn now has acoustics. The map's bounds did not contain it.
- A place is announced when its name changes, not each time you cross into another part of it.
- The field is nineteen machines, including three kinds of motorcycle and diesels with and without turbos.
- The grandstand crowd can now be heard from the spawn.
- The crowd reflects off the stands as a soft wash, not a sharp copy.
- Naming a place no longer makes it sound indoors.

### Engines
- Turbocharged engines have a compressor and a turbine. The fixed tone on the school bus when lifting off is gone (26 dB down).
- Diesels have ignition delay, clatter at idle and go smooth under load. Knock pitch depends on the cylinder size.
- Intakes have throttle hiss, so the F1's airbox resonates.
- New engines: 5.9 Cummins and the International DT466 school bus.
- A car is heard from both ends, exhaust and intake, when you are close.

### Sound priority
- Sounds are ranked by how loud they will be at your ear, not by type. A distant clap no longer cuts out a nearby car.
- A car that loses its voice fades out instead of stopping.
- A distant car no longer gets two Doppler shifts.

### Sounds
- Clapping sounds like hands. Bigger hands are deeper and louder.
- A sound that cannot get a 3D voice no longer jumps to full volume.

### Machines as data
- Vehicles are parts lists. Hosts can add machines in `machines/*.json` beside the maps.

### For contributors
- New lab commands: `--machines`, `--machine-pass`, `--intake-ir`, `--engine-alias`. The rev bench holds its rpm and reports a `structure` column.
- The Room: log line says what the reverb is doing.
- A mechanism-based footstep model exists in the lab (`--footsteps`). It is not used in the game.
- Planning notes.
