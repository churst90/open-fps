---
name: machines-as-parts
description: "Machines are parts lists in data; a close car gets two voices whose taps SUM to the single voice"
metadata:
  type: project
---

Built in session 10 (2026-09-16), commit `cfb09eb`. `OpenFPS.Common/Machines.cs`.

**A machine is a list of parts** — `engine`, `exhaust`, `intake`, `tyres`, `body`, `gearbox`,
`chassis` — each a model, a profile key in that model's registry, an offset, an extent.
`MachineRegistry.Describe`/`Assemble` are the two directions; the round trip over every built-in is
the test that the vocabulary is sufficient (`MachineTests`). Authored machines are `machines/*.json`
beside the maps, loaded by server, client AND lab (`MachineRegistry.EnsureLoaded`), and one of the
same name **overrides** the built-in. Game-side lookups go through `MachineRegistry.VehicleFor` /
`Knows`, never `VehicleProfile.ByName` — that is now only the built-in library.

Rules that must not be undone:

- **Never check the exported library into `machines/`.** An authored file overrides the built-in, so
  a generated copy silently freezes every car at the numbers it had the day it was written.
  `--machines export=DIR` writes it somewhere to read and copy from.
- **The two engine taps must SUM to exactly the single voice.** `_ring` is exhaust+body, `_front` is
  intake+block, each half the tyres; `Consume` sums them with a slewed share and applies the soft
  ceiling ONCE. A level that changed when the mixer changed its mind about voice count would be heard
  as the car jumping. `TwoOutletMachineTests` holds it to 1e-4 sample for sample.
- **When to split is `Localisation.Resolvable`** — the angle the outlets subtend (10 degrees), not a
  distance and not an authored flag. The SAME function is what step 3's aggregation must use to
  collapse many like sources. Nothing in it knows what a car is.
- A machine's second outlet is the FIRST thing shed under mixer load, ahead of reflections.

Open: the intake voice can sit up to one mixer block (23 ms) either side of the exhaust (FMOD does
not promise DSP call order; exact alignment wants `getclock`). One acoustic path per machine serves
both outlets. `ChooseFrontVoices` is untested — nothing constructs a `ClientAudioSystem` in the suite.

See [[next-order-voices-machines]], [[engine-render-pool]], [[a-source-has-a-size]], [[no-special-cases]].
