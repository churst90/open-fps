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
