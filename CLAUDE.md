# open-fps: read this first

open-fps is Cody's game: a C#/.NET multiplayer world heard by screen reader (Cody is blind and works with
Orca and NVDA). Sound falls out of physics, materials and geometry; there are no per-map special cases.

## Handoff (2026-10-12): working from another machine

Cody is near his weekly Claude limit, so work also goes on on another developer's Windows machine. There you
are **not a contributor on this repo**: never `git push` to open-fps. Your work comes back to Cody as patches
through that developer's Resonance repo, as described below. Nothing of Resonance's code goes into open-fps.

### Before each task
1. `git fetch origin` and start from Cody's latest `origin/main` (a fresh branch or worktree from it). Say in
   the task's README which commit it is based on.
2. Read `.claude/handoff/memory/MEMORY.md` and `.claude/handoff/memory/resume-2026-10-12.md` once a session.
3. The order of work is `todo.md` ("Next, in order") and three plans: `docs/PHYSICS_BOUNDARY.md` (section 7,
   the stages; section 11, the decisions), `docs/ENGINE_PACKAGES.md` (section 9, the stages; section 13, the
   decisions, all approved 2026-10-12) and `docs/GEOMETRY.md` (stage 6). Cody's latest note says which task is
   next.

### Several agents at once
- Run several agents at once where tasks touch different files, each in its own git worktree from
  `origin/main`, each task its own patch folder. The plans list what may run beside what; two tasks that change
  the same files run one after the other, the second based on the first (say so in its README).
- At most two test runs at once across all agents, each filtered, each with its own artifacts folder.
- Send each folder as soon as it is finished and go on to the next task; do not wait for Cody between tasks.

### What this machine cannot do
Anything that has to be measured with Steam Audio, run in the FMOD mixer or heard: say exactly what in the
README (file, method, expected result) and Cody's machine does it. The Steam Audio halves of tasks (geometry
stage 6's scene instances, anything "measured with --pop-hunt") are Cody's machine's.

### Handing work back (every finished task)
1. Commit on the task's branch. End each message with `Co-Authored-By: Claude <noreply@anthropic.com>` and
   explain any stream or render regeneration in it. No names or email addresses of that machine's owner in
   commits or docs; write "made on Windows", not where it was made.
2. Make patches against the commit you started from:
   `git format-patch <start>..HEAD -o <resonance-repo>/outbox/open-fps/<yyyy-mm-dd>-<short-name>/`
3. In that folder, add `README.md` covering:
   - "Questions for Cody" first: numbered, each with your recommendation and what you did meanwhile (go with
     your recommendation unless the question blocks the task);
   - whether the wire hash changes (then the server and the Windows client deploy together);
   - what the patches do, and the open-fps commit they apply on;
   - the tests run, with pass/fail counts, and each new test proven by breaking what it holds;
   - what Cody's machine must check (FMOD, Steam Audio, the boundary surveys' `run.sh`);
   - what Cody should listen for, with `/tp` spots (x east, y north, z floor height), each checked with
     `StandingSpots` or AudioLab `--walk-pair ears=`.
4. Stored streams go in their own commit, last, explained voice by voice; a new scenario's stream file is left
   out for Cody's machine to make.
5. Commit and push the Resonance repo. Cody pulls it and applies the patches here with `git am`.
6. End your reply with the same "Questions for Cody" list, so it can be passed on.

### Rules that keep the machine and the work safe
- Build only through `tools/build-local.sh <artifacts-dir> <project>`, and test only through
  `tools/test-local.sh <artifacts-dir> "<filter>"`. They pin the CPU and allow two test runs at once. Never
  run the whole suite locally; Cody's GitHub Actions runs it after the patches land. Never use coverage.
- **On Windows** those two scripts don't run (they need `taskset`, `flock` and `/proc`). Use `dotnet`
  directly with the same limits:
  - build with `dotnet build <project> --artifacts-path %USERPROFILE%\.cache\openfps-build\<name>`;
  - test with `dotnet test OpenFPS.Tests --artifacts-path <same> --no-build --filter
    "Category!=Timing&(<filter>)" -- xUnit.MaxParallelThreads=4 RunConfiguration.MaxCpuCount=1`;
  - keep it to at most two test runs at once.

  Streams regenerated on Windows carry Windows' maths line. Cody's machine compares them within tolerance
  and regenerates them on Linux after `git am`; say in the README when you regenerated any.
- The tests find the sound bank through `TestPaths.Sounds()`: `OPENFPS_SOUNDS` if set, else the checkout's
  `OpenFPS.Client/ASSETS/SOUNDS`.
- Put artifacts outside the checkout, for example `~/.cache/openfps-build/<name>`.
- Run the guards on every change: LibraryBoundary, RenderFingerprint, EmitterStreamReplay, VoiceIdBand,
  VoiceRegistry, ModelKindTable, MapScoped, PhysicsBoundary, EnginePackages, and the physics replays. Also run the replays
  with `-e DOTNET_PROCESSOR_COUNT=4`: streams must not depend on the machine (memory "ci-differs-from-local").
- Builds stay at 0 warnings: server, GTK client, AudioLab, Tests, and the Windows client
  (`-p:EnableWindowsTargeting=true`).
- Fix rules, not maps (memory "fix-the-rule-not-the-map"). `city.json` and the places' maps come from their
  generators; never hand-edit them.
- Docs are plain prose: one-line summaries, comments that say why, measured facts kept.
- Steam Audio and FMOD native libraries live in `lib/`. Most tests don't need them; the opt-in Steam Audio
  tests need `OPENFPS_STEAMAUDIO_TESTS=<repo>/lib`.
- A new project is added to the boundary surveys' reader too (`tools/sound_boundary/Program.cs`, its project
  list and every list of references above it), and to `tools/engine_packages/classify.py`. Lists edited by hand
  (`files.tsv`, `types.tsv`, the ratchets) are said to be in the README; Cody's machine reruns the surveys'
  `run.sh` to check them.
- Nothing that can take longer than a frame runs where sounds are placed (`ClientAudioSystem.Update`,
  `AudioEngineFacade.Tick`): build it ahead or off that thread (`PlacementThread`, `VoiceBuilds`), and mark it
  with `PlacementThread.Heavy()`.
- An approved sound is not respecified: change only what the task forces, and say if a render could change.
