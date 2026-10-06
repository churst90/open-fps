# Audio quality: why synthesis sounds grainy, static or "low bitrate" (2026-10-06)

Cody, 2026-10-06: the synthesis sounds scratchy, grainy, staticy, low bitrate, not high quality.
Several models (fountain, trees, rain, thunder) each needed rounds of de-graining. Was something
systemic wrong?

Yes, in two places. The largest cause is in the synthesis itself and shows in the lab files as much as
in the game. Below that, several stages of the mixer added artefacts of their own. The mixer faults
are fixed on branch `audio-quality-2026-10-06`. The synthesis cause is a recommendation, because it
changes approved sounds and needs listening rounds.

## How it was measured

- `OpenFPS.AudioLab --quality ...` (QualitySpike) plays known signals and typical scenes through the
  real FmodAudioProvider. It captures the master in 32-bit float, before and after the master
  limiter, plus the 16 bits the output is handed.
- `tools/audio_quality.py` works out the numbers from those captures:
  - `resampler`: tone purity per resampler.
  - `orbit` and `blocks`: steps at the 1024-sample block.
  - `jumps`: discontinuities.
  - `scenes`: what the limiter did.
  - `texture`: spectrum and band-envelope statistics.
  - `check`: level, peak, clipping and gaps.
- Files given to a listener were checked with `audio_quality.py check`.

## Ranked findings

| # | Suspect | Measured | Audible | Fix | Cost |
|---|---------|----------|---------|-----|------|
| 1 | Textures are Gaussian noise at the top. Fountain, tree and rain are sums of tens of thousands of similar small events. | Band-envelope spread at 4k / 8k / 12.5k: fountain 0.24 / 0.19 / 0.17, rain 0.26 / 0.23 / 0.21. Gaussian noise is 0.22 / 0.16 / 0.12. Recordings are 0.23-1.05, with kurtosis mostly 5-75 against the renders' 4-5. Same in the direct renders and through the mixer. | Yes. Most likely the "static". | Recommendation: refit the models on envelope statistics, with fewer and louder high-frequency events. | Model rounds by ear |
| 2 | A car's reflection and its front voice read the car in whole-block jumps. | Front voice 3.6-7.8 discontinuities a second on a car passing at 60 km/h. Reflection 0.5-0.8 a second. Street scene 0.43-0.61 a second before, 0.09-0.13 after. Unit test: front 76 / reflection 7 before, 0 / 0 after. | Yes: clicks and crackle on traffic. | Fixed: a continuous source clock. | Small |
| 3 | FMOD's linear resampler, on every moving voice (Doppler) and every 48 kHz / 24 kHz buffer. | THD+N at 5 kHz: -38 dB (48k) and -35 dB (Doppler 1.004); spline -71 / -65. The top octave's level wobbles 16.9 % against noise's own 11.3 % (spline 12.4 %), and 16-20 kHz loses a further 3.7 dB (spline 1.5 dB). Even spline images 10-20 kHz content 3.9 kHz down at -24 to -36 dB. | Likely, on bright moving sounds and on one-shots. | Fixed: spline; one-shots and thunder brought to the mixer's rate band-limited. | Free; about 4 ms of worker time per second of audio |
| 4 | One-shots registered as truncated 16-bit. | Error -95 dB under each buffer's own peak. A tail 72 dB down has its error 23 dB under it; 88 dB down, 7 dB. Below about -100 dB the tail is cut to digital silence. Renders over full scale clipped. | Yes, in the tails of rings, doors and gun trails. | Fixed: float. Also UI sounds, beacons and driving aids. | Free |
| 5 | The lab's own files. | Thunder round 2 was upsampled 24 to 48 kHz linearly: its 20 kHz third-octave stood 26 dB over the 12.5 kHz one (now -114 dB). Rain round 3 is 16-bit at game level: snow is -69 dBFS RMS, 40 % of samples within 4 steps of zero, 32 dB over the step floor. Nature round 3 is 16-bit at -24 to -27 dBFS (fine). | Thunder: possibly a faint sizzle. Quiet rain files: yes, as grain once turned up. | Thunder fixed in ThunderSpike. Rain: recommendation (write float or 24-bit). | Small |
| 6 | Master limiter. | Idle in the park and street scenes (0 dB gain reduction). Thunder at 3 km: up to 7.9 dB, with flat tops at exactly -2.0 dBFS. Cody's 16-minute capture: 975 flat-top runs in 0.8 % of 100 ms blocks, around gunfire and storm. | Yes on gunfire and thunder cracks; no elsewhere. | Recommendation (the master chain belongs to the loudness work): a look-ahead true-peak limiter, or a soft clipper ahead of it. | Medium |
| 7 | FMOD hands PulseAudio 16-bit at 44.1 kHz, undithered. PipeWire then converts to its 48 kHz float sink. | A tone two steps tall came out as 3 values, with H3 at -16 dB. With dither its harmonics sit at the noise floor. In ordinary scenes the error is already white (park: coherence 0.003). | Only for what is in the last few bits: far sounds and tails, at high volume. | Fixed: triangular dither, one step, last in the chain. Raises the floor from -101 to -96 dBFS. Off for the lab's WAV writer, whose files are read for exact silence; OPENFPS_DITHER=0 or 1 decides. | Free |
| 8 | Extended sources are placed as one point. | Fountain at 2 m: ears correlated 0.92. Rain renders: 0.55-0.63. | Likely: a fountain or a crown heard as a speaker. | Recommendation: several decorrelated taps across the source's extent. | Medium |
| 9 | Per-block gain and filter steps. | Spatial blend, traced echo input gain and ear-wind knee stepped every 1024 samples; the knee moved 21 % of its gap per block. | Small; the knee on head turns. | Fixed: ramped per sample, or every 32 samples for the knee. Leftovers below. | Small |
| 10 | The boundary DSP ran at the sound card's rate, not the mixer's. | Delays 9 % long on a 48 kHz device. | Subtle | Fixed | Free |
| 11 | Steam Audio binaural frame steps. | Orbit at 360°/s: residual -95 dB under a 300 Hz tone; 90°/s: -103 dB; still: -107 dB. | No | None. Steam Audio crossfades. | |
| 12 | Voices' SoftCeiling. | 0 % of samples bent at cruise for nine engines; fire 0.001 % (-77 dB). | No | None | |
| 13 | Noise generators. | System.Random (31-bit doubles) and xorshift generators, nothing shared between threads. | No | None | |
| 14 | Denormals. | No flush-to-zero anywhere; glides toward zero and ringing resonators can sit on subnormals. Not measured. | Indirect (CPU, then starves) | Recommendation: measure | Small |

## 1. The textures are noise, not events

Followed up in texture round 1 (changes.md, 2026-10-06; inbox/textures-round1-2026-10-06): the
fountain, rain, trees and fire refitted on McDermott-Simoncelli statistics (tools/texture_stats.py,
TextureStatistics), and the fountain given five voices across its extent (item 8).

This is the systemic cause, and it is not in the mixer. The nature round 3 files were written straight
from the synths: 48 kHz mono, no FMOD, no HRTF. The fountain through the whole mixer, standing still,
has the same statistics: 0.27 / 0.23 / 0.21 at 4k / 8k / 12.5k against 0.24 / 0.19 / 0.17 direct.

What makes rain sound like rain, rather than noise with rain's spectrum, is how each frequency band's
envelope moves (McDermott and Simoncelli, 2011). Measured with `audio_quality.py texture`: Hilbert
envelope, 100 Hz smoothing, std/mean and kurtosis per third-octave band.

| | 4 kHz spread / kurtosis | 8 kHz | 12.5 kHz |
|---|---|---|---|
| Gaussian noise | 0.22 / 3.1 | 0.16 / 3.1 | 0.12 / 3.0 |
| Fountain round 3 | 0.24 / 3.7 | 0.19 / 3.9 | 0.17 / 4.8 |
| Fountain recordings (3) | 0.38-0.93 / 4.7-93 | 0.30-0.72 / 3.0-19 | 0.23-0.60 / 3.2-12 |
| Tree round 3, 6 m/s | 0.31 / 3.6 | 0.27 / 3.3 | 0.25 / 3.3 |
| Wind-in-leaves recordings (3) | 0.37-0.73 / 7-57 | 0.34-0.74 / 3.7-29 | 0.31-0.76 / 5-71 |
| Rain round 3, street moderate | 0.26 / 4.2 | 0.23 / 4.9 | 0.21 / 5.1 |
| Rain recordings (5) | 0.33-0.95 / 4.4-37 | 0.30-0.98 / 4.7-75 | 0.26-1.05 / 2.7-55 |

The renders' top half is as steady as Gaussian noise. The recordings move 1.5 to 5 times as much, with
far higher kurtosis: separate, audible events. The fountain model makes about 87,000 events a second
(63,000 drops, 6,000 lumps, 18,000 bubbles: nearly two per sample at 48 kHz); a tree makes 7,600 leaf
strikes a second, 12,000 in a gust. With that many events of similar size, the sum in every band is
Gaussian (the central limit theorem). Gaussian noise is what "static" is. The de-graining rounds took
out single loud samples (clicks) and did not change this: the fountain's round 2 and round 3 files
measure the same at 8 and 12.5 kHz (0.20 / 0.17 and 0.19 / 0.17).

What a real texture has instead: the energy in each band comes from a minority of loud events. Only some
drop impacts entrain a bubble. Large drops carry most of the impact energy. A leaf rustle is twig
episodes, not a steady rate. Each event is a burst a few milliseconds long, not one sample.

Recommendation, for the models' next rounds:
1. Fit each model to the recordings' envelope spread and kurtosis per band, as well as their spectrum.
   `audio_quality.py texture` prints both for a render and for the reference files.
2. Give event amplitudes heavy-tailed distributions, and let the loud tail carry the high frequencies.
   Cut the number of events that matter at the top: most of the 63,000 drops should be inaudible
   there.
3. Make the high-frequency events band-limited bursts of 0.5-5 ms with soft onsets, not near-Dirac
   impulses. `EventSum.Impact` is a Gaussian with sigma about 0.7 samples at integer positions, -21 dB
   at Nyquist, so its sampled spectrum folds back as well.

### What the lab files went through

Cody heard the same "low quality" in lab files that never touched the game's mixer, so each folder's
path was checked:

| Folder | Path to the file | Format | Shared with the game |
|---|---|---|---|
| nature-round3-2026-10-05 | NatureSpike: the synth straight to a buffer, one gain per pair | 48 kHz mono, 16-bit, RMS -24 to -27 dBFS | The synth only |
| rain-round3-2026-10-06 (branch worktree-agent-a1ed680f4a07e0ec3) | RainSpike: the synths at 48 kHz, the loudness law and the provider's measured gain, amplitude panning (no HRTF), an emulation of the master limiter, written as truncated 16-bit | 48 kHz stereo, 16-bit, RMS -35 to -69 dBFS | The synth, the loudness law, undithered 16 bits |
| thunder-round2-2026-10-05 | ThunderSpike: Thunder.Render at 24 kHz (far) or 48 kHz (near), linear interpolation to 48 kHz, Steam Audio's HRTF offline | 48 kHz stereo, 24-bit | The synth, and a linear resampler like FMOD's old one |

The one stage all three share is the synthesis, and the texture statistics above are the same with
and without the mixer. Two smaller shared faults are now fixed for the game and for the thunder files:
- The quiet rain files carry the undithered 16 bits the game's output also had. Snow at -69 dBFS RMS
  is only 32 dB over the step floor.
- The thunder files have the linear interpolator's images: above 16 kHz was -60 dB of the total, and
  is -103 dB band-limited (`--quality thunderfile`).

## 2. Reading a car in whole blocks

FMOD resamples a pitched DSP channel by calling the DSP more or fewer times per mixer block, 1024
samples each time. Measured: a car closing at 60 km/h was called 4.9 % more often, never with a
different length. So a car's play position (`EngineVoiceState.Played`) moves in whole blocks.

- A reflection (`EngineEchoState`) read a fixed distance behind Played, so it skipped or repeated
  23 ms of the car at every extra call.
- The front voice (`EngineTapState`) stepped up to ten samples toward Played at nearly every block.

Both now read on a continuous clock that runs at the car's channel pitch
(`EngineVoiceState.ConsumeRate`, set by the provider with the pitch). The clock leans on Played through
a half-second average, at most 0.5 % in rate (`SourceClock`). The borrowed voice uses the same clock
for its target. It still clamps when its source's Doppler differs from its own by more than the 1 %
it may correct: 1-1.5 discontinuities a second in the lab's pass at 3 m, unchanged. Borrowed voices
are distant cars.

## 3. The resampler

`ADVANCEDSETTINGS.resamplerMethod` was never set, so FMOD used linear interpolation. Tone ladders
through one voice, THD+N (20 Hz-20 kHz):

| Tone | 48k linear | 48k cubic | 48k spline | Doppler 1.004 linear | Doppler spline |
|---|---|---|---|---|---|
| 1 kHz | -63 | -88 | -91 | -64 | -116 |
| 5 kHz | -38 | -52 | -71 | -35 | -65 |
| 10 kHz | -19 | -28 | -36 | -21 | -35 |
| 15 kHz | -18 | -21 | -25 | -12 | -18 |

White noise at a walking listener's Doppler:
- Linear: the 12-20 kHz level moved 16.9 % from one 3 ms frame to the next (11.3 % at pitch 1), and
  16-20 kHz sat 9.4 dB under 1-4 kHz against 5.7 dB at pitch 1 (the HRTF's own tilt is the 5.7).
- Spline: 12.4 % and 7.2 dB.

Spline costs nothing measurable (mixer DSP 2-4 % either way). Above 10 kHz every polynomial resampler
images, so the synthesised one-shots and thunder are now brought to the mixer's rate on their render
worker (`MixerQuality.Resample`: Kaiser-windowed sinc, 64 taps a side). A stationary one-shot now
plays at pitch 1 with no FMOD resampling at all. Thunder in the mixer: above 16 kHz went from -70 to
-86 dB of the total. One-shots: 16-20 kHz is 2-3 dB brighter, which is the render as made; it was
being low-passed.

## 6. The master limiter (recommendation only)

The "Mix loudness" line's peak was FMOD's maximum since the meter started, so after the first
full-scale moment every line read -0.0 dBFS (320 of 391 lines). The line now gives the peak of the
interval since the last one.

Measured before and after the limiter:
- Park and street scenes: no gain reduction.
- Thunder at 3 km: up to 7.9 dB, 0.5 % of the time, with samples flat at exactly -2.0 dBFS. FMOD's
  limiter does not look ahead, so a crack's leading edge is clipped.
- Cody's 16-minute capture: flat tops at the ceiling in 80 of 9,541 blocks of 100 ms.

That is crunch on gunfire and thunder, not the everyday grain. A look-ahead true-peak limiter, or a
soft clipper before it, would take it out. That decision belongs with the loudness work.

## 7. The output

While the lab ran, `pactl list sink-inputs` showed FMOD's stream ("FMOD Audio", OpenFPS.AudioLab) as
`s16le 2ch 44100Hz`, resampled by PipeWire into its `float32le 48000Hz` sink. The conversion has no
dither (`--quality lsb`). A triangular dither of one 16-bit step now runs last in the master chain.
`OPENFPS_FMOD_OUTPUT=alsa|pulse|wasapi` is a lever for trying FMOD's other outputs; ALSA opened no
PipeWire stream here.

## 8. Extended sources

A fountain, a tree's crown and a field of rain are many sources over metres, heard with partly
different signals at the two ears. As one point in front of the listener they are nearly identical
at both ears: 0.92 for the fountain at 2 m. Recommendation: give extended sources several
decorrelated taps across their extent (as the train has one tap per bogie), or one decorrelation
stage scaled by the angle the source fills.

## Other recommendations

- Leftover per-block steps: FoliageSynth retunes its shedding resonators and their gain once a
  512-sample control block (at most about 2 % a step). AircraftSynth and SmallMachineSynth apply gains
  from 64-sample slow ticks. VehicleSynth's squeal recomputes coefficients every 64 samples. All are
  small; ramp them if a model is reopened.
- Denormals: measure the producer threads' time on a voice ringing down to silence. If it rises, add a
  tiny offset or flush values under 1e-20 in the glides and resonators listed in the survey
  (GroundReflection, BoundaryProximityProcessor, Modes, the all-passes).
- The mixer at 48 kHz: devices and PipeWire run at 48 kHz, the renders are 48 kHz, and the mixer is
  44.1 kHz. Moving it means every hard-coded 44100: Steam Audio's IPLAudioSettings in five places,
  TracedReverbDsp, EarDecorrelator, EarlyCopies, SynthProcessor, GranularProcessor, AmbisonicBedDsp,
  and the traced reverb, echo, late-field and simulator defaults. Worth doing once; not urgent now
  that nothing plays through a polynomial resampler unless it moves.
- Lab renders: write float or 24-bit at game level (the rain spike writes 16-bit). Never upsample with
  linear interpolation; use `MixerQuality.Resample`.
- GranularProcessor read one sample past the end of a grain near the end of its buffer (fixed).
  Nothing in the game uses it today.

## What changed (branch audio-quality-2026-10-06)

- `MixerQuality`: resampler SPLINE (OPENFPS_RESAMPLER), the mixer rate, `Resample` (band-limited),
  the output lever.
- WorldAudioPlayer: one-shots and thunder in float at the mixer's rate.
- BeaconAids, DrivingAids, UI sounds: float.
- EngineProcessor: `ConsumeRate`, `SourceClock`; reflections, front voices and borrowed voices read
  on it.
- SteamAudioDsp: spatial blend ramped. TracedEchoes: input gain ramped. EarWindSynth: knee and
  buffeting glide every 32 samples.
- MasterDither, last in the chain, for a sound card (OPENFPS_DITHER=0 or 1 overrides; off for the WAV writer).
- MasterTap: float captures and a capture before the limiter (OPENFPS_AUDIO_CAPTURE_FLOAT,
  OPENFPS_AUDIO_CAPTURE_PRE).
- Mix loudness: interval peak.
- Boundary DSP at the mixer's rate. GranularProcessor read clamp. ThunderSpike files band-limited.
- Tests: MixerQualityTests, EngineReadContinuityTests. BorrowedVoiceDopplerTests now sets ConsumeRate,
  as the provider does.

A/B renders: `inbox/audio-quality-2026-10-06` (README there).

## Done later on 10-06: the limiter (finding 6) and the 48 kHz mixer

The master limiter is now `MasterLimiter` (TruePeakLimiter):
- Look-ahead of 2 ms, with a smooth attack spanning it.
- True peak per ITU-R BS.1770-4 Annex 2.
- Both ears at one gain.
- Ceiling -1 dBTP.
- Release depends on the material: held 25 ms, then fast (40 ms) down to the average reduction, and
  slow (500 ms) from there.

The makeup is unchanged. The added latency is 2.1 ms (101-102 samples at 48 kHz). Every capture after
it (the post capture, the loudness meter) is that much later than the pre capture; `audio_quality.py`
lines them up by cross-correlation. OPENFPS_LIMITER=fmod is the A/B.

Through the real mixer (`--quality limiter`, `audio_quality.py limiter`):

| Tones 6-12 dB over | FMOD's | TruePeakLimiter |
|---|---|---|
| 50 Hz THD+N | -26 dB | -146 dB |
| 1 kHz THD+N | -50 dB | -149 dB |
| SMPTE IMD | -31 dB | -153 dB |
| CCIF IMD | -46 dB | -152 dB |

- Output true peak: FMOD's reached -0.88 dBTP, over its own -2 ceiling. The new limiter stays within
  -1.00 to -1.17 dBTP.
- Shots: FMOD's reached +0.02 dBTP between samples. The new limiter stays at -0.92 dBTP.
- Flat-topped runs on thunder: 24 before, 0 after.

The mixer runs at 48 kHz (MixerQuality.DefaultRate; OPENFPS_MIXER_RATE overrides).
- Every hard-coded 44100 in the list above now reads the mixer's rate.
- Per-sample constants chosen at 44.1 kHz are carried to the voice's rate by At44k. An audit found
  about 25 of them: decays, one-pole steps and slews.
- FMOD's PulseAudio stream is s16le 48000 Hz into a 48000 Hz PipeWire sink, so nothing resamples it.
- Footstep, weapon and bird recordings are 44.1 kHz files. FMOD's spline resampler plays them at the
  right pitch. Speech, voice chat (Opus) and every render are 48 kHz.
