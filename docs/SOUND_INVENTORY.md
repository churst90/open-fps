# What still needs recording, and what does not

_Written 2026-09-15._

The question this answers is "what sounds do I need to go and find". The more useful half of the
answer is how short that list is, because this engine's whole argument is that a recording is a
sound of one thing, at one size, of one material, struck once with one force — and almost everything
in a world is a thing whose size, material and force are known at the moment it happens.

The test for whether something must be recorded is not "is it hard to synthesize". It is:

> **Does the sound carry information that varies with the thing, and does the engine already know
> what varies?**

If yes, synthesize it — a recording throws the variation away and a player stops being able to tell
one door from another. If the sound is a fixed, complicated, one-off artefact of a particular
physical object nobody will ever parameterise — a specific voice, a specific machine, a specific
place — record it.

---

## Already synthesized, and nothing to acquire

These exist in the tree and need no assets at all. Listed so they are not accidentally shopped for.

| Sound | Model | What varies |
|---|---|---|
| Engines | `EngineSynth` + `EngineProfile` (14 presets) | Cylinders, firing order, pipe lengths, rpm, load |
| Tyres | `TyreFriction` | Slip, surface, load, speed |
| Turbo / wastegate | `Turbocharger` | Boost, throttle transitions |
| Gunfire | `WeaponSynth` | Calibre, barrel, muzzle device, distance |
| Supersonic crack | `Ballistics` | Range — the crack-to-report gap IS the rangefinder |
| Doors | `DoorSystem` + `PanelAcoustics` | Material, leaf size, thickness, swing speed |
| Impacts of any two things | `ImpactAcoustics.Between` | Both materials, both masses, size, closing speed |
| Glass breaking and landing | `GlassBreak` | Pane type, area, height above ground |
| Struck panels, rings | `PanelAcoustics` | Modal series from material and dimensions |
| Knocks, rings, hisses, scrapes | `TransientSynth` | Level, pitch, decay, noisiness |
| **Breathing and exertion** | `Breathing` (new) | Speed, how long, how long ago |
| Room reverb | Steam Audio simulator | Real geometry and materials |
| Wall reflections, diffraction | `ImageSource`, `Diffraction` | Geometry |

---

## Should be synthesized, and is not yet

The work is the model, not the shopping. In rough order of what a player would notice.

### 1. Footsteps — the biggest one

Currently the LAST recorded sample in the walking path, and the one that most deserves to go. A
footstep is a mass meeting a floor at a speed: that is `ImpactAcoustics.Between`, which already
decides what a dropped rifle sounds like on the same floor. Synthesizing it gets, free:

- **Loudness with speed.** Every footstep is currently the same level whether you are strolling or
  sprinting, and "how fast is that person moving" is precisely what a listener wants to know.
- **Every surface, without a take for each.** `AcousticRegistry` knows 36 materials; the sample
  library knows a handful, and `Wet_Concrete` has no footsteps at all.
- **Mass.** A heavy person in boots and a light one in trainers stop being the same sound.
- **Shoe as a material.** Boot, trainer, bare foot — a second material in a call that takes two.

The 146 unsorted takes in `ASSETS/SOUNDS/_unsorted/footsteps` are still worth labelling: they are the
*reference* to measure the model against, exactly as the door latch and glass recordings were.

### 2. Rain on a surface

Never a sample. Rain on a panel is a stochastic impact process — drops arrive at rate
`intensity × area / drop volume` and each excites the panel at its own modes, which is the same
machinery as tyre-tread impacts and `GlassBreak`. Every variable is information: a steel car roof, a
glass windscreen, a tin shed, grass, and standing water are five different sounds from one model, and
the intensity is already on the wire as `PrecipitationIntensity`.

### 3. Wind

Two separate things, and only one is a sound in the world:

- **Wind in the environment** — through foliage, round a building's corners, whistling in a gap. This
  is broadband noise shaped by what it is blowing past, and it belongs to geometry.
- **Wind at the listener's own ears** — the thing you asked about. Yes, it should be there, and it is
  not a world sound at all: it is turbulence at the pinnae, so it is a stereo bed that rotates with
  your head rather than a source in the world, its level goes as roughly the cube of wind speed, and
  it is the cue that tells you which way you are facing in a gale. Synthesized: filtered noise plus a
  gust envelope, both of which `WorldEnvironmentSystem` already computes.

### 4. Clothing and carried gear

A body that moves rustles, and a body carrying something knocks. Both are stochastic impact/friction
processes driven by the gait the engine now has. The interesting one is carried gear, because it is
the difference between a body and an armed body.

### 5. Water

Footsteps in it, rain into it, things dropped into it. Bubble acoustics is a well-understood model
(a bubble is a resonator whose pitch goes as 1/radius) and it is one of the few places where a
synthesized result is dramatically better than any small sample library.

---

## Must be recorded, and why

Genuinely irreducible. These are artefacts of specific physical objects, not parameterised classes.

**1. Reference recordings for calibration — the highest-value category by far.**
Not to play. To *measure against*, which is how the door latch was found to be 42 % body rather than
2 %, and the glass shards an octave low and three times too long. Every one of these corrected a
model that tests were confidently asserting was fine. Needed, close-mic'd and unclipped:

- **A door closing** — the current model is "better but not perfect"; the thud and the panel ring are
  the weakest parts of it.
- **A car crash, or any heavy metal impact.** There is currently *no measurement at all* behind
  collisions — the biggest single gap in the calibration set.
- **Footsteps on known surfaces**, once the model above exists.
- **Breathing** — a person at rest, after a jog, and after a hard sprint. Three takes settle the
  whole exertion curve.
- **Gunshots that are not clipped.** The existing takes are limited flat for up to 96 ms and 34 dB
  darker at 4 kHz than at 55 Hz, which is why `RecordedLayerLevel` is down at 0.45.

**2. Ambience beds.** A place is not a parameterised object. Woods, a city street, a harbour, rain on
a landscape — 4-channel ambisonic where possible, because the rotation is the whole point.

**3. Voices.** Speech, shouts, grunts, pain. Not synthesizable to any standard a player would accept,
and the one category where identity matters more than physics.

**4. Specific machines with published identities.** A particular siren, a station announcement chime,
a named alarm. These are *signals*, not physics — their whole job is to be recognisable, and a
synthesized approximation of a recognisable signal is worse than useless.

**5. Music.**

---

## Things you asked about specifically

- **Opening an inventory** — do not record this, and consider not having it. There is no inventory
  screen (the readout is a sentence), so there is nothing to open. If you want a confirmation sound,
  it belongs to the *item*: taking a rifle off your back is a strap and a mass moving, which is
  `ImpactAcoustics`. A UI blip would be the only sound in the game that is not something that
  happened in the world.
- **Walking with an object in your hands** — synthesize. The gait is known, the item's mass and
  material are known, and what you want is a knock whose loudness and pitch tell the listener what
  you are carrying. A sample tells them only that you are carrying something.
