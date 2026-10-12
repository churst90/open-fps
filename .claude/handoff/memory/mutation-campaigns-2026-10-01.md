---
name: mutation-campaigns-2026-10-01
description: Two Stryker campaigns running from ~/.cache/openfps-stryker (server since 09-30 21:10; new code restarted 10-01 00:02); how to read progress and where reports land
metadata:
  type: project
---

Root ~/.cache/openfps-stryker (btrfs, not /tmp). run.sh <copy> <proj> <home> <log> isolates HOME.
- **Server**: copy-server (main as of 09-30 ~20:50), whole OpenFPS.Server less Program/Sqlite/AppDbContext/
  MudGateway/NetworkService. 6,068 mutants under test from 23:00 09-30 (2,127 no coverage). Reporters
  progress/cleartext print NOTHING without a TTY, so no progress in server.log until it ends. Its 35
  "survived" are only-covered-by-the-two-failing-traffic-tests (not real survivors).
- **New code**: copy-new re-synced from main 10-01 00:02 (includes WallTransmission.cs, SpatialService,
  SpatialAcoustics); campaign-new.sh runs Common then Client.Core sequentially; logs new-common.log /
  new-clientcore.log; reporter "dots" gives per-mutant progress. Filter excludes the flaky sparrow test
  and the two failing traffic tests.
- Status lines: campaign-*.status. Reports: <copy>/<proj>/StrykerOutput/<date>/reports/.
- Rate reference: engine run 2,827 mutants in 67 h at concurrency ~10 (half timed out).
**10-01 outcome.** Server campaign FINISHED 13:27, score 53.67 %, report in
copy-server/OpenFPS.Server/StrykerOutput/2026-09-30.21-10-34/reports/. Common campaign DIED at ~81 %
(3,120 of 3,830 mutants: 1,888 killed, 1,204 survived, 28 timeout) when the machine froze; Client.Core
never started. Cause, from /var/log/messages: a mutant made ONE Stryker test host allocate ~36-38 GB,
three times (20:09, 20:15, 23:09). The third OOM came with swap full (56 kB free) and the box thrashed
until Cody force-restarted at 23:24. Stryker's timeout is too slow to stop a runaway allocator.
Before rerunning: cap each .NET process (DOTNET_GCHeapHardLimit in run.sh, so a runaway throws
OutOfMemoryException and the mutant counts as killed), and consider concurrency below 10.
Stryker.NET cannot resume a partial run; Common restarts from zero.

Server survivors TRIAGED 10-01 into docs/MUTATION_2026-10-01.md (7 high-confidence bugs, traffic-safety gap, nothing fixed yet; Cody said discuss and document first). Likely runaway mutant: RaceLine.Resample k++ -> k-- (List<Vector3> peaks at 36 GiB). See [[next-cleansing-pass]].

**10-02 harness hardened (nothing run; Cody: "hold off on the server mutation for now").** run.sh sets
DOTNET_GCHeapHardLimit 8 GiB and starts watchdog.sh (kills the largest testhost under 10 GiB available;
no user cgroups on this OpenRC box). Runs are now SECTIONS: sync.sh copy-section, then
section.sh <name>... with sections/*.json (server-people/commands/core/world/vehicles,
common-acoustics/vehicles/voices), concurrency 6. ~/.cache/openfps-stryker/README has it all.
