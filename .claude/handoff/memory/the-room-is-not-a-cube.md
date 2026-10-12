---
name: the-room-is-not-a-cube
description: The room equation assumed S = 13.5·MFP²; a car park has 5x that, which was 9 dB of extra reverb
metadata:
  type: project
---

`Enclosure.ReverberantToDirectPower` needs the room's SURFACE AREA — the reverberant field goes as
1/(S·ā). It used to substitute **S ≈ 13.5·MFP²**, exact for a cube and hopeless for anything flat or
long. A city is made of flat and long things:

| | true S | cube form | error |
|---|---|---|---|
| car park 21 × 28 × 2.5 | 1,390 m² | 246 m² | **+7.6 dB of reverb** |
| corridor, tunnel, street | all far off | | |

Fixed 2026-09-19: `Enclosure.Look` measures S from the same sphere of rays as everything else —
**S = 4π·⟨d²/cosθ⟩**, exact for any convex room from any point inside it (the solid angle a patch
subtends is dS·cosθ/d²). V = (4π/3)·⟨d³⟩ if it is ever wanted. Grazing rays are floored at cos 0.05.
Accurate to a few per cent for a cube, a slab and a hall; **a long tube reads ~30 % low** (192 rays
cannot resolve the far end of a corridor) = 1.6 dB too wet, which is the stated limit.

Consequences in the city: the garage went from a 456 % reverb send to 115 %, which is what stopped
every footstep pinning the master limiter at −1 dBFS.

Two more things fixed with it:
- **The room is now SLEWED, not stepped** (`FmodAudioProvider.AdvanceListenerRoom`). The probe runs
  every few ticks, so crossing the mouth of the garage moved the send **+17.5 dB in one step** —
  "it clicks in, then pops out". A reverberant field is stored energy and takes an RT60 to build or
  die, so the live values walk toward the measured ones with the room's own time constant.
- **`CompositeAcoustics.Face` picks the face's material by area, then NEARER, then INNERMOST.** A
  carpet laid on a slab touches the same plane as the slab; the flat above's carpet is half a metre
  the other side of the same slab. Without both tie-breaks the flats had carpeted ceilings and
  concrete floors, exactly inverted.

`--enclosure map=city walk=-4,30:-30,30` is the instrument: it prints enclosure, mfp, surface, decay
and the send at every step of a walk, with the dB change from the last step.

See [[regions-measure-themselves]], [[reverb-needs-enclosure]], [[instrument-must-walk-like-the-game]].
