---
name: ear-model
description: 10-06 Cody: "account for how the ears work, no hacky patches" — loudness law and idle lift move to perceived loudness (ISO 226/532), loudness compensation per voice, playback calibration; agent started
metadata:
  type: project
---

Found 10-06 (idle-loudness agent, merged 37930897): the loudness law and EngineVoiceState.LiftDb use
UNWEIGHTED level, so bass-heavy sources (idling engines, 77-99 % below 100 Hz) read 10-20 dB louder
than heard; police idle 7.5 dB under speech at 2 m from behind (law intends +2). Moving cars are on
the law. The limiter was NOT the cause: the log's "-0.0 dBFS peak" is FMOD's sticky max since start.
Other quiet causes: ear wind masking idles (breeze louder than speech at 2 m), mix median -30 LUFS,
gunfire ducking, literal distance vs compressed level (law as designed; /levels 60 gives ~6 dB).

Cody chose the principled fix: perceived loudness per source (ISO 532-2/ISO 226), law + lift in
loudness units, loudness compensation EQ (contour at real level vs playback level), a playback
calibration setting; masking (partial loudness) designed, later. Doc docs/EAR_MODEL.md.
Balance changes > 2 dB must be shown to Cody in a table. See [[renders-at-game-level]].

**BUILT 10-06, NOT MERGED (waiting on Cody):** branch ear-model-2026-10-06 (worktree agent-a44c2da71632d377e),
main merged in. ISO 532-1 Zwicker + ISO 226:2023; law maps each sound to the equally loud speech line;
engine lift = the law (no 20 dB cap); 200 Hz/10 kHz shelves per voice (EarStage); /listening calibration
(default 62.35 dB), /ear on|off, OPENFPS_EAR_MODEL=0. Changes: police idle +10, diesel +6, rain +3,
thunder +2, door -2.6, birds -8, near drops +12, beacons +7-9 est, EAR WIND +13 (4.5 m/s breeze -24 dBFS,
15 dB over speech in RMS). Proposed: merge on + lower the ear wind's declared level ~10 dB. Asked Cody.
**MERGED 10-06** (Cody: merge yes, ear wind stays as is — do not lower it).

2026-10-07 Cody chose: rank voices by LOUDNESS to the ear, not the compensated gain (the old rank multiplied by
EarTimbres.CorrectionDb, so a 65 dB 25 Hz rumble ranked +20.8 dB up). Agent started (also shore 11 s silent start,
dropped repeating one-shot).
2026-10-07 ranking by loudness APPROVED by ear ("new ranking sounds are good"), merged 615b8ae3: Loudness.HeardGain
(ISO 532-1 loudness of the voice's spectrum at its PLAYED level) ranks both budgets; shore start pre-roll in the
same branch (sea at level from the first block).
