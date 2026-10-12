---
name: a-source-has-a-size
description: "Why the grandstand was inaudible — transients were placed as points and range-capped at 250 m; fixed 2026-09-16 (session 9)"
metadata:
  type: project
---

The speedway's crowd was emitting the whole time — ninety reactions a minute across eleven of twelve
blocks at 114-118 dB — and could not be heard. Nothing was wrong with `CrowdSystem`.

- **A transient's range was capped at 250 m** (`WorldAudioPlayer.MaxRange`) and the mixer fades a voice
  to nothing over the LAST QUARTER of its range, so everything past 187 m was fading out for being far
  away. The stand is 219 m from the spawn. A range shorter than the level can carry is a silence, not a
  saving; `Loudness.AudibleRange` sets it now and the server's per-map earshot bounds the cost.
- **A crowd was placed as a point.** `TransientSound.ExtentMetres` + `Loudness.Place(level, extent)`.
  400 people at half a square metre each fill a patch ~8 m across. THE GAIN MUST COME DOWN TO PAY FOR
  THE WIDER REFERENCE — a distributed source and a point source of the same power are identical in the
  far field, so widening the reference alone just makes it louder than physics allows. Hold
  `gain * reference`; only the near field changes.
- **The applause key carried unrounded floats**, so the quantisation `WorldAudioPlayer.IdFor` does for
  every other sound never reached the escape hatch. `Applause.Quantise` fixes it — but the churn was
  MUCH smaller than first claimed: the crowd's intensity is built from two saturating terms, so a steady
  race produces ~15 distinct buffers a minute quantised and ~22 unquantised, not 90. Measure before
  claiming a saving; a count-based test passed whatever the quantiser did.
- **Transients now reflect**: `WorldAudioPlayer.QueueReflections` runs the same `ImageSource.FirstOrder`
  search the cars use over `EngineReflections.Surfaces` and queues the same sound again at the moment it
  arrives. A cheer comes off the wall behind the seats 53 ms later. Gunshots get it free.

**Transient reflections, three traps found by ear the same evening:** a transient's voice id was a hash
of the SOUND, and the id is what `VoiceManager` keys a submission by — so a sound and its own reflection
replaced each other (heard as "I only hear the reflections"); that hash band (−1,000 to −1,001,000)
crossed the engine-echo band at −600,000 and the borrowed-voice band at −700,000, so a footstep could
take over a car's reflection voice (heard as a reflection of a bike that had long gone); the delay was
counted twice, because `AudioEngineFacade.Submit` already delays every submission by its own distance
over the speed of sound. And the threshold must be `ImageSource.EchoAudibleRatio` (20 dB, one-shots) not
`MinGain` (34 dB, continuous sources): a ratio is what ties the echo to the loudness of the thing.

`Loudness.RenderedGain` now holds the mixer's distance law (inverse from the reference, then a fade over
the last quarter of the range) so a test can ask what the mixer will do to two sources without a sound
card. That is the only way a balance question can be settled: `CrowdAudibilityTests` compares the
rendered ratio against the physical SPL ratio.

**Note for level work:** engines are placed with `max(reference, 3 m)` and NO gain compensation, which
lifts every car about 6 dB above what `Loudness.Place` intends. Deliberate, settled by ear, and the
reason the crowd/bike comparison lands 3.6 dB out rather than 0.

See [[region-is-not-a-room]], [[speedway-map]], [[no-special-cases]].

**A one-shot that does not win a voice must be DROPPED, not queued.** `VoiceManager` keeps a submission
until it has been observed to start and then stop — right for a car, wrong for an event: a clap that
lost the budget this frame cannot be played later, because later is a different moment. Left in, stale
one-shots fired whenever the listener moved somewhere that made their score win, from mirrored
positions computed for a place they had walked away from (heard as "reflections piling up where nothing
is happening" and "the crowd is coming from the inside of the oval"). Transients now set
`SpatialEmitter.IsEvent` and Process drops an event that did not win a slot. It was invisible while
transients shared voice ids by SOUND — the next footstep overwrote the stale entry, so the queue stayed
small by accident. `VoiceManager.SubmissionCount` is the gauge.

**The clap itself (2026-09-16 late).** "Sounds like a crinkling bag": the clap had no cavity RING (a
sharp resonator had been rejected as "pouring water" and replaced by a plain tilt — the question is the
DAMPING, Q≈3, not whether it resonates) and no flesh THUMP (which is what gives it a tail: −49 dB at
30 ms with it, −66 without). Per octave: was 31/45/24 below-200 / to-1.5k / above; now 16/74/11. Air
takes the 4-12 kHz half away over 200 m, so a clap made only of edge arrives across a stadium as a
crinkle BY CONSTRUCTION — the body is what makes it audible at distance. Measure per OCTAVE
(`VehicleBody.Bands`), never per hertz: the ten kilohertz above 1.5 kHz swamp the two octaves where
hands are, and a paper bag measures as well balanced.

**Rendering a transient took 170 ms on the game thread** (a thousand claps a second to synthesise). It
happens on a worker now; the event that discovers a new sound is dropped, not delayed.

