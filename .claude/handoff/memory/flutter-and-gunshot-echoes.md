---
name: flutter-and-gunshot-echoes
description: 09-28 — moving blocked sirens/horns fluttered because the heard point was lerped through the head; gunshot echoes occluded by their own reflecting wall; both fixed (unheard)
metadata:
  type: project
---

Flutter (Cody: sirens/horns "fluttering, lumpy" when moving): only when BLOCKED. AsyncAcousticWorker
picks the blocked bearing per result (edge vs SA probes, no hysteresis) → flips up to 6×/s; the
voice lerped CurrentApparentPosition in a straight line (FmodAudioProvider) → swept through the
listener's head: ears swapped, level lumped 5-10 dB at 2-5 Hz (distance loss read the dragged point).
FIX 09-28: TurnToward() — direction turns at ≤120°/s blocked (1500°/s clear), distance eased
separately; distance loss, cone and reverb send from the REAL source position. Root (bearing choice
in the worker, energy-blended routes) still open — see [[blocked-bearing-flips]]. Horns also sit at a
stale tailpipe path point (UpdateHorns should use SirenApparent) — not done.
Gunshot echoes (Cody: "don't appear all that loud"): each echo's occlusion ray ran listener → image
point, THROUGH the reflecting wall: -40..-60 dB. FIX: WorldAudioPlayer gives IsReflection items
Occlusion 0 / EQ 1 / own air, no reverb send. Caps now on EXTRA path (ImageSource.MaxPathLength 400,
EarlyReflections.RangeMetres 200, flutter 343 all `path - direct`); higher-order surfaces searched
near listener OR source. Not done: traced echoes for impulses; reverb tail from unoccluded level;
Maekawa cap on outdoor occlusion (~20-25 dB). Evidence: /tmp/openfps-sa-on-client.log [ADBG],
/tmp/openfps-capture.wav (OPENFPS_AUDIO_CAPTURE).
