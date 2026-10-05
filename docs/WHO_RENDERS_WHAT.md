# Who renders what

Every part of what reaches the ear is rendered by exactly one mechanism. When two mechanisms render
the same thing, you hear a copy: the same sound twice, a few milliseconds apart. That is heard as
"it sounds like a room" or "hollow", and nothing in a log says so.

## The parts of a sound

| Part | Rendered by | Notes |
|---|---|---|
| Direct sound (level, direction, occlusion, air) | The source's own voice | Every source except your own voice, which you never hear dry (`OwnVoice`). |
| Ground reflection | The source's own voice: `GroundReflection` inside synthesised voices (engines, machines, horns, sirens) and in the binaural stage (`SteamAudioDsp`) for short one-off impulses (a shot, a clap, a knock, an impact) | Impulses only: knock character, decay up to 1 s, extent up to 1 m (`WorldAudioPlayer.HearsTheGround`). Not for speech. Not for a recorded sound made within 15 cm of the ground (`RecordedGroundMinHeight`): footsteps and drops have it already. Not for echo copies. The echo system's own ground arrival is under 12 ms and never gets a voice (`ImageSource.MinDelaySeconds`). |
| Discrete echoes off walls and facades | Per-source: `EngineReflections`, one-off echoes and flutter (`WorldAudioPlayer`), `TracedEchoes` for the loudest few running sources | Not for speech, which moves while it plays. Not for a sound made inside a vehicle cabin. |
| Late field (the place's tail) | The traced reverb (`TracedReverb`), with each source's own late energy and direction from `LateField` | The tail is traced from the listener's head, so it is only valid as a tail. |
| Near-field walls round the head | Boundary probes (`BoundaryModel`, `BoundaryProximityProcessor`) | |

## The rule for the traced reverb

The listener's trace plays every sound as if it came from where the listener stands. Anything it
hands back early is wrong for every other source position, and it comes back as a copy of the
sound close behind it.

- The listener's trace is built without the open ground (`SteamAudioScene.WithoutOpenGround`): the
  ground under the listener's feet came back 10-15 ms behind every voice (measured from a capture,
  2026-09-27: -2 to -7 dB), and a close voice sounded like it was in a small room.
- `--traced-reverb` in the lab checks it: the "as traced" places must hand back nothing within
  25 ms (PASS/FAIL, exit code 1 on a failure).

## Adding something new

A new object on the map needs nothing: its surfaces go into the traced scene through
`SteamAudioScene.BoxesFromWorld`, and the ground rule applies to any slab at ground level with the
sky over it. A new kind of sound picks one row above for each part and must not add a second
mechanism for a part another one already renders.

To check a new sound by measurement, capture the mix (`./run-gtk-client.sh capture`), then compare
each occurrence with its dry source: a copy within 50 ms that is not in the table is a double.
