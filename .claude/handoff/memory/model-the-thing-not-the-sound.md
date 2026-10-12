---
name: model-the-thing-not-the-sound
description: Cody 10-03 — doors (and by extension machines) must be physical models of the object; per-event fitted sound recipes were rejected; he does not want recordings, the model is the point
metadata:
  type: feedback
---

Cody, 2026-10-03, after a round of door sounds synthesised per event (contact + modal recipes fitted
to 27 Freesound recordings, docs/DOOR_TYPES.md): "are we modeling the doors or modeling the sound?
... we need to model the physical doors. that means dynamically squeaky hinges, simulate the motor on
the sliding door ... the push bar sounds are very grainy, some are too tonal". He liked none of them;
the round was reverted (88fe0de7). He is NOT sourcing recordings: "I want to model them anyway".

**Why:** a fitted recipe plays the same event the same way whatever the door is doing; the detail he
hears in real doors comes from the mechanism's state (speed, load, wear, clearances). The engine,
starter and car door work the same way and are what he approved.

**How to apply:** build the object as a simulation (rigid leaf on a hinge axis, stick-slip hinge
friction, spring latch on the strike, linkage clearances that rattle as impacts, hydraulic closer,
DC motor + gearbox + rollers on a rough track) and let the sound come out of its state. Recordings
are a yardstick only, never a template. Pilot one door kind and get his verdict before the rest.
Noise only where the physics is noise; rattles are impacts. See [[house-door-model]],
[[car-starter-model]], [[render-check-before-sending]].
