---
name: footsteps-are-the-bank
description: Settled by measurement 2026-09-19 — the twelve recordings replaced the whole bank; the model is not close
metadata:
  type: project
---

**Decided session 17, and it was Cody's call that the measurement answered.** The twelve walking
recordings in `inbox/foot steps sounds/` ARE the footstep bank now:
`tools/rebuild_footstep_bank.py` is the mapping (recording → material folder), 518 samples across
twelve materials, and the old forty-folder tree is gone.

Measured with the repo's own instrument, `--footsteps compare=<dir> surface=<material>`:
**wood +31 dB at 8-16 kHz, carpet −21 dB at 60-125, metal +19, dirt −17.** The model is not close,
and it is furthest off where it matters most. See [[synthesis-failures]] — the mechanisms stay, the
bank plays.

The recordings are physically right per material: wood carries 60-250 Hz and dies above 1 kHz because
a board resonates, carpet is a low thud 12-19 dB down everywhere above 250, gravel is a 500 Hz-4 kHz
crunch with no bass.

**Two splitter faults fixed, both measured, both in `tools/split_footsteps.py`:**
- the onset threshold was a fraction of the file's LOUDEST step, so the far steps of a walk never
  fired and were swallowed into the previous cut (cement_34 held three footfalls). It now tracks the
  loudest step within 1.5 s either side.
- the tail trim worked on sample amplitude, so a low bed with −30 dB peaks stalled it. It trims on
  the ENVELOPE, and stops early when the energy comes back up (a scuff puts two strikes 130 ms apart).

**Twelve recordings, twenty-two materials.** The borrowing is written down ONCE, in
`SoundMappingService.RecordedStandIn` — asphalt and brick take cement, grass takes dirt, marble takes
tile — and NEVER by copying wavs under new names. One fault fixed there: the end of the fallback
chain was the literal folder `Generic`, which no longer exists, so glass/plaster/plastic resolved to
a **silent footstep**.

**How to apply:** LANDING/ is a different action and was deliberately left alone — these are walking
steps only. Re-run the script after changing the mapping.
