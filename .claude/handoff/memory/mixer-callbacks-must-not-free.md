---
name: mixer-callbacks-must-not-free
description: Never free a GCHandle or an FMOD object a DSP callback may still resolve — the rule the Steam Audio path knew and the engine path did not
metadata:
  type: project
---

**The city crash, session 17.** Found from a 2.6 GB minidump: the stack was
`libfmod.so -> libcoreclr (fatal path) -> abort`. FMOD's mixer thread called into managed code and
the runtime aborted. Signal 11, no exception, no log line.

Every DSP read callback in `FmodAudioProvider` begins by resolving the DSP's userdata back to its
state: `GCHandle.FromIntPtr(userData).Target`. **`ReleaseActiveSoundResources` was freeing that
handle the moment a voice was released.** FMOD may call a released DSP's callback once more;
resolving a freed handle does not throw, it dereferences a slot that no longer belongs to it.

**The rule was already written in the same file**, on `ReleaseSteamAudioVoice`:
> *"We never free the Phonon effect/buffers or the GCHandle here — that is exactly what raced the
> mixer callback and corrupted the heap. The resources live until Dispose."*

The Steam Audio path learned it; the engine path never did, because on a racetrack voices are made
once and kept. **A city makes and drops them continuously** — 30 engine voices created in 45 seconds
with the player standing still — so the race is a matter of time. Fixed: clear the userdata, release
the DSP, park the handle in `_retiredHandles` until Dispose. Sixteen bytes per retired voice.

**The same mistake, made fresh, in the same session:** a reverb-bus "retirement" that released
channel groups and SFXREVERB units mid-mix and pushed their HRTF voices back into the shared
`_saPool`. That drained the pool the spatial voices draw from, which is heard as **everything going
MONO** — a voice that cannot get a binaural effect plays without one. Replaced with a creation cap:
past 24 buses the far ones are simply not built. **Nothing is torn down while the mixer runs.**

## ...and the rule above it, which is the one that actually kept crashing it

**NOTHING MAY ESCAPE A DSP READ CALLBACK.** It runs on FMOD's mixer thread, called from native code;
an exception reaching that native frame is not a caught fault, it is a **CLR fatal error** — the
runtime aborts on the spot. The core reads `libfmod -> libcoreclr -> abort`, with no managed stack.

Not freeing the handle narrows the window; it does not close it. `GCHandle.FromIntPtr(userData)
.Target` throws `InvalidOperationException` for any handle that is not allocated, and there are other
ways to get there. **The guard has to cover the handle resolution, and in four places it started one
line too late:**

| callback | was |
|---|---|
| `EngineProcessor` (engine, echo, tap) | no whole-callback guard at all — only `try { state.Render(mono); }` |
| `MachineProcessor` | copied from the engine's, gap and all |
| `BoundaryProximityProcessor` | guarded the body; its own comment says this crash "killed the client outright" — and the `try` began AFTER the GCHandle line |
| Granular, Synth, SteamAudioDsp, AmbisonicBedDsp, MasterTap | already correct: `try { return ReadCallbackCore(...) }` FIRST |

All eight are now the correct shape, with a one-shot log (`"<X> DSP faulted; the block was
silenced"`) so a faulting DSP says so once instead of 43 times a second. The boundary unit passes
the mix THROUGH on fault rather than clearing — it is on the master bus, so silence there is silence
everywhere.

**Why:** the reverb buses and the spatial voices share ONE 96-voice Steam Audio pool, and 185 buses
ate it. Mono is the symptom of pool exhaustion, and it is worth recognising on sight.
**How to apply:** `--scene-churn` reproduces the load headlessly (27,600 voices against 120 rooms).
Granular and synth DSPs are pooled and safe; the four remaining `Free()` calls are all on the
`playDSP` failure path, where nothing ever played.
