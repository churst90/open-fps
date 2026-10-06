# The ear model

How loud a sound is to a listener, and how the game uses that to place every sound and to keep its
tone right when it plays quieter or louder than it really is. Written 2026-10-06, before the build;
the numbers marked "estimate" come from a Python prototype and are replaced by measured ones in
changes.md and inbox/ear-model-2026-10-06/README.txt.

## The problem

The loudness law (`Loudness.Place`, a 70 dB pivot, `/levels` 45 % by default) and the engine idle
lift (`EngineVoiceState.LiftDb`, capped at 20 dB) work on unweighted level, dB SPL. The ear does not.

- An idling engine is 77-99 % bass below 100 Hz, where the ear is least sensitive. The law counts that
  bass as if it were heard and pulls the engine down further than what can be heard of it. Measured
  in the game's output (inbox/idle-loudness-2026-10-05): against a talker at 2 m, idling cars are
  4-5 dB(A) short of where the law means them (hatchback) to 9-12 (police car). Moving cars are on
  the law, because at load their bass and their A-weighted level agree.
- A loud sound played quieter than it is (a 100 dB car compressed toward 70) loses its bass and treble
  to the ear: equal-loudness contours are steeper at low level. That is why loud things sound thin.
- The same holds the other way: a quiet sound lifted toward the pivot gains bass it never had.

## What a loudness model has to give us

For a source we know: its declared level (dB SPL at 1 m, unweighted) and, from its own signal, the
shape of its spectrum. We need its loudness (sones, and loudness level in phons) at any level, cheaply
enough to ask for every source on every audio update, off the mixer thread.

## The model chosen

**ISO 532-1:2017, method for stationary sounds (Zwicker), from one-third-octave band levels,
free field.** One loudness evaluation is 28 band levels in and one number out, about 20 table lookups
and powers plus the slope loop over 21 critical bands. It is the method DIN 45631 and most measuring
instruments implement, the standard publishes its code and test signals, and its input is the form
the game already has spectra in. It models what matters here: the absolute threshold, the low-frequency
weighting by level (its table A.3, from equal-loudness contours, so bass counts for less at low
level), the transmission of the outer and middle ear, summation across critical bands (why broadband
noise is louder than a tone of the same level), and upward spread of masking (the slopes).

Not chosen:

- **ISO 532-2:2017 (Moore-Glasberg).** Better at low frequencies and binaural, but it builds an
  excitation pattern over about 370 auditory-filter channels from every spectral component: tens of
  thousands of operations per evaluation against Zwicker's hundreds. The game evaluates thousands of
  times a second. Kept as the reference to check against later if the low end is ever in doubt.
- **Time-varying methods (ISO 532-1 clause 6, Glasberg-Moore 2002 / ANSI S3.4 time-varying).**
  They give the loudness of an impulse as heard, which the stationary method overstates for very short
  sounds. The game declares impulsive sources (a door, a shot) by their event level and placed them by
  that level before; the stationary method is applied to the same declared level with the sound's
  long-term spectrum. The time-varying refinement is listed under later work.
- **ISO 226 contours applied band by band** as a weighting. It treats a broadband sound as a set of
  independent tones: no critical-band summation, no masking. It is the right tool for the tone
  correction (below), not for loudness.

**ISO 226:2023** (normal equal-loudness-level contours, formula (1) and table 1) is used for the
tone correction and the threshold of hearing. Its parameters (alpha_f, L_U, T_f at the 29 preferred
frequencies 20 Hz-12.5 kHz) were taken from two independent implementations that agree to the last
digit (jmrplens/phonometry `contours.py`; ronitsingh10/FineTune `ISO226Contours.swift`). The 2023
contours differ from 2003 mainly below 100 Hz.

Checks the C# implementation must pass (tests): ISO 532-1 annex B.2 test signal 1 (a measured
machinery spectrum) gives 83.296 sone; a 1 kHz tone at 40 dB gives about 1 sone; the ISO 226:2023
formula returns the 1 kHz level unchanged at every phon and the published threshold row; formula (2)
inverts formula (1).

## A sound's spectrum: measured, never authored

Every voice's third-octave spectrum shape (28 bands, 25 Hz-12.5 kHz) is measured from the sound
itself:

- a recorded sound (a footstep take, a speech line): from its samples, once, when it first plays,
  cached by sound;
- a rendered sound (a door, a transient, thunder): from its buffer, when it is registered;
- a live voice (engines, machines, water, fire, rain, horns, bells, sirens, trains, voice chat): from
  its own output, continuously, smoothed over about two seconds. Engines measure their own engine
  sound (without the air brakes and the door beeper, which the lift has never touched).

Band powers come from an FFT, each bin weighted by the response of a third-octave band filter of
order 3 (the IEC 61260 class of filter ISO 532-1 assumes), so a pure tone leaks into its neighbours
as it does through the standard's filters. Low bands are read from a decimated copy (16x, after a
300 Hz low-pass) so the 25 Hz band has bins 3 Hz apart without a 16k FFT.

A spectrum with nothing in it (a voice not yet sounding) has no shape; the source is then treated as
the reference sound below, which is exactly the old law.

## The reference sound and the pivot

The old law's pivot is "a sound 70 dB at its reference distance plays at its own level, whatever
/levels says". In loudness units the same intent needs a sound to say 70 dB of. The reference sound
is **speech at normal effort, ANSI S3.5-1997 table 3** (the standard speech spectrum, 160 Hz-8 kHz,
extended at 12 dB an octave either side). Speech because the mix was balanced against a talker and
the calibration (below) uses one, and because its spectrum is an everyday one.

- 70 dB of the reference speech is **83.4 phon** (20.3 sone) by ISO 532-1.
- A pure 1 kHz tone at 70 dB is 68 phon: the same level, 15 phon quieter. That is loudness
  summation, and it is real.

Why not a literal 70-phon pivot: phon is defined on a 1 kHz tone, and broadband sound at 70 dB is
about 83 phon. A 70-phon pivot would move every broadband source (speech, footsteps, the fountain,
rain) about 7 dB quieter, the whole mix with it. A uniform shift is not what the model is about and
would undo approved balances for nothing; the speech pivot keeps them and moves only what the ear
hears differently from the reference.

## The law in loudness units

### Two ways a level is declared

The game declares levels two ways, and loudness needs the real one (found while building, 2026-10-06):

- A **recording or a render** (every world sound through WorldAudioPlayer, footsteps, birds, beacons,
  near rain drops) is placed by mapping its buffer's FULL SCALE to its declared level at a metre
  (Speech.LevelDb: a speech line sits 28 dB under full scale, so normal speech, 62.35 dB, is declared
  90.35). Whatever a declaration's comment calls the number, that is what `Place` does with it. Its
  real level is the declared level plus where the sound sits in its buffer, measured: a **gated RMS**,
  the mean square of its 125 ms blocks within 20 dB of the loudest (the sound while it sounds; the
  125 ms is the ear's temporal integration, which is why a click is quieter than its peak says).
- A **physical voice** (engines, machines, water, fire, rain patches, sirens, horns, bells, trains,
  the wind at the ears) declares its RMS level and plays it 16 dB under full scale
  (VehicleProfile.PeakHeadroomDb).

Treating a bird call, a beacon and a footfall as declaring their real level (as their comments say)
made footsteps 18 dB louder: the game's balance was built on the full-scale mapping, and it stays.

### The law

For a source S with declared level L (at its reference distance):

1. its real level R = L + its offset (gated RMS for a recording, 0 for a physical voice);
2. the speech line exactly as loud (ISO 532-1, S's measured spectrum at R against the ANSI speech
   spectrum): declared at E = R_speech + 28;
3. the old law places that line: its RMS plays at  K + c (E - C) - 28;
4. S plays exactly as loud as that line, at its own level, in its own digital units.

C is the old ceiling (112 dB at 45 %), c the /levels compression, and K the **designed playback**:
the level at the ear of a 0 dB rendered level at which a normal voice a metre away plays as loud as
life (62.35 dB) at the shipped /levels: K = 100.8 dB (Loudness.DesignFullScaleDb). The reference
distance is where S is as loud as a line declared at the ceiling.

- Speech is placed exactly as before (Loudness.Place returns the old law for the reference).
- A source exactly as loud as a speech line plays exactly as loud as that line, at any /levels, from
  either convention (EarModelTests.AsLoudAsALineIsPlayedAsLoudAsTheLine). The old law's pivot (a line
  declared 70 at its reference) is where the speech-equivalent of every source turns from lifted to
  lowered.
- Everything keeps its order of loudness.
- The two conventions are now consistent at every /levels. Under the old law a physical voice and a
  speech line of the same real level played the same only near 45 % (28c - 12 = 0.6 dB apart at 45 %,
  16 dB apart at 100 %): at 100 % physical voices were 16 dB under real. At the shipped 45 % this
  costs nothing.
- The gain is not held to 1 as the old law's was: a sound the ear hears less of per decibel (an idle,
  a tone) needs more level than speech to be as loud, and at the ceiling that is more than speech's
  full scale.

The correction a source gets over the old law is `PlacedDb(L, S) - PlacedDb(L)`; beyond the
reference distance it does not depend on distance. It is applied as a smooth gain on the voice
(slewed at 6 dB/s, never stepped). The estimates below were made before the build, on the spectrum
alone; the measured results are under "What it changed".

| source (estimate, spectrum only) | change against the old law |
|---|---|
| speech (the game's walker lines) | +0.4 dB |
| own footsteps, concrete | -0.5 |
| fountain | -0.6 |
| hatchback idling, front / rear (spectrum only) | +2.3 / +7.4 |
| saloon idling, front / rear | +1.9 / +9.2 |
| police car idling, front / rear | +4.6 / +10.1 |
| muscle car idling, front / rear | +0.7 / +2.3 |
| diesel pickup idling, front / rear | +5.2 / +3.3 |
| a pure tone 0.5-2 kHz (beacons, UI tones, near-sine signals) | +7.5 to +8.7 |

(The engine rows are the spectrum term alone; the lift below adds what the old cap took off.)
Tonal sources gain most: a sine is 15 phon quieter than speech at the same level, so the law lifts
it more. Beacons have a player level (`/beacons louder`, `/beacons quieter`), so a player who finds
them loud turns them down; this is the change Cody most needs to hear.

## The engine lift is the law

The lift was "however far under its declared level the engine runs, lift it by (1 - c) of that",
capped at 20 dB. In loudness units it is the law itself:

    lift = PlacedDb(L_now, S_now) - PlacedDb(L_declared) - (L_now - L_declared)

(PlacedDb(L_declared) is the old law: how the voice was placed) where L_now is the level the engine is running at (its own pressure, smoothed over half a second, as
before) and S_now the spectrum it is making now. The voice was placed as its declared level of the
reference sound; the lift moves it to where the law puts what it is actually doing, level and tone.

`MaxLiftDb` (20 dB) existed only to bound the unweighted law. Before/after:

- The hatchback idles 38 dB under its declared 99.5 dB: the old law wanted 20.9 dB of lift and the
  cap took 0.9 off. Measured in the game's output the idling hatchback comes up 1 to 1.2 dB, the
  diesel pickup 6, the police car 10 (it was the furthest under its declared level, and its idle
  is 94-99 % bass): see "What it changed".
- The cap is removed. What it also did, by accident, was limit the burst at a voice's first blocks
  (the level estimate starts at zero) and the lift chasing silence. Those get their own rules:
  the level estimate starts from the first block it hears rather than from zero, and when the
  engine's sound is below the threshold of hearing (zero sones) the lift holds where it was: there
  is nothing to place. Below the threshold the law has no meaning, which is a bound from the ear,
  not a number.

## Keeping the tone: loudness compensation

A voice plays at a level the law chose (L_play at the ear) instead of its real one (L_real at the
ear). Both are known per voice: L_real is the declared level spread over the distance and through
the path; L_play adds the law's placement and the playback calibration. Their loudness levels P_real
and P_play (phons, ISO 532-1 with the voice's own spectrum) choose two ISO 226:2023 contours, and the
voice is equalised by their difference, normalised at 1 kHz:

    EQ(f) = [L(f, P_play) - P_play] - [L(f, P_real) - P_real]

so each band is as loud, relative to the rest, as it would be at the real level. Played quieter, the
bass and the top come up; lifted, they come down.

The 2023 contours make this nearly linear in P_real - P_play: per phon of difference, +0.50 dB at
20 Hz, +0.37 at 63 Hz, +0.27 at 125 Hz, about -0.04 at 1.6-5 kHz, +0.15 at 12.5 kHz. Examples:

| real -> played (phon) | 31.5 Hz | 63 Hz | 125 Hz | 10 kHz |
|---|---|---|---|---|
| 90 -> 70 (a car at load, compressed) | +9.4 | +7.5 | +5.4 | +1.4 |
| 80 -> 70 | +4.7 | +3.7 | +2.7 | +0.7 |
| 40 -> 55 (an air conditioner, lifted) | -6.8 | -5.3 | -3.7 | -0.8 |

It is realised as two second-order shelves per voice, a low shelf at 200 Hz and a high shelf at
10 kHz (shelf slope 1), whose gains are fitted to EQ(f) by least squares over the 29 contour
frequencies; the fit is within 1 dB for a 10-phon difference and 2 dB for 25. Corners searched over
60-315 Hz and 4-10 kHz; 200 Hz and 10 kHz fit best.

Bounds: P_real and P_play are held to 20-90 phon, the range ISO 226:2023 specifies (formula (1) is
defined from 20 to 90 phon below 4 kHz; outside it the contours are extrapolations). So a sound near
the threshold is treated as a 20-phon sound and a gunshot as a 90-phon one: nothing is boosted toward
the threshold, and the loudest sounds are not extrapolated. Shelf gains are also held to +-15 dB.
Gains move at most 6 dB a second and the filter coefficients are recomputed per mixer block from the
slewed gains, so there is no step.

Where: a small DSP on every world voice, first in its chain, so the direct sound and the voice's
reverb send are both equalised (a room answers with the source's real tone too). It also taps the
signal for the spectrum measurement. It does two biquads and a copy per sample on the mixer thread,
allocates nothing and catches everything (DspFault). UI sounds and voices with no declared level are
left alone.

## Playback calibration

The compensation needs the absolute level at the ears. The game cannot know the volume knob, so it
assumes and lets the player correct it.

`ClientSettings.ListeningLevelDb`: how loud the player's headphones play the game, as the level,
dB SPL at the ears, of a normal voice a metre away as the game plays it at the shipped /levels.
**Default 62.35** (ANSI S3.5 normal effort at a metre): that voice as loud as life. Conversational
speech is where most people set headphones in a quiet room, and it is well under the 80 dB(A) a
week's listening is referenced to in WHO / ITU-T H.870. A level 5 dB under the default says the
headphones play everything 5 dB quieter than the design (K above), and the compensation gives back
the tone that costs. Held to 40-90.

`/listening` in game opens the calibration:

1. "Listening level. A person will talk to you from one step in front. Set your volume, or use Up
   and Down, until they sound like someone talking to you normally at arm's length. Enter saves,
   Escape cancels."
2. A walker's line plays on a loop from 1 m ahead, at the digital level that is 62.35 dB (ANSI S3.5
   normal effort at 1 m) if the current calibration is right. At the default it is exactly the gain
   the law gives a normal voice a metre away, so the game's own voice is the reference.
3. Up: "Louder" (the voice is too quiet: your headphones play quieter than assumed; the setting goes
   down 1 dB and the voice up 1 dB). Down: "Quieter". Shift with either: 5 dB. Space: the line again.
   R: back to the default.
4. Enter: saved, and said back ("Listening level 58: your headphones play 4 decibels under life, and
   the game gives back more of the bass and treble a quiet sound loses.").
   Escape: the old value is put back.

`/listening 58` sets it directly; `/listening default`. The system volume is the player's: the
calibration changes no level in the mix, only the tone correction (a quieter system gets more bass
and treble back) and the loudness figures the instruments report.

## Ranking

The voice budget ranks on what each voice delivers to the ear (`VoiceManager.Audibility`); it now
adds the voice's loudness correction when the voice's spectrum is known (from an earlier play of the
same sound, or a live voice's latest measurement), so a pure tone and a broadband source of the same
level rank by loudness. Engines are ranked by their declared level as before.

## Cost and threads

- ISO 532-1 evaluation: a few microseconds. The law caches per spectrum and level; a voice
  re-evaluates its compensation when its level at the ear moves more than a quarter of a decibel.
- Spectrum of a recorded sound: one FFT pass over up to 10 s of it, on first play (a few ms once).
- Live voices: one analysis every half second each, on the audio update thread (about 0.4 ms each).
  Engines analyse their own sound on their render thread.
- Mixer thread: two biquads and a copy per sample per voice; no allocation, no throw.

## A/B

`/ear off` and `/ear on` switch the whole model (law correction, lift, compensation) live, for
listening. `OPENFPS_EAR_MODEL=0` starts with it off (what `--game-levels` uses for "before").

## What it changed (measured 2026-10-06)

Through the game's own client audio (AudioLab `--game-levels set=ear`, once with `ear=off` and once
with `ear=on`), measured by `tools/ear_loudness.py` (an independent Python port of ISO 532-1). "phon"
is the loudness level of the output, mapped so the talker at 2 m reads 56.3 dB before. Bass and top
are the 63 Hz and 8 kHz octaves against the 1 kHz octave: what the compensation did. Renders and the
full measurements: inbox/ear-model-2026-10-06.

| source | dB RMS | LUFS | dB(A) | phon before -> after | bass | top |
|---|---|---|---|---|---|---|
| speech, normal, 2 m | -0.8 | -0.8 | -0.8 | 70 -> 69 | -1.4 | -0.6 |
| own footsteps, concrete (takes differ by run) | +0.5 | +1.8 | +1.2 | 64 -> 66 | -6.5 | +5.0 |
| fountain, 5 m | -1.1 | -1.1 | -1.2 | 70 -> 69 | +2.4 | +0.2 |
| window air conditioner, 3 m | -0.6 | -0.5 | -0.5 | 59 -> 59 | -1.0 | -0.1 |
| door, knob, open and close, 2 m | -2.6 | -1.2 | -2.9 | 72 -> 69 | +3.3 | -2.2 |
| hatchback idling, front / rear 2 m | +1.2 / +1.0 | +1.9 / +1.2 | +2.1 / +2.1 | 62 -> 64 / 59 -> 61 | -1.3 / -1.2 | 0 |
| police car idling, front / rear 2 m | +10.0 / +9.9 | +9.7 / +9.9 | +8.1 / +8.5 | 55 -> 65 / 64 -> 74 | +2.0 / +1.8 | 0 |
| diesel pickup idling, front / rear 2 m | +6.2 / +6.2 | +5.9 / +5.9 | +5.4 / +5.5 | 61 -> 67 / 53 -> 60 | +0.9 | 0 |
| hatchback passing, 30 km/h, 7.5 m | +1.6 | +1.5 | +1.0 | 65 -> 67 | +3.5 | +0.4 |
| police car passing, 30 km/h, 7.5 m | +2.9 | +2.7 | +2.3 | 65 -> 67 | +4.1 | +0.1 |
| diesel pickup passing, 30 km/h, 7.5 m | +3.0 | +2.3 | +1.1 | 65 -> 66 | +3.8 | +0.2 |
| thunder, ground strike 3 km | +2.1 | +1.4 | +0.9 | 67 -> 68 | +3.7 | +0.1 |
| rain, moderate (5 mm/h), open street | +2.9 | +2.6 | +2.9 | 70 -> 73 | -6.9 | -3.8 |
| wind at the ears, 2.2 / 4.5 / 7 m/s | the law's own gain: +17 / +13 / +8 dB | | | | | |

Over 2 dB, which Cody approved by ear: the idling police car (+10), the idling diesel pickup (+6),
the police car and the pickup passing slowly (+3), thunder at 3 km (+2, with 3.7 dB more rumble),
rain (+3), the door (2.6 dB quieter, with more bass), and the wind at the ears (a light breeze
+17 dB, a 7 m/s wind +8). Measured on main with the 48 kHz mixer and the true-peak limiter. The wind is bass at a level the ear barely hears, so the law, which lifts what is quiet toward
the pivot, lifts it the most; the captures of it differ run to run because the wind wanders, so the
figures given are the model's own (the [EAR] log line). Measured in loudness rather than LUFS, the
wind at 4.5 m/s was 8 to 19 phon UNDER the talker at 2 m before, not over him as LUFS said.

Other sources the log shows, not in the captures: bird calls about 8 dB quieter (their recordings sit
13 dB hotter in their buffers than speech lines, a difference the old law passed through uncompressed
and the loudness law compresses), and the near rain drops about 12 dB louder (a drop's buffer is
declared at its peak; its 125 ms level, what the ear integrates, is far under that, and the law lifts
what is quiet). Beacons (sine blips) are estimated +7 to +9.

## Later: masking (partial loudness), designed, not built

A sound under a louder one is heard less than its own loudness says: a car idling beside a running
fountain. Partial loudness (Moore, Glasberg and Baer 1997; ISO 532-2's binaural partial-loudness
extension) gives the loudness of a target in the presence of a masker from their excitation
patterns. The design:

1. Each audio update, form the masker spectrum at the ear: the sum of every playing voice's
   third-octave band powers at its playback level (all known from this model).
2. For each voice, its partial loudness against everything else (masker = total minus itself) in
   Zwicker's critical bands: the excitation-domain form of Zwicker and Fastl's partial masking
   (chapter 8) on the specific-loudness pattern, cheap enough at 21 bands.
3. Use it for ranking first (a voice nobody can hear does not need a slot) and only then, if Cody
   wants it, to lift a masked voice that matters (essential voices: speech, own footsteps).

It is not built now because it changes what every voice is worth whenever anything else plays, and
that wants its own round of listening.

## Sources

- ISO 532-1:2017, Acoustics - Methods for calculating loudness - Part 1: Zwicker method. Test signal
  values as reproduced in MOSQITO (Eomys, tests/input/test_signal_1, annex B.2: 83.296 sone).
- E. Zwicker, H. Fastl et al., "Program for calculating loudness according to DIN 45631 (ISO 532 B)",
  J. Acoust. Soc. Jpn (E) 12, 1 (1991): the BASIC program the standard's code follows.
- ISO 532-2:2017 (Moore-Glasberg), considered.
- ISO 226:2023, Acoustics - Normal equal-loudness-level contours, formulae (1) and (2), table 1.
- ANSI S3.5-1997 (R2017), Methods for calculation of the speech intelligibility index, table 3
  (standard speech spectrum level, normal effort; 62.35 dB overall at 1 m).
- IEC 61260-1:2014, octave and fractional-octave band filters (the band shape used to weight bins).
- WHO / ITU-T H.870 (2022), Guidelines for safe listening devices/systems (80 dB(A), 40 h a week).
- B. C. J. Moore, B. R. Glasberg, T. Baer, "A model for the prediction of thresholds, loudness and
  partial loudness", J. Audio Eng. Soc. 45 (1997) 224-240 (masking, later).
- Measured in this game: inbox/idle-loudness-2026-10-05/README.txt (the problem), the --game-levels
  captures.
