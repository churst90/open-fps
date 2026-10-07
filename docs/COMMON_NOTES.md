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
