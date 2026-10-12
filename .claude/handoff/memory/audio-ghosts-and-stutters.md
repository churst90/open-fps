---
name: audio-ghosts-and-stutters
description: "\"Cars vanish, only reflections remain\" and \"close fast cars stop mid-pass\" — diagnosed session 6 (2026-09-14), FIXED session 7; record in docs/AUDIO_GHOSTS_AND_STUTTERS.md"
metadata:
  node_type: memory
  type: project
---

Both faults are **fixed** in the working tree (session 7, 2026-09-14); the full record — diagnosis,
what was applied, and what was deliberately left — is `docs/AUDIO_GHOSTS_AND_STUTTERS.md`. Neither
was the voice budget or the mixer.

**Ghost cars** was four things at once, all now addressed: the Steam Audio pathing bake ran INSIDE
`SetScene` and blocked the direct stage for ~100 s (now a background thread, probe count budgeted);
the occlusion probe was the entity origin with a fixed 0.5 m sphere, half underground for anything
that drives (now `AudioEmission.PointFor` + a radius that fits the emitter's clearance, shared by the
voice AND the probe); reflections were never obstruction-tested and never occluded (now both); and a
knee-high wall was a total block (now `OpenFPS.Common/Diffraction.cs` — Maekawa loss from the path
difference, per band, as a floor under the simulator's transmission).

**Stuttering close pass** was the position's timestamp: stamped on SUBMIT, so `VoiceManager`'s 250 Hz
refresh reset the age and dead reckoning never saw more than 4 ms. Now `SpatialEmitter.PositionSampledAt`
carries the interpolation time off `WorldSnapshot.PositionsSampledAt`. A SECOND cause found while
fixing it: `LastAttributeAt` and `DeadReckon` both read `_loadClock`, a Stopwatch the mixer-load report
**restarts every 250 ms** — the age went negative and the reckoning was inert. Everything timestamped
now uses `OpenFPS.Common.AudioClock`.

**Also fixed:** the speedway's front straight ran through the grandstand deck (138 sampled points of
the lap). `tools/gen_speedway.py` takes the setback from the wall's FURTHEST point now, and
`OpenFPS.Common/TrackClearance.cs` fails any map whose track passes through solid geometry, at load.

**Still open:** vehicles as prefabs with a LIST of emitter slots; the two unexplained field-wide starve
bursts (needs a producer-side scheduling gauge); a borrowed distant engine voice inheriting the source
car's Doppler and then applying its own.

**How to confirm:** `OPENFPS_AUDIO_DEBUG=1` — `[SAWORKER] vis=` lines must start in the first second or
two, not at +100 s. `OPENFPS_AUDIO_TRACE=<entityId>` prints an `[ATRACE]` CSV at the full 250 Hz for
one voice; a staircase is eight identical rows and a jump, a glide is not. Watch for
"Voice N was placed at a position X ms old" in the log; it should not appear.

**Why:** Cody wants every lesson generalised — nothing in the fixes names a car, a track or a wall.
See [[speedway-map]], [[no-special-cases]], [[engine-audio-next-step]], [[racing-line-banking]].
