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
