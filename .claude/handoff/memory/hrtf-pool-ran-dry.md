---
name: hrtf-pool-ran-dry
description: "Close passes sounded inside out / from above = those voices had NO HRTF (pool of 96 dry); measured as ILD with zero ITD in the capture (09-24, unheard)"
metadata:
  type: project
---

Cody 09-24: "anything that passes close to me is inverted... like it is coming from above me", with the
split off too (`./run-gtk-client.sh nosplit`). The debug directions were right. The capture showed it:
the loudest stretches had up to 11 dB ILD with **zero** interaural lag, which is FMOD's amplitude panner.
HRTF voices show about 0.6-0.7 ms of lag.

Cause: the binaural pool (SaPoolSize 96) is first come, first served. Every region's reverb return holds a slot
for its bus's lifetime, and every echo takes one. The log showed 81 active, 12 without HRTF. A voice that
starts when the pool is empty stays panned for its whole life, and the newest arrival is the car
coming past. Fix (09-24, not yet heard): pool 160, plus `SaDirectReserve` 24 slots that echoes and
reverb returns cannot take. The load line now reports direct voices without HRTF and "binaural free".

**How to apply:** for "inside out", "above me" or "in my head", first read the load line's "without
HRTF" and run the capture through an ear-to-ear lag meter (scratchpad iacc.c: level difference
with zero lag = no HRTF). The `--pass-by` lab spike showed the HRTF's per-block direction update
is not a cause. See [[coherent-copies-sound-inside-out]], [[sa-source-pool]].
