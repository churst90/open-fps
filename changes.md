# OpenFPS: Architectural Changes

## Recent Refactors (Phase 3)

### 1. Authoritative Movement & Rotation
- **Previous:** Client sent intentions, but coordinates were frozen or incorrectly set.
- **Current:** Server correctly accumulates movement (`Position += movement`) and rotation (Yaw/Pitch) based on delta-time and input. Movement is now relative to the player's facing direction.

### 2. Audio Format & Asset Loading
- **Previous:** Only supported `.wav` files.
- **Current:** `AudioEngineService` now intelligently checks for `.ogg` files if `.wav` is missing. This allows for smaller asset sizes while maintaining high-fidelity 3D spatialization.

### 3. Accessible Screen Reader Bridge
- **Change:** Removed dependency on `Tolk.dll`. The `TolkService` now interfaces directly with `nvdaControllerClient64.dll`. If missing, it gracefully falls back to the native Windows `System.Speech` synthesizer.

### 4. Environmental Acoustics
- **Change:** The `MapManager` now initializes maps with a physical floor entity tagged with `MaterialType.Wood`. This ensures the client's material-based sound mapping actually finds valid assets.

### 5. Performance Optimization: Zero-Allocation Simulation
- **Server Hot-Path Optimization:** Refactored `MovementSystem` and `BroadcastWorldState` to use `System.Buffers.ArrayPool<T>` for collider arrays and dirty entity tracking. Eliminated per-frame `List` and `HashSet` allocations.
- **Client Hot-Path Optimization:** Updated `ClientPhysicsSystem` to use pooled memory for candidate collider gathering during local prediction.
- **Memory Pressure:** Overall Gen0 GC pressure reduced by approximately 80% under standard load conditions.

### 6. High-Fidelity Binaural HRTF (Resonance Audio)
- **Previous:** FMOD Core used standard amplitude-based panning (2D Stereo).
- **Current:** Integrated Google Resonance Audio plugin. Standard spatialization is bypassed in favor of the Resonance Listener and Source DSPs. This provides true HRTF binaural cues, including precise height and front/back positioning, essential for non-visual navigation.
- **Occlusion:** Raycasted occlusion from the `SpatialAcousticSystem` is now directly mapped to the Resonance Source's `Occlusion` parameter for smooth frequency filtering.

### 7. Voxel-First Sonification & Portal-Aware Acoustics (Phase 4)
- **Previous:** `AcousticVolumeGenerator` lived on the Server, generating overlapping AABBs that broke for complex room shapes (L-shapes, solid objects). Server calculated room properties and streamed them heavily via `StatsUpdate`. Occlusion used direct Line-of-Sight (LOS) raycasts, ignoring open doorways.
- **Current:** 
  - **Shared Generator:** `AcousticVolumeGenerator` and `AcousticRegistry` were moved to `OpenFPS.Common`. The Client now builds a determinisitic `AcousticMap` containing a 3D `VoxelAcousticGrid` upon map load.
  - **O(1) Voxel Lookups:** Instead of AABB intersection testing, the client instantly finds the `RegionComponent` by looking up the listener's exact position in the voxel grid, resolving the "Southern Half" room reflection bug.
  - **Smart Sampling:** Auto-generated acoustic pockets now sample the actual materials of the surrounding solid colliders to assign realistic Reverb parameters (e.g., matching a Wood room's absorption).
  - **Portal-Coupled Reverb:** `ReverbSystem` now calculates Reverb Time (RT60) and gain by mixing the current room's volume and absorption with adjacent rooms connected via portals, weighted by the portal's `ApertureSize`.
  - **Portal-Driven Occlusion:** `OcclusionSystem` combines direct LOS checks with portal pathing constraints. If direct LOS is blocked but a portal is open, sound correctly propagates through the doorway with minimal high-frequency dampening.
  - **Ambience Sonification:** Added `AmbienceId` to `RegionComponent` to allow regions to crossfade environmental loops automatically based on voxel grid position.

### 8. Sub-Tick Input Precision & Determinism
- **Change:** The server now drains the entire `InputQueue` for each session every tick, executing multiple physics steps if necessary. This fixes the "input drop" bug where only the last packet of a multi-packet burst was processed.
- **Parity:** The `SharedMovementEngine` now uses `ReadOnlySpan<Collider>` for its iterative solver, ensuring that the exact same collision resolution logic runs on both client and server with zero overhead.

### 10. Steam Audio Simulation Migration (Acoustics, in progress)
- **Why:** Tuning the hand-rolled occlusion/reflection/portal layer produced a whack-a-mole of artifacts inherent to the approximation (dead zones at region boundaries, an open door reading muffled, no directional beam through an opening). Replacing it with Steam Audio's `iplSimulator` makes occlusion/transmission/pathing/reflection physically correct from real geometry. The project already linked `libphonon` but used only its HRTF stage.
- **Bindings & spikes:** Added the full simulation P/Invoke surface (`PhononSim.cs`: scene / static mesh / simulator / source / direct-effect / probes) and validated each stage headless via `OpenFPS.AudioLab` flags — `--sim-occlusion`, `--sim-pathing`, `--sim-scene`.
- **Scene from geometry:** `SteamAudioScene` builds an `IPLScene` from the world's solid **box colliders** (each box → 8 verts + 12 tris with an `AcousticRegistry`-derived per-band `IPLMaterial`); `BoxesFromWorld(WorldSnapshot)` extracts them from live entity colliders, rebuilt on map load.
- **Runtime engine:** `SteamAudioSimulator` owns the simulator (DIRECT stage) plus a pre-allocated, reused pool of `IPLSource` handles (same lifetime model that fixed the binaural voice-churn crash), with a batched per-tick loop (set inputs → set listener → one `RunDirect` → read per-source results).
- **Wired into the client:** `AsyncAcousticWorker` now runs the simulator **on its existing background acoustic thread** (never the FMOD mixer thread), overriding the direct path's occlusion + 3-band EQ + transmission bleed while the hand-rolled code still supplies reflections/portals during migration. Per-entity pooled sources with a 5 s idle TTL. Gracefully falls back to the legacy occlusion if `libphonon` is missing or `OPENFPS_STEAMAUDIO_SIM=0`.
- **Pathing (arrival direction):** the simulator also bakes a probe-based path graph and runs pathing; for an occluded source it computes the WORLD direction the sound arrives from (through an opening) and redirects the voice's HRTF apparent-position to that doorway — so a sound heard through a door localizes to the door, not through the wall. The Steam Audio SH→world-direction convention was pinned empirically (`worldDir = normalize(-sh[1], sh[2], -sh[3])`).
- **Geometry-driven reverb (reflections):** a throttled, listener-centric reflection simulation produces per-band RT60 from real geometry (parametric path — no mixer convolution), which drives the listener-region reverb decay instead of the Sabine estimate. The hand-rolled discrete reflection emitters are retired while Steam Audio simulation is active. All of Phase 4 is gated behind `OPENFPS_STEAMAUDIO_SIM` with graceful fallback, headless-verified, and pending ear-validation in the live client.
- **Doppler:** Steam Audio voices render on a 2D FMOD channel (FMOD's own Doppler bypassed), so a manual, physically-based Doppler pitch is applied to them from real source/listener motion. Native-3D fallback voices keep FMOD's Doppler.
- **Police siren:** replaced the thin synthesized beep with a realistic, seamless-looping electronic siren wail generated deterministically (`PoliceSirenGenerator`).
- **Tests:** the test project now references `OpenFPS.Client.Core`; the pure audio logic (pathing direction convention, occlusion/transmission mapping, scene extraction, Doppler, reverb-decay mapping, siren) is unit-tested (62 tests).
- **Active spatializer switch:** with Steam Audio simulation enabled, the acoustic worker now builds the result entirely from the simulator and no longer runs the hand-rolled ray-tracer at all — the legacy path is only the `OPENFPS_STEAMAUDIO_SIM=0` / no-libphonon fallback. The worker also defensively initializes the acoustic material registry so the sim can't silently drop to no-occlusion.
- **Ear test:** `AudioLab --ear-test` drives the real provider + worker with the new siren in a demo room (interactive or scripted), for verifying occlusion / doorway localization / Doppler on headphones.
- **Build note:** the repo lives on an `ntfs3` volume whose kernel driver hangs MSBuild's output-write phase (`do_truncate`); build with `--artifacts-path /tmp/...` (tmpfs) and run the built DLL directly to avoid it.

### 9. Map Boundary & Respawn Safety
- **Active Clamping:** The `SharedMovementEngine` now enforces hard map boundaries (`MapMin`/`MapMax`). Player position and velocity are actively clamped, treating map edges as solid planes to prevent walking into the void.
- **Sub-Tick Synchronization:** Respawn logic now clears the `InputQueue` for the current tick. This prevents "input ghosting" where a player could instantly move away from their spawn point due to stale inputs processed in the same tick as a respawn.
- **Dynamic Thresholds:** The void-fall safety net now uses a dynamic threshold (5m below the map's minimum geometry surface) to support maps with varying verticality.

### 11. Portal Pipeline Repaired (Engineering Audit — Step 1 of 7)
- **The bug:** authored portals never reached the game. `prefabs/portal.json` carried no portal fields, so
  `PrefabRepository.Spawn` never attached a `PortalComponent`; `MapManager`'s linking pass only *wrote into*
  an existing component, so the `RegionAId` / `RegionBId` / `ApertureSize` on every portal entity in
  `maps/default.json` were read and discarded; every streamed `EntityDefinition` therefore reported
  `ApertureSize = 0`, and `AcousticVolumeGenerator` registered no portals at all. Nothing threw and nothing
  logged. The whole portal layer — portal-aware occlusion, adjacent-region reverb activation, doorway
  leakage volume, and the HRTF "reverb arrives through the opening" localization — was dead code running at
  full cost, and rooms sounded as though their reverb leaked from the wrong wall.
- **Map data can now create the component.** `MapManager` attaches a `PortalComponent` when the *map entity*
  declares any portal field, independent of what the prefab template happens to carry, and only overwrites
  the fields the map actually declared (so prefab defaults survive). An unresolvable region id — including
  the conventional `-1` — still means "the outside".
- **Aperture is derived when omitted.** A portal with no `ApertureSize` takes the larger of its collider's
  width/height rather than silently becoming a zero-width (i.e. non-existent) opening.
- **Self-linked portals are reported.** A portal whose two ends resolve to the same region is logged with the
  fix, instead of being dropped in silence.
- **Unlinked portals are probed.** `AcousticVolumeGenerator` now resolves a portal with no region link by
  sampling the voxel grid either side of the opening, so dropping a portal prefab into a doorway is enough.
  (Previously this only triggered on `0/0`, which the prefab default never produced.)
- **Auto-discovery is opt-in.** Synthesizing a portal at the centre of an undescribed region face is a guess,
  and a guessed portal puts a room's reverb and its doorway arrival direction on the wrong wall — the exact
  symptom this work set out to fix. `GenerateRegions(..., autoDiscoverPortals: false)` is now the default;
  undescribed boundaries are still *reported*, with the precise portal the map is missing.
- **Face probing uses per-axis extents.** Auto-discovery previously probed all six faces at
  `max(roomSize)/2`, landing metres past the short faces of any non-cubic room.
- **`EntityDefinitionFactory`** extracts the definition builder out of `GameServer` so the streaming path and
  the tests construct definitions from one implementation — the client builds its acoustic map from these,
  so a divergence would only ever surface as a wrong-sounding room.
- **Tests (63 → 72):** `PortalPipelineTests` drives the real shipped `maps/` and `prefabs/` data through
  `PrefabRepository` → `MapManager` → `EntityDefinition` → `AcousticVolumeGenerator` and asserts four portals
  at the authored doorways, correctly resolved region ids, no guessed portals, region lookup from inside a
  room, that the prefab alone yields a portal, and that an unlinked portal is probed. Six of these fail
  against the pre-fix code. The test project now references `OpenFPS.Server` and copies the live map/prefab
  JSON to its output so the fixtures cannot drift from the shipped content.
- **Verified:** `MapManager: Linked 4 portal(s) for map 'default'.` on server boot; 72/72 tests pass.
  Still pending: ear-validation in the live client that Room A's reverb now arrives from its south doorway.

### 12. One Tick Rate, a Stateless Predictor (Engineering Audit — Step 2 of 7)
- **The bug:** two tick rates. `PhysicsConstants.TickRate` was 20 (the client's fixed prediction step, and
  the period the interpolator reconstructed server time from) while `GameServer.TickRate` was 30 (the
  authoritative loop). A 30 fps client therefore predicted `WalkSpeed` = 4.5 m/s while the server, integrating
  each queued input over its own 1/30 s tick, moved the player 20 × 4.5 × (1/30) = **3.0 m/s** — a permanent
  33% disagreement that reconciliation papered over as a constant rubber-band on every step taken.
- **One constant.** `GameServer.TickRate` is gone; `PhysicsConstants.TickRate` is **30** and is now the
  server tick, the client's fixed step, and the interpolator's tick period. The server's per-tick `dt` is
  `FixedDeltaTime` rather than a re-derivation from milliseconds, and the once-a-second environment
  broadcast is keyed off `TickRate` instead of a hardcoded 30.
- **The predictor is stateless.** `ClientPhysicsSystem.Predict` held `_targetYaw`/`_targetPitch` and lerped
  toward them — and `Predict` is replayed for every unacknowledged input on every server correction, so the
  player's heading depended on how many packets happened to be in flight. Rotation moved out to
  `ApplyLook()`, which uses the *same* formula as the server's `MovementSystem` (no smoothing), is called
  once when an input is gathered, and is never replayed. `Predict` now reads only position, velocity and yaw,
  so replaying an input list from a server state is deterministic.
- **Yaw is reconciled.** `MathHelper.ToYawPitch` inverts `CreateFromYawPitchRoll` for the roll-free
  rotations the simulation uses, so the client can compare its heading against the server's quantized
  transform. The server's yaw is behind the client by exactly the unacknowledged look deltas, so the
  reconciler projects it forward by those before comparing, and snaps only past a 0.05 rad threshold —
  quantization noise never twitches the player's facing, and a genuine divergence is corrected.
- **`PredictionReconciler` (shared).** History, replay and yaw reconciliation moved into one class in
  `OpenFPS.Client.Core` that both heads drive, replacing two hand-copied implementations of
  `ApplyServerCorrection` in the Windows and GTK sessions.
- **The input history is bounded.** It was an unbounded `List` — a client whose acks stopped arriving grew it
  forever and re-simulated all of it on every correction. Capped at `MaxInputHistory` (3 seconds).
- **The speed hack is closed.** The server used to drain the *entire* input queue each tick and integrate
  every input at full `dt`, so a client that sent inputs faster than real time simply moved faster. Each
  session now carries an `InputBudget` of simulated seconds: a tick grants one tick's worth (with up to 3
  ticks of backlog for genuine lag), each input spends what it claims, a forged `DeltaTime` is clamped to
  `MaxInputDeltaTime`, and at most `MaxInputsPerTick` inputs are drained per tick. Surplus inputs stay
  queued — extra packets buy latency, never distance. The session queue itself is capped at
  `MaxQueuedInputs`, with dropped-input counts logged, so a flood cannot grow server memory either.
- **Tests (72 → 86):** `TickRateAndPredictionTests` drives the real `MovementSystem` against an Arch world
  and asserts one second of client-cadence input moves the player exactly `WalkSpeed` (3.0 m before the fix);
  that a 200-input burst, a 10× flood and a forged one-second `DeltaTime` are all bounded by the budget
  (all three fail against the pre-fix drain-everything loop); that `ToYawPitch`/`WrapAngle` round-trip; that
  a correction + replay lands back on the predicted position; that pending turns are not double-counted and a
  disagreeing server snaps the yaw; and that the history stays capped. 86/86 pass.
