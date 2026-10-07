# Housekeeping

Cody, 2026-10-07: remove dead and unused code and update the comments. "Comments in the code should be
as needed, let the code do the talking, comments to help explain important or non obvious things.
Joanie style comments, short and useful where they apply." Joanie is Joanmarie Diggs, Orca's developer.

## Comments

Her code is the reference: `/usr/lib/python3.14/site-packages/orca`, for example `focus_manager.py`.

- A one-line summary on a type or method when its name does not say it all. No summary that repeats
  the name.
- Inside code, a comment only for the why that the code cannot show: a rule that must hold (a thread,
  an order, an allocation), a measured number, a trap that bit before, a link to the doc or bug.
  One to three plain lines.
- No narration of what the next line does. No history of how it came to be written ("before, this
  was..."): that belongs in `changes.md` and the docs, and is there already for most of it.
- Keep every warning and every measured fact, said in one sentence. If a long comment holds history
  or reasoning that is in no doc, move it to the doc for that area (docs/*.md) and leave a one-line
  pointer. Losing a warning is how an old bug comes back.
- Open questions: `// TODO: ...` in one line.
- XML doc comments stay valid (`<see cref>` targets that exist).

## Dead code

- Remove a type, member, file, parameter or setting only when the build and the tests prove nothing
  uses it: no references in any project (tests, AudioLab and tools included), no reflection or
  string lookup (prefab names, JSON keys, command names, env vars), no wire type (MemoryPack unions
  and members stay, positions matter).
- AudioLab spikes are instruments Cody's work relies on: keep one unless it no longer builds against
  anything real; list any removed in the report.
- Commented-out code goes.

## What must not change

- Behaviour and sound. The guards prove it: `RenderFingerprintTests`, `EmitterStreamReplayTests` and
  `LibraryBoundaryTests` must pass with no regeneration. A housekeeping change that moves them is a
  mistake to undo, not a stream to regenerate.
- The wire: no field reordered, removed or renamed in a MemoryPack type; `WireContract.Hash` and
  `DoorModelFingerprint` will change with any edit to their sources, which is fine.
- Public names used by data files (prefab-schema.json, machines/, maps, scripts.json).

## How to work

- One project per agent, small commits (a folder or a theme each), messages saying what went.
- Build only with `tools/build-local.sh`, test only with `tools/test-local.sh` (twelve cores, a
  machine-wide lock: Cody's machine froze from parallel test runs). Pushes are held, so at the end of
  a project run the whole suite once through the script:
  `tools/test-local.sh ART "FullyQualifiedName~OpenFPS.Tests"`, and fix what it shows.
- Report: lines of code and of comments before and after, what dead code went, anything moved to
  docs, anything left for Cody.
