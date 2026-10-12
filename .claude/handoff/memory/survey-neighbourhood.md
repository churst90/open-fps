---
name: survey-neighbourhood
description: Enclosure.Look gathers the boxes in reach ONCE instead of rejecting them per ray — 32 ms to 9 on a city
metadata:
  type: project
---

**Session 17, 2026-09-19.** `Enclosure.Nearby`, in `OpenFPS.Common/Enclosure.cs`.

The survey casts 192 rays and 192 bounces, and every cast walked the WHOLE solid list rejecting what
was out of range by a distance test. That test is right and cheap, but doing it 384 times means a
4,165-box city does one and a half million distance tests to look at the ~200 boxes actually within
reach.

Measured: **7.5 ms** on the 390-box block, **32 ms** on the city — for the same question about the
same room. With one prefilter pass: **9 ms** on a map ten times the size, better than the old small
map cost.

Nothing about the measurement changes — the same boxes are hit in the same order. The sphere kept is
`2 × ReverberantRangeMetres` plus the box's own half-diagonal, because a ray can bounce twice and the
second surface is still this room's. Scratch list is `[ThreadStatic]`: the survey runs on the
acoustic worker and on whatever thread a spike calls it from.

**Why:** a map fifty times the area of the block it replaced would otherwise have made the listener
reverb the most expensive thing on the audio worker. See [[the-room-is-not-a-cube]], [[city-block-map]].
**How to apply:** time it with `--enclosure map=city walk=... step=...` over a few hundred points;
the per-survey cost is the wall clock over the point count.
