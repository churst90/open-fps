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

## Phase 0 — version: RESOLVED (no mismatch)

The `"4.4.0"` string in `libphonon.so` is a red herring (an embedded reference). The shipped
`lib/libphonon.so` is **byte-identical to the Steam Audio 4.8.1 core-SDK** linux-x64 binary, and
`iplContextCreate` already accepts the 4.8.1 version constant — so the lib **is** 4.8.1 and matches the
4.8.1 headers (`~/Downloads/steamaudio_extract/steamaudio/include/phonon.h`). The simulation structs are
bound verbatim from those 4.8.1 headers in `PhononSim.cs`. No lib swap needed.

## Phased plan

**Phase 0 — resolve version** (above). Confirm `iplContextCreate` succeeds with no version warning.

**Phase 1 — headless occlusion spike: DONE ✅** (`AudioLab --sim-occlusion`, `SimOcclusionSpike.cs`).
One-wall scene → simulator → source → `iplSimulatorRunDirect` → read `IPLDirectEffectParams.occlusion`.
Result: visibility 0.00 behind the wall, 1.00 beside it. Proven on Linux; struct layouts in `PhononSim.cs`
are valid (no crash, correct values). **Note: `occlusion` is a VISIBILITY/gain factor — 1 = clear,
0 = fully blocked — and the direct effect multiplies the signal by it** (NOT "fraction occluded").
Original step list (for reference):
1. Create context (already have), scene (`iplSceneCreate`, type DEFAULT).
2. Add one static mesh: a single wall quad (4 verts / 2 triangles) + a material; `iplStaticMeshAdd`;
   `iplSceneCommit`.
3. `iplSimulatorCreate` (flags: DIRECT), `iplSimulatorSetScene`, `iplSimulatorCommit`.
4. `iplSourceCreate`; set source + listener `IPLCoordinateSpace3`; `iplSourceSetInputs` with
   `IPL_SIMULATIONFLAGS_DIRECT` + occlusion (RAYCAST/VOLUMETRIC) + transmission.
5. `iplSimulatorSetSharedInputs` (listener), `iplSimulatorRunDirect`, `iplSourceGetOutputs`.
6. Print the returned occlusion/transmission as the source moves behind vs beside the wall.
   PASS = occluded behind the wall, clear beside it. No ears needed.

**Phase 2 — pathing spike: DONE ✅** (`AudioLab --sim-pathing`, `SimPathingSpike.cs`).
Floor + wall-with-doorway scene → `UNIFORMFLOOR` probes → **`iplPathBakerBake`** (the probe-to-probe
visibility graph; pathing finds nothing without it) → `iplSimulatorRunPathing` → read the path's
order-1 SH + EQ from `IPLPathEffectParams`. Result: a path is found through the opening (W=0.047,
eq≈1.0 = clear). Pipeline + bindings proven. **Gotchas for integration:** (1) `iplPathBakerBake`
requires a non-null progress callback — a null one segfaults. (2) floor geometry must be wound
normal-UP or `UNIFORMFLOOR` places no probes. (3) the SH→world-direction convention still needs the
exact ACN/axis mapping pinned down (the spike reported `-x` for a `+z` path). (4) routing a *bent* path
around a fully-blocked straight line needs probe-graph density/`pathRange` tuning (the straight-through
gap path is found; the off-axis bent one needs work).

**Phase 3 — scene from the game world: DONE ✅** (`AudioLab --sim-scene`, `SimSceneSpike.cs`).
`SteamAudioScene.cs` builds an `IPLScene` from box colliders (each box → 8 verts + 12 tris with an
`AcousticRegistry`-derived `IPLMaterial`; rebuild on map load). `SimSceneSpike.cs` builds the demo
wood-room's walls+doorway as boxes and asserts occlusion against the generated mesh. **Verified
2026-06-28:** visibility 1.00 clear through the open doorway, 0.00 blocked behind the east wall —
PASSED. Next: wire `SteamAudioScene.Build(...)` to the live solid colliders from the `WorldSnapshot`
on map load. (`AcousticVolumeGenerator`'s voxel grid becomes unnecessary for audio once Phase 4 lands.)

**Build-env note (the recurring "build wedges at startup"):** the repo is on an **ntfs3** volume, and
MSBuild's output-write phase hangs in the *kernel* — a `RequestBuilder` thread blocks in `do_truncate`
on ntfs3 holding a lock while the "Parallel Copy" threads wait behind it at 0% CPU (looks like a stuck
compiler server but is a filesystem hang; source *reads* are fine, only *writes/truncates* hang). Fix:
build with output on tmpfs and run the built DLL directly (so `dotnet run` doesn't rebuild→copy back
onto ntfs3):
`dotnet build OpenFPS.AudioLab --artifacts-path /tmp/openfps-art` (succeeds in ~5s), then
`LD_LIBRARY_PATH=/tmp/openfps-art/bin/OpenFPS.AudioLab/debug dotnet /tmp/openfps-art/bin/OpenFPS.AudioLab/debug/OpenFPS.AudioLab.dll --sim-scene`.

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
