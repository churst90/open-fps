---
name: listener-trace-heard-the-ground
description: 09-27 "NPCs sound like they're in a room outdoors" = traced reverb's ground bounce from the listener's head; fixed by a no-open-ground scene for the listener trace; how it was found
metadata:
  type: project
---

Confirmed by Cody: `/reverb room` fixed it. Measured from his capture (`./run-gtk-client.sh capture`,
log /tmp/openfps-sa-on.log `[WAUDIO] play` lines): deconvolve each close voice line against its dry
.ogg, align on the direct, AVERAGE across lines (background averages out) → copy at 9-15 ms, -2..-7 dB.
Cause: TracedReverb traces with the source AT the listener; open ground = only the listener's own
ground bounce; every sound sent at unity. Fix: `SteamAudioScene.WithoutOpenGround` (thin slab, top
<= 1 m, sky open over 3 of 5 points) → second scene `_saListenerScene` for the listener trace only
(rooms/echoes keep full scene). Lab `--traced-reverb` "as traced" rows PASS/FAIL (<-40 dB in 25 ms).
Street tail -15 → -25 dB total (mostly the early bounce); first arrival 48 ms = facades.
Steam Audio 4.5 SDK is at ~/Downloads/sa_sdk (phonon.h); the live reflection IR is an opaque handle,
so trimming IRs was not possible — change the SCENE instead.
Rule written in docs/WHO_RENDERS_WHAT.md. Analysis script: scratchpad ir3.py (recreate if needed).
See [[coherent-copies-sound-inside-out]], [[traced-reverb]], [[pedestrian-speech]].
