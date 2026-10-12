---
name: tests-on-github
description: 10-06 Cody moved full test runs to GitHub Actions (8 shards); never run the 48-min suite locally or in agents unless asked
metadata:
  type: feedback
---

Cody 2026-10-06: "stop the test because I want to run it on github ... I don't want to use agents to
test if I don't need to burn tokens on that."

**Why:** the full suite is 48 minutes of his machine and, inside agents, tokens.
**How to apply:** push to main (or a PR) and read `gh run list --workflow tests.yml` /
`gh run view <id>`; workflow .github/workflows/tests.yml shards by class via tools/ci/shard_tests.py.
Agents run only `--filter`ed tests touching their change. Tests need no FMOD/Steam Audio libs.

**10-06 LESSON:** merging agent branches after only filtered tests let 9 failures into main (CI run 37526845230:
NetworkTrim x4, StateSplit, MachineTests TyreProfile round trip x2 (wet roads), MachineVoiceTests flow:/shore:
kinds, RemoteFootsteps realistic). Most later CI runs were CANCELLED by the next push (concurrency), so nobody saw
it. Before merging a branch that touches wire/state/vehicles/prefabs, run the broad classes (NetworkTrim, StateSplit,
MessageRoundTrip, Machine*, MachineVoice, RemoteFootsteps, ClientAudioSystem) or let one CI run FINISH before the
next push. Wet-roads agent asked to fix + bisect RemoteFootsteps. Geometry stage 1 merge held until then.
10-06: fixes merged (241f7fbb: StatePacking sized wheels by Unsafe.SizeOf<WheelState>; tyre inflation/tread in
Describe/Assemble) -> CI run 37545148884 ALL GREEN, the first full pass since the morning. Geometry stage 1
(branch worktree-agent-abcda6a1357fe0b76) waits on Cody's 5 decisions, then merge.

2026-10-07 run 37576816453 shard 2: PresenceTests.ALoginIsAnnouncedToEveryoneElseOnTheAllChannel HUNG 30 min (blame
timeout) after 39 tests in 5 s; passed 25/25 locally. Login's ContinueWith already uses TaskScheduler.Default, so
not that. Suspects: SqliteConnection.ClearAllPools() (global) in other classes' Dispose racing a live login; watch
for a repeat. To find a hung test: `gh run download RUN`, compare Sequence_*.xml (started) with the trx results.

2026-10-07 LOCKUP: parallel full-suite runs by agents (one with ~1050 % CPU for 40 min) + mine took the 24-core
desk machine to load ~350; Cody's desktop and Orca froze; another session killed the runs at his request.
RULES NOW: OpenFPS.Tests/xunit.runner.json maxParallelThreads 4 (in the csproj); every brief says ONE filtered test
command at a time with `-- xUnit.MaxParallelThreads=4 RunConfiguration.MaxCpuCount=1`, never alongside a build,
no coverage collector runs locally; agents clean /tmp. Check `uptime` before starting tests.
2026-10-07 CI 37595864603 on 99ce1541 (library stages 0-2, editor phase 2, ranking, fire, downpipe): ALL 8 GREEN,
including the render fingerprint and stream replay guards on CI's glibc (tolerance path) and the login test.
2026-10-09 LESSON: any new SpatialEmitter field changes EVERY line of EmitterStreamReplayTests' stored streams
(OpenFPS.Tests/LibraryBoundary/streams/*.txt.gz) - the scattering fix failed drive/traffic_rain/walk after merge.
Check: OPENFPS_REPLAY_DUMP=dir, strip the new field, diff against zcat of the stored; if identical, regenerate with
OPENFPS_REPLAY_WRITE=1 in the same commit with the reason. Put EmitterStreamReplayTests in every agent's filter.

10-10: merging many agent branches in a row, each checked only with its own filter, let three cross-merge breaks
through to GitHub (BorrowedVoiceDoppler after engine CPU; campfire declared level 63 -> 78 after fire+loudspeaker;
DistantUpdates poor-connection pitch 43 % after later merges). The per-merge filter only covers the branch's own area.
**How to apply:** after a batch of merges, run one broad filter across every area the batch touched (and the
ClientAudioSelection/DistantUpdates/BorrowedVoice style integration tests) before pushing, and always read GitHub's
result for each push.
