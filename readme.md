# OpenFPS (C# Migration)

A highly customizable, audio-first multiplayer game framework designed for blind and visually impaired players.

## The Vision
OpenFPS focuses on a rich **binaural landscape** and **spatial awareness** rather than graphics. It is built for 100% customizability, allowing creators to build maps, items, and NPCs using a high-performance, data-oriented architecture.

## Tech Stack
- **Language:** C# (.NET 10.0)
- **Networking:** LiteNetLib 1.2.0 (Reliable UDP)
- **ECS:** Arch (High-performance Entity Component System)
- **Serialization:** MemoryPack (Zero-allocation binary)
- **Accessibility:** Tolk (Direct NVDA Bridge with SAPI fallback)
- **Audio Engine:** FMOD Core engine with Steam Audio (phonon) HRTF binaural; environmental acoustics migrating to Steam Audio's geometry-driven simulator

## Architecture (SRP Modular)
- **OpenFPS.Common**: Shared ECS components, spatial partitioning (Uniform Grid), and binary network protocols. Contains the **SharedMovementEngine**, a stateless physics solver ensuring 100% deterministic parity between client and server.
- **OpenFPS.Server**: Authoritative simulation. Features Behavior Tree AI, sub-tick precision movement, and dynamic material-based acoustics.
- **OpenFPS.Client**: Accessible interface. Features **Client-Side Prediction**, **Server Reconciliation**, and advanced spatial rendering (Atmospheric Absorption & Diffraction).

## Performance & Optimization
- **Zero-Allocation Hot Paths:** Critical simulation and networking loops use `System.Buffers.ArrayPool<T>` and `ReadOnlySpan<T>` to eliminate per-frame garbage collection.
- **Sub-Tick Input Precision:** The server processes every individual client input packet within a single tick, preventing movement "glitches" and ensuring high-fidelity control.
- **Lock-Free Audio Threading:** The client audio engine operates on a dedicated high-priority thread using `ConcurrentQueue` and atomic state snapshots to ensure glitch-free binaural rendering.

## Recent Improvements
- **Performance Overhaul:** Eliminated Gen0 GC pressure in the server's movement and broadcasting systems.
- **Deterministic Physics:** Implemented a unified `SharedMovementEngine` for sliding, step-climbing, and OBB collisions.
- **Spatial Partitioning:** Implemented a Uniform Grid for O(1) collision and acoustic scanning.
- **Networking:** Added Prediction and Reconciliation to eliminate movement jitter.
- **Spatial Audio:** FMOD Core + Steam Audio HRTF binaural, with dynamic LPF-based atmospheric absorption and diffraction.
- **Geometry-Driven Acoustics (in progress):** Migrating the hand-rolled occlusion/portal/reflection layer to Steam Audio's `iplSimulator`, so occlusion and transmission are ray-traced from real box-collider geometry on a background thread. See `docs/STEAM_AUDIO_MIGRATION.md`.
- **Loud Degradation:** Nothing in the audio stack is allowed to fail quietly. A Steam Audio tick that
  produces no result falls the affected sources back to the hand-rolled ray-tracer rather than reporting
  "nothing is in the way"; the SIMD level handed to `libphonon` is read from the running CPU rather than
  assumed; FMOD *and* Steam Audio are both required natives, and a missing one is named along with exactly
  what it costs; connection, disconnect and protocol failures are logged and **spoken**; and a sound whose
  asynchronous decode has not finished is retried instead of having its first play dropped. In a game played
  entirely by ear, a component that quietly stops working is indistinguishable from one that is working.
- **A Reliable Entity Lifecycle:** The server tells each client exactly which entities it can see and, just
  as importantly, which have gone — `EntityRemoved` on destroy and on area-of-interest exit, tracked per
  client in `KnownEntities`, so a player who disconnects does not leave a body that still blocks movement,
  still answers scans and still makes noise. A static object that moves goes out on the reliable channel,
  where delivery is the acknowledgement. Runtime spawns go through one path that registers, indexes and
  broadcasts, so an object created by `/spawn` is solid, audible and scannable the moment it exists.
- **A Server That Stops Cleanly:** Ctrl-C and SIGTERM finish the current tick and then tear down in order —
  players notified, gateway stopped, socket closed, worlds destroyed. The loop clamps how much elapsed time
  it will bank, so a long pause costs a few dropped ticks instead of a fast-forward.
- **One Text-Command Path:** Telnet (MUD) sessions are ordinary sessions. Commands answer through a reply
  callback rather than a UDP peer and every command body runs on the tick thread, which both unblocks the
  text interface — login, `ready`, `scan`, `move`, `spawn`, and chat in both directions — and removes the
  data race that the old peer-null guard was quietly standing in for.
- **Authored Portals:** A map describes its doorways explicitly — a `portal` entity carries the two region ids it joins (`-1` = outside) and the width of the opening. Portals drive portal-aware occlusion, adjacent-room reverb coupling, doorway leakage, and the HRTF localization that makes a room's reverb arrive *through* its door. Boundaries with no portal are reported at load with the exact entry the map is missing; nothing is guessed by default.
- **One Prefab Spec, Checked at Load:** `PrefabTemplate` is the prefab format — one class, documented field
  by field, with the schema generated against it and a test that fails if they drift. A prefab that
  describes something the engine cannot honour is **rejected** at load with the reason named: an unknown
  field, emitter settings on an object whose emitter is switched off, an inside-out directivity cone, a room
  volume that is also solid geometry, a doorway that blocks the doorway. Rooms name their six surface
  materials instead of numbering them, emitters can be aimed and given spin-up/spin-down sounds, and
  colliders can be shapes other than a box. Authoring guide: `docs/AUTHORING.md`.

## Current Engineering Priorities
A full component-by-component audit of the rewrite (grades, ranked defects, sequenced remediation plan) lives at
<https://claude.ai/code/artifact/2505b86c-2813-41c2-9a9d-fa9f1a22a1f9>. The active work list is tracked under
**Engineering Audit Remediation** in `todo.md`.
- **NPC System:** Integrated a Behavior Tree system for autonomous NPC logic.
