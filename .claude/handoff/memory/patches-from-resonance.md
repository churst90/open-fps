---
name: patches-from-resonance
description: From 2026-10-12 work continues on the Resonance developer's machine; their open-fps commits come back as git format-patch folders in the Resonance repo's outbox/open-fps/, applied here with git am
metadata:
  type: project
---

Cody 2026-10-12, near his weekly limit: his friend (the Resonance developer, $100 plan, not a contributor on
open-fps) pulls open-fps and continues, one task at a time, at most 2 agents (P3d step 6, then P4). The repo's
root CLAUDE.md and .claude/handoff/memory/ (this memory, scrubbed of private details) tell their session.
**How to apply:** when Cody brings patches: they are in the Resonance repo under outbox/open-fps/<date>-<name>/
with a README (base commit, tests, what to listen for). Check the README's base commit, then
`git am <folder>/*.patch` on main, run the guards + a broad check, push only when Cody says.
