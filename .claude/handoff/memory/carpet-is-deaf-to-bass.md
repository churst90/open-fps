---
name: carpet-is-deaf-to-bass
description: A carpeted room with hard walls keeps a 2 s bass tail; plasterboard is the membrane that takes it out
metadata:
  type: project
---

Carpet absorbs 0.19 mid and 0.28 high and **0.061 low** — a thin absorber is deaf to a long
wavelength. So a carpeted flat with solid concrete/brick walls measured **2051 / 608 / 390 ms**
(low/mid/high) and was reported as *"the carpeted flat sounds reverby like it's a reflective room not
carpet"*. The carpet was working; nothing in the room was taking the bottom out.

What does that is **Plaster** (added 2026-09-19): plasterboard on studs, absorption **0.28 low /
0.10 mid / 0.05 high** — the only common material whose absorption goes DOWN with frequency, because
it is a membrane and a long wavelength flexes it. Plus `furniture_soft` (Audience material): a sofa
and a bed, upholstery with air behind it, which works where carpet cannot.

City flats after: **499 / 445 / 344 ms, send 74 %** — a furnished room. And the stairwell inverted as
it should: a plastered soffit eats the bass, tile reflects the treble, **818 / 2061 / 3127 ms**,
bright and ringing, which is what a tiled stairwell is.

**Why:** "add more carpet" is the wrong instinct for a boomy room. The bands are separate problems and
they need different mechanisms.
**How to apply:** read `--enclosure map=<id> at=x,y,z`, which prints decay AND absorption per band.
A tail that is bass-heavy wants membranes; one that is bright wants soft surfaces.
See [[regions-measure-themselves]], [[the-room-is-not-a-cube]].
