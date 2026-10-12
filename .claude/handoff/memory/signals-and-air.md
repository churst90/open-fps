---
name: signals-and-air
description: Horns, whistles, bells and compressed air — approved by ear 2026-09-18, and the four measurement failures that got them there
metadata:
  type: project
---

Built 2026-09-18, commit `224ad05`. `OpenFPS.Common/Signals.cs` + `Pneumatics.cs`,
`AudioEngine/Core/Signals/` (`ChimeHorn`, `SteamWhistle`, `StruckBell`, `Mode`, `HeadShadow`) and
`Core/Pneumatics/AirSystem.cs`.

**Cody's verdicts (these are the baseline — do not re-tune without a listening test):**
whistles *"definitely way better, perfect in fact, excellent, keepers"*; Leslie RS3L *"sounds
great"*; air brakes *"sound great"*; trains *"sounds great actually"*.

**Four failures worth remembering, because three of them recurred:**

1. **A self-oscillating physical model that does not oscillate sounds like filtered noise and
   measures as its own resonance.** The horn reported 14.7 kHz while hissing. Both the horn and the
   whistle failed this way. Fix: `MeasuredHz` by AUTOCORRELATION around the expected note (zero
   crossings count the noise, not the note), plus a crest factor and a shut-fraction, printed for
   every bell. Both now use an oscillator LOCKED to the column rather than one that finds it —
   documented as a deliberate simplification, since entrainment is a fact about real horns.
2. **A pipe's input impedance is RESISTIVE at resonance** — `Mode` must be a bandpass, not all-pole
   (which lags 90° and stops the loop dead). And an outward-striking reed only pumps energy in when
   driven ABOVE its own resonance (`ReedRatio < 1`, the brass-player's-lips condition).
3. **Retuning a filter must not drop its state.** Rebuilding the whistle's modes as the steam warmed
   clicked forty times a wail: *"lots of white noise and it sounds crackly"*. `Mode.Retune` keeps
   the delayed samples.
4. **Jet noise has nothing to do with the note.** Banding the whistle's breath around its
   fundamental made it *"sound like under water"*; it is a 3 mm gap at 400 m/s and it is broadband
   and bright. And a 6% continuous pitch wobble is *"not like solid notes"* — under 1%.

**Anchors:** horn 139 dB at 1 m (legal 110 dBA at 100 ft); **bells are RMS WHILE RINGING, not the
peak of one blow** — anchoring the peak put them 25 dB down and made a locomotive bell inaudible
under its own train; air jets −15 dB against Lighthill K=1e-4, the same sign and nearly the same
size as the aircraft's trims (see [[aircraft-model]]).

**Binaural** (`HeadShadow`, `--crossing`, `--train binaural`): the interaural TIME difference is not
applied, it happens — the arrival-time deposit already gives it. Level is inverse distance plus
Brown-Duda spherical-head shadow per ear. **No pinna, so no elevation and no front-back.**

See [[rail-model]], [[synthesis-by-listening]], [[audio-lab-instruments]].

**2026-09-24, air horns weak and breaky at both ends — FIXED; ALL horns APPROVED by ear 09-24:
car, truck, bus and train horns "these are good", trains "perfect" (inbox/air-horns-2026-09-24, train-horns-2026-09-24). Do not re-tune.** Cause: a 6.5% pitch bend at low
supply (plus 8x jitter) drags every harmonic off the column's ~3%-wide modes, so level drops in
and out during swell and fade; and a slow valve (90/130 ms) is heard as "weak on the start and end"
(the chime hung 15-25 dB down for 0.5 s). Now `ChimeHornSpec.PitchBend` 0.008, rise 0.025, fall 0.03
by default. Also fixed: Bell.Step returned 0 below 1e-3 supply and cut the column dead ~35 dB down
(now `RingOut`); the column state froze between blasts; bell StartDelay counted from construction,
so the chord stagger never played. Measure horn envelopes in 20 ms windows — 4 ms windows on a
multi-bell chord measure its beating, not the envelope. Tests: RailAndSignalTests.AirHorns*.

**2026-09-25 RETUNED at Cody's request (commit 4c0e5f0)**, sourced by research: K5LA D#4 F#4 G#4 B4 D#5
(top two were wrong); RS3L C4-D#4-A4; two_tone = UIC 644 370/660; NEW lrv_two_chime 311+440 on the
light rail; truck_dual an octave down 173/228 (roof trumpets 150-250 Hz); bus_horn 400. Lengths are the
model's EFFECTIVE lengths (c/2L cone) for the measured notes — a real flared reed horn speaks nearer c/3L.
