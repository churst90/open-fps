# Who renders what

Every part of what reaches the ear is rendered by exactly one mechanism. When two mechanisms render
the same thing, you hear a copy: the same sound twice, a few milliseconds apart. That is heard as
"it sounds like a room" or "hollow", and nothing in a log says so.

## The parts of a sound

| Part | Rendered by | Notes |
|---|---|---|
| Direct sound (level, direction, occlusion, air) | The source's own voice | Every source. |
| Ground reflection | The source's own voice (engines: `GroundReflection`; shots, claps, doors: the ground wash) | Voices, footsteps, horns and sirens have none: at their distances it merges with the direct sound. |
| Discrete echoes off walls and facades | Per-source: `EngineReflections`, one-off echoes and flutter (`WorldAudioPlayer`), `TracedEchoes` for the loudest engines | Not for speech, which moves while it plays. |
| Late field (the place's tail) | The traced reverb (`TracedReverb`) | Traced from the listener's head, so it is only valid as a tail. |
| Near-field walls round the head | Boundary probes | |

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
