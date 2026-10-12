---
name: sa-source-pool
description: "Steam Audio source pool (64) ran out indoors — newest source fell back to the hand-rolled tracer; now reclaims the stalest (09-23)"
metadata:
  type: project
---

Cody: "the acoustic simulator fell back to the hand-rolled, then switched back when I stepped out".
923 DEGRADED lines: `SaMaxSources = 64`, sources held 5 s after last request, first-come; inside the
towers > 64 were live and the newest was refused. `AsyncAcousticWorker.PickSourceToReclaim` now
takes the least-recently-requested source not asked about this tick. A DEGRADED line now means > 64
in ONE tick. Tests: AcousticSourcePoolTests. See [[occluded-voices-burst-every-five-seconds]].
