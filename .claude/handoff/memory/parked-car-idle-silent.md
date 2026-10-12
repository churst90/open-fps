---
name: parked-car-idle-silent
description: "10-03 OPEN: car left running in the garage inaudible; voice reads ~20 dB under the isolated voice; telemetry added (f9cb2025), waiting on a rerun"
metadata:
  node_type: memory
  type: project
  originSessionId: e3233dbc-96e4-4267-8826-c87e85309fd2
  modified: 2026-10-04T02:49:19.277Z
---

Cody 2026-10-03 (again; first time 09-22, see [[idle-engine-under-the-loudness-law]]): get in a
parked car (garage, i4_turbo e6449), start it, get out, it can't be heard.

Ruled out from his log (/tmp/openfps-sa-on.log 21:29-21:30): server keeps it running ("You left the
engine running"), client rpm 860, path clear (occ 0, eq 1), nearest car so always in budget, 0
starves. Ruled out by probe (EngineVoiceState in isolation, same seed, off 30 s, interior on, start,
interior off): idles at -36.0 dBFS WITH CompensateLevel, -55.5 without; listener position changes it
at most 8.5 dB (front quietest). Declared levels are honest (--voice-levels: i4_turbo 111 = live).

In game, [ADBG] L/R for e6449 at idle ~0.001 (-57 dBFS). L/R is the Steam Audio binaural DSP's
OUTPUT RMS (SteamAudioDsp.cs), placed at the channel TAIL, so effectively the voice output. That is
~20 dB under the probe, the size of the lift, so the lift looks absent in-game. Not proven.

Next: Cody reruns (get in, T, get out, wait 10 s). The 5 s census now logs `car <id>: voice out
.. dBFS, envelope, lift .. dB, channel volume, audibility` for the 3 nearest engines. If voice out
is -36: the loss is downstream (channel volume/audibility). If -57 with lift 0 dB: lift not
applied in-game, find why (_levelMs path, render pool). If envelope < 1: the voice is fading.

Also fixed in f9cb2025: liftStep had MathF.Max(1e-4, ...) so a falling lift sawtoothed every block.

**SOLVED (rerun 22:22):** the census showed `lift 0.0 dB` on EVERY car. client.json LevelCompression = 1 (/levels real). LiftDb is PlacedDb(running)-PlacedDb(declared)-(running-declared), which is exactly 0 when compression is 1. Not a bug: at literal levels an idle car is 40 dB under full load and buried by traffic. Told Cody `/levels default`.
