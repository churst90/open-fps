---
name: own-voice-room
description: 10-03 your own mic voice played into your room only (copies + reverb feed, never dry); unheard; where it lives and what to check if Cody says it is wrong
metadata:
  type: project
---

2026-10-03, Cody: "I want to hear myself in the room I'm actually in" (no /mictest; lag is fine).
Merged to main (own-voice-room). Mic samples (IMicrophoneCapture.SamplesCaptured, both heads) go to
`OwnVoiceRing.Shared` (48 kHz), never the server. `ClientAudioSystem.UpdateOwnVoice`: room feed emitter
`ownvoice:room` at its reference distance in a ZERO-volume group (`_ownVoiceRoomGroup`; sends leave from
the channel's fader so the reverb still gets it), plus up to 8 `ownvoice:copy` reflections found by
EarlyReflections.Find(mouth, ear) every 0.1 s, delay = path/c - 60 ms mic latency. Level: mic talking
RMS (slow avg, gate -50 dBFS) = Speech.NormalDb at 1 m. `OwnVoiceTap` reads with its own clock, +-1 %
pull averaged ~2 s. Log line "Own voice: talking at ... surface(s) answering" every 5 s while live.
Unheard. Risks if wrong: muted-group sends may not carry in traced mode (ownMix needs _traced region);
copies at 60 ms floor may sound like slap-back in small rooms. See [[a-reflection-is-the-room-answering]].
