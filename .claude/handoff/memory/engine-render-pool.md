---
name: engine-render-pool
description: Vehicle engines render on a worker pool ahead of the mixer, not inside the FMOD callback
metadata:
  type: project
---

Since 2026-09-14 the physical engine synthesis does NOT run in the FMOD DSP callback. `EngineRenderPool` renders every `EngineVoiceState` ahead into its own ring buffer across all cores; the callback only copies out (`Consume`). Before this, every car was integrated serially on the mixer thread — 12 voices pegged `dsp` at 100% on a 24-core machine. After: 60 voices at 17%.

Rules that were learned the hard way and must not be undone:
- **A starved block is a GAP, never a delay.** `Consume` advances the play position by the WHOLE
  block whether or not the ring could fill it, and `Produce` resyncs `_written` to `_played` when the
  consumer has run past. Taking only what is there and keeping your place makes the voice play slow —
  900 of 1024 samples is 88 % speed, a tone and a half flat — and walks the car's sound further and
  further behind where the car is. Heard as cars "pitch correcting" on a pass, the whole field
  seeming to slow down together, and thirty cars smearing into a wash. None of it sounds like a
  dropout.
- A new voice hands out SILENCE until primed, and is warmed for 0.1 s with its output discarded (cold waveguides are a pressurisation transient). Skipping either makes a map load crackle.
- `FadeOutEngine` must be paired with `ReviveEngine`. A faded voice that regains its slot is otherwise silent forever — heard as cars that stop passing while the distant pack still circulates.
- Never cut an engine voice: it has no zero-crossing. Fade via `TargetEnvelope`/`FadedOut`.
- Voices played with `PlayPhysicalSoundDirect` bypass `VoiceManager`, so `RequestStop` returns false for them and the facade must stop them at the provider — otherwise they leak forever.

**Why:** the owner wanted 30+ vehicles; the limit was one thread, not CPU or the GPU (a serial ODE with data-dependent branching is a poor GPU fit).
**How to apply:** diagnose with the `Mixer load` / `Mix loudness` log lines and `OPENFPS_AUDIO_CAPTURE=<path>` (pass-through WAV tap, playback continues). See [[speedway-map]] and [[engine-synthesis-state]].

2026-10-09 (merged 292001d2, bit-identical on 58 renders): pool ~12 % cheaper (valve solver constants, bracket
skip, WaveLine slot, pipe impedance cache, crank sin/cos once). The pool does not spin; "50 % per thread" in top
is real synthesis inflated ~1.5x by the hybrid CPU (fast cores 0-3,12-15; slow 4-11,16-23). Profile: valve solver
33 %, WaveLine 19 %, rest of Step 10 %. Mowers cost as much as a car (EngineSynth underneath). Any non-exact change
diverges to full scale within seconds (chaotic): judge by ear, not diff. Bench: AudioLab --pool-cost.
Sound-changing levers left for Cody: looser valve stop rule, cycle cache (item 12), lower rate for far voices,
cheaper mowers.
