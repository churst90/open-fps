---
name: registry-falls-back-silently
description: AcousticRegistry.GetProperties returns Generic for an unknown name without saying so — steel is "Metal"
metadata:
  type: reference
---

`AcousticRegistry.GetProperties("<anything unknown>")` returns **Generic** — 1,200 kg/m³, 5 GPa,
loss 0.02, which is a plastic — and logs nothing.

**Steel is spelled `"Metal"`.** Asking for `"Steel"` builds your sheet-metal part out of plastic, and
the symptom is a mode series that is quietly wrong, not an error: a 0.8 × 0.9 m panel that should have
six driven modes came back with one. Cost an hour on the AC cabinet and the mower deck.

The full table as of 2026-09-19: Generic, Wood, Metal, Concrete, Marble, Carpet, Glass, None, Plastic,
Grass, Audience, Dirt, Gravel, Rubber, Leather, BootRubber, Skin, **Brick, Asphalt, Tile, Foliage**.

`PrefabValidator` DOES check a prefab's material against the registry and names the known ones.
Nothing checks a material named in C#. When adding a spec with a material name, add a test that its
modulus is not Generic's — `SmallMachineTests.TheMetalPartsAreActuallyMetal` is the pattern.
