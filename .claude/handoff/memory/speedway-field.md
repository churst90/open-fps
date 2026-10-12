---
name: speedway-field
description: The speedway is 40 cars and 17 different machines now; how to change it and what each class is for
metadata:
  type: project
---

`FIELD_SIZE = 40` in `tools/gen_speedway.py`. **The map is GENERATED — edit the script and re-run it,
never the JSON — and the server loads maps once at startup, so every regeneration needs a restart.**

Seven stock cars still make it a stock car race; the `SUPPORT` list is everything being caught. The
point of the roster is that **no two classes share a mechanism**, so the map demonstrates that
character comes from the machine and not from a recording:

- 5 ways to exhaust ONE V8 — open headers (no muffler and therefore no case ring), big cam (330 deg,
  will not idle straight), blown (Roots rotors at order 12, no lag because it is geared to the crank),
  Flowmaster coupe (the can the case was tuned against), glasspack (packing damps the case too, so
  mellow is an ABSENCE), mild small block (cast logs, 112 dB, the quiet one). Same mass and gearing,
  fifteen decibels apart.
- Diesels 4+2, two turbo sizes on one lap, which is what makes it read as a turbocharger.
- V10 supercar with a 60 ms automated single-clutch box; F1; three motorcycle types; three hatchbacks
  at 94 dB, because a circuit where everything is dramatic has no scale to it.

**The trap that cost a round:** a NASCAR has open side exits and the pace car open pipes, so 13 of 30
cars had NO muffler, and the glasspack cars have theirs damped. Only 2 cars carried the ringing
chambered can that had just been tuned, which is why it "could barely be heard". **Check what the
field actually carries before concluding a sound is wrong.**

Best listening position found so far: **(-19.3, 0, -172) facing about 45 degrees into the oncoming
cars** — the retaining wall is audible on the left and the reflections read correctly on a pass.

Load at 40 has never been measured; it was fine at 60 voices with 30 cars. Watch the `Mixer load` line.

See [[speedway-map]], [[audio-lab-instruments]].

**2026-09-18:** the field is 21 now — two `f1_v10` (back on the map once their tailpipes radiated from
their own positions), five of each bike, two diesel pairs. No stock cars, no pace car. The server was
restarted on the regenerated map at 14:19 (started with `setsid nohup ... OpenFPS.Server.dll`, log at
`/tmp/openfps-srv/server.log`); a release GTK client is prebuilt in `/tmp/openfps-gtk`.
09-28: SportBike and DirtBike gearboxes lacked the PRIMARY reduction (1st overall 7.7:1 / 9:1 vs real
~12 / ~20) → lugged at 1.5-3.3k rpm at city speed, "sounds like a diesel pickup, shifting weird".
FinalDrive now 3.0×1.63 and 3.8×2.9. Dirt bike exhaust cut to ~0.5 m total (Cody's ask).
