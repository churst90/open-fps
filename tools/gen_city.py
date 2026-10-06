#!/usr/bin/env python3
"""
Generates OpenFPS.Server/maps/city.json — a whole city, not one block.

WHAT CHANGED, AND WHY IT IS A DIFFERENT MAP. The first city was one block: two three-storey
buildings, a street between them, a tunnel, a garage and a bus shelter. It answered the questions it
was built for — is a flat a different place from a stairwell, does a tunnel ring, does a car park —
and those are now answered by ear. What it could not answer is everything that needs DISTANCE and
NUMBER: traffic that arrives and goes away, a train crossing a mile off, an airliner overhead, a
mower two gardens along, a city's worth of window air conditioners none of which is worth a voice on
its own. So the map grew to the size those questions need.

  1 km square, in four quarters:

        z +560  ┌────────────────────────────────────────────────────────┐
                │   RESIDENTIAL     │      DOWNTOWN       │   AIRPORT    │
                │   houses, yards,  │  five towers, a     │  runway,     │
                │   mowers, fences  │  garage, a plaza    │  terminal,   │
                │                   │                     │  hangar      │
        z -360  └────────────────────────────────────────────────────────┘
                x -520                                              x +520

  ...with a light rail loop right round the outside of all three, and the tunnel carrying Main
  Street south under the rail's own bridge.

WHY THIS IS GENERATED. Unchanged from the first city and more true at this size: a building is a
hundred boxes whose coordinates all have to agree, and a storey is the same hundred boxes three
metres up. This map is about five thousand boxes. Typing them is how a wall ends up a decimetre out
and a room stops being enclosed — not a visible mistake, a room that sounds slightly wrong for ever.
The dimensions below are the source; the map is an output. Run it again after changing one.

NOTHING HERE DECLARES AN ACOUSTIC ANYTHING. No region names its own materials: every one is measured
at load from the walls actually around it (MapManager.SurveyRegions). A flat is brick because there
is brick round it, a hangar is steel because it is made of steel, and a front lawn is outdoors
because there is nothing over it. The log line that says so is the first thing to read after a
change.
"""
import json, math, os, glob, sys

# Run from the repository root. The map it writes is exactly the one shipped: every choice that looks
# random is seeded, and RoadNetworkTests.The_generator_reproduces_the_shipped_city checks it.
#   python3 tools/gen_city.py                 writes OpenFPS.Server/maps/city.json
#   python3 tools/gen_city.py --out=FILE      writes FILE instead
PREFAB_DIR = "OpenFPS.Server/prefabs"
OUT = next((a[len("--out="):] for a in sys.argv[1:] if a.startswith("--out=")), "OpenFPS.Server/maps/city.json")

# ── The prefabs' own dimensions, read rather than remembered ──────────────────────────────────────
#
# Every box below is written as the space it occupies, and the scale that produces it is worked out
# from the prefab's collider (tools/mapgen.py, which tools/gen_osm.py shares): hard-coding those sizes
# here is the standing way for a generated map to drift from the prefabs it is made of.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mapgen import BASE, HINGED, SOLID, v3, yaw  # noqa: E402


_next_id = [1000]


def new_id():
    _next_id[0] += 1
    return _next_id[0]


entities = []

# ── What a part is called ─────────────────────────────────────────────────────────────────────────
#
# Every part has a name that says where it is and what it is, because the name is what a player
# hears when they scan or walk into it: "Brandt Court east wall", "Brandt Court front entrance",
# "Brandt Court roof parapet, west side", "Marlow Tower stairwell wall", "Parking garage west side,
# level 2", "12 Elm Street front door". The place comes first, then the part, then after a comma
# which floor, level or side. A side is a compass point (X east, Z north); a house's front faces its
# street. Unnamed, a part answered to its prefab — "Brick Wall", "Concrete Floor" — which says what it
# is made of and nothing about where you are (Cody, 2026-10-04: "I need to hear what I ran into").
#
# The material stays the prefab's: the acoustics key on it, never on these names.


def box(prefab, x0, x1, y0, y1, z0, z1, name=None):
    """One box, written as the space it fills. Scale comes from the prefab's own collider."""
    bx, by, bz = BASE[prefab]
    e = {
        "EntityId": new_id(),
        "PrefabId": prefab,
        "Position": v3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
        "Scale": v3((x1 - x0) / bx, (y1 - y0) / by, (z1 - z0) / bz),
    }
    if name:
        e["Name"] = name
    entities.append(e)
    return e["EntityId"]


def prop(prefab, x, y, z, name=None, facing=None):
    """A thing that stands somewhere at its own size — a machine, a speaker, a bollard.

    Unlike box(), the prefab's collider IS the size: an air conditioner is the size an air
    conditioner is, and scaling one to fill a hole in a wall would make it a different machine.
    """
    e = {
        "EntityId": new_id(),
        "PrefabId": prefab,
        "Position": v3(x, y, z),
        "Scale": v3(1, 1, 1),
    }
    if facing is not None:
        e["Rotation"] = yaw(facing)
    if name:
        e["Name"] = name
    entities.append(e)
    return e["EntityId"]


def region(name, x0, x1, y0, y1, z0, z1):
    """A named place. It says WHERE and WHAT IT IS CALLED and nothing else.

    Naming a place costs nothing (region-is-not-a-room) and an unnamed one is a place a player
    cannot be told they are in, so these are generous.
    """
    return box("acoustic_region", x0, x1, y0, y1, z0, z1, name=name)


named_places = []


def named_place(name, x0, x1, y0, y1, z0, z1):
    """A part of a room with a name of its own — a flight of stairs, a landing — and NOT a room.

    A region is a name and a room for sound at once (region-is-not-a-room is about the name; the
    sound is still keyed on the box), so cutting a stairwell into a region per flight would cut its
    reverberation and its openings into pieces as well. A named place changes what you are told and
    nothing you hear. Written after everything else, so adding one moves no other part's id.
    """
    named_places.append((name, x0, x1, y0, y1, z0, z1))


def portal(x, y, z, a, b, aperture):
    """A hole between two named places. -1 is the outside."""
    entities.append({
        "EntityId": new_id(),
        "PrefabId": "portal",
        "Position": v3(x, y, z),
        "RegionAId": a, "RegionBId": b, "ApertureSize": aperture,
    })


def door(x, y0, z, a, b, facing_z=True, prefab="door", opening=None, turn=0.0, name=None):
    """A leaf in a doorway, joining two named places.

    A door is ALREADY a portal — PrefabRepository attaches one to anything with IsDoor — so a doorway
    needs a door and NOT a door plus a portal beside it.

    `opening` is the width of the gap in the wall, when it is not the leaf's own. The leaf is made
    to cover it and lap each jamb by DOOR_LAP: a leaf narrower than its opening leaves a slot you can
    walk round shut, which is what 71 doors on this map did until they were refitted on 2026-09-23.

    `turn` turns the leaf about its own middle, radians: half a turn sends a sliding leaf the other
    way. A swinging leaf is turned by where its rooms are instead, and `turn` is not used for it.

    WHICH WAY IT SWINGS. A hinged leaf's +Z face is its outside, and its prefab says which face it is
    pushed from (PushSide): a room door is pushed from outside and swings into the room, an exit door
    (a push bar, a building's front entrance) is pushed from inside and swings out, toward the street,
    as an exit does. So every hinged leaf here is turned with its +Z face AWAY from place `a`, the room
    it belongs to, which is also where a front door's key side (KeyedSide +1) has to be. Until
    2026-10-05 half the flats' doors swung into the corridor and every entrance swung inward.
    """
    bx, by, bz = BASE[prefab]
    base = 0.0 if facing_z else math.pi / 2
    if prefab in HINGED:
        room = next((r for r in reversed(entities) if r["EntityId"] == a), None)
        if room is None:
            raise SystemExit(f"door {name!r}: place {a} must be made before its door, to know which side is in")
        to_room = (room["Position"]["X"] - x) * math.sin(base) + (room["Position"]["Z"] - z) * math.cos(base)
        turn = math.pi if to_room > 0 else 0.0
    e = {
        "EntityId": new_id(),
        "PrefabId": prefab,
        "Position": v3(x, y0 + by / 2, z),
        "Rotation": yaw(base + turn),
        "RegionAId": a, "RegionBId": b,
    }
    if opening is not None:
        e["Scale"] = v3(round((opening + 2 * DOOR_LAP) / bx, 4), 1, 1)
    if name:
        e["Name"] = name
    entities.append(e)


def bi_parting(x, y0, z, a, b, facing_z=True, prefab="auto_sliding_door", opening=2.0, name=None):
    """Two sliding leaves meeting in the middle of a doorway, each sliding away from the other.

    Each leaf covers half the opening and laps its own jamb by DOOR_LAP. A sliding leaf moves toward
    its own +X, so the leaf on the far side is turned half a turn to slide the other way.
    """
    bx, by, bz = BASE[prefab]
    base = 0.0 if facing_z else math.pi / 2
    ux, uz = math.cos(base), -math.sin(base)      # the leaf's own +X, in (x, z)
    w = opening / 2 + DOOR_LAP
    for s in (+1, -1):
        e = {
            "EntityId": new_id(),
            "PrefabId": prefab,
            "Position": v3(x + s * ux * w / 2, y0 + by / 2, z + s * uz * w / 2),
            "Rotation": yaw(base + (0.0 if s > 0 else math.pi)),
            "RegionAId": a, "RegionBId": b,
            "Scale": v3(round(w / bx, 4), 1, 1),
        }
        if name:
            e["Name"] = name
        entities.append(e)


DOOR_LAP = 0.05                          # how far a leaf overlaps each jamb, m


# ══ Dimensions ════════════════════════════════════════════════════════════════════════════════════

CARRIAGEWAY = 12.0                       # kerb to kerb on a city avenue, m — two lanes each way
PAVEMENT    = 3.5
KERB        = CARRIAGEWAY / 2            # 6.0
WALK        = KERB + PAVEMENT            # 9.5: the building line

LANE        = 3.0                        # one traffic lane; a car's line sits on the middle of one
RES_CARRIAGEWAY = 7.0                    # a residential street is one lane each way and no more
RES_WALK    = RES_CARRIAGEWAY / 2 + 1.8

STOREY      = 3.0
SLAB        = 0.25
WALL_T      = 0.35
DOOR_W      = 1.0
DOOR_H      = 2.1                        # a door leaf's height (prefabs/door.json); the lintel starts here
CORRIDOR    = 2.2
FLAT_DEPTH  = 9.0
FLATS       = 4                          # per side per storey, plus the stairwell slot

# A stair is built to the figures a real one is, from the International Building Code (2021),
# chapter 10, the stairs a block of flats has to have:
#   1011.5.2  a riser 4 to 7 in (102 to 178 mm); a tread at least 11 in (279 mm) deep
#   1011.2    at least 44 in (1118 mm) wide where it serves fifty people or more
#   1011.6    a landing top and bottom of every flight, as wide as the stair and as deep as it is wide
#   1011.8    no flight rising more than 12 ft (3658 mm) without a landing: one flight a storey is allowed
#   1015.3    a guard 42 in (1067 mm) high wherever the floor drops away more than 30 in
#
# Until 2026-10-04 a flight was ten 30 cm risers on 32 cm goings — a ladder of a stair, 43 degrees,
# three metres long a storey — and the two flights of the dog-leg stood either side of a 20 cm wall,
# each in a 1.6 m slot between brick and concrete. "The stairs also seem kind of short ... way too
# narrow and close to each other" (Cody). Seventeen 17.6 cm risers on 28 cm goings is the ordinary
# office stair, 32 degrees and 4.8 m a storey, and the two flights now have an open well between
# them, guarded, so the whole shaft is one space, as it is in a real stairwell.
STAIR_RISE  = 0.178                      # the steepest riser allowed; a flight takes as many as it needs
STAIR_GOING = 0.28                       # the shallowest tread allowed, on every step
STAIR_W     = 1.6                        # each flight; over the 1.12 m minimum and not narrowed from before
STAIR_WELL  = 0.8                        # the open gap between the two flights, guarded both sides
# A storey's flight covers this much ground. The ground floor's flight climbs 23 cm more (its floor is
# laid on the ground, not on a slab) and takes the two extra steps it needs at its foot, on the
# same 28 cm tread, rather than steeper ones.
FLIGHT_RUN  = math.ceil(STOREY / STAIR_RISE - 1e-6) * STAIR_GOING
STAIR_LANDING = 2.0                      # floor between the stairwell's end wall and the flights: the
                                         # turn, deeper than the stair is wide, with room for the ground
                                         # floor's two extra steps
# How far the opening in the floor above runs back past the first riser. A body on a step has its
# head 1.8 m up, and the movement engine checks a step-up 0.4 m higher still before taking it, so
# the opening has to start behind the foot of the flight or the fourth step is a ceiling.
STAIR_HEAD  = 0.5
RAIL_H      = 1.07                       # a guard: round an opening in a floor, along the well (1015.3)
RAIL_T      = 0.1
GUARD_STEPS = 3                          # a flight's guard is stepped up it in pieces this many treads long
MARKER_BACK = 0.5                        # a stair marker stands this far out from the end riser, clear of the well's guard
FLIGHT_HEAD = 2.0                        # a flight's named place reaches this far over its top tread: an eye and some
PARAPET_H   = 1.1                        # a roof's edge wall: the height building rules ask for
BULK_H      = 2.6                        # clear height inside the stair housing on a roof
BULK_T      = 0.2
BULK_LANDING = 2.0                       # from the top riser to the housing's door

# The grid. Avenues run north-south, streets east-west, and they cross at nine places downtown.
AVENUES = (-130.0, 0.0, 130.0)
STREETS = (-130.0, 0.0, 130.0, 260.0)

MAIN_Z0, MAIN_Z1 = -300.0, 420.0         # Main Street, x = 0: the longest road on the map
AVE_Z0, AVE_Z1   = -140.0, 272.0         # the two side avenues
ST_X0, ST_X1     = -140.0, 140.0         # the cross streets, downtown span

TUNNEL_Z0, TUNNEL_Z1 = -300.0, -200.0    # Main Street goes under for a hundred metres
TUNNEL_H = 5.5

# ── The rail ──────────────────────────────────────────────────────────────────────────────────────
#
# A loop right round the outside, because a train is the one thing on this map that is loud enough to
# be worth hearing from the other side of it. Two and a half kilometres of it, so a unit doing
# 60 km/h takes two and a half minutes to come round — long enough that hearing one approach is an
# event rather than a loop.
RAIL_W      = 4.2                        # the ballast shoulder to shoulder
RAIL_X_E, RAIL_X_W = 178.0, -466.0
# The south leg clears the TUNNEL, and that is why it is at -180 rather than -228.
#
# At -228 it crossed Main Street where Main Street is a hundred metres of roofed concrete box: the
# formation is at rail level and the tunnel's walls stand five and a half metres out of the ground,
# so the line ran straight through them. The shipped "every track is driveable" test said so, at six
# sampled points. A bridge would have needed a nine per cent gradient to clear the roof, which is
# more than twice what a railway can climb; north of the portal it crosses on the flat, which is a
# LEVEL CROSSING — a better thing to have on the map than a bridge anyway.
RAIL_Z_N, RAIL_Z_S = 452.0, -180.0
# Southgate's square of streets. Its north side IS Dock Street (z = -130): it was a separate road ten
# metres south of it, which as road data is two carriageways side by side.
SG_X0, SG_X1, SG_Z0, SG_Z1 = -160.0, -40.0, -260.0, -130.0
RAIL_CORNER = 26.0                       # radius of the four corners, m

# ── The airport ───────────────────────────────────────────────────────────────────────────────────
RUNWAY_X   = 380.0
RUNWAY_W   = 45.0
RUNWAY_Z0, RUNWAY_Z1 = -288.0, 300.0     # 588 m of asphalt
TAXI_X     = 302.0
TAXI_W     = 23.0
APRON_X0, APRON_X1 = 228.0, 288.0
APRON_Z0, APRON_Z1 = -10.0, 128.0
TERM_X0, TERM_X1 = 196.0, 226.0
TERM_Z0, TERM_Z1 = -24.0, 140.0
TERM_H     = 6.5                         # one tall storey, a concourse
HANGAR_X0, HANGAR_X1 = 230.0, 278.0
HANGAR_Z0, HANGAR_Z1 = 162.0, 218.0
HANGAR_H   = 11.0

# ── The residential quarter ───────────────────────────────────────────────────────────────────────
RES_STREETS = (-96.0, -24.0, 48.0, 120.0)      # four east-west streets of houses
RES_X0, RES_X1 = -424.0, -168.0
# ...and the two north-south lanes that cross them. A grid of streets with no way between them is a
# set of cul-de-sacs, and a route round the estate needs two sides that are actually roads: the
# first draft ran the traffic loop up x = -176 where there was no road at all, and the shipped
# "every track is driveable" test caught it as twenty sample points inside somebody's front room.
RES_LANES = (-296.0, -180.0)
RES_LANE_CLEAR = 13.0                          # no plot may stand this close to a lane
PLOT_W     = 22.0                        # street frontage of one plot
HOUSE_W, HOUSE_D = 11.0, 8.5
HOUSE_H    = 2.6                         # floor to ceiling inside a bungalow

# The bounds are the ACOUSTIC grid's bounds, and aircraft live inside them.
#
# The built city is 1040 x 920 on the ground. The bounds are much bigger than that in every
# direction, and taller than any building by a factor of fifteen, because an airliner's approach
# starts a kilometre out and nine hundred metres up — and anything outside the grid has no region at
# all and nothing says so. That is how a third of the speedway's corners once had no acoustics: the
# bounds were guessed and the track did not fit in them. The grid is a SPARSE octree, so empty sky
# costs nothing to declare and a great deal to leave out.
MAP_MIN = (-900.0, 0.0, -1150.0)
MAP_MAX = (900.0, 900.0, 1350.0)

# ...and where the GROUND is, which is a different question from where the bounds are.
#
# The bounds are big because aircraft are: an approach starts a kilometre out and nine hundred metres
# up, and anything outside the grid has no region at all. The ground has no such need, and laying one
# box over the whole of it is expensive in a way that is easy to miss — the spatial grid is a
# dictionary of 10 m cells and a box is filed in EVERY cell it overlaps, so a ground plane across the
# bounds is 45,000 cells and 45,000 lists for one entity, rebuilt every time a definition arrives.
# Across the built area it is 1,900. (IsSolid defaults to TRUE, so a floor is in the grid like
# anything else; that is the part that makes it matter.)
BUILT_MIN = (-520.0, -360.0)
BUILT_MAX = (520.0, 560.0)

# Where a new player lands: on the south pavement of Foundry Street (x 60, z 122; the pavement is
# z 120.5..124 and 0.12 high), facing north across the street. It was the middle of Main Street, in
# the traffic, until Cody asked on 2026-10-04 to start on a sidewalk, at a spot he had stood on.
SPAWN = (60.0, 0.15, 122.0)

# ══ Ground ════════════════════════════════════════════════════════════════════════════════════════
#
# Laid before anything else, in the materials the surfaces really are. The ground probe takes the
# HIGHEST surface under your feet and a tie goes to whichever box it tested first, so every made
# surface stands PROUD of the one under it: bare ground at -0.2..0, asphalt 0..0.05, pavement
# 0..0.12. A flush tie is what once put 374 footsteps on dirt in the middle of a road.
box("dirt_floor", BUILT_MIN[0], BUILT_MAX[0], -0.2, 0.0, BUILT_MIN[1], BUILT_MAX[1], name="Ground")


def carriageway(x0, x1, z0, z1, name=None):
    box("asphalt_road", x0, x1, 0.0, 0.05, z0, z1, name=name)


# ── Roads as data ─────────────────────────────────────────────────────────────────────────────────
#
# Every road is also a record: its centreline, its lanes and its type (OpenFPS.Common/Roads.cs). The
# record is written by the same call that lays the asphalt, so the two cannot disagree. The server
# works out the lanes between junctions and where each can go next; the junctions are found here,
# wherever two centrelines meet.
#
# Traffic keeps to the right. A lane's offset is measured to the right of the centreline's own
# direction, so the lanes going that way have positive offsets.
ROAD_TYPES = {
    # type: speed limit, km/h
    "collector":   50.0,
    "residential": 40.0,
    "service":     30.0,
}
ROADS = []


def record_road(name, rtype, a, b, width, surface=None):
    """A straight road from a to b, (x, z), with as many LANE-wide lanes each way as fit."""
    each_way = max(1, int(width / 2 // LANE))
    lw = width / 2 / each_way
    lanes = []
    for k in range(each_way):
        off = (k + 0.5) * lw
        lanes.append({"OffsetMetres": round(off, 3), "Direction": 1, "WidthMetres": round(lw, 3),
                      "SpeedLimitKmh": ROAD_TYPES[rtype]})
        lanes.append({"OffsetMetres": round(-off, 3), "Direction": -1, "WidthMetres": round(lw, 3),
                      "SpeedLimitKmh": ROAD_TYPES[rtype]})
    rid = name.lower().replace(" ", "_")
    ROADS.append({"Id": rid, "Name": name, "Type": rtype,
                  "Centreline": [v3(a[0], 0.05, a[1]), v3(b[0], 0.05, b[1])],
                  "WidthMetres": width, "Lanes": lanes,
                  "Surfaces": [] if surface is None else surface})
    return rid


FOOTWAYS = []                            # every pavement, for the people who walk them


def footway(x0, x1, z0, z1, name=None, label=None):
    """A pavement. Called "<street> sidewalk, <side>" from its label, "Main Street, west side"."""
    if name is None and label:
        road, _, where = label.partition(", ")
        name = f"{road} sidewalk, {where}" if where else f"{road} sidewalk"
    box("concrete_floor", x0, x1, 0.0, 0.12, z0, z1, name=name)
    FOOTWAYS.append((x0, x1, z0, z1, label))


def avenue(x, z0, z1, label):
    """A north-south road: asphalt between two kerbed pavements."""
    record_road(label, "collector", (x, z0), (x, z1), CARRIAGEWAY)
    carriageway(x - KERB, x + KERB, z0, z1, name=f"{label} carriageway")
    footway(x - WALK, x - KERB, z0, z1, label=f"{label}, west side")
    footway(x + KERB, x + WALK, z0, z1, label=f"{label}, east side")


def street(z, x0, x1, label):
    """An east-west road."""
    record_road(label, "collector", (x0, z), (x1, z), CARRIAGEWAY)
    carriageway(x0, x1, z - KERB, z + KERB, name=f"{label} carriageway")
    footway(x0, x1, z - WALK, z - KERB, label=f"{label}, south side")
    footway(x0, x1, z + KERB, z + WALK, label=f"{label}, north side")


avenue(AVENUES[0], AVE_Z0, AVE_Z1, "Wharf Avenue")
avenue(AVENUES[1], MAIN_Z0, MAIN_Z1, "Main Street")
avenue(AVENUES[2], AVE_Z0, AVE_Z1, "Calder Avenue")
street(STREETS[0], SG_X0 - KERB, ST_X1, "Dock Street")
street(STREETS[1], RES_X0 - 24.0, TERM_X0 - 6.0, "Central Street")
# The main streets that run through the housing estate, (z, x0, x1): a back garden stops at them.
CROSSING_STREETS = [(STREETS[1], RES_X0 - 24.0, TERM_X0 - 6.0)]
street(STREETS[2], ST_X0, APRON_X0, "Foundry Street")
street(STREETS[3], ST_X0, ST_X1, "North Street")

# The intersections themselves: asphalt laid over the pavement corners, so that walking across one is
# walking on a road the whole way rather than stepping onto a kerb in the middle of it.
#
# Named for the two roads, as the road network names its junctions, and "junction": the driving aids
# know a junction by that word (DrivingAids.IsJunction) and say "a junction" rather than its name.
AVENUE_NAMES = dict(zip(AVENUES, ("Wharf Avenue", "Main Street", "Calder Avenue")))
STREET_NAMES = dict(zip(STREETS, ("Dock Street", "Central Street", "Foundry Street", "North Street")))
for ax in AVENUES:
    for sz in STREETS:
        if ax != 0.0 and not (AVE_Z0 <= sz <= AVE_Z1):
            continue
        if sz == STREETS[3] and ax not in AVENUES:
            continue
        carriageway(ax - WALK, ax + WALK, sz - WALK, sz + WALK,
                    name=" and ".join(sorted((AVENUE_NAMES[ax], STREET_NAMES[sz]))) + " junction")

# ══ An apartment or office tower ══════════════════════════════════════════════════════════════════


def tower(label, x0, x1, z0, z1, storeys, street_side, ac_floors):
    """
    Storeys of flats either side of a corridor, with a stairwell at one end and a lift shaft.

    Built out of the parts a building is built out of and nothing else: brick outside, concrete slabs
    between the storeys, a tiled stairwell, plaster inside, carpet in the flats, doors in the
    doorways, and — the new part — a window air conditioner in a window of every flat on the lower
    floors. `street_side` is the face the entrance and the air conditioners are on: '+x', '-x', '+z'
    or '-z'.
    """
    vertical = street_side in ("+x", "-x")
    # Work in a frame where the corridor runs along z and the street is at +x, then map back.
    if vertical:
        sx0, sx1, sz0, sz1 = x0, x1, z0, z1
    else:
        sx0, sx1, sz0, sz1 = z0, z1, x0, x1

    def place(a0, a1, b0, b1):
        """(across, along) -> (x, z), whichever way this building is turned."""
        return (a0, a1, b0, b1) if vertical else (b0, b1, a0, a1)

    def B(prefab, a0, a1, y0, y1, b0, b1, name=None):
        px0, px1, pz0, pz1 = place(a0, a1, b0, b1)
        return box(prefab, px0, px1, y0, y1, pz0, pz1, name=name)

    def R(name, a0, a1, y0, y1, b0, b1):
        px0, px1, pz0, pz1 = place(a0, a1, b0, b1)
        return region(name, px0, px1, y0, y1, pz0, pz1)

    side = +1 if street_side in ("+x", "+z") else -1
    inner = sx0 + WALL_T, sx1 - WALL_T
    if side > 0:
        far_flat = (inner[0], inner[0] + FLAT_DEPTH)
        corridor = (far_flat[1] + WALL_T, far_flat[1] + WALL_T + CORRIDOR)
        near_flat = (corridor[1] + WALL_T, inner[1])
    else:
        near_flat = (inner[0], inner[0] + FLAT_DEPTH)
        corridor = (near_flat[1] + WALL_T, near_flat[1] + WALL_T + CORRIDOR)
        far_flat = (corridor[1] + WALL_T, inner[1])

    slots = FLATS + 1                                   # the extra slot is the stairwell
    slot_len = (sz1 - sz0 - WALL_T * 2) / slots
    stair_b = (sz0 + WALL_T, sz0 + WALL_T + slot_len)

    def P(a, b):
        """One point (across, along) -> (x, z)."""
        px, _, pz, _ = place(a, 0, b, 0)
        return px, pz

    def heading(d):
        """The yaw that faces +along (d > 0) or -along (d < 0)."""
        if vertical:
            return 0.0 if d > 0 else math.pi
        return math.pi / 2 if d > 0 else -math.pi / 2

    def holed(make, a0, a1, b0, b1, hole):
        """A rectangle with a rectangular opening cut out of it, as the pieces round the opening:
        each piece is (a0, a1, b0, b1, whatever make() returned for it)."""
        if hole is None:
            return [(a0, a1, b0, b1, make(a0, a1, b0, b1))]
        ha0, ha1 = max(hole[0], a0), min(hole[1], a1)
        hb0, hb1 = max(hole[2], b0), min(hole[3], b1)
        out = []
        for p0, p1, q0, q1 in ((a0, a1, b0, hb0), (a0, a1, hb1, b1), (a0, ha0, hb0, hb1), (ha1, a1, hb0, hb1)):
            if p1 - p0 > 1e-6 and q1 - q0 > 1e-6:
                out.append((p0, p1, q0, q1, make(p0, p1, q0, q1)))
        return out

    def B_holed(prefab, a0, a1, y0, y1, b0, b1, hole, name=None):
        return holed(lambda p0, p1, q0, q1: B(prefab, p0, p1, y0, y1, q0, q1, name=name), a0, a1, b0, b1, hole)

    def compass(axis, high):
        """The side of this building an edge is on: `axis` "a" (across) or "b" (along), at its high
        or low end, as a compass point. X is east and Z is north."""
        if (axis == "a") == vertical:
            return "east" if high else "west"
        return "north" if high else "south"

    def wall(side):
        return f"{label} {side} wall"

    # ── The stair plan: a dog-leg, the same on every storey ─────────────────────────────────────
    #
    # Two lanes against the street wall with an open well between them, and the flights going up
    # them alternately: one runs along the building, the next comes back beside it. At the top of a
    # flight you turn round and the foot of the next one is beside you, across the well.
    #
    # The well is open from the ground floor to the top storey — a gap in every floor between the
    # flights, guarded along both flights and across both ends on every floor — so the stairwell is
    # one tall space from bottom to top. It was a 20 cm wall floor to ceiling, which made each flight
    # a corridor of its own.
    #
    # The flights used to be stacked straight above one another, every storey's flight in the same
    # place, under ceiling slabs laid whole across the stairwell. The movement engine could not get
    # a body past the second step — its head was in the slab (2026-10-04, --walk) — and had the
    # slabs been cut, the flight above would have been the ceiling instead. A stair in the same
    # column as the one below it is not a stair anybody can climb.
    #
    # The flight from the top storey to the roof is always the one against the street wall, so the
    # way onto the roof is up that flight and straight on through the door at the end of the housing.
    street_a = inner[1] if side > 0 else inner[0]       # the stairwell's face of the street wall
    inward = -side

    def span(start, width):
        end = start + inward * width
        return (min(start, end), max(start, end))

    def lane(i):
        return span(street_a + inward * i * (STAIR_W + STAIR_WELL), STAIR_W)

    well = span(street_a + inward * STAIR_W, STAIR_WELL)

    def well_edge(i):
        """The strip of the well along lane i, where that lane's flight has its guard."""
        return span(street_a + inward * (STAIR_W if i == 0 else STAIR_W + STAIR_WELL - RAIL_T), RAIL_T)

    L0 = stair_b[0] + STAIR_LANDING
    L1 = L0 + FLIGHT_RUN
    roof_y = storeys * STOREY + SLAB

    def level(s):
        """What you stand on in the stairwell on storey s (its tile), or on the roof."""
        if s >= storeys:
            return roof_y
        return (s * STOREY + SLAB if s else 0.02) + 0.03

    def flight_lane(s):
        return 0 if (storeys - 1 - s) % 2 == 0 else 1

    def flight_dir(s):
        return +1 if flight_lane(s) == 0 else -1

    def flight(s):
        """Flight s, from storey s up to the next or the roof: (lo, hi, risers, rise, lane, direction,
        foot, top). Its top is at the turn, L1 going one way and L0 the other; a flight that needs more
        risers than a storey's starts further back at its foot."""
        lo, hi = level(s), level(s + 1)
        n = math.ceil((hi - lo) / STAIR_RISE - 1e-6)
        d = flight_dir(s)
        top_b = L1 if d > 0 else L0
        return lo, hi, n, (hi - lo) / n, lane(flight_lane(s)), d, top_b - d * n * STAIR_GOING, top_b

    def hole(s):
        """The opening flight s comes up through, in the floor above it: back past its foot by
        STAIR_HEAD, and level with its top riser, where the floor carries on. In every floor up to the
        top storey's the well beside it is open too; the roof is whole over the well."""
        if s < 0:
            return None
        _, _, _, _, la, d, foot_b, top_b = flight(s)
        a0, a1 = la
        if s + 1 < storeys:
            a0, a1 = min(a0, well[0]), max(a1, well[1])
        b0, b1 = sorted((foot_b - d * STAIR_HEAD, top_b))
        return (a0, a1, b0, b1)

    for s in range(storeys):
        y0 = s * STOREY
        ceil = y0 + STOREY - SLAB
        floor_top = y0 + SLAB if s else 0.02

        floor_name, ceiling_name = f"{label} floor {s}", f"{label} ceiling, floor {s}"
        if s:
            B_holed("concrete_floor", sx0, sx1, y0, y0 + SLAB, sz0, sz1, hole(s - 1), name=floor_name)
        else:
            B("concrete_floor", sx0, sx1, -0.15, 0.02, sz0, sz1, name=floor_name)
        B_holed("concrete_floor", sx0, sx1, ceil, y0 + STOREY, sz0, sz1, hole(s), name=ceiling_name)

        # The brick shell. The street face is BROKEN at the stairwell on the ground floor, and that
        # gap is the front door: a doorway is an absence, not a leaf standing against solid brick.
        entrance = None
        if s == 0:
            eb = (stair_b[0] + stair_b[1]) / 2
            entrance = (eb - 0.9, eb + 0.9)

        street_wall = (sx1 - WALL_T, sx1) if side > 0 else (sx0, sx0 + WALL_T)
        other_wall = (sx0, sx0 + WALL_T) if side > 0 else (sx1 - WALL_T, sx1)
        street_name, other_name = wall(compass("a", side > 0)), wall(compass("a", side < 0))
        B("brick_wall", other_wall[0], other_wall[1], floor_top, ceil, sz0, sz1, name=other_name)
        if entrance:
            B("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, sz0, entrance[0], name=street_name)
            B("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, entrance[1], sz1, name=street_name)
            B("brick_wall", street_wall[0], street_wall[1], floor_top + 2.15, ceil, entrance[0], entrance[1],
              name=street_name)
        else:
            B("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, sz0, sz1, name=street_name)
        B("brick_wall", sx0, sx1, floor_top, ceil, sz0, sz0 + WALL_T, name=wall(compass("b", False)))
        B("brick_wall", sx0, sx1, floor_top, ceil, sz1 - WALL_T, sz1, name=wall(compass("b", True)))

        near_wall = corridor[1] if side > 0 else corridor[0] - WALL_T
        far_wall = corridor[0] - WALL_T if side > 0 else corridor[1]

        # Corridor walls with a doorway per flat cut out of each. On the stairwell's side, the wall
        # either side of the stairwell's opening, and over it, is the stairwell's wall.
        corridor_name, stair_wall_name = f"{label} corridor wall, floor {s}", f"{label} stairwell wall"
        for wall_a in (near_wall, far_wall):
            cuts = []
            for i in range(slots):
                b0 = sz0 + WALL_T + i * slot_len
                dz = b0 + slot_len * 0.5
                cuts.append((dz - DOOR_W / 2, dz + DOOR_W / 2))
            at = sz0
            for i, (c0, c1) in enumerate(cuts):
                piece = stair_wall_name if wall_a == near_wall and i == 0 else corridor_name
                if c0 > at:
                    B("plaster_wall", wall_a, wall_a + WALL_T, floor_top, ceil, at, c0, name=piece)
                # The wall over the door. The cut ran floor to ceiling, so above every shut flat door
                # was a 65 cm slot into the corridor (2026-09-29: a shut door passed -7/-11/-19 dB).
                B("plaster_wall", wall_a, wall_a + WALL_T, floor_top + DOOR_H, ceil, c0, c1, name=piece)
                at = c1
            if at < sz1:
                B("plaster_wall", wall_a, wall_a + WALL_T, floor_top, ceil, at, sz1, name=corridor_name)

        # Between the flats; the first on the street side is the stairwell's other wall.
        for i in range(1, slots):
            pb = sz0 + WALL_T + i * slot_len
            for fa0, fa1 in (far_flat, near_flat):
                B("plaster_wall", fa0, fa1, floor_top, ceil, pb - WALL_T / 2, pb + WALL_T / 2,
                  name=stair_wall_name if i == 1 and (fa0, fa1) == near_flat else f"{label} flat wall, floor {s}")

        # Carpet in the flats and down the corridor, tile left bare in the stairwell — the stairwell
        # is the live space in the building and the contrast is the point. A plastered soffit over
        # all of it, which is what you are under in a flat rather than the bare structural slab.
        B("carpet_floor", far_flat[0], far_flat[1], floor_top, floor_top + 0.04, sz0 + WALL_T, sz1 - WALL_T,
          name=floor_name)
        B("carpet_floor", near_flat[0], near_flat[1], floor_top, floor_top + 0.04, stair_b[1], sz1 - WALL_T,
          name=floor_name)
        B("carpet_floor", corridor[0], corridor[1], floor_top, floor_top + 0.04, sz0 + WALL_T, sz1 - WALL_T,
          name=floor_name)
        B_holed("plaster_wall", inner[0], inner[1], ceil - 0.03, ceil, sz0 + WALL_T, sz1 - WALL_T, hole(s),
                name=ceiling_name)

        # What is IN them. Carpet is deaf to bass (carpet-is-deaf-to-bass): it does its job at mid and
        # top and almost nothing at the bottom, so a carpeted room with hard walls keeps a two-second
        # BASS tail under a 600 ms middle. What takes the bottom out is upholstery with air behind it,
        # which is a membrane absorber — a sofa and a bed per flat, as Audience.
        for i in range(slots):
            b0 = sz0 + WALL_T + i * slot_len
            for which, (fa0, fa1) in (("front", near_flat), ("back", far_flat)):
                if which == "front" and i == 0:
                    continue
                flat = f"{label} flat {s}{i}{which[0].upper()}"
                B("furniture_soft", fa0 + 0.6, fa0 + 1.5, floor_top, floor_top + 0.85, b0 + 1.0, b0 + 3.2,
                  name=f"{flat} sofa")
                B("furniture_soft", fa1 - 2.1, fa1 - 0.3, floor_top, floor_top + 0.6, b0 + 4.6, b0 + 6.4,
                  name=f"{flat} bed")

        corridor_id = R(f"{label} corridor, floor {s}",
                        corridor[0], corridor[1], floor_top, ceil, sz0 + WALL_T, sz1 - WALL_T)

        for i in range(slots):
            b0 = sz0 + WALL_T + i * slot_len
            b1 = b0 + slot_len
            db = b0 + slot_len * 0.5
            for which, (fa0, fa1) in (("front", near_flat), ("back", far_flat)):
                if which == "front" and i == 0:
                    continue
                flat_id = R(f"{label} flat {s}{i}{which[0].upper()}", fa0, fa1, floor_top, ceil, b0, b1)
                wall_a = near_wall if which == "front" else far_wall
                dx0, _, dz0, _ = place(wall_a + WALL_T / 2, 0, db, 0)
                door(dx0, floor_top, dz0, flat_id, corridor_id, facing_z=not vertical, opening=DOOR_W,
                     name=f"{label} flat {s}{i}{which[0].upper()} door")

        # ── The stairwell ──────────────────────────────────────────────────────────────────────
        sa0, sa1 = near_flat
        # The stairwell is one shaft: each storey's box runs through the slabs to the next one's, so
        # the opening a flight comes up through is in a named place all the way and not, for the half
        # metre of the slabs' thickness, out of doors (CityOpeningsTests).
        stair_id = R(f"{label} stairwell, floor {s}", sa0, sa1, y0 if s else floor_top, y0 + STOREY,
                     stair_b[0], stair_b[1])
        B_holed("tile_floor", sa0, sa1, floor_top - 0.02, floor_top + 0.03, stair_b[0], stair_b[1], hole(s - 1),
                name=floor_name)
        px, _, pz, _ = place(near_wall + WALL_T / 2, 0, (stair_b[0] + stair_b[1]) / 2, 0)
        portal(px, floor_top + 1.0, pz, stair_id, corridor_id, 1.4)

        # The flight up from this storey: to the next one, or from the top storey to the roof. A box a
        # step: box k stands on the slab and its top is k + 1 risers over this floor, so the last
        # box's top is the floor above and the count of boxes is the count of risers — the steps up
        # you take, the last of them the one onto the landing. That count is what the markers say.
        lo, hi, n, rise, la, d, foot_b, top_b = flight(s)
        going = STAIR_GOING
        flight_name = f"{label} stairs, floor {s} to {'the roof' if s + 1 == storeys else s + 1}"
        for k in range(n):
            q0, q1 = foot_b + d * k * going, foot_b + d * (k + 1) * going
            B("concrete_floor", la[0], la[1], floor_top, lo + (k + 1) * rise, min(q0, q1), max(q0, q1),
              name=flight_name)

        # "The stairs themselves need a zone, then the landings need a zone" (Cody, 2026-10-04). The
        # flight is a named place from its first riser to its last, up to a body's eye over its top
        # tread; the landing is the floor at the end of it you get on and off at, both lanes and the
        # well between them across, the depth of a landing out from the end risers. Named places, not
        # regions: the stairwell stays one room for sound (see named_place).
        #
        # Every storey has a flight up (the top one's goes to the roof), so every storey's landing is
        # at the foot of its flight up — and on every floor above the ground, the top of the flight
        # from below is beside it, across the well. The roof's landing is the stair housing.
        def NP(name, a0, a1, y_0, y_1, b0, b1):
            px0, px1, pz0, pz1 = place(a0, a1, b0, b1)
            named_place(name, px0, px1, y_0, y_1, pz0, pz1)
        fb0, fb1 = sorted((foot_b, top_b))
        NP(flight_name, la[0], la[1], lo, hi + FLIGHT_HEAD, fb0, fb1)
        out = -d                                        # from the flight up, away along the building
        edge = max([foot_b] + ([flight(s - 1)[7]] if s else []), key=lambda b: out * b)
        lb0, lb1 = sorted((edge, edge + out * STAIR_LANDING))
        both = span(street_a, 2 * STAIR_W + STAIR_WELL)
        NP(f"{label} landing, floor {s}", both[0], both[1], floor_top, ceil,
           max(lb0, stair_b[0]), min(lb1, stair_b[1]))
        # Its guards, along the well and, in the lane away from the street wall, along its open side
        # too: stepped up the flight a few treads a piece, each piece a guard's height over the highest
        # tread beside it and down to a riser under the lowest, so the pieces meet. The last flight's
        # stops at the roof, where the stair housing's wall stands on it.
        railing = f"{label} stairwell railing"
        guards = [well_edge(flight_lane(s))]
        if flight_lane(s) == 1:
            guards.append(span(la[1] if inward > 0 else la[0], RAIL_T))
        for k0 in range(0, n, GUARD_STEPS):
            k1 = min(n, k0 + GUARD_STEPS)
            q0, q1 = foot_b + d * k0 * going, foot_b + d * k1 * going
            g1 = lo + k1 * rise + RAIL_H
            if s + 1 == storeys:
                g1 = min(g1, roof_y)
            for ge in guards:
                B("concrete_wall", ge[0], ge[1], lo + k0 * rise, g1, min(q0, q1), max(q0, q1), name=railing)

        # Each end of the flight says what it is, as data: the line the client speaks when you reach it
        # facing along the flight, turned to face the way you walk to take the flight from that end.
        # The client blips the stairs beacon from one of them a floor: the foot of each floor's flight
        # up, and on the roof the top of the flight down (StairCues.FloorBeacons).
        ca = (la[0] + la[1]) / 2
        dest = "the roof" if s + 1 == storeys else f"floor {s + 1}"
        fx, fz = P(ca, foot_b - d * MARKER_BACK)
        prop("stair_marker", fx, lo + 1.0, fz, name=f"Stairs up, {n} steps, to {dest}", facing=heading(d))
        tx, tz = P(ca, top_b + d * MARKER_BACK)
        prop("stair_marker", tx, hi + 1.0, tz, name=f"Stairs down, {n} steps, to floor {s}", facing=heading(-d))

        # The opening this flight comes up through, and the well beside it, are a drop on the floor
        # above. Everything round them is guarded except the end you step off the stairs at: across
        # the back of the opening, across the well at the top, along lane 1's far side (lane 0's is
        # the street wall), and along the well where the opening runs on past the end of the next
        # flight up (whose own guard covers the rest). Not on the roof, where the stair housing's own
        # walls close the opening and the well stops under the slab.
        if s + 1 < storeys:
            ha0, ha1, hb0, hb1 = hole(s)
            back = (hb0 - RAIL_T, hb0) if d > 0 else (hb1, hb1 + RAIL_T)
            B("concrete_wall", ha0, ha1, hi, hi + RAIL_H, back[0], back[1], name=railing)
            tb = sorted((top_b, top_b + d * RAIL_T))
            B("concrete_wall", well[0], well[1], hi, hi + RAIL_H, tb[0], tb[1], name=railing)
            if flight_lane(s) == 1:
                ra = span(la[1] if inward > 0 else la[0], RAIL_T)
                B("concrete_wall", ra[0], ra[1], hi, hi + RAIL_H, min(back[0], hb0), max(back[1], hb1), name=railing)
            _, _, _, _, _, _, nf, nt = flight(s + 1)
            ne = well_edge(flight_lane(s + 1))
            for q0, q1 in ((hb0, min(nf, nt)), (max(nf, nt), hb1)):
                if q1 - q0 > 1e-6:
                    B("concrete_wall", ne[0], ne[1], hi, hi + RAIL_H, q0, q1, name=railing)

        if s == 0:
            ex, _, ez, _ = place(sx1 - WALL_T / 2 if side > 0 else sx0 + WALL_T / 2, 0,
                                 (stair_b[0] + stair_b[1]) / 2, 0)
            # The same line as the flats' doors: the facade runs the same way as the corridor wall.
            # It was `facing_z=vertical`, which stood every tower's entrance at right angles to its
            # own facade — shut, the doorway was open round it; opened, the leaf swung across it.
            # city.json's doors were re-fitted to their openings on 2026-09-23 (turned and sized).
            # A glass front door with a push bar inside and a key outside (Cody, 2026-10-02: like his
            # own building's). Its key side is the leaf's +Z face, which door() turns to the street,
            # and it swings out over the pavement: unlocked and pulled from the street, pushed by its
            # bar from inside (Cody, 2026-10-05).
            # It is the building's front entrance and is called that: the thing a player walking
            # along the facade is looking for.
            door(ex, 0.02, ez, stair_id, -1, facing_z=not vertical, prefab="glass_front_door",
                 opening=entrance[1] - entrance[0], name=f"{label} front entrance")

        # ── The air conditioners ───────────────────────────────────────────────────────────────
        #
        # One in a window of every street-side flat, on the lower floors. A window unit sits in the
        # opening with its condenser hanging OUTSIDE, which is why you hear them from the street and
        # barely at all from the flat — so it goes on the outer face of the street wall, at sill
        # height, aimed out.
        #
        # There are dozens of these and only a handful can have a voice. That is the point of them:
        # a city's worth of small machines is the case aggregation exists for, and until the renderer
        # ranks and sums them what you should hear is the nearest few. Authoring five and calling it
        # a city would have hidden the problem rather than posed it.
        if s in ac_floors:
            face_a = sx1 + 0.18 if side > 0 else sx0 - 0.18
            for i in range(slots):
                if i == 0:
                    continue
                b0 = sz0 + WALL_T + i * slot_len
                ax_, _, az_, _ = place(face_a, 0, b0 + slot_len * 0.5, 0)
                prop("ac_window", ax_, floor_top + 1.35, az_,
                     name=f"{label} air conditioner {s}{i}",
                     facing=(0.0 if side > 0 else math.pi) if not vertical
                            else (math.pi / 2 if side > 0 else -math.pi / 2))

    B_holed("concrete_floor", sx0, sx1, storeys * STOREY, roof_y, sz0, sz1, hole(storeys - 1), name=f"{label} roof")

    # ── The way onto the roof ───────────────────────────────────────────────────────────────────
    #
    # The last flight comes up through the roof into a stair housing (a bulkhead): a small brick room
    # over the top of the flight with a landing and a steel door at the far end. Up the flight, keep
    # walking, and the door is in front of you. The housing's street side stands on the street wall,
    # its other side on the well's edge below.
    _, _, _, _, la, _, roof_foot, roof_top = flight(storeys - 1)
    ca = (la[0] + la[1]) / 2
    street_wall = (sx1 - WALL_T, sx1) if side > 0 else (sx0, sx0 + WALL_T)
    inner_wall = span(la[1] if inward > 0 else la[0], BULK_T)
    hb0, hb1 = roof_foot - STAIR_HEAD, roof_top + BULK_LANDING   # the housing's inside, along
    ha0, ha1 = min(street_wall[0], inner_wall[0]), max(street_wall[1], inner_wall[1])
    top = roof_y + BULK_H
    housing = f"{label} roof access wall"
    B("brick_wall", street_wall[0], street_wall[1], roof_y, top, hb0 - BULK_T, hb1 + BULK_T, name=housing)
    B("brick_wall", inner_wall[0], inner_wall[1], roof_y, top, hb0 - BULK_T, hb1 + BULK_T, name=housing)
    B("brick_wall", ha0, ha1, roof_y, top, hb0 - BULK_T, hb0, name=housing)
    B("brick_wall", ha0, la[0], roof_y, top, hb1, hb1 + BULK_T, name=housing)
    B("brick_wall", la[1], ha1, roof_y, top, hb1, hb1 + BULK_T, name=housing)
    B("brick_wall", la[0], la[1], roof_y + DOOR_H, top, hb1, hb1 + BULK_T, name=housing)
    B("concrete_floor", ha0, ha1, top, top + SLAB, hb0 - BULK_T, hb1 + BULK_T, name=f"{label} roof access roof")
    access_id = R(f"{label} roof access", la[0], la[1], storeys * STOREY, top, hb0, hb1)

    # A parapet round the edge, on the outer walls, so walking about up here cannot take you over
    # the side. Not where the housing's own wall already stands on the street wall.
    def parapet(axis, high):
        return f"{label} roof parapet, {compass(axis, high)} side"
    B_holed("brick_wall", street_wall[0], street_wall[1], roof_y, roof_y + PARAPET_H, sz0, sz1,
            (street_wall[0], street_wall[1], hb0 - BULK_T, hb1 + BULK_T), name=parapet("a", side > 0))
    far_wall = (sx0, sx0 + WALL_T) if side > 0 else (sx1 - WALL_T, sx1)
    B("brick_wall", far_wall[0], far_wall[1], roof_y, roof_y + PARAPET_H, sz0, sz1, name=parapet("a", side < 0))
    B("brick_wall", sx0 + WALL_T, sx1 - WALL_T, roof_y, roof_y + PARAPET_H, sz0, sz0 + WALL_T,
      name=parapet("b", False))
    B("brick_wall", sx0 + WALL_T, sx1 - WALL_T, roof_y, roof_y + PARAPET_H, sz1 - WALL_T, sz1,
      name=parapet("b", True))

    # The roof is a named place in the open air, so arriving on it is announced. Its boxes go round
    # the housing rather than over it: a place outdoors must not hold a room (CityZoneTests).
    #
    # Said to be outdoors rather than measured. The survey calls a place a room when four of its six
    # faces are walled, and a strip of roof between the parapet and the housing's wall, under open
    # sky, came out as one (2026-10-04). A roof has no ceiling whatever stands round its edge.
    def roof_piece(p0, p1, q0, q1):
        rid = R(f"{label} roof", p0, p1, roof_y, roof_y + STOREY, q0, q1)
        entities[-1]["IsIndoor"] = False
        return rid
    pieces = holed(roof_piece, sx0 + WALL_T, sx1 - WALL_T, sz0 + WALL_T, sz1 - WALL_T,
                   (ha0, ha1, hb0 - BULK_T, hb1 + BULK_T))
    outside = hb1 + BULK_T + 0.5
    roof_id = next(rid for p0, p1, q0, q1, rid in pieces if p0 <= ca <= p1 and q0 <= outside <= q1)
    dx, dz = P(ca, hb1 + BULK_T / 2)
    door(dx, roof_y, dz, access_id, roof_id, facing_z=vertical, prefab="steel_door", opening=DOOR_W,
         name=f"{label} roof access door")

    # A condenser on the roof — the big brother of the window units, and the one machine on this map
    # that is heard from above rather than across.
    prop("ac_condenser", (x0 + x1) / 2, storeys * STOREY + SLAB + 0.45, (z0 + z1) / 2,
         name=f"{label} roof plant")


# ── The five towers ───────────────────────────────────────────────────────────────────────────────
#
# Different heights on purpose. A street with one building height has one echo delay in it, and a
# city does not: the reflection off a ten-storey face and the one off a six-storey face arrive from
# different places and that is most of what tells you which street you are on.
BUILD_D = 2 * FLAT_DEPTH + CORRIDOR + 3 * WALL_T          # outer face to outer face, 21.25 m

# Which floors get air conditioners: the lower ones, where a listener in the street is looking up at
# them and where the sound has a short path to the pavement. Five storeys of them up a facade is what
# "in people's windows going up the building" is; three window units 22 m apart is a wall with three
# machines on it.
def lower(n, storeys):
    return tuple(range(min(n, storeys)))


tower("Marlow Tower",  -WALK - BUILD_D, -WALK,  -112.0, -22.0,  8, "+x", lower(5, 8))
tower("Kestrel House",  WALK, WALK + BUILD_D,   -112.0, -26.0,  6, "-x", lower(6, 6))
tower("Union Building", WALK, WALK + BUILD_D,     18.0, 116.0, 10, "-x", lower(5, 10))
tower("Brandt Court",  -WALK - BUILD_D, -WALK,  148.0, 236.0,   6, "+x", lower(6, 6))
tower("Selby House",    WALK, WALK + BUILD_D,   148.0, 240.0,   7, "-x", lower(5, 7))

# ══ The parking garage ════════════════════════════════════════════════════════════════════════════
#
# Three decks now rather than two. Low, hard and open down one side: the longest tail on the map and
# the place the reverb work was finally settled against.
GAR_X0, GAR_X1 = -122.0, -WALK - 2.0
GAR_Z0, GAR_Z1 = 20.0, 104.0
GAR_CLEAR = 2.5
GAR_LEVELS = 3
for lv in range(GAR_LEVELS):
    y0 = lv * (GAR_CLEAR + SLAB)
    # The ground deck's slab is laid ON the ground, not level with it: at y0-SLAB..y0 its top tied
    # with the map's dirt at exactly 0.0 and level 0 measured a DIRT floor while level 1 measured
    # concrete — two decks of the same car park, 615 ms and 4557 ms.
    if lv == 0:
        box("concrete_floor", GAR_X0, GAR_X1, 0.0, SLAB, GAR_Z0, GAR_Z1, name=f"Parking garage floor, level {lv}")
    else:
        box("concrete_floor", GAR_X0, GAR_X1, y0 - SLAB, y0, GAR_Z0, GAR_Z1, name=f"Parking garage floor, level {lv}")
    # ── The sides are SPANDRELS ON PIERS, not walls ──────────────────────────────────────────────
    #
    # An open-deck car park is open by law: it is ventilated by having no walls, and what stands
    # between the deck and the outside is a waist-high upstand to stop a car going over the edge,
    # with piers every few metres carrying the deck above. Building it as three solid walls and one
    # open side made a 110 by 84 metre concrete box with a lid on, and the survey said so — 2.4 %
    # absorption and a nine-second tail, where a real multi-storey measures two to four.
    #
    # This is not a tuning: the tail comes down because the openings are there, the same way a
    # courtyard is not a reverberation chamber because its ceiling is missing.
    SPANDREL = 1.1
    # Each side is named for the side it is and the deck it is on: "Parking garage west side, level 2".
    for x0, x1, z0, z1, where in ((GAR_X0, GAR_X0 + 0.3, GAR_Z0, GAR_Z1, "west"),
                                  (GAR_X0, GAR_X1, GAR_Z0, GAR_Z0 + 0.3, "south"),
                                  (GAR_X0, GAR_X1, GAR_Z1 - 0.3, GAR_Z1, "north")):
        side_name = f"Parking garage {where} side, level {lv}"
        box("concrete_wall", x0, x1, y0, y0 + SPANDREL, z0, z1, name=side_name)
        # Piers on a 7.5 m grid, up the rest of the way.
        along_z = (z1 - z0) > (x1 - x0)
        span = (z1 - z0) if along_z else (x1 - x0)
        for k in range(int(span // 7.5) + 1):
            at = (z0 if along_z else x0) + k * 7.5
            if along_z:
                box("concrete_wall", x0, x1, y0 + SPANDREL, y0 + GAR_CLEAR, at - 0.25, at + 0.25, name=side_name)
            else:
                box("concrete_wall", at - 0.25, at + 0.25, y0 + SPANDREL, y0 + GAR_CLEAR, z0, z1, name=side_name)
    for k in range(int((GAR_Z1 - GAR_Z0) // 6) + 1):
        cz = GAR_Z0 + k * 6.0
        box("concrete_wall", GAR_X1 - 0.4, GAR_X1, y0, y0 + GAR_CLEAR, cz - 0.2, cz + 0.2,
            name=f"Parking garage east side, level {lv}")
    region(f"Parking garage, level {lv}", GAR_X0, GAR_X1, y0, y0 + GAR_CLEAR, GAR_Z0, GAR_Z1)
box("concrete_floor", GAR_X0, GAR_X1, GAR_LEVELS * (GAR_CLEAR + SLAB) - SLAB,
    GAR_LEVELS * (GAR_CLEAR + SLAB), GAR_Z0, GAR_Z1, name="Parking garage roof")
box("metal_wall", GAR_X1 - 0.1, GAR_X1, 0.0, GAR_CLEAR, GAR_Z1 - 6.2, GAR_Z1 - 0.3, name="Parking garage shutter")

# ══ The tunnel ════════════════════════════════════════════════════════════════════════════════════
#
# A roofed box with two openings, and the best test the enclosure survey has: long, hard, closed on
# four faces and open on two. The rail crosses OVER it at z = -228, so a unit going round the loop
# passes above your head while you are in there.
carriageway(-KERB, KERB, TUNNEL_Z0, TUNNEL_Z1, name="Main Street carriageway")
box("concrete_wall", -KERB - 0.5, -KERB, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel west wall")
box("concrete_wall", KERB, KERB + 0.5, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel east wall")
box("concrete_wall", -WALK, -KERB - 0.5, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel west wall")
box("concrete_wall", KERB + 0.5, WALK, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel east wall")
box("concrete_floor", -WALK, WALK, TUNNEL_H, TUNNEL_H + 0.4, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel roof")
for i in range(4):
    zz0 = TUNNEL_Z0 + i * (TUNNEL_Z1 - TUNNEL_Z0) / 4
    zz1 = TUNNEL_Z0 + (i + 1) * (TUNNEL_Z1 - TUNNEL_Z0) / 4
    region(f"Tunnel, {['south portal', 'south half', 'north half', 'north portal'][i]}",
           -KERB, KERB, 0.0, TUNNEL_H, zz0, zz1)

# ══ The bus shelter ═══════════════════════════════════════════════════════════════════════════════
#
# NO REGION, still. A bus shelter is street furniture, not a room, and calling it one is what made it
# measure a two-and-a-half-second tail (a-small-room-inside-a-big-one). The listener stays in the
# STREET under it, which is what standing in one is. Two of them now, so the fault — when the survey
# can finally see an open front — has somewhere to be checked twice.
for shelter_z, shelter_x in ((-60.0, WALK), (86.0, -WALK)):
    inward = -1 if shelter_x > 0 else 1
    back = shelter_x
    front = shelter_x + inward * 3.2
    box("glass_wall", min(back, back - inward * 0.1), max(back, back - inward * 0.1),
        0.0, 2.4, shelter_z - 2.2, shelter_z + 2.2, name="Bus shelter")
    box("glass_wall", min(front, back), max(front, back), 0.0, 2.4, shelter_z - 2.2, shelter_z - 2.14,
        name="Bus shelter")
    box("glass_wall", min(front, back), max(front, back), 0.0, 2.4, shelter_z + 2.14, shelter_z + 2.2,
        name="Bus shelter")
    # The canopy is one 0.7 mm sheet of profiled steel, and the box is the sheet: as a 10 cm box of
    # Metal it was a steel slab to anything that asked what it was, and rain on a slab is silent.
    box("metal_roof", min(front, back), max(front, back), 2.5 - 0.0007, 2.5, shelter_z - 2.2, shelter_z + 2.2,
        name="Bus shelter roof")

# ══ The plaza ═════════════════════════════════════════════════════════════════════════════════════
#
# The one big hard open space downtown. Brick underfoot, a colonnade down one side and nothing over
# it: a place with strong early reflections and almost no reverberant field, which is the pair a
# street does not give you.
# It lies behind Brandt Court, which stands between it and Main Street: drawn to the building line it
# ran through the tower, put a pillar in its stairwell and named the tower's wall gaps "Market Square".
PLZ_X0, PLZ_X1 = -120.0, -WALK - BUILD_D - 2.0
PLZ_Z0, PLZ_Z1 = 148.0, 232.0
box("brick_floor", PLZ_X0, PLZ_X1, 0.0, 0.1, PLZ_Z0, PLZ_Z1, name="Market Square")
for k in range(9):
    px = PLZ_X0 + 4.0 + k * 12.0
    if px > PLZ_X1 - 4.0:
        break
    box("pillar_round", px - 0.35, px + 0.35, 0.0, 5.0, PLZ_Z0 + 3.0, PLZ_Z0 + 3.7, name="Market Square pillar")
region("Market Square", PLZ_X0, PLZ_X1, 0.0, 6.0, PLZ_Z0, PLZ_Z1)

# ══ The light rail ════════════════════════════════════════════════════════════════════════════════


def rail_loop_points():
    """The centreline, as a rounded rectangle. The corners are arcs and not right angles.

    A right angle in a centreline is a corner of zero radius, and the speed a vehicle may take a
    corner of radius R is sqrt(g * 9.81 * R) — which at R = 0 is zero. A track with square corners
    is a track nothing can drive.
    """
    r = RAIL_CORNER
    pts = []
    corners = [(RAIL_X_E - r, RAIL_Z_N - r, 0.0),          # NE
               (RAIL_X_W + r, RAIL_Z_N - r, math.pi / 2),   # NW
               (RAIL_X_W + r, RAIL_Z_S + r, math.pi),       # SW
               (RAIL_X_E - r, RAIL_Z_S + r, 3 * math.pi / 2)]  # SE
    for cx, cz, a0 in corners:
        for k in range(7):
            a = a0 + k * (math.pi / 2) / 6
            pts.append((cx + r * math.cos(a), 0.9, cz + r * math.sin(a)))
    # Straights: sampled every 30 m so the line has something to follow.
    out = []
    for i, p in enumerate(pts):
        out.append(p)
        q = pts[(i + 1) % len(pts)]
        d = math.dist((p[0], p[2]), (q[0], q[2]))
        if d > 40.0:
            n = int(d // 30.0)
            for k in range(1, n):
                t = k / n
                out.append((p[0] + (q[0] - p[0]) * t, 0.9, p[2] + (q[2] - p[2]) * t))
    return out


RAIL = rail_loop_points()

# The formation the track sits on: ballast, two rails, and a concrete deck where it crosses the
# tunnel. Laid as straight segments between the centreline points, which is what a railway is.
CROSSING_HALF = WALK + 2.0          # where the line crosses Main Street, on the flat


def on_the_crossing(x, z):
    """True where a road runs over the line rather than under it: Main Street, and Southgate's two.

    A level crossing is PLANKED: the road surface is carried across the rails, not laid beside them.
    So the formation and the rails stop short either side and the asphalt runs through — which is
    also the difference between walking over a crossing and tripping over a sixteen-centimetre lip.
    Southgate's crossings were added to the map by hand without their gap, so its traffic drove
    through the embankment (GhostsAndStuttersTests.EveryShippedTrackIsDriveable).
    """
    if abs(z - RAIL_Z_S) >= 12.0:
        return False
    return abs(x) < CROSSING_HALF or any(abs(x - cx) < KERB + 2.0 for cx in (SG_X0, SG_X1))


for i, p in enumerate(RAIL):
    q = RAIL[(i + 1) % len(RAIL)]
    mx, mz = (p[0] + q[0]) / 2, (p[2] + q[2]) / 2
    if on_the_crossing(mx, mz):
        continue
    dx, dz = q[0] - p[0], q[2] - p[2]
    seg = math.hypot(dx, dz)
    ang = math.atan2(dx, dz)
    extra = 0.6
    # A segment whose END reaches into a crossing is trimmed back to the crossing's edge: its middle
    # was clear, so it was laid whole, and 30 m of embankment ran straight across Southgate's road.
    # Trimmed rather than split, so the map keeps one box per segment and every id after it.
    steps = max(2, int(seg / 0.5))
    clear = [not on_the_crossing(p[0] + dx * k / steps, p[2] + dz * k / steps) for k in range(steps + 1)]
    if not all(clear):
        # The longest run of clear samples, which for a crossing at one end is the rest of the segment.
        best, run_start, t0, t1 = 0, None, 0, 0
        for k, c in enumerate(clear + [False]):
            if c and run_start is None:
                run_start = k
            elif not c and run_start is not None:
                if k - run_start > best:
                    best, t0, t1 = k - run_start, run_start / steps, (k - 1) / steps
                run_start = None
        mx, mz = p[0] + dx * (t0 + t1) / 2, p[2] + dz * (t0 + t1) / 2
        seg, extra = seg * (t1 - t0), 0.0
    # Written as an axis-aligned box and then turned, because a box IS axis-aligned until it is.
    bx, by, bz = BASE["dirt_floor"]
    entities.append({
        "EntityId": new_id(), "PrefabId": "dirt_floor",
        "Position": v3(mx, 0.35, mz), "Rotation": yaw(ang),
        "Scale": v3(RAIL_W / bx, 0.7 / by, (seg + extra) / bz),
        "Name": "Railway embankment",
    })
    for rail_off in (-0.72, 0.72):
        ox, oz = math.cos(ang) * rail_off, -math.sin(ang) * rail_off
        # A rail stops a foot, not a wave: typed as the palisade, which is solid to a body and
        # nearly transparent to sound. As sheet steel, 200 of them silenced every vehicle beyond
        # the embankment (a-fence-is-not-a-wall, 2026-09-22).
        mbx, mby, mbz = BASE["fence_palisade"]
        entities.append({
            "EntityId": new_id(), "PrefabId": "fence_palisade",
            "Position": v3(mx + ox, 0.78, mz + oz), "Rotation": yaw(ang),
            "Scale": v3(0.14 / mbx, 0.16 / mby, (seg + extra) / mbz),
            "Name": "Railway track",
        })


def rail_station(label, cx, cz, along_z=True):
    """A side platform with a canopy, beside the track.

    Tile under a steel canopy on columns and nothing else — the livest place on the map that is not
    the tunnel, and the one a unit will later stop at.
    """
    half_len, half_w = 34.0, 3.2
    off = 4.6                                   # platform edge, clear of the ballast
    if along_z:
        x0, x1 = cx - off - 2 * half_w, cx - off
        z0, z1 = cz - half_len, cz + half_len
    else:
        x0, x1 = cx - half_len, cx + half_len
        z0, z1 = cz - off - 2 * half_w, cz - off
    box("concrete_floor", x0, x1, 0.0, 0.95, z0, z1, name=f"{label} platform")
    box("tile_floor", x0, x1, 0.93, 0.99, z0, z1, name=f"{label} platform")
    box("metal_wall", x0 - 0.3, x1 + 1.6, 4.4, 4.55, z0, z1, name=f"{label} canopy")
    n = int((z1 - z0) // 9) if along_z else int((x1 - x0) // 9)
    for k in range(n + 1):
        if along_z:
            pz = z0 + k * 9.0
            box("concrete_wall", x1 - 0.5, x1 - 0.1, 0.95, 4.4, pz - 0.2, pz + 0.2, name=f"{label} canopy post")
        else:
            px = x0 + k * 9.0
            box("concrete_wall", px - 0.2, px + 0.2, 0.95, 4.4, z1 - 0.5, z1 - 0.1, name=f"{label} canopy post")
    for k in range(3):
        if along_z:
            a, b = z0 + k * (z1 - z0) / 3, z0 + (k + 1) * (z1 - z0) / 3
            region(f"{label}, {['south', 'middle', 'north'][k]} end", x0, x1, 0.95, 4.4, a, b)
        else:
            a, b = x0 + k * (x1 - x0) / 3, x0 + (k + 1) * (x1 - x0) / 3
            region(f"{label}, {['west', 'middle', 'east'][k]} end", a, b, 0.95, 4.4, z0, z1)


# NOTHING RUNS ON IT YET, and that is deliberate rather than unfinished.
#
# The rail model is built and was approved by ear in September (docs/TRAINS.md): a train is a LINE OF
# BOGIES, ten or eleven separate radiators spread over fifty metres of consist, each with its own
# place along the track. Every other model on this map is one pressure at one point and gets the
# voice in MachineProcessor.cs; a train is the one that is not, and giving it one means either the
# client knowing the track geometry — a fifty-metre consist on a twenty-six-metre corner is nowhere
# near a straight line behind the head — or the server placing each radiator itself. That is a real
# decision about an approved model and is not one to take while nobody is listening.
#
# So the railway is here, complete and measurable, and the unit that will run on it is next.
rail_station("Calder Road station", RAIL_X_E, 60.0, along_z=True)
rail_station("Airport station", RAIL_X_E, 300.0, along_z=True)
rail_station("Elm Park halt", RAIL_X_W + 9.4, 40.0, along_z=True)

# The crossing itself: asphalt carried over the line, and a named place — because "I am standing on a
# level crossing" is something a player should be told.
#
# IN TWO PIECES, AND AT THE HEIGHT OF WHAT IT CROSSES. The first draft was one box 62 cm tall,
# matching the ballast either side, which is not a crossing — it is a wall across Main Street, 22 cm
# over PhysicsConstants.StepHeight, and nothing on foot or on wheels gets past it. A crossing is the
# road surface carried through at ROAD LEVEL with the rails buried in it, so each piece stands four
# centimetres proud of the surface it is laid over and no more: over the carriageway (0.05) and over
# the footways (0.12). Four centimetres is a lip you feel; twelve is one that lands you.
box("asphalt_road", -KERB, KERB, 0.0, 0.09, RAIL_Z_S - 3.2, RAIL_Z_S + 3.2,
    name="Main Street level crossing")
for side_x0, side_x1 in ((-CROSSING_HALF - 2.0, -KERB), (KERB, CROSSING_HALF + 2.0)):
    box("asphalt_road", side_x0, side_x1, 0.0, 0.16, RAIL_Z_S - 3.2, RAIL_Z_S + 3.2, name="Main Street level crossing")
region("Level crossing", -CROSSING_HALF, CROSSING_HALF, 0.0, 4.0, RAIL_Z_S - 3.2, RAIL_Z_S + 3.2)

# ══ The airport ═══════════════════════════════════════════════════════════════════════════════════
#
# A runway is the largest flat hard surface a person is ever near, and it is the one place on this
# map with NOTHING to reflect off for hundreds of metres in every direction. That makes it the
# control: an engine heard on the apron and the same engine heard in a street should differ by the
# street, and here is the street taken away.
box("asphalt_road", RUNWAY_X - RUNWAY_W / 2, RUNWAY_X + RUNWAY_W / 2, 0.0, 0.06,
    RUNWAY_Z0, RUNWAY_Z1, name="Runway 18/36")
box("concrete_floor", RUNWAY_X - RUNWAY_W / 2 - 8.0, RUNWAY_X - RUNWAY_W / 2, 0.0, 0.04, RUNWAY_Z0, RUNWAY_Z1,
    name="Runway 18/36 shoulder")
box("concrete_floor", RUNWAY_X + RUNWAY_W / 2, RUNWAY_X + RUNWAY_W / 2 + 8.0, 0.0, 0.04, RUNWAY_Z0, RUNWAY_Z1,
    name="Runway 18/36 shoulder")
box("asphalt_road", TAXI_X - TAXI_W / 2, TAXI_X + TAXI_W / 2, 0.0, 0.06, RUNWAY_Z0 + 40.0, RUNWAY_Z1 - 40.0,
    name="Taxiway A")
box("concrete_floor", APRON_X0, APRON_X1, 0.0, 0.08, APRON_Z0, APRON_Z1, name="Apron")
# The link from the taxiway to the apron, and the one from the runway to the taxiway.
box("asphalt_road", APRON_X1, TAXI_X - TAXI_W / 2, 0.0, 0.06, 40.0, 63.0, name="Taxiway A link")
box("asphalt_road", TAXI_X + TAXI_W / 2, RUNWAY_X - RUNWAY_W / 2, 0.0, 0.06, 40.0, 63.0, name="Taxiway A link")
box("asphalt_road", TAXI_X + TAXI_W / 2, RUNWAY_X - RUNWAY_W / 2, 0.0, 0.06, -220.0, -197.0, name="Taxiway A link")
region("Runway", RUNWAY_X - RUNWAY_W / 2, RUNWAY_X + RUNWAY_W / 2, 0.0, 8.0, RUNWAY_Z0, RUNWAY_Z1)
region("Apron", APRON_X0, APRON_X1, 0.0, 8.0, APRON_Z0, APRON_Z1)

# ── The terminal: one tall glazed concourse ───────────────────────────────────────────────────────
box("concrete_floor", TERM_X0, TERM_X1, -0.15, 0.04, TERM_Z0, TERM_Z1, name="Terminal floor")
box("tile_floor", TERM_X0, TERM_X1, 0.02, 0.08, TERM_Z0, TERM_Z1, name="Terminal floor")
box("concrete_floor", TERM_X0, TERM_X1, TERM_H, TERM_H + 0.3, TERM_Z0, TERM_Z1, name="Terminal roof")
# A suspended acoustic ceiling, 0.6 m below the slab, as every real concourse has. Without it the
# terminal was concrete and tile on every side and rang for 7-10 s (2026-09-29, Cody: "give the
# terminal an acoustic ceiling"); a real one is 2-3.
box("acoustic_ceiling", TERM_X0 + 0.4, TERM_X1 - 0.06, TERM_H - 0.65, TERM_H - 0.6, TERM_Z0 + 0.4, TERM_Z1 - 0.4,
    name="Terminal ceiling")
# The road-side wall, with the steel door's doorway cut in it at z 62: a leaf in an uncut wall is a
# door into nothing (the opening graph's "a wall stands in it", 2026-10-02).
TERM_WEST_DOOR = 62.0
box("concrete_wall", TERM_X0, TERM_X0 + 0.4, 0.0, TERM_H, TERM_Z0, TERM_WEST_DOOR - 0.45, name="Terminal west wall")
box("concrete_wall", TERM_X0, TERM_X0 + 0.4, 0.0, TERM_H, TERM_WEST_DOOR + 0.45, TERM_Z1, name="Terminal west wall")
box("concrete_wall", TERM_X0, TERM_X0 + 0.4, 0.08 + 2.1 - DOOR_LAP, TERM_H, TERM_WEST_DOOR - 0.45, TERM_WEST_DOOR + 0.45,
    name="Terminal west wall")
box("concrete_wall", TERM_X0, TERM_X1, 0.0, TERM_H, TERM_Z0, TERM_Z0 + 0.4, name="Terminal south wall")
box("concrete_wall", TERM_X0, TERM_X1, 0.0, TERM_H, TERM_Z1 - 0.4, TERM_Z1, name="Terminal north wall")
# The apron face is glass, in bays, with two doorways cut out of it.
TERM_DOORS = (30.0, 92.0)
bay_at = TERM_Z0 + 0.4
for dz in TERM_DOORS:
    box("glass_wall", TERM_X1 - 0.06, TERM_X1, 0.0, TERM_H, bay_at, dz - 1.1, name="Terminal east wall")
    box("glass_wall", TERM_X1 - 0.06, TERM_X1, 2.2, TERM_H, dz - 1.1, dz + 1.1, name="Terminal east wall")
    bay_at = dz + 1.1
box("glass_wall", TERM_X1 - 0.06, TERM_X1, 0.0, TERM_H, bay_at, TERM_Z1 - 0.4, name="Terminal east wall")
# A row of seating down the middle, which is the only soft thing in the building.
for k in range(7):
    sz = TERM_Z0 + 14.0 + k * 17.0
    if sz > TERM_Z1 - 14.0:
        break
    box("furniture_soft", (TERM_X0 + TERM_X1) / 2 - 1.2, (TERM_X0 + TERM_X1) / 2 + 1.2,
        0.08, 0.95, sz - 2.4, sz + 2.4, name="Terminal seating")
term_ids = []
for k in range(3):
    a = TERM_Z0 + k * (TERM_Z1 - TERM_Z0) / 3
    b = TERM_Z0 + (k + 1) * (TERM_Z1 - TERM_Z0) / 3
    term_ids.append(region(f"Terminal concourse, {['south', 'middle', 'north'][k]} end",
                           TERM_X0 + 0.4, TERM_X1 - 0.06, 0.08, TERM_H, a, b))
# Each door joins the end it is in: z 30 is in the south end, z 92 in the north (it named the middle).
# The public entrances are automatic: two glass leaves that part for anyone who comes up to them and
# slide away into the glazing either side.
for dz, rid, end in zip(TERM_DOORS, (term_ids[0], term_ids[2]), ("south", "north")):
    bi_parting(TERM_X1 - 0.03, 0.08, dz, rid, -1, facing_z=False, opening=2.2,   # the wall runs along z
               name=f"Terminal apron entrance, {end}")
# ...and a door from the road side: steel, with a push bar and a closer. It is the one the Terminal
# approach comes to, so it is the terminal's front entrance.
door(TERM_X0 + 0.2, 0.08, TERM_WEST_DOOR, term_ids[1], -1, facing_z=False, prefab="steel_door",
     name="Terminal front entrance")

# ── The hangar: a steel box the size of a church ──────────────────────────────────────────────────
#
# Eleven metres to the underside of a steel roof, steel on every side, a concrete floor and one
# enormous opening. Metal absorbs five per cent of what hits it, so this is the most reverberant
# place on the map by a distance, and the one that most needs the survey to see that the door is a
# door — it is the small-enclosure fault the other way up.
box("concrete_floor", HANGAR_X0, HANGAR_X1, -0.2, 0.06, HANGAR_Z0, HANGAR_Z1, name="Hangar floor")
# The back wall, with the personnel door's doorway cut in it (the door is below).
HANGAR_BACK_DOOR = HANGAR_Z0 + 8.0
box("metal_wall", HANGAR_X0, HANGAR_X0 + 0.12, 0.0, HANGAR_H, HANGAR_Z0, HANGAR_BACK_DOOR - 0.45, name="Hangar west wall")
box("metal_wall", HANGAR_X0, HANGAR_X0 + 0.12, 0.0, HANGAR_H, HANGAR_BACK_DOOR + 0.45, HANGAR_Z1, name="Hangar west wall")
box("metal_wall", HANGAR_X0, HANGAR_X0 + 0.12, 0.06 + 2.1 - DOOR_LAP, HANGAR_H, HANGAR_BACK_DOOR - 0.45, HANGAR_BACK_DOOR + 0.45,
    name="Hangar west wall")
box("metal_wall", HANGAR_X0, HANGAR_X1, 0.0, HANGAR_H, HANGAR_Z1 - 0.12, HANGAR_Z1, name="Hangar north wall")
box("metal_wall", HANGAR_X0, HANGAR_X1, 0.0, HANGAR_H, HANGAR_Z0, HANGAR_Z0 + 0.12, name="Hangar south wall")
box("metal_wall", HANGAR_X0, HANGAR_X1, HANGAR_H, HANGAR_H + 0.15, HANGAR_Z0, HANGAR_Z1, name="Hangar roof")
# The door: the apron face, open across the middle 26 m and shut either side.
box("metal_wall", HANGAR_X1 - 0.12, HANGAR_X1, 0.0, HANGAR_H, HANGAR_Z0, HANGAR_Z0 + 15.0, name="Hangar east wall")
box("metal_wall", HANGAR_X1 - 0.12, HANGAR_X1, 0.0, HANGAR_H, HANGAR_Z1 - 15.0, HANGAR_Z1, name="Hangar east wall")
# The wall over the opening, which is the hangar's way in from the apron and is called that.
box("metal_wall", HANGAR_X1 - 0.12, HANGAR_X1, 8.4, HANGAR_H, HANGAR_Z0 + 15.0, HANGAR_Z1 - 15.0,
    name="Hangar front entrance")
hangar_id = region("Hangar", HANGAR_X0 + 0.12, HANGAR_X1 - 0.12, 0.06, HANGAR_H,
                   HANGAR_Z0 + 0.12, HANGAR_Z1 - 0.12)
portal(HANGAR_X1 - 0.06, 4.0, (HANGAR_Z0 + HANGAR_Z1) / 2, hangar_id, -1, 26.0)
# A steel personnel door in the back, which is the small opening the big one is measured against.
door(HANGAR_X0 + 0.06, 0.06, HANGAR_BACK_DOOR, hangar_id, -1, facing_z=False, prefab="steel_door",
     name="Hangar back door")
prop("ac_condenser", HANGAR_X0 + 2.4, HANGAR_H + 0.6, HANGAR_Z0 + 6.0, name="Hangar roof plant")

# The airport road: Foundry Street carries on east to the terminal.
carriageway(APRON_X0 - 46.0, TERM_X0 - 6.0, STREETS[2] - KERB, STREETS[2] + KERB, name="Airport Road")
footway(APRON_X0 - 46.0, TERM_X0 - 6.0, STREETS[2] - WALK, STREETS[2] - KERB, name="Airport Road sidewalk, south side")
record_road("Terminal Approach", "service", (TERM_X0 - 3.0, STREETS[2]), (TERM_X0 - 3.0, 40.0), 6.0)
box("asphalt_road", TERM_X0 - 6.0, TERM_X0, 0.0, 0.05, 40.0, STREETS[2] + KERB, name="Terminal approach")

# ══ The residential quarter ═══════════════════════════════════════════════════════════════════════


def house(label, cx, cz, facing, two_storey=False):
    """
    A house with a front garden, a back garden, a drive, a fence and two doors.

    `facing` is +1 if the street is at larger z, -1 if it is at smaller z. The plot runs from the
    pavement, over the front lawn, through the house and out to the back garden — which is where the
    mower is, and is why the front and the back of a house have to be different places.
    """
    d = facing
    front_z = cz + d * (HOUSE_D / 2)
    back_z = cz - d * (HOUSE_D / 2)
    x0, x1 = cx - HOUSE_W / 2, cx + HOUSE_W / 2
    zlo, zhi = min(front_z, back_z), max(front_z, back_z)
    h = HOUSE_H * 2 + 0.2 if two_storey else HOUSE_H

    # The back garden runs fourteen metres out of the back door — unless a street crosses the plot
    # first. Birch Street's north side backs onto Central Street, and its gardens ran straight over
    # the pavement and onto the road: a lawn, a hedge and a place called "back garden" in the middle
    # of a carriageway. Nothing noticed while the pavement's name happened to be found first; once the
    # smaller of two overlapping places won, 54 steps of pavement were "30 Birch Street back garden".
    # So a garden stops at the building line, and where there is no room for one there is none.
    out = -d                                  # the way the garden runs, in z
    garden_end = back_z + out * 14.0
    for sz, sx0, sx1 in CROSSING_STREETS:
        if cx + PLOT_W / 2 <= sx0 or cx - PLOT_W / 2 >= sx1:
            continue
        lo, hi = sz - WALK, sz + WALK         # the street between its two building lines
        if lo < back_z < hi:
            garden_end = back_z               # the back wall is already on the pavement
        elif out > 0 and back_z <= lo < garden_end:
            garden_end = lo
        elif out < 0 and back_z >= hi > garden_end:
            garden_end = hi
    has_garden = (garden_end - back_z) * out >= 2.0
    g0, g1 = min(back_z, garden_end), max(back_z, garden_end)

    # Lawns first, so the house sits on them and the ground probe still finds grass either side.
    box("grass_floor", cx - PLOT_W / 2 + 1.0, cx + PLOT_W / 2 - 1.0, 0.0, 0.09,
        min(front_z, front_z + d * 9.0), max(front_z, front_z + d * 9.0), name=f"{label} front lawn")
    if has_garden:
        box("grass_floor", cx - PLOT_W / 2 + 1.0, cx + PLOT_W / 2 - 1.0, 0.0, 0.09, g0, g1,
            name=f"{label} back garden")
    # The drive, up one side. Asphalt, so walking off the grass onto it is audible.
    dv0, dv1 = cx + PLOT_W / 2 - 4.6, cx + PLOT_W / 2 - 1.2
    box("asphalt_road", dv0, dv1, 0.0, 0.1, min(front_z, front_z + d * 9.0), max(front_z, front_z + d * 9.0),
        name=f"{label} drive")

    box("concrete_floor", x0, x1, -0.15, 0.04, zlo, zhi, name=f"{label} floor")
    box("wood_floor", x0 + 0.2, x1 - 0.2, 0.02, 0.08, zlo + 0.2, zhi - 0.2, name=f"{label} floor")
    # A house's walls are its front and back, toward the street and the garden, and its two sides.
    box("brick_wall", x0, x0 + 0.25, 0.0, h, zlo, zhi, name=f"{label} west wall")
    box("brick_wall", x1 - 0.25, x1, 0.0, h, zlo, zhi, name=f"{label} east wall")

    # THE FRONT WALL IS THE ONE FACING THE STREET, which depends on which side of it the house is.
    # It did not: both walls were built at zlo whichever way the plot was turned, so every house on
    # the north side of a street had its front door in the back garden and its back door on the
    # pavement. Nothing complains about that — a door is a door to the code — and it is the kind of
    # thing only walking up to one finds.
    #
    # And it was still backwards after that (found 2026-09-28 walking 64 Alder Street): `facing` +1
    # means the street is at LARGER z, so the front is zhi, and the doorway had gone into the garden
    # side of every house on both sides of every street — the front wall solid, with the back door
    # standing inside it. front_z (above) was always right; this now agrees with it.
    front_z0, front_z1 = (zhi - 0.25, zhi) if d > 0 else (zlo, zlo + 0.25)
    back_z0, back_z1 = (zlo, zlo + 0.25) if d > 0 else (zhi - 0.25, zhi)
    # The back wall has a doorway too. It was one solid box with the back door standing inside it,
    # so there was no way into the garden but the front, round the side.
    bdx = cx - 2.4
    box("brick_wall", x0, bdx - 0.45, 0.0, h, back_z0, back_z1, name=f"{label} back wall")
    box("brick_wall", bdx + 0.45, x1, 0.0, h, back_z0, back_z1, name=f"{label} back wall")
    box("brick_wall", bdx - 0.45, bdx + 0.45, 2.15, h, back_z0, back_z1, name=f"{label} back wall")
    box("brick_wall", x0, cx - 0.65, 0.0, h, front_z0, front_z1, name=f"{label} front wall")
    box("brick_wall", cx + 0.65, x1, 0.0, h, front_z0, front_z1, name=f"{label} front wall")
    box("brick_wall", cx - 0.65, cx + 0.65, 2.15, h, front_z0, front_z1, name=f"{label} front wall")
    box("concrete_floor", x0, x1, h, h + 0.2, zlo, zhi, name=f"{label} roof")

    # ── Furnished, or a bungalow is a brick box with a wooden floor and rings like one ───────────
    #
    # One sofa and a carpet was not enough: measured at <-325, 1.6, -106> it read a 827 ms middle
    # over a 2.1 s BOTTOM, which is carpet-is-deaf-to-bass exactly — a thin absorber does nothing at
    # a long wavelength, so a carpeted room with bare brick walls keeps its bass tail. What takes the
    # bottom out is upholstery with air behind it, which is a membrane absorber, and a house has more
    # than one piece of it: a sofa, a bed, a wardrobe against a wall and a curtain over the window.
    box("furniture_soft", x0 + 0.8, x0 + 2.6, 0.08, 0.95, zlo + 1.4, zhi - 1.4, name=f"{label} sofa")
    box("furniture_soft", x1 - 2.4, x1 - 0.4, 0.08, 0.75, zlo + 1.2, zlo + 3.4, name=f"{label} bed")
    box("furniture_soft", x0 + 0.4, x0 + 1.0, 0.08, 2.0, zhi - 2.6, zhi - 0.6, name=f"{label} wardrobe")
    # The curtain hangs over the front WINDOW, beside the door — not across the doorway, which is
    # where it hung until 2026-09-23: every front door on the estate opened onto a solid curtain.
    box("carpet_wall", cx + 1.1, cx + 3.3, 0.9, h - 0.15,
        front_z0 - 0.05 if d > 0 else front_z1, front_z0 if d > 0 else front_z1 + 0.05, name=f"{label} curtain")
    box("carpet_floor", cx - 1.0, x1 - 0.4, 0.08, 0.12, zlo + 0.4, zhi - 0.4, name=f"{label} floor")
    # PLASTER ON THE INSIDE OF THE BRICK, which is what a house is and is the missing membrane.
    #
    # Brick absorbs 3 % of the bottom and plaster absorbs 28 % — a skin on a wall is a membrane and
    # a solid wall is not, which is the same fact as carpet-is-deaf-to-bass seen from the other side.
    # Lined, the low band comes down from 2.1 s to where the middle is; unlined, a brick box with a
    # carpet in it keeps a bass tail no amount of soft furnishing touches.
    box("plaster_wall", x0 + 0.25, x0 + 0.31, 0.08, h, zlo + 0.25, zhi - 0.25, name=f"{label} west wall")
    box("plaster_wall", x1 - 0.31, x1 - 0.25, 0.08, h, zlo + 0.25, zhi - 0.25, name=f"{label} east wall")
    pz0 = min(back_z0, back_z1) + (0.25 if d > 0 else -0.06)
    pz1 = min(back_z0, back_z1) + (0.31 if d > 0 else 0.0)
    box("plaster_wall", x0 + 0.25, bdx - 0.45, 0.08, h, pz0, pz1, name=f"{label} back wall")
    box("plaster_wall", bdx + 0.45, x1 - 0.25, 0.08, h, pz0, pz1, name=f"{label} back wall")
    box("plaster_wall", x0 + 0.25, x1 - 0.25, h - 0.06, h, zlo + 0.25, zhi - 0.25, name=f"{label} ceiling")

    hid = region(label, x0 + 0.25, x1 - 0.25, 0.08, h, zlo + 0.25, zhi - 0.25)
    door(cx, 0.04, (front_z0 + front_z1) / 2, hid, -1, facing_z=True, prefab="door", opening=1.3,
         name=f"{label} front door")
    # The back door, which is how you get to the garden without going round: a patio door, slid along
    # its track into the wall toward the middle of the house.
    door(bdx, 0.04, (back_z0 + back_z1) / 2, hid, -1, facing_z=True, prefab="patio_door", opening=0.9,
         name=f"{label} back door")
    back_door = entities[-1]

    if has_garden:
        garden = region(f"{label} back garden", cx - PLOT_W / 2 + 1.0, cx + PLOT_W / 2 - 1.0, 0.0, 3.0, g0, g1)
        # The back door opens into the garden, not onto "outdoors": the garden is a place of its own, and
        # an opening joined to the wrong place is one the routes through doorways cannot use (the path
        # probe: "its sides are in the house and the garden, not the house and the outdoors"). Set here,
        # after the garden is made, so no entity's id moves.
        back_door["RegionBId"] = garden
        # The fence between this garden and the next. Not solid: you can hear a mower through a
        # fence, which is most of the point of putting one there.
        for fx in (cx - PLOT_W / 2, cx + PLOT_W / 2):
            box("foliage_hedge", fx - 0.4, fx + 0.4, 0.0, 1.8, g0, g1, name=f"{label} garden hedge")
    return hid


HOUSES = []
for si, sz in enumerate(RES_STREETS):
    # The street itself.
    record_road(f"{['Elm', 'Birch', 'Rowan', 'Alder'][si]} Street", "residential",
                (RES_X0, sz), (RES_X1, sz), RES_CARRIAGEWAY)
    carriageway(RES_X0, RES_X1, sz - RES_CARRIAGEWAY / 2, sz + RES_CARRIAGEWAY / 2,
                name=f"{['Elm', 'Birch', 'Rowan', 'Alder'][si]} Street carriageway")
    footway(RES_X0, RES_X1, sz - RES_WALK, sz - RES_CARRIAGEWAY / 2,
            label=f"{['Elm', 'Birch', 'Rowan', 'Alder'][si]} Street, south side")
    footway(RES_X0, RES_X1, sz + RES_CARRIAGEWAY / 2, sz + RES_WALK,
            label=f"{['Elm', 'Birch', 'Rowan', 'Alder'][si]} Street, north side")
    region(f"{['Elm', 'Birch', 'Rowan', 'Alder'][si]} Street",
           RES_X0, RES_X1, 0.0, 5.0, sz - RES_WALK, sz + RES_WALK)
    for k in range(int((RES_X1 - RES_X0) // PLOT_W)):
        cx = RES_X0 + PLOT_W / 2 + k * PLOT_W
        # A plot on top of a lane is a house in the middle of a road.
        if any(abs(cx - lx) < RES_LANE_CLEAR + PLOT_W / 2 for lx in RES_LANES):
            continue
        for d in (+1, -1):
            n = len(HOUSES) + 1
            HOUSES.append(house(f"{n} {['Elm', 'Birch', 'Rowan', 'Alder'][si]} Street",
                                cx, sz - d * (RES_WALK + 1.0 + HOUSE_D / 2), d,
                                two_storey=(k % 3 == 1)))

# The road that connects the houses to the city.
carriageway(RES_X1, -WALK, STREETS[1] - KERB, STREETS[1] + KERB, name="Central Street carriageway")
# ...and the two that run north-south through the estate, joining its streets into a grid.
for li, lx in enumerate(RES_LANES):
    record_road(f"{['Sycamore', 'Willow'][li]} Lane", "residential",
                (lx, RES_STREETS[0] - RES_WALK - 8.0), (lx, RES_STREETS[-1] + RES_WALK + 8.0), RES_CARRIAGEWAY)
    carriageway(lx - RES_CARRIAGEWAY / 2, lx + RES_CARRIAGEWAY / 2,
                RES_STREETS[0] - RES_WALK - 8.0, RES_STREETS[-1] + RES_WALK + 8.0,
                name=f"{['Sycamore', 'Willow'][li]} Lane")
    footway(lx - RES_WALK, lx - RES_CARRIAGEWAY / 2,
            RES_STREETS[0] - RES_WALK - 8.0, RES_STREETS[-1] + RES_WALK + 8.0,
            label=f"{['Sycamore', 'Willow'][li]} Lane, west side")
    footway(lx + RES_CARRIAGEWAY / 2, lx + RES_WALK,
            RES_STREETS[0] - RES_WALK - 8.0, RES_STREETS[-1] + RES_WALK + 8.0,
            label=f"{['Sycamore', 'Willow'][li]} Lane, east side")
    region(f"{['Sycamore', 'Willow'][li]} Lane", lx - RES_WALK, lx + RES_WALK, 0.0, 5.0,
           RES_STREETS[0] - RES_WALK, RES_STREETS[-1] + RES_WALK)

# ── The mowers ────────────────────────────────────────────────────────────────────────────────────
#
# Six of them, in back gardens, spread so that walking down a residential street takes you past one
# after another rather than through a field of them. A push mower is a metre-high machine at the far
# end of a garden behind a hedge, which is a very different sound from the same machine in front of
# you — and the difference is the hedge and the distance, not a setting on it.
# They MOVE. A push mower is pushed, at a walking pace, up one strip of the lawn and back down the
# next, and its sound goes with it: the engine bogs where the grass is thick and the whole machine
# comes and goes behind the hedge. So a mower is not a prop here but a shuttle — the same object
# the server drives a truck with — out and back across the garden at four kilometres an hour, with
# a pause at each end where the pusher turns it round. The VEHICLES list is built further down; the
# runs are collected here, where the gardens are.
MOWER_RUNS = []
for idx in (2, 9, 17, 26, 34, 41):
    if idx >= len(HOUSES):
        continue
    e = entities[[i for i, en in enumerate(entities)
                  if en.get("Name", "").endswith("back garden")][idx]]
    gx, gz = e["Position"]["X"], e["Position"]["Z"]
    MOWER_RUNS.append((idx, gx, gz))
# One riding mower, on the open grass beside the airport road, which is where a big one lives. It
# is a shuttle like the push mowers, not a prop: a mower standing still is not mowing, and a prop
# never moves. The run keeps between the avenue's hedges (x 138) and the rail fence (x 177).
box("grass_floor", 120.0, 190.0, 0.0, 0.09, 60.0, 122.0, name="Airport verge")
# The id the verge mower PROP had (6508) before it became a shuttle. The shipped map deleted it
# without renumbering, so it is spent here too and every id after it matches city.json.
new_id()

# ══ Street trees ══════════════════════════════════════════════════════════════════════════════════
#
# Down both pavements of every downtown avenue. Foliage is the extreme of the scattering pair —
# almost everything that goes in comes back out in every direction, and the higher the frequency the
# less of it comes back at all — so a treed street measurably loses the top of a pass-by.
for ax in AVENUES:
    z0, z1 = (MAIN_Z0 + 40.0, MAIN_Z1) if ax == 0.0 else (AVE_Z0, AVE_Z1)
    k = 0
    while z0 + k * 14.0 < z1:
        tz = z0 + k * 14.0
        box("foliage_hedge", ax - WALK + 0.5, ax - WALK + 2.1, 0.0, 5.0, tz - 0.8, tz + 0.8,
            name=f"{AVENUE_NAMES[ax]} tree")
        box("foliage_hedge", ax + WALK - 2.1, ax + WALK - 0.5, 0.0, 5.0, tz + 7.0 - 0.8, tz + 7.0 + 0.8,
            name=f"{AVENUE_NAMES[ax]} tree")
        k += 1

# ══ Zones over the open ground ════════════════════════════════════════════════════════════════════
#
# A street is a place and wants a name as much as a room does. None of these is indoors; the survey
# will find one or two covered faces and say so, which is the point — "Outside" is not a location.
# Both sides of a street are "<street> sidewalk", one name the whole length, so walking along it is
# silent and stepping off it onto the carriageway ("<street>, block N") is said.
for ax, aname in zip(AVENUES, ("Wharf Avenue", "Main Street", "Calder Avenue")):
    z0, z1 = (MAIN_Z0, MAIN_Z1) if ax == 0.0 else (AVE_Z0, AVE_Z1)
    n = max(1, int((z1 - z0) // 60))
    for k in range(n):
        a, b = z0 + k * (z1 - z0) / n, z0 + (k + 1) * (z1 - z0) / n
        if ax == 0.0 and b <= TUNNEL_Z1:
            continue                                  # that stretch is the tunnel, already named
        region(f"{aname}, block {k + 1}", ax - KERB, ax + KERB, 0.0, 6.0, a, b)
        region(f"{aname} sidewalk", ax - WALK, ax - KERB, 0.0, 4.0, a, b)
        region(f"{aname} sidewalk", ax + KERB, ax + WALK, 0.0, 4.0, a, b)

for sz, sname, sx0, sx1 in ((STREETS[0], "Dock Street", ST_X0, ST_X1),
                            (STREETS[1], "Central Street", RES_X0, TERM_X0 - 6.0),
                            (STREETS[2], "Foundry Street", ST_X0, APRON_X0),
                            (STREETS[3], "North Street", ST_X0, ST_X1)):
    n = max(1, int((sx1 - sx0) // 60))
    for k in range(n):
        a, b = sx0 + k * (sx1 - sx0) / n, sx0 + (k + 1) * (sx1 - sx0) / n
        region(f"{sname}, block {k + 1}", a, b, 0.0, 6.0, sz - KERB, sz + KERB)

# The cross streets' sidewalks, named as the avenues' are. Without them the footway fell into the
# map-wide outdoor region and was announced as "Outside". Emitted after the carriageways, in the same
# order, so the ids come out as they are in city.json.
for sz, sname, sx0, sx1 in ((STREETS[0], "Dock Street", ST_X0, ST_X1),
                            (STREETS[1], "Central Street", RES_X0, TERM_X0 - 6.0),
                            (STREETS[2], "Foundry Street", ST_X0, APRON_X0),
                            (STREETS[3], "North Street", ST_X0, ST_X1)):
    n = max(1, int((sx1 - sx0) // 60))
    for k in range(n):
        a, b = sx0 + k * (sx1 - sx0) / n, sx0 + (k + 1) * (sx1 - sx0) / n
        region(f"{sname} sidewalk", a, b, 0.0, 4.0, sz - WALK, sz - KERB)
        region(f"{sname} sidewalk", a, b, 0.0, 4.0, sz + KERB, sz + WALK)

# ══ Where the traffic runs ════════════════════════════════════════════════════════════════════════
#
# Roads are already data on this map, so a route round them is data too. Every loop below is a
# CENTRELINE with rounded corners, and what a car does on it is not scripted anywhere: its speed at
# each point comes from the curvature there (v = sqrt(g * 9.81 * R)) and a backward pass pulls each
# limit down to what the brakes can shed before the next slower point. Give two cars different grip
# and they corner at different speeds, catch each other and queue — with no notion of traffic in the
# code at all.
#
# So a city corner is slow because it is a corner. At 0.45 g on a 12 m radius that is 28 km/h, which
# is what turning into a side street is.


def loop(points, corner=12.0, spacing=12.0):
    """A closed route through a list of (x, z) turning points, with a proper ARC at each corner.

    THE CORNER HAS TO BE A REAL RADIUS, and the first version of this was not. It backed off along
    each leg and then interpolated between two hand-waved offsets, which produced a kink rather than
    a fillet: measured on the generated map, the tightest radius on every road loop came out at
    **1.4 metres**. A 1.4 m corner is a hairpin nothing can drive — sqrt(g * 9.81 * R) at half a g is
    seven km/h — so a car spent the loop at top speed, braked to walking pace for something that was
    supposed to be a street corner, and did it over and over. That is heard as traffic that never
    changes note and then screams.

    So it is the fillet a road is actually built with. For a corner of interior angle t between two
    legs, an arc of radius R is tangent to both at a distance R / tan(t/2) back from the corner, with
    its centre R / sin(t/2) along the bisector. Twelve metres is a city street corner; a bus needs
    more than that and gets it by being slow, not by the geometry bending.
    """
    n = len(points)
    out = []
    for i in range(n):
        px, pz = points[i - 1]
        cx, cz = points[i]
        nx, nz = points[(i + 1) % n]

        v1 = (px - cx, pz - cz)
        v2 = (nx - cx, nz - cz)
        l1 = math.hypot(*v1) or 1.0
        l2 = math.hypot(*v2) or 1.0
        u1 = (v1[0] / l1, v1[1] / l1)
        u2 = (v2[0] / l2, v2[1] / l2)

        cos_t = max(-0.9999, min(0.9999, u1[0] * u2[0] + u1[1] * u2[1]))
        half = math.acos(cos_t) / 2.0                      # half the interior angle
        if half < 1e-3 or abs(math.pi / 2 - half) < 1e-3:
            out.append((cx, 0.15, cz))                     # straight through: no corner here
            continue

        # The radius must fit: the tangent cannot reach past the middle of either leg.
        r = min(corner, 0.45 * l1 * math.tan(half), 0.45 * l2 * math.tan(half))
        t = r / math.tan(half)
        t1 = (cx + u1[0] * t, cz + u1[1] * t)
        t2 = (cx + u2[0] * t, cz + u2[1] * t)

        bis = (u1[0] + u2[0], u1[1] + u2[1])
        bl = math.hypot(*bis) or 1.0
        bis = (bis[0] / bl, bis[1] / bl)
        centre = (cx + bis[0] * (r / math.sin(half)), cz + bis[1] * (r / math.sin(half)))

        a1 = math.atan2(t1[1] - centre[1], t1[0] - centre[0])
        a2 = math.atan2(t2[1] - centre[1], t2[0] - centre[0])
        sweep = (a2 - a1 + math.pi) % (2 * math.pi) - math.pi   # the short way round
        steps = max(3, int(abs(sweep) * r / 3.0))               # a point every ~3 m of arc
        for k in range(steps + 1):
            a = a1 + sweep * k / steps
            out.append((centre[0] + math.cos(a) * r, 0.15, centre[1] + math.sin(a) * r))

    # Fill in the straights so the line has points to follow.
    dense = []
    for i, p in enumerate(out):
        dense.append(p)
        q = out[(i + 1) % len(out)]
        d = math.dist((p[0], p[2]), (q[0], q[2]))
        if d > spacing * 1.6:
            n_seg = max(1, int(d // spacing))
            for k in range(1, n_seg):
                t = k / n_seg
                dense.append((p[0] + (q[0] - p[0]) * t, 0.15, p[2] + (q[2] - p[2]) * t))
    return dense


# Lanes: a car keeps to the right, so an anticlockwise loop takes the OUTER lane and a clockwise one
# the inner. The loops below are laid out so that two of them run in opposite directions round the
# same blocks, which is what makes the street have two directions of traffic in it.
TRACKS = [
    # The rail loop, as a route rather than as ballast. Nothing runs on it yet — see the note where
    # the formation is laid — but the line a unit would take is a property of the railway and belongs
    # with it, not with whatever eventually drives it. It is the same centreline the ballast and the
    # rails were laid along, so the two cannot drift apart.
    {"Id": "rail_loop", "WidthMetres": RAIL_W, "BankingDegrees": 0.0,
     "Waypoints": [v3(p[0], 0.95, p[2]) for p in RAIL]},
]


def lap_metres(track):
    w = [(p["X"], p["Z"]) for p in track["Waypoints"]]
    return sum(math.dist(w[i], w[(i + 1) % len(w)]) for i in range(len(w)))


def metres_to(track, x, z):
    """How far round a track, from its first waypoint, the point nearest (x, z) is."""
    w = [(p["X"], p["Z"]) for p in track["Waypoints"]]
    best, along, at = None, 0.0, 0.0
    for i in range(len(w)):
        a, b = w[i], w[(i + 1) % len(w)]
        seg = math.dist(a, b)
        t = max(0.0, min(1.0, ((x - a[0]) * (b[0] - a[0]) + (z - a[1]) * (b[1] - a[1])) / (seg * seg)))
        d = math.dist((x, z), (a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])))
        if best is None or d < best:
            best, at = d, along + t * seg
        along += seg
    return at


# ── Where things stop ─────────────────────────────────────────────────────────────────────────────
#
# Read by VehicleSystem (MapRepository's TrackStop). A bus stops at bus stops (ForPreset: a vehicle
# whose preset contains "bus"); everything stops at a give-way; a train at a platform; a car at a
# level crossing only while the crossing is closed. Bus stops sit a third of the way along each side
# of a loop, the give-ways at two of its corners, as fractions of the lap.
BUS_STOP_DWELL = 16.0
for t in TRACKS:
    if t["Id"] == "rail_loop":
        t["Stops"] = [{"AtMetres": 400.0, "DwellSeconds": 26.0, "Kind": "platform"},
                      {"AtMetres": 1200.0, "DwellSeconds": 26.0, "Kind": "platform"}]

# The road traffic's stops are places on the roads (RoadStops): the bus stops, in the kerb lane
# beside each shelter. Give-way lines come from the junctions, and a level crossing stops whatever
# drives over it, both worked out by the server from the roads.
ROAD_STOPS = [
    {"Name": "Main Street northbound, by the shelter", "Position": v3(KERB - LANE / 2, 0.05, -60.0),
     "Kind": "bus_stop", "DwellSeconds": BUS_STOP_DWELL, "ForPreset": "bus"},
    {"Name": "Main Street southbound, by the shelter", "Position": v3(-(KERB - LANE / 2), 0.05, 86.0),
     "Kind": "bus_stop", "DwellSeconds": BUS_STOP_DWELL, "ForPreset": "bus"},
]

# The two level crossings, where Southgate's streets cross the railway's south side.
CROSSINGS = [
    {"Name": f"Southgate {side} crossing", "Position": v3(cx, 0.95, RAIL_Z_S),
     "WarningSeconds": 20.0, "ClearMetres": 35.0, "Bell": "crossing_gong"}
    for side, cx in (("west", SG_X0), ("east", SG_X1))
]

# Mean seconds between events, map-wide; 0 is off. See MapData.StreetLife.
# GunfireEverySeconds (Cody, 2026-09-25): "every now and then have an npc fire a couple round so I
# can hear what they sound like periodically as I move around the city." One of the walkers fires two
# to four rounds; every two minutes was "too few and far apart", 35 s still too few ("a few more
# people shooting along the streets"), so every 20 s on average.
# Parking every 45 s, so "people parking, getting out, going inside, coming out, leaving" is heard, and
# a car alarm every couple of minutes from one of the cars standing empty.
STREET_LIFE = {"HornEverySeconds": 15, "HardBrakeEverySeconds": 60, "ParkEverySeconds": 45,
               "GunfireEverySeconds": 20, "AlarmEverySeconds": 150}

# ── The field ─────────────────────────────────────────────────────────────────────────────────────
#
# Ordinary cars, mostly, because that is what a city is: four-cylinder hatchbacks and saloons, two
# delivery diesels, a pair of buses, one police car and one motorcycle. The presets are the same ones
# the speedway uses; what differs is the top speed and the grip, and those are what make this traffic
# and that a race.
#
# TopSpeedKmh is a CITY speed. 50 is the limit on an avenue and nobody does it in the wet.
# BRAKING AND CORNERING COME OUT OF THE SAME FRICTION CIRCLE, and the map has to respect that.
#
# VehicleSystem measures what the tyres are being asked for as
#     sqrt(lateral^2 + longitudinal^2),  longitudinal = |dv/dt| / (CorneringG * 9.81)
# so a vehicle whose BrakingMps2 exceeds CorneringG * 9.81 is over its own limit EVERY TIME IT SLOWS,
# and the client renders that as tyre squeal. Authoring a bus with 0.28 g of grip and 3.2 m/s^2 of
# brake asks for 1.16 of the grip it has, on every corner, for ever — which is what "the buses seemed
# to skid right in front of me" was.
#
# Measured across the first draft: every heavy vehicle on the map was over — bus 1.16, box truck
# 1.36, delivery diesel 1.20, coach 1.09. The cars were all under and none of them squealed.
#
# So the brake is DERIVED from the grip rather than typed in beside it. Sixty per cent of the circle
# leaves room for the corner itself, which is the other axis of the same sum.
# A THIRD OF THE CIRCLE, not two thirds. These are fractions of the GRIP now, not of the gentle
# cornering number, so the same share buys a much harder stop than it used to: 0.60 of a car's
# 0.85 g is 5.0 m/s^2, which is an emergency stop, and combined with the corner it put the police
# car back at 0.93 of its tyres. Service braking in traffic is about 0.3 g. Nobody in a city drives
# like this on purpose, which is the point — the squeal is supposed to mean something.
BRAKE_SHARE = 0.35
ACCEL_SHARE = 0.30


def brake_for(g):
    """The hardest this vehicle may brake without asking more of its tyres than it has. Takes the
    GRIP, not the cornering number."""
    return round(g * 9.81 * BRAKE_SHARE, 2)


def accel_for(g, cap):
    """...and how hard it may pull away, which for anything heavy is engine-limited long before
    it is grip-limited — so the cap wins for a bus and the circle wins for a sports car."""
    return round(min(cap, g * 9.81 * ACCEL_SHARE), 2)


# HOW HARD IT CORNERS IS NOT HOW MUCH GRIP IT HAS.
#
# The racing line takes a corner at sqrt(CorneringG * 9.81 * R), so a vehicle tracking its own line
# is at exactly 1.0 of CorneringG the whole way round — and the tyres used to be measured against
# that same number, so the demand came out at 1.0 in every corner and the client rendered 1.0 as a
# tyre at its limit. Every vehicle in the city screeched through every junction.
#
# On the speedway that is correct: a racing line IS at the limit. Ordinary traffic is not. A dry
# road gives a car about 0.85 g and a bus about 0.70, and neither uses a third of it turning into a
# side street. So the map declares both, and GripG is what the friction circle is made of — the
# brake comes out of it too.
GRIP = {"car": 0.85, "van": 0.75, "truck": 0.70, "bus": 0.70, "bike": 0.90}


def car(name, preset, track, top, g, lane, start, accel=2.2, brake=None, grip="car"):
    """A vehicle on a track, or, with `track` a dict, on the roads by that route (see via, wander)."""
    grip_g = GRIP[grip]
    v = {"Name": name, "Preset": preset,
         "TopSpeedKmh": top, "CorneringG": g, "GripG": grip_g,
         "AccelerationMps2": accel_for(grip_g, accel),
         "BrakingMps2": brake if brake is not None else brake_for(grip_g),
         "StartOffsetMetres": start}
    if isinstance(track, dict):
        v["Route"] = track
    else:
        v["Track"] = track
        v["LaneOffsetMetres"] = lane
    return v


def jid(x, z):
    """A junction's id, as find_junctions names it."""
    return f"j_{int(x)}_{int(z)}"


def via(*points):
    """A fixed route through junctions, in order, and back to the first."""
    return {"Via": [jid(x, z) for x, z in points]}


def wander(road, direction, seed, metres=900.0):
    """A seeded random tour from a road, turning at random, and back to where it began."""
    return {"StartRoad": road, "Direction": direction, "Seed": seed, "WanderMetres": metres}


# THE FIELD IS CHOSEN ON MEASURED LEVELS, and the first draft was not.
#
# `--engine-levels` renders every preset at full load and prints what it measures at a metre. The
# presets were tuned for the SPEEDWAY, where a race-spec straight six really is 117 dB, and picking
# from them by name gave this street a 47 dB spread — a school bus at 85 dB sharing a road with a
# police V8 at 132, which is a factor of two hundred in pressure. Reported, correctly, as "some of
# the vehicles are quiet" and "some with loud exhaust".
#
#   the road-car band          85-107 dB   diesel_i4 88, diesel_truck 93, i4_economy 94,
#                                          v6 98, school_bus_na 98, i4_turbo 100, diesel_cummins 107
#   deliberately loud          121-132 dB  vtwin 121, v8_muscle 122, police_v8 132
#
# Ordinary traffic comes out of the road band, which is a 19 dB spread and is what a street is. The
# loud three are kept as exactly that: ONE motorcycle, ONE muscle car, ONE police car — the vehicles
# you are supposed to hear from two blocks away, and which mean nothing if everything else does too.
#
# school_bus (85 dB) is the silenced one and is wrong for a city bus, which is among the LOUDER
# things on a street; school_bus_na, the naturally aspirated one, measures 98.
VEHICLES = []
# The field, rebuilt 2026-09-26 (Cody): "Remove the muscle cars, just have regular type vehicles
# now, a city bus, a couple twin turbo pickups with some nice aggressive exhaust, some economy cars,
# hondas toyotas that type of sounding stuff. a few cars with more aggressive sounding exhaust but
# nothing too crazy." The street cars are the Vehicles.cs street presets: the same engines as the
# speedway's with road exhausts, 98-104 dB at a metre flat out; the twin-turbo pickups 106-112.
# And, 2026-09-28, four of the ordinary cars swapped for loud ones — "loud cars in traffic": the
# economy cars keep their stock silencers and their tyres as loud as their pipes, which is what real
# ones do at thirty miles an hour, and now and then something with a real exhaust goes by. The
# stock cars cruise at 87-89 dB at a metre; these at 94-104, and 101-109 pulling away. They drive
# exactly as the cars they replaced did (speed, cornering, grip): only the sound is new.
CITY_CW = [
    ("Hatchback",    "i4_economy",           52.0, 0.48, 1.8, "car"),
    ("Compact",      "i4_compact",           52.0, 0.48, 1.8, "car"),
    ("Sedan",        "v6",                   50.0, 0.46, 1.8, "car"),
    ("Sport compact", "i4_sport_street",     56.0, 0.52, 1.8, "car"),
    ("Police car",   "police_interceptor",   62.0, 0.60, 1.8, "car"),
    ("Twin-turbo Cummins", "cummins_compound", 52.0, 0.42, 1.8, "van"),
    ("V8 pickup",    "pickup_v8_flowmaster", 50.0, 0.46, 1.8, "car"),
    ("Flat-four",    "boxer4_street",        54.0, 0.50, 1.8, "car"),
    ("Sport compact", "i4_sport_street",     50.0, 0.48, 1.8, "car"),
    ("Motorcycle",   "vtwin_slipon",         56.0, 0.60, 3.4, "bike"),
]
CITY_CCW = [
    ("Compact",      "i4_compact",           52.0, 0.48, 1.8, "car"),
    ("Turbo hatch",  "i4_turbo",             56.0, 0.52, 1.8, "car"),
    ("Sedan",        "v6",                   50.0, 0.47, 1.8, "car"),
    ("Sport saloon", "i6_street",            54.0, 0.50, 1.8, "car"),
    ("Turbo hatch",  "i4_turbo",             50.0, 0.48, 1.8, "car"),
    ("Mid-size",     "i4_midsize",           50.0, 0.46, 1.8, "car"),
    ("Twin-turbo Duramax", "duramax_compound", 52.0, 0.42, 1.8, "van"),
    ("Muscle car",   "v8_mild",              50.0, 0.48, 1.8, "car"),
    ("Motorcycle",   "vtwin_stock",          56.0, 0.60, 3.4, "bike"),
]
# Since 2026-09-27 the traffic drives the roads (Roads, Junctions): each car a seeded wander through
# the lanes from its own starting road, turning at random at the junctions, so no two follow the same
# way. Starting roads and directions go round the downtown streets in turn.
DOWNTOWN_STARTS = [("main_street", 1), ("dock_street", 1), ("calder_avenue", 1), ("foundry_street", -1),
                   ("wharf_avenue", -1), ("north_street", -1), ("central_street", 1), ("main_street", -1)]
for i, (nm, preset, top, g, lane, kind) in enumerate(CITY_CW + CITY_CCW):
    road, direction = DOWNTOWN_STARTS[i % len(DOWNTOWN_STARTS)]
    VEHICLES.append(car(f"{nm} {i + 1}", preset,
                        wander(road, direction, 1000 + i), top, g, lane, (i // len(DOWNTOWN_STARTS)) * 60.0, grip=kind))
# ONE bus (Cody, 2026-09-25: "we need only 1 city bus on the map, not a bunch"). It goes round the
# north block and serves its bus stops, which is what makes it a bus you can get on.
# A transit bus: the engine in the back (Vehicles.cs TransitBus).
# Its route passes both shelters on Main Street the right way: up Main past the one at z -60 on the
# east side, round the north block, down Main past the one at z 86 on the west side, and home by
# Central Street and Wharf Avenue.
BUS_ROUTE = via((-130.0, -130.0), (0.0, -130.0), (0.0, 130.0), (130.0, 130.0), (130.0, 260.0),
                (0.0, 260.0), (0.0, 0.0), (-130.0, 0.0))
VEHICLES.append(car("City bus 1", "transit_bus", BUS_ROUTE, 40.0, 0.28, 2.0, 0.0, accel=1.4, grip="bus"))
VEHICLES.append(car("Parcel van 1", "step_van", wander("foundry_street", 1, 2001), 44.0, 0.32, 2.0, 0.0, grip="truck"))
VEHICLES.append(car("Mail truck 1", "mail_truck", wander("north_street", 1, 2002), 40.0, 0.36, 1.8, 0.0, grip="van"))
VEHICLES.append(car("Sedan, north block", "i4_midsize", wander("calder_avenue", -1, 2003), 50.0, 0.46, 1.8, 0.0, grip="car"))
# Two more motorcycles, of other kinds than the cruisers (Cody, 2026-09-26: "add a couple motorcycle
# back on the map"): a litre sports bike here, and a 450 single on the downtown loop, in the gap
# after its last car (ten cars at 96 m on a 1,040 m loop).
# No sports bike: the 600 was taken off the city by ear on 2026-09-28 ("sounds like a car stuck in
# first gear"), and the preset with it.
VEHICLES.append(car("Dirt bike", "single", wander("dock_street", -1, 2005), 50.0, 0.60, 3.4, 0.0, grip="bike"))
# The estate: slow, quiet, and the thing you hear over the mowers.
for i, (nm, preset) in enumerate((("Hatchback", "i4_economy"), ("Sedan", "v6"), ("Compact", "i4_compact"),
                                  ("Mail truck", "mail_truck"), ("Parcel van", "step_van"))):
    VEHICLES.append(car(f"{nm}, Elm Street", preset,
                        wander(["elm_street", "birch_street", "rowan_street", "alder_street", "sycamore_lane"][i], 1, 3000 + i, 600.0),
                        30.0, 0.35, 1.2, 0.0, accel=1.6, grip="car"))

# ── ...and the ones that shuttle rather than lap ──────────────────────────────────────────────────
#
# A shuttle is the demonstration case: out and back along a straight line, at each speed in a list.
# Main Street's is the one that goes through the tunnel, which is the whole reason for having it —
# a diesel entering a hundred metres of hard concrete box and coming out the far end.
for idx, gx, gz in MOWER_RUNS:
    VEHICLES.append({
        "Name": f"Mower, garden {idx}", "Preset": "mower_push",
        "RoadStart": v3(gx - 8.0, 0.42, gz), "RoadEnd": v3(gx + 8.0, 0.42, gz),
        "SpeedsKmh": [4.0, 3.6], "AccelerationMps2": 0.5, "BrakingMps2": 0.8,
        "WaitSeconds": 1.5, "StartDelaySeconds": (idx % 7) * 2.0,
    })

VERGE_MOWER = {
    "Name": "Verge mower", "Preset": "mower_riding",
    "RoadStart": v3(156.0, 0.62, 66.0), "RoadEnd": v3(156.0, 0.62, 116.0),
    "SpeedsKmh": [7.0, 6.5], "AccelerationMps2": 0.8, "BrakingMps2": 1.2,
    "WaitSeconds": 2.0, "StartDelaySeconds": 3.0,
}

# ── Parked cars you can get into ──────────────────────────────────────────────────────────────────
#
# On the ground deck of the garage, nose out between the street-side piers, so pulling away is
# straight onto the pavement and Main Street. Each is "vehicle:<profile>": the server builds the shell
# from the profile (VehicleShell) rather than from a file, so a car here cannot drift from the car.
PARKED = []
for preset, z in (("i4_economy", 29.0), ("v6", 35.0), ("i4_compact", 41.0), ("i4_turbo", 47.0)):
    PARKED.append({"TemplateId": "vehicle:" + preset, "Position": v3(GAR_X1 - 4.5, SLAB, z),
                   "Rotation": {"X": 0, "Y": 0.707107, "Z": 0, "W": 0.707107}, "Owner": ""})

# ── People ────────────────────────────────────────────────────────────────────────────────────
#
# Somebody walking is heard by their footsteps and their voice, which the client makes from the body's
# own movement and the server's speech events, so a walker is a body on a line at a walking pace.
#
# Every pavement on the map gets people (Cody, 2026-09-27: "add 200 people or so on the map on all
# streets"), found from the footways themselves rather than listed: each pavement's length is walked,
# split wherever something solid stands on it (a bus shelter, a tunnel wall, a tree), and every clear
# stretch of 25 m or more gets walkers in proportion to its length, half each way, keeping to their
# own side of the pavement, spread along it by their start times.
WALKER_SPACING = 30.0                    # metres of pavement per person
WALK_MIN = 25.0                          # a stretch shorter than this is not a walk


def _solid_boxes():
    # The server's rule (PrefabRepository, mapgen.SOLID): a prefab with a collider is solid unless it
    # says it is not. Reading a missing IsSolid as false put the Main Street walks through the tunnel's
    # concrete sides, where a player outside heard them walking inside the wall.
    solid = SOLID
    out = []
    for e in entities:
        pid = e.get("PrefabId")
        if not solid.get(pid) or pid not in BASE:
            continue
        bx, by, bz = BASE[pid]
        sc = e.get("Scale") or {"X": 1, "Y": 1, "Z": 1}
        hx, hy, hz = bx * sc["X"] / 2, by * sc["Y"] / 2, bz * sc["Z"] / 2
        if e.get("Rotation") and abs(e["Rotation"].get("Y", 0)) > 0.3:
            hx = hz = max(hx, hz)            # turned: take the larger footprint both ways
        q = e["Position"]
        out.append((q["X"] - hx, q["X"] + hx, q["Y"] - hy, q["Y"] + hy, q["Z"] - hz, q["Z"] + hz))
    return out


def _clear_stretches(line_at, length, boxes, step=0.5, body=0.3):
    """The parts of a line a person can walk along without passing through anything solid."""
    n = int(length / step)
    ok = []
    for i in range(n + 1):
        x, z = line_at(i * step)
        hit = any(b[0] - body < x < b[1] + body and b[4] - body < z < b[5] + body
                  and b[2] < 1.85 and b[3] > 0.45 for b in boxes
                  if b[0] - 2 < x < b[1] + 2 and b[4] - 2 < z < b[5] + 2)
        ok.append(not hit)
    runs, start = [], None
    for i, clear in enumerate(ok + [False]):
        if clear and start is None:
            start = i
        elif not clear and start is not None:
            runs.append((start * step + 0.5, (i - 1) * step - 0.5))
            start = None
    return [(a, b) for a, b in runs if b - a >= WALK_MIN]


_rng = __import__("random").Random(2026_09_27)
_boxes = _solid_boxes()
WALKERS = []
PAIR_COUNT = [0]
for x0, x1, z0, z1, label in FOOTWAYS:
    along_z = (z1 - z0) >= (x1 - x0)
    width = (x1 - x0) if along_z else (z1 - z0)
    length = (z1 - z0) if along_z else (x1 - x0)
    # Two tracks on a wide pavement, one either side of the middle; one down the middle of a narrow one.
    offsets = (-0.7, 0.7) if width >= 3.0 else (0.0, 0.0)
    mid = ((x0 + x1) / 2) if along_z else ((z0 + z1) / 2)
    for side, off in enumerate(offsets):
        def at(d, off=off):
            return (mid + off, z0 + d) if along_z else (x0 + d, mid + off)
        # Two people walking together keep 0.6 m apart, the companion on the side nearer the middle.
        def beside(d, off=off):
            o = off - 0.6 if off > 0 else off + 0.6
            return (mid + o, z0 + d) if along_z else (x0 + d, mid + o)
        companion_clear = _clear_stretches(beside, length, _boxes) if width >= 3.0 else []
        for a, b in _clear_stretches(at, length, _boxes):
            n = max(1, round((b - a) / WALKER_SPACING / 2))
            for k in range(n):
                kmh = round(_rng.uniform(4.2, 5.4), 1)
                walk_s = (b - a) / (kmh / 3.6)
                pa, pb = at(a), at(b)
                if side == 1:
                    pa, pb = pb, pa              # the other track walks the other way
                wait, delay = round(_rng.uniform(3, 9), 1), round(k * walk_s / n + _rng.uniform(0, 6), 1)
                # About one in four on a wide pavement walks with somebody (Cody, 2026-09-28: "two
                # people walking down the street together"), where the companion's line is clear too.
                pair = ""
                if any(ca <= a and cb >= b for ca, cb in companion_clear) and _rng.random() < 0.25:
                    PAIR_COUNT[0] += 1
                    pair = f"pair {PAIR_COUNT[0]}"
                    qa, qb = beside(a), beside(b)
                    if side == 1:
                        qa, qb = qb, qa
                    WALKERS.append((f"Pedestrian, {label or 'pavement'}, walking with somebody", v3(qa[0], 0.15, qa[1]),
                                    v3(qb[0], 0.15, qb[1]), kmh, wait, delay, pair))
                WALKERS.append((f"Pedestrian, {label or 'pavement'}", v3(pa[0], 0.15, pa[1]),
                                v3(pb[0], 0.15, pb[1]), kmh, wait, delay, pair))

# The plaza, which has no pavement: people crossing it.
PLAZA_WALKS = [
    ("Pedestrian, Market Square, crossing west", v3(-112.0, 0.1, 190.0), v3(-36.0, 0.1, 190.0), 4.5, 8.0, 1.0),
    ("Pedestrian, Market Square, crossing east", v3(-36.0, 0.1, 214.0), v3(-112.0, 0.1, 214.0), 4.3, 10.0, 6.0),
    ("Pedestrian, Market Square, north and south", v3(-66.0, 0.1, 160.0), v3(-66.0, 0.1, 226.0), 4.1, 12.0, 4.0),
    ("Pedestrian, Market Square, crossing west", v3(-100.0, 0.1, 170.0), v3(-40.0, 0.1, 228.0), 4.6, 7.0, 9.0),
]
for name, a, b, kmh, wait, delay in PLAZA_WALKS:
    WALKERS.append((name, a, b, kmh, wait, delay, ""))
    WALKERS.append((name + ", the other way", b, a, round(kmh * 0.94 + 0.3, 1), wait + 3.0, delay + 20.0, ""))

for name, a, b, kmh, wait, delay, pair in WALKERS:
    VEHICLES.append({
        "Name": name, "Preset": "walker", "RoadStart": a, "RoadEnd": b,
        "SpeedsKmh": [kmh, kmh * 0.92], "AccelerationMps2": 0.8, "BrakingMps2": 1.0,
        "WaitSeconds": wait, "StartDelaySeconds": delay, **({"Pair": pair} if pair else {}),
    })

# ── Alex ──────────────────────────────────────────────────────────────────────────────────────
#
# A homeless man (Cody, 2026-10-06: "he walks the streets, rides the bus, hangs out at bus stops and in
# front of stores and the lobby in apartment buildings because he's homeless"). One of him, with his
# own voice, which no walker or driver is given. Only who he is is declared here: where he goes is
# found by the server from what the map is (its bus stops, its front entrances, its squares), and so is
# the way between them (its pavements). See CharacterSystem.
CHARACTERS = [
    {"Name": "Alex", "Voice": "alex", "Kind": "homeless", "Description": "a homeless man"},
]

# ── Trains ────────────────────────────────────────────────────────────────────────────────────
#
# Two light rail sets on the loop, half a lap apart. The server places one entity per sound
# source of a set — each bogie, each drive, the body — round the track at head minus offset, and the
# client runs one synth for the set; see RailSystem and TrainLayout. Speed comes from the loop's
# curvature, so the sets slow for the 26 m corners on their own.
TRAINS = [
    {"Name": "Light rail 1", "Preset": "light_rail", "Track": "rail_loop", "TopSpeedKmh": 45.0,
     "StartOffsetMetres": 0.0, "AccelerationMps2": 0.9, "BrakingMps2": 1.0},
    {"Name": "Light rail 2", "Preset": "light_rail", "Track": "rail_loop", "TopSpeedKmh": 45.0,
     "StartOffsetMetres": 1250.0, "AccelerationMps2": 0.9, "BrakingMps2": 1.0},
]

VEHICLES.append({
    "Name": "Tunnel truck", "Preset": "diesel_truck",
    "RoadStart": v3(LANE, 0.15, MAIN_Z1 - 40.0), "RoadEnd": v3(LANE, 0.15, TUNNEL_Z0 + 8.0),
    "SpeedsKmh": [46.0, 54.0, 40.0], "AccelerationMps2": 1.6, "BrakingMps2": brake_for(0.30),
    "WaitSeconds": 9.0, "StartDelaySeconds": 3.0,
})
VEHICLES.append({
    "Name": "Tunnel car", "Preset": "i4_turbo",
    "RoadStart": v3(-LANE, 0.15, TUNNEL_Z0 + 8.0), "RoadEnd": v3(-LANE, 0.15, MAIN_Z1 - 40.0),
    "SpeedsKmh": [58.0, 48.0], "AccelerationMps2": 2.6, "BrakingMps2": brake_for(0.52),
    "WaitSeconds": 7.0, "StartDelaySeconds": 18.0,
})
VEHICLES.append({
    "Name": "Apron tug", "Preset": "diesel_i4",
    "RoadStart": v3(APRON_X0 + 8.0, 0.15, APRON_Z0 + 10.0),
    "RoadEnd": v3(APRON_X1 - 8.0, 0.15, APRON_Z1 - 10.0),
    "SpeedsKmh": [18.0, 24.0], "AccelerationMps2": 1.2, "BrakingMps2": brake_for(0.35),
    "WaitSeconds": 12.0, "StartDelaySeconds": 0.0,
})

# ══ What is in the air ════════════════════════════════════════════════════════════════════════════
#
# Aircraft are shuttles, and that is not a shortcut — a shuttle is a straight line between two points
# in THREE dimensions, out and back, and that is exactly what a departure and an approach are. The
# same entry therefore gives both: outbound down the line is an aeroplane coming down onto the
# runway, and the return leg is the same aeroplane climbing out. Nothing declares which is which.
#
# The POWER is not declared either. The client reads the lever off the climb angle
# (ClientAudioSystem.FlightPower), so an aeroplane going up is at full power and one coming down is
# at idle with the air doing the work, and the two legs of one shuttle sound completely different
# with nothing about the aeroplane changed. That is the whole reason to do it this way.
#
# The one dishonest moment is the far end, where a shuttle brakes to a stop and waits. An aeroplane
# does not stop in the sky. It is parked at a kilometre and a half of slant range at idle, which is
# under anything else on this map, and it waits there for most of its cycle.
# Only the helicopter now (Cody, 2026-09-25): "the planes are getting to be a bit much ... keep the
# helicopter that flies over every now and then but remove the props." The two airliners, the
# turboprop and the light single are gone; the airport is still there for when they come back.
# No aircraft for now (Cody, 2026-09-25): the helicopter "sounds good too btw but you can remove it for
# now". The airport is still there for when they come back.
AIR = [
]
VEHICLES.extend(AIR)

# ── More motorcycles, and Southgate's traffic ──────────────────────────────────────────────
#
# ONE police car on the city (Cody, 2026-09-25): Police car 9, in the downtown field. The two
# more that were added here are gone.
#
# Added to the shipped map by hand on 2026-09-22 and 2026-09-24 and brought back here. Southgate's
# are declared with car grip whatever they are, as they were shipped (five, since the bus went).
# Two motorcycles on the whole map now, both in the downtown field above (Cody, 2026-09-25: "reduce
# the number of bikes/motor cycles on the map").
# Regular traffic since 2026-09-26: a parcel van, two saloons, a hatch and the mail.
for nm, preset, start in (("van", "step_van", 0.0), ("saloon", "v6", 130.0), ("hatch", "i4_economy", 260.0),
                          ("mid-size", "i4_midsize", 190.0),
                          ("mail truck", "mail_truck", 70.0)):
    VEHICLES.append(car(f"Southgate {nm}", preset,
                        via((SG_X0, SG_Z1), (SG_X0, SG_Z0), (SG_X1, SG_Z0), (SG_X1, SG_Z1)),
                        48.0, 0.48, 1.8, start))
VEHICLES.append(VERGE_MOWER)

# ── Southgate's streets ───────────────────────────────────────────────────────────────────────────
#
# Its traffic has always run a square here, on bare ground: the roads were never laid. Laid last, so
# every entity before them keeps its id. Its north side is Dock Street. No pavements: the rail fences
# stand where they would be at the crossings.
for sg_name, a, b in (("Mill Road", (SG_X0, SG_Z1), (SG_X0, SG_Z0)),
                      ("Kiln Street", (SG_X0, SG_Z0), (SG_X1, SG_Z0)),
                      ("Tanner Road", (SG_X1, SG_Z0), (SG_X1, SG_Z1))):
    record_road(sg_name, "collector", a, b, CARRIAGEWAY)
    x0, x1 = sorted((a[0], b[0]))
    z0, z1 = sorted((a[1], b[1]))
    carriageway(x0 - KERB, x1 + KERB, z0 - KERB, z1 + KERB, name=f"{sg_name} carriageway")
    # The name stops at Dock Street's pavement: past it, walking along Dock Street was being called
    # the side road's name (PlaceNameTests).
    region(sg_name, x0 - KERB, x1 + KERB, 0.0, 6.0, z0 - KERB, min(z1 + KERB, SG_Z1 - WALK))
# Dock Street's new west end, where it meets Mill Road, named like the rest of it.
region("Dock Street, west end", SG_X0 - KERB, ST_X0, 0.0, 6.0, STREETS[0] - KERB, STREETS[0] + KERB)


def find_junctions(roads):
    """Where two roads' centrelines meet, crossing or ending on the other, one junction each."""
    out = {}
    for i, r in enumerate(roads):
        for q in roads[i + 1:]:
            (ax0, _, az0), (ax1, _, az1) = [(p["X"], p["Y"], p["Z"]) for p in r["Centreline"]]
            (bx0, _, bz0), (bx1, _, bz1) = [(p["X"], p["Y"], p["Z"]) for p in q["Centreline"]]
            # Straight roads, each along x or along z.
            r_ns, q_ns = ax0 == ax1, bx0 == bx1
            if r_ns == q_ns:
                continue                                   # parallel: they never meet
            (nx, nz0, nz1, nw), (ez, ex0, ex1, ew) = (
                ((ax0, az0, az1, r["WidthMetres"]), (bz0, bx0, bx1, q["WidthMetres"])) if r_ns
                else ((bx0, bz0, bz1, q["WidthMetres"]), (az0, ax0, ax1, r["WidthMetres"])))
            lo_z, hi_z = sorted((nz0, nz1))
            lo_x, hi_x = sorted((ex0, ex1))
            if not (lo_z - ew / 2 <= ez <= hi_z + ew / 2 and lo_x - nw / 2 <= nx <= hi_x + nw / 2):
                continue
            key = (round(nx, 1), round(ez, 1))
            names = sorted({r["Name"], q["Name"]})
            if key in out:
                out[key]["Name"] = " and ".join(sorted(set(out[key]["Name"].split(" and ") + names)))
                out[key]["RadiusMetres"] = max(out[key]["RadiusMetres"], max(nw, ew) / 2 + 1.0)
                continue
            out[key] = {"Id": f"j_{int(nx)}_{int(ez)}", "Name": " and ".join(names),
                        "Position": v3(nx, 0.05, ez), "RadiusMetres": max(nw, ew) / 2 + 1.0,
                        "Control": "give_way"}
    return list(out.values())


JUNCTIONS = find_junctions(ROADS)

# Who gives way. At each junction the road of the highest class has priority, and between roads of
# the same class the longest (Main Street over the cross streets, the avenues over the streets);
# everything else gives way. A rule rather than a list, so a new junction needs nothing written down.
ROAD_RANK = {"collector": 3, "residential": 2, "service": 1}


def road_length(r):
    (a, b) = r["Centreline"][0], r["Centreline"][-1]
    return math.dist((a["X"], a["Z"]), (b["X"], b["Z"]))


for j in JUNCTIONS:
    here = [r for r in ROADS if r["Name"] in j["Name"].split(" and ")]
    top = max(here, key=lambda r: (ROAD_RANK[r["Type"]], road_length(r)))
    j["PriorityRoads"] = [top["Id"]]
    j["GiveWaySeconds"] = 2.0

# ══ Elm Park ══════════════════════════════════════════════════════════════════════════════════════
#
# The open ground north of the estate, inside the rail loop, which was bare dirt: a lawn with a
# fountain in the middle and trees round it. Elm Park halt, on the rail loop's west side, was named
# for a park that was not there. Laid after Southgate so every entity before it keeps its id.
#
# Nothing in it is a recording. The fountain is water falling into a basin (FallingWaterSynth), the
# trees are the wind in their leaves (FoliageSynth), and both read the one wind field, so a gust
# crosses the park from the west-south-west, through the trees on that side first.
PARK_X0, PARK_X1 = -400.0, -250.0
PARK_Z0, PARK_Z1 = 162.0, 292.0
PARK_CX, PARK_CZ = -325.0, 227.0
PARK_PATH = 3.0
box("grass_floor", PARK_X0, PARK_X1, 0.0, 0.09, PARK_Z0, PARK_Z1, name="Elm Park lawn")
# Paths: north-south through the middle, east-west across it, and the way in from the end of
# Sycamore Lane. Asphalt, so stepping off the grass onto a path is heard.
box("asphalt_road", PARK_CX - PARK_PATH / 2, PARK_CX + PARK_PATH / 2, 0.0, 0.1, PARK_Z0, PARK_Z1, name="Elm Park path")
box("asphalt_road", PARK_X0, PARK_X1, 0.0, 0.1, PARK_CZ - PARK_PATH / 2, PARK_CZ + PARK_PATH / 2, name="Elm Park path")
lane_x = RES_LANES[0]
lane_end = RES_STREETS[-1] + RES_WALK + 8.0
box("asphalt_road", lane_x - PARK_PATH / 2, lane_x + PARK_PATH / 2, 0.0, 0.1, lane_end, PARK_CZ - PARK_PATH / 2,
    name="Elm Park path from Sycamore Lane")
region("Elm Park, path from Sycamore Lane", lane_x - PARK_PATH, lane_x + PARK_PATH, 0.0, 6.0, lane_end, PARK_Z0)

# The fountain (WaterFeatureSpec.ParkFountain): an 11 m square basin with a stone kerb too high to
# step over, standing water, a pedestal carrying a bowl 2.8 m across with a jet rising out of it, and
# round the pedestal's foot a heap of boulders the bowl's overflow falls onto and runs off into the
# pool. Twelve rim jets in the kerb, three a side, arch in towards the rocks.
FTN_HALF, FTN_KERB, FTN_KERB_H = 5.5, 0.3, 0.5
FTN_WATER = 0.35
box("brick_floor", PARK_CX - 12.0, PARK_CX + 12.0, 0.0, 0.11, PARK_CZ - 12.0, PARK_CZ + 12.0,
    name="Elm Park fountain square")
fx0, fx1, fz0, fz1 = PARK_CX - FTN_HALF, PARK_CX + FTN_HALF, PARK_CZ - FTN_HALF, PARK_CZ + FTN_HALF
for (x0, x1, z0, z1) in ((fx0, fx1, fz0, fz0 + FTN_KERB), (fx0, fx1, fz1 - FTN_KERB, fz1),
                         (fx0, fx0 + FTN_KERB, fz0 + FTN_KERB, fz1 - FTN_KERB),
                         (fx1 - FTN_KERB, fx1, fz0 + FTN_KERB, fz1 - FTN_KERB)):
    box("concrete_wall", x0, x1, 0.0, FTN_KERB_H, z0, z1, name="Elm Park fountain basin")
box("water_surface", fx0 + FTN_KERB, fx1 - FTN_KERB, 0.0, FTN_WATER, fz0 + FTN_KERB, fz1 - FTN_KERB,
    name="Elm Park fountain pool")
box("concrete_wall", PARK_CX - 0.3, PARK_CX + 0.3, FTN_WATER, 1.45, PARK_CZ - 0.3, PARK_CZ + 0.3, name="Elm Park fountain pedestal")
box("concrete_floor", PARK_CX - 1.4, PARK_CX + 1.4, 1.45, 1.6, PARK_CZ - 1.4, PARK_CZ + 1.4, name="Elm Park fountain bowl")
# The rocks: eight boulders heaped round the pedestal, out to 1.9 m from it, standing 0.35-0.65 m
# out of the water, under the bowl's lip (1.4 m out) so its overflow falls 0.75 m onto them. Each is
# (x0, x1, z0, z1, top) about the basin's middle.
FTN_ROCKS = [(-0.9, 0.7, 0.3, 1.9, 0.95, "north"), (0.7, 1.8, 0.6, 1.7, 0.8, "north-east"),
             (0.3, 1.9, -0.8, 0.6, 1.0, "east"), (0.5, 1.6, -1.8, -0.8, 0.75, "south-east"),
             (-0.8, 0.5, -1.9, -0.3, 0.9, "south"), (-1.8, -0.8, -1.6, -0.5, 0.8, "south-west"),
             (-1.9, -0.3, -0.5, 0.8, 0.95, "west"), (-1.7, -0.9, 0.8, 1.7, 0.7, "north-west")]
for (x0, x1, z0, z1, top, side) in FTN_ROCKS:
    box("rock_boulder", PARK_CX + x0, PARK_CX + x1, FTN_WATER, top, PARK_CZ + z0, PARK_CZ + z1,
        name=f"Elm Park fountain rocks, {side} side")
# The water, where it lands: five voices of one fountain (WaterFeatureSpec.ParkFountain.Taps). The
# bowl's over the bowl, where the jet comes down; each side's over the water just past the rocks,
# between where the overflow strikes the stone and where that side's rim jets come down. None inside
# the pedestal or a rock: a source inside a box is heard through it.
FTN_TAPS = [(0.0, 1.9, 0.0, "bowl"), (0.0, 0.8, 2.3, "north"), (2.3, 0.8, 0.0, "east"),
            (0.0, 0.8, -2.3, "south"), (-2.3, 0.8, 0.0, "west")]
for i, (tx, ty, tz, where) in enumerate(FTN_TAPS):
    prop(f"elm_fountain_water_{i}", PARK_CX + tx, ty, PARK_CZ + tz,
         name="Elm Park fountain" if i == 0 else f"Elm Park fountain, {where} side")
named_place("Elm Park, by the fountain", PARK_CX - 12.0, PARK_CX + 12.0, 0.0, 3.0, PARK_CZ - 12.0, PARK_CZ + 12.0)

# The trees: a trunk you can walk into, a crown that scatters sound, and the wind in it heard from
# the middle of the crown. Kept five metres or more off every path.
PARK_TREES = [(-385.0, 180.0), (-355.0, 175.0), (-338.0, 170.0), (-310.0, 190.0), (-268.0, 178.0),
              (-262.0, 205.0), (-388.0, 210.0), (-372.0, 250.0), (-345.0, 262.0), (-305.0, 258.0),
              (-275.0, 250.0), (-390.0, 280.0), (-340.0, 286.0), (-265.0, 284.0)]
for tx, tz in PARK_TREES:
    box("wood_floor", tx - 0.25, tx + 0.25, 0.0, 3.0, tz - 0.25, tz + 0.25, name="Elm Park tree")
    box("foliage_hedge", tx - 4.0, tx + 4.0, 3.0, 11.0, tz - 4.0, tz + 4.0, name="Elm Park tree")
    prop("tree_crown", tx, 7.0, tz, name="Elm Park tree")
region("Elm Park", PARK_X0, PARK_X1, 0.0, 12.0, PARK_Z0, PARK_Z1)

# ── A fire in a fenced back garden ────────────────────────────────────────────────────────────────
#
# 58 Alder Street, the house nearest the park's south gate. Its back garden is fenced in timber
# inside the hedges, with a gate in the back fence onto the strip of ground before the park, and
# there is a brick fire pit in it with a fire going (FireSpec.GardenFirePit). From the park you hear
# it over the fence, louder through the gate, and from the garden the fence is the near wall.
FIRE_HOUSE_CX = -325.0
fire_garden = next(e for e in entities if e.get("Name") == "58 Alder Street back garden"
                   and e["PrefabId"] == "grass_floor")
fg_z0 = fire_garden["Position"]["Z"] - fire_garden["Scale"]["Z"] * BASE["grass_floor"][2] / 2
fg_z1 = fire_garden["Position"]["Z"] + fire_garden["Scale"]["Z"] * BASE["grass_floor"][2] / 2
fg_x0, fg_x1 = FIRE_HOUSE_CX - PLOT_W / 2 + 1.0, FIRE_HOUSE_CX + PLOT_W / 2 - 1.0
FENCE_T, FENCE_H, GATE_W, GATE_X = 0.05, 1.8, 1.2, FIRE_HOUSE_CX - 5.0
box("fence_timber", fg_x0, fg_x0 + FENCE_T, 0.0, FENCE_H, fg_z0, fg_z1, name="58 Alder Street garden fence")
box("fence_timber", fg_x1 - FENCE_T, fg_x1, 0.0, FENCE_H, fg_z0, fg_z1, name="58 Alder Street garden fence")
box("fence_timber", fg_x0, GATE_X - GATE_W / 2, 0.0, FENCE_H, fg_z1 - FENCE_T, fg_z1, name="58 Alder Street garden fence")
box("fence_timber", GATE_X + GATE_W / 2, fg_x1, 0.0, FENCE_H, fg_z1 - FENCE_T, fg_z1, name="58 Alder Street garden fence")
named_place("58 Alder Street garden gate", GATE_X - GATE_W / 2, GATE_X + GATE_W / 2, 0.0, 2.5, fg_z1 - 1.0, fg_z1 + 1.0)
FIRE_X, FIRE_Z = FIRE_HOUSE_CX + 4.0, (fg_z0 + fg_z1) / 2 + 1.0
box("brick_floor", FIRE_X - 1.5, FIRE_X + 1.5, 0.0, 0.1, FIRE_Z - 1.5, FIRE_Z + 1.5, name="58 Alder Street fire pit hearth")
for (x0, x1, z0, z1) in ((FIRE_X - 0.7, FIRE_X + 0.7, FIRE_Z - 0.7, FIRE_Z - 0.5), (FIRE_X - 0.7, FIRE_X + 0.7, FIRE_Z + 0.5, FIRE_Z + 0.7),
                         (FIRE_X - 0.7, FIRE_X - 0.5, FIRE_Z - 0.5, FIRE_Z + 0.5), (FIRE_X + 0.5, FIRE_X + 0.7, FIRE_Z - 0.5, FIRE_Z + 0.5)):
    box("brick_wall", x0, x1, 0.0, 0.45, z0, z1, name="58 Alder Street fire pit")
# The flames, above the ring: at the ring's height the brick would stand between them and anyone
# sitting round it.
prop("fire_pit", FIRE_X, 0.6, FIRE_Z, name="58 Alder Street fire pit")

# The named places last, so that adding one moved no other part's id.
for _name, *_span in named_places:
    box("named_place", *_span, name=_name)

map_data = {
    "Id": "city",
    "IsDefault": False,
    "Description": "A city: five towers over a grid of avenues and cross streets, a three-deck "
                   "parking garage, a plaza, a tunnel under Main Street, a light rail loop right "
                   "round the outside with three stations, an airport with a runway, a terminal and "
                   "a steel hangar, and an estate of houses with gardens and mowers. Traffic runs "
                   "the roads and aircraft work the airfield. Generated by tools/gen_city.py.",
    "Size": v3(MAP_MAX[0] - MAP_MIN[0], MAP_MAX[1], MAP_MAX[2] - MAP_MIN[2]),
    "MinBound": v3(*MAP_MIN),
    "MaxBound": v3(*MAP_MAX),
    # Where a player can walk and drive: the ground, not the acoustic bounds.
    "PlayMin": v3(BUILT_MIN[0], -20.0, BUILT_MIN[1]),
    "PlayMax": v3(BUILT_MAX[0], MAP_MAX[1], BUILT_MAX[1]),
    "MinimumY": -20.0,
    "SpawnPoint": {"Position": v3(*SPAWN), "Rotation": {"X": 0, "Y": 0, "Z": 0, "W": 1}},
    # No ambience bed. See no-ambience-beds: a recorded loop has no source, no distance and no
    # geometry, and on a map built to demonstrate exactly those three it buries all of them.
    "AmbienceId": "",
    # A warm, dry afternoon. Temperature and Humidity are read as OFFSETS from the sim's baselines
    # (20 C / 0.5), and the world's calendar starts on day 1 — deep winter, about -5 C before the
    # daily swing. Twenty-two degrees over puts the whole daily swing clear of freezing, under which
    # the client swaps every footstep for SNOW.
    "Temperature": 42.0,
    "Humidity": 0.40,
    # One metre, not a half. The map is fifty times the area of the block it replaces and the
    # occlusion grid is a sparse octree, so what this costs is leaf count along SURFACES — and a city
    # is mostly surface. Half-metre leaves over a kilometre of frontage is a load time nobody waits
    # through. Read the server's grid line after any change here.
    "VoxelResolution": 1.0,
    "OcclusionFloor": 0.1,
    "Tracks": TRACKS,
    "Roads": ROADS,
    "Junctions": JUNCTIONS,
    "RoadStops": ROAD_STOPS,
    "Characters": CHARACTERS,
    "StreetLife": STREET_LIFE,
    "Vehicles": VEHICLES,
    "Trains": TRAINS,
    "Entities": entities,
    "Crossings": CROSSINGS,
    "Composites": PARKED,
}

with open(OUT, "w") as f:
    json.dump(map_data, f, indent=1)

regions = sum(1 for e in entities if e["PrefabId"] == "acoustic_region")
portals = sum(1 for e in entities if e["PrefabId"] == "portal")
DOOR_PREFABS = ("door", "steel_door", "glass_front_door", "glass_pull_door", "auto_sliding_door",
                "patio_door", "elevator_door")
doors = sum(1 for e in entities if e["PrefabId"] in DOOR_PREFABS)
door_kinds = ", ".join(f"{p} {n}" for p in DOOR_PREFABS
                       if (n := sum(1 for e in entities if e["PrefabId"] == p)))
machines = sum(1 for e in entities if e["PrefabId"] in ("ac_window", "ac_condenser", "mower_push", "mower_riding"))
solid = sum(1 for e in entities if e["PrefabId"] in BASE and e["PrefabId"] not in ("acoustic_region", "portal", "named_place"))
parts_named = sum(1 for e in entities if e["PrefabId"] == "named_place")
print(f"{OUT}: {len(entities)} entities — {regions} regions, {parts_named} named parts of rooms, {portals} portals, {doors} doors, "
      f"{machines} machines, {solid} boxes")
print(f"  doors by kind: {door_kinds}")
print(f"  bounds {MAP_MAX[0] - MAP_MIN[0]:.0f} x {MAP_MAX[1]:.0f} x {MAP_MAX[2] - MAP_MIN[2]:.0f} m, "
      f"{len(HOUSES)} houses, {len(TRACKS)} routes, {len(VEHICLES) - len(AIR)} vehicles, {len(AIR)} aircraft")
print(f"  rail loop {sum(math.dist((RAIL[i][0], RAIL[i][2]), (RAIL[(i + 1) % len(RAIL)][0], RAIL[(i + 1) % len(RAIL)][2])) for i in range(len(RAIL))):.0f} m, "
      f"{len(RAIL)} points")
print(f"  spawn {SPAWN}, on the Foundry Street pavement facing north")
