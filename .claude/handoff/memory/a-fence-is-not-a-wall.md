---
name: a-fence-is-not-a-wall
description: 2026-09-22 — 200 sheet-steel panels fenced the city's railway and silenced every vehicle behind them; a fence stops a body, not a wave, and the acoustics key on MATERIAL NAME not on per-prefab overrides
metadata:
  type: project
---

Cody: *"when vehicles drive over the railroad tracks the sound of the vehicle cuts out, it should
sound like it is running over tracks, not go silent like it is going under a bridge."*

Measured: **no road track on the city comes within 4 m of the rail line.** Nothing drives over the
tracks at all. What the vehicles were doing was passing BEHIND the rail corridor's boundary — two
hundred `metal_wall` panels, 2 x 3 m sheet steel, transmission 0.1 / 0.05 / 0.02. Opaque. The rail
runs on a 0.85 m embankment (Y 0.95 against the roads' 0.1), which is also the answer to "if it is
a bridge, why can I jump into the track area" — there is no bridge, just a low embankment with a
fence round it.

**A fence stops a body and not a wave, and those are different jobs.** A palisade is half to two
thirds gap: most of the sound goes straight through and the pales scatter the top end, which is why
a real fence takes the edge off without taking the sound away. New `fence_palisade` prefab, solid,
and the 200 ground-level panels beside the rail now use it. (Three at 4.5 m were reverted — a fence
does not float.)

**THE TRAP, and it would have made the whole fix a no-op:** `SteamAudioScene.MaterialIndex` builds
its material table **by material NAME**, from `AcousticRegistry.GetProperties`. A prefab's
`TransmissionLow/Mid/High` are overrides that the Steam Audio path — the default path — never
reads. A fence declared as `Material: "Metal"` with the numbers changed is still sheet steel to the
acoustics. So `Fence` is a registered material in its own right: transmission 0.94 / 0.88 / 0.72,
scattering 0.55, absorption near nothing. Check the registry, not the prefab, when a material does
not behave.

**Still open and it is a good one:** there is no level crossing anywhere on the city, so the
crossing bells (`StruckBellSpec.CrossingGong`, `--crossing`) have nothing to ring at and no vehicle
ever runs over a rail. Putting a road across the rail is the natural next piece.

See [[registry-falls-back-silently]], [[the-empty-grid-answer]], [[route-stops]].
