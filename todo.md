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
- [x] **3. Make every degradation loud.** A failed Steam Audio tick no longer reports "nothing is in the way":
      each un-simulated source falls back to the hand-rolled ray-tracer (`HandRolledPath`), the listener is
      taken from any pending request so an exhausted source pool costs only that source, degradation
      transitions are logged and exposed as `IsDegraded`, and reflections are gated on the data rather than on
      `SteamAudioActive`. `Phonon.DetectSimdLevel()` queries the CPU (AVX-512 → SSE2/NEON) and
      `DefaultContextSettings()` replaces the assumed-AVX2 struct at every call site. `NativeAudioLibraries`
      is the one shared, platform-aware required-native list — phonon is now `Required`, and both heads report
      exactly which libraries are missing and what each one costs. `ClientNetworkService` raises spoken
      `OnConnectionFailed` / `OnProtocolError` for socket, disconnect, decode and send failures that were
      previously empty catch blocks. `FmodResourceManager` returns `Ready`/`Loading`/`Missing` and a deferred
      queue retries a still-decoding sound instead of dropping its first play. Covered by `DegradationTests`.
- [x] **4. Close the server's structural holes.** `EntityRemoved` is in the protocol and sent reliably on
      destroy and on area-of-interest exit; `UserSession.KnownEntities` now decides when a definition is
      sent (so remote players finally arrive as entities, not bare transforms) and moved *static* entities
      go out on the reliable channel — delivery is the acknowledgement. The loop clamps its accumulator at
      the shared `PhysicsConstants.MaxCatchUpSeconds` and says how many ticks it dropped.
      `Console.CancelKeyPress` and SIGTERM both request an ordered teardown on the loop thread (notify,
      close sockets, destroy worlds). Registration reports duplicates instead of always claiming success,
      usernames fold with `ToLowerInvariant`, and a token bucket per remote address limits login and
      register alike. `CommandHandler` answers through a reply callback and runs every command body on the
      tick thread via `_commandBuffer`, which is what let the MUD peer guard go: telnet players now log in,
      spawn, scan, move, spawn objects, and send and receive chat. `MapManager.SpawnEntity` /
      `IndexEntity` / `DestroyEntity` are the single path that registers, indexes and broadcasts — `/spawn`
      objects are now solid, audible and scannable. Covered by `ServerHolesTests`.
- [x] **5. One prefab spec, validated at load.** `PrefabTemplate` is now the format, in its own file with
      every field documented, and it gained the four things the components had and the format could not
      describe: collider `Shape`, `EmitterDirection`, `StartSoundId` / `StopSoundId`, and `RoomMaterials` by
      material NAME (Floor, Ceiling, North, South, East, West — deliberately not the `FaceMask` bit order).
      `PrefabValidator` runs on every file at load and REJECTS a prefab the engine cannot honour, naming
      each problem: an unknown field (which `System.Text.Json` would drop in silence), a duplicate id, an
      unknown material, emitter settings with `HasEmitter` false, an inside-out cone, `MinDistance >= Range`,
      a solid region, a region with no `RoomSize`, a portal that is solid or links a region to itself, synth
      parameters without `IsSynth`, out-of-range numbers. A map entity referring to a rejected prefab is told
      it was rejected, not that it does not exist. Maps get the same unknown-field report (logged, not
      rejected — dropping an entity would delete a wall) and name their room materials too.
      `prefab-schema.json` is written against the class and a test fails if they drift;
      `GEMINI_MAP_STANDARD.md` (which described a format the loader had never read) is replaced by
      `docs/AUTHORING.md`. Covered by `PrefabSpecTests`.
- [x] **6. Profile, then cut the hot paths.** `PerfProbe` is the measuring half — named timers and counters
      with a 30 s report, off unless `OPENFPS_PROFILE=1` and free when off, wired into the server tick, both
      client loops, the audio update and the Steam Audio sim. A frame now builds **one** snapshot, not three
      to six: `ClientWorldState` stamps every mutation with a version and serves the copy built for it (a
      change makes a NEW copy, never edits one already handed out), and both heads advance interpolation —
      the tick's only mutation — before the readers. Map load no longer copies the world per arriving entity
      (`EntityCount`). The audio update is capped at 60 Hz by `UpdateThrottle` inside `ClientAudioSystem`,
      so both heads obey one rule instead of running at the ~200 Hz the network poll spins at. The server's
      ground probe — once per INPUT, each run walking every cell within 50 m — is memoized per session by
      `GroundProbeMemo`, valid while the static geometry version, the position and a 150 ms age all hold
      (dynamic colliders are not in the version, so the age bound is what keeps it honest).
      `SpatialGrid.CollectInRadius` replaces the Count()-then-walk double enumeration **and** the repeats a
      multi-cell wall produced, for every caller, into caller-owned buffers. Playing voices are indexed by
      entity id, so the per-frame audio calls stop being linear scans and a frame stops being quadratic in
      the number of things making noise. `SteamAudioSimulator` times its own runs and reports the ray budget
      (rays × bounces × sources vs. measured ms), warning loudly when a run outruns the 16.6 ms audio frame.
      Also cut: quadratic interpolation with a per-entity closure, the radar's per-update array, the pathing
      SH array per source per tick — and a real bug this surfaced, `_lastSnapshot` being assigned before it
      was compared against, which had silently disabled the moving-region check. Covered by `HotPathTests`;
      the FMOD voice index and the ray-budget report are verified by inspection (no native library in CI).
- [ ] **7. Finish weather, converge the heads, delete the dead code.** Scenario temperature/gustiness/air
      absorption wired end to end; complete the Phase-A interface extraction so one session class drives both
      heads; remove the reverb-slot block, dead `SoundMappingService` methods, and the spike files.

### Corrections to the lists above
- "Server: Implement Inventory/Take/Drop commands" is **not** done — `/inv`, take and drop are absent from
  `CommandHandler`, so pressing I returns "Command 'inv' not recognized".
- There is no combat system of any kind: no weapon, damage, projectile or hit-detection code exists, and
  `HealthComponent` is never modified after spawn.
