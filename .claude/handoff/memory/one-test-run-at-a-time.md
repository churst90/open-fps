---
name: one-test-run-at-a-time
description: "Cody 10-07 - parallel test runs froze his machine and screen reader; only tools/test-local.sh, one run at a time"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 568c4779-136f-4fbd-b7f7-35b87bd6f916
  modified: 2026-10-07T07:31:47.963Z
---

Cody 2026-10-07: "the test run locked up my machine, please do not run simultanious test runs at the same time..."
Agents' and my test runs overlapped (one agent ran the whole suite at ~1050 % CPU for 40 min); load hit 350 on
24 cores, the desktop and Orca froze, another session killed the runs.

**Why:** Cody works by screen reader on this same machine; a frozen desktop means he cannot use his computer.
**How to apply:** builds via `tools/build-local.sh ART PROJ`, tests via `tools/test-local.sh ART "FILTER"` (both pinned to the last 12 cores (half; Cody OK) with taskset + DOTNET_PROCESSOR_COUNT=12, nice; tests also flock /tmp/openfps-tests.lock, load gate < 16, xunit 4 classes, filter required).
Put both scripts in EVERY agent brief; no coverage runs locally; never a test run while my own build or another run is going. Full suite = CI.
Related: [[tests-on-github]], [[tmp-is-eight-gigabytes-of-ram]].
Also in every brief: "delete only your own /tmp/openfps-<you> folders; never the scratchpad or anything in /tmp/claude-1000".


**2026-10-11, Cody: "would it hurt if we could allow 2 simultanious tests to run"** -> two slots now
(tools/test-local.sh: /tmp/openfps-tests.lock and /tmp/openfps-tests.2.lock), both pinned to the last 12 cores,
load gate 24. Checked first: per-process scratch config (TestConfigIsolation), ephemeral ports. Still never the
full suite locally, never coverage; agents use the script and nothing else.
