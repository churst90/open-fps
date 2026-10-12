---
name: decorrelator-was-a-reverb
description: 09-29 — "stadium / cupped ears / too many reflections" was the EarDecorrelator's long all-passes; measure a capture's envelope per clap before blaming the trace
metadata:
  type: project
---

Cody (09-29): clapping in rooms sounds "like a stadium... should simply be the immediate walls";
stairwell flutter "like I've got my ears cupped"; outdoor clap in front of a building "not right".

Cause: EarDecorrelator (added 09-28 for the mono tail) had 6 all-passes of 41-587 samples at G 0.6.
A click came out as ~100 ms of build-up peaking at 20-45 ms, on top of every traced IR. In the
capture, every clap's wet energy peaked about 48 ms after the clap, 2-6 dB under the direct sound.
This happened in rooms and outdoors alike.
FIX: delays 7-97 samples, G 0.5. 90 % of a click comes back by 9 ms; IACC 0.14. Test: AClickStaysAClick. Unheard.

Ruled out:
- The traced IR level. --traced-reverb puts the wood/carpet room at -6.7 dB re impulse, drier than
  the room equation.
- Echo copies. Indoors the traced reverb carries them. Outdoors the only facade echo was 21 dB under
  a clap 0.5 m from the ear, below ImageSource.EchoAudibleRatio; this was left as it is.

Also found: AcousticTile shared ResonanceIndex 6 with Carpet, so the terminal ceiling read back as
Carpet. It is now 30, and a test checks that no two materials share an index.

Method:
- Capture header is zeros while the client runs. Strip 44 bytes and read it as raw 44.1 k s16 stereo.
- Clap times: the `recv 'clap'` log lines minus the client start time.
- Use a 2 ms rms envelope per clap. Window-energy ratios lie when the windows differ in length.
- Region 103 is the Marlow Tower stairwell, floor 0: concrete, RT60 2 s.

See [[traced-reverb]], [[a-reflection-is-the-room-answering]].
