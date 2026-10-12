---
name: traced-reverb
description: 09-26 outdoor tail = Steam Audio listener-centric traced IR (TracedReverb), /reverb traced|room A/B; UNHEARD — Cody judging
metadata:
  type: project
---

Commit 05ba36d (pushed). Cody asked why outdoor reverb is a room algorithm at all; built
**TracedReverb** (SteamAudio/TracedReverb.cs): its own SA simulator in the WORKER's context/scene,
REFLECTIONS + CONVOLUTION, 8192 rays, 16 bounces, 2 s, order 1, source AT the listener, own thread
every 250 ms. **TracedReverbDsp** on each OUTDOOR reverb bus, inserted just after the SFXREVERB
(insert at the SFXREVERB's DSP index). Traced mode: SFXREVERB dry 0 / wet -80 (passthrough),
sends into outdoor buses = 1 (reflections 0), ApplySimulatedReverb skipped for outdoor listener
region. Room mode restores. `/reverb traced|room`, OPENFPS_REVERB=room.
`--traced-reverb` lab: open ground = ground bounce only; 20 m street ~60 dB/s decay; concrete room most.
Caveats: street tail comes out ~15 dB under direct (maybe quiet — Gain trim on TracedReverbState);
the IR's own ground bounce doubles GroundReflection slightly; far sources under-reverberated (1/d on
the send). /reverb status shows runs & ms (stage in/out RMS only via provider instance, not wired).
Next if liked: per-source convolution for the nearest few; the road/signal design discussion is pending.

**09-26 later — traced EVERYWHERE (1896824, 9e73389, pushed).** Every reverb bus has the traced stage;
listener's room traced from listener (250 ms), other audible ROOMS from their RegionPositions centre
(TracedReverbSet.ForRoom, 1 s, max 6), open ground elsewhere uses the listener's. Bounces 16 → 64
(rooms' tails died at 0.5 s); room trace ~120 ms. Retired in traced mode: SFXREVERB tail (dry passthrough),
Sabine/enclosure/wet loop (ApplySimulatedReverb returns), room-equation sends (unity), own-footstep
discrete echoes, one-off first-order copies and worker reflection slots INDOORS. Kept: proximity probes,
outdoor shot echoes + flutter, engine echoes, cabin low modes, glass transmission.
**Vehicles:** cabin traced as its own scene in vehicle frame (TracedReverbSet.RideIn/Cabin), geometry
from Common/VehicleCabin (shared with server VehicleShell). Bus cabin live (no seats modelled yet →
maybe too live); hatchback dead in 0.2 s. Doors are not in the traced scene (dynamic) — not done.
**Overload fix (after 9e73389):** 24 traced stages ran on every bus regardless of volume → mixer 40→77%,
budget shed to 2 engines (= "bit crushed" cars, train cutting out). Stages now bypassed unless their bus
volume > 0 (≤4 audible). Stage cost 0.15 ms/block, 0.5 worst during re-trace. Trains now ONE machine
group in ChooseLiveMachines (ranked by loudest tap, one slot, taps together). If Cody still hears
crunch: read "Mixer load: dsp X%" and "Audio: mixer at" lines FIRST.
