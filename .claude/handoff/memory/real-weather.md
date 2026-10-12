---
name: real-weather
description: 10-10 design docs/REAL_WEATHER.md (merged 96403cbd): Open-Meteo + METAR correction; 7 decisions open for Cody; build stays in todo item 6 order
metadata:
  type: project
---

Cody 2026-10-10 was tempted by real weather data; agreed to stick to the plan, so only a design was written.
docs/REAL_WEATHER.md: Open-Meteo (worldwide, gridded, no key, CC BY 4.0, free tier NON-COMMERCIAL) as the base,
METAR from aviationweather.gov (one worldwide file every 10 min, public domain) correcting it near the ground (model
winds were 1/3-1/2 low at Magnolia/Albany on 10-10). NWS API later; mesonets not practical; GOES GLM lightning
optional. Real data = conditions only; simulation still makes gusts, cells, fronts. 0.1 deg grid, front as a moving
line on the wire, 48 h forecast fallback then the simulation after 6 h. 6 stages, 8-13 sessions (1-3 = 4-6 give real
weather at the places). Feeds "the per-world weather the host feeds" (library stage 5 slice 1, [[sound-library]]).
7 decisions OPEN for Cody (doc section on decisions); biggest is the world clock: real local time at real places vs
the 60x game clock. Build waits for todo item 6 (weather as a system) unless Cody reorders.
**2026-10-11 decided:** real local time at real places (Cody). Sun/moon readout (key + /sky) planned with stage 1.
