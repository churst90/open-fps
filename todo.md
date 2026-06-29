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
- [ ] Phase 4c: tune bent-path routing (probe density / `pathRange`) for fully-blocked straight lines
- [ ] Phase 4b/4c: validate by ear in the live client (A/B `OPENFPS_STEAMAUDIO_SIM=0` vs on)
- [ ] Phase 4d: reflections — `iplReflectionEffect` + mixer (replace hand-rolled reflection emitters)
- [ ] Phase 5: retire hand-rolled `SpatialAcoustics` / `AcousticPathfinder` / reflection generation
