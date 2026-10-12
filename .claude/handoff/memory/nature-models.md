---
name: nature-models
description: "10-05 water/fire/wind-in-trees physical models + Elm Park + fenced fire at 58 Alder St, on branch nature-water-fire-wind, UNHEARD, merge only on Cody's ear"
metadata:
  node_type: memory
  type: project
  originSessionId: 31b0320f-53e4-4690-93cd-0ee21bb52c49
  modified: 2026-10-05T05:09:01.925Z
---

Built 2026-10-04/05 on branch `nature-water-fire-wind` (main checkout), NOT merged — Cody's rule:
merge after his ear approves. Cody asked: water model + park fountain, fire in a fenced back yard,
wind through trees "if simple enough".

- Models: `OpenFPS.Common/NatureModels.cs` (WaterFeatureSpec/FireSpec/FoliageSpec, ModelLibrary kinds
  water/fire/foliage), `OpenFPS.Common/Wind.cs` (WindField: one deterministic field, gusts carried
  downwind, log-law height), synths in `OpenFPS.Client.Core/AudioEngine/Core/Nature/` (EventSum =
  overlap-add of physical events: Bubble, Impact, Pulse, Burst, Ring), voices in
  `Fmod/NatureVoices.cs`. Sound ids `water:park_fountain`, `fire:fire_pit`, `foliage:park_tree`.
- Per-voice headroom: `PhysicalVoiceState(level, rate, headroomDb)` + `HeadroomGain` given back in
  ClientAudioSystem, because the fire's crackles are 45 dB over its Leq; declaring it by peaks would
  have the loudness law play it ~14 dB quiet. Spec field `PeakHeadroomDb`.
- Fitted against Freesound refs (yardstick only) with `--nature compare=`: water 500 Hz-16 kHz within
  2 dB, fire 125 Hz-8 kHz within 3 dB, tree growth 10.7 dB per wind doubling (Fégeant 9-11).
  Refs + SOURCES.txt archived in ~/openfps-scratch-archive/nature-2026-10-05/refs.
- Lessons: a hard cutoff on a bubble size law piles energy on one note (a 550 Hz line); leaf strikes
  as a steady rain = hiss, real rustle is twig EPISODES; rigid-cylinder shedding (U^6) is wrong for a
  crown (Vogel reconfiguration); a 1-minute window misreads a gusty source by 2+ dB, measure 10 min;
  a voice's ring is 1.4 s — render tests in blocks.
- Map (gen_city.py, appended before named places): Elm Park x -400..-250, z 162..292 (map coords),
  fountain at (-325, 227), 14 trees; 58 Alder Street garden fenced (fence_timber), gate at x -330 on
  the park side, fire pit at (-321, 142.8). Player /tp: fountain `/tp -325 219 0.2`, fire
  `/tp -325 141 0.2`, outside the gate `/tp -330 152 0.2`, among trees `/tp -375 195 0.2`.
- Renders for listening: inbox/nature-round1-2026-10-05. Common changed → new Windows zip + VPS
  only after approval.

See [[model-the-thing-not-the-sound]], [[render-check-before-sending]], [[synthesis-failures]].

Same branch, 10-05, Cody's report "most of the time I couldn't hear the doors, like the front doors":
every door sound sat on the leaf edge = 5 cm INSIDE the brick jamb (leaf laps 0.05) and inside the leaf;
the SA simulator answer (which overrides the one-shot path) gave -30/-46/-83 dB from 3 m in plain view.
Fix: DoorSystem.OnTheLeaf — handle backset 7 cm, shut-doorway frame, TransientSound.FaceNormal (appended
field, wire change) → client places it 25 cm + half-thickness off the leaf on the listener's side: 0 dB.
Sliders: TransientSound.MovesTo/MoveSeconds, client follows (WorldAudioPlayer Following.Moving). Patio
doors now RegionBId = their garden (gen_city back_door). DoorSoundPlacementTests. Measure with
--path-probe src at the handle. Wire format changed: client and server must both be this branch.
