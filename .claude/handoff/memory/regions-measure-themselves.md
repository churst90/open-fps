---
name: regions-measure-themselves
description: A map's named places take their materials and their indoor-ness from the geometry at load
metadata:
  type: project
---

`MapManager.SurveyRegions` (2026-09-19) runs the composite survey over a STATIC map at load. A region
entity now says only **where a place is and what it is called**; its six face materials and its
`IsIndoor` are measured from the walls actually round it. `CompositeAcoustics.SurveyBox` is the new
entry point — the same three questions asked of a box that is **already known**, because a map's
walls are shared (the wall between two flats belongs to both) and deriving a box from them hands you
the whole building.

Three rules, all load-bearing:
- **Fills blanks, never overrules.** Only material `None` (index 0) is replaced. That is why no
  approved map changed — the speedway is untouched and `default.json` keeps its four authored regions.
- **Only enclosures.** Outdoors the reflections come from the individual walls, so a named stretch of
  street gets `IsIndoor=false` and no materials.
- `IsIndoor` is measured unless the map asserted it.

Two faults it exposed in `Face`, both invisible and both fixed: a part is credited only its **overlap
with the face's rectangle** (a slab six metres sideways was roofing the street), and distance is
measured to the part's **nearest** edge (a 3.5 m thick tunnel wall was not a wall, so the tunnel read
as open sky). `MapSurveyTests` holds both.

**Why:** it is the "automatic, with the entity as an override" item open since session 9, and it is
what lets a building be boxes and doors and nothing else.
**How to apply:** author no `RoomMaterials` and no `IsIndoor` on a new map; read the
`'<map>' measured '<name>'` log lines after a change. See [[city-block-map]], [[region-is-not-a-room]], [[no-special-cases]].
