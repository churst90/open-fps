---
name: engine-in-packages
description: Cody 2026-10-11 - every part of the engine its own package, working together; physics becomes OpenFPS.Physics (supersedes vehicle physics in Sound)
metadata:
  type: feedback
---

Cody 2026-10-11: "add the separation of physics into its own project in the todo....we'll get to this...all parts
of this game engine need to be separated in their own packages and all work cohesively together".

**Why:** separation is good architecture in its own right (same as his 10-07 word on the sound library), not only
for Resonance; physics is spread over Common, Sound, Geometry and the server, and impacts/air need one home.
**How to apply:** plan OpenFPS.Physics on Geometry (bodies, contacts, momentum, movement, vehicles/wheels/doors out
of Sound, moving air) like the sound library: survey, guards, stages. Any new subsystem gets its own project with
stated dependencies. Supersedes the 2026-10-06 "vehicle physics in Sound" decision. Queued in todo item 2, after
the special-cases audit. Related: [[sound-library]].
