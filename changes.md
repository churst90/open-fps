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

### 13. Every Degradation Is Loud (Engineering Audit — Step 3 of 7)
Five places where the engine could not do its job and answered as if it had. None of them threw; none of
them logged; every one of them was invisible from inside a game played entirely by ear.

- **A failed Steam Audio tick reported "nothing is in the way".** When `RunSteamAudio` returned null — a
  scene that had not built, a simulator exception, an exhausted source pool — `AsyncAcousticWorker`
  substituted `DirectResult.Clear` for *every* source. That is not a fallback; it is the single worst answer
  available: every wall in the level silently disappears and the player is told, by ear, that a sound behind
  concrete is in the open. Each un-simulated source now falls back to the hand-rolled ray-tracer
  (`HandRolledPath`), which is a worse model but still a *model* — it occludes, it finds portals, it
  attenuates. If even that throws, the source keeps its previous result rather than being reset to clear, and
  the failure is logged once per entity. `IsDegraded` exposes the state, and the transition into and out of
  degradation is logged (rate-limited), so a session that quietly stopped being geometry-simulated says so.
- **A source that lost the pool race lost its listener too.** `RunSteamAudio` only recorded the listener
  position from requests that successfully acquired an `IPLSource`, so an exhausted pool made the *whole tick*
  return null instead of just the sources it could not fit. The listener is now taken from the first pending
  request.
- **Reflections were gated on a mode flag, not on the data.** `ClientAudioSystem` skipped discrete reflection
  emitters whenever `SteamAudioActive` was true — so a source that fell back mid-session lost its reflections
  as well as its simulation. The simulator never emits a reflection path, so a reflection entry can only have
  come from the hand-rolled tracer; it is now honoured unconditionally.
- **AVX2 was assumed, never checked.** Every `iplContextCreate` call site passed `IPL_SIMDLEVEL_AVX2`. Steam
  Audio does not probe the CPU — it emits code for whatever level you hand it, so on a machine without AVX2
  that is an illegal instruction inside `libphonon`, not an error code. `Phonon.DetectSimdLevel()` now queries
  `System.Runtime.Intrinsics` (AVX-512 → AVX2 → AVX → SSE4.2 → SSE2/NEON) and `Phonon.DefaultContextSettings()`
  replaces the hand-built struct at all fourteen call sites. The chosen level is named in the enable log line.
- **phonon was missing from the required-native check.** `ClientRunner` required `fmod.dll` and
  `fmodstudio.dll` but not `phonon.dll` — so a client with no Steam Audio started, made noise, and ran with
  the HRTF binaural the entire game exists for quietly switched off. That is the dangerous failure, because it
  *looks* like it works. `NativeAudioLibraries` (new, in `Client.Core/Platform`) is the one shared,
  platform-aware list of expected libraries with the cost of each; phonon is `Required`. The Windows head
  refuses to start and both speaks and shows exactly which libraries are missing and what each one costs; the
  GTK head, which deliberately still runs without audio, now speaks the same specific report instead of
  "FMOD audio library not found".
- **Connection and protocol failures were empty method bodies.** `ClientNetworkService.OnNetworkError` was
  `{ }`, `OnPeerDisconnected` silently nulled the peer, a failed deserialize was `catch (Exception) { }`, and
  `Send` with no peer dropped the message without a word. For a blind player there is no greyed-out button or
  spinner to look at: the game simply stopped responding. Every one of those paths now logs *and* raises a
  finished, speakable sentence — `OnConnectionFailed` (with LiteNetLib's `DisconnectReason` translated into
  something worth hearing: "The server did not answer", "The server is running an incompatible protocol
  version") and `OnProtocolError` (rate-limited, so a packet-rate fault stays loud without flooding). Both
  heads speak them. A socket that fails to open is reported too — the Windows head now subscribes *before*
  `Start()`, which is where that failure is raised.
- **The first play of every NONBLOCKING sound was dropped.** `FmodResourceManager.TryGetSound` created sounds
  with `MODE.NONBLOCKING` and returned `true` immediately, while the decode was still in flight; `playSound`
  then answered `ERR_NOTREADY` and the caller returned, losing the play. It came back on the *next* trigger,
  so every un-preloaded sound in the game was silently swallowed once — and the GTK head, which never calls
  `PreloadAll`, swallowed the first play of everything. `TryGetSound` now returns a tri-state
  (`Ready` / `Loading` / `Missing`): a still-decoding sound is parked in a deferred-play queue and retried
  from `Update()` until it is ready or a 3 s deadline passes, and a genuinely missing asset is logged once by
  name with the reason. A late sound instead of a lost one.
- **Tests (86 → 97):** `DegradationTests` drives the real shipped map through `AsyncAcousticWorker` and
  asserts a source behind a wall never comes back with zero occlusion, that a clear-line-of-sight source is
  not blanket-occluded, and that every requested source is answered; that the detected SIMD level never
  exceeds what the running CPU actually supports and that `DefaultContextSettings` carries it; that phonon is
  present in the required-native list as `Required` with an HRTF cost, that the expected names match the
  platform, and that `DescribeMissing` names every library and what it costs; and that a connect to a dead
  port produces a spoken, finished sentence rather than silence. 97/97 pass.
### 14. The Server's Structural Holes, Closed (Engineering Audit — Step 4 of 7)

The theme of these was state that only ever grew. The wire protocol could add an entity to a client and had
no way to remove one. The loop banked unbounded elapsed time. Registration always answered "Success". Text
commands mutated the ECS world from whichever thread happened to call them — and the guard preventing that
race worked by throwing every MUD command away, silently. `/spawn` reported success for an object nothing
could see, hear or walk into.

- **The client never learned an entity was gone.** The message union had no despawn message, so
  `ClientWorldState`'s four dictionaries were append-only and cleared only on map load. A disconnected
  player left a corpse on every other client: still occupying space in the collision grid, still answering
  proximity scans, still playing whatever emitter it carried. `EntityRemoved` (union tag 26, a list of ids)
  is now sent **reliably** on destroy and on area-of-interest exit; `ClientWorldState.RemoveEntities` purges
  definitions, transforms, velocities and audio ids and rebuilds the static grid, and both heads then call
  `ClientAudioSystem.ForgetEntity`, which stops the entity's voice *and* the reflection voices derived from
  its id (`-10000-id`, `-5000-id`, and the `-30000-(id*100)` band) and drops its cached acoustic result.
- **`UserSession.KnownEntities` existed and was never used.** It now decides when a definition is sent: the
  map stream records what it streamed, and the broadcast sends a definition the first time an entity enters
  a client's earshot. That closes a second hole in the same place — remote players were only ever sent
  *state*, never a definition, so they never appeared in the client's snapshot at all. Removal is tracked
  separately in `VisibleDynamicEntities`: only dynamic entities are evicted on AoI exit, because the client
  builds its acoustic map from the whole streamed map and dropping a distant wall would change how the
  world sounds.
- **A moved wall could be lost forever.** `ServerStateUpdate` went out `Unreliable` and `Transform.IsDirty`
  was cleared for the whole map after one pass, so a dropped packet permanently lost that move for that
  client. Dynamic entities stay unreliable (the next tick corrects them); a **static** entity that moved now
  goes on the reliable channel, where delivery is itself the acknowledgement.
- **No accumulator clamp.** A GC pause or a debugger break banked arbitrary elapsed time and the server then
  ran hundreds of catch-up ticks back to back with no network poll between them. `RunLoop` clamps at
  `PhysicsConstants.MaxCatchUpSeconds` — one shared 0.2 s that both client heads now reference too — and
  logs how many ticks it dropped rather than fast-forwarding the world.
- **No graceful shutdown.** `_isRunning` was never set false, there was no `Console.CancelKeyPress` handler
  and `NetworkService.Stop()` was never called: Ctrl-C tore down sockets, ECS worlds and SQLite mid-tick.
  Ctrl-C and SIGTERM now both cancel the signal and ask the loop to stop; teardown runs on the loop thread
  after the last tick, in order — notify the players, stop the MUD gateway, disconnect and close the socket,
  destroy the map worlds.
- **Registration always reported success.** `HandleRegister` replied `Success = true` even when `AddUser`
  rejected a duplicate, so the player was told the account existed and then could not log in with it.
  `IUserRepository.AddUser` returns a bool, both implementations honour it, and the reply now says "That
  username is already taken." Usernames also fold with `ToLowerInvariant` rather than the current culture,
  which under a Turkish locale mapped the same name to two different keys.
- **No rate limiting on the UDP path at all.** Login and registration each verify a bcrypt hash inline on
  the tick thread, so unthrottled they are both a credential oracle and a way to occupy the simulation. A
  token bucket (`RateLimiter`) keyed by **remote address** — not connection id, which is free to cycle —
  allows six attempts in a burst and one every five seconds thereafter. Both transports share it, because
  login itself is now one method instead of two.
- **MUD commands were silently dropped, and the null guard was load-bearing.** Every gameplay handler began
  with `_network.GetPeer(id)`, which returns null for MUD ids (10000+), so `scan`, `move`, `spawn` and chat
  did nothing for telnet clients — and that guard was also the only thing preventing `MudGateway`'s TCP task
  thread from mutating the Arch world underneath the simulation. Both are fixed together: `CommandHandler`
  no longer knows what a `NetPeer` is (it answers through a reply callback) and enqueues every command body
  onto `_commandBuffer`, so all of them run on the tick thread whatever the transport. `GameServer.SendToSession`
  routes by whichever transport a session actually has, so chat reaches MUD players and `say` sends it from
  them; `ready` spawns a telnet player a real body; a command from a session with no body says so instead of
  dereferencing `Entity.Null`; and a MUD disconnect now ends the session and removes that body.
- **`/spawn` created objects nobody could see, hear or touch.** `HandleSpawn` called `world.Create` and
  returned: the entity never entered the map's `lookup` or the `SpatialGrid`, and both broadcast and
  collision iterate the grid. `MapManager.SpawnEntity` / `IndexEntity` / `DestroyEntity` are now the single
  path — register, index, flag for definition broadcast — and the player spawn uses it too. `HandleSetSound`
  had the mirror bug (`world.Add` throws on an entity that already has the component) and now uses `SetOrAdd`
  like its siblings.
- **Also fixed in passing:** `HandleLogin` logged and rethrew, abandoning the manifest send with the session
  already registered; the manifest called `MapRepository.LoadAll()` — re-reading and re-parsing every map
  file on disk — per login, and now reads the already-loaded `MapData`; "default" is no longer hardcoded in
  the login and ready paths (both use `session.CurrentMapId`); the dirty-audio scan was linear per entity
  per player and is now a set; and `world.Get<HealthComponent>` in the broadcast is guarded by a `Has` check.
- **Tests (97 → 116):** `ServerHolesTests` covers the `EntityRemoved` union round-trip and the client-side
  purge (definitions, audio ids and collision grid); the AoI departure diff; the accumulator clamp; the rate
  limiter's burst, refill and per-key isolation; duplicate and Turkish-locale registration against a real
  SQLite database; `SpawnEntity` landing in the lookup, the grid and the dirty flag, and `DestroyEntity`
  removing it from all three; and that a command defers to the tick thread, that a MUD-range connection id
  is executed rather than dropped, that a player with no body gets told to send `ready`, and that elevated
  commands are still refused. 116/116 pass. Verified live as well: a telnet session logs in, enters the
  world, scans, spawns a metal box and sees it in the next scan; the seventh rapid login attempt is refused
  and recovers after the bucket refills; and SIGINT produces an ordered "Server stopped cleanly."

### 15. One Prefab Spec, Validated at Load (Engineering Audit — Step 5 of 7)

Three documents described the prefab format and the loader had read none of them. `prefab-schema.json`
documented nested `Collider` / `SoundEmitter` / `Acoustics` objects and an integer `Type`;
`GEMINI_MAP_STANDARD.md` documented per-entity components; `PrefabTemplate` — the class actually
deserialized — is flat, uses enum *names*, and carries fields neither document mentioned. An author
following either produced a file that deserialized into all-defaults and spawned an invisible, silent,
materialless cube, with no error at all: `System.Text.Json` drops a key it does not recognise without a
word, which is the same failure as the rest of this audit — the engine answering as though it had done
what it was asked.

- **`PrefabTemplate` is the format.** Moved into its own file with every field documented against the
  component it produces, and given the four things the components had and the format could not describe:
  collider `Shape` (the loader hard-coded `Box`), `EmitterDirection`, `StartSoundId` / `StopSoundId`, and
  `RoomMaterials`. The last three existed on `SoundEmitterComponent` and `RegionComponent`, crossed the
  wire in `EntityDefinition`, and were consumed by `VoiceManager` and the Sabine reverb — no prefab could
  set them, and `ClientAudioSystem` never handed the start/stop ids to the voice or read the emitter's own
  direction. `Description` was in the format and thrown away at spawn; it now reaches `IdentityComponent`.
- **Room materials are named, not numbered.** `RegionComponent.Materials` stores resonance indices, so a
  map said `"Materials": [21, 18, 18, 18, 18, 18]` — unreadable and unverifiable. `RoomMaterials` takes six
  material NAMES in the order the reverb math reads them (Floor, Ceiling, North, South, East, West, which
  is deliberately **not** the `FaceMask` bit order), on the prefab and on the map entity alike; the shipped
  map now uses them. `AcousticRegistry` gained `IsKnown` / `TryGetResonanceIndex` / `KnownMaterials` and an
  idempotent `EnsureInitialized`, because the server had never initialized the registry at all.
- **`PrefabValidator` rejects what the engine cannot honour**, and says which file and which rule. The rules
  are one rule: *a prefab must not be able to describe something the engine will then quietly ignore.* An
  unknown field (named, with the list of real ones); a duplicate `Id`, or a missing `Id` or `Name`; an
  unknown material; a collider with a non-positive extent, `IsSolid` with nothing to be solid, `Shape` with
  no collider; emitter settings with `HasEmitter` false (a permanently mute object), an emitter with nothing
  to play, `Type: "Beacon"` with no emitter, an inside-out cone, `MinDistance >= Range` (inverted
  attenuation), a zero-length `EmitterDirection`; synth or granular parameters without their flag, or both
  flags at once; a region that is solid or has no `RoomSize`; a portal that is solid, or links a region to
  itself, or an entity that is both; and out-of-range numbers throughout. Warnings load anyway — a non-`Box`
  solid collider is one, because movement collides against the AABB whatever the shape and Steam Audio's
  scene is built from boxes only, so a solid sphere is walked around but never *heard*.
- **Rejection is visible where it bites.** Rejected ids are remembered, so a map entity naming one is told
  the prefab "was REJECTED at load with N problem(s)" instead of "not found", and the startup log ends with
  a single line saying how many prefabs loaded and how many did not. One shipped prefab failed its own spec:
  `building_box` was a 10×5×10 **solid** box that also declared itself an indoor region, so its region
  voxels sat inside geometry the listener can never stand in.
- **Maps are checked, not rejected.** An unknown field in a map file or on one of its entities is logged as
  an error naming the file, the entity and the field — but the map still loads, because dropping the entity
  would delete a wall. Room materials on a non-region entity, an array that is not six long, and a name the
  registry does not know are reported the same way. `EntityData.RegionId` was read by nothing and is gone.
- **The documents now describe the format.** `prefab-schema.json` is written against `PrefabTemplate` with
  `additionalProperties: false`, and `PrefabSpecTests.Schema_DescribesExactlyTheTemplateClass` fails if the
  two ever drift apart again. `GEMINI_MAP_STANDARD.md` is replaced by `docs/AUTHORING.md`, which covers
  prefabs and maps against the real loaders, with the shipped map as the worked example. The stale root
  `prefabs/` directory — a migration leftover the server never read, holding a second `concrete_wall` with
  different transmission values — is deleted, and the shipped prefab files no longer carry pages of
  explicit `null`s.

Tests 116 → 146 (`PrefabSpecTests`), including "every shipped prefab satisfies the spec" and "the shipped
map uses only fields the loader reads". Verified live: the server loads 18 prefabs with no complaint, links
4 portals and spawns the map; a map with `Occlusion_Flooor` and `Aperture` typed into it names both fields
and the entity that carries one. Docs updated: todo.md step 5, readme.md, docs/AUTHORING.md (new),
GEMINI_MAP_STANDARD.md (removed).

### 16. Profile, Then Cut the Hot Paths (Engineering Audit — Step 6 of 7)

The theme is repeated work: the same answer computed several times over, because nothing remembered it and
nothing said how often it was allowed to be asked for. Every item below was found by reading the frame, and
every one is now measurable rather than asserted.

- **A profiler that lives in the code.** `PerfProbe` — named timers and counters any thread can write to,
  and a report every 30 s — is off unless `OPENFPS_PROFILE=1`, at which point each call is a static bool
  test and a return. That is what makes it permanent instead of scaffolding put up and taken down around
  each investigation: the next time something is slow the numbers are one environment variable away.
  Wired into the server tick, both client loops, the audio update and the Steam Audio simulation.
- **One snapshot per version of the world, not per consumer.** A `WorldSnapshot` is a full copy of every
  definition and transform in the map, and a frame built between three and six of them — one for
  prediction, one for the shelter raycast, one for the proximity scan, one for the audio system, all
  describing the same instant. `ClientWorldState` now stamps every mutation with a version and hands back
  the copy built for it; a change produces a NEW copy rather than editing one already given out, which is
  what keeps it safe to share with the acoustic worker thread. Both heads advance remote interpolation —
  the tick's only mutation — *before* the readers rather than between two of them, so the whole frame reads
  one build. Map load no longer builds a snapshot per arriving entity to count entities (`EntityCount`).
- **The audio update is capped at 60 Hz.** It hung off a loop that spins as fast as it can poll the socket —
  a 5 ms sleep, so roughly 200 Hz — and drove all of it at that rate: listener sync, region resolution, six
  near-field raycasts, an acoustic request per active voice, the FMOD tick. Nothing there resolves faster
  than a frame. `UpdateThrottle` caps it inside `ClientAudioSystem` so both heads are capped by one rule,
  and re-arms from the moment it ran, so a stall does not then burn catch-up frames for audio nobody can
  still hear.
- **The server's ground probe is memoized.** It ran once per INPUT, not once per tick, and each run walks
  every grid cell within 50 m and tests five points against everything in them; standing still cost exactly
  as much as sprinting. `GroundProbeMemo` (on the session, so it is per-player and disappears with the
  disconnect) returns the last answer while the static geometry version, the probe position and the age all
  hold. The age bound is the honest part: dynamic colliders are not in the version count, so a remembered
  floor is allowed to be at most 150 ms stale — under a fifth of the interpolation delay the client already
  tolerates, and still enough to cut a standing player from thirty-plus probes a second to under seven.
- **Grid queries walk the cells once and return each item once.** `GetItemsInRadius` is an iterator, so
  asking it for a count and then walking it — which both the server's collision gather and the client's
  candidate query did — walked every cell twice. Worse, a wall wide enough to span cells is filed in each
  of them, so it came back four or nine times and was ray-tested four or nine times, per ray, per source,
  per frame. `SpatialGrid.CollectInRadius` fixes both once, for every caller, into caller-owned buffers so
  the grid stays safe to read from several threads.
- **Playing voices are indexed by entity id.** Every per-frame audio call — `IsPlaying`,
  `GetSoundPosition`, `SetAcousticPath`, and the lookup at the head of `PlaySpatialSound` and
  `UpdateSpatialAttributes` — was a linear scan of the active-voice list, run once per active voice, so the
  cost of a frame grew as the square of the number of things making noise. Silencing one removed entity was
  worse: `ForgetEntity` probes a hundred reflection ids, each a full scan.
- **The Steam Audio ray budget is measured.** Its cost is a configuration — rays × bounces × sources — not
  an emergent one, so `SteamAudioSimulator` times its own runs and reports what was asked for against what
  it cost (`4096 rays x 1 bounce(s), 12/64 sources: avg 2.1ms, max 7.4ms over 900 runs`). A run that costs
  more than the 16.6 ms audio frame it feeds says so loudly, throttled, because a worker that cannot keep
  up does not fail — it silently delivers older and older acoustics.
- **Smaller cuts on the same paths:** remote interpolation was quadratic in the entity count with a lambda
  closure allocated per entity per frame (now an indexed lookup); the near-field radar allocated its
  direction array every update; the pathing stage allocated a four-float array per occluded source per
  tick. And a real bug the snapshot work surfaced: `ClientAudioSystem` assigned `_lastSnapshot` *before*
  comparing against it, so the moving-region check compared the world with itself and never fired.

Tests 146 → 159 (`HotPathTests`). Two things are verified by inspection rather than by test and are called
out in the file: the FMOD voice index (no native library in CI) and the ray-budget report (needs a real
simulation run). Verified live: the server ran 75 s under `OPENFPS_PROFILE=1` and reported
`server.tick n=900 avg=0.015ms max=0.081ms` — 900 ticks in 30 s, exactly the 30 Hz the rate is set to.
Docs updated: todo.md step 6, readme.md.

### 17. Finish Weather, Converge the Heads, Delete the Dead Code (Engineering Audit — Step 7 of 7)

Two families of defect with one shape between them: something was written down and never read.

**Weather, end to end.** Every stage of the atmospheric chain had a break in it, and each break was
silent.

- **The scenario temperature reached nothing.** `WorldEnvironmentSystem` computed rain and storm cooling
  into `_targetTemp`, a field no other line of the file ever read; the temperature lerped to the bare
  seasonal/daily curve. It also *compounded* — `_targetTemp -= 2.0f` subtracted from a running total, so
  two rain fronts in a row would have cooled the world by four degrees and never given them back. The
  front's contribution is now an offset plus an optional ceiling, applied to the curve each tick: rain
  −2 °C, a storm −4 °C, and snow capped below freezing so it cannot fall into a summer afternoon.
- **Gustiness went nowhere, and snapped when it went.** It was assigned straight onto the state (a front
  took the air from calm to a gale between one tick and the next) and nothing on the client consumed it.
  It now fades like the wind it travels with, and the split is explicit: the server broadcasts the
  *sustained* wind plus a gustiness scalar once a second, and each client synthesizes the gust locally at
  audio rate from that scalar (`WindModel` in Common — a deterministic sum of three incommensurate sines,
  bounded, never reversing the wind, stilled by shelter). Sampling a two-second swell at 1 Hz would have
  aliased it into a stutter; this is the standard split, and it makes wind audible as weather rather than
  as a constant.
- **`AirAbsorptionMultiplier` was broadcast but never assigned.** `BroadcastEnvironment` built a
  `WorldStateUpdate` without it, so it arrived as `0` — and the client's `distance / Math.Max(0.1f, m)`
  guard turned an unset field into a TEN-FOLD increase in the absorption distance. Air absorption was
  therefore off for the entire game, and the map's authored value never applied. The guard now
  substitutes the *neutral* value at both ends, and the broadcast carries the field.
- **Air pressure was authored in the wrong unit.** The default everywhere — `MapData`, `ZoneComponent`,
  `MapManifest` — was `1.0`, while every consumer reads millibars (sea level 1013.25). Divided by 1013.25
  that clamped at the floor of the normalisation, so every map on the server described a near-vacuum. The
  defaults are millibars now, and `MapRepository.NormalizeAtmosphere` names the problem at load and
  substitutes sea level rather than absorbing it — the same rule step 5 set for prefabs.
- **The broadcast is per map.** The weather is global, but air pressure is altitude and the absorption
  multiplier is authored tuning; both belong to the map the player is standing on. `GetStateForMap`
  overlays them, and reads the map's authored temperature and humidity as *biases* from the baseline, so
  a map that authors the defaults behaves exactly as before while one authored ten degrees colder stays
  ten degrees colder than the season.
- **The map's air applies on arrival.** `ClientWorldState.ApplyManifestAtmosphere` runs from the manifest
  handler, so the first second in a new map is no longer heard through the previous map's air (the world
  state broadcast only arrives once a second). A default-constructed `WorldEnvironmentComponent` is now a
  still, temperate, sea-level day rather than a freezing vacuum.
- **Temperature reaches the mix.** `AudioPhysics.SpeedOfSoundAt` (c = 331.3 + 0.606·T) feeds the Doppler
  factor through a new `IAudioProvider.SetAirTemperature`. It is a ~4% swing across a playable range,
  which is small alone but moves every Doppler shift in the world together: a siren on a winter night is
  measurably flatter than the same siren in high summer.

**One session class, two heads.** The Windows `ClientSimulationSystem` and the GTK `GameSession` were two
implementations of one job, and they had already drifted — the Linux client had no chat buffers, no
proximity announcements, no voice key and no loading progress; the two spoke different sentences on spawn.
`ClientGameSession` in `OpenFPS.Client.Core` is now the whole of the client's game logic, and both heads
run it verbatim. What is genuinely platform-specific sits behind four interfaces and nothing else:

| Seam | Windows | Linux |
|---|---|---|
| `ISpeechOutput` | `NvdaSpeechOutput` (NVDA + SAPI fallback) | `SpeechDispatcherOutput` (Orca / espeak-ng) |
| `IClientShell` | `WinFormsClientShell` over `ClientNavigationService` | `GtkClientShell` (loading, console, quit dialogs) |
| `IMicrophoneCapture` | `VoiceCapture` (NAudio → Opus) | `NullMicrophoneCapture` — says so out loud |
| key map | `WinFormsKeyMap` (`Keys` → `GameKey`) | `GtkKeyMap` (GDK keyval → `GameKey`) |

`InputStateBuffer`, `InputCommandMapper` and `ChatManager` moved into Core and are typed on `GameKey` and
`ISpeechOutput`, so there is now one binding table rather than three key processors on one head and a
hand-rolled `if` ladder on the other. Both heads gained what the other had: Linux gets chat scrollback,
proximity announcements, a command console, a quit confirmation and loading progress; Windows gets the
look-ahead and inventory bindings in the same table as everything else. The GTK head's `GameSession`,
`GameInput`, and the Windows head's `ClientSimulationSystem`, `InputHandler`, `InputCommandMapper`,
`InputStateBuffer` and three input processors are all deleted. `EnableWindowsTargeting` is set on the
Windows csproj so that head can be **compile-checked from Linux** — a shared class only one of its two
heads can be built against is exactly how they drifted apart in the first place.

**Dead code.** The reverb *slot* machinery in `FmodAudioProvider` (`LeaseSlot`, `EvictSlot`,
`FindBestSlotToLease`, `UpdateSlotAcoustics`, `ConfigureReverbDsp` and five backing fields) was superseded
by direct per-bus volume management and had no callers; `SoundMappingService` lost
`PlayPhysicalInteraction`, `PlayUiSound`, `PlayReflection` and its backward-compat constructor, all of
which duplicated `ClientAudioSystem`'s emitter construction with drifted values and none of which were
called. The ten Steam Audio migration spikes moved out of the shipped client library into
`OpenFPS.AudioLab/Spikes/`, which is the only thing that runs them — the migration's remaining phases
still need `--sim-*`, so they are relocated rather than destroyed. All ten were re-run from their new home
and pass.

Tests 159 → 188 (`WeatherAndConvergenceTests`). The whole solution builds, the Windows head included.
Not verified: a live logged-in walkthrough on either head (needs a GUI session), and the weather is
audible-by-design but has not been ear-checked.

---

# Live-session findings, 2026-09-13

The first logged-in walkthrough on the GTK head since the audit. Three things were reported by ear, and
all three turned out to be the same kind of defect the audit was about: a signal that was connected to
nothing, and no way to tell from the outside.

## The reverb was a wash, not a room

A siren in the concrete room was heard as "the wash of the siren all around me, not representative of the
walls it is bouncing off of" — which is exactly what the DSP graph was doing.

FMOD's DSP chain runs **TAIL (input) → HEAD (output)**, and the region reverb bus was wired the other way
round. The reverb was added at the `HEAD`, the doorway HRTF stage at the `TAIL`, and every source's send
was connected with `bus.getDSP(HEAD)` — straight into the reverb. So the send arrived *downstream* of both
the bus fader and the binaural stage, and three things followed that nothing outside the mixer could see:

- **`bus.setVolume` applied to nothing.** The per-portal `(aperture / 2) / distance` gating in
  `UpdateReverbBuses` is implemented as the bus fader, which sat between the reverb and the send's
  injection point. A room's reverb therefore played at full level from anywhere on the map, and got no
  quieter as you walked away from its door. That, on its own, is most of "the wash is all around me".
- **The doorway localization localized silence.** The HRTF stage was at the input end, and the bus has no
  channel inputs — only sends. It ran on an empty buffer for the life of the bus. The `--reverb-route`
  spike measures it: on the old graph both ears read *exactly* zero.
- **Every send was also a dry copy.** SFXREVERB's `DryLevel` was left at 0 dB on what is an aux send bus,
  so each source was mixed in a second time with no position at all, on top of its own HRTF voice.

The bus is now `send → reverb → fader → doorway HRTF → out`: the reverb at the `TAIL`, the binaural stage
at the `HEAD`, the dry path muted, and the sends addressed to `_reverbDsps[regionId]` by identity rather
than to "whatever is at the head" — which is the part that would have drifted again. Re-routing a send
now `disconnectFrom`s the old one instead of muting it and leaving it attached; a source whose region
flipped back and forth was accumulating connections on a DSP that caps them. `ReverbSendMix` (0.35) and
`ReverbCrossSendScale` (0.25) are in `AcousticConstants` — one knob for "how wet is a room", raised from
the old 0.08 because the dry path that used to pad it is gone.

**`OpenFPS.AudioLab --reverb-route`** is the verification, and it needed to be a real one: the reverb tail
is generated by FMOD and the localization by Steam Audio's HRTF, so neither exists in a unit test. It
builds the shipped map's west room, plays a source inside it, and measures the bus's own HRTF stage from
four listener positions. It checks that the stage is engaged from outside and bypassed from inside, that
the two ears differ, that crossing to the other side of the doorway *swaps which ear leads*, and that the
measured level (not the bookkeeping value — that one was right all along) falls with distance. It fails on
the pre-fix graph and passes on this one.

Not verified: how it actually sounds. The routing is right; the level is a guess.

## Crossing a doorway read the portal prefab's authoring notes aloud

`CheckInteractableProximity` announced every named entity within three metres, and everything on a map is
named — the walls, the floors, the auto-injected foundation, the acoustic region volumes and the portals
all carry a `Name` so that authors and logs can refer to them. So stepping through a doorway announced
"Acoustic Portal. An opening between two acoustic regions. Place it in the gap between walls and set
RegionAId / RegionBId on the MAP entity…" — a paragraph written for an author, read to a player, mid-step.

`Announce` is now part of the prefab spec (`PrefabTemplate`, `prefab-schema.json`, `docs/AUTHORING.md`)
and travels to the client on `IdentityComponent`. Omitted, it follows `Type`: true for `Item`, `NPC` and
`Beacon`, false for `StaticObject`, `Trigger` and `Projectile`. An author can set it either way — false to
silence a named landmark, true for a staircase or a door worth narrating. The client just obeys it; it is
not the client's business to guess which of the server's entities are scenery.

## A rejected login was silent, and retrying it did nothing at all

Two independent faults, stacked, which is why the symptom was "it returns me to the main menu without any
spoken error" and then nothing on the retry.

**The rejection was spoken over.** Both heads closed their connect form the instant the button was
pressed. Focus fell back to the menu, the menu's focus handler spoke the button's name **with interrupt**,
and it landed on top of the "Login failed. Invalid Credentials" the session had said microseconds earlier.
The form now stays up until the server has answered: `ClientGameSession` raises `LoginSucceeded` /
`LoginFailed`, the head closes the form only on success, and on failure it writes the reason into a
focusable status line and returns focus to the username field — with a `_suppressFocusSpeech` flag so that
the focus move does not talk over the reason it exists to explain. Both heads got it; the Windows one is
compile-checked but unheard.

**The retry never happened.** `NetManager.Connect` returns the existing peer when one is already connected
to that endpoint, and fires no `OnPeerConnected`. A rejected login leaves the peer connected, so the second
attempt produced no event — so the head never sent a second `LoginRequest`, so the server never answered,
so there was nothing to speak. `ClientNetworkService.Connect` now checks for a live peer and re-runs the
connected handshake itself, and a repeat press while an attempt is still in flight says so
(`OnConnectionNotice`) instead of queueing a second login. The loopback test in
`AnnouncementAndLoginTests` drives a real LiteNetLib server for this, because the bug was in LiteNetLib's
contract rather than in ours.

**And the server said nothing either.** A failed `VerifyPassword` replied and returned without a log line,
which made "the client never asked" indistinguishable from "the client asked and said nothing". A rejected
login is now logged with the username and connection id (never the password).

Tests 188 → 205 (`AnnouncementAndLoginTests`), plus `OpenFPS.AudioLab --reverb-route`. The whole solution
builds, the Windows head included; the existing provider and Steam Audio spikes were re-run and pass.

---

# Second live session, 2026-09-13

Reverb confirmed by ear — "reverb comes from the actual source now". Three more findings, and the first
two were the same bug wearing two hats.

## Footsteps that never stopped, and a wall that clicked

Walking into a wall and holding forward kept producing footsteps indefinitely, and produced a stream of
pops and clicks with them. Both came out of four lines in `SharedMovementEngine.Step`:

```csharp
Vector3 nextPos = pos + remainingMove;      // where we are trying to go
... bestHit measured AT nextPos ...
pos += bestHit.Normal * (bestHit.Penetration + 0.001f);   // ...applied to where we STARTED
```

The penetration is measured at the position being moved *to*; it was applied to the position being moved
*from*, which was outside the wall to begin with. So pressing into a wall pushed the player **backwards**
by most of a step, every tick, and the next tick walked them back in. A test pinned it at **0.15 m of
movement per tick** — 4.5 m/s of path length going nowhere. Two consequences:

- `LocalPlayerController` accumulates stride from the *magnitude* of each frame's movement, so an
  oscillation banks distance at full walking speed in both directions. Footsteps forever.
- Wherever that 0.15 m oscillation straddled a boundary, the listener's acoustic region flipped back and
  forth at half the tick rate — and every flip re-routed the reverb sends and toggled a DSP.

The resolution now takes the move and comes back out of the surface it landed in
(`pos = nextPos + normal * (penetration + skin)`), which removes the normal component of the motion and
keeps the tangential — that *is* the slide. Step-climbing is gated on actually having motion to climb
with, so a depenetration pass cannot lift a merely-overlapping player into the air. `SharedMovementEngine`
is shared by client prediction and server authority, so both ends were wrong in exactly the same way and
both are fixed together.

## Crossing a threshold clicked

Even with the oscillation gone, a real crossing flipped the region bus's binaural stage from engaged to
bypassed between one mixer block and the next. That is a step change in the sample stream, and a step
change is a click. Steam Audio has the continuous form of the same switch — `spatialBlend` — so the
transition is a ramp now: 1 (arriving through the opening) to 0 (filling the room you are standing in)
over about 0.2 s, with the apparent doorway direction smoothed alongside it so a change of nearest portal
does not snap the reverb across the head either. Bypass is only touched at the bottom of the ramp, where
the stage contributes no direction at all and the switch changes decorrelation rather than level.

`--reverb-route` measures it: the largest single-update change in localization across a crossing is now
**0.08**, where a hard switch is 1.0.

## Walls you can hear before you touch them

There was already a "near-field proximity" effect. It was wrong in every particular:

- **The delay was seven times too short.** It used an FMOD echo of 0.1–1.2 ms. The physical round trip to
  a surface 1.5 m away is 2d/c = **8.7 ms**. At those delays the comb peaks sit above 1.4 kHz, which is
  metallic ringing, not the boxy colouration of a corridor.
- **It had 45% feedback.** A feedback delay is a resonator: it rings at a fixed pitch no matter what the
  geometry does. A single boundary reflection is *feedforward* — one tap.
- **It was one scalar.** "Distance to the nearest wall", from world-axis probes. A ceiling a metre up and
  a wall thirty centimetres to the left produced identical output, and turning your head changed nothing.
- **It ignored material.** Carpet and concrete reflected the same.

`BoundaryModel` + `BoundaryProximityProcessor` replace it. Six rays leave the listener's head in **head
space** — right, left, up, down, forward, back — so what comes back is "concrete, half a metre, on my
left". Each becomes one tap of a multi-tap feedforward comb on the master bus:

| | |
|---|---|
| delay | `2d/c`, with c from the world's air temperature — the same knob that moves every Doppler shift |
| gain | mid-band reflectivity of the material, times a linear distance window out to 3 m |
| timbre | a one-pole low-pass from the material's *high-band* absorption — carpet returns a dull thump, concrete returns the lot |
| position | constant-power pan plus a real interaural delay, so the near ear hears it first |

Everything glides per-sample — gains *and* delays, with fractional-delay interpolation — so a 60 Hz update
from the game thread can never put a step into the stream, and walking toward a wall sweeps the comb
continuously rather than stepping between whole samples. A corner or a stairwell can put a surface in
every probed direction at once, so the summed gain is trimmed to `MaxBoundaryReflectionSum` — scaling the
set together, because the *ratios* between the surfaces are the cue.

The tests measure the rendered **impulse response**, not the parameters the renderer was handed: at
0.4 m, 0.8 m and 1.5 m the first notch is where `c/4d` says it should be, a wall at 0.4 m and one at
1.2 m demonstrably do not produce the same notch, a carpeted wall reflects less and duller than concrete,
colder air moves the whole comb, nothing nearby leaves the signal bit-for-bit untouched, and a wall
appearing in one update fades in rather than stepping. That first assertion is the one the old
implementation could not have passed — its notch did not move with distance at all.

`OpenFPS.AudioLab --boundary` runs it through the real mixer and prints, for each distance, the round trip
and the frequency of the first notch. `--boundary-live` walks a wall from your right, to the front, to
your left, to be judged by ear.

Also here: a non-allocating `SpatialService.RaycastAll` overload, since the allocating one ran three
array allocations per call on every audio frame.

Tests 209 → 223. Not verified: how strong the boundary effect should be. The geometry is measured; the
level is a judgement, and `BoundaryModel.ReflectionGain` is the knob.

---

# Ambisonic ambience beds, 2026-09-13

The groundwork for ambience that stays where you left it.

## Why not just play a binaural recording

Because a binaural recording is **head-locked**. Its spatial image is baked to the orientation of the
head that recorded it, so the bird on your left is on your left after you turn around. For a sighted
player that is a mild wrongness; for a player navigating by ear it is a false spatial reference that
never moves, which is worse than plain stereo — plain stereo at least does not claim to be anywhere.

A first-order ambisonic recording instead describes sound arriving from every direction as a *field*.
Rotate the field by the listener's orientation, then decode it binaurally, and the world stays put while
the player turns in it. Steam Audio does exactly this, and `libphonon.so` already shipped in the build —
but `Phonon.cs` bound only the binaural effect. Nothing else.

## What was built

**`PhononAmbisonics.cs`** binds `iplAmbisonicsDecodeEffect*` and `iplAmbisonicsEncodeEffect*` against the
real header. The decode effect does the rotation *and* the binaural render in one call — the separate
rotation effect is not needed. `IPLCoordinateSpace3` already existed in `PhononSim.cs` with matching
field order, so it is reused rather than redeclared. `ListenerFrame()` converts a game rotation (+Z
forward) into Steam Audio's frame (−Z forward); per Valve's documentation, you hand the decoder the
listener's own axes as world-space vectors and it works out the rotation.

**`AmbisonicFormat`** converts what you download into what the decoder wants. This is the part with no
failure mode you can hear as a failure:

| Layout | What it is | What happens if you skip the conversion |
|---|---|---|
| AmbiX (ACN/SN3D) | what nearly every modern recording and every ambisonic mic produces | directional channels arrive 4.8 dB down; the field renders over-wide and badly localized |
| N3D | Steam Audio's native format | nothing — it is already right |
| FuMa | what "B-format" meant before AmbiX; common in older free recordings | the axes are permuted outright: front becomes up |

Neither mismatch returns an error. `GuessLayout` reads the filename as a hint — and its test caught a
real bug immediately, because **"sn3d" contains "n3d"**, so checking for N3D first read every
SN3D-labelled file (which is to say most AmbiX files) as N3D and skipped the conversion it needed.

**`AmbisonicBedDsp`** is an FMOD **generator** DSP — no input buffers, owning its own PCM. That is
deliberate: FMOD downmixes a four-channel sound to the output speaker mode long before any DSP sees it,
which would destroy the soundfield on the way past. Owning the PCM sidesteps the channel-format question
entirely and makes looping exact. Per block it reads (order+1)² channels, hands them to the decode effect
with the listener's current frame, and takes back a binaural pair. Sample-rate conversion is interpolated,
so a 48 kHz bed plays correctly through the 44.1 kHz mixer instead of running fast; levels glide, so
starting, stopping and cross-fading a bed never steps the signal.

**The provider** gains `PlayAmbientBed` / `SetAmbientBedVolume` / `StopAmbientBed`, keyed by sound id
with several beds live at once — which is a cross-fade as soon as something wants one. A file that is not
a full-sphere ambisonic layout (4, 9 or 16 channels) is refused with the reason, rather than played as if
it were: the alternative is a soundfield pointing in an arbitrary direction with nothing to say so. The
N3D conversion happens once at load, on that bed's own copy, never per block.

## How it is verified

`OpenFPS.AudioLab --ambisonic` generates the test field with Steam Audio's **own encoder**, so it tests
the round trip rather than testing a guess at the library's spherical-harmonic convention. Every way of
getting a P/Invoke layer wrong here is silent — a struct field out of order, a handedness left
unconverted, a rotation applied backwards — so the assertions are about *direction*, not about whether
sound came out:

```
  source left,  facing forward   L/R = 0.37265 / 0.28424   balance = LEFT
  source right, facing forward   L/R = 0.28435 / 0.37496   balance = right
  source left,  facing backward  L/R = 0.28435 / 0.37496   balance = right
  source left,  facing it        L/R = 0.26846 / 0.25619   balance = LEFT
```

The third line is the one that earns its keep: turning the listener 180° produced **exactly** the numbers
of a source on the right, which is the rotation being applied correctly rather than at half angle or
mirrored — and it is the assertion a head-locked binaural recording would fail while sounding fine.

`AmbisonicFormatTests` covers the channel maths and the three layout conversions offline, including that
AmbiX and FuMa provably do not produce the same thing (if they agreed, the layout field would be
decoration).

## One thing fixed along the way

`HotPathTests.TheProfilerCostsNothingUntilItIsTurnedOn` asserts `PerfProbe` holds nothing until switched
on — but `PerfProbe` is global static state written to by `ClientWorldState`, `ClientAudioSystem` and
`PhysicsUtils`, and xUnit runs test classes in parallel. It was racing any class that stepped a client
session: passing alone, passing most of the time, failing at random in a full run. Adding test classes
made it likely enough to actually fire. The assembly is serialized now
(`DisableTestParallelization`), which costs ~2 s across 244 tests and removes the whole class of failure
rather than the one instance that surfaced.

Tests 223 → 244. Every spike re-run and passing. Not verified: a real recording — there is no ambisonic
asset in the repo yet, so the decode path has been proven against a synthesised field only.

---

# Asset ingest, and a decoder that never decoded, 2026-09-13

## `tools/ingest_audio.py`

Nothing arrives usable. The ambisonic beds came at 96 kHz and up to seven minutes long — 647 MB of RAM
once the bed DSP loads one as float. The footstep "samples" were ninety-second studio takes with **172
steps in them**, not one-shots. The stereo ambiences were 128 kbps MP3 at arbitrary levels. So this is
the step between "downloaded" and "committed": read `inbox/`, write the ASSETS tree, record where every
file came from.

**Ambisonic beds** go through **sox, not ffmpeg, deliberately.** ffmpeg gives a 4-channel WAV the "quad"
speaker layout and is entitled to reorder or downmix it during a conversion; to sox the channels are
opaque and numbered, which is what W/Y/Z/X require. Resampled to 44.1 kHz (the mixer rate — 96 kHz is
resampled on playback anyway and costs only memory), trimmed to 90 s, normalized with a single
whole-file gain because per-channel normalization would rescale W against the directional channels and
tilt the entire soundfield. The output's channel count is re-probed afterwards and the file deleted if
it changed. 69–485 MB became 31–46 MB.

**Footsteps** are split on the silence between hits by sox's `silence ... : newfile : restart`, which is
C-speed and completely reliable here because the takes have a −94 dBFS floor between steps. Slices
outside 0.06–1.5 s are discarded (a short one is a click, a long one is two steps and a pause), as are
any under 2% peak. Then mono, 3 ms fades, peak-normalized to −3 dBFS. **1,807 usable slices** came out
of 47 labelled takes.

Footwear becomes the **variant**, not another sample in the pool: `SoundMappingService` picks at random
within `<Material><Variant>`, so putting barefoot and high heels in one folder would have the player
changing shoes between steps. Shoes/sneakers are variant 0, barefoot 1, heels 2, flip-flops 3.
Filenames containing "land" go to `LANDING/` rather than `FOOTSTEPS/`.

**The 146 unlabelled takes are copied to `_unsorted/` and not guessed at.** A wrong guess here puts
gravel under a player walking on carpet and nothing ever says so.

Nothing is destructive — the inbox is only read, existing outputs are left alone without `--force`, and
`--dry-run` reports without writing. The manifest preserves licence fields a human has already filled in.

## GranularBank has never worked

The bed played silence. Callbacks were running, the block size matched, the position was advancing, the
file reported the right channel count and sample rate — and the input RMS was **exactly zero**.

`GranularBank.TryGetPcmData` opened sounds with `MODE.CREATESAMPLE | MODE.OPENONLY`. `OPENONLY` is the
opposite of what was wanted: it tells FMOD to open the file and parse its header but **not to read or
decode any sample data**, leaving the caller to pull it with `readData`. The code then called `lock`,
which for a sound like that returns a buffer of exactly the right size containing nothing at all.

So every load logged a confident success with the correct channel count, sample rate and length, and
handed back silence. Nothing caught it because the only consumer was the granular engine — which has
never had a caller. Dropping `OPENONLY` fixes it, and fixes granular synthesis before its first use.

## `OpenFPS.AudioLab --bed`

The `--ambisonic` spike proves the Phonon calls against a synthesised field. This proves everything
above them, which is where the bug actually was: that a four-channel file survives being loaded as PCM,
that the N3D conversion reaches it, that the generator DSP streams and resamples it, and that the field
turns when the listener does. It sweeps a full circle and checks the ear balance actually moves;
`--bed-live` turns slowly and audibly. On `farm_ambiance` the balance swings 0.081 across a turn — modest
because a farm ambience is largely diffuse, which is the physically correct answer.

Tests still 244. Every spike re-run and passing.

---

# One synthesized weapon, and the crack that tells you where it came from

## Why synthesis is the right answer here, not a fallback

Every gunshot recording is a few milliseconds of muzzle blast followed by a second of the field it was
recorded in — you cannot fire a rifle in an anechoic chamber. This engine now generates its own field:
region reverb, boundary reflections, occlusion, Steam Audio. Feed it a recording with a tail and you hear
two rooms at once, and the wrong one wins.

What comes out of `WeaponSynth` has no room in it at all, so the room it ends up in is the one the player
is standing in. Mono, dry, and peak-normalized, because the engine owns distance.

## `Ballistics` — the part worth having

A supersonic round makes **two** sounds, and the relationship between them is worth more than either:

- The **muzzle blast** leaves the weapon at the speed of sound and arrives at `d/c`.
- The bullet outruns it, dragging a shock cone behind it. The **crack** you hear is made at the moment
  the round passes *you*, and arrives at roughly `d/v`.

Since `v > c` the crack lands **first**, and the gap is `d·(1/c − 1/v)` — about 1.7 ms per metre for a
rifle. The spike prints it:

```
  range    crack at   report at      gap   (gap read back as range)
     25 m      32.8 ms     72.9 ms    44.5 ms     25.0 m
    100 m     118.0 ms    291.5 ms   177.9 ms    100.0 m
    400 m     458.9 ms   1166.2 ms   711.6 ms    400.0 m
```

That last column is the test: run the gap back through the physics and it recovers the range exactly. A
player learning to read that gap is doing real arithmetic on real numbers, not responding to a designed
cue — so it stays true at any range, for any muzzle velocity, and it widens in cold air along with every
Doppler shift in the world. Sighted shooters throw this information away.

A subsonic round never outruns its own report, so it makes no crack at all. That is a gameplay fact
about suppressed weapons, not an omission.

## The layers

`MuzzleBlast` is a transient (what tells you a gun went off rather than a door slamming), a resonant
body (what tells you *which* gun), and a low thump that falls slightly in pitch as it decays (what stops
it sounding like a test tone). `SupersonicCrack` is an N-wave — a pressure step up and back down lasting
a fraction of a millisecond — which is why it reads as a whip rather than a bang, and why it is so easy
to place: nearly all its energy is high and broadband. `MechanicalAction` is the bolt, quiet and close,
and it tells a listener a weapon was re-cocked rather than fired again.

They are played at different *places* as well as different times: the blast at the weapon, the crack at
the listener, the action at the weapon a moment later.

A `WeaponProfile` holds everything weapon-specific — velocity, body resonance, decay, brightness — so a
new gun is a profile, not a renderer.

## Two defects the spike caught

The raw N-wave came out sitting **0.119 above the centre line**: resonators and one-pole filters settle
at an offset rather than at zero. That is twelve per cent of the headroom spent on something inaudible,
and a step in the waveform every time a voice starts or stops. And both layers' buffers ended while the
sound was still decaying, which is a click at the end of every shot. `Finish` now DC-blocks at 18 Hz,
fades the last 8 ms to true silence, and only then scales to peak.

The crack's noise tail also decayed too slowly to be over by the end of its buffer — and a crack that
trails off slowly stops sounding like a whip and starts sounding like a firework, which is both wrong and
much harder to place. Shortened to a fifth of the buffer.

## Verifying it

`OpenFPS.AudioLab --gunshot` renders the layers into `ASSETS/SOUNDS/WEAPONS`, prints the timing table,
and asserts the crack precedes the report, the gap recovers the range, a subsonic round is silent, a
distant crack is longer than a near one, and every layer is dry and centred. `--gunshot-live` fires the
same shot from 25 m, 100 m and 400 m through the real spatial path — crack at the listener, report out in
front — so the gap can be heard opening up.

`BallisticsTests` holds the physics to the physics rather than to taste: the moment the gap stops being
`d·(1/c − 1/v)` it stops being trustworthy, and a player who has learned to read range by ear would be
quietly misled.

Tests 244 → 261.
