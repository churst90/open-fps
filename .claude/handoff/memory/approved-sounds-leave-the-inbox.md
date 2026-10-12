---
name: approved-sounds-leave-the-inbox
description: Cody's rule 09-28 — anything approved or used by a model moves from inbox/ to approved/ (gitignored, not shipped); he clears the inbox freely
metadata:
  type: feedback
---

Once Cody approves a sound, or a model/importer uses a recording, MOVE it out of `inbox/` into
`approved/<category>/` and add a row to `approved/README.md` (what, when approved, what uses it).
**Why:** he cleans the inbox of old/approved files himself; anything left there can vanish (the
09-24 door references already did). **How to apply:** `approved/` sits beside `inbox/`, gitignored
(public repo, 1.1 GB, cloned voices) and NOT under ASSETS — everything under
OpenFPS.Client/ASSETS is copied into the client build, and Cody wants the client small. Tools that
read sources point at approved/ (import_npc_voices, rebuild_footstep_bank, cut_calls). Put new
listening sets in inbox; move the chosen one on approval. See [[car-door-fit]].
