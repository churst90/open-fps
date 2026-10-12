---
name: footstep-take-levels
description: 09-26 footsteps "dropping out" in the apartment was the recordings: bank takes spread 22 dB; TakeLevels normalises each take to its bank's median
metadata:
  type: project
---

Cody 2026-09-26, in a tower flat (carpet): "my foot steps seem to be dropping out ... I only hear foot steps on few key presses".
- NOT the stride logic: `--walk taps=8 y=3.3 from=-13,-60 to=-13,-58` (Marlow Tower) gives one step per tap on both floors.
- NOT the voice budget: footsteps are `Essential`.
- It WAS the bank. Carpet takes' peaks spread over 22 dB, and the quiet third vanished under the air conditioner and the traffic. Concrete, tile and wood are the same.
- `Fmod/TakeLevels.cs`: the loudest 20 ms of each WAV, and a gain to the folder's median, clamped at ±18 dB. It applies to banks of 3 or more files, sample voices only, through `BaseVolume` in the provider. Each floor keeps its own level against the others.
- `TakeLevelsTests`: every bank's corrected spread is under 1 dB.
