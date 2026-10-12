---
name: magnolia-map
description: "10-05 Cody asked for a real-place map of Magnolia TX (his home, 31907 Bobcat Ln spawn), real scale, address zones, NOT default, listed as \"magnolia tx\"; publishing is fine"
metadata:
  node_type: memory
  type: project
  originSessionId: 568c4779-136f-4fbd-b7f7-35b87bd6f916
  modified: 2026-10-06T01:35:51.351Z
---

Cody 2026-10-05: map from real data, start at 31907 Bobcat Lane, Magnolia TX 77355 (his home). He said
visibility is fine ("no one knows about the game yet, just me and him testing") — public repo OK.
Real scale, street names as zones, addresses on lot zones, as detailed as possible; not IsDefault;
in the F6 maps list as "magnolia tx" (id magnolia_tx + display name). F6 is the maps menu; he called it F5.

- Spawn: Census geocoder 30.123703, -95.740935 (TIGER line 79278501, left side). OSM has Bobcat Lane at
  30.124178, -95.740871 but no house numbers there.
- OSM 3 km extract: ~/openfps-scratch-archive/osm-magnolia-2026-10-05/magnolia-3km.json — 93 named roads,
  but only ~42 buildings and 5 addresses: buildings from Overture/Microsoft, addresses from MCAD parcels /
  OpenAddresses / TIGER ranges, trees from NLCD/WorldCover.
- Built by a worktree agent as tools/gen_osm.py (generic importer; city.json must stay untouched).
- Never put Cody's email in a User-Agent or request (I did once with Nominatim, don't repeat).

See [[city-json-is-generated]], [[map-travel-mono]].

10-05 later: Cody wants whole towns eventually (needs tile streaming; client low/med/high = full-detail radius);
importer gets area-by-boundary + tile keys + detail levels. For now Magnolia = area around the address (for his
friend to walk his own area). SECOND map: Albany OR, start 1042 Belmont Ave SW 97321 = 44.590205, -123.113244
(Census); OSM extract ~/openfps-scratch-archive/osm-albany-2026-10-05/albany-3km.json (1,889 buildings, 1,483
addresses). Id albany_or, display "albany or", not default.

**MERGED 10-06 (cce0e7ca, not pushed at write):** maps/places/magnolia_tx.json (12 MB, 32.6k entities medium)
and albany_or.json (16 MB, 41.3k); tools/gen_osm.py + fetch_place.py + mapgen.py (shared with gen_city),
inputs in tools/places/<id>/. MapData.Name display name ("magnolia tx"), /join by name or id. Spawns:
Magnolia /tp -11.2 23.2 1 (drive of 31907 Bobcat Lane; was -11.0 22.6 before 10-09 UTM-grid regen), Albany /tp -25.3 -10.6 1.4 (may be in front of a
larger second building on the lot, not the house). MCAD parcels NOT usable (licence) -> lots generated.
Full rooms within 300 m (Albany 200 m). Steam Audio scene 4.0 / 7.5 s. Restart server to serve them.
PRIVACY SLIPS 10-05/06: my Nominatim request and a wide-sources subagent's Wikimedia Commons API request
both carried Cody's email in the User-Agent. Told Cody. Every brief that may fetch from the web must say:
generic User-Agent, never the email.
