# open-fps: read this first

open-fps is Cody's game: a C#/.NET multiplayer world heard by screen reader (Cody is blind and works with
Orca and NVDA). Sound falls out of physics, materials and geometry; there are no per-map special cases.

## Before each task
1. Start from Cody's latest `main`, in a fresh branch or worktree of its own.
2. Read the memory index and its newest resume note once a session.
3. The order of work is `todo.md` ("Next, in order") and the plans it names: `docs/SOUND_LIBRARY_BOUNDARY.md`,
   `docs/ENGINE_PACKAGES.md`, `docs/PHYSICS_BOUNDARY.md` and `docs/GEOMETRY.md`, each with its stages and
   Cody's decisions. Cody's latest note says which task is next.
4. Before any push, run a broad sweep: every test class changed since the last push, in filtered runs.
   Name-filtered guard runs alone have missed failures.

## Several agents at once
- Run several agents at once where tasks touch different files, each in its own git worktree from `main`. The
  plans list what may run beside what; two tasks that change the same files run one after the other, the second
  based on the first.
- At most two test runs at once across all agents, each filtered, each with its own artifacts folder.
- Merge only locally. Push and deploy only when Cody says so, each time.

## Finishing a task
- Commit on the task's branch. End each message with the Co-Authored-By line the session gives, and explain any
  stream or render regeneration in it.
- Stored streams go in their own commit, last, explained voice by voice.
- Say whether the wire hash changes (then the server and the Windows client deploy together), and whether the
  door fingerprint does (then the door renders are made again).
- Say what Cody should listen for, with `/tp` spots (x east, y north, z floor height), each checked with
  `StandingSpots` or AudioLab `--walk-pair ears=`.

## Rules that keep the machine and the work safe
- Build only through `tools/build-local.sh <artifacts-dir> <project>`, and test only through
  `tools/test-local.sh <artifacts-dir> "<filter>"`. They pin the CPU and allow two test runs at once. Never
  run the whole suite locally; GitHub Actions runs it after a push. Never use coverage.
- The tests find the sound bank through `TestPaths.Sounds()`: `OPENFPS_SOUNDS` if set, else the checkout's
  `OpenFPS.Client/ASSETS/SOUNDS`.
- Put artifacts outside the checkout, for example `~/.cache/openfps-build/<name>` (`/tmp` is 8 GB of RAM).
- Run the guards on every change: LibraryBoundary, EnginePackages, PhysicsBoundary, RenderFingerprint,
  EmitterStreamReplay, SpokenLinesReplay, VoiceIdBand, VoiceRegistry, VoiceStop, BinauralStages,
  ReverbBusPanning, ModelKindTable, MaterialName, OneConstantPerLaw, MapScoped, WorldWireBytes,
  EnvironmentWireBytes, RicochetTable, and the physics replays: DrivingReplay, RailReplay, MovementReplay and
  PhysicsCrossMachine. Name the replays one by one in a filter: "PhysicsReplay" matches none of
  DrivingReplayTests, RailReplayTests or MovementReplayTests. Also run the replays with
  `-e DOTNET_PROCESSOR_COUNT=4`: streams must not depend on the machine (memory "ci-differs-from-local").
- Builds stay at 0 warnings: server, GTK client, AudioLab, Tests, and the Windows client
  (`-p:EnableWindowsTargeting=true`).
- Fix rules, not maps (memory "fix-the-rule-not-the-map"). `city.json` and the places' maps come from their
  generators; never hand-edit them.
- Docs are plain prose: one-line summaries, comments that say why, measured facts kept.
- Steam Audio and FMOD native libraries live in `lib/`. Most tests don't need them; the opt-in Steam Audio
  tests need `OPENFPS_STEAMAUDIO_TESTS=<repo>/lib`.
- A new project is added to the boundary surveys' reader too (`tools/sound_boundary/Program.cs`, its project
  list and every list of references above it), and to `tools/engine_packages/classify.py`. Rerun the surveys'
  `run.sh` after editing lists by hand (`files.tsv`, `types.tsv`, the ratchets).
- Nothing that can take longer than a frame runs where sounds are placed (`ClientAudioSystem.Update` and
  `AudioWorld.Tick` under it, `AudioEngineFacade.Tick`): build it ahead or off that thread (`PlacementThread`, `VoiceBuilds`), and mark it
  with `PlacementThread.Heavy()`.
- An approved sound is not respecified: change only what the task forces, and say if a render could change.
