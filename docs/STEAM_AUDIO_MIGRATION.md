# Migration: hand-rolled acoustics → Steam Audio simulation

_Decision (2026-06-28): replace the hand-rolled occlusion / reflection / portal-pathing layer with
Steam Audio's `iplSimulator` so portal/occlusion/reflection behaviour is physically correct instead of
hand-tuned. The project already links `libphonon` but currently uses **only** its HRTF binaural stage._

## Why

Tuning the hand-rolled `SpatialAcoustics` / `AcousticPathfinder` / reverb-bus system blind produced a
whack-a-mole of artifacts that are inherent to the approximation, not fixable by tuning:
- "Dead zone" at region boundaries (neither the through-door path nor the inside path is active).
- A loud source through an **open** door reads muffled/quiet (occlusion conflates "door is small" with
  "door is blocked").
- No directional-beam-through-an-opening ("yelling at an angle through a door").
- Building reflections inaudible / scattered.

Steam Audio's simulation models all of this from real geometry: occlusion, transmission, **pathing**
(sound arriving from the opening it came through), and ray-traced reflections, plus source directivity.

## Current binding surface (`Phonon.cs`)

Bound today (HRTF only): `iplContextCreate/Release`, `iplHRTFCreate/Release`,
`iplBinauralEffectCreate/Apply/Release`, `iplAudioBuffer*`.

**Available in `lib/libphonon.so` but NOT yet bound** (verified via `nm -D`): `iplSceneCreate/Commit`,
`iplStaticMeshCreate/Add/SetMaterial`, `iplSimulatorCreate/SetScene/SetSharedInputs/Commit`,
`iplSimulatorRunDirect/RunReflections/RunPathing`, `iplSourceCreate/Add/SetInputs/GetOutputs`,
`iplDirectEffectCreate/Apply`, `iplPathEffectCreate/Apply`, `iplReflectionEffectCreate/Apply`,
`iplReflectionMixer*`, `iplProbe*` (for pathing/baked reverb).

## ⚠ Blocker to resolve first: version mismatch

`Phonon.cs` `STEAMAUDIO_VERSION` = **4.8.1**; the shipped `lib/libphonon.so` is **4.4.0**. The HRTF
structs are stable across that range so it works today, but the **simulation** structs are not. Pick one
before writing any sim binding:
- (A) Drop Steam Audio **4.8.x** `libphonon.so`/`libphonon.dll` into `lib/` and bind to the 4.8 API, or
- (B) Bind to the **4.4.0** API/structs that ship today.
Recommend (A) so the version constant matches and we track the current SDK.

## Phased plan

**Phase 0 — resolve version** (above). Confirm `iplContextCreate` succeeds with no version warning.

**Phase 1 — headless occlusion spike** (de-risk on Linux, like the original HRTF spike).
New `AudioLab --sim-occlusion`:
1. Create context (already have), scene (`iplSceneCreate`, type DEFAULT).
2. Add one static mesh: a single wall quad (4 verts / 2 triangles) + a material; `iplStaticMeshAdd`;
   `iplSceneCommit`.
3. `iplSimulatorCreate` (flags: DIRECT), `iplSimulatorSetScene`, `iplSimulatorCommit`.
4. `iplSourceCreate`; set source + listener `IPLCoordinateSpace3`; `iplSourceSetInputs` with
   `IPL_SIMULATIONFLAGS_DIRECT` + occlusion (RAYCAST/VOLUMETRIC) + transmission.
5. `iplSimulatorSetSharedInputs` (listener), `iplSimulatorRunDirect`, `iplSourceGetOutputs`.
6. Print the returned occlusion/transmission as the source moves behind vs beside the wall.
   PASS = occluded behind the wall, clear beside it. No ears needed.

**Phase 2 — pathing spike**: add `IPL_SIMULATIONFLAGS_PATHING` + a probe batch over the scene; verify
the pathing output direction points at the opening when the source is in another "room".

**Phase 3 — scene from the game world.** Build the `IPLScene` from the server geometry the client
already receives (`EntityDefinition` colliders/walls → triangle meshes + acoustic materials, reusing
`AcousticRegistry` absorption/scattering). Rebuild on map load. (`AcousticVolumeGenerator`'s voxel grid
becomes unnecessary for audio once this lands.)

**Phase 4 — per-source simulation in `FmodAudioProvider`.** One `IPLSource` per `ActiveSound`. Each
audio frame: update source/listener coordinates, run the simulator (on the audio/worker thread),
`iplSourceGetOutputs`, and apply:
- `iplDirectEffect` (occlusion + transmission EQ) — replaces the hand-rolled occlusion EQ.
- `iplPathEffect` (arrival direction through the opening) — feeds the **HRTF direction** so through-door
  sound comes from the doorway automatically (replaces the cross-region snap hack).
- `iplReflectionEffect` + `iplReflectionMixer` (early reflections off buildings) — replaces the
  hand-rolled reflection emitters.
Keep the existing `iplBinauralEffect` HRTF as the final stage. Keep the voice **pool** model (pre-alloc
sources/effects; never create/free under the mixer callback — same crash class we already fixed).

**Phase 5 — retire hand-rolled code.** Remove/disable `SpatialAcoustics`, `AcousticPathfinder`, the
reflection-emitter generation, and the 3D reverb-bus positioning, keeping only what the simulator
doesn't (e.g. UI region readouts). Re-validate occlusion/portal/reflection by ear.

## Risks / notes
- Simulation runs rays on a worker; budget it (Steam Audio has a thread-pool + per-frame source cap).
- Reflections/pathing are heavier than direct — gate by distance/importance (the existing per-source
  update-rate scaling already does this).
- Mixed model is fine during migration: direct-effect occlusion first (Phase 4a), then pathing, then
  reflections — each independently testable headless before wiring to ears.
- The voice-pool lifetime lesson applies to `IPLSource`/effects too: pre-allocate, reuse, free only at
  shutdown.
