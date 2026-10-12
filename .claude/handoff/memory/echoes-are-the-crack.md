---
name: echoes-are-the-crack
description: 09-28 Cody: an echo is "a crack that comes from the wall, not smeared, the size of the flat surface" — mirror share clean, scattered taps smeared
metadata:
  type: feedback
---

Cody 2026-09-28 on gunshot echoes: "it should be a crack but a crack that comes from the wall, not
smeared, but a crack that's the size of the flat surface of the wall". Earlier (09-2x) he had asked
for "a smeared wash" — this supersedes it for the specular part.
**Why:** every one-off echo went through EchoDiffuser (4 all-passes); off Metal/Concrete/Glass
(s ≈ 0.05-0.1) the delays are 0.1-0.8 ms and ring at 1-3 kHz: "processed sounding". He guessed it
was cars; vehicles are NOT in the echo scene (Moves = true), and on Calder Ave the nearest wall is 90 m.
**How to apply:** WorldAudioPlayer.EchoId — mirror arrivals and higher-order chains play item.SoundId;
only ImageSource's IsDiffuse taps (the scattered share, spread across the face) get the wash. The
geometry already splits energy (1 - s) mirror / s taps. Don't re-add a smear to the mirror copy.
Engines keep full diffusion ([[coherent-copies-sound-inside-out]]). Unheard. See [[flutter-and-gunshot-echoes]].
