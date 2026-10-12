---
name: small-machines
description: Mowers and air conditioners as parts lists — --yard; ON THE MAP since session 17
metadata:
  type: project
---

`SmallMachineSpec` / `SmallMachineSynth`, 2026-09-19 — `docs/YARD_MACHINES.md`. Presets `mower_push`,
`mower_riding`, `ac_condenser`, `ac_window`, in the `ModelLibrary` under `small_machine`.
Instruments: `--yard levels` (measure first, always), `--yard` (a scripted run), `--yard pass`.
Demo WAVs in `inbox/yard-demo-2026-09-19/`.

Three things that are new and reusable:
- **`GovernorSpec`** — a proportional controller with no integral term, so it trades speed for
  throttle and cannot hold its setting. The droop IS the bog. Any governed machine wants this.
- **`BladeRowSpec.SelfNoiseDb`** — the broadband half of a blade row, placed by a Strouhal number on
  the blade's thickness and scaling with the cube of tip speed. The aircraft declare none and are
  unchanged bit for bit; below about Mach 0.3 it is nearly all of what you hear.
- Engine presets `mower_single` (163 cc, fires at 24 Hz — below pitch, hence *chuffing*) and
  `mower_twin` (500 cc V-twin at 53 Hz, hence *droning*).

Measured: push mower **92 dB** at 1 m, tractor 96, condenser 65, window unit 59.

**The counter-intuitive one:** the cutting hiss is forty decibels under the machine, because five
milligrams at eighty metres a second is sixteen millijoules. What you hear between grass and a path
is the **engine bogging**, not a hiss. A test holds it there so nobody props it up.

**ON THE MAP since 2026-09-19.** The voice path they were waiting for is [[physical-voice-path]],
and the city carries 121 of them — a window unit in every street-side flat on the lower five floors of five
towers, plant on every roof, a mower in every third garden. They are placed by PREFAB
(`ac_window.json`, `ac_condenser.json`, `mower_push.json`, `mower_riding.json`), because a map entity
cannot carry a sound emitter — only a prefab can. See [[machines-as-parts]], [[audibility-ranking]].

**2026-09-22, "the lawnmowers don't move":** the six push mowers DID move (server shuttles), but
`MachineVoiceState` hard-coded `GroundSpeed = 0.95`, so pushing, turning and waiting sounded
identical. It now takes `TargetGroundSpeed` from the entity's velocity. Measured: governor throttle
0.37 standing → 0.51 pushed (push), 0.32 → 0.80 (riding). Mean RPM is the WRONG measure — the
governor holds it within ~1%. And the "Verge mower" (riding) was a static PROP; it is now a
shuttle at 7 km/h, x 156, z 66↔116. **`gen_city.py` is behind `city.json`** (crossings, southgate,
stops, platforms were added to the JSON only since 09-20) — do not regenerate without porting them.

