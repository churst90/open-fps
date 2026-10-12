---
name: bodies-wheels-roads-plan
description: 09-27 Cody's order: physical bodies for everything, nothing hard-coded, per-wheel physics, roads as data, traffic lights + APS; plan in docs/NEXT_BODIES_WHEELS_ROADS.md
metadata:
  type: project
---

Cody 2026-09-27: "all bodies in the game on a map need to have physical bodies. nothing should be
hard coded in any models or maps"; wants per-wheel physics (load transfer, slip, wheel speed from
tyre size) and roads carrying surface as data; traffic lights and accessible crosswalks as the
natural extension. Plan: docs/NEXT_BODIES_WHEELS_ROADS.md, stages: 1 roads as data (regenerates the
city), 2 bodies, 3 per-wheel physics, 4 four tyre sources (train multi-tap voice), 5 signals + APS.
Numbers marked "to confirm" must come from sources (IDM, Pacejka, CPX ISO 11819-2, MUTCD 4D/4E,
US Access Board). The plan's table lists the hard-coded constants found by survey.
**How to apply:** show Cody the plan before stage 1 changes the generator; every physical value
becomes preset/map data with today's value as default. See [[no-special-cases]], [[city-block-map]].

**Progress 09-27 (dfb608b):** stage 1a done — Roads.cs (RoadData/LaneData/JunctionData/RoadNetwork),
gen_city.py record_road() + find_junctions(), MapManager.TryGetRoads, RoadNetworkTests. 17 roads,
27 junctions, 202 lane segments. Southgate square moved north side onto Dock Street (SG_Z1 -130) and
its streets finally laid (appended so ids hold). Next: 1b car following (IDM), 1c traffic on lanes,
1d gap acceptance, 1e pedestrians crossing.
Voice ground paper (inbox/aacus230104.pdf) read: reflection 0..+2.6 dB <800 Hz at 3-7 m, weaker
800-1.6k; our render was weaker than reality, so "too strong" is ruled out; next idea is head/body
movement in a lab render.

**Progress 09-27 later:** 1b car following (IDM, VehicleSystem.Following.cs) and 1c traffic on lanes
(LaneRoutes.cs: Via + seeded Random tours as CLOSED loops so RaceLine/stops/parking work unchanged;
VehicleSystem.Routes.cs: give-way from JunctionData.PriorityRoads, RoadStops by position, crossings by
position, following by lane segment using PROJECTED world position — lap mapping disagreed by metres
between routes). Only rail_loop remains a drawn track. Tests: RouteTrafficTests, CarFollowingTests
(junction interiors excluded: two cars turning into one lane still overlap = 1d gap acceptance next).
gen_city.py reproduces city.json byte for byte (RoadNetworkTests). Lab siren tools default track=downtown
(built from roads; run from repo root).
Cody 09-27 also: bumps "woofy hollow thunk, all the same" (need radiation efficiency + hard ~1 ms hand
contact); beacons → bell/chord earcons per category; doors need mechanism model (refs in inbox); voice
ground reflection stays OFF.
**1d gap acceptance done (1357304):** VehicleSystem.Junctions.cs. Lessons: drivers must see traffic
several lane-stretches back (150 m look); gap counted from reaching the line; committed vehicles
(can't stop before line) count as inside; stop with bumper at line (half length); lane-origin turn
rules TRAPPED tours (no lane changes) → reverted until lane changes exist; "follow what left your
lane into the junction" only when nothing is between on the lane. Test runs that share an artifacts
folder with a rebuild are invalid; and `pgrep -f 'dotnet test ...'` matches its own bash line (never
use it as a wait loop). Next: 1e pedestrians crossing (then stage 2 bodies).

**1e pedestrians crossing done 09-28 (uncommitted at write time):** VehicleSystem.Crosswalks.cs —
crossings DERIVED where a walker's shuttle line passes over a road carriageway (90 in the city);
vehicle events found by sampling the RaceLine itself every 0.5 m (route Points scaled onto the line
were metres out at corners). Walker gap = walk time + StreetLife.PedestrianStartUpSeconds (3), after
PedestrianRiskSeconds (30) walk time only; drivers stop for OnIt, and for a kerb-waiter > 8 s ONLY on
their arrival lane (stopping inside a junction for an exit-arm waiter gridlocked it). Junction fixes it
exposed: ShortOfTheLine (lap says in-junction but position still on the lane before; used in
JunctionHold, IndexJunctions, IndexSegments — NOT in WhereOnLane itself, that gave 51 meetings);
patience release waits for anything < 4 s out. CrosswalkTests; turning paths on 7 m estate roads cut
the kerb (todo). Next: stage 2 bodies.
