---
name: route-stops
description: TrackData.Stops, added 2026-09-21 — one list on a route that makes buses kneel, air brakes fire, trains halt at platforms; everything else falls out of a vehicle that actually stops
metadata:
  type: project
---

Built after [[nothing-on-the-city-ever-stops]] identified the gap. A `TrackData.Stops` list —
`{ AtMetres, DwellSeconds, Kind, ForPreset }` — and both movers honour it: `VehicleSystem.UpdateRacer`
and `RailSystem.Update` brake on `v = sqrt(2·a·s)`, hold at a DEAD STOP for the dwell, then move on.

**Nothing about the sounds is declared at the stop.** That is the whole design: every noise a
halted vehicle makes was already read off its own speed history, and none of it could ever fire
because nothing on a racing line ever stopped. With the stop in place they all arrive for free —
service release at the end of the deceleration, spring brakes after 2.5 s still, the kneel, the
doors, and the release on moving off. `ForPreset` keeps a bus stop for buses: a car does not use one.

Verified with `OPENFPS_TRACE_SHUTTLE="City bus 1"` (which now traces racers too): 8.7 → 5.1 → 1.5 →
0.0 m/s into the stop, sixteen seconds at zero, away to the next one 124 m round.

**Also added:**
- **The kneel.** `transit_bus` declared a `kneel` air port from the start and *nothing ever fired
  it*. It vents with the doors now: the kerb-side bags dump and the body drops 100 mm.
- **`DoorChimeSpec`** — the beep-beep-beep. Not pneumatic and not part of the air system: a piezo
  disc at its own resonance (2,730 Hz) with a square on it, pulsed at 2 Hz, 80 dB at 1 m. High on
  purpose, for the same reason a siren's band is: it is where a diesel is weakest.
  `VehicleProfile.DoorChime` turns it on, and only the bus has it.
- **City map:** four bus stops per downtown loop and on north_block, two platforms on rail_loop.

**Cody asked whether this means modelling the inside of the bus. It does not** — kneel, doors and
beeper are all heard from the pavement, they are what a bus DOES at a stop. What a cabin does to an
engine when you are sitting in one is a separate model and is still to do.

**Still open from the same list:** crossing bells triggered by an approaching train (the
`StruckBellSpec.CrossingGong` and the `--crossing` spike already exist; what is missing is crossings
on the map and the detection), and vehicles giving way at junctions (`Kind` is carried for it).

See [[nothing-on-the-city-ever-stops]], [[signals-and-air]].
