---
name: distant-voices-had-no-path
description: 09-24 — the white-noise wash at the map edge was the 12 borrowed distant-car voices, never given an acoustic path (id < -5000 skipped as "reflections"); fixed d538b01, approved by ear
metadata:
  type: project
---

Cody (09-24): "a wash of white noise" at (519, 559) and (400, -130) on the city. The loudest-voice
log (added 1ab4e64/592c065: every 5 s, `loudest:` lines with low/mid/high dBFS) showed `engine-distant`
voices at 445-1,011 m with high only 10 dB under low, where every other far source was 42 under.
Cause: ClientAudioSystem's path loop skips ids < -5000 as reflections; horns/sirens were forwarded
their car's path, the DistantVoiceBase voices never were — no occlusion, no air, no vehicle shadow.
Fixed d538b01. Cody: "definitely sounds way way better ... even at 60 122 things sound better".
Same session: VehicleShadow broadside bus gave 0 detour (fixed eafb4c1, corner routing).

**How to apply:** any new borrowed/derived voice id must be given its source's path explicitly. The
`loudest:` log lines are the instrument for "why can I still hear X". See [[the-mixer-does-not-use-the-loudness-law]]
(STALE: inverse is the default since 09-22).
