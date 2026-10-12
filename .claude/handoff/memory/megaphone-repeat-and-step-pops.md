---
name: megaphone-repeat-and-step-pops
description: "The \"two copies\" was two OLD re-triggered reflection generators hitting the emitter's own collider at distance zero; the per-step pop was a pooled one-shot id inheriting the worker's stale result. Both fixed session 15 (2026-09-18); record in docs/REPEATS_AND_POPS.md"
metadata: 
  node_type: memory
  type: project
  originSessionId: 533cca09-d522-4068-9ae3-27ffc791e408
  modified: 2026-09-19T03:37:18.800Z
---

Session 15 (2026-09-18). Cody: *"I hear like 2 copies, one latent like it is echoing off something
way far away... each time I step I hear a pop pop pop click... nothing we've done has worked."*
He was right that nothing had: session 14 fixed the image-source path and the mechanism was elsewhere.

**The repeat** was two generators in `ClientAudioSystem.ProcessAudioEmitter` that predate everything:
a floor slapback (`-10000-id`, 30 %, pitched 0.98) and a "cone reflection" for directional emitters
(`-5000-id`, 40 %, never occluded). Both rays used `RaycastSingle`, which tests every collider solid or
not INCLUDING THE EMITTER'S OWN, and `RayIntersectsAABB` returns distance 0 from inside a box — so both
copies sat AT the megaphone, and the pitched one drifted 113 ms per loop. Deleted; the image-source
pass already covers floor and facing wall. Image voices now: start at the source voice's PCM position
(`SpatialEmitter.ReflectionOf`), get the 6 ms onset ramp, fade+stop when their slot stops answering,
and skip engines (was asking for a sample of `engine:v8_muscle` every frame).

**The pops**: Cody's post-VOL0 5-minute capture is CLEAN (1 discontinuity in 300 s, none in rooms).
Footsteps in it are doublets 16 ms apart, quiet-then-loud. Cause: footstep ids come from a 12-id
pool, the worker caches the last result per id, so a fresh step took the previous occupant's
occlusion/EQ/APPARENT POSITION for its first frames and glided toward it. Results now carry
`SourcePosition`; `ResultIsForThisVoice` applies a cached result to a non-entity voice only within
1 m (`StaleAcousticResultTests`). Also `NotifyMaterialChange` no longer rebuilds the acoustic map
(a full bus teardown per floor change).

**Instrument:** `AudioLab --room-walk [cone=off|reverb=off|boundary=off|steps=off|megaphone=off|still|echo=on]`
drives the real provider through the wood room silently (`OPENFPS_FMOD_WAV`) — link the client's
`ASSETS/SOUNDS/{BEACONS,FOOTSTEPS}` into the lab bin first. All seven variants measured clean, which is
what pointed at the worker. Detectors: scratchpad `artefacts.py` (steps/holes/clip), `zipper.py`
(residual-ratio percentiles), `onsets.py` (per-onset "later bumps" — the footstep doublet showed here).

**Ear-validated 2026-09-19** — Cody: *"I think we've finally solved this problem... the map does sound a lot better."* What was listened for: one announcement by the megaphone; walking the wood
room, steps + a ~400 ms room and nothing starting elsewhere. See [[fmod-must-not-virtualise]],
[[reverb-needs-enclosure]], [[audio-lab-instruments]], [[no-special-cases]].

## Second pass (same evening) — the ones that actually mattered

- **setDelay/addFadePoint were written against the channel's OWN DSP clock** (`getDSPClock` first
  out-param); they need the PARENT's (second). Every scheduled delay and onset ramp in the engine had
  been silently ignored since forever: reflections played in exact sync with their source (a comb → the
  "metal room"). Found by `--room-walk clock`: a 300 ms-delayed one-shot started at +24 ms. Fixed;
  now +308 ms, and an echo shows its autocorrelation peak at its delay.
- **Own footsteps were world-locked at `_state.Position` while the listener is at `VisualPosition`** →
  each step landed at the prediction/server disagreement and trailed behind ("footsteps slide, trail
  left when I move right"; a click off to one side per step). `SpatialEmitter.FollowsListener` +
  `ListenerOffset`; `OnOwnFootstep`/`OnOwnLand` for the local controller only.
- **The "door open/close" on every crossing of the lab's halves** was `SetReverbDirection` swapping
  inside/outside per bus and toggling the binaural stage's bypass. Now: the survey reports
  `ReturnDirection` + `Anisotropy` (energy-weighted mean of ray returns); the listener's bus is steered
  there with blend = anisotropy; the DSP crossfades stereo through instead of bypass. This IS Cody's
  "on carpet facing the gym, the wash comes from the front" — no special case. `ReverbFieldDirectionTests`.
- Carpet in the registry is 0.15/0.5/0.75 absorption — reported, not tuned. Critical distance (no
  distance term in wet/dry) is the next question: "footsteps in a room" outdoors near walls.

## Third pass — the actual "footsteps loud, pop per step"

- **Footsteps played at full scale** (`Volume 1`, `MinDistance 1`) = a 112 dB source on the Loudness
  scale; `Loudness.FootstepDb = 55` existed and was used by nothing. Every step hit the master limiter
  (−1 dBFS ceiling, 10 dB makeup) by ~9 dB → pumping. Now `Loudness.Place(FootstepDb)`: −26 dB, ref 1.2 m.
  Step peak −16 dBFS. INDEPENDENT of every reflection mechanism — that is why nothing else changed it.
- **Reverb level had no distance term and the SFXREVERB's own gain was +16 dB** (measured by FMOD DSP
  metering in-engine). Now `Enclosure.ReverberantToDirectPower(e, MFP, r)` per source in the SEND
  (room equation, r_c from survey), and the bus wet level only normalises the unit's gain to unity via
  metering. Unit's synthetic early reflections off; tail starts after 2 MFP.
- Megaphone from behind: send divided by cone attenuation (a room is excited by radiated power).
- Comb's floor tap removed (a footstep is ON the floor). Rest of the comb still on — next suspect.
- Stride model: 0.5 m per step, >0.5 m/s — taps of A/D make no step by design; told Cody.
- Reference for "the designed way": Steam Audio C API guide, reflections rendered by convolution INTO
  the source signal (never a second playback); the simulator's reflections stage is still there.
