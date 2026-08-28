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

*Integration seam (mapped 2026-06-28):* the existing acoustics already run off the FMOD mixer thread —
`ClientAudioSystem.Update` enqueues `AcousticRequest`s to `AsyncAcousticWorker` (its own
`AcousticWorkerThread`), which calls `SpatialAcoustics.CalculateAcousticPaths(WorldSnapshot, …)` and
pushes results back via `IAudioProvider.SetAcousticPath(id, AcousticPathData)`. So Phase 4a runs the
simulator on **that worker thread** (NOT the mixer) and just fills `AcousticPathData` — no mixer-callback
risk. Scene geometry comes from `WorldSnapshot.Entities[*].Definition.Collider` (`IsSolid`, `Shape` Box,
`Size`) + `Transform` + `Definition.Material.Material` → maps 1:1 onto `SteamAudioScene.Box`. Note SA's
`occlusion` is a VISIBILITY gain (1=clear) while `AcousticPathData.Occlusion`/`ActiveSound.CurrentOcclusion`
is "fraction blocked" (provider does `dryVol = 1 - occlusion`), so convert: `pathOcclusion = 1 - visibility`.

  **Phase 4a — runtime engine: DONE ✅** (`AudioLab --sim-perframe`, `SimPerFrameSpike.cs`).
  `SteamAudioSimulator.cs` owns an `IPLSimulator` (DIRECT) + a fixed pool of `IPLSource` handles, created
  & added up front (mutating the source graph needs a commit, so per-tick work never touches it — same
  voice-pool lesson). API matches the worker loop: `SetScene` (lazily creates the pool, rebuildable on map
  change), `AcquireSource`/`ReleaseSource`, then per tick `SetSourceInputs(src,pos)` ×N → `SetListener` →
  `Run()` (one batched `iplSimulatorRunDirect`) → `GetResult(src)` (visibility + 3-band transmission).
  Not thread-safe — single worker thread only; does not own the context/scene. Verified 2026-06-28: two
  sources batched in one `Run` give INDEPENDENT occlusion (room source 0.00 behind the east wall →
  1.00 in the doorway as the listener walks in; open control source stays 1.00), and pool
  release/reacquire returns a working source.

  **Phase 4b — wired into the acoustic worker: DONE ✅** (`AudioLab --sim-worldscene`, `SimWorldSceneSpike.cs`).
  `AsyncAcousticWorker` now lazily creates (on its own thread) an `IPLContext` + `SteamAudioScene` +
  `SteamAudioSimulator`, rebuilds the scene from `SteamAudioScene.BoxesFromWorld(WorldSnapshot)` whenever
  the `AcousticMap` reference changes (per-map-load cost), and on each tick **drains all queued requests**
  (latest per entity), batches one `iplSimulatorRunDirect` for every active source, then reads per-source
  occlusion/transmission. It still calls the hand-rolled `SpatialAcoustics.CalculateAcousticPaths` for
  reflections/portals/air-absorption/room-gain, but **overrides the DIRECT path's occlusion + 3-band EQ +
  transmission bleed** with the simulator result (`occ = 1 - visibility`; `eqBand = v + (1-v)·transBand`;
  `bleed = transLow`). Sources are pooled per entity with a 5 s idle TTL (cleared + released so idle voices
  cost no rays). Falls back to pure hand-rolled occlusion if libphonon is missing or `OPENFPS_STEAMAUDIO_SIM=0`.
  No FMOD-mixer-thread involvement — all Phonon sim calls stay on `AcousticWorkerThread`; freed in
  `Dispose` after the thread joins. The provider's HRTF positioning, distance, cone and reverb paths are
  untouched. Verified 2026-06-28: `BoxesFromWorld` extracts the 5 wood-room solids and the simulator
  reports blocked-behind-wall (0.00) / clear-through-door (1.00) end-to-end from a `WorldSnapshot`.
  **Not yet validated by ear in the live client** (Linux client is the GTK port in progress); next pass
  should A/B `OPENFPS_STEAMAUDIO_SIM=0` vs on in-game.

  **Phase 4c — pathing → HRTF arrival direction: DONE ✅** (`AudioLab --sim-pathdir`, `--sim-pathframe`;
  `SimPathDirSpike.cs`, `SimPathFrameSpike.cs`).
  - **SH→direction convention PINNED** (the open Phase 2 question). `SimPathDirSpike` runs pathing through
    four scenes whose opening forces a known pure-axis arrival (±x, ±z) and reads the raw order-1 SH; the
    mapping is `worldDir = normalize(-sh[1], sh[2], -sh[3])` (world X = -ACN(m=-1), Z = -ACN(m=+1),
    Y = ACN(m=0)) — agrees 4/4. Locked into `SteamAudioSimulator.PathingWorldDirection`.
  - **Engine pathing.** `SteamAudioSimulator(enablePathing:true)` adds PATHING to the sim+source flags and,
    on `SetScene`, generates UNIFORMFLOOR probes over the scene's world bounds (`SteamAudioScene.BoundsMin/Max`)
    and bakes the path graph (`iplPathBakerBake`, non-null progress cb — null segfaults; built once, re-baked
    on map change). `SetSourceInputs` adds the pathing inputs, `Run` also calls `iplSimulatorRunPathing`, and
    `GetPathing(src)` returns the world arrival direction + energy (`PathResult`). Needs floor geometry wound
    normal-up or no probes are placed (`PathingReady` is then false and pathing silently no-ops).
  - **Wired into the worker.** `AsyncAcousticWorker` now constructs the simulator with pathing; per tick,
    for a source that is occluded (`direct.Visibility < 0.5`) and has a found path, it synthesizes an
    apparent position = `listener + arrivalDir · distance(listener,source)` and overrides the direct path's
    `ApparentPosition`. The provider's existing HRTF code already localizes to `ApparentPosition`, so the
    occluded sound now comes from the doorway it actually arrives through — **no provider change needed**.
    Clear-LOS sources (visibility ≥ 0.5) keep their real position.
  - Verified 2026-06-28/29 headless: `--sim-pathframe` builds the wood-room + floor, the simulator generates
    probes from bounds, bakes, runs pathing, and reports the source arriving from the doorway (+z) with a
    synthesized apparent position. All seven sim spikes pass.
  - **Not yet validated by ear** (same GTK-client caveat). **Known limitation:** bent paths around a fully
    blocked straight line still want probe-density/`pathRange` tuning (Phase 2 gotcha #4); the lined-up /
    through-opening case is solid. Probe positions are not relocated on map change (re-bake only) — tuned for
    single-map sessions.

  **Phase 4d — geometry-driven reverb: DONE ✅** (`AudioLab --sim-reflect`, `SimReflectSpike.cs`).
  Chose the **parametric** path over convolution-in-the-mixer (lower risk, no new mixer-thread DSP, headless-
  testable). Probe finding: `iplSimulatorRunReflections` with `IPL_REFLECTIONEFFECTTYPE_PARAMETRIC` yields
  usable per-band RT60 — a sealed concrete room reads 0.62 s vs 0.10 s in the open. So reflections drive
  *reverb decay* from real geometry instead of the Sabine estimate.
  - `SteamAudioSimulator` now takes independent `enableDirect/enablePathing/enableReflections` flags (each
    `Run` stage gated), adds `GetReverb` (per-band RT60) and the pure `ReverbDecayMs` mapping (longest band →
    FMOD SFXREVERB ms, clamped). `iplSimulatorRunReflections` bound in `PhononSim`.
  - `AsyncAcousticWorker` runs a **second, reflections-only** simulator with a single listener probe on a
    throttle (every 6th tick — reflections are the heaviest stage, so not per-source/per-tick), producing the
    listener room's RT60 → decay ms, exposed via `TryGetListenerReverbDecayMs`.
  - `ClientAudioSystem` pushes that to `IAudioProvider.SetSimulatedReverbDecay`; `FmodAudioProvider` overrides
    the listener-region reverb DSP decay with it (no-op/Sabine when 0). The hand-rolled **discrete reflection
    emitters are retired when SA sim answers for a source**; a reflection path can only come from the
    hand-rolled tracer, so one that *is* present is always rendered (see the per-source fallback note below).
  - Verified headless 2026-06-29 (`--sim-reflect` via the real simulator API: sealed 0.62 s ≫ open 0.10 s) and
    62/62 unit tests pass. **Not yet validated by ear.** **Future:** per-source early-reflection *direction*
    (convolution path) if parametric room reverb proves insufficient; relocate probes on map change.

**Phase 4 is feature-complete (4a–4d), all geometry-driven and behind `OPENFPS_STEAMAUDIO_SIM` with graceful
fallback. The whole of Phase 4 is headless-verified but NOT yet ear-validated — that gate comes when the
Linux/GTK client runs. Phase 5 (below) should follow ear-validation, not precede it.**

**Active-path switch (2026-06-29):** when SA sim is enabled the worker builds the acoustic result from the
simulator (`AsyncAcousticWorker.BuildSimPath`) rather than from the hand-rolled
`SpatialAcoustics.CalculateAcousticPaths` — the old ray-tracer is the `OPENFPS_STEAMAUDIO_SIM=0` /
no-phonon path.

**Per-source fallback (2026-08-28, audit step 3):** "the simulator is active" is decided *per source, per
tick*, not once for the session. When `RunSteamAudio` returns null (unbuilt scene, simulator exception) or
simply has no entry for a source (exhausted `IPLSource` pool, a source added this tick), that source is
routed through `HandRolledPath` — the hand-rolled tracer. It previously received
`SteamAudioSimulator.DirectResult.Clear`, i.e. **zero occlusion**, which in an audio-first game means every
wall in the level silently vanishing for as long as the fault lasts. Related fixes: the listener position is
now taken from the first pending request rather than only from requests that won a source (an exhausted pool
used to drop the whole tick); `ClientAudioSystem` honours reflection paths whenever they are present rather
than gating them on the `SteamAudioActive` flag, so a source that falls back keeps its reflections; and the
degraded/recovered transition is logged (rate-limited) and exposed as `AsyncAcousticWorker.IsDegraded`.

**SIMD level (2026-08-28, audit step 3):** every `iplContextCreate` call site used to pass
`IPL_SIMDLEVEL_AVX2`. Steam Audio does not probe the CPU — it emits code for the level you give it, so on a
non-AVX2 machine that is an illegal instruction inside `libphonon`, not an error code. All call sites now use
`Phonon.DefaultContextSettings()`, whose `simdLevel` comes from `Phonon.DetectSimdLevel()`
(AVX-512 → AVX2 → AVX → SSE4.2 → SSE2/NEON via `System.Runtime.Intrinsics`). The chosen level is named in the
"Steam Audio simulation enabled" log line. The worker also defensively calls `AcousticRegistry.Initialize()` (idempotent) so a
forgotten registry init can't make scene-material lookups throw and silently drop the sim to no-occlusion
(this bit the first ear-test harness run).

**Ear test:** `AudioLab --ear-test` drives the real `FmodAudioProvider` + `AsyncAcousticWorker` with the new
police siren looping inside the wood-room. Interactive (WASD move / J,L turn / Q quit) on a terminal, or a
scripted flythrough when stdin is redirected. Headless run confirmed occlusion 0.00 in the doorway → 0.95
behind the east wall → 0.00 back at the door, end-to-end through the real provider. HEADPHONES; set
`OPENFPS_STEAMAUDIO_SIM=0` to A/B the old spatializer. The full game ear-test path is the GTK client
(`OpenFPS.Client.Gtk`, which inits the registry in `GameSession`).

**Phase 5 — retire hand-rolled code (AFTER ear-validation).** Once 4a–4d are confirmed good by ear in the
live client, remove/disable `SpatialAcoustics`, `AcousticPathfinder`, the reflection-emitter generation, and
the 3D reverb-bus positioning, keeping only what the simulator doesn't yet provide (air absorption, distance
model, region detection / UI readouts) — or migrate those onto the simulator first. Do NOT delete the
hand-rolled fallback before ear-validation: it is the `OPENFPS_STEAMAUDIO_SIM=0` / no-phonon path AND the
per-source fallback for any tick or source the simulator cannot answer (audit step 3) — deleting it would
restore the "zero occlusion for everything" failure mode that step removed.
Discrete reflection emitters are already disabled when SA sim is active (4d), so the main remaining work is
collapsing the now-redundant occlusion ray-tracing and reverb Sabine math once the sim path is trusted.

## Risks / notes
- Simulation runs rays on a worker; budget it (Steam Audio has a thread-pool + per-frame source cap).
- Reflections/pathing are heavier than direct — gate by distance/importance (the existing per-source
  update-rate scaling already does this).
- Mixed model is fine during migration: direct-effect occlusion first (Phase 4a), then pathing, then
  reflections — each independently testable headless before wiring to ears.
- The voice-pool lifetime lesson applies to `IPLSource`/effects too: pre-allocate, reuse, free only at
  shutdown.
