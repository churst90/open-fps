#!/usr/bin/env python3
"""
Downloads the open data a real-place map is made from, and boils it down to the small inputs
tools/gen_osm.py reads. Two steps, both driven by the place's own file:

    fetch_place.py download tools/places/NAME/place.json
    fetch_place.py prepare  tools/places/NAME/place.json

`download` fills the place's archive directory ("Archive" in place.json, outside the repository) with
the raw layers, skipping any that are already there, and writes SOURCES.txt there saying where each
came from and under what licence. `prepare` reads them, clips them to the place's area, and writes the
plain JSON files beside place.json that the generator reads. The generator needs nothing but the
Python standard library and those files, so the map can be rebuilt byte for byte from the repository.
This script needs overturemaps, shapely, rasterio and numpy, in a venv of its own (see
docs/MAPS_FROM_REAL_PLACES.md).

THE AREA. place.json's "Area" is one of:
    {"Bbox": [S, W, N, E]}                       degrees
    {"Centre": [LAT, LON], "SizeMetres": 3000}   a square
    {"OsmRelation": 6586793}                     a boundary from OpenStreetMap (a city's limits)
    {"Zcta": "77355"}                            a Census ZIP code tabulation area
A relation or ZCTA is fetched once and kept as area.geojson in the archive; everything else is
downloaded for its bounding box and clipped to the polygon in `prepare`.

Requests carry a generic User-Agent and nothing that identifies whoever runs it.
"""
import json, math, os, subprocess, sys, time, urllib.parse, urllib.request

UA = "openfps-map-import/0.1"
HERE = os.path.dirname(os.path.abspath(__file__))


def get(url, data=None, timeout=300):
    req = urllib.request.Request(url, data=data, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def load_place(path):
    with open(path) as f:
        place = json.load(f)
    place["_dir"] = os.path.dirname(os.path.abspath(path))
    place["_archive"] = os.path.expanduser(place["Archive"])
    return place


# ══ The area ══════════════════════════════════════════════════════════════════════════════════════

def area_polygon(place):
    """The area as a list of rings of (lon, lat), outer rings only (holes are ignored: a town with a
    hole in it is still mapped across the hole)."""
    a = place["Area"]
    if "Bbox" in a:
        s, w, n, e = a["Bbox"]
        return [[(w, s), (e, s), (e, n), (w, n), (w, s)]]
    if "Centre" in a:
        lat, lon = a["Centre"]
        half = a["SizeMetres"] / 2
        dlat = half / 110_900.0
        dlon = half / (111_320.0 * math.cos(math.radians(lat)))
        s, w, n, e = lat - dlat, lon - dlon, lat + dlat, lon + dlon
        return [[(w, s), (e, s), (e, n), (w, n), (w, s)]]
    path = os.path.join(place["_archive"], "area.geojson")
    if not os.path.exists(path):
        os.makedirs(place["_archive"], exist_ok=True)
        if "OsmRelation" in a:
            q = f"[out:json][timeout:120];relation({a['OsmRelation']});out geom;"
            rel = json.loads(get("https://overpass-api.de/api/interpreter",
                                 data=urllib.parse.urlencode({"data": q}).encode()))["elements"][0]
            rings = join_rings([[(p["lon"], p["lat"]) for p in m["geometry"]]
                                for m in rel["members"] if m["type"] == "way" and m.get("role") in ("outer", "")])
            gj = {"type": "Feature", "properties": {"source": f"OSM relation {a['OsmRelation']}",
                                                     "name": rel.get("tags", {}).get("name", "")},
                  "geometry": {"type": "MultiPolygon", "coordinates": [[r] for r in rings]}}
        elif "Zcta" in a:
            # TIGERweb's 2020 ZCTA layer.
            q = urllib.parse.urlencode({"where": f"ZCTA5='{a['Zcta']}'", "outFields": "ZCTA5,AREALAND",
                                        "outSR": 4326, "f": "geojson"})
            gj = json.loads(get("https://tigerweb.geo.census.gov/arcgis/rest/services/TIGERweb/"
                                "PUMA_TAD_TAZ_UGA_ZCTA/MapServer/1/query?" + q))["features"][0]
        else:
            raise SystemExit("place.json: Area needs Bbox, Centre+SizeMetres, OsmRelation or Zcta")
        with open(path, "w") as f:
            json.dump(gj, f)
    with open(path) as f:
        g = json.load(f)["geometry"]
    polys = g["coordinates"] if g["type"] == "MultiPolygon" else [g["coordinates"]]
    return [[tuple(p) for p in poly[0]] for poly in polys]


def join_rings(ways):
    """Outer ways of a boundary relation, end to end, into closed rings."""
    ways = [list(w) for w in ways if len(w) >= 2]
    rings = []
    while ways:
        ring = ways.pop(0)
        changed = True
        while ring[0] != ring[-1] and changed:
            changed = False
            for i, w in enumerate(ways):
                if w[0] == ring[-1]:
                    ring += w[1:]
                elif w[-1] == ring[-1]:
                    ring += w[::-1][1:]
                elif w[-1] == ring[0]:
                    ring = w[:-1] + ring
                elif w[0] == ring[0]:
                    ring = w[::-1][:-1] + ring
                else:
                    continue
                ways.pop(i)
                changed = True
                break
        if ring[0] == ring[-1] and len(ring) >= 4:
            rings.append(ring)
    return rings


def area_bbox(rings):
    xs = [p[0] for r in rings for p in r]
    ys = [p[1] for r in rings for p in r]
    return min(ys), min(xs), max(ys), max(xs)          # S, W, N, E


# ══ download ══════════════════════════════════════════════════════════════════════════════════════

SOURCES = []


def source(name, what, url, licence):
    SOURCES.append(f"{name}\n  {what}\n  from: {url}\n  licence: {licence}\n")


def download(place):
    d = place["_archive"]
    os.makedirs(d, exist_ok=True)
    rings = area_polygon(place)
    s, w, n, e = area_bbox(rings)
    venv_bin = os.path.dirname(sys.executable)
    if "OsmRelation" in place["Area"] or "Zcta" in place["Area"]:
        source("area.geojson", "the area's boundary polygon",
               "OpenStreetMap relation via Overpass" if "OsmRelation" in place["Area"] else "Census TIGERweb 2020 ZCTA",
               "ODbL 1.0" if "OsmRelation" in place["Area"] else "public domain (US Census Bureau)")

    # OpenStreetMap, everything in the bbox with its nodes and members.
    osm = os.path.join(d, "osm.json")
    if not os.path.exists(osm):
        q = f"[out:json][timeout:600];(nwr({s},{w},{n},{e}););(._;>;);out body;"
        with open(osm, "wb") as f:
            f.write(get("https://overpass-api.de/api/interpreter", data=urllib.parse.urlencode({"data": q}).encode(), timeout=900))
    source("osm.json", "OpenStreetMap: roads, names, lanes, surfaces, gates, points of interest",
           "Overpass API (overpass-api.de), nwr in the bbox with recursion", "ODbL 1.0, (c) OpenStreetMap contributors")

    # Overture Maps: buildings (Microsoft ML footprints + OSM + USGS lidar), the National Address
    # Database's points, places (shops, churches, the fire station) and water.
    for t, what, lic in (
            ("building", "building footprints with heights (Microsoft ML Buildings, OpenStreetMap, USGS Lidar)",
             "ODbL 1.0 (Overture buildings theme)"),
            ("address", "address points; in the US these are the National Address Database",
             "NAD: public domain (US DOT); Overture addresses theme CDLA-Permissive-2.0"),
            ("place", "points of interest (Meta, Microsoft and others via Overture)", "CDLA-Permissive-2.0"),
            ("water", "rivers, streams, ponds", "ODbL 1.0")):
        out = os.path.join(d, f"overture-{t}.geojson")
        if not os.path.exists(out):
            subprocess.run([os.path.join(venv_bin, "overturemaps"), "download", f"--bbox={w},{s},{e},{n}",
                            "-f", "geojson", "-t", t, "-o", out], check=True)
        source(os.path.basename(out), f"Overture Maps {t}: {what}", "overturemaps download (release in the .state file)", lic)

    # ESA WorldCover 2021, 10 m land cover: where the trees are.
    wc = os.path.join(d, "esa-worldcover-2021-10m.tif")
    lat, lon = (s + n) / 2, (w + e) / 2
    tiles = sorted({tile_name(la, lo) for la in (s, n) for lo in (w, e)})
    urls = [f"https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{t}_Map.tif"
            for t in tiles]
    if not os.path.exists(wc):
        window_from_cogs(urls, wc, s, w, n, e)
    source(os.path.basename(wc), "ESA WorldCover 2021 v200, 10 m land cover classes, a window of the 3-degree tile(s)",
           " ".join(urls), "CC BY 4.0, (c) ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021)")

    # USGS 3DEP elevation, ~5 m. Kept for later terrain work; the engine's ground is flat.
    dem = os.path.join(d, "usgs-3dep-dem.tif")
    if not os.path.exists(dem):
        px = min(4000, int(max(n - s, (e - w) * math.cos(math.radians(lat))) * 111_000 / 5))
        q = urllib.parse.urlencode({
            "bbox": f"{w},{s},{e},{n}", "bboxSR": 4326, "size": f"{px},{px}", "imageSR": 4326,
            "format": "tiff", "pixelType": "F32", "interpolation": "RSP_BilinearInterpolation", "f": "image"})
        with open(dem, "wb") as f:
            f.write(get("https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage?" + q))
    source(os.path.basename(dem), "USGS 3D Elevation Program bare-earth elevation, metres (not used yet: the engine's ground is flat)",
           "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer", "public domain (USGS)")

    # Census TIGER/Line address ranges, for a house the address points miss: one file per county
    # the area touches (Albany, Oregon is in two).
    for fips in counties(place):
        tiger = os.path.join(d, f"tl_2024_{fips}_addrfeat.zip")
        url = f"https://www2.census.gov/geo/tiger/TIGER2024/ADDRFEAT/tl_2024_{fips}_addrfeat.zip"
        if not os.path.exists(tiger):
            with open(tiger, "wb") as f:
                f.write(get(url))
        source(os.path.basename(tiger), f"US Census TIGER/Line 2024 address range features, county {fips}",
               url, "public domain (US Census Bureau)")

    with open(os.path.join(d, "SOURCES.txt"), "w") as f:
        f.write(f"Inputs for the OpenFPS map '{place['Id']}' ({place.get('Name', '')}).\n")
        f.write(f"Area: {json.dumps(place['Area'])}\n")
        f.write(f"bbox S,W,N,E = {s:.6f},{w:.6f},{n:.6f},{e:.6f}\n")
        f.write(f"written {time.strftime('%Y-%m-%d')} by tools/fetch_place.py; files already present were kept as they were.\n\n")
        f.write("\n".join(SOURCES))
        if place.get("SourcesNote"):
            f.write("\n" + place["SourcesNote"] + "\n")


def counties(place):
    c = place.get("CountyFips") or []
    return [c] if isinstance(c, str) else list(c)


def tile_name(lat, lon):
    tl = int(math.floor(lat / 3) * 3)
    tn = int(math.floor(lon / 3) * 3)
    return f"{'N' if tl >= 0 else 'S'}{abs(tl):02d}{'E' if tn >= 0 else 'W'}{abs(tn):03d}"


def window_from_cogs(urls, out, s, w, n, e):
    import numpy as np
    import rasterio
    from rasterio.windows import from_bounds
    from rasterio.transform import from_origin
    res = 1 / 12000.0                                      # WorldCover's pixel, degrees
    width, height = int(round((e - w) / res)), int(round((n - s) / res))
    tr = from_origin(w, n, res, res)
    a = np.zeros((height, width), dtype=np.uint8)
    with rasterio.Env(GDAL_HTTP_USERAGENT=UA, GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR"):
        for url in urls:
            with rasterio.open("/vsicurl/" + url) as src:
                b = src.bounds
                cw, cs, ce, cn = max(w, b.left), max(s, b.bottom), min(e, b.right), min(n, b.top)
                if cw >= ce or cs >= cn:
                    continue
                win = from_bounds(cw, cs, ce, cn, src.transform).round_offsets().round_lengths()
                part = src.read(1, window=win)
                r0 = int(round((n - cn) / res))
                c0 = int(round((cw - w) / res))
                a[r0:r0 + part.shape[0], c0:c0 + part.shape[1]] = part[:height - r0, :width - c0]
            crs = "EPSG:4326"
    with rasterio.open(out, "w", driver="GTiff", width=width, height=height, count=1, dtype="uint8",
                       crs="EPSG:4326", transform=tr, compress="deflate") as dst:
        dst.write(a, 1)


# ══ prepare ═══════════════════════════════════════════════════════════════════════════════════════
#
# Everything the generator reads, clipped to the area and rounded to 7 decimal places of a degree
# (1 cm), sorted by a stable key so the files only change when the data does.

R7 = lambda v: round(v, 7)


def prepare(place):
    from shapely.geometry import Point, Polygon, shape, LineString, MultiPolygon
    from shapely.prepared import prep
    d = place["_archive"]
    out = place["_dir"]
    rings = area_polygon(place)
    area = MultiPolygon([Polygon(r) for r in rings]) if len(rings) > 1 else Polygon(rings[0])
    inside = prep(area.buffer(0))
    s, w, n, e = area_bbox(rings)

    def ll(lon, lat):
        return [R7(lat), R7(lon)]

    # ── OpenStreetMap ────────────────────────────────────────────────────────────────────────
    with open(os.path.join(d, "osm.json")) as f:
        els = json.load(f)["elements"]
    nodes = {x["id"]: x for x in els if x["type"] == "node"}
    KEEP_WAY = ("highway", "barrier", "landuse", "natural", "waterway", "amenity", "shop", "leisure", "place",
                "building", "railway", "addr:housenumber", "water")
    WAY_TAGS = ("highway", "name", "lanes", "lanes:forward", "lanes:backward", "maxspeed", "surface", "width",
                "oneway", "access", "service", "bridge", "tunnel", "layer", "barrier", "landuse", "natural",
                "waterway", "water", "amenity", "shop", "leisure", "place", "junction", "footway", "sidewalk",
                "crossing", "building", "building:levels", "height", "roof:shape", "addr:housenumber",
                "addr:street", "addr:unit", "railway", "usage", "gauge", "religion", "brand", "operator")
    NODE_KEYS = ("highway", "barrier", "amenity", "shop", "name", "craft", "man_made", "leisure", "railway",
                 "brand", "addr:housenumber", "addr:street", "addr:unit", "entrance", "door", "natural",
                 "crossing", "traffic_signals", "religion", "office", "tourism")
    ways_out, used = [], set()
    for x in els:
        if x["type"] != "way":
            continue
        t = x.get("tags", {})
        if not any(k in t for k in KEEP_WAY) or t.get("highway") in ("proposed", "construction", "abandoned"):
            continue
        pts = [nodes[i] for i in x["nodes"] if i in nodes]
        if len(pts) < 2:
            continue
        if not any(inside.contains(Point(p["lon"], p["lat"])) for p in pts):
            # A way with no node inside may still cross the area (a long straight road).
            if not inside.intersects(LineString([(p["lon"], p["lat"]) for p in pts])):
                continue
        ways_out.append({"id": x["id"], "tags": {k: t[k] for k in WAY_TAGS if k in t},
                         "nodes": [p["id"] for p in pts]})
        used.update(p["id"] for p in pts)
    nodes_out = {}
    for i, p in nodes.items():
        t = {k: v for k, v in p.get("tags", {}).items() if k in NODE_KEYS}
        if i in used or (t and inside.contains(Point(p["lon"], p["lat"]))):
            nodes_out[str(i)] = ll(p["lon"], p["lat"]) + ([t] if t else [])
    ways_out.sort(key=lambda x: x["id"])
    write(out, "osm.json", {"nodes": dict(sorted(nodes_out.items(), key=lambda kv: int(kv[0]))), "ways": ways_out})

    # ── Buildings ───────────────────────────────────────────────────────────────────────────
    with open(os.path.join(d, "overture-building.geojson")) as f:
        feats = json.load(f)["features"]
    bl = []
    for x in feats:
        g = shape(x["geometry"])
        if g.is_empty or not inside.contains(g.representative_point()):
            continue
        polys = list(g.geoms) if g.geom_type == "MultiPolygon" else [g]
        poly = max(polys, key=lambda p: p.area)
        p = x["properties"]
        src = (p.get("sources") or [{}])[0].get("dataset", "")
        names = (p.get("names") or {}).get("primary") if isinstance(p.get("names"), dict) else None
        rec = (p.get("sources") or [{}])[0].get("record_id") or ""
        bl.append({"id": x.get("id") or p.get("id"), "src": src, "record": rec,
                   "h": round(p["height"], 2) if p.get("height") else None,
                   "floors": p.get("num_floors"), "class": p.get("class"), "name": names,
                   "ring": [ll(*c[:2]) for c in poly.exterior.coords]})
    bl.sort(key=lambda b: b["id"])
    write(out, "buildings.json", bl)

    # ── Addresses ───────────────────────────────────────────────────────────────────────────
    with open(os.path.join(d, "overture-address.geojson")) as f:
        feats = json.load(f)["features"]
    ad = []
    for x in feats:
        lon, lat = x["geometry"]["coordinates"][:2]
        if not inside.contains(Point(lon, lat)):
            continue
        p = x["properties"]
        unit = p.get("unit") or ""
        ad.append([p.get("number") or "", p.get("street") or "", unit, R7(lat), R7(lon)])
    ad.sort(key=lambda a: (a[1], a[0], a[2], a[3], a[4]))
    write(out, "addresses.json", ad)

    # ── Places ──────────────────────────────────────────────────────────────────────────────
    with open(os.path.join(d, "overture-place.geojson")) as f:
        feats = json.load(f)["features"]
    pl = []
    for x in feats:
        lon, lat = x["geometry"]["coordinates"][:2]
        if not inside.contains(Point(lon, lat)):
            continue
        p = x["properties"]
        addr = (p.get("addresses") or [{}])[0].get("freeform") or ""
        pl.append({"name": (p.get("names") or {}).get("primary") or "", "category": p.get("basic_category") or "",
                   "confidence": round(p.get("confidence") or 0, 3), "status": p.get("operating_status") or "",
                   "address": addr, "at": ll(lon, lat)})
    pl.sort(key=lambda q: (q["name"], q["at"]))
    write(out, "places.json", pl)

    # ── Water ───────────────────────────────────────────────────────────────────────────────
    with open(os.path.join(d, "overture-water.geojson")) as f:
        feats = json.load(f)["features"]
    wa = []
    for x in feats:
        g = shape(x["geometry"]).intersection(area)
        if g.is_empty:
            continue
        p = x["properties"]
        parts = list(g.geoms) if hasattr(g, "geoms") else [g]
        for part in parts:
            if part.geom_type == "Polygon":
                wa.append({"kind": "area", "class": p.get("class") or p.get("subtype") or "",
                           "name": (p.get("names") or {}).get("primary") or "",
                           "ring": [ll(*c[:2]) for c in part.exterior.coords]})
            elif part.geom_type == "LineString":
                wa.append({"kind": "line", "class": p.get("class") or p.get("subtype") or "",
                           "name": (p.get("names") or {}).get("primary") or "",
                           "line": [ll(*c[:2]) for c in part.coords]})
    wa.sort(key=lambda q: (q["kind"], q["class"], q["name"], (q.get("ring") or q.get("line"))[0]))
    write(out, "water.json", wa)

    # ── Census address ranges ───────────────────────────────────────────────────────────────
    rng = []
    for fips in counties(place):
        import shapefile
        r = shapefile.Reader(os.path.join(d, f"tl_2024_{fips}_addrfeat.zip"))
        for sr in r.iterShapeRecords():
            pts = sr.shape.points
            if not pts or not inside.intersects(LineString(pts) if len(pts) > 1 else Point(pts[0])):
                continue
            q = sr.record
            rng.append({"tlid": q["TLID"], "name": q["FULLNAME"] or "",
                        "left": [q["LFROMHN"] or "", q["LTOHN"] or ""], "right": [q["RFROMHN"] or "", q["RTOHN"] or ""],
                        "line": [ll(x, y) for x, y in pts]})
    rng.sort(key=lambda q: q["tlid"])
    write(out, "addrfeat.json", rng)

    # Where it all came from, beside it: the licences travel with the data.
    import shutil
    shutil.copyfile(os.path.join(d, "SOURCES.txt"), os.path.join(out, "SOURCES.txt"))

    # The area itself, so the generator knows its edge without the archive.
    write(out, "area.json", {"rings": [[ll(lon, lat) for lon, lat in ring] for ring in rings]})

    # ── Land cover and elevation, as grids ──────────────────────────────────────────────────
    import numpy as np
    import rasterio
    CLASS = {10: "T", 20: "S", 30: "G", 40: "C", 50: "B", 60: "D", 70: "I", 80: "W", 90: "M", 95: "N", 100: "L", 0: "."}
    with rasterio.open(os.path.join(d, "esa-worldcover-2021-10m.tif")) as src:
        a = src.read(1)
        t = src.transform
        write(out, "landcover.json", {
            "source": "ESA WorldCover 2021 v200 (CC BY 4.0)",
            "legend": {"T": "tree cover", "S": "shrubland", "G": "grassland", "C": "cropland", "B": "built-up",
                       "D": "bare", "I": "snow and ice", "W": "water", "M": "herbaceous wetland", "N": "mangroves",
                       "L": "moss and lichen", ".": "no data"},
            "west": R7(t.c), "north": R7(t.f), "dlon": t.a, "dlat": -t.e,
            "rows": ["".join(CLASS.get(int(v), ".") for v in row) for row in a]})
    with rasterio.open(os.path.join(d, "usgs-3dep-dem.tif")) as src:
        a = src.read(1).astype(float)
        t = src.transform
        step = max(1, int(round(30.0 / (abs(t.a) * 111_000 * math.cos(math.radians((s + n) / 2))))))
        sub = a[::step, ::step]
        write(out, "elevation.json", {
            "source": "USGS 3DEP (public domain), resampled to about 30 m; the full grid is in the archive",
            "west": R7(t.c), "north": R7(t.f), "dlon": t.a * step, "dlat": -t.e * step, "units": "metres",
            "rows": [[round(float(v), 1) for v in row] for row in sub]})
    print(f"prepared {out}: {len(ways_out)} ways, {len(nodes_out)} nodes, {len(bl)} buildings, {len(ad)} addresses, "
          f"{len(pl)} places, {len(wa)} water features, {len(rng)} address ranges")


def write(dirname, name, obj):
    with open(os.path.join(dirname, name), "w") as f:
        json.dump(obj, f, separators=(",", ":"), sort_keys=False)
        f.write("\n")


if __name__ == "__main__":
    if len(sys.argv) < 3 or sys.argv[1] not in ("download", "prepare"):
        print(__doc__)
        sys.exit(2)
    place = load_place(sys.argv[2])
    if sys.argv[1] == "download":
        download(place)
    else:
        prepare(place)
