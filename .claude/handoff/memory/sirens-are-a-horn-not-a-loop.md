---
name: sirens-are-a-horn-not-a-loop
description: The police siren built 2026-09-21 from the real chain — sawtooth oscillator, compressed driver, horn cutoff — with wail/yelp/phaser/hi-lo off ONE oscillator and measured against spec
metadata:
  type: project
---

Cody: "the existing beacon and siren files are no good. These need to be generated synthetically
according to real specs of police sirens... one slow, one medium and then the real fast one."

`PoliceSirenGenerator` was a peak-normalised sum of three sines written to a WAV. Replaced by
`SirenSpec` (Signals.cs) + `ElectronicSiren` (Core/Signals), modelled as the chain:

- **SQUARE oscillator at ~50 % duty — odd harmonics only.** It was a sawtooth first, reasoned from
  "a siren must be heard, so give it every harmonic". True of the job, wrong about the hardware:
  the instrument every electronic head imitates is a ROTARY CHOPPER, ports and lands cut equally
  wide, airflow switched fully on and off — a square, whose even harmonics vanish. Cody heard it
  before it was reasoned about: *"are real sirens based on square waves or sawtooth? Sounds like
  these are sawtooth."* A sawtooth's even harmonics fill the octave above every partial and read as
  BRASSY (a horn); a square leaves those gaps and reads hollow and hard, which is the siren sound.
  Summed from the rectangular series a_k = sin(pi k d)/(pi k), so nothing aliases by construction.
  **Duty must be within about one per cent of a half**: the error is multiplied by k, so 0.48 left
  the 5th only 8.7 dB over the 4th — halfway back to a sawtooth. 0.49 gives 20 / 15 dB.
- **Compression driver pushed past linear** (`Compression`), which is the brassy edge.
- **The horn, which is the whole character.** `FlareCutoffHz` is DERIVED, not declared:
  `f = c / (pi * D_mouth)` — a 200 mm mouth cuts off at 546 Hz, which is why a siren has no bottom
  end at all. Driver rolls off above ~4.5 kHz. What is left is 500 Hz–4 kHz, where the ear is most
  sensitive and road noise is weakest. Measured: **94–97 % of the energy lands in that band.**
- **Beams forward** ~10 dB, like any horn under a bumper.

**One oscillator, three modes** — wail / yelp / phaser differ ONLY in sweep rate, as the hardware
does, so switching mid-sweep is continuous in phase and frequency. Swept in LOG frequency (a linear
sweep spends most of its time at the top). Up fast, down slow (40/60), which is the electromechanical
original's spin-up and coast-down and what people actually recognise.

**Two faults found by ear on the first listen and fixed 2026-09-21:**

- *"The fast siren sounds like it is stepping, not sweeping."* The oscillator was a PolyBLEP
  sawtooth run through a tanh for the driver's compression — and a waveshaper AFTER band-limiting
  manufactures a fresh harmonic series, most of it above Nyquist, which folds back. Aliased
  partials slide DOWN while the real ones slide up, so the faster the sweep the worse it is:
  roughness on a 5 s wail, a stutter on a 10-a-second phaser. Now summed harmonic by harmonic, only
  the partials the horn will radiate (3-7 of them), with compression expressed as what it
  physically is — a flatter rolloff, 1/n^(1−0.6c) instead of 1/n. **Off-harmonic energy measured at
  0.00 %**, and it is cheaper than the waveshaper it replaced.
- *"I can hardly hear the siren over the engine."* The head was measuring 125.8 dB against the 130
  it declares, because the flare cutoff sits a quarter-octave under the bottom of the sweep and the
  two-pole high-pass was already working on the fundamental. Now the chain's insertion loss is
  MEASURED once at construction across a whole sweep and compensated — the same rule
  `BladeRow` follows for its band. All three heads now land within 0.3 dB of their declaration.

**The horn sets the bottom of the wail.** A sweep whose low end sits under the flare cutoff does
not sound like it is descending, it fades out — the mouth stops coupling and the last of the
descent is filtered away. Cody: *"on the down wail I think it should go a little lower."* The mouth
was 200 mm (cutoff 546 Hz) against a 650 Hz bottom — a quarter-octave of headroom. A 100 W siren is
fitted with an eleven-inch assembly: 280 mm, cutoff 390 Hz, and the sweep is 500-1500 now.
`TheBottomOfTheWailIsAboveTheHornsCutoff` holds every head to it, and caught the two-tone sweeping
to 435 Hz through a 455 Hz cutoff.

Measured against spec with `--siren`: patrol wail 12/min (spec 12), yelp 195 (194), phaser 602
(600); level 128.6 dB at 1 m against the declared 130 (= 120 dB at ten feet, which is how sirens
are actually type-approved — Title 13 / SAE J1849 want ≥110). Presets `patrol`, `apparatus`,
`two_tone`. WAVs in `inbox/sirens-2026-09-21/`.

**In the game since 2026-09-21.** `SirenVoiceState` is its OWN voice, not part of the engine's:
a siren is 35 dB over the car's exhaust, so one shared `PascalsAtFullScale` would either square the
siren or bury the engine — two sources 35 dB apart need two references. Placed at the grille, where
the horn is. `VehicleProfile.Siren` names the head; `ClientAudioSystem.SirenVoice` makes it.

**A bench that cannot reproduce how a thing is DRIVEN can only exonerate it.** The head sounded
right on `--siren` and wrong on the map, and the difference was not the synthesis at all: the
mode was being chosen from the INSTANT deceleration, and a racing line brakes for every corner, so
it flipped between wail and yelp **nine times in two laps** — measured. The decision moved into
`SirenController` (Common, so a test can drive it) with a smoothed decel, a sustain requirement and
a 22-second hold: **5 changes in two laps** now, each at a real braking point. `--siren drive`
renders the head as the map's own racing line commands it, which is the bench that was missing.

**A PATROL CAR IS MOSTLY NOT ON A CALL, and that is the only reason its engine exists.** The head
is 130 dB and the car is 95, so a siren that never stops means an engine that is never heard —
reported as "the police cars sound like they have no engine, they're all siren, just sounds like a
siren driving by". The answer is not a quieter siren (it is the right level, it IS a siren) but one
that is off most of the time. `SirenController` runs a call for 35-80 s and then goes quiet for
70-160, seeded per vehicle so no two cars are in step: measured at **35 % sounding**. While on a
call it draws its mode — mostly wail, sometimes yelp, and a fast sweep (yelp or phaser, drawn) when
it brakes hard for a junction. A mode hold of 22 s was tried and was wrong the other way: a pass-by
lasts three seconds, so the head could never change while it went past.

**When it sounds is read off what the car is DOING**, the same rule as the aircraft power lever and
the air brakes: under a walking pace it is off, braking hard into a junction it goes to yelp (which
is what a real crew does, because a fast sweep is far easier to place), otherwise wail. Nothing on
the wire carries it. Driver-activated (a key, like a horn) is still to do.

See [[signals-and-air]], [[the-police-car-was-a-pace-car]].
