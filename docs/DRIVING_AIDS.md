# Driving aids and controls

Design for the four "Vehicles" items in todo.md (2026-10-06): horn and siren keys, driving cues,
aircraft roll-out, level crossings. Written before the code; "As built" at the end says what changed.

## What exists

- `DrivingAids` (client) works from the asphalt boxes under the car (`LaneGuide`):
  - a guide beep on the middle of your lane ahead (1175 Hz, rate follows speed);
  - parking-sensor beeps for the centre line (660 Hz) and the kerb (220 Hz buzz) within 1.5 m, a
    steady tone once over the line;
  - a click per 15 degrees of heading, a chime when lined up with the road;
  - speech: road name, "junction in N metres" with the exits, "road ends", "off the road";
  - lane assist (K): pure pursuit to the guide point.
- Nothing warns about speed. Nothing says when to brake for a junction turn, a give-way line, a
  closed level crossing or the end of a road. Cody overshoots the road at junctions.
- The road network (`RoadNetwork`: centrelines, lanes with speed limits, junctions with give-way
  priority) is loaded only on the server. The client has only the asphalt boxes.
- Horns travel as one-shot honk rhythms (`Honk.Key`, street life). Sirens are driven only by
  `SirenController` from what the car is doing; nothing on the wire carries them.
- Aircraft are shuttles. An approach brakes in the air to a stop at the runway point, spins round in
  place and climbs back out.
- Level crossings ring their bell and hold traffic. There are no gates and no sound of tyres over
  the rails.

## 1. Horn and siren keys

Keys (driver's seat only; none uses Control or Alt):

| Key | Action |
|---|---|
| H, held | Horn: sounds while held |
| U | Siren on / off (vehicles that have one) |
| Shift+U | Next siren tone: wail, yelp, phaser |

H on foot still says your health. In the driver's seat H is the horn; Shift+H says health there.
U and Shift+U are not bound anywhere else.

Wire:
- `ClientInputUpdate.Horn` (appended bool): the horn key is down. Sent with every input packet, so
  the horn follows the key with no separate message.
- `TextCommand "siren"` (`/siren [on|off|wail|yelp|phaser|next]`) and `"horn"` (`/horn`, a half-second
  tap, for text sessions).
- `EntityState.Signals` (appended byte, packed only when not zero): bit 0 horn, bit 1 siren on,
  bits 2-4 the siren tone, bit 7 "signals are the driver's". A vehicle with a drive seat always has
  bit 7 set, so its siren follows the switch even with nobody in it; traffic leaves it clear and
  keeps `SirenController`.
- Server: `VehicleSignals` holds the switch state per vehicle. The horn is let go when the driver
  leaves the seat or stops sending input.

Client:
- The held horn plays the vehicle's own horn model (`VehicleProfile.HornFor`: electric disc pair, air
  horns on trucks and buses) through `HornVoiceState` with a "hold" rhythm: blowing while the voice's
  `Running` is set. Release lets the model's own valve and relay envelope finish.
- Horn and siren are placed at the front of the body from the profile's length (behind the grille),
  not a fixed 1.9 m: a bus's horn is at its nose, 5.7 m ahead of its centre.
- From the driver's seat both are outside sounds heard through the cabin glass, like everything
  else outside.

## 2. Driving cues

### Principles
- One cue, one meaning, one kind of sound.
- Physical where the real car gives a cue (tyres on lines, rails, kerbs); informative where it
  does not (how hard to brake, the speed limit).
- Every cue can be switched off. Levels are set under the engine and traffic (checked in renders).

### Road data on the client
`MapRoads` (new message, sent before `MapLoadComplete`): the map's roads, junctions, level crossings
(with the rail direction and gauge, derived on the server from the rail lines) and drivable
(non-rail) tracks, as JSON. The client builds the same `RoadNetwork` the server uses. Maps without
roads fall back to the asphalt boxes for everything the boxes can answer.

### The planner (`DrivingCuePlanner`, Common, no audio)
Each update, from the car's pose, speed, preset and the water under its wheels:
1. Locate the car on a lane segment (nearest lane path within half a lane, same direction).
2. Build the path ahead (to 4 s of travel or 40 m, whichever is longer): along the lane, through
   the next junction, into the next lane. At a junction it goes the way the indicator says (J left,
   L right), else straight on, else the only way out. The turn is a quadratic curve from the lane's
   end to the next lane's start (tangent at both ends), sampled every 0.5 m.
3. On a track (speedway) the path is the track's own line at the car's offset.
4. Targets along the path, each a speed at a distance:
   - bends and turns: `DriverSteering.ComfortTurnSpeed(curvature)` (AASHTO comfortable side friction,
     the same speed traffic uses);
   - a give-way line where your road gives way: 4 m/s at the line (15 km/h);
   - a stop control or a closed level crossing: 0 at the stop line;
   - the end of a road: 0 two metres before it;
   - the lane's speed limit, everywhere.
5. Needed deceleration: the largest `(v^2 - v_target^2) / (2 d)` over the targets ahead.
6. Available grip: `PeakGripG * g * RoadWaterLaw.GripFactor(...)` with the water and surface the
   server sends for the wheels, so a wet road raises the cue.
7. Braking ratio `r` = needed / available. 0.15 is the start of a comfortable lift, about 0.35 is
   ordinary braking, 1 is everything the tyres have.

### The sounds
- **Guide beep** (existing): now sits on the planned path, so it leads into a bend or the indicated
  turn at the look-ahead distance for the speed you are going. Its direction is the line to steer.
- **Brake cue** (new, informative): a short tone placed toward the hazard ahead. Silent below
  r = 0.15. Above it the pulses come faster and higher as r rises: 0.15-0.35 slow and low ("lift"),
  0.35-0.7 faster ("brake"), 0.7-1 fast and high ("brake hard"), over 1 continuous ("you will not
  make it"). It is the Forza braking line turned into time.
- **Line rumble** (new, physical): a wheel on the centre line plays raised pavement markers (a clack
  every 1.2 m of road); a wheel on the kerb-side edge plays a milled rumble strip (a groove every
  0.3 m). From that side, at the wheel, at a rate that is your speed. It replaces the steady "over the
  line" tone. The approach beeps stay.
- **Speed limit** (new, informative): the limit is said with the road name ("limit 50") and in the Z
  readout. Over it by more than 5 km/h, a soft two-note fall plays once, and again every 10 s while
  you stay over.
- **Give-way and crossings** (speech, existing voice): "Junction in 40 metres, give way" when your road
  gives way; "Level crossing in 60 metres, closed" while its bell rings.
- **Indicators** (new): J and L switch the left and right indicator; again switches it off. The
  flasher relay ticks in the cabin at 1.5 Hz (SAE J590: 60-120 flashes a minute). It cancels itself
  once the car has turned more than 45 degrees and straightened, as a steering column does.

### Keys and settings
| Key (driver's seat) | Action |
|---|---|
| J / L | Left / right indicator on or off |
| K | Lane assist on / off (as before) |
| Shift+K | All driving sounds on / off (speech stays) |

Settings, one checkbox each, saved in client.json: guide beep, line sensors and rumble, turn clicks,
brake cue, speed warning. `/drivecues` in game lists them and switches one by name.

### Measuring it
A scripted drive in the tests: a driver model that hears only the cues (reaction time 0.8 s, brakes
at the ratio the cue names, steers for the guide beep with a lag) approaches a right turn at 50 km/h,
with and without the brake cue. Measured: the speed at the turn's entry and how far outside the lane
the car runs. A drive that brakes only on the spoken "junction in N metres" is the comparison.

## 3. Aircraft roll-out

A shuttle whose far end is on the ground is an approach. The new sequence, all kinematics from the
aircraft's preset:
1. Approach at `ApproachSpeedMps` down the line (no braking in the air).
2. Touchdown where the line meets the ground.
3. Roll-out along the runway heading at a deceleration from the declared landing ground roll:
   `a = Vapp^2 / (2 * LandingRollMetres)`.
4. Below taxi speed (`TaxiSpeedMps`), turn round on the runway in a 180-degree arc of the aircraft's
   declared turning radius at taxi speed, and stop.
5. Wait, then the take-off roll back up the runway: acceleration from the declared take-off ground
   roll and rotation speed (`a = Vr^2 / (2 * TakeoffRollMetres)`), lifting off at the touchdown point
   and climbing out up the approach line.

The client reads the power lever on the ground from what the aeroplane is doing: accelerating on its
wheels is take-off power, decelerating fast is reverse thrust, taxiing is idle plus a little.
Figures are sourced per preset (POH and manufacturer airport planning documents); the helicopter
does not roll out.

## 4. Level crossings

### Tyres over the rails
A planked crossing has a flangeway gap beside each rail head, and the panels and rail are never quite
flush. Each wheel crossing each rail is a strike: the tyre meets a step of a few millimetres and
a 65 mm gap. Modelled as an impact on that wheel through its own tyre path (outside: its axle's tap
with the wheel's distance gain; inside: its arch's cabin path):
- force pulse length = contact patch length / speed;
- the tyre's response: the tread band's radial mode (80-100 Hz), the air cavity mode
  (`c / (pi * mean diameter)`, about 200-230 Hz on a car tyre) and a short slap above 1 kHz;
- level from the step height and the wheel's load.

The client schedules the strikes itself from the rail lines in `MapRoads` and the car's wheel
positions, for every vehicle with an engine voice. Strikes are scheduled a voice-lead ahead, so the
rhythm (front wheels, a gauge later, the rear wheels a wheelbase later) is exact.

### Gates
A gate per road approach, on the traffic's side, 4.5 m from the near rail, derived from the roads
through the crossing (nothing new declared on the map). Server-side the gate is an emitter
"gate:<mechanism>" whose `SynthRunning` is the crossing's closed state, like the bell. The mechanism
model (Common `GateSpec`, client `GateVoiceState`):
- arm starts down 4 s after the bell (49 CFR 234.223: no less than 3 s), down in 12 s, up in 10 s;
- a DC gear motor (commutator buzz and a gear mesh tone, both following the arm speed);
- the arm landing on its rest at the bottom and the stop at the top.

Traffic already waits at a closed crossing; the gates do not change that.

## Order of work
1. Horn and siren. 2. Road data on the client and the planner. 3. The cue sounds, keys, settings.
4. Crossings: strikes, then gates. 5. Aircraft roll-out. 6. Renders in
`inbox/driving-2026-10-06/` with a README and measurements; MANUAL and changes.

## As built (2026-10-06)

Built as designed, with these differences and findings:

- **Keys.** As in the table above, plus J/L for the indicators and Shift+K for all driving sounds.
  J, K and L were already idle in a seat (they turn and look on foot, which a seat ignores), so the
  three right-hand keys are the indicator and lane-assist cluster.
- **The turn.** Traffic drives the quadratic curve between lane ends (LaneRoutes.Connector). At the
  near corner of a city junction, kerb lane into kerb lane, that curve has a radius of a metre or
  two, which no car can make, and its comfortable speed (10 km/h) made the brake cue ask for far more
  than a driver needs. The planner's turn (DrivingCuePlanner.TurnCurve) is an arc tangent to both
  lanes no tighter than the car turns (6 m; 1.6 times wheelbase over the tangent of full lock for a
  long vehicle), starting before the lane's end when the junction is too small for it. Traffic is
  unchanged.
- **The guide in a turn.** The guide at its straight-road distance (8 m + 0.8 s of travel) cut a
  6 m turn by five metres in the drill. Round a tight turn it now sits no further along the line
  than 0.7 of the turn's radius.
- **The turn under the car.** How hard the turn the car is already in is working the tyres comes
  from the car's yaw rate, not from the planned line: a car a metre off the middle of its lane put a
  kink in the first metre of the line and the cue read it as a hairpin.
- **Bands.** Lift from 0.12 of the grip, brake from 0.25 (about 0.24 g dry, ordinary braking for a
  junction), hard from 0.5, near the limit from 0.85 (DrivingCueBands).
- **The drill** (DrivingCueDrillTests): a driver who hears only the cues, into the right turn from
  Main Street onto Central Street, indicator on.

  | Approach, road | Speech only: into the turn, grip used, over the centre line | Brake cue |
  |---|---|---|
  | 40 km/h dry | 25 km/h, 93 %, 0 m | 21 km/h, 60 %, 0 m |
  | 40 km/h wet | 25 km/h, 100 %, 0.3 m | 20 km/h, 72 %, 0 m |
  | 50 km/h dry | 25 km/h, 93 %, 0 m | 23 km/h, 53 %, 0 m |
  | 50 km/h wet | 25 km/h, 100 %, 0.3 m | 19 km/h, 55 %, 0 m |
  | 65 km/h dry | 40 km/h, 100 %, 1.8 m | 21 km/h, 61 %, 0 m |
  | 65 km/h wet | 40 km/h, 100 %, 25.8 m | 19 km/h, 53 %, 0 m |

  "Speech only" brakes at 0.2 g down to 25 km/h a second after "Junction in N metres". It is a model
  of a driver: it shows the cue carries what is needed, not how a person will use it.
- **Rails.** Strikes are scheduled by the client for every vehicle with a voice of its own, from
  the crossings in MapRoads; a rail is a step of 6 mm (an assumption: no measured figure for a tyre
  over a crossing was found), 100 dB peak at a metre for a car wheel at 30 km/h.
- **Gates.** The motor (62 dB at a metre) and the clunk (86 dB peak) are assumptions to be judged by
  ear. The gate is a sound and a point; it has no arm in the world and does not block a player's car.
- **Main Street** crossed the rails at grade with no crossing declared. It has one now.
- **Aircraft.** No aircraft are on the city map at present (Cody, 2026-09-25). Its runway is 588 m:
  enough for the light single (lands in 175 m, needs 293 m to take off), not for the turboprop
  (600 m and 1,000 m) or the airliner (1,100 m and 1,800 m).

### Heard in the renders, not changed here
- A car's electric horn rises about 7 dB over the first second and a half it is held, from the kerb;
  the street-life honk does the same. It is the approved horn model (2026-09-24).
- From the driver's seat a horn's first 0.15 to 0.3 s comes through about 16 dB louder than the rest,
  across the spectrum; the street-life honk does it too, for half as long. It looks like the mixer
  catching a very loud close source late, not the horn.
