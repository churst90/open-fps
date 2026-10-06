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
