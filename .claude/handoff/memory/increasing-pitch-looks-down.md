---
name: increasing-pitch-looks-down
description: The sign trap that has now caught three call sites — CreateFromYawPitchRoll's pitch takes forward toward -Y, so positive pitch is LOOKING DOWN
metadata:
  type: project
---

`Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0)` applies pitch as a right-handed rotation about
+X, which takes forward (+Z) toward **-Y**. So **increasing pitch looks DOWN**. The word "pitch"
reads as "up" and the arithmetic does the opposite, and every step between the two is individually
plausible — which is why this has now been got wrong three separate times:

1. The K and O keys themselves ("k and o seem to be swapped").
2. The comment above them, which asserted that positive pitch looks up and was part of the reason.
3. `LocalPlayerState`'s facing readout, 2026-09-22: `> 60 => "looking up"`. Reported as *"when I
   press O and I'm looking up, pressing f says looking down"* — the keys were right and the words
   were wrong.

**How to apply:** never take the sign from the word. Follow it to the forward vector: transform
`Vector3.UnitZ` by the rotation and look at which way Y goes. `TurnKeyTests` does exactly that for
the keys; the readout now matches.

See [[coarse-turns-snap-to-the-grid]], [[screen-reader-keys-are-not-game-keys]].
