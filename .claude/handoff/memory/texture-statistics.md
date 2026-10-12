---
name: texture-statistics
description: 10-06 why synthesis sounds "low quality": textures are Gaussian above 4 kHz; real ones have spiky band envelopes (McDermott & Simoncelli 2011); de-grain rounds measured the wrong statistic
metadata:
  type: project
---

Audio-quality agent 10-06 (merged 05ccebf4; docs/AUDIO_QUALITY_2026-10-06.md; tools/audio_quality.py
texture; AudioLab --quality): band-envelope spread/kurtosis at 4/8/12.5 kHz — Gaussian noise ~0.2/3,
our fountain/rain/tree ~0.2-0.3/3.5-5, recordings 0.23-1.05 / 5-75. Cause: ~87k events/s (fountain),
~10k leaf strikes/s sum to Gaussian noise. Same in game and lab, so it is the models.

**The nature rounds 2-3 fitted 10 ms waveform kurtosis in 2-8 kHz — the wrong statistic; they pushed
textures further toward static.** Next texture work must fit envelope statistics (marginal moments
per band, cross-band envelope correlation, modulation spectra) measured on the refs, with fewer,
heavier-tailed events and 0.5-5 ms bursts. Waiting on Cody's go-ahead.

Also fixed there: FMOD linear resampler -> spline (MixerQuality.cs), one-shots float at mixer rate,
EngineProcessor ConsumeRate/SourceClock (front voice/echo skipped whole 1024-sample blocks = clicks),
MasterDither for the 16-bit PulseAudio output, ramps in SteamAudioDsp/TracedEchoes/EarWind.
Open: look-ahead limiter (gunfire/thunder flat-top), decorrelated taps for large sources, 48 kHz mixer.
See [[ear-model]], [[renders-at-game-level]].

Cody 10-06 said yes to all four: texture rounds (fountain, rain, trees, fire), a look-ahead true-peak
limiter, a bigger fountain with rocks the water splashes over (gen_city.py + model, several taps),
and the mixer at 48 kHz. Two agents started: textures+fountain (inbox/textures-round1-2026-10-06),
limiter+48k (inbox/limiter-48k-2026-10-06).

**Round 1 MERGED 10-06 (pushed after 73458adf):** tools/texture_stats.py + C# TextureStatistics, AudioLab
--textures; fountain 14/14 stats in range (11 m basin, 8 boulders rock_boulder Concrete, 5 taps
water:park_fountain/elm_park/<tap>, 75 dB, 7.6 % core); rain clustered flux; tree heavy-tailed gusts
(steady wind 14/14, game wind field still too gusty 4/14 — slow eddies, wind field's area); fire fizz.
Renders inbox/textures-round1-2026-10-06 at game level (quiet: fountain 2 m -44 dBFS, tree 3 m/s -65).

10-06: Cody: fountain "much better", rain still "low bit rate, crunchy". Measured: rain band levels fine, but
4-16 kHz WAVEFORM kurtosis in 10 ms windows 9.0 street moderate / 7.2 heavy vs refs 3.1-4.3 (in-car 3.6 ok):
needle-sharp drop clicks. Fountain got better because its clicks became 2-4 ms splashes. BOTH scales matter:
10 ms waveform kurtosis ~3-4 (no sample-sharp clicks) AND long-window envelope statistics (spiky events).
Rain round 2 sent to the texture agent (impact pulse duration D/v, HF from secondary splash/film bubbles).
Cody 10-06: shelter roof rain sounds like light rain at every rate while outside sounds heavy — suspect NearDrops
~12/s cap and the plate energy cap (4ff2a94e); sent to the rain round 2.

10-06: Cody: textures round 1 "still sounds bit-crushed, like 16/22 kHz". NOT bandwidth (content to 23 kHz)
or bit depth. Renders are effectively MONO: L/R correlation tree 1.00, fountain 0.97, rain ~0.6 — one point
source (or one synth feeding 5 taps) gives both ears the same noise = narrow, in-head, harsh. Agent started:
extended sources as several INDEPENDENT streams over their real extent (not copies), measured by interaural
coherence through the game's HRTF path; inbox/wide-sources-2026-10-06. Ear model merged+pushed f5606602.
Wide sources MERGED+pushed 10-06: ExtendedSources.cs — tree 7 places, fire 4, fountain taps 4 each, rain roof
4 parts + near quarters 2; independent events per place; spread by angle (2.5-6 deg); <=36 place voices
(OPENFPS_PLACE_VOICES), OPENFPS_WIDE_SOURCES=0 off. IACC tree 2 m 0.9 -> 0.1-0.3. Open: fountain rim jets
want their own kerb taps; machines still one point.
**APPROVED by ear 10-06:** Cody: "the rain sounds way better now, sleet, everything" (after rain round 2 +
wide sources). The recipe works: physical impact pulses (no sample-sharp clicks), envelope statistics
fitted to refs, and extended sources as independent streams. Use it for every new texture.
