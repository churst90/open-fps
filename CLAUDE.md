# open-fps: read this first

open-fps is Cody's game: a C#/.NET multiplayer world heard by screen reader (Cody is blind and works with
Orca and NVDA). Sound falls out of physics, materials and geometry; there are no per-map special cases.

## Handoff (2026-10-12): working from the Resonance side

Cody is near his weekly Claude limit, so work continues on the Resonance developer's machine. You are **not a
contributor on this repo**. Never `git push` to open-fps. Your work comes back to Cody as patches through the
Resonance repo, as described below.

### Before anything else
1. Read `.claude/handoff/memory/MEMORY.md`, the index of the coordinator's working memory. Private details
   are removed. Then read `.claude/handoff/memory/resume-2026-10-12.md`, which says where things stand and
   what comes next.
2. Read `todo.md` ("Next, in order", item 2) and `docs/SPECIAL_CASES_AUDIT.md`: the "P3d as built" entry and
   the P4 entry.

### The work, one thing at a time (to save tokens)
- **First: P3d step 6, the reseed.** Seeds become a stable hash of each voice's kind, owner and part. The
  seed table goes, and the woods' ids move into the allocator. Every stream is regenerated in that one
  commit, with the reason, plus a band-level render check. Cody approved this on 2026-10-11. With it, if
  cheap: rain patches asked under probes (the last of P6), and the S24 default ranges. The rain and bump start
  levels need Cody's decision; list them, don't change them.
- **Then: P4, one ranking.** Every voice is ranked by its rendered level at the ear, including the provider's
  own orderings. See the audit's P4 entry.
- **Use at most two agents, and preferably one.** Finish one task before starting the next.

### Handing work back (every finished task)
1. Commit on a local branch of open-fps. End each message with
   `Co-Authored-By: Claude <noreply@anthropic.com>` and explain any stream or render regeneration in it.
2. Make patches against the commit you started from:
   `git format-patch <start>..HEAD -o <resonance-repo>/outbox/open-fps/<yyyy-mm-dd>-<short-name>/`
3. In that folder, add `README.md` covering:
   - what the patches do;
   - the open-fps commit they apply on;
   - the tests run, with pass/fail counts;
   - what Cody should listen for, with `/tp` spots (x east, y north, z floor height), each checked with
     `StandingSpots` or AudioLab `--walk-pair ears=`.
4. Commit and push the Resonance repo. Cody pulls it and applies the patches here with `git am`.

### Rules that keep the machine and the work safe
- Build only through `tools/build-local.sh <artifacts-dir> <project>`, and test only through
  `tools/test-local.sh <artifacts-dir> "<filter>"`. They pin the CPU and allow two test runs at once. Never
  run the whole suite locally; Cody's GitHub Actions runs it after the patches land. Never use coverage.
- Put artifacts outside the checkout, for example `~/.cache/openfps-build/<name>`.
- Run the guards on every change: LibraryBoundary, RenderFingerprint, EmitterStreamReplay, VoiceIdBand,
  VoiceRegistry, ModelKindTable, MapScoped, PhysicsBoundary, and the physics replays. Also run the replays
  with `-e DOTNET_PROCESSOR_COUNT=4`: streams must not depend on the machine (memory "ci-differs-from-local").
- Builds stay at 0 warnings: server, GTK client, AudioLab, Tests, and the Windows client
  (`-p:EnableWindowsTargeting=true`).
- Fix rules, not maps (memory "fix-the-rule-not-the-map"). `city.json` and the places' maps come from their
  generators; never hand-edit them.
- Docs are plain prose: one-line summaries, comments that say why, measured facts kept.
- Steam Audio and FMOD native libraries live in `lib/`. Most tests don't need them; the opt-in Steam Audio
  tests need `OPENFPS_STEAMAUDIO_TESTS=<repo>/lib`.
