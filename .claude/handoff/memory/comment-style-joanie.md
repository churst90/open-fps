---
name: comment-style-joanie
description: "Cody wants code comments in Joanmarie Diggs' (Orca's developer) style - short, why-only; let the code talk"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 568c4779-136f-4fbd-b7f7-35b87bd6f916
  modified: 2026-10-07T08:39:53.791Z
---

Cody 2026-10-07: "comments in the code should be as needed, let the code do the talking, comments to help
explain important or non obvious things. Joanie style comments, short and useful where they apply." Joanie =
Joanmarie Diggs, Orca's developer; Cody likes her style. Orca's source is local:
/usr/lib/python3.14/site-packages/orca (e.g. focus_manager.py) - read it as the reference.

Her pattern: a one-line summary per method ("Sets the locus of focus (i.e., the object with visual focus).");
inline comments only for the WHY, 1-3 lines, plain ("We clear the cache on the locus of focus because too many
apps and toolkits fail to emit the correct accessibility events."), a bug link where one exists; open questions
as "TODO - JD:"; no narration of what the next line does. About 13 % of Orca's lines are comments.

**Why:** this repo's comments grew into long essays; Cody wants code that reads clean.
**How to apply:** new code and the housekeeping pass. KEEP each comment's warning or measured fact (a
thread rule, a measured number, why a strange thing is done) in a sentence; move long histories to
docs/changes.md rather than lose them. Related: [[plain-docs]].
