---
name: play-session-2026-10-05
description: "10-05 Cody+Sean on VPS f1a0421123d3: remote footsteps silent, driver's steps heard by passenger, vehicles reverby/same, Sean pushed off Kestrel roof, bump says 'something'/concrete, vehicle beacons, Windows audio thread 60 Hz; four fix agents"
metadata:
  type: project
---

Reported by Cody 2026-10-05 afternoon (Sean's log inbox/client-20261005.log, Windows, 24 cores):
- No footsteps from the other player (beacon only), both ways; as a passenger Cody hears footsteps from
  Sean driving. Remote steps come from OtherBodies/StrideAccumulator off the sent velocity — suspect
  today's net-trim (packed velocity, HoldThrough) or riding velocity. Branch fix-remote-steps.
- Vehicles "too reverby and all sound the same" (approved by ear 09-30/10-04). Branch fix-vehicle-sound.
- Sean on Kestrel House roof (map ~30, 18.25, -26.4), no input, pushed 0.467 m back and forth every
  second for minutes (server WRN "moved 14 m/s across the ground on foot"); then off the roof onto the
  dirt. Bump into a person says "something" and sounds concrete. Branch fix-roof-bumps.
- Vehicle beacons not heard (only a high chime near a vehicle). Sean's audio thread ~60 Hz of 250
  (Windows 15.6 ms timer), pass-bys step 2.7 %, 1993 "position 110 ms old". Branch fix-beacons-winaudio.
Agents report changes.md text; merge, test, then server + zip if Common changes.

**Outcome (all merged, full suite 2393/2394 + OwnClap load flake passes alone; pushed eec2dd3b; VPS
51e2b9562ee9 at 16:00 CDT; zip ~/OpenFPS-windows-51e2b9562ee9.zip):**
- Remote steps: occluded by the walker's own solid body (CarryThePath ignored only the sound id), and
  MovementSystem wrote the floor into the player's Material -> body = Concrete. Seated players carried
  the car's velocity since 09-15 -> EntityDefinition.RidingEntityId (wire change), no stride when seated.
- Roof: SharedMovementEngine used the full look rotation (pitch) for a cylinder -> tipped box swept the
  neighbour; 3 push passes left a body inside the parapet every other tick. StandingRotation + no step
  that ends deeper. People = PersonMaterial (Skin), players carry their name -> bump says the name.
- Vehicle beacons: blip origin inside the car's floor -> always occluded; now nearest body side.
  "High chime" near vehicles is not a beacon (likely bus door beeper).
- Windows: TimerResolution.cs timeBeginPeriod(1) (audio thread was 60 Hz, engine starves x5).
- Vehicles "reverby/all same": NO regression (byte-identical renders vs cc1855a8); commuter cars are tyre
  noise on one shared TyreProfile, 11-20 dB over engine. Cody to decide; inbox/vehicles-2026-10-05.
- Linux client log hit Serilog's 1 GB cap 10-04 19:36 (silent); run-gtk-client.sh rotates to .prev now.
