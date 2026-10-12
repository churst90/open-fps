---
name: reverb-needs-enclosure
description: How loud a reverb tail is comes from how enclosed the place is, never from how long the tail is — the decay time cannot tell a yard from a room
metadata:
  type: project
---

Session 14 (2026-09-18). Cody's speedway log: the geometry reverb read **101 ms at the infield** and
**200–1579 ms on the front straight**, swinging by more than a second while he stood still, and the
wet level followed it up to **−22 dB**. A cathedral over an open racetrack.

**The premise was false, not the tuning.** `ApplySimulatedReverb` read the wet level OFF the decay
time — two constants (`OutdoorDryDecayMs` 260, `OutdoorFullWetDecayMs` 1400) mapped RT60 to "openness".
Steam Audio's parametric estimator fits an exponential to whatever energy its rays bring home and
**has no way to report that there was hardly any**. Measured (`AudioLab --sim-reverbfield`, new):

| place | RT60 | enclosure |
|---|---|---|
| open field | 0.10 s | 0% |
| beside the 0.9 m pit wall | 0.10 s | 2% |
| grandstand across the track | **0.58 s** | 4% |
| roofless walled yard | **1.01 s** | 44% |
| SEALED concrete room | **0.60 s** | 97% |

A yard with the roof OFF fits a longer tail than the same walls with the roof ON. No threshold can
separate places that sit on the same side of it.

**The rule: `Enclosure.Measure` (OpenFPS.Common) — fire 192 fixed Fibonacci-sphere rays, follow each
past its first surface, and a ray is worth what survives BOTH.** Then the field level is the geometric
series over every generation of return, `10·log10(e/(1−e))` — the classical room equation — and the wet
is `OutdoorMaxWetDb + (gain − SealedRoomGainDb)`. No thresholds, no span, nothing that knows what kind
of place it is. Works on any map because it only reads boxes + `AcousticRegistry` absorption.

**TWO bounces, and that is the whole trick.** One bounce cannot tell a PLANE from a ROOM: half of every
direction ends in the ground, concrete returns 98%, so bare concrete scored **48% enclosed** — measured
on the battle spike. A plane reflects sound away once and it never returns. Following the ray past the
ground took open concrete to 0%. `GeometryUtils.RayHitsOBB` (new) gives distance + face normal for it.

**Two things I added on hypotheses the measurement killed, and then removed** — keep it that way:
- `ReverbResult.Eq*` from `IPLReflectionEffectParams.eq`. Looks exactly like the missing energy; on the
  PARAMETRIC path it is **zero in every band**, field and sealed room alike (hybrid-only). Don't re-bind it.
- An absorbing "sky" shell round the scene, to fix what the old comment blamed ("the sky is an absorber
  a ray tracer working from box colliders cannot see"). Moved the roofless yard 993 → 1023 ms. Nothing.
  `OutdoorMaxDecayMs` (1100) still stands as a cap on the TIME only.

`EnclosureTests` (9). Not yet ear-validated — the open field now goes fully silent and the speedway
straight drops about 24 dB. See [[diffracted-bearing]], [[outdoor-acoustics-map]], [[no-special-cases]],
[[audio-lab-instruments]].

## Early reflections replaced the blanket (same session, after the above)

Cody: *"shouldn't it just be the natural reflections off the surfaces of the roof walls and such rather
than a blanket reverb? the reflections themselves should cause the reverb naturally right?"* — Yes.
`OpenFPS.Common/EarlyReflections.cs`: **first-order image sources**. Mirror the source through each box
face; where the line from the image to the ear crosses the plane is the bounce point; the copy's level
is what the material did not absorb times `direct/pathLength`; the delay is the extra distance over c.
`MaxArrivals` 4, `RangeMetres` 60 (same horizon as [[reverb-needs-enclosure]]'s enclosure).

**Used by BOTH paths** — `AsyncAcousticWorker.AddEarlyReflections` under Steam Audio, and
`SpatialAcoustics.CalculateAcousticPaths` on the fallback. That retired the old generator: a recursive
ray solve that jittered every normal with `new Random(entityId + DateTime.Now.Millisecond)` — a fresh
seed per call, so a wall's reflection moved every frame — then merged the scatter to hide it.
`MaxReflectionOrder` and `ReflectionMergeDistance` deleted with it: an image source makes exactly one
arrival per surface, so there is nothing to merge.

Rules that must not be undone:
- **Both legs are checked against every other solid.** A facade behind a building reflects nothing.
- **Survivors are cut by GAIN, then re-sorted by SURFACE.** The voice slot is `ReflectionIndex`, so
  loudness order would make two near-equal arrivals swap slots on the smallest movement and hand each
  voice the other's reflection. It was `ReflectionId % 100` before, which two surfaces can collide on.
- **The tail is now applied to EVERY region, not just outdoor ones.** An enclosed region was built at
  `0 dB wet` and never touched — that was the blanket. Same enclosure law everywhere.

Verified against the REAL rooms map (not a toy scene, which is what caught a probe that had included
the megaphone's own collider and reported zero everywhere): inside the wood room, 3 walls answer from
3 directions at −10 dB, 20–28 ms; in the doorway, 4 arrivals including the floor; open ground, none.
**Known limit:** first order only, so from OUTSIDE and off to the side, the room's reflections do not
follow the direct sound out through the doorway — that needs a reflect-then-diffract path.

## The repeat, and reverb as a consequence of materials (same session, third pass)

Cody, after the reflections landed: *"the megaphone is like repeating echoing, not an environmental
reverb... if I stand by the megaphone, I hear it repeat softer but in the same place... the room is
basically dry... reverb needs to be a natural consequence of the materials and sound bouncing around."*

**Two faults, and the rendering one is the subtle one.**

1. **A voice is an independent playback, so a reflection of a SUSTAINED source can never be one.**
   `channel.setDelay` schedules the START of a second read of the same sound at an unrelated position
   in it — for a looping announcement that is a second copy of the announcement, not a reflection.
   Inside the ~50 ms fusion window the ear would not have heard a separate event anyway. So:
   `EarlyReflections.IsSeparateEvent` (delay >= `FusionSeconds` 0.05) gates whether an arrival becomes
   a voice. In a 10 m room every arrival is under 30 ms → **zero discrete voices, no repeat**; a stand
   20 m off gives 117 ms → a real slapback. **The search still FINDS them all** — the model reports the
   physics, the renderer decides how to realise it. `RangeMetres` 60 → 200 (it is a search bound;
   `MinRelativeAmplitude` is what prunes, and 60 m could not reach a facade down a street).

2. **The tail's character and level now come from the ray survey, not from Steam Audio.**
   `Enclosure.Look` returns enclosure + open fraction + **mean free path** + **per-band absorption** from
   the same sphere of rays; `Enclosure.DecaySeconds` is Eyring on those (`0.161·MFP/(4·−ln(1−a))`).
   `SteamAudioSimulator` reflections are **retired from the worker** (`_saReverbSim` deleted — the
   heaviest stage, one bandless number that could not tell a yard from a room). FMOD gets decay (param 0),
   **HF decay ratio (4)** and LF shelf (7/8) from the measurement, so a material is audible as the tail's
   colour. Measured on the real rooms map: carpet side **171 ms, HF 0.64, −7.2 dB**; gym side of the SAME
   open room **338 ms, HF 0.91, −3.8 dB**. That is his "hear the wash from the gym while standing on the
   carpet" — it falls out of surveying from where you stand.

   **Mean free path counts only the rays that HIT.** Letting escapes contribute the horizon put a 10 m
   room's MFP at 21 m and its RT60 at 2 s; over hits it is 4.1 m and 0.35 s.

   **Wet level is the ratio itself**, `clamp(ReverberantGainDb(e), −80, 0)`. It was that ratio pushed
   down to a −16 dB ceiling, which put a hard courtyard at −35 dB — "basically dry". `OutdoorMaxWetDb`
   is 0 now. Open ground lands at −19.9 dB: if outdoors is ever too wet, that is the number, not a
   threshold.
