---
name: portal-penalty-leak
description: 10-01 flat broadband leaks into Selby House = legacy portal pathfinder in SpatialAcoustics.CalculateMainPath (penalty constants, door-state blind); open front door = one-obstacle barrier search can't do 2 bends; fix = routes through openings for every voice
metadata:
  type: project
---

Cody 10-01 (after walls + wheels merged): sirens/horns leak into the flats un-low-passed; with the front
door open, passing cars barely heard. Trace (~/.cache/sess.log = his 06:38-07:02 session; [ADBG] eqLMH per
voice): one-shots in the corridor at a FLAT (0.21,0.20,0.20); horns 300 m at flat 0.65/0.82. Cause: the
hand-rolled portal route (SpatialAcoustics.CalculateMainPath) charges arbitrary penalties (DetourPenalty
0.6 cap 0.4, aperture 0.25, bleed*0.2) and knows nothing of door state; per band the better route wins
so it floors everything. Open door: barrier search finds ONE obstacle detour; street->door->corridor
needs 2+ bends -> falls to wall transmission. Fix in progress (agent, worktree): opening graph routes
(door state from leaf pose, diffraction per opening, shut door = leaf TL) for all voices; retire constants.
Also 10-01: "shelter" in the corridor was the Z key in a doorway (zones stop at walls). FIXED 4dae3e39: gaps say "doorway between A and B". Client logs [SAY] and [ZONE] now (3682f76f) — read them first for any "what said that".
UnderShelter (auto-announce suppressed since fe9ede65; where-am-I key still says it). Asked him when.
Parallel agent: traffic skids/brakes harder since per-wheel physics + per-tyre screech (stage 4).

**FIXED + MERGED 10-01 (c5566a73), unheard.** OpenFPS.Common/OpeningRoutes.cs + Client OpeningGraph.cs:
openings (doorways measured from walls, shut leaf = WallTransmission, open = free), Dijkstra up to 4
routes, Fresnel-Kirchhoff aperture loss chained Epstein-Peterson, intermediate rooms' reverberant field
by the transmission-room equation; every voice uses it; AcousticPathfinder + penalty constants retired.
PortalComponent gained OpeningCentre/OpeningRotation (wire: restart server). Lab: corridor, door open, car
13 m: -13/-10/-9 (was -52/-89/-100); door shut -28/-61/-70; far horn one-shot -1/-3/-6 -> -58/-100/-100.
Open: corridor reverb fed only at dry level (street too quiet far down the corridor); stair voids not
openings; 14/524 openings disagree with geometry (logged at load); whole buildings of many boxes block
the one-box barrier.
