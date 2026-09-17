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

## Audio: the load-time dropouts, diagnosed and fixed (2026-09-14, sessions 4-5)
The speedway cutting out for seconds on load was read as proof that live synthesis does not scale. It
was not. Four mechanical faults stalled the FMOD MIXER THREAD itself, and ring depth cannot help when
the thing that stopped is the CONSUMER — which is exactly why "buffer depth, thread priority and
dedicated threads all helped and none fixed it". Diagnosis and measurements: `docs/AUDIO_LOAD_DROPOUTS.md`.
- [x] `EngineRenderPool` moved steady-state synthesis off the mixer callback onto dedicated threads:
      60 voices at dsp 17%, against ~100% for 12 voices before.
- [x] Fixed on the way: a voice leak (`VoiceManager.RequestStop` silently dropped directly-played
      voices), a faded voice that could never be revived, engines restarting on every lead change,
      the Doppler staircase (60 Hz -> 250 Hz attribute updates), and phantom footsteps from position
      corrections being banked as strides.
- [x] **1. The mixer callback blocked on the producer's lock.** `Produce` held it for a whole top-up
      (warm-up plus up to 0.7 s of audio ~ 175 ms of wall clock at load-time speed); `Consume` took
      the same lock and, holding it, synthesized the shortfall ON THE MIXER THREAD. FMOD's buffer was
      93 ms. The ring is now single-producer / single-consumer and lock-free: the callback takes what
      is there, ramps out of the rest, and reports a starve. Deeper buffers can no longer make it
      worse, because nothing waits. The lead is per voice, grown only by a voice's own starvation and
      only once it has been playing for a lead. The snapshot is sorted nearest-first, so a shortfall
      lands on the car at the far end of the back straight.
- [x] **2. The JIT ran the synthesis at tier-0 for the whole load — measured 2x.** Tiering promotes
      after 30 calls plus a 100 ms quiet period that RESTARTS while other methods are being jitted, and
      a map load jits continuously for seconds. `AggressiveOptimization` on the per-sample path:
      `nascar_v8` under a pinned tier-0 went 4.0x -> ~6.9x realtime (25% -> 14.5% of a core per voice)
      with steady state unchanged at ~8x. `--engine-cost` now has a "first 500 ms" column, which is the
      condition it never measured.
- [x] **3. `ThreadPriority.Highest` is a placebo on Linux** (needs CAP_SYS_NICE). What works is
      lowering: `BackgroundPriority.RunLowered` nices the acoustic bake to +10 and takes it off the
      shared thread pool, so the mixer and the producers win contention with it.
- [x] **4. GC suspensions freeze every managed DSP** — a native mixer thread entering managed code
      during a suspension waits for the collection. `GCSettings.LatencyMode = SustainedLowLatency`.
      The Mixer load line now carries gen2 count and total pause time so this is checked, not assumed.
- [x] **5. The budget loop churned on load.** It steered on a `dsp %` that pins at 100% while the mixer
      is STALLED rather than busy, shed echoes/voices/cars, then rebuilt them a second later — each
      rebuild a new synth, a warm-up, a fill and an FMOD graph rebuild. Held for 3 s after a load and
      after a spawn, and the ceiling must now be exceeded for 0.75 s together before anything goes.
- [x] **6. Two FMOD knobs that were never set.** `setDSPBufferSize(1024, 8)` doubles the stall
      tolerance to ~185 ms. `setSoftwareChannels(128)`: the 512 at init is the VIRTUAL count, the real
      limit was 64 — and a 30-car speedway measures 59-62 real channels, so it was being reached.
- [x] **The field is back to thirty** (`FIELD_SIZE` in `tools/gen_speedway.py`). Measured on the
      speedway spike: 30 cars, 60 voices, dsp 12-16%, starves only in the first two seconds, capture
      continuous from 0.2 s with no clipping.
- [ ] Confirm in the REAL client (the spike has no map load, no bake, no GC pressure): run the
      speedway, watch the Mixer load line's starve / gen2 / pause / real-channel figures across the
      first ten seconds, and line them up against an `OPENFPS_AUDIO_CAPTURE` WAV.
- [ ] Not done, and deliberately: reuse retired `EngineVoiceState` objects for the same entity rather
      than reconstructing them. With the budget held and the existing keep-bias the churn it fixes is
      no longer measurable; do it if the client run shows otherwise.
- [ ] **Pre-rendered engine sample banks** — still the right long-term design for a field of a hundred,
      and now a SEPARATE decision rather than a forced one. `EngineSynth` offline into seamless loops
      per preset across an RPM grid, crossfaded and pitch-shifted at run time; per-car cost falls to an
      ordinary sample voice. Only steady state at 30+ cars can justify it now, and faults 4, 5 and 6
      apply to that design just as much, because they have nothing to do with how the sound is made.

## Audio: the speedway — a race you log into (2026-09-14, session 3)
`maps/speedway.json` is now the map a player lands on (`IsDefault`). A one-mile banked oval, eight
cars lapping it, a concrete wall the whole way round, a grandstand you stand on, and no ambience bed.
- [x] Two race engines: `nascar_v8` (5.9 cross-plane pushrod V8, open side exits, 9200 rpm) and
      `f1_v10` (3.0 even-firing V10, open pipes, 15,500 rpm). Vehicle profiles `StockCar` / `FormulaCar`
      geared for a one-mile oval. The stock car rumbles because its BANKS fire unevenly; the V10
      screams on order 5 because they do not. Both measured with `--engine-orders`.
- [x] **Radiation efficiency capped at ka = 1** (`OpenEnd.Radiate`). The monopole derivative rose at
      6 dB/octave without limit, so every engine that revved hard came out top-heavy — the V10's
      spectral centroid was at 8 kHz. One pole at c/(2*pi*a) is the physics and it fixes the
      long-standing "small high-revving engines are 10 dB too loud" item below. `v8_muscle` is
      unchanged to within 0.2 dB (its orders all live under a kilohertz).
- [x] Racing: `RaceLine` (OpenFPS.Common) turns a map's track waypoints into a speed profile from the
      local curvature (v = sqrt(g*9.81*R)) and a backward braking pass. `VehicleSystem` laps it.
      Cars lift for the turns and pull on the straights because of the geometry, not a script.
- [x] Engine reflections IN THE GAME (`EngineReflections`): image sources off the map's walls, each
      rendered as the engine's own ring buffer read back at the mirrored path's delay. This existed
      only in the lab before; a live engine in a map got no reflections at all.
- [x] `ImageSource.FacesOfBox` with a rotation, so a curved wall's segments are mirrors at their own
      angles; and coincident reflections are deduplicated, because overlapping wall segments were
      each answering the same bounce (two voices, 6 dB too loud).
- [x] An engine voice budget (`EngineVoiceBudget`, default 4, `OPENFPS_ENGINE_VOICES`) with
      hysteresis. A V8 costs ~12% of a core in release and ~30% in debug (`--engine-cost`), so
      `run-gtk-client.sh` now builds RELEASE by default.
- [x] `--speedway` auditions the shipped map without a server; `--speedway speedway probe` prints
      what reflects from where.
- [x] **Levels.** `EngineVoiceState` had ONE clipping reference (40 Pa / 126 dB) for every engine, so
      race presets arrived as square waves (`f1_v10` peaks 13x over it) and several road presets were
      clipping too. Now `VehicleProfile.SourceLevelDb`, measured per preset with `--engine-levels`,
      plus a shared 16 dB `PeakHeadroomDb`, guarded by a test.
- [x] **Gain staging.** The headroom is paid back once on the master limiter's makeup gain (10 dB,
      chosen against the FMOD loudness meter now on the master: -19 to -23 LUFS), not per source.
      Also fixed: the limiter was added at the DSP chain's TAIL, so it ran BEFORE the boundary
      reflections it exists to catch.
- [x] **Popping.** Three discontinuities, all in the voice lifecycle, none of them clipping: a voice
      started from rest and spun up inside 80 ms; a voice was cut mid-waveform when it lost its
      budget slot; a wall's echo was held at gain then cut. Now `PlaceAtSpeed`, a 60 ms envelope with
      `FadedOut`, and a ramped echo release.
- [ ] Only the nearest four cars sound at all. A cheap "distant engine" voice — a filtered, decimated
      render, or one shared voice for the pack — would let the whole field be heard.
- [ ] The V10 is limited to 15,500 rpm by the SAMPLE RATE, not by the engine: past about sixteen
      thousand the firing events alias and half orders climb above whole ones on an engine that
      cannot have them. Oversampling the synthesis would lift it, at four times the CPU.
- [ ] Reflections are first-order only and mono-band. Second order is written (`ImageSource`) but not
      wired in; it is what would make the turns sound enclosed.
- [ ] `ImageSource.MaxPathLength` is 400 m, which is short for a mile oval: cars on the far side get
      no reflection at all rather than a late one.

## Audio: vehicle engine synthesis — physical engine (2026-09-14, session 2)
- [x] Replaced the drawn-pulse engine with a physical one: cylinders as gas volumes, valves as flow
      boundaries into waveguides in real units (Pa, m, K), crank driven by piston pressure, lumped
      plenum with emergent manifold pressure, finite-amplitude steepening, Levine-Schwinger open ends
      with monopole radiation, expansion-chamber/absorptive/Helmholtz mufflers. `EngineSynth`,
      `ExhaustNetwork`, `IntakeNetwork`, `Waveguide`, `Driveline` under `AudioEngine/Core/Engine/`.
- [x] `EngineProfile` is general: any cylinder count, firing order, bank layout, collector grouping,
      crossover, muffler, cam, valve, induction, fuel. 14 presets, 14 vehicle presets (`VehicleProfile.Presets`).
- [x] Real-time: `EngineProcessor` FMOD DSP runs the engine live following an entity's speed
      (`VirtualDriver`). Server `VehicleSystem` drives map `Vehicles` along a road; default map has a
      big block passing the spawn at 30/60/90 km/h. Client recognises `engine:<preset>` emitters.
- [x] Instruments: `--engine-trace`, `--engine-orders`, `--engine-gallery`, `--engine-live`, `--engine-solver`.
- [x] Bench matches the literature: port pulses 0.1 bar idle -> 0.8 bar WOT, 25 dB idle-to-WOT, 92-116 dB.
- [x] Levels of small high-revving engines came out 120-127 dB at full load, ~10 dB high, because the
      radiation derivative scaled with frequency without limit. Fixed in session 3 by capping the
      radiation efficiency at ka = 1 — see the speedway section above.
- [ ] V6 and some stock presets still hunt ±150 rpm at idle; raise `IdleGovernorGain` or tune.
- [ ] Two-stroke timing is accepted by the profile but untested.
- [ ] The in-game vehicle is one voice at the tailpipe; the lab rig (exhaust/intake/tyres apart) is offline only.
- See `docs/ENGINE_SYNTHESIS.md`.

## Audio: vehicle engine synthesis, session 1 (superseded)
- [x] Rewrote the exhaust source as a gas-dynamic blowdown transient, not a harmonic stack
- [x] Hot-gas speed of sound throughout the exhaust (pipes were an octave flat at 343 m/s)
- [x] `ExhaustNetwork`: digital waveguide with real collector junctions, crossover, true duals
- [x] Measurement harness `--engine-orders` / `--engine-tones` (order spectrum, lope, chop, bands)
- [x] Lope from a shared stochastic burn-quality walk rather than a fixed pattern
- [ ] Muffler as a transfer-matrix network instead of nine biquads and a scalar — **next**
- [ ] `<80 Hz` band drops out at 1500-3000 rpm but not at 5000; find out why
- See `docs/ENGINE_SYNTHESIS.md` for findings, dead ends, and how to read the metrics.

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
- [x] **7. Finish weather, converge the heads, delete the dead code.** Every stage of the atmospheric chain
      had a silent break in it. The scenario temperature was computed into `_targetTemp`, which nothing read
      — and which compounded, so two rain fronts would have cooled the world twice and never given it back;
      it is now a per-scenario offset plus a ceiling, applied to the seasonal/daily curve. Gustiness snapped
      onto the state and nothing consumed it; it now fades, and the split is explicit — the server broadcasts
      the *sustained* wind plus one gustiness scalar, and each client synthesizes the gust locally at audio
      rate (`WindModel`: deterministic, bounded, never reverses the wind, stilled by shelter). The big one:
      `BroadcastEnvironment` never assigned `AirAbsorptionMultiplier`, so it arrived as 0 and the client's
      `Math.Max(0.1f, m)` guard multiplied the absorption distance by TEN — air absorption was off for the
      whole game. Both ends now substitute the neutral value. `AirPressure` was defaulted to `1.0` while
      every consumer reads millibars, so every map described a near-vacuum; the defaults are millibars and
      `MapRepository.NormalizeAtmosphere` names a bad one at load. The broadcast is per map
      (`GetStateForMap` overlays pressure + multiplier, reads authored temperature/humidity as biases), the
      manifest's air applies on arrival (`ApplyManifestAtmosphere`), and temperature finally reaches the mix
      as the speed of sound (`AudioPhysics.SpeedOfSoundAt` → `IAudioProvider.SetAirTemperature`).
      **The heads are converged:** `ClientGameSession` in Core is the whole of the client's game logic and
      both heads run it verbatim, behind four seams and nothing else — `ISpeechOutput`, `IClientShell`,
      `IMicrophoneCapture`, and each head's key map. `InputStateBuffer` / `InputCommandMapper` /
      `ChatManager` moved to Core on `GameKey` + `ISpeechOutput`, so there is one binding table instead of
      three processors on one head and an `if` ladder on the other. Linux gained chat scrollback, proximity
      announcements, a command console, a quit confirmation and loading progress; Windows gained the same
      table. Deleted: `ClientSimulationSystem`, `InputHandler`, the Windows input mapper/buffer/processors,
      the GTK `GameSession` + `GameInput`. `EnableWindowsTargeting` lets the Windows head be
      compile-checked from Linux. **Dead code gone:** the reverb-slot block in `FmodAudioProvider`, the
      three uncalled `SoundMappingService` play methods and its compat constructor; the ten Steam Audio
      spikes moved out of the shipped client into `OpenFPS.AudioLab/Spikes/` (the migration's open phases
      still need `--sim-*`; all ten re-run and pass from there). Covered by `WeatherAndConvergenceTests`.
      *(Ear-validation of the weather, and a live logged-in walkthrough on either head, still outstanding.)*

## Live-session findings (2026-09-13)

First logged-in walkthrough on the GTK head since the audit. Three defects, all fixed; see `changes.md`.

- [x] **The reverb was an omnidirectional wash.** A region bus is built as `send -> reverb -> fader ->
      doorway HRTF -> out`; it was built as `send -> reverb -> out` with the fader and the HRTF stage
      stranded upstream, because FMOD's DSP chain runs TAIL (input) to HEAD (output) and everything was
      attached the other way round. The per-portal gating applied to nothing and the doorway HRTF stage
      binauralized silence. Also: the bus passed SFXREVERB's dry path at 0 dB, so each send was a second,
      undirected copy of the source. Verified by `OpenFPS.AudioLab --reverb-route`, which fails on the
      old graph (both ears exactly zero) and passes on the new one.
- [x] **Crossing a portal spoke the portal prefab's authoring notes.** The proximity announcer spoke
      every named entity in range, and everything is named. `Announce` is now part of the prefab spec,
      defaulting to true only for Item / NPC / Beacon.
- [x] **A rejected login was silent, and retrying it did nothing.** Two separate faults stacked: the head
      closed its connect form on the button press, so the focus announcement interrupted the rejection;
      and `NetManager.Connect` returns the existing peer without an event when one is already connected,
      so the retry never sent a second `LoginRequest`.

## Second live session (2026-09-13)

Reverb confirmed by ear — it comes from the source now. Three more findings.

- [x] **Footsteps never stopped at a wall, and pushing into one popped and clicked.** One bug, not two:
      `SharedMovementEngine.Step` pushed the pre-move position out by a penetration measured at the
      post-move position, shoving a blocked player BACKWARDS by most of a step every tick. 0.15 m of
      phantom movement per tick — 4.5 m/s of stride going nowhere, so the footstep accumulator never
      stopped, and an acoustic region that straddled the oscillation flipped at 15 Hz. Shared by client
      and server, so both ends were doing it identically. Covered by `WallProximityAndJitterTests`.
- [x] **Crossing a threshold clicked.** The region bus's binaural stage had its bypass flipped in a
      single frame, which is a step change in the signal. It is a ramp now (Steam Audio's `spatialBlend`,
      ~0.2 s), with the apparent doorway direction smoothed alongside it. `--reverb-route` measures the
      largest single-update change across a crossing: 0.08, where a hard switch is 1.0.
- [x] **Near-field boundary effect — walls you can hear before you touch them.** There WAS one, and it
      was wrong in every particular: a fixed-ish 0.1–1.2 ms FMOD echo (the physical round trip at 1.5 m
      is 8.7 ms — seven times longer) with 45% feedback, which is a resonator that rings on one pitch
      rather than a reflection that tracks geometry, driven by a single "nearest wall" scalar so a
      ceiling and a wall behind you sounded identical. Replaced by `BoundaryModel` +
      `BoundaryProximityProcessor`: six head-relative probes, each rendered as its own delayed, damped,
      lateralized reflection at 2d/c. Covered by `BoundaryReflectionTests` (which measure the rendered
      impulse response, not the parameters) and `OpenFPS.AudioLab --boundary[-live]`.

## Ambisonic ambience beds (2026-09-13)

- [x] **Steam Audio ambisonics bindings.** `PhononAmbisonics.cs` binds the decode effect (which rotates
      the soundfield by the listener's frame and then renders it binaurally, in one call) and the encode
      effect. `AmbisonicFormat` converts AmbiX/SN3D and FuMa into the N3D the decoder actually expects —
      the conversion nothing warns you about and that makes a field sound merely *vague* when skipped.
      `AmbisonicBedDsp` is an FMOD generator DSP that owns its PCM, because FMOD would downmix a
      4-channel sound to the output speaker mode long before a DSP saw it. `FmodAudioProvider` gains
      `PlayAmbientBed` / `SetAmbientBedVolume` / `StopAmbientBed`, several beds at once so regions can
      cross-fade. Verified by `OpenFPS.AudioLab --ambisonic` and `AmbisonicFormatTests`.
- [x] **Asset ingest** (`tools/ingest_audio.py`): ambisonic beds resampled and trimmed through sox (which
      does not reorder channels), footstep takes sliced on silence into per-material/variant pools,
      unlabelled takes parked in `_unsorted` rather than guessed at, manifest written for licences.
      1,807 slices from 47 labelled takes; beds 69–485 MB -> 31–46 MB.
- [x] **`GranularBank` fixed.** It opened every sound with `MODE.OPENONLY` — "parse the header, read no
      sample data" — then called `lock`, getting a correctly-sized buffer full of nothing. Every load
      logged success and returned silence. Never caught because its only consumer, the granular engine,
      has never had a caller. Verified by `--bed`.
## Weapons (2026-09-13)

- [x] **One synthesized weapon, dry and mono.** `WeaponProfile` + `WeaponSynth` render muzzle blast,
      supersonic crack and mechanical action as separate layers with no room baked in, so the engine's
      own acoustics are the only ones heard. `Ballistics` computes the crack-to-report gap —
      `d·(1/c − 1/v)`, ~1.7 ms/m — which recovers the range exactly when read back. Covered by
      `BallisticsTests` and `--gunshot` / `--gunshot-live`.
- [x] **Recorded transients ingested.** `tools/ingest_audio.py --only weapons` now handles the drop:
      83 files. The firing takes are cut to the DIRECT SOUND ONLY — a 2 ms RMS envelope is walked
      forward from the peak and the cut goes just before the first sustained 4 dB rise, which is the
      recording's own first reflection; where nothing rises the window caps at 30 ms. Two takes cut at
      12 ms on a real arrival, four at the cap. Handling sounds (charge, magazines, selectors, bolt,
      pump, shell) are filed per weapon per action; the AKM's metal and polymer magazines are separate
      pools because pooled they would change magazine at random between reloads. Three defects the
      measurements turned up and the code now states: every take is **clipped** (0.15-2.4% of samples
      on the rail, reconstructed by parabola across runs of 2-64); every take is **limited flat at full
      scale** for 9-96 ms after the peak, so the natural blast decay is simply not in the file; and the
      handgun take spends 8 ms pinned against the NEGATIVE rail, leaving the extracted window with a
      mean of -0.40 against a peak of 0.72 until a 30 Hz high-pass takes it to 0.03.
- [x] **The weapon spec.** `WeaponDefinition` + `WeaponRegistry` (`OpenFPS.Common/Weapons.cs`): five
      weapons, real cartridges and real muzzle velocities, because the velocity is not a damage stat
      here — it decides whether the round cracks, how tight the cone is, and how big the gap is. AKM
      (7.62x39, 715 m/s, Safe/Auto/Semi, 30), AR-15 (5.56x45, 940, Safe/Semi, 30), Glock 17 (9x19, 375
      — Mach 1.09, so it barely cracks, 17, no selector at all), a hammer-fired .45 service pistol (253
      m/s, **subsonic, no crack**, 8), and a pump 12 gauge (9 pellets, 3.5° cone, tube-fed one shell at
      a time).
- [x] **The mechanism.** `WeaponMechanics` — stateless and deterministic like `SharedMovementEngine`,
      events written into a caller-owned span. Covers what a listener can actually learn: safe makes no
      sound at all (not even a click), semi needs the trigger released, a pump gun's trigger is dead
      until it is pumped and silent while it is, a magazine comes out sounding loaded or empty, the
      bolt locks back audibly on the last round, a reload without charging leaves an empty chamber, and
      a shotgun's tube is loaded one countable shell at a time. Covered by `WeaponSystemTests` (23,
      mutation-checked).
- [x] **Bullets are hitscan, and their flight is modelled in the AUDIO.** `ShotResolver` resolves the
      hit on the tick the trigger is pulled — no projectile entity to tick, network, reconcile, or add
      0.3 s of latency to hit registration — and then tells each listener when each of the three sounds
      reaches THEM: the crack from the closest-approach point beside them, the report from the muzzle,
      the impact from wherever it stopped. `SpatialEmitter.DelayMs` is FMOD's sample-accurate
      `setDelay`, which matters when the gap being conveyed is single-digit milliseconds. Projectile
      entities stay the right answer for grenades, where the flight IS the event.
- [x] **Outdoors can reverberate now.** The outdoor bus was built muted and `ApplySimulatedReverb`
      returned early on the global region, so a concrete street canyon produced no reflections at all.
      The mute is right for the *Sabine* estimate it was written against — that takes the whole map as
      one room and washes the world — but Steam Audio's ray-traced RT60 is computed from the geometry
      actually standing around the listener, and the early return was suppressing the one model that
      gets it right. Outdoors now takes its decay AND its wet level from the simulation, ramped rather
      than stepped. Measured: 2.1 s in the canyon, 0.10 s on open ground, so open ground stays exactly
      as dry as it was. `AcousticConstants.OutdoorDryDecayMs` / `OutdoorFullWetDecayMs` /
      `OutdoorMaxWetDb` are the knobs.
- [x] **`--battle` / `--battle-live`.** Concrete Row: a 24 m street, 26 m concrete blocks down both
      sides, two cross streets, six shooters. Headless it checks that the crack leads the report, that
      the gap recovers the range *given the miss distance*, that the canyon reverberates and open
      ground does not, and that nobody is standing inside a building (two of the six were).

## Listening session (2026-09-14) — "thin", "a tick", "thumping on a bathtub", "one big room"

Four complaints, four separate root causes, all real. Three fixed, one only half fixed.

- [x] **"Sounds like a tick."** The report was the recorded transient played alone — twelve to
      thirty-three milliseconds of limited, clipped audio. `WeaponSynth.CompositeBlast` puts the
      synthesized body and decay back underneath it.
- [x] **"Thumping on a bathtub."** The synthesis deserved that. `MuzzleBlast` was a resonant band-pass
      ringing at 280-470 Hz plus a decaying SINE for the thump — a tuned resonator and a tone, which
      between them are the definition of a boxy thud, and it ran for 860 ms when a DRY blast is under
      a tenth of a second. Rebuilt on the physics: the low end is now a **Friedlander wave**,
      p(t) = P(1 − t/T)·e^(−t/T), which is one pressure excursion rather than a cycle, so it has weight
      without pitch; over it sits broadband noise through a low-pass whose cutoff FALLS as it decays,
      which is what makes a blast open bright and close dark. Blasts are 160-340 ms, not 860.
- [x] **"The guns don't seem that loud — have we introduced the concept of dB?"** We had not, and the
      arithmetic was stark: every asset ships normalised to the same peak and the only per-sound
      control was a 0-1 multiplier, so a gunshot ten metres away rendered **fourteen decibels quieter
      than a footstep at one metre**. In air those differ by eighty-five in the other direction.
      `Loudness` gives every sound a source level in dB SPL (rifle 165, handling 78, footstep 55) and
      converts it, with the range compression written down as the deliberate lie it is.
- [x] **"Shouldn't distant ones be muffled?"** They were not, at all. `AsyncAcousticWorker` set
      `AirAbsorption = 0f` with a comment calling it "a later phenomena pass" — so switching the Steam
      Audio simulator ON switched air absorption OFF for every source in the game. There is now one
      law in `AudioPhysics`, used by both paths, plus `AtmosphericBands` with ISO 9613 coefficients and
      an urban excess term for the scattering a city adds. A shot at 178 m now arrives 19 dB down at
      4 kHz and untouched at the bottom; at 10 m it is 0.4 dB. `UrbanExcessHighDbPer100M` is the knob.
- [x] **Five weapons were two sounds.** The blast came from a table of three profiles keyed by name,
      so the Glock and the .45 rendered BYTE-IDENTICAL and the AKM and AR-15 differed only by which
      take got picked at random. Blast character now lives on `WeaponDefinition` — one place per
      weapon — which also retires the name lookup that had already caused the Glock's crack to be
      scheduled and then rendered as silence.
- [x] **The street's reverb could vanish mid-firefight.** The ray-traced RT60 is stochastic — the same
      canyon measured 1729, 2131 and 2800 ms on consecutive runs — and occasionally a run finds
      nothing and reports zero, which read as "open field" and muted the outdoor bus for a frame.
      `SetSimulatedReverbDecay` now median-filters the last five readings: a dropout cannot move the
      median, but walking out of the canyon still does.

- [x] **"The demo sounded dry" — it was, completely, and for a second reason.** With the bus finally
      metered directly (`TryMeterReverbBus`, because `TryGetReverbDiagnostics` reads a stage that is
      BYPASSED whenever the listener is inside the region and so reports zero for the outdoors no
      matter what) the send level into the outdoor reverb was 0.021 and is now 0.486. Two causes: the
      play-time send had `if (sourceRegionId != -1 ...)`, which excluded every sound in the open world
      from every reverb bus; and the whole chain was quiet because of the dB bug below.
- [x] **Guns are very loud, and the model now says so.** `Loudness` was wrong in a way that looked
      right: it put the CEILING at the loudest source level (165 dB, a rifle at one metre), so a
      gunshot only reached full scale if you were standing at the muzzle and the inverse-square law
      had taken 30 dB off it before the mix saw it. The ceiling is now the SPL at the LISTENER that
      uses all the headroom (130 dB, about where loud becomes pain), and the reference distance is
      DERIVED from the source level — a 159 dB rifle holds full scale out to 28 m and only then falls.
      An AKM at 48 m went from -31 dBFS to -5.
- [x] **"Thumping on a bathtub", then "still too thin".** Both were the synthesis. The first was a
      resonant band-pass plus a sine — a tuned resonator and a tone. The second was my own arithmetic:
      a Friedlander wave of time constant T puts its energy near 1/(2*pi*T), not 1/T, so the sub-bass
      was being generated at THIRTEEN HERTZ and then removed by the DC-blocking filter. The weight was
      rendered and thrown away. Corrected, plus a second slower blast wave and a hard soft-clip drive
      (saturation is what lets the low end be loud without simply owning the peak).
- [x] **"A gun that sounds like a bug zapper firing four times".** The AR-15, and two causes. It was
      firing SEMI-automatic at its CYCLIC rate — 800 rpm, four rounds inside 225 ms, which nobody's
      finger can do and which does not read as four shots. `SemiAutoRoundsPerMinute` (280) now limits
      the trigger and `IntervalFor(mode)` picks the right one. And every round of a burst played the
      same cached crack render; four variants now.
- [x] **A dryness regression I caused and the tests caught.** I changed the blast buffer from six time
      constants to four, over a comment in the file explaining why four is not enough — at four the
      layer is still at 1.8% when the buffer ends, which is a step at the end of every shot. Restored,
      with shorter decays so the blast stays under 230 ms.

## Real gunshot recordings (2026-09-14)

- [x] **The Cadre Forensics dataset replaces four of the five firing sounds.** NIJ grant
      2016-DN-BX-0183, free with registration, and it measures as well as it reads: the Zoom H4N
      recordings are **96 kHz, 0.00% clipped and 0 ms limited at every position tested**. That is the
      defect nothing downstream recovers, and every take in the original drop had it (0.15-2.4%
      clipped, 9-96 ms limited). Mapped M16 -> AR-15 (a real 5.56 at last), WASR -> AKM (7.62x39 AK
      pattern), Glock9 -> Glock, Colt1911 -> the hammer-fired .45. The dataset has no shotgun, so the
      pump gun is now the ONLY weapon still firing a borrowed take, and it is the weakest by ear.
- [x] **`--measure` for vetting a library before committing to it.** Reports the three defects that
      decide usability: clipping, how long the peak envelope stays pinned at full scale, and energy
      above 2 kHz. The original drop scores `good 0, usable 1, poor 5`; the Cadre takes score
      `good 8, usable 0, poor 0`; the handling sounds we already had score `good 9, usable 2, poor 0`,
      which is why they are kept.
- [x] **A measurement bug in that tool, found by using it.** It took a fixed SAMPLE count and
      decimated by 4 without filtering, so everything above sr/8 aliased into the band being measured —
      at 48 kHz that inflated the high band with fold-down from above 6 kHz and at 96 kHz it did not,
      making the two datasets incomparable. Comparing them is the entire purpose of the tool. Now a
      fixed TIME window with no decimation.
- [x] **Angle held constant across the distance series.** Bucketing by distance alone mixed 90-degree
      and 180-degree takes, and a muzzle blast is strongly directional — the AKM came out BRIGHTER at
      40 m than at 3 m, which is not what air does to sound, it is what standing beside the muzzle
      rather than behind it does. Everything is now 180 degrees (the shooter's own perspective, and
      the one angle the dataset has at 3/10/20/40 m), leaving directivity to the engine's cone.
- [x] **Five weapons are now five sounds.** AR-15 brightest and shortest (+0.7 dB high/low, 126 ms),
      .45 darkest (-3.7 dB), shotgun longest (240 ms) — the ordering their calibres imply.
- [x] **The synthesizer was drowning the recordings.** Synth at 1.0, recording at 0.45 — so replacing
      four of the five firing sounds with 96 kHz unclipped takes changed almost nothing, because what
      was audible was still mostly synthetic. That balance was set when every recording was clipped,
      limited and dark; it survived the reason for it. The recording now leads at full level.
- [x] **And most of the synthesis was inventing a tail.** A 7.62 blast three metres from the muzzle
      decays TWENTY-SEVEN DECIBELS IN THIRTY MILLISECONDS — it is genuinely over by then, and what
      follows in the file is the desert. The synthesized layer ran 120-240 ms, so it was not
      reinforcing the blast, it was appending a room to it — inside the sample, where the engine's own
      reflections and reverb cannot get at it. That is a large part of why everything sounded like one
      echoey space. What is left for synthesis is one narrow job: the bottom octave a handheld
      recorder's capsules roll off below 80 Hz (`SubReinforcementLevel`, 0.25).
- [x] **The AR-15 sounded like a tick because I picked the wrong microphone position.** 180 degrees is
      BEHIND the muzzle, which for a rifle is the extreme of the directivity pattern, not a sample of
      it. Measured on the M16 at 3 m: side-on, the low band sits +17.3 dB relative to the mids; from
      behind, MINUS 2.8. Twenty decibels of body that was never in the file, and no amount of
      downstream work puts it back. The AKM survived it because a 7.62 has low end to spare; the 5.56
      did not. The whole series is now 90 degrees, which the dataset also has at 3/10/20/40 m, and the
      AR-15's body went from -2.8 dB to +8.2. It is also the more honest default: most shots a player
      hears are someone else's, from the side.
- [x] **`--blast-compare` / `--blast-compare-live`.** Plays each weapon three ways — recording alone,
      recording plus sub, pure synthesis — at the same level through the same path, so "do we still
      need the synthesizer" is answerable by ear rather than by argument.
- [x] **The shotgun is now deliberately fully synthesized.** Its only recording is twelve milliseconds
      of a single-shot RIFLE take, and twelve milliseconds of the wrong weapon is worse than a
      synthesized one of the right weapon. `RecordedBlend = 0` says so explicitly.

- [ ] **Use the distance series at runtime.** 3/10/20/40 m of every weapon are ingested and only the
      3 m set is wired up. A blast that genuinely travelled forty metres beats one we filtered.
      Measured honestly, though, the effect is smaller than expected at these ranges: across 3->40 m
      the 2-8 kHz band falls only 0.8 dB faster than 150-1500 Hz. Air absorption does not really bite
      below 8 kHz until hundreds of metres, which means `AirDbPerMetreHigh` is right and
      `UrbanExcessHighDbPer100M` — scattering and obstruction, a design choice rather than a measured
      one — is doing most of the "distant is dull" work in the engine. Worth saying out loud.
- [ ] A shotgun take. Nothing free measured so far has one that passes.

## Vehicles (2026-09-14) — `--vehicle` / `--vehicle-live`

A V8 sports car with Flowmaster 40s, synthesized from its mechanism rather than sampled. Three
emitters, because a car is three sources in three places: exhaust at the back, intake at the front
3.4 m ahead of it, tyres at the axles between. On a pass the front reaches you before the back and each
Dopplers on its own schedule — none of which has to be authored, it falls out of putting the sources
where they are.

- [x] **The engine is its firing pattern.** A cross-plane V8's banks fire at 90/180/180/270 degree
      intervals — UNEVENLY — and that irregularity is the lope. (A flat-plane fires every 180 exactly,
      which is why a Ferrari screams instead.) Impulse train -> header quarter-wave -> H-pipe coupling
      -> system resonance -> muffler -> tailpipe radiation.
- [x] **The Flowmaster is its chambers.** A 40-series is CHAMBERED, so it cancels rather than absorbs —
      each chamber notches c/4L and its odd multiples (591, 746, 953 Hz here), and because nothing is
      absorbed the upper harmonics survive, which is the rasp. The model even reproduces the famous
      highway drone for free: the system's 5th mode meets the firing rate at **2042 rpm**, which is
      exactly where Flowmasters are notorious for droning.
- [x] **Manual gearbox, simulated not scripted.** Six speeds, 260 ms shifts. The RPM drop across a
      change is the actual ratio step (6150 -> 4270 on the 2-3 change, which is 2.97/2.07), and a shift
      at part throttle differs from one at full because the torque curve is being asked for different
      things. Overrun pops on a closed throttle — on a loud chambered system that IS the character.
- [x] **Tyres are two mechanisms, not one.** Broadband roar rising ~30log10(v), plus the TREAD BLOCKS
      striking the road at speed/spacing — several hundred hertz at 25 m/s. That second component is
      why tyre noise rises in PITCH with speed, and it is what a plain noise generator misses.
- [x] **The start is a trajectory, not a sample.** Solenoid, starter churn at 260 rpm with compression
      pulses but NO combustion (so the pipe never rings — that is why cranking sounds hollow), catch,
      flare to ~1500, settle to idle. A failed start would fall out of the same model for free.

Three bugs found by measuring rather than listening, each of which would have been mistaken for a
tuning problem:
- The torque curve was a narrow lognormal giving the engine **5.6 Nm at idle**, so the car could not
  pull away at all and never reached a shift point — the entire gearbox was unreachable behind one
  wrong curve shape. A parabola about the peak is both more realistic and much harder to get wrong.
- There was no CLUTCH, so at a standstill the gearing said idle and nothing could launch. Slipping it
  at 2300 rpm is also what a launch sounds like: revs held flat while the road catches up.
- The muffler chambers were feedforward combs, which have a **zero at DC** — three in series took the
  firing fundamental down 33 dB, and an idle measured 45 dB below its own firing rate and came out as a
  buzz near 300 Hz. A quarter-wave side branch removes a band AROUND its resonance and passes the rest;
  it is a notch, not a high-pass.

Measured afterwards, the band balance moves the way it should: idle peaks at 60-150 Hz, under load at
150-400 with the rasp arriving above 1.2 kHz, and on the overrun at 400-1200 — the crackle.

- [x] **"Electronicy, buzzy like a sawtooth" — four causes, all structural.** A sawtooth is perfectly
      periodic harmonics with no noise between them, and the model was literally that.
      * The pipes were LOSSLESS delay lines, so they reflected 5 kHz as readily as 100 Hz and rang like
        metal. A real pipe's loss rises with frequency — wall drag and end radiation both take the top
        first — so there is now one pole in each feedback path. This is the single biggest change: the
        harmonics above the 5th now roll off 30-40 dB, and the buzz went with them.
      * There was NO turbulent noise anywhere. A real exhaust is moving a lot of hot gas fast and that
        flow is noisy BETWEEN the firing harmonics; without it there is nothing but harmonics, which is
        the definition of the problem. Added both continuous flow noise (scales with gas velocity) and
        turbulence carried by each blowdown (present at any engine speed).
      * Every firing event was identical. Cylinders are not: different runners, different breathing,
        different wear. The trims are small, a few per cent, and FIXED per cylinder — consistency is
        what makes it read as a machine with eight of them rather than as noise.
      * Combustion pressure now varies cycle to cycle. (Valve TIMING deliberately does not — that is
        mechanically fixed by the camshaft, so jittering it would have been wrong.)
- [x] **The engine blipped to 1035 rpm every time it stopped.** The start-up flare was applied to every
      Idling order rather than to the catch that earns it, so pulling up sounded like a driver blipping
      the throttle. Gated to the catch; idle now settles at 772.

- [x] **`--engine-match FILE`** measures a real exhaust recording and prints the synthesis constants it
      implies: firing rate (so, RPM), harmonic rolloff (-> pipe damping), harmonic-to-noise (-> the two
      noise levels) and band balance (-> the gas hump). Any format ffmpeg reads; a phone clip is plenty,
      because every measurement is a RATIO within the clip and so is insensitive to microphone, level
      and most of the room. Nothing from the recording enters the game — the output is numbers.
      Self-testing it against our own render caught two bugs in it: it picked SILENCE as the "steadiest"
      passage (perfectly steady, says nothing) and reported the engine's character from a moment after
      it had been switched off; and the noise recommendation had its SIGN BACKWARDS, so a clip measuring
      as a sawtooth would have been told to get cleaner still.
- [x] Pointed at our own V8 it measured **+34 dB harmonic-to-noise at a steady 2302 rpm** — arithmetic
      confirmation of "sounds electronic". Applying its correction overshot to +4.9; interpolating
      between the two lands at FlowNoiseLevel 2.10 / PulseNoiseLevel 1.32, now +23.

- [ ] **The first reference file could not be used, and the measurement says why.** A 71 kbps MP3 of a
      muffler comparison. Even in 120 ms windows — short enough that a moving fundamental cannot smear —
      its harmonics sit 20-36 dB under the fundamental and the 1-6 kHz band is 46-63 dB down. A real V8
      exhaust keeps its harmonics within roughly 6-20 dB and has real energy at 1-6 kHz, which IS the
      rasp. At that bitrate a bass-dominant signal gets its whole bit budget spent on the fundamental
      and the rest is discarded; what looks like content at 8-20 kHz is encoder noise, flat at -40 dB.
      Recoverable from it: the firing rate, so the RPM (1920-2160 across the passages). Nothing else.
      A phone recording at 2-5 m would be far better than a low-bitrate copy of a good one.
- [ ] **Scale the pipe delays by exhaust gas temperature.** Raised by the impulse-response question and
      worth doing on its own: gas leaves the head at 700-900 C, and the speed of sound goes as the
      square root of absolute temperature, so every resonance in the system sits about 75% higher hot
      than cold. It also moves with load, which is part of why an engine's timbre changes under power
      rather than only its pitch. The model has the pipe lengths as delays already, so this is one
      multiplier — and it is the thing a static impulse response fundamentally cannot represent.

- [x] **Fitted to a real reference at last.** The second file — a Mustang 5.0 with Original 40s, idle,
      rev and take-off, 125 kbps — has its harmonics intact (x2 at -7 to -14 dB, against -20 to -36 in
      the unusable one), so it could be measured.
      **The headline number: real Flowmaster 40s measure +6.5 dB harmonic-to-noise.** The 8-18 dB
      target the fitting had been aiming at was my own estimate and it was wrong — a real exhaust is
      NOISIER than that. Ours measured +27.6, so it was 21.1 dB too clean, which is the arithmetic
      behind "sounds electronic". Two iterations closed it to within 2.0 dB:
      FlowNoiseLevel 4.95, PulseNoiseLevel 3.13, HeaderDamping 0.11, SystemDamping 0.07, HumpLevel 1.85.
- [x] **`--engine-match REF --compare OURS`.** The tool conflated two different jobs — measuring a
      reference and correcting our engine — so handed a reference it said "this is noisier than target,
      reduce the noise", which is backwards. Measurement and recommendation are now separate: it
      measures both files and derives the correction from the DIFFERENCE.

- [ ] **The rolloff cannot be fitted independently by this method, and that is a property of the
      measurement rather than a bug.** Damping and noise interact through it: heavy damping lowers the
      harmonics, which lowers measured harmonic-to-noise, and a high noise floor makes the upper
      harmonics measure flat because they are sitting ON it. A third iteration chasing the rolloff went
      backwards on two of the three metrics and was reverted. Fitting it properly needs the harmonic
      envelope measured ABOVE the noise floor — the "sample the transfer function at the harmonics"
      idea from the impulse-response discussion — rather than a single slope number.

 Everything above is physics, but the LEVELS are
      still guesses — including the harmonic-to-noise target, which I picked rather than measured. A
      YouTube link cannot be analysed; an audio FILE in the inbox can, and all the measuring tooling
      already exists. Given one, the pipe damping, flow noise and pulse noise can be fitted to the real
      thing instead of estimated.
- [ ] Real-time DSP. The drive is rendered offline and then moved through space, which is right for a
      demo and wrong for a game: RPM has to follow a player's right foot. The synthesis is sample-by-
      sample already, so it wants wrapping as an FMOD DSP like `SynthProcessor`.
- [ ] No engine braking, no rev-matched downshifts, no differential or transmission whine.
- [ ] Tyres do not know what they are rolling on — `AcousticRegistry` knows 36 surfaces and gravel,
      wet asphalt and concrete all sound different. This is the granular engine's other obvious job.

## Why it sounded sterile (2026-09-14)

- [x] **There was no ambience at all.** The demo played gunshots into perfect silence, which is not a
      place, it is a test signal. An ambisonic bed now plays — first order, so it stays PUT in the world
      while the listener turns, rather than being glued to the head the way a stereo bed is. It does
      two jobs: gives the ear a continuous reference to localize the shots against, and fills the gaps
      so the silence between them stops sounding like the engine has stopped.
- [x] **The near-field boundary system was never fed.** `BoundaryModel` has been in the engine since
      the near-field work and `--battle` never called `UpdateBoundaries`, so every surface in Concrete
      Row was silent until it was far enough away to echo. Six head-relative probes are now cast
      against the street geometry each frame.
- [x] **`DemonstrateProximity`** — the same shot from mid-road and from two metres in front of a block,
      back to back, with the probe distances printed. Mid-road finds only the ground below (1.7 m);
      beside the building it finds the wall on the LEFT at 2.0 m, returning 12 ms after the direct
      sound. That is the "would I not sense the skyscraper" question, answerable by ear.

Worth being precise about the two regimes, because they are different phenomena and only one of them
is "proximity":

  * Closer than about five metres, a surface is NOT heard as an event. Its return arrives inside the
    window where the ear fuses it with the direct sound, and what changes is the timbre plus an
    unmistakable sense of something solid being there. `BoundaryModel`, capped at 3 m.
  * Beyond that it is an arrival: 2d/c is 70 ms at twelve metres, late enough to be its own event.
    `ImageSource`.

- [x] **Beds replaced by world-placed emitters.** The diagnosis was "the environment is not part of
      the game environment", and that is exactly right: a recorded bed does not reflect off OUR
      buildings, is not occluded when you step behind one, comes from nowhere, and does not change as
      you move. The gunshots are being filtered, delayed and reflected by the geometry and the bed is
      not, so the two do not share a world and the bed reads as a soundtrack. The street's noise is now
      made of EMITTERS going through the identical pipeline — a main road north-east at 283 m, traffic
      south at 165 m, a rooftop plant on the near block at 54 m. Step behind a building and they
      occlude. They also give the player something to orient BY, which a head-locked bed can never do.
- [x] **A synthesized rumble floor** for everything too far to be any one thing. Steeply low-passed —
      "low level white noise" is the right instinct about LEVEL and the wrong one about SPECTRUM: a
      distant city is a rumble, not a hiss, because kilometres of air have taken everything above a few
      hundred hertz. Synthesized so it never loops, and so it can be made to respond to going indoors,
      which a recording cannot.
- [x] **Reflector SIZE now decides how much comes back.** The model treated a garden fence and a
      fifty-storey tower as equally reflective at the same distance. It now weighs the surface against
      the first FRESNEL ZONE at the bounce point, radius sqrt(lambda*d1*d2/(d1+d2)): a reflector much
      larger than that zone behaves as an infinite plane, a smaller one intercepts only part of the
      contributing area and the rest diffracts past. Frequency dependent through lambda, which is why a
      fence mirrors a whistle and is transparent to a lorry, and it grows with distance — so at 150 m
      only genuinely large things still answer. A 2 m fence returns 71% at 5 m and 25% at 40 m; a
      tower returns 100% at any range.

- [x] **Ambience sources now get the same reflections gunshots do, which is what stops them sounding
      dry.** `Arrivals()` was only ever called from the gunshot path, so every placed emitter was a
      bare point source in an empty field. The important part is that NO special case was needed: a
      delayed copy of an impulse is heard as a separate arrival (an echo), and a delayed copy of a
      SUSTAINED sound is not heard as a second event at all — it sums with the original into a comb
      filter, notches every 1/d Hz, which the ear reads as colour and as the size of the place. A dog
      barking from a sixth-floor window does not echo off the block opposite; it takes on that block's
      colouration. Same geometry, same code; the difference is entirely whether the source is a bang
      or a drone. Capped at 3 facades per source, because these run forever and each holds a voice.

- [ ] **Get SINGLE-SOURCE recordings, not fields.** The three emitters above are stereo FIELD
      recordings being used as point sources, which is a stopgap — each contains several things at
      once, so it localizes to one place while sounding like many. What the architecture wants is one
      recording per object: an air-conditioning unit, a generator, one road, one dog, one aircraft
      pass. Those are far easier to find than a convincing field, and they are what makes the world
      composable.
- [ ] **A synthesis plan for the things that are hard to record** (the answer to "how do I get
      vehicles"). The taxonomy matters more than any one asset, because it says which tool fits what:
      * PERIODIC IMPULSE TRAIN for anything with a firing or blade rate — engines, fans, compressors,
        generators, rotors. An engine is impulses at RPM/60 x cylinders/2 through exhaust and body
        resonances, with a little cycle-to-cycle jitter so it does not read as a synth. This is the
        right answer for VEHICLES specifically: it gives continuous RPM, load and Doppler for free,
        where a sample set needs a dozen loops and still cannot cover arbitrary revs.
      * GRANULAR for stochastic textures made of many small irregular events — tyres on gravel, rain,
        crowds, fire, debris, water. The control that matters is DENSITY, and it has to track a
        physical quantity: grains per second proportional to speed is what makes acceleration read as
        acceleration rather than as a volume knob.
      * SUBTRACTIVE (shaped noise) for broadband continuous sources — wind, jets, airflow, the distant
        traffic rumble. A distant jet is very nearly pure filtered noise with a slow spectral sweep.
      * MODAL for struck solids — casings, glass, pipes, railings. A handful of decaying sinusoids,
        endlessly variable, never repeats. This also replaces the six casing recordings, which will be
        heard repeating.
      An AC unit is the instructive case because it is two of these at once: a blade-pass tone with
      harmonics (periodic) plus airflow hiss (subtractive), and no part of it wants to be granular.
- [ ] The granular engine exists and still has no caller. Tyres on a loose surface and the glass
      fragment shower are its two obvious first jobs.
- [ ] Nothing moves yet. A car passing, an aircraft overhead — the engine already has Doppler, and a
      moving source is the single most alive-sounding thing a street can have.
- [ ] Nothing is occasional. Everything placed loops forever; real environments have events. A dog at
      a random interval, a door, a distant siren.
- [ ] The rumble floor does not yet respond to going indoors, which is the main reason for
      synthesizing it rather than recording it.
- [ ] **The 4-channel beds are still right for the places they were recorded.** Woods and farm are
      genuinely diffuse and have no localizable sources, so a soundfield is the correct tool there.
      The mistake was using one in a city, not having them.- [ ] `BoundaryModel.MaxDistance` is 3 m, so a building twelve metres away contributes nothing to the
      sense of enclosure — only its discrete reflection. A large surface reflects the whole ambient
      FIELD back at you, which is why standing near one feels different even in silence, and nothing
      models that yet.

## Outdoor reflections (2026-09-14) — "it sounds like one big echoy room"

- [x] **Discrete, directional building reflections.** `ImageSource` (Common) is the missing piece: a
      first- and second-order image-source solver. Mirror the source through a facade's plane and you
      have, exactly, where the reflected wavefront appears to come from — not an approximation, just
      what the geometry does. The engine's existing reflection pass could never do this: it scans for
      surfaces within 40 m of the LISTENER, so it finds the wall you are standing next to and never
      the one the sound came off in the distance. The AR-15 at 154 m now gets answers at 15, 184 and
      380 ms; something in the fight answers 692 ms late, off 237 m of extra path.
- [x] **Second order, because a canyon needs it.** Two flat parallel walls offer exactly ONE
      first-order bounce each, so a shot down the middle of a road produced two reflections and then
      nothing — and with nothing else arriving, the diffuse tail was all that was left, which is
      precisely "one big echoey room". Wall-to-wall paths are where a street's character lives.
      Every shot in the fight now gets at least one discrete arrival; before, three of six got none.
- [x] **Coincident reflections no longer get a voice.** `MinDelaySeconds` (12 ms). A shooter and a
      listener both 1.5 m up and 178 m apart have a GROUND-bounce path three centimetres longer than
      the direct one — a tenth of a millisecond. Rendered separately at 98% gain that is not a
      reflection off anything, it is the direct sound played twice, 6 dB louder, comb-filtered. It was
      also crowding the genuinely distant bounces out of the voice budget.
- [x] **The diffuse wash turned down and capped.** `OutdoorMaxWetDb` -6 -> -16 and a new
      `OutdoorMaxDecayMs` of 1100. The ray tracer measures 1.7-2.8 s for a concrete canyon and taken
      literally that is not wrong — but a two-second decay is a cathedral, and the sky is an infinite
      absorber that a tracer working from box colliders cannot see. Now the facades answer
      individually and this is only the tail behind them.
- [x] **Realistic rather than exaggerated.** Facade absorption 0.02 -> 0.25 and `ReflectionLevel` 0.5.
      A building front is not a polished slab: it is windows, reveals, sills and signage, and all of
      that scatters. At 0.02 every facade answered at 98% and the street came back like a hall of
      mirrors. A reflection you notice AS a reflection is already too loud.
- [x] **Cross streets are one-sided.** Cutting both rows put the gap exactly where a mid-street shot
      would have bounced, and three shooters got nothing from anything. Real blocks are not
      symmetrical either.
- [x] Fold `ImageSource` into `ClientAudioSystem` / `SpatialAcoustics` so the GAME gets this and not
      just `--battle`. `EngineReflections` holds the surface cache (keyed by PLANE, so a wall built
      from twelve blocks is one surface and one voice), the budget is an audibility floor rather than
      a count, and as of session 7 the occlusion callback IS wired: both legs of every mirrored path
      are obstruction-tested, and each echo carries the direct path's occlusion and EQ.

## The cars that vanish, and the cars that stop (2026-09-14, sessions 6-7)

Two reports from the speedway. Diagnosed with no code changed in session 6, fixed in session 7. The
full record — what each fault was, what was done, and what was deliberately left — is in
`docs/AUDIO_GHOSTS_AND_STUTTERS.md`.

- [x] **A position is stamped by when it was SAMPLED, not submitted.** `SpatialEmitter.PositionSampledAt`
      off `WorldSnapshot.PositionsSampledAt`, so the voice manager's 250 Hz refresh re-applies the
      same age instead of resetting it. Dead reckoning works for the first time.
- [x] **One monotonic clock for every timestamp** (`OpenFPS.Common.AudioClock`). The provider's stamps
      and its dead reckoning were both measured on a stopwatch the mixer-load report RESTARTS every
      250 ms, so the age went negative for most of every quarter second and the reckoning did nothing.
- [x] **The pathing bake is off the critical path.** It used to run inside `SetScene` and take 100 s of
      one core before ANY source got an occlusion value. Now a `Lowest`-priority background thread,
      with the probe count budgeted rather than fixed at 2 m spacing.
- [x] **The occlusion probe is at the emission point** (`AudioEmission`, `SoundEmitterComponent.Offset`,
      `VehicleProfile.ExhaustOffset`), with a volumetric radius that fits above what the emitter rests
      on. It was at the entity origin with a fixed 0.5 m sphere — half underground for anything that
      drives, on every level stretch of every track.
- [x] **A barrier attenuates by its geometry** (`OpenFPS.Common/Diffraction.cs`): Maekawa insertion
      loss from the path difference round the obstacle, per band, as a floor under the simulator's
      transmission. A 0.9 m pit wall cost 26 dB and three octaves; it now costs single digits.
- [x] **Reflections obey the same physics as the direct path.** Obstruction-tested, occluded with
      their source, and no longer given the listener's Doppler twice.
- [x] **The map is validated against what drives on it** (`TrackClearance`, run at map load and
      asserted by `EveryShippedTrackIsDriveable`). The speedway's front straight ran through the
      grandstand deck for 138 sampled points of the lap; `gen_speedway.py` now takes the setback from
      the wall's furthest point rather than its nearest.
- [ ] Vehicles as prefabs with a LIST of emitter slots (exhaust, intake, tyres, horn), not one offset
      and a map-level `VehicleData`. The acoustic half is done; the authoring half is a feature.
- [ ] The two field-wide starve bursts (~150 ms of silence, twice in thirteen minutes). Still
      unexplained; needs the producer-side scheduling gauge — time between two consecutive `Produce`
      calls on the same thread — on the census line.
- [ ] A borrowed distant engine voice reads another car's ring, so it inherits THAT car's Doppler and
      then applies its own. Needs the borrowed voice to advance its own read cursor rather than follow
      the source's play position.

## Composites: a house, a car and a map are the same idea (2026-09-15)

The spine under "how do I build a house, customise it, classify it as an object later, and get inside
it like a car" — which turned out to be one question asked four ways. A composite is a set of entities
with a local origin that can be saved, placed again, owned and entered. Members wear a
`ParentComponent` pointing at the root, and `ParentSystem` — which has run every tick since long
before this — already carries them with it, so a house that never moves and a vehicle body you can
drive away are the same structure. Only whether anything moves the root differs.

- [x] `CompositeComponent`, `CompositeTemplate`/`CompositePart`, `CompositeRepository` (JSON on disk,
      mirroring `PrefabRepository` — a composite IS a prefab, just one made of more than one entity).
- [x] `CompositeService`: **group** what is standing there, **ungroup** it leaving the parts exactly
      where they were, **save** it as a template anyone can place, **place** an instance.
- [x] The origin is where the thing meets the ground, not its middle — so a house placed at your feet
      has its floor at your feet rather than being buried or floating.
- [x] Grouping is by RADIUS, because a selection needs pointing at things and pointing is the one
      thing a player here cannot do. "Everything within twelve metres of me" is a selection anybody
      can make, and can widen until it is the right one.
- [x] Players, vehicles and existing composites are never swallowed by a sweep.
- [x] `IdentityComponent.PrefabId` — nothing recorded what an entity was an instance OF, and a wall
      that cannot say it is a `concrete_wall` cannot be written back out to a template.
- [x] `MapData.Composites` + `MapManager.SaveMap` — a placement is recorded on the map the moment it
      is made, and `/savemap` commits it. Without that a house lasts until the next restart, which is
      not a house, it is a rehearsal. Covered end to end by `APlacedBuildingSurvivesARestart`.
- [x] Commands: `/group name [radius] [free]`, `/ungroup`, `/saveas id`, `/place id [yaw]`,
      `/composites`, `/savemap`.
- [x] **Occupancy and driving** — see the section below.
- [x] Ownership — `CompositeComponent.Owner`, enforced.
- [x] A sweep no longer swallows anything WIDER THAN ITSELF. `/group` on a map with a floor took the
      floor, because the floor is within twelve metres of you; it is within twelve metres of
      everybody. Geometric, not a list of things called floors: you cannot be selecting something
      whose far side is nowhere near you, and widening the radius correctly brings bigger things into
      scope.
- [ ] Parts that were not spawned from a prefab cannot be saved. Right answer for now (it fails
      loudly rather than dropping a wall), but hand-built geometry needs a home eventually.
- [x] **A composite's own acoustics** — see "Inside is a fact about the geometry" below.

## Inside is a fact about the geometry (2026-09-15)

Nobody should have to author the inside of a building they just built. Put four walls, a floor and a
roof around yourself and you are indoors; that is true of a shed, a cathedral and the cab of a lorry
by the same rule, and if it is not derived then every building a player makes is silent inside until
a developer visits it.

- [x] `CompositeAcoustics.Derive` asks three questions of the PARTS, and each rules out a thing that
      is not a room. **Big enough to be inside** — a fence is a wall with more wall next to it;
      whatever its footprint, one of its dimensions is a wall's thickness. **Mostly empty** — a stack
      of crates the size of a garage is not a garage. **Mostly covered** — four faces of six, which
      is a roofless courtyard, and that is generous on purpose because a walled yard genuinely does
      sound closer to a room than to a field.
- [x] What the room is MADE of comes from the parts too: each of the six faces takes the material of
      whichever part covers most of it. A glass-sided office is bright and a carpeted one is dead
      because of what somebody built them out of, not because anyone ticked a box.
- [x] The room is a PART — an ordinary entity wearing `ParentComponent` and `DerivedRoomComponent` —
      not a component on the root. So ParentSystem carries it for free and a caravan takes its
      acoustics with it. It is also exactly what the `building_box` prefab already tells a person
      authoring by hand to do: "place an acoustic_region inside it if the interior is enterable".
- [x] It sits at the middle of the SPACE, not at the composite's origin. The origin is where the
      thing meets the ground — that is what makes a house placed at your feet have its floor at your
      feet — so a volume centred there is half underground with its ceiling at your knees, and
      standing up inside your own building would put you outdoors.
- [x] Rotated parts are measured by the box that CONTAINS them. A wall turned ninety degrees is two
      metres of wall across, not half a metre, and measuring it the naive way makes every building
      that has corners come out the wrong shape.
- [x] `ClientWorldState` registers regions that arrive AFTER the acoustic bake. The client bakes once
      from the static geometry streamed at map load; a building somebody puts down while you are
      standing there is not in that, and the inside of a car never can be, because it moves. Build-
      then-swap rather than mutating in place: those tables are read without a lock from the audio
      worker and the FMOD thread.
- [x] Deliberately NOT voxelized. The voxel grid is the fallback for entities the client cannot see
      in its snapshot; a composite's room is one it can always see, so the exact point-in-box test
      against the live transform is both cheaper and the only one that can be right for a room that
      moves.
- [x] `CompositeRoomTests`: 12 tests, and a **sabotage pass** (`tools/sabotage-rooms.py`) that breaks
      each rule in turn and checks the matching test actually goes red. It caught a real hole on the
      first run: the fence fixture was being rejected by the HOLLOWNESS rule, so the
      minimum-dimension rule was doing no work in any test and would have passed for free however
      badly it was written. The fence now has gaps in it, which isolates the rule it is there to test.

## Vehicle sound: what is left, in the order it should go (2026-09-15, planned)

Everything here was asked for in the walkthrough after occupancy landed. Ordered by what unblocks
what, not by how interesting it is.

### 1. DONE — Doors (2026-09-15)

A door is two things at once and only one of them is obvious.

- [x] **The leaf swings aside.** It is solid the whole time; what opening changes is where it is. A
      door that went insubstantial instead would be one you could walk through while it was shut and
      standing in front of you, and one whose open leaf was in the way of nothing. So there is no
      collider mutation anywhere in this — just a leaf that moves.
- [x] **The opening appears**, and that is the half a listener cares about. A `PortalComponent` on
      the same part has its aperture driven by how far the leaf has swung, so the room beyond opens
      up gradually as it moves. No new acoustics were written: the portal machinery already knew how
      to do this, and all `DoorSystem` does is move the number.
- [x] The hinge is an EDGE. Swinging about the centre would sweep the leaf through the doorway in
      both directions and leave half of it in the way at ninety degrees. Which edge is authored, and
      it is not cosmetic — an open door heard on your left is a different fact from one on your right.
- [x] `DoorSystem` runs BEFORE `ParentSystem` and writes the swing into the door's
      `ParentComponent`, because a door in a building is one of its parts and ParentSystem rewrites
      every part's world pose from its local one every tick. A door standing on its own has no parent
      and is moved directly.
- [x] The client is told as the leaf moves, over the path that already re-sends an entity whose audio
      changed — but only when the aperture has moved enough to matter. Every tick would be thirty
      reliable messages for a thing that takes a second.
- [x] `ClientWorldState` tracks runtime portals the way it already tracks runtime regions, and a shut
      door comes straight back OFF the map: an aperture of zero is not an opening, which is the same
      thing the bake already does with one.
- [x] Which room a doorway joins is a property of WHERE IT IS, so the link is made when the room is
      derived and remade whenever the shape changes. A door in a building leads out of it, with
      nobody authoring the pair.
- [x] **Which door serves which seat is proximity, not a table.** `/open` from a seat reaches from
      the seat, so on a bus you open the door beside you rather than the one nearest the middle.
- [x] `door` and `steel_door` prefabs, `IsDoor` / `SwingSeconds` / `SwingDegrees` / `HingeSide` in
      the prefab format. A door is the one thing legitimately a portal AND solid, so the validator
      learned that exception — and only that one; everything else that is both is still the mistake
      it always was.
- [x] Commands `/open [name]`, `/close`, `/doors`. Not elevated: building a door needs a role, going
      through one does not. `/doors` says the STATE as well as the name, because an open door and a
      shut one in the same place are different facts and the only other way to find out which you
      have is to walk into it.
- [x] Reach is `PhysicsConstants.InteractionRange`, the same as everything else you reach for, and a
      refusal names the distance — found live, where `/doors` reported a door at 4.7 m while `/open`
      said there was none within four, which is the tool contradicting itself.
- [x] **Found live: grouping a building changes the frame its doors live in.** A door records where
      "shut" is in whatever frame it lives in — world when loose, parent-local when part of a
      building — so one that kept a world pose and was then asked to swing as a part computed its
      local pose from a world one and flung the leaf out of the world. A shed's door opened
      perfectly until the shed was grouped, and then vanished. Doors now shut and forget their
      reference pose whenever they are grouped or ungrouped, which is both easy to say and what
      anybody would expect. The tests had missed it because the fixture grouped in the same instant
      it built, so no door ever recorded a loose pose to go stale; the fixture now lets the building
      stand for a moment first, the way a real one does.
- [x] `DoorTests`: 14 tests, and the sabotage suite is up to 31 rows, all caught.

#### The sound of one (2026-09-15)

- [x] **Materials gained mechanical properties.** Everything the registry held described a material
      as a SURFACE — what it absorbs, what it lets through — which is enough for a wall between you
      and a noise and nothing like enough for a wall that IS the noise. `DensityKgM3`,
      `YoungsModulusGPa` and `LossFactor` now sit beside them, because a door panel, a windscreen in
      hail and two cars meeting are the same calculation asked three times.
- [x] `DoorAcoustics` — the model, and there is not a per-door setting in it. A door is a FAMILY of
      sounds, not a sound:
      * **The latch** first, before the leaf has met anything — the bolt rides the strike plate and
        drops. Nearly independent of the door, because the mechanism is steel whatever the leaf is.
      * **The seal**, where there is one: air driven out of a closing gap, low and soft. It absorbs
        more than half the impact energy, so a sealed door is QUIETER than the same leaf bare and it
        robs the panel of the ring. That is a car door against a garden gate, and it is most of why
        an expensive car sounds expensive.
      * **The impact**: half m v squared arriving, so twice the closing speed is six decibels. That
        ratio is what makes a slam recognisable AS a slam rather than as a louder close.
      * **The panel** ringing on afterwards at its plate-bending fundamental — 0.4755 t sqrt(E/rho)
        (1/a² + 1/b²), which is the plate constant and not a tuning knob.
      * **The hinges**, while it is moving and only if they are dry — a stick-slip relaxation
        oscillation, the same process as tyre squeal.
- [x] Three things the tests found, all of them the model being wrong rather than the test:
      * A carpet was being given a note, because a frequency below the bottom of pitch was being
        CLAMPED into existence rather than treated as the thud it is.
      * A steel door rang for ninety-eight seconds. A hung panel loses energy through its edges far
        faster than steel loses it internally — published total loss factors for panels in situ run
        one to five per cent and are dominated by exactly that. `HungPanelLoss` is that, and its
        absence was being hidden by a clamp, so the test now asserts the physics decides the figure
        rather than the ceiling.
      * A wooden door rang for a second and a half, because the loss factor was the one for a solid
        clear billet rather than for the plywood or hollow core a door is actually made of.
- [x] And one comfortable belief the model refused to support: **a steel door and a wooden one of the
      same thickness ring at almost the same pitch**, because steel's twenty-fold stiffness is paid
      for in twelve-fold weight. What tells them apart is how LONG, the seal, and the level — so the
      test asserts that, rather than asserting folklore.
- [x] `DoorSoundTests`: 13 tests on relationships rather than absolutes — it does not matter whether
      a door rings at 480 Hz or 520, it matters that the ratios are right. Sabotage suite up to 43.

## The world audio channel (2026-09-15) — the thing everything else was waiting for

Until this existed the server's entire vocabulary for sound was "this entity carries a looping
emitter". There was no way to say **that just happened** — only *that is always happening* — which is
the single reason `GlassBreak`, `WeaponSynth` and every collision in the tree are written, tested and
completely silent.

- [x] `WorldAudioEvent`: a place, a label, a seed, and a list of `TransientSound`. Sent reliably,
      because a transient is a one-off that nothing will ever resend — a dropped state packet costs
      nothing and a dropped door is a door that opened in silence.
- [x] **It carries no sound id, and that is the whole point.** A file name means every new thing in
      the world needs a recording of itself, made in advance, at one size and one material and one
      force. Parameters mean a door somebody builds out of a material somebody else invented is
      audible the first time it shuts.
- [x] `SoundCharacter` — **Knock, Ring, Hiss, Scrape**. Named for the PHYSICS, not the source. A door
      latch and a bullet striking concrete are both knocks; a struck panel and a bell are both rings;
      a door seal and a tyre letting go are a hiss and a scrape. A synthesiser that knows about
      "doors" needs a new case for every new thing in the world; one that knows about knocks and
      rings already handles the ball nobody has invented yet.
- [x] `TransientSynth` — four short DSPs on one resonator, and not a single case for doors or glass
      or gunfire anywhere in the file. Rings are three slightly INHARMONIC partials whose upper ones
      die first, because a plate is not a string and that inharmonicity is most of what separates a
      struck panel from a synthesiser pretending to be one.
- [x] **Render once, play by name.** A sound's parameters ARE its identity, so the id is a hash of
      them: two doors of the same material and size shutting at the same speed are the same buffer,
      and a corridor of identical doors costs one render.
- [x] `RegisterSynthesisedSound` — the bridge, and deliberately one method. Everything downstream
      works from a sound id, so a rendered latch is placed, attenuated, occluded through walls,
      reverberated, routed through the region and voice-budgeted by exactly the paths that handle a
      recording, none of which needed changing and none of which knows nobody recorded it. Bridging
      at the sound-id layer rather than playing buffers directly is what buys all of that.
- [x] The seed travels, so two players standing together hear the SAME variation of the same event —
      and two events from one door are not bit-identical, because twenty rounds that are the same
      twenty samples read as a recording, which is the one thing this engine exists not to be.
- [x] Sounds are queued to their own moments rather than fired together: a latch precedes its own
      impact, and a pane's glass lands a second and a half after it broke.
- [x] Doors are the first user. Everything the model needs is already on the leaf — material, size,
      thickness, and the edge speed implied by its own swing — so `DoorSystem` says what it sounded
      like and nothing about doors appears anywhere in the audio engine.
- [x] `WorldAudioEventTests`, sabotage suite up to 49.

### The three that were within easy reach, done (2026-09-15)

- [x] **`PanelAcoustics`** — the plate law lifted out of `DoorAcoustics`, where it had no business
      being. A door leaf, a pane of glass, a car's wing and the side of a container are all a flat
      piece of stuff that rings when struck, and they ring by the same law. One place, so they cannot
      slowly stop agreeing.
- [x] It returns the lowest AUDIBLE mode, not always the fundamental. A window pane's fundamental is
      down around fifteen hertz and it plainly does not thud at fifteen hertz when struck: what you
      hear is the lowest of its higher modes your ears can reach, and a plate's modes go as
      (m²/a² + n²/b²), so climbing that series is the honest way up rather than clamping.
- [x] **Having a note is not the same as ringing.** The modal series hands back an audible frequency
      for almost anything — a carpet has modes too — so `RingsAudibly` asks the question that
      actually separates a bell from a bag of sand: does the note outlast the blow that caused it.
      Found by a test noticing a carpeted wall had started to ring.
- [x] **`ImpactAcoustics.Between`** — two materials, two masses, a size, a closing speed. A car
      meeting a wall, a ball meeting a floor and a crate off a lorry are one function called three
      times; there is no case for cars anywhere in it. Energy uses the REDUCED mass, because a lorry
      hitting a drink can and a drink can hitting a lorry are the same collision and the lighter one
      governs. The blow takes the character of the SOFTER of the pair; the ring belongs to whichever
      of them rings, which is why a hammer on a bell is a bell.
- [x] Driven composites now make that noise when they hit something. The impulse and the damage are
      still to come; the sound is not a placeholder for them, it is the same calculation everything
      else will use.
- [x] **Glass speaks.** `GlassBreak` had been written, tested and silent for want of anything to
      speak through. The mapping is a mapping and not a second model — the physics was decided long
      ago. A shatter is a HISS rather than a knock, which is the interesting part: a pane letting go
      is thousands of tiny impacts inside a tenth of a second, and a crowd that dense stops being
      heard as impacts at all, whereas one shard landing IS one impact. That is why a window breaking
      sounds nothing like the pieces of it arriving — and the gap between them is which floor it was.
- [x] **Gunfire**, via a narrow escape hatch: `TransientSound.SynthKey`. Four characters describe
      nearly everything and a few things they cannot, and a gunshot — blast wave, body resonance,
      brightness sweep, action working — is one of them, with a model that already exists and is
      better. So a sound may name one, exactly as an engine emitter names "engine:v8_sports".
- [x] `/fire [weapon]`, honestly labelled a DEV TRIGGER. `WeaponSynth`, `ShotResolver`,
      `WeaponMechanics` and `GlassBreak` have all been tested for a while and had never made a sound
      because nothing connects a gun to a player. Held items will supply the real trigger; having any
      at all is the difference between four tested models and four tested models nobody has heard.
- [x] `ImpactAndGlassSoundTests`; sabotage suite up to 57.
- [x] **`--street` / `--street-live`** — a minute on a street with all of it in one place, because the
      only way to judge "nothing here was recorded" is to stand in the street and listen. A wooden
      door and a steel one through the same doorway; a window on the second floor shot out from
      thirty metres, and the gap before the glass arrives reading back as thirteen metres against an
      actual twelve; a truck coasting past and standing on it hard enough to put the tyres a third
      over their grip; and the same truck finding a wall at eleven metres a second, 830 kJ arriving.
      Headless it checks the arithmetic and writes every layer out to audition.

#### Calibration against recordings, and what only an ear could find (2026-09-15)

Five reference recordings in `inbox/`, measured rather than played. Every one corrected something,
and two corrected things the tests were confidently asserting.

- [x] **A door latch is mostly BODY** — 42 per cent of the recording is below 200 Hz, against two per
      cent in the model. A latch is not a little click, it is a mechanism bolted through a leaf, and
      what you mostly hear is the leaf answering the bolt. Rendered as pure click it had nothing
      underneath it, and a bright transient with nothing underneath it is a puff of air — which is
      what it was called. Three parts now: ramp tick, click into the keeper, and body.
- [x] **Glass shards were an octave low and three times too long**, which is a bottle cap. The
      recording of shards on cement is 38 per cent above 4 kHz with two per cent below 1.5 kHz and
      66 ms a piece; the model now measures within a few points in every band.
- [x] **A piece of glass landing RINGS**, it does not knock. It is the same piece of glass on the
      ground that it was in the air, and the shards — which ring — were judged right while the
      landings, which knocked, were judged to sound like plastic.
- [x] **The car screech recording matched the sports car band for band** (21/44/28 against 21/40/27),
      so the model was right and the TRUCK was the dull one: it squeals at 430 Hz against a car's 950.
- [x] **AN IMPACT IS NOT ONE RESONANCE.** A single pole struck by an impulse is a cork coming out of
      a bottle, and that is what listeners called it — twice, about a door shutting and a car hitting
      a wall. Both were one pole, so both were a cork. Five inharmonic modes now, the high ones dying
      first, over a broadband contact burst whose brightness is tied to the sound's own pitch.
- [x] **A latch bolt and a fifteen-hundred-kilo car had the same frequency law.** Contact time grows
      with mass, and a longer contact is a lower sound; a car meeting a wall was coming out at 1.4 kHz
      for 28 ms, which is a tap. With mass in the formula it is 185 Hz for 115 ms.
- [x] **Sound pressure is not loudness**, and this one cost three rounds. Rendering the whole engine
      voice with and without slip showed a full squeal moving a sports car by SIX TENTHS OF A
      DECIBEL. A squeal sits where hearing is most sensitive and an engine's energy sits an octave or
      two below it, so a 92 dB squeal against a 116 dB engine is not 24 dB down to a listener.
      `SquealProminence` is that correction and was measured, not chosen.
- [x] **The render ceiling was anchored 18 dB too high.** At 130 dB = full scale the only thing that
      ever reached the top of the mix was gunfire, and everything everyday — 60 to 95 dB, which is
      what the game is mostly made of — lived in the bottom third. 112 now.
- [x] Hinges silent by default. A creak is a FAULT, not a feature; every door in the game was
      creaking, which a listener heard as an unexplained hiss either side of every thud.

Still not right, and the honest state of it: **the door is "better but not perfect"**. The thud and
the panel ring are the weakest parts. What would settle it is a close-mic'd recording of a door
closing — the reference list is in the session notes, and a car crash or heavy metal impact is the
biggest gap of all, since there is currently no measurement behind collisions at all.

#### What listening to it found, earlier the same day

Every one of these passed every test in the suite. None of the tests could hear.

- [x] **The muzzle blast was a burst of white noise**, and measuring agreed: barely a FIFTH of its
      energy sat below 500 Hz, where nearly all of a real gunshot's does. The noise layer — the gas
      jet — carried more than both blast waves together and decayed at the same rate as the pressure
      wave, leaving two hundred milliseconds of hiss under every shot. The jet is loud and BRIEF;
      what crosses a street is the wave behind it. Rebalanced to 68 per cent below 500 Hz, and
      `AShotIsMostlyLowEnergyAndNotABurstOfNoise` now holds it there — the gap being that the four
      existing tests checked it was dry, centred, deterministic and different per weapon, and not one
      of them could tell a gunshot from a hiss.
- [x] **No glass at all**, because it was authored at 60 dB — the level of a conversation — and heard
      from thirty metres. A pane letting go is ninety-odd at a metre; it is one of the loudest things
      a building does.
- [x] **Glass shards were a wash of white noise.** A fragment in the air is a TINKLE: a small stiff
      plate that rings, short and bright and pitched. Rendered as a long broadband hiss, a dozen of
      them became one hiss.
- [x] **Doors were nearly silent**, because `MinDistance` was hardcoded to a metre. Gain and reference
      distance are decided TOGETHER — that is the whole of what `Loudness.Place` is for — and taking
      the gain while hardcoding the reference threw half of it away.
- [x] **The truck never squealed.** The number handed to the tyre model IS the demand, where 0.78 is
      where a tyre starts to sing; the scene subtracted 0.55 from it first and put it below the onset.
      A truck accelerating in perfect silence.

### 1b. The original plan, kept for the reasoning

Three pieces already exist and compose; the thing to resist is inventing a fourth.

- A door is a **part** like a wall. What it is called is `IdentityComponent.Name` — "front left
  door" — which is already how a player refers to anything.
- A door **is a portal**. `PortalComponent` already carries `ApertureSize`; open and closed are that
  going to the door's area or to zero. Opening a door then changes what you hear through it with NO
  new acoustic code, because the portal transmission already exists.
- Which door serves which seat is **proximity, not a table**: the door for a seat is the nearest door
  to it. That is automatically right for a two-door, a four-door, a bus with a middle door, and
  whatever somebody invents on a Tuesday.

So: `DoorComponent { Closed, HingeAxis, Swing, OpenSeconds }` on a part that also carries a
`PortalComponent`, collider going non-solid while it swings. `/open` with no argument takes the
nearest; from a seat it takes YOUR door.

The SOUND of it is three things, and they are worth separating because each one tells you something
different. The **latch** is a small sharp metallic transient. The **seal** is a short pressure
whoomp as it compresses — which is most of why an expensive car sounds expensive, and it is absent
entirely on a van's sliding door. The **panel** rings at its own modes afterwards, so a steel door,
a glass one and a canvas flap are three different events. All three fall out of material and area;
none of them wants a sample, because a sample cannot tell you which vehicle you just heard.

Hinges creaking are a stick-slip relaxation oscillation — the SAME process as tyre squeal, which
`TyreFriction` already models. Reuse it.

### 2. Weather on the vehicle — rain on a roof, and why it must not be a sample

Asked directly: do we need samples of rain on a windscreen? **No, and a sample would be worse.**

Rain on a panel is a stochastic impact process: drops arrive at a rate of intensity x area / drop
volume, and each one briefly excites the panel at its own modes. That is the same machinery as the
tyre-tread impacts and `GlassBreak` already in the tree. What makes it worth synthesizing is that
every variable in it is information:

- **The panel decides the sound.** Steel car roof: rings a few hundred Hz, moderately damped. Glass
  screen: stiffer, brighter, faster decay. Canvas: a dull thud with no ring at all. A truck's
  aluminium box booms. That is `MaterialComponent` plus area — the same inputs as everything else.
- **Drop size is the difference between sounds people recognise.** Drizzle is a dense hiss of tiny
  impacts; heavy rain is fewer, bigger, harder strikes you can count. A loop cannot move between
  them, and moving between them is exactly what tells you the weather is turning.
- **Sleet and hail are a different impact regime**, not louder rain. A hailstone does not splash, it
  BOUNCES — a much shorter, harder contact, higher up the spectrum, with individual strikes audible
  even at high rates. Getting that from a rain loop is impossible.
- **Snow is nearly silent on impact** and that is the point: what you hear is everything else going
  quiet. The hush after snowfall is an absorption change, not a new sound, and the acoustic map
  already has the machinery to express it.
- **The rate scales with speed.** Driving sweeps out more volume per second, so rain on a moving
  screen gets louder and moves forward off the roof and onto the glass. A free speed cue.

Needs: a precipitation TYPE on the wire (`WeatherType` exists server-side as Clear/Rain/Snow/Storm
but only the intensity is broadcast), plus sleet and hail, plus a drop-size figure. Then a panel
impact synth keyed by intensity, type, material, area and relative speed.

And the payoff from the room work: `ShelterFactor` already kills direct rain under a roof. Getting
into a car should therefore stop the rain ON you and start the rain ABOVE you — which is as strong
an "I am now inside" cue as exists, and it costs nothing extra once a car is a region.

### 3. Interior audio — the cab, the road, the wind

Right now sitting in a car puts the listener a metre from an engine rendered as though you were
standing next to it in the open. The room work makes the CAB real; these make the rest of it.

- **The engine from inside** is the same source through the bulkhead: a steep low-pass plus a
  structure-borne path carrying the low orders more than the high. This falls out for free once the
  car's own body occludes, which needs dynamic geometry in the acoustic path — the one genuinely
  hard piece on this list. An "inside" flag applying a bulkhead filter would be a special case and
  is the wrong answer.
- **Road noise is the one that matters most.** Above about fifty km/h it is the dominant interior
  sound and it is what actually tells you your speed. `VehicleSynth.Tyre` already synthesizes it
  from `TreadBlocks` and `SurfaceRoughness`; the interior version is the same source arriving
  structure-borne, so it is LESS filtered than the engine and comes from below and all round rather
  than from a point. `EngineProcessor` already has `TyreMix` and `FrontMix` as constants — inside is
  tyres up, intake down.
- **It should change with the surface.** `GetGroundHeight` already returns the floor material, so
  asphalt to gravel to a bridge deck would be instantly audible. For a blind driver that is
  navigation, not decoration, and it is nearly free.
- **Wind noise** is the best high-speed cue there is: broadband, from the A-pillar and mirrors,
  rising far more steeply with speed than engine or tyres. It should come out of
  `VehicleProfile.DragArea`, so a brick of a van roars and a slippery coupe hisses. `WindModel`
  already exists for weather; the interior version is the same synthesis on relative airspeed.
  Cheapest item here and the highest navigational payoff.
- A truck versus a car then needs nothing bespoke: a bigger cab gives lower room modes, the engine
  sits under the seat rather than beyond a bulkhead, and the diesel clatter is already in the
  profiles.

### 4. Gears and pedals — a wire change, and it makes the model simpler

Today it is an automatic, and the client picks the gear, not the server: `DrivingSystem.SelectGear`
picks one only to compute tractive force, while `EngineProcessor`'s `VirtualDriver` independently
picks its own from the speed it is sent. They agree because they share a rule, not because anything
is transmitted.

It SOUNDS right — `VirtualDriver` works a real clutch (`_shiftTimer` holds it down for
`Gearbox.ShiftSeconds` with the throttle shut, which is the gap in the middle of a shift) and
`Driveline` fires `TyreFriction.ShiftChirp` on a big ratio step. But a player cannot be given manual
gears without sending them, because the client INFERS the gear from speed: hold second at a hundred
and the client still plays top. There is a subtler version of the same problem already — the virtual
driver is a PID chasing a target SPEED, so it is not reproducing your pedal, it is inferring a pedal
that would produce your speed. Lift off at the crest of a hill and it may stay on the throttle.

The fix removes machinery rather than adding it: for a player-driven vehicle, stop inferring the
driver and SEND them. `VirtualDriver` exists to guess throttle, clutch and gear from speed alone;
for a car with a real driver the server already knows all three. A "real driver" path in
`EngineProcessor` that takes them off the wire and skips `VirtualDriver.Apply` leaves `EngineSynth`,
`Driveline` and `ExhaustNetwork` untouched, and makes manual gears, holding a gear downhill, and
engine braking you chose all audible — because the synthesis was always physical.

About four bytes per driven vehicle per tick, and there are only ever a handful. Controls: keep
automatic as the default, `[` and `]` to shift, gear announced, auto-clutch. A clutch key is one key
too many.

### 4a. DONE — building where you cannot point (2026-09-15)

`/spawn` could only drop a box three metres ahead at your own feet height, so a player could make a
heap and could not make a shed. Replaced with a cursor.

- [x] `BuildSession` on the player: an origin they set at their own feet, a cursor in origin-relative
      metres, and what they have placed. The axes are the BUILDER'S and they are fixed at the moment
      the origin is set — fixed rather than live, because a coordinate system that turns when you turn
      is one where the wall you placed a moment ago has moved.
- [x] It reads out as "two right, three forward, one up", never as a world coordinate. Small numbers
      relative to somewhere you chose are somewhere you can hold in your head and walk back to.
- [x] `/at` says what is ALREADY at the cursor. Moving somewhere and being told nothing is the same
      as not moving; being told "Concrete Wall" is how you find the wall you placed a minute ago and
      build the next one against it.
- [x] `/put prefab [turn d] [run n [direction]]`. The run is the important half: a wall is a LINE of
      parts, and a line placed by hand is only as straight as the arithmetic somebody did in their
      head. A run steps by the part's own footprint, so they touch, and leaves the cursor at the end.
- [x] It refuses to build inside something that is already there, which is otherwise undetectable.
- [x] `/undo`, which is not a convenience: a part in the wrong place is invisible to somebody who
      cannot see it, so the mistake is not merely unfixed, it is undetectable until they walk into it,
      and by then they have built three more things around it.
- [x] **`/room` — the replacement for standing back and looking.** A dry run of the rule `/group`
      will apply, asked BEFORE committing, and it names what is missing rather than saying no:
      "only 3 of its six faces are walled — 4 are needed. Open: floor, ceiling, north wall." The
      three rules are always all measured rather than stopping at the first failure, because a
      builder told only the first thing wrong fixes it and is told the next thing, and building a
      shed becomes twenty round trips.
- [x] `/prefabs`, so there is a way to find out what can be put down and how big each one is.
- [x] `BuildCursorTests`, driven through the real command path rather than the services — the
      arithmetic being right is worth nothing if the words a player types do not reach it. Sabotage
      pass extended to nineteen rows, all caught.

Proved live over telnet: a four-walled shed with a roof, built from nothing, with `/room` correctly
reporting a mis-aimed first wall as a missing north wall before it was fixed.

### 4b. `/spawn` cannot actually build a shell — FIXED by 4a above, kept for the record

Walking the room work through a real server turned up the practical blocker. `/spawn` always drops
its box THREE METRES AHEAD OF YOU AT YOUR OWN FEET HEIGHT, and there is no way to turn it or raise
it. So with the commands as they stand you can make a heap, and you cannot make a shed: no roof, and
no wall that runs the other way. (The heap is correctly not a room — the hollowness rule threw it
out, which is the right answer arrived at honestly.)

Everything the region work does is reachable from `/place` and from prefab-built composites, which is
what the tests exercise, and none of it is reachable from the building commands a player actually has.
That makes the authoring commands the next thing standing between all of this and anyone using it —
they were deliberately left until after composites so they would be a thin shell over the right
model, and the model is now here.

Minimum to unblock: an offset and a rotation on `/spawn` (`/spawn Box Concrete 6 3 0.4 at 0 3 0`
relative to where you stand and which way you face), which is the same "stand where you mean and say
here" idea that `/group` and `/addseat` already use.

### 5. Lending a vehicle

`Owner` is one name and there is no grant, so an owner cannot let a friend drive. `/lend <player>`
is a two-line stopgap. The real answer is **keys as an item**: a key is an entity, it lives in
`InventoryComponent`, it is handed over, dropped, lost and stolen, and `MayModify` becomes "you own
it, or you are holding its key". Ownership stops being a name check and becomes a physical fact you
can hear change hands — the same move as "a house, a car and a map are the same idea", applied to
permission. It waits for the inventory work.

### Synthesize or sample?

The rule that falls out of all of the above: **synthesize what must vary, sample what is a fixed
signature.** Rain, impacts, latches, hinges and panels all carry information in how they change, and
a sample freezes exactly the dimension that mattered. An indicator relay, a seatbelt, a handbrake
ratchet and a warning chime are the same every time and derive from nothing — those are samples, and
the existing `SoundEmitterComponent` path already plays them with no new machinery.

## Occupancy: get in, and drive it away (2026-09-15)

The last of the four things a composite is for — saved, placed again, owned, ENTERED — and the one
that makes driving stop being a feature of its own. A car somebody built out of walls and a car the
map spawned are now the same kind of object, so everything that already makes traffic audible works
on the player's one without being told it exists.

The whole loop, from a pile of walls to driving away:

    /group car 6 free        — one thing, not fixed down
    /addseat driver drive    — a seat where you are standing, and it drives
    /addseat passenger       — and one for somebody else
    /drivable v8_sports      — an engine, a gearbox, tyres and a mass
    /saveas car              — anyone can place another
    /enter                   — or press E beside it
    W and S to drive, A and D to steer, space to brake, /exit to get out

- [x] `Seat` / `OccupancyComponent` on the root, `OccupantComponent` on the person. Who is in a seat
      lives on the OCCUPANT only — one place knows, so there is nothing to keep in step.
- [x] `OccupancySystem` runs last, after everything that could have moved a root. It carries two
      things differently, and the difference is the point. POSITION belongs to the seat outright.
      HEADING is the player's own, with the vehicle's rotation ADDED to it: turn a car ninety degrees
      and its driver is facing ninety degrees further round having turned their head not at all.
      Overwriting it instead would leave a driver hearing the track swing around them every corner.
      That is also why occupants do NOT wear a `ParentComponent` like the parts do — a wall keeps no
      opinion of its own about which way it is pointing.
- [x] Adding the delta rather than setting the angle is what makes it survive the trip to the client:
      the client already reconciles its heading against the server's, so a yaw the server turned
      arrives as an ordinary correction and the listener turns with the car. No new client machinery.
- [x] `DrivingSystem`, and there is not one handling number in it. What a car pulls comes from its
      engine's torque through its own gearbox; what it corners and stops at comes from its tyres'
      peak grip against its mass; what it will not exceed comes from its drag area, because top speed
      is where the engine stops out-pulling the air. `WhatItDrivesLikeComesOutOfWhatItIs` holds that:
      an F1 car and a fourteen-tonne truck are told apart by their profiles alone.
- [x] The friction circle is `TyreFriction.Demand` — the same function the client uses to decide
      whether a tyre squeals. Ask for more cornering than is left after the braking and the car runs
      wide, and the noise it makes doing so is the same number that made it run wide. Neither half
      was written for the other, and tyre squeal under a player's own braking therefore costs nothing.
- [x] Engine braking, because a closed throttle drags the engine rather than disconnecting it. Added
      when a coasting car took several minutes to stop — which is what a car in NEUTRAL does, and not
      what anyone lifting off expects. It is stronger in a low gear, as it should be.
- [x] Held controls, not sampled ones: a driver does not lift off because a packet was lost. They
      decay after three quarters of a second of silence, so a client that dies mid-corner coasts to a
      stop rather than driving away forever (`ASilentDriverCoastsToAStop`).
- [x] Driving is fed from the same input queue, budget and sequence numbering as walking, so the
      sub-tick speed limiter covers it: a driver cannot outrun it by sending packets faster any more
      than a pedestrian can.
- [x] The client stops PREDICTING while riding. A passenger's position belongs to a seat, the seat
      belongs to something the client has no simulation of, and guessing earns a correction every
      tick. Look is untouched — turning your head is still yours. `ServerStateUpdate.RidingEntityId`
      carries it, and the transition throws away the input history and the stride accumulator exactly
      as a spawn does.
- [x] No footsteps while riding. Feeding a vehicle's motion to the stride generator produces a
      footstep every stride-length of ROAD, which at sixty miles an hour is a machine gun.
- [x] A free composite and everything in it is now gridded as DYNAMIC. `RefreshGrid` was also putting
      moving things into the static half, which was harmless while only players and traffic moved —
      both spawn after the last refresh — and stops being harmless the moment a building can drive
      away, because a static entry is a permanent ghost of wherever the thing was.
- [x] Interact (E) is get in / get out. From inside something, the only interaction there is IS
      getting out. It also now runs on the tick thread through the command buffer like every other
      world-touching handler; it was reading the Arch world from the network thread.
- [x] Ownership gates taking something apart, saving it out as your own, changing what it is, and
      DRIVING it. It gates nothing else: an unowned composite is public property, and riding in
      somebody's passenger seat is not trespass. A world you cannot get a lift in is not a world.
- [x] Getting out searches outward from the SEAT, preferring the spot you climbed in from — ground
      you demonstrably fitted on a moment ago. Searching from the composite's origin works for a car
      and is absurd for a bus: stepping off one would put you level with its front bumper.
- [x] `OccupancyTests`: 21 tests, driven through the real tick in the real order.

Not done, and deliberately:

- [ ] **Standing aboard** — being carried by something while free to walk about inside it (a bus
      aisle, a boat deck, a lift). The server side is small; the client side is not, because such a
      player has movement of their own to predict AND a floor moving under it. Seats are the whole of
      occupancy for now, and `OccupantComponent.SeatIndex` is already shaped to take a -1 for it.
- [ ] **Collision response.** A driven composite that hits something stops dead. The impulse, the
      damage and the SOUND of it are the next step, and stopping dead is the honest placeholder
      rather than a pretence that it is done.
- [ ] A driven composite's parts are dynamic, so they are outside the client's acoustic bake — you
      hear a car drive past you correctly, but not the room its walls make around you while you are
      inside it. That is the same work as "a composite should be a REGION", above.
- [ ] Interior sound. Sitting in a car puts the listener a metre from its own engine with nothing
      between them; a cabin is a filter and a much quieter place than a bonnet.

## Held items, inventory, occupancy, collisions (2026-09-15, planned)

The order these go in, and why. Everything here was blocked on composites existing.

- [x] **Held items and a hand slot.** `HandsService` + `/take /drop /stow /draw /hands /inv`, and G, Q
      and R on the client. Two hands, and something needing both is recorded in BOTH slots — the same
      id twice — so "have I a hand free" is one question with one answer. A carried item wears a
      `ParentComponent` pointing at its holder, so carrying is the composite machinery once more and
      a held rifle and a wall in a house are attached the same way. Covered by `HeldItemTests`.
- [x] **Inventory over `InventoryComponent`.** A back rather than a bag, limited by MASS (25 kg) and
      not by slots, because two rifles and a crowbar is a load and six torches is not. A stowed thing
      is still an entity at a position, riding on you, and it falls from there. `HeldComponent` means
      "somebody has it" — hands versus back is answered by `HandsComponent` and `InventoryComponent`,
      so there are three places and each is one question. `LocalPlayerState.Inventory` is gone.
- [x] **`/fire` fires what you are holding**, with no `EquippedWeaponComponent` anywhere: the weapon
      is the `ItemComponent.WeaponId` of the thing in your hands, so picking a gun up and equipping it
      are the same act and putting it down disarms you. Naming one out of the air is still the dev
      trigger, and still elevated. The rest of the combat join — a shot MESSAGE, `HealthComponent` ever
      decremented, damage from `ShotResolver` — is still outstanding; see below.
- [x] **The map's own entities were unreachable by their runtime ids.** Found by a live walkthrough in
      the first minute, after twenty green tests: `/take` said "You take the Torch", `/inv` said your
      hands were empty, and the torch really was gone from the floor. `MapManager` poured TWO id
      namespaces into one dictionary — a command-spawned entity keyed by its ECS id, a map-spawned one
      keyed by the number its author typed in the JSON — so a map entity could not be found by the id
      every component in the game carries. The author's numbers now live in a load-time dictionary of
      their own. `SomethingTheMapPutThereCanBePickedUpAndReadBack` fails on the old code.
- [ ] **There is no way to change map at runtime**, so everything in `default.json` — the two rooms,
      the doors, the megaphone, the material lab — is unreachable in play: the speedway claims
      `IsDefault` and nothing else can be asked for. This is why the walkthrough above needed the flag
      flipped by hand. A `/goto <map>` needs the client to tear down and rebuild its acoustic map, so
      it is not a one-liner, but it is the thing standing between the demo map and anybody's ears.
- [ ] Held things are not HEARD yet. A carried item moves with you and a dropped one lands audibly, but
      nothing rattles, swings or knocks against a doorway on the way past, and a thing on your back
      makes no sound when you run. The emitter is the missing half.
- [ ] Taking something from ANOTHER PLAYER (handing over, stealing) — `HeldComponent.HolderEntityId`
      already says who has it, so the refusal is there and the transfer is not. The ownership note
      further down (`MayModify` becoming "you own it, or you are holding its key") waits on this.
- [x] **Occupancy**: enter a composite. Vehicles came out drivable, as predicted. See the section
      above.
- [ ] **Collision response**: `MassKg` is on every vehicle profile already. Missing is the impulse
      from relative velocity and mass, damage from kinetic energy, and the SOUND of it — which should
      come from the materials and the energy, not a sample library. Stepping onto a live track should
      be lethal, and lethal in a way you hear coming.
- [ ] Authoring commands (`/createmap` and friends) — deliberately AFTER composites, because they are
      a thin shell over the model and building them first would freeze the wrong model.
- [ ] Weather. Mostly there already (wind, precipitation, shelter) and the least blocked, which is
      why it waits for the blocked things to be unblocked.

## Glass (2026-09-14)

- [x] **`GlassBreak`** — the mechanic, tested, not yet wired to anything that can be shot. A pane shot
      out five floors up makes TWO sounds separated by most of two seconds, from two different places:
      the break at the window, then silence, then the glass arriving at the pavement at the foot of the
      wall. The gap is sqrt(2h/g) — a direct readout of which floor the shot was on, the same trick as
      the crack-to-report gap. `GlassType` matters and is not a difficulty setting: tempered always
      fails completely (it is held in compression, so there is no such thing as a neat hole in it),
      laminated never does (the interlayer keeps the pieces, and that ABSENCE is information), annealed
      depends on what hit it. Uses the mayonnaise jar for the break and the sliced tinkle pool for the
      shower and the landing. Covered by `ReflectionAndGlassTests`.
- [ ] Wire it to something shootable: a `GlassPane` on the prefab spec, hit detection against it, and
      the events rendered through the audio system. Then a window in the demo map.
- [ ] The fragment shower should be the granular engine's first real caller (`GLASS/BED` is ingested
      and waiting) rather than a scatter of one-shots.

- [ ] **"I didn't hear any buildings" — the engine still does not have this; only `--battle` does.** Outdoors now
      reverberates, but what it has is ONE parametric RT60 wash, which by construction sounds like one
      big room. What is missing is what was actually asked for: discrete, delayed, DIRECTIONAL
      reflections, so a facade a hundred metres up the street answers a shot 0.6 s later from over
      there specifically. The machinery exists — `SpatialAcoustics.CalculateAcousticPaths` returns
      reflection paths with `ReflectionDelayMs`, `ApparentPosition`, `EqHigh` and `Spread`, and
      `ClientAudioSystem` renders them — but `--battle` bypasses all of it and talks to the provider
      directly, so the demo has never exercised it. Wire the spike through the real acoustic path.
- [ ] The recordings cannot carry a gunshot on their own and the measurements say why: they are
      CLIPPED, LIMITED FLAT for up to 96 ms, and very DARK — the handgun take has 34 dB less energy at
      4 kHz than at 55 Hz, and a gunshot's character lives between 2 and 8 kHz. `RecordedLayerLevel`
      is down to 0.45 for that reason. Better takes are the fix; see the recording brief.

- [ ] Tune by ear, now that the knobs exist and are named: `Loudness.DynamicRangeCompression` (0.45)
      is how much of the real 115 dB survives into the mix; `WeaponSynth.BlastWaveLevel` /
      `NoiseLayerLevel` / `RecordedLayerLevel` are the three layers of a shot;
      `AudioPhysics.UrbanExcessHighDbPer100M` is how dull distance makes things;
      `AcousticConstants.OutdoorMaxWetDb` is how loud the street answers.
- [ ] **Fire weapons from the SERVER.** Half done as of 2026-09-15: `/fire` now fires the weapon in your
      hands and the shot reaches everyone in earshot. `EquippedWeaponComponent` turned out not to be
      needed — the held item IS the equip. Still missing: a shot message in the protocol so a trigger
      is a key rather than a typed command, `ShotResolver` consulted for what the round actually hit,
      and `HealthComponent` decremented by something, ever.
- [ ] Impact layer per material: `ShotAudition` already computes when and where the strike is heard and
      `AcousticRegistry` knows 36 surfaces — what is missing is the assets and the emitter.
- [ ] Glass: assets are in (`GLASS/SHATTER`, `GLASS/TINKLE` 24 one-shots, `GLASS/BED` 30 s for the
      granular engine). The MECHANIC is not: a pane that is shot out at height should puncture, shower,
      and then collapse to the ground a `sqrt(2h/g)` fall later — 1.7 s from a fifth floor — with the
      landing placed at the foot of the building, not at the window. That delayed second sound is the
      cue that tells a player how high up the shot was.
- [ ] Casings: the event and the sound exist and the spike plays them, but they are played at the
      shooter rather than where the case actually lands, and they do not bounce on the material
      underneath them.

- [x] **`AmbienceId` wired.** Map-level outdoor bed on the manifest, ducked by `ShelterFactor` rather
      than switched off; region-level beds play on top inside their region. Sirens removed from the
      default map. `OPENFPS_WEATHER=Clear` pins the weather so it stops swapping the ground underfoot
      mid-test. Covered by `AmbienceAndWeatherTests`.
- [ ] The outdoor bed ducks but does not arrive THROUGH the doorway — it is centred on the listener, so
      it rotates correctly but has no position in the room. Directional leakage needs the bed feeding a
      portal-positioned send.
- [ ] Rain: a precipitation layer driven by `PrecipitationIntensity` and `ShelterFactor`, plus surface
      variation (rain on a roof is not rain on grass). `Wet_Concrete` has no footsteps at all yet.
- [ ] Label the 146 unsorted footstep takes in `ASSETS/SOUNDS/_unsorted/footsteps`, then re-run ingest.
- [ ] **Wire `AmbienceId` to the bed API.** Still the dead field it was: the prefab spec carries it, the
      server sends it, no client code reads it. Now that beds play, this is the join — a region's
      ambience starts on entry and cross-fades on exit, gated by `ShelterFactor` so indoors is quieter.
- [ ] Decide what happens without Steam Audio: a bed currently refuses to start and says so. A stereo
      downmix fallback would be kinder, at the cost of the rotation that is the whole point.
- [ ] An asset ingest step: resample, loudness-normalise and loop-prep a drop folder, and record the
      layout (AmbiX/FuMa) per file rather than guessing it from the name.

Still outstanding from the audit, and still needing ears rather than a harness:
- [ ] Ear-validate the doorway-localized reverb now that it is actually routed (audit step 1 + 7).
      `AcousticConstants.ReverbSendMix` is the one knob if the rooms are too wet or too dry.
- [ ] Ear-validate the weather chain (audit step 7).
- [ ] A full logged-in walkthrough on the Windows head — its login form got the same fix, unheard.
- [ ] Ear-validate the near-field boundary effect and tune it. The geometry is verified; the LEVEL is a
      judgement. `BoundaryModel.ReflectionGain` is how strong, `MaxDistance` is how far out it reaches,
      and `AcousticConstants.MaxBoundaryReflectionSum` caps what a corner or a corridor can add up to.
- [ ] The boundary probe is six rays: a surface met at a glancing angle reflects away rather than back,
      and nothing models that yet. Worth revisiting if walls read as too present at oblique approaches.

### Corrections to the lists above
- "Server: Implement Inventory/Take/Drop commands" is done as of 2026-09-15, and done over
  `InventoryComponent` rather than beside it. See the held-items section above.
- There is still no combat system wired into the GAME, but the gap is narrower than it was. As of
  2026-09-15 a player can hold a gun and `/fire` it, and the shot goes out through `EmitWorldAudio` to
  everyone in earshot. What is still missing is the half that hurts: no shot MESSAGE in the protocol
  (the client cannot pull a trigger, only the text command can), `ShotResolver` is not consulted for
  what was hit, and `HealthComponent` has still never been decremented by anything.

## Everybody else's feet (2026-09-15)

The only body in the world that made any noise walking was your own. Another player could run past
you, round you and into you and the map stayed silent — in a game whose whole proposition is knowing
where things are by ear, the things that matter most were the quietest in it.

- [x] `StrideAccumulator` — what a stride IS, lifted out of `LocalPlayerController` and shared. There
      are two kinds of body that walk and only one kind of walking: the local player's position is
      predicted here and corrected by the server, everybody else's arrives interpolated between two
      snapshots, and a footstep is a footstep either way. Every rule in it was learned from a fault,
      which is exactly why they must not be written a second time somewhere else and allowed to drift.
- [x] `OtherBodies` — one accumulator per body on the map, fed from the same snapshot everything else
      reads. Players and NPCs; a crate is not a body.
- [x] **Derived, not sent.** A footstep as a message would be a reliable packet per body per half
      metre, for something the receiver can work out — and it would describe a position the listener
      has already heard the body leave. Deriving it from the interpolated transform puts the sound
      where the body is *as this client understands it*, which is the only place it can be without
      contradicting everything else that client is being told.
- [x] **A passenger is silent for free, and nothing here knows what a vehicle is.** The server zeroes
      an occupant's velocity and its movement system leaves their body to the seat, so a rider is a
      body at rest whose position is changing — the same shape as a teleport, a spawn and a
      reconciliation, refused by the same rule. Without it a car at sixty miles an hour would be a
      footstep every half metre of road.
- [x] **Grounded is read off the vertical velocity, not off the floor.** The movement step clamps a
      grounded body's `vel.Y` to zero and gives it to gravity otherwise, so this is a fact the client
      is already being told; one tick of free fall is a third of a metre a second. The floor itself is
      only probed at the moment a foot meets it — twice a second at a walk, not once a frame per body,
      which is what makes it affordable at render rate.
- [x] The material under somebody else's feet comes from the listener's own copy of the world, so a
      body on grass sounds like grass with nothing added to the protocol.
- [x] `OtherBodiesTests`. Tests 503 -> 511.

What is still missing, in the order it matters:

- [ ] **A footstep does not know how hard it was.** Every step is the same recorded sample at the same
      level whether the body is strolling or sprinting, and "how fast is that person moving" is
      precisely what a listener needs. It should not be a chosen curve: a foot is a mass meeting a
      floor at a speed, which is `ImpactAcoustics.Between` — the same calculation that already decides
      what a dropped rifle sounds like. That would also retire the last recorded sample in the walking
      path and make a footstep respond to the floor's material rather than selecting a file by it.
- [ ] Held things are still not heard. A body carrying something should knock, rattle and swing as it
      walks, and the client cannot know what a remote body is carrying — `HandsComponent` and
      `InventoryComponent` are not on `EntityDefinition`. Either the definition carries what is held,
      or the server emits the knock as a `WorldAudioEvent` off the carrier's own gait, which it
      currently has no notion of.
- [ ] Nothing is heard of a body that is not walking: no breathing, no effort, no clothing. A body
      standing still in a room with you is completely silent, and a body that has stopped is exactly
      the one you most want to locate.
- [ ] Jumping has a landing and no launch, for the local player as well.
- [ ] Ear-validation, which is the only thing that can settle it: two clients on one map, one walking
      circles round the other. Everything above is measured and none of it has been heard.

## Keys, running, breathing and the map list (2026-09-15)

From a list of nine. The ones that were decisions rather than work are recorded as decisions below.

- [x] **Modifiers are part of a binding.** `InputCommandMapper` is keyed on key AND modifiers, and a
      binding that names modifiers is tried first. A binding that names none still fires whatever is
      held, which is what keeps the chat brackets working — they read shift themselves.
- [x] **F5 / shift-F5** players everywhere and players here; **F6 / shift-F6** maps on the server and
      your own maps; **F8** friends (it was F7, which item 2 wants for the editor). The plain key is
      the wider question and shift narrows it to here, on both, so there is one rule rather than two.
- [x] A player list is a list of STATUSES now, not names: who is on your map, who is elsewhere and
      where, sorted with the people you can actually reach first.
- [x] `MapListRequest` / `MapListResponse` / `MapSummary`, answered from LOADED maps — a map file the
      server has not loaded is not somewhere you can go, and a chooser that offers unreachable places
      is worse than no chooser. `MapData.OwnerId` and `MapData.IsPublic` exist now, defaulting to
      public so a map authored before the question existed does not vanish by having said nothing.
      Walkable as `maps` / `mymaps` from the MUD gateway.
- [x] **Control fires**, both of them. Control no longer suppresses movement, because a player has to
      be able to fire while moving; alt still does, since alt belongs to the window manager.
- [x] **P is "what am I looking at"**, answered on the client from geometry it already has instead of
      a round trip. It says what, what it is MADE OF, and how far — the material because that is what
      the thing will sound like when something happens to it. Nothing ahead gives you your zone and
      your heading rather than "nothing directly ahead", which is a non-answer. Shift-P is the old
      `scan` (the five nearest things in any direction).
- [x] **Zones announce themselves as you cross.** Keyed on the region ID, never the name: two rooms
      can share a name, and the outdoor fallback flips between "Outside" and "Under Shelter" on a
      continuous shelter value, which would announce itself every time a bridge passed overhead.
      Spoken without interrupting, because crossing a doorway must not cut off the thing that made
      you walk through it. Z still reads it on demand.
- [x] **Shift runs.** `PhysicsConstants.SprintSpeed` (4.5 -> 7.2 m/s), `ClientInputUpdate.Sprint`
      appended to the wire, read identically by prediction and by the authority so a stride does not
      mispredict.
- [x] **Shift stopped being a blanket suppressor.** It used to stop movement dead so a shift chord
      could never walk you somewhere, which also made shift-W unusable. It now modifies the key it is
      pressed WITH: shift with a turn key is still a one-degree nudge, shift with a movement key is a
      run, and holding both does both. **Confirm this by ear** — it is the one change here that
      alters a key you already use.
- [x] **Every half metre is a footstep, and there is no cadence floor.** There was one — five steps a
      second — and it was eating most of them: a walk is 4.5 m/s, a footfall every 111 ms, so more
      than half of every walk was silent and a run lost seven in ten. A cadence cap is a rule about
      the clock standing in for a rule about distance, and the distance rule is the true one.
- [x] **`Breathing`** — exertion as an integrator with two time constants, because getting out of
      breath and getting it back are not the same process at the same speed (15 s up, 35 s down).
      That asymmetry is the point: a body that has been running is still findable well after it has
      stopped and gone quiet, and breathing is the ONLY sound a body makes once it stops moving.
      A breath is a Hiss through `WorldAudioPlayer` — nobody recorded one, and it is occluded,
      reverberated and placed by the same code as a gunshot. Both your own and everybody else's.
      B reads your exertion out in words.
- [x] `RunningAndBreathingTests`, `KeyBindingTests`. Tests 511 -> 527. Live-walked on a real server.

Still to settle, and why each is a decision rather than work:

- [ ] **Enter on a map in the list should take you there, and there is no runtime map change at all.**
      `session.CurrentMapId` is assigned once at login and never again. The pieces are closer than
      they look — the client's `MapManifest` handler already tears the world down and rebuilds it,
      and `SendManifest(peer, session)` is already a method — so travel is: migrate the player's
      entity between the two maps' ECS worlds, clear `KnownEntities`, re-send the manifest, and let
      the client's existing load path run. What makes it a session of its own is the acoustic rebake
      and proving it live.
- [ ] **Breath and footstep levels have never been heard**, only measured. The breath curve in
      particular (22 dB at rest, 56 dB flat out) is a first estimate that wants three reference
      recordings — at rest, after a jog, after a sprint. See `docs/SOUND_INVENTORY.md`.
- [ ] Your own breath is placed at your own head, so it is spatialised like anything else. It may
      want to bypass the HRTF entirely, since a sound made inside your own skull has no direction.
- [ ] Footsteps are still the last recorded sample in the walking path. `ImpactAcoustics.Between`
      would give loudness-with-speed, every one of the 36 materials, and the difference between a
      heavy person in boots and a light one in trainers. See `docs/SOUND_INVENTORY.md`.

## The car the engine is bolted into (2026-09-15)

Step one of the impulse-response experiment: generate the body's response from the geometry rather
than shopping for one. No recording needed, no licence attached, and variants come from numbers.

- [x] **`VehicleBody`** — a car as panels and a box of air. Modes from `PanelAcoustics`, the same
      plate law a door leaf and a window pane already use; cabin modes from the dimensions of the box.
      Five presets (saloon, van, supercar, race saloon, open-wheeler) plus `None` for A/B.
- [x] **`BodyResonator`** — a parallel bank of two-pole resonators, which IS the convolution. A modal
      impulse response is a sum of decaying sinusoids, so running the signal through resonators tuned
      to those modes computes exactly what convolving with the rendered response computes. The
      difference is only cost: 2,600 taps per sample against six multiply-adds per mode. Each mode is
      normalised to unity at its own note by evaluating the transfer function there exactly, so a
      weight means what it says instead of inheriting whatever gain the pole placement gave it.
- [x] **Why the body and not the exhaust.** The gas path is not time-invariant — exhaust gas leaves
      the head at 700-900 C and every pipe resonance sits ~75 % higher hot than cold, moving with load
      — so an impulse response fitted at one engine speed is wrong at every other. A steel roof panel
      does not care how hot the gas is. Gas path stays in the waveguide model; the body is the part
      that can honestly be a fixed response.
- [x] **`--body-ir [preset] [out=DIR] [sec=]`** renders each body's response, writes it to a WAV and
      measures it. One command for all three because they are one job: a response is a sound you can
      audition, a file another tool can load, and a spectrum to hold a recording against.
- [x] Applied in `EngineVoiceState`'s per-sample loop, ADDED to the direct sound rather than replacing
      it — the tailpipe still radiates straight at you and the car rings as well.
- [x] **It is free.** Measured A/B in Release with `--engine-cost body=off`: nascar_v8 8.5x realtime
      with the body against 8.4x without; v8_muscle 8.2 against 8.3. Sixteen two-pole resonators is
      nothing beside an engine integrating cylinders, valves and waveguides every sample. And levels
      are unchanged — 119.3 dB measured against 119 declared — so it colours without adding gain.
- [x] `VehicleBodyTests`. Tests 527 -> 545.

**Two faults the measurement caught, both of them physics that had been left out:**

- [x] **The panel spans were the size of the pressings.** First render: 97 % of a saloon's energy
      below 200 Hz, which is a bass boost rather than a car. A door skin 1.2 m across, taken as a
      free flat plate, has a fundamental around four hertz — and a real one plainly does not drum at
      four hertz, because it is pressed with beads, curved in two directions and spot-welded every
      few inches. What sets the note is the distance between STIFFENERS, 150-400 mm on a production
      car. It is also why a van booms: its flat sides are stiffened far more sparsely.
- [x] **The low modes were given the radiation efficiency of the high ones.** A panel small against
      its wavelength barely radiates — its two halves push and pull against each other and the air
      moves round the edge — so power goes as (ka)² until it is about a wavelength across. This is
      the SAME law `OpenEnd.Radiate` applies at an exhaust's open end, where leaving it out made
      every high-revving engine ten decibels too loud and top-heavy. Here it does the opposite and is
      just as necessary: a panel's low modes are driven hardest and radiate worst, and the balance
      needs both facts.
- [x] **And a third, about who is listening.** The cabin modes at full weight swamped everything.
      A cabin boom is a resonance of the air INSIDE the car — it is what the DRIVER hears, and it
      reaches the street only by driving the bodywork from inside or leaking past the seals. Hence
      `CabinLeak`, deliberately a leak rather than a deletion: the same cabin at FULL weight is
      exactly what an interior mix needs, and a player can already sit in one of these cars.

After all three, the presets differ the way they should and none of it was dialled in:
saloon 37/55/8 ringing 29 ms, van 30/60/10 at 85 ms, supercar 33/53/14 at 38 ms, race saloon
44/51/5 at 248 ms, open-wheeler 34/66/1 at 16 ms (percent below 200 Hz / to 1.5 kHz / above).

Next, in order:

- [ ] **Ear-validation, which is the only thing that can settle it.** `--body-ir` writes the responses
      to audition on their own; `--speedway` hears them under an engine. Nothing here has been heard.
- [ ] **`Coupling` is the one number most worth fitting to a real car** — how much of the engine gets
      into the structure at all. It is currently 0.06 to 0.40 by judgement.
- [ ] **Fit against recordings**, once clips arrive: render our engine at the recording's firing rate
      and take the ratio of the two spectra AT THE HARMONICS. That also replaces the single rolloff
      slope `--engine-match` uses, which the vehicles section already records as the wrong shape.
      Clips want to be uncompressed or >=192 kbps, a steady rpm held for several seconds, and two or
      three different rpms — because whatever moves with rpm is gas path and whatever stays put is
      body, so the separation falls out of the recordings themselves.
- [ ] **Scale the pipe delays by exhaust gas temperature** — the part an impulse response can never
      do, and one multiplier on delays that already exist.
- [ ] Interior audio can now turn `CabinLeak` up rather than invent a model.

### Fitting to the Mustang clip (2026-09-15)

- [x] **`--engine-envelope FILE`** in `tools/ingest_audio.py`: finds every steady passage in a clip,
      measures the harmonic series at each, pools them, fits the recording's own broadband tilt and
      subtracts it. Pure-Python radix-2 FFT, because this machine has no numpy and a per-bin DFT over
      a 32k window costs minutes. `--envelope-compare OURS` holds our render against the reference.
- [x] **A boomy clip does not need EQ-ing first, and should not be.** The boom is a SMOOTH curve —
      proximity, a car park, platform normalisation, codec rolloff — and an exhaust's character is
      STRUCTURE, sharp peaks at the firing harmonics and notches where the chambers cancel. Different
      shapes, so arithmetic separates them. EQ-ing by ear removes by judgement exactly what the fit
      removes by measurement, and leaves nobody able to say how much of the engine went with it.
      Measured on the Mustang: the tilt runs **-6.7 dB at 60 Hz to -52.5 dB at 4 kHz**, a 46 dB
      slope. That is the "super boomy" quantified, and removing it leaves 6.1 dB mean of structure.
- [x] **The clip did not need shortening.** Six steady passages found by themselves at three
      operating points: idle at 739-769 rpm (three separate passages agreeing to 4 %), a rev at
      1815-1901, and the take-off at 4219.
- [x] **A band is only believed where the passages AGREE.** A fixed filter sits at the same frequency
      however fast the engine turns, so disagreement across engine speeds means that band is not
      measuring the system. On this clip 5 of 20 bands agree, **147 Hz to 562 Hz** — above about a
      kilohertz the 125 kbps codec floor is what is being measured, not the car.
- [x] **The comparison refused to produce a correction, and that is the tool working.** Only two
      bands were measurable in BOTH the reference and our render, which is not enough to move a
      constant on. Comparing a band the reference cannot measure against one we render perfectly is
      how a fitting run talks itself into changing something that was already right.

- [ ] **Our muscle car's idle hunts by more than a thousand rpm** — the measurement's own by-product.
      A real Mustang held 739-769 rpm across three separate passages; ours logs "Idling (675-1832,
      hunting 1157)" and "(481-3617, hunting 3136)". That is why it produces almost no steady passage
      to compare, and it is a fault in its own right — see the idle-governor item in the session-2
      list. Fix it and the comparison gets its bands.
- [ ] Then re-run the compare. The reference's numbers are already measured and will not need redoing.

### And two faults in the body work, both found by trying to demo it

- [x] **The body was in only one of the two renderers.** `--vehicle` renders offline through
      `VehicleSynth`; the game renders live through `EngineVoiceState`. The body went into the live
      one, so the demo measured a 0.1 dB difference — the change was real, tested and completely
      inaudible in the thing built to listen to it. Both now drive the body from `Engine.Exhaust`,
      which is also the more physical choice: the exhaust is what shakes a floorpan, not the finished
      mix with the tyres in it.
- [x] **The mode weights were normalised against their SUM, which made the layer 30 dB too quiet.**
      Sixteen modes summing to one leaves the loudest around a tenth, and a tenth of a coupling of
      0.25 is two and a half per cent. It also had the wrong shape — it made a body with MORE
      resonances quieter at each of them, and a car with more panels is not a quieter car. Normalised
      against the strongest mode now, so `Coupling` means what it says. Guarded by
      `AddingModesDoesNotQuietenTheOnesAlreadyThere`.
- [x] Declared source levels re-measured with `--engine-levels` and updated across all 18 presets: a
      shell radiates more than a bare pipe, so a car with one IS louder. Most moved 2-4 dB; the
      open-wheelers barely moved at all, which is the check that the coupling numbers mean something.
- [x] `--vehicle-live <preset> body=off` and `coupling=X` for A/B listening. Tests -> 546.
- [ ] **Where `Coupling` should sit is a listening question**, and three pass-bys are with Cody: no
      shell, 0.25 as modelled, and 0.60. At 0.25 the shell measures +1 to +2.3 dB across 40-680 Hz.

## The can, as metal (2026-09-15)

"Metallic, throaty" — and the metallic half was not modelled anywhere. `ExhaustNetwork` described
what a muffler does to the GAS (chambers that cancel, packing that absorbs, a resonator that notches
a drone) and nothing at all about the steel around it. A Flowmaster was a set of gas volumes.

- [x] **`MufflerSpec.Shell`** — the can as a `VehicleBody`, deliberately the same type as a car's
      bodywork. A door leaf, a window pane, a car's wing and the side of a muffler are all a flat
      piece of stuff that rings by one law; a second implementation for exhausts would be that law
      written twice and free to drift. Everything that makes it sound different is in the numbers:
      spans of 100-230 mm instead of 250-350 (so the modes land high), a loss factor of 0.03 because
      a chambered can has no packing and no deadening (so it rings where a car panel thuds), and a
      drive that is the pressure INSIDE rather than what leaks through a structure.
- [x] **Driven from the chamber pressure, not the tailpipe.** Those are different signals — the
      chambers cancel particular frequencies on the way through — so a note can be quiet at the pipe
      and still ring loudly off the can. Taken as the sum of the two waves meeting at each chamber's
      junction.
- [x] **A packed muffler comes out right for free.** The glasspack that absorbs the gas is pressed
      against the case and damps it too, so `PackedMufflerCase` is the same model with ten times the
      loss factor. Same model, opposite result, no special case.
- [x] `shell=off` and `shell=X` on the vehicle spike, for A/B.

**The fault the rev bench found, and it was in the resonator itself:**

- [x] **Every mode was all-pole, so it passed DC.** Measured, the can was adding 5 to 8 dB BELOW
      200 Hz — beneath every mode it has — and taking the whole render's headroom with it. An
      all-pole resonator has real gain a long way under its own note; that is harmless with a
      broadband drive and ruinous with an exhaust, whose pressure is dominated by the firing
      fundamental at 40-200 Hz, well below the lowest panel mode. A small off-resonance gain on an
      enormous drive is a lot of output.
      A panel is a mechanical high-pass — below its fundamental it barely moves, and what little it
      moves radiates as (ka)² of nothing. Each mode now carries a zero at DC and a matching one at
      Nyquist, which is what makes it a resonance rather than a filter that happens to peak. The
      low band fell from 37 % to 2 %, and the sibling of this bug is already in the notes: the
      muffler chambers were once feedforward combs with a zero at DC where they needed none. This
      was the same mistake with the sign reversed.
- [x] With the zeros in, the can measures as structure in the right place: **+5.0 dB at 178 Hz** (the
      0.23 m span's fundamental, predicted 179), +3.8 at 422, and a lift right through 562 Hz to
      4.2 kHz — filling in BETWEEN the exhaust's own peaks, which is where "metallic" lives.
- [x] Comparing renders needs the spectra normalised to their own energy first: `VehicleSynth` scales
      every render by its peak, so adding energy anywhere makes every other band measure lower. The
      first comparison read as the can making everything quieter.
- [x] Declared source levels re-measured twice over, once per change to the resonator. Tests -> 548.

- [ ] **`ShellLevel` is a calibration and it is currently a guess: 0.004.** It carries the whole
      conversion from pascals inside the can to pascals at a metre outside — transmission through the
      steel, radiating area, and spreading — as one ratio. Three rev-bench renders are with Cody at
      0, 0.004 and 0.016 to settle where it sits by ear.
- [ ] The car BODY layer is honestly a small effect and was oversold: a car's outer panels are big,
      low and deliberately damped. It stays because it is free, physically right, and the substrate
      both this and interior audio needed — but it is not what makes a car sound like that car.
- [ ] **Interior audio still does not exist.** `ClientAudioSystem` never asks whether the listener is
      riding, so sitting in a car sounds exactly like standing next to it. What it needs: the engine
      through the body's transmission loss, `CabinLeak` turned up, road noise through the floor, and
      wind. The cabin MODEL is the part that is done.

## Settled by ear: the can's note, and a field of V8s (2026-09-15)

**Span is the tube's NOTE; the loss factor is its Q.** Confusing those two cost several rounds. Asked
for "deeper", the case's big face was emphasised — and it made the exhaust MUFFLED rather than fuller,
because energy piled into 75-180 Hz pulls everything above 750 Hz down with it once the render
normalises by peak. Asked for more ring, the modes were lengthened — which was heard, exactly right,
as "making the tube longer when it needs to be wider".

- [x] The shipped `MufflerCase` is the SMALL spans, widened 30 % — lowest mode 194 Hz, ring 0.030.
      Every setting here was chosen by ear from a bracket, with the level held constant so that only
      one thing moved at a time.
- [x] `--muscle-rev` knobs for all of it: `shell=` level, `ring=` loss factor, `wide=` span scale,
      `case=deep|bright`, `shell=off`. The listening test is reproducible.
- [x] `DeepMufflerCase` kept, with the reasoning, so nobody re-tries the big face.
- [x] The diagnostic was lying: it compared the can against PIPE PLUS CAN, so it saturated at 0 dB
      however loud the can got and a six-fold change read as three decibels. `PipeRadiated` is now
      tracked separately.
- [x] `BigCam_IdlesRougherThanStockCam` measures the cam with the case SILENCED. It began failing
      for a real reason: a can ringing 220 ms carries energy across a 170 ms idle cycle and averages
      neighbouring cycles together, burying the very variation a lopey cam produces. True of the
      exhaust, false about the camshaft.

**Four ways to exhaust one V8**, all on the speedway, same mass and gearing so the hardware is the
only variable — `v8_open_headers` (127.5 dB, no collector, no crossover, no muffler, and therefore no
case ring at all), `v8_bigcam` (7.4 l on a 330-degree cam that will not idle straight), `v8_glasspack`
(packing absorbs the top AND damps the case, so mellow is an absence), `v8_mild` (112.1 dB, cast log
manifolds at 0.42 primary spread). Fifteen decibels apart, none of it dialled in — every figure came
off `--engine-levels` afterwards.

**Turbo diesels**: four 13-litre truck sixes (2.2 bar, whistle 0.9, a shaft taking 1.4 s to spool)
and two small turbo-diesels (1.4 bar, 0.9 s). The same mechanism at two sizes on one lap is what
makes it read as a turbocharger rather than as a noise.

- [ ] Ear-check the new field on the real speedway rather than the rev bench.
- [ ] `ShellLevel` is still one measured ratio standing in for transmission, radiating area and
      spreading. It is set where it sounds right, which is honest but is not the same as measured.

- [ ] **Restart the server from inside the game, as an admin.** Every map change currently means
      finding the process, stopping it and running `run-server.sh` from a shell — and a map is loaded
      once at startup, so every edit costs a manual restart. An elevated `/restart` (and probably
      `/reloadmap <id>`, which is the cheaper half and does not drop anybody) would take that out of
      the loop. The pieces exist: the server already shuts down gracefully on SIGTERM, finishing the
      tick and notifying players first, so this is mostly announcing it and re-execing.

## The diesels had no engine in them (2026-09-15)

Heard on the track as "loud white noise ... I expected to hear more of the engine", and the
arithmetic agreed. Measured at load the truck six was **+29 dB harmonic-to-noise** — extremely tonal,
not noisy at all — with **-26.5 dB at 0.8-2.5 kHz and -61.7 dB at 2.5-6 kHz**. There was nothing above
800 Hz to hear. Tyres were 11 dB under the exhaust, so they were not it either.

- [x] **A BLOCK stem.** The block was folded into the intake, which made it impossible to tell
      whether the mechanical layer was quiet or merely buried under a much louder intake. It is the
      whole character of a diesel and it had never been separable. `VehicleRender.Block`, written as
      `v8_block.wav`.
- [x] It showed the fault at once: the block was **94.3 % below 200 Hz**, 1.7 % between 800 Hz and
      2.5 kHz. All thud, no clatter. `CombustionKnock = 1.3`, `ValvetrainLevel = 0.8` and
      `TurboWhistleLevel = 0.9` were all specified high and all 15-25 dB under the thud.
- [x] **The two paths are not alike and had been scaled alike.** The thud is STRUCTURE-BORNE —
      cylinder pressure into the block, through rubber mounts, into a chassis, every junction a
      mismatch reflecting most of the energy back. The knock and the clatter radiate straight off the
      block's surfaces into the air. Weighting the indirect path above the direct one is what buried
      the engine. Thud down 14 dB, knock up 8.5, valvetrain up 9.5, turbo whistle up 10.
- [x] Block now measures **64 / 28 / 5.7 / 2.0** across <200 / 200-800 / 0.8-2.5k / 2.5-8k, against
      94 / 3.6 / 1.7 / 0.5. Injector knock and valvetrain clatter live between about 500 Hz and 4 kHz
      and they are what makes a diesel recognisable AS one.
- [x] It does not touch the petrol engines: a V8's block sits 26 dB under its own intake either way,
      and every declared source level is still within 0.5 dB of measured — the balance moved, the
      loudness did not.

- [ ] Ear-check on the track. If the clatter still wants more, `thud` is the one number: it is a
      single scale and everything else is already where it should be.
- [ ] The turbo whistle is now 10 dB up but has never been heard in the map against a real field.

## The white-noise tail: every car was sliding, all the time (2026-09-15)

Heard walking beside the track: the tyres screeching with a long broadband tail that travelled with
the field. It was not the turbos and it was not a tail — it was every car in every corner rendering
a FULL SLIDE continuously.

- [x] **The sibling of the racing-line banking bug, surviving by one file.** `RaceLine` learned that
      banking raises the cornering limit — that was the fix for cars lifting four semitones a lap.
      `TyreDemand` never did: it differentiated the interpolated velocity and divided by the tyre's
      FLAT-ground grip. On the speedway's ten degrees with a 1.75 g slick that is
      `(mu + tan)/(1 - mu·tan) / mu` = **1.59**, against a full-slide threshold of **1.45**. Measured
      across the field: stock 1.59, muscle 1.43, truck 1.43. Every one of them at or past full slide,
      for the length of both turns, for ever.
- [x] **A listener cannot fix this for itself**, which is why the number is on the wire now. A banked
      constant-radius turn at constant speed and a flat one have IDENTICAL accelerations — the bank
      appears in the normal load, not in the kinematics — so no amount of differentiating a velocity
      can tell them apart. `EntityState.TyreDemand`, one byte, 0..2 over 0..255.
- [x] Computed in `VehicleSystem`, which is the only place that knows the corner. Laterally it is
      `(v / vlimit)²` — not an approximation: the limit speed is where lateral acceleration equals
      available grip and a = v²/R either way, so the ratio of accelerations is the square of the
      ratio of speeds, and the line's limit ALREADY has the banking in it. Longitudinally it is the
      applied acceleration against the same grip. They combine in quadrature, because one contact
      patch does both.
- [x] A car tracking its line now reads **1.0** — singing, which is right for a car at the limit, and
      not sliding. `ACarOnItsLineThroughABankedCornerIsAtTheLimitAndNotPastIt` asserts both halves:
      that the naive reading really would have exceeded the slide threshold, and that the fraction
      form lands between squeal onset and slide onset.
- [x] It also removes a numerical differentiation of an interpolated velocity from the client's hot
      path, which was fragile for its own reasons. Tests 548 -> 556.

- [ ] Ear-check: the corners should now SING rather than hiss, and the hiss should appear only where
      a car is genuinely over the limit — the pace car being caught, a truck braking too late.

## A crowd that reacts (2026-09-15)

The one sound in a stadium that needs no recording, and the granular/stochastic case's first real
caller. A clap is two flat surfaces meeting — an impact — and a crowd is a Poisson-ish field of them.

- [x] `Applause` in Common: renders a crowd from a head count, an intensity and a duration.
- [x] `CrowdComponent` + `CrowdSystem` + a `crowd` prefab. A source with a PLACE and a SIZE, not a
      bed: silent until something goes past. Twelve of them along the grandstand deck, so the stand
      has width. It reacts to anything moving fast enough close enough — it does not know what a car
      is, so whatever goes on the track next gets a reaction for free.
- [x] Level is a TEN-log of the head count, not a twenty: independent sources add in power, so twice
      the crowd is three decibels. That is also why it is affordable — past a few hundred simultaneous
      clappers the sum is statistically noise, so 320 are rendered and the rest are accounted for by
      that square root. The budget IS the physics.
- [x] Routed through `TransientSound.SynthKey` — "applause:400:0.55:3" — the same escape hatch a
      gunshot uses, for the same reason: four characters cannot describe a thousand people.
- [x] `--applause [people= intensity= sec=]`, `ApplauseTests`. Tests 557 -> 567.

**Two listening verdicts, two real faults, and they are worth keeping:**

- [x] **"They all sound like pouring water."** The first clap was a short noise burst through a sharp
      resonator — which is an excellent model of a WATER DROPLET, because a drip is exactly a brief
      narrowband resonance. A clap is the opposite: two broad flat surfaces meet and stop, radiating
      two or three milliseconds of BROADBAND burst. The trapped air colours it rather than sustaining
      it, so the cavity became a tilt across a wide band instead of a note.
- [x] **"Crackling static."** The arrivals were one Poisson process for the whole crowd, and a
      Poisson process is MEMORYLESS — it has no rhythm by construction, and dense identical
      memoryless clicks is the definition of static. Real applause is not memoryless, because a person
      has a TEMPO: they keep roughly to it, drift off it, and no two share one. Each rendered clapper
      now keeps an identity for the whole burst — where they are sitting, what their hands sound like,
      how fast they clap and where in their own cycle they are — and the sum of a few hundred slightly
      different quasi-periodic trains has structure a listener can pick individuals out of.
- [x] Also added on the second pass: people fill an AREA, so the count at a given distance grows with
      it while the level falls as 1/r. A few near ones are much louder than the wash behind them, and
      without that spread every clap is the same size and the sum is a texture rather than a room.

- [ ] Cheers, gasps and boos need voices and cannot be synthesized. Babble needs 8-12 takes of
      ordinary conversation — see `docs/SOUND_INVENTORY.md`.
- [ ] The crowd should feed the same reflection path the cars do: the grandstand is a big hard
      surface, and a cheer arriving off the deck a beat after the direct sound is most of what makes
      a stand sound occupied.

## A field of forty, and seventeen different machines (2026-09-15)

`FIELD_SIZE = 40`. Seven stock cars still make it a stock car race — the support runners are the
thing being caught, not the thing being watched — and no two of the rest share a mechanism.

- [x] **`SportBike`** — a litre inline-four to 14,500 rpm. It sounds nothing like a car for a reason
      that is arithmetic rather than character: four cylinders firing every 180 degrees at 14,500 is
      483 firings a second, so its FUNDAMENTAL is a musical pitch where a car's is a beat you could
      count, and its orders run into the kilohertz where the ear is most sensitive. A quickshifter
      box: 90 ms, the clutch never opening, heard as a cut rather than a lift.
- [x] **`V8Blown`** — 7.4 with a Roots blower. Two sounds, not one: it WHINES, because meshing rotors
      pump in discrete gulps at a high multiple of engine speed (order 12, so 800 Hz at 4,000 rpm),
      and it MOVES AIR, so every cylinder gets a harder blowdown. And unlike a turbo there is no lag
      at all — it is geared to the crank, so there is nothing to spool, which is exactly why one
      sounds instant and the other does not. `BlowerWhineLevel` and `BlowerWhineOrder` had been in
      `MechanicalSpec` since the beginning with nothing ever using them.
- [x] **The V10 supercar got its box.** An automated single-clutch, 60 ms against a manual's 280 —
      too short to hear as a gap, so the note steps down like a hammer instead of falling through a
      lift. That one number is most of the difference between a supercar and a fast car.
- [x] Three ordinary hatchbacks at 94 dB, twenty decibels under everything else, because a circuit
      where everything is dramatic has no scale to it.
- [x] Two faults the new presets found:
      * **The bike's idle flared to 4,500 rpm.** Its crank is a tenth of a big block's, so anything
        that pushes it moves it a long way and the catch flare overshot before the governor could get
        near it. What brings a real bike back is FRICTION — a litre four is pumping and rubbing far
        harder for its size than a lazy V8 — so the losses were raised to match the inertia rather
        than the governor being stiffened enough to do the physics' job for it.
      * The applause density test measured peak crossings, which the crack and the RMS normalisation
        invalidated. It measures CREST FACTOR now: sparse claps are spiky, a roar approaches noise,
        and that is the same statistic as "can you pick individuals out".

- [ ] **Load check at forty.** Measured fine at 60 voices with thirty cars; forty has never been run.
      Watch the `Mixer load` line's starve / real-channel figures in the first ten seconds.
- [ ] The borrowed-voice Doppler bug matters more at forty than at thirty, because more of the field
      is borrowing. Still the main suspect for "they don't sound like they're going that fast".

### The bikes were all rumble (2026-09-15)

"I expect to hear a popping and growling from the engine, not just rumble." Measured, the V-twin's
exhaust was **76.5 % below 200 Hz with half a per cent between 800 Hz and 2.5 kHz** — where the V8 is
13 / 70 / 17. Pure rumble, no bark, and the others were fine, so it was not a general fault.

- [x] **A big slow twin is the hardest case for keeping the top of the band, and it is arithmetic.**
      Harmonics are spaced by the FIRING RATE, and at 3,000 rpm a twin fires 50 times a second where a
      V8 fires 200 — so to have any energy at a kilohertz the twin needs its TWENTIETH harmonic where
      the V8 needs its fifth. Any per-harmonic rolloff therefore hits a twin four times as hard. That
      is why one preset can be rumble while every other one is fine.
- [x] Two things push back, both properties of this pipe rather than taste:
      * **`WallLossMultiplier` 1.8 -> 0.85.** A straight pipe on a cruiser is under a metre from valve
        to air and runs very hot, so there is little wall loss to take the top off.
      * **`Steepening` 1.0 -> 2.0**, and this is the mechanism behind a big twin's bark. 1.75 litres
        across two cylinders is the largest single-cylinder charge in the catalogue, and a
        finite-amplitude wave that big STEEPENS as it travels, converting its own energy upward into
        exactly the harmonics that were missing. It had been left at the default.
- [x] **It was carrying a glasspack while calling itself "straight pipes"** in its own Name field.
      Now `MufflerSpec.StraightPipe`, which also means no case ring — correct for open pipes.
- [x] `OverrunPopRate` 6 -> 16, which is the "popping" literally.
- [x] Result: **17.6 / 37.5 / 38.2 / 6.7**, against 76.5 / 22.9 / 0.5 / 0.1. The sports bike got the
      same treatment less severely (0 / 75.7 / 21.2 / 3.1 from 0 / 92.7 / 6.9 / 0.3).

## Listening session, 40 cars (2026-09-16) — what is right, what is not

**Confirmed fixed by ear:** cars no longer stop dead at close range. Wall reflections off the
retaining wall are clearly audible and read as coming from the wall. A listener standing at
(-19.3, 0, -172) looking 45 degrees into the oncoming cars is the best perspective on the map so far
— worth keeping as the reference position for any future listening test.

### 1. "Their engine rpm sounds like they're just driving at a comfortable rpm"

NOT a gearing fault — the stock car is at 9,128 rpm against a 9,200 redline at 300 km/h, and the
formula cars read as flying, correctly. So the arithmetic is right and the PERCEPTION is not, which
points at the voice rather than the car. Two suspects, in order:

- [ ] **The borrowed distant engine voice** (already open, from session 7). A car beyond the synthesis
      budget reads another car's ring, inheriting THAT car's Doppler and then applying its own. With
      forty cars far more of the field is borrowing than at thirty, so this is worse than it was. It
      is the single best explanation for "technically at the redline, sounds like cruising", because
      a borrowed voice's pitch movement belongs to a different car.
- [ ] Failing that: `OPENFPS_AUDIO_TRACE=<entityId>` prints the Doppler factor per update at 250 Hz.
      A car passing at 70 m/s should swing between about 1.25 and 0.83. If it does not, the pass is
      not being rendered at the speed it is happening.

### 2. "Sound comes into view, fades out, then I hear them flying up as they approach"

- [ ] Reported on the F1 most clearly. Wind up, cut out, fade back in on the pass. The listener's own
      guess — that the first part is the REFLECTION of an approaching car and the direct sound arrives
      later — is plausible and would be correct behaviour heard as a fault. But a voice that "cuts
      out" is also exactly what a budget eviction sounds like, and forty cars is past anything the
      budget has been measured at. Check `EngineVoiceBudget` churn in the log before assuming it is
      the reflections.

### 3. Occasional pops at certain listening angles

- [ ] Rare, angle-dependent. Candidate: reflections arriving coincidentally and summing — the
      `AudibilityFloor` ranks candidate reflections each frame, so a set flipping in and out at a
      particular geometry would click. `--speedway speedway probe` prints what bounces off what.

### 4. The F1 sounds like a siren — DIAGNOSED, not fixed

- [x] Measured on a drive render: **90 % of the exhaust energy in 0.8-2.5 kHz and 0.0 % below 200 Hz.**
      Part of that is structural and correct: a V10 at 15,500 rpm fires 1,292 times a second, so its
      fundamental is at 1.3 kHz and the exhaust has nothing lower to make. It should scream.
- [x] **The obvious fix was tried and rejected.** A formula car breathes through a 26-litre airbox on
      a 0.8 m snorkel — a Helmholtz resonator at about fifty hertz, the lowest thing on the car by an
      order of magnitude — and its intake renders 20 dB under the exhaust, so raising it looked like
      the answer. It is not: the intake is itself **96 % inside 0.8-2.5 kHz with 0.0 % below 200 Hz**,
      so turning it up adds more siren and no body. Reverted; the reasoning is in the preset.
- [ ] **So the airbox is described in the spec and is not producing its resonance.** That is upstream
      of any level and it is where to look next. Check whether `IntakeNetwork` models `AirboxLitres` /
      `SnorkelLengthMetres` as a Helmholtz volume at all, or only as another pipe. If a 26-litre box
      on a 0.8 m snorkel does not put a peak near 50 Hz, nothing else will give this engine a bottom.
- [ ] Also worth checking once the airbox works: the block is **41 dB under the exhaust**. Forty
      valves closing 129 times a second each is an enormous amount of metal, and a racing engine is
      mechanically NOISIER than a road one, not quieter.

## The map named its places and the whole track moved indoors (2026-09-16)

Logging in put the listener in a big room: every car, the PA and the crowd arrived with a tail on
them, on an oval in the open air. It appeared with the zones — the commit that gave the speedway
Front straight, Turns one and two, Infield and Grandstand so a blind player could be told where they
were standing — and the reason is one sentence: **every open-air behaviour in the engine was keyed on
"the listener is in the GLOBAL region id", which is not a question about the place, it is a question
about whether the map has bothered to name it.** Name the place and you were indoors.

Measured through the real FMOD graph with the new `--open-air-reverb` spike, with the old rule put
back to make sure the instrument can fail:

| standing in | decay | wet |
| --- | --- | --- |
| the named infield (old rule) | 500 ms | 0 dB |
| the identical unnamed ground one step away (old rule) | — | −80 dB |
| either of them (now) | — | −80 dB |
| a sealed concrete room (now) | 10,000 ms | 0 dB |

- [x] **A face of material "None" is an OPENING, not a mirror.** The speedway's regions declare no
      surfaces, which resolves to six "None" faces — absorption 0, transmission 1. Sabine reads
      absorption 0 as a perfect reflector, so an unbounded 277,000 m³ infield came out as a sealed
      box; and because nothing had absorbed, the total fell under the "did anything absorb?" guard
      and the code substituted `DefaultReverbDecayMs`. A 500 ms room, at full wet, over the whole map.
- [x] **The estimate has a precondition and it is not a taste.** Sabine's V/A describes a diffuse
      field in a CLOSED enclosure, where the energy keeps coming back until the surfaces have eaten
      it. Open one face and there is no such field. `OpenFPS.Common/RoomAcoustics.cs` owns the question
      now: `IsEnclosure`, `Enclosure` (area-weighted, because an open sky over an infield is not "one
      face out of six"), and `DecayMs`, which returns ZERO where the model does not apply and the
      clamp — not a 500 ms default — where a closed boundary absorbs nothing.
- [x] **The ray-traced RT60 was writing over it uncapped and ungated.** `ApplySimulatedReverb` sets the
      listener region's decay from the simulator and then, for the global region only, caps it and
      opens the wet level in proportion to what the rays found. Both of those returned early on a
      named region, so the infield got the rays' decay at full wet. It now applies to any bus with no
      estimate behind it, which is the same set plus every named stretch of open ground.
- [x] **Three more sites asked the same wrong question** and all now ask `RoomAcoustics.IsEnclosure`:
      air absorption doubled its reference distance "indoors", so two hundred metres of track kept its
      high frequencies and the field read as small and close; the small-room gain lift took 6 dB off
      every car on the map (`clamp(1000 / volume)` of an infield is the 0.5 floor); and a room's
      reverb could only be heard through its doorway by a listener standing in the global region.
- [x] The async worker was passing the SOURCE's region as `listenerIndoors`. It asks about the
      listener now.
- [x] The outdoors is built with six open faces, because that is what outdoors IS. It used to be
      given six real materials and kept dry only by a test for its own id.
- [x] A region marked indoors that declares no surfaces now warns at map load. "Not configured" and
      "working" look the same from everywhere else, which is the fault this whole session is about.

### Still open

- [ ] **The grandstand deck should declare its boundary.** You are twelve metres up with a wall at
      your back and a roof over part of it, and the map says "no surfaces" for that region like every
      other. It is the one place on the speedway that should have a short real decay of its own.
- [ ] **A region's faces could be MEASURED rather than declared.** `AcousticVolumeGenerator` has every
      static collider and its material, and `CompositeAcoustics.Survey` already computes per-face
      coverage for player-built rooms — the same test against map geometry would let a map stop
      declaring surfaces at all and have the boundary fall out of what is standing there.
- [ ] **Air absorption is not in the Sabine estimate.** 4mV is the reason a large hall does not ring
      for ten seconds, and a sealed concrete 10 x 5 x 10 currently clamps at 10 s. Left alone
      deliberately: it changes rooms that have been approved by ear, and nothing forced it here.
- [ ] `--speedway` in the lab still hardcodes `GlobalRegionId` as the listener's region and builds no
      acoustic map, so it could not have reproduced this and cannot confirm the fix. The confirmation
      is `--open-air-reverb` and `OpenAirReverbTests`.

## The stand was full and silent, and a borrowed car had two Dopplers (2026-09-16)

Three faults, found by measuring rather than by listening, because none of them was audible as itself:
the crowd was simply not there, and a distant car's revs were simply wrong.

### The crowd was emitting the whole time

Ninety reactions a minute, eleven of the twelve blocks, at 114-118 dB. `CrowdSystem` was working
perfectly and nothing it produced could be heard. Three reasons, and only the last is about crowds:

- [x] **A transient's range was capped at 250 m**, and the mixer fades a voice to nothing over the
      last quarter of its range — so everything past 187 m was on its way out for being far away, and
      the grandstand is 219 m from where you land. A range shorter than a sound can be heard is not a
      saving, it is a silence. `Loudness.AudibleRange` decides it now, and the SERVER's per-map
      earshot (400 m here, computed from what is actually in the map) is what bounds the cost.
- [x] **A crowd was placed as a POINT.** `TransientSound.ExtentMetres` and `Loudness.Place(level,
      extent)`: four hundred people at half a square metre each fill a patch eight metres across, and
      inside that the level is flat because stepping towards one clapper steps you away from another.
      The gain comes down to pay for the wider reference, so the FAR field does not move — a crowd and
      a point source of the same power sound the same once you are well outside them, and widening the
      reference without paying for it would just have made the stand louder than physics allows.
      Measured: the crowd now sits 16.4 dB under a bike 60 m away where the physics says 12.8; it was
      out by 16 dB before. (The residual is the engines' own `max(reference, 3 m)`, which lifts every
      car about 6 dB above what `Place` intends. Left alone — it is a calibration that was settled by
      ear, and nothing here forced it.)
- [x] **The applause key carried unrounded floats**, so nothing above `WorldAudioPlayer.IdFor` — which
      quantises for exactly this reason — reached the escape hatch. `Applause.Quantise` fixes that.
      **The claim that went with it was wrong and the measurement is here:** it is NOT ninety fresh
      renders a minute. The intensity is built from two SATURATING terms, so a steady race lands on the
      same handful of values whatever the key does — about fifteen distinct buffers in a minute with
      the quantiser and twenty-two across the whole (cars nearby, fastest car) grid without it. The
      test that was meant to protect it counted keys and passed either way; it asserts the property
      now (two crowds a person apart are one buffer) and the sabotage row bites.
- [x] **A transient now reflects off the world**, which is the todo item from the day the crowd was
      written: "a cheer arriving off the deck a beat after the direct sound is most of what makes a
      stand sound occupied". A reflection of a one-shot IS the one-shot, delayed, quieter and from
      somewhere else — so it needs no voices and no model, only the same image-source search the cars
      use and the queue that already exists. Measured on the real map: a cheer from the middle of the
      stand reaches the racing line 53 ms later off the back wall behind the seats, at 0.75 gain. A
      gunshot gets the slapback off the building opposite for nothing.

### The reverb that appeared when you stepped onto the track

- [x] The same fault as the morning's, with a trigger. The client overrides a region's FLOOR material
      with whatever is underfoot, so a sector of track with no declared surfaces had NO absorption
      while you stood on the grass — the 500 ms default — and exactly one absorbing surface in a
      9,000 m³ box the moment you stepped onto the asphalt, which Sabine reads as ten seconds. Now
      five open faces are five open faces whatever you are standing on.
- [x] And `SetAcousticMap` — which tears down every reverb bus on the map and every Steam Audio voice
      attached to them — is no longer called when the underfoot material changes nothing, which out of
      doors is always. It was firing every few steps.

### A borrowed voice had two Dopplers

- [x] A car beyond the synthesis budget borrows a near car's ring buffer, and read it back from that
      car's PLAY POSITION — which advances at whatever rate the mixer is consuming it, which is its
      channel pitch, which is ITS Doppler. So the borrowed car inherited the lead car's Doppler and
      then applied its own: two cars' pitch movement on one voice, going different ways. It keeps its
      own read cursor now, advancing one sample per sample, nudged back into place at a hundredth of
      the sample rate — inaudible, and slow enough that it is correcting drift rather than adding
      pitch. A REFLECTION still follows the source, because an echo of a car is that car's sound
      arriving late and the source's Doppler belongs in it.
- [x] `BorrowedVoiceDopplerTests` drives the mechanism with the source consumed 25 % fast: following
      it, the borrowed voice comes out at 333 Hz where the source is at 98; with its own cursor, 99.
      The control — the same code with the source consumed at real time — is correct, which is why
      this was invisible until a car moved.

### Still open

- [ ] The three blocks at the ends of the stand (x = 140 to 220) are 71-79 m from the racing line and
      the react radius is 70, so they never react at all. Not obviously wrong — the action is in front
      of the main straight — but it means a sixth of the stand is dead.
- [ ] Cheers, gasps and boos still need voices and cannot be synthesized. Babble needs 8-12 takes.
- [ ] The crowd's reflections are first-order and capped at two, like a car's. The stand is the one
      place on the map where a second-order path (deck, then back wall) might be worth having.

### Listening pass on the transient reflections, same evening

Three reports, three faults, all introduced by the reflection change a few hours earlier:

- [x] **"I'm only hearing the reflections of the clapping."** A transient's voice id was a hash of the
      SOUND — but the id is what the VoiceManager keys a submission by, so a sound and its own
      reflection, submitted together under one id, replaced each other and the last one written won.
      Every transient gets its own id now, from its own band.
- [x] **And that band overlapped two others.** `-|hash % 1,000,000| - 1000` spans −1,000 to −1,001,000,
      straight across the engine-echo band at −600,000 and the borrowed-voice band at −700,000. A door
      or a footstep whose hash landed there took over a car's reflection voice or a distant car's
      voice — which is heard as "a loud reflection from a bike that was long gone", and as cars going
      quiet for no reason. Transients now live at −100,001 to −500,000, above both.
- [x] **"Footsteps shouldn't have such pronounced and delayed echo."** Two things. The delay was
      counted TWICE: the facade already delays every submission by its own distance over the speed of
      sound, and the echo is submitted at the mirrored position, so queueing it late as well doubled
      the extra path. And the threshold was `ImageSource.MinGain` — 34 dB down, which is right for a
      continuous source whose reflection is part of the wash it makes, and wrong for a one-shot. A
      copy of a single event far enough under the original is not heard as a second event at all.
      `ImageSource.EchoAudibleRatio` is 20 dB, and it is a RATIO, which is what ties it to the
      loudness of the thing: your own footstep is a metre away and the wall is fifty, so its echo is
      40 dB down and there is no echo; the same footstep in a corridor two metres wide is 10 dB down
      and slaps. Measured on the real map: the cheer off the back of the stand is 0.753, the
      footsteps under your own feet are 0.078 and 0.040.
- [x] **The transient reflections were never obstruction-tested**, which reproduced exactly the fault
      `EngineReflections` was written to fix — an echo that cannot be blocked is all that is left
      where the direct sound is. They go through `EngineReflections.FindReflections` now, which does
      the near-face prefilter and tests both legs.
- [x] A map that names no regions at all now says so at load. Regions are OPTIONAL — since a region's
      acoustics come from its own boundary rather than from its existence, naming a place costs
      nothing and changes nothing — but a map where everywhere answers to "Outside" is useless to a
      player who cannot see, and that is exactly what went unnoticed on the speedway for weeks.

### The one-shot that missed its moment (same evening, second pass)

Reported at a specific spot — walk across the track at (-19, 0, -172) and reflections from bikes pile
up where there are none, and the clapping arrives from the INSIDE of the oval instead of from the
stand outside it.

- [x] **A submission that does not win a voice was kept and re-scored for ever.** `VoiceManager` holds
      a submission until it has been seen to start and then stop, which is right for a car — it is
      still there, still making a noise, and deserves a voice back when one frees. A ONE-SHOT is not:
      a clap that lost the budget this frame cannot be played later, because later is a different
      moment. Left in the queue it FIRED whenever the listener moved somewhere that made its score
      win — from a mirrored position computed for a place they had walked away from, which is exactly
      "loud reflections on the inside of the track, only at that location". An event that did not win
      a slot is dropped now, and a transient is marked `IsEvent`.
- [x] It was invisible until transients got one voice id each this afternoon: sharing ids by SOUND
      meant the next footstep overwrote the last one's stale entry, so the queue stayed small by
      accident and the leak looked like a feature. `VoiceManager.SubmissionCount` is exposed for the
      next time.

**And the spot itself is not a fault.** Measured out of the real map: at (-19, 1.7, -172) there is a
3.5 m concrete wall 2.5 m away — `centre (-20.8, 1.75, -173.6), size 44.5 x 3.5 x 0.6` — and the
crowd is 47.7 m away and 13.5 m up on the far side of it. The direct path is BLOCKED, and once the
reflections are obstruction-tested nothing reaches that point either. Standing there, the stand
should be muffled and arrive over the top of the wall by diffraction. Before this afternoon's
obstruction fix the echoes were the only thing exempt from that, which is why they were all that
was left.

### A stand is a diffuser, not a slab (2026-09-16, evening)

> "the clapping reflections are crisp and they shouldn't be... a lot of the noise from clapping will be
> taken by the open space so reflections shouldn't be so directional, more diffused than an exact copy
> like a mirror"

- [x] **A surface's scattering now splits its return.** `ReflectingSurface` carries `Scattering`, and
      `ImageSource.FirstOrder` gives the mirror image only `(1 - s)` of what the surface reflects. The
      rest radiates FROM THE SURFACE, sampled at two points across its width: each tap is the Lambert
      fraction `A·cosθs·cosθl·direct² / (π·rs²·rl²)` — which is "taken by the open space" written down —
      and the two arrive over the spread of path lengths the face covers, which is what makes it a wash
      rather than a copy. Measured on a 40 x 8 m wall: **concrete slab specular 0.829 / scattered 0.045;
      the same wall as a full stand, specular 0.053 / scattered 0.104**, the taps 28 ms apart.
- [x] **A rough surface answers from wherever you are standing.** The diffuse share is no longer gated
      by the specular bounce landing on the face — that test is what makes a mirror a mirror, and a
      scattering face does not obey it.
- [x] **`Audience` is a material**: absorption 0.72, scattering 0.8. An occupied seating area is the
      most absorbent and most scattering thing in ordinary acoustics, which is why a full house deadens
      a hall. Nothing about grandstands is written anywhere in the engine.
- [x] **The speedway's stand is surfaced in it** (`grandstand_seating` prefab, deck and upper back
      wall). The retaining walls are still concrete and still slap. Measured at the back straight, the
      crowd's return went from **one specular arrival at gain 0.93** to **two diffuse arrivals at 0.105
      each, 88 and 92 ms out** — a wash 20 dB down instead of a copy 0.6 dB down. The bike's slapback
      off the concrete retaining wall is unchanged at 0.41.
- [x] Scattering also reaches the CARS' reflections, because it is a property of the surface: off grass
      (scattering 0.9) a car's mirror image all but disappears, which is what grass does. Off the
      concrete walls, where the cars are actually heard, it costs 10 %.

### "The reflections feel like they're beamed right into my ears"

- [x] Not the geometry. When the Steam Audio voice pool is empty a sound plays without HRTF — that is
      deliberate and fine — but it then fell back to FMOD's **LINEAR** rolloff, which ramps straight
      from the reference distance to the range: over the 3 km range a transient now honestly carries,
      a source two hundred metres away comes out at **93 % of full scale**. So an occasional clap or
      skid arrived essentially undimmed while everything around it obeyed `min/distance`. It was made
      much more likely by everything else today — more voices, longer ranges. The fallback uses
      INVERSE rolloff now, which is the same law the HRTF path applies by hand.
- [x] The mixer load line reports `N without HRTF (M new since last)` so pool exhaustion is visible
      rather than inferred.

### Sabotage

- [x] 17 new rows, every one caught: the open-face rule, `None` as a surface, room materials reaching
      the decay, the outdoors' own faces, the underfoot override, the crowd's extent, the gain
      compensation, the applause key, the range cap, the echo ratio, the borrowed cursor, the one-shot
      drop, the voice-id bands, audience scattering, the specular/diffuse split, the tap spread, and
      whether a rough surface answers off-axis. The runner takes substrings now (`sabotage-rooms.py
      <substring> ...`) so adding a row costs one test run instead of sixty.
- [x] The budget had NO tests because `VoiceManager` needed FMOD to construct. `IVoiceSink` — four
      methods — makes it testable, and `VoiceBudgetTests` now proves a one-shot that loses the budget
      is forgotten while an engine that loses it comes back.

## A clap is hands, not cellophane (2026-09-16, late)

> "the clapping itself needs to be more pronounced. each clap should have a long distinct transient
> because this clapping sounds like a crinkling bag. I thought we worked on this but it doesn't sound
> like the clapping was updated on the map"

It IS the same renderer on the map as in the lab — one `Applause.Render`, no second path. What was
wrong was the clap, and measuring one told the whole story. The old model, one clap:

```
decay    -20 dB at 12 ms, nothing at all after 24 ms
energy   31% below 200 Hz, 45% to 1.5 kHz, 24% above   (per octave, `--applause`)
```

- [x] **The pocket of air had stopped ringing.** The first version had a sharp resonator and was called
      pouring water — right, a drip IS a brief narrow resonance — so it was replaced with a plain
      low-pass tilt, which has no note in it at all. Both were wrong the same way: the question is not
      whether the cavity resonates but HOW HARD IT IS DAMPED. Two soft leaky palms give a Q of about
      three. Cupped hands trap more of it and ring more; flat hands let it out sideways. `Cupping` is
      per clapper, so a crowd is a mixture, and the mixture is the texture.
- [x] **And the flesh did not thump.** Two palms meeting is a soft heavy impact before it is anything
      else: low, slow, and the part that carries. It is what gives a clap a TAIL — with it the clap is
      at −49 dB thirty milliseconds in; without it, −66 and gone.
- [x] Rebalanced with both in: `16% below 200 Hz, 74% to 1.5 kHz, 11% above` against the old
      `31 / 45 / 24`. The bulk is now in the two octaves where hands are, instead of up where paper is.
- [x] **Why it was WORSE across the track**, and this is the part worth keeping: air takes the
      4-12 kHz half of a clap away over a couple of hundred metres. A clap made only of edge arrives at
      the infield as a crinkle by construction. One with a body arrives as a clap.
- [x] `ClapTests` — body per OCTAVE (counted per hertz, the ten kilohertz above 1.5 kHz swamp the two
      octaves that matter and a paper bag measures as well balanced), the tail, the near edge of the
      crowd, and that it never repeats. Four sabotage rows, all caught.

### And the render was stopping the world

- [x] **170 ms, on the game thread.** A new crowd buffer was synthesised by whichever thread took the
      packet off the wire — three hundred people clapping for three and a half seconds is a thousand
      claps a second to render. The same reasoning that moved engine synthesis off the mixer callback
      applies: the thread that must not be blocked is the one holding the clock. Rendering happens on a
      worker now and the finished buffer is registered in `Update`. The event that discovers a new
      sound is dropped rather than delayed — a one-shot belongs to a moment — and every one after it
      finds the buffer waiting.

### Still open

- [ ] The balance is measured, not settled by ear. `CrackLevel` (2.0), the thump mix (0.30) and
      `Cupping` are the three knobs; `logs/clap/applause_400_0.70.wav` is the current render.
- [ ] A crowd's first reaction of each kind is silent, because that is the one that pays for the
      render. Pre-warming the handful of keys a map's crowds actually produce would remove it.

## WHERE THE NEXT SESSION STARTS (2026-09-16, end of session 9)

The argument, the numbers and the order are in **`docs/VOICES_MACHINES_AND_THE_CITY.md`**. Read that
first; this is the index to it.

The specification, in Cody's words: *"this stuff needs to be done without the user thinking about it.
I want to place a tree, or water fountain or a race track, and those features should have
corresponding correct binaural audio effects, reflections — most things are diffused reflections."*

- [x] **1. Parts as data.** DONE (session 10). `MachineDefinition` is a machine as a list of parts —
      `engine`, `exhaust`, `intake`, `tyres`, `body`, `gearbox`, `chassis`, each a model plus a profile
      plus where it sits — and `MachineRegistry.Describe`/`Assemble` are the two directions of one
      translation. Every built-in vehicle survives the round trip field for field, including through
      JSON (`MachineTests`), which is what makes an authored machine use the SAME parts the library is
      made of. Authored machines live in `machines/*.json` beside the maps, loaded by server, client
      and lab; one may be built from parts alone or `base`d on an existing machine, and one of the same
      name overrides the built-in. `--machines`, `--machines <id>`, `--machines export=DIR`.
      `--engine-levels` measures machines now and every built-in still measures its declared level.
      The two-voice car is done too: the engine is integrated once and writes two taps (exhaust+body,
      intake+block), a car close enough for its outlets to subtend more than ten degrees gets a voice
      for each (`Localisation`), and the taps SUM to exactly the single voice, so spending or not
      spending the second one is inaudible as a level change. `--machine-pass` auditions it.

      Still open from it:
      - The intake voice aligns to the exhaust voice's play position at its first block, so it can sit
        up to one mixer block (23 ms) either side of it. Stable thereafter. Exact alignment wants
        FMOD's DSP clock (`getclock`), which costs a marshalled call on the mixer thread.
      - One acoustic path is computed per machine, at its acoustic centre, and both outlets use it. A
        wall between you and one end of a bus is not modelled yet.
      - `ChooseFrontVoices` has no test: nothing constructs a `ClientAudioSystem` in the suite.
        `Localisation` and the tap summing are tested; the bookkeeping around them is not.

- [x] **2. Extent on every emitter, and audibility ranking.** DONE (session 10). `Priority` deleted;
      voices rank on `Loudness.RenderedGain x (1 - occlusion)`, which is the mixer's own law, with a
      2 dB hysteresis for a voice already playing and a short pinned list (`Essential`: your own
      footsteps and landing, speech when it exists). Continuous sources FADE out of the budget
      (`IVoiceSink.FadeOut`/`CancelFade`, an 80 ms slew on the channel gain in the provider's own
      volume pass); one-shots drop. `VoiceManager.MaxVoices` is now asked of the mixer every frame —
      what it holds plus the free HRTF voices — so the budget is the real resource rather than 256.
      And `MathF.Max(reference, 3f)` is replaced by the machine's own extent through `Loudness.Widen`,
      which cost every quiet vehicle the 8 dB it had not earned.

      Still open from it:
      - The acceptance that needs a live client: `without HRTF` should now stay at zero through a full
        crowd reaction. Nothing headless can check it.
      - `AudioEngineFacade` sets the budget and has no test — it needs FMOD to construct.
      - The lab's older spikes (`VehicleSpike`, `StreetSceneSpike`) still carry their own
        `MathF.Max(reference, 3f)`, so they are now 8 dB louder than the game for quiet machines.

- [ ] **3. Aggregation.** Many like sources collapse into one extended source past the distance at
      which nobody can tell them apart — the crowd is the worked example and it is already built
      (`ExtentMetres`, `Place(level, extent)`, ten log of the count). This is what makes a city fit in
      96 HRTF slots. Acceptance: a street of thirty cars costs a handful of voices at 200 m and thirty
      at five, measured.
- [ ] **4. The city map.** Materials first (brick, foliage, water — foliage is absorption 0.5-0.9 AND
      scattering 0.9, which is why a tree-lined street is not a canyon), then geometry, zones, houses
      with doors, then traffic AI (`VehicleSystem` follows a racing line and knows nothing about
      junctions), then birds (`RepeatIntervalSeconds` already does them). NPC speech waits on
      recordings — see `docs/SOUND_INVENTORY.md`.
- [ ] **5. Aircraft**, which by then cost a rotor model and a jet-mixing model rather than a subsystem.
      Jet mixing noise goes as the EIGHTH power of exhaust velocity and is broadband and low; the whine
      is beamed forward and stripped by air over a few kilometres, so "a faint roar with no whine"
      falls out. A helicopter's rotor is 10-25 Hz with blade-vortex thump, which air barely touches and
      which diffracts round buildings — heard blocks away, for free, from the mechanisms already here.

### The one thing needing a measurement before it can be fixed

- [ ] **"Vehicles stop close in front of me while going past."** Mechanism identified, not confirmed.
      Ask for two lines from a live client:
      `Mixer load: ... N without HRTF (M new since last)` and
      `Cars: N on the map — X synthesized, Y borrowed, Z out of budget`.
      `without HRTF` in the tens means voice pressure (raise `SaPoolSize`, or cut further); zero with a
      non-zero `out of budget` means the adaptive engine budget is shedding cars under mixer load,
      which is a different fix.

### Found while listening, session 10: the utility diesels may be 10-15 dB under life

Reported by ear — "the truck and the bus are quiet at a distance at idle, and the muscle car too until
it revs". Measured with `--machine-levels`, declared levels are at FULL LOAD and at 1 m:

| machine | idle | cruise 50 | full load | a real one, full load |
|---|---|---|---|---|
| 13 L semi | 72 | 83 | 93 | ~102 (drive-by 84 dBA at 7.5 m) |
| school bus | 71 | 79 | 85 | ~95-100 |
| big-block muscle car | 103 | 107 | 122 | ~100-110 |
| NASCAR | 119 | 126 | 130 | ~130 |

If those reference figures hold, the spread is wrong at both ends: the muscle car sits 37 dB over the
bus where life says about 10. Every one of those numbers is MEASURED from the synthesis and held by
`EngineSynthTests`, so this is not a gain to nudge — it is a question about how much power the model
radiates for a big slow diesel against a fast petrol V8, and the answer belongs in the mechanism
(radiating area, stack diameter, how block noise is scaled against the gas path). Worth doing before
any decision about the mix's anchor, because it would move the anchor.

### Reported from the track, session 10 — reverb outdoors, and a synthesis that failed

- [ ] **A gunshot on the open track is echoey and footsteps sound boxy.** Reported after crossing onto
      the speedway. Two candidate causes, and they are fixed in completely different places, so the
      next run must say which:
      1. **The bus is OPEN.** Outdoors the ray-traced RT60 sets the decay AND opens the wet in
         proportion to it (`ApplySimulatedReverb`) — capped at `OutdoorMaxDecayMs` 1100 ms and
         `OutdoorMaxWetDb` -16. A ray tracer working from box colliders cannot see the sky, so
         grandstands down one side of a track can give it a canyon's RT60.
      2. **The SEND is distance-independent.** `ReverbSendMix` is a constant 0.35 and the send is
         taken POST-FADER, so it carries the same distance attenuation the direct sound does — which
         makes the direct-to-reverberant ratio CONSTANT at every distance. Your own footstep at one
         metre gets a reverberant return only 9 dB below its direct sound, where in a real space at
         one metre it would be 30-40 dB below. In a real room the reverberant field is roughly uniform
         and the direct falls as 1/r, so D/R should fall as (r_c/r)² — nothing here computes a
         critical distance at all.
      The new `Room:` log line reports the listener's region, whether it has a Sabine estimate, the
      bus decay and wet, the ray-traced RT60 and the outdoor bus level. A long decay at an open wet is
      cause 1; a shut bus with the complaint still audible is cause 2. **Do not change the reverb
      model before that line is read** — the rooms were settled by ear in session 9.

- [ ] **The PA announcement never decodes.** `Audio asset 'ANNOUNCE/st_louis_welcome' still not
      decoded after 3000 ms; entity -5124 stayed silent`, repeatedly, for three different entities.
      Either the asset is missing from the bank or the decode never completes. Separately,
      `synth:applause:...` keys are still timing out at 3000 ms — the pre-warm item below is not just
      the FIRST reaction of each kind, it is every new parameter combination a crowd produces.

- [ ] **A voice was placed at a position 1912 ms old.** Voice -5125 (a reflection band id). Two orders
      of magnitude past the 23 ms the engine voices manage, so something is submitting a reflection
      and then not updating it while it keeps playing.

- [ ] **Footstep synthesis, calibrated against a recording.** Worst band is now **9.9 dB** (125-250 Hz,
      the band a footstep keeps its body in) against a measured reference, down from 32 dB. Six of
      nine bands are within 3 dB. `FootstepTests.TheModelMatchesTheShapeOfARealFootstep` ratchets it
      at 11 dB so it cannot get worse; tighten as it closes. NOT played to Cody as finished — the
      agreement is that he listens once it passes, and a 10 dB hole in the body band is not passing.

      What went in, all found by measuring rather than by listening:
      - **Radiation efficiency.** The model computed the force correctly and then radiated it as
        though a shoe sole were a perfect loudspeaker. It is not: (ka)², the same term
        `VehicleBody.RadiationFactor` uses. The radiator is the SOLE (9 cm), not the Hertzian contact
        patch (7 mm) — getting those confused was worth fifty decibels on the first try.
      - **Two paths out.** The sole pays (ka)²; the GROUND does not, because a point load on a stiff
        plate spreads over a bending wavelength — metres of concrete at 100 Hz — so its ka is of
        order one where the sole's is a hundredth. That is why you hear someone walking upstairs and
        not the grit under their shoes.
      - **A third contact scale.** Whole heel (35 mm, 43 Hz) and surface grit (0.45 mm, 3.4 kHz) had
        nothing between them; the sole's own lumps — a tread block, 8 mm — land at ~200 Hz, which is
        exactly where the hole was.
      - **The bulk contact is the ENVELOPE of the tread contacts, not a source beside them.** Treating
        it as both double-counted the force and put it at the wrong frequency.
      - Stones click rather than ring (sine resonances per stone were the "broken glass"); the
        contact band is half a decade wide, not a full one; the scuff conserves energy so a longer
        contact is quieter; loose pieces scale with mass so grass blades are not pebbles.

      Fitted rather than derived, and named so they stay visible: `GroundCoupling` (0.16),
      `ThumpShare` (0.12), `TreadShare` (3.0), `ShoeRingShare` (6.0). Same status as
      `VehicleBody.Coupling`. Re-fit, do not nudge.

      **Open, and the next thing to do:**
      - `SoftGroundIsQuieterThanHardGround` is SKIPPED and failing: concrete 76 dB, grass 79, carpet
        80 — soft ground comes out louder, which is the opposite of true. It passed before this
        rebuild. Needs a per-mechanism level breakdown for two surfaces side by side; that diagnostic
        does not exist yet and guessing at it burned an hour.
      - The shoe's own panel note is computed (trainer 137 Hz, dress shoe 245) but contributes
        nothing audible even at 7x weight, because both radiating paths attenuate ~137 Hz by 25 dB.
      - Only concrete has a reference. Wood, grass and gravel are unmeasured and their numbers are
        the model's opinion.
      - Still not in the game path: the client plays sampled footsteps.

      Tools: `--footsteps table` (the numbers), `--footsteps compare=<dir>` (against real steps),
      `tools/split_footsteps.py` (makes the reference out of a recording).

