---
name: fmod-must-not-virtualise
description: VOL0_BECOMES_VIRTUAL was the popping — a voice here is a chain of stateful filters, and virtualising one stops and resumes them mid-signal
metadata:
  type: project
---

Session 14 (2026-09-18). *"Lots of popping and clicking from the reverb as I walk near the walls."*
Guessed twice and was wrong twice (the diffracted-bearing jump; the reverb send reconnect — the send
crossfade was a real fault and worth keeping, but it was not this). **Found by capturing the mix.**

`./run-gtk-client.sh capture` taps the master to `/tmp/openfps-capture.wav` while it keeps playing.
Analysis of 46 s walking the inside of a room: **no clipping** (0 samples within 2% of full scale, no
flat tops), **no hard voice starts/stops**, but **5 waveform discontinuities** (2nd-order prediction
residual 6-8x the local rms) and **6 two-millisecond holes** inside loud passages. About one artefact
every four seconds, which is what "lots" means in practice.

The mixer log named the cause and had done all along:
`21:07:02 active=5 real=2 playing=5` — three of five voices VIRTUAL, with 256 real channels free.

**`INITFLAGS.VOL0_BECOMES_VIRTUAL`** was set at `_system.init`. It is a sound optimisation for a mixer
playing samples and the wrong flag for this one: a voice here carries a Steam Audio binaural stage, a
three-band EQ, a diffraction low-pass and (for a vehicle) a physically integrated engine — **filters
with state**. Virtualising stops the DSP chain; reviving resumes those filters against a signal that
moved on. And the trigger is volume reaching ZERO, which is exactly what occlusion does when you step
behind a wall and what a reflection does as it fades — so walking along a wall virtualised and revived
voices continuously. Removed. This restates a rule already learned once for engines
([[engine-render-pool]]: a virtualised synthesized engine jumps in pitch when revived) — **do not let
FMOD do the rationing**; [[audibility-ranking]] is what decides.

**Method note worth keeping:** for anything only findable by ear, capture and measure. A V8's exhaust
is a sharp 125 Hz impulse train and a naive envelope-step detector flags every pulse of it — the
detectors that actually separate an artefact from the music are (1) prediction residual against local
rms, for a step inside a running signal, (2) a short hole inside a loud passage, (3) near-full-scale
sample counts and flat-top runs, for clipping.

Same session, also fixed: `TryGetSound` accepted only `OPENSTATE.READY`, and FMOD reports `PLAYING`
for a sound that has a channel on it — so **once any looping sample started, every further request for
it was told "still loading" and parked for ever** (a second emitter, a reflection, a replay after it
went out of earshot). 100 "still not decoded after 3000 ms" warnings for a file that had decoded
instantly. A CREATESAMPLE sound is decoded PCM and any number of channels may play it, so PLAYING
carries the same guarantee READY does.

See [[audio-ghosts-and-stutters]], [[no-ambience-beds]], [[audio-lab-instruments]].
