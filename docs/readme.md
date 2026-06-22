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
- **Audio Engine:** FMOD Studio Engine with Resonance Audio HRTF

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
- **Spatial Audio:** Fully migrated to FMOD + Resonance. Implemented dynamic LPF-based atmospheric absorption and sound diffraction.
- **NPC System:** Integrated a Behavior Tree system for autonomous NPC logic.
