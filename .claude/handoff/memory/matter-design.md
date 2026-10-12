---
name: matter-design
description: Cody 10-10 vision - all materials interact as in real life; wind physical; built things sound by material/size/shape (modal synthesis); docs/MATTER.md
metadata:
  type: project
---

Cody 2026-10-10 ("I love all of this, document and remember all of this"): every material interacts with every
other like real life (fire/weather, water, wind). Wind must move things physically and its sound must come only
from what it moves (leaves, wires' aeolian tones, gaps, flags, loose things, ears), never a recording where
nothing makes it. A thing a player builds sounds by its material, size and shape: tap an aluminium cube and hear
a synthesized tap (pitch falls with size, ~sqrt(E/rho)/L; decay from LOSS FACTOR not density - Cody assumed
density sets decay; density sets how hard it is to excite). Same method for bump sounds (body into wall, glass door
rattling in its frame), knocks, drops, scrapes. Asked: periodic table? Answer given: elements are a start but
structure matters (graphite vs diamond, steel vs cast iron), so a material LIBRARY in families with cited
properties; chemistry later as data (reactions -> physical consequences -> sound).

Design written: docs/MATTER.md (commit 8f53eb0a). AcousticRegistry already has DensityKgM3, YoungsModulusGPa,
LossFactor per material (~24 materials; "Metal" = steel). Order agreed: material table -> struck things (modal
synthesis, van den Doel FoleyAutomatic) + bumps -> fire by fuel (agent running) -> water over terrain ->
weather as a system (item 11) -> wind on things -> gases -> chemistry.

**How to apply:** judge any new sound/interaction against this: computed from material properties and geometry,
energy/heat/mass moving by real laws; no per-behaviour tables or placed loops. See [[no-special-cases]],
[[model-the-thing-not-the-sound]], [[fire-model]], [[synthesis-failures]].

10-10 struck sounds heard (inbox/struck-things-2026-10-10): aluminium bar = bright bell; car bump crunchy/staticky;
fence and glass door sound the same, fence not like a fence; aluminium sheet far too sustained and tonal. Cody: push
them for now, LOW priority; geometry stage 4 and sound library stage 3 come first. Gas hob round 2 approved. Orca
works in the editor dialog.
