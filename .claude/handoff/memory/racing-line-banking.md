---
name: racing-line-banking
description: RaceLine ignored the banking the speedway's geometry already had, so every car lifted 3-4.6 semitones twice a lap
metadata:
  type: project
---

Fixed 2026-09-14. `RaceLine` capped corner speed at `sqrt(mu*g*R)` — FLAT-track physics — while the
speedway's generator builds 24 degrees of banking into the turns and the centreline carries the
elevation. A centreline cannot describe a CROSS-slope, so the bank angle has to be declared:
`TrackData.BankingDegrees`, emitted by `tools/gen_speedway.py` as `BANK_DEG`, passed to
`RaceLine(..., bankingDegrees)`, which now uses `v^2 = R*g*(mu + tan@)/(1 - mu*tan@)`. When
`mu*tan@ >= 1` the bank alone holds the car and there is no cornering limit at all.

Measured before: stock car 231-296 km/h round the lap (22 %, **4.2 semitones** of rev drop), formula
273-327 (3.1), big block 145-189 (4.6) — twice a lap, and the grandstand is at start/finish where
they brake for turn 1. Heard, correctly, as "the cars sound like they are slowing down". After: flat
out, 0 %. The old `CorneringG` values (2.90, 4.00) were a fudge for the missing banking and are now
real tyre grip (1.75, 2.60, 0.95).

Same session: `FormulaCar.DragArea` was 1.35 (road-course wings) so the car could only reach
**230 km/h** while the map commanded 327 — ten of the thirty cars permanently flat out, a hundred
short, droning at 10,500 of 15,500 rpm and never revving out. Oval trim is 0.85, which gives
296 km/h at 13,450 rpm against a 13,500 torque peak. Map asks 300 now, not 330.

**Why:** a map that commands a speed the car cannot reach does not make a fast car, it makes a flat one.
**Also that session:** every custom FMOD DSP callback is now wrapped in try/catch — an exception in one unwinds into the native mixer thread and KILLS THE CLIENT, which is how `BoundaryProximityProcessor` ended a session. Six of seven had no guard. And `LocalPlayerController` now gates stride accumulation on the player's OWN velocity (>0.5 m/s): a spawn reconciliation is thirty sub-metre corrections, each too small for the teleport rule, together nine metres of phantom walking.

**How to apply:** `IAudioProvider.TryGetEngineTelemetry` and the `Cars:` census line print told vs
driveline speed vs rpm vs gear — that is how these were told apart from a synthesis fault. The
speedway is GENERATED; edit `tools/gen_speedway.py` and re-run, and **restart the server**, which
loads maps once at startup. See [[speedway-map]], [[engine-audio-next-step]].

**2026-09-16 — the sibling of this bug, one file over.** `RaceLine` learned about banking; the TYRE
DEMAND never did. It differentiated the interpolated velocity and divided by FLAT-ground grip, which
on ten degrees with a 1.75 g slick reads 1.59 against a full-slide threshold of 1.45 — every car in
every corner rendering pure broadband skid, heard as "a long white noise tail travelling with the
vehicles". A listener CANNOT fix this for itself: a banked constant-radius turn at constant speed and
a flat one have identical accelerations, because the bank appears in the normal load and not in the
kinematics. So the demand is on the wire now (`EntityState.TyreDemand`, one byte), computed in
`VehicleSystem`.

And the first attempt at that fix was wrong in a way worth remembering: it measured against the
racing line's SPEED limit, which is the car's top speed on a straight — so a car flat out down the
back straight read 1.0 and squealed the whole way. `RaceLine` now keeps the GRIP-limited speed
separately (infinite on a straight) from the speed it allows. Two different questions.

