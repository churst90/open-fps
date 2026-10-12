---
name: nothing-on-the-city-ever-stops
description: City buses and box trucks run RACING LINES, so they never halt — which is why the air brakes' park and door events can never fire, and the same gap blocks train platform stops and intersections
metadata:
  type: project
---

Cody, 2026-09-20: "Are the air brakes correctly firing when they should?" Traced it: no, and the
reason is not in the air system.

`EngineVoiceState.AirEvents` reads the events off the voice's own speed history and is correct:
service release at the end of a deceleration, spring brakes + doors after `ParkAfterSeconds` = 2.5 s
stopped, release on moving off. But on `maps/city.json` **City bus 1/2 and Box truck 7/17 declare a
`Track`**, not a road — and a vehicle with a track is a RACER (`VehicleSystem.UpdateRacer`), which
laps continuously and never comes to rest. So:

- `service_release` fires at every corner exit where the line's braking exceeds 0.45 m/s² — too
  often for a bus, and the only air event that fires at all;
- `parking` and `door` can never fire, because `_stoppedSeconds` never reaches 2.5.

Only the two SHUTTLE diesels stop: Tunnel truck (wait 9 s) and Airport coach (wait 14 s).

**The missing thing is stops on a route**, and it is the same missing thing behind three other
requests: trains stopping at platforms, vehicles halting at intersections, and pedestrian crossings.
A track wants a list of points where a vehicle decelerates, waits and moves off. Build that once and
the air brakes become correct for free, because nothing about them needs changing.

**Also found:** `ASSETS/SOUNDS/BEACONS/TRAFFIC/` already holds `walk.wav`, `wait.wav` and
`rapid.wav`, and **nothing references them** — crossing chirps need placing, not making.

See [[machines-as-parts]], [[city-block-map]].
