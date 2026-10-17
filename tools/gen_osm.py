#!/usr/bin/env python3
"""
Generates a map of a REAL place from open data: OpenStreetMap's roads and names, building footprints,
the National Address Database's addresses, and satellite land cover for the trees.

    python3 tools/gen_osm.py tools/places/magnolia_tx                 writes OpenFPS.Server/maps/places/magnolia_tx.json
    python3 tools/gen_osm.py tools/places/magnolia_tx --detail=low    a lighter map (see DETAIL)
    python3 tools/gen_osm.py tools/places/magnolia_tx --out=FILE      writes FILE instead
    python3 tools/gen_osm.py tools/places/magnolia_tx --decisions=FILE  also writes what build() decided for each
                                                                       building (for WorldBuildingsTests)

Run from the repository root. A place is a folder under tools/places with a place.json (what it is
called, its area, where its origin and spawn are) and the inputs tools/fetch_place.py prepared from the
downloads (osm.json, buildings.json, addresses.json, places.json, addrfeat.json, landcover.json,
water.json, area.json). Everything here is the Python standard library, every choice that looks random
is seeded from the thing it is about, and the output is the same file byte for byte every time
(RealPlaceMapTests checks it). See docs/MAPS_FROM_REAL_PLACES.md for the sources and their licences.

WHAT IS REAL AND WHAT IS MADE UP. Real: where every road runs and what it is called, its lanes and
surface where OpenStreetMap says; where every building stands and its footprint and height (Microsoft's
footprints traced from aerial imagery, OpenStreetMap's, and USGS lidar's, via Overture); every address
the National Address Database has, on the building it belongs to; where the woods are (ESA WorldCover,
10 m); gates, the fire station, the shops and churches. Made up, deterministically, from those: the
lot lines (no public parcel data may be redistributed here), the rooms inside a house and its doors,
what the walls are made of, the individual trees, the speed limits where none is mapped (the
state's defaults, from place.json), and the address of a house the address points miss (interpolated
from the Census address ranges, as a geocoder would).

THE ENGINE HAS BOXES. Every footprint is turned into a few rectangles in its own frame (an L-shaped
house is two), walls go round the outside of their union, and a pitched roof is a deck with a ridge on
it. Everything is made on flat ground and then set on the real ground from elevation.json (ground_all,
near the end); without elevation.json the ground stays flat.

DETAIL. The map is layered so that a later loader can stream it by tile and by layer without
regenerating it: every entity carries the 250 m tile it stands in ("Tile", from the origin) and what
layer it belongs to ("Layer"). What is emitted depends on --detail:
    low      roads, zones, building shells with one room and a front door, outbuildings as solid
               boxes, the woods as volumes
    medium   + a floor in every house, front lawns, generated driveways, sparse trunks and the wind
               in the woods, fences and gate posts, traffic. Everything within FullDetailMetres
               (place.json) of the origin, which is the spawn address, is built as at high.
    high     + every house's rooms, inner doors, back door and furniture, outbuildings you can go
               into, verges, back and side lawns, ridged roofs, denser woods
"""
import json, math, os, re, sys, zlib
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mapgen import BASE, HINGED, SOLID, DOOR_LAP, v3, yaw  # noqa: E402

ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
OPTS = dict((a[2:].split("=", 1) + [""])[:2] for a in sys.argv[1:] if a.startswith("--"))
PLACE_DIR = ARGS[0] if ARGS else "tools/places/magnolia_tx"
with open(os.path.join(PLACE_DIR, "place.json")) as _f:
    PLACE = json.load(_f)
DETAIL = OPTS.get("detail") or PLACE.get("Detail", "medium")
LEVEL = {"low": 0, "medium": 1, "high": 2}[DETAIL]
NEAR_M = float(OPTS.get("near-metres") or PLACE.get("FullDetailMetres", 0))
OUT = OPTS.get("out") or f"OpenFPS.Server/maps/places/{PLACE['Id']}.json"
TILE = float(PLACE.get("TileMetres", 250.0))


def near_spawn(x, z):
    """Within FullDetailMetres of the origin, which is where the spawn address is: built as at high."""
    return NEAR_M > 0 and math.hypot(x, z) <= NEAR_M


def detail_at(x, z):
    return 2 if (LEVEL == 1 and near_spawn(x, z)) else LEVEL


def load(name, default=None):
    p = os.path.join(PLACE_DIR, name)
    if not os.path.exists(p):
        return default
    with open(p) as f:
        return json.load(f)


def h01(*keys):
    """A number in [0, 1) from what a thing is: the same house is the same house every run."""
    return (zlib.crc32("|".join(str(k) for k in keys).encode()) & 0xFFFFFFFF) / 4294967296.0


# ══ Where things are ══════════════════════════════════════════════════════════════════════════════
#
# The world's grid (docs/WORLD_STREAMING.md, stage 2): UTM metres of the origin's zone, less the 2 m post
# nearest the origin in place.json, x east and z north along the grid, as the game has them (and /tp and
# the C key report them, with the height last). The world's tiles are 250 m squares of the same grid, so
# the map is the world moved, never turned (OpenFPS.Server/OneWorld/WorldPlaces). Grid north is within
# 1.4 degrees of true north at Magnolia, 0.1 at Albany.
import utm  # noqa: E402

LAT0, LON0 = PLACE["Origin"]
UTM_ZONE, UTM_NORTH, UTM_E0, UTM_N0 = utm.origin_of(LAT0, LON0)


def P(lat, lon):
    e, n = utm.from_latlon(lat, lon, UTM_ZONE, UTM_NORTH)
    return (e - UTM_E0, n - UTM_N0)


# ══ Geometry in the ground plane ══════════════════════════════════════════════════════════════════

def pip(x, z, ring):
    inside = False
    n = len(ring)
    j = n - 1
    for i in range(n):
        xi, zi = ring[i]
        xj, zj = ring[j]
        if (zi > z) != (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi) + xi:
            inside = not inside
        j = i
    return inside


def ring_distance(x, z, ring):
    """How far a point is from a closed ring's edges."""
    best = float("inf")
    for (ax, az), (bx, bz) in zip(ring, ring[1:]):
        dx, dz = bx - ax, bz - az
        t = max(0.0, min(1.0, ((x - ax) * dx + (z - az) * dz) / max(1e-12, dx * dx + dz * dz)))
        best = min(best, math.hypot(x - ax - t * dx, z - az - t * dz))
    return best


def firmly_in(x, z, ring, by=0.001):
    """Inside a ring, and still inside `by` to either side along x and along z."""
    return pip(x, z, ring) and pip(x - by, z, ring) and pip(x + by, z, ring) and pip(x, z - by, ring) and pip(x, z + by, ring)


def seg_point(px, pz, ax, az, bx, bz):
    """(distance, t) from a point to a segment."""
    dx, dz = bx - ax, bz - az
    L2 = dx * dx + dz * dz
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, ((px - ax) * dx + (pz - az) * dz) / L2))
    qx, qz = ax + t * dx, az + t * dz
    return math.hypot(px - qx, pz - qz), t


def plen(pts):
    return sum(math.dist(pts[i], pts[i + 1]) for i in range(len(pts) - 1))


def project(pts, x, z):
    """The nearest point of a polyline: (distance, along, side, tangent, foot). Side +1 is left of
    the line's direction, -1 right."""
    best = None
    along = 0.0
    for i in range(len(pts) - 1):
        (ax, az), (bx, bz) = pts[i], pts[i + 1]
        L = math.dist(pts[i], pts[i + 1])
        if L < 1e-9:
            continue
        d, t = seg_point(x, z, ax, az, bx, bz)
        if best is None or d < best[0]:
            tx, tz = (bx - ax) / L, (bz - az) / L
            cross = tx * (z - az) - tz * (x - ax)
            best = (d, along + t * L, 1 if cross > 0 else -1, (tx, tz), (ax + t * (bx - ax), az + t * (bz - az)))
        along += L
    return best


def point_at(pts, d):
    along = 0.0
    for i in range(len(pts) - 1):
        L = math.dist(pts[i], pts[i + 1])
        if along + L >= d and L > 0:
            t = (d - along) / L
            return (pts[i][0] + t * (pts[i + 1][0] - pts[i][0]), pts[i][1] + t * (pts[i + 1][1] - pts[i][1]))
        along += L
    return pts[-1]


def offset_line(pts, left):
    """A polyline moved sideways, positive to the LEFT of its direction (x east, z north)."""
    out = []
    for i in range(len(pts)):
        a, b = pts[max(0, i - 1)], pts[min(len(pts) - 1, i + 1)]
        dx, dz = b[0] - a[0], b[1] - a[1]
        L = math.hypot(dx, dz) or 1.0
        out.append((pts[i][0] - dz / L * left, pts[i][1] + dx / L * left))
    return out


def simplify(pts, tol):
    """Douglas-Peucker, keeping the ends."""
    if len(pts) < 3:
        return list(pts)
    keep = [False] * len(pts)
    keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    while stack:
        a, b = stack.pop()
        best, bi = -1.0, -1
        for i in range(a + 1, b):
            d, _ = seg_point(pts[i][0], pts[i][1], pts[a][0], pts[a][1], pts[b][0], pts[b][1])
            if d > best:
                best, bi = d, i
        if best > tol:
            keep[bi] = True
            stack.append((a, bi))
            stack.append((bi, b))
    return [p for p, k in zip(pts, keep) if k]


def hull(points):
    pts = sorted(set(points))
    if len(pts) < 3:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lo, hi = [], []
    for p in pts:
        while len(lo) >= 2 and cross(lo[-2], lo[-1], p) <= 0:
            lo.pop()
        lo.append(p)
    for p in reversed(pts):
        while len(hi) >= 2 and cross(hi[-2], hi[-1], p) <= 0:
            hi.pop()
        hi.append(p)
    return lo[:-1] + hi[:-1]


def min_rect(points):
    """The smallest rectangle round some points: (angle of its long side, centre x, centre z, long, short)."""
    h = hull(points)
    best = None
    for i in range(len(h)):
        (ax, az), (bx, bz) = h[i], h[(i + 1) % len(h)]
        a = math.atan2(bz - az, bx - ax)
        c, s = math.cos(a), math.sin(a)
        us = [x * c + z * s for x, z in h]
        vs = [-x * s + z * c for x, z in h]
        area = (max(us) - min(us)) * (max(vs) - min(vs))
        if best is None or area < best[0] - 1e-9:
            best = (area, a, min(us), max(us), min(vs), max(vs))
    _, a, u0, u1, v0, v1 = best
    if v1 - v0 > u1 - u0:
        a += math.pi / 2
        u0, u1, v0, v1 = v0, v1, -u1, -u0
    a = norm_half(a)
    c, s = math.cos(a), math.sin(a)
    cu, cv = (u0 + u1) / 2, (v0 + v1) / 2
    # The centre, recomputed in the normalised frame.
    us = [x * c + z * s for x, z in h]
    vs = [-x * s + z * c for x, z in h]
    cu, cv = (min(us) + max(us)) / 2, (min(vs) + max(vs)) / 2
    return a, cu * c - cv * s, cu * s + cv * c, max(us) - min(us), max(vs) - min(vs)


def norm_half(a):
    """An angle folded into (-pi/2, pi/2]: a box turned half round is the same box."""
    while a > math.pi / 2 + 1e-12:
        a -= math.pi
    while a <= -math.pi / 2 + 1e-12:
        a += math.pi
    return a


class Frame:
    """A frame in the ground plane: u along `a` (radians from +x toward +z), v a quarter turn on."""
    __slots__ = ("ox", "oz", "a", "c", "s")

    def __init__(self, ox, oz, a):
        self.ox, self.oz, self.a = ox, oz, a
        self.c, self.s = math.cos(a), math.sin(a)

    def w(self, u, v):
        return (self.ox + u * self.c - v * self.s, self.oz + u * self.s + v * self.c)

    def l(self, x, z):
        dx, dz = x - self.ox, z - self.oz
        return (dx * self.c + dz * self.s, -dx * self.s + dz * self.c)


WORLD = Frame(0.0, 0.0, 0.0)


def bbox_of(F, u0, u1, v0, v1):
    cs = [F.w(u, v) for u in (u0, u1) for v in (v0, v1)]
    return (min(c[0] for c in cs), max(c[0] for c in cs), min(c[1] for c in cs), max(c[1] for c in cs))


def ring_in_box(ring, u0, u1, v0, v1):
    """The part of a ring (points (u, v) in a box's frame, not closed) inside the box, by clipping it to each side
    in turn; its corners, which bound it as tightly as the part itself."""
    poly = list(ring)
    for k, s, side in ((0, 1, u0), (0, -1, u1), (1, 1, v0), (1, -1, v1)):
        out = []
        for i in range(len(poly)):
            p, q = poly[i], poly[(i + 1) % len(poly)]
            dp, dq = s * (p[k] - side), s * (q[k] - side)
            if dp >= 0:
                out.append(p)
            if (dp >= 0) != (dq >= 0):
                t = dp / (dp - dq)
                out.append((p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t))
        poly = out
        if not poly:
            break
    return poly


def poly_area(poly):
    n = len(poly)
    return abs(sum(poly[i][0] * poly[(i + 1) % n][1] - poly[(i + 1) % n][0] * poly[i][1] for i in range(n))) / 2 if n >= 3 else 0.0


CLEAR_GAP = 0.02          # how far a surface laid along a line stops short of a building's wall, m
CLEAR_LEAST = 0.3         # the shortest or narrowest piece of one worth keeping, m


def cut_off_rings(u0, u1, v0, v1, rings):
    """A box along a line (u along it, v across) cut back off every footprint it enters (rings in the box's frame):
    for each in turn, the cut that keeps the most of it, shorter from either end or with a gap where it passes
    through (in u), or narrower from either side (in v). The pieces left, each clear of every ring."""
    pieces = [(u0, u1, v0, v1)]
    for ring in rings:
        out = []
        for a0, a1, b0, b1 in pieces:
            part = ring_in_box(ring, a0, a1, b0, b1)
            if poly_area(part) <= 1e-4:
                out.append((a0, a1, b0, b1))
                continue
            pu0, pu1 = min(p[0] for p in part), max(p[0] for p in part)
            pv0, pv1 = min(p[1] for p in part), max(p[1] for p in part)
            cuts = [[c for c in ((a0, pu0 - CLEAR_GAP, b0, b1), (pu1 + CLEAR_GAP, a1, b0, b1)) if c[1] - c[0] >= CLEAR_LEAST]]
            if pv0 - CLEAR_GAP - b0 >= CLEAR_LEAST:
                cuts.append([(a0, a1, b0, pv0 - CLEAR_GAP)])
            if b1 - pv1 - CLEAR_GAP >= CLEAR_LEAST:
                cuts.append([(a0, a1, pv1 + CLEAR_GAP, b1)])
            out.extend(max(cuts, key=lambda cut: sum((c[1] - c[0]) * (c[3] - c[2]) for c in cut)))
        pieces = out
    return pieces


def line_to_ring(line, ring):
    """How far a polyline comes to a closed ring: 0 where they cross or a point of the line is inside it."""
    best = float("inf")
    for x, z in ring:
        best = min(best, project(line, x, z)[0])
    for i in range(len(line)):
        if pip(line[i][0], line[i][1], ring):
            return 0.0
        for k in range(len(ring) - 1):
            best = min(best, seg_point(line[i][0], line[i][1], *ring[k], *ring[k + 1])[0])
            if i < len(line) - 1 and _crosses(line[i], line[i + 1], ring[k], ring[k + 1]):
                return 0.0
    return best


def _crosses(a, b, c, d):
    def cr(o, p, q):
        return (p[0] - o[0]) * (q[1] - o[1]) - (p[1] - o[1]) * (q[0] - o[0])
    d1, d2, d3, d4 = cr(a, b, c), cr(a, b, d), cr(c, d, a), cr(c, d, b)
    return ((d1 > 0) != (d2 > 0)) and ((d3 > 0) != (d4 > 0))


def largest_rect(mask, nu, nv):
    """The largest all-true rectangle in a grid: (area, i0, i1, j0, j1), i along u, j along v."""
    heights = [0] * nu
    best = (0, 0, 0, 0, 0)
    for j in range(nv):
        row = mask[j]
        for i in range(nu):
            heights[i] = heights[i] + 1 if row[i] else 0
        stack = []
        for i in range(nu + 1):
            hgt = heights[i] if i < nu else 0
            start = i
            while stack and stack[-1][1] >= hgt:
                si, sh = stack.pop()
                area = sh * (i - si)
                if area > best[0]:
                    best = (area, si, i, j - sh + 1, j + 1)
                start = si
            stack.append((start, hgt))
    return best


def cover(ring, a, cell, max_rects, min_area, stop_frac=0.06):
    """Rectangles, in a frame at angle `a` through the origin, covering a polygon: the biggest first,
    until what is left is a sliver. The grid is laid on the polygon's own bounding box in that frame
    and the cell stretched to divide it exactly, so a rectangle's outer edges are the footprint's."""
    F = Frame(0.0, 0.0, a)
    loc = [F.l(x, z) for x, z in ring]
    u0, u1 = min(p[0] for p in loc), max(p[0] for p in loc)
    v0, v1 = min(p[1] for p in loc), max(p[1] for p in loc)
    nu = max(1, int(round((u1 - u0) / cell)))
    nv = max(1, int(round((v1 - v0) / cell)))
    cu, cv = (u1 - u0) / nu, (v1 - v0) / nv
    # A cell is in when its middle is in by a millimetre each way: a middle on the line would be in or out by the
    # last bit of its projection, which the world's generator (WorldBuildings.Cover) reaches by another way.
    mask = [[firmly_in(u0 + (i + 0.5) * cu, v0 + (j + 0.5) * cv, loc) for i in range(nu)] for j in range(nv)]
    total = sum(sum(r) for r in mask)
    if total == 0:
        return [], (u0, v0, cu, cv, nu, nv), mask
    rects = []
    left = total
    while len(rects) < max_rects:
        area, i0, i1, j0, j1 = largest_rect(mask, nu, nv)
        if area == 0 or area * cu * cv < min_area:
            break
        rects.append((i0, i1, j0, j1))
        for j in range(j0, j1):
            for i in range(i0, i1):
                mask[j][i] = False
        left -= area
        if left < stop_frac * total:
            break
    return rects, (u0, v0, cu, cv, nu, nv), None


class SpatialHash:
    def __init__(self, cell=50.0):
        self.cell = cell
        self.d = defaultdict(list)

    def add(self, item, x0, x1, z0, z1):
        c = self.cell
        for i in range(math.floor(x0 / c), math.floor(x1 / c) + 1):
            for j in range(math.floor(z0 / c), math.floor(z1 / c) + 1):
                self.d[(i, j)].append(item)

    def near(self, x0, x1, z0, z1):
        c = self.cell
        seen, out = set(), []
        for i in range(math.floor(x0 / c), math.floor(x1 / c) + 1):
            for j in range(math.floor(z0 / c), math.floor(z1 / c) + 1):
                for it in self.d.get((i, j), ()):
                    if id(it) not in seen:
                        seen.add(id(it))
                        out.append(it)
        return out


# ══ Entities ══════════════════════════════════════════════════════════════════════════════════════

_next_id = [1000]
entities = []
named_places = []
COUNT = defaultdict(int)


def new_id():
    _next_id[0] += 1
    return _next_id[0]


def tile_of(x, z):
    return f"{math.floor(x / TILE)},{math.floor(z / TILE)}"


# A building is kept whole in the tile its middle is in: while one is being built, everything it is
# made of (walls, roof, floor, rooms, doors, doorways, furniture) is tagged with that tile, wherever its
# own centre falls. Cut by a tile edge, a streamer loading one tile got half a house: walls on one side
# and not the other, a room whose doorway was in the next tile (104 of Magnolia's buildings).
_TILE_PIN = [None]


def entity_tile(x, z):
    return _TILE_PIN[0] or tile_of(x, z)


# While a building is being built, the height of the pad it stands on: everything it is made of is set on
# it (ground_all, below). None outside a building.
_Y_PIN = [None]


# While a building that is not made (one standing in a carriageway) is built, or a drive that is not laid: what
# is made then is left out of the map at the end, its ids unused, so no other entity's id moves.
_DROPPING = [False]


def _finish(e, name, layer):
    if name:
        e["Name"] = name
    if _DROPPING[0]:
        e["_drop"] = True
    e["_pin"] = _Y_PIN[0]
    e["Tile"] = entity_tile(e["Position"]["X"], e["Position"]["Z"])
    e["Layer"] = layer
    entities.append(e)
    COUNT[layer] += 1
    return e["EntityId"]


# Walls whose two faces differ (an assembly: cladding out, plasterboard in, OpenFPS.Acoustics/Assemblies.cs): turned as
# their frame is, their +Z the frame's +v, never folded into a half turn as a box of one stuff may be.
FACED = {"brick_veneer_wall", "brick_veneer_wall_unlined", "framed_siding_wall", "framed_siding_wall_unlined"}


def norm_full(a):
    """An angle folded into (-pi, pi]."""
    while a > math.pi + 1e-12:
        a -= 2 * math.pi
    while a <= -math.pi + 1e-12:
        a += 2 * math.pi
    return a


def obox(prefab, F, u0, u1, v0, v1, y0, y1, name=None, layer="structure"):
    """A box written as the space it fills, in frame F. Scale comes from the prefab's own collider."""
    bx, by, bz = BASE[prefab]
    cx, cz = F.w((u0 + u1) / 2, (v0 + v1) / 2)
    e = {"EntityId": new_id(), "PrefabId": prefab, "Position": v3(cx, (y0 + y1) / 2, cz)}
    a = norm_full(F.a) if prefab in FACED else norm_half(F.a)
    if abs(a) > 1e-9:
        e["Rotation"] = yaw(-a)
    e["Scale"] = v3((u1 - u0) / bx, (y1 - y0) / by, (v1 - v0) / bz)
    e["_box"] = (F, u0, u1, v0, v1, y0, y1)
    return _finish(e, name, layer)


def seg_box(prefab, ax, az, bx, bz, width, y0, y1, ext0=0.0, ext1=0.0, name=None, layer="roads", heights=None):
    """A box along a segment, `width` across it, lengthened at each end. `heights`: the ground's height at
    each end, which the box is pitched to (ground_all); the ground under each end if not given."""
    L = math.dist((ax, az), (bx, bz))
    if L < 0.05:
        return None
    F = Frame(ax, az, math.atan2(bz - az, bx - ax))
    eid = obox(prefab, F, -ext0, L + ext1, -width / 2, width / 2, y0, y1, name=name, layer=layer)
    entities[-1]["_seg"] = (L, heights)
    return eid


def prop(prefab, x, y, z, name=None, layer="props"):
    e = {"EntityId": new_id(), "PrefabId": prefab, "Position": v3(x, y, z), "Scale": v3(1, 1, 1)}
    return _finish(e, name, layer)


def region(name, F, u0, u1, v0, v1, y0, y1, layer="zones"):
    """A place. A room of a building is indoors, said here: the server's survey of a region's box (CompositeAcoustics)
    reads a face as walled by what crosses it, and a wing's box against an angled wall was walled only by the 25 cm a
    wall used to be; with walls as thick as they are built (12 to 23 cm), two rooms read as out of doors."""
    eid = obox("acoustic_region", F, u0, u1, v0, v1, y0, y1, name=name, layer=layer)
    if layer == "rooms":
        entities[-1]["IsIndoor"] = True
    return eid


def named_place(name, F, u0, u1, v0, v1, y0, y1):
    """A name without a room (named_place.json); written after everything else."""
    named_places.append((name, F, u0, u1, v0, v1, y0, y1, _Y_PIN[0]))


def portal(x, z, phi, width, y0, y1, depth, a, b, layer="interiors"):
    """An opening with nothing in it, joining place `a` to place `b` (-1 the outdoors), in a wall running along angle
    `phi`: its frame is its place, X across (along the wall), Y up, Z through. A room's flood stops at it
    (docs/GEOMETRY.md 2.7), so it says where one room ends and the next begins where no wall does."""
    e = obox("portal", Frame(x, z, phi), -width / 2, width / 2, -depth / 2, depth / 2, y0, y1, layer=layer)
    e = entities[-1]
    e["RegionAId"], e["RegionBId"], e["ApertureSize"] = a, b, round(width, 3)
    return e["EntityId"]


# Openings made where the rooms are made but written after everything else, so adding them moves no other
# part's id: (x, z, phi, width, y0, y1, depth, a, b, the pad's height, the tile).
openings_later = []


def portal_later(x, z, phi, width, y0, y1, depth, a, b):
    openings_later.append((x, z, phi, width, y0, y1, depth, a, b, _Y_PIN[0], _TILE_PIN[0]))


# Rooms made for the parts of a footprint no rectangle holds, written last as the openings are:
# (name, F, u0, u1, v0, v1, y0, y1, the pad's height, the tile, the place's id once written).
rooms_later = []


# Steps down from each outside door to the ground, written last of all, after the openings, so adding them moves no
# other part's id: (frame along the wall, u0, u1, v0, v1, y0, y1 over the pad, name, the pad's height, the tile).
steps_later = []

# The finish laid on a home's slab where the home is one room a rectangle (detail 1), written after the steps so adding
# it moves no other part's id: (frame, outline, prefab, name, the pad's height, the tile). The slab under it is the
# footprint stood up in concrete, as every other building's is: until 2026-10-11 the finish was the slab, 27 cm of
# carpet or wood on the ground (docs/GEOMETRY.md 12.6, "A floor is a construction").
finishes_later = []

STEP_RISE = 0.17          # a step's rise, m
STEP_TREAD = 0.28         # ...and its going
STEP_LANDING = 1.0        # the landing at the door, out from the wall
STEP_SIDE = 0.3           # how far the steps reach past each side of the doorway
STEP_LEFT = 0.3           # the most the last step may stand over the ground beyond it (a body steps 0.4)
STEP_MOST = 10
SLAB_CUT = 0.15           # the floor slab's underside under the pad: the ground is cut down to it round a floor
SLAB_REACH = 3.0          # ...as far as a post a cell and a half from its outline (TerrainBuilder.Grade)


def steps_out(E, s, width, name):
    """A concrete landing outside a doorway on a building's pad, its top just under the threshold, and steps from it
    down to the ground: a floor is level and the ground round it is not, and a body steps no more than 0.4 m (Cody,
    2026-10-10: the front doors of houses on a slope stood half a metre over their yards and could not be walked
    into). The frame E runs along the wall, v into the building; each piece reaches 0.65 m and more down into the
    ground, so the ground is never graded to it as to a slab. A building on no pad (set into the ground) has none."""
    pad = _Y_PIN[0]
    if pad is None or not HAS_GROUND:
        return
    half = width / 2 + STEP_SIDE

    def low(v):
        g = min(ground(*E.w(u, v)) for u in (s - half, s, s + half)) - pad
        return min(g, -SLAB_CUT) if -v <= SLAB_REACH else g
    top = FLOOR_TOP - 0.02
    pieces = [(-STEP_LANDING, 0.0, top)]
    v = -STEP_LANDING
    while top - min(low(v), low(v - STEP_TREAD)) > STEP_LEFT and len(pieces) <= STEP_MOST:
        top -= STEP_RISE
        pieces.append((v - STEP_TREAD, v, top))
        v -= STEP_TREAD
    for k, (v0, v1, t) in enumerate(pieces):
        bottom = min(min(low(v0), low(v1)) - 0.3, t - 0.65)
        steps_later.append((E, s - half, s + half, v0, v1, round(bottom, 4), t, f"{name} {'landing' if k == 0 else 'step'}",
                            pad, _TILE_PIN[0]))


WING_LEAST = 2.0          # the least piece of a footprint touching no room that is a room of its own, m2


def wings(F, ring, grid, rooms, top, new_name):
    """Every part of a footprint that no room's rectangle holds is a room's: a piece of the footprint's grid cells
    no rectangle covers goes to the room it shares most of its edge with, and opens onto every other room it touches
    (an opening along each run of the edge between them); a piece that touches no room is a room of its own, named
    `new_name(area)`, its box the biggest rectangle in it. So no air inside the walls belongs to no room
    (docs/GEOMETRY.md 2.7). `rooms` is [(id, (u0, u1, v0, v1))] in F, in the order they were made."""
    u0g, v0g, cu, cv, nu, nv = grid
    loc = [F.l(x, z) for x, z in ring]
    inside = [[firmly_in(u0g + (i + 0.5) * cu, v0g + (j + 0.5) * cv, loc) for i in range(nu)] for j in range(nv)]
    own = [[-1] * nu for _ in range(nv)]
    for k, (_, (ru0, ru1, rv0, rv1)) in enumerate(rooms):
        for j in range(nv):
            vc = v0g + (j + 0.5) * cv
            if not rv0 <= vc <= rv1:
                continue
            for i in range(nu):
                uc = u0g + (i + 0.5) * cu
                if ru0 <= uc <= ru1 and own[j][i] < 0:
                    own[j][i] = k
    seen = [[False] * nu for _ in range(nv)]
    for j0 in range(nv):
        for i0 in range(nu):
            if seen[j0][i0] or not inside[j0][i0] or own[j0][i0] >= 0:
                continue
            piece, stack = [], [(i0, j0)]
            seen[j0][i0] = True
            while stack:
                i, j = stack.pop()
                piece.append((i, j))
                for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    a, b = i + di, j + dj
                    if 0 <= a < nu and 0 <= b < nv and not seen[b][a] and inside[b][a] and own[b][a] < 0:
                        seen[b][a] = True
                        stack.append((a, b))
            piece.sort(key=lambda c: (c[1], c[0]))
            # Its edges against rooms: (room, axis, line, s0, s1); axis "u" is a line of constant u.
            edges = []
            for i, j in piece:
                for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    a, b = i + di, j + dj
                    if not (0 <= a < nu and 0 <= b < nv) or own[b][a] < 0:
                        continue
                    if di:
                        line = u0g + (max(i, a)) * cu
                        edges.append((own[b][a], "u", line, v0g + j * cv, v0g + (j + 1) * cv))
                    else:
                        line = v0g + (max(j, b)) * cv
                        edges.append((own[b][a], "v", line, u0g + i * cu, u0g + (i + 1) * cu))
            if not edges:
                # A sliver in a corner touching no room's cells (no more than a couple of square metres) is left to
                # whichever room's flood reaches it: a room of its own there is a room in a gap between two walls.
                if len(piece) * cu * cv < WING_LEAST:
                    continue
                mask = [[False] * nu for _ in range(nv)]
                for i, j in piece:
                    mask[j][i] = True
                _, i0r, i1r, j0r, j1r = largest_rect(mask, nu, nv)
                area = len(piece) * cu * cv
                rooms_later.append([new_name(area), F, u0g + i0r * cu, u0g + i1r * cu, v0g + j0r * cv, v0g + j1r * cv,
                                    FLOOR_TOP - 0.02, top, _Y_PIN[0], _TILE_PIN[0], None])
                continue
            count = defaultdict(int)
            for e in edges:
                count[e[0]] += 1
            owner = max(sorted(count), key=lambda k: count[k])
            for i, j in piece:
                own[j][i] = owner
            runs = defaultdict(list)
            for k, axis, line, s0, s1 in edges:
                if k != owner:
                    runs[(k, axis, round(line, 6))].append((s0, s1))
            for (k, axis, line), spans in sorted(runs.items()):
                spans.sort()
                merged = [list(spans[0])]
                for s0, s1 in spans[1:]:
                    if s0 <= merged[-1][1] + 1e-6:
                        merged[-1][1] = max(merged[-1][1], s1)
                    else:
                        merged.append([s0, s1])
                for s0, s1 in merged:
                    sm = (s0 + s1) / 2
                    x, z = F.w(line, sm) if axis == "u" else F.w(sm, line)
                    phi = F.a + math.pi / 2 if axis == "u" else F.a
                    portal_later(x, z, phi, s1 - s0, FLOOR_TOP - 0.02, top, 0.1, rooms[owner][0], rooms[k][0])
                    WAYS_MADE.append((rooms[owner][0], rooms[k][0], open_width(F, ring, axis, line, s0, s1)))


def door(prefab, x, z, y0, phi, a, b, opening, room_xz, name, layer="structure"):
    """A leaf in a doorway whose wall runs along angle `phi`, joining place `a` (the room it belongs
    to, at room_xz) to place `b`. A hinged leaf's +Z face is its outside and is turned away from its
    room, as gen_city's doors are; the leaf is made to cover the opening and lap each jamb."""
    bx, by, bz = BASE[prefab]
    if prefab in HINGED:
        nx, nz = -math.sin(phi), math.cos(phi)            # the leaf's +Z, in (x, z)
        if (room_xz[0] - x) * nx + (room_xz[1] - z) * nz > 0:
            phi += math.pi
    e = {"EntityId": new_id(), "PrefabId": prefab, "Position": v3(x, y0 + by / 2, z),
         "Rotation": yaw(-phi), "RegionAId": a, "RegionBId": b,
         "Scale": v3(round((opening + 2 * DOOR_LAP) / bx, 4), 1, 1)}
    return _finish(e, name, layer)


# ══ The inputs ════════════════════════════════════════════════════════════════════════════════════

OSM = load("osm.json")
NODES = OSM["nodes"]
BUILDINGS = load("buildings.json", [])
ADDRESSES = load("addresses.json", [])
PLACES = load("places.json", [])
WATER = load("water.json", [])
ADDRFEAT = load("addrfeat.json", [])
LANDCOVER = load("landcover.json")
AREA = [[P(lat, lon) for lat, lon in ring] for ring in load("area.json")["rings"]]


def nxz(nid):
    n = NODES[str(nid)]
    return P(n[0], n[1])


def ntags(nid):
    n = NODES.get(str(nid))
    return n[2] if n and len(n) > 2 else {}


def in_area(x, z):
    return any(pip(x, z, r) for r in AREA)


def geo_of(x, z):
    """The latitude and longitude of a point of the map."""
    return utm.to_latlon(UTM_ZONE, UTM_NORTH, x + UTM_E0, z + UTM_N0)


def landcover_at(x, z):
    """The WorldCover class letter at a point (landcover.json's legend), '.' off the grid."""
    if LANDCOVER is None:
        return "."
    lat, lon = geo_of(x, z)
    r = int((LANDCOVER["north"] - lat) / LANDCOVER["dlat"])
    c = int((lon - LANDCOVER["west"]) / LANDCOVER["dlon"])
    rows = LANDCOVER["rows"]
    if 0 <= r < len(rows) and 0 <= c < len(rows[r]):
        return rows[r][c]
    return "."


XMIN = min(p[0] for r in AREA for p in r)
XMAX = max(p[0] for r in AREA for p in r)
ZMIN = min(p[1] for r in AREA for p in r)
ZMAX = max(p[1] for r in AREA for p in r)


# ══ The ground ════════════════════════════════════════════════════════════════════════════════════
#
# The ground follows the survey (docs/GEOMETRY.md 5.1). elevation.json is 3DEP's posts every 2 m on the
# world's UTM grid over whole 250 m tiles (tools/fetch_place.py elevation), and the map's origin is one of
# those posts, so the map's 2 m posts are the survey's own: nothing is resampled. The map ships the same
# file beside it (PLACE_ID.elevation, the same bytes) and names it; everything here is set on those posts
# read the way the server reads them (between posts, bilinear), so the server's terrain, the world's tiles
# and this file agree. y = 0 is the ground at the origin, where the spawn address is. Without
# elevation.json the ground is flat, as it was.
ELEV = load("elevation.json")
HAS_GROUND = bool(ELEV and "data" in ELEV)
ELEV_STEP = 2.0
ELEV_MARGIN = 60.0                    # the map's bounds reach this far past the area (MARGIN below)
if HAS_GROUND:
    import array, base64
    if (ELEV.get("zone"), ELEV.get("north"), ELEV.get("spacing")) != (UTM_ZONE, UTM_NORTH, ELEV_STEP):
        raise SystemExit(f"{PLACE_DIR}/elevation.json is not on this place's grid: run tools/fetch_place.py elevation")
    _d = array.array("h")
    _d.frombytes(zlib.decompress(base64.b64decode(ELEV["data"])))
    if sys.byteorder != "little":
        _d.byteswap()
    ECOLS, EROWS = ELEV["cols"], ELEV["rows"]
    ECM = list(_d)
    for _k in range(ECOLS, len(ECM)):
        ECM[_k] += ECM[_k - ECOLS]
    del _d
    EX0, EZ0 = ELEV["east0"] - UTM_E0, ELEV["north0"] - UTM_N0
    _sea = ECM[int(round(-EZ0 / ELEV_STEP)) * ECOLS + int(round(-EX0 / ELEV_STEP))]
    SEA_Y = round(ELEV["base"] + _sea * 0.01, 2)         # the height over the sea of y = 0
    EBASE = round(ELEV["base"] - SEA_Y, 2)


def ground(x, z):
    """The ground's height at a point of the map, as the server's terrain has it (TerrainBuilder): the
    map's elevation posts, bilinear between them, held at the edge. 0 on flat ground."""
    if not HAS_GROUND:
        return 0.0
    fx = (x - EX0) / ELEV_STEP
    fz = (z - EZ0) / ELEV_STEP
    i = min(max(int(math.floor(fx)), 0), ECOLS - 2)
    j = min(max(int(math.floor(fz)), 0), EROWS - 2)
    tx = min(max(fx - i, 0.0), 1.0)
    tz = min(max(fz - j, 0.0), 1.0)
    k = j * ECOLS + i
    h00, h10 = EBASE + ECM[k] * 0.01, EBASE + ECM[k + 1] * 0.01
    h01, h11 = EBASE + ECM[k + ECOLS] * 0.01, EBASE + ECM[k + ECOLS + 1] * 0.01
    return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz


ROAD_SMOOTH = 10.0                    # half the width of the square a road's height is averaged over, m


def road_y(x, z):
    """The height a road surface is laid at: the ground averaged over a 20 m square, so a road runs smooth
    over the survey's bumps, and the same function of where it is for the carriageway, its verges, its
    sidewalks and the junctions it meets at."""
    if not HAS_GROUND:
        return 0.0
    s, n = 0.0, 0
    for a in (-1.0, -0.5, 0.0, 0.5, 1.0):
        for b in (-1.0, -0.5, 0.0, 0.5, 1.0):
            s += ground(x + a * ROAD_SMOOTH, z + b * ROAD_SMOOTH)
            n += 1
    return s / n


def clip_line(pts):
    """The parts of a polyline inside the area, each cut where it crosses the edge."""
    out, cur = [], []

    def cross_point(a, b):
        # Bisection on the segment for where it leaves the area: the edge is a box or a boundary
        # line, and a centimetre is plenty.
        lo, hi = 0.0, 1.0
        ina = in_area(*a)
        for _ in range(30):
            m = (lo + hi) / 2
            q = (a[0] + m * (b[0] - a[0]), a[1] + m * (b[1] - a[1]))
            if in_area(*q) == ina:
                lo = m
            else:
                hi = m
        m = (lo + hi) / 2
        return (a[0] + m * (b[0] - a[0]), a[1] + m * (b[1] - a[1]))
    for i, p in enumerate(pts):
        inside = in_area(*p)
        if i > 0 and inside != in_area(*pts[i - 1]):
            q = cross_point(pts[i - 1], p)
            if inside:
                cur = [q]
            else:
                cur.append(q)
                out.append(cur)
                cur = []
        if inside:
            cur.append(p)
    if len(cur) >= 2:
        out.append(cur)
    # No point twice: a crossing point a hair from the node beyond it is a zero-length segment, and a
    # zero-length segment has no direction.
    out = [[p for i, p in enumerate(c) if i == 0 or math.dist(p, c[i - 1]) > 0.05] for c in out]
    return [c for c in out if len(c) >= 2 and plen(c) > 0.5]


# ══ Names ═════════════════════════════════════════════════════════════════════════════════════════
#
# Street names from three sources that spell them three ways: OpenStreetMap "Country Meadow Lane", the
# address points "COUNTRY MEADOW", the Census "Country Meadow Ln". A name is matched on its words with
# the abbreviations spelled out, and failing that on its base (the name without "Lane" or "South"), to
# the nearest road that has it. What a player hears is OpenStreetMap's, the name on the road.
ABBR = {"n": "north", "s": "south", "e": "east", "w": "west", "ne": "northeast", "nw": "northwest",
        "se": "southeast", "sw": "southwest", "st": "street", "str": "street", "ave": "avenue", "av": "avenue",
        "rd": "road", "ln": "lane", "dr": "drive", "ct": "court", "blvd": "boulevard", "pl": "place",
        "cir": "circle", "trl": "trail", "tr": "trail", "hwy": "highway", "pkwy": "parkway", "ter": "terrace",
        "cv": "cove", "xing": "crossing", "sq": "square", "pt": "point", "lp": "loop", "cres": "crescent",
        "aly": "alley", "expy": "expressway", "fwy": "freeway", "mt": "mount", "ft": "fort", "hl": "hill",
        "rdg": "ridge", "hts": "heights", "spg": "spring", "mdw": "meadow", "est": "estate", "ests": "estates"}
TYPES = {"street", "avenue", "road", "lane", "drive", "court", "boulevard", "place", "circle", "trail",
         "highway", "parkway", "terrace", "way", "cove", "crossing", "loop", "run", "path", "square", "point",
         "row", "bend", "pass", "alley", "expressway", "freeway", "crescent"}
DIRS = {"north", "south", "east", "west", "northeast", "northwest", "southeast", "southwest"}


def words(name):
    return [ABBR.get(w, w) for w in re.split(r"[^a-z0-9]+", (name or "").lower()) if w]


def full_key(name):
    return " ".join(words(name))


def base_key(name):
    ws = [w for w in words(name) if w not in TYPES and w not in DIRS]
    return " ".join(ws) if ws else full_key(name)


def title(name):
    """'MCINTOSH Road' -> 'McIntosh Road', 'Misty Cedar Ln' -> 'Misty Cedar Lane', 'N Marek Ln' ->
    'North Marek Lane', 'Belmont Ave SW' -> 'Belmont Avenue Southwest'. An abbreviation is spelled out
    only where a street type or a direction stands: a leading direction, or a word followed by
    nothing but directions ('St' at the end is Street; 'St Johns' is left alone)."""
    ws = [w for w in re.split(r"\s+", (name or "").strip()) if w]
    out = []
    for k, w in enumerate(ws):
        lw = w.lower().strip(".")
        rest = [ABBR.get(x.lower().strip("."), x.lower().strip(".")) for x in ws[k + 1:]]
        full = ABBR.get(lw)
        if full and ((k == 0 and full in DIRS and len(ws) > 1) or all(r in DIRS for r in rest)) and len(ws) > 1:
            out.append(full.capitalize())
            continue
        if w.isupper() or w.islower():
            w = w.lower().capitalize()
            if w.startswith("Mc") and len(w) > 3:
                w = "Mc" + w[2:].capitalize()
        out.append(w)
    return " ".join(out)


def slug(name):
    return re.sub(r"[^a-z0-9]+", "_", (name or "").lower()).strip("_") or "road"


# ══ Roads ═════════════════════════════════════════════════════════════════════════════════════════
#
# Roads as data (OpenFPS.World/Roads.cs): a centreline, a type and lanes, with junctions wherever two
# roads share a node in OpenStreetMap. Ways of the same name and class that meet end to end are one
# road, so a street the mapper happened to split in three is still one street; a road that comes back
# on itself (a loop, a lollipop cul-de-sac) is cut where it does, because a junction joins two roads.
# The world's tiles lay roads with a C# port of this (OpenFPS.World/OneWorld/WorldFeatures.cs: ROAD_CLASS,
# SURFACE, way_width, lay_line with road_y, ground_all's pitch, the zones and junctions): change both, and
# WorldFeaturesTests.Magnolia_s_roads_are_laid_as_gen_osm_laid_them holds them together.
ROAD_CLASS = {
    # OSM highway: (our type, rank, two-lane width m)
    "motorway": ("arterial", 5, 7.4), "trunk": ("arterial", 5, 7.4), "primary": ("arterial", 4, 7.2),
    "secondary": ("arterial", 4, 7.2), "tertiary": ("collector", 3, 6.7),
    "motorway_link": ("arterial", 4, 5.0), "trunk_link": ("arterial", 4, 5.0), "primary_link": ("arterial", 4, 5.0),
    "secondary_link": ("collector", 3, 5.0), "tertiary_link": ("collector", 3, 5.0),
    "unclassified": ("residential", 2, 6.0), "residential": ("residential", 2, 6.0), "road": ("residential", 2, 6.0),
    "living_street": ("residential", 2, 5.5), "service": ("service", 1, 4.0),
}
NOT_ROADS = {"driveway", "parking_aisle", "drive-through", "emergency_access"}
LANE_W = 3.4
SPEED_MPH = PLACE.get("SpeedMph", {})
VERGE = float(PLACE.get("VergeMetres", 2.5))           # grass between the carriageway and a yard


def speed_kmh(tags, hw):
    ms = tags.get("maxspeed", "")
    m = re.match(r"\s*([0-9.]+)\s*(mph)?", ms)
    if m:
        v = float(m.group(1))
        return round(v * 1.609344, 1) if (m.group(2) or PLACE.get("Units") == "mph") else v
    mph = SPEED_MPH.get(hw, SPEED_MPH.get("default", 30))
    return round(mph * 1.609344, 1)


SURFACE = {  # OSM surface -> (prefab, acoustic material)
    "asphalt": ("asphalt_road", "Asphalt"), "paved": ("asphalt_road", "Asphalt"), "chipseal": ("asphalt_road", "Asphalt"),
    "concrete": ("concrete_floor", "Concrete"), "concrete:plates": ("concrete_floor", "Concrete"),
    "concrete:lanes": ("concrete_floor", "Concrete"), "paving_stones": ("brick_floor", "Brick"),
    "sett": ("brick_floor", "Brick"), "bricks": ("brick_floor", "Brick"),
    "gravel": ("gravel_floor", "Gravel"), "fine_gravel": ("gravel_floor", "Gravel"), "compacted": ("gravel_floor", "Gravel"),
    "unpaved": ("gravel_floor", "Gravel"), "pebblestone": ("gravel_floor", "Gravel"),
    "dirt": ("dirt_floor", "Dirt"), "ground": ("dirt_floor", "Dirt"), "earth": ("dirt_floor", "Dirt"),
    "mud": ("dirt_floor", "Dirt"), "sand": ("dirt_floor", "Dirt"), "grass": ("grass_floor", "Grass"),
    "wood": ("wood_floor", "Wood"),
}


def surface_of(tags, default="asphalt"):
    return SURFACE.get(tags.get("surface", default), SURFACE[default])


def way_width(tags, hw):
    try:
        return max(2.5, float(re.match(r"[0-9.]+", tags["width"]).group(0)))
    except (KeyError, AttributeError, ValueError):
        pass
    base = ROAD_CLASS[hw][2]
    try:
        lanes = int(tags.get("lanes", "0"))
    except ValueError:
        lanes = 0
    if lanes > 2:
        return round(lanes * LANE_W + 0.6, 2)
    if lanes == 1 or tags.get("oneway") == "yes" and hw in ("service",):
        return max(3.5, min(base, 4.0))
    return base


ways_by_kind = defaultdict(list)
for w in OSM["ways"]:
    t = w["tags"]
    hw = t.get("highway")
    if hw in ROAD_CLASS and t.get("service") not in NOT_ROADS and t.get("area") != "yes" \
            and not (hw == "service" and t.get("oneway") in ("yes", "-1")):
        ways_by_kind["road"].append(w)
    elif hw == "service" or (hw in ROAD_CLASS and t.get("service") in NOT_ROADS):
        ways_by_kind["drive"].append(w)
    elif hw in ("footway", "path", "cycleway", "pedestrian", "track", "bridleway", "steps"):
        ways_by_kind["path"].append(w)
    if t.get("railway") in ("rail", "light_rail", "tram", "narrow_gauge", "subway") and t.get("tunnel") != "yes":
        ways_by_kind["rail"].append(w)
    if t.get("barrier") in ("fence", "wall", "hedge", "retaining_wall", "guard_rail"):
        ways_by_kind["barrier"].append(w)
    if t.get("amenity") == "parking" and w["nodes"][0] == w["nodes"][-1]:
        ways_by_kind["parking"].append(w)
    if t.get("building") and w["nodes"][0] == w["nodes"][-1]:
        ways_by_kind["building"].append(w)


class Road:
    def __init__(self, ways):
        self.ways = ways                    # [(way, reversed)]
        self.nodes = []
        for w, rev in ways:
            ns = list(reversed(w["nodes"])) if rev else list(w["nodes"])
            if self.nodes and self.nodes[-1] == ns[0]:
                ns = ns[1:]
            self.nodes += ns
        t0 = ways[0][0]["tags"]
        self.hw = t0["highway"]
        self.osm_name = t0.get("name", "")
        self.name = self.osm_name


def road_key(w):
    t = w["tags"]
    return (t.get("name", ""), ROAD_CLASS[t["highway"]][0], t.get("name", "") or w["id"])


def build_roads():
    ways = sorted(ways_by_kind["road"], key=lambda w: w["id"])
    # Chains: join ways end to end where exactly two ends meet at a node nothing else passes through,
    # and both are the same road (same name and class; an unnamed way only ever joins itself).
    uses = defaultdict(int)
    for w in ways:
        for n in w["nodes"]:
            uses[n] += 1
    chains = [[(w, False)] for w in ways]

    def ends(ch):
        first = ch[0][0]["nodes"][-1] if ch[0][1] else ch[0][0]["nodes"][0]
        last = ch[-1][0]["nodes"][0] if ch[-1][1] else ch[-1][0]["nodes"][-1]
        return first, last
    merged = True
    while merged:
        merged = False
        at = defaultdict(list)
        for ci, ch in enumerate(chains):
            f, l = ends(ch)
            if f != l:
                at[f].append((ci, 0))
                at[l].append((ci, 1))
        for node in sorted(at):
            lst = at[node]
            if len(lst) != 2 or uses[node] != 2:
                continue
            (ca, ea), (cb, eb) = lst
            if ca == cb or road_key(chains[ca][0][0]) != road_key(chains[cb][0][0]):
                continue
            A, B = chains[ca], chains[cb]
            if ea == 0:
                A = [(w, not r) for w, r in reversed(A)]
            if eb == 1:
                B = [(w, not r) for w, r in reversed(B)]
            chains[ca] = A + B
            chains.pop(cb)
            merged = True
            break
    roads = [Road(ch) for ch in chains]
    # Cut every road where it meets itself, so a junction is always between two roads: a closed loop
    # in half, a lollipop where its stick meets its loop.
    out = []
    while roads:
        r = roads.pop(0)
        seen, cut = {}, None
        for i, n in enumerate(r.nodes):
            if n in seen:
                cut = (seen[n], i)
                break
            seen[n] = i
        if cut is None:
            out.append(r)
            continue
        i0, i1 = cut
        if i0 > 0:
            mid = i0
        elif i1 < len(r.nodes) - 1:
            mid = i1
        else:
            mid = max(1, i1 // 2)
        for part in (r.nodes[:mid + 1], r.nodes[mid:]):
            if len(part) >= 2:
                q = Road(r.ways)
                q.nodes = part
                roads.append(q)
    return out


ROADS_RAW = build_roads()
ROADS = []          # RoadData dicts
ROAD_GEOM = []      # (road dict, pts, width, name, hw) for the boxes and the zones


def tiger_name_near(pts):
    """A Census street name for an unnamed road, from the address-range line along it."""
    if not ADDRFEAT:
        return ""
    mx, mz = point_at(pts, plen(pts) / 2)
    best = None
    for f in ADDRFEAT:
        if not f["name"]:
            continue
        line = [P(a, b) for a, b in f["line"]]
        pr = project(line, mx, mz)
        if pr and pr[0] < 15 and (best is None or pr[0] < best[0]):
            best = (pr[0], f["name"])
    return title(best[1]) if best else ""


def make_roads():
    used_ids = set()
    for r in ROADS_RAW:
        pts_all = [nxz(n) for n in r.nodes]
        for pts in clip_line(pts_all):
            if plen(pts) >= 4.0:
                ROAD_GEOM.append([r, pts])
    # Names for the unnamed, then RoadData.
    for g in ROAD_GEOM:
        r, pts = g
        name = r.osm_name or tiger_name_near(pts)
        if not name:
            name = "Private road" if r.ways[0][0]["tags"].get("access") in ("private", "no") else \
                   ("Service road" if r.hw == "service" else "Unnamed road")
        t = r.ways[0][0]["tags"]
        width = max(way_width(w["tags"], w["tags"]["highway"]) for w, _ in r.ways)
        typ, rank, _ = ROAD_CLASS[r.hw]
        rid = slug(name)
        k = 2
        while rid in used_ids:
            rid = f"{slug(name)}_{k}"
            k += 1
        used_ids.add(rid)
        oneway = t.get("oneway") == "yes"
        kmh = speed_kmh(t, r.hw)
        try:
            lanes_tag = int(t.get("lanes", "0"))
        except ValueError:
            lanes_tag = 0
        lanes = []
        if oneway:
            n = max(1, min(lanes_tag or 1, int(width // 3.0)))
            lw = width / n
            for k in range(n):
                lanes.append({"OffsetMetres": round(-width / 2 + (k + 0.5) * lw, 3), "Direction": 1,
                              "WidthMetres": round(lw, 3), "SpeedLimitKmh": kmh})
        else:
            each = max(1, min(lanes_tag // 2 if lanes_tag >= 4 else 1, int(width / 2 // 3.0)))
            lw = min(LANE_W, width / 2 / each)
            for k in range(each):
                off = (k + 0.5) * lw
                lanes.append({"OffsetMetres": round(off, 3), "Direction": 1, "WidthMetres": round(lw, 3), "SpeedLimitKmh": kmh})
                lanes.append({"OffsetMetres": round(-off, 3), "Direction": -1, "WidthMetres": round(lw, 3), "SpeedLimitKmh": kmh})
        # Surfaces, stretch by stretch, where a way is not asphalt.
        surfaces = []
        along = 0.0
        for i in range(len(pts) - 1):
            along += math.dist(pts[i], pts[i + 1])
        mat = surface_of(t)[1]
        if mat != "Asphalt":
            surfaces.append({"FromMetres": 0.0, "ToMetres": round(along, 2), "Material": mat})
        d = {"Id": rid, "Name": name, "Type": typ,
             "Centreline": [v3(x, 0.08, z) for x, z in pts], "WidthMetres": round(width, 2), "Lanes": lanes,
             "Surfaces": surfaces, "Tiles": road_tiles(pts)}
        ROADS.append(d)
        g += [d, width, name, r.hw]
        for w, _ in r.ways:
            WAY_NAME.setdefault(w["id"], name)


WAY_NAME = {}


def road_tiles(pts):
    tiles = set()
    for i in range(len(pts) - 1):
        L = math.dist(pts[i], pts[i + 1])
        n = max(1, int(L // 25))
        for k in range(n + 1):
            x = pts[i][0] + (pts[i + 1][0] - pts[i][0]) * k / n
            z = pts[i][1] + (pts[i + 1][1] - pts[i][1]) * k / n
            tiles.add(tile_of(x, z))
    return sorted(tiles, key=lambda s: tuple(int(v) for v in s.split(",")))


make_roads()
ROAD_INDEX = SpatialHash(60.0)
for g in ROAD_GEOM:
    pts = g[1]
    for i in range(len(pts) - 1):
        (ax, az), (bx, bz) = pts[i], pts[i + 1]
        ROAD_INDEX.add((g, i), min(ax, bx), max(ax, bx), min(az, bz), max(az, bz))


def nearest_road(x, z, reach=150.0, want=None):
    """(distance, along, side, tangent, foot, geom) of the nearest road, or of the nearest called `want`."""
    best = None
    for (g, i) in ROAD_INDEX.near(x - reach, x + reach, z - reach, z + reach):
        if want is not None and not want(g):
            continue
        (ax, az), (bx, bz) = g[1][i], g[1][i + 1]
        d, t = seg_point(x, z, ax, az, bx, bz)
        if d <= reach and (best is None or d < best[0]):
            best = (d, g)
    if best is None:
        return None
    g = best[1]
    pr = project(g[1], x, z)
    return pr + (g,)


# ── Junctions ─────────────────────────────────────────────────────────────────────────────────────
JUNCTIONS = []


def make_junctions():
    at = defaultdict(set)
    for gi, g in enumerate(ROAD_GEOM):
        for x, z in (g[1][0], g[1][-1]):
            at[(round(x, 3), round(z, 3))].add(gi)
    # Interior nodes another road ends on, or crosses at a shared node.
    node_pts = defaultdict(set)
    for gi, g in enumerate(ROAD_GEOM):
        for x, z in g[1]:
            node_pts[(round(x, 3), round(z, 3))].add(gi)
    signals = {}
    for nid, n in NODES.items():
        t = n[2] if len(n) > 2 else {}
        if t.get("highway") in ("traffic_signals", "stop"):
            x, z = P(n[0], n[1])
            signals[(round(x, 3), round(z, 3))] = t["highway"]
    ids = set()
    for key in sorted(node_pts):
        gis = sorted(node_pts[key])
        if len(gis) < 2:
            continue
        x, z = key
        here = [ROAD_GEOM[i] for i in gis]
        names = sorted({g[4] for g in here})
        r = max(g[3] for g in here) / 2 + 1.0
        jid = f"j_{int(round(x))}_{int(round(z))}"
        k = 2
        while jid in ids:
            jid = f"j_{int(round(x))}_{int(round(z))}_{k}"
            k += 1
        ids.add(jid)
        top = max(here, key=lambda g: (ROAD_CLASS[g[5]][1], plen(g[1])))
        control = {"traffic_signals": "signal", "stop": "stop"}.get(signals.get(key, ""), "give_way")
        JUNCTIONS.append({"Id": jid, "Name": " and ".join(names), "Position": v3(x, 0.08, z),
                          "RadiusMetres": round(r, 2), "Control": control, "PriorityRoads": [top[2]["Id"]],
                          "GiveWaySeconds": 2.0, "Tile": tile_of(x, z), "_roads": gis})
    # Two junction points a few metres apart (a road that meets another twice, a jog in a mapped
    # junction) are one junction: the second is dropped and the roads through it simply pass.
    keep = []
    for j in JUNCTIONS:
        p = (j["Position"]["X"], j["Position"]["Z"])
        twin = next((k for k in keep if math.dist(p, (k["Position"]["X"], k["Position"]["Z"])) < 4.0), None)
        if twin is None:
            keep.append(j)
        elif len(j["_roads"]) > len(twin["_roads"]):
            keep[keep.index(twin)] = j
    # A junction where two roads only end on each other at a hairpin is a U-turn and nothing else.
    def heading(g, x, z):
        pts = g[1]
        if math.dist(pts[0], (x, z)) < 0.01:
            a, b = pts[0], pts[1]
        elif math.dist(pts[-1], (x, z)) < 0.01:
            a, b = pts[-1], pts[-2]
        else:
            return None
        L = math.dist(a, b) or 1.0
        return ((b[0] - a[0]) / L, (b[1] - a[1]) / L)
    JUNCTIONS[:] = []
    for j in keep:
        if len(j["_roads"]) == 2:
            x, z = j["Position"]["X"], j["Position"]["Z"]
            h = [heading(ROAD_GEOM[i], x, z) for i in j["_roads"]]
            if all(h) and h[0][0] * h[1][0] + h[0][1] * h[1][1] > 0.7:
                continue
        JUNCTIONS.append(j)
    # Two junctions closer along a road than their reaches leave no lane between them: shrink both.
    for gi, g in enumerate(ROAD_GEOM):
        pts = g[1]
        on = []
        for j in JUNCTIONS:
            if gi in j["_roads"]:
                pr = project(pts, j["Position"]["X"], j["Position"]["Z"])
                on.append((pr[1], j))
        on.sort(key=lambda t: t[0])
        # Between two junctions: both reaches shrunk until a lane of at least a metre and a bit
        # is left between them.
        for (a, ja), (b, jb) in zip(on, on[1:]):
            gap = b - a
            if ja["RadiusMetres"] + jb["RadiusMetres"] + 1.2 > gap:
                each = max(0.5, (gap - 1.2) / 2)
                ja["RadiusMetres"] = round(min(ja["RadiusMetres"], each), 2)
                jb["RadiusMetres"] = round(min(jb["RadiusMetres"], each), 2)
        # Between a junction and the road's own end: a stub shorter than a lane is swallowed by the
        # junction (the road then starts in it, which the network allows), never left as a sliver.
        L = plen(pts)
        for along, j in on[:1]:
            if j["RadiusMetres"] < along < j["RadiusMetres"] + 1.2:
                j["RadiusMetres"] = round(along + 0.05, 2)
        for along, j in on[-1:]:
            if j["RadiusMetres"] < L - along < j["RadiusMetres"] + 1.2:
                j["RadiusMetres"] = round(L - along + 0.05, 2)
    for j in JUNCTIONS:
        del j["_roads"]


make_junctions()


# ══ The ground and the roads, laid ════════════════════════════════════════════════════════════════
#
# Every made surface stands PROUD of the one under it, because the ground probe takes the highest
# surface under your feet: the forest floor at -0.2..0, a verge 0.03, a lawn 0.04, a drive 0.06, the
# road 0.08, a pavement 0.12. A flush tie is what once put footsteps on dirt in the middle of a road.
Y_VERGE, Y_LAWN, Y_DRIVE, Y_ROAD, Y_WALK = 0.03, 0.04, 0.06, 0.08, 0.12
# On real ground the server lays the terrain instead (TerrainBuilder): no slab.
if not HAS_GROUND:
    obox("dirt_floor", WORLD, XMIN, XMAX, ZMIN, ZMAX, -0.2, 0.0, name="Ground", layer="ground")

# Things the woods keep clear of, and the lots and buildings must not stand in.
CLEAR = SpatialHash(20.0)
# The buildings set on a level pad, with its height: a lawn beside one is dug down to its floor (ground_all).
PADS = SpatialHash(20.0)
LAWN_DUG = 1.0            # how near a building a lawn is dug down to its floor, m


def clear_strip(ax, az, bx, bz, half):
    CLEAR.add(("strip", ax, az, bx, bz, half), min(ax, bx) - half, max(ax, bx) + half, min(az, bz) - half, max(az, bz) + half)


def is_clear(x, z, pad=0.0):
    for it in CLEAR.near(x - 1, x + 1, z - 1, z + 1):
        if it[0] == "strip":
            _, ax, az, bx, bz, half = it
            if seg_point(x, z, ax, az, bx, bz)[0] < half + pad:
                return False
        else:
            _, F, u0, u1, v0, v1 = it
            u, v = F.l(x, z)
            if u0 - pad < u < u1 + pad and v0 - pad < v < v1 + pad:
                return False
    return True


def lay_line(pts, width, prefab, y1, name, layer, verge=0.0, y0=0.0, tol=0.25, hfun=None, form=None):
    """Boxes along a polyline, each lengthened at a bend by as much as the bend opens on its outside, each
    pitched along its run to the heights `hfun` gives its ends (the ground under them by default) and
    level across: a road's verges, sidewalks and carriageway share its heights (road_y)."""
    pts = simplify(pts, tol)
    hts = [(hfun or ground)(x, z) for x, z in pts]
    dirs = [math.atan2(pts[i + 1][1] - pts[i][1], pts[i + 1][0] - pts[i][0]) for i in range(len(pts) - 1)]
    for i in range(len(pts) - 1):
        def ext(k):
            if k < 0 or k >= len(dirs) - 1:
                return 0.0
            turn = abs((dirs[k + 1] - dirs[k] + math.pi) % (2 * math.pi) - math.pi)
            return min(width, (width / 2) * math.tan(min(turn, 2.6) / 2) + 0.05)
        e0, e1 = ext(i - 1), ext(i)
        (ax, az), (bx, bz) = pts[i], pts[i + 1]
        # On real ground a straight run is laid in pieces of at most SEG_MAX, each pitched to its own ends,
        # so a long straight road follows the hills it crosses instead of bridging them.
        n = max(1, int(math.ceil(math.dist(pts[i], pts[i + 1]) / SEG_MAX))) if HAS_GROUND else 1
        for k in range(n):
            px, pz = (ax, az) if k == 0 else (ax + (bx - ax) * k / n, az + (bz - az) * k / n)
            qx, qz = (bx, bz) if k == n - 1 else (ax + (bx - ax) * (k + 1) / n, az + (bz - az) * (k + 1) / n)
            hp = hts[i] if k == 0 else (hfun or ground)(px, pz)
            hq = hts[i + 1] if k == n - 1 else (hfun or ground)(qx, qz)
            # Pieces of one run overlap by SEAM at the joins between them: pitched differently, two boxes that only
            # met there left a crack a ray straight down could fall through to the ground under the road.
            f0, f1 = (e0 if k == 0 else SEAM), (e1 if k == n - 1 else SEAM)
            if verge > 0:
                seg_box("grass_floor", px, pz, qx, qz, width + 2 * verge, 0.0, Y_VERGE,
                        f0 + (verge if k == 0 else 0.0), f1 + (verge if k == n - 1 else 0.0),
                        name=f"{name} verge" if name else "Verge", layer="verges", heights=(hp, hq))
            if seg_box(prefab, px, pz, qx, qz, width, y0, y1, f0, f1, name=name, layer=layer, heights=(hp, hq)) is not None and form:
                entities[-1]["Form"] = form
        clear_strip(ax, az, bx, bz, width / 2 + verge)


SEG_MAX = 20.0                        # the longest piece of a road laid on real ground, m
SEAM = 0.05                           # how far the pieces of one run overlap at their joins, m
KERB_W, KERB_UP = 0.15, 0.15          # a kerb's width, and how far it stands over the carriageway, m
# Across and up, the top edges taken off by 2 cm; the engine fits it to the kerb's box (Shapes.Sweep).
KERB_FORM = {"Kind": "Swept", "Profile": [-0.075, 0.0, 0.075, 0.0, 0.075, 0.21, 0.055, 0.23, -0.055, 0.23, -0.075, 0.21],
             "Path": [0.0, 0.0, 0.0, 1.0, 0.0, 0.0]}


# Each way in its own width and surface, once; the records above are the roads as wholes.
for w in sorted(ways_by_kind["road"], key=lambda w: w["id"]):
    if w["id"] not in WAY_NAME:
        continue
    t = w["tags"]
    for part in clip_line([nxz(n) for n in w["nodes"]]):
        verge = VERGE if max(detail_at(x, z) for x, z in part) >= 2 else 0.0
        ww = way_width(t, t["highway"])
        lay_line(part, ww, surface_of(t)[0], Y_ROAD, WAY_NAME[w["id"]], "roads", verge=verge, hfun=road_y)
        # A sidewalk the road's own tags say it has (sidewalk=both|left|right), a planting strip out
        # from the kerb. Mapped as ways of their own they are laid with the footways instead.
        sides = {"both": (1, -1), "left": (1,), "right": (-1,)}.get(t.get("sidewalk", ""), ())
        sides += tuple(s for k, s in (("sidewalk:left", 1), ("sidewalk:right", -1)) if t.get(k) == "yes" and s not in sides)
        sw = float(PLACE.get("SidewalkMetres", 1.5))
        for side in sides:
            off = side * (ww / 2 + 1.0 + sw / 2)
            lay_line(offset_line(part, off), sw, "concrete_floor", Y_WALK, f"{WAY_NAME[w['id']]} sidewalk", "paths",
                     hfun=lambda x, z, part=part: road_y(*project(part, x, z)[4]))
            # A kerb where the carriageway meets the planting strip: a swept profile, square with its top
            # edges taken off, standing KERB_UP over the road.
            lay_line(offset_line(part, side * (ww / 2 + KERB_W / 2)), KERB_W, "concrete_wall", Y_ROAD + KERB_UP,
                     f"{WAY_NAME[w['id']]} kerb", "paths", hfun=lambda x, z, part=part: road_y(*project(part, x, z)[4]),
                     form=KERB_FORM)

# Driveways, parking aisles and private service ways: surfaces only, not roads to route on.
DRIVES = []
for w in sorted(ways_by_kind["drive"], key=lambda w: w["id"]):
    t = w["tags"]
    for part in clip_line([nxz(n) for n in w["nodes"]]):
        ww = way_width(t, "service") if t.get("service") != "driveway" else 3.6
        prefab = surface_of(t, "concrete" if t.get("service") == "driveway" else "asphalt")[0]
        DRIVES.append((part, ww, prefab, t, w["id"]))

# Footways, paths, tracks.
for w in sorted(ways_by_kind["path"], key=lambda w: w["id"]):
    t = w["tags"]
    hw = t["highway"]
    for part in clip_line([nxz(n) for n in w["nodes"]]):
        if t.get("footway") == "sidewalk" or (hw == "footway" and t.get("footway") in ("sidewalk", "crossing")):
            near = nearest_road(*point_at(part, plen(part) / 2), reach=30.0)
            nm = (f"{near[5][4]} sidewalk" if near else "Sidewalk") if t.get("footway") != "crossing" else \
                 (f"{near[5][4]} crosswalk" if near else "Crosswalk")
            if t.get("footway") == "crossing":
                continue                      # the road's own surface is the crossing
            # Level with the road it runs beside, as its own sidewalks are.
            along = (lambda x, z, g=near[5]: road_y(*project(g[1], x, z)[4])) if near else None
            lay_line(part, float(PLACE.get("SidewalkMetres", 1.5)), surface_of(t, "concrete")[0], Y_WALK, nm, "paths", hfun=along)
        elif hw == "track":
            lay_line(part, 3.0, surface_of(t, "dirt")[0], Y_DRIVE, t.get("name") or "Track", "paths")
        elif hw == "cycleway":
            lay_line(part, 2.5, surface_of(t, "asphalt")[0], Y_DRIVE, t.get("name") or "Cycle path", "paths")
        elif hw == "steps":
            continue
        else:
            lay_line(part, 1.5, surface_of(t, "dirt")[0], Y_DRIVE, t.get("name") or "Footpath", "paths")

# Railways: ballast with two rails on it. Nothing runs on them yet.
for w in sorted(ways_by_kind["rail"], key=lambda w: w["id"]):
    t = w["tags"]
    nm = t.get("name") or "Railway"
    for part in clip_line([nxz(n) for n in w["nodes"]]):
        lay_line(part, 3.4, "gravel_floor", 0.3, f"{nm} ballast", "rail")
        if LEVEL >= 1:
            sp = simplify(part, 0.25)
            for i in range(len(sp) - 1):
                (ax, az), (bx, bz) = sp[i], sp[i + 1]
                L = math.dist(sp[i], sp[i + 1])
                if L < 0.1:
                    continue
                F = Frame(ax, az, math.atan2(bz - az, bx - ax))
                n = max(1, int(math.ceil(L / SEG_MAX))) if HAS_GROUND else 1
                for k in range(n):
                    for off in (-0.75, 0.75):
                        obox("metal_wall", F, L * k / n if k else 0.0, L * (k + 1) / n if k < n - 1 else L,
                             off - 0.035, off + 0.035, 0.3, 0.45, name=f"{nm} rail", layer="rail")

# Water: ponds as rectangles of their outline, streams as strips. Solid underfoot, because the
# engine has no swimming: walking onto a pond is walking on water, and the name says so.
for wf in WATER:
    nm = wf["name"] or ("Pond" if wf["kind"] == "area" else "Creek")
    if wf["kind"] == "area":
        ring = [P(a, b) for a, b in wf["ring"]]
        if len(ring) < 4:
            continue
        a, *_ = min_rect(ring)
        rects, (u0, v0, cu, cv, nu, nv), _ = cover(ring, a, 2.0, 8, 30.0)
        F = Frame(0.0, 0.0, a)
        for (i0, i1, j0, j1) in rects:
            b = (u0 + i0 * cu, u0 + i1 * cu, v0 + j0 * cv, v0 + j1 * cv)
            obox("water_surface", F, *b, 0.0, Y_ROAD + 0.01, name=nm, layer="water")
            CLEAR.add(("box", F) + b, *bbox_of(F, *b))
    else:
        for part in clip_line([P(a, b) for a, b in wf["line"]]):
            lay_line(part, 3.0 if wf["class"] in ("stream", "ditch", "drain") else 12.0, "water_surface", Y_ROAD + 0.01, nm, "water")


# ══ Addresses ═════════════════════════════════════════════════════════════════════════════════════

ROAD_NAMES = sorted({g[4] for g in ROAD_GEOM})


def street_for(street, x, z):
    """The road an address is on, as the road is called; or None if no road near has its name."""
    fk, bk = full_key(street), base_key(street)
    best = None
    for (g, i) in ROAD_INDEX.near(x - 300, x + 300, z - 300, z + 300):
        nm = g[4]
        score = 0 if full_key(nm) == fk else 1 if base_key(nm) == bk else None
        if score is None:
            continue
        (ax, az), (bx, bz) = g[1][i], g[1][i + 1]
        d, _ = seg_point(x, z, ax, az, bx, bz)
        if d > 300:
            continue
        key = (score, d)
        if best is None or key < best[0]:
            best = (key, g)
    return best[1] if best else None


class Address:
    def __init__(self, number, street, x, z, source):
        self.number, self.street_raw, self.x, self.z, self.source = number, street, x, z, source
        self.road = street_for(street, x, z)
        self.street = self.road[4] if self.road else title(street)
        self.label = f"{number} {self.street}"
        self.buildings = []
        self.main = None
        self.lot = None
        self.interpolated = source == "census"


ADDR = {}


def add_address(number, street, x, z, source):
    if not number or not street or not in_area(x, z):
        return None
    key = (number.strip().lower(), full_key(street))
    if key in ADDR:
        a = ADDR[key]
        if a.source == source:                 # several units at one number: the middle of them
            a._pts.append((x, z))
            a.x = sum(p[0] for p in a._pts) / len(a._pts)
            a.z = sum(p[1] for p in a._pts) / len(a._pts)
        return a
    a = Address(number.strip(), street, x, z, source)
    a._pts = [(x, z)]
    ADDR[key] = a
    return a


# OpenStreetMap's own addresses first: on a building they say which building.
OSM_BUILDING_ADDR = []
for w in sorted(ways_by_kind["building"], key=lambda w: w["id"]):
    t = w["tags"]
    if t.get("addr:housenumber") and t.get("addr:street"):
        pts = [nxz(n) for n in w["nodes"]]
        cx, cz = sum(p[0] for p in pts[:-1]) / (len(pts) - 1), sum(p[1] for p in pts[:-1]) / (len(pts) - 1)
        a = add_address(t["addr:housenumber"], t["addr:street"], cx, cz, "osm")
        if a:
            OSM_BUILDING_ADDR.append((a, cx, cz))
for nid in sorted(NODES, key=int):
    t = ntags(nid)
    if t.get("addr:housenumber") and t.get("addr:street"):
        x, z = nxz(nid)
        add_address(t["addr:housenumber"], t["addr:street"], x, z, "osm")
for number, street, unit, lat, lon in ADDRESSES:
    x, z = P(lat, lon)
    add_address(number, street, x, z, "nad")


# ══ Buildings ═════════════════════════════════════════════════════════════════════════════════════

class Building:
    def __init__(self, b, ring):
        self.src = b
        self.ring = ring
        self.area = abs(sum(ring[i][0] * ring[i + 1][1] - ring[i + 1][0] * ring[i][1] for i in range(len(ring) - 1))) / 2
        self.cx = sum(p[0] for p in ring[:-1]) / (len(ring) - 1)
        self.cz = sum(p[1] for p in ring[:-1]) / (len(ring) - 1)
        self.height = b.get("h") or 0.0
        self.addr = None
        self.place = None
        self.on_road = False
        self.kind = None
        a, mx, mz, long_, short = min_rect(ring[:-1])
        self.angle, self.long, self.short = a, long_, short
        self.rect_fill = self.area / max(1e-6, long_ * short)
        self.x0, self.x1 = min(p[0] for p in ring), max(p[0] for p in ring)
        self.z0, self.z1 = min(p[1] for p in ring), max(p[1] for p in ring)


BLD = []
B_INDEX = SpatialHash(40.0)
# Every way a car drives on, whole, and half its width: what a building may not stand in.
CARRIAGEWAYS = SpatialHash(40.0)
for w in sorted(ways_by_kind["road"], key=lambda w: w["id"]):
    line = []
    for n in w["nodes"]:
        x, z = nxz(n)
        if not line or abs(x - line[-1][0]) + abs(z - line[-1][1]) >= 1e-6:
            line.append((x, z))
    if len(line) < 2 or plen(line) < 4.0:
        continue
    half = way_width(w["tags"], w["tags"]["highway"]) / 2
    CARRIAGEWAYS.add((line, half), min(p[0] for p in line) - half, max(p[0] for p in line) + half,
                     min(p[1] for p in line) - half, max(p[1] for p in line) + half)
ON_ROAD = []
for b in BUILDINGS:
    ring = [P(lat, lon) for lat, lon in b["ring"]]
    if len(ring) < 4:
        continue
    bd = Building(b, ring)
    if bd.area < 6.0 or not in_area(bd.cx, bd.cz):
        continue
    # A building standing on a road is a mistake in somebody's data, or a canopy over a forecourt;
    # either way it is not something to wall in across the carriageway. The road is kept, with its lanes
    # the whole of its width, and the building is not made (as the world's tiles do, WorldFeatures.Site).
    # Everything else is decided as if it were there (its address, its lot, its place's name), and it is
    # built and then left out, so no other entity's id moves (DROPPING).
    bd.on_road = any(line_to_ring(line, bd.ring) < half for line, half in CARRIAGEWAYS.near(bd.x0, bd.x1, bd.z0, bd.z1))
    if bd.on_road:
        ON_ROAD.append(bd)
    BLD.append(bd)
    B_INDEX.add(bd, bd.x0, bd.x1, bd.z0, bd.z1)


def building_at(x, z):
    for bd in B_INDEX.near(x - 1, x + 1, z - 1, z + 1):
        if bd.x0 <= x <= bd.x1 and bd.z0 <= z <= bd.z1 and pip(x, z, bd.ring):
            return bd
    return None


def dist_to_building(bd, x, z):
    if pip(x, z, bd.ring):
        return 0.0
    return min(seg_point(x, z, *bd.ring[i], *bd.ring[i + 1])[0] for i in range(len(bd.ring) - 1))


# Places: what a building is, where OpenStreetMap or Overture says. Only premises a passer-by would
# name — a shop, a church, the fire station — and never a business registered at somebody's house.
PREMISES = set(PLACE.get("PlaceCategories", [
    "fire_station", "police_station", "gas_station", "discount_store", "convenience_store", "grocery_store",
    "supermarket", "christian_place_of_worship", "place_of_worship", "church", "rv_park", "park",
    "storage_facility", "self_storage", "hotel", "motel", "nursery_and_gardening_store",
    "hardware_home_and_garden_store", "hardware_store", "automotive_repair", "car_repair", "barber",
    "restaurant", "fast_food_restaurant", "cafe", "bar", "school", "elementary_school", "high_school",
    "middle_school", "library", "post_office", "pharmacy", "bank", "hospital", "clinic", "community_center",
    "food_truck_stand", "laundromat", "liquor_store", "dollar_store", "variety_store", "fuel", "fuel_station",
    "shopping_center", "department_store", "pet_store", "veterinarian", "dentist", "doctor", "townhall",
    "city_hall", "museum", "stadium", "sports_centre", "golf_course", "cemetery", "fitness_center", "gym"]))
POI = []
for nid in sorted(NODES, key=int):
    t = ntags(nid)
    kind = t.get("amenity") or t.get("shop") or t.get("leisure") or t.get("tourism") or t.get("office")
    if t.get("name") and kind and kind not in ("bench", "waste_basket", "parking_space", "bicycle_parking"):
        x, z = nxz(nid)
        if in_area(x, z):
            POI.append({"name": t["name"], "kind": kind, "x": x, "z": z, "src": "osm"})
for w in sorted(OSM["ways"], key=lambda w: w["id"]):
    t = w["tags"]
    kind = t.get("amenity") or t.get("shop") or t.get("leisure")
    if t.get("name") and kind and w["nodes"][0] == w["nodes"][-1] and kind != "parking":
        pts = [nxz(n) for n in w["nodes"][:-1]]
        x, z = sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts)
        if in_area(x, z):
            POI.append({"name": t["name"], "kind": kind, "x": x, "z": z, "src": "osm"})
for q in PLACES:
    if q["category"] not in PREMISES or q["confidence"] < 0.75 or q["status"] == "closed":
        continue
    x, z = P(*q["at"])
    # The same place twice (OSM's "Magnolia Volunteer Fire Department Station 183", Overture's
    # "Magnolia Fire Department"): the one already named within 60 m wins.
    if any(math.dist((x, z), (p["x"], p["z"])) < 60 and (base_key(p["name"]).split()[:1] == base_key(q["name"]).split()[:1]
                                                        or p["kind"] in q["category"] or q["category"] in p["kind"])
           for p in POI):
        continue
    POI.append({"name": q["name"].strip(), "kind": q["category"], "x": x, "z": z, "src": "overture"})
for p in POI:
    bd = building_at(p["x"], p["z"])
    if bd is None:
        cands = [b for b in B_INDEX.near(p["x"] - 40, p["x"] + 40, p["z"] - 40, p["z"] + 40) if b.area >= 60]
        if cands:
            bd = min(cands, key=lambda b: dist_to_building(b, p["x"], p["z"]))
            if dist_to_building(bd, p["x"], p["z"]) > 30:
                bd = None
    if bd is not None and bd.place is None:
        bd.place = p
    p["building"] = bd
# A building with a name of its own in the footprint data (a college's halls) is that place.
for bd in BLD:
    nm = (bd.src.get("name") or "").strip()
    if nm and bd.place is None:
        bd.place = {"name": nm, "kind": bd.src.get("class") or "building", "x": bd.cx, "z": bd.cz, "src": "building"}

# ── Buildings to addresses ────────────────────────────────────────────────────────────────────────
#
# A building with an address point inside it is that address's. Otherwise it is the address on the
# same side of the same road whose point is nearest along it, within a lot's width; anything left is
# a building nobody's address reaches, and gets one from the Census ranges if it is the size of a
# house.
for a, cx, cz in OSM_BUILDING_ADDR:
    bd = building_at(cx, cz)
    if bd is not None and bd.addr is None:
        bd.addr = a
for key in sorted(ADDR):
    a = ADDR[key]
    bd = building_at(a.x, a.z)
    if bd is not None and bd.addr is None:
        bd.addr = a


def road_side(x, z, road_geom):
    pr = project(road_geom[1], x, z)
    return pr


A_BY_ROAD = defaultdict(list)
for key in sorted(ADDR):
    a = ADDR[key]
    g = a.road or (nearest_road(a.x, a.z, 120.0) or [None] * 6)[5]
    if g is None:
        continue
    pr = project(g[1], a.x, a.z)
    a.g, a.along, a.side, a.off = g, pr[1], pr[2], pr[0]
    A_BY_ROAD[id(g)].append(a)

for bd in sorted(BLD, key=lambda b: (round(b.cx, 2), round(b.cz, 2))):
    if bd.addr is not None:
        continue
    near = nearest_road(bd.cx, bd.cz, 200.0)
    if near is None:
        continue
    g = near[5]
    best = None
    for a in A_BY_ROAD.get(id(g), ()):
        if a.side != near[2]:
            continue
        dal = abs(a.along - near[1])
        if dal > 45.0 or abs(a.off - near[0]) > 120.0:
            continue
        score = dal + 0.3 * abs(a.off - near[0])
        if best is None or score < best[0]:
            best = (score, a)
    if best:
        bd.addr = best[1]

for bd in BLD:
    if bd.addr is not None:
        bd.addr.buildings.append(bd)


def interpolate(bd):
    """An address for a house the address points miss: along the Census range of the road it faces,
    on its side, with the side's parity, and never a number the street already has."""
    near = nearest_road(bd.cx, bd.cz, 150.0)
    if near is None or not ADDRFEAT:
        return None
    best = None
    for f in ADDRFEAT:
        if not f["name"]:
            continue
        line = [P(a, b) for a, b in f["line"]]
        pr = project(line, bd.cx, bd.cz)
        if pr is None or pr[0] > 120:
            continue
        side = f["left"] if pr[2] > 0 else f["right"]
        if not side[0] or not side[1]:
            continue
        if best is None or pr[0] < best[0]:
            best = (pr[0], f, pr, side, line)
    if best is None:
        return None
    _, f, pr, side, line = best
    try:
        lo, hi = int(side[0]), int(side[1])
    except ValueError:
        return None
    frac = pr[1] / max(1e-6, plen(line))
    n = int(round(lo + (hi - lo) * frac))
    if (n - lo) % 2:
        n += 1 if hi >= lo else -1
    taken = {k[0] for k in ADDR if k[1] == full_key(title(f["name"]))}
    for step in range(0, 40):
        for cand in ((n + 2 * step), (n - 2 * step)):
            if min(lo, hi) <= cand <= max(lo, hi) and str(cand) not in taken:
                a = add_address(str(cand), title(f["name"]), bd.cx, bd.cz, "census")
                if a is None:
                    return None
                g = a.road or near[5]
                pr2 = project(g[1], a.x, a.z)
                a.g, a.along, a.side, a.off = g, pr2[1], pr2[2], pr2[0]
                A_BY_ROAD[id(g)].append(a)
                return a
    return None


INTERPOLATED = 0
for bd in sorted(BLD, key=lambda b: (round(b.cx, 2), round(b.cz, 2))):
    if bd.addr is None and bd.place is None and 70.0 <= bd.area <= 600.0:
        a = interpolate(bd)
        if a is not None:
            bd.addr = a
            a.buildings.append(bd)
            INTERPOLATED += 1

# A home's footprint is under a hectare (the largest in both places is 3,721 m2, a block of flats). A bigger one
# is never an address's house, wherever the point falls: Albany's way 125102808, building=yes and nothing else,
# 643 by 355 m round 34414 Highway 99e, was a "house" whose living room was 640 by 193 m.
HOME_MAX_M2 = 10000.0

# The house on each lot: the building the address point is in, or the biggest house-sized one.
for key in sorted(ADDR):
    a = ADDR[key]
    if not a.buildings:
        continue
    inside = [b for b in a.buildings if pip(a.x, a.z, b.ring) and b.area <= HOME_MAX_M2]
    houses = [b for b in a.buildings if 45 <= b.area <= HOME_MAX_M2]
    main = inside[0] if inside and inside[0].area >= 30 else \
        (max(houses, key=lambda b: (b.area - 2.0 * math.dist((b.cx, b.cz), (a.x, a.z)), b.cx)) if houses else None)
    a.main = main


OSM_HOMES = {"house", "detached", "residential", "semidetached_house", "terrace", "bungalow", "apartments",
             "static_caravan", "cabin", "farm"}
OSM_SHEDS = {"shed", "carport", "garage", "garages", "roof", "hut"}


def urban(bd):
    """Built-up ground round it, by the satellite: in a town a second big building on a lot is a
    building (a duplex, a block of flats), in the country it is a barn."""
    return PLACE.get("Setting") == "town" or landcover_at(bd.cx, bd.cz) == "B"


def classify(bd):
    cls = bd.src.get("class") or ""
    if bd.place is not None:
        k = bd.place["kind"]
        if "worship" in k or k in ("church",):
            return "church"
        return "premises"
    if bd.addr is not None and bd.addr.main is bd and cls not in OSM_SHEDS:
        narrow = bd.short <= 5.2 and bd.long >= 2.8 * bd.short
        if (narrow and cls not in OSM_HOMES) or cls == "static_caravan":
            return "mobile_home"
        return "house"
    if bd.area < 30 or cls == "shed":
        return "shed"
    if cls in ("garage", "garages", "carport"):
        return "garage"
    if bd.addr is not None:
        m = bd.addr.main
        if m is not None and math.dist((bd.cx, bd.cz), (m.cx, m.cz)) < 30 and bd.area < 100:
            return "garage"
        if urban(bd) and (bd.area >= 100 or cls in OSM_HOMES):
            return "building"
        return "workshop" if bd.area < 160 else "barn"
    if urban(bd) or cls:
        return "building" if bd.area >= 60 else "outbuilding"
    return "barn" if bd.area >= 160 else "outbuilding"


for bd in BLD:
    bd.kind = classify(bd)


# ══ Lots ══════════════════════════════════════════════════════════════════════════════════════════
#
# There are no lot lines in this data: the county's parcels may not be redistributed. So a lot is drawn
# from what is known: it runs back from the road's verge, square to the house where there is one, as
# far along the road as halfway to the next address on that side, and as deep as its buildings and a
# back yard. It is cut into the yard in front of the house (named by the address alone, which is what
# you hear standing on the drive), the back yard and the two sides: the house itself is rooms, and a
# yard that ran through it would put the outdoors in its doorways (CityZoneTests).
LOT_HALF_MAX = 45.0
LOT_DEPTH_MAX = 140.0
LOTS = []
OWN = {}            # building -> address whose lot it is in


def lot_frame(a):
    """(frame, front v, house extents in the frame or None). u along the road, v away from it."""
    g = getattr(a, "g", None)
    hb = a.main
    if g is None:
        if hb is None:
            return None
        return Frame(hb.cx, hb.cz, hb.angle), None, None
    pr = project(g[1], a.x, a.z)
    d, along, side, (tx, tz), (fx, fz) = pr
    nx, nz = (-tz, tx) if side > 0 else (tz, -tx)             # from the road toward the address
    ang = math.atan2(nz, nx) - math.pi / 2                    # the frame's v is the normal
    if hb is not None:
        # Square to the house: whichever of its axes is nearest the road's direction.
        best = None
        for k in range(4):
            cand = hb.angle + k * math.pi / 2
            dv = (-math.sin(cand), math.cos(cand))
            dot = dv[0] * nx + dv[1] * nz
            if best is None or dot > best[0]:
                best = (dot, cand)
        if best[0] > math.cos(math.radians(35)):
            ang = best[1]
    F = Frame(fx, fz, ang)
    front = g[3] / 2 + VERGE
    return F, front, g


def extents(F, ring):
    loc = [F.l(x, z) for x, z in ring]
    return (min(p[0] for p in loc), max(p[0] for p in loc), min(p[1] for p in loc), max(p[1] for p in loc))


def make_lots():
    for key in sorted(ADDR):
        a = ADDR[key]
        lf = lot_frame(a)
        if lf is None:
            continue
        F, front, g = lf
        if front is None:
            continue
        au, av = F.l(a.x, a.z)
        # Along the road: halfway to the neighbours on this side.
        nbrs = sorted((F.l(b.x, b.z)[0], b.label) for b in A_BY_ROAD.get(id(g), ())
                      if b is not a and b.side == a.side and abs(b.along - a.along) < 2 * LOT_HALF_MAX)
        u0, u1 = au - LOT_HALF_MAX * 0.6, au + LOT_HALF_MAX * 0.6
        for nu, _ in nbrs:
            if nu < au - 0.5:
                u0 = max(u0, (nu + au) / 2)
            elif nu > au + 0.5:
                u1 = min(u1, (nu + au) / 2)
        if u1 - u0 < 12:
            u0, u1 = au - 6, au + 6
        house = extents(F, a.main.ring) if a.main else None
        if house:
            u0, u1 = min(u0, house[0] - 3), max(u1, house[1] + 3)
            back = max(front + 25, house[3] + 18)
        else:
            back = max(front + 25, av + 20)
        for b in a.buildings:
            e = extents(F, b.ring)
            u0, u1 = min(u0, e[0] - 2), max(u1, e[1] + 2)
            back = max(back, e[3] + 4)
        back = min(back, front + LOT_DEPTH_MAX)
        if house and house[2] < front + 1:
            front = min(front, house[2] - 1)          # a house nearer the road than the verge: the yard stops at it
        # Not over anybody else's building: trim the far side, or a side, short of it.
        mine = set(id(b) for b in a.buildings)
        hu0, hu1 = (house[0], house[1]) if house else (au - 1, au + 1)
        for _ in range(2):
            x0, x1, z0, z1 = bbox_of(F, u0, u1, front, back)
            for b in B_INDEX.near(x0, x1, z0, z1):
                if id(b) in mine:
                    continue
                e = extents(F, b.ring)
                if e[1] <= u0 or e[0] >= u1 or e[3] <= front or e[2] >= back:
                    continue
                if e[2] >= (house[3] if house else av) - 1:
                    back = max(front + 6, e[2] - 1.0)
                elif e[0] >= hu1:
                    u1 = max(hu1 + 1, e[0] - 1.0)
                elif e[1] <= hu0:
                    u0 = min(hu0 - 1, e[1] + 1.0)
        lot = {"addr": a, "F": F, "u0": u0, "u1": u1, "front": front, "back": back, "house": house, "yards": []}
        a.lot = lot
        LOTS.append(lot)
        for b in a.buildings:
            OWN[id(b)] = a


make_lots()


def yards(lot):
    """The lot's pieces round its house: (name suffix, u0, u1, v0, v1)."""
    a, u0, u1, front, back, h = lot["addr"], lot["u0"], lot["u1"], lot["front"], lot["back"], lot["house"]
    if h is None:
        return [p for p in (clear_of_rooms(lot, ("", u0, u1, front, back)),) if p is not None]
    hu0, hu1, hv0, hv1 = max(u0, h[0]), min(u1, h[1]), max(front, h[2]), min(back, h[3])
    out = []
    if hv0 - front > 0.5:
        out.append(("", u0, u1, front, hv0))
    if back - hv1 > 0.5:
        out.append((", back yard", u0, u1, hv1, back))
    if hu0 - u0 > 0.5:
        out.append((", side yard", u0, hu0, hv0, hv1))
    if u1 - hu1 > 0.5:
        out.append((", side yard", hu1, u1, hv0, hv1))
    return [p for p in (clear_of_rooms(lot, piece) for piece in out) if p is not None]


ROOMED = ("house", "mobile_home", "premises", "church", "building")
OUTBUILDINGS = ("garage", "workshop", "barn", "outbuilding")


def has_rooms(b):
    """Whether a building is built with rooms you can go into: a home or premises always; a garage, a
    workshop or a barn only where the map is built in full (below that it is one solid box)."""
    return b.kind in ROOMED or (b.kind in OUTBUILDINGS and detail_at(b.cx, b.cz) >= 2)


def clear_of_rooms(lot, piece):
    """A yard cut back off anybody else's building with rooms in it: a named place over a room is the
    room's name. Each time, the cut that keeps the most of the yard."""
    F, a = lot["F"], lot["addr"]
    suffix, u0, u1, v0, v1 = piece
    for _ in range(6):
        x0, x1, z0, z1 = bbox_of(F, u0, u1, v0, v1)
        hit = None
        for b in B_INDEX.near(x0, x1, z0, z1):
            if not has_rooms(b) or b is a.main:
                continue
            e = extents(F, b.ring)
            if e[1] <= u0 or e[0] >= u1 or e[3] <= v0 or e[2] >= v1:
                continue
            hit = e
            break
        if hit is None:
            return (suffix, u0, u1, v0, v1)
        cuts = [(u0, hit[0] - 0.3, v0, v1), (hit[1] + 0.3, u1, v0, v1), (u0, u1, v0, hit[2] - 0.3), (u0, u1, hit[3] + 0.3, v1)]
        cuts = [c for c in cuts if c[1] - c[0] >= 2.0 and c[3] - c[2] >= 2.0]
        if not cuts:
            return None
        u0, u1, v0, v1 = max(cuts, key=lambda c: (c[1] - c[0]) * (c[3] - c[2]))
    return None


# ══ Spawn ═════════════════════════════════════════════════════════════════════════════════════════

SPAWN_ADDR = None
want = PLACE.get("Spawn", {}).get("Address", "")
if want:
    m = re.match(r"\s*(\S+)\s+(.*)", want)
    SPAWN_ADDR = ADDR.get((m.group(1).lower(), full_key(m.group(2)))) if m else None
    if SPAWN_ADDR is None and m:
        for k, a in sorted(ADDR.items()):
            if k[0] == m.group(1).lower() and base_key(a.street) == base_key(m.group(2)):
                SPAWN_ADDR = a
                break
if want and SPAWN_ADDR is None:
    raise SystemExit(f"spawn address {want!r} is not among the addresses")
if SPAWN_ADDR is not None:
    SPAWN_XZ = (SPAWN_ADDR.x, SPAWN_ADDR.z)
else:
    SPAWN_XZ = (0.0, 0.0)


# ══ Driveways ═════════════════════════════════════════════════════════════════════════════════════
#
# OpenStreetMap's where it has them; otherwise one is made for a house, from the road's edge straight
# to the front of the house at the end its garage is (or its middle), 3.6 m of concrete. The yard is
# laid after, under it.
def lot_of_point(x, z):
    for lot in YARD_LOTS.near(x - 1, x + 1, z - 1, z + 1):
        F = lot["F"]
        u, v = F.l(x, z)
        if lot["u0"] <= u <= lot["u1"] and lot["front"] - VERGE - 1 <= v <= lot["back"]:
            return lot
    return None


DRIVE_HALF = 1.8          # half a made-up driveway's width, m
STATS_EARLY = defaultdict(int)


def made_drive_u(F, h, v0, end, house, lot_u0, lot_u1):
    """Where across a lot (u in its frame, lot_u0 to lot_u1) a made-up driveway runs from the road (v0) to the front of
    the house (extents h): 2.4 m in from the house's chosen end, else the nearest point to that, a quarter metre at a
    time either way (toward the house's middle first) and no further than the lot, where the drive's strip enters no
    other building; None if there is none."""
    pref = h[0] + 2.4 if end == 0 else h[1] - 2.4
    inward = 1 if end == 0 else -1
    lo, hi = lot_u0 + DRIVE_HALF, lot_u1 - DRIVE_HALF
    v1 = h[2] - 0.2
    cands = [pref]
    for k in range(1, int((lot_u1 - lot_u0) / 0.25) + 2):
        cands += [pref + inward * 0.25 * k, pref - inward * 0.25 * k]
    for du in cands:
        if du != pref and not lo <= du <= hi:
            continue
        x0, x1, z0, z1 = bbox_of(F, du - DRIVE_HALF, du + DRIVE_HALF, v0, v1)
        if not any(poly_area(ring_in_box([F.l(x, z) for x, z in b.ring[:-1]], du - DRIVE_HALF, du + DRIVE_HALF, v0, v1)) > 1e-4
                   for b in B_INDEX.near(x0, x1, z0, z1) if b is not house and not b.on_road):
            return du
    return None


YARD_LOTS = SpatialHash(40.0)
for lot in LOTS:
    YARD_LOTS.add(lot, *bbox_of(lot["F"], lot["u0"], lot["u1"], lot["front"] - VERGE, lot["back"]))

DRIVE_FOR = {}
for part, ww, prefab, t, wid in DRIVES:
    lot = lot_of_point(*part[-1]) or lot_of_point(*part[0])
    nm = f"{lot['addr'].label} driveway" if lot else ("Parking aisle" if t.get("service") == "parking_aisle" else "Driveway")
    if lot:
        DRIVE_FOR[id(lot)] = part
    lay_line(part, ww, prefab, Y_DRIVE, nm, "drives", tol=0.6)

for lot in LOTS:
    a = lot["addr"]
    if LEVEL < 1 or id(lot) in DRIVE_FOR or lot["house"] is None or a.main is None or a.main.kind not in ("house", "mobile_home"):
        continue
    F, h = lot["F"], lot["house"]
    # The end of the house the drive comes to: the side its garage is on, seeded from the address. It goes where
    # its way from the road is clear of every other building, as near that end as the lot allows; a house with no
    # such way (one behind another on a flag lot) has none made up.
    end = 0 if h01(a.label, "drive") < 0.5 else 1
    du = made_drive_u(F, h, lot["front"] - VERGE, end, a.main, lot["u0"], lot["u1"])
    if du is None:
        STATS_EARLY["houses with no clear way from the road for a driveway"] += 1
    # One with no clear way, or to a house that is not made (on_road), is laid where it would have been and left
    # out, so no other id moves.
    _DROPPING[0] = du is None or a.main.on_road
    if not _DROPPING[0]:
        lot["drive_u"] = du
        lot["drive_end"] = end
    du = du if du is not None else (h[0] + 2.4 if end == 0 else h[1] - 2.4)
    x0, z0 = F.w(du, lot["front"] - VERGE)
    x1, z1 = F.w(du, h[2] - 0.2)
    lay_line([(x0, z0), (x1, z1)], 3.6, "concrete_floor", Y_DRIVE, f"{a.label} driveway", "drives")
    _DROPPING[0] = False


# ══ Gates and fences ══════════════════════════════════════════════════════════════════════════════
GATES = []
for nid in sorted(NODES, key=int):
    t = ntags(nid)
    if t.get("barrier") not in ("gate", "lift_gate", "swing_gate", "sliding_gate", "cattle_grid"):
        continue
    x, z = nxz(nid)
    if not in_area(x, z):
        continue
    # Across whatever way runs through it.
    best = None
    for part, ww, prefab, tt, wid in DRIVES:
        pr = project(part, x, z)
        if pr and pr[0] < 1.0 and (best is None or pr[0] < best[0]):
            best = (pr[0], pr[3], ww)
    if best is None:
        nr = nearest_road(x, z, 3.0)
        if nr:
            best = (nr[0], nr[3], nr[5][3])
    tx, tz = best[1] if best else (1.0, 0.0)
    width = (best[2] if best else 3.6) + 0.4
    lot = lot_of_point(x, z)
    nm = f"{lot['addr'].label} gate" if lot else ("Cattle grid" if t.get("barrier") == "cattle_grid" else "Gate")
    GATES.append((x, z, tx, tz, width, nm))
    F = Frame(x, z, math.atan2(tz, tx))
    named_place(nm, F, -1.0, 1.0, -width / 2, width / 2, 0.0, 2.5)
    if LEVEL >= 1:
        for s in (-1, 1):
            obox("wood_floor", F, -0.1, 0.1, s * width / 2 - 0.1, s * width / 2 + 0.1, 0.0, 1.4, name=f"{nm} post", layer="props")

if LEVEL >= 1:
    for w in sorted(ways_by_kind["barrier"], key=lambda w: w["id"]):
        t = w["tags"]
        kind = t["barrier"]
        prefab, thick, hgt, nm = {"fence": ("fence_timber", 0.05, 1.8, "Fence"), "hedge": ("foliage_hedge", 1.0, 2.0, "Hedge"),
                                  "wall": ("brick_wall", 0.25, 1.5, "Wall"), "retaining_wall": ("concrete_wall", 0.3, 1.0, "Retaining wall"),
                                  "guard_rail": ("metal_wall", 0.1, 0.8, "Guard rail")}[kind]
        gates_on = [nxz(n) for n in w["nodes"] if ntags(n).get("barrier") in ("gate", "swing_gate", "sliding_gate", "lift_gate")]
        for part in clip_line([nxz(n) for n in w["nodes"]]):
            sp = simplify(part, 0.2)
            for i in range(len(sp) - 1):
                (ax, az), (bx, bz) = sp[i], sp[i + 1]
                L = math.dist(sp[i], sp[i + 1])
                if L < 0.2:
                    continue
                F = Frame(ax, az, math.atan2(bz - az, bx - ax))
                cuts = sorted(F.l(gx, gz)[0] for gx, gz in gates_on if abs(F.l(gx, gz)[1]) < 0.5 and -0.6 < F.l(gx, gz)[0] < L + 0.6)
                s0 = 0.0
                for c in cuts + [None]:
                    s1 = L if c is None else c - 0.6
                    if s1 - s0 > 0.1:
                        obox(prefab, F, s0, s1, -thick / 2, thick / 2, 0.0, hgt, name=nm, layer="props")
                    if c is not None:
                        s0 = c + 0.6


# ══ Building a building ═══════════════════════════════════════════════════════════════════════════

STOREY_H = 2.75           # wall plate over the slab; a single-storey ranch house
FLOOR_TOP = 0.12          # slab top 0.10, a finished floor 2 cm on it
CEIL = 2.68               # underside of the ceiling
WALL_T = 0.25
PART_T = 0.12

# The walls are timber-framed, as the houses of both places are (docs/GEOMETRY.md 12.6, "Timber walls are framed"): a
# wall's prefab names its assembly (OpenFPS.Acoustics/Assemblies.cs) and its collider is the assembly's thickness.
# Outside, cladding (lap siding, or a brick veneer over an inch of air) on sheathing, 2x4 studs at 16 inch centres with
# batts between, plasterboard inside; a partition is plasterboard on both sides of the studs. A house built room by
# room has its plasterboard as a sheet of its own, the rooms' face, so its walls stop at their studs (the unlined
# prefab). WALL_T and PART_T stay the plan's allowance for a wall: where rooms, doors and furniture go is decided by
# them as before, and a wall stands from the footprint in by its own thickness.
FRAMED = {"brick_veneer_wall": "brick_veneer_wall_unlined", "framed_siding_wall": "framed_siding_wall_unlined"}
# The head over a doorway bears on the wall either side, as a header sits on a jack stud at each end, 1 1/2 in. past
# the opening: it laps the pieces beside it, so no hairline between them (the map's numbers round to 0.1 mm) lets a
# room's air out where a flood's cell falls in it (four rooms on the two places, 2026-10-11, with walls 12 cm thick).
BEARING = 0.038
# How far an outer wall runs on past an inside corner, behind the wall it meets: a quarter-metre flood cell's worth
# whatever its thickness. A rooms' flood takes a wall thinner than its cell as a cell thick (a digital plane's least
# thickness, RoomFlood), and two 12 cm walls meeting at 45 degrees to its grid, each run on only its own thickness,
# left the flood a way out of the corner (three houses on Albany, 2026-10-11).
CORNER_REACH = WALL_T
BOARD = "gypsum_board"
BOARD_T = BASE[BOARD][2]
PARTITION = "stud_partition"
PART_BUILD = BASE[PARTITION][2]


# How far in the plan allows for an outer wall and the plasterboard on it (drawn 3 cm thick before 2026-10-11):
# which pieces a wall is in, and so its ids, is decided by these (edge_wall, same_pieces), however thick it is built.
PLAN_REACH = (WALL_T, WALL_T + 0.03)


def wall_thick(prefab):
    """How thick a wall of this prefab stands: a framed one its assembly's thickness, anything else WALL_T."""
    return BASE[prefab][2] if prefab in FRAMED or prefab in FRAMED.values() else WALL_T
DOOR_TOP = FLOOR_TOP + 2.13           # the head of a doorway with no leaf in it (a barn's, an archway)


def head_over(prefab):
    """The head of a doorway with `prefab`'s leaf in it: it laps the leaf by DOOR_LAP, as a jamb does and as
    gen_city's do. 3 cm over the leaf, it was a slot every house's rooms got out to the street by
    (docs/GEOMETRY.md 12.6)."""
    return FLOOR_TOP + BASE[prefab][1] - DOOR_LAP


def exterior_runs(rects, grid):
    """The outside of a union of grid rectangles, as wall runs: (axis, line, s0, s1, outward)."""
    u0, v0, cu, cv, nu, nv = grid
    mask = [[False] * nu for _ in range(nv)]
    for (i0, i1, j0, j1) in rects:
        for j in range(j0, j1):
            for i in range(i0, i1):
                mask[j][i] = True

    def m(i, j):
        return 0 <= i < nu and 0 <= j < nv and mask[j][i]
    runs = []
    for j in range(nv + 1):                      # lines of constant v
        i = 0
        while i < nu:
            a, b = m(i, j - 1), m(i, j)
            if a == b:
                i += 1
                continue
            out = 1 if a else -1                 # inside below: outward is +v
            k = i
            while k < nu and m(k, j - 1) == a and m(k, j) == b:
                k += 1
            runs.append(("v", v0 + j * cv, u0 + i * cu, u0 + k * cu, out))
            i = k
    for i in range(nu + 1):                      # lines of constant u
        j = 0
        while j < nv:
            a, b = m(i - 1, j), m(i, j)
            if a == b:
                j += 1
                continue
            out = 1 if a else -1
            k = j
            while k < nv and m(i - 1, k) == a and m(i, k) == b:
                k += 1
            runs.append(("u", u0 + i * cu, v0 + j * cv, v0 + k * cv, out))
            j = k
    return runs, mask


def wall_run(F, run, prefab, top, name, cuts, layer="structure", thick=WALL_T):
    """A wall along a run, inside its line, with openings cut out of it and a lintel over each."""
    axis, line, s0, s1, out = run
    pieces = []
    s = s0
    for c0, c1, ctop in sorted(cuts):
        if c0 > s:
            pieces.append((s, c0, 0.0, top))
        if ctop < top:
            pieces.append((c0 - BEARING, c1 + BEARING, ctop, top))
        s = c1
    if s < s1:
        pieces.append((s, s1, 0.0, top))
    for a0, a1, y0, y1 in pieces:
        if a1 - a0 < 0.02:
            continue
        if axis == "v":       # runs along u at v = line
            w0, w1 = (line - thick, line) if out > 0 else (line, line + thick)
            obox(prefab, F, a0, a1, w0, w1, y0, y1, name=name, layer=layer)
        else:
            w0, w1 = (line - thick, line) if out > 0 else (line, line + thick)
            obox(prefab, F, w0, w1, a0, a1, y0, y1, name=name, layer=layer)


def run_point(F, run, s, depth=0.0):
    """A point on a run's line, `depth` outward of it, in world (x, z)."""
    axis, line, s0, s1, out = run
    if axis == "v":
        return F.w(s, line + out * depth)
    return F.w(line + out * depth, s)


def run_phi(F, run):
    return F.a if run[0] == "v" else F.a + math.pi / 2


# ── The footprint itself (docs/GEOMETRY.md 5.2, stage 4) ─────────────────────────────────────────────
#
# Walls go along the footprint's own ring, simplified, one wall a side; the floor is the ring stood up a
# slab thick and the roof is the straight skeleton of the ring (shapes the engine makes from the outline,
# OpenFPS.Geometry/Triangles/Shapes.More.cs). The rooms are still rectangles inside it.
RING_TOL = 0.1            # a corner this near the line through its neighbours does no work, m
RING_MIN_EDGE = 0.25      # nor does one this near the corner before it, m


def simplify_ring(ring, tol=RING_TOL, min_edge=RING_MIN_EDGE):
    """Polygons.Simplify (OpenFPS.Geometry), the same arithmetic in the same order: the ring without its
    repeated last point, the corner doing least work removed one at a time while that is under `tol`."""
    r = list(ring)
    if len(r) > 1 and r[0] == r[-1]:
        r.pop()
    while len(r) > 3:
        n = len(r)
        worst, least = -1, float("inf")
        for i in range(n):
            a, p, b = r[(i + n - 1) % n], r[i], r[(i + 1) % n]
            abx, abz = b[0] - a[0], b[1] - a[1]
            apx, apz = p[0] - a[0], p[1] - a[1]
            ab = math.sqrt(abx * abx + abz * abz)
            off = math.sqrt(apx * apx + apz * apz) if ab < 1e-12 else abs(abx * apz - abz * apx) / ab
            if math.sqrt(apx * apx + apz * apz) < min_edge:
                off = min(off, 0)
            if off < least:
                least, worst = off, i
        if least > tol:
            break
        r.pop(worst)
    return r


def ccw(ring):
    """Polygons.CounterClockwise: counter-clockwise with x east and z north (the inside on the left)."""
    r = list(ring)
    a = 0.0
    for i in range(len(r)):
        p, q = r[i], r[(i + 1) % len(r)]
        a += p[0] * q[1] - q[0] * p[1]
    if a < 0:
        r.reverse()
    return r


def wall_ring(bd):
    return ccw(simplify_ring(bd.ring))


def ring_edges(ring):
    """Each side of a counter-clockwise ring: (frame along it from its start, its length, whether its start and
    end corners are reflex). The frame's v runs into the building."""
    n = len(ring)
    dirs = []
    for k in range(n):
        (ax, az), (bx, bz) = ring[k], ring[(k + 1) % n]
        L = math.sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az))
        dirs.append(((bx - ax) / L, (bz - az) / L, L))
    out = []
    for k in range(n):
        (ax, az), (bx, bz) = ring[k], ring[(k + 1) % n]
        p, d, q = dirs[(k + n - 1) % n], dirs[k], dirs[(k + 1) % n]
        reflex0 = p[0] * d[1] - p[1] * d[0] < 0
        reflex1 = d[0] * q[1] - d[1] * q[0] < 0
        out.append((Frame(ax, az, math.atan2(bz - az, bx - ax)), d[2], reflex0, reflex1))
    return out


def edge_wall(E, L, reflex0, reflex1, prefab, top, name, cuts, layer="structure", thick=WALL_T, inset=0.0, plan=None,
              corner=None):
    """A wall along one side, inside it, with openings cut out of it and a lintel over each: lengthened at a
    reflex corner as far in as it reaches (or `corner`), so the inside of the corner is closed. Which pieces there are
    is decided as far in as the plan allows for (`plan`, WALL_T for an outer wall), so a wall thinner than that has the
    same pieces and ids; a piece of the plan's reach its own does not reach is kept as a 2 cm end where it stops."""
    reach = thick + inset if corner is None else max(thick + inset, corner)
    plan = reach if plan is None else plan
    p0, p1 = -plan if reflex0 else 0.0, L + (plan if reflex1 else 0.0)
    s0, s1 = -reach if reflex0 else 0.0, L + (reach if reflex1 else 0.0)
    pieces = []
    s = p0
    for c0, c1, ctop in sorted(cuts):
        if c0 > s:
            pieces.append((s, c0, 0.0, top))
        if ctop < top:
            pieces.append((c0 - BEARING, c1 + BEARING, ctop, top))
        s = c1
    if s < p1:
        pieces.append((s, p1, 0.0, top))
    made = []
    for a0, a1, y0, y1 in pieces:
        if a1 - a0 < 0.02:
            continue
        mid = (a0 + a1) / 2
        a0, a1 = max(a0, s0), min(a1, s1)
        if a1 - a0 < 0.02:
            a0, a1 = (s1 - 0.02, s1) if mid > (s0 + s1) / 2 else (s0, s0 + 0.02)
        obox(prefab, E, a0, a1, inset, inset + thick, y0, y1, name=name, layer=layer)
        made.append((entities[-1], a0, a1, y0, y1))
    return made


def edge_word(E):
    """Which way a side faces out: a quarter turn clockwise from along it."""
    ang = math.degrees(math.atan2(-E.c, E.s)) % 360
    return ["east", "northeast", "north", "northwest", "west", "southwest", "south", "southeast"][int(((ang + 22.5) % 360) // 45)]


def oform(prefab, F, outline, y0, y1, form, name=None, layer="structure"):
    """A shape filling the box round `outline` ((u, v) in frame F) from y0 to y1, written as obox writes the
    box, the outline about the box's middle (the engine fits the shape to the box)."""
    u0, u1 = min(p[0] for p in outline), max(p[0] for p in outline)
    v0, v1 = min(p[1] for p in outline), max(p[1] for p in outline)
    eid = obox(prefab, F, u0, u1, v0, v1, y0, y1, name=name, layer=layer)
    cu, cv = (u0 + u1) / 2, (v0 + v1) / 2
    f = dict(form)
    f["Outline"] = [round(c, 4) for p in outline for c in (p[0] - cu, p[1] - cv)]
    entities[-1]["Form"] = f
    return eid


def roof_of(bd, kind, mat, wall_h, label):
    """A roof's style and rise: a house a hip or a gable at about 6 in 12 (a quarter of its short side, never
    above the measured height), a metal building a low gable, a church a steep one, a brick shop flat."""
    cap = max(0.8, (bd.height or 99) - wall_h - 0.2)
    if mat == "metal":
        return "Gable", min(max(0.3, bd.short / 8), cap)
    if kind == "premises":
        return "Flat", 0.2
    if kind == "church":
        return "Gable", min(max(0.8, bd.short / 3), cap)
    if kind == "mobile_home":
        return "Gable", min(max(0.3, bd.short / 8), cap)
    return ("Hip" if h01(label, "roof") < 0.5 else "Gable"), min(max(0.8, bd.short / 4), cap)


def room_at(ids, F, x, z):
    """room_for_point at a point: the room it is in, and the room's middle; the first room if none."""
    u, v = F.l(x, z)
    for rid, (ru0, ru1, rv0, rv1) in ids:
        if ru0 <= u <= ru1 and rv0 <= v <= rv1:
            return rid, F.w((ru0 + ru1) / 2, (rv0 + rv1) / 2)
    rid, (ru0, ru1, rv0, rv1) = ids[0]
    return rid, F.w((ru0 + ru1) / 2, (rv0 + rv1) / 2)


DOOR_DEPTH = 0.7          # a doorway's room must reach this near its side, m: a step inside the leaf
GROW_REACH = 1.5          # how far out a room's side may move to meet the wall round the footprint, m


def grow_side(ring_uv, r, side):
    """Where a room's side meets the inside of the wall round the footprint, moving straight out from it: the
    rectangles are cut from a grid laid on the footprint, so a wall at an angle to them stands up to a cell off,
    and a bay or a porch too small for a rectangle of its own is left beside it. The nearest the wall comes
    along the side (looked for every half metre and at its ends), of the points where it is within GROW_REACH,
    and no further than any corner of the footprint in the way (a rectangle of whole cells may reach a little past
    the footprint: then the side comes back in); None when it is nowhere near. `side` is 0 to 3:
    u0, u1, v0, v1."""
    u0, u1, v0, v1 = r
    axis_u = side < 2
    sign = -1.0 if side in (0, 2) else 1.0
    at = (u0, u1, v0, v1)[side]
    lo, hi = (v0, v1) if axis_u else (u0, u1)
    if hi - lo < 0.2:
        return None
    best = None
    n = len(ring_uv)
    steps = max(2, int(math.ceil((hi - lo - 0.1) / 0.5)))
    for i in range(steps + 1):
        w = lo + 0.05 + (hi - lo - 0.1) * i / steps
        nearest = None
        for k in range(n):
            (pu, pv), (qu, qv) = ring_uv[k], ring_uv[(k + 1) % n]
            du, dv = qu - pu, qv - pv
            L = math.sqrt(du * du + dv * dv)
            if L < 1e-9:
                continue
            mu, mv = dv / L, -du / L                       # out of the footprint
            # Along u: the side's line v = w crosses the edge; along v, u = w.
            a0, a1, b0, b1 = (pv, qv, pu, qu) if axis_u else (pu, qu, pv, qv)
            if a1 == a0:
                continue
            s = (w - a0) / (a1 - a0)
            if s < 0.0 or s > 1.0:
                continue
            t = sign * (b0 + s * (b1 - b0) - at)
            facing = sign * (mu if axis_u else mv)
            if t < -GROW_REACH or facing <= 0.5:
                continue
            inner = t - WALL_T / facing
            if nearest is None or t < nearest[0]:
                nearest = (t, inner)
        if nearest is None or nearest[0] > GROW_REACH:
            continue
        best = nearest[1] if best is None else min(best, nearest[1])
    if best is None:
        return None
    # A corner of the footprint standing in the strip the side would sweep stops it a wall short of the corner.
    for (pu, pv) in ring_uv:
        a, b = (pv, pu) if axis_u else (pu, pv)
        t = sign * (b - at)
        if lo + 0.01 < a < hi - 0.01 and -GROW_REACH < t < best + WALL_T:
            best = min(best, t - WALL_T)
    return at + sign * max(best, -WALL_T - 0.3)


def room_boxes(F, ring, R):
    """Each rectangle's room: its sides that face out of the house moved to the inside of the footprint's wall
    where it is near, the others WALL_T in (a wall between two rectangles is a gap: an opening)."""
    ring_uv = [F.l(x, z) for x, z in ring]
    out = []
    for i, r in enumerate(R):
        box = [r[0] + WALL_T, r[1] - WALL_T, r[2] + WALL_T, r[3] - WALL_T]
        for side in range(4):
            u0, u1, v0, v1 = r
            # A side another rectangle touches (along more than a centimetre of it) is inside the house.
            touched = False
            for j, o in enumerate(R):
                if j == i:
                    continue
                facing_side = (o[1], o[0], o[3], o[2])[side]
                lap = min(v1, o[3]) - max(v0, o[2]) if side < 2 else min(u1, o[1]) - max(u0, o[0])
                if abs(facing_side - r[side]) < 1e-3 and lap > 0.01:
                    touched = True
            if touched:
                continue
            g = grow_side(ring_uv, r, side)
            if g is not None:
                box[side] = g
        out.append(tuple(box))
    return out


def covered(E, L, boxes, F, depth=DOOR_DEPTH):
    """The stretches of a side, (s0, s1) along it, whose inside `depth` in lies in one of `boxes` (rooms,
    (u0, u1, v0, v1) in frame F), overlapping stretches merged."""
    p0, p1 = F.l(*E.w(0.0, depth)), F.l(*E.w(L, depth))
    du, dv = p1[0] - p0[0], p1[1] - p0[1]
    spans = []
    for (u0, u1, v0, v1) in boxes:
        t0, t1, ok = 0.0, 1.0, True
        for p, d, lo, hi in ((p0[0], du, u0, u1), (p0[1], dv, v0, v1)):
            if abs(d) < 1e-12:
                if p < lo or p > hi:
                    ok = False
                    break
                continue
            a, b = (lo - p) / d, (hi - p) / d
            if a > b:
                a, b = b, a
            t0, t1 = max(t0, a), min(t1, b)
        if ok and t1 > t0:
            spans.append((t0 * L, t1 * L))
    spans.sort()
    merged = []
    for s0, s1 in spans:
        if merged and s0 <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], s1))
        else:
            merged.append((s0, s1))
    return merged


def edge_for_run(edges, F, run, s, width, box=None):
    """The side of the ring a door planned on a rectangle's run belongs on, and where along it: the nearest side
    facing the same way with the door's room (`box`) behind it for the door's width, or failing that the nearest
    facing the same way, kept clear of its ends."""
    x, z = run_point(F, run, s)
    axis, line, s0, s1, out = run
    nx, nz = F.w(0, out) if axis == "v" else F.w(out, 0)
    best = None
    for k, (E, L, _, _) in enumerate(edges):
        u, v = E.l(x, z)
        same = (-E.s) * nx + E.c * nz < -0.7          # its outward normal (s, -c) against the run's
        stretches = covered(E, L, [box], F) if box else []
        for a, b in stretches or [(0.0, L)]:
            half = min(width / 2 + 0.05, (b - a) / 2)
            along = min(max(u, a + half), b - half)
            fits = bool(stretches) and b - a >= width + 0.1
            cand = (0 if same and fits else 1 if same else 2, math.hypot(u - along, v), k, along)
            if best is None or cand < best:
                best = cand
    return best[2], best[3]


WALLS = {"brick": "brick_veneer_wall", "siding": "framed_siding_wall", "metal": "metal_wall", "concrete": "concrete_wall"}
STATS = defaultdict(int)
FOOTPRINTS = SpatialHash(40.0)  # every building's wall ring as built, for the surfaces laid along lines


# What build() decided for each building, written by --decisions=FILE for the test that holds the world's port of
# build() (OpenFPS.World/OneWorld/WorldBuildings.cs, WorldBuildingsTests) to this one: what to build is decided here
# from addresses and lots, the world decides it from the footprint and the roads, and the shell is the same code.
# KEEP THE TWO THE SAME: a change to build() at medium detail is a change to WorldBuildings.Build.
DECIDED = {}


def build(bd):
    """Walls, floor, roof, rooms and doors for one building."""
    a = bd.addr
    kind = bd.kind
    label = bd.place["name"] if bd.place else (a.label if a else None)
    if label is None:
        nr = nearest_road(bd.cx, bd.cz, 200.0)
        label = f"{kind.replace('_', ' ').capitalize()} off {nr[5][4]}" if nr else kind.replace("_", " ").capitalize()
    word = {"garage": "detached garage"}.get(kind, kind.replace("_", " "))
    name = label if kind in ("house", "mobile_home", "premises", "church") else \
        (f"{label}, {word}" if (a or bd.place) else label)
    detail = detail_at(bd.cx, bd.cz)
    STATS[kind] += 1
    DECIDED[id(bd)] = {"label": label, "name": name, "detail": detail,
                       "place": bd.place["kind"] if bd.place else None}

    # Small sheds, and every outbuilding at the lowest detail, are one solid of their footprint: you walk
    # into the shed, you hear "shed".
    ring = wall_ring(bd)
    if kind == "shed" or (detail <= 1 and kind in ("garage", "workshop", "barn", "outbuilding")):
        F = Frame(bd.cx, bd.cz, bd.angle)
        hgt = max(2.2, min(bd.height or 2.5, 6.0))
        oform("frame_shed" if bd.area < 30 else "metal_wall", F, [F.l(x, z) for x, z in ring], 0.0, hgt, {"Kind": "Prism"},
              name=name, layer="structure")
        if not _DROPPING[0]:
            FOOTPRINTS.add(ring, bd.x0, bd.x1, bd.z0, bd.z1)
        return

    # The rooms, in rectangles inside the footprint (a rectangle's is one, the whole of it). The walls go round
    # the footprint itself, so a rectangle round a footprint a little off square would reach out past them.
    cell = 0.5 if bd.area < 1500 else 1.0
    rects, grid, _ = cover(bd.ring, bd.angle, cell, 4 if detail >= 2 else 2, max(9.0, 0.08 * bd.area))
    if not rects:
        return
    u0g, v0g, cu, cv, nu, nv = grid
    F = Frame(0.0, 0.0, bd.angle)
    R = [(u0g + i0 * cu, u0g + i1 * cu, v0g + j0 * cv, v0g + j1 * cv) for (i0, i1, j0, j1) in rects]
    runs, mask = exterior_runs(rects, grid)

    two = (bd.height or 0) >= 6.8 and bd.area >= 110 and kind in ("house", "premises", "church")
    if kind in ("house", "mobile_home"):
        wall_h = STOREY_H * (2 if two else 1) + (0.1 if two else 0.0)
    else:
        wall_h = max(3.0, min((bd.height or 4.5) * 0.85, 9.0))
    mat = {"house": "brick" if h01(label, "walls") < float(PLACE.get("BrickShare", 0.55)) else "siding",
           "mobile_home": "siding", "church": "brick", "premises": "metal" if bd.area > 250 else "brick",
           "garage": "siding", "workshop": "metal", "barn": "metal", "outbuilding": "metal",
           "building": "brick" if h01(label, "walls") < float(PLACE.get("BrickShare", 0.55)) else "siding"}[kind]
    wall_prefab = WALLS[mat]
    roof_prefab = "metal_wall" if mat == "metal" else "shingle_roof"
    # A house built room by room has its plasterboard as a sheet of its own: its walls stop at their studs.
    board = kind in ("house", "mobile_home") and detail >= 2
    if board:
        wall_prefab = FRAMED.get(wall_prefab, wall_prefab)
    wall_t = wall_thick(wall_prefab)
    inner = wall_t + (BOARD_T if board else 0.0)      # the inside face, from the footprint

    # Where the street is, for which wall is the front.
    if a is not None and getattr(a, "g", None) is not None:
        pr = project(a.g[1], bd.cx, bd.cz)
        sx, sz = pr[4]
    else:
        nr = nearest_road(bd.cx, bd.cz, 400.0)
        sx, sz = nr[4] if nr else (bd.cx, bd.cz - 10)
    to_street = (sx - bd.cx, sz - bd.cz)
    DECIDED[id(bd)]["street"] = [sx, sz]

    def facing(run):
        axis, line, s0, s1, out = run
        n = F.w(0, out) if axis == "v" else F.w(out, 0)
        L = math.hypot(*to_street) or 1.0
        return (n[0] * to_street[0] + n[1] * to_street[1]) / L

    edges = ring_edges(ring)

    def facing_edge(k):
        E = edges[k][0]
        L = math.hypot(*to_street) or 1.0
        return (E.s * to_street[0] - E.c * to_street[1]) / L

    # ── Slab, roof: the footprint itself ────────────────────────────────────────────────────────
    homes = kind in ("house", "mobile_home")
    outline = [F.l(x, z) for x, z in ring]
    oform("concrete_floor", F, outline, -0.15, 0.10, {"Kind": "Prism"}, name=f"{name} floor", layer="structure")
    if not _DROPPING[0]:
        FOOTPRINTS.add(ring, bd.x0, bd.x1, bd.z0, bd.z1)
    if homes and detail == 1:
        finish = "wood_floor" if h01(label, "floor") < 0.5 else "carpet_floor"
        finishes_later.append((F, outline, finish, f"{name} {'floorboards' if finish == 'wood_floor' else 'carpet'}",
                               _Y_PIN[0], _TILE_PIN[0]))
    style, rise = roof_of(bd, kind, mat, wall_h, label)
    oform(roof_prefab, F, outline, wall_h, wall_h + rise, {"Kind": "Roof", "Style": style}, name=f"{name} roof", layer="structure")
    STATS[f"{style.lower()} roofs"] += 1
    boxes = room_boxes(F, ring, R)
    if homes:
        # One ceiling over the whole footprint: per room it left a gap over the opening between two rooms,
        # straight up into the eaves.
        oform("plaster_wall", F, outline, CEIL, CEIL + 0.06, {"Kind": "Prism"}, name=f"{name} ceiling", layer="structure")

    # ── Rooms ───────────────────────────────────────────────────────────────────────────────────
    ways_from = len(WAYS_MADE)        # the ways through between this building's rooms start here
    late_from = len(rooms_later)      # ...and the rooms of its leftover pieces
    cuts = defaultdict(list)          # side of the ring -> [(s0, s1, top)]
    doors_out = []                    # (side of the ring, s along it, width, prefab, (room id, room xz), name)
    if kind in ("house", "mobile_home") and detail >= 2:
        rooms = plan_house(bd, F, R, runs, mask, grid, facing, label, two, edges)
    else:
        rooms = None

    if rooms is None:
        # One room per rectangle, all called the same; the gaps between them are openings.
        suffix = {"house": ", house", "mobile_home": ", house", "premises": "", "church": "",
                  "garage": "", "workshop": "", "barn": "", "outbuilding": "", "building": ""}[kind]
        top = CEIL if homes else wall_h - 0.05
        ids = []
        for box in boxes:
            ids.append((region(f"{name}{suffix}", F, *box, FLOOR_TOP - 0.02, top, layer="rooms"), box))
        # Where two rectangles touch there is no wall: the gap between their rooms is an opening, along the side they
        # share and as wide as both rooms reach along it.
        for i in range(len(R)):
            for j in range(i + 1, len(R)):
                a, b = R[i], R[j]
                for side, (mine, theirs) in enumerate(((a[1], b[0]), (a[0], b[1]), (a[3], b[2]), (a[2], b[3]))):
                    if abs(mine - theirs) > 1e-3:
                        continue
                    bi, bj = boxes[i], boxes[j]
                    # In the middle of the gap between the two rooms' boxes, thin: a room narrower than a cell keeps
                    # its own air on its side of it.
                    g0, g1 = ((bi[1], bj[0]), (bj[1], bi[0]), (bi[3], bj[2]), (bj[3], bi[2]))[side]
                    at, depth = (g0 + g1) / 2, 0.1
                    if side < 2:
                        lo, hi = max(bi[2], bj[2]), min(bi[3], bj[3])
                        x, z = F.w(at, (lo + hi) / 2)
                        phi = F.a + math.pi / 2
                    else:
                        lo, hi = max(bi[0], bj[0]), min(bi[1], bj[1])
                        x, z = F.w((lo + hi) / 2, at)
                        phi = F.a
                    if hi - lo > 0.3:
                        portal_later(x, z, phi, hi - lo, FLOOR_TOP - 0.02, top, depth, ids[i][0], ids[j][0])
                        WAYS_MADE.append((ids[i][0], ids[j][0], open_width(F, bd.ring, "u" if side < 2 else "v", at, lo, hi)))
        wings(F, bd.ring, grid, [(ids[i][0], R[i]) for i in range(len(R))], top, lambda area: f"{name}{suffix}")
        # The front door: the longest stretch of a side facing the street with a room behind it, in its middle.
        stretch = []
        for k in range(len(edges)):
            spans = covered(edges[k][0], edges[k][1], boxes, F)
            stretch.append(max(spans, key=lambda sp: (sp[1] - sp[0], -sp[0])) if spans else None)
        roomy = [k for k in range(len(edges)) if stretch[k] and stretch[k][1] - stretch[k][0] >= 1.4]
        if not roomy:
            roomy = list(range(len(edges)))
            stretch = [(0.0, edges[k][1]) for k in roomy]
        fronts = sorted(roomy, key=lambda k: (-(facing_edge(k) > 0.5) * (stretch[k][1] - stretch[k][0]), -facing_edge(k)))
        k = fronts[0]
        E = edges[k][0]
        s0, s1 = stretch[k]
        L, s = s1 - s0, (s0 + s1) / 2
        if kind == "barn":
            width, prefab, dname = min(3.0, L - 1.0), None, f"{name} doorway"
        elif kind == "premises" and bd.place and bd.place["kind"] not in ("fire_station", "storage_facility", "rv_park", "self_storage"):
            width, prefab, dname = 1.0, "glass_pull_door", f"{name} entrance"
        elif kind == "church":
            width, prefab, dname = 1.0, "door", f"{name} front door"
        elif kind in ("house", "mobile_home"):
            width, prefab, dname = 0.95, "door", f"{name} front door"
        else:
            width, prefab, dname = 0.95, "door", f"{name} door"
        width = max(0.8, min(width, L - 0.6))
        doors_out.append((k, s, width, prefab, room_at(ids, F, *E.w(s, DOOR_DEPTH)), dname))
        if homes and detail >= 2:
            backs = sorted(range(len(edges)), key=lambda k: (facing_edge(k), -edges[k][1]))
            kb = backs[0]
            Eb, Lb = edges[kb][0], edges[kb][1]
            if kb != k and Lb > 2.0:
                sb = Lb * 0.3
                doors_out.append((kb, sb, 0.9, "patio_door", room_at(ids, F, *Eb.w(sb, WALL_T + 0.3)), f"{name} back door"))
    else:
        wings(F, bd.ring, grid, rooms["rects"], CEIL, lambda area: f"{label}, {'den' if area >= 12 else 'closet'}")
        # The plan's doors are on its rectangles' runs: each goes on the side of the ring it lies on.
        for (kr, s, width, prefab, room, dname) in rooms["doors"]:
            k, s = edge_for_run(edges, F, runs[kr], s, width, rooms["boxes"].get(room[0]))
            doors_out.append((k, s, width, prefab, room, dname))

    # A door with another building, or a wing of its own, in the way a step outside it is moved along its wall to
    # the nearest spot clear of it (Cody's listen, 2026-10-10: the front doors of four houses opened into a shed),
    # its room still behind it and its wall in as many pieces, so no part of the map changes its id.
    boxes_of = rooms["boxes"] if rooms is not None else {}
    for n, (k, s, width, prefab, room, dname) in enumerate(doors_out):
        if prefab == "garage" or door_clear(bd, edges[k][0], s, width):
            continue
        taken = [d[0] for m, d in enumerate(doors_out) if m != n]
        moved = None
        for step in range(1, 41):
            for sign in (1, -1):
                s2 = s + sign * step * 0.25
                if not (same_pieces(edges, (k, s), (k, s2), width, taken) and door_clear(bd, edges[k][0], s2, width)):
                    continue
                inside = edges[k][0].w(s2, DOOR_DEPTH)
                if rooms is None:
                    if room_at(ids, F, *inside)[0] != room[0]:
                        continue
                elif room[0] in boxes_of:
                    iu0, iu1, iv0, iv1 = boxes_of[room[0]]
                    u, v = F.l(*inside)
                    if not (iu0 <= u <= iu1 and iv0 <= v <= iv1) or not inside_clear(F, edges[k][0], s2, width, boxes_of[room[0]]):
                        continue
                moved = s2
                break
            if moved is not None:
                break
        if moved is None:
            STATS["doors with something in the way outside"] += 1
            continue
        doors_out[n] = (k, moved, width, prefab, room, dname)

    for (k, s, width, prefab, room, dname) in doors_out:
        cuts[k].append((s - width / 2, s + width / 2, FLOOR_TOP + 2.25 if prefab == "garage" else head_over(prefab) if prefab else DOOR_TOP))

    pieces = defaultdict(list)         # side of the ring -> [(entity, a0, a1, y0, y1, prefab, inset, thick, top, layer)]
    for k, (E, L, reflex0, reflex1) in enumerate(edges):
        for pc in edge_wall(E, L, reflex0, reflex1, wall_prefab, wall_h, f"{name} {edge_word(E)} wall", cuts.get(k, []), thick=wall_t,
                            plan=PLAN_REACH[0], corner=CORNER_REACH):
            pieces[k].append(pc + (wall_prefab, 0.0, wall_t, wall_h, "structure"))
        if board:
            # Plasterboard on the inside of the outer wall: the second leaf over its studs, and the membrane that takes
            # the bass out of a room.
            for pc in edge_wall(E, L, reflex0, reflex1, BOARD, CEIL, f"{name} {edge_word(E)} wall",
                                [(c0, c1, min(ct, CEIL)) for c0, c1, ct in cuts.get(k, [])], layer="interiors", thick=BOARD_T, inset=wall_t,
                                plan=PLAN_REACH[1]):
                pieces[k].append(pc + (BOARD, wall_t, BOARD_T, CEIL, "interiors"))

    for (k, s, width, prefab, room, dname) in doors_out:
        E = edges[k][0]
        x, z = E.w(s, inner / 2)
        rid, rxz = room
        outside = -1                      # the outdoors: yards and roads are names, not rooms
        if prefab != "garage":
            steps_out(E, s, width, dname)
        if prefab is None:
            # An open doorway: the gap is the opening, to the outdoors.
            portal_later(x, z, E.a, width, FLOOR_TOP, DOOR_TOP, inner, rid, outside)
            continue
        if prefab == "garage":
            # A sectional door, shut: a steel panel in the opening.
            G = Frame(x, z, E.a)
            obox("metal_wall", G, -width / 2, width / 2, -0.03, 0.03, 0.0, FLOOR_TOP + 2.25, name=dname, layer="structure")
            continue
        door(prefab, x, z, FLOOR_TOP, E.a, rid, outside, width, rxz, dname)
        STATS["doors"] += 1
    if rooms is not None:
        for d in rooms["inner"]:
            d()

    # Every room on foot from outside: one that no way wide enough for a body reaches gets a door of its own.
    walk_rooms = [(rid, box, lambda rid=rid: rid) for rid, box in (rooms["boxes"].items() if rooms is not None else ids)]
    # The rooms of its leftover pieces too, written later: a piece touching no room has no way in at all.
    for n, r in enumerate(rooms_later[late_from:], late_from):
        walk_rooms.append((("piece", n), (r[2], r[3], r[4], r[5]), lambda r=r: r[10]))
    # A house's plan says its ways through; the openings to its leftover pieces can run along a partition.
    ways = list(rooms["walk"]) if rooms is not None else [w for w in WAYS_MADE[ways_from:]]
    for (k, s, width, prefab, room, dname) in doors_out:
        if prefab != "garage":
            ways.append((room[0], -1, width))
    side_doors(bd, name, F, edges, walk_rooms, ways, pieces, facing_edge, inner)


def room_for_point(ids, F, run, s):
    x, z = run_point(F, run, s, -WALL_T - 0.3)
    for rid, (ru0, ru1, rv0, rv1) in ids:
        u, v = F.l(x, z)
        if ru0 <= u <= ru1 and rv0 <= v <= rv1:
            cx, cz = F.w((ru0 + ru1) / 2, (rv0 + rv1) / 2)
            return rid, (cx, cz)
    rid, (ru0, ru1, rv0, rv1) = ids[0]
    return rid, F.w((ru0 + ru1) / 2, (rv0 + rv1) / 2)


def side_word(F, run):
    axis, line, s0, s1, out = run
    nx, nz = F.w(0, out) if axis == "v" else F.w(out, 0)
    nx, nz = nx - F.ox, nz - F.oz
    ang = math.degrees(math.atan2(nz, nx)) % 360
    return ["east", "northeast", "north", "northwest", "west", "southwest", "south", "southeast"][int(((ang + 22.5) % 360) // 45)]


# ── A house's rooms ───────────────────────────────────────────────────────────────────────────────
#
# A ranch house, laid out the way most of them are: the garage at one end of the front, the living
# room behind the front door, the kitchen behind that with the back door, and the bedrooms down a hall
# at the other end, the bathroom between them. A narrow house (a single-wide) is a row of rooms end to
# end. A wing of the footprint is a garage if it is the size of one and the house has none, and a den
# otherwise. Then every room is checked to be reachable from the front door, and a doorway is cut to a
# neighbour for any that is not. The plan is invented; the footprint it is cut from is real.
WAY_SIDE = 0.35           # floor kept clear past each jamb of a way through: a body's width and a hand, m
WAY_DEPTH = 1.2           # ...and out from the wall's line on each side: the wall and a step to it, m
BACK_DOOR_IN = 1.05       # a back door's middle from the end of its wall: the corner, a jamb and half the leaf, m
COUNTER_MIN = 1.2         # the shortest kitchen counter, m


def clear_of(axis, line, s, width):
    """The floor (u0, u1, v0, v1) a way through `width` wide at `s` along a wall on `line` needs clear on both sides."""
    half = width / 2 + WAY_SIDE
    if axis == "v":
        return (s - half, s + half, line - WAY_DEPTH, line + WAY_DEPTH)
    return (line - WAY_DEPTH, line + WAY_DEPTH, s - half, s + half)


def clear_run(c, ways, shortest):
    """The longest piece of a box along its long side that stands in none of `ways`, or None if none is `shortest`."""
    u0, u1, v0, v1 = c
    along_u = (u1 - u0) >= (v1 - v0)
    lo, hi, a0, a1 = (u0, u1, v0, v1) if along_u else (v0, v1, u0, u1)
    pieces = [(lo, hi)]
    for w in ways:
        wl, wh, wa0, wa1 = (w[0], w[1], w[2], w[3]) if along_u else (w[2], w[3], w[0], w[1])
        if wa1 <= a0 or wa0 >= a1:
            continue
        pieces = [p for q in pieces for p in ((q[0], min(q[1], wl)), (max(q[0], wh), q[1])) if p[1] - p[0] > 1e-6]
    best = max(pieces, key=lambda p: (p[1] - p[0], -p[0]), default=None)
    if best is None or best[1] - best[0] < shortest:
        return None
    return (best[0], best[1], v0, v1) if along_u else (u0, u1, best[0], best[1])


def inside_clear(F, E, s, width, box):
    """Whether a body a step inside a doorway at `s` along the wall E has its room's floor beside it on both sides, past
    each jamb: a door clamped to the end of its room's stretch of wall stood against the partition there."""
    for u in (s - width / 2 - 0.3, s + width / 2 + 0.3):
        uu, vv = F.l(*E.w(u, DOOR_DEPTH))
        if not (box[0] - 1e-6 <= uu <= box[1] + 1e-6 and box[2] - 1e-6 <= vv <= box[3] + 1e-6):
            return False
    return True


def door_clear(bd, E, s, width):
    """Whether the ground a step outside a doorway at `s` along the wall E (v into the building) is clear of any
    building, its own included (a wing round a corner), across the steps' width and out to a metre."""
    half = width / 2 + STEP_SIDE
    for u in (s - half, s, s + half):
        for v in (-0.35, -1.0):
            x, z = E.w(u, v)
            if pip(x, z, bd.ring):
                return False
            for o in B_INDEX.near(x - 0.4, x + 0.4, z - 0.4, z + 0.4):
                if o is not bd and (pip(x, z, o.ring) or ring_distance(x, z, o.ring) < 0.3):
                    return False
    return True


# The ways through between the rooms of the building being made, (room, room, how wide): the openings between its
# rectangles and its leftover pieces (wings), and a house's inner doors and archways.
WAYS_MADE = []
UNWALKED = []             # the rooms side_doors found no wall for, by name, for the summary
WALK_WIDE = 0.65          # the narrowest way through a body (0.6 m) takes, m
SIDE_DOOR = 0.9           # the width of a door given to a room no way reaches

# A side door's leaf and the wall round it, written after the openings and before the steps, so adding them moves no
# other part's id: (the pad's height, the tile, what makes them).
doors_later = []


def open_width(F, ring, axis, line, s0, s1, step=0.05):
    """How wide a way a body has through an opening with no wall in it, along `line` (u if `axis` is "u") from s0 to
    s1 in F: the longest run of it a body's middle can stand on, clear of the walls inside the ring there and a step
    either side of it, and a body's width less a hand for the corners of the walls (one clear point of 5 cm, measured
    off the ring, was not one a body got through); 0 if there is none. Two rooms' boxes can meet along more than the footprint
    is wide there (a neck)."""
    n = max(1, int((s1 - s0) / step))
    run = best = 0
    for i in range(n + 1):
        t = s0 + (s1 - s0) * i / n
        sides = [F.w(line + d, t) if axis == "u" else F.w(t, line + d) for d in (0.0, -0.6, 0.6)]
        if all(pip(x, z, ring) and ring_distance(x, z, ring) >= WALL_T + 0.3 for x, z in sides):
            run += 1
            best = max(best, run)
        else:
            run = 0
    return 0.0 if best == 0 else (best - 1) * (s1 - s0) / n + 0.5


def reshape(e, F, u0, u1, v0, v1, y0, y1):
    """An entity obox made, made again as the box it now fills, keeping its id, name and tile."""
    bx, by, bz = BASE[e["PrefabId"]]
    cx, cz = F.w((u0 + u1) / 2, (v0 + v1) / 2)
    e["Position"] = v3(cx, (y0 + y1) / 2, cz)
    e["Scale"] = v3((u1 - u0) / bx, (y1 - y0) / by, (v1 - v0) / bz)
    e["_box"] = (F, u0, u1, v0, v1, y0, y1)


def side_doors(bd, name, F, edges, rooms, ways, pieces, facing_edge, depth):
    """A door to the outdoors for every room no way wide enough for a body reaches from outside: a part of a footprint
    joined to the rest by less than a body's width, or not at all, or a garage whose only door in was cut from a
    stretch of partition too short for one (Magnolia and Albany, 2026-10-10: eight such rooms no one could walk into).
    It goes in the stretch of outside wall behind the room facing the street most, then the longest, where nothing
    stands outside it; the piece of wall it is cut from keeps its id, and the rest is written later."""
    ways = list(ways)

    def reached():
        reach, grew = {-1}, True
        while grew:
            grew = False
            for a, b, w in ways:
                if w >= WALK_WIDE and (a in reach) != (b in reach):
                    reach |= {a, b}
                    grew = True
        return reach
    for rid, box, id_of in rooms:
        if rid in reached() or min(box[1] - box[0], box[3] - box[2]) < 2 * 0.3 + 0.05:
            continue
        best = None
        for k, (E, L, _, _) in enumerate(edges):
            for c0s, c1s in covered(E, L, [box], F):
                for n, (e, a0, a1, y0, y1, prefab, inset, thick, top, layer) in enumerate(pieces[k]):
                    if inset != 0.0 or y0 > 0.0:
                        continue
                    lo, hi = max(c0s, a0) + 0.35 + SIDE_DOOR / 2, min(c1s, a1) - 0.35 - SIDE_DOOR / 2
                    if hi < lo:
                        continue
                    mid = (lo + hi) / 2
                    for step in range(0, 200):
                        at = [mid + step * 0.25, mid - step * 0.25]
                        hit = next((s2 for s2 in at if lo <= s2 <= hi and door_clear(bd, E, s2, SIDE_DOOR)), None)
                        if hit is not None:
                            cand = (facing_edge(k) > 0.5, hi - lo, -abs(hit - mid), -k, k, n, hit)
                            if best is None or cand > best:
                                best = cand
                            break
                        if mid + step * 0.25 > hi and mid - step * 0.25 < lo:
                            break
        if best is None:
            STATS["rooms no one can walk into"] += 1
            UNWALKED.append(f"{name} room {rid}")
            continue
        k, n, at = best[4], best[5], best[6]
        E = edges[k][0]
        c0, c1 = at - SIDE_DOOR / 2, at + SIDE_DOOR / 2
        later = []
        # The wall, and the plaster on its inside, round the doorway: each piece keeps its first part.
        for e, a0, a1, y0, y1, prefab, inset, thick, top, layer in pieces[k]:
            if y0 > 0.0 or a0 > c0 - 0.02 or a1 < c1 + 0.02:
                continue
            reshape(e, E, a0, c0, inset, inset + thick, 0.0, top)
            head = min(head_over("door"), top)
            later.append((prefab, E, c0 - BEARING, c1 + BEARING, inset, inset + thick, head, top, e["Name"], layer))
            later.append((prefab, E, c1, a1, inset, inset + thick, 0.0, top, e["Name"], layer))
        x, z = E.w(at, depth / 2)
        rxz = F.w((box[0] + box[1]) / 2, (box[2] + box[3]) / 2)
        dname = f"{name} side door"

        def make(later=later, x=x, z=z, a=E.a, id_of=id_of, rxz=rxz, dname=dname):
            for prefab, G, u0, u1, v0, v1, y0, y1, nm, layer in later:
                obox(prefab, G, u0, u1, v0, v1, y0, y1, name=nm, layer=layer)
            door("door", x, z, FLOOR_TOP, a, id_of(), -1, SIDE_DOOR, rxz, dname)
        doors_later.append((_Y_PIN[0], _TILE_PIN[0], make))
        steps_out(E, at, SIDE_DOOR, dname)
        ways.append((rid, -1, SIDE_DOOR))
        STATS["side doors"] += 1


def same_pieces(edges, a, b, width, taken):
    """Whether a door at `b` (side of the ring, s along it) leaves its wall in the pieces a door at `a` does: the same
    side, one no other outside door is on, and a piece of wall (and of its plaster) on each side of it, or none,
    alike (edge_wall), each lengthened at a reflex corner as far in as it reaches."""
    if a[0] != b[0] or a[0] in taken:
        return False
    E, L, reflex0, reflex1 = edges[a[0]]
    for thick in PLAN_REACH:
        start, end = (-thick if reflex0 else 0.0), L + (thick if reflex1 else 0.0)
        if ((a[1] - width / 2 - start >= 0.02) != (b[1] - width / 2 - start >= 0.02)
                or (end - (a[1] + width / 2) >= 0.02) != (end - (b[1] + width / 2) >= 0.02)):
            return False
    return True


def walk_between(room, box, points, cell=0.1):
    """Whether a body (0.3 m round) can walk from every one of `points` to every other across `room` with `box`
    standing in it (all in one frame, (u0, u1, v0, v1)): a bed across a narrow room cut the way from the door to the
    next room's (Albany, 2026-10-10: four dens behind a main bedroom no one could cross)."""
    r0, r1, q0, q1 = room[0] + 0.3, room[1] - 0.3, room[2] + 0.3, room[3] - 0.3
    if r1 <= r0 or q1 <= q0:
        return len(points) <= 1
    nu, nv = max(1, int((r1 - r0) / cell) + 1), max(1, int((q1 - q0) / cell) + 1)
    b0, b1, c0, c1 = box[0] - 0.3, box[1] + 0.3, box[2] - 0.3, box[3] + 0.3

    def free(i, j):
        u, v = r0 + i * cell, q0 + j * cell
        return not (b0 - 0.02 <= u <= b1 + 0.02 and c0 - 0.02 <= v <= c1 + 0.02)

    def cell_of(p):
        return (min(nu - 1, max(0, int(round((p[0] - r0) / cell)))), min(nv - 1, max(0, int(round((p[1] - q0) / cell)))))
    starts = [cell_of(p) for p in points]
    if any(not free(*c) for c in starts):
        return False
    seen = {starts[0]}
    stack = [starts[0]]
    while stack:
        i, j = stack.pop()
        for a, b in ((i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1)):
            if 0 <= a < nu and 0 <= b < nv and (a, b) not in seen and free(a, b):
                seen.add((a, b))
                stack.append((a, b))
    return all(c in seen for c in starts)


def ways_into(room, ways):
    """Where each way through that reaches into a room meets its floor: the middle of what the two share."""
    out = []
    for w in ways:
        u0, u1, v0, v1 = max(room[0], w[0]), min(room[1], w[1]), max(room[2], w[2]), min(room[3], w[3])
        if u1 > u0 and v1 > v0:
            out.append(((u0 + u1) / 2, (v0 + v1) / 2))
    return out


def clear_spot(box, room, ways):
    """`box` moved as little as it can be within `room` (both (u0, u1, v0, v1)) to stand in none of `ways` and leave
    every way into the room walkable to every other, or None."""
    points = ways_into(room, ways)
    u0, u1, v0, v1 = box
    r0, r1, q0, q1 = room
    cands = []
    for du in [k * 0.25 for k in range(-24, 25)]:
        if u0 + du < r0 - 1e-6 or u1 + du > r1 + 1e-6:
            continue
        for dv in [k * 0.25 for k in range(-24, 25)]:
            if v0 + dv < q0 - 1e-6 or v1 + dv > q1 + 1e-6:
                continue
            moved = (u0 + du, u1 + du, v0 + dv, v1 + dv)
            if any(moved[0] < w[1] and moved[1] > w[0] and moved[2] < w[3] and moved[3] > w[2] for w in ways):
                continue
            cands.append((du * du + dv * dv, abs(du), du, dv))
    for _, _, du, dv in sorted(cands):
        moved = (u0 + du, u1 + du, v0 + dv, v1 + dv)
        if walk_between(room, moved, points):
            return moved
    return None


def plan_house(bd, F, R, runs, mask, grid, facing, label, two, edges):
    # The main rectangle, and which of its sides faces the street.
    main_i = max(range(len(R)), key=lambda i: (R[i][1] - R[i][0]) * (R[i][3] - R[i][2]))
    ru0, ru1, rv0, rv1 = R[main_i]
    best = None
    for key, run in (("v-", ("v", rv0, 0, 0, -1)), ("v+", ("v", rv1, 0, 0, 1)),
                     ("u-", ("u", ru0, 0, 0, -1)), ("u+", ("u", ru1, 0, 0, 1))):
        dot = facing(run)
        if best is None or dot > best[0]:
            best = (dot, key)
    front = best[1]
    # (a, b): a along the front, b from the front wall back.
    W = (ru1 - ru0) if front[0] == "v" else (rv1 - rv0)
    D = (rv1 - rv0) if front[0] == "v" else (ru1 - ru0)

    def uv(a_, b_):
        if front == "v-":
            return ru0 + a_, rv0 + b_
        if front == "v+":
            return ru1 - a_, rv1 - b_
        if front == "u-":
            return ru0 + b_, rv1 - a_
        return ru1 - b_, rv0 + a_

    def rect(a0, a1, b0, b1):
        p, q = uv(a0, b0), uv(a1, b1)
        return (min(p[0], q[0]), max(p[0], q[0]), min(p[1], q[1]), max(p[1], q[1]))

    rooms = []        # [name, (u0, u1, v0, v1), floor prefab or None, (a0, a1, b0, b1) or None]

    def add(nm, a0, a1, b0, b1, floor):
        rooms.append([nm, rect(a0, a1, b0, b1), floor, (a0, a1, b0, b1)])
        return len(rooms) - 1
    seed = label
    garage_end = None
    narrow = W < 6.0 or D < 6.0
    if not narrow and W >= 15.0 and D >= 7.0 and W * D >= 120 and bd.kind == "house":
        garage_end = 0 if h01(seed, "drive") < 0.5 else 1
    gw = 6.6 if W < 20 else 7.2
    gd = min(D, 7.4)
    a_lo, a_hi = 0.0, W
    if garage_end is not None:
        g0, g1 = (0.0, gw) if garage_end == 0 else (W - gw, W)
        add("garage", g0, g1, 0.0, gd, None)
        if D - gd > 2.4:
            add("utility room", g0, g1, gd, D, "tile_floor")
        a_lo, a_hi = (gw, W) if garage_end == 0 else (0.0, W - gw)
    Wm = a_hi - a_lo
    carpet = "carpet_floor"
    living_floor = "wood_floor" if h01(seed, "floor") < 0.6 else "carpet_floor"
    if narrow:
        # A row of rooms end to end, the way a single-wide is built.
        plan = {2: [("living room", living_floor), ("kitchen", "tile_floor")],
                3: [("main bedroom", carpet), ("living room", living_floor), ("kitchen", "tile_floor")],
                4: [("main bedroom", carpet), ("bathroom", "tile_floor"), ("living room", living_floor), ("kitchen", "tile_floor")],
                5: [("main bedroom", carpet), ("bathroom", "tile_floor"), ("living room", living_floor), ("kitchen", "tile_floor"),
                    ("bedroom 2", carpet)]}[max(2, min(5, int(Wm // 3.0)))]
        fixed = sum(2.4 for nm, _ in plan if nm == "bathroom")
        each = (Wm - fixed) / sum(1 for nm, _ in plan if nm != "bathroom")
        a = a_lo
        for nm, floor in plan:
            w = 2.4 if nm == "bathroom" else each
            add(nm, a, a + w, 0.0, D, floor)
            a += w
    else:
        # The public end next to the garage, the bedrooms at the other.
        Wp = max(4.5, min(Wm - 3.0, Wm * 0.45))
        if garage_end == 0 or (garage_end is None and h01(seed, "mirror") < 0.5):
            pa0, pa1, ra0, ra1 = a_lo, a_lo + Wp, a_lo + Wp, a_hi
        else:
            pa0, pa1, ra0, ra1 = a_hi - Wp, a_hi, a_lo, a_hi - Wp
        Wr = ra1 - ra0
        if Wr < 3.0:
            pa0, pa1 = a_lo, a_hi
        dl = D * 0.55 if D >= 8.0 else D
        add("living room", pa0, pa1, 0.0, dl, living_floor)
        if dl < D:
            add("kitchen", pa0, pa1, dl, D, "tile_floor")
        public_high = pa0 >= ra1 - 1e-6           # the public end is at larger a
        if Wr >= 3.0:
            if D >= 9.0 and Wr >= 6.0:
                hall_w = 1.1
                hb0 = (D - hall_w) * 0.5
                add("hall", ra0, ra1, hb0, hb0 + hall_w, carpet)
                nfront = 2 if Wr >= 7.0 else 1
                fw = Wr / nfront
                for k in range(nfront):
                    add(f"bedroom {k + 2}", ra0 + k * fw, ra0 + (k + 1) * fw, 0.0, hb0, carpet)
                bath_w = 2.6
                if public_high:
                    add("bathroom", ra1 - bath_w, ra1, hb0 + hall_w, D, "tile_floor")
                    add("main bedroom", ra0, ra1 - bath_w, hb0 + hall_w, D, carpet)
                else:
                    add("bathroom", ra0, ra0 + bath_w, hb0 + hall_w, D, "tile_floor")
                    add("main bedroom", ra0 + bath_w, ra1, hb0 + hall_w, D, carpet)
            else:
                bath_w = min(2.4, Wr * 0.35)
                if public_high:
                    add("bathroom", ra1 - bath_w, ra1, 0.0, D, "tile_floor")
                    add("main bedroom", ra0, ra1 - bath_w, 0.0, D, carpet)
                else:
                    add("bathroom", ra0, ra0 + bath_w, 0.0, D, "tile_floor")
                    add("main bedroom", ra0 + bath_w, ra1, 0.0, D, carpet)
    living = next(i for i, r in enumerate(rooms) if r[0] == "living room")
    kitchen = next((i for i, r in enumerate(rooms) if r[0] == "kitchen"), living)
    # Wings.
    has_garage = any(r[0] == "garage" for r in rooms)
    for i, (wu0, wu1, wv0, wv1) in enumerate(R):
        if i == main_i:
            continue
        a_, b_ = wu1 - wu0, wv1 - wv0
        if not has_garage and 5.5 <= min(a_, b_) and max(a_, b_) <= 9.0 and a_ * b_ >= 30 and bd.kind == "house":
            rooms.append(["garage", (wu0, wu1, wv0, wv1), None, None])
            has_garage = True
        else:
            rooms.append(["den" if a_ * b_ >= 12 else "closet", (wu0, wu1, wv0, wv1), carpet, None])

    # Shared edges between rooms: where the partitions go, and the inner doors.
    def shared(r1, r2):
        (a0, a1, b0, b1), (c0, c1, d0, d1) = r1, r2
        for line, ok in ((a1, abs(a1 - c0) < 1e-6), (a0, abs(c1 - a0) < 1e-6)):
            if ok:
                s0, s1 = max(b0, d0), min(b1, d1)
                if s1 - s0 > 0.3:
                    return ("u", line, s0, s1)
        for line, ok in ((b1, abs(b1 - d0) < 1e-6), (b0, abs(d1 - b0) < 1e-6)):
            if ok:
                s0, s1 = max(a0, c0), min(a1, c1)
                if s1 - s0 > 0.3:
                    return ("v", line, s0, s1)
        return None
    n = len(rooms)
    adj = {}
    for i in range(n):
        for j in range(i + 1, n):
            sh = shared(rooms[i][1], rooms[j][1])
            if sh:
                adj[(i, j)] = sh
    # The ways through: an open archway between the living room and the kitchen and into the hall; a
    # door into anything private, and from the garage into the house.
    names = [r[0] for r in rooms]
    living_hall = any({names[i], names[j]} == {"living room", "hall"} for (i, j) in adj)
    private = {"bathroom", "main bedroom", "bedroom 2", "bedroom 3"}
    links = {}
    garage_linked = False
    for (i, j) in sorted(adj):
        pair = {names[i], names[j]}
        if pair == {"living room", "kitchen"}:
            links[(i, j)] = ("open", 1.6)
        elif pair == {"living room", "hall"} or (pair == {"kitchen", "hall"} and not living_hall):
            links[(i, j)] = ("open", 1.0)
        elif "hall" in pair and pair & private:
            links[(i, j)] = ("door", 0.8)
        elif "garage" in pair and pair & {"kitchen", "utility room", "living room"} and not garage_linked:
            links[(i, j)] = ("door", 0.85)
            garage_linked = True
        elif pair == {"utility room", "kitchen"}:
            links[(i, j)] = ("door", 0.8)
        elif not any(nm == "hall" for nm in names) and pair & {"living room", "kitchen"} and pair & private:
            links[(i, j)] = ("door", 0.8)
        elif pair == {"bathroom", "main bedroom"} and not any(nm == "hall" for nm in names):
            links[(i, j)] = ("door", 0.8)
    # Every room reachable from the living room, where the front door is.
    reach = {living}
    while True:
        grew = True
        while grew:
            grew = False
            for (i, j) in links:
                if (i in reach) != (j in reach):
                    reach |= {i, j}
                    grew = True
        missing = [(k, sh) for k, sh in sorted(adj.items(), key=lambda kv: (-(kv[1][3] - kv[1][2]), kv[0]))
                   if (k[0] in reach) != (k[1] in reach)]
        if not missing:
            break
        links[missing[0][0]] = ("door", 0.8)

    # The rooms as places.
    def inset(axis, val, rr):
        for run in runs:
            if run[0] == axis and abs(run[1] - val) < 1e-6:
                lo, hi = (rr[0], rr[1]) if axis == "v" else (rr[2], rr[3])
                if min(hi, run[3]) - max(lo, run[2]) > 0.3:
                    return WALL_T
        return PART_T / 2
    ids, centres, inner_rects = [], [], []
    ring_uv = [F.l(x, z) for x, z in wall_ring(bd)]
    for nm, rr, floor, ab in rooms:
        u0, u1, v0, v1 = rr
        sides = [u0 + inset("u", u0, rr), u1 - inset("u", u1, rr), v0 + inset("v", v0, rr), v1 - inset("v", v1, rr)]
        # A side on the outside of the house meets the wall round the footprint (room_boxes).
        for side, (axis, val) in enumerate((("u", u0), ("u", u1), ("v", v0), ("v", v1))):
            if inset(axis, val, rr) == WALL_T:
                g = grow_side(ring_uv, rr, side)
                if g is not None:
                    sides[side] = g
        iu0, iu1, iv0, iv1 = sides
        rid = region(f"{label}, {nm}", F, iu0, iu1, iv0, iv1, FLOOR_TOP - 0.02, CEIL, layer="rooms")
        ids.append(rid)
        centres.append(F.w((iu0 + iu1) / 2, (iv0 + iv1) / 2))
        inner_rects.append((iu0, iu1, iv0, iv1))
        if floor:
            obox(floor, F, iu0, iu1, iv0, iv1, 0.10, FLOOR_TOP, name=f"{label} {nm} floor", layer="interiors")

    # The outside doors, on the outer runs: decided before anything stands in a room, so nothing does where
    # somebody has to walk to one.
    def on_run(rr, toward_street):
        """The outer run one of a room's sides lies on, most toward (or away from) the street:
        (score, overlap, run index, middle of the overlap, start of it, end of it)."""
        u0, u1, v0, v1 = rr
        best = None
        for k, run in enumerate(runs):
            axis, line, s0, s1, out = run
            sides = ((v0, -1, u0, u1), (v1, 1, u0, u1)) if axis == "v" else ((u0, -1, v0, v1), (u1, 1, v0, v1))
            for val, o, lo_, hi_ in sides:
                if abs(line - val) < 1e-6 and out == o:
                    lo, hi = max(s0, lo_), min(s1, hi_)
                    if hi - lo > 1.2:
                        f = facing(run)
                        cand = (round(f if toward_street else -f, 6), hi - lo, k, (lo + hi) / 2, lo, hi)
                        if best is None or cand > best:
                            best = cand
        return best
    doors_out = []
    fr = on_run(rooms[living][1], True)
    if fr is None:
        for i in range(len(rooms)):
            fr = on_run(rooms[i][1], True)
            if fr and rooms[i][0] != "garage":
                living = i
                break
    if fr:
        doors_out.append((fr[2], fr[3], 0.95, "door", (ids[living], centres[living]), f"{label} front door"))
    boxes_by_id = dict(zip(ids, inner_rects))

    def placed(entry):
        """The side of the ring an outside door goes on and how far along it, as build() puts it (edge_for_run)."""
        kr, s, width, prefab, room, dname = entry
        return edge_for_run(edges, F, runs[kr], s, width, boxes_by_id.get(room[0]))

    bk = on_run(rooms[kitchen][1], False)
    if bk and (not fr or bk[2] != fr[2]):
        back = (bk[2], bk[3], 0.9, "patio_door", (ids[kitchen], centres[kitchen]), f"{label} back door")
        # Toward one end of the kitchen's back wall, as a kitchen's back door is, so the counter has the rest of the
        # wall (Cody, 2026-10-10: on Bobcat Lane the counter stood across the middle of it); only where that leaves the
        # door on the same side of the ring with its wall in as many pieces, so no part of the map changes its id.
        lo, hi = bk[4], bk[5]
        inset = min(BACK_DOOR_IN, (hi - lo) / 2)
        moved = (bk[2], lo + inset if h01(seed, "back door") < 0.5 else hi - inset) + back[2:]
        if same_pieces(edges, placed(back), placed(moved), 0.9, [placed(d)[0] for d in doors_out]) \
                and inside_clear(F, edges[placed(moved)[0]][0], placed(moved)[1], 0.9, boxes_by_id[ids[kitchen]]):
            back = moved
        doors_out.append(back)
    for i, (nm, rr, floor, ab) in enumerate(rooms):
        if nm == "garage":
            g = on_run(rr, True)
            if g and g[1] >= 3.0 and not (fr and g[2] == fr[2] and abs(g[3] - fr[3]) < 3.0):
                doors_out.append((g[2], g[3], min(4.9, g[1] - 0.6), "garage", (ids[i], centres[i]), f"{label} garage door"))

    # Where each way through is, with the floor a body crosses to reach it on either side: the inner doors and
    # archways where the partitions will be cut, and the outside doors where build() will put them.
    ways = []
    for (i, j), (axis, line, s0, s1) in adj.items():
        if (i, j) in links:
            wdt = min(links[(i, j)][1], s1 - s0 - 0.3)
            ways.append(clear_of(axis, line, (s0 + s1) / 2, wdt))
    for entry in doors_out:
        k, s = placed(entry)
        u, v = F.l(*edges[k][0].w(s, 0.0))
        axis = runs[entry[0]][0]
        ways.append(clear_of(axis, v, u, entry[2]) if axis == "v" else clear_of(axis, u, v, entry[2]))

    # Furniture: upholstery with air behind it is what takes the bass out of a room
    # (carpet-is-deaf-to-bass), and a kitchen has a counter along its back wall, clear of every way through.
    for (nm, rr, floor, ab), (iu0, iu1, iv0, iv1) in zip(rooms, inner_rects):
        cu_, cv_ = (iu0 + iu1) / 2, (iv0 + iv1) / 2
        if iu1 - iu0 < 2.4 or iv1 - iv0 < 2.4:
            continue
        if nm == "living room" or "bedroom" in nm:
            sofa = nm == "living room"
            want = (cu_ - 1.0, cu_ + 1.0, cv_ - 0.45, cv_ + 0.45) if sofa else (cu_ - 0.8, cu_ + 0.8, cv_ - 1.0, cv_ + 1.0)
            # In the middle of the room unless a way through crosses it: then the nearest spot clear of them all.
            at = clear_spot(want, (iu0, iu1, iv0, iv1), ways)
            if at is None:
                STATS["rooms whose furniture stands in a way through"] += 1
                at = want
            obox("furniture_soft", F, *at, FLOOR_TOP, 0.95 if sofa else 0.65, name=f"{label} {'sofa' if sofa else 'bed'}", layer="interiors")
        elif nm == "kitchen" and ab is not None and abs(ab[3] - D) < 1e-6 and ab[1] - ab[0] > 3.0:
            c = clear_run(rect(ab[0] + 0.6, ab[1] - 0.6, D - WALL_T - 0.62, D - WALL_T - 0.02), ways, COUNTER_MIN)
            if c is None:
                # No stretch of the back wall is clear: a short counter in its far corner, where nobody walks.
                STATS["kitchens with a corner counter"] += 1
                c = clear_run(rect(ab[0] + 0.02, ab[0] + 0.02 + COUNTER_MIN, D - WALL_T - 0.62, D - WALL_T - 0.02), ways, 0.3) \
                    or clear_run(rect(ab[1] - 0.02 - COUNTER_MIN, ab[1] - 0.02, D - WALL_T - 0.62, D - WALL_T - 0.02), ways, 0.3) \
                    or rect(ab[0] + 0.02, ab[0] + 0.32, D - WALL_T - 0.62, D - WALL_T - 0.02)
            obox("tile_wall", F, c[0], c[1], c[2], c[3], FLOOR_TOP, 0.92, name=f"{label} kitchen counter", layer="interiors")

    # Partitions with their openings; the inner doors are made after the outer ones.
    inner = []
    walk = []                         # the ways through the plan cuts, (room, room, how wide)
    order = ["garage", "bathroom", "main bedroom", "bedroom", "bedroom 2", "bedroom 3", "closet", "den", "utility room"]
    for (i, j), (axis, line, s0, s1) in sorted(adj.items()):
        lk = links.get((i, j))
        cut = []
        if lk:
            kind_, wdt = lk
            wdt = min(wdt, s1 - s0 - 0.3)
            sm = (s0 + s1) / 2
            cut = [(sm - wdt / 2, sm + wdt / 2, head_over("door") if kind_ == "door" else DOOR_TOP)]
            walk.append((ids[i], ids[j], wdt))
            x, z = F.w(line, sm) if axis == "u" else F.w(sm, line)
            phi = F.a + math.pi / 2 if axis == "u" else F.a
            if kind_ == "door" and wdt < WALK_WIDE:
                # Too short a stretch of partition for a door a body goes through (a garage's into the kitchen was 0.28
                # m): the gap is walled up, in the leaf's place in the order, and the room is given a way elsewhere.
                inner.append(lambda line=line, sm=sm, wdt=wdt, axis=axis, i=i:
                             obox(PARTITION, F, *((line - PART_BUILD / 2, line + PART_BUILD / 2, sm - wdt / 2, sm + wdt / 2) if axis == "u"
                                                       else (sm - wdt / 2, sm + wdt / 2, line - PART_BUILD / 2, line + PART_BUILD / 2)),
                                  0.0, head_over("door"), name=f"{label} {names[i]} wall", layer="interiors"))
            elif kind_ == "door":
                ri = order.index(names[i]) if names[i] in order else 99
                rj = order.index(names[j]) if names[j] in order else 99
                own, other = (i, j) if ri <= rj else (j, i)
                inner.append(lambda x=x, z=z, phi=phi, own=own, other=other, wdt=wdt:
                             door("door", x, z, FLOOR_TOP, phi, ids[own], ids[other], wdt, centres[own],
                                  f"{label} {names[own]} door", layer="interiors"))
            else:
                inner.append(lambda x=x, z=z, phi=phi, i=i, j=j, wdt=wdt:
                             portal(x, z, phi, wdt, FLOOR_TOP, DOOR_TOP, PART_T, ids[i], ids[j]))
        # the partition as thick as it is built, centred on the line
        wall_run(F, (axis, line + PART_BUILD / 2, s0, s1, 1), PARTITION, CEIL, f"{label} {names[i]} wall", cut,
                 layer="interiors", thick=PART_BUILD)

    return {"doors": doors_out, "inner": inner, "boxes": dict(zip(ids, inner_rects)),
            "rects": [(rid, r[1]) for rid, r in zip(ids, rooms)], "label": label, "walk": walk}


# THE OUTDOOR PLACES ARE NAMES, NOT ROOMS. A yard and a stretch of road are where you are told you
# are, and for sound they are the outdoors, which is what they are: a named place (named_place.json)
# says the first and leaves the second alone. As regions, which is what the city's streets are, the
# 6,500 of them on this map took 20 s to rasterise into the client's acoustic grid at load (a turned
# box is cut into metre voxels all round its edge) and made every region lookup, which asks each
# region in turn, ten times the city's. They are cut round each house, because a named place over a
# room renames the room.
ZONE_H = 4.0

# ══ The lots' grounds, and their zones ════════════════════════════════════════════════════════════
for lot in LOTS:
    a, F = lot["addr"], lot["F"]
    for suffix, u0, u1, v0, v1 in yards(lot):
        if u1 - u0 < 0.5 or v1 - v0 < 0.5:
            continue
        if LEVEL >= 1:
            # A lawn in front, and behind and beside where the satellite sees no trees; under trees
            # it is the woods' own floor.
            cx, cz = F.w((u0 + u1) / 2, (v0 + v1) / 2)
            if not suffix or (detail_at(cx, cz) >= 2 and landcover_at(cx, cz) != "T"):
                obox("grass_floor", F, u0, u1, v0, v1, 0.0, Y_LAWN, name=f"{a.label}{suffix or ', front yard'}", layer="yards")
        named_place(f"{a.label}{suffix}", F, u0, u1, v0, v1, 0.0, ZONE_H)
        lot["yards"].append((suffix, u0, u1, v0, v1))

# ══ Zones over the roads ══════════════════════════════════════════════════════════════════════════
#
# Each road is a place called by its name, carriageway and verges, in boxes along it: walking along it
# is silent, and stepping off it into a yard says the address.
for g in ROAD_GEOM:
    r, pts, d, width, name, hw = g
    sp = simplify(pts, 2.5)
    half = width / 2 + VERGE
    for i in range(len(sp) - 1):
        (ax, az), (bx, bz) = sp[i], sp[i + 1]
        L = math.dist(sp[i], sp[i + 1])
        if L < 0.5:
            continue
        F = Frame(ax, az, math.atan2(bz - az, bx - ax))
        e0 = half if i > 0 else 0.0
        e1 = half if i < len(sp) - 2 else 0.0
        lo, hi = -half, half
        x0, x1, z0, z1 = bbox_of(F, -e0, L + e1, lo, hi)
        for b in B_INDEX.near(x0, x1, z0, z1):
            if not has_rooms(b):
                continue
            e = extents(F, b.ring)
            if e[1] <= -e0 or e[0] >= L + e1 or e[3] <= lo or e[2] >= hi:
                continue
            if e[2] >= 0.5:
                hi = min(hi, e[2] - 0.3)
            elif e[3] <= -0.5:
                lo = max(lo, e[3] + 0.3)
            else:
                lo = hi = 0.0                 # a building on the road itself: the road is not named here
        if hi - lo >= 1.0:
            named_place(name, F, -e0, L + e1, lo, hi, 0.0, ZONE_H)

# ══ The buildings ═════════════════════════════════════════════════════════════════════════════════
CUT_BY_EDGE = 0                       # buildings whose parts stand in more than one tile


def pad_of(bd):
    """The height of the level pad a building stands on: the ground at the middle of its wall nearest the
    street, where its front door is, so a house on a slope shows a foundation on the low side and is dug
    in on the high side (docs/GEOMETRY.md 5.1). A building that is one solid box (a shed) stands on no
    pad: it is set down into the ground like anything else solid."""
    if not HAS_GROUND or bd.kind == "shed" or (detail_at(bd.cx, bd.cz) <= 1 and bd.kind in ("garage", "workshop", "barn", "outbuilding")):
        return None
    near = nearest_road(bd.cx, bd.cz, 200.0)
    if near is None:
        return ground(bd.cx, bd.cz)
    pts = near[5][1]
    best = None
    for i in range(len(bd.ring) - 1):
        mx, mz = (bd.ring[i][0] + bd.ring[i + 1][0]) / 2, (bd.ring[i][1] + bd.ring[i + 1][1]) / 2
        d = project(pts, mx, mz)[0]
        if best is None or d < best[0]:
            best = (d, mx, mz)
    return ground(best[1], best[2])


LATER_LISTS = {"rooms": rooms_later, "openings": openings_later, "doors": doors_later, "steps": steps_later,
               "finishes": finishes_later}
DROP_LATER = {k: set() for k in LATER_LISTS}
for bd in sorted(BLD, key=lambda b: (round(b.cx, 2), round(b.cz, 2))):
    first = len(entities)
    _TILE_PIN[0] = tile_of(bd.cx, bd.cz)
    _Y_PIN[0] = pad_of(bd)
    _DROPPING[0] = bd.on_road
    later_from = {k: len(v) for k, v in LATER_LISTS.items()}
    build(bd)
    if bd.on_road:
        for k, v in LATER_LISTS.items():
            DROP_LATER[k].update(range(later_from[k], len(v)))
    _DROPPING[0] = False
    if _Y_PIN[0] is not None:
        PADS.add(("pad", bd, _Y_PIN[0]), bd.x0, bd.x1, bd.z0, bd.z1)
    if id(bd) in DECIDED:
        DECIDED[id(bd)].update({"id": bd.src.get("id"), "kind": bd.kind, "pad": _Y_PIN[0], "entities": [first, len(entities)]})
        if bd.on_road:
            del DECIDED[id(bd)]
    _Y_PIN[0] = None
    _TILE_PIN[0] = None
    if len({tile_of(e["Position"]["X"], e["Position"]["Z"]) for e in entities[first:]}) > 1:
        CUT_BY_EDGE += 1
    CLEAR.add(("box", Frame(bd.cx, bd.cz, bd.angle), *extents(Frame(bd.cx, bd.cz, bd.angle), bd.ring)),
              bd.x0, bd.x1, bd.z0, bd.z1)

# Places without a building: a park, a kiosk. A name where they are.
for p in POI:
    if p.get("building") is None:
        F = Frame(p["x"], p["z"], 0.0)
        named_place(p["name"], F, -15, 15, -15, 15, 0.0, 6.0)


# ══ The woods ═════════════════════════════════════════════════════════════════════════════════════
#
# ESA WorldCover says, every ten metres, whether there are trees. There are far too many to place
# one by one — a 3 km square of pine woods is a quarter of a million trees — so they are three
# things at three densities: the canopy as volumes of foliage over every stretch of woods (the
# scatterer that takes the top off sound through a wood), a trunk you can walk into on a spacing set
# by the detail level, and the wind in the trees heard from one crown in every so many metres. The
# species are named in the proportions of a Southern pine-hardwood wood; which tree is which is not
# known, and is seeded.
TREES = PLACE.get("Trees", {})
SPECIES = TREES.get("Species", [["Loblolly pine", 0.55], ["Water oak", 0.2], ["Sweetgum", 0.15], ["Post oak", 0.1]])
CELL = 10.0
nx_ = int(math.ceil((XMAX - XMIN) / CELL))
nz_ = int(math.ceil((ZMAX - ZMIN) / CELL))
WOODED = [[landcover_at(XMIN + (i + 0.5) * CELL, ZMIN + (j + 0.5) * CELL) == "T" for i in range(nx_)] for j in range(nz_)]
FREE = [[WOODED[j][i] and is_clear(XMIN + (i + 0.5) * CELL, ZMIN + (j + 0.5) * CELL, 2.0) for i in range(nx_)] for j in range(nz_)]

# Canopy: 20 m squares that are wooded in at least three of their four 10 m cells, merged into
# rectangles, and none over a road or a roof.
MC = 2
mx_, mz_ = nx_ // MC, nz_ // MC
canopy = [[sum(FREE[j * MC + b][i * MC + a] for a in range(MC) for b in range(MC)) >= 3 for i in range(mx_)] for j in range(mz_)]
CANOPY_LO, CANOPY_HI = float(TREES.get("CanopyLow", 5.0)), float(TREES.get("CanopyHigh", 18.0))
n_canopy = 0
while True:
    area, i0, i1, j0, j1 = largest_rect(canopy, mx_, mz_)
    if area < 2:
        break
    # Cap a volume at 300 m a side: the spatial grids file a box in every cell it covers.
    i1 = min(i1, i0 + 15)
    j1 = min(j1, j0 + 15)
    obox("foliage_hedge", WORLD, XMIN + i0 * MC * CELL, XMIN + i1 * MC * CELL, ZMIN + j0 * MC * CELL, ZMIN + j1 * MC * CELL,
         CANOPY_LO, CANOPY_HI, name="Woods", layer="trees")
    n_canopy += 1
    for j in range(j0, j1):
        for i in range(i0, i1):
            canopy[j][i] = False

# Trunks, round (docs/GEOMETRY.md 5.5): eight sides, each narrower than a hand, which sound cannot tell
# from a circle.
TRUNK_FORM = {"Kind": "Cylinder", "Segments": 8}
SPACING ={0: None, 1: float(TREES.get("SpacingMedium", 40.0)), 2: float(TREES.get("SpacingHigh", 16.0))}
SPACING_NEAR = float(TREES.get("SpacingNear", 10.0))
n_trunks = 0


def species(i, j):
    r = h01("species", i, j)
    acc = 0.0
    for nm, share in SPECIES:
        acc += share
        if r < acc:
            return nm
    return SPECIES[-1][0]


for j in range(nz_):
    for i in range(nx_):
        if not WOODED[j][i]:
            continue
        x = XMIN + (i + h01("tx", i, j)) * CELL
        z = ZMIN + (j + h01("tz", i, j)) * CELL
        sp = SPACING_NEAR if (LEVEL >= 1 and near_spawn(x, z)) else SPACING[LEVEL]
        if sp is None:
            continue
        # One trunk per spacing-square on average, chosen by the cell's own seed.
        if h01("trunk", i, j) >= (CELL * CELL) / (sp * sp):
            continue
        if not is_clear(x, z, 1.5) or not in_area(x, z):
            continue
        nm = species(i, j)
        d = 0.5 if "pine" in nm.lower() else 0.6
        obox("wood_floor", WORLD, x - d / 2, x + d / 2, z - d / 2, z + d / 2, 0.0, CANOPY_LO + 1.0, name=nm, layer="trees")
        entities[-1]["Form"] = TRUNK_FORM
        n_trunks += 1

# The wind in the trees: one crown for every so many metres of woods.
n_crowns = 0
CROWN = float(TREES.get("CrownEveryMetres", 80.0))
if LEVEL >= 1:
    step = int(CROWN // CELL)
    for j0 in range(0, nz_, step):
        for i0 in range(0, nx_, step):
            cells = [(i, j) for j in range(j0, min(nz_, j0 + step)) for i in range(i0, min(nx_, i0 + step)) if FREE[j][i]]
            if len(cells) < 0.5 * step * step:
                continue
            ci, cj = cells[int(h01("crown", i0, j0) * len(cells))]
            prop("tree_crown", XMIN + (ci + 0.5) * CELL, (CANOPY_LO + CANOPY_HI) / 2, ZMIN + (cj + 0.5) * CELL, name="Trees", layer="trees")
            n_crowns += 1

# Single trees OpenStreetMap places.
for nid in sorted(NODES, key=int):
    if ntags(nid).get("natural") == "tree" and LEVEL >= 1:
        x, z = nxz(nid)
        if in_area(x, z) and is_clear(x, z, 0.5):
            obox("wood_floor", WORLD, x - 0.25, x + 0.25, z - 0.25, z + 0.25, 0.0, 4.0, name="Tree", layer="trees")
            entities[-1]["Form"] = TRUNK_FORM
            n_trunks += 1


# Junctions are named where they are.
for j in JUNCTIONS:
    F = Frame(j["Position"]["X"], j["Position"]["Z"], 0.0)
    r = j["RadiusMetres"] + 1.0
    named_place(f"{j['Name']} junction", F, -r, r, -r, r, 0.0, 4.0)

# The named places last, so adding one moves no other part's id.
for nm, F, u0, u1, v0, v1, y0, y1, pin in named_places:
    _Y_PIN[0] = pin
    obox("named_place", F, u0, u1, v0, v1, y0, y1, name=nm, layer="zones")
    _Y_PIN[0] = None


# ══ Shores ════════════════════════════════════════════════════════════════════════════════════════
#
# Waves lapping at the edge of every pond and lake (ShoreSpec, ShoreSynth; docs/WAVES_AND_SHORES.md).
# The edge is cut into stretches of about SHORE_STRETCH metres along the real outline, each a source
# whose box says what the waves at it are made from: X the stretch's length along the edge, Z the
# fetch, how far the water reaches straight out from it to the far side (the wind blowing straight
# onshore has that much water to raise waves over), its +Z turned to the water. The client works the
# fetch at any other wind from it, and the wind is the weather's, so a stretch on the lee side of a pond
# is still and the one the wind blows onto laps. A swimming pool's edge is a skimmer, not a shore, and a
# stream's water is the running water's (RunningWaterSpec). Which bank it is: what the land cover says
# just ashore, a reed bed where WorldCover has herbaceous wetland, a grassed bank otherwise (nothing open
# says what a pond's edge is made of). Laid after the named places so no other id moves.
SHORE_STRETCH = 20.0
SHORE_CLASSES = ("pond", "water", "lake", "reservoir", "basin", "lagoon")
n_shores = 0


def ring_ccw(ring):
    """The ring turning anticlockwise (x east, z north), so the water is on the left of the way it runs."""
    a = sum(ring[i - 1][0] * ring[i][1] - ring[i][0] * ring[i - 1][1] for i in range(len(ring)))
    return list(ring) if a > 0 else list(reversed(ring))


def fetch_across(ring, x, z, nx, nz):
    """How far a ray from (x, z) along (nx, nz) runs inside the ring before it leaves, m."""
    best = None
    for i in range(len(ring)):
        (ax, az), (bx, bz) = ring[i - 1], ring[i]
        ex, ez = bx - ax, bz - az
        den = nx * ez - nz * ex
        if abs(den) < 1e-12:
            continue
        t = ((ax - x) * ez - (az - z) * ex) / den
        s = ((ax - x) * nz - (az - z) * nx) / den
        if t > 0.05 and 0.0 <= s <= 1.0 and (best is None or t < best):
            best = t
    return best or 1.0


if LEVEL >= 1:
    for wf in WATER:
        if wf["kind"] != "area" or wf["class"] not in SHORE_CLASSES:
            continue
        ring = [P(a, b) for a, b in wf["ring"]]
        if len(ring) > 1 and ring[0] == ring[-1]:
            ring = ring[:-1]
        if len(ring) < 3:
            continue
        ring = ring_ccw(ring)
        closed = ring + [ring[0]]
        perim = plen(closed)
        n = max(1, int(round(perim / SHORE_STRETCH)))
        piece = perim / n
        nm = (wf["name"] or "Pond") + " shore"
        for k in range(n):
            ax, az = point_at(closed, k * piece)
            bx, bz = point_at(closed, (k + 1) * piece)
            mx, mz = point_at(closed, (k + 0.5) * piece)
            a = math.atan2(bz - az, bx - ax)
            nx, nz = -math.sin(a), math.cos(a)            # to the left: the water
            if not in_area(mx, mz):
                continue
            fetch = round(fetch_across(ring, mx + 0.1 * nx, mz + 0.1 * nz, nx, nz))
            ashore = landcover_at(mx - 5.0 * nx, mz - 5.0 * nz)
            preset = "reed_shore" if ashore == "M" else "pond_bank"
            e = {"EntityId": new_id(), "PrefabId": "shore_" + preset, "Position": v3(mx, Y_ROAD + 0.06, mz),
                 "Rotation": yaw(-a), "Scale": v3(round(piece, 2), 1, max(1, fetch))}
            _finish(e, nm, "water")
            n_shores += 1


# ══ Traffic ═══════════════════════════════════════════════════════════════════════════════════════
VEHICLES = []
if LEVEL >= 1:
    GRIP = {"car": 0.85, "van": 0.75, "truck": 0.70, "bus": 0.70, "bike": 0.90}
    fleet = PLACE.get("Traffic", [])
    for k, v in enumerate(fleet):
        # The longest stretch of the road of that name: a short piece may be a dead end with no way round.
        pieces = [d for d in ROADS if d["Name"] == v["Road"]]
        if not pieces:
            continue
        road = max(pieces, key=lambda d: (plen([(q["X"], q["Z"]) for q in d["Centreline"]]), d["Id"]))
        g = GRIP[v.get("Kind", "car")]
        VEHICLES.append({"Name": v["Name"], "Preset": v["Preset"], "TopSpeedKmh": v.get("TopKmh", 70.0),
                         "CorneringG": v.get("CorneringG", 0.4), "GripG": g,
                         "AccelerationMps2": round(min(v.get("Accel", 2.2), g * 9.81 * 0.30), 2),
                         "BrakingMps2": round(g * 9.81 * 0.35, 2), "StartOffsetMetres": 0.0,
                         "Route": {"StartRoad": road["Id"], "Direction": v.get("Direction", 1),
                                   "Seed": 3000 + k, "WanderMetres": v.get("WanderMetres", 2500.0)}})


# ══ The spawn ═════════════════════════════════════════════════════════════════════════════════════
#
# On the drive in front of the house, facing the street.
spawn = (SPAWN_XZ[0], 0.15, SPAWN_XZ[1])
spawn_yaw = 0.0
if SPAWN_ADDR is not None and SPAWN_ADDR.lot is not None:
    lot = SPAWN_ADDR.lot
    F, h = lot["F"], lot["house"]
    if id(lot) in DRIVE_FOR:
        # The mapped drive: four metres down it from the house end.
        part = DRIVE_FOR[id(lot)]
        hx, hz = (SPAWN_ADDR.main.cx, SPAWN_ADDR.main.cz) if SPAWN_ADDR.main else (SPAWN_ADDR.x, SPAWN_ADDR.z)
        if math.dist(part[0], (hx, hz)) < math.dist(part[-1], (hx, hz)):
            part = list(reversed(part))
        x, z = point_at(part, max(0.0, plen(part) - 4.0))
        spawn = (x, Y_DRIVE + 0.09, z)
    elif h is not None:
        su = lot.get("drive_u", (h[0] + h[1]) / 2)
        sv = h[2] - 4.0
        x, z = F.w(su, sv)
        spawn = (x, Y_DRIVE + 0.09, z)
    # In the yard named by the address: where something stands between the house and the road, the
    # nearest point of the front yard that is clear of it, a metre in from its edge.
    # A neighbour's yard may overlap it, and the smaller place is the one you are told, so the point
    # chosen is the nearest one where the name you hear is this address.
    def named_at(x, z):
        best = None
        for nm, G, u0, u1, v0, v1, y0, y1, _ in named_places:
            if abs(G.ox - x) > 400 or abs(G.oz - z) > 400:
                continue
            u, v = G.l(x, z)
            if u0 <= u <= u1 and v0 <= v <= v1 and y0 <= 1.6 <= y1:
                vol = (u1 - u0) * (v1 - v0) * (y1 - y0)
                if best is None or vol < best[0]:
                    best = (vol, nm)
        return best[1] if best else None
    if named_at(spawn[0], spawn[2]) != SPAWN_ADDR.label:
        su, sv = F.l(spawn[0], spawn[2])
        cands = []
        for _, u0, u1, v0, v1 in (y for y in lot["yards"] if y[0] == ""):
            u = u0 + 1.0
            while u <= u1 - 1.0:
                v = v0 + 1.0
                while v <= v1 - 1.0:
                    cands.append((math.dist((u, v), (su, sv)), u, v))
                    v += 1.0
                u += 1.0
        for _, cu, cv in sorted(cands):
            x, z = F.w(cu, cv)
            if named_at(x, z) == SPAWN_ADDR.label and is_clear(x, z, 0.6):
                spawn = (x, Y_DRIVE + 0.09, z)
                break
    # Facing the road: the frame's -v, turned to the game's facing (identity looks north, +z).
    dx, dz = F.w(0, -1)
    dx, dz = dx - F.ox, dz - F.oz
    spawn_yaw = math.atan2(dx, dz)
SPAWN_ROT = yaw(spawn_yaw)


# ══ Everything on the ground ══════════════════════════════════════════════════════════════════════
#
# Everything above was made on flat ground at y = 0, and is set on the real ground here
# (docs/GEOMETRY.md 5.1 and 5.3). The server lays its terrain from the same posts and grades it to what
# lies on it (TerrainBuilder): flattened under each slab that rests on the ground, so the ground never
# shows through a road, a lawn or a floor. What is solid and stands on the ground is set into it instead.
#   a building           on its pad (pad_of), everything it is made of: walls, floors, rooms, doors
#   a strip of a line     pitched along its run to its ends' heights, level across (lay_line)
#   a slab                tilted to the ground under its corners (a lawn, a yard)
#   a pond                level, at the lowest ground round it
#   a long solid          pitched along its length and set 5 cm in (a fence, a rail)
#   anything else solid   its foot at the lowest ground under it (a trunk, a post, a shed)
#   a named place, a wood  stretched from the lowest ground under it to the highest
#   a point               on the ground where it is (a crown, a gate)
def _qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def _turn(a, pitch_u, pitch_v):
    """The frame's turn about the vertical, then the box tilted so its +X rises pitch_u per metre and its
    +Z pitch_v per metre."""
    # Turned as obox turns it: folded into a half turn, which turns the box's own axes round with it.
    n = norm_half(a)
    if abs(math.remainder(n - a, 2 * math.pi)) > 1.0:
        pitch_u, pitch_v = -pitch_u, -pitch_v
    y = yaw(-n) if abs(n) > 1e-9 else {"X": 0.0, "Y": 0.0, "Z": 0.0, "W": 1.0}
    q = (y["X"], y["Y"], y["Z"], y["W"])
    al, be = math.atan(pitch_u), -math.atan(pitch_v)
    q = _qmul(q, (0.0, 0.0, math.sin(al / 2), math.cos(al / 2)))
    q = _qmul(q, (math.sin(be / 2), 0.0, 0.0, math.cos(be / 2)))
    return {"X": round(q[0], 6), "Y": round(q[1], 6), "Z": round(q[2], 6), "W": round(q[3], 6)}


def _samples(F, u0, u1, v0, v1, every=20.0):
    """The ground under a box's footprint: its corners, its middle, and a grid at most `every` apart."""
    nu = max(1, min(24, int(math.ceil((u1 - u0) / every))))
    nv = max(1, min(24, int(math.ceil((v1 - v0) / every))))
    out = [ground(*F.w(u0 + (u1 - u0) * i / nu, v0 + (v1 - v0) * j / nv)) for i in range(nu + 1) for j in range(nv + 1)]
    out.append(ground(*F.w((u0 + u1) / 2, (v0 + v1) / 2)))
    return out


def _set(e, F, u0, u1, v0, v1, y0, y1, centre_y, turn=None):
    """A box of the frame's footprint, its bottom and top as given about its middle at centre_y."""
    by = BASE[e["PrefabId"]][1]
    cx, cz = F.w((u0 + u1) / 2, (v0 + v1) / 2)
    e["Position"] = v3(cx, centre_y, cz)
    e["Scale"]["Y"] = round((y1 - y0) / by, 4)
    if turn is not None:
        e["Rotation"] = turn


def dug(F, u0, u1, v0, v1, g00, g10, g01, g11):
    """A lawn's corners (the ground under each, as laid over the slope) lowered so that along any side of it that meets
    a building on a pad it stands no higher than just under that building's floor: the ground round a house on a slope
    is dug down to it on the high side. Laid over the slope, a lawn stood up to half a metre over the threshold of a
    door into the hill, with the door's head at a body's height (Cody, 2026-10-10). Lowering a side's two corners by d
    lowers the lawn's plane (ground_all's fit) along that side by d and leaves the far side where it was."""
    su, sv = u1 - u0, v1 - v0
    g = {"00": g00, "10": g10, "01": g01, "11": g11}
    sides = (("00", "01", lambda t: (u0, v0 + t * sv), sv), ("10", "11", lambda t: (u1, v0 + t * sv), sv),
             ("00", "10", lambda t: (u0 + t * su, v0), su), ("01", "11", lambda t: (u0 + t * su, v1), su))
    for _ in range(2):
        gu = ((g["10"] + g["11"]) - (g["00"] + g["01"])) / (2 * su)
        gv = ((g["01"] + g["11"]) - (g["00"] + g["10"])) / (2 * sv)
        centre = (g["00"] + g["10"] + g["01"] + g["11"]) / 4
        drops = {}
        for a, b, at, length in sides:
            drop = 0.0
            for i in range(int(length / 0.5) + 1):
                u, v = at(min(1.0, i * 0.5 / length))
                x, z = F.w(u, v)
                height = centre + gu * (u - (u0 + u1) / 2) + gv * (v - (v0 + v1) / 2) + Y_LAWN
                for _, bld, pad in PADS.near(x - LAWN_DUG, x + LAWN_DUG, z - LAWN_DUG, z + LAWN_DUG):
                    if pip(x, z, bld.ring) or ring_distance(x, z, bld.ring) <= LAWN_DUG:
                        drop = max(drop, height - (pad + FLOOR_TOP - 0.06))
            if drop > 1e-4:
                drops[(a, b)] = drop
        if not drops:
            break
        for (a, b), drop in drops.items():
            g[a] -= drop
            g[b] -= drop
    return g["00"], g["10"], g["01"], g["11"]


def drive_end(x, z, h, top):
    """The ground under an end of a drive or a path whose surface stands `top` over it: no higher than leaves that
    surface just under the floor of a building on a pad it ends at (dug)."""
    for _, bld, pad in PADS.near(x - LAWN_DUG, x + LAWN_DUG, z - LAWN_DUG, z + LAWN_DUG):
        if pip(x, z, bld.ring) or ring_distance(x, z, bld.ring) <= LAWN_DUG:
            h = min(h, pad + FLOOR_TOP - 0.06 - top)
    return h


def ground_all():
    for e in entities:
        pin, box, seg = e.pop("_pin", None), e.pop("_box", None), e.pop("_seg", None)
        if not HAS_GROUND:
            continue
        p = e["Position"]
        if pin is not None:
            p["Y"] = round(p["Y"] + pin, 4)
            continue
        if box is None:
            p["Y"] = round(p["Y"] + ground(p["X"], p["Z"]), 4)
            continue
        F, u0, u1, v0, v1, y0, y1 = box
        su, sv = u1 - u0, v1 - v0
        prefab = e["PrefabId"]
        if seg is not None:
            L, heights = seg
            ha, hb = heights if heights is not None else (ground(F.ox, F.oz), ground(*F.w(L, 0.0)))
            if e["Layer"] in ("drives", "paths"):
                # A drive or a path that runs up to a building on a pad comes up to it no higher than just under its
                # floor, as the ground there is dug: one pitched to the slope stood 0.3 m over a threshold.
                ha, hb = drive_end(F.ox, F.oz, ha, y1), drive_end(*F.w(L, 0.0), hb, y1)
            g = (hb - ha) / L
            uc = (u0 + u1) / 2
            _set(e, F, u0, u1, v0, v1, y0, y1, ha + g * uc + (y0 + y1) / 2, _turn(F.a, g, 0.0))
        elif prefab == "water_surface":
            lo = min(_samples(F, u0, u1, v0, v1))
            _set(e, F, u0, u1, v0, v1, y0, y1, lo + (y0 + y1) / 2)
        elif not SOLID.get(prefab, True):
            s = _samples(F, u0, u1, v0, v1)
            lo, hi = min(s), max(s)
            _set(e, F, u0, u1, v0, v1, lo + y0, hi + y1, (lo + y0 + hi + y1) / 2)
        elif y1 - y0 <= 0.6 and su >= 1.0 and sv >= 1.0:
            g00, g10 = ground(*F.w(u0, v0)), ground(*F.w(u1, v0))
            g01, g11 = ground(*F.w(u0, v1)), ground(*F.w(u1, v1))
            if prefab == "grass_floor":
                g00, g10, g01, g11 = dug(F, u0, u1, v0, v1, g00, g10, g01, g11)
            gu = ((g10 + g11) - (g00 + g01)) / (2 * su)
            gv = ((g01 + g11) - (g00 + g10)) / (2 * sv)
            _set(e, F, u0, u1, v0, v1, y0, y1, (g00 + g10 + g01 + g11) / 4 + (y0 + y1) / 2, _turn(F.a, gu, gv))
        elif max(su, sv) >= 4.0 and min(su, sv) < 1.0:
            if su >= sv:
                vc = (v0 + v1) / 2
                ga, gb = ground(*F.w(u0, vc)), ground(*F.w(u1, vc))
                turn, g = _turn(F.a, (gb - ga) / su, 0.0), (ga + gb) / 2
            else:
                uc = (u0 + u1) / 2
                ga, gb = ground(*F.w(uc, v0)), ground(*F.w(uc, v1))
                turn, g = _turn(F.a, 0.0, (gb - ga) / sv), (ga + gb) / 2
            _set(e, F, u0, u1, v0, v1, y0, y1, g - 0.05 + (y0 + y1) / 2, turn)
        else:
            lo = min(_samples(F, u0, u1, v0, v1))
            p["Y"] = round(p["Y"] + lo, 4)


# The rooms of the footprints' leftover pieces, then the openings made with the rooms, after everything else.
for i, r in enumerate(rooms_later):
    nm, F, u0, u1, v0, v1, y0, y1, pin, tile, _ = r
    _Y_PIN[0], _TILE_PIN[0], _DROPPING[0] = pin, tile, i in DROP_LATER["rooms"]
    r[10] = region(nm, F, u0, u1, v0, v1, y0, y1, layer="rooms")
    _Y_PIN[0] = _TILE_PIN[0] = None
for i, (x, z, phi, width, y0, y1, depth, a, b, pin, tile) in enumerate(openings_later):
    _Y_PIN[0], _TILE_PIN[0], _DROPPING[0] = pin, tile, i in DROP_LATER["openings"]
    portal(x, z, phi, width, y0, y1, depth, a, b)
    _Y_PIN[0] = _TILE_PIN[0] = None
for i, (pin, tile, make) in enumerate(doors_later):
    _Y_PIN[0], _TILE_PIN[0], _DROPPING[0] = pin, tile, i in DROP_LATER["doors"]
    make()
    _Y_PIN[0] = _TILE_PIN[0] = None
for i, (E, u0, u1, v0, v1, y0, y1, nm, pin, tile) in enumerate(steps_later):
    _Y_PIN[0], _TILE_PIN[0], _DROPPING[0] = pin, tile, i in DROP_LATER["steps"]
    obox("concrete_floor", E, u0, u1, v0, v1, y0, y1, name=nm, layer="structure")
    _Y_PIN[0] = _TILE_PIN[0] = None
for i, (F, outline, prefab, nm, pin, tile) in enumerate(finishes_later):
    _Y_PIN[0], _TILE_PIN[0], _DROPPING[0] = pin, tile, i in DROP_LATER["finishes"]
    oform(prefab, F, outline, 0.10, FLOOR_TOP, {"Kind": "Prism"}, name=nm, layer="interiors")
    _Y_PIN[0] = _TILE_PIN[0] = None
_DROPPING[0] = False

# The place a building that is not made was (a shop's name on a canopy over its forecourt): a name where it is, as
# a place without a building has, written last.
for bd in ON_ROAD:
    if bd.place is not None:
        obox("named_place", Frame(bd.place["x"], bd.place["z"], 0.0), -15, 15, -15, 15, 0.0, 6.0, name=bd.place["name"], layer="zones")


# Nothing laid along a line goes into a building: a road's surfaces, a sidewalk, a kerb, a drive, a path or a
# track stops short of every footprint it would enter (cut_off_rings), a mapped drive at its own house's wall as at
# anybody else's. A piece keeps its id; a piece a building cuts in two has its second part written last.
SURFACE_LAYERS = ("roads", "verges", "paths", "drives")
SURFACES_CUT = defaultdict(int)


def clear_surfaces():
    gone = 0
    for e in [e for e in entities if e.get("_seg") is not None and e["Layer"] in SURFACE_LAYERS and not e.get("_drop")]:
        F, u0, u1, v0, v1, y0, y1 = e["_box"]
        rings = [[F.l(x, z) for x, z in ring] for ring in FOOTPRINTS.near(*bbox_of(F, u0, u1, v0, v1))]
        pieces = cut_off_rings(u0, u1, v0, v1, rings)
        if pieces == [(u0, u1, v0, v1)]:
            continue
        SURFACES_CUT[e["Layer"]] += 1
        if not pieces:
            e["_drop"] = True
            gone += 1
            continue
        for k, (a0, a1, b0, b1) in enumerate(pieces):
            p = e if k == 0 else dict(e, EntityId=new_id(), Position=dict(e["Position"]), Scale=dict(e["Scale"]))
            reshape(p, F, a0, a1, b0, b1, y0, y1)
            p["Tile"] = tile_of(p["Position"]["X"], p["Position"]["Z"])
            if k:
                entities.append(p)
    return gone


SURFACES_GONE = clear_surfaces()
ground_all()
if HAS_GROUND:
    spawn = (spawn[0], spawn[1] + ground(spawn[0], spawn[2]), spawn[2])
    # The traffic rides the centreline: on real ground a point at least every SEG_MAX, so its height is the
    # road's between them and not a chord across a dip.
    for r in ROADS:
        line = r["Centreline"]
        dense = [line[0]]
        for q0, q1 in zip(line, line[1:]):
            n = max(1, int(math.ceil(math.dist((q0["X"], q0["Z"]), (q1["X"], q1["Z"])) / SEG_MAX)))
            for k in range(1, n):
                dense.append(v3(q0["X"] + (q1["X"] - q0["X"]) * k / n, 0.0, q0["Z"] + (q1["Z"] - q0["Z"]) * k / n))
            dense.append(q1)
        for q in dense:
            q["Y"] = round(road_y(q["X"], q["Z"]) + Y_ROAD, 4)
        r["Centreline"] = dense
    for j in JUNCTIONS:
        j["Position"]["Y"] = round(road_y(j["Position"]["X"], j["Position"]["Z"]) + Y_ROAD, 4)

# What was made and left out (_DROPPING, clear_surfaces) leaves the map now, its ids unused; the decisions' ranges of
# entities are counted again without it.
_kept = [0]
for e in entities:
    _kept.append(_kept[-1] + (0 if e.get("_drop") else 1))
for d in DECIDED.values():
    d["entities"] = [_kept[d["entities"][0]], _kept[d["entities"][1]]]
entities[:] = [e for e in entities if not e.get("_drop")]
COUNT.clear()
for e in entities:
    COUNT[e["Layer"]] += 1


# ══ The map ═══════════════════════════════════════════════════════════════════════════════════════
MARGIN = ELEV_MARGIN
# On real ground the bounds reach from under its lowest point to 200 m over its highest.
LOW = math.floor(EBASE) - 5.0 if HAS_GROUND else 0.0
HIGH = math.ceil(EBASE + max(ECM) * 0.01) if HAS_GROUND else 0.0
MAP_MIN = (math.floor(XMIN - MARGIN), LOW, math.floor(ZMIN - MARGIN))
MAP_MAX = (math.ceil(XMAX + MARGIN), HIGH + 200.0, math.ceil(ZMAX + MARGIN))
STREET_LIFE = PLACE.get("StreetLife", {"HornEverySeconds": 0, "HardBrakeEverySeconds": 0, "ParkEverySeconds": 0,
                                       "GunfireEverySeconds": 0, "AlarmEverySeconds": 0})
map_data = {
    "Id": PLACE["Id"],
    "Name": PLACE.get("Name", PLACE["Id"]),
    "IsDefault": False,
    "Description": PLACE["Description"],
    "Size": v3(MAP_MAX[0] - MAP_MIN[0], MAP_MAX[1] - MAP_MIN[1], MAP_MAX[2] - MAP_MIN[2]),
    "MinBound": v3(*MAP_MIN),
    "MaxBound": v3(*MAP_MAX),
    "PlayMin": v3(XMIN, LOW - 20.0, ZMIN),
    "PlayMax": v3(XMAX, MAP_MAX[1], ZMAX),
    "MinimumY": LOW - 20.0,
    "SpawnPoint": {"Position": v3(*spawn), "Rotation": SPAWN_ROT},
    "Temperature": PLACE.get("Temperature", 20.0),
    "Humidity": PLACE.get("Humidity", 0.5),
    "VoxelResolution": 1.0,
    "OcclusionFloor": 0.1,
    "GeoOrigin": {"Lat": LAT0, "Lon": LON0},
    # Where the map's (0, 0) is on the world's grid: the map is the world's tiles moved by this, never turned.
    "Utm": {"Zone": UTM_ZONE, "North": UTM_NORTH, "Easting": UTM_E0, "Northing": UTM_N0},
    "TileMetres": TILE,
    "Roads": ROADS,
    "Junctions": JUNCTIONS,
    "StreetLife": STREET_LIFE,
    "Vehicles": VEHICLES,
    "Entities": entities,
}
ELEV_FILE = PLACE["Id"] + ".elevation"
if HAS_GROUND:
    # The ground's posts, for the server's terrain (MapElevation, TerrainBuilder): elevation.json as it
    # is, beside the map, with where its first post is in the map's metres and its heights over y = 0.
    map_data["Elevation"] = {
        "OriginX": EX0, "OriginZ": EZ0, "Spacing": ELEV_STEP, "Columns": ECOLS, "Rows": EROWS,
        "BaseY": EBASE, "SeaLevelY": round(-SEA_Y, 2), "File": ELEV_FILE,
        "Source": ELEV.get("source", "USGS 3DEP")}
os.makedirs(os.path.dirname(OUT) or ".", exist_ok=True)
with open(OUT, "w") as f:
    json.dump(map_data, f, indent=1)
if OPTS.get("decisions"):
    with open(OPTS["decisions"], "w") as f:
        json.dump([DECIDED[id(bd)] for bd in sorted(BLD, key=lambda b: (round(b.cx, 2), round(b.cz, 2))) if id(bd) in DECIDED], f)
if HAS_GROUND:
    with open(os.path.join(PLACE_DIR, "elevation.json"), "rb") as src, open(os.path.join(os.path.dirname(OUT) or ".", ELEV_FILE), "wb") as dst:
        dst.write(src.read())

by_prefab = defaultdict(int)
for e in entities:
    by_prefab[e["PrefabId"]] += 1
regions = by_prefab["acoustic_region"]
print(f"{OUT}: {len(entities)} entities at detail '{DETAIL}'" + (f" (high within {NEAR_M:.0f} m of the spawn)" if LEVEL == 1 and NEAR_M else ""))
print(f"  area {XMAX - XMIN:.0f} x {ZMAX - ZMIN:.0f} m; {len(ROADS)} roads, {len(JUNCTIONS)} junctions, {len(ADDR)} addresses "
      f"({INTERPOLATED} interpolated from Census ranges), {len(LOTS)} lots, {len(BLD)} buildings")
print("  buildings: " + ", ".join(f"{k} {STATS[k]}" for k in ("house", "mobile_home", "premises", "church", "building", "garage", "workshop", "barn", "outbuilding", "shed")))
print(f"  {regions} zones and rooms, {by_prefab['named_place']} named places, {STATS['doors']} outside doors, "
      f"{n_canopy} canopy volumes, {n_trunks} trunks, {n_crowns} crowns, {n_shores} stretches of shore, {len(VEHICLES)} vehicles")
print("  by layer: " + ", ".join(f"{k} {v}" for k, v in sorted(COUNT.items())))
if STATS["kitchens with a corner counter"]:
    print(f"  {STATS['kitchens with a corner counter']} kitchens with no clear stretch of back wall have a short counter in a corner")
if STATS["rooms whose furniture stands in a way through"]:
    print(f"  {STATS['rooms whose furniture stands in a way through']} rooms have no spot for their sofa or bed clear of the ways through")
if STATS["doors with something in the way outside"]:
    print(f"  {STATS['doors with something in the way outside']} outside doors have another building in the way a step out, with no clear spot along their wall")
if ON_ROAD:
    print(f"  {len(ON_ROAD)} buildings stand in a carriageway and are not made: "
          + "; ".join(f"{b.src.get('id')} at ({b.cx:.1f}, {b.cz:.1f})" for b in ON_ROAD))
if SURFACES_CUT:
    print("  pieces cut back off a building: " + ", ".join(f"{k} {v}" for k, v in sorted(SURFACES_CUT.items()))
          + f" ({SURFACES_GONE} wholly inside one, not laid)")
for k, v in sorted(STATS_EARLY.items()):
    print(f"  {v} {k}")
print(f"  {len(steps_later)} landings and steps at outside doors")
print(f"  {len(finishes_later)} floors finished on a home's slab")
if STATS["side doors"]:
    print(f"  {STATS['side doors']} rooms no way wide enough for a body reached have a side door")
if STATS["rooms no one can walk into"]:
    print(f"  {STATS['rooms no one can walk into']} rooms no way reaches have no outside wall for a door: {'; '.join(UNWALKED)}")
print(f"  {CUT_BY_EDGE} buildings stand across a {TILE:.0f} m tile edge; each is tagged whole with the tile its middle is in")
print(f"  spawn {tuple(round(c, 2) for c in spawn)} facing {math.degrees(spawn_yaw) % 360:.0f} degrees from north, "
      f"at {SPAWN_ADDR.label if SPAWN_ADDR else 'the origin'}")
