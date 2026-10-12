---
name: voice-chat-network
description: "10-03 player-to-player voice chat rebuilt: 64k fullband Opus, numbered packets, per-talker jitter buffer, voice relayed off the tick; the E2E bot and how it was measured"
metadata:
  node_type: memory
  type: project
  originSessionId: e3233dbc-96e4-4267-8826-c87e85309fd2
  modified: 2026-10-04T00:14:45.512Z
---

2026-10-03, Cody: "voice data should be sent to the server so my voice is played in other players
clients... high quality... compressed to be fast but not compromise on sound quality." The previous
session's "no round trip" was about [[own-voice-room]] only; the network path existed but played
each 20 ms packet as a separate one-shot FMOD sound (gaps, clicks, no jitter buffer, no acoustics).

Built and pushed 10-03, main dc1de247 (unheard):
- `VoiceCodec` (Client.Core/Platform): Opus AUDIO app, 64 kbit/s VBR, complexity 10, fullband, FEC,
  5 % loss hint. Concentus loads native libopus 1.5.2 on Linux; it picks HYBRID fullband (TOC
  config 15) at 32-64k, so FEC is real. Measures 48 kbit/s actual on a buzz.
- `SincResampler` replaced the linear one (FmodMicrophoneCapture, only when device != 48 kHz).
- `VoiceData.Sequence` appended (1..65535, skips 0; 0 = old client, played as it comes).
- `TalkerStream` (Fmod/TalkerVoice.cs): reorder by sequence, hold a gap up to max(frame, spread),
  then PLC + FEC; margin = spread + hold + 2 frames + 10 ms, 70-500 ms; `Talkers` static registry.
- `OwnVoiceTap` generalised: ring.MarginSeconds, spurt restart with priming, STALLS on underrun
  instead of running past, maxPull (talker 0.2 %, own voice 1 %).
- `ClientAudioSystem.UpdateTalkers`: emitter `talker:<id>` at the mouth, level from the stream's
  SpeechRmsDbfs as normal speech, path from the acoustic worker keyed by the SENDER's entity id
  (ignores their own capsule), no ground (see [[voicing-and-recorded-ground]]).
- Server: `NetworkService.HandleNow` dispatches VoiceData during PollEvents instead of at the
  30 Hz tick; `Flush()` = TriggerUpdate on both ends. Loopback median 19.5 ms -> 2-3 ms.

**E2E instrument**: scratchpad console `voicee2e` (two ClientNetworkService bots, register+login,
`ready`, A sends a 200 Hz buzz, B records arrivals and replays them through TalkerStream+tap),
against a scratch server copy on port 34288. Goertzel per harmonic was a TRAP (1 Hz bins vs the
reader's pull); use Welch PSD bands (numpy lives in /home/cody/voice/venv). Result: all 19
harmonics within ~1 dB, 0 lost, 0 underruns.

**Why:** the wire contract changed, so the Windows zip and VPS server must be redeployed together
(see [[hardening-2026-10-02]], [[windows-client]]).
**How to apply:** if Cody reports choppy or robotic voices, read the "Voice chat: e<id>" log line
(buffer ms, 70 minimum, lost, rebuilt, late, "ran dry"; the ring's raw Underruns also counts every sentence end) before changing anything.
