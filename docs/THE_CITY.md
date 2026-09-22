# The city, the footsteps, and a voice for everything that is not a car

Session 17, 2026-09-19. What changed, what it measures, and the one thing that is not finished.

---

## 1. Footsteps: the bank won

`inbox/foot steps sounds/` held twelve recordings of somebody walking, one surface each. They are now
the whole of `ASSETS/SOUNDS/FOOTSTEPS`, and the old bank — forty folders, mostly one file each, in
three formats, with `ingest_*`, `peaks/` and bare numbers side by side and nothing anywhere saying
where any of it came from — is gone.

**The decision the plan was holding open (`docs/NEXT_AFTER_THE_TAIL.md` §3) is settled by
measurement, not by preference.** `--footsteps compare=<dir> surface=<material>` puts the real steps
and the synthesised ones side by side in bands:

| material | worst band | gap |
|---|---|---|
| Wood | 8–16 kHz | model **+31.3 dB** |
| Carpet | 60–125 Hz | model **−21.2 dB** |
| Metal | 8–16 kHz | model **+18.8 dB** |
| Dirt | 60–125 Hz | model **−17.4 dB** |
| Concrete | 125–250 Hz | model −10.9 dB |
| Gravel | 8–16 kHz | model +7.8 dB |

The recordings are physically right per material and plainly different from each other — wood carries
60–250 Hz and dies above 1 kHz because a board resonates; carpet is a low thud 12–19 dB down
everywhere above 250 Hz; gravel is a 500 Hz–4 kHz crunch with no bass at all. The model is not close
enough to replace them, and it is not close on the materials where it would matter most.

`tools/rebuild_footstep_bank.py` is the bank: recording → material folder, run it again after
changing the map. 518 samples across twelve materials.

### The splitter had two faults, both measured

`tools/split_footsteps.py` cut eight of fifty cement files at the 562 ms length cap.

1. **The onset threshold was a fraction of the file's LOUDEST step.** A library recording is somebody
   approaching and going away again, so the far steps are 15–20 dB down and never fired — they were
   swallowed into the previous cut. `cement_34` held three footfalls. The reference now walks with
   the walker: the loudest step within 1.5 s either side.
2. **The tail trim worked on sample amplitude.** A recording with a low bed in it has peaks that
   cross `peak/50` long after the step has gone, so `cement_19` kept 500 ms of room at −35 dB RMS.
   It trims on the ENVELOPE now, and it also stops early when the energy comes back up — a scuff
   really does put two strikes 130 ms apart, and those were landing in one file.

Cut lengths are now 76–314 ms on cement, 198–284 on metal, 156–400 on carpet.

### Materials with no recording of their own

Twelve recordings, twenty-two materials. The borrowing is written down once, in
`SoundMappingService.RecordedStandIn`, and never by copying wavs under new names: asphalt and brick
take cement, grass takes dirt, marble takes tile, foliage takes leaves, a crowd takes concrete.

**One fault fixed on the way:** the end of the fallback chain was the literal folder `Generic`, which
no longer exists, so a material with no bank and no stand-in — glass, plaster, plastic, the shoe
materials — resolved to a SILENT footstep. The chain now ends at `NearestRecorded("Generic")`, which
is a folder with files in it.

---

## 2. The map is a city

`tools/gen_city.py` replaces one block with about a square kilometre. 5,825 entities, 658 named
places, 470 doors, 121 machines.

```
 z +560  ┌──────────────────────────────────────────────────────────┐
         │   RESIDENTIAL      │      DOWNTOWN        │   AIRPORT     │
         │   64 houses on a   │  five towers 6-10    │  588 m runway │
         │   street grid,     │  storeys, a 3-deck   │  taxiway,     │
         │   gardens, hedges, │  open-deck garage,   │  apron,       │
         │   seven mowers     │  a plaza, a tunnel   │  terminal,    │
         │                    │  under Main Street   │  steel hangar │
 z -360  └──────────────────────────────────────────────────────────┘
         x -520                                                x +520
```

...with a 2.5 km light rail loop right round the outside of all three, three stations, and a level
crossing where it meets Main Street.

### What each place measures

`--enclosure map=city at=x,y,z`:

| place | at | enclosure | open | mfp | mid decay | send at 1.6 m |
|---|---|---|---|---|---|---|
| Runway | `380,1.6,0` | 0 % | 52 % | 5.5 | 274 ms | 0 % |
| Level crossing | `0,1.6,-180` | 3 % | 51 % | 4.5 | 223 ms | 3 % |
| Residential street | `-300,1.6,-96` | 3 % | 49 % | 6.8 | 352 ms | 2 % |
| Flat, Kestrel House | `20,1.6,-60` | 61 % | 0 % | 2.1 | 425 ms | 44 % |
| Inside a house | `-325,1.6,-106` | 60 % | 0 % | 4.2 | 616 ms | 76 % |
| Main Street | `0,1.6,-40` | 43 % | 19 % | 9.0 | 1314 ms | 16 % |
| Bus shelter | `9,1.6,-60` | 90 % | **0 %** | 2.4 | **2204 ms** | **161 %** |
| Tunnel, middle | `0,1.6,-250` | 90 % | 1 % | 6.1 | 4805 ms | 59 % |
| Garage, level 0 | `-20,1.6,60` | 93 % | 1 % | 5.0 | 5884 ms | 38 % |
| Hangar | `250,1.6,190` | 86 % | 2 % | 12.6 | 8870 ms | 34 % |

The runway is the control and the reason the airport is on the map: it is the one place with nothing
to reflect off for hundreds of metres, so an engine heard there and the same engine heard in a street
differ by the street and by nothing else.

The bus shelter row is the known fault, unchanged and now reproduced at a second site on a new map.
See §7.

### Two map faults the shipped tests caught

`GhostsAndStuttersTests.EveryShippedTrackIsDriveable` failed twice while this was being built, and
both were real:

* **The estate loop ran through houses at 20 sampled points.** The residential grid had one
  north–south lane and the plots were laid across it. There are two lanes now, and no plot may stand
  within 13 m of one.
* **The rail loop ran through the tunnel at six.** The south leg crossed Main Street where Main
  Street is a hundred metres of roofed concrete box, and the formation is at rail level. A bridge
  would have needed a 9 % gradient to clear the roof — more than twice what a railway can climb — so
  the line crosses north of the portal, on the flat, which is a level crossing.

### And two geometry faults found by measuring

**Every house had its front door in the back garden.** The front wall was built at the same end of
the plot whichever way the house was turned, so the sixty-four on the north side of each street had
their front door facing the garden and their back door on the pavement. Nothing complains about that
— a door is a door to the code — and the only way to find it is to walk up to one.

**A bungalow rang at 827 ms over a 2.1 s bottom**, which is `carpet-is-deaf-to-bass` seen from the
other side: a thin absorber does nothing at a long wavelength, so a carpeted room inside bare brick
keeps its bass tail whatever you put on the floor. Brick absorbs 3 % of the bottom; plaster absorbs
28 %, because a skin on a wall is a membrane and a solid wall is not. Lined with plaster and given
the furniture a house actually has — a sofa, a bed, a wardrobe and a curtain rather than one sofa —
it measures **853 / 616 / 451 ms** low/mid/high: a living room, decaying bright-first. Geometry, not
a constant.

### ...one in the level crossing

The crossing was one box 62 cm tall, matching the ballast either side. That is not a crossing, it is
a wall across Main Street — 22 cm over `PhysicsConstants.StepHeight`, so nothing on foot or on wheels
gets past it, and the only route out of the city on that side is through it. A crossing is the road
surface carried through at ROAD LEVEL with the rails buried in it: two pieces now, each four
centimetres proud of what it is laid over. Four centimetres is a lip you feel; twelve is the one that
landed you (`a-kerb-is-not-a-cliff`).

### ...and one in the garage

The garage was built as three solid walls and one open side: a 110 × 84 × 2.5 m concrete box with a
lid on, measuring 2.4 % absorption and a **9.2 s** tail. An open-deck car park is open by law — it is
ventilated by having no walls. Rebuilt as waist-high spandrels on piers it measures 5.9 s, which is
the family the old 21 × 28 garage was in when it was approved by ear.

It is still long. Real multi-storey car parks measure 2–4 s, and that gap is the open modelling
question in `docs/NEXT_AFTER_THE_TAIL.md` §5 — the room equation uses ENCLOSURE, `e/(1−e)`, rather
than measured absorption `(1−ᾱ)/ᾱ` — not a fault in this map.

---

## 3. Traffic, and what is NOT scripted about it

22 vehicles on five routes. Nothing about traffic exists in the code: a route is a closed centreline
with rounded corners, and a vehicle's speed at every point of it comes from the curvature there
(`v = sqrt(g · 9.81 · R)`), with a backward pass pulling each limit down to what the brakes can shed
before the next slower point. A city corner is slow because it is a corner — 0.45 g on a 12 m radius
is 28 km/h, which is what turning into a side street is. Give two cars different grip and they corner
at different speeds, catch each other and queue.

* `downtown_cw` / `downtown_ccw` — the same four streets in both directions, ten cars and seven.
* `north_block` — two buses and a delivery diesel, because a ten-metre machine has its two ends five
  metres apart and you hear it turn.
* `estate` — three slow cars, which is what you hear over the mowers.
* Four shuttles: a truck and a car through the **tunnel**, an airport coach, an apron tug.

---

## 4. A voice for everything that is not a car

`OpenFPS.Client.Core/AudioEngine/Fmod/MachineProcessor.cs`.

Three models were measured and approved by ear in September and then could not be placed on a map,
because nothing gave a voice to anything that was not a vehicle engine. `PhysicalVoiceState` is that
voice: one output, rendered ahead of the mixer into a ring, on the same worker pool the engines use
(`EngineRenderPool` now takes `IRenderedVoice` rather than `EngineVoiceState`).

It covers every model whose output is one pressure at one point:

* **`MachineVoiceState`** — `machine:ac_window`, `machine:ac_condenser`, `machine:mower_push`,
  `machine:mower_riding`. 114 of them on the map: one in a window of every street-side flat on the
  lower five floors of all five towers, plant on every roof, a mower in every third garden.
* **`AircraftVoiceState`** — `aircraft:airliner`, `aircraft:turboprop`, `aircraft:piston_single`,
  `aircraft:helicopter`. Five on the map.

**The ring rules are the engine's rules**, written out again with the fault each one prevents,
because the two voices differ in shape and must not differ in behaviour. A starved block is a gap and
never a delay; a new voice hands out silence until primed; nothing is ever cut, only faded.

### What nothing on the wire carries

A mower's load is how thick the grass is and an air conditioner's compressor is on when its
thermostat calls for it. Neither is world state anybody needs to agree about, so both are driven in
the voice from a seed taken from the **entity id** — the same mower walks through the same grass on
every client, and forty window units on one wall are forty machines rather than one machine forty
times. `MachineVoiceTests.TwoOfTheSameMachineDoNotRunInStep` holds that.

An aircraft's **power lever comes from its climb angle** (`ClientAudioSystem.FlightPower`). Going up
is full power, level is cruise, coming down is idle with the drag doing the work — which is the whole
difference between an airliner overhead and the same airliner on approach, with nothing about the
aeroplane changed. So a map declares a descending flight path and gets an aeroplane on approach
without saying so, and one shuttle gives both an approach and a climb-out on alternate legs.

### The budget is ranked by LEVEL, not by distance

Ten voices for a hundred and twenty-six sources. Ranking by distance answers the wrong question: a 92 dB mower
three gardens away is plainly audible where a 59 dB window unit at the same distance is not, and a
142 dB airliner half a kilometre up beats every machine in the city. `ChooseLiveMachines` ranks on
`Loudness.RenderedGain` — each source's own level, placed, paid down for its own extent, rolled off
over its own distance. Aircraft share that one budget for the same reason: two budgets would mean
deciding in advance how many aeroplanes are worth how many machines, which has no answer that does
not depend on where the listener is standing.

There is no borrowed-voice fallback. A machine outside the budget is not heard, because there is no
sense in which forty air conditioners are one air conditioner heard from further away. That is what
aggregation is for, and this is not it.

### Held by measurement

`OpenFPS.Tests/MachineVoiceTests.cs`: every machine renders within 6 dB of the level it declares,
none of them lives on the soft ceiling, placement keeps them in the order their levels put them in at
3, 10 and 30 m, and two of the same machine do not correlate.

---

## 5. The survey got 3.5x faster, for free

`Enclosure.Look` cast 192 rays and 192 bounces, and every one of them walked the WHOLE solid list
rejecting what was out of range. On a 4,165-box city that is one and a half million distance tests to
look at the two hundred boxes actually within reach: **32 ms per survey**, against 7.5 ms on the
390-box block, for the same question about the same room.

It is asked once now (`Enclosure.Nearby`). **9 ms** on a map ten times the size — better than the old
small map cost. Nothing about the measurement changes; the same boxes are hit in the same order.

---

## 6. What a fifty-fold map found in the client

Reported as *"when I load the map and begin to move around the client crashes"*. It was not a crash;
it was the client stalling itself — `Game loop: 0 Hz, worst iteration 88 ms`, the audio pass at
74 ms against a 17 ms budget, gen2 collections pausing 57 ms. Four faults, and every one of them is
the same shape: a loop over the whole map that was invisible at 504 entities.

**1. An EMPTY spatial-grid answer was treated as a failure.** `ClientPhysicsSystem` asked the static
grid for solid geometry within `CollisionSearchRadius` (5 m) and, finding none, fell back to
`snapshot.Entities.Values` — all 5,825 of them — through the collision solver, every physics tick.
An empty answer is an answer: nothing within five metres is exactly what standing in the middle of a
twelve-metre carriageway is, so it fired continuously, and it fired precisely where a listener spends
their time. `SpatialService` did the same for occlusion. Only a *missing* grid — the seconds before
the first rebuild — is now a reason to consider everything.

**2. The moved-region check walked every entity every frame** to find the ~600 that declare a region.
`WorldSnapshot.RegionEntityIds` carries them now, the way `AudioEntityIds` already did.

**3. `PhysicalLevel` built a whole model per machine per frame.** `SmallMachineSpec.ByName` goes
through `ModelLibrary.Get`, which *constructs* the spec on every call — a governor, a deck, a blade
row, a casing, all nested records. Measured: **2.3 MB allocated and 5.1 ms of CPU for one second's
worth** (121 machines × 60 Hz), on the thread that also places every moving sound.
`VehicleProfile.ByName` is memoised for exactly this reason and says so.

**4. The sample reflection path tried to play `machine:ac_window` as a file**, failing every frame
with a deferred play queued behind each — 174 of them in a two-minute log. It skipped `engine:` by
name, and the comment above it already described this exact failure for engines. It skips anything
with `IsSynth` now, which is the property that actually decides it.

And the instrument that should have existed first: the audio update now reports where its time goes
by stage on the "Audio placement" line — `listener+map, paths, budget, emitters, mixer`. "The pass
took 74 ms" is not a number anybody can act on.

---

## 7. What is not finished

### The bus shelter, and the gate on it

`OpenFPS.Tests/EnclosureTests.cs` now holds the pair the plan demanded before any fourth attempt:

* `AStreetShelterIsNotACathedral` — shelter, road and both facades at city.json's real distances and
  materials. Asserts open > 15 %, surface < 150 m², mid decay < 0.8 s. **Fails at the first
  assertion (1 % open)** and is marked `[Fact(Skip = ...)]` with the reason, following the
  convention in `FootstepTests.cs`.
* `AFlatGarageStillRings` — 21 × 28 × 2.5, sealed. **Passes**, and is the guard: median-keyed
  shrinking put this at 0.7 s last time.

The fix specified but not attempted: **a boundary is where the openness changes.** For each ray that
finds a surface, sample a point along its path and compare that point's openness with the listener's
own. A jump means the ray crossed out. Checked against every place on the map — it fires on the
shelter's front cone (listener 0 % open, the road 32 %), leaves the garage alone (1 % against 1 %),
and leaves the street's modest tail alone (32 % against 32 %), which the naive "sky above = escape"
test would have killed. The map is static, so the openness field is bakeable at scene build and the
per-ray lookup is a table read.

Its known limit, stated in advance: it fixes a small structure standing in the OPEN. It does not fix
a small room opening onto a big enclosed hall — both read closed, so no jump fires.

### The light rail

The railway is complete and measurable: 2.5 km of formation and rails, three tiled stations under
steel canopies, a level crossing, and `Tracks["rail_loop"]` — the same centreline the ballast was
laid along, so the two cannot drift apart. **Two light rail sets run it since 2026-09-20**, half a
lap apart, the second of the two answers below: the server places one entity per sound source
(`RailSystem`, `TrainLayout`) at `head − along` round the loop, and the client runs one `TrainSynth`
per set with a voice per source (`RailVoice.cs`). They slow for the corners on the loop's own
curvature and do not stop at the stations yet. The rest of this section is the reasoning as it stood
before that, kept because it is still the reason it was done this way.

A train is the one approved model that is NOT one pressure at one point: it is a line of bogies, ten
or eleven radiators spread over fifty metres of consist, each with its own place along the track
(`docs/TRAINS.md`). Giving it a voice means one of two things, and both are decisions about an
approved model:

1. **The client knows the track geometry.** Placing the sources in a straight line behind the head is
   fine on a straight and nowhere near right on a 26 m corner, where a 55 m consist wraps more than a
   half turn. The track polyline is server-side `MapData` today and would have to be sent.
2. **The server places each radiator**, spawning the consist as one entity per source and sampling
   the track at `head − along` for each. Correct round curves, and it needs the client to run ONE
   `TrainSynth` shared across several entities, resolved through a parent — the sources share
   `Speed`, `Notch` and one `TrackResponse`, and splitting them into independent synths would give
   each bogie its own rail.

(2) is the better answer. Neither is a thing to guess at while nobody is listening.

---

## What to listen to first

```
./run-server.sh city

/tp 0 -40 0.1        Main Street, the spawn — traffic both ways, towers either side
/tp 9 -60 0.1        the bus shelter — the fault, still there
/tp 20 -60 0.1       a flat in Kestrel House, carpet and furniture
/tp -20 60 0.1       the garage, level 0
/tp 0 -250 0.1       the tunnel — a truck goes through it every minute or so
/tp 0 -180 0.1       the level crossing
/tp -300 -96 0.1     the estate — mowers, and one slow car
/tp -325 -106 0.1    inside a house — plaster, carpet, a sofa and a bed
/tp 380 0 0.1        the runway — nothing to reflect off, the control
/tp 250 190 0.1      inside the hangar
```

Aircraft: the overhead airliner comes round about every two minutes, the turboprop and the light
single work the airfield, and the helicopter crosses the city at rooftop height once a cycle.
