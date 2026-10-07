# Common notes

The reasoning behind OpenFPS.Common code that has no other doc of its own. The code points here.

## Reverberation

Outdoors is dry in a field and not in a street. A concrete canyon between two rows of tall buildings has
a measurable reverberation time, and the slapback off a facade a hundred metres away tells a blind player
the street has sides and roughly where they are. The Sabine estimate for "the outdoors" takes the whole
map as one room, returns an enormous number and washes the world in undirected reverb; that is why the
outdoor bus is muted.

Steam Audio's ray-traced RT60 gets the time right but not the level. The estimator fits a curve to
whatever energy its rays bring home and cannot report that there was hardly any, so a roofless yard fits
a longer tail than the same walls with a roof on (1.00 s against 0.60 s, AudioLab --sim-reverbfield). No
threshold on the decay separates places on the same side of it, so the level of the tail comes from how
enclosed the place is, measured directly (Enclosure), never from the decay time.

Measured along the way, when the outdoors still had a decay cap (1,100 ms) and a full-wet ceiling (0 dB):

- The ray tracer gave 1.7 to 2.8 s for a concrete street canyon. Real streets measure nearer a second,
  because the sky is an infinite absorber and the tracer's rays do not all find it.
- Enclosure.Look treats a direction that hits nothing as a perfect absorber, so the sky is in its
  measurement: a street on the city map reads 525 ms and a pavement 627, with no cap.
- A cap applied to every region without a Sabine estimate silenced the places that should ring: a roofed
  tunnel measuring three seconds would be served 1.1 and sound dry. A car park's upper deck measures
  4.5 s, a tiled stairwell 2.
- A full-wet ceiling held 16 dB down put a hard-walled courtyard at -35 dB: audible in a meter, not in
  the ear. The level of the diffuse remainder is the fraction of emitted energy that comes back, about
  one percent for open ground and nearly all of it for a sealed hard box.

The reverb unit's own wet level is a constant (FMOD's internal scaling against unity, a property of the
DSP and not of the place). Never meter the unit and hold its gain at unity: a reverberation unit
accumulates energy in proportion to its decay, so such a loop trims a live room by exactly as much as it
is live, six decibels for a corridor against thirteen for a seven-second hall, and cancels the rooms.
Minus six is where such a loop settles for a mid-sized room.

The unit's synthetic early reflections are off. Early reflections are a fact about the geometry (which
wall, how far, what it is made of) and the image-source pass measures them per source; a reverb unit's
are a fixed pattern of copies stamped onto every transient. Measured on a footstep in the wood room: with
them the step peaked 9 dB louder than dry and sat on the master limiter's ceiling, heard as four or five
copies piling up on every step. The unit renders the diffuse tail only, starting after the mean free path
has been crossed a couple of times, when reflections are too dense to have a direction.

FMOD's EARLYLATEMIX is the blend of late reverb to early reflections: 0 is all early, 100 all late. Read
as "early reflections off", 0 means early reflections only: the unit renders its fixed pattern and no tail
whatever, in every room. Measured with AudioLab --tailcheck, one footstep in a room set for six seconds: at
0 the mixer is at the noise floor 500 ms later; at 100 it is still 28 dB up two seconds later.

## Where a room ends

A ray that meets a surface is counted as the room's surface, however far away it is. For a small place
in the open that is wrong: under a bus shelter the rays leave through the open front, cross the road,
hit the building opposite and come back recorded as the shelter's own hard walls: 612 m² of surface for
a 65 m² box, and a two-second tail under a sheet of glass.

Three distance-keyed fixes failed: anything that says "far means gone" also cuts a big flat room's own
far wall. What marks the edge of a room is that the openness changes across it. So for every ray that
goes a fair way (3 m) before it strikes, the openness halfway along it is compared with the listener's
own; a jump of more than fifteen points means the ray crossed into a much more open place and counts as
escaped. Under the shelter (0 % open) a ray out of the front reaches the road (a third open): gone. In
the garage (1 %) a ray to its far wall stays in the garage (1 %): kept. In the street (a third) a ray to
a facade stays in the street: kept. A listener already more than 35 % open is not inside anything small,
and the boundary is not looked for.

Known limit: a small room opening onto a big enclosed hall reads closed on both sides, so nothing jumps
and it is surveyed as the hall.

## A region is not a room

A region is a named volume, not a room. The distinction did not exist until a map needed both: the
speedway names Front straight, Turns one and two, Infield and Grandstand so that a blind player standing
on two kilometres of identical asphalt knows where they are, and the moment it did, the whole map started
sounding like the inside of a building. Every open-air behaviour in the engine (the muted reverb bus, the
ray-traced outdoor decay and its wet gate, the outdoor air absorption, the absence of a small-room gain)
was keyed on "the listener is in the global region id", that is, on the map not having named the place.
Name the place and you were indoors.

So those questions are answered from the boundary itself: a face whose material is "None" is no surface;
sound that reaches it leaves and does not come back.

That reading fixed the estimate as well. Sabine reads absorption zero as a perfect mirror, so six open
faces came out as a sealed box of infinite reverberation, and because the total then fell under the "did
anything absorb?" guard, the code quietly substituted a 500 ms default room: an unbounded 277,000 m³
infield was handed a 500 ms room, and then the ray tracer wrote a longer decay over it at full wet.
Sabine's V/A describes a diffuse field in a closed enclosure; open one face and there is no such field.
An unclosed region gets no statistical estimate; what reverberation it has (a grandstand at your back, a
street with facades on both sides) comes from the ray tracer. The 500 ms fallback is gone: a closed
boundary that absorbs nothing rings as long as the clamp allows.

## Car windows

Four window characters were rendered on 2026-10-03; Cody chose the old one on 10-04 ("the car window v3
sounds the best, use that"), and every window uses it. The other three, as `Character` arguments:

- new: 45, 0.42, 0.36, 3.0, 0.40, 0.36, 6e-6, 3e-6
- standard: 50, 0.48, 0.40, 3.5, 0.50, 0.42, 9e-6, 4e-6
- worn: 55, 0.55, 0.44, 4.5, 0.85, 0.55, 12e-6, 5e-6

## Walking

The traps each of SharedMovementEngine's rules guards against:

- Stepping down off a kerb. A body walking off a kerb steps down by the same StepHeight it steps up
  by. With only the 0.1 m landing window, a 12 cm lip put the body in the air for two ticks and then
  landed it, and a landing is a heavy sound (it plays the footstep bank). Where made ground sits proud
  of the dirt beside it, that fired wherever a pavement ended; standing on the boundary, the five-point
  ground probe straddled it and flickered, so it fired again every half second. Reported as "walk a few
  steps, stop, and for like 10 seconds, periodic bangs". A body already jumping or falling keeps the
  0.1 m tolerance: walking off a roof is still walking off a roof.
- A fall ends on the floor, not inside it. The landing only caught a body that started a tick within a
  tenth of a metre of the floor. Falling faster than about 3 m/s at 30 Hz (any drop over about half a
  metre) it crossed that window between ticks and arrived a tick's fall deep in the floor; collision met
  the floor box from inside and pushed the body out sideways to the box's nearest edge. Sean walked off
  the west side of Brandt Court (2026-10-04), fell eighteen metres and was put 489 m west, at the map's
  edge, in one tick; Cody's drop onto the same roof was put 0.85 m south. GroundHeight is the highest
  top below the body, so a fall that would pass through it stops on it.
- The push is applied after the move. It used to be applied to the position before the move, with a
  penetration measured after it: pressing into a wall shoved the body backwards most of a step every
  tick and the next tick walked it back in. No net movement, 4.5 m/s of path length, footsteps that
  never stopped, and an acoustic region flipping at half the tick rate where that straddled a doorway.
- A push never ends deeper in something else. The three passes each lift the body out of the deepest
  thing it is in; when two things disagree an odd number of passes ends inside the second. Sean,
  standing still against Kestrel House's north parapet (2026-10-05), was pushed half a metre into it by
  the player beside him every other tick, for minutes; and a push that carries the centre past the
  middle of a 35 cm wall comes out of its far side, off the roof, eighteen metres onto the dirt.
- Step-up is asked of the cylinder. The capsule's rounded bottom fits past an edge up to 0.85 m high
  when not right against it, and the ground probe then stood it on top: a body walked up a 56 cm ledge
  the cylinder could not (the parity harness, Kestrel Street's steps).
- A person's collider stands upright. Movement once took a player's whole orientation for their box,
  look pitch included: looking down at 45 degrees tipped a 1.8 m box over sideways through whoever stood
  beside them, and turning on the spot swung its corners round. The one standing still was shoved half
  a metre a tick with no input (Kestrel House roof, 2026-10-05).
- The solid gather keeps the old grid's reach (every 10 m cell within CollisionSearchRadius, at every
  height): a push out of something big carries the body metres, PushedDeeperIntoAnything can see only
  what is gathered, and every past fix was heard with that reach.

### Footfalls

StrideAccumulator is the one place the rules for a footfall live, for the local player (predicted and
corrected) and for remote bodies (interpolated). Each rule was learned from a fault, so they are not
written twice to drift apart.

- A stride is something a body did, not something done to it. `MaxStrideStep` (1 m in one update; a
  sprinter at 6 m/s covers a fifth of that between frames) catches a teleport, one big jump. It misses
  a reconciliation, a run of small ones: arriving on a map, predicted and authoritative positions
  converged in steps of a few centimetres, each plausible, together nine metres of phantom walking.
  `MinStrideSpeed` tells those apart: a correction moves you while the body's own velocity is zero.
- A passenger is not silenced by that rule (it was once written that it was): the server gives an
  occupant the velocity of what it rides in (OccupancySystem). Each caller keeps riders out instead,
  the local player by RidingEntityId, others by their definition (OtherBodies). A vehicle's motion fed
  to a stride generator is a footstep every stride of road: at sixty miles an hour, a machine gun.
- A step is as long as the speed makes it (`StepLength`). A fixed half metre at the game's 4.5 m/s
  walk is nine footfalls a second, fourteen at a sprint: insects running. A human tops out near four
  a second because a leg is a pendulum, and above that buys speed with a longer step. Alexander's
  dynamic similarity (one curve for mouse, human and elephant): stride / L = 2.3 (v^2 / gL)^0.3, with
  L the leg length and a footfall half a stride. It gives 1.4 m/s: 0.69 m per step, 2.0 a second;
  4.5 m/s (W): 1.38 m, 3.3 a second; 7.2 m/s (Shift): 1.82 m, 4.0 a second. A steady cadence and a
  stride that does the work, which is what a run sounds like against a walk. There is deliberately
  no cadence cap: a cap is a rule about the clock standing in for the body's, and it eats footfalls.
- A walk's first footfall is at its start. Counted from a standstill the first falls half a stride in,
  and a body cannot move without putting a foot down. A tap of a movement key moves one 30 Hz tick at
  4.5 m/s, 15 cm: counting from standstill took three or four taps to bank a step. Now a tap is a
  footfall and a longer press is that and then one every step length. Only a body on the ground can
  have stopped (a run ending in a jump has its feet in the air), and a landing is the foot going down,
  so it takes the place of the start footfall.
- Distance is judged per update, not as a speed: a speed needs a delta time, and this is driven at
  whatever rate its caller manages (in tests, as fast as a loop goes).
- A landing needs a fall (`MinLandingSpeed`, 1.5 m/s, a drop of about 7 cm). A blip in the ground (a
  map still streaming in, a probe straddling two surfaces, a correction across a lip) without it
  sounded as a landing gated to two a second: five in two seconds at the city spawn before a step was
  taken. Speed, not time in the air, because time needs a clock. Anything the movement engine really
  puts in the air has dropped more than a StepHeight first and arrives at 3.5 m/s.
- The foot's height is the lower of now and an update ago: the movement engine lifts a body the whole
  StepHeight for one update when it steps up and the probe settles it the next, so a footfall in that
  update put the next one, on the landing at the top, 22 cm below it, and the last step of a flight of
  17.6 cm risers went down as a heel drop (2026-10-04, the walk up Selby House).
- Stair footfalls: up, the ball of the foot is put down slower than a heel strike on the flat (about
  0.4 m/s against 0.6); down, the heel drops with the body already falling (about 1.0 m/s). Estimates
  of a controlled stair gait, not measurements: -3.5 dB and +4.4 dB against a level step.

## The racing line's arc length

RaceLine's nodes are not evenly spaced once built: Resample lays them out
evenly along the centreline, Smooth then pulls each point half way toward the average of its neighbours
(shortening the loop wherever it curves), and the lateral offset onto a car's lane lengthens an outside
lane's turns and shortens an inside one's, in proportion to offset over radius. Neither touches a
straight.

Indexing by division on the nominal spacing concentrated all its error in one place: the distance wraps
on the true perimeter but was divided by the nominal one, so on an outside lane `s / spacing` ran past
the last node and the clamp pinned it there. The car stopped dead at one fixed point of the track until
the lap wrapped: about forty metres of lane on the St Louis egg, most of a second for a stock car, half
of one for a formula car. Reported as "the car will stop in front of me, the Doppler change in place,
and then the car keeps going". An inside lane had the mirror of it, teleporting forward across the seam.
The speed was never wrong, only the position. Locate now binary-searches the arc-length table: nine
comparisons for a five-hundred-node circuit, exact across the closing segment.

## Machines as parts

Why machines are data (Machines.cs):

- MachineModels is a vocabulary of strings, not a type hierarchy, because the list grows (a rotor, a
  turbine, a fountain's jet) and each is "a model, a profile for it, and where it sits"; a map author
  names the model in data and nothing in C# is recompiled.
- MachinePart has the same shape for a tailpipe, a rotor and a fountain, which lets a helicopter, a bus
  and a tree be one kind of thing. An absent setting means "whatever the profile said", which is what
  lets a definition say "a school bus, but with open pipes" in three lines.
- MachineDefinition exists because VehicleProfile.Presets is a dictionary of factory functions: a map
  could say "nascar_v8" but not "that engine, in that body, with the pipes out of the side". Base lets
  an author's machine lean on the built-in library instead of copying it.
- Authored machines are found before built-ins so a map can replace a car without editing the library,
  and the library can move out of C# a machine at a time.
- Air horns are a part, not a train fitting: a lorry, a bus, a locomotive and a ship all have one.
- MachinePart.ExtentMetres: a 40 m airliner and a tailpipe are not the same thing at ten metres. Nothing
  reads it yet; replacing ClientAudioSystem's car-sized MathF.Max(reference, 3f) with it is the step,
  with extent and audibility ranking done together (left as a TODO in the code).

## Loudness: the ceiling and the pivot

The level that renders at full scale is a level at the listener, not a source level. Set to a source
level (165, a rifle at one metre), a gunshot is full scale only at the muzzle; at any real range the
inverse law has already taken 30 dB off it. At a 130 dB ceiling (where loud becomes pain) a rifle at
thirty metres arrived at 129 dB and played at full scale, at ten metres 139 and clipped, as an ear
does, but nothing except gunfire ever reached full scale: a door slam at 88 dB rendered at -28 dBFS and
glass across a street at -41, weak and dull though the physics was right. Everyday sounds are 60 to 95
dB, so the mix spends itself there.

With the compression a player setting, a fixed 112 dB ceiling would make "real" (1.0) put everything
below a jackhammer as far under the volume knob as it is under one: a street scene 40 dB down. So the
compression turns about an everyday level: a sound 70 dB at its reference distance (the 1.2 m minimum,
for anything that quiet) plays at the same level at every setting, and the full-scale level follows:
112 dB at the shipped 0.45, about 89 dB at 1.0, where a V8 floored beside you runs into the ceiling as
it does into an ear while a door, a footstep and a beacon stay where they were.

## The park tree

FoliageSpec.ParkTree (NatureModels.cs).

Vogel exponent. At −0.7 (taken before for every tree) the crown grew 10.8 dB from 3 to 6 m/s, Fégeant's
birch, and the gusts in an ordinary breeze swung it 4.4 dB (the standard deviation of its 400 ms level
within a minute, over ten minutes) against 1.3-4.2 dB in recordings of leaves in wind; Cody heard the
swings as too obvious. At −0.9 it grows 9.5 dB (32 dB a decade, between Fégeant's oak at 30 and birch at
36) and swings 4.0 dB.

Its level, measured with `--nature levels park_tree sec=600`:

- 2026-10-04, ten minutes of the field (4.1 m/s mean at the crown): Leq 48.2 dB, 46.7 dB(A); the
  gustiest second 7.5 dB over. A minute is not enough to measure it by: a minute of gusts read 2.3 dB high.
- 2026-10-05, the boughs reading the wind across the crown and the field's turbulence at 0.25: Leq
  47.9 dB, 46.3 dB(A), the gustiest second 6.6 dB over.
- Round 3 (Vogel −0.9, strikes by contact angle): 47.8 dB, 46.2 dB(A), the gustiest second 6.7 dB over.
- Texture round 1 (2026-10-06: strikes and twig episodes from exponential-tailed turbulent increments),
  five minutes: 47.4 dB, 45.7 dB(A), 10 ms peaks' 99.9th percentile 21.1 dB over, so 22 of room.

## Enclosure, not decay time

Reported 2026-09-18 from the speedway's front straight: the geometry reverb read up to 1579 ms and
swung by more than a second while the listener stood still, where the infield correctly read 101 ms.
The wet level was taken from the decay time, which cannot carry it: Steam Audio's parametric
estimator fits an exponential to whatever energy its rays bring home and cannot report that there was
hardly any. Measured with AudioLab --sim-reverbfield, a walled yard with no ceiling fitted a 1.00 s
tail where the same walls with a roof fitted 0.60 s: the roofless one read as the more reverberant,
which is backwards, and no threshold could fix it because both sit on the same side of every
threshold. Enclosure replaced it, and counts two bounces so that a plane is not a room: half of every
direction from a standing listener ends in the ground, and concrete returns 98 %, so one bounce scored
a bare plaza 48 % enclosed (measured on the battle spike's geometry). EnclosureTests hold it to that.

## The clap

Why a crowd of claps once sounded like a bag being crushed (ClapTests). Measured on the old model, a
single clap put 0.4 % of its energy below 200 Hz and over 40 % above 1.5 kHz, and was gone in 12 ms:
a tick, and a thousand ticks a second is cellophane. Two mechanical parts were missing.

- The pocket of air between the palms rings. The first version had a sharp resonator and a listener
  called it pouring water (a drip is a brief narrow resonance), so it was replaced by a plain low-pass
  tilt with no note at all. The question was never whether the cavity resonates but how hard it is
  damped: two soft leaky palms give a Q of about three.
- The flesh thumps. Two palms meeting is a soft heavy impact first; it is low and slow, and it is the
  half of a clap that survives 200 m of air, so a clap made only of edge arrived across a stadium as a
  crinkle, which is what was reported.

The reference (67 clean claps cut by tools/split_footsteps.py from `approved/applause/Slow Clapping  HQ
Sound Effects.mp3`, 2026-09-19): a clap peaks at 1-2 kHz, as a footstep does, with a plateau of flesh
from 125 to 500 Hz about 8 dB under the peak, a 12 dB fall in the octave above, and a cliff below
60 Hz. Before it was measured the model had been settled by ear with its cavity at 800 Hz and a 12 ms
thump, and was 11 dB heavy at 250-500 Hz, 9 dB light at 1-2 kHz, and twice too slow. A real clap is
20 dB down 5 ms after its peak; the model had taken 13.5 ms. The recording's slow tail (-52 dB at
30 ms, -64 at 45) is the room it was made in, which is the engine's job; the first model stopped dead
at 24 ms.
