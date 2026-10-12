---
name: clack-is-a-loose-bar
description: 10-03 push bar round 8 REJECTED for a buzz at the end of the opening; "clack" means a loose, undamped mechanism, not a treble lift; round 7 approved and merged
metadata:
  type: feedback
---

Cody, 2026-10-03, on push bar round 8: "why do i hear a buzzing on the push bar open ... when I say
clack i don't mean bump the high frequencies, I mean think of the bar being loose, remove the
dampening. think of the bar mechanism being loose, not stiff ... round 7 sounds better than round 8
simply because of that buzzing ... round 7 approved for now".

Round 8 (b27e2511, reverted by f4c3fe34) made the clack bright by ringing the steel case from the
bolt's 0.1 ms stop and muted the leaf. The buzz at the end of the opening was never identified:
suspects are the bar chattering on its back stop under leaf vibration (bar-back hits every ~4.5 ms,
equal peaks = a driven limit cycle at ~220 Hz) or the loose-parts ticks.

**Why:** a bright spectrum is not a clack. A clack is parts with play, free to knock: clearance, low
damping, several hits within a few ms. **How to apply:** for "clack", give the parts play and take the
damping out of their contacts, then check the event train for buzz (equal-peak repeated contacts)
before sending. Round 7 is merged to main (e96e13cb). See [[doors-need-detail-and-level]],
[[knob-door-pilot]].
