# Door types and their sound events

Cody's decision, 2026-10-02. Each door has a kind. The kind decides how it is opened, how it
moves, what it does by itself, and which mechanical events it sends.

The sounds are not synthesised yet. Each event is sent now with the door sounds that already
exist, or with no sound. The next session synthesises each one from measured recordings.

## The kinds

| Kind (`DoorKind`) | Prefab | Moves | Opened by | By itself |
|---|---|---|---|---|
| `knob` | `door` | swings 90°, 0.9 s | hand (E, `/open`) | nothing |
| `pushbar` | `steel_door` | swings 90°, 1.4 s | hand | closer: 3 s after the doorway is clear, sweep 4.5 s, latch at 1.4 s |
| `glass-pushbar` | `glass_front_door` | swings 90°, 1.1 s | hand: key outside, bar inside | closer: 3 s, sweep 4.5 s, latch at 1.1 s |
| `glass-pull` | `glass_pull_door` | swings 90°, 1.0 s | hand | closer: 3 s, sweep 4.0 s, latch at 1.0 s |
| `auto-slide` | `auto_sliding_door` | slides its width at its controller's speeds (0.7 m/s open, 0.3 m/s shut, a creep into each end: about 3.3 s and 5.3 s for the city's 1.15 m leaves; `SlidingDoor.AutomaticSeconds`) | anyone within 2.5 m in front, either side | shuts 2 s after clear; reverses for anyone in the doorway |
| `patio-slide` | `patio_door` | slides its width, 1.4 s | hand | nothing |
| `elevator` | `elevator_door` | slides its width, 1.8 s | the lift (`DoorSystem.Set`), never by hand | shuts 4 s after clear, over 2.5 s; reverses for anyone in the doorway |

- A bi-parting door is two leaves, the second turned half a turn so it slides the other way.
  `tools/gen_city.py` `bi_parting()` places a pair.
- A closer sweeps at `CloseSeconds` and does the last 12 % (`DoorSystem.LatchZone`, about the
  last 10 degrees) at `SwingSeconds`, to carry the latch.
- A closer never shuts on anyone in the doorway. If someone steps in while it is closing, the
  leaf stops where it is and carries on once they have gone.
- "In the doorway": within the leaf's width plus 0.4 m across it, and within 0.8 m through it
  (a swinging leaf: its own width). "Anyone" is a player on foot or a person in the street
  (city walkers, drivers who parked).
- Parked drivers open hand doors by hand and leave closers and automatic doors to shut
  themselves. They wait for a door to be open before walking through it.

## The key

A glass front door is keyed on its leaf's +Z face (`KeyedSide`), turned to the street.
Opening it from that side turns a key first. Every player and every resident has the key, so
it always opens. A real lock needs a key item: `DoorComponent` would get a lock id, an
inventory item would carry the same id, and `DoorSystem.Set` would refuse from the keyed side
without it. That is not built.

## Event keys

Each event is a world sound whose label is `door:KIND:EVENT`. An event with no sound yet is
still fired (the server drops it before sending, as it does any empty sound).

| Key | When it fires | Sound now |
|---|---|---|
| `door:knob:latch-retract` | a shut door starts to open | the existing opening sounds |
| `door:knob:swing` | the leaf starts moving, open or shut, by hand | none |
| `door:knob:latch` | it arrives shut | the existing closing sounds |
| `door:pushbar:bar` | a shut door starts to open (the bar pushed in) | the existing opening sounds |
| `door:pushbar:swing` | it starts moving by hand | none |
| `door:pushbar:closer` | the closer starts to shut it | none |
| `door:pushbar:latch` | it arrives shut | the existing closing sounds, at latch speed |
| `door:glass-pushbar:key` | opened from the keyed side: the key turned | none |
| `door:glass-pushbar:latch-retract` | opened from the keyed side, after the key | the existing opening sounds |
| `door:glass-pushbar:bar` | opened from the other side | the existing opening sounds |
| `door:glass-pushbar:swing` | it starts moving by hand | none |
| `door:glass-pushbar:closer` | the closer starts to shut it | none |
| `door:glass-pushbar:latch` | it arrives shut | the existing closing sounds |
| `door:glass-pull:pull` | a shut door starts to open | the existing opening sounds |
| `door:glass-pull:swing` | it starts moving by hand | none |
| `door:glass-pull:closer` | the closer starts to shut it | none |
| `door:glass-pull:latch` | it arrives shut | the existing closing sounds |
| `door:auto-slide:motor-start` | the motor starts, opening or closing | the whole run, `SlidingDoor` (`slidingdoor:auto:...`) |
| `door:auto-slide:rollers` | the leaf starts travelling (lasts the travel) | none |
| `door:auto-slide:stop` | it arrives fully open | none |
| `door:auto-slide:reopen` | it reverses for someone while closing | none |
| `door:auto-slide:shut` | it arrives shut | none (in the run) |
| `door:patio-slide:latch-retract` | a shut door starts to open (the thumb latch) | the whole opening, `SlidingDoor` (`slidingdoor:patio:open:...`) |
| `door:patio-slide:rollers` | the leaf starts travelling, either way | shutting: the whole shut, roll to latch, `SlidingDoor` |
| `door:patio-slide:stop` | it arrives fully open | none |
| `door:patio-slide:latch` | it arrives shut | none (in the run) |
| `door:elevator:motor-start` | the motor starts, opening or closing | none |
| `door:elevator:rollers` | the leaves start travelling | none |
| `door:elevator:stop` | it arrives fully open | none |
| `door:elevator:reopen` | it reverses for someone while closing | none |
| `door:elevator:shut` | it arrives shut | the existing closing sounds |

Order for one open and shut (tested in `DoorTypeTests.EachKindNamesItsEvents`):

- knob: `latch-retract swing` ... `swing latch`
- pushbar, glass-pull: `bar swing` (or `pull swing`) ... `closer latch`
- glass-pushbar: `key latch-retract swing` from outside, `bar swing` from inside ... `closer latch`
- auto-slide, elevator: `motor-start rollers stop` ... `motor-start rollers shut`
- patio-slide: `latch-retract rollers stop` ... `rollers latch`

A bi-parting pair sends each event from each leaf.

## For the synthesis session

- The keys are built by `DoorEvents.Of(kind, event)` in `OpenFPS.Common/Doors.cs`, and the
  sounds for each are chosen in `DoorSystem.Events` and `DoorSystem.OpenStart`.
- To give an event its own model, send it with a `SynthKey` (as `CarDoor` does) and route that
  key in `WorldAudioPlayer.RenderOne`. A `SynthKey` is cached as one buffer per seed, so it
  must name one fixed sound, not a family.
- What each leaf is made of, its size and the speed it arrives at are known where the event is
  fired (`DoorSystem.Sounds`).
