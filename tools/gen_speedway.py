#!/usr/bin/env python3
"""
Generates OpenFPS.Server/maps/speedway.json — a one-mile banked oval with a grandstand.

The shape is computed rather than typed, because every wall segment on a curve needs its own
rotation and getting one of a hundred of them wrong is a reflection that comes back off nothing.
Run it again after changing a dimension; the map is an OUTPUT, and the numbers below are the source.
"""
import json, math

# ── Dimensions ───────────────────────────────────────────────────────────────────────────────────
#
# Modelled on the 1.25-mile egg at St Louis: turns 1 and 2 are noticeably TIGHTER than 3 and 4, which
# is the whole character of the place. A car that is flat through 3 and 4 has to lift for 1 and 2, so
# the field strings out differently at each end of the lap instead of circulating as one block — and
# from the infield you hear that as the pack arriving loose at one end and stacked at the other.
#
# A symmetric oval cannot do that, which is why this is not one.
R12      = 137.0     # turns 1-2 centreline radius, m (the tight end)
R34      = 183.0     # turns 3-4 centreline radius, m (the wide end)
D        = 501.0     # distance between the two turn centres, m -> lap 2011 m (1.25 miles)
WIDTH    = 18.0      # track width, m
APRON    = 2.0       # from the track edge to the foot of the wall, m
WALL_H   = 3.5       # retaining wall height, m
WALL_T   = 0.6       # retaining wall thickness, m
# The bank angle the turns are BUILT at, and the number the racing line needs.
#
# A centreline is a single line of points and the bank is a CROSS-slope, which a single line cannot
# describe — so it has to be declared. Leaving it unsaid made every car lift 16-23 % for the turns,
# three to four and a half semitones of rev drop, heard as the whole field slowing down. See
# RaceLine.CorneringSpeed.
#
# Ten degrees, not the twenty-four of a superspeedway. This track is flat enough that the cars DO
# have to lift for the tight end — about 220 km/h against a 300 top speed — and that is the point:
# a lap with shape in it, arrived at from the geometry rather than written down.
BANK_DEG = 10.0
BANK_Y   = WIDTH * math.tan(math.radians(BANK_DEG)) * 0.5   # centreline rise through a turn, m

OUT_R    = WIDTH / 2 + APRON            # 11.0 m from the centreline to the wall

# ── The shape, from the two circles ──────────────────────────────────────────────────────────────
# The straights are the external tangents to the two turn circles. With unequal radii they are not
# parallel to the axis, and the arcs are not half-circles: the tight end wraps MORE than 180 degrees
# and the wide end less. All of that falls out of one number.
_K       = (R34 - R12) / D                       # sine of the tangent's angle to the centre line
_NZ      = math.sqrt(1.0 - _K * _K)
C12      = ( D / 2.0, 0.0)                       # turn 1-2 centre
C34      = (-D / 2.0, 0.0)                       # turn 3-4 centre
STRAIGHT = math.sqrt(D * D - (R34 - R12) ** 2)   # both straights, m

# Grandstand, behind the front straight.
#
# The setback is measured from the FURTHEST point of the wall, not the nearest, and that distinction
# is worth a paragraph because getting it wrong put a building on the racetrack.
#
# The front straight is the z < 0 external tangent between two circles of DIFFERENT radii, so it is
# not parallel to the x axis: its outward normal is (K, -NZ), and the wall that follows it runs from
# z = -NZ*(R12 + OUT_R) at the tight end to z = -NZ*(R34 + OUT_R) at the wide end — forty-six metres
# apart. The grandstand is a row of axis-parallel boxes at one fixed z, so a setback taken from the
# near end is a setback the far end does not get: the old value put the deck's front face twenty-eight
# metres INSIDE the wall line, and the last hundred and twenty metres of the straight ran THROUGH the
# building. Every car spent two seconds a lap inside a solid box, which the occlusion model, quite
# correctly, rendered as silence.
#
# Taking the extreme leaves the near end further back than it needs to be, which costs nothing, and
# guarantees the clearance everywhere — which is the only property worth having. The map validator
# (TrackClearance, run at map load) now fails a map where this is got wrong, on any map.
GS_FRONT = -(_NZ * (R34 + OUT_R) + 17.0)
GS_BACK  = GS_FRONT - 16.0
GS_TOP   = 12.0
GS_BLOCKS = 12
GS_HALF_X = 240.0
UPPER_Z  = GS_BACK - 2.0                # the wall behind the seats. THE reflector.
UPPER_BASE = 0.0
UPPER_H  = 36.0

# YOU STAND IN THE INFIELD, not on the stand.
#
# The middle of the oval is the only place a listener is surrounded: cars pass on every side over a
# lap, the near wall is different in each direction, and nothing is ever behind you for long. From
# the grandstand every car is in front and every reflection comes off the same wall behind your head,
# which is a much easier problem and a much worse demonstration.
SPAWN    = (0.0, 0.6, 0.0)

# ── Banking: elevation as a function of distance round the lap ────────────────────────────────────
# Zero on the straights, BANK_Y through the middle of each turn, smoothstepped over the first and
# last sixth of the turn so a car is never asked to climb a step.
def smoothstep(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)

def elevation_for_turn_fraction(f):
    ramp = 0.18
    if f < ramp:      return BANK_Y * smoothstep(f / ramp)
    if f > 1 - ramp:  return BANK_Y * smoothstep((1 - f) / ramp)
    return BANK_Y

# ── The centreline, as a loop of (x, y, z) ────────────────────────────────────────────────────────
#
# Walked as: front straight -> turns 1/2 (tight) -> back straight -> turns 3/4 (wide). Start/finish
# is the middle of the front straight. A left-hand oval, the way ovals run.
#
# The tangent points come from the outward normal n = (K, +/-NZ): the straight touches each circle
# where that normal points, which is what makes the join smooth at BOTH ends despite the radii being
# different. Getting this wrong shows up as a kink, and a kink in a racing line is a car that brakes
# for nothing.
def _tangent_points():
    nlo = (_K, -_NZ)          # front side (z < 0)
    nhi = (_K, +_NZ)          # back side  (z > 0)
    p12lo = (C12[0] + R12 * nlo[0], C12[1] + R12 * nlo[1])
    p34lo = (C34[0] + R34 * nlo[0], C34[1] + R34 * nlo[1])
    p12hi = (C12[0] + R12 * nhi[0], C12[1] + R12 * nhi[1])
    p34hi = (C34[0] + R34 * nhi[0], C34[1] + R34 * nhi[1])
    return p34lo, p12lo, p12hi, p34hi

def _arc(centre, radius, a_from, a_to, n, bank):
    """Points along an arc, sweeping a_from -> a_to the short way in the given direction."""
    out = []
    for i in range(n):
        f = i / n
        a = a_from + (a_to - a_from) * f
        out.append((centre[0] + radius * math.cos(a),
                    elevation_for_turn_fraction(f) if bank else 0.0,
                    centre[1] + radius * math.sin(a)))
    return out

def _lerp_line(p, q, n):
    return [(p[0] + (q[0] - p[0]) * i / n, 0.0, p[1] + (q[1] - p[1]) * i / n) for i in range(n)]

def centreline(n_straight=34, n_turn=64):
    p34lo, p12lo, p12hi, p34hi = _tangent_points()
    # Angles of the tangent points on each circle.
    a12lo = math.atan2(p12lo[1] - C12[1], p12lo[0] - C12[0])
    a12hi = math.atan2(p12hi[1] - C12[1], p12hi[0] - C12[0])
    a34hi = math.atan2(p34hi[1] - C34[1], p34hi[0] - C34[0])
    a34lo = math.atan2(p34lo[1] - C34[1], p34lo[0] - C34[0])

    # Turns 1-2 sweep the +x side, going anticlockwise from the front tangent to the back one.
    if a12hi < a12lo: a12hi += 2 * math.pi
    # Turns 3-4 sweep the -x side, from the back tangent round to the front one.
    if a34lo < a34hi: a34lo += 2 * math.pi

    pts = []
    mid = ((p34lo[0] + p12lo[0]) * 0.5, (p34lo[1] + p12lo[1]) * 0.5)   # start/finish
    pts += _lerp_line(mid, p12lo, n_straight // 2)
    pts += _arc(C12, R12, a12lo, a12hi, n_turn, bank=True)
    pts += _lerp_line(p12hi, p34hi, n_straight)
    pts += _arc(C34, R34, a34hi, a34lo, n_turn, bank=True)
    pts += _lerp_line(p34lo, mid, n_straight // 2)
    return pts

def v3(x, y, z):  return {"X": round(x, 3), "Y": round(y, 3), "Z": round(z, 3)}

def yaw_quat(yaw):
    return {"X": 0.0, "Y": round(math.sin(yaw / 2), 6), "Z": 0.0, "W": round(math.cos(yaw / 2), 6)}

# A concrete_wall is 2 x 3 x 0.5 (length along local X, height Y, thickness Z) before scaling, and
# its local +X is rotated to lie along the wall's run: yaw = atan2(-tz, tx).
def wall(eid, cx, cy, cz, length, height, thickness, tx, tz, prefab="concrete_wall"):
    yaw = math.atan2(-tz, tx)
    return {
        "EntityId": eid, "PrefabId": prefab,
        "Position": v3(cx, cy + height / 2, cz),
        "Rotation": yaw_quat(yaw),
        "Scale": v3(length / 2.0, height / 3.0, thickness / 0.5),
    }

entities = []
eid = 1000

# ── The walls, FROM THE CENTRELINE ───────────────────────────────────────────────────────────────
#
# Offset each centreline point sideways by its own local normal and put a wall panel between
# consecutive offsets. Nothing here knows the track is an oval, which is the point: the previous
# version emitted two straights and two half-circle arcs from the same constants the shape was built
# from, so any change to the shape had to be made twice and a road course could not be expressed at
# all. This walks whatever `centreline()` returns.
#
# The wall rides the banking, plus the extra rise from the centreline out to its own foot.
def _normals(pts):
    """Outward (away from the infield) unit normal at each point, in the ground plane."""
    out = []
    n = len(pts)
    for i in range(n):
        ax, _, az = pts[(i - 1) % n]
        bx, _, bz = pts[(i + 1) % n]
        tx, tz = bx - ax, bz - az
        L = math.hypot(tx, tz) or 1.0
        tx, tz = tx / L, tz / L
        # Left-hand oval runs anticlockwise seen from above, so the outward normal is the tangent
        # turned one way; pick the sign that points AWAY from the track's centroid.
        nx, nz = tz, -tx
        px, _, pz = pts[i]
        if nx * px + nz * pz < 0: nx, nz = -nx, -nz
        out.append((nx, nz))
    return out

def _wall_ring(pts, offset, height, thickness, every=3, lift=1.0):
    ring = []
    norms = _normals(pts)
    n = len(pts)
    for i in range(0, n, every):
        j = (i + every) % n
        (ax, ay, az), (anx, anz) = pts[i], norms[i]
        (bx, by, bz), (bnx, bnz) = pts[j], norms[j]
        p = (ax + anx * offset, ay, az + anz * offset)
        q = (bx + bnx * offset, by, bz + bnz * offset)
        cx, cz = (p[0] + q[0]) / 2, (p[2] + q[2]) / 2
        base = (p[1] + q[1]) / 2 * lift + offset * math.tan(math.radians(BANK_DEG)) * (0.0 if ay == 0 else 1.0)
        length = math.dist((p[0], p[2]), (q[0], q[2])) + 0.5
        ring.append((cx, base, cz, length, height, thickness, q[0] - p[0], q[2] - p[2]))
    return ring

_cl = centreline()
LAP_LEN = sum(math.dist(_cl[i], _cl[(i + 1) % len(_cl)]) for i in range(len(_cl)))
for (cx, base, cz, length, height, thickness, tx, tz) in _wall_ring(_cl, OUT_R, WALL_H, WALL_T):
    entities.append(wall(eid, cx, base, cz, length, height, thickness, tx, tz)); eid += 1

# ── Pit wall: low, front straight only, on the INSIDE ────────────────────────────────────────────
for (cx, base, cz, length, height, thickness, tx, tz) in _wall_ring(_cl, -(OUT_R), 0.9, 0.4, every=3):
    if cz > 0: continue                                  # front straight and its turn entries only
    if abs(cx) > STRAIGHT * 0.45: continue
    entities.append(wall(eid, cx, base, cz, length, height, thickness, tx, tz)); eid += 1

# ── Grandstand: a raked deck you stand on, and the wall behind the seats ─────────────────────────
# Split into blocks rather than one 480 m box so the server's 10 m spatial grid carries it properly
# and each face is still a large flat mirror.
blk = 2 * GS_HALF_X / GS_BLOCKS
for i in range(GS_BLOCKS):
    cx = -GS_HALF_X + blk * (i + 0.5)
    entities.append({
        "EntityId": eid, "PrefabId": "building_box",
        "Position": v3(cx, GS_TOP / 2, (GS_FRONT + GS_BACK) / 2),
        "Scale": v3(blk / 10.0, GS_TOP / 5.0, abs(GS_FRONT - GS_BACK) / 10.0),
    }); eid += 1
    # The upper tier's back wall: the big flat concrete face that answers every car on the straight.
    entities.append(wall(eid, cx, UPPER_BASE, UPPER_Z, blk + 0.5, UPPER_H, 1.5, 1.0, 0.0)); eid += 1

# ── Infield grass ────────────────────────────────────────────────────────────────────────────────
# The ground everywhere else is the concrete foundation MapManager injects when a map has none, which
# is right for a speedway: the track, the apron and the paddock are all paved. Only the infield is
# grass, and it is here so a listener who walks down off the stand hears the change under their feet
# and so the reflection off the middle of the oval is a soft one rather than a slab.
for gx in (-1, 0, 1):
    for gz in (-1, 0, 1):
        entities.append({
            "EntityId": eid, "PrefabId": "grass_floor",
            "Position": v3(gx * 90.0, 0.06, gz * 80.0),
            "Scale": v3(9.0, 1.0, 8.0),
        }); eid += 1

# ── The PA, in the infield ───────────────────────────────────────────────────────────────────────
# Four horns on a pole at the middle of the oval, aimed outward at the four compass points, so that
# wherever you stand in the infield one of them is pointing at you and the other three arrive off the
# walls. It says its piece every twenty seconds; the SILENCE is what makes it a landmark you can
# navigate by rather than a drone you stop hearing.
for k, (dx, dz) in enumerate(((0, 1), (1, 0), (0, -1), (-1, 0))):
    entities.append({
        "EntityId": eid, "PrefabId": "pa_speaker",
        "Position": v3(dx * 1.2, 6.0, dz * 1.2),
        "Rotation": yaw_quat(math.atan2(dx, dz)),
    }); eid += 1

# ── Pit gear, where you are standing ─────────────────────────────────────────────────────────────
# Two things to pick up, at the foot of the PA pole you spawn beside. Not scenery: a crowbar is five
# kilos of steel and a torch is four hundred grams of plastic, so putting one down on the infield
# grass and the other on the paved apron is four different noises out of one calculation, and the
# difference between them is audible without anybody having recorded any of it.
#
# They are here rather than only on the demo map because this is the map a player actually lands on,
# and a verb you cannot reach is a verb nobody has.
for prefab, ox, oz in (("crowbar", 1.6, -0.9), ("torch", -1.4, -1.1)):
    entities.append({
        "EntityId": eid, "PrefabId": prefab,
        "Position": v3(SPAWN[0] + ox, 0.08, SPAWN[2] + oz),
    }); eid += 1

# ── The field ────────────────────────────────────────────────────────────────────────────────────
# Lane offsets spread the cars across the eighteen metres so they are not in single file; start
# offsets spread them round the lap so the grid is a race in progress rather than a standing start.
# A full field. The point of thirty rather than eight is that the number of cars a map may carry
# should not be a property of the audio engine: only a handful are ever SYNTHESIZED (the nearest,
# decided at run time by measured mixer load), and the rest are voiced by borrowing one of those.
# Thirty costs about what four does and sounds like a race instead of a demonstration.
# The g figures are now TYRE grip, not a fudge. They used to be inflated — 2.90 and 4.00 — because
# the racing line worked the corner speed out for a FLAT track and the only way to stop the cars
# crawling round a 150 m radius was to give them grip no tyre has. The line understands the banking
# now (BANK_DEG below), so these can be what a tyre actually does: a slick with downforce about 1.7,
# a formula car more because its wings make grip out of speed, a street-tyred muscle car under one.
# ONE class of car, because a NASCAR field is one class of car. The variation that makes a field
# string out comes from the per-car jitter below and from the racing line, not from putting three
# different formulae on the same track.
CLASSES = [
    # name       preset        top   g     accel brake
    ("Stock car", "nascar_v8",  300, 1.75, 5.5,  8.0),
]

# ── And a support race, because one class of car tells you nothing about the others ──────────────
#
# Two trucks and two turbo hatches share the circuit with the field. This is not decoration: a map
# with one kind of vehicle on it cannot show that an engine's character comes from its MECHANISM
# rather than from a recording, and it cannot show a turbocharger at all, because there is no
# turbocharged stock car.
#
# What you should be able to hear without being told: the trucks are slower and far lower — a
# 600 rpm idle and a 2100 redline against the stock cars' 9000, so they are an octave and a half
# under everything else and they take an age between shifts. Their turbine whistles on the way up
# and hisses away when the driver lifts. Their tyres give up at three-quarters of a g where a slick
# holds three, so they are the only things on the track that actually squeal in the corners. And
# the turbo hatches sound lazy next to the atmospheric cars at the same speed, because torque at
# three thousand means taller gearing and fewer revs for the same lap.
# Eight MUSCLE CARS join them, and they are the same car four ways.
#
# One V8, four exhausts, nothing else changed between them — same mass, same gearing, same street
# tyres — so anything you can hear between them is the hardware and nothing else. No sample library
# ships the same engine four ways; this is the clearest demonstration the project has that none of
# this is a recording.
#
# What to listen for as they come past:
#   OPEN HEADERS    the primaries dumping straight into the air. No collector, no crossover, no
#                   muffler, no tailpipe — so nothing cancels the harmonics and nothing absorbs
#                   them. It is also the only car out there with no muffler CASE, so it has none of
#                   the metallic ring the chambered cars have. Loudest thing on the circuit at
#                   127 dB, and raw because of what is ABSENT.
#   BIG CAM         7.4 litres on a 330-degree cam. Listen at low revs: it will not idle straight,
#                   because that much overlap has a cylinder breathing its neighbour's exhaust and
#                   the burn goes ragged. The lope is a misfire nobody fixed.
#   GLASSPACK       the same big block, mellowed. Packing ABSORBS the top of the band instead of
#                   cancelling notches out of it, and because the packing is pressed against the
#                   case it damps that too — so the metallic ring goes with it. Mellow here is an
#                   absence, which is why it could not be faked by turning something down.
#   MILD           a small block on cast log manifolds, 112 dB and the quietest thing on the track.
#                   Its primaries are nowhere near equal (spread 0.42 against a header's 0.12) and
#                   eight pipes at eight pitches is a band where eight at one pitch is a tube.
#
# Fifteen decibels separate the open headers from the mild one, and none of that was dialled in:
# every figure came off --engine-levels after the fact.
SUPPORT = [
    # name              preset             top   g     accel brake  count
    # Four big diesels rather than two, because the turbo is the point of them and one passing
    # occasionally is not enough to hear it work. A 13-litre truck six runs 2.2 bar of boost with a
    # whistle level of 0.9 and a shaft that takes 1.4 SECONDS to come up — so you hear it spool on
    # the way out of a corner, hold on the straight, and hiss off when the driver lifts for the
    # next one. It idles at 600 and stops at 2100, an octave and a half under everything else.
    ("Race truck",      "diesel_truck",    160, 0.72, 2.2,  5.5,   4),
    # ...and two small turbo-diesels, which are the SAME mechanism at a different size: 1.4 bar,
    # half the whistle, and a shaft that spools in 0.9 s instead of 1.4. Hearing a big turbo and a
    # small one on the same lap is what makes it read as a turbocharger rather than as a noise.
    ("Diesel pickup",   "diesel_i4",       185, 0.88, 3.0,  6.0,   2),
    ("Turbo hatch",     "i4_turbo",        235, 1.10, 4.4,  7.5,   2),
    ("Open header",     "v8_open_headers", 245, 1.05, 4.8,  6.5,   2),
    ("Big cam",         "v8_bigcam",       235, 1.02, 4.6,  6.5,   2),
    ("Glasspack",       "v8_glasspack",    225, 1.00, 4.3,  6.5,   2),
    ("Mild small block","v8_mild",         200, 0.98, 3.6,  6.5,   2),
]

# ...and one that is not. The pace car runs the same lap in a road car with a bar on the roof: an
# interceptor V8 on a cam that cannot idle straight, open pipes, and half a tonne more to carry. It
# is slower than the field, so it is always being caught, which means you hear it pass you at a
# different closing speed every lap.
PACE = ("Pace car", "police_v8", 250, 1.35, 4.5, 7.0)

NUMBERS = [24, 3, 48, 11, 9, 22, 5, 17, 43, 88, 12, 20, 2, 19, 77, 8, 14, 6, 45, 31]

# FIELD SIZE. Thirty was cut to eight when a full field broke the load, and the reason was never the
# synthesis — four mechanical faults stalled the FMOD mixer thread itself. All fixed; see
# docs/AUDIO_LOAD_DROPOUTS.md. The number of cars a map may carry is not a property of the audio
# engine: only the nearest handful are ever SYNTHESIZED and the rest borrow one of those.
FIELD_SIZE = 30

# The support runners take the LAST slots, so they start at the back of a lap that is already
# spread out and spend their time being caught and passed by the field. That is the point of them:
# a truck at 160 and a stock car at 300 on the same circuit means a closing speed you can hear.
SUPPORT_SLOTS = {}
_slot = FIELD_SIZE - 1
for _name, _preset, _top, _g, _acc, _brk, _count in SUPPORT:
    for _k in range(_count):
        SUPPORT_SLOTS[_slot] = (f"{_name} {_k + 1}", _preset, _top, _g, _acc, _brk)
        _slot -= 1

field = []
for i in range(FIELD_SIZE):
    if i in SUPPORT_SLOTS:
        name, preset, top, g, acc, brk = SUPPORT_SLOTS[i]
    else:
        name, preset, top, g, acc, brk = PACE if i == 0 else CLASSES[0]
    # Each car is its own machine: a per-car spread in top speed and grip is what makes a field
    # string out into traffic instead of circulating as one block.
    jitter = ((i * 7919) % 100) / 100.0 - 0.5          # deterministic, -0.5..+0.5
    field.append((
        name if i == 0 or i in SUPPORT_SLOTS else f"{name} {NUMBERS[i % len(NUMBERS)]}",
        preset,
        top * (1.0 + 0.03 * jitter),
        g * (1.0 + 0.05 * jitter),
        acc, brk,
        # Spread across the racing surface, and round the whole lap.
        round(-6.5 + (i % 5) * 3.25, 2),
        round(i * (LAP_LEN / FIELD_SIZE), 1),
    ))

vehicles = [{
    "Name": name, "Preset": preset, "Track": "oval",
    "TopSpeedKmh": round(top, 1), "CorneringG": round(g, 3),
    "AccelerationMps2": acc, "BrakingMps2": brk,
    "LaneOffsetMetres": lane, "StartOffsetMetres": start,
} for (name, preset, top, g, acc, brk, lane, start) in field]

cl = _cl
lap_len = sum(math.dist(cl[i], cl[(i + 1) % len(cl)]) for i in range(len(cl)))

map_data = {
    "Id": "speedway",
    "IsDefault": True,
    "Description": "St Louis raceway: a 1.25-mile egg, turns 1-2 tighter than 3-4. A full stock-car field, a pace car that cannot keep up, and you standing in the infield.",
    "Size": v3(720, 60, 420),
    "MinBound": v3(-360, 0, -230),
    "MaxBound": v3(360, 60, 200),
    "MinimumY": -20.0,
    "SpawnPoint": {"Position": v3(*SPAWN), "Rotation": {"X": 0, "Y": 0, "Z": 0, "W": 1}},
    "AmbienceId": "",
    # Race day, not a blizzard. Temperature and Humidity are read as OFFSETS from the sim's baselines
    # (20 C / 0.5), and the world's calendar starts on day 1 — deep winter — so a map that authors
    # nothing sits below freezing. The client picks its footstep material from the weather, so an
    # unauthored speedway had everyone walking through snow, and the snow samples then failed to
    # decode in time and left the steps silent. Eighteen degrees over baseline is a warm, dry track.
    "Temperature": 38.0,
    "Humidity": 0.35,
    "VoxelResolution": 1.0,
    "OcclusionFloor": 0.1,
    "Tracks": [{"Id": "oval", "WidthMetres": WIDTH, "BankingDegrees": BANK_DEG,
                "Waypoints": [v3(*p) for p in cl]}],
    "Vehicles": vehicles,
    "Entities": entities,
}

path = "OpenFPS.Server/maps/speedway.json"
with open(path, "w") as f:
    json.dump(map_data, f, indent=1)

print(f"{path}: lap {lap_len:.1f} m, {len(cl)} waypoints, {len(entities)} entities, {len(vehicles)} cars")
print(f"  spawn {SPAWN}  grandstand deck y={GS_TOP} at z={GS_FRONT}..{GS_BACK}, back wall z={UPPER_Z}")
