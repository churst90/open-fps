---
name: fire-model
description: 10-07 fire at any size APPROVED by ear; how it is built and what round 2 fixed
metadata:
  type: project
---

Cody 2026-10-07: "new fire sounds are great" (inbox/fire-2026-10-06/round2, merged 5797bfce). docs/FIRE.md.
Up to 48 burning bodies, heard from 9 places on a ring sized so they spread as the burning area does
(radius factor sqrt((m+1)/6m)); roar = turbulent tail of dQ/dt (f^-2.5), crackle crowds, events by fuel (house
windows/collapse, car struts/tyres/tank, trees torching), wind, t^2 growth to smoulder. 7 presets; /spawn fire
PRESET|out. ExtendedSources.MaxPlaces = 12 voice ids per source (shore had 10 places on 8 ids).
**How to apply:** judging width by IACC, compute the EXPECTED value from the geometry first: a fire straight
ahead reads ~1.0 at 125-500 Hz through this HRTF whatever is done; my "mono" alarm on round 1 was mostly that.
Fire pit at 58 Alder St roar is now physical (+9 dB under 125 Hz) - included in the approval.

Cody 2026-10-10 direction: fire driven by FUEL, not a grid or nearness: a thing ignites because it is flammable and
receives enough heat (radiation, flame contact tilted by wind/slope) or an ember lands on it (wind-carried);
lightning is one ignition. Fire must be dynamic: its character and sound change with what is burning (grass flash,
wood pile, car plastics/tyres/tank, tree torching). Fire fills a given shape (circle/rect/outline/zone), roar at
flame height, crackle at the bed; zones carry fuel. Agent started 10-10 (renders inbox/fire-fuel-2026-10-10).
10-10: fire-by-fuel renders (inbox/fire-fuel-2026-10-10) APPROVED by ear ('new fire sounds good as well'); agent told to commit as rendered.
