---
name: borrowed-voice-doppler
description: "A borrowed distant engine voice inherited the lead car's Doppler through the ring's play position; fixed 2026-09-16 (session 9)"
metadata:
  type: project
---

A car beyond the synthesis budget is voiced by reading a near car's ring buffer (`DistantEngine` →
`EngineEchoState`). It read back from the SOURCE'S PLAY POSITION, and `_played` advances by whatever
block the mixer asks the source's DSP for — which is scaled by that channel's pitch, which is its
Doppler. So the borrowed car inherited the lead car's Doppler and then applied its own: two cars'
pitch movement on one voice, belonging to cars going opposite ways. This is the best explanation for
"technically at the redline, sounds like cruising".

`EngineEchoState.OwnCursor` (true for a borrowed voice, false for a reflection) keeps an absolute
cursor into `EngineVoiceState.ReadAt`, advancing one sample per sample and nudged back into place at
most 1 % — inaudible, and correcting drift rather than adding pitch. A REFLECTION must keep following
the source: an echo of a car is that car's sound arriving late, so the source's Doppler belongs in it,
which is why a reflection's channel pitch is pinned to 1 and a borrowed voice's is not.

**How to confirm:** `BorrowedVoiceDopplerTests` consumes the source 25 % fast — following it, the
borrowed voice comes out at 333 Hz where the source is 98; with its own cursor, 99. The control (same
code, source consumed at real time) is correct, which is why this was invisible until a car moved.

See [[speedway-field]], [[engine-render-pool]].
