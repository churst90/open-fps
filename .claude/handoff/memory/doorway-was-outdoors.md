---
name: doorway-was-outdoors
description: "10-03 FIXED (c1ff493f, unheard): \"bursts of outside noise when I step into different zones\" = listener region -1 in an interior doorway; outdoor bus 0.01 -> 1.00; read [REVERB] bus=-1 lines to confirm"
metadata:
  node_type: memory
  type: project
  originSessionId: e3233dbc-96e4-4267-8826-c87e85309fd2
  modified: 2026-10-04T02:04:37.365Z
---

Cody 2026-10-03, after several sessions: bursts of outside noise at every zone crossing in a building,
and siren pops. Cause (from /tmp/openfps-sa-on.log): a doorway is the wall's thickness, in no region
box, so `SpatialService.GetRegionAt` returned GlobalRegionId (-1, outdoors). While he stood in the
Brandt Court stairwell/corridor doorway, `[REVERB] listenerRegion=-1 bus=-1(Outside) target=1.00`
(was 0.01); stepping in, vol decayed from 0.30. The siren's own direct path ([ADBG] eqLMH) was smooth
and fully blocked, so the pops rode the outdoor reverb.

Fix: `SpatialAcoustics.GetRegionAt` -> `RoomInOpening` over the opening graph (the room on the
point's side, found by asking the boxes just past the face; Opening.Normal does NOT reliably point at
RegionA, CheckSides accepts either order). `GetZoneAt` = boxes only, used for names so the doorway is
still announced. Kept in Client.Core, NOT OpenFPS.Common: any Common .cs edit changes WireContract.Hash.

**How to apply:** for any "burst"/"pop" at a crossing, grep the client log for
`[REVERB] listenerRegion=` with `bus=-1(Outside)` and line it up against `[ZONE]` before theorising.
[ADBG] L/R is measured at the spatializer INPUT, before occlusion EQ: not what is heard.
See [[region-is-not-a-room]], [[doorway-field-law]], [[portal-penalty-leak]].
