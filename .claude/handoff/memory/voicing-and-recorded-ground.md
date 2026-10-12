---
name: voicing-and-recorded-ground
description: 09-27 NPC voices — low-end cut vs Byrne LTASS at the voice's own f0, loudness by BS.1770 not RMS, and GroundReflection in the binaural stage for recorded sounds
metadata:
  type: project
---

Cody 09-27: voices "sound like a speaker that plays", tim "quite bassy"; wanted low end off, loudness
matched, a mouth. Built (unheard; clips in ~/openfps-listen/voices-2026-09-27):
- **Voicing** in tools/import_npc_voices.py: median f0 by autocorrelation (checked at 8 and 16 kHz),
  Byrne 1994 Table II male/female LTASS interpolated by f0 (120→210 Hz), bands <500 Hz compared
  with the voice's own 500-2k, CUTS ONLY (max 12). A first version corrected the whole spectrum and
  would have flattened the shouting drivers' brightness (a shout reads ~290 Hz f0) — narrowed.
  Table II values live in the importer; PDF copy was in the session scratchpad.
- **Loudness**: RMS matching left 2.6 dB between voices (seanterry -28.0 .. joel -25.4 LUFS); the bass
  cut changed LUFS by ≤0.2 dB, so it was NOT a byproduct. Client now uses Speech.LoudnessLufs
  (BS.1770 48 kHz coefficients, ungated) to BufferLoudnessLufs -27.2 (the old median). Peaks -1.9 dBFS.
- **Recorded ground**: SteamAudioVoiceState.Ground (GroundReflection before the HRTF), reset on pool
  reuse, set only when the voice has no Engine/Tap/Machine state. Game thread:
  ClientAudioSystem.ApplyRecordedGround via WorldAudioPlayer.Ground; skipped under 0.15 m (the
  recording has its own bounce), for echo copies and extent > 1 m. One-offs don't touch _groundCache.
See [[ground-reflection-and-flutter]], [[pedestrian-speech]], [[listener-trace-heard-the-ground]].

**Later 09-27:** Cody: voice + ground "flanges a lot"; research abstract (Acta Acustica 2024) says the
bounce is physically strong, so the first theory was direction: moved the reflection to its own HRTF
at the image below (SteamAudioVoiceState.GroundEffect, --ground-voice lab spike renders 3 ways). Cody:
"the ones with the ground reflections still flange" — BOTH ways. Speech now has NO ground; recorded
ground only on impulses (IsImpulse). Missing piece unknown: HRTF torso shadow from below? talker
vertical directivity (need the paper's full text — publisher captcha blocks fetch; ask Cody)? head
movement? Linda before/after voicing: "sound the same" (expected: cut only <160 Hz).
