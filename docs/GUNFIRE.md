# Gunfire

Plan and measurements for realistic gunfire. Started 2026-09-24.

## Goal

A shot should sound like a real shot at every distance and angle: close, far, in front of the
muzzle, behind it, downrange of the bullet, indoors and outdoors.

## What the game does now

- The muzzle blast is fully synthetic (`WeaponSynth.MuzzleBlast`, client): a damped gas-bubble pulse
  plus filtered noise, about 0.1 s long. The world's reflections and reverb are added by the normal
  sound path.
- Every round is flown (`BulletFlyby`, `Ricochet` in `OpenFPS.Common`). A supersonic round passing a
  listener makes a crack (Whitham N-wave) placed at the Mach-cone emission point; a subsonic one makes
  a wake whizz. Rounds can ricochet.
- Where a round lands it strikes (`BulletImpact`): a physical event per material class (brittle,
  metal, soil, water, wood, panel, soft). Glass breaks (`GlassFracture`).
- Handling is synthesised, not played from recordings (`WeaponHandling`): reload routines, dry fire,
  a bolt worked by hand, the selector click. The routine's length is also the reload's length on the
  server. The drinkingwindgames recordings were the spec (`--reload-spec`).
- Fire selector: each gun's settings are on the weapon (`WeaponDefinition.Selector`, `FireMode`
  Safe, Semi, Auto). X steps on, Shift+X back (`/selector`); a step past the end wraps round. A gun
  nobody has moved sits on Semi. Auto fires at the cyclic rate while Enter is held; the client sends
  `/cease` when Enter comes up.
- Death leaves a body (`OpenFPS.Server/Core/Bodies.cs`): an item named "body of NAME", 70 kg (both
  arms, slows the carrier), with a bag of what they carried beside it. At most 30 per map;
  an uncarried body goes after 30 minutes.
- The admin gun: see below.

## The admin gun

`OpenFPS.Common/AdminGun.cs` (sounds and modes), `OpenFPS.Server/Core/CombatService.AdminGun.cs`
(firing). Prefab `admin_gun`, weapon id `admingun`. Holding, firing, setting and being given it all
need the `admin-gun` permission (Admin only).

- Modes: kill, vaporize (removes the thing hit; a player is killed instead), freeze (10 s), inspect
  (says what was hit). X and Shift+X step through them; `/admingun MODE` sets one.
- Calibre: it fires any registry weapon's ballistics and report. Y and Shift+Y, or `/calibre`. Starts
  on `ar15`. It never runs dry.
- Its sounds are designed, not modelled, since it is not a real object: the calibre's own report
  with a marking layer (ring, drop, zap or flam; 5 variants, `/admingun` lists them), a mode-switch
  click and note, and a hit effect per mode. `--admin-gun` in the AudioLab renders them
  (`Spikes/AdminGunSpike.cs`).

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

## Decision (Cody, 2026-09-24): synthesis designed to a measured spec

First chosen: hybrid (recordings for character, physics for distance and angle). Revised the same
day: nothing recorded goes into the game, above all not the recordings' echoes, which belong to the
range they were made at. The recordings are the SPEC: the blast's duration, decay, spectrum against
angle, and the crack's shape and timing, measured from the first 20-25 ms of the clean takes (before
the range's echo at about 150 ms). The shot is synthesized to match that spec, and the game's own
acoustics make the place it is heard in. Demos compare the synthesis with the real shot with its
echo cut away.

### The spec (measured over the event, 2026-09-24)

An early spectrum measurement used a Hann window starting at the onset, which is nearly zero over
the first 3 ms where almost all of a shot is, and measured the tail instead. Corrected (flat-topped
window from 1 ms before the onset to 20 ms after), side-on at 20 and 40 m, dB re the loudest band:

| 125 Hz | 250 | 500 | 1 kHz | 2 kHz | 4 kHz | 8 kHz | 16 kHz |
|---|---|---|---|---|---|---|---|
| 0 | -2 to -8 | -5 to -9 | -2 to -9 | -7 to -14 | -17 to -23 | -20 to -22 | -35 |

Envelope: positive phase 0.35-0.5 ms; down 10 dB in 0.75-1.5 ms, 20 dB in 2.5-3.5 ms, 30 dB in
4-7 ms. The game's current synthetic shot takes 18-26 ms to fall 20 dB.

First synthesis to spec (`--gun-spec`, OpenFPS.AudioLab/Spikes/GunSpecSpike.cs): a Friedlander
pulse, a fast turbulent burst and a slower trail, one pole above 2.3-2.5 kHz. Matches 4-16 kHz within
a few dB; about 8 dB short at 1-2 kHz, partly the modelled ground comb at 20 m.

### The bubble model (2026-10-02)

`--gun-fit` (OpenFPS.AudioLab/Spikes/GunFitSpike.cs) measures every weapon against its own gun's
clean NIJ takes (90, 130, 180 degrees at 20 and 40 m) with the ruler in `ReportMeasure`, which
matches `inbox/gunfire-357-2026-10-02/nij.py`. `grid` searches a weapon's values.

- The blast is a second-order band-pass on the gas outflow. Critically damped it is the Friedlander
  pulse; the guns are fitted at damping 0.2-0.6, which makes the recordings' deep negative phase
  (0.4-1.0 of the peak) and their peak at 1 kHz.
- The turbulent burst goes through the same band-pass. The trail (the grown plume) does not.
- Two corners: the gas's eddies (1.2-4 kHz, per weapon) and the shock's rise (12 kHz).
- The separate high-pass is gone; the band-pass's lower skirt does that job.
- The shot is rendered at 48 kHz. It was 44.1 and played at 48, 9 per cent fast.
- Every report carries the same energy (`WeaponSynth.ReportEnergyDb`, -39 dB re 1 s full scale),
  the most the sharpest report can carry under full scale. A clap carries -26.5.

Band error 125 Hz-8 kHz, dB rms, before and after: Glock 8.1 to 1.6, .45 6.7 to 1.9, AR-15 6.6 to
1.4, AK 4.4 to 1.9; the new .357 1.3. 16 kHz renders 5-7 dB under the recordings.

Per-position errors are larger (2-6 dB) because the ground bounce assumed in `Propagate` (both 1.5 m
up, coefficient 0.8) puts notches at 500 Hz (20 m) and 2 kHz (40 m) that the recordings do not show.
The NIJ heights are not published.

The .357 (`revolver357`): 410 m/s, 164 dB, a cylinder-gap blast at -10 dB leading the muzzle by
0.46 ms. It falls 10 dB in 1.22 ms against the Glock's 0.88 (recorded 1.13 and 0.80, pooled). The
gap level is the lab demo's guess; the recordings show no separate early zero crossing.

## Plan

Status 2026-10-05: steps 1-4 done for the report; the crack, strikes, ricochets and handling are in
the game. Still open from step 5: the shotgun has no reference, casings do not land.

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

## The crack

Built (`BulletFlyby`), as below. Kept as the design note.

A supersonic round drags a Mach cone. Downrange, a listener near the path hears the crack before
the report: a sharp N-wave that arrives from the point on the trajectory where the cone met them,
not from the shooter. That direction and the crack-to-report gap tell a listener where the shooter
is and roughly how far (`Ballistics.CrackToReportSeconds`).

To build it, once the protocol carries a shot's origin and direction:
- per listener, find the emission point on the path (where the cone's angle, asin(c/v), meets
  them) and the arrival time;
- the N-wave's peak overpressure and duration from Whitham's law: peak falls as the miss
  distance to the -3/4 power and the duration grows as its 1/4 power, both scaled by the
  bullet's diameter, length and Mach number;
- play it as a placed one-shot at the emission point with the normal reflections and reverb, so
  the crack echoes off nearby walls the way it does in the NIJ recordings;
- check the arrival times and the crack-report gap against the NIJ set (30 degrees at 40 m: M16
  21 ms, AK 15.5 ms).

## Rounds are flown

Every round, from the hip or through a scope, is flown: along the aim from the body's own axis at the
height the gun is held, so a wall between you and your muzzle is the first thing it meets, and what it
hits is said when it gets there (FlyBullets). It used to be a hit-scan: the nearest entity by its centre
inside a flat 14-degree cone, a body taking the hit and a solid stopping it. A long wall's centre is
metres away along it, so from the second floor of Brandt Court the floor's own east wall (88 m long,
its centre 40 m off) was never in the cone, and the pedestrian below in the street was (Cody,
2026-10-04). The aim assist's half-angle, 14 degrees (a dot of 0.97), is that old cone's, so close in
it forgives what the hit-scan forgave.

Order of a shot: the report (a gunshot is a blast wave, a body resonance, a brightness sweep and the
action working, so it is named, not described by a few numbers); then aim assistance if it is on
(/aimassist, on by default), which turns the gun onto somebody near the aim and in plain view; then the
hip's own scatter; then the flight, so what the round does on the way is still the world's to decide.
