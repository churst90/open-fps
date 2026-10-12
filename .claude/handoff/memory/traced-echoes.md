---
name: traced-echoes
description: 09-26 per-source traced reflections for the loudest few sustained sources; rigs in the channel chain; /echoes; shots back into the traced tail
metadata:
  type: project
---

Cody 2026-09-26: reflections "bounce around from one building to another as the source moves"; he wants detailed reflections only for loud or recycling sources. Built, but NOT YET HEARD:
- `SteamAudio/TracedEchoes.cs`: one simulator, 6 source slots, 4096 rays, 32 bounces, 1.5 s, order 1, refreshed every 125 ms. `TracedEchoRig` = a capture DSP plus a mix DSP, pooled and never freed.
- FMOD chain order, measured with `--dsp-order`: index 0 is the OUTPUT end. The game's chain runs source → HRTF → diffraction → EQ → fader. The capture sits at index numDSPs (raw mono) and the mix at index 0 (after the fader). There is no block lag.
- Selection (`FmodAudioProvider.UpdateTracedEchoes`): engine or machine voices with an HRTF voice, ranked by `LastVolume`, within 30 dB of the loudest, 4 dB hold, at most 2 taps per train.
  - A traced voice has its reverb sends cut. `EngineReflections.Update(traced:)` stops its mirror images.
  - `InputGain` = BaseVolume × FadeGain × RenderedGain(d) × d, so echoes keep a physical ratio to the direct sound.
- Lab `--traced-echoes`: 6 sources trace in 19 ms, the effect costs 0.1 ms per block, and a pass-by is smooth (less than about 2 dB between traces). The log line is "Traced echoes:".
- `/echoes on|off`; `OPENFPS_ECHOES=off` starts with them off.
- Gunshots: every gun is pinned at the mix ceiling within 40 m (`--tap-balance placed`). In traced mode, outdoor impulses go to the traced tail again; the old skip was for the room algorithm.

**First hearing (09-26), rejected for close sources:** "sources that are close to me sound boxy and flanged and reflections stack up. this should only apply for sources far away and loud".
- Now gated: a source enters at 30 m or more and is held down to 22 m.
- The hand-over fades both ways over 0.6 s (`EchoWeight`): the sends × (1−w), the rig input × w, and the mirror images stop only when w ≥ 0.5.
- Still open: a far source's IR includes the ground bounce, and the voice has its own GroundReflection, so the ground is counted twice.

**Second hearing (09-26):** "way too strong ... they're coloring, not the primary reflections ... reflect, cut out, cut in, cut out".
- Research (street-canyon measurements, image bound at 1 dB per facade): multi-bounce energy re the direct sound is −12 dB at 10 m, about 0 at 30 m and +1 at 100 m. So the trace's level was roughly physical. What was wrong:
  - the copies are coherent, where real facades scatter;
  - first order was counted twice;
  - the selection flickered.
- ON by default (checked in code 2026-10-09: FmodAudioProvider.TracedEchoesOn; `OPENFPS_ECHOES=off` starts with them off). `/echoes -12` sets the trim; the default is −10.
- Ranking uses BaseVolume × the distance law with no occlusion or cone, plus a 3 s minimum hold.
- The listener tail alone measures −15 dB re its input in a 20 m street (`--traced-reverb`). Its first 50 ms is mostly a ground bounce at the listener's own height.
- Next if revisited: decorrelate the copies (a scattering all-pass) before raising the level.

**Third pass (09-26):** Cody set −24 dB by ear ("-24 is about there"), but it still "jumps and cuts out a little". Now default ON at −24.
- **ROOT CAUSE, a Steam Audio rule:** an IR update goes to the FIRST effect that reads it after a trace, so a second effect on the same source IR stays silent or stale (proved in `--traced-echoes`: readers [0,0] give 32 dB and −200 dB).
  - Every outdoor reverb bus had played the listener trace through its own effect, so only one bus ever had a live tail. A source's reverb came and went as it crossed regions.
  - FIX: `TracedReverb.EnsureReader(i)` gives each stage its own source at the listener (`TracedReverbState.Reader`, at most 24).
- Traced echoes use two banks of sources in ONE simulator. A second simulator on the same scene gave silence.
  - The bank being refreshed gets REFLECTIONS flags and the other gets 0, so it keeps its IR.
  - The rig crossfades to the newest bank over 106 ms. Both convolutions run every block.
  - `BankGeneration` gates a bank until it has been traced since the rig attached.
- Levels measured smooth either way (0.11 dB per block); the crossfade is for changes in the fine echo pattern.
- Two tests (propeller phase, borrowed-voice Doppler) now run in the non-parallel `ValveFlowSwitch` collection with the valve noise off.

**Pushed 09-26 as commit 7b80c62**, together with the valve flow noise, turbo, roster and footstep take levels. Full suite 1297/1297 on the reflection code.
