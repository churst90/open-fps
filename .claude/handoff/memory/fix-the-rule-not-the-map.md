---
name: fix-the-rule-not-the-map
description: Cody 2026-10-11 - Magnolia/Albany are test beds; fix the generator/engine rule systematically with a guard test, never hand-tune a place or chase single buildings
metadata:
  type: feedback
---

Cody 2026-10-11: "the albany and magnolia maps aren't important themselves, only that the issues you've spotted are
systematically fixed so they don't occur again, you don't necessarily have to spend time editing the maps themselves".

**Why:** the real target is the generated world (every place, Overture buildings everywhere); a fix that only
repairs two maps repeats on the next town.
**How to apply:** when a map shows a defect, find the general cause in the generator / engine rule, fix it once,
and add a test over every instance (every room, door, floor) as the guard; regenerate maps from generators only;
leftover odd cases are listed with their cause, never special-cased. Put this in agent briefs. Related:
[[no-special-cases]], [[city-json-is-generated]].
