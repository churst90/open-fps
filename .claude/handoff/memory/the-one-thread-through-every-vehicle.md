---
name: the-one-thread-through-every-vehicle
description: The systematic fault — Loudness.Widen pays a source's gain down for having a size and is repaid only under an INVERSE law, which the mixer was not running. Long vehicles were quiet, loud ones exempt.
metadata:
  type: project
---

Cody 2026-09-21: *"Is there a common thread/issue between all vehicles which dictates the noise
level they emit and how far that sound is thrown?"* Yes, one, and this is it.

`Loudness.Widen` gives a source with a SIZE a reference distance equal to its own body and pays its
gain down by the same ratio, holding `gain x reference` — because under an inverse law that product
IS the far field, so only the near field goes flat. **Under FMOD's linear rolloff that product means
nothing**: the level at distance barely depends on the reference. The payment was taken and the
compensation never arrived, so **every vehicle was attenuated in proportion to its own length**:

| | extent | penalty |
|---|---|---|
| motorcycle | 1.0 m | 0.0 dB |
| v8 muscle | 3.5 m | −0.9 dB |
| road police | 3.5 m | −9.3 dB |
| school bus | 9.8 m | −18.2 dB |

And it is UNEVEN: a source loud enough to earn a reference bigger than its own body pays nothing,
so the 132 dB pace car (reference 10 m) was exempt while the same shape of car at 95 dB paid nine
decibels. **The loud got louder and the long got quieter** — exactly the map that was reported.

**Fixed by making the mixer run the law the model describes.** `Rolloff.Mode` now defaults to
`_3D_INVERSEROLLOFF`; `OPENFPS_ROLLOFF=linear` restores the old behaviour for A/B.

Measured with the new `--earshot` instrument (the city's own fleet, at their real positions):

| | linear (old) | inverse (new) |
|---|---|---|
| 42 vehicles summed at spawn | **+6.4 dBFS (clipping)** | −30.1 dBFS |
| motorcycle at 149 m | −0.4 dBFS | −39.4 dBFS |
| airliner at 1,415 m | −5.5 dBFS | −37.0 dBFS |

Under linear, an airliner a mile away was louder than a police car across the street and the whole
fleet clipped the master. Under inverse the ordering is physical: a jet at 1.4 km reads 75 dB SPL
and a saloon at 50 m reads 71, and the render puts them 0.5 dB apart — which is right.

**The headroom that frees up belongs to a MASTER trim, not to per-source nudges** —
`OPENFPS_MASTER_DB`, **defaulting to +6 dB** — settled by ear 2026-09-21 (twelve was tried and was
too much). The balance between sources is physics now; moving individual presets to get overall
loudness would undo exactly the work that made them agree.

**And every preset on a map keeps one live engine.** A car outside the voice budget borrows the
ring of the nearest car of its OWN preset, and with no such car it is silent — fine for a preset
the map has twenty of, fatal for the city's two motorcycles, which went silent together. Measured
at their closest approach they are the loudest thing on that street (−14.2 dBFS at 8 m), and they
could not be heard at all.

See [[the-mixer-does-not-use-the-loudness-law]], [[live-voice-is-not-the-bench]], [[audibility-ranking]].
