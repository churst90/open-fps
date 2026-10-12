---
name: audio-lab-instruments
description: The measuring tools in this repo and which one answers which question — reach for these before changing a constant
metadata:
  type: project
---

Never tune an audio constant without measuring first. The instruments, and what each is for:

**C# lab** (build per [[build-on-tmpfs]], run from `/tmp/openfps-lab/bin/OpenFPS.AudioLab/release`):
- `--voice-levels [preset ...] [sweep]` — **what the GAME'S voice measures at one metre**, against
  what the profile declares, plus what the bay leak, the cooling fan and the tyres are each worth.
  This is the authority; `--engine-levels` is not. See [[live-voice-is-not-the-bench]]. `sweep`
  gives the curve against rpm and the tailpipe alone beside it; `parts` takes the voice APART —
  exhaust, intake and block metered where they are made, body, bay, fan and tyres by muting and
  differencing. `parts` is what found [[the-block-had-no-anchor]].
- `--engine-levels` — the TAILPIPE alone (exhaust + 0.35 × intake), offline. A component
  measurement, not the vehicle. Still the right tool for headroom and crest factor.
- `--spool [preset] [alt=]` — an aircraft's mechanisms against the power lever, at two bearings,
  with what the compressor tone is WORTH (rendered twice, once silenced) and how much of the blade
  row is in its blade-passing line. See [[the-whine-is-buried-not-missing]].
- `OPENFPS_TRACE_SHUTTLE="<name substring>" ./run-server.sh city` — where a shuttle actually is,
  twice a second. Settles "it doesn't move" claims that the client cannot answer.
- `--earshot [map=city] [at=x,z] [top=N]` — everything audible from a spot, ranked, under BOTH
  distance laws side by side, plus what the whole fleet sums to. Reads the map's own vehicle list
  and samples racing lines for lapping vehicles. See [[the-one-thread-through-every-vehicle]].
- `--siren [preset] [sec=]` — the police siren: level against the ten-foot spec figure, the sweep
  rate actually achieved per mode, and what share of the energy lands in the 500 Hz-4 kHz band.
- `--landing [preset] [offset= speed= sec=]` — an arrival: approach, touchdown, rollout. Writes a
  WAV per preset. The touchdown is the flight path reaching the ground, nothing else.
- `--engine-orders <preset> rpm=N` — order content, band balance, centroid, half/whole ratio.
- `--engine-trace <preset>` — rpm/MAP/torque per quarter second. Use for idle hunting and flares.
- `--engine-cost [body=off]` — realtime multiple per voice; the A/B switch measures what a layer costs.
- `--vehicle <preset>` — a full drive, writing SEPARATE STEMS: `v8_exhaust/intake/tyres/block.wav`.
  The block stem exists because a quiet mechanical layer is indistinguishable from one buried under
  a louder intake — that is how the diesels' missing clatter was found.
- `--vehicle-live <preset> [body=off] [shell=off|X] [ring=X] [wide=X] [case=deep] [coupling=X]` —
  a pass-by through the real spatialiser. `OPENFPS_AUDIO_CAPTURE=<path>` taps the master to a WAV.
- `--muscle-rev [same knobs]` — the STATIONARY rev bench. Use this for TIMBRE, never a pass-by:
  Doppler and the level sweep bury a couple of dB of colouration.
- `--body-ir [preset] out=DIR` — a body/case impulse response, its mode table and band balance.
- `--applause [people= intensity= sec=]` — a crowd on its own.
- `--speedway [speedway probe]` — audition the map with no server; `probe` prints what reflects.
  NOTE it hardcodes `GlobalRegionId` as the listener's region and builds no acoustic map, so it can
  reproduce NOTHING about regions, reverb buses or occlusion.
- `--open-air-reverb` — drives the real FMOD graph and asks whether naming a place puts a roof over
  it: reads each region's reverb DSP back OUT of FMOD (decay, wet dB) and meters it. See
  [[region-is-not-a-room]].
- `--reverb-route` — is a room's reverb gated by, and heard through, its doorway.

**Python** (`tools/ingest_audio.py`, no numpy on this machine — there is a hand-written FFT in it):
- `--engine-match FILE [--compare OURS]` — harmonic-to-noise, rolloff, band balance of a recording.
- `--engine-envelope FILE [--envelope-compare OURS]` — finds every steady passage, pools the harmonic
  series, fits and removes the recording's own broadband tilt. **A boomy clip does not need EQ-ing
  first and should not be**: the boom is a smooth curve, the engine is structure, and only bands where
  the passages AGREE across engine speeds are believable.

**Two measurement traps that cost real time:**
1. Comparing two renders needs each spectrum normalised to ITS OWN energy. `VehicleSynth` scales every
   render by its peak, so adding energy anywhere makes every other band read lower.
2. A ratio must not include the thing it is measuring. The "how loud is the can" figure compared the
   can against pipe-PLUS-can and saturated at 0 dB however loud the can got.

See [[synthesis-by-listening]], [[engine-synthesis-state]].

2026-10-07 capture formats: `--game-levels` uses OPENFPS_FMOD_WAV (FMOD WAVWRITER, replaces the card) = the game's
own dithered 16-bit output, fine for listening. OPENFPS_AUDIO_CAPTURE (MasterTap, ahead of the dither) truncated
to 16 bits with no dither; float by default since c2e91385 (OPENFPS_AUDIO_CAPTURE_FLOAT=0 for the old). Runs of
equal samples in a quiet 16-bit render are quantisation, not clamping: check the step size (1/32768).
