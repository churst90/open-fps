---
name: mixer-cost-is-convolution
description: 10-09 measured: mixer load is the traced reverb's convolution (26.5 %), not tracing; the separate-process split would not help
metadata:
  type: project
---

Cody ran /echoes on then off on a city street, 2026-10-09 (/tmp/openfps-client.log 11:36-11:40).
Mixer load 70 % on, 62 % off. "Mixer time" shares: traced reverb convolution 26.5 % either way,
traced echoes 10.4 %, binaural 13 %, rest < 3 % each. Governor took voices back at 60-62 %.
Trace threads now nice 10 (f1a6915a, confirmed with ps -L): TracedReverb ~34 % of a core, LateField 5 %.
FMOD's AudioInit threads show NI "-" (not SCHED_OTHER). 12 EngineRender threads ~50 % each = 6 cores;
AcousticWorker ~70 % of one.

**Why:** todo item 7 (reflections in their own process) was conditional on tracing being the cost. It is not.
**How to apply:** recommend against the split; make the reverb's convolution cheaper instead. See
[[traced-reverb]], [[engine-render-pool]].

10-09 reverb agent: branch worktree-agent-a66793f76bedee27a (06a863ae, 6049811a, e101df6c), NOT merged until Cody
hears the room tail. Two-level SharedInputConvolver + one head response per direction: listener's stage 3.0 ms ->
1.9 ms per 1024 block (-38 %), null <= -120 dBFS, same delay. Next: other rooms' stages still Steam Audio native
(~9.4 % core each); DiffuseLateConvolver memory-bound. LAB TRAP: time with DOTNET_TieredCompilation=0, or the
first sections time unoptimised tier-0 code (1071 us vs 472 us, fake 17 ms worst blocks).
