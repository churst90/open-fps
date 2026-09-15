# The cars that vanish, and the cars that stop — diagnosis (2026-09-14, session 6)

Two reports from the speedway, investigated from the code and from the log of the 19:42 run
(`/tmp/openfps-client.log`, 14 minutes, spawn at 19:43:07). **No code was changed in this session.**
This is the diagnosis and the generalisation; the fixes are listed at the end in the order to do them.

1. *"I'm walking on the track with the cars, they sound cool, then suddenly the cars disappear and I
   hear the ghost of the cars' reflections. This happens suddenly after a couple of minutes."*
2. *"Cars going across my face left to right or vice versa: the close ones going fast stop for a
   second before continuing."*

Neither is a voice-budget fault and neither is the mixer. The census line said `30 synthesized, 0
borrowed, 0 out of budget` at every one of the 160 five-second reports; real channels never went
virtual; the budget never shed anything. Both faults are in what the audio engine is TOLD — where a
source is, and whether anything is in the way — not in how it renders.

## What the log of the run actually says

Condensed from the 5-second census, mixer-load and placement lines (times are wall clock):

| time | event |
|---|---|
| 19:43:07 | MapLoadComplete, acoustic bake finishes (`Acoustics done; sending ready`), PlayerSpawned in the infield at `<0, 1.11, 0>`, 30 engine voices start within the second. Nearest car 155 m. |
| 19:43:08 → 19:44:47 | 30 engines, 19–22 reflection voices, dsp 12–15 %, 0 starves. **No acoustic result exists for any source in this window** (see below). |
| **19:44:47** | `[AcousticWorker] Built Steam Audio scene from 111 solid box colliders.` — **100 s after the first request.** Same second: `Audio placement stalled: every source held its position for up to 160 ms`, and the game loop's worst iteration is 140 ms. From here on every car has a simulated occlusion value. |
| 19:48:14 | `dsp 34.0 %, 332 starve(s)` in one second, then 193 the next. GC: 0 gen2, 5 ms paused. Player 61 m from the track. |
| 19:48:58 | `dsp 20.2 %, 270 starve(s)`, then 95. GC 8 ms. Player 41 m from the track. |
| 19:49:08 → 19:52:23 | Player on or beside the track (nearest car 5–13 m for most of it). Reflection voices fall from ~20 to **0–5** for a minute (19:51:43–19:52:23) and stay at 10–16 afterwards. Audibility floor 0 throughout, meaning fewer than 16 candidates existed on the whole map. |
| all run | Audio placement worst gap 21–22 ms every report; audio thread 240–242 Hz; attribute pass ≤ 0.06 ms mean. |

Three numbers in that table are the story: the 100 seconds, the 160 ms stall that coincides with it,
and the reflection count that never once dropped to zero *because of the budget* (the floor was 0).

---

## Issue 1 — the cars vanish and only their reflections remain

Four things combine. Each is a general fault, not a speedway one; the speedway is just the map that
lines them all up at once.

### 1. Occlusion arrives all at once, a hundred seconds in

`AsyncAcousticWorker` runs the Steam Audio direct stage for every sound source. Its first job on the
first request is `RebuildSceneIfNeeded` → `SteamAudioScene.Build` → `SteamAudioSimulator.SetScene`,
and because the simulator was created with `enablePathing: true`, `SetScene` calls
`BuildOrRebakeProbes`: a `UNIFORMFLOOR` probe array at **2 m spacing over the whole map** (720 × 420 m
of foundation is about 75,000 probes), then `iplPathBakerBake` with `visRange 16`, `pathRange 100`,
**`numThreads = 1`**, synchronously, on the worker thread.

While that runs the worker produces nothing. `ClientAudioSystem.ProcessAudioEmitter` handles "no
result yet" by building an UNOCCLUDED direct path, and step 5 never calls `SetAcousticPath` because
`TryGetResult` is false. So for the first 100 s of a session every car in the world is rendered
clean: no occlusion, no transmission EQ, air absorption from the fallback only. Then the bake
finishes and, on the next worker tick, all thirty cars receive their first simulated occlusion in the
same frame. Nothing logs the bake starting, nothing logs it finishing except the "Built" line, and
nothing warns that the model just changed under the listener's feet.

That is the "suddenly after a couple of minutes". It is a fixed delay after map load, not a function
of where the player is standing, and it will be longer on a slower machine and on a bigger map
(probe count is area / 4 m², bake cost is worse than linear in it).

The bake is also pointless for the thing it was added for. Pathing only ever redirects a source's
*apparent position*; it never changes its level. And `ProcessAudioEmitter` places an engine at its
exhaust regardless of what the path says (`ApparentPosition = engineKey.Length > 0 ?
emitterPosition : acousticPath.ApparentPosition`), so for a car the pathing result is never even
read. A hundred seconds of a single core, every map load, for a direction hint no vehicle uses.

### 2. What the occlusion says about a car when it does arrive

The simulator is asked about a **volumetric** source of **radius 0.5 m** at the entity's
`Transform.Position` (`ClientAudioSystem` step 5: `sourcePos = snap.Transform.Position`). For a car
that is the racing-line point, which the server puts at the centreline's elevation: **y = 0.0 on the
straights**, rising to 1.59 m through the banked turns. The injected foundation's top face is at
y = 0.0. So on every straight the occlusion sphere is half inside the ground, and about half of the
16 samples report "blocked" before any wall is considered.

Then the walls. `tools/gen_speedway.py` computed the grandstand's setback from the *nearest* point of
the front straight and noted that the straight's distance from the origin "varies slightly along its
length". It varies by 46 m, because the straight is a tangent between two circles of different radii.
Checked numerically against the emitted `speedway.json` (script in the session scratchpad, results
below): the front straight **runs through the grandstand deck box** for the last ~120 m before turn 4,
and the outer lane at start/finish is 4.5 m from the deck's front face.

Fraction of the lap where a car's occlusion sphere (radius 0.5, 16 samples) is fully blocked,
half-blocked, or clear, for four listener positions:

| listener | silenced (vis 0) | half (vis ≤ 0.56) | clear | what silences them |
|---|---|---|---|---|
| spawn, infield | 19 % | 17 % | 58 % | the 0.9 m pit wall (34 of 37), the grandstand deck (3) |
| infield edge of the front straight, 10 m from the cars | 11 % | 22 % | 56 % | the grandstand deck (all 22) |
| back-straight apron, 10 m from the cars | 18 % | 13 % | 59 % | the pit wall across the infield (32), the deck (3) |

So from anywhere in the infield or on the back straight, the entire front straight — a fifth of the
lap, and the stretch the 36 m grandstand wall answers — is behind a knee-high pit wall, and the
simulator treats that as total. From the front straight the cars vanish for the two seconds they
spend inside the deck. Everywhere, cars on the straights are half-buried.

What those values do in `FmodAudioProvider.ApplyAcousticFilters` (occlusion cap 0.95, muffle caps
−40/−30/−20 dB, `totalMuffle` capped at 0.8):

| visibility | dry gain | highs | mids | lows | heard as |
|---|---|---|---|---|---|
| 1.0 | 0 dB | 0 | 0 | 0 | the car |
| 0.5 (half-buried) | −6 dB | −40 dB | −25 dB | −9 dB | a thump behind a wall |
| 0.0 (pit wall, deck) | −26 dB | −72 dB | −46 dB | −20 dB | nothing |

### 3. Reflections are exempt from all of it

`EngineReflections` builds each echo from `ImageSource.FirstOrder(surfaces, source, listener, c,
into)` — **without the `occluded` callback the method offers** — so a mirrored path is never checked
for anything standing between the car and the wall or between the wall and the listener. The echo
voice it creates (`Make`) carries `Occlusion = 0`, its id is below −600000, and
`ClientAudioSystem` step 5 skips every id under −5000, so it is never sent to the worker and
`SetAcousticPath` never touches it. Its level is `direct.Volume` times the surface's own gain
(`r.Gain * PathLength / directDist`, clamped to 1), with the same MinDistance and Range as the car.

So the moment the simulator silences a car by 26 dB and takes its top three octaves, that car's
reflection off the grandstand wall — or off the retaining wall two metres beside it — keeps playing
at full level and full brightness. Thirty muffled or silent cars and twenty bright echoes of them is
exactly "I hear the ghost of the cars' reflections".

There is a second, smaller way the echoes are wrong: `EngineEchoState.Render` reads the source ring
at `Source.Played − back`, and `Played` advances at the DIRECT voice's consumption rate — which is
pitch-scaled by that voice's Doppler. An echo therefore carries the direct path's Doppler on top of
its own path-length slew. A reflection should have only its own.

### 4. A fence is not a wall

The sim path sets `ApertureFactor = 1f // sim owns occlusion EQ; bypass the diffraction LPF`, so the
one diffraction mechanism the engine has is switched off exactly when the simulator is on. Steam
Audio's direct stage is line-of-sight plus transmission; it has no edge diffraction. A 130 dB engine
20 m behind a 0.9 m wall is a few dB down in the real world (Maekawa: the path difference over the
top of a knee-high wall is centimetres, which is 5–8 dB at engine frequencies), not 26 dB down with
its top gone. Every map with a parapet, a hedge, a parked car, a low fence or a raised kerb will do
this to whatever drives past it.

### Why it was never seen in the lab or the spikes

`--speedway` has no `AsyncAcousticWorker`, so it has no occlusion at all: it is permanently in the
"first hundred seconds" state, which is the one that sounds right.

### How to confirm it in one run

Run the client with `OPENFPS_AUDIO_DEBUG=1`. Before the `Built Steam Audio scene` line there are no
`[SAWORKER]` lines; after it every source prints `[SAWORKER] e<id> vis=<0..1>`. Cars on the far
straight from the infield should read `vis=0.00`, cars on the same straight about `0.4–0.6`, cars in
the turns `1.00`. The `[ADBG]` lines then show the `occ=` each engine voice is being rendered with.
Nothing in the reflection voices will show a value, because none is ever computed for them.

---

## Issue 2 — a close, fast car stops for a moment as it crosses

### Confirmed by reading: the position is a 30 Hz staircase, and the dead-reckoning fix is inert

The fix in session 5 (`FmodAudioProvider.DeadReckon`) carries a source forward on its velocity from
`ActiveSound.LastAttributeAt` — the moment its position was last written — so the 250 Hz loop sees
continuous motion between game-thread updates. It assumed `LastAttributeAt` is stamped when a NEW
position arrives. It is not. The chain, as it actually runs:

1. `Program.GameLoop` runs `SimStep` on a fixed 33.3 ms step; `ClientWorldState.UpdateInterpolation`
   is called only from there. Remote positions therefore change **30 times a second**, full stop.
2. `ContinuousUpdate` → `ClientAudioSystem.Update` at ≤ 60 Hz (measured 45–50 Hz: the 21–22 ms
   placement gap on every census) submits every car — so roughly every other submit carries a
   position that has not changed.
3. The submission is queued to the audio thread, where `VoiceManager.Submit` stores it and
   **`VoiceManager.Process`, which runs on every 250 Hz tick, calls
   `_audio.UpdateSpatialAttributes(status.Emitter)` for every playing voice with the same stored
   emitter.** `FmodAudioProvider.UpdateSpatialAttributes` does `active.LastAttributeAt = now` and
   `active.Position = emitter.Position` unconditionally.

So every engine voice is re-stamped every 4 ms with a position that is up to 33 ms old, and
`DeadReckon` sees an age of at most 4 ms. The reckoning can never carry the car further than a
quarter of a metre. The 250 Hz loop is once again writing the same pitch and the same direction
eight times over and then jumping — as it was before session 5, but now on a 33 ms grid rather than
22 ms.

The step goes as v²/d, which is why it is only the close, fast ones. At the closest point of a pass,
per 33 ms hold (c = 343 m/s):

| miss distance | 235 km/h (the field's telemetry) | 300 km/h |
|---|---|---|
| 3 m | 47 m/s radial swing → **14 %, 2.3 semitones**, thirty times a second | 23 % |
| 5 m | 8.4 %, 1.4 semitones | 13 % |
| 10 m | 4.1 %, 0.7 semitones | 6 % |
| 25 m | 1.6 % | 2.6 % |
| 50 m | 0.8 % — a glide | 1.3 % |

The image does the same thing: at 3 m the bearing swings 60° in 33 ms, so a pass across the face is
five or six freeze-and-jump cycles rather than a sweep. Freeze, jump, freeze, jump is the shape of
"stops, then continues". The test `AClosePassGlidesInPitchInsteadOfStepping` passes because it is a
pure arithmetic model of the reckoning with a fresh timestamp per update; it never runs the voice
manager, the provider, or their clocks.

Per-voice pathing cannot be what holds a car either — see issue 1, an engine never reads it — but it
is worth knowing that for non-engine emitters the pathing redirect *does* land, one frame stale, in
`ApparentPosition`.

### Two things the log shows and cannot explain

At 19:48:14 and 19:48:58 the field starved: 332 and 270 blocks in a second across ~50 engine and echo
voices, which is roughly **150 ms of ramped silence for every car at once**, with the mixer's own dsp
figure spiking to 34 % and 20 %. GC was not the cause (0 gen2, 5–8 ms paused). The reflection
simulator runs with `numThreads = 1`, so it is not saturating the cores. Six dedicated producer
threads all missing 150 ms together is a process-wide or machine-wide stall, and nothing in the
process currently times its own scheduling. A car crossing at 3 m takes about 150 ms to cross, so
one of these landing mid-pass is also "it stopped, then carried on" — but it would be global, and it
happened twice in thirteen minutes, not on every pass.

The one 160 ms placement stall (19:44:47) is the bake finishing and the game thread absorbing thirty
first results and a scene swap in one iteration — a one-off, and part of issue 1.

### What the instruments cannot see, which is why this took a session

- The "engine voice has not been repositioned for N ms" check samples staleness at the instant of the
  5-second report. A hold that started and ended between reports is invisible. It should track the
  maximum hold per voice over the interval.
- `InterpolationStalls` is logged at Debug and the client logs at Information. It has never been
  read.
- Nothing measures the age of the position a voice is being placed at — which is the number that
  matters, and which the stamp in `UpdateSpatialAttributes` currently lies about.
- There is no per-pass trace. One CSV of the nearest car for one lap — time, placed position,
  bearing, pitch, visibility, at 250 Hz — would have shown both issues in an hour.

### How to confirm it

Watch a single pass with the attribute pass logged at 250 Hz (`[ADBG]` is 1-in-60 today, which is
too coarse; it needs a per-source trace mode). The expectation from the reading above: the bearing
and the pitch hold for 8 consecutive ticks and jump, at a 30 Hz cadence, with the jump size matching
the table. If instead the hold is 100 ms or more and lands on every car at once, it is the
interpolation clock or a producer stall, and the fix is different.

---

## What generalises — the rules for any map anyone builds

These are the things that have to be true of the *engine* for the speedway not to need a special case.
Each one is stated as the invariant, then what currently breaks it.

1. **A reflection obeys the same physics as the direct path.** A mirrored path is obstruction-tested
   (`FirstOrder` already takes the callback), a reflection of an occluded source is occluded with it,
   and a reflection carries only its own Doppler. Today echoes are never tested, never occluded, and
   inherit the direct voice's pitch through the ring's play position.

2. **The acoustic source point is the emission point.** Occlusion is asked about where the sound comes
   out — the exhaust, 0.3 m up — not the entity's origin on the ground, and a volumetric radius never
   reaches below the surface the emitter rests on. Today it is asked about `Transform.Position` with a
   0.5 m sphere, which is half underground for anything that drives.

3. **A barrier attenuates by its geometry, not by a boolean.** Line-of-sight blocked by a low edge is
   a diffraction loss that depends on path difference and frequency, with a floor that a knee-high
   wall cannot go under. The simulator's visibility is an input to that model, not the answer. Today
   the diffraction stage is bypassed whenever the simulator is on.

4. **The acoustic model never changes under the listener.** Nothing that takes more than a frame may
   gate the direct stage: bake pathing off the worker (or drop it: nothing reads it for a vehicle),
   deliver occlusion from the first frame, and if a better model becomes available later, cross-fade
   into it. Any period in which sources are being served a fallback is logged as a transition, the way
   the worker already logs a degraded simulator.

5. **A moving source is timestamped by when it was sampled, not by when it was submitted.** The
   emitter carries the interpolation time of its position; dead reckoning runs from that; re-submits
   and the voice manager's per-tick refresh cannot reset it. Better still, the audio thread
   interpolates the server snapshots itself at its own rate — the snapshot buffer is the truth and a
   30 Hz copy of it is not. This is the same rule for a car, a drone, a train and a running NPC.

6. **The map is validated against the things that drive on it.** The server validates prefabs at
   load; it should also walk every declared track and warn when the line passes through a solid
   collider or comes within a vehicle's width of one. The speedway would have failed that check.

7. **Instruments report the worst over the interval, not the state at the report.** Maximum hold per
   voice, maximum result age per source, interpolation stalls, and a producer-side scheduling gauge
   (time between two consecutive `Produce` calls on the same thread) all belong on the census line.

Nothing above mentions a car, a track or a wall. That is the test: if a rule needs to know what the
map is, it is a special case and it will be wrong on the next map.

## Vehicles as prefabs — and their parts

A vehicle today is not a prefab. It is an entry in the map's `Vehicles` list that `VehicleSystem`
turns into an entity with a non-solid box collider, a `SoundEmitterComponent` (`engine:<preset>`,
range from the engine's loudness, MinDistance 3) and a `VehicleComponent`. The engine preset lives in
`OpenFPS.Common/Engines.cs` and the body in `Vehicles.cs`. None of that goes through the validated
prefab spec, which is why a car has no authored emission point and its occlusion probe ends up at its
origin on the ground.

It should be a prefab, and the question of whether its components should be prefabs too has a clear
answer: **the vehicle is the prefab; its components are slots on it, not entities of their own.**

- What belongs on the vehicle prefab: the body collider (and the rule that a thing which emits sound
  is excluded from the acoustic mesh already exists in `BoxesFromWorld`), the body material (a car is
  a reflector for other cars), the engine preset, and a list of **emitter slots** — exhaust, intake,
  tyres, horn, siren, radio — each with a local offset, a directivity, and a level. The exhaust slot's
  offset is what fixes rule 2 with no special case: the occlusion probe goes where the slot says.
- Why the parts are slots rather than separate prefabbed items: they are not placeable on their own,
  their position is derived from the parent every tick, and their lifetime is the parent's. A
  `ParentSystem` exists, but a child entity per exhaust pipe is thirty extra entities in every
  broadcast for no authoring benefit. The lab's rig already treats intake and exhaust as separate
  sources mixed into one voice (`FrontMix`); the slot list is the authored form of that.
- What stays map-level: the route (a `Track` reference, lane, start offset) and the driver (top speed,
  grip, brake). Those are how a *placed* vehicle behaves on *this* map, which is exactly what a map's
  entity entry is for. A road car placed in a city map and the same prefab placed on the speedway
  share everything except that entry.

That also answers the editor question: a vehicle becomes an item in the same prefab list as a wall or
a PA speaker, with the same validation, and picking it drops a thing that already knows where its
sound comes out.

## What not to do

- Do not clamp or hide the occlusion for engines ("cars are loud, don't occlude them"). That is the
  `EchoCarLimit` mistake again: right here, wrong on the next map, and it leaves the model that
  silenced them in place for everything else.
- Do not raise the source probe by a fixed height. The height belongs to the emitter slot.
- Do not lengthen the dead-reckoning cap to hide the re-stamp. The stamp is wrong; fix the stamp.
- Do not move the grandstand and call issue 1 fixed. The map is wrong *and* the engine let it be
  inaudible for a hundred seconds and then made it worse than it is.

## Recommended order

1. Stamp positions with their sample time and stop re-stamping in `VoiceManager.Process` (rule 5).
   Add the per-pass trace and the max-hold instrument. Listen to one pass. This is issue 2 and it is
   small.
2. Stop the pathing bake from gating the direct stage — off the worker thread, or removed until
   something reads it (rule 4). Log the transition. Listen for the cliff at 100 s to be gone.
3. Move the occlusion probe to the emitter's emission point with a ground-aware radius (rule 2), and
   put diffraction back on the sim path (rule 3). Now listen to the front straight from the infield.
4. Pass the obstruction callback to `FirstOrder` and give echoes the direct path's occlusion and only
   their own Doppler (rule 1). Now listen for ghosts.
5. Fix the generator's grandstand setback and add the track-vs-collider check to map load (rule 6).
6. Vehicle prefab with emitter slots.

Then look at the two starve bursts with the scheduling gauge from rule 7, which are a separate and
so far unexplained fault.

---

# What was applied (2026-09-14, session 7)

The diagnosis above was written with no code changed. This section records what was then done about
it, in the recommended order, and what was deliberately left. Everything below is in the working tree
and the full suite (355 tests) passes.

## 1. A position is timestamped by when it was SAMPLED (rule 5) — issue 2

- `SpatialEmitter.PositionSampledAt` carries the interpolation time of the position it holds.
  `ClientWorldState` stamps it whenever `UpdateInterpolation` (or `SyncState`) actually moves
  something, and it rides on `WorldSnapshot.PositionsSampledAt` so anything holding a snapshot is
  holding its age. `ClientAudioSystem` puts it on every entity-attached emitter.
- `FmodAudioProvider.UpdateSpatialAttributes` now stamps `LastAttributeAt` from that value rather
  than from "now". `VoiceManager.Process` is unchanged and did not need to be: re-applying the same
  emitter now re-applies the same age, so the 250 Hz refresh is idempotent instead of amnesiac.
  An emitter with no sample time (an event, a UI sound, a reflection placed this instant) is treated
  as fresh, which is the old behaviour exactly.

**And a second cause of the same fault, found while fixing the first.** `LastAttributeAt` and
`DeadReckon` were both measured on `_loadClock` — a `Stopwatch` that `ReportMixerLoad` **restarts
every 250 ms**. For most of every quarter second, "now" was smaller than the stamp, the age came out
negative, and `DeadReckon` returned the position unchanged. The session-5 fix was inert twice over.
All timestamps now come from `OpenFPS.Common.AudioClock`, a process-wide monotonic clock that nothing
restarts; the load timer keeps its own stopwatch and is named for what it does.

`MaxDeadReckonSeconds` went from 0.05 to 0.08 — not to hide anything, but because with an honest
stamp a position legitimately reaches one simulation step (33 ms) plus one audio-update period
(22 ms) old before a newer one exists anywhere in the process, and a cap under that is a hold.

**Instruments** (rule 7): the provider tracks `WorstPositionAge` per voice, the maximum over the whole
report interval rather than the value at the instant of the report, and warns above 60 ms naming the
voice. And `OPENFPS_AUDIO_TRACE=<entityId>` prints an `[ATRACE]` CSV line at the full attribute rate
for one voice — time, placed position, bearing, distance, Doppler, position age, occlusion. A
staircase is eight identical rows and a jump, thirty times a second; a glide is not. That is the
per-pass trace the diagnosis said would have found this in an hour.

## 2. The pathing bake no longer gates the direct stage (rule 4) — issue 1, part 1

`SteamAudioSimulator.SetScene` sets the scene and commits, and nothing else. `BeginProbeBake` runs the
probe generation and `iplPathBakerBake` on a `Lowest`-priority background thread; the finished batch is
published, and `CommitPendingProbes` — called by the worker between runs, because the simulator is
single-threaded by contract — attaches it and logs the moment pathing comes alive. Until then
`PathingReady` is false, pathing is simply not part of the simulation, and **every source gets its
direct occlusion from the first tick**. `Dispose` joins the bake thread so nothing is freed underneath
native code.

The bake is also bounded: probe spacing is derived from a `MaxProbes` budget rather than fixed at 2 m,
so a bigger map gets coarser pathing instead of an unbounded bake. The speedway's 720 x 420 m asked
for about seventy-five thousand probes; it now asks for eight thousand.

## 3. The probe is at the emission point, with a radius that fits (rule 2) — issue 1, part 2

- `SoundEmitterComponent.Offset` — the emitter slot, authored per prefab (`EmitterOffset` in the
  prefab schema), in the entity's own frame. `VehicleProfile.ExhaustOffset` supplies it for a vehicle
  preset, so the 0.3 m height now lives with the vehicle description instead of being an unexplained
  literal in two files.
- `AudioEmission.PointFor` is the single answer for where a sound comes out, used by BOTH the voice
  and the occlusion probe in step 5. They cannot disagree again, which is what they were doing.
- `AudioEmission.OcclusionRadiusFor` shrinks the volumetric radius to the clearance the emitter has
  above what it is resting on, and `AcousticRequest.SourceRadius` carries it per source into
  `SetSourceInputs`. A tailpipe 0.3 m up gets 0.3 m of sphere, entirely in the air. No probe is lifted
  by a fixed height.

## 4. A barrier attenuates by its geometry (rule 3) — issue 1, part 3

`OpenFPS.Common/Diffraction.cs`: Maekawa insertion loss from the path difference, per band, with the
5 dB grazing value and the 24 dB single-screen ceiling falling out of the curve rather than being
imposed. `PathDifferenceAroundBox` finds the shortest route past one box — over a single edge for a
thin wall, and over two for anything with depth, because every over-the-top candidate for a
sixteen-metre-deep grandstand passes through the building itself. Routes that dip below both endpoints
are rejected: sound does not tunnel under something standing on the ground, and without that rule an
obstacle's buried bottom edge always wins and a thirty-six-metre tower measures as free.

In `AsyncAcousticWorker.BuildSimPath` the simulator's visibility became an INPUT: each band keeps the
better of transmission (through the material) and diffraction (round the edge), and the dry level
cannot fall below what the loudest band still delivers. A 0.9 m pit wall now costs single-digit
decibels weighted to the top end instead of 26 dB and three octaves. `ApertureFactor` stays at 1 on
the sim path — it models a sound squeezing through an OPENING, a different phenomenon, and the band
gains already carry the barrier's frequency dependence.

## 5. Reflections obey the same physics as the direct path (rule 1) — issue 1, part 4

- `EngineReflections.FirstOrderNear` now passes the obstruction callback `ImageSource.FirstOrder` has
  always offered. Both legs of every mirrored path are tested against the same box list the surfaces
  were built from.
- `ApplyPath` gives each echo voice the direct path's occlusion and per-band EQ, with the echo's own
  apparent position and path length. They were the one thing on the map exempt from the acoustic
  model: never sent to the worker (their ids are below the threshold step 5 scans), never given a
  path, permanently unoccluded and full-bandwidth. Thirty muffled cars and twenty bright copies of
  them is exactly what "the ghost of the cars' reflections" was.
- The echo's Doppler: the diagnosis above says an echo wrongly inherits the direct voice's. Reading it
  through, **that half is not a fault**. The echo reads the source ring at `Played − delay`, `Played`
  is the emission time whose DIRECT sound is arriving now, and `DelaySeconds` is the DIFFERENTIAL
  delay — so the read rate is `1 − dτ_echo/dt`, which is the reflection's whole Doppler, correctly.
  What was wrong is that the echo voice was ALSO given a channel pitch from the listener's motion,
  counting that component twice. Reflection voices now play at pitch 1.0 and take their Doppler
  entirely from the ring.

## 6. The map is validated against the things that drive on it (rule 6)

- `OpenFPS.Common/TrackClearance.cs` walks a closed route at 4 m intervals, across the usable lane
  band and through the height band a vehicle's body occupies, against every solid box. Grown sideways
  by the vehicle's half width so "close enough to clip it" fails too, and never grown vertically —
  the road is a solid box directly underneath. `MapManager.ValidateTracks` runs it at load, warns in
  the map author's terms, and publishes `TrackObstructions` so a test can hold a shipped map to it.
  `EveryShippedTrackIsDriveable` does.
- `tools/gen_speedway.py`: the grandstand setback is measured from the FURTHEST point of the wall, not
  the nearest. The front straight is a tangent between circles of different radii, so its wall runs
  from z = −NZ·(R12+OUT_R) to z = −NZ·(R34+OUT_R) — forty-six metres apart — and a setback taken from
  the near end put the deck's front face twenty-eight metres INSIDE the wall line. Checked against the
  old numbers, **138 sampled points of the lap were inside the grandstand deck**; the regenerated map
  has none.

## What was deliberately not done

- **Vehicles as prefabs with emitter slots (step 6 of the order).** The acoustic half of it is done —
  the emitter offset is now a general, authored property of any emitter and a vehicle preset supplies
  its own exhaust slot — but a vehicle is still a map-level `VehicleData` rather than an entry in the
  validated prefab list, and there is still only one slot rather than a list (exhaust, intake, tyres,
  horn). That is a feature, not a fix, and it is independent of both reported faults.
- **The two starve bursts** at 19:48:14 and 19:48:58. Still unexplained, still needing the
  producer-side scheduling gauge from rule 7. Nothing here would have changed them.
- **A borrowed distant engine voice** (`ClientAudioSystem.DistantEngine`) reads another car's ring
  buffer, so it inherits THAT car's Doppler and then applies its own — the double-count that was just
  removed from reflections, in a place where the two components are not even about the same path.
  Fixing it needs the borrowed voice to advance its own read cursor rather than follow the source's
  play position, which is a change to a path that is currently working and was not part of either
  report. Written down here rather than done quietly.

## How to confirm it

1. `OPENFPS_AUDIO_DEBUG=1`. The `[SAWORKER] vis=` lines should start in the first second or two, not
   at +100 s, and `[AcousticWorker] Built Steam Audio scene` should be immediate. The pathing bake
   reports separately, later, and nothing waits for it.
2. Stand in the infield and listen down the front straight. Cars behind the pit wall should be
   muffled and clearly there, not gone. `vis=0.00` in the log is now an input, not a verdict.
3. Walk with the field and listen for a car passing at a few metres. `OPENFPS_AUDIO_TRACE=<car id>`
   gives the CSV; the bearing and Doppler columns should move on every row rather than in eights.
4. Watch for `Voice N was placed at a position X ms old` in the log. It should not appear.
