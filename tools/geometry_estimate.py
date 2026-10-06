#!/usr/bin/env python3
"""Triangle counts for a real-place map as triangle meshes (docs/GEOMETRY.md, section 5).

Throwaway measurement, standard library only:

    python3 tools/geometry_estimate.py magnolia_tx albany_or

For each place it reports
  * today's solid boxes as the Steam Audio scene takes them (12 triangles a box), by layer;
  * the buildings as extruded footprints (buildings.json rings, no box decomposition);
  * terrain from elevation.json at several grid spacings, plain and with roads cut in;
  * the per-250 m-tile spread of the box triangles.
"""
import json
import math
import os
import sys
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def prefab_table():
    out = {}
    d = os.path.join(ROOT, "OpenFPS.Server", "prefabs")
    for f in os.listdir(d):
        if not f.endswith(".json") or f == "prefab-schema.json":
            continue
        txt = open(os.path.join(d, f)).read()
        # the loader accepts // comments and trailing commas
        lines = [l for l in txt.splitlines() if not l.strip().startswith("//")]
        txt = "\n".join(lines)
        txt = txt.replace(",\n}", "\n}").replace(",\n]", "\n]")
        try:
            p = json.loads(txt)
        except Exception:
            continue
        solid = p.get("IsSolid", True) and "ColliderSize" in p
        emitter = bool(p.get("SoundId"))
        out[p["Id"]] = dict(solid=solid and not emitter, size=p.get("ColliderSize"))
    return out


def ring_metrics(ring, lat0, lon0):
    # local metres, equirectangular about the origin (good to well under 1 % over 3 km)
    k = 111320.0
    pts = [((lon - lon0) * k * math.cos(math.radians(lat0)), (lat - lat0) * 110574.0) for lat, lon in ring]
    if pts[0] == pts[-1]:
        pts = pts[:-1]
    n = len(pts)
    per = sum(math.dist(pts[i], pts[(i + 1) % n]) for i in range(n))
    area = abs(sum(pts[i][0] * pts[(i + 1) % n][1] - pts[(i + 1) % n][0] * pts[i][1] for i in range(n))) / 2
    return n, per, area


def main(places):
    prefabs = prefab_table()
    for place in places:
        mpath = os.path.join(ROOT, "OpenFPS.Server", "maps", "places", place + ".json")
        m = json.load(open(mpath))
        lat0, lon0 = m["GeoOrigin"]["Lat"], m["GeoOrigin"]["Lon"]
        T = m.get("TileMetres") or 250.0
        solids = Counter()
        per_tile = Counter()
        for e in m["Entities"]:
            p = prefabs.get(e["PrefabId"])
            if not p or not p["solid"]:
                continue
            solids[e.get("Layer", "?")] += 1
            x, z = e["Position"]["X"], e["Position"]["Z"]
            per_tile[(math.floor(x / T), math.floor(z / T))] += 12
        nbox = sum(solids.values())
        print(f"\n== {place}: {len(m['Entities'])} entities, {nbox} solid boxes = {12 * nbox:,} triangles today")
        for layer, c in solids.most_common():
            print(f"   {layer:10s} {c:6d} boxes {12 * c:8,d} tris")
        tv = sorted(per_tile.values())
        print(f"   per 250 m tile: {len(tv)} tiles, median {tv[len(tv) // 2]:,} tris, 90th {tv[int(len(tv) * .9)]:,}, max {tv[-1]:,}")

        # buildings as extruded footprints
        b = json.load(open(os.path.join(ROOT, "tools", "places", place, "buildings.json")))
        nv = []
        per_sum = area_sum = 0.0
        shell = 0
        for x in b:
            n, per, area = ring_metrics(x["ring"], lat0, lon0)
            nv.append(n)
            per_sum += per
            area_sum += area
            # outer wall faces 2n, inner faces 2n (a wall has two sides and a thickness),
            # wall tops 2n, floor slab 2(n-2), ceiling 2(n-2), roof: pitched hip ~ 2n, eaves 2n
            shell += 2 * n + 2 * n + 2 * n + 2 * (n - 2) + 2 * (n - 2) + 2 * n + 2 * n
        nv.sort()
        print(f"   buildings.json: {len(b)} footprints, ring vertices median {nv[len(nv) // 2]}, 90th {nv[int(len(nv) * .9)]}, max {nv[-1]}, "
              f"perimeter {per_sum / 1000:.1f} km, roof area {area_sum / 1e6:.3f} km2")
        print(f"   extruded shells (walls both sides + tops, floor, ceiling, roof, eaves): {shell:,} tris")

        # terrain
        e = json.load(open(os.path.join(ROOT, "tools", "places", place, "elevation.json")))
        rows = e["rows"]
        vals = [v for r in rows for v in r if v is not None]
        dx = e["dlon"] * 111320.0 * math.cos(math.radians(lat0))
        dz = e["dlat"] * 110574.0
        # steepest 30 m cell-to-cell slope
        steep = 0.0
        for i in range(len(rows)):
            for j in range(len(rows[i]) - 1):
                a, c = rows[i][j], rows[i][j + 1]
                if a is not None and c is not None:
                    steep = max(steep, abs(a - c) / dx)
        for i in range(len(rows) - 1):
            for j in range(len(rows[i])):
                a, c = rows[i][j], rows[i + 1][j]
                if a is not None and c is not None:
                    steep = max(steep, abs(a - c) / dz)
        W, H = m["Size"]["X"], m["Size"]["Z"]
        print(f"   elevation: {len(rows)}x{len(rows[0])} posts at {dx:.0f} x {dz:.0f} m, {min(vals):.1f}-{max(vals):.1f} m "
              f"(relief {max(vals) - min(vals):.1f} m), steepest post-to-post grade {100 * steep:.0f} %")
        road_len = 0.0
        for r in m.get("Roads", []):
            c = r["Centreline"]
            road_len += sum(math.dist((c[i]["X"], c[i]["Z"]), (c[i + 1]["X"], c[i + 1]["Z"])) for i in range(len(c) - 1))
        print(f"   roads: {len(m.get('Roads', []))}, {road_len / 1000:.1f} km of centreline")
        for g in (30, 10, 5, 2, 1):
            grid = 2 * math.ceil(W / g) * math.ceil(H / g)
            tile = 2 * math.ceil(T / g) ** 2
            # a road draped and cut in: both edges and both kerb lines become constrained edges,
            # one vertex every g metres along each (or every 2 m on curves), two triangles each side
            cut = int(road_len * 4 / min(g, 2.0) * 2)
            print(f"   terrain at {g:2d} m: {grid:>11,} tris for the map, {tile:>8,} per tile; +{cut:,} for road edges and kerbs")


if __name__ == "__main__":
    main(sys.argv[1:] or ["magnolia_tx", "albany_or"])
