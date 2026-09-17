# Voices, machines, and the city — where this goes next (2026-09-16, session 9)

A design record, written at the end of the session that fixed the speedway's reverb, crowd, clap and
borrowed-voice Doppler (commit `94bb376`). **No code in this document has been written yet.** It
exists so the next session can start from the argument rather than rebuild it, and it ends with the
order to do things in and how to know each step worked.

> **Step 1 was done in session 10 (2026-09-16).** Machines are parts lists (`OpenFPS.Common/Machines.cs`,
> `machines/*.json`, `--machines`) and a close car is heard through two voices, one per outlet
> (`EngineTapState`, `Localisation`, `--machine-pass`). What was learned doing it, and the three things
> left open, are at the end of section 7. Everything from step 2 on is still an argument, not code.

The question that prompted it, verbatim:

> "so we're limited to the fmod voice slots then? I'm not sure what to tell you in terms of sound
> prioritization. On the speedway I'd say cars are more important, but what about a user's map of a
> city scape with lots of npc walking around talking, cars, general noise from the environment, birds
> once I have them, etc. this stuff needs to be done without the user thinking about it."

That last sentence is the whole specification.

## 1. What actually binds

| ceiling | value | set in |
|---|---|---|
| FMOD software channels | 256 real, 512 virtual | `FmodAudioProvider.Initialize` (pre-`init`, see the speedway memo) |
| Steam Audio HRTF voices | 96 | `FmodAudioProvider.SaPoolSize` |
| VoiceManager budget | 256 | `AudioEngineFacade` constructs `new VoiceManager(this, _bank, 256)` |

FMOD's 256 is not the wall. **The 96 HRTF slots are**, because every spatialised voice needs one, and
behind both sits the only budget that is physically real: DSP time.

Letting FMOD do the rationing is worse than doing it ourselves. FMOD virtualises the quietest channels
and revives them as they become audible, and a *synthesised* engine that gets virtualised and revived
jumps in pitch — a fault already paid for once (see the speedway memo's `setSoftwareChannels` note).
Whatever ranks voices has to be ours, and has to be stable.

Raising `SaPoolSize` is a stopgap and not an answer: a city has thousands of candidate sources and no
constant is big enough.

## 2. Priority must be COMPUTED, not authored

Today `SpatialEmitter.Priority` is an authored integer and `VoiceManager.Process` scores

```
score = (Priority * Priority) / (1 + distance) * playingBoost
```

Engines are `Priority = 1`. Transients are `Priority = 2`. So a clap two hundred metres away carries
four times the weight of a car five metres away, and a car that loses its slot is not faded, it is
`StopSoundImmediate`d and restarted next frame — which for a synthesised engine means a fresh ring,
priming silence and an envelope fade. That is the reported "vehicles stop close in front of me".

It is also the exact thing the no-special-cases rule forbids: a number that ranks KINDS OF OBJECT
rather than ranking what can be heard.

**The right quantity already exists.** `Loudness.RenderedGain(gain, referenceDistance, range,
distance)` is the mixer's own distance law, extracted into `OpenFPS.Common/Loudness.cs` this session
precisely so it can be asked without a sound card. Multiply it by the path's occlusion and it is the
predicted level at the ear, in identical units for a bird, a bus, a fountain and a jet.

**The pattern already exists too.** `EngineReflections.AudibilityFloor` recomputes each frame the Nth
loudest candidate reflection across the whole map and spends its budget above that line — that is
already "rank on physical audibility, spend the real resource top-down". Generalise it from
reflections to every voice:

- one ranked list per frame, of everything that wants to be heard;
- the top N get slots, where N is the real resource (HRTF voices, then DSP headroom);
- everything below the floor is silent — not because of what it is, but because nobody can hear it;
- a CONTINUOUS source that drops out of the list fades rather than stopping (it is still there);
  a ONE-SHOT that drops out is dropped (its moment has gone — `VoiceManager` already does this half).

Then `Priority` as an authored field goes away. The single legitimate override is accessibility and
gameplay: speech, the player's own footsteps, a warning tone. That is a short explicit list pinned
above the physics on purpose — not a knob on every object.

## 3. Aggregation is what makes a city fit

The crowd is the worked example and it is already built. Four hundred people clapping are not four
hundred voices: they are ONE source with a SIZE (`TransientSound.ExtentMetres`,
`Loudness.Place(level, extent)`) at a level that adds in POWER — ten log of the count, not twenty
(`Applause.LevelDb`). The gain is paid down as the reference widens so the far field is unchanged,
because a distributed source and a point source of the same power are identical once you are well
outside them.

That collapse generalises to everything a city is made of:

- traffic two streets away → one extended source along the street
- a flock of birds → one source with a spread
- a fountain → one source about three metres across
- a market, a playground, a motorway, a river

Near the listener the individuals are individuals. Past the distance at which nobody can tell them
apart they become one thing. **That transition is physical** — it is where the level difference
between neighbouring sources drops below what the ear resolves — and it should be computed, not
authored as a level-of-detail setting.

**Extent belongs on every emitter, not just transients.** Engines currently hardcode
`engineMinDistance = MathF.Max(reference, 3f)` in `ClientAudioSystem` — a car-sized fudge with no gain
compensation, which is also why the crowd/bike balance measures 3.6 dB out rather than 0. A 40 m
airliner and a 15 m rotor disc will need the real number.

## 4. A machine is a parts list

What a vehicle is today:

```
EngineProfile (cylinders, firing order, bore/stroke, compression, induction)
  -> IntakeNetwork / ExhaustNetwork (plenum, runners, turbine, silencer)
  -> Driveline + Gearbox + VirtualDriver
  -> BodyResonator (the shell, as modes)
  -> one radiated voice, placed at an emitter slot
```

`VehicleProfile` is already a RIG rather than a sound: `ExhaustOffsetZ`, `IntakeOffsetZ`,
`ExhaustHeight`, and a comment explaining that the exhaust being three metres behind the intake is how
a listener tells which way a car is pointing. That compositional structure is why a turbocharger could
be added as "a turbine between the manifold and the downpipe" instead of as an EQ curve.

**What is not true yet:** the composition lives in C#. `VehicleProfile.Presets` is a dictionary of
factory functions, and a map can only NAME a preset — it cannot assemble one. The open todo item
"vehicles as prefabs with a LIST of emitter slots" is this.

The target shape, which a helicopter, a bus, a fountain and a tree all fit:

```json
{ "id": "huey", "parts": [
    { "model": "turbine",    "profile": "t53", "at": [0, 1.8, 0.5] },
    { "model": "rotor",      "blades": 2, "rpm": 324,  "at": [0, 3.2, 0] },
    { "model": "tail_rotor", "blades": 2, "rpm": 1660, "at": [0, 2.1, -6.5] },
    { "model": "airframe",   "body": "thin_alloy" } ] }
```

`CompositeComponent` already says "a house, a car and a map are the same idea at four scales", so the
structure has a home; what is missing is that the audio side of a composite is one hardcoded voice.

It buys back something real: a car is ONE voice biased 0.6 toward the exhaust
(`VehicleProfile.ExhaustEmitterBias`) purely because voices are scarce. With ranking and aggregation, a
car passing at five metres can afford two — intake in front, exhaust behind, which is most of the sense
of direction — and the same car at two hundred metres gets none of its own.

## 5. The city map

Already there: zone naming (free since this session — naming a place costs nothing and changes
nothing), portals and doors with apertures, materials with absorption AND scattering, image-source
reflections plus ray-traced RT60, Maekawa diffraction, per-band air absorption,
`SoundEmitterComponent.RepeatIntervalSeconds` (that is birds), composites (that is houses), footsteps
by material, the crowd.

Missing:

- **Traffic AI.** `VehicleSystem` follows a racing line. It has no concept of stopping, giving way,
  turning at a junction, or a speed limit.
- **Recorded voices.** NPC speech and babble cannot be synthesised — 8-12 takes, see
  `docs/SOUND_INVENTORY.md`. The crowd can only clap until they exist.
- **City materials.** Brick, foliage, water. Foliage is the interesting one: absorption 0.5-0.9 AND
  scattering ~0.9, which is exactly why a tree-lined street does not sound like a canyon. "Most things
  are diffused reflections" is now a material property, so a tree-lined street falls out of placing
  trees.
- **The aggregation above**, or a city of NPCs will spend the whole voice budget on footsteps.

## 6. Aircraft, from the mechanism

- **Jet.** The far-field sound is JET MIXING noise, and its power goes as the EIGHTH power of exhaust
  velocity (Lighthill). Broadband, low, and dominant. The whine — fan blade-pass, compressor buzzsaw —
  is high-frequency and beamed into the forward arc, so air absorption strips it over a few kilometres.
  A jet high overhead is therefore a faint roar with no whine FOR FREE, provided the source carries the
  right spectrum. We already have per-band air absorption; nothing needs a special case.
- **Turboprop.** Blade-pass = rpm/60 x blades — 1,200 rpm on four blades is 80 Hz — with strong
  harmonics. The propeller dominates the engine at low speed.
- **Helicopter.** Main rotor blade-pass lands at 10-25 Hz with strong harmonics, plus blade-vortex
  interaction: the THUMP, loudest in descent and turns, when the advancing blade meets the tip vortex
  of the one before it. At 20-50 Hz air absorption is nearly nothing and diffraction around buildings is
  nearly everything, which is why it is heard blocks away before it is seen. Both mechanisms exist in
  the engine already.
- Each needs a MEASURED `SourceLevelDb` the way every engine preset has one (`--engine-levels`), and an
  extent: a point-source airliner is wrong in the near field.

## 7. The order, and how to know each step worked

1. ~~**Parts as data.**~~ **Done, session 10.** A machine prefab with a parts list, each part a model plus an offset. Acceptance:
   the speedway's field is expressed as parts lists and `--engine-levels` measures every preset
   unchanged; a two-voice car (intake + exhaust) is audibly directional at five metres in
   `--vehicle-live`.
2. **Extent on every emitter, and audibility ranking.** Delete authored `Priority`; rank by
   `RenderedGain x occlusion`; continuous sources fade out of the list, one-shots drop. Acceptance: a
   `VoiceBudgetTests` case where a hundred quiet distant sources cannot displace one near car, and the
   reverse; the mixer load line's `without HRTF` count stays at zero through a full crowd reaction.
3. **Aggregation.** Collapse many like sources past the distance they stop being distinguishable.
   Acceptance: a street of thirty cars costs a handful of voices at two hundred metres and thirty at
   five; measured, not asserted.
4. **The city map.** Materials first (brick, foliage, water), then geometry and zones, then traffic AI,
   then birds. NPC speech waits on recordings.
5. **Aircraft**, which by then cost a rotor model and a jet-mixing model rather than a subsystem.

### What step 1 taught, and what it left open

- **The round trip is the test.** Not "can an author write a machine" but "does the library survive
  being written as one" — every built-in taken apart into parts and reassembled, field for field. It
  found a real hole on the first run: the turbo four had no key in `EngineProfile.Presets`, so the car
  using it could not say what engine it held.
- **The exported library is not checked in.** An authored machine overrides the built-in of its name,
  so a generated copy sitting in `machines/` would freeze every car at the numbers it had the day it
  was written. `--machines export=DIR` writes it somewhere to read and to copy from.
- **The two taps must SUM to the one voice**, or a machine changes level when the mixer changes its
  mind about how many voices to spend on it. That is the property to keep when step 2 starts moving
  voices around on a ranked list, and it is held by a test to a ten-thousandth of full scale.
- **The split criterion is the aggregation criterion.** `Localisation.Resolvable` asks whether two
  things subtend enough angle to be told apart; step 3 asks the same question of thirty cars in a
  street. It is one rule, and neither caller knows what a car is.
- Open: the intake voice can sit up to one mixer block (23 ms) either side of the exhaust voice,
  because it aligns to the source's play position at its first block and FMOD does not promise which
  DSP it calls first. Exact alignment wants `getclock` on the mixer thread.
- Open: one acoustic path per machine, taken at its acoustic centre, is used by both outlets. A wall
  between you and one end of a bus is not modelled.
- Open: `ChooseFrontVoices` is untested — nothing in the suite constructs a `ClientAudioSystem`.

## 8. Still open from this session

- **"Vehicles stop close in front of me while going past."** Mechanism identified but NOT confirmed:
  the upside-down priority above, made worse by the transient voices added this session (reflections
  are now `Priority = 1` and `MaxEchoes` is back to 2). Two lines from a live client settle it —
  `Mixer load: ... N without HRTF (M new since last)` and
  `Cars: 19 on the map — X synthesized, Y borrowed, Z out of budget`. If `without HRTF` climbs into the
  tens it is voice pressure; if it is zero and `out of budget` is not, it is the adaptive engine budget
  shedding cars under mixer load, which is a different fix.
- A crowd's FIRST reaction of each kind is silent: that is the event that pays for the render, which
  now happens on a worker. Pre-warming the handful of keys a map's crowds actually produce removes it.
- Three of the twelve grandstand blocks never react — 71-79 m from the racing line against a 70 m
  radius — so a sixth of the stand is dead.
- Air absorption (the 4mV term) is not in the Sabine estimate; a sealed concrete room clamps at ten
  seconds. Left alone deliberately: it changes rooms that were approved by ear.
- `--speedway` in the AudioLab builds no acoustic map and hardcodes `GlobalRegionId` as the listener's
  region, so it cannot reproduce anything about regions, reverb or occlusion.
- The clap balance is measured, not settled by ear. `logs/clap/applause_400_0.70.wav`; the knobs are
  `Applause.CrackLevel` (2.0), the thump mix (0.30) and `Cupping`.
- The F1 airbox: whether `IntakeNetwork` models `AirboxLitres` / `SnorkelLengthMetres` as a Helmholtz
  volume at all. Untouched since it was filed.
