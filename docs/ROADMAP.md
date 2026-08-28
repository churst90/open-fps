# OpenFPS — Prioritized Roadmap & Deep Analysis

_Authored 2026-06-19. Based on a full read of the .NET rewrite (Client, Common, Server)._

## 0. Context: where the project actually is

The git history is the **old Python** client/server (pygame / accessible_output2 / OpenAL). The working tree is a **complete, uncommitted rewrite into .NET 10 / C#**:

- `OpenFPS.Common` — shared ECS components, messages, physics kernel, acoustic model (`net10.0`).
- `OpenFPS.Server` — Arch ECS, LiteNetLib, MemoryPack, BepuPhysics, EF Core + SQLite (`net10.0`).
- `OpenFPS.Client` — WinForms UI, FMOD audio engine, NVDA/SAPI speech (`net10.0-windows`).

**The rewrite is not committed.** Everything under the new project folders is untracked. **First action, before any change: commit a baseline** so the work is recoverable and diffs are meaningful.

The architecture is fundamentally sound — authoritative server, a genuinely shared stateless movement kernel, client-side prediction + reconciliation, area-of-interest snapshots. Most problems are in **plumbing and edges**, not the design. Two exceptions are strategic: the spatial-audio renderer and Linux portability.

---

## Priority 0 — Baseline & hygiene (do first, ~0.5 day)

- [ ] **Commit the .NET rewrite** on a branch; the Python tree is the only thing git currently tracks.
- [ ] Add `bin/`, `obj/`, `*.db` to `.gitignore` (build artifacts and `server/game.db` are currently untracked noise).
- [ ] Decide what to do with the dead Python tree (keep on a tag/branch, then remove from `main`).
- [ ] Confirm the solution builds clean from a fresh checkout (the FMOD DLL copy is currently broken — see Audio P1).

---

## Priority 1 — Spatial audio: make it actually work (THE core product)

**This is the heart of the game and it is currently broken in a fundamental way.** The design docs claim "Google Resonance Audio HRTF," but:

### Diagnosis (confirmed by reading `FmodAudioProvider.cs`)

1. **There is no HRTF / binaural rendering at all.** No `loadPlugin` / `setPluginPath` anywhere; no Resonance/gvraudio binary shipped. `Initialize()` (`FmodAudioProvider.cs:218-249`) sets `setSoftwareFormat(44100, STEREO, 0)` and `init(512, NORMAL | VOL0_BECOMES_VIRTUAL)`. **All "3D" sound is plain FMOD amplitude panning between two stereo speakers.** For a blind-accessibility FPS this is the whole problem: there are no real front/back or elevation cues on headphones.
2. **Handedness is never reconciled.** No `_3D_RIGHTHANDED` flag; `System.Numerics` (right-handed, `+Z` forward) vectors are passed straight to FMOD (left-handed by default) — `UpdateListener` (`:986-995`), emitter positions (`:725`). Left/right or front/back can be mirrored.
3. **3D sources are forced to stereo**, which defeats FMOD positioning (only mono point sources localize): synth DSP `setChannelFormat(…STEREO)` (`:128`), granular outputs 2ch, and asset `createSound` (`:58`) doesn't force mono.
4. **Phantom Doppler**: wind velocity is folded into listener velocity (`ClientAudioSystem.cs:74`), so everything pitch-shifts even when standing still.
5. **Build silently disables audio**: csproj copies `..\libs\*.dll` but the folder is `lib` (singular) — glob matches nothing → FMOD DLLs don't land next to the exe on a clean build → `DllNotFoundException` → caught, audio off, one log line (`csproj:31`, `FmodAudioProvider.cs:248`).
6. **`materials.json` corrupts the material table**: it only carries `Absorption/Scattering/ResonanceIndex` and **overwrites** the hardcoded frequency-band properties with zeros, collapsing the occlusion EQ (`AcousticRegistry.cs:40-49`). Path is also cwd-relative and not copied to output.
7. **Resource bugs**: `Dispose()` calls `release()` then `close()` — wrong order, use-after-free (`:1172-1173`); pooled DSPs and `GCHandle`s are never freed; the entire reverb-slot system (`:844-983`) is dead code.
8. **Threading race**: the background `AsyncAcousticWorker` reads the acoustic octree while the game thread mutates/replaces its nodes — unsynchronized → intermittent crashes / dropped audio.

**Verdict:** the bespoke acoustics layer (image-source reflections, portal Dijkstra, Sabine reverb, voxel octree) is research-grade and has run far ahead of a renderer that can't even do HRTF. Fix the foundation first, then re-introduce acoustics behind a working binaural path.

### Step 1a — Diagnostic spike (chosen path: "diagnose first") — ~1-2 days
- [ ] Fix the DLL copy (`lib` vs `libs`) and `materials.json` deployment so the engine initializes on a clean build.
- [ ] Add `RESULT`-code checking + `fmod_errors` logging across init and the DSP chain (today almost every result is discarded). This alone will reveal which calls fail.
- [ ] Build a tiny standalone harness: one mono looping sound orbiting the listener, print/verify channel positions, validate left/right/front/back/up by ear with each candidate renderer below.
- [ ] Resolve handedness explicitly (`_3D_RIGHTHANDED` or Z-negation) and force mono on 3D sources.

### Step 1b — Choose the binaural renderer (decision after the spike)

| Option | Effort | Pros | Cons / Risk |
|---|---|---|---|
| **A. Google Resonance Audio plugin** | ~3-5 days | Matches the (claimed) original design; free; good HRTF; the FMOD plugin route is well-trodden. Load via `setPluginPath`/`loadPlugin`, add a Resonance **Listener** DSP on the master + a Resonance **Source** DSP per channel. | Resonance is **archived/unmaintained** by Google; need to source plugin binaries compatible with the FMOD version (2.03); may fight modern toolchains. |
| **B. Steam Audio (Valve / phonon)** | ~1-2 weeks | Actively maintained; HRTF binaural **plus** occlusion, reflections, reverb baked in — could **replace most of the custom acoustics layer**, shrinking the codebase. Best long-term fit for accessibility. | Its FMOD integration targets FMOD **Studio**; using it with FMOD **Core** means driving the phonon C API directly — more integration work. |
| **C. FMOD built-in object/spatializer HRTF** | ~2-3 days | Fewest moving parts; no third-party plugin; reliable. | Less advanced acoustics; exact Core API path (object panner vs. resonance-style built-in) needs verification in the spike. |

**Recommendation:** Use the spike to A/B the three by ear in the orbit harness. Strategic pick is **Steam Audio** (maintained, replaces bespoke code), with **Resonance** as the fast path to design parity if Steam Audio's Core integration proves heavy. Keep the `IAudioProvider` interface (`AudioEngine/Fmod/IAudioProvider.cs`) as the seam so the renderer is swappable.

### Step 1c — Re-introduce acoustics incrementally (after binaural works) — ongoing
- [ ] Serialize access to the octree/`AcousticMap` between the game thread and the acoustic worker (double-buffer or lock).
- [ ] Re-enable occlusion → map to the renderer's occlusion parameter; verify by ear before adding reflections/reverb back.
- [ ] Delete dead code (reverb slots, `_pathCache`, FMOD Studio bindings) and trim per-frame LINQ in the hot path.
- [ ] Fix `Dispose` order, free pooled DSPs/GCHandles.

---

## Priority 2 — Client ↔ server sync plumbing (~2-3 days)

The sync **model** is correct (shared `SharedMovementEngine`, seq/ack prediction + rollback, AoI snapshots, remote interpolation is actually implemented despite `todo.md` saying otherwise). The bugs are in the **constants and dt plumbing**:

- [ ] **Tick-rate mismatch (critical):** `PhysicsConstants.TickRate = 20` / `FixedDeltaTime = 0.05`, but the server runs at **30 Hz / 0.0333s** (`Program.cs:59`). The client interpolation clock multiplies server ticks by the wrong `0.05` → remote entities drift ~1.5×. Unify the server on `PhysicsConstants.TickRate`.
- [ ] **Client predicts with variable frame-dt; server steps fixed dt** → systematic misprediction every packet. Make the client predict at the fixed timestep.
- [ ] **Rotation is never reconciled** — client lerp-smooths yaw, server applies it raw, correction only syncs position/velocity. Heading desync is structural and feeds position error (movement direction derives from yaw).
- [ ] **No protocol version field / no `MapManifest.Checksum` validation client-side** — a mixed-version client/server mis-deserializes silently (`ClientNetworkService.cs:53` swallows the exception). Add a version handshake.
- [ ] **Static-but-moved entity desync**: `IsDirty` is cleared after one broadcast; an unreliable drop of that tick permanently loses the move. Track per-client acks (`UserSession.KnownEntities` exists but is unused).
- [ ] De-duplicate `PhysicsUtils.GetGroundHeight` (two copied overloads — divergence hazard).

---

## Priority 3 — Server robustness & security (~2-3 days)

- [ ] **ECS data race (critical):** the MUD gateway mutates the Arch `World` off the sim thread (`MudGateway.cs:147` → `CommandHandler` inline mutations). Arch isn't thread-safe → corruption/crash. Route **all** handler mutations through the existing `_commandBuffer`, or lock the world.
- [ ] **Speed-hack:** `MovementSystem` drains *all* queued inputs per tick with no cap (`MovementSystem.cs:44`) → input flooding = N× speed. Add a per-tick input budget.
- [ ] **Spiral-of-death:** the accumulator loop has no max-frame-time clamp (`Program.cs:165-167`).
- [ ] **`HandleLogin` rethrows** (`Program.cs:307`) — one bad login aborts the whole tick for everyone. Don't rethrow.
- [ ] **No graceful shutdown:** `_isRunning` is never set false; no `Console.CancelKeyPress`; worlds/sockets/DB torn down abruptly.
- [ ] **Unauthenticated `RegisterRequest`** writes to the DB with no rate limit (DoS/account-spam vector).
- [ ] **Delete dead `UserRepository`** (JSON impl, unused) — move the shared `UserData` DTO out first. Decide JSON-vs-SQLite once.
- [ ] **Wire up or delete `PhysicsAcousticBridgeSystem`** (dead — portal apertures never update).
- [ ] Finish persistence (player progress/position; friends/map-ownership are hardcoded TODOs).

---

## Priority 4 — Linux port (chosen target: **Avalonia**)

The client is hard Windows-locked today: `net10.0-windows` + WinForms, a Win32 `SetWindowsHookEx` global keyboard hook, and NVDA/`System.Speech`. But the **game logic is already platform-neutral**, and FMOD itself is portable (`[DllImport("fmod")]` resolves `libfmod.so` automatically — just ship the Linux `.so`).

Target: **Avalonia** — one cross-platform UI codebase with AT-SPI/Orca accessibility. Sequenced refactor:

- [x] **Extract `ISpeechOutput`** (done, audit step 7) from the (misnamed) `TolkService`, now `NvdaSpeechOutput` — it's already the single chokepoint every announcement flows through; cheapest, highest-leverage portability win. Provide `WindowsSpeechOutput` (NVDA/SAPI) and `LinuxSpeechOutput` (speech-dispatcher / libspeechd).
- [x] **Abstract the `Keys` enum** out of the input/logic layer (done, audit step 7). `GameKey` is the neutral type; `WinFormsKeyMap` and `GtkKeyMap` map at the OS boundary, and `ClientSimulationSystem` is gone entirely — both heads run `ClientGameSession` from Core.
- [ ] **Replace the global keyboard hook** with focused-window key events (Avalonia provides these). The global hook is also a *design* smell — it captures system-wide keystrokes then filters by focus in software, fights the screen reader, and is unportable (Wayland forbids it).
- [ ] **Introduce `INavigationService`** (ShowMenu / ShowLoading / EnterGame) to isolate WinForms in `ClientNavigationService`; build the Avalonia head against it.
- [ ] **Replace NAudio mic capture** (`VoiceCapture`) with a cross-platform backend (OpenAL/PortAudio, or FMOD's own recording API).
- [ ] **Ship FMOD Linux `.so`** and fix the `RequiredNativeDlls` hard-coded `.dll` filename check (`ClientRunner.cs:56`).
- [ ] Multi-target the project (neutral core lib + Windows head + Avalonia head), or move fully to Avalonia.

The duplicate `InputHandler` instances are gone with `InputHandler` itself (audit step 7): there is one session, one binding table and one input buffer. Unsynchronized reads of `LocalPlayerState` from the TTS thread remain to be checked.

---

## Suggested sequencing

1. **P0 baseline commit** (0.5d) — recoverability first.
2. **P1 audio spike** (1-2d) — fix init/DLL/handedness, then A/B the binaural options. This unblocks the core product and the renderer decision.
3. **P2 sync plumbing** (2-3d) — cheap, high-value correctness wins; pairs well with audio testing.
4. **P3 server robustness** (2-3d) — close the data race, speed-hack, shutdown.
5. **P1 acoustics re-introduction** — incremental, behind the working renderer.
6. **P4 Linux/Avalonia** — the largest effort; start with `ISpeechOutput` extraction (low-risk, useful even on Windows) and proceed once audio + sync are solid.
