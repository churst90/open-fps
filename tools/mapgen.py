"""
What the map generators share: the prefabs' own dimensions, read rather than remembered, and how a
position and a turn are written. tools/gen_city.py and tools/gen_osm.py both import it.

Run the generators from the repository root; PREFAB_DIR is relative to it.
"""
import glob, json, math, os

PREFAB_DIR = "OpenFPS.Server/prefabs"

# ── The prefabs' own dimensions ───────────────────────────────────────────────────────────────────
#
# Every box is written as the space it occupies, and the scale that produces it is worked out from
# the prefab's collider. Hard-coding those sizes in a generator is the standing way for a generated
# map to drift from the prefabs it is made of.
BASE = {}
HINGED = set()                           # door prefabs whose leaf swings rather than slides
SOLID = {}                               # the server's rule, below
for _path in glob.glob(os.path.join(PREFAB_DIR, "*.json")):
    if _path.endswith("prefab-schema.json"):
        continue
    with open(_path) as _f:
        _p = json.load(_f)
    _c = _p.get("ColliderSize")
    if _c:
        BASE[_p["Id"]] = (_c.get("X", 1.0), _c.get("Y", 1.0), _c.get("Z", 1.0))
    if _p.get("IsDoor") and not _p.get("Slides", _p.get("DoorKind") in ("auto-slide", "patio-slide", "elevator")):
        HINGED.add(_p["Id"])
    # The server's rule (PrefabRepository): a prefab with a collider is solid unless it says it is
    # not. Reading a missing IsSolid as false put the city's Main Street walks through the tunnel's
    # concrete sides (generator-solidity-rule).
    SOLID[_p["Id"]] = "ColliderSize" in _p and bool(_p.get("IsSolid", True))

DOOR_LAP = 0.05                          # how far a leaf overlaps each jamb, m


def v3(x, y, z):
    return {"X": round(x, 4), "Y": round(y, 4), "Z": round(z, 4)}


def yaw(radians):
    """A turn about the vertical. A box's own +X goes to (cos r, -sin r) in (x, z), its +Z to
    (sin r, cos r): x is east and z north, so a positive turn takes east toward south."""
    return {"X": 0.0, "Y": round(math.sin(radians / 2), 6), "Z": 0.0, "W": round(math.cos(radians / 2), 6)}


# ── Openings where two rooms share open air ───────────────────────────────────────────────────────
#
# A room is a name and a point, and its shape is a flood from the point stopped by walls, shut door
# leaves and openings (docs/GEOMETRY.md 2.7). Where two rooms' boxes meet with no wall between them (a
# house's two rectangles over one floor, a stairwell's storeys, the halves of a tunnel), only the boxes
# said where one ended and the other began. An opening there says it without them.

def _qrot(q, v):
    x, y, z, w = q["X"], q["Y"], q["Z"], q["W"]
    vx, vy, vz = v
    # v + 2w(q x v) + 2 q x (q x v)
    cx, cy, cz = y * vz - z * vy, z * vx - x * vz, x * vy - y * vx
    cx2, cy2, cz2 = y * cz - z * cy, z * cx - x * cz, x * cy - y * cx
    return (vx + 2 * (w * cx + cx2), vy + 2 * (w * cy + cy2), vz + 2 * (w * cz + cz2))


def _qmul(a, b):
    ax, ay, az, aw = a["X"], a["Y"], a["Z"], a["W"]
    bx, by, bz, bw = b["X"], b["Y"], b["Z"], b["W"]
    return {"X": aw * bx + ax * bw + ay * bz - az * by, "Y": aw * by - ax * bz + ay * bw + az * bx,
            "Z": aw * bz + ax * by - ay * bx + az * bw, "W": aw * bw - ax * bx - ay * by - az * bz}


def _box(e):
    b = BASE[e["PrefabId"]]
    s = e.get("Scale", {"X": 1.0, "Y": 1.0, "Z": 1.0})
    q = e.get("Rotation") or {"X": 0.0, "Y": 0.0, "Z": 0.0, "W": 1.0}
    p = e["Position"]
    return (p["X"], p["Y"], p["Z"]), (b[0] * s["X"] / 2, b[1] * s["Y"] / 2, b[2] * s["Z"] / 2), q


def _inside(point, box):
    (cx, cy, cz), h, q = box
    inv = {"X": -q["X"], "Y": -q["Y"], "Z": -q["Z"], "W": q["W"]}
    l = _qrot(inv, (point[0] - cx, point[1] - cy, point[2] - cz))
    return all(abs(l[k]) <= h[k] + 1e-4 for k in range(3))


OPEN_FACE_GAP = 2.0       # how far apart two rooms' boxes may be with open air between them, m (a margin each)
OPEN_FACE_MIN = 0.3       # the least side of an opening worth one, m


def open_faces(entities):
    """Openings ("portal" entities with their frame: X across, Y up, Z through, scaled to the gap) wherever two
    rooms' boxes, turned alike, face each other across open air (touching, overlapping, or up to OPEN_FACE_GAP
    apart with no solid layer between) and the rooms are not already joined by a door or a portal. A room is an
    acoustic region under a roof: its top is covered by a solid. Each is (position, rotation, scale, region a,
    region b, width); the caller gives it an id, a tile and a layer. In entity order, so a map gives the same
    openings every time."""
    linked = set()
    for e in entities:
        a, b = e.get("RegionAId"), e.get("RegionBId")
        if a is not None and b is not None and a != b:
            linked.add((min(a, b), max(a, b)))
    solids = [_box(e) for e in entities if SOLID.get(e["PrefabId"], False) and e["PrefabId"] in BASE and "Scale" in e]
    cell = 16.0
    grid = {}
    for i, sb in enumerate(solids):
        (cx, cy, cz), h, _ = sb
        r = math.sqrt(h[0] * h[0] + h[2] * h[2])
        for gx in range(int(math.floor((cx - r) / cell)), int(math.floor((cx + r) / cell)) + 1):
            for gz in range(int(math.floor((cz - r) / cell)), int(math.floor((cz + r) / cell)) + 1):
                grid.setdefault((gx, gz), []).append(i)

    def solid_at(p):
        for i in grid.get((int(math.floor(p[0] / cell)), int(math.floor(p[2] / cell))), ()):
            if _inside(p, solids[i]):
                return True
        return False

    def world(box, l):
        c, _, q = box
        g = _qrot(q, l)
        return (c[0] + g[0], c[1] + g[1], c[2] + g[2])

    def covered(box):
        _, h, _ = box
        hits = 0
        for fx, fz in ((0, 0), (-0.5, -0.5), (0.5, -0.5), (-0.5, 0.5), (0.5, 0.5)):
            if any(solid_at(world(box, (fx * h[0], h[1] + dy, fz * h[2]))) for dy in (-0.2, 0.0, 0.2, 0.5, 1.0, 2.0, 3.0)):
                hits += 1
        return hits >= 3

    rooms = [e for e in entities if e["PrefabId"] == "acoustic_region" and e.get("IsIndoor", True) is not False]
    boxes = [(e, _box(e)) for e in rooms]
    boxes = [(e, b) for e, b in boxes if covered(b)]
    rgrid = {}
    for i, (e, ((cx, cy, cz), h, _)) in enumerate(boxes):
        r = math.sqrt(h[0] * h[0] + h[2] * h[2]) + OPEN_FACE_GAP
        for gx in range(int(math.floor((cx - r) / cell)), int(math.floor((cx + r) / cell)) + 1):
            for gz in range(int(math.floor((cz - r) / cell)), int(math.floor((cz + r) / cell)) + 1):
                rgrid.setdefault((gx, gz), []).append(i)
    pairs = set()
    for ids in rgrid.values():
        for x in range(len(ids)):
            for y in range(x + 1, len(ids)):
                pairs.add((min(ids[x], ids[y]), max(ids[x], ids[y])))
    out = []
    for i, j in sorted(pairs):
        ea, (ca, ha, qa) = boxes[i]
        eb, (cb, hb, qb) = boxes[j]
        if (min(ea["EntityId"], eb["EntityId"]), max(ea["EntityId"], eb["EntityId"])) in linked:
            continue
        dot = qa["X"] * qb["X"] + qa["Y"] * qb["Y"] + qa["Z"] * qb["Z"] + qa["W"] * qb["W"]
        if abs(dot) < 0.99999:
            continue
        inv = {"X": -qa["X"], "Y": -qa["Y"], "Z": -qa["Z"], "W": qa["W"]}
        l = _qrot(inv, (cb[0] - ca[0], cb[1] - ca[1], cb[2] - ca[2]))
        lo = [max(-ha[k], l[k] - hb[k]) for k in range(3)]
        hi = [min(ha[k], l[k] + hb[k]) for k in range(3)]
        over = [hi[k] - lo[k] for k in range(3)]
        # Facing each other across one axis (apart by up to the gap, or overlapping), and over the other two.
        apart = [k for k in range(3) if over[k] < OPEN_FACE_MIN]
        if len(apart) > 1:
            continue
        k = apart[0] if apart else min(range(3), key=lambda a: over[a])
        if over[k] < -OPEN_FACE_GAP:
            continue
        others = [a for a in range(3) if a != k]
        if any(over[a] < OPEN_FACE_MIN for a in others):
            continue
        g0, g1 = (hi[k], lo[k]) if over[k] < 0 else (lo[k], hi[k])   # the gap (or the overlap) along k
        if k == 2:
            turn, across, up = {"X": 0.0, "Y": 0.0, "Z": 0.0, "W": 1.0}, 0, 1
        elif k == 0:
            turn, across, up = {"X": 0.0, "Y": math.sin(math.pi / 4), "Z": 0.0, "W": math.cos(math.pi / 4)}, 2, 1
        else:
            turn, across, up = {"X": -math.sin(math.pi / 4), "Y": 0.0, "Z": 0.0, "W": math.cos(math.pi / 4)}, 0, 2
        w, h = over[across], over[up]

        def layer(depth):
            pts = []
            for fa in (0.5, 0.15, 0.85):
                for fb in (0.5, 0.15, 0.85):
                    pl = [0.0, 0.0, 0.0]
                    pl[k] = depth
                    pl[across] = lo[across] + fa * w
                    pl[up] = lo[up] + fb * h
                    pts.append(world((ca, ha, qa), tuple(pl)))
            return sum(1 for p in pts if solid_at(p))
        # Open air across: no layer of the gap mostly solid (a wall between two rooms, or two buildings' walls).
        # Where they overlap (a stairwell's storeys run through the slab), the slab with its stair's hole is the
        # opening: solid in it anyway.
        if over[k] < 0:
            depths = [g0 + 0.05, (g0 + g1) / 2, g1 - 0.05] if g1 - g0 > 0.1 else [(g0 + g1) / 2]
            if any(layer(d) >= 7 for d in depths):
                continue
        elif layer((g0 + g1) / 2) == 9:
            continue
        mid = [(lo[a] + hi[a]) / 2 for a in range(3)]
        mid[k] = (g0 + g1) / 2
        pos = world((ca, ha, qa), tuple(mid))
        rot = _qmul(qa, turn)
        base = BASE["portal"]
        scale = {"X": round(w / base[0], 4), "Y": round(h / base[1], 4), "Z": 1.0}
        out.append((pos, {key: round(v, 6) for key, v in rot.items()}, scale, ea["EntityId"], eb["EntityId"], round(w, 3)))
    return out
