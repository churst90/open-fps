# AudioLab notes

The reasoning and history behind OpenFPS.AudioLab instruments that has no other doc of its own. The
spikes point here; `--help` lists the instruments.

## The simulator's reverb outdoors

`--sim-reverbfield` (SimReverbFieldSpike) was written for a fault found live on 2026-09-18: on the
speedway's front straight the geometry reverb read 200 to 1579 ms and swung by more than a second
while the listener stood still, where the infield read a correct 101 ms. Outdoors beside two walls,
that is a cathedral.

The question is not "is the RT60 too long" but "does the RT60 mean anything here". Steam Audio's
parametric estimator fits an exponential decay to the energy its rays bring back. In a room that is a
measurement; in the open, where a handful of rays return off one wall and the rest fly away, the fit is
made on noise: it reports some time and cannot say there was nothing to fit. What does know the
difference is how enclosed the place is, measured from the geometry, so the spike prints both for a
ladder of places from an empty field to a sealed box.

Steam Audio's reflections output carries an `eq` triple that looks like the energy this needs. It was
bound and measured: on the parametric path it is zero everywhere, field and sealed room alike, which is
why the second column is a geometric measure.

## The server's step and the client's, side by side

`--walk-pair` (WalkPairSpike) was written for Cody's 2026-10-10 report at 19617 Turtle Dove Lane, Magnolia:
"it's like I hit a wall but then it lets me keep walking ... and then the cords don't change". One walk is
taken twice from the same spot: by the server's own `MovementSystem.Update`, with a session and queued inputs,
and by the client's own `ClientPhysicsSystem.Predict` over the client's world as the server streams it to a
client standing there (`from=` streams it first to another spot, as a /tp across the map does: the coarse
ring's ground is sent again whole when its tile comes up to full; `full` gives the client every definition).
Every tick the two bodies part by more than a centimetre is printed with the ground each reads; `open=` swings
doors open on both sides first, and `key=` holds or taps (`taps= gap=`) any of W, A, S, D.

On 19617 the two agreed to the millimetre on every walk tried: inside and out, held and tapped, straight
through the open front door, along the east wall, and after streaming in from the Bobcat Lane spawn. The
server was not refusing moves there, and the stall was not reproduced. The client log has the body at the
house's walls (C read 0.3 m off their faces, a body's radius) in the trench the floor's grading digs round
the house, 0.25 m below the lawn. `room=` turns a room's id into a /tp spot (docs/LISTENING_SPOTS.md).

Its machinery became physics stage 0's movement replay (docs/PHYSICS_BOUNDARY.md section 14): nineteen stored
walks, each step taken from the server's state on both sides so a parting shows on the tick it happens.
`--physics-replay` prints them with the three parity risks, and the drives, traffic, trains and ballistics
table beside them; `--physics-parity` sweeps the same questions over thousands of standing points;
`--jolt-trial` measures Jolt Physics in our scenes.
