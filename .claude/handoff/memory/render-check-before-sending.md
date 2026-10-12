---
name: render-check-before-sending
description: "Cody 10-02 \"actually render and listen yourself\" — compare renders to real recordings by spectrogram and line/band analysis before handing him a round"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 30d35a67-deba-4f21-b6ec-8486f88764ed
  modified: 2026-10-02T22:38:06.313Z
---

Cody, 2026-10-02, after three starter rounds that "sound the same": "actually render and listen
yourself" and "research how a starter is modeled ... way too synthetic".

**Why:** I changed what the numbers said (an octave of crank speed) without checking what the ear
would hear (the sweeping sine and the buzz comb were untouched). Rounds 1-3 wasted his listening.

**How to apply:** before sending any synthesis round:
- fetch real recordings of the thing (Freesound HQ previews download without login:
  grep `cdn.freesound.org/previews/...-hq.mp3` from the sound page, ffmpeg to wav);
- `sox file -n spectrogram -o x.png` for ours and the real ones, and LOOK (Read the png):
  sweeping tones, combs, a climbing note, noise vs lines are obvious in a picture;
- measure octave bands (`sox ... sinc a-b stats`), tonal lines over local median in a long-window
  averaged spectrum, envelope beat period, A-weighted level against the rest of the file;
- render as the game plays it (CompensateLevel on for engines; one gain per car for kerb vs seat).
Pure-python FFT in scratchpad works (no numpy here). See [[car-starter-model]], [[audio-lab-instruments]].
