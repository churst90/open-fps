# Trains, air and signals, from the mechanism

_Built 2026-09-18. `OpenFPS.Common/Trains.cs`, `Signals.cs`, `Pneumatics.cs` (the parts lists);
`AudioEngine/Core/Rail/` and `Core/Signals/` and `Core/Pneumatics/` (the synthesis);
`--train`, `--signals`, `--airbrake`, `--crossing` in the AudioLab. Not yet wired into the game:
nothing on a map runs on rails, and there is no `train:` emitter. This is the spec the code follows
and the list of what it does not do yet._

## The rule, again

Nothing here is a tone and nothing here is a recording. Every object is described as parts with
dimensions — wheel diameters, rim sections, rail lengths, bogie centres, blast nozzles, chime
lengths, orifice sizes — and the sound is integrated from them. The only numbers that are not
physics are the level anchors, and each one is named, commented and justified where it sits.

## Most of a train is not the engine

A locomotive under power is one source among sixty. From the lineside, what a train sounds like is
steel wheels on steel rail, and every vehicle makes it whether it is driven or towed.

### Rolling noise

Neither the wheel nor the railhead is smooth. Both carry a roughness spectrum a few microns deep,
and rolling one over the other at V metres a second turns a wavelength λ into a frequency V/λ. That
one sentence gives the whole character: broadband from 100 Hz to 5 kHz, the entire spectrum sliding
UP with speed, and quieter on newly ground rail.

Two things shape it before anything radiates:

- **The contact patch filters it.** Wheel and rail touch over an ellipse about a centimetre long, so
  an irregularity shorter than that is averaged out rather than ridden over. That is a low-pass in
  WAVELENGTH, which means its corner in hertz rises with speed — and it is why a slow train rumbles
  where a fast one hisses, rather than being the same sound louder.
- **Tread brakes roughen wheels.** A cast-iron block dragging on the tread corrugates it; a disc
  brake leaves it alone. Nine decibels, and it is the single biggest difference between a freight
  train and a passenger train. It lives on `WheelsetSpec.TreadBraked` — a property of the BRAKE,
  not a "freight is louder" rule.

Then three things radiate it (`TrackResponse`, `BogieVoice`):

| radiator | owns | set by |
|---|---|---|
| the rail | 200 Hz – 1.5 kHz | rail section and sleeper spacing (the **pinned-pinned** resonance, where half a bending wave fits one sleeper bay: 1,427 Hz for UIC60 at 0.6 m) plus the rail-on-pad bounce |
| the sleepers | below ~400 Hz | how big and heavy they are; a slab has none and makes it up in the middle |
| the wheel | above ~1 kHz | the rim as a ring bending out of plane: `f_n ∝ n(n²−1)/√(n²+1) · √(EI/ρA) / R²` |

The wheel's modes come out at 442, 1249, 2395 and 3873 Hz for a 915 mm coach wheel and 669, 1835,
3632 for a 660 mm tram wheel, purely from the rim's dimensions. Below a few hundred hertz a rail is
narrow against the wavelength and radiates badly — efficiency rises as f² — and that term is a large
part of why speed costs so much.

**The speed law is the check.** Nothing in the model declares one. Measured on the rendered pass-by,
80 → 130 km/h gives +6 dB, which is **28·log₁₀(V)**; the measured figure for real trains is 30. That
is the test that the roughness exponent, the contact filter and the radiation efficiency are all
pulling in the right direction.

### Clatter is geometry

Jointed rail has a gap every rail length (39 ft in North America, because that is what fitted in a
gondola). The wheel drops through the dip angle, so it arrives with a vertical speed of dip × train
speed; what stops it is the Hertzian contact spring, about 1.4 GN/m, against the unsprung mass.
Those two give a contact resonance near 160–200 Hz, so a blow two or three milliseconds long, and a
peak force of `Δv·√(k·m)` — about 150 kN for an ordinary joint at line speed. That force goes into
the same rail and the same wheel the roughness does, which is why a clack sounds like the train it
is attached to.

Everything about the RHYTHM falls out of where the axles are. On 39 ft staggered rail at 65 km/h:

```
a joint every 330 ms · the two axles of a bogie 99 ms apart
· the two bogies of a wagon 689 ms apart · the next wagon 967 ms after that
```

Nothing sequences that. Each axle has its own place on the rail and meets each joint at its own
moment. A wheel flat does the same thing once a revolution, and much harder, which is why one bad
wheel in a train is audible over everything else in it.

### Curve squeal

A wheelset is two wheels rigidly joined, so on a curve both have to creep sideways — about
`wheelbase/2R`. Past about five milliradians the friction saturates and starts falling with sliding
speed, which is negative damping, and one of the wheel's own axial modes grows into a limit cycle.
So whether a train squeals is a question about the curve radius and the bogie wheelbase and nothing
else — and a resilient wheel does not squeal, because its loss factor beats the negative damping
before the loop gain reaches one. A tram on a 25 m curve squeals; a metro on 250 m does not.

### Traction

| | what it is | what you hear |
|---|---|---|
| `ge_7fdl16` | 175 L turbocharged four-stroke V16, 440–1,050 rpm in eight notches | firing at 59–140 Hz; steps, not sweeps, because a governor holds a notch |
| `emd_645e3` | 169 L **two-stroke** V16, 315–900 rpm | fires every cylinder every revolution: 240 Hz at full, so it hums where the GE hammers; plus the Roots blower |
| `ElectricDrive` | gears, motor, inverter | mesh at pinion teeth × motor rev/s; motor hum at 2× electrical; and the inverter's **pulse-mode staircase** |
| `SteamFrontEnd` | cylinders, blast nozzle, chimney | see below |

The inverter staircase is worth calling out because it is emergent. A drive switching at a fixed
carrier makes a steady tone at a standstill; as the output frequency rises, holding that carrier
would mean more switchings per cycle and more heat, so the drive locks the carrier to a whole number
of pulses per cycle and steps that number down — 27, 15, 9, 5, 3, 1. Inside each mode the tone rises
with the train and at each change it drops. Nobody designed that sound; it is an engineer keeping
switching losses down, and the model produces it because it chooses the pulse count the way the
drive does.

### Steam

The chuff **is a jet**, not a drum. The exhaust valve opens on a cylinder still holding a couple of
bar and it empties up the blast pipe and out of a 135 mm nozzle at the speed of sound in steam — so
it is Lighthill's eighth power like any other jet, which is exactly why an engine working hard is a
cannon and the same engine drifting is nearly silent. Each burst decays as the cylinder empties:
cylinder volume over nozzle area over sound speed, **35 ms** for a big engine.

The **bark is the chimney** — a pipe open at both ends, so c/2L, **157 Hz** for 0.95 m. A short wide
stack barks; a tall narrow one rings.

The **rate is geometry**: two cylinders, each double-acting, is four beats per turn of the drivers.
A 1.85 m wheel at 12.5 m/s gives 8.6 beats a second; at 24 m/s it gives 16.5, against a 35 ms decay
— so the beats have run together into a roar, which is what a big engine at speed really sounds like
and not what anybody expects. And no valve gear was ever square: `ValveSettingError` is the limp in
the beat.

## Horns, whistles and bells

**An air horn is a reed and a column.** The note is c/2L of the bell — a flaring horn behaves like a
full cone, so all the harmonics — and the chord a K5LA plays is five lengths of brass: 518, 431, 383,
338 and 282 mm giving 311, 372, 416, 468 and 556 Hz. The reed beats on its seat, shut for about half
of every cycle, and a valve that shuts makes a pulse and a pulse has everything in it. The flare's
cutoff (160 Hz for the longest bell) is why an air horn has so little fundamental and why the ear
supplies the missing one. And the mouth beams above about a kilohertz, so an approaching horn is all
harmonics and a departing one is nearly a sine — the "opening up" people hear before Doppler has
done anything.

**A steam whistle is a stopped pipe**, so c/4L and the odd harmonics: half an air horn's frequency
for the same length, and hollow where the horn is brassy. What makes it a steam whistle is the gas —
sound travels at about 510 m/s in steam at 170 °C — so it stands a fifth sharp of a pipe of the same
length, and it **wails up** as the bell fills and warms, because the sound speed inside is climbing.

**A bell is a shell, and a shell's modes are not harmonic.** A flat disc has one family; curving it
adds a membrane stiffness that pushes the modes up, but only the ones that have to stretch the metal
to move. The high ones, which flex inextensionally, barely notice. So the low modes climb, the
spacing closes, and a plate becomes a bell. How long it rings is not a decay time, it is three
losses: the metal's internal friction, the air it pushes (which rises with frequency, so a bell gets
rounder as it dies), and the mast it is bolted to (which grips what moves at the crown and cannot
touch what does not). 2.5 seconds for a crossing gong comes out of those three numbers.

## Air

Every air sound on a lorry, a bus or a train is one event: **a vessel at pressure emptying through a
hole.** At 120 psi the pressure ratio is about nine, far past the 1.893 it takes to choke, so the
throat is sonic, the jet leaves underexpanded at about Mach 1.6, and there are shock cells in it
radiating a rasp of their own. The vessel empties exponentially while it stays choked — volume over
area over a constant, 0.5 s for a bus door and 2.1 s for a trailer's relay valve — and then the
ratio falls under 1.893, the cells vanish, and the eighth power takes the level away fast. That knee
is the shape of the sound.

Two things that surprise people and fall straight out of the model: the loud one is the **release**,
not the application (applying moves air INTO the chambers); and the **air dryer purge** — the bang
and sigh from a parked truck every couple of minutes — is nothing but a pressure switch reaching
cut-out and a 12 mm hole blowing 2.2 litres down in 140 ms.

## Levels, and what they are anchored to

| anchor | value | from |
|---|---|---|
| `TrainProfile.RollingReferenceDb` | 104 dB per axle at 1 m at 100 km/h | set so the rendered PASS-BY reads 82 dB at 7.5 m for a disc-braked train at 80 km/h |
| `ChimeHornSpec.ReferenceDb` | 139 dB at 1 m on axis (K5LA) | the legal 96–110 dBA at 100 ft |
| `StruckBellSpec.ReferenceDb` | 86 (gong) / 110 (loco) dB at 1 m, **RMS while ringing** | meter readings of bells in use |
| `AirSystemSpec.JetTrimDb` | −15 dB against Lighthill with K = 1e-4 | the same sign and nearly the same size as the aircraft's jet trims |
| `SteamLocoSpec` blast | Lighthill straight, no trim | 135 dB at 1 m at full effort, which puts it at ~105 dB at 30 m |

The rolling anchor is the one worth reading twice. Setting it by extrapolating a single axle back to
one metre and forward again to the lineside put it **twelve decibels light**, because a line of
sources does not behave like one of them. Anchoring against the measurement that actually exists —
a pass-by at 7.5 m — is the only honest way to do it.

## A train is not a thing at a place

A six-coach Amtrak is 177 m long and a fifty-wagon freight is nearly a kilometre. The difference
between a train going past and a lorry going past is not timbre, it is that the train ARRIVES FOR A
MINUTE. `TrainSynth` is therefore a list of sources — one per bogie, plus bodies, plus traction,
plus signals — each knowing how far behind the head of the train it sits. Three things fall out of
that and out of nothing else:

- the level rises to a **plateau** instead of a peak (a line source falls off 3 dB a doubling until
  you are further away than it is long — measured here: 7.5 m → 25 m costs 6 dB, not 10);
- the clatter **sweeps** down the train past you, because each bogie's bangs arrive from a different
  place;
- the far end of a long freight is **duller** than the near end, because the air has had four
  hundred metres to take the top off it.

## The instruments

```
--signals [horn|whistle|bell] [preset ...]     each one at a metre, on the bench
--train [preset ...] [speed=] [offset=] [sec=] [notch=] [cars=N] [horn=0|1] [stems] [binaural]
--airbrake [tractor_trailer|transit_bus|locomotive]
--crossing [train preset] [speed=] [sec=] [track=m] [mast=m]
```

`--train stems` writes `rolling`, `traction` and `signals` apart, for the same reason the vehicle
bench writes a block stem: a quiet layer buried under a loud one is indistinguishable from a missing
one. `--crossing` is binaural, and `--train binaural` will be.

**Binaural, and what it is not.** The interaural TIME difference is not applied, it HAPPENS: each
ear is a different distance from the source, and the renderer already deposits every sample at the
moment it arrives, so a source sweeping past produces the continuous sliding delay it should. The
LEVEL difference is the inverse distance plus a spherical-head shadow per ear (Brown and Duda's
one-pole-one-zero fit, complete with the bright spot a sphere focuses into its own shadow). There is
**no pinna**, so there is no elevation and no front-back discrimination; that wants a measured HRTF,
and the engine has a path for one.

## What the measurements caught

Each of these was found by an instrument and would not have been found by listening:

1. **The horn reported 14.7 kHz** when it should have played 311 Hz. It was not oscillating at all —
   what I was radiating was differentiated slit noise. Counting zero crossings measures the noise in
   a signal, not the note in it; autocorrelation around the expected note is the right instrument,
   and it is now printed for every bell of every horn along with the crest factor and how much of
   each cycle the reed spends shut.
2. **A pipe's input impedance is resistive at resonance.** The column modes were all-pole sections,
   which lag ninety degrees there — enough on its own to stop a reed and a column oscillating
   together. They are bandpass now.
3. **An outward-striking reed only pumps energy into a pipe when it is driven ABOVE its own
   resonance** (the same reason a brass player's lips buzz below the note). Tuned the other way, the
   whole thing damps.
4. **The whistle crackled** because its modes were rebuilt as the steam warmed, which drops the
   filter state. `Mode.Retune` changes the coefficients and keeps the two delayed samples.
5. **The whistle's jet noise was banded around the note.** It is a jet through a 3 mm gap; its noise
   is broadband and bright and has nothing to do with the note. Banding it took all the air out and
   the verdict was "like under water".
6. **The whistle wobbled 6% in pitch**, which is not far off a semitone, continuously. "Not like
   solid notes."
7. **The squeal had a gain factor outside its feedback loop**, so every wheel squealed equally hard
   however well damped it was — which defeats the point of asking what the wheel is made of.
8. **The bells were anchored on the peak of one blow** rather than the meter reading of a bell
   ringing, which put them 25 dB down and made a locomotive bell inaudible under its own train.
9. **The axle count was normalised away** by the calibration, so a six-wheel bogie was exactly as
   loud as a two-wheel one.
10. **The pass-by level meter averaged each source's contribution** instead of summing them, and
    read twenty decibels low.

## Not done, in the order it should go

1. **Ear-validation of the trains.** The horn, the whistles, the bells and the air brakes have been
   passed by ear (2026-09-18: "sounds great", "perfect in fact, keepers"). The train pass-bys have
   had one listen and the crossing scene one; neither has been iterated on.
2. **Into the game.** `MachineDefinition` parts `bogie`, `rail`, `traction`, `chime`; a
   `RailVoiceState` on the render pool with `SetListener` like the exhaust; a server-side
   `RailSystem` moving a consist along a spline with the crossing circuits as triggers. The
   arrival-time deposit in the spikes is what the mixer already does with distance delay and
   Doppler, so none of the renderer code is needed in the game.
3. **The track is not in the acoustic map.** A train in a cutting, under a bridge, or in a tunnel
   should get that from the geometry the way everything else does; `TrackSpec.StructureDb` is a
   stand-in and should go.
4. **What the model does not carry yet:** wheel–rail creep in traction (a locomotive slipping on a
   grade); brake squeal as distinct from curve squeal; the two exhaust stacks of a big steam engine
   as separate sources the way the F1's tailpipes are; rail lubrication; the rumble a train makes
   through the ground rather than the air; buffer and coupler slack running through a freight train
   as it starts and stops, which is one of the most recognisable sounds a train makes and is a
   sequence of impacts down a line of sources, so the machinery for it is already here.
