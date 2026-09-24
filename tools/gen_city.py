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
import json, math, os, glob

PREFAB_DIR = "OpenFPS.Server/prefabs"
OUT = "OpenFPS.Server/maps/city.json"

# ── The prefabs' own dimensions, read rather than remembered ──────────────────────────────────────
#
# Every box below is written as the space it occupies, and the scale that produces it is worked out
# from the prefab's collider. Hard-coding those sizes here is the standing way for a generated map to
# drift from the prefabs it is made of.
BASE = {}
for path in glob.glob(os.path.join(PREFAB_DIR, "*.json")):
    if path.endswith("prefab-schema.json"):
        continue
    with open(path) as f:
        p = json.load(f)
    c = p.get("ColliderSize")
    if c:
        BASE[p["Id"]] = (c.get("X", 1.0), c.get("Y", 1.0), c.get("Z", 1.0))


def v3(x, y, z):
    return {"X": round(x, 4), "Y": round(y, 4), "Z": round(z, 4)}


_next_id = [1000]


def new_id():
    _next_id[0] += 1
    return _next_id[0]


entities = []


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


def yaw(radians):
    return {"X": 0.0, "Y": round(math.sin(radians / 2), 6), "Z": 0.0, "W": round(math.cos(radians / 2), 6)}


def region(name, x0, x1, y0, y1, z0, z1):
    """A named place. It says WHERE and WHAT IT IS CALLED and nothing else.

    Naming a place costs nothing (region-is-not-a-room) and an unnamed one is a place a player
    cannot be told they are in, so these are generous.
    """
    return box("acoustic_region", x0, x1, y0, y1, z0, z1, name=name)


def portal(x, y, z, a, b, aperture):
    """A hole between two named places. -1 is the outside."""
    entities.append({
        "EntityId": new_id(),
        "PrefabId": "portal",
        "Position": v3(x, y, z),
        "RegionAId": a, "RegionBId": b, "ApertureSize": aperture,
    })


def door(x, y0, z, a, b, facing_z=True, prefab="door"):
    """A leaf in a doorway, joining two named places.

    A door is ALREADY a portal — PrefabRepository attaches one to anything with IsDoor — so a doorway
    needs a door and NOT a door plus a portal beside it.
    """
    bx, by, bz = BASE[prefab]
    entities.append({
        "EntityId": new_id(),
        "PrefabId": prefab,
        "Position": v3(x, y0 + by / 2, z),
        "Rotation": yaw(0.0) if facing_z else yaw(math.pi / 2),
        "RegionAId": a, "RegionBId": b,
    })


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
CORRIDOR    = 2.2
FLAT_DEPTH  = 9.0
FLATS       = 4                          # per side per storey, plus the stairwell slot

STAIR_RISE  = 0.3                        # under PhysicsConstants.StepHeight (0.4), or it is a wall
STAIR_GOING = 0.32
STAIR_W     = 1.6

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

SPAWN = (0.0, 0.1, -40.0)                # Main Street, downtown, facing north up the grid

# ══ Ground ════════════════════════════════════════════════════════════════════════════════════════
#
# Laid before anything else, in the materials the surfaces really are. The ground probe takes the
# HIGHEST surface under your feet and a tie goes to whichever box it tested first, so every made
# surface stands PROUD of the one under it: bare ground at -0.2..0, asphalt 0..0.05, pavement
# 0..0.12. A flush tie is what once put 374 footsteps on dirt in the middle of a road.
box("dirt_floor", BUILT_MIN[0], BUILT_MAX[0], -0.2, 0.0, BUILT_MIN[1], BUILT_MAX[1], name="Ground")


def carriageway(x0, x1, z0, z1, name=None):
    box("asphalt_road", x0, x1, 0.0, 0.05, z0, z1, name=name)


def footway(x0, x1, z0, z1, name=None):
    box("concrete_floor", x0, x1, 0.0, 0.12, z0, z1, name=name)


def avenue(x, z0, z1, label):
    """A north-south road: asphalt between two kerbed pavements."""
    carriageway(x - KERB, x + KERB, z0, z1, name=f"{label} carriageway")
    footway(x - WALK, x - KERB, z0, z1)
    footway(x + KERB, x + WALK, z0, z1)


def street(z, x0, x1, label):
    """An east-west road."""
    carriageway(x0, x1, z - KERB, z + KERB, name=f"{label} carriageway")
    footway(x0, x1, z - WALK, z - KERB)
    footway(x0, x1, z + KERB, z + WALK)


avenue(AVENUES[0], AVE_Z0, AVE_Z1, "Wharf Avenue")
avenue(AVENUES[1], MAIN_Z0, MAIN_Z1, "Main Street")
avenue(AVENUES[2], AVE_Z0, AVE_Z1, "Calder Avenue")
street(STREETS[0], ST_X0, ST_X1, "Dock Street")
street(STREETS[1], RES_X0 - 24.0, TERM_X0 - 6.0, "Central Street")
street(STREETS[2], ST_X0, APRON_X0, "Foundry Street")
street(STREETS[3], ST_X0, ST_X1, "North Street")

# The intersections themselves: asphalt laid over the pavement corners, so that walking across one is
# walking on a road the whole way rather than stepping onto a kerb in the middle of it.
for ax in AVENUES:
    for sz in STREETS:
        if ax != 0.0 and not (AVE_Z0 <= sz <= AVE_Z1):
            continue
        if sz == STREETS[3] and ax not in AVENUES:
            continue
        carriageway(ax - WALK, ax + WALK, sz - WALK, sz + WALK)

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

    for s in range(storeys):
        y0 = s * STOREY
        ceil = y0 + STOREY - SLAB
        floor_top = y0 + SLAB if s else 0.02

        if s:
            B("concrete_floor", sx0, sx1, y0, y0 + SLAB, sz0, sz1)
        else:
            B("concrete_floor", sx0, sx1, -0.15, 0.02, sz0, sz1)
        B("concrete_floor", sx0, sx1, ceil, y0 + STOREY, sz0, sz1)

        # The brick shell. The street face is BROKEN at the stairwell on the ground floor, and that
        # gap is the front door: a doorway is an absence, not a leaf standing against solid brick.
        entrance = None
        if s == 0:
            eb = (stair_b[0] + stair_b[1]) / 2
            entrance = (eb - 0.9, eb + 0.9)

        street_wall = (sx1 - WALL_T, sx1) if side > 0 else (sx0, sx0 + WALL_T)
        other_wall = (sx0, sx0 + WALL_T) if side > 0 else (sx1 - WALL_T, sx1)
        B("brick_wall", other_wall[0], other_wall[1], floor_top, ceil, sz0, sz1)
        if entrance:
            B("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, sz0, entrance[0])
            B("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, entrance[1], sz1)
            B("brick_wall", street_wall[0], street_wall[1], floor_top + 2.15, ceil, entrance[0], entrance[1])
        else:
            B("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, sz0, sz1)
        B("brick_wall", sx0, sx1, floor_top, ceil, sz0, sz0 + WALL_T)
        B("brick_wall", sx0, sx1, floor_top, ceil, sz1 - WALL_T, sz1)

        near_wall = corridor[1] if side > 0 else corridor[0] - WALL_T
        far_wall = corridor[0] - WALL_T if side > 0 else corridor[1]

        # Corridor walls with a doorway per flat cut out of each.
        for wall_a in (near_wall, far_wall):
            cuts = []
            for i in range(slots):
                b0 = sz0 + WALL_T + i * slot_len
                dz = b0 + slot_len * 0.5
                cuts.append((dz - DOOR_W / 2, dz + DOOR_W / 2))
            at = sz0
            for c0, c1 in cuts:
                if c0 > at:
                    B("plaster_wall", wall_a, wall_a + WALL_T, floor_top, ceil, at, c0)
                at = c1
            if at < sz1:
                B("plaster_wall", wall_a, wall_a + WALL_T, floor_top, ceil, at, sz1)

        for i in range(1, slots):
            pb = sz0 + WALL_T + i * slot_len
            for fa0, fa1 in (far_flat, near_flat):
                B("plaster_wall", fa0, fa1, floor_top, ceil, pb - WALL_T / 2, pb + WALL_T / 2)

        # Carpet in the flats and down the corridor, tile left bare in the stairwell — the stairwell
        # is the live space in the building and the contrast is the point. A plastered soffit over
        # all of it, which is what you are under in a flat rather than the bare structural slab.
        B("carpet_floor", far_flat[0], far_flat[1], floor_top, floor_top + 0.04, sz0 + WALL_T, sz1 - WALL_T)
        B("carpet_floor", near_flat[0], near_flat[1], floor_top, floor_top + 0.04, stair_b[1], sz1 - WALL_T)
        B("carpet_floor", corridor[0], corridor[1], floor_top, floor_top + 0.04, sz0 + WALL_T, sz1 - WALL_T)
        B("plaster_wall", inner[0], inner[1], ceil - 0.03, ceil, sz0 + WALL_T, sz1 - WALL_T)

        # What is IN them. Carpet is deaf to bass (carpet-is-deaf-to-bass): it does its job at mid and
        # top and almost nothing at the bottom, so a carpeted room with hard walls keeps a two-second
        # BASS tail under a 600 ms middle. What takes the bottom out is upholstery with air behind it,
        # which is a membrane absorber — a sofa and a bed per flat, as Audience.
        for i in range(slots):
            b0 = sz0 + WALL_T + i * slot_len
            for which, (fa0, fa1) in (("front", near_flat), ("back", far_flat)):
                if which == "front" and i == 0:
                    continue
                B("furniture_soft", fa0 + 0.6, fa0 + 1.5, floor_top, floor_top + 0.85, b0 + 1.0, b0 + 3.2)
                B("furniture_soft", fa1 - 2.1, fa1 - 0.3, floor_top, floor_top + 0.6, b0 + 4.6, b0 + 6.4)

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
                door(dx0, floor_top, dz0, flat_id, corridor_id, facing_z=not vertical)

        # ── The stairwell ──────────────────────────────────────────────────────────────────────
        sa0, sa1 = near_flat
        stair_id = R(f"{label} stairwell, floor {s}", sa0, sa1, floor_top, ceil, stair_b[0], stair_b[1])
        B("tile_floor", sa0, sa1, floor_top - 0.02, floor_top + 0.03, stair_b[0], stair_b[1])
        px, _, pz, _ = place(near_wall + WALL_T / 2, 0, (stair_b[0] + stair_b[1]) / 2, 0)
        portal(px, floor_top + 1.0, pz, stair_id, corridor_id, 1.4)

        if s + 1 < storeys:
            steps = int(round(STOREY / STAIR_RISE))
            run = steps * STAIR_GOING
            base_b = stair_b[0] + 0.4
            ca = (sa0 + sa1) / 2
            for k in range(steps):
                B("concrete_floor", ca - STAIR_W / 2, ca + STAIR_W / 2,
                  floor_top, floor_top + (k + 1) * STAIR_RISE,
                  base_b + k * STAIR_GOING, base_b + (k + 1) * STAIR_GOING)
            void0, void1 = base_b - 0.3, base_b + run + 0.3
            B("concrete_floor", sa0, sa1, ceil, ceil + SLAB, stair_b[0], void0)
            B("concrete_floor", sa0, sa1, ceil, ceil + SLAB, void1, stair_b[1])

        if s == 0:
            ex, _, ez, _ = place(sx1 - WALL_T / 2 if side > 0 else sx0 + WALL_T / 2, 0,
                                 (stair_b[0] + stair_b[1]) / 2, 0)
            # The same line as the flats' doors: the facade runs the same way as the corridor wall.
            # It was `facing_z=vertical`, which stood every tower's entrance at right angles to its
            # own facade — shut, the doorway was open round it; opened, the leaf swung across it.
            # city.json's doors were re-fitted to their openings on 2026-09-23 (turned and sized).
            door(ex, 0.02, ez, stair_id, -1, facing_z=not vertical, prefab="steel_door")

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

    box("concrete_floor", x0, x1, storeys * STOREY, storeys * STOREY + SLAB, z0, z1, name=f"{label} roof")
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
        box("concrete_floor", GAR_X0, GAR_X1, 0.0, SLAB, GAR_Z0, GAR_Z1)
    else:
        box("concrete_floor", GAR_X0, GAR_X1, y0 - SLAB, y0, GAR_Z0, GAR_Z1)
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
    for x0, x1, z0, z1 in ((GAR_X0, GAR_X0 + 0.3, GAR_Z0, GAR_Z1),
                           (GAR_X0, GAR_X1, GAR_Z0, GAR_Z0 + 0.3),
                           (GAR_X0, GAR_X1, GAR_Z1 - 0.3, GAR_Z1)):
        box("concrete_wall", x0, x1, y0, y0 + SPANDREL, z0, z1)
        # Piers on a 7.5 m grid, up the rest of the way.
        along_z = (z1 - z0) > (x1 - x0)
        span = (z1 - z0) if along_z else (x1 - x0)
        for k in range(int(span // 7.5) + 1):
            at = (z0 if along_z else x0) + k * 7.5
            if along_z:
                box("concrete_wall", x0, x1, y0 + SPANDREL, y0 + GAR_CLEAR, at - 0.25, at + 0.25)
            else:
                box("concrete_wall", at - 0.25, at + 0.25, y0 + SPANDREL, y0 + GAR_CLEAR, z0, z1)
    for k in range(int((GAR_Z1 - GAR_Z0) // 6) + 1):
        cz = GAR_Z0 + k * 6.0
        box("concrete_wall", GAR_X1 - 0.4, GAR_X1, y0, y0 + GAR_CLEAR, cz - 0.2, cz + 0.2)
    region(f"Parking garage, level {lv}", GAR_X0, GAR_X1, y0, y0 + GAR_CLEAR, GAR_Z0, GAR_Z1)
box("concrete_floor", GAR_X0, GAR_X1, GAR_LEVELS * (GAR_CLEAR + SLAB) - SLAB,
    GAR_LEVELS * (GAR_CLEAR + SLAB), GAR_Z0, GAR_Z1, name="Garage roof")
box("metal_wall", GAR_X1 - 0.1, GAR_X1, 0.0, GAR_CLEAR, GAR_Z1 - 6.2, GAR_Z1 - 0.3, name="Garage shutter")

# ══ The tunnel ════════════════════════════════════════════════════════════════════════════════════
#
# A roofed box with two openings, and the best test the enclosure survey has: long, hard, closed on
# four faces and open on two. The rail crosses OVER it at z = -228, so a unit going round the loop
# passes above your head while you are in there.
carriageway(-KERB, KERB, TUNNEL_Z0, TUNNEL_Z1)
box("concrete_wall", -KERB - 0.5, -KERB, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel west wall")
box("concrete_wall", KERB, KERB + 0.5, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel east wall")
box("concrete_wall", -WALK, -KERB - 0.5, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1)
box("concrete_wall", KERB + 0.5, WALK, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1)
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
        0.0, 2.4, shelter_z - 2.2, shelter_z + 2.2, name="Shelter back")
    box("glass_wall", min(front, back), max(front, back), 0.0, 2.4, shelter_z - 2.2, shelter_z - 2.14)
    box("glass_wall", min(front, back), max(front, back), 0.0, 2.4, shelter_z + 2.14, shelter_z + 2.2)
    box("metal_wall", min(front, back), max(front, back), 2.4, 2.5, shelter_z - 2.2, shelter_z + 2.2,
        name="Shelter roof")

# ══ The plaza ═════════════════════════════════════════════════════════════════════════════════════
#
# The one big hard open space downtown. Brick underfoot, a colonnade down one side and nothing over
# it: a place with strong early reflections and almost no reverberant field, which is the pair a
# street does not give you.
PLZ_X0, PLZ_X1 = -120.0, -WALK - 2.0
PLZ_Z0, PLZ_Z1 = 148.0, 232.0
box("brick_floor", PLZ_X0, PLZ_X1, 0.0, 0.1, PLZ_Z0, PLZ_Z1, name="Market Square")
for k in range(9):
    px = PLZ_X0 + 4.0 + k * 12.0
    if px > PLZ_X1 - 4.0:
        break
    box("pillar_round", px - 0.35, px + 0.35, 0.0, 5.0, PLZ_Z0 + 3.0, PLZ_Z0 + 3.7)
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
    """True where Main Street's carriageway runs over the line rather than under it.

    A level crossing is PLANKED: the road surface is carried across the rails, not laid beside them.
    So the formation and the rails stop short either side and the asphalt runs through — which is
    also the difference between walking over a crossing and tripping over a sixteen-centimetre lip.
    """
    return abs(x) < CROSSING_HALF and abs(z - RAIL_Z_S) < 12.0


for i, p in enumerate(RAIL):
    q = RAIL[(i + 1) % len(RAIL)]
    mx, mz = (p[0] + q[0]) / 2, (p[2] + q[2]) / 2
    if on_the_crossing(mx, mz):
        continue
    dx, dz = q[0] - p[0], q[2] - p[2]
    seg = math.hypot(dx, dz)
    ang = math.atan2(dx, dz)
    # Written as an axis-aligned box and then turned, because a box IS axis-aligned until it is.
    bx, by, bz = BASE["dirt_floor"]
    entities.append({
        "EntityId": new_id(), "PrefabId": "dirt_floor",
        "Position": v3(mx, 0.35, mz), "Rotation": yaw(ang),
        "Scale": v3(RAIL_W / bx, 0.7 / by, (seg + 0.6) / bz),
        "Name": "Rail ballast" if i == 0 else None,
    })
    entities[-1] = {k: v for k, v in entities[-1].items() if v is not None}
    for rail_off in (-0.72, 0.72):
        ox, oz = math.cos(ang) * rail_off, -math.sin(ang) * rail_off
        mbx, mby, mbz = BASE["metal_wall"]
        entities.append({
            "EntityId": new_id(), "PrefabId": "metal_wall",
            "Position": v3(mx + ox, 0.78, mz + oz), "Rotation": yaw(ang),
            "Scale": v3(0.14 / mbx, 0.16 / mby, (seg + 0.6) / mbz),
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
    box("tile_floor", x0, x1, 0.93, 0.99, z0, z1)
    box("metal_wall", x0 - 0.3, x1 + 1.6, 4.4, 4.55, z0, z1, name=f"{label} canopy")
    n = int((z1 - z0) // 9) if along_z else int((x1 - x0) // 9)
    for k in range(n + 1):
        if along_z:
            pz = z0 + k * 9.0
            box("concrete_wall", x1 - 0.5, x1 - 0.1, 0.95, 4.4, pz - 0.2, pz + 0.2)
        else:
            px = x0 + k * 9.0
            box("concrete_wall", px - 0.2, px + 0.2, 0.95, 4.4, z1 - 0.5, z1 - 0.1)
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
    box("asphalt_road", side_x0, side_x1, 0.0, 0.16, RAIL_Z_S - 3.2, RAIL_Z_S + 3.2)
region("Level crossing", -CROSSING_HALF, CROSSING_HALF, 0.0, 4.0, RAIL_Z_S - 3.2, RAIL_Z_S + 3.2)

# ══ The airport ═══════════════════════════════════════════════════════════════════════════════════
#
# A runway is the largest flat hard surface a person is ever near, and it is the one place on this
# map with NOTHING to reflect off for hundreds of metres in every direction. That makes it the
# control: an engine heard on the apron and the same engine heard in a street should differ by the
# street, and here is the street taken away.
box("asphalt_road", RUNWAY_X - RUNWAY_W / 2, RUNWAY_X + RUNWAY_W / 2, 0.0, 0.06,
    RUNWAY_Z0, RUNWAY_Z1, name="Runway 18/36")
box("concrete_floor", RUNWAY_X - RUNWAY_W / 2 - 8.0, RUNWAY_X - RUNWAY_W / 2, 0.0, 0.04, RUNWAY_Z0, RUNWAY_Z1)
box("concrete_floor", RUNWAY_X + RUNWAY_W / 2, RUNWAY_X + RUNWAY_W / 2 + 8.0, 0.0, 0.04, RUNWAY_Z0, RUNWAY_Z1)
box("asphalt_road", TAXI_X - TAXI_W / 2, TAXI_X + TAXI_W / 2, 0.0, 0.06, RUNWAY_Z0 + 40.0, RUNWAY_Z1 - 40.0,
    name="Taxiway A")
box("concrete_floor", APRON_X0, APRON_X1, 0.0, 0.08, APRON_Z0, APRON_Z1, name="Apron")
# The link from the taxiway to the apron, and the one from the runway to the taxiway.
box("asphalt_road", APRON_X1, TAXI_X - TAXI_W / 2, 0.0, 0.06, 40.0, 63.0)
box("asphalt_road", TAXI_X + TAXI_W / 2, RUNWAY_X - RUNWAY_W / 2, 0.0, 0.06, 40.0, 63.0)
box("asphalt_road", TAXI_X + TAXI_W / 2, RUNWAY_X - RUNWAY_W / 2, 0.0, 0.06, -220.0, -197.0)
region("Runway", RUNWAY_X - RUNWAY_W / 2, RUNWAY_X + RUNWAY_W / 2, 0.0, 8.0, RUNWAY_Z0, RUNWAY_Z1)
region("Apron", APRON_X0, APRON_X1, 0.0, 8.0, APRON_Z0, APRON_Z1)

# ── The terminal: one tall glazed concourse ───────────────────────────────────────────────────────
box("concrete_floor", TERM_X0, TERM_X1, -0.15, 0.04, TERM_Z0, TERM_Z1)
box("tile_floor", TERM_X0, TERM_X1, 0.02, 0.08, TERM_Z0, TERM_Z1)
box("concrete_floor", TERM_X0, TERM_X1, TERM_H, TERM_H + 0.3, TERM_Z0, TERM_Z1, name="Terminal roof")
box("concrete_wall", TERM_X0, TERM_X0 + 0.4, 0.0, TERM_H, TERM_Z0, TERM_Z1, name="Terminal west wall")
box("concrete_wall", TERM_X0, TERM_X1, 0.0, TERM_H, TERM_Z0, TERM_Z0 + 0.4)
box("concrete_wall", TERM_X0, TERM_X1, 0.0, TERM_H, TERM_Z1 - 0.4, TERM_Z1)
# The apron face is glass, in bays, with two doorways cut out of it.
TERM_DOORS = (30.0, 92.0)
bay_at = TERM_Z0 + 0.4
for dz in TERM_DOORS:
    box("glass_wall", TERM_X1 - 0.06, TERM_X1, 0.0, TERM_H, bay_at, dz - 1.1)
    box("glass_wall", TERM_X1 - 0.06, TERM_X1, 2.2, TERM_H, dz - 1.1, dz + 1.1)
    bay_at = dz + 1.1
box("glass_wall", TERM_X1 - 0.06, TERM_X1, 0.0, TERM_H, bay_at, TERM_Z1 - 0.4)
# A row of seating down the middle, which is the only soft thing in the building.
for k in range(7):
    sz = TERM_Z0 + 14.0 + k * 17.0
    if sz > TERM_Z1 - 14.0:
        break
    box("furniture_soft", (TERM_X0 + TERM_X1) / 2 - 1.2, (TERM_X0 + TERM_X1) / 2 + 1.2,
        0.08, 0.95, sz - 2.4, sz + 2.4)
term_ids = []
for k in range(3):
    a = TERM_Z0 + k * (TERM_Z1 - TERM_Z0) / 3
    b = TERM_Z0 + (k + 1) * (TERM_Z1 - TERM_Z0) / 3
    term_ids.append(region(f"Terminal concourse, {['south', 'middle', 'north'][k]} end",
                           TERM_X0 + 0.4, TERM_X1 - 0.06, 0.08, TERM_H, a, b))
for dz, rid in zip(TERM_DOORS, (term_ids[0], term_ids[1])):
    door(TERM_X1 - 0.03, 0.08, dz, rid, -1, facing_z=False, prefab="door")   # the wall runs along z
# ...and a way in from the road side.
door(TERM_X0 + 0.2, 0.08, 62.0, term_ids[1], -1, facing_z=False, prefab="steel_door")

# ── The hangar: a steel box the size of a church ──────────────────────────────────────────────────
#
# Eleven metres to the underside of a steel roof, steel on every side, a concrete floor and one
# enormous opening. Metal absorbs five per cent of what hits it, so this is the most reverberant
# place on the map by a distance, and the one that most needs the survey to see that the door is a
# door — it is the small-enclosure fault the other way up.
box("concrete_floor", HANGAR_X0, HANGAR_X1, -0.2, 0.06, HANGAR_Z0, HANGAR_Z1)
box("metal_wall", HANGAR_X0, HANGAR_X0 + 0.12, 0.0, HANGAR_H, HANGAR_Z0, HANGAR_Z1, name="Hangar back")
box("metal_wall", HANGAR_X0, HANGAR_X1, 0.0, HANGAR_H, HANGAR_Z1 - 0.12, HANGAR_Z1)
box("metal_wall", HANGAR_X0, HANGAR_X1, 0.0, HANGAR_H, HANGAR_Z0, HANGAR_Z0 + 0.12)
box("metal_wall", HANGAR_X0, HANGAR_X1, HANGAR_H, HANGAR_H + 0.15, HANGAR_Z0, HANGAR_Z1, name="Hangar roof")
# The door: the apron face, open across the middle 26 m and shut either side.
box("metal_wall", HANGAR_X1 - 0.12, HANGAR_X1, 0.0, HANGAR_H, HANGAR_Z0, HANGAR_Z0 + 15.0)
box("metal_wall", HANGAR_X1 - 0.12, HANGAR_X1, 0.0, HANGAR_H, HANGAR_Z1 - 15.0, HANGAR_Z1)
box("metal_wall", HANGAR_X1 - 0.12, HANGAR_X1, 8.4, HANGAR_H, HANGAR_Z0 + 15.0, HANGAR_Z1 - 15.0)
hangar_id = region("Hangar", HANGAR_X0 + 0.12, HANGAR_X1 - 0.12, 0.06, HANGAR_H,
                   HANGAR_Z0 + 0.12, HANGAR_Z1 - 0.12)
portal(HANGAR_X1 - 0.06, 4.0, (HANGAR_Z0 + HANGAR_Z1) / 2, hangar_id, -1, 26.0)
# A steel personnel door in the back, which is the small opening the big one is measured against.
door(HANGAR_X0 + 0.06, 0.06, HANGAR_Z0 + 8.0, hangar_id, -1, facing_z=False, prefab="steel_door")
prop("ac_condenser", HANGAR_X0 + 2.4, HANGAR_H + 0.6, HANGAR_Z0 + 6.0, name="Hangar roof plant")

# The airport road: Foundry Street carries on east to the terminal.
carriageway(APRON_X0 - 46.0, TERM_X0 - 6.0, STREETS[2] - KERB, STREETS[2] + KERB, name="Airport Road")
footway(APRON_X0 - 46.0, TERM_X0 - 6.0, STREETS[2] - WALK, STREETS[2] - KERB)
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

    # Lawns first, so the house sits on them and the ground probe still finds grass either side.
    box("grass_floor", cx - PLOT_W / 2 + 1.0, cx + PLOT_W / 2 - 1.0, 0.0, 0.09,
        min(front_z, front_z + d * 9.0), max(front_z, front_z + d * 9.0), name=f"{label} front lawn")
    box("grass_floor", cx - PLOT_W / 2 + 1.0, cx + PLOT_W / 2 - 1.0, 0.0, 0.09,
        min(back_z, back_z - d * 14.0), max(back_z, back_z - d * 14.0), name=f"{label} back garden")
    # The drive, up one side. Asphalt, so walking off the grass onto it is audible.
    dv0, dv1 = cx + PLOT_W / 2 - 4.6, cx + PLOT_W / 2 - 1.2
    box("asphalt_road", dv0, dv1, 0.0, 0.1, min(front_z, front_z + d * 9.0), max(front_z, front_z + d * 9.0))

    box("concrete_floor", x0, x1, -0.15, 0.04, zlo, zhi)
    box("wood_floor", x0 + 0.2, x1 - 0.2, 0.02, 0.08, zlo + 0.2, zhi - 0.2)
    box("brick_wall", x0, x0 + 0.25, 0.0, h, zlo, zhi)
    box("brick_wall", x1 - 0.25, x1, 0.0, h, zlo, zhi)

    # THE FRONT WALL IS THE ONE FACING THE STREET, which depends on which side of it the house is.
    # It did not: both walls were built at zlo whichever way the plot was turned, so every house on
    # the north side of a street had its front door in the back garden and its back door on the
    # pavement. Nothing complains about that — a door is a door to the code — and it is the kind of
    # thing only walking up to one finds.
    front_z0, front_z1 = (zlo, zlo + 0.25) if d > 0 else (zhi - 0.25, zhi)
    back_z0, back_z1 = (zhi - 0.25, zhi) if d > 0 else (zlo, zlo + 0.25)
    box("brick_wall", x0, x1, 0.0, h, back_z0, back_z1)
    box("brick_wall", x0, cx - 0.65, 0.0, h, front_z0, front_z1)
    box("brick_wall", cx + 0.65, x1, 0.0, h, front_z0, front_z1)
    box("brick_wall", cx - 0.65, cx + 0.65, 2.15, h, front_z0, front_z1)
    box("concrete_floor", x0, x1, h, h + 0.2, zlo, zhi, name=f"{label} roof")

    # ── Furnished, or a bungalow is a brick box with a wooden floor and rings like one ───────────
    #
    # One sofa and a carpet was not enough: measured at <-325, 1.6, -106> it read a 827 ms middle
    # over a 2.1 s BOTTOM, which is carpet-is-deaf-to-bass exactly — a thin absorber does nothing at
    # a long wavelength, so a carpeted room with bare brick walls keeps its bass tail. What takes the
    # bottom out is upholstery with air behind it, which is a membrane absorber, and a house has more
    # than one piece of it: a sofa, a bed, a wardrobe against a wall and a curtain over the window.
    box("furniture_soft", x0 + 0.8, x0 + 2.6, 0.08, 0.95, zlo + 1.4, zhi - 1.4)
    box("furniture_soft", x1 - 2.4, x1 - 0.4, 0.08, 0.75, zlo + 1.2, zlo + 3.4)
    box("furniture_soft", x0 + 0.4, x0 + 1.0, 0.08, 2.0, zhi - 2.6, zhi - 0.6)
    # The curtain hangs over the front WINDOW, beside the door — not across the doorway, which is
    # where it hung until 2026-09-23: every front door on the estate opened onto a solid curtain.
    box("carpet_wall", cx + 1.1, cx + 3.3, 0.9, h - 0.15,
        front_z0 + 0.25 if d > 0 else front_z1 - 0.3, front_z0 + 0.3 if d > 0 else front_z1 - 0.25)
    box("carpet_floor", cx - 1.0, x1 - 0.4, 0.08, 0.12, zlo + 0.4, zhi - 0.4)
    # PLASTER ON THE INSIDE OF THE BRICK, which is what a house is and is the missing membrane.
    #
    # Brick absorbs 3 % of the bottom and plaster absorbs 28 % — a skin on a wall is a membrane and
    # a solid wall is not, which is the same fact as carpet-is-deaf-to-bass seen from the other side.
    # Lined, the low band comes down from 2.1 s to where the middle is; unlined, a brick box with a
    # carpet in it keeps a bass tail no amount of soft furnishing touches.
    box("plaster_wall", x0 + 0.25, x0 + 0.31, 0.08, h, zlo + 0.25, zhi - 0.25)
    box("plaster_wall", x1 - 0.31, x1 - 0.25, 0.08, h, zlo + 0.25, zhi - 0.25)
    box("plaster_wall", x0 + 0.25, x1 - 0.25, 0.08, h,
        min(back_z0, back_z1) + (0.25 if d > 0 else -0.06), min(back_z0, back_z1) + (0.31 if d > 0 else 0.0))
    box("plaster_wall", x0 + 0.25, x1 - 0.25, h - 0.06, h, zlo + 0.25, zhi - 0.25)

    hid = region(label, x0 + 0.25, x1 - 0.25, 0.08, h, zlo + 0.25, zhi - 0.25)
    door(cx, 0.04, (front_z0 + front_z1) / 2, hid, -1, facing_z=True, prefab="door")
    # The back door, which is how you get to the garden without going round.
    door(cx - 2.4, 0.04, (back_z0 + back_z1) / 2, hid, -1, facing_z=True, prefab="door")

    region(f"{label} back garden", cx - PLOT_W / 2 + 1.0, cx + PLOT_W / 2 - 1.0, 0.0, 3.0,
           min(back_z, back_z - d * 14.0), max(back_z, back_z - d * 14.0))

    # The fence between this garden and the next. Not solid: you can hear a mower through a fence,
    # which is most of the point of putting one there.
    for fx in (cx - PLOT_W / 2, cx + PLOT_W / 2):
        box("foliage_hedge", fx - 0.4, fx + 0.4, 0.0, 1.8,
            min(back_z, back_z - d * 14.0), max(back_z, back_z - d * 14.0))
    return hid


HOUSES = []
for si, sz in enumerate(RES_STREETS):
    # The street itself.
    carriageway(RES_X0, RES_X1, sz - RES_CARRIAGEWAY / 2, sz + RES_CARRIAGEWAY / 2,
                name=f"{['Elm', 'Birch', 'Rowan', 'Alder'][si]} Street carriageway")
    footway(RES_X0, RES_X1, sz - RES_WALK, sz - RES_CARRIAGEWAY / 2)
    footway(RES_X0, RES_X1, sz + RES_CARRIAGEWAY / 2, sz + RES_WALK)
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
carriageway(RES_X1, -WALK, STREETS[1] - KERB, STREETS[1] + KERB)
# ...and the two that run north-south through the estate, joining its streets into a grid.
for li, lx in enumerate(RES_LANES):
    carriageway(lx - RES_CARRIAGEWAY / 2, lx + RES_CARRIAGEWAY / 2,
                RES_STREETS[0] - RES_WALK - 8.0, RES_STREETS[-1] + RES_WALK + 8.0,
                name=f"{['Sycamore', 'Willow'][li]} Lane")
    footway(lx - RES_WALK, lx - RES_CARRIAGEWAY / 2,
            RES_STREETS[0] - RES_WALK - 8.0, RES_STREETS[-1] + RES_WALK + 8.0)
    footway(lx + RES_CARRIAGEWAY / 2, lx + RES_WALK,
            RES_STREETS[0] - RES_WALK - 8.0, RES_STREETS[-1] + RES_WALK + 8.0)
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
        box("foliage_hedge", ax - WALK + 0.5, ax - WALK + 2.1, 0.0, 5.0, tz - 0.8, tz + 0.8)
        box("foliage_hedge", ax + WALK - 2.1, ax + WALK - 0.5, 0.0, 5.0, tz + 7.0 - 0.8, tz + 7.0 + 0.8)
        k += 1

# ══ Zones over the open ground ════════════════════════════════════════════════════════════════════
#
# A street is a place and wants a name as much as a room does. None of these is indoors; the survey
# will find one or two covered faces and say so, which is the point — "Outside" is not a location.
for ax, aname in zip(AVENUES, ("Wharf Avenue", "Main Street", "Calder Avenue")):
    z0, z1 = (MAIN_Z0, MAIN_Z1) if ax == 0.0 else (AVE_Z0, AVE_Z1)
    n = max(1, int((z1 - z0) // 60))
    for k in range(n):
        a, b = z0 + k * (z1 - z0) / n, z0 + (k + 1) * (z1 - z0) / n
        if ax == 0.0 and b <= TUNNEL_Z1:
            continue                                  # that stretch is the tunnel, already named
        region(f"{aname}, block {k + 1}", ax - KERB, ax + KERB, 0.0, 6.0, a, b)
        region(f"{aname} west pavement, block {k + 1}", ax - WALK, ax - KERB, 0.0, 4.0, a, b)
        region(f"{aname} east pavement, block {k + 1}", ax + KERB, ax + WALK, 0.0, 4.0, a, b)

for sz, sname, sx0, sx1 in ((STREETS[0], "Dock Street", ST_X0, ST_X1),
                            (STREETS[1], "Central Street", RES_X0, TERM_X0 - 6.0),
                            (STREETS[2], "Foundry Street", ST_X0, APRON_X0),
                            (STREETS[3], "North Street", ST_X0, ST_X1)):
    n = max(1, int((sx1 - sx0) // 60))
    for k in range(n):
        a, b = sx0 + k * (sx1 - sx0) / n, sx0 + (k + 1) * (sx1 - sx0) / n
        region(f"{sname}, block {k + 1}", a, b, 0.0, 6.0, sz - KERB, sz + KERB)

# The cross streets' pavements, named as the avenues' are. They were left out, so every metre of
# footway along Dock, Central, Foundry and North Street fell into the map-wide outdoor region and was
# announced as "Outside" — the one word a player cannot navigate by. Emitted after the carriageways,
# in the same order, so the ids come out as they are in city.json.
for sz, sname, sx0, sx1 in ((STREETS[0], "Dock Street", ST_X0, ST_X1),
                            (STREETS[1], "Central Street", RES_X0, TERM_X0 - 6.0),
                            (STREETS[2], "Foundry Street", ST_X0, APRON_X0),
                            (STREETS[3], "North Street", ST_X0, ST_X1)):
    n = max(1, int((sx1 - sx0) // 60))
    for k in range(n):
        a, b = sx0 + k * (sx1 - sx0) / n, sx0 + (k + 1) * (sx1 - sx0) / n
        region(f"{sname} south pavement, block {k + 1}", a, b, 0.0, 4.0, sz - WALK, sz - KERB)
        region(f"{sname} north pavement, block {k + 1}", a, b, 0.0, 4.0, sz + KERB, sz + WALK)

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
    {"Id": "downtown_cw", "WidthMetres": CARRIAGEWAY, "BankingDegrees": 0.0,
     "Waypoints": [v3(*p) for p in loop([(-130.0, -130.0), (130.0, -130.0), (130.0, 130.0), (-130.0, 130.0)])]},
    {"Id": "downtown_ccw", "WidthMetres": CARRIAGEWAY, "BankingDegrees": 0.0,
     "Waypoints": [v3(*p) for p in loop([(-130.0, 130.0), (130.0, 130.0), (130.0, -130.0), (-130.0, -130.0)])]},
    {"Id": "north_block", "WidthMetres": CARRIAGEWAY, "BankingDegrees": 0.0,
     "Waypoints": [v3(*p) for p in loop([(0.0, 130.0), (130.0, 130.0), (130.0, 260.0), (0.0, 260.0)])]},
    # Round the estate, on the two lanes and the two outer streets — all four of which are roads,
    # which is what the driveable test is for.
    {"Id": "estate", "WidthMetres": RES_CARRIAGEWAY, "BankingDegrees": 0.0,
     "Waypoints": [v3(*p) for p in loop([(RES_LANES[0], RES_STREETS[0]), (RES_LANES[1], RES_STREETS[0]),
                                         (RES_LANES[1], RES_STREETS[-1]), (RES_LANES[0], RES_STREETS[-1])],
                                        corner=8.0)]},
]

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
    grip_g = GRIP[grip]
    return {"Name": name, "Preset": preset, "Track": track,
            "TopSpeedKmh": top, "CorneringG": g, "GripG": grip_g,
            "AccelerationMps2": accel_for(grip_g, accel),
            "BrakingMps2": brake if brake is not None else brake_for(grip_g),
            "LaneOffsetMetres": lane, "StartOffsetMetres": start}


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
CITY = [
    ("Civic",        "i4_economy",      52.0, 0.48, 1.8, "car"),    #  94 dB
    ("Hatchback",    "i4_turbo",        56.0, 0.52, 1.8, "car"),    # 100
    ("Saloon",       "v6",              50.0, 0.46, 1.8, "car"),    #  98
    ("Estate",       "i4_economy",      48.0, 0.45, 1.8, "car"),    #  94
    ("Sedan",        "v6",              50.0, 0.47, 1.8, "car"),    #  98
    ("Pickup",       "diesel_i4",       46.0, 0.40, 1.8, "van"),    #  88
    ("Box truck",    "diesel_truck",    38.0, 0.30, 1.8, "truck"),  #  93
    ("Muscle car",   "v8_muscle",       58.0, 0.55, 1.8, "car"),    # 122 — one of them, on purpose
    ("Police car",   "police_v8",       62.0, 0.60, 1.8, "car"),    # 132 — one of them, on purpose
    ("Motorcycle",   "vtwin",           56.0, 0.60, 3.4, "bike"),   # 121 — one of them, on purpose
]
for i, (nm, preset, top, g, lane, kind) in enumerate(CITY):
    VEHICLES.append(car(f"{nm} {i + 1}", preset, "downtown_cw", top, g, lane, i * 96.0, grip=kind))
for i, (nm, preset, top, g, lane, kind) in enumerate(CITY[:7]):
    VEHICLES.append(car(f"{nm} {i + 11}", preset, "downtown_ccw", top, g, lane, 40.0 + i * 130.0, grip=kind))
# The buses go round the north block, stopping for nothing, which is what makes a ten-metre machine
# worth having: its two ends are five metres apart and you hear it turn.
VEHICLES.append(car("City bus 1", "school_bus_na", "north_block", 40.0, 0.28, 2.0, 0.0, accel=1.4, grip="bus"))
VEHICLES.append(car("City bus 2", "school_bus_na", "north_block", 40.0, 0.28, 2.0, 280.0, accel=1.4, grip="bus"))
VEHICLES.append(car("Delivery diesel", "diesel_cummins", "north_block", 44.0, 0.34, 2.0, 140.0, grip="truck"))
# The estate: slow, quiet, and the thing you hear over the mowers.
for i, (nm, preset) in enumerate((("Civic", "i4_economy"), ("Wagon", "v6"), ("Pickup", "diesel_i4"))):
    VEHICLES.append(car(f"{nm}, Elm Street", preset, "estate", 30.0, 0.35, 1.2, i * 180.0,
                        accel=1.6, grip="car"))

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

VEHICLES.append({
    "Name": "Verge mower", "Preset": "mower_riding",
    "RoadStart": v3(156.0, 0.62, 66.0), "RoadEnd": v3(156.0, 0.62, 116.0),
    "SpeedsKmh": [7.0, 6.5], "AccelerationMps2": 0.8, "BrakingMps2": 1.2,
    "WaitSeconds": 2.0, "StartDelaySeconds": 3.0,
})

# ── Parked cars you can get into ──────────────────────────────────────────────────────────────────
#
# On the ground deck of the garage, nose out between the street-side piers, so pulling away is
# straight onto the pavement and Main Street. Each is "vehicle:<profile>": the server builds the shell
# from the profile (VehicleShell) rather than from a file, so a car here cannot drift from the car.
PARKED = []
for preset, z in (("i4_economy", 29.0), ("v6", 35.0), ("diesel_i4", 41.0), ("v8_muscle", 47.0)):
    PARKED.append({"TemplateId": "vehicle:" + preset, "Position": v3(GAR_X1 - 4.5, SLAB, z),
                   "Rotation": {"X": 0, "Y": 0.707107, "Z": 0, "W": 0.707107}, "Owner": ""})

# ── People ────────────────────────────────────────────────────────────────────────────────────
#
# Somebody walking is heard by their footsteps, which the client makes from the body's own
# movement, so a walker carries no sound of its own: it is a body on a line at a walking pace. Four
# of them, on pavements, each with a wait at the end of the walk where they would stand at a
# crossing or a door. The Main Street pair keep north of the tunnel, whose pavements are wall.
PAVEMENT_X = KERB + 1.0                                # a metre off the kerb, clear of the shelter
WALKERS = [
    ("Pedestrian, Main Street east", v3(PAVEMENT_X, 0.15, -180.0), v3(PAVEMENT_X, 0.15, 115.0), 5.0, 6.0, 0.0),
    ("Pedestrian, Main Street west", v3(-PAVEMENT_X, 0.15, 110.0), v3(-PAVEMENT_X, 0.15, -175.0), 4.6, 4.0, 11.0),
    ("Pedestrian, Foundry Street", v3(12.0, 0.15, STREETS[2] - KERB - 1.0), v3(118.0, 0.15, STREETS[2] - KERB - 1.0), 5.2, 8.0, 5.0),
    ("Pedestrian, Sycamore Lane", v3(RES_LANES[0] - RES_CARRIAGEWAY / 2 - 0.9, 0.15, -100.0),
                                  v3(RES_LANES[0] - RES_CARRIAGEWAY / 2 - 0.9, 0.15, 125.0), 4.4, 5.0, 3.0),
]
for name, a, b, kmh, wait, delay in WALKERS:
    VEHICLES.append({
        "Name": name, "Preset": "walker", "RoadStart": a, "RoadEnd": b,
        "SpeedsKmh": [kmh, kmh * 0.92], "AccelerationMps2": 0.8, "BrakingMps2": 1.0,
        "WaitSeconds": wait, "StartDelaySeconds": delay,
    })

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
    "Name": "Airport coach", "Preset": "school_bus_na",
    "RoadStart": v3(APRON_X0 - 40.0, 0.15, STREETS[2] - LANE / 2),
    "RoadEnd": v3(-WALK - 20.0, 0.15, STREETS[2] - LANE / 2),
    "SpeedsKmh": [44.0, 36.0], "AccelerationMps2": 1.3, "BrakingMps2": brake_for(0.28),
    "WaitSeconds": 14.0, "StartDelaySeconds": 6.0,
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
AIR = [
    # An airliner on final for runway 18, and climbing out again. 3.9 degrees, which is a degree
    # steeper than a real glideslope and is what fits inside the bounds.
    {"Name": "Airliner, runway 18", "Preset": "airliner",
     "RoadStart": v3(RUNWAY_X, 92.0, 1320.0), "RoadEnd": v3(RUNWAY_X, 3.0, RUNWAY_Z0 + 120.0),
     "SpeedsKmh": [270.0, 250.0], "AccelerationMps2": 1.9, "BrakingMps2": 2.4,
     "WaitSeconds": 26.0, "StartDelaySeconds": 8.0},
    # ...and one that does not land: a cruise pass straight over the city, high and fast. This is the
    # one the whole map is the test of — an aeroplane you hear for half a minute, moving, with five
    # towers and a tunnel between you and it depending on where you stand.
    {"Name": "Airliner overhead", "Preset": "airliner",
     "RoadStart": v3(-620.0, 760.0, -1080.0), "RoadEnd": v3(420.0, 830.0, 1280.0),
     "SpeedsKmh": [820.0, 780.0], "AccelerationMps2": 1.2, "BrakingMps2": 1.6,
     "WaitSeconds": 40.0, "StartDelaySeconds": 0.0},
    # A regional turboprop on the parallel taxiway approach, lower and much slower — the aircraft a
    # listener can actually follow across the sky.
    {"Name": "Turboprop, inbound", "Preset": "turboprop",
     "RoadStart": v3(RUNWAY_X - 40.0, 240.0, 980.0), "RoadEnd": v3(RUNWAY_X, 3.0, RUNWAY_Z0 + 200.0),
     "SpeedsKmh": [210.0, 180.0], "AccelerationMps2": 1.6, "BrakingMps2": 2.2,
     "WaitSeconds": 20.0, "StartDelaySeconds": 40.0},
    # A light single in the circuit, low and slow over the airfield — the only aircraft on this map
    # you can hear the individual blade passes of.
    {"Name": "Light single, circuit", "Preset": "piston_single",
     "RoadStart": v3(RUNWAY_X + 120.0, 210.0, -260.0), "RoadEnd": v3(RUNWAY_X + 120.0, 210.0, 280.0),
     "SpeedsKmh": [165.0, 150.0, 180.0], "AccelerationMps2": 1.1, "BrakingMps2": 1.4,
     "WaitSeconds": 6.0, "StartDelaySeconds": 22.0},
    # A helicopter across the city at rooftop height, which is the one aircraft that is ever CLOSE.
    {"Name": "Helicopter, city transit", "Preset": "helicopter",
     "RoadStart": v3(-420.0, 118.0, -240.0), "RoadEnd": v3(300.0, 96.0, 340.0),
     "SpeedsKmh": [190.0, 150.0], "AccelerationMps2": 1.4, "BrakingMps2": 1.8,
     "WaitSeconds": 24.0, "StartDelaySeconds": 62.0},
]
VEHICLES.extend(AIR)

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
    "Vehicles": VEHICLES,
    "Trains": TRAINS,
    "Composites": PARKED,
    "Entities": entities,
}

with open(OUT, "w") as f:
    json.dump(map_data, f, indent=1)

regions = sum(1 for e in entities if e["PrefabId"] == "acoustic_region")
portals = sum(1 for e in entities if e["PrefabId"] == "portal")
doors = sum(1 for e in entities if e["PrefabId"] in ("door", "steel_door"))
machines = sum(1 for e in entities if e["PrefabId"] in ("ac_window", "ac_condenser", "mower_push", "mower_riding"))
solid = sum(1 for e in entities if e["PrefabId"] in BASE and e["PrefabId"] not in ("acoustic_region", "portal"))
print(f"{OUT}: {len(entities)} entities — {regions} named places, {portals} portals, {doors} doors, "
      f"{machines} machines, {solid} boxes")
print(f"  bounds {MAP_MAX[0] - MAP_MIN[0]:.0f} x {MAP_MAX[1]:.0f} x {MAP_MAX[2] - MAP_MIN[2]:.0f} m, "
      f"{len(HOUSES)} houses, {len(TRACKS)} routes, {len(VEHICLES) - len(AIR)} vehicles, {len(AIR)} aircraft")
print(f"  rail loop {sum(math.dist((RAIL[i][0], RAIL[i][2]), (RAIL[(i + 1) % len(RAIL)][0], RAIL[(i + 1) % len(RAIL)][2])) for i in range(len(RAIL))):.0f} m, "
      f"{len(RAIL)} points")
print(f"  spawn {SPAWN}, facing north up Main Street")
