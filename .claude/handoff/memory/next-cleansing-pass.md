---
name: next-cleansing-pass
description: START HERE — todo.md Now order updated 09-27 (acoustics first, then mutation); the 2026-09-24 plan (docs/NEXT_CLEANSING_PASS.md) and how far it got; todo.md is now the priority list
metadata:
  type: project
---

Plan: `docs/NEXT_CLEANSING_PASS.md` (Cody's 2026-09-24 words). The live priority list is now root
`todo.md` (rewritten 09-24; docs/todo.md, docs/readme.md, docs/changes.md were deleted as stale
duplicates — root files are canonical).

Status at end of 2026-09-24 session:
1. Air horns — DONE, all horns approved ([[signals-and-air]]).
2. Coverage — DONE: `docs/COVERAGE_2026-09-24.md` (ClientAudioSystem 9%, Fmod 17%, physics 93-96%).
3. Mutation testing — Stryker.NET 5.0 RUNNING overnight on a tmpfs copy `/tmp/openfps-stryker`
   (configs `stryker-config.json` in OpenFPS.Common and OpenFPS.Client.Core; Common has 7,585
   mutants). Reports land in each project's `StrykerOutput/`. Logs in the session scratchpad
   (lost on reboot — the reports in /tmp/openfps-stryker are what matter). Next: kill survivors.
4. Cleansing — first cut committed (112beb5): dead code, voice packets owned by their voice,
   PlayUiBeep gone, crowd cooldown per map. Rest listed in todo.md "Now 2".
   Also done on Cody's word: door opening uses mass/material (c757f3e), mufflers use Steepening +
   continuous SoftCeiling (2df6f97) — both approved by ear ([[soft-ceiling-and-muffler-steepening]]).
5. Vehicle consistency audit — NOT STARTED (todo.md "Now 4"; bikes unmeasured on --voice-levels).
6. Gunfire — NOT STARTED.
7. Docs — DONE (783d91f): readme, todo, changes, docs/MANUAL.md (one manual, two parts, Cody chose).
See [[plain-docs]].

09-27: todo.md rewritten current. Now 1 = acoustics (blocked bearing flips [[blocked-bearing-flips]],
per-surface reverb, ground reflection for sample-played sounds, bird stalls, hear /reverb traced).
Old session scratchpads archived to ~/openfps-scratch-archive (gun analysis in 53f7c168.../guns).
Repo is PUBLIC and 10 shipped NPC voices are clones of real people: asked Cody before pushing them.
