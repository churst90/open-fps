# OpenFPS: TODO List

## Phase 1: Foundation (COMPLETED)
- [x] Scaffolding: .NET 10.0 Solution
- [x] Networking: LiteNetLib 1.2.0 established
- [x] Serialization: MemoryPack messaging
- [x] ECS Backbone: Arch world initialized

## Phase 2: Audio & Accessibility (COMPLETED)
- [x] Client: Integrated FMOD with Resonance Audio for 3D HRTF
- [x] Client: Integrated Tolk (Direct NVDA Bridge) with SAPI fallback
- [x] Client: Implemented SpatialAcousticSystem (Raycast Occlusion & Diffraction)
- [x] Client: Automated Distance-Based Footsteps
- [x] Common: Support for `.ogg` spatial samples

## Phase 3: World & Physics (COMPLETED)
- [x] Server: Authoritative Accumulative Movement System
- [x] Server: Authoritative Rotation System (Yaw/Pitch)
- [x] Server: Map Manager with Material-tagged environments
- [x] Server: User Registration and Login flow

## Phase 4: Gameplay Systems (COMPLETED)
- [x] Common: Defined Item/Inventory components
- [x] Server: Implement Inventory/Take/Drop commands
- [x] Client: J/K/L/O controls for facing direction
- [x] Client: Escape menu for graceful exit
- [x] Server: Implement AISystem (Idle/Wander/Chase)
- [x] Server: Optimized Movement System (Sub-tick precision & Zero-allocation)
- [x] Server: Optimized World Broadcasting (ArrayPool & Buffer Re-use)
- [ ] Server: Data Persistence (Auto-save world state)

## Phase 5: Production Polish & Optimization (IN PROGRESS)
- [ ] Server: Full Persistence (SQLite or JSON per-user save/load)
- [ ] Client: Advanced Spatial Reverb (Room Coupling)
- [ ] Client: Interpolation/Extrapolation for non-player entities
- [ ] System: Automated Stress Testing (Simulated 50+ bots)

## Audio: Steam Audio Simulation Migration (IN PROGRESS)
Replacing the hand-rolled occlusion/reflection/portal layer with Steam Audio's `iplSimulator` so
portal/occlusion/reflection behaviour is physically correct. See `docs/STEAM_AUDIO_MIGRATION.md`.
- [x] Phase 0–1: simulation P/Invoke bindings (`PhononSim`) + headless occlusion spike (`--sim-occlusion`)
- [x] Phase 2: pathing spike — probe bake + `RunPathing` (`--sim-pathing`)
- [x] Phase 3: scene builder from box colliders (`SteamAudioScene`) + headless verify (`--sim-scene`)
- [x] Phase 4a: per-source simulation engine (`SteamAudioSimulator`) with pooled sources (`--sim-perframe`)
- [x] Phase 4b: wired into `AsyncAcousticWorker` — scene from `WorldSnapshot`, batched per-source direct
      occlusion/EQ/transmission override on the worker thread (`--sim-worldscene`)
- [x] Phase 4c: pathing — SH→world-direction convention pinned (`--sim-pathdir`), probe/bake in the
      simulator, occluded sources' HRTF apparent-position redirected to the opening (`--sim-pathframe`)
- [x] Phase 4d: geometry-driven reverb — parametric reflection RT60 drives the listener-region reverb
      decay; discrete hand-rolled reflection emitters retired under SA sim (`--sim-reflect`)
- [ ] Phase 4c: tune bent-path routing (probe density / `pathRange`) for fully-blocked straight lines
- [ ] Phase 4 (ALL): validate by ear in the live client (A/B `OPENFPS_STEAMAUDIO_SIM=0` vs on)
- [ ] Phase 5 (after ear-validation): retire hand-rolled `SpatialAcoustics` / `AcousticPathfinder` /
      reflection generation / Sabine reverb; migrate air-absorption + distance + region onto the sim

## Audio engine: production-readiness (planned — see overview)
- [x] Doppler for Steam Audio (2D-channel) voices (`AudioPhysics.DopplerFactor`)
- [x] Real police-siren wail asset (`PoliceSirenGenerator`, replaces the thin beep)
- [ ] Synth / sampler / granular engines: correctness pass + headless render tests (golden buffers)
- [ ] Additional phenomena: air absorption by distance/humidity on the sim path, occlusion-aware
      reverb send, near-field/proximity, wind-modulated Doppler, optional crossfaded ambiences
- [ ] Voice (Opus) path through the SA DSP pool (avoid per-packet effect churn)
- [ ] Performance: per-source reflection budget, distance/importance gating, profiling

## Engineering Audit Remediation (2026-08-28)
Full component-by-component review of the .NET rewrite at commit `4d7f70d`:
<https://claude.ai/code/artifact/2505b86c-2813-41c2-9a9d-fa9f1a22a1f9>

Sequenced so each step is verifiable before the next begins. Steps 1–4 are the gate before feature work.

- [x] **1. Make portals real.** Map-declared portal fields now create the `PortalComponent`; aperture derived
      from the collider when omitted; unlinked portals probed; auto-discovery demoted to opt-in with the
      undescribed boundaries reported; `PortalPipelineTests` covers the whole chain. *(Ear-validation of
      doorway-localized reverb still outstanding — needs a live client session.)*
- [x] **2. Unify the tick rate and de-state the predictor.** `GameServer.TickRate` deleted;
      `PhysicsConstants.TickRate` is **30** (the agreed rate) and drives the server tick, the client's
      fixed step and the interpolator alike — the client's predicted 4.5 m/s and the server's 3.0 m/s
      now agree. Rotation moved out of the replayed `Predict` into `ApplyLook` (server-identical, never
      replayed); yaw reconciled against the server transform via `MathHelper.ToYawPitch`, projected
      forward by the unacknowledged look deltas. History bounded at `MaxInputHistory`; a per-session
      `InputBudget` of simulated seconds plus a `DeltaTime` clamp, a per-tick input cap and a queue cap
      close the speed hack. Both heads now share one `PredictionReconciler`. Covered by
      `TickRateAndPredictionTests`.
- [ ] **3. Make every degradation loud.** Real hand-rolled fallback when a Steam Audio tick returns null
      (today it yields zero occlusion for every source); add phonon to the required-native check; query the
      CPU's SIMD level instead of assuming AVX2; log and speak protocol/connection failures; stop dropping
      the first play of each `NONBLOCKING` sound.
- [ ] **4. Close the server's structural holes.** `EntityRemoved` message + per-client acks via
      `KnownEntities`; accumulator clamp; `Console.CancelKeyPress` shutdown; registration reports the truth;
      rate limits on login/register; route `CommandHandler` mutations through `_commandBuffer` then unblock
      MUD commands; one `SpawnEntity` path that registers, indexes and broadcasts.
- [ ] **5. One prefab spec, validated at load.** `PrefabTemplate` becomes the single source of truth
      (`prefab-schema.json` and `GEMINI_MAP_STANDARD.md` currently describe two other formats); add collider
      shape, emitter direction, start/stop sounds, room materials; reject incoherent prefabs at load; write
      the authoring guide against the real format.
- [ ] **6. Profile, then cut the hot paths.** One `GetSnapshot()` per frame (currently 3–6); cap the audio
      update to 60 Hz; cache the server ground probe; fix the double enumeration in `GetEntitiesToTest`;
      index active sounds by entity id; measure the Steam Audio ray budget.
- [ ] **7. Finish weather, converge the heads, delete the dead code.** Scenario temperature/gustiness/air
      absorption wired end to end; complete the Phase-A interface extraction so one session class drives both
      heads; remove the reverb-slot block, dead `SoundMappingService` methods, and the spike files.

### Corrections to the lists above
- "Server: Implement Inventory/Take/Drop commands" is **not** done — `/inv`, take and drop are absent from
  `CommandHandler`, so pressing I returns "Command 'inv' not recognized".
- There is no combat system of any kind: no weapon, damage, projectile or hit-detection code exists, and
  `HealthComponent` is never modified after spawn.
