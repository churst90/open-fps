---
name: shelter-is-not-insulation
description: The upward-ray ShelterFactor is for rain/wind; applying it to outdoor SOUNDS muffled everything under a bus shelter or beside a tall building by up to 40 dB
metadata:
  type: project
---

2026-09-23, "at the back of the bus shelter I can't hear through the opening; I have to stand right
at it". `ClientGameSession.UpdateShelterFactor` casts 16 rays upward; under a shelter roof nearly all
hit, and the provider took `shelter*40` dB off the top and `*20` off the mids of every outdoor-region
source. The same fired beside any tall facade (half the upward rays hit it). Now the provider uses
`InsulationFactor` = shelter only when the LISTENER's region is an enclosure; Atmospheric (rain,
wind) keeps the raw sky shelter. Also found the same day: the estate's 64 window curtains
(`carpet_wall`) hung solid across every front door — moved beside it (city.json + gen_city.py).
See [[region-is-not-a-room]], [[a-small-room-inside-a-big-one]].


**Superseded 2026-09-30:** InsulationFactor is deleted entirely for sounds; see [[doors-are-geometry]].
