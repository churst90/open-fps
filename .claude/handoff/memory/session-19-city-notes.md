---
name: session-19-city-notes
description: What Cody reported after the crash fix and what each turned out to be — windows per /tp, quiet diesels by construction, whine anchored 60 dB under the jets, region ids that are not JSON ids
metadata:
  type: project
---

Session 19 (2026-09-20), after [[the-mixer-thread-crash]] was fixed. Cody's notes and the answers:

- **"Several OpenFPS windows on alt-tab."** `GtkClientShell.EnterGame` built a new `GameWindow` on
  EVERY `PlayerSpawned`, and the server answers `/tp` with one. Made idempotent — **and that was
  only half of it: `GameWindow.Present` still built a fresh `ApplicationWindow` on every call and
  dropped the old one in the field, so the toplevel leaked per teleport anyway. Fixed properly
  2026-09-20; Present now builds once and afterwards only raises.** If windows stack again, check
  BOTH the shell's `_gameWindow` guard and `Present` itself.
- **"Buses and trucks are very quiet; only the muscle car is noticeable."** `--engine-levels` shows
  the models render EXACTLY what they declare (school_bus 85/98, diesel_truck 93, v8_muscle 122,
  police 132 at 1 m). Quiet by construction, not by placement. The physical lever added:
  `VehicleProfile.EngineBayLeakage` — a truck/bus's block + intake reaching the street (0.6 / 0.45);
  cars stay 0 so nothing approved moved. Not yet re-measured at 1 m — the bench renders exhaust only.
- **"No air brakes."** Nothing on a road vehicle had an `AirSystem`; it only lived in the crossing
  spike. `VehicleProfile.AirSystem` (tractor_trailer / transit_bus) now renders inside
  `EngineVoiceState`, events read off the voice's own speed history: service release at the end of a
  deceleration, spring brakes + doors after 2.5 s stopped, release on moving off. Unheard so far.
- **"The airliner whine stops when it spools up."** `WhineDb` was anchored at full power at 84 dB
  against 142 dB of jet: it existed only at idle. Now 118; the approved flyover figure (73 dB at the
  ear) did not move. Cody has not heard it yet.
- **"Footsteps very quiet / none in the tunnel."** Own steps now get +8 dB bone conduction
  (`OwnFootstepBoneConductionDb`); others unchanged. Tunnel: `--walk` fires 124 steps through it, so
  it is on the audio side; NOT reproduced. `./run-gtk-client.sh foot` logs every step as `[FOOT]`.
- **Region ids in client logs are SERVER ids, not city.json ids.** Looking them up in the JSON
  said "door", "plaster wall" — the doc's old "region lookup can return plain geometry" item was
  this artefact. Server 5761 = "Main Street east pavement, block 5". Don't chase it again.
- Mowers are shuttles now (`Preset: mower_push` in `Vehicles`); `walker` preset = a body with no
  emitter, heard by its footsteps (`OtherBodies` keeps the stride for `machine:mower_push` — the
  pusher — and drops it for every other synth machine).
