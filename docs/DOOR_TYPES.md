# Door types

A measured spec for door sounds, from 27 recordings. Written 2026-10-02.

**Status 2026-10-05.** The per-event recipes below (a contact into a bank of modes, fitted to the
recordings) were rendered on 2026-10-03 and rejected by Cody: "are we modeling the doors or modeling
the sound?" The round was reverted (88fe0de7). The game now uses a physical model of each door
instead: `KnobDoor`, `PushBarDoor`, `SlidingDoor`, `GlassDoor`, `ElevatorDoor` and `LockCylinder` in
`OpenFPS.Common`. Which model plays which event is in docs/DOOR_TYPES_EVENTS.md.

What is still current here: the recordings, the measurements and the "Test numbers" sections. They
are the yardstick the models are checked against (AudioLab `--door-models refs`, `--pushbar-vs-ref`,
`--patio-vs-ref`). The "Recipe" sections and "Order of work" are not current: do not build from them.

Seven door types: an interior hinged door, a steel fire door with a push bar, a glass storefront
door with a push bar and a lock, a pull-open glass box-store door, automatic sliding doors, a
manual patio slider, and elevator doors.

## Rules this spec follows

- The synth is dry. The game's room supplies the reflections and the reverb. Every number below
  is a direct-sound number, and the notes say where a recording's room got in the way.
- Solid parts are contact plus modes: a force pulse of a set length strikes a body, and the body
  rings at its own modes. Material comes from the modes (frequencies, Q, how many), never from a
  filter on noise. The house door fitted as octave-band noise was rejected on 2026-10-01: "too
  scratchy, grainy; wood and steel both sound the same".
- The opposite failure is known too. The first car door model, a few resonators, was rejected as
  "sounds like an instrument, too tonal" (`inbox/door sounds/synth-2026-09-28 rejected, too
  tonal`). So a leaf needs many modes at the measured prominence, not a handful of loud ones. Only
  small hardware (a latch, a bar, a frame tube, a chime) rings as a few clear tones.
- Noise is used only where the source is noise: rolling contact, motors, air. Friction (a latch
  bolt riding the strike, a hinge) is a train of tiny contacts that excite the same modes as the
  big hit, not a filtered noise burst.

## Recordings

27 Freesound recordings, mono 44.1 kHz, in `inbox/door-types-2026-10-02/real/` with a README that
gives each one's URL, author and licence. They are HQ previews (128 kbit/s MP3). Above about 16 kHz
the MP3 encoder cuts in and out, so nothing above 12 kHz is used as a target.

Freesound has good interior, fire door, patio and elevator recordings. It is thin on glass
storefront doors: there is no clean recording of an aluminium storefront door with a push bar, a
door closer and a key cylinder in one take. Type 3 is assembled from three recordings and the gap
is noted there.

## How it was measured

Tools are plain Python and sox, copied to `inbox/door-types-2026-10-02/tools/` (`ev2.py` events,
`evt.py` band balance at given times, `modes.py` peaks and T60, `d20.py`, `tail.py` room decay,
`dry.py` C50, `track.py` tone tracking, `segs.py` motion durations; `fs.py` and `get.py` search
and fetch Freesound). Run them from that folder on the files in `real/`. They do these things:

- Onsets: 2 ms RMS frames, an onset is a rise of 12-15 dB over the minimum of the last 30 ms.
- Band balance: octave bands 63 Hz to 16 kHz (sox `sinc`, long filters below 1 kHz so the 63 Hz
  band reads true), energy in the first 30 ms of an event minus the recording's own band floor.
  Given in dB relative to the event's loudest band.
- Level between events: peak 2 ms frame, dB relative to the main hit of the same recording.
- Decay: d20 = time for the 2 ms envelope (full band or one octave) to fall 20 dB from its peak.
- Modes: FFT of 120-200 ms from the onset, peaks at least 10 dB over the median of the 800 Hz
  around them. Each peak's T60 comes from a narrow band (f plus or minus f/40) fitted from -3 to
  -25 dB. Q = pi f T60 / 6.91.
- Room: early decay against late decay per octave (Schroeder), and C50 (energy 0-50 ms over
  50-400 ms) after the main hit.

Two cautions about the numbers:

- A mode's T60 can never be longer than the room's. When a mode's T60 is close to the room's late
  decay, the true T60 is shorter and only an upper bound is known. These are marked "room-limited".
- The band balance depends on where the microphone was. A close microphone at the latch hears the
  latch; one at the middle of the leaf hears the leaf. The ranges below come from more than one
  recording where possible.

## Physics used for the recipes

The recipes are not current (see the status at the top). The physics below still holds and the
models use it.

- A force pulse of length tau (raised cosine) has a flat spectrum up to about 0.5/tau and falls
  steeply above about 2/tau. So tau sets the brightness of a hit. Metal on metal: 0.05-0.15 ms.
  Wood on wood: 0.3-0.6 ms. Anything through a rubber silencer, felt or a gasket: 2-6 ms.
- A mode's decay: T60 = 2.2 / (f x eta), where eta is the loss factor (Q = 1/eta).
- A plate's mode count per hertz is constant: n = 1.73 x area / (thickness x c_L), with c_L the
  plate's longitudinal wave speed. A 40 mm wood leaf (c_L about 3000 m/s) has a mode every 40-45
  Hz; a 10 mm glass pane a mode every 15 Hz; a 1 mm steel skin one every 1-2 Hz. So a big thin leaf
  sounds dense and a small thick part sounds tonal. A synth can stop at 30-60 modes per body if
  the rest would be 15 dB or more down; what matters is that no handful of modes stands alone.
- Plate fundamental: f11 = 0.453 x c_L x h x (1/a^2 + 1/b^2), with a and b the panel sides and h its
  thickness. Glass: c_L = 5300 m/s. A 10 mm storefront pane 1.0 x 2.1 m gives f11 about 30 Hz and
  modes from there up, which is why glass doors have a strong low end (measured 75-135 Hz below).
- A bar or tube rings along its length at f_n = n x c / (2L): a harmonic series. Aluminium
  c = 5100 m/s, steel 5100 m/s, brass 3500 m/s.

Material table. The eta values are what the measurements below support; the ranges are wide on
purpose and a test should pick a value and keep it.

| Material | Leaf eta | Leaf modes start | Main hit d20, full band | Hardware rings |
|---|---|---|---|---|
| Wood, hollow or light core | 0.01-0.02 | 600-900 Hz cluster, many | 22-68 ms | knob/latch 10-11.5 kHz |
| Wood, solid or heavy | 0.02-0.04 | 70-90 Hz, then 1.2-1.6 kHz | 30-68 ms | as above |
| Hollow steel, honeycomb core | 0.02-0.03 low, 0.005 high | 100-210 Hz | 62-96 ms | bar/latch 5-14 kHz, Q 500-1300 |
| Glass in aluminium frame, gasket | 0.03-0.08 low | 75-200 Hz, strong | 46-64 ms | 1.5-3 kHz Q 130-280; 6-8.5 kHz Q 500-1100 |
| Aluminium frame or handle alone | 0.0005-0.001 | - | - | 6.7 kHz Q 1300; tube series f0 = c/2L |
| Stainless elevator leaf | 0.0005-0.002 | 170 Hz, 0.8-3 kHz rings | 380 ms (rings) | 0.8, 1.25, 3.0, 6.7 kHz, T60 0.75-3.2 s |

The "solid or heavy" wood row is the residential door recording; its low modes may be partly the
room. Glass d20 is from the kraft and vaztur slams before their rattle.

## Type 1. Interior hinged door, knob or lever

### Recordings

| File | What | Quality |
|---|---|---|
| `1-interior-kyles-light-wood-knob-latch` | light wood door, knob, latch, deadbolt, many takes | best. C50 13-14 dB, -30 dB in 130 ms. Room late decay 0.35-0.45 s |
| `1-interior-kyles-residential-knob-spring` | residential door, knob, thud, spring | good. Room longer, C50 0-2 dB full band, 8-18 dB above 500 Hz |
| `1-interior-matucha-apartment-soft` | apartment door, two Neumann KM76 close | HF good. Below 200 Hz it is mostly air from the leaf hitting the microphones (50-100 Hz, 20-30 dB over everything above 1 kHz). Do not fit the low end to it |
| `1-interior-sudd-lever-turn` | modern lever handle turned and let go, isolated | good for the handle only |

### Events in order

Opening:

1. Handle or knob turned: the latch bolt is pulled back against its spring. Sudd's lever: a
   cluster of small contacts over 80-100 ms (the cam sliding, the bolt moving). Level within 2 dB
   of the release snap. Bands: peak at 2-4 kHz, 8 kHz -2 dB, 1 kHz -8 dB, 500 Hz -19 dB.
   Modes in the cluster: 4.6, 5.6, 7.9, 10.6 kHz (Q 400-1200).
2. Handle reaches its end stop: one click 270 ms after the turn began, -8 dB re the release snap.
3. Leaf swings. Silent unless the hinge creaks. A creak is stick-slip: a harmonic tone that wanders
   (LampEight's theatre door: f0 about 2.3 kHz with harmonics to 9 kHz, gliding by up to 30 %).
   Treat a creak as optional and rare on interior doors.
4. Handle let go: the bolt snaps out under its spring. Sudd: 590 ms after the turn began. Bands:
   peak 2-8 kHz (2 kHz -2, 4 kHz 0, 8 kHz -1, 16 kHz -7), 500 Hz -18. Modes 2.38-2.45 kHz (Q
   100-170, T60 100-150 ms), 2.85, 3.9 kHz. A second smaller click 80 ms later (-7 dB): the
   handle's own spring stop.

Closing:

5. The latch bolt meets the strike plate lip and rides over its ramp. This is friction, measured
   as a level shelf before the main hit: 30-60 ms long at -21 to -28 dB re the main hit (kyles:
   50 ms at -25 to -30 dB, and 30 ms at -28 dB). Bands flat 250 Hz-2 kHz, 4 kHz -3, 8 kHz -7.
6. Leaf hits the stop and the bolt drops into the strike hole. The main hit. On the latch-side
   microphone (kyles light door) the bands are: 63 Hz -31 to -40, 125 -23 to -26, 250 -19 to -24,
   500 -10 to -13, 1 kHz -3 to -4, 2 kHz 0 to -7, 4 kHz 0, 8 kHz -2 to -9, 16 kHz -7 to -11. The
   residential door with the microphone further off: flat within 12 dB from 63 Hz to 8 kHz, peak at
   1 kHz. Use the second for the leaf and the first for the latch.
7. The bolt and knob settle: the envelope after the hit is not smooth but has bumps of 3-6 dB
   for 20-40 ms, at -10 to -16 dB re the hit.

### Measured modes and decays (main close hit)

- Leaf (kyles light wood): 646, 738, 851, 899, 1346-1378, 2304 Hz. Prominence 11-14 dB. Q 100-220,
  T60 260-760 ms, but the room's late decay is 350-450 ms, so these are room-limited; the true leaf
  T60 is shorter.
- Leaf (residential): 70, 92 Hz (Q 40-50), 1217, 1572, 1642 Hz (Q 160-190, T60 210-300 ms).
- Knob and latch hardware: 10.03, 10.32-10.37, 11.27-11.41 kHz. Prominence 14-27 dB, -7 to -12 dB
  below the strongest leaf peak. T60 300-1050 ms (Q 1300-5400). This ring is what says "metal
  knob on a wooden door".
- d20, full band: 22-58 ms (light door, four closes), 30-68 ms (residential, three). Per octave,
  both doors: 500 Hz 22-88 ms, 1 kHz 18-68, 2 kHz 18-46, 4 kHz 8-40, 8 kHz 10-44.

### Recipe

- Turn (event 1): a train of 6-12 micro-contacts over 80-100 ms, tau 0.05-0.1 ms, random spacing
  5-20 ms, each exciting the latch hardware modes below. Overall level within 2 dB of the
  release snap.
- Release snap (event 4): one contact, tau 0.05-0.1 ms, into the latch modes: 2.4 kHz (Q 150), 2.85
  kHz (Q 200), 3.9 kHz (Q 350), plus the knob modes at 10.3 and 11.4 kHz (Q 2000, -10 dB). Second
  click 80 ms later at -7 dB.
- Ride (event 5): 30-60 ms of micro-contacts (one every 0.5-2 ms, random), tau 0.05 ms, level
  ramping from -30 to -22 dB re the main hit, exciting the same latch modes.
- Main hit (event 6): two contacts within 0-5 ms.
  - Leaf on stop: tau 0.3-0.6 ms, into a leaf mode bank. Light wood: 20-30 modes from 300 Hz to 6
    kHz, density rising with frequency, first cluster 600-900 Hz, eta 0.01-0.02 (so T60 at 700 Hz
    is 150-300 ms). No single mode more than 14 dB over its neighbours.
  - Bolt into strike: tau 0.05-0.1 ms, into the latch and knob modes, -3 to -6 dB re the leaf
    contact.
- Settling (event 7): 3-8 contacts in 20-40 ms, -10 to -16 dB, latch modes only.
- Size: leaf mode frequencies scale as thickness / (width x height). A heavier (solid core) door
  lowers the first cluster to 150-400 Hz and raises eta to 0.02-0.04.

### Test numbers for a render

- Ride to main hit: 30-60 ms, ride at -21 to -30 dB.
- Main hit band balance: within 4 dB of the residential row (1 kHz peak, 63 Hz to 8 kHz within 12
  dB) at 1 m from the leaf centre; within 4 dB of the light-wood row at 0.3 m from the latch.
- Main hit d20 full band 20-70 ms; 4 kHz octave 8-40 ms. Knob ring at 10-11.5 kHz still visible in a
  spectrum 150 ms after the hit.
- Release snap 2-8 kHz peak, 500 Hz at least 15 dB down.

## Type 2. Fire or stair door, steel, push bar (panic bar)

### Recordings

| File | What | Quality |
|---|---|---|
| `2-firedoor-kyles-institutional-pushbar` | institutional steel door, push bar, open and close | close and clear, but an edited take in a live corridor: late decay 0.6-0.9 s, C50 about 0 dB. Event order and band balance only |
| `2-firedoor-berumen-fire-exit-latch` | fire exit door, several open/close/latch takes | best steel slams. Room late decay 0.7-0.87 s, early decay usable, C50 4-7 dB |
| `2-firedoor-klangfabrik-stairwell-panicbar` | stairwell door, several pushes on the bar | stairwell reverb of seconds. Timing only |

### Events in order

1. Bar pushed: the bar's pivots and the latch rods move, the latch bolt retracts. kyles: 3
   contacts over 70 ms (at 70, 96 and 142 ms from the file start), -8, -7 and -1 dB re the slam.
   The first two: peak 4 kHz, 8 kHz -1 to -4, 2 kHz -5 to -9, 1 kHz -6 to -10, 250 Hz -8 to -9, 63
   Hz -17 to -23. The last and loudest (the bolt reaching its stop) peaks at 1 kHz: a 1.44 kHz ring
   (Q 110), 2-8 kHz -6 to -7. Rings at 9.6-15 kHz (Q 750-2000).
2. Bar let go as the door swings: a return clack, 240-320 ms after the press, -5 dB. Peak 4 kHz,
   2 kHz -2, 500 Hz -11. Rings at 1.64 kHz (Q 170, T60 220 ms) and 8.9-9.1 kHz (Q 300-400).
3. Swing. The closer is a sealed hydraulic damper; the recordings show no closer sound worth
   modelling on a steel door. Closing time from release to the slam, inferred from the stairwell
   take: 3.7-3.9 s. ADA 404.2.8.1 asks for at least 5 s from 90 degrees to 12 degrees; real doors
   run 3-7 s. A closer has two valves: the sweep speed down to 10-15 degrees, then the latch speed
   for the rest, usually set faster so the leaf pushes the bolt over the strike.
4. Latch bolt rides the strike: 60-140 ms before the main hit, 15-24 dB below it (berumen). Bands
   with a 4 kHz peak, the rest 6-15 dB down. In the kyles take a contact 140 ms before the slam is
   only 3 dB down: the leaf meeting the silencers before the latch.
5. Slam: leaf onto the steel frame stop (three rubber silencers), bolt into the strike. Bands,
   six slams (berumen, stairwell): flat from 63 Hz to 8 kHz within 12 dB. 63 Hz 0 to -4, 125 Hz -5
   to -10, 250 -6 to -12, 500 -3 to -8, 1 kHz -2 to -8, 2 kHz -2 to -8, 4 kHz -2 to -8, 8 kHz 0 to
   -3, 16 kHz -5 to -10. The kyles door (in its corridor, microphone near the latch) peaks at 2 kHz
   with 63-250 Hz 16-20 dB down. The flat, wide balance against wood's 1-4 kHz hump is the first
   thing that separates steel from wood.
6. Rattle: the bar, the rods and the bolt shake in their guides. Irregular contacts every 8-30 ms,
   the envelope falling about 20 dB in 300-500 ms (kyles, the stairwell). The rattle's bands sit
   lower than the slam: 63-250 Hz 0 to -5, 1 kHz -9, 4 kHz -11, 8 kHz -21.

### Measured modes and decays (slam)

- Leaf low modes: 129 Hz in every berumen slam (prominence 15-16 dB, Q 27-45), with 108, 145,
  172, 183, 205 Hz. kyles door: 102, 151, 188, 253, 458 Hz. Room-limited T60 0.45-1 s, so take the
  leaf's eta from the early decay instead: about 0.02-0.03.
- Mid: 565-807 Hz (Q 70-500), 1.32-1.45 kHz (Q 80-370, T60 under 570 ms).
- Hardware rings: 5.16-5.25 kHz (Q 470-650), 7.56, 8.35-8.46 kHz (Q 600-720), 10.5-14.5 kHz (Q
  200-1300). Each -20 to -38 dB below the leaf peak, T60 40-275 ms.
- d20 full band 62-96 ms (five slams). Per octave 500 Hz 18-60 ms, 1 kHz 38-90, 2 kHz 28-68, 4 kHz
  40-50, 8 kHz 26-64. Longer than wood in the full band (wood 22-68 ms) and at 4 kHz (wood 8-40
  ms); not at 500 Hz-1 kHz, where wood's own leaf modes ring.

### Recipe

- Bar press: 3-4 contacts spread over 70-140 ms, tau 0.05-0.1 ms, last one loudest. Excite a bar
  mode set: 1.44 kHz (Q 110), 2.75 kHz (Q 400), 3.9 kHz (Q 260), 4.66 kHz, 9.6 kHz (Q 2000), 15 kHz
  (Q 800). Also a weak leaf excitation (tau 1 ms, -15 dB) since the bar is bolted to the leaf.
- Bar return: one contact, tau 0.1 ms, modes 1.64 kHz (Q 170) and 8.9-9.1 kHz (Q 350), -5 dB.
- Swing: silent. Closer timing as in event 3, from the door's opening angle.
- Ride: 60-140 ms of micro-contacts at -15 to -24 dB, latch modes.
- Slam: two contacts.
  - Leaf through silencers: tau 2-4 ms (this gives the 63-250 Hz weight), into a steel leaf bank:
    first mode 100-210 Hz, 25-40 modes to 8 kHz, eta 0.02-0.03 below 500 Hz and 0.005 above (the
    skins ring longer than the core damps).
  - Bolt and frame, steel on steel: tau 0.05 ms, into frame and latch modes 5.2, 8.4, 10.5, 12.3 kHz
    (Q 500-1300), 0 to -3 dB re the leaf contact. This contact makes the 8 kHz bump.
- Rattle: 15-40 contacts over 300-500 ms, random spacing 8-30 ms, levels falling 20 dB, tau
  0.1-0.3 ms, into the bar modes plus a weak leaf excitation.
- Size: a double door has two leaves and two rattles, offset 50-300 ms when both close.

### Test numbers

- Bar press to bar return 240-320 ms. Press contacts at -1 to -8 dB re slam, the first ones
  peaking at 4 kHz.
- Slam bands: all of 63 Hz-8 kHz within 12 dB of each other; 8 kHz within 3 dB of the loudest band.
- A mode within 100-210 Hz at least 12 dB over its neighbours.
- d20 full band 60-100 ms; 4 kHz octave 40-50 ms.
- Rattle present for 300-500 ms, contacts 8-30 ms apart.

## Type 3. Glass storefront or apartment building front door, push bar, key cylinder

Aluminium stile frame, glass in gaskets, a push bar or push pad, a surface or concealed closer,
often a mortise lock with a key cylinder.

### Recordings

| File | What | Quality |
|---|---|---|
| `3-storefront-vaztur-iron-glass-slam-lock` | glass door in an iron frame: slam, then the lock | the best glass-and-metal slam and lock. Zoom iQ5 (a phone-mounted microphone), close. The rattle after the slam spoils C50 (-4 dB); the slam itself is dry enough |
| `3-storefront-kyles-commercial-spring-door` | commercial metal spring door, recorded outside | good slams, no glass |
| `3-storefront-fossarts-thumbturn-deadbolt` | exterior door deadbolt thrown by a thumb turn | clean lock throw; stands in for the key cylinder |

Not found: an aluminium storefront door with push bar, closer and key cylinder in one take. The
glass behaviour (low panel modes, frame rings, rattle) comes from type 4's recordings, which are the
same construction.

### Events in order

1. Bar pushed: as type 2, events 1 and 2. On a storefront door the bar is lighter and is screwed
   to an aluminium stile, so the bar modes are higher and ring longer (use the aluminium frame
   rings below). No direct measurement.
2. Swing: closer as type 2, event 3. Gaskets and brush seals give a soft sweep as the leaf passes
   the frame (see type 4, event 3).
3. Ride: 64 ms at -31 dB before the slam (vaztur).
4. Slam: bands rise toward the top: 63 Hz -16, 125 -17 to -18, 250 -11 to -15, 500 -9 to -12, 1 kHz
   -7 to -9, 2 kHz -4 to -5, 4 kHz 0 to -2, 8 kHz 0, 16 kHz -10 to -15 (two vaztur slams).
   Brighter than steel, far brighter than wood.
5. Glass rattle: the pane shifts in its gasket and the leaf rocks on the latch. Irregular contacts
   every 8-30 ms for 0.8-0.9 s, the envelope falling from -2 dB to -30 dB. The first rattle hit
   can be as loud as the slam (-2 dB at +90 ms).
6. Key cylinder and lock. A key turn is cam contacts plus the bolt throw (FOSSarts' thumb turn: a
   small contact 62 ms before the throw at -17 dB, then the throw at 0 dB). Throw bands: 1-8 kHz
   within 5 dB with the peak at 8 kHz, 125-500 Hz -10 to -11, 63 Hz -6. vaztur's lock: a throw at
   -3 dB re its slam with rings at 5.35 kHz (Q 180), 6.8 kHz, 8.55-8.73 kHz (Q 180-500, the 8.6
   kHz ring lasting about 200 ms), and a second click 1.1 s later at -14 dB.

### Measured modes (vaztur slam)

- 576 and 673 Hz with long T60 (1.7-2.9 s, Q 530-770). Too long for glass in a gasket; this is the
  iron frame's tube ringing. On aluminium expect similar tube modes.
- 2.76 kHz (Q 1000), 3.84 kHz (Q 580), 7.0 and 9.37 kHz (Q 260-470), 11.7-13.6 kHz.
- Lock throw: 1.53 kHz (Q 230), 3.58 kHz, 5.35 kHz, 6.81 kHz, 8.55-8.73 kHz.

### Recipe

- Bar press and return: type 2's recipe with the bar modes raised 1.3x and Q doubled (aluminium
  bar on an aluminium stile).
- Slam: three contacts within 0-5 ms.
  - Leaf through the gasket and silencers: tau 2-4 ms, into a glass panel bank (see type 4: strong
    modes at 75-200 Hz, eta 0.03-0.08).
  - Stile on frame, aluminium on aluminium: tau 0.05 ms, into frame tube modes (series f_n = n x
    5100 / 2L for each stile and rail length; a 2.1 m stile gives 1.21 kHz, 2.43 kHz, ...), Q
    500-1500.
  - Bolt into strike: tau 0.05 ms, latch modes 2.7-3.9 kHz and 7-9.4 kHz.
- Rattle: 30-60 contacts over 0.8-0.9 s, random spacing 8-30 ms, falling 28 dB, each a weak glass
  contact (tau 0.2-0.5 ms, glass HF modes 1.7-3 kHz and 6-8.5 kHz). This is the sound that says
  "glass door". Keep it.
- Key: 2-5 cam contacts over 100-300 ms (tau 0.05 ms, -15 to -20 dB), then the throw (tau 0.05
  ms, lock modes 5.35, 6.8, 8.6 kHz, Q 200-500, the 8.6 kHz one longest).

### Test numbers

- Slam: 4-8 kHz loudest, 1 kHz 7-9 dB down, 63-250 Hz 11-18 dB down.
- Rattle 0.8-0.9 s long, contacts 8-30 ms apart, first rattle within 3 dB of the slam.
- Lock throw: 8 kHz the loudest band, 1-8 kHz within 5 dB, d20 full band under 60 ms.

## Type 4. Pull-open glass and aluminium box-store door with a closer

No latch, or a quiet roller catch. The door is pulled open, let go, and the closer brings it back.

### Recordings

| File | What | Quality |
|---|---|---|
| `4-pullglass-kraftaggregat-glass-closing` | glass door, several closings, Zoom H6 | best glass closes. Room late decay 0.23-0.35 s, C50 above 500 Hz 13-16 dB |
| `4-pullglass-kraftaggregat-glass-opening` | the same door opening | handle and latch contacts |
| `4-pullglass-ifm185-library-glass-aluminium-closer` | library glass doors in aluminium with a closer, rocking shut | shows the closer's rocking bounces. Library reverb about 1 s; use timing and level, not decay |
| `4-pullglass-lampeight-heavy-glass-metal-slow` | heavy glass and metal theatre door, slow close, creaks, seal | shows the seal sweep, the shut and a creak. Theatre reverb; timing and band balance only |
| `4-pullglass-latranz-aluminium` | aluminium door, phone recording | no bass at all (phone). Only its 6.7 kHz frame ring is used |

### Events in order

1. Pull: a handle contact or a quiet catch release, -5 to -13 dB re the close (kraft, ifm). ifm's
   push click has long rings: 759 Hz (Q 90), 2.13 kHz (Q 150), 7.6 kHz (Q 250), 11.0-11.3 kHz (Q
   600-900), lasting about 400 ms. That ring is the glass and the aluminium stile together.
2. Swing back under the closer: 3-7 s (same law as type 2, event 3). A heavy glass door is slower.
3. Seal sweep: the brush or rubber seal wipes the frame in the last 0.5-0.7 s. LampEight: a soft
   noise in the 4 and 8 kHz octaves (4 kHz 9.6 dB over the room noise, 8 kHz 14.8 dB over it). Its
   total level is about 30 dB below the shut's, but in the 4 and 8 kHz octaves it is about as loud
   as the shut (this heavy door's shut has almost no top). This is the only noise in a pull door.
4. Shut: the leaf meets the stop. Band balance depends on the door's weight:
   - Light glass door (kraft): 1-8 kHz within 2 dB, 500 Hz -9, 63-250 Hz -10 to -17. Or, in
     another take, 125 Hz the loudest and 1-4 kHz 9-12 dB down.
   - Heavy glass and metal (LampEight): 63-250 Hz loudest, 500 Hz-1 kHz -9, 2 kHz and up 24-36 dB
     down. Its HF is lost in the theatre, so use it for the low end only.
   - Low panel modes, both kraft takes: 75-81 Hz (prominence 21-24 dB, Q 13-19, T60 0.39-0.51 s),
     102 Hz, 135-156 Hz (Q 35-36, T60 0.49-0.6 s), 194 Hz. These are the glass panel's first modes;
     the 10 mm pane formula gives 30-100 Hz for f11 to f21.
   - Higher: 1.49-1.83 kHz (Q 130-220, T60 160-330 ms), 2.07-2.35 kHz (Q 250), 3.08 kHz (Q 420),
     5.9-6.0 kHz (Q 500-560), 8.5 kHz (Q 1100).
5. Rocking bounces (double doors on one closer, ifm): about 10 contacts over 1.5 s at growing
   spacing (+176, +270, +494, +610, +870, +1160, +1510 ms after the shut), levels from -18 to -39
   dB. Bands peak at 1 kHz.
6. Optional latch on a heavy door (LampEight): a click 2.7 s after the shut, with a ring at
   1.72-1.80 kHz (Q 70-85, T60 80-110 ms).

### Recipe

- Pull: one contact, tau 0.1 ms, into a glass-and-stile set: 760 Hz (Q 90), 2.13 kHz (Q 150), 7.6
  kHz (Q 250), 11 kHz (Q 700). -8 dB re the shut.
- Seal sweep: here noise is right. Noise band-passed to the 4 and 8 kHz octaves, 0.5-0.7 s, level
  rising with the leaf's edge speed, ending at the shut, total level 30 dB below the shut.
- Shut: two contacts.
  - Leaf through the seal: tau 2-5 ms (light door 2 ms, heavy 5 ms), into a glass panel bank:
    modes from the plate formula for the pane (first at 30-80 Hz), 30-50 modes to 8 kHz, eta 0.05 at
    75 Hz falling to 0.003 above 5 kHz. The first three modes in 75-160 Hz must stand 20 dB or more
    over the median: they are the glass "thunk".
  - Frame stop: tau 0.1 ms, into aluminium modes (the 1.5-3 kHz and 6-8.5 kHz sets above, plus the
    6.7 kHz ring, Q 1300, at -10 dB).
- Bounces: as event 5, each a scaled copy of the shut with tau 3 ms and the frame contact dropped.

### Test numbers

- Shut: a low mode within 70-160 Hz at least 20 dB over its neighbours, T60 0.35-0.6 s.
- Light door shut: 1-8 kHz within 3 dB of each other; 500 Hz 8-10 dB down.
- Seal sweep present only in the 4-8 kHz octaves, total level 25-35 dB below the shut, 0.5-0.7 s,
  ending at the shut.
- Double door: bounces at growing spacing for 1-1.6 s, the first 15-20 dB below the shut.

## Type 5. Automatic sliding doors (supermarket, box store)

### Recordings

| File | What | Quality |
|---|---|---|
| `5-auto-snailham-retail-sliding` | retail entrance sliding door, open, close, then store tone | best. Motor tone clear. Licence CC BY-NC (reference only) |
| `5-auto-klangfabrik-sliding` | four movements | noise-reduced, with the bass re-synthesised by the uploader. Timing only |
| `5-auto-egrow-library-opening` / `-closing` | library sliding doors outside | clean, no motor tone heard; rolling noise only. CC BY-NC |

### Events in order

1. Start: the motor engages and the belt takes up. One soft contact (Snailham at 0.09 s), 4-6 dB
   over the run noise that follows, loudest at 250 Hz and 1 kHz.
2. Accelerate: a gear or motor tone rises with speed. Opening: from about 1.0 kHz to 1.7-2.0 kHz in
   0.25-0.3 s. Closing: from 0.9 kHz to 1.4-1.6 kHz in 0.6 s. The door opens faster than it closes.
3. Run: the tone holds at 1.35-2.0 kHz with wander, over the rolling noise of the carriage wheels
   on the track and the belt. Run noise bands (Snailham, opening): 125 Hz -12, 250 -4, 500 -3, 1 kHz
   0, 2 kHz -8, 4 kHz -12, 8 kHz -13. Closing has the 2 kHz octave within 1 dB of 1 kHz (the tone
   sits there). egrow (no tone): flat from 500 Hz to 8 kHz within 3 dB. The run is 14-19 dB over
   the shop's own noise. The tonal lines stand 10-15 dB over the noise beside them: one near 700 Hz
   that stays put at any speed (opening and closing), and others between 1.1 and 2.8 kHz that move
   with speed.
4. Brake: in the last 150-250 ms the tone falls from 1.3-1.5 kHz to 0.6-0.85 kHz.
5. Stop: a bump into the rubber stop at open and at close, 4-7 dB over the run noise (Snailham),
   10-15 dB (klangfabrik). Snailham's stops peak at 1-2 kHz with 4-8 kHz 19-24 dB down;
   klangfabrik's are flat from 500 Hz to 8 kHz. A second hit follows each stop: 0.45 s after the
   open stop at the same level (the leaf settling or the second leaf), and 0.5 s after the close
   stop (the lock, 15 dB over the quiet after the run).

Durations (start to stop): Snailham open 1.74 s, close 2.55 s; klangfabrik 3.45-4.05 s; egrow open
3.15 s, close 5.9 s. A bi-parting leaf is about 0.9 m, so the average speed is 0.15-0.5 m/s.

### Recipe

- The leaf moves on a speed curve v(t): a 0.3 s (open) or 0.6 s (close) ramp, a steady run, a
  0.15-0.25 s brake, a stop. Pick a run speed of 0.3-0.5 m/s opening and 60-70 % of that closing.
- Motor tone: a sawtooth-like tone with harmonics 2 and 3 at -10 to -15 dB, frequency
  proportional to v(t), 1.7-2.0 kHz at the opening run speed, 10-15 dB over the noise beside it.
  Its level follows v(t). A second, fixed line near 700 Hz at the same prominence while the motor
  runs.
- Rolling noise (noise is right here): wheels on the track and the belt. Noise shaped to the run
  band balance above, level proportional to v(t)^2 (from 0 at rest to full at the run speed).
- Start: one contact, tau 3 ms (rubber belt), into the leaf glass bank, 4-6 dB over the run noise.
- Stop: one contact through the rubber stop, tau 2-6 ms, into the leaf glass bank and the
  aluminium carriage, 4-15 dB over the run noise. A second contact 0.45-0.5 s later: at open the
  leaf settling, at close the lock (tau 0.1 ms, steel modes 3-6 kHz).
- Size: two leaves bi-parting is two of everything with 0-30 ms offset, both tones within 2 %.

### Test numbers

- Opening shorter than closing (ratio 0.5-0.75).
- Tone rising from 0.9-1.1 kHz to 1.4-2.0 kHz during the ramp, falling to 0.6-0.85 kHz in the last
  150-250 ms.
- Run noise loudest at 500 Hz-1 kHz, 4-8 kHz 10-13 dB down.
- Stop bump 4-15 dB over the run, then a second hit 0.45-0.5 s later.

## Type 6. Manual patio sliding glass door

### Recordings

| File | What | Quality |
|---|---|---|
| `6-patio-kijjaz-slide-glass` | sliding glass door, open and close, Rode iXY | best slide noise. A live room: C50 0 dB full band, -30 dB after 0.5 s. The hit decays are room-limited |
| `6-patio-goblinjack-big-smooth` | big smooth slider, open and close several times | good. C50 5-13 dB |
| `6-patio-jpkweli-shut` | a slide and a shut | good rolling with irregular ticks |
| `6-patio-applecorey-latch` | the latch (hook) on a sliding glass door | clean, shows a harmonic ring |

### Events in order

1. Unlatch: the hook lever is flipped (corey, second click: -5 dB re the latch, flat 125 Hz-8 kHz).
2. Slide: rollers on the bottom track. A hand-pushed slide lasts 1.25-4 s (goblin 1.25-2.0 s,
   jpkweli 2.4 s, kijjaz 3.6-4.0 s). Rolling noise bands:
   - Healthy rollers (goblin): 250-500 Hz loudest, 1 kHz -4, 2 kHz -13, 4 kHz -19, 8 kHz -20.
   - Older or dirtier track (kijjaz): 2 kHz loudest, 1 kHz -3, 500 Hz -6, 4 kHz -7, 250 Hz -11,
     8 kHz -12.
   - jpkweli: 125 Hz and 1 kHz loudest, 2 kHz -8, 4 kHz -15.
   The rolling noise has small ticks on top (grit, the track joints). They are irregular; no steady
   wheel period was found (a 25-38 mm roller at 0.5 m/s turns 4-6 times a second, and only kijjaz
   shows a weak 170 ms repeat).
3. Stop at full open: the leaf hits the end stop. kijjaz: as loud as the closing hit (+2 dB), 125
   Hz-250 Hz loudest, 500 Hz-4 kHz -6 to -8.
4. Close hit: the leaf meets the jamb with its interlock and weatherstrip. kijjaz: 125-250 Hz 0 to
   -2, 500 Hz-1 kHz -9 to -10, 2 kHz -1, 4 kHz -8, 8 kHz -22. goblin: 250 Hz peak, 125 Hz -9, 500 Hz
   -3, 1 kHz -10, 2 kHz -17, 8 kHz -21.
5. Bounce: the leaf rebounds off the jamb and settles, 0.45 s later at -24 dB (kijjaz), mostly
   125-250 Hz.
6. Latch: the hook engages. corey: one contact with a harmonic ring, f0 2.08-2.12 kHz and
   partials at 4.20, 6.31, 8.37, 10.43 kHz, i.e. exactly n x 2.09 kHz. Partial levels 0, -15, -20,
   -23, -34 dB. T60 155 ms on the fundamental, 530-770 ms on the upper partials. A harmonic series
   with long upper partials is a bar ringing along its length. f0 = 5100 / 2L gives L = 1.22 m for
   aluminium: the lock stile or the hook rod. The 2 kHz octave is 15-20 dB above all others in this
   event.

### Measured modes

- Close hit: 102-237 Hz cluster (124, 161, 183, 215, 237 Hz; Q 74-105), 2.55 kHz (Q 360), 6.7-7.2
  kHz (Q 2300-2500, -38 dB).
- Open stop: 194 and 226 Hz (prominence 25-27 dB, Q 41-69), 345 Hz, 2.5-2.6 kHz (Q 380-410).
- goblin close: 167, 221 (prominence 20 dB, Q 32), 258, 301 Hz, then rings above 6 kHz at -45 dB
  or lower.
- d20: kijjaz close hit 36-68 ms per octave (500 Hz-8 kHz, room-limited), goblin 22-40 ms.

### Recipe

- Slide: noise is right for the rolling. Shape it to one of the three band sets above (pick by the
  door's state), level proportional to v^2 for the leaf speed v. A slide is a hand push: speed
  rises over 0.2-0.4 s, holds, and falls only at the end. Add ticks: Poisson contacts at 2-8 per
  second, tau 0.1-0.3 ms, into the leaf glass bank at -6 to -12 dB re the rolling.
- Leaf glass bank: a pane 0.9 x 2.0 m, 5-6 mm tempered or 2 x 4 mm insulated glass, in an aluminium
  sash. Low modes from about 100 Hz with 4-6 strong ones in 100-250 Hz (prominence 15-27 dB), eta
  0.03-0.05 there. Aluminium sash rings at 2.5 kHz and 6.7-7.2 kHz, Q 400-2500, -20 to -38 dB.
- Stops: one contact into the leaf bank and the sash, tau 2-4 ms (weatherstrip and interlock),
  level set by the leaf speed at impact (v^1 for amplitude).
- Bounce: a second contact 0.3-0.5 s later, -20 to -26 dB, tau 4 ms, leaf bank only.
- Latch: one contact, tau 0.05 ms, into a bar series f_n = n x c / 2L, n = 1-5, partial levels 0,
  -15, -20, -23, -34 dB, T60 rising with n from 0.15 s to 0.75 s. Lever flip before it: one contact,
  -5 dB, flat.

### Test numbers

- Rolling: loudest octave 250 Hz-2 kHz depending on the chosen set; level rising and falling with
  the leaf speed, silent at rest.
- Close hit: 125-250 Hz the loudest, 8 kHz at least 20 dB down. A mode cluster in 100-250 Hz.
- Latch: partials within 1 % of exact harmonics of f0; the 2 kHz octave at least 15 dB above
  every other octave in the first 30 ms.

## Type 7. Elevator doors (automatic, bi-parting, steel)

### Recordings

| File | What | Quality |
|---|---|---|
| `7-elevator-krystianpawlowski-doors` | doors opening and closing, a pair of AKG C391B close | best motion. The motion itself is long, so C50 says nothing about the room here |
| `7-elevator-launchsite-open-close` | one open and one close, hall side | good. Shows the close clunk and the long steel ring |
| `7-elevator-magedu-inside-cabin` | from inside the car | strong; a small steel box adds its own low end. Close bump and rebounds |
| `7-elevator-mredig-open-chime` | arrival chime and door opening | the chime |

### Events in order (one cycle)

1. Arrival chime (mredig): two electronic tones, 1055 Hz then 894 Hz (a falling minor third,
   ratio 1.18), each about 0.55 s. Harmonic partials: first tone 2nd -26 dB, 3rd -39 dB; second tone
   2nd -17, 3rd -23, 4th -33, 5th -27 dB. A bell-like decay, T60 0.8-1.4 s. Krystian's car beeps
   instead: 2331 Hz, two beeps 150 ms apart, harmonics 2nd -10, 3rd -7, 4th -24 dB. The rule for
   hall signals (ADA 407.2.2.2, ASME A17.1) is one sound for up and two for down.
2. Clutch: the door operator's vane (on the car door) grips the rollers of the landing door lock and
   lifts the interlock. A low clunk before the doors move. launchsite: 125 Hz the loudest band by
   10-16 dB, a 135-156 Hz ring (Q 60-70, T60 about 1 s, room-limited). Krystian: 500 Hz loudest, a
   burst at 3-6 kHz. -1 to -11 dB re the close bump.
3. Opening motion: the doors roll on the hanger track (sheaves on a steel track) driven by the
   operator motor. Krystian: 2.5 s, launchsite 4.8 s (with dwell), magedu 5.3 s. The level rises
   for about 1 s (accelerating), holds, and falls over the last 0.8-1 s (slowing to the stop).
   Bands of the motion: 125-500 Hz loudest, 1 kHz -3 to -10, 2 kHz -13 to -16, 4-8 kHz -16 to -19
   (Krystian, magedu). launchsite (hall side) has motor tones at about 250 Hz, 700 Hz and 1 kHz,
   and its loudest band is 500 Hz-1 kHz.
4. Open stop: small bumps at the end, 15-25 dB under the motion peak.
5. Dwell: 3-5 s.
6. Closing motion: Krystian 3.7 s, launchsite 5.1 s, magedu 3.4 s. Closing is slower than opening.
   Krystian's close has two parts: a fast run, a dip in level 0.4 s long, then a slow final creep
   of about 0.7 s (the operator slows the doors before they meet; codes cap the closing energy at
   about 10 J).
7. Close bump: the leaves meet at their rubber edges. magedu (inside): 125 Hz loudest, 250 Hz -6,
   500 Hz -13, 1-4 kHz -19 to -20, 8 kHz -23. launchsite (hall): flat from 63 Hz to 4 kHz within 7
   dB.
   Rebounds: 64 ms (-1.5 dB), 220 ms (-4 dB) after the bump (magedu); 240 ms (+1 dB) and 410 ms
   (-5 dB) (launchsite).
8. Steel ring after the bump: launchsite 797 Hz (T60 1.27 s, Q 460), 1254-1276 Hz (T60 0.76-0.79
   s), 2955 Hz (T60 3.2 s, Q 4300), 6713 Hz (T60 1.35 s). magedu: 172 Hz (prominence 20 dB, Q 34),
   2.13-2.16 kHz (Q 330), 4.28 kHz. These long rings are what make it "elevator" and not "glass
   slider".

### Recipe

- Chime: two sine-like tones with the harmonic levels above, 0.55 s each, 1055 then 894 Hz (or the
  building's own pair), exponential decay T60 1 s. This is electronics; a tone generator is right.
  The 42 tones in `inbox/elevator sounds` are signal tones of this kind (buzzers 340-400 Hz, beeps
  631 Hz to 3.7 kHz, dings 361 Hz, chimes 721 and 1324 Hz, struck bells 3.0-3.1 kHz). None of them
  is door motion.
- Clutch: one contact, tau 3-5 ms, into a mechanism bank: 135-156 Hz (Q 60-70), 500 Hz cluster, plus
  a weak 3-6 kHz set. -1 to -11 dB re the close bump.
- Motion: noise for the sheaves on the track (rolling), shaped to 125-500 Hz with -6 dB per
  octave above 1 kHz, level proportional to v^2. Motor tones (250, 700, 1000 Hz with a little
  wander) proportional in frequency to the motor speed, -6 to -10 dB under the rolling. Speed curve:
  1 s ramp, run, 0.8-1 s slowdown. Closing: run speed 60-80 % of opening, then a creep at about 25 %
  for the last 0.5-0.7 s.
- Close bump: two leaves meeting, one contact each 0-10 ms apart, tau 4-6 ms (rubber edges), into a
  stainless leaf bank: first mode 150-200 Hz, then clear long rings at 0.8, 1.25, 3.0 and 6.7 kHz
  (eta 0.0005-0.002, so T60 0.75-3 s), and a dense set between them at eta 0.005. Rebounds at
  60-70 ms and 200-250 ms, -1 to -5 dB, tau 6 ms.
- Size: a 1.1 m opening with two 0.55 m leaves at 0.3-0.5 m/s gives the 2.5-5 s motions measured.

### Test numbers

- Opening shorter than closing. Opening 2.5-5 s, closing 3.4-5.1 s.
- Motion: 125-500 Hz loudest, 2 kHz 13-16 dB down.
- Close bump followed by rebounds at 60-70 ms and 200-250 ms, within 5 dB of the bump.
- After the bump, at least one ring between 0.7 and 3.2 kHz still above -40 dB 0.5 s later.
- Chime: two tones, the second 1.15-1.2 times lower than the first, each 0.5-0.6 s.

## Which recordings are too reverberant for decay times

Use them for event order, timing and band balance only:

- `2-firedoor-kyles-institutional-pushbar`: corridor, late decay 0.6-0.9 s.
- `2-firedoor-klangfabrik-stairwell-panicbar`: stairwell, seconds.
- `2-firedoor-berumen-fire-exit-latch`: late decay 0.7-0.87 s; the first 20 dB is usable.
- `4-pullglass-ifm185-library-glass-aluminium-closer`: library, about 1 s.
- `4-pullglass-lampeight-heavy-glass-metal-slow`: theatre.
- `6-patio-kijjaz-slide-glass`: live room, C50 0 dB.
- `7-elevator-launchsite-open-close`: lobby; the steel ring T60s may be partly the lobby, though
  the 2.95 kHz ring at Q 4300 is far longer than any room at that frequency.
- `1-interior-matucha-apartment-soft`: the room is fine, but below 200 Hz the microphones hear air
  from the moving leaf.

Dry enough for decay: `1-interior-kyles-light-wood-knob-latch` (to 130 ms),
`4-pullglass-kraftaggregat-*` (above 500 Hz), `6-patio-goblinjack-big-smooth`,
`3-storefront-fossarts-thumbturn-deadbolt`.

## Order of work

Not current: the plan for the rejected recipes. The physical models replaced it.

1. One modal impact engine (contact of length tau into a bank of modes per body), with banks made
   from the material table and the part's size, not tuned per door.
2. Type 1 and type 2 first: they share the latch and differ only in the leaf bank and the bar. If
   Cody can tell wood from steel by ear with the same engine, the approach holds.
3. Glass (types 3, 4, 6) adds the strong low panel modes, the frame tube series and the rattle.
4. The moving doors (types 5, 7) add a speed curve, rolling noise and motor tones on top of the same
   impact engine.

## Model history

The longer story behind the door models' constants; each warning and number is also in the code, a
sentence each.

### Knob door

- Round 1 (2026-10-03) fitted a recipe to each event, and every opening sounded the same whatever the door
  was doing. Cody: "are we modeling the doors or modeling the sound? ... we need to model the physical
  doors." Everything since is parts, contacts and a hand.
- The first knob was an open cylinder: a wine glass, and it sounded like one. A closed 27 mm ball is too
  stiff to ring below about 18 kHz; what rings is the 19 mm base annulus, near 10 kHz.
- The first hollow leaf lost more than 0.05 and sounded like plastic; 0.03 now.
- Round 1 pulled on the knob with a sixth of today's 12 N and the opening was a faint click.
- A creak on every door made the building sound haunted. A merely worn pin (wear 0.55) never stuck at a
  game's opening speed and Cody heard no squeak, so the one squeaky character is dry and rusty (0.95-1.0).
- Declared levels used to be the model's LAFmax less a 14 dB calibration. A close's crack stands 21-29 dB
  over its own LAFmax, so a normal close was heard at 51 dBA at 1.5 m on "real" where the model puts 86,
  under a pedestrian's greeting at 60. Cody, 2026-10-03: "way way way too quiet ... if I'm 5 feet away from
  a door at these levels I'd barely know someone opened a door", and no calibration taking level off the
  doors. Every door model now declares its render's peak.

### Push-bar door

- Round 1 put every bar on soft urethane and Cody heard no push at all; bare metal on every one rang the
  case's walls at 134 dBA. Characters now run nylon, acetal, worn, and an old one with its slider worn to
  almost nothing.
- Round 8 (2026-10-04) put the pad's landings on its steel lever tabs; plastic only knocks on the rails.
  The steel door's latch case, as a damped lump on a spring, passed its bolt's 0.1 ms stop on as a thud
  under 2 kHz until round 8 made its stops tabs of the case's steel; the glass door's lock body had the
  same fault and the same fix.
- Thin aluminium for the case and pad rang long and high: "a spoon in the sink". The case is 1.5 mm pressed
  steel, the pad moulded plastic (Cody: "the push bar that gets pushed in is usually plastic, the casing
  around the pusher is usually metal").

### Sliding doors

- The automatic door's prefab times (1.5 s open, 2.5 s shut) were a guess; a real one opens at 0.7 m/s and
  shuts at the 0.3 m/s ANSI/BHMA A156.10 allows, and AutomaticSeconds now derives the time from the
  controller.
- The patio stop was once a single soft bulb: a 20 ms push with nothing above 1 kHz, the blow carried by a
  76 Hz frame mode and the glass, Cody's "hollow". Now a cushion of pile and bulb, then frame on frame.
- The round 2 rolling profiles were noise smoothed on a 3 mm grain and drawn straight between points every
  0.4 mm: every point a kink, and the roll came out flat to 8 kHz. Now RollingProfile builds the spectrum
  and Smooth interpolates with a cubic.
- Round 3's "gritty": grains met at a point clicked to 20 kHz and, with the roll no longer a hiss to hide
  them, a worn door's grit was all that was heard. Grains are now met across the Hertz patch.
- Openings used to run the leaf into the head-track bumper: "the open sound sounds like a close sound".
  The hand now brings the leaf to rest short of it.
- Steel wheels on a patio door: each grain clicked and rang, Cody's "metallic". Every patio door rolls on
  nylon; the old one is the same door dirty, pitted and flatted.
- The header as one 0.45 by 2 m sheet from 60 Hz boomed: "a metallic tube".
- The drive's housings at a loss of 0.01: "too much resonance", and the brushes' friction as white noise
  was the hiss.

### Car window

The four characters and Cody's choice of the old one: docs/COMMON_NOTES.md, Car windows.
