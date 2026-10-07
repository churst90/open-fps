# Client notes

The reasoning behind OpenFPS.Client.Core code that has no other doc of its own. The code points here.

## Stair cues

What the client says about stairs ("Stairs up, 17 steps, to floor 3") comes from the map's stair
markers (prefabs/stair_marker.json): each end of a flight is a marker standing a metre above the
landing a step back from the end riser, facing the way you walk to take the flight, named with its
line. Nothing works out where a flight is from the geometry.

Said once per arrival, never about the flight you came by. This replaced a rule of reach and leave
radii a metre and a half apart (Cody, 2026-10-04: "I hear indicators repeating several times"). On a
landing the top of one flight and the foot of the next stand side by side facing the same way, so a
sidestep from one lane to the other left one and reached the other, and "Stairs up, Stairs down,
Stairs up" came out of shuffling on one spot; any two-metre walk away and back said it again, nine
times in three minutes; and arriving off a flight walking backwards (facing up it) announced the
flight just walked down. Now a marker is quiet until you have been somewhere else: another zone,
another floor, or `LeaveMetres` across a big one; and both ends of the flight under your feet are
quiet.

On a flight. Where the map names its flights and landings (NamedPlaces) the flight is a zone of its
own, "Marlow Tower stairs, floor 2 to 3". Where it does not, a storey's zone stops at its ceiling and
the next starts at its floor, so at eye height the name would change halfway up the stairs; the zone
announcer holds a room's name while `OnFlight` is true and says where you are when you step off.

The cue speaks for the stairs. Walking up to a flight, the cue is said half a metre before the first
riser, and a moment later you step into the flight's zone (and you crossed the landing's zone to get
there). The cue says more (which way, how many steps, to where), so a flight's or landing's name is
not said when the cue has just been, or for a flight whose end the cue told you about (`CoversZone`,
`FlightAnnounced`). The zone is said when the cue was not: stepping on from the side, or backwards,
or arriving on a landing off a flight.

One beacon a floor (Cody, 2026-10-04: a beacon only at the bottom and top left "the levels in
between" to be found without seeing where the stairs are). Each floor's beacon is the foot of its
flight up; on the roof, the top of the flight down. Which end of a flight a marker is comes from the
markers alone, paired from the bottom up: in a dog-leg every other flight is in the same lane, so
the top of one flight faces, along one line, both its own foot a storey down and the foot of the
flight two up a storey up. The lowest marker can only be a foot, and pairs with the nearest marker
above facing back down its line (that flight's top); in order of height, every marker not already a
top is the foot of the flight above it, or the top of the whole stair if nothing above faces back.
The same stacking is why `OnTreads` looks for the other end on the side of the marker the feet are.
