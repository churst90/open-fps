---
name: driving-exists
description: Player driving EXISTS (2026-09-15, DrivingSystem + OccupancyService) but only for composites with a drive seat — no map has a ready-made car; traffic is not enterable
metadata:
  type: project
---

I told Cody on 2026-09-22 that "the player can't drive anything yet" — WRONG. Commit 5e6b320 "Get in,
and drive it away": `OccupancyService` (seats on a composite root, `/addseat name drive`, `/enter`,
`/board`, `/getin`, `/seats`), `DrivingSystem` (no handling constants: torque through the gearbox,
tyre grip, drag), E = get in / get out, W/S throttle and brake-then-reverse, A/D steer (from
`input.MoveDirection`). Client stops predicting position and footsteps while seated.

What is NOT there: any drivable vehicle placed on a map (only composites a player builds), entering
NPC traffic (VehicleSystem cars are not composites), standing aboard a moving vehicle, collision
response (a driven composite stops dead), and interior/cabin audio (`VehicleBody.CabinLeak` is the
number an interior mix turns up — see the comment there).

**How to apply:** before claiming a feature is missing, grep Server/Systems and the git log.
Vehicles became SOLID 2026-09-22 (declared `LengthMetres/WidthMetres/HeightMetres` on VehicleProfile;
aircraft and pedestrians stay walk-through; client collides with `DynamicEntities`).
See [[route-stops]], [[level-crossings]].

**2026-09-22 session built on it (all by test, NOT yet heard by Cody):**
- `vehicle:<preset>` composite = `VehicleShell` built from the profile (declared body L/W/H + cabin
  dims); room is the CABIN box (`TryCabin` + `CompositeAcoustics.DeriveInBox`) — deriving it put the
  bonnet in and made a 4.1 m room. 4 parked on garage L0: `/tp -16 29 0.3` (player order), E to get in.
- A driven car found its own floor as ground and climbed 22.5 m in 3 s — GetGroundHeight ignore set.
- Steering slews (1 s to lock, 0.45 s back), lock limited to grip at speed; driven cars report TyreDemand.
- Cab sound: `EngineVoiceState.Interior` (mass-law LP at rho*c/(pi*m), SealLeak, cabin modes at full
  weight, wind v^3 anchored WindNoiseDbAt110). Hatchback inside unweighted: 60.5 idle / 69 / 73.5 dB.
- Buses that stop at `bus_stop` are shells with passenger seats (`InstantiateForTraffic`); no boarding
  or leaving above walking pace (`OccupancyService.Moving`).
- `EntityDefinition.Moves` (appended): moving parts are dynamic on the client, never in the SA scene.
  So a car's glass does NOT attenuate outside sound yet — needs moving geometry.
- `DrivingAids` + `LaneGuide`: dash ticks (US 12.19 m period) and kerb/centre tones from the asphalt
  box under the car; driver only (`ServerStateUpdate.RidingControls`, appended).

**First drive (Cody, 2026-09-22) found the big one:** `NetworkService.SendStateUpdate` splits an
oversized update in two and rebuilt the halves from 3 fields, so `RidingEntityId` arrived as -1 on
EVERY tick on the city (which always splits). Client never knew it was riding: footsteps, exterior
engine panning, no cab, no lane cues, walking prediction. Symptom to grep: server logs "took the
'driver' seat", client log never says "Riding entity". Also: seated ears 1.0 m not 1.7 (were above
the roof); T = ignition (parked cars start OFF); car doors via DoorAcoustics; /tp from a seat
disembarks. Glass enclosure filter (~21/30/30 dB) on everything outside while riding.
Tp beside the first car: `/tp -16 31.5 0.3` — `-16 29` IS the car and says "area is solid".

**Second drive (same day):** Cody: the dash ticks were "popping" that told him nothing. Replaced by
a lane-centre GUIDE beep ahead (1175 Hz, rate = speed), parking-sensor beeps for centre line (660)
and kerb (220 buzz), and SPOKEN road name / junction ahead with exits / road ends / off road; Z =
road readout while driving; J/L disabled in the driver's seat. Client logs `[DRIVE]` at 1 Hz —
read that to see what he did. Lesson: a cue must MEAN one thing he can act on; a texture is noise.

**Third drive:** 15 min mostly OFF ROAD at up to 72 km/h (garage exits perpendicular to Main St;
off road the aids were silent). Now off road the guide beep sits on the nearest road and the voice
says "nearest road is Main Street 10 metres ahead". T = start only, Shift+T = off (the toggle
switched a running engine off). Announcements log as `[DRIVE-SAY]`.

**Fourth drive:** zig-zagged across Main St at 45 deg — no sense of how far the car had turned.
Added: click per 15 deg of heading, rising chime within 4 deg of the road line, Z "pointing N
degrees right/left of the road", and LANE ASSIST (default on, K toggles; pure pursuit to the guide
point, only within 35 deg and when A/D not held; client-side via MoveDirection.X).

**Fifth drive — "not hearing any cues":** NONE of the driving beeps ever played. FollowsListener
emitters still need a world `Position`: VoiceManager culls at 1.5 x Range from the listener using
e.Position, and the default (origin) put them 200 m away. Rule: any head-following emitter sets
Position = ear + offset. Own car door was being filtered by the cabin-glass enclosure — sounds
from the ridden vehicle are exempt (InsideListenersVehicle).
