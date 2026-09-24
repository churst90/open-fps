# Gunfire

Plan and measurements for realistic gunfire. Started 2026-09-24.

## Goal

A shot should sound like a real shot at every distance and angle: close, far, in front of the
muzzle, behind it, downrange of the bullet, indoors and outdoors.

## What the game does now

- The muzzle blast is fully synthetic (`WeaponSynth.MuzzleBlast`): a Friedlander wave plus filtered
  noise, about 0.1 s long. The world's reflections and reverb are added by the normal sound path.
- A supersonic crack exists (`WeaponSynth.SupersonicCrack`) and is played when the bullet passes
  close enough (`Ballistics.MakesCrack`, `ShotResolver`).
- Handling, casing and reload recordings are in `OpenFPS.Client/ASSETS/SOUNDS/WEAPONS`.

## Reference recordings

`inbox/weapons/cadreforensics`: the NIJ gunshot dataset (Cadre Research, grant 2016-DN-BX-0183).
20 firearms recorded in open desert from about 20 positions each, six shots per position. Zoom H4N
files are 96 kHz stereo. Geometry per experiment number is in `AudioDatafileDescription.pdf`
(0 degrees is downrange, 180 is behind the shooter).

Matches for the game's weapons: WASR (AK, 7.62x39), M16 (5.56), Glock 9, Colt 1911 and Kimber
(.45). No shotgun.

### Measured 2026-09-24 (WASR and M16)

- Most close and downrange recordings clip at 0 dBFS (up to 3,000 clipped samples in a shot).
- The recorder gain changed between positions: at 90 degrees, 40 m reads louder than 20 m. So the
  absolute levels in this dataset cannot be compared across positions.
- Clean (unclipped) recordings: 90, 130 and 180 degrees at 20 m and 40 m, 30 degrees downrange at
  40 m, 23 degrees downrange at 150 m, and (AK) 180 degrees at 3 m and 10 m. These are the timbre
  references. One shot of each is in `inbox/gunfire-references-2026-09-24/`.
- The crack leads the report at 30 degrees, 40 m: 21 ms for the M16, 15.5 ms for the AK. A straight
  bullet path with the Mach cone (muzzle velocity 930 and 715 m/s) predicts 25 and 17 ms; the
  shortfall is the bullet slowing over the first 30 m. The geometry model is right.

### The close recordings are not dry

`inbox/weapons/firing` (six takes): every file clips (194 to 2,892 samples) and rings for 500 to
900 ms to -40 dB, so the place they were recorded in is part of them. They cannot be the dry source.
No clean close recording of these guns exists in the inbox; the clean material is the NIJ set at 20 m
and beyond.

## Plan

1. An offline renderer: weapon, listener angle and distance in, what that listener hears out. Dry
   blast source, directivity, distance and air absorption, the ground reflection, and the crack
   from the Mach cone geometry with a slowing bullet.
2. Render at the reference positions and compare with the recordings: timing first (crack lead,
   ground-reflection delay), then spectrum per band, then decay.
3. Calibrate per weapon from the unclipped references; directivity from the spectral shape against
   angle, since the absolute levels are not usable.
4. Render WAVs to judge by ear before anything goes into the game.
5. Then: shotgun (no reference recording), an impact sound per material, casings that land and
   bounce where they fall, and a proper fire message in the protocol.
