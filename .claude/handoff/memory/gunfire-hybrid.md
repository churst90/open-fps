---
name: gunfire-hybrid
description: Gunfire 09-24 — synthesis designed to a spec measured from the NIJ recordings (no recorded audio or echo in game)
metadata:
  type: project
---

Cody first chose HYBRID, then REVISED (2026-09-24): "I don't want the echo from the recordings in the
gunshots" — nothing recorded goes in the game. SYNTHESIS DESIGNED TO SPEC: measure blast duration,
decay, spectrum vs angle and the crack from the first 20-25 ms of clean takes; synthesize to match;
the game's acoustics make the place. Demos: synth vs real-with-echo-cut. Plan and measurements: docs/GUNFIRE.md.

Facts that shape it:
- inbox/weapons/firing close takes CLIP (194-2,892 samples) and ring 0.5-0.9 s: not a dry source.
- NIJ/Cadre set (inbox/weapons/cadreforensics, 96 kHz stereo Zoom): most close/downrange takes clip,
  and recorder GAIN CHANGED between positions (90 deg: 40 m louder than 20 m) — absolute levels across
  positions are unusable; use spectra and timing. Clean: 90/130/180 deg at 20 and 40 m, 30 deg 40 m,
  23 deg 150 m, AK 180 deg at 3 and 10 m. Geometry by experiment number range (1-20 -30deg, 41-60 90,
  61-80 130, 81-100 180, 101-120 180 close, 121-140, 141-160, 161-180, 181-200, 201-220; A/B letters).
- Crack lead at 30 deg 40 m: M16 21 ms, AK 15.5 ms; straight path + Mach cone predicts 25/17 (bullet
  slows) — the geometry is right.
- Reference shots + the game's current synth: inbox/gunfire-references-2026-09-24/.
See [[synthesis-by-listening]], [[audio-lab-instruments]].
