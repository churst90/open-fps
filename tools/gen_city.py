#!/usr/bin/env python3
"""
Generates OpenFPS.Server/maps/city.json — one city block, static.

Step 1 of docs/NEXT_THE_CITY.md: "two apartment blocks with interiors, a street between, a tunnel at
one end, a garage, a bus shelter, a metro platform with overhang, zones under all of it." No traffic,
no pedestrians, no trains, no aircraft — those come after, and each of them needs something this map
is the test of.

WHY THIS IS GENERATED. A building is a hundred boxes whose coordinates all have to agree, and a
storey is the same hundred boxes three metres up. Typing that is how a wall ends up a decimetre out
and a room stops being enclosed — and an enclosure that is 0.9 covered instead of 1.0 is not a
visible mistake, it is a room that sounds slightly wrong for ever. The dimensions below are the
source; the map is an output. Run it again after changing one.

WHAT THIS MAP IS FOR. The acoustics now fall out of boxes, materials and the survey
(MapManager.SurveyRegions), so no region here names its own materials: every one of them is measured
at load from the walls actually around it. The log line that says so is the first thing to read after
a change. The four questions it exists to answer are in docs/NEXT_THE_CITY.md section 4.
"""
import json, math, os, glob

PREFAB_DIR = "OpenFPS.Server/prefabs"
OUT = "OpenFPS.Server/maps/city.json"

# ── The prefabs' own dimensions, read rather than remembered ──────────────────────────────────────
#
# Every box below is written as the space it occupies, and the scale that produces it is worked out
# from the prefab's collider. Hard-coding those sizes here is the standing way for a generated map to
# drift from the prefabs it is made of: change concrete_wall from 0.5 m thick to 0.35 and every wall
# on the map silently changes thickness, or does not, depending on which copy of the number won.
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

def box(prefab, x0, x1, y0, y1, z0, z1, name=None, eid=None):
    """One box, written as the space it fills. Scale comes from the prefab's own collider."""
    bx, by, bz = BASE[prefab]
    e = {
        "EntityId": eid or new_id(),
        "PrefabId": prefab,
        "Position": v3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
        "Scale": v3((x1 - x0) / bx, (y1 - y0) / by, (z1 - z0) / bz),
    }
    if name:
        e["Name"] = name
    entities.append(e)
    return e["EntityId"]

def region(name, x0, x1, y0, y1, z0, z1):
    """
    A named place. It says WHERE and WHAT IT IS CALLED and nothing else.

    No RoomMaterials and no IsIndoor anywhere on this map, deliberately. MapManager.SurveyRegions
    measures each of these six faces against the geometry at load: what they are made of, and whether
    four of the six are walled at all — so a flat is brick because there is brick round it, and a
    stretch of pavement is outdoors because there is nothing over it. Either one authored here would
    be wrong the first time a wall moved. Naming a place costs nothing (region-is-not-a-room), so
    they are generous.
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
    """
    A leaf in a doorway, joining two named places.

    A door is ALREADY a portal — PrefabRepository attaches one to anything with IsDoor, because the
    opening it makes when it swings aside is the entire point of it — so a doorway needs a door and
    NOT a door plus a portal beside it. Putting both there gave every doorway on the first draft of
    this map a second aperture joining the outside to itself, which the server logged forty-four times
    and then ignored. What the door needs is the two regions it joins, which is what these are.
    """
    bx, by, bz = BASE[prefab]
    rot = {"X": 0, "Y": 0, "Z": 0, "W": 1} if facing_z else {"X": 0, "Y": 0.7071, "Z": 0, "W": 0.7071}
    entities.append({
        "EntityId": new_id(),
        "PrefabId": prefab,
        "Position": v3(x, y0 + by / 2, z),
        "Rotation": rot,
        "RegionAId": a, "RegionBId": b,
    })

# ══ Dimensions ════════════════════════════════════════════════════════════════════════════════════
#
# A street between two blocks, running north-south. Everything else hangs off these.
CARRIAGEWAY = 12.0          # kerb to kerb, m — two lanes and no parking
PAVEMENT    = 3.5           # each side
KERB_X      = CARRIAGEWAY / 2
WALK_X      = KERB_X + PAVEMENT          # 9.5: the building line

STOREY      = 3.0           # floor to floor, m
SLAB        = 0.25          # structural slab thickness
STOREYS     = 3
WALL_T      = 0.35          # brick, from brick_wall's own collider
DOOR_W      = 1.0           # a doorway gap
CORRIDOR    = 2.2           # corridor width, m
FLAT_DEPTH  = 9.0           # from the corridor wall to the outside wall
BLOCK_Z     = 40.0          # how long a building is along the street
FLATS       = 4             # per side per storey

STAIR_RISE  = 0.3           # one step, m. Under PhysicsConstants.StepHeight (0.4), or it is a wall.
STAIR_GOING = 0.32          # tread depth
STAIR_W     = 1.6

# The two buildings, offset along the street. A symmetric street is the ONE geometry a city never
# has, and it is also the one that makes a flutter echo between two parallel faces at exactly the
# same z — an artefact of the map rather than of the model. Sixteen metres of offset removes it.
WEST_Z0, WEST_Z1 = -28.0, -28.0 + BLOCK_Z
EAST_Z0, EAST_Z1 = -12.0, -12.0 + BLOCK_Z

BUILDING_W = CORRIDOR + 2 * FLAT_DEPTH + 3 * WALL_T     # outer face to outer face
WEST_X1 = -WALK_X                    # the building line, west side
WEST_X0 = WEST_X1 - BUILDING_W
EAST_X0 = WALK_X
EAST_X1 = EAST_X0 + BUILDING_W

TUNNEL_Z1, TUNNEL_Z0 = -40.0, -70.0   # the street runs into it at the south end
TUNNEL_H = 5.5

GARAGE_Z0, GARAGE_Z1 = WEST_Z1 + 6.0, WEST_Z1 + 6.0 + 28.0
GARAGE_CLEAR = 2.5
GARAGE_LEVELS = 2

PLAT_X0 = EAST_X1 + 6.0              # the platform sits behind the east block
PLAT_W  = 6.0
PLAT_Z0, PLAT_Z1 = -6.0, 38.0
PLAT_Y  = 1.0                        # platform height above the track bed
PLAT_ROOF = 4.6
TRACK_W = 9.0

MAP_MIN = (-60.0, 0.0, -80.0)
MAP_MAX = (80.0, 40.0, 70.0)

SPAWN = (0.0, 0.1, -20.0)            # in the carriageway, south of both buildings, facing north

# ══ Ground ════════════════════════════════════════════════════════════════════════════════════════
#
# Laid before anything else, and in the materials the surfaces really are: the carriageway is
# asphalt, which absorbs three or four times what concrete does and most of it at the top of the
# band, and the pavement is concrete, which does not. Walking from one to the other is the simplest
# demonstration on this map that a material is a fact about a place.
# LAID ON the ground, each with its own thickness, so the probe can tell them apart.
#
# The ground probe takes the HIGHEST surface under your feet, and a tie goes to whichever box it
# happened to test first. Making the bare ground flush with the carriageway — which fixed a lip —
# made them tie, and the whole street came back as Dirt: 374 footsteps on dirt in one session, on a
# road. Five centimetres of asphalt on the dirt and a seven-centimetre kerb up to the pavement is
# what a street actually is, and each surface then wins where it is.
box("asphalt_road", -KERB_X, KERB_X, 0.0, 0.05, TUNNEL_Z0, MAP_MAX[2], name="Road")
box("concrete_floor", -WALK_X, -KERB_X, 0.0, 0.12, TUNNEL_Z1, MAP_MAX[2], name="West pavement")
box("concrete_floor", KERB_X, WALK_X, 0.0, 0.12, TUNNEL_Z1, MAP_MAX[2], name="East pavement")
# Everything else the map stands on. Dirt, so that stepping off the made ground is audible — and
# FLUSH with the carriageway, not ten centimetres below it. A lip is a thing a body has to step down,
# and the only lip on this map that should exist is the kerb.
box("dirt_floor", MAP_MIN[0], MAP_MAX[0], -0.2, 0.0, MAP_MIN[2], MAP_MAX[2], name="Ground")

# ══ An apartment building ═════════════════════════════════════════════════════════════════════════

def apartment_block(side, x0, x1, z0, z1, label):
    """
    Three storeys of flats either side of a corridor, with a stairwell at the street end.

    Built out of the parts a building is built out of and nothing else: brick outside, concrete slabs
    between the storeys, a tiled stairwell, plaster-on-brick inside, doors in the doorways. Nothing
    here declares an acoustic anything. What a flat sounds like is what the survey finds around it.

    `side` is which way the street is: +1 means the street is to the EAST of this block.
    """
    street_x = x1 if side > 0 else x0
    inner_x = x0 + WALL_T, x1 - WALL_T
    # The corridor runs the length of the building, with a row of flats on each side of it.
    if side > 0:
        far_flat = (inner_x[0], inner_x[0] + FLAT_DEPTH)                      # away from the street
        corridor = (far_flat[1] + WALL_T, far_flat[1] + WALL_T + CORRIDOR)
        near_flat = (corridor[1] + WALL_T, inner_x[1])                        # street side
    else:
        near_flat = (inner_x[0], inner_x[0] + FLAT_DEPTH)                     # street side
        corridor = (near_flat[1] + WALL_T, near_flat[1] + WALL_T + CORRIDOR)
        far_flat = (corridor[1] + WALL_T, inner_x[1])

    flat_len = (z1 - z0 - WALL_T * 2) / FLATS
    stair_z = (z0 + WALL_T, z0 + WALL_T + flat_len)       # the southernmost slot, near side: stairs

    for s in range(STOREYS):
        y0 = s * STOREY
        ceil = y0 + STOREY - SLAB
        floor_top = y0 + SLAB if s else 0.02

        # Slabs. The floor of a storey is the ceiling of the one below, so it is written once — but
        # the GROUND floor still needs one of its own, or the flats stand on the map's bare dirt and
        # the survey quite correctly says so.
        if s:
            box("concrete_floor", x0, x1, y0, y0 + SLAB, z0, z1)
        else:
            box("concrete_floor", x0, x1, -0.15, 0.02, z0, z1)
        box("concrete_floor", x0, x1, ceil, y0 + STOREY, z0, z1)

        # The brick shell.
        #
        # The street face is BROKEN at the stairwell on the ground floor, and that gap is the front
        # door. It has to be cut out of the wall, not covered by a leaf standing in front of one: the
        # first draft put a steel door against unbroken brick, so the building had a door you could
        # open onto a wall and no way in at all. A doorway is an absence.
        entrance = None
        if s == 0:
            ez = (stair_z[0] + stair_z[1]) / 2
            entrance = (ez - 0.9, ez + 0.9)

        street_wall = (x1 - WALL_T, x1) if side > 0 else (x0, x0 + WALL_T)
        other_wall = (x0, x0 + WALL_T) if side > 0 else (x1 - WALL_T, x1)
        box("brick_wall", other_wall[0], other_wall[1], floor_top, ceil, z0, z1)
        if entrance:
            box("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, z0, entrance[0])
            box("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, entrance[1], z1)
            # The head of the doorway: brick above it, because a hole to the ceiling is not a door.
            box("brick_wall", street_wall[0], street_wall[1], floor_top + 2.15, ceil, entrance[0], entrance[1])
        else:
            box("brick_wall", street_wall[0], street_wall[1], floor_top, ceil, z0, z1)
        box("brick_wall", x0, x1, floor_top, ceil, z0, z0 + WALL_T)
        box("brick_wall", x0, x1, floor_top, ceil, z1 - WALL_T, z1)

        # Which corridor wall belongs to which row of flats. It depends on which side of the building
        # the street is: the near row is the one against the street, and on the east block that is the
        # row at SMALLER x. Hard-coding it cost a set of doors opening into the wrong wall.
        near_wall_x = corridor[1] if side > 0 else corridor[0] - WALL_T
        far_wall_x = corridor[0] - WALL_T if side > 0 else corridor[1]

        # Corridor walls, with a doorway per flat cut out of each.
        for wall_x in (near_wall_x, far_wall_x):
            cut = []
            for i in range(FLATS):
                fz0 = z0 + WALL_T + i * flat_len
                door_z = fz0 + flat_len * 0.5
                cut.append((door_z - DOOR_W / 2, door_z + DOOR_W / 2))
            at = z0
            for c0, c1 in cut:
                if c0 > at:
                    box("plaster_wall", wall_x, wall_x + WALL_T, floor_top, ceil, at, c0)
                at = c1
            if at < z1:
                box("plaster_wall", wall_x, wall_x + WALL_T, floor_top, ceil, at, z1)

        # Partitions between flats, both rows.
        for i in range(1, FLATS):
            pz = z0 + WALL_T + i * flat_len
            for fx0, fx1 in (far_flat, near_flat):
                box("plaster_wall", fx0, fx1, floor_top, ceil, pz - WALL_T / 2, pz + WALL_T / 2)

        # ── What is ON the floors ───────────────────────────────────────────────────────────────
        #
        # A flat is not a bare concrete box. Measured with `--enclosure map=city at=14.35,1.6,3.1`,
        # one of these with a concrete floor reads a 330 % reverb send at a metre and a half — the
        # room answering ten decibels over your own footstep — and that is CORRECT for four hard
        # walls, a hard floor and nothing in the room at all. Nobody lives in that.
        #
        # Carpet in the flats and down the corridors, which is what an apartment block has, and it is
        # the one material in the table that takes sixty per cent of what reaches it. The stairwell
        # keeps its tile deliberately: it is the live space in the building and the contrast is the
        # point. This is the "thick carpet and curtains are material entries, not code" item from
        # docs/NEXT_THE_CITY.md, and it belongs to the map rather than the engine.
        box("carpet_floor", far_flat[0], far_flat[1], floor_top, floor_top + 0.04, z0 + WALL_T, z1 - WALL_T)
        # The near row stops at the stairwell: carpet laid over it would bury the tile and the
        # stairwell would measure the same as a flat, which is the one thing it must not do.
        box("carpet_floor", near_flat[0], near_flat[1], floor_top, floor_top + 0.04, stair_z[1], z1 - WALL_T)
        box("carpet_floor", corridor[0], corridor[1], floor_top, floor_top + 0.04, z0 + WALL_T, z1 - WALL_T)
        # A plastered soffit, which is what you are under in a flat — not the bare structural slab.
        box("plaster_wall", inner_x[0], inner_x[1], ceil - 0.03, ceil, z0 + WALL_T, z1 - WALL_T)

        # ── ...and what is IN them ──────────────────────────────────────────────────────────────
        #
        # Carpet alone is not a flat. Measured with `--enclosure map=city at=14.35,1.6,3.1`: the
        # carpet does its job at mid and top (0.19 and 0.28 of what reaches it) and almost nothing at
        # the bottom (0.061), because that is what carpet is — a thin absorber is deaf to a long
        # wavelength. So a carpeted room with bare hard walls keeps a two-second BASS tail over a
        # 600 ms middle, and is heard, correctly, as "reverby like it's a reflective room not carpet".
        #
        # What takes the bottom out of a room is not more carpet, it is furniture: upholstery with air
        # behind it, which is a membrane absorber and works where carpet cannot. A sofa and a bed in
        # each flat, as Audience — the most absorbent thing in the table and the right one, since an
        # upholstered seat is what that material IS.
        for i in range(FLATS):
            fz0 = z0 + WALL_T + i * flat_len
            for which, (fx0, fx1) in (("front", near_flat), ("back", far_flat)):
                if which == "front" and i == 0:
                    continue          # the stairwell, which is furnished with stairs
                # A sofa against the inner wall and a bed against the outer one.
                box("furniture_soft", fx0 + 0.6, fx0 + 1.5, floor_top, floor_top + 0.85, fz0 + 1.0, fz0 + 3.2)
                box("furniture_soft", fx1 - 2.1, fx1 - 0.3, floor_top, floor_top + 0.6, fz0 + 5.0, fz0 + 7.0)

        # ── Zones, and the doors between them ──────────────────────────────────────────────────
        corridor_id = region(f"{label} corridor, floor {s}",
                             corridor[0], corridor[1], floor_top, ceil, z0 + WALL_T, z1 - WALL_T)

        for i in range(FLATS):
            fz0 = z0 + WALL_T + i * flat_len
            fz1 = fz0 + flat_len
            door_z = fz0 + flat_len * 0.5
            for which, (fx0, fx1) in (("front", near_flat), ("back", far_flat)):
                # The southernmost near-side slot is the stairwell, not a flat.
                if which == "front" and i == 0:
                    continue
                flat_id = region(f"{label} flat {s}{i + 1}{which[0].upper()}",
                                 fx0, fx1, floor_top, ceil, fz0, fz1)
                wall_x = near_wall_x if which == "front" else far_wall_x
                door(wall_x + WALL_T / 2, floor_top, door_z, flat_id, corridor_id, facing_z=False)

        # ── The stairwell ──────────────────────────────────────────────────────────────────────
        sx0, sx1 = near_flat
        stair_id = region(f"{label} stairwell, floor {s}", sx0, sx1, floor_top, ceil, stair_z[0], stair_z[1])
        # Tiled, because a stairwell is, and because it is the liveliest room in the building — which
        # is worth having on a map whose flats are all carpet-less concrete boxes anyway. The wall
        # between it and the flat beyond is the partition the loop above already laid; a second one
        # here was two walls in the same place.
        box("tile_floor", sx0, sx1, floor_top - 0.02, floor_top + 0.03, stair_z[0], stair_z[1])
        # Its way out is the doorway the corridor wall was cut for, one slot along like every flat's —
        # with no leaf in it, because a stairwell does not have a front door on the inside.
        portal(near_wall_x + WALL_T / 2, floor_top + 1.0,
               (stair_z[0] + stair_z[1]) / 2, stair_id, corridor_id, 1.4)

        # The flight up to the next storey: real steps, each one under the player's step height, so
        # the building is walked rather than teleported through.
        if s + 1 < STOREYS:
            steps = int(round(STOREY / STAIR_RISE))
            run = steps * STAIR_GOING
            base_z = stair_z[0] + 0.4
            cx = (sx0 + sx1) / 2
            for k in range(steps):
                box("concrete_floor", cx - STAIR_W / 2, cx + STAIR_W / 2,
                    floor_top, floor_top + (k + 1) * STAIR_RISE,
                    base_z + k * STAIR_GOING, base_z + (k + 1) * STAIR_GOING)
            # The hole the stairs come up through: the slab above is cut back over the flight.
            # Written as two slabs either side of the void rather than one with a gap, because a
            # generated map cannot subtract.
            void0, void1 = base_z - 0.3, base_z + run + 0.3
            box("concrete_floor", sx0, sx1, ceil, ceil + SLAB, stair_z[0], void0)
            box("concrete_floor", sx0, sx1, ceil, ceil + SLAB, void1, stair_z[1])

        # The entrance, ground floor only: a doorway in the street face of the stairwell.
        if s == 0:
            ex = street_x
            door(ex - side * WALL_T / 2, 0.02, (stair_z[0] + stair_z[1]) / 2, stair_id, -1,
                 facing_z=True, prefab="steel_door")

    # The roof, so the top storey has a ceiling that is not the sky.
    box("concrete_floor", x0, x1, STOREYS * STOREY, STOREYS * STOREY + SLAB, z0, z1, name=f"{label} roof")

apartment_block(+1, WEST_X0, WEST_X1, WEST_Z0, WEST_Z1, "Westside")
apartment_block(-1, EAST_X0, EAST_X1, EAST_Z0, EAST_Z1, "Eastside")

# ══ The tunnel ════════════════════════════════════════════════════════════════════════════════════
#
# A roofed box with two openings, and the best test the enclosure survey has: long, hard, closed on
# four faces and open on two. If a tunnel does not read as the most enclosed place on the map with
# the longest decay, the survey is wrong.
box("asphalt_road", -KERB_X, KERB_X, 0.0, 0.05, TUNNEL_Z0, TUNNEL_Z1)
# Walls half a metre thick, not three and a half. A tunnel bored out of solid rock would be the
# latter, and what a listener stands next to either way is a face — but a part is only credited to a
# face it is near, so a wall whose far side is metres away is a wall the survey has to reach for.
box("concrete_wall", -KERB_X - 0.5, -KERB_X, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel west wall")
box("concrete_wall", KERB_X, KERB_X + 0.5, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel east wall")
box("concrete_wall", -WALK_X, -KERB_X - 0.5, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1)
box("concrete_wall", KERB_X + 0.5, WALK_X, 0.0, TUNNEL_H, TUNNEL_Z0, TUNNEL_Z1)
box("concrete_floor", -WALK_X, WALK_X, TUNNEL_H, TUNNEL_H + 0.4, TUNNEL_Z0, TUNNEL_Z1, name="Tunnel roof")
for i in range(3):
    zz0 = TUNNEL_Z0 + i * (TUNNEL_Z1 - TUNNEL_Z0) / 3
    zz1 = TUNNEL_Z0 + (i + 1) * (TUNNEL_Z1 - TUNNEL_Z0) / 3
    region(f"Tunnel, {['south', 'middle', 'north'][i]} third", -KERB_X, KERB_X, 0.0, TUNNEL_H, zz0, zz1)

# ══ The parking garage ════════════════════════════════════════════════════════════════════════════
#
# Low, hard and open down one side: the second-best survey test, and the case the wet/dry law has
# never been heard in. Two decks on columns, a solid back and ends, and nothing but columns between
# it and the street.
for lv in range(GARAGE_LEVELS):
    y0 = lv * (GARAGE_CLEAR + SLAB)
    # The ground deck's slab is laid ON the ground, not level with it. At `y0 - SLAB .. y0` its top
    # tied with the map's dirt at exactly 0.0, and a tie goes to whichever box the probe tested first
    # — so level 0 measured a DIRT floor (absorption 0.60) where level 1 measured concrete (0.02).
    # Two decks of the same car park came out 615 ms and 4557 ms.
    if lv == 0:
        box("concrete_floor", WEST_X0, WEST_X1, 0.0, SLAB, GARAGE_Z0, GARAGE_Z1)
    else:
        box("concrete_floor", WEST_X0, WEST_X1, y0 - SLAB, y0, GARAGE_Z0, GARAGE_Z1)
    box("concrete_wall", WEST_X0, WEST_X0 + 0.3, y0, y0 + GARAGE_CLEAR, GARAGE_Z0, GARAGE_Z1)
    box("concrete_wall", WEST_X0, WEST_X1, y0, y0 + GARAGE_CLEAR, GARAGE_Z0, GARAGE_Z0 + 0.3)
    box("concrete_wall", WEST_X0, WEST_X1, y0, y0 + GARAGE_CLEAR, GARAGE_Z1 - 0.3, GARAGE_Z1)
    # Columns down the open side, on a 6 m grid.
    n = int((GARAGE_Z1 - GARAGE_Z0) // 6)
    for k in range(n + 1):
        cz = GARAGE_Z0 + k * 6.0
        box("concrete_wall", WEST_X1 - 0.4, WEST_X1, y0, y0 + GARAGE_CLEAR, cz - 0.2, cz + 0.2)
    region(f"Parking garage, level {lv}", WEST_X0, WEST_X1, y0, y0 + GARAGE_CLEAR, GARAGE_Z0, GARAGE_Z1)
box("concrete_floor", WEST_X0, WEST_X1, GARAGE_LEVELS * (GARAGE_CLEAR + SLAB) - SLAB,
    GARAGE_LEVELS * (GARAGE_CLEAR + SLAB), GARAGE_Z0, GARAGE_Z1, name="Garage roof")
# A roller shutter across the ramp end, so there is one steel face in the city.
box("metal_wall", WEST_X1 - 0.1, WEST_X1, 0.0, GARAGE_CLEAR, GARAGE_Z1 - 6.2, GARAGE_Z1 - 0.3,
    name="Garage shutter")

# ══ The bus shelter ═══════════════════════════════════════════════════════════════════════════════
#
# Three glass sides and a roof, open to the street. Four covered faces out of six by the survey's own
# rule, so it should come back as enclosed-but-barely — which is exactly what standing in one is.
SH_Z0, SH_Z1 = -3.0, 1.4
box("glass_wall", WALK_X - 0.1, WALK_X, 0.0, 2.4, SH_Z0, SH_Z1, name="Shelter back")
box("glass_wall", KERB_X + 0.3, WALK_X, 0.0, 2.4, SH_Z0, SH_Z0 + 0.06)
box("glass_wall", KERB_X + 0.3, WALK_X, 0.0, 2.4, SH_Z1 - 0.06, SH_Z1)
box("metal_wall", KERB_X + 0.3, WALK_X, 2.4, 2.5, SH_Z0, SH_Z1, name="Shelter roof")
# NO REGION. A bus shelter is not a room, it is street furniture, and calling it one is what made it
# measure a two-and-a-half-second tail: the survey's rays leave through the open front, cross the
# street, hit the building opposite, and come back recorded as this three-metre box's own hard walls.
# Its surface measured 612 m^2 against a true 65.
#
# The listener stays in the STREET under it, which is what standing in a shelter is — you hear the
# street, with a pane of glass a metre from your ear, and the near-field probes already give you that
# (reported as working: "only when I get close to walls do I hear the proximity, which is good").
# The thing a small enclosure inside a big one needs is a survey that knows a ray has LEFT, and that
# is real work: keyed to the median it shrinks a car park, keyed to the mean free path it does not
# move a shelter, and settling it does both. Not guessed at here.

# ══ The metro platform ════════════════════════════════════════════════════════════════════════════
#
# A tiled platform under a canopy, beside an open track bed. Tile is the least absorbent surface in
# the table and the canopy covers only the top, so this should read as the LIVEST place on the map
# that is not the tunnel — and it is the one a train will later stop at.
box("concrete_floor", PLAT_X0, PLAT_X0 + PLAT_W, 0.0, PLAT_Y, PLAT_Z0, PLAT_Z1, name="Platform")
box("tile_floor", PLAT_X0, PLAT_X0 + PLAT_W, PLAT_Y - 0.02, PLAT_Y + 0.03, PLAT_Z0, PLAT_Z1)
box("brick_wall", PLAT_X0 - 0.35, PLAT_X0, 0.0, 4.0, PLAT_Z0, PLAT_Z1, name="Platform wall")
box("gravel_bed" if "gravel_bed" in BASE else "dirt_floor",
    PLAT_X0 + PLAT_W, PLAT_X0 + PLAT_W + TRACK_W, -0.1, 0.05, PLAT_Z0, PLAT_Z1, name="Track bed")
# The overhang: a canopy on columns, covering the platform and half the track.
box("metal_wall", PLAT_X0 - 0.35, PLAT_X0 + PLAT_W + 3.0, PLAT_ROOF, PLAT_ROOF + 0.15, PLAT_Z0, PLAT_Z1,
    name="Platform canopy")
for k in range(int((PLAT_Z1 - PLAT_Z0) // 7) + 1):
    cz = PLAT_Z0 + k * 7.0
    box("concrete_wall", PLAT_X0 + PLAT_W - 0.6, PLAT_X0 + PLAT_W - 0.2, PLAT_Y, PLAT_ROOF, cz - 0.2, cz + 0.2)
for k in range(3):
    zz0 = PLAT_Z0 + k * (PLAT_Z1 - PLAT_Z0) / 3
    zz1 = PLAT_Z0 + (k + 1) * (PLAT_Z1 - PLAT_Z0) / 3
    region(f"Metro platform, {['south', 'middle', 'north'][k]} end",
           PLAT_X0, PLAT_X0 + PLAT_W, PLAT_Y, PLAT_ROOF, zz0, zz1)

# ══ Street trees ══════════════════════════════════════════════════════════════════════════════════
#
# A row of them down each pavement. Foliage is the extreme of the Audience's pair of numbers — almost
# everything that goes in comes back out in every direction, and the higher the frequency the less of
# it comes back at all — so a treed street should measurably lose the top of a pass-by. Not solid:
# you can walk through a hedge, and more to the point sound can.
for k in range(14):
    tz = -34.0 + k * 7.0
    box("foliage_hedge", -WALK_X + 0.4, -WALK_X + 2.0, 0.0, 4.5, tz - 0.8, tz + 0.8)
    box("foliage_hedge", WALK_X - 2.0, WALK_X - 0.4, 0.0, 4.5, tz + 3.5 - 0.8, tz + 3.5 + 0.8)

# ══ Zones over the open ground ════════════════════════════════════════════════════════════════════
#
# A street is a place and wants a name as much as a room does. None of these is indoors; the survey
# will find one or two covered faces and say so, which is the point — "Outside" is not a location.
for k in range(6):
    zz0 = -36.0 + k * 18.0
    zz1 = zz0 + 18.0
    region(f"Main Street, block {k + 1}", -KERB_X, KERB_X, 0.0, 6.0, zz0, zz1)
    region(f"West pavement, block {k + 1}", -WALK_X, -KERB_X, 0.0, 4.0, zz0, zz1)
    region(f"East pavement, block {k + 1}", KERB_X, WALK_X, 0.0, 4.0, zz0, zz1)

map_data = {
    "Id": "city",
    "Description": "One city block: two apartment buildings with insides, a street between them, a "
                   "tunnel at the south end, a parking garage, a bus shelter and a metro platform. "
                   "Static — no traffic yet. Generated by tools/gen_city.py.",
    "Size": v3(MAP_MAX[0] - MAP_MIN[0], 40, MAP_MAX[2] - MAP_MIN[2]),
    "MinBound": v3(*MAP_MIN),
    "MaxBound": v3(*MAP_MAX),
    "MinimumY": -20.0,
    "SpawnPoint": {"Position": v3(*SPAWN), "Rotation": {"X": 0, "Y": 0, "Z": 0, "W": 1}},
    # No ambience bed. See no-ambience-beds: a recorded loop has no source, no distance and no
    # geometry, and on a map built to demonstrate exactly those three it buries all of them.
    "AmbienceId": "",
    # A warm, dry afternoon. Temperature and Humidity are read as OFFSETS from the sim's baselines
    # (20 C / 0.5), and the world's calendar starts on day 1 — deep winter, which on the seasonal
    # curve is about -5 C before the daily swing. Plus twelve degrees, which is what this said, still
    # let the cold end of the day fall under +2, and under +2 with any precipitation the client swaps
    # every footstep for SNOW: reported as "I hear snow so I think the weather keeps randomly changing
    # on me". Twenty-two puts the whole daily swing comfortably clear of freezing.
    #
    # Fronts still roll in on their own (WorldEnvironmentSystem, about one a minute). To hold the
    # weather still for a listening test, set OPENFPS_WEATHER.
    "Temperature": 42.0,
    "Humidity": 0.40,
    "VoxelResolution": 0.5,
    "OcclusionFloor": 0.1,
    "Entities": entities,
}

with open(OUT, "w") as f:
    json.dump(map_data, f, indent=1)

regions = sum(1 for e in entities if e["PrefabId"] == "acoustic_region")
portals = sum(1 for e in entities if e["PrefabId"] == "portal")
doors = sum(1 for e in entities if e["PrefabId"] in ("door", "steel_door"))
print(f"{OUT}: {len(entities)} entities — {regions} named places, {portals} portals, {doors} doors")
print(f"  buildings {BUILDING_W:.2f} m deep x {BLOCK_Z:.0f} m, {STOREYS} storeys of {STOREY} m")
print(f"  street {CARRIAGEWAY:.0f} m of asphalt between pavements at x = +/-{WALK_X:.1f}")
print(f"  spawn {SPAWN}, facing north up the street")
