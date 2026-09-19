# What comes next: the city (2026-09-19, after session 15)

The rooms map was approved by ear on 2026-09-19 — *"I think we've finally solved this problem... the
map does sound a lot better"* — after the faults recorded in `docs/REPEATS_AND_POPS.md`. This is the
plan agreed at the end of that session: what was discussed, what Cody wants the next map to contain,
what already exists for each of those things, what does not, and the order to do it in. It supersedes
the order in `docs/VOICES_MACHINES_AND_THE_CITY.md` §7 only where it says so.

## 1. What was discussed, in the order it should go

1. **Commit.** Done with this document.
2. **Build a map.** The acoustics now fall out of boxes, materials and the survey, so a real map is the
   test that finds what is still missing. Everything guessed at from here, a map shows.
3. **The architectural piece still owed: fused early reflections.** An image-source arrival inside
   the fusion window (50 ms) contributes nothing except its share of the diffuse tail. That is the gap
   behind "behind the megaphone" and "the room heard through its door", and it caps how good a
   building can sound. Two ways to close it:
   - *Cheap:* render fused arrivals as delay taps on the source's own signal (the engine-echo ring
     buffer already does this for synthesised engines; a sample voice needs a tap of its own).
   - *Designed:* Steam Audio's reflections stage rendered by convolution — an ambisonic impulse
     response per source, decoded binaurally, so early reflections and the tail arrive from the right
     place at the right time by construction, and the listener's reverb is "a source placed at the
     listener" (C API guide, "Reverb"). The engine links it (`SteamAudioSimulator`, `IPLReflectionEffectParams`)
     and once used only its decay number. It costs CPU per source; it would be for the nearest handful,
     measured with `--room-walk` before it is trusted. **Recommendation: try this on the rooms map
     before the city.** If it works it replaces three hand-built mechanisms (image-source voices, the
     region reverb bus with its doorway swing, the near-field comb) with one.
4. **Small things while fresh:** the rest of the near-field comb double-counts image sources for walls
   beside you — retire it if walking along a wall still colours the mix; thick carpet, curtains and
   crowd barriers are material entries, not code; the stride model makes no step for a short tap
   (0.5 m per stride above 0.5 m/s) — Cody's call whether a tap should always answer.
5. **Then the agreed order:** aggregation (§3 of the earlier doc — what makes a city fit in 96
   binaural voices), then aircraft and trains into the game, both of which the city needs.

## 2. The city Cody wants

> tall apartment buildings with insides, not solid blocks; doors; planes overhead, high up; buses
> (air brakes), cars, tractor-trailers; NPCs that walk (no speech, but footsteps); intersections,
> bus stops; the occasional gang fight with gunshots; a metro station with an overhang where the
> light rail stops for people; tunnels with roads through them; two-way traffic obeying the rules;
> a parking garage; maybe an airport with take-offs and landings; zones naming everywhere you walk;
> crowds, air conditioners, lawn mowers from recordings he will gather. The most complex map the
> engine can carry, as a demo of what it can do.

For each of these: what exists, what is missing, and the mechanism.

| Wanted | Exists | Missing | Mechanism |
|---|---|---|---|
| **Apartment buildings with insides** | **DONE 2026-09-19.** `tools/gen_city.py` builds them: floors, corridors, stairwells with climbable steps, flats, doors. `MapManager.SurveyRegions` runs the survey at static map load, so a region says only where a place is and what it is called | Nothing. Lifts, and flats with anything in them | `CompositeAcoustics.SurveyBox` — the same survey asked of a box that is already known, because a map's walls are SHARED and deriving a box from them gives you the building. |
| **Doors** | `DoorComponent`, `/doors`, open/close on the server; the latch is a synthesised transient through `WorldAudioPlayer`; a door leaf is a box the diffraction model bends round | Doors placed in buildings by the prefab; a closed door as an occluder with its material's transmission | Nothing new: place them. |
| **Planes overhead** | `docs/AIRCRAFT.md` — jets, turboprops, pistons, rotors from the mechanism, approved by ear; the flyover renderer `--aircraft` | Not in the game: no aircraft entity, no flight path, no spawner | An `AircraftSystem` like `VehicleSystem`: a great-circle line at altitude, the aircraft's own Doppler and air absorption (both already general). The renderer is the machine; it needs a body in the world. |
| **Buses with air brakes** | `Pneumatics/` — air brakes approved by ear (`signals-and-air` memory); machines as parts lists in data (`machines/`) | A bus preset (engine + air system) and a *stop* — the air brake needs a vehicle that decelerates to zero at a place | Depends on traffic AI (below): a bus is a car with a route and stops. |
| **Cars, tractor-trailers** | `VehicleSystem` drives a racing line; engines by preset; two-voice car; tyres, body, exhaust | A truck preset (big diesel, air brakes, trailer as extent); **any concept of a road** | Traffic AI (below). Extent already exists for a long vehicle. |
| **Two-way traffic, intersections, rules** | Nothing — `VehicleSystem` follows one line with no stopping, giving way or speed limit | A road graph: lanes as directed polylines, junctions with right-of-way, stops. Vehicles follow lanes, queue behind each other, stop at signals | This is the largest single piece. Author roads as data (`Roads` in the map JSON), a lane-follower on the server replacing the racing line for city vehicles. |
| **Tunnels** | Boxes; the survey measures enclosure from any boxes; diffraction round edges; air absorption | Nothing specific — a tunnel is a roofed box with two openings | Place it. It is the best test the survey has: enclosed, hard, long. |
| **Metro station, light rail stopping** | `docs/TRAINS.md` — trains as a line of bogies, horns, bells, air brakes, approved by ear | Not in the game: no train entity, no track that stops at a platform; the speed law is not declared (`rail-model` memory) | A rail line with a station: the train decelerates, the doors and brakes sound, dwells, departs. Shares the "route with stops" machinery the bus needs. |
| **NPCs that walk** | `OtherBodies` — every body gets a stride and footsteps by material; breath | A pedestrian AI: pavements, crossings, destinations; and **aggregation**, or a crowd of walkers spends the whole voice budget on feet | Pedestrian paths as data like roads; a wanderer that walks them. Aggregation before there are more than a few. |
| **Gunfights** | `WeaponSynth`, `Loudness` (rifle 159 dB, supersonic crack, casings), `WorldAudioEvent` transients, occlusion and diffraction for loud far sources | An event scheduler: "somewhere in this district, now and then, a short exchange" | A `WorldEventSystem` on the server: pick a place, fire a burst of world audio events. The sounds exist. |
| **Bus stops, overhangs, parking garage** | Boxes and materials; an overhang is a roof box, a garage is a low, hard, open-sided room | Prefabs for them | Author. The garage is the second-best survey test after the tunnel. |
| **Airport** | Aircraft models; extent for a distant runway | Flight phases (take-off roll, rotation, climb) as a path with a speed profile | After planes overhead work. |
| **Zones everywhere** | Regions name places; naming costs nothing since session 9 (`region-is-not-a-room`); F6 lists, C reads position | A zone under every pavement, junction, platform, floor of every building | Author, generously. |
| **Crowds, AC units, lawn mowers** | Crowd = `Applause` (claps only until recordings). **AC units and mowers are DONE 2026-09-19** — `docs/YARD_MACHINES.md`, `SmallMachineSpec`, `--yard`: a governed engine with a blade in a pan, and a fan with a compressor in a box | Recordings Cody will gather for crowds (`docs/SOUND_INVENTORY.md`); and the two machines are **not in the game yet** — a stationary machine needs the client voice path a vehicle's engine has | Recordings for crowds and voices; the voice path for the machines, with the city block. |
| **AC units and mowers, placed** | `SmallMachineSpec` + `SmallMachineSynth`, measured and scripted (`--yard`) | A client voice path for a machine that stands still: a render-pool voice, a place, an extent, a level — the treatment `ClientAudioSystem` gives a vehicle's engine | A condenser on the garage roof and a mower behind the west block, once a stationary machine can be given a voice. |
| **Synthetic footsteps** | `FootstepSpike` renders walks from foot and ground (`--footsteps`); 21 files in `/tmp/openfps-footsteps` for Cody to judge | Cody's verdict. The model failed the ear once (`synthesis-failures` memory) and was recalibrated against a recording | If they pass: `SoundMappingService` resolves footsteps from the model instead of the file bank, per material and shoe. If not: keep the recordings and use the synth for the materials that have none. |

## 3. The order for the city, and how to know each step worked

1. ~~**Static city block**~~ **DONE 2026-09-19 — `docs/THE_CITY_BLOCK.md`.** `./run-server.sh city`:
   397 entities, 81 named places, two three-storey blocks with flats and climbable stairs, a 30 m
   tunnel, a two-deck garage, a bus shelter, a tiled metro platform under a canopy, street trees.
   Nothing on it authors an acoustic anything — `MapManager.SurveyRegions` measures all 81 places at
   load (60 come back rooms), which is the "automatic, with the entity as an override" item that had
   been open since session 9. Four materials added for it: Brick, Asphalt, Tile, Foliage.
   *Still to do on it:* **walk it and listen.** Every number is measured and none of it is heard —
   `capture` and `--room-walk` in the tunnel, the garage and a stairwell.
2. **Fused early reflections** (§1.3) tried on this block, because a corridor and a stairwell are
   where the difference between "a tail" and "the walls answering" is largest.
3. **Roads as data + lane follower** replacing the racing line for city vehicles; one car each way;
   then a junction with right-of-way; then a bus with stops; then a truck.
   *Check:* the `Cars:` log line; a pass-by captured on the pavement; the air brake at a stop.
4. **Aggregation**, then pedestrians on pavements with footsteps, then a crowd at the platform.
5. **Rail:** the light rail on its own line, stopping at the platform.
6. **Aircraft overhead** on a high line; then the airport if wanted.
7. **World events:** the occasional gunfight.

Each step ships with its instrument: the capture, the room walk, or a new spike where none fits.

## 4. What this map will tell us that nothing else can

- Whether the material table is rich enough (brick, glass, foliage, asphalt, tile, steel).
- Whether rooms can be found from geometry at load (they should be) so that a building is boxes and
  doors and nothing else.
- Whether 96 binaural voices are enough for a street, which is the question aggregation answers.
- Where the wet/dry law (`Enclosure.ReverberantToDirectPower`) is right and where it is not — a
  street between tall buildings is the case it has not been heard in.
