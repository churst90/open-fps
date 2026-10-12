---
name: tile-scene-swap-empty
description: "10-06 indoor traffic + mono reverb = first door swing swapped in the second pair of tile top scenes, which traced empty"
metadata:
  node_type: memory
  type: project
  originSessionId: 568c4779-136f-4fbd-b7f7-35b87bd6f916
  modified: 2026-10-07T04:15:38.542Z
---

2026-10-06 Cody: "outside traffic inside" an apartment and "reverb/reflections are mono, not binaural around me".
Cause: Embree TileSceneSet double buffer. The first rebuild (any door swinging) hands over the other pair of top
scenes and it traces as empty: worker occlusion 0.00 for every sustained voice for good, traced reverb/echoes
lose their walls. Not geometry stage 2 (f94e8677 has it). Reuse-one-pair or start-on-pair-1 experiments: the
SECOND, different pair handed over is the broken one.

Stopgap a0821b20: tile scenes OFF unless OPENFPS_TILE_SCENES=1 (whole-scene build is correct). Root-cause agent
sent; it must turn them back on (streamed maps rebuild whole scenes while driving until then).

**How to apply:** in a client log, all `[ADBG] ... occ=0.00` right after `You ... pull it open` = this. Repro:
AudioLab `--path-probe map=city ear=20.3,1.6,194 src=38.3,1.0,194 door=9.675,1.07,156.6` (0.95 -> 0.00).
Related: [[door-rebuilds-and-jamb-routes]], [[world-streaming]], [[map-travel-mono]].
