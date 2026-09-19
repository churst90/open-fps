# The city block, and rooms that measure themselves (2026-09-19)

Step 1 of `docs/NEXT_THE_CITY.md`: *"two apartment blocks with interiors, a street between, a tunnel
at one end, a garage, a bus shelter, a metro platform with overhang, zones under all of it."* Static —
no traffic, no pedestrians, no rail, no aircraft. Each of those needs something this map is the test
of, and the order they come in is in that document.

    ./run-server.sh city

## The map

`tools/gen_city.py` → `OpenFPS.Server/maps/city.json`. 397 entities, **81 named places**, 44 doors.

Generated, because a building is a hundred boxes whose coordinates all have to agree and a storey is
the same hundred boxes three metres up. The dimensions at the top of the generator are the source;
the map is an output. A wall a decimetre out is not a visible mistake — it is a room that stops being
enclosed and sounds slightly wrong for ever.

| | what it is | why it is on the map |
|---|---|---|
| Two apartment blocks | 21 × 40 m, three storeys, a corridor with four flats each side, a tiled stairwell with real climbable steps, doors in every doorway | "Buildings with insides, not solid blocks". Also the case the survey has to get right eighty times over |
| The street | 12 m of asphalt between concrete pavements, street trees down both | Asphalt absorbs three or four times what concrete does. Stepping off the kerb should be audible |
| The tunnel | 30 m of roofed concrete, open at both ends | The most enclosed place on the map. If it does not read as the longest decay, the survey is wrong |
| The parking garage | two decks, 2.5 m clear, solid on three sides, columns on the fourth | Low, hard and open-sided: the case the wet/dry law has never been heard in |
| The bus shelter | three glass sides and a metal roof | Enclosed *barely* — five faces of six |
| The metro platform | tiled, under a steel canopy on columns, beside an open track bed | Tile is the least absorbent surface there is. And it is where a light rail will stop |

**Nothing on this map authors an acoustic anything.** No `RoomMaterials`, no `IsIndoor`. Every one of
the 81 places is measured at load from the walls that are actually round it.

## Rooms that measure themselves

The open item from the last two sessions — *"run the survey over a static map's solid parts at load
and derive the six face materials from the walls actually there; keep `acoustic_region` for what
geometry cannot say"* — is done, as `MapManager.SurveyRegions`.

A region entity now says **where a place is and what it is called**, and nothing else. What it is
made of and whether it is a room at all are measured. The prefab ships no materials, so a region that
forgot them was six faces of `None`, which the reverb reads as **perfectly reflective** — a flat with
the reverberation of a cathedral, and silent about it.

Three rules keep it honest:

- **It fills in blanks and never overrules.** A face that already names a material was named by
  somebody who could see the map, possibly to say a thing geometry cannot (carpet over a slab, a
  lined ceiling). Only material `None` is replaced. That is also why no approved map changed: the
  speedway is untouched, the rooms map keeps its four authored regions.
- **It only measures enclosures.** A face's material decides what comes back off it, and that happens
  inside something. Outdoors the reflections come from the individual walls that are there. A named
  stretch of street gets `IsIndoor = false` and no materials.
- **`IsIndoor` is measured too**, unless the map asserted it — four of six faces walled, the same
  rule a player's shed is judged by.

`CompositeAcoustics.SurveyBox` is the new half: the same three questions asked of a box that is
**already known**. A composite is measured in a box derived from its own parts, which is the whole
trick of it; a map's room is a box an author drew, and the walls round it are shared — the wall
between two flats belongs to both, the corridor wall runs the length of the building. Ask those parts
to derive a box and they hand you the building.

### Two faults it found in the shared survey, both invisible

**A slab six metres to one side was a ceiling.** `Face` measured only the distance *along* the face's
axis, so a building's first-floor slab — three metres up and entirely inside the building — was
within tolerance of a four-metre pavement's ceiling plane. Every stretch of pavement in the city came
back enclosed, indoors, with a concrete ceiling. A part is now credited the area of its **overlap
with the face's own rectangle**, so a part that is not over you is not your ceiling. For a composite,
whose box is derived from its parts, this is the same answer it always gave.

**A wall three and a half metres thick was not a wall.** A part belonged to a face if its *outer*
edge was near the plane — true for a composite and false for a box drawn first, where a thick wall's
far side is metres away. The tunnel came back with no side walls at all and read as open sky. It now
measures to the part's **nearest** edge, and zero if the plane runs through it. What faces you is the
side of the wall that faces you.

Both are in `MapSurveyTests`, with the city geometry that produced them.

### What it reads on the city block

```
'city' measured 'Tunnel, middle third' (12 x 5.5 x 10): 4/6 walled, 0 % solid
    -> floor Asphalt, ceiling Concrete, east wall Concrete, west wall Concrete
'city' measured 'Parking garage, level 0' (21 x 2.5 x 28): 5/6 walled, 4 % solid
    -> floor Concrete, ceiling Concrete, north/south/west Concrete
'city' measured 'Eastside flat 01B' (8.7 x 2.7 x 9.8): 6/6 walled, 2 % solid
    -> floor Concrete, ceiling Concrete, north Concrete, south Brick, east Brick, west Concrete
'city' measured 'Bus shelter' (3.2 x 2.4 x 4.4): 5/6 walled, 7 % solid
    -> floor Dirt, ceiling Metal, north Glass, south Glass, east Glass
'city': 60 region(s) had blank faces filled in from the geometry, 0 kept an authored list.
```

Sixty of the eighty-one are rooms; the other twenty-one are street, pavement and platform, and are
outdoors because nothing is over them.

## Four materials a city needed and the table did not have

Each is a difference a listener can hear against the Concrete that was standing in for all of them.

- **Brick** — takes as little as concrete and **scatters four times as much**, because it is courses
  and raked joints rather than a poured face. A brick street is a wash where a concrete underpass is
  a slapback, and it is one number apart.
- **Asphalt** — porous, so three or four times concrete's absorption and most of it at the top of the
  band. That is why a concrete motorway is louder than a bituminous one. Bitumen is also two orders
  lossier, so a road is the one hard ground that does not ring.
- **Tile** — one per cent absorption and six per cent scattering: the hardest, flattest surface in
  ordinary life, and why a tiled concourse is the most reverberant room most people ever stand in.
- **Foliage** — not a surface but a volume of thousands of small scatterers. Ninety-two per cent
  scattering and rising absorption with frequency, which is what people mean when they say a treed
  street is quieter.

A trap worth knowing: **`AcousticRegistry.GetProperties` falls back to `Generic` in silence.** Steel
is spelled `"Metal"`; asking for `"Steel"` gets a 5 GPa plastic. `PrefabValidator` catches it for
prefabs and nothing catches it in C#.

## Where this leaves the order

Done: the block, and rooms that measure themselves. Next, in the order of `docs/NEXT_THE_CITY.md`:

1. **Walk it and listen** — `capture`, and `--room-walk` in the tunnel, the garage and a stairwell.
   Every number above is measured; none of it has been heard.
2. **Fused early reflections** (§1.3 of that document), tried here, because a corridor and a
   stairwell are where the difference between "a tail" and "the walls answering" is largest.
3. **Roads as data and a lane follower**, then aggregation, then rail, then aircraft.

And the two machines that now exist and are not yet placeable (`docs/YARD_MACHINES.md`): a condenser
unit belongs on the garage roof and a mower in the strip behind the west block, and both need the
client voice path a stationary machine does not have yet.
