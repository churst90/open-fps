# Changes

Recent work, newest first. `git log` has the rest.

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
