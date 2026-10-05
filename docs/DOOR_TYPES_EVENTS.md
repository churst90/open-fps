# Door types and their sound events

Cody's decision, 2026-10-02. Each door has a kind. The kind decides how it is opened, how it
moves, what it does by itself, and which mechanical events it sends. Push and pull sides, the key
sequence and obstruction were added 2026-10-05.

Every kind has a physical sound model: `KnobDoor`, `PushBarDoor`, `SlidingDoor` (patio, automatic),
`GlassDoor` (glass-pushbar, glass-pull), `ElevatorDoor`, and `LockCylinder` for a key in any keyed door
(branch door-models-glass, 2026-10-05, unheard). Events with no sound yet are still
sent (see the table below).

## The kinds

| Kind (`DoorKind`) | Prefab | Moves | Opened by | By itself |
|---|---|---|---|---|
| `knob` | `door` | swings 90°, 0.9 s | hand (E, `/open`): pushed from its +Z face, pulled from the other | nothing |
| `pushbar` | `steel_door` | swings 90°, 1.4 s | hand: bar pushed from inside (-Z face), pull handle outside (+Z face) | closer: 3 s after the doorway is clear, sweep 4.5 s, latch at 1.4 s |
| `glass-pushbar` | `glass_front_door` | swings 90°, 1.1 s | hand: key and pull outside (+Z), bar inside | closer: 3 s, sweep 4.5 s, latch at 1.1 s |
| `glass-pull` | `glass_pull_door` | swings 90°, 1.0 s | hand: pulled outside (+Z), pushed inside | closer: 3 s, sweep 4.0 s, latch at 1.0 s |
| `auto-slide` | `auto_sliding_door` | slides its width at its controller's speeds (0.7 m/s open, 0.3 m/s shut, a creep into each end: about 3.3 s and 5.3 s for the city's 1.15 m leaves; `SlidingDoor.AutomaticSeconds`) | anyone within 2.5 m in front, either side | shuts 2 s after clear; reverses for anyone in the doorway |
| `patio-slide` | `patio_door` | slides its width, 1.4 s | hand | nothing |
| `elevator` | `elevator_door` | slides its width, 1.8 s | the lift (`DoorSystem.Set`), never by hand | shuts 4 s after clear, over 2.5 s; reverses for anyone in the doorway |

- A bi-parting door is two leaves, the second turned half a turn so it slides the other way.
  `tools/gen_city.py` `bi_parting()` places a pair.
- A closer sweeps at `CloseSeconds` and does the last 12 % (`DoorSystem.LatchZone`, about the
  last 10 degrees) at `SwingSeconds`, to carry the latch.
- "In the doorway": within the leaf's width plus 0.4 m across it, and within 0.8 m through it
  (a swinging leaf: its own width). "Anyone" is a player on foot or a person in the street
  (city walkers, drivers who parked).
- Parked drivers open hand doors by hand and leave closers and automatic doors to shut
  themselves. They wait for a door to be open before walking through it.

## Push and pull

Every hinged leaf has a push side, `PushSide` on the prefab (and on `DoorComponent`): +1 means it
is pushed from its own +Z face, -1 from its -Z face. It swings away from the push side, so from the
other side it is pulled. A door saved before this existed reads 0, which means +1: the way every
door swung before.

The convention is that a door's +Z face is its outside:

- A room door (`door`, `PushSide` 1) is pushed from the corridor or the street and swings into
  the room. From inside it is pulled.
- An exit door (`steel_door`, `glass_front_door`, `glass_pull_door`, `PushSide` -1) is pushed from
  inside and swings out, toward the street or the roof, the way an exit swings. Its push bar, if it
  has one, is on the inside face only. From outside it has a pull handle.

`tools/gen_city.py` `door()` turns every hinged leaf so its +Z face points away from its first
place (`a`, the room it belongs to). So the flats' and houses' doors swing into the flat or house,
and the tower entrances, roof doors, the terminal's road door and the hangar's back door swing out.
`DoorSidesTests.TheCitysDoorsSwingTheRightWay` checks all 409.

The side someone opens a door from is where they stand (`DoorSystem.Set`'s `by`, or the position of
`who`). If nobody is known, it is the push side. The side used last is kept in
`DoorComponent.OpenedFrom`: +1 pushed, -1 pulled, 0 not by hand (a slider, the lift, a sensor).

A map entity can override `KeyedSide` and `PushSide` for one door, for example to key a fire exit
from outside on one map only.

## The key

`KeyedSide` (prefab or map entity) says which face is locked: +1 the +Z face, -1 the other, 0
neither. The glass front door is keyed on its +Z face, the street.

From the keyed side a shut door is locked. Opening it takes `DoorSystem.KeySequenceSeconds` (1 s)
before the leaf moves:

| Time | Event |
|---|---|
| 0 s | `key-insert` |
| 0.45 s (`KeyTurnSeconds`) | `key-turn` |
| 0.7 s (`UnlockSeconds`) | `unlock`: the latch drawn back. Carries that kind's pull-side latch sound for now (none on a knob door) |
| 1.0 s | the leaf starts to move: `pull` (or `push` when the keyed side is the push side). A knob door sends `latch-retract` first, because its knob is still turned after the key |

When it shuts again it is locked again. A door that is not fully shut is not locked. Shutting it
while the key is still turning leaves it shut.

Every player and every resident has the key, so it always opens. A real lock needs a key item:
`DoorComponent` would get a lock id, an inventory item would carry the same id, and
`DoorSystem.Set` would refuse from the keyed side without it. That is not built, and who holds keys
is Cody's decision.

What a player hears said:

- "You unlock the Brandt Court front entrance with your key and pull it open."
- "You push the bar and the stair door swings open."
- "You pull the flat 2A door open." / "You push the flat 2A door open."
- "The back door slides open."

## Nobody is swept aside

No leaf moves into a space a person occupies, whatever moves it: a hand, a closer or a motor.
"A person" is a player on foot or a pedestrian, counted with a body radius of 0.3 m
(`DoorSystem.BodyRadiusMetres`), on the same floor.

- A hinged leaf sweeps a slice of a disc about its hinge, as wide as the leaf, between where it is
  and where it is going. Only what is ahead of it counts: someone behind an open leaf, between it
  and the wall, is not in the way of it shutting. Someone within a body's radius of where it ends
  up is (the doorway when shutting, the wall when opening).
- A sliding leaf sweeps the strip its leading edge runs along, the leaf's thickness and a body
  either side.
- The leaf moves until it reaches the person and stops there. When they move, it carries on.
- A closer waits. It also still waits for anyone in the doorway box above, as before.
- A motor closing on someone reverses (`reopen`).
- A hand close is refused while someone else is in the way: "Someone is in the way of the X." The
  person closing it walks it shut, so they are only in the way standing in the doorway itself: "You
  are in the way of the X. Step out of the doorway first." If someone steps in while a hand is
  shutting it, the leaf stops against them and goes on when they move.
- A door pushed open into someone behind it stops against them. Whoever pulls a door open steps
  back with it and does not stop it.

`DoorSystem.InTheWay(world, door, to, who)` says who is in the way. `DoorSystem.Set` refuses a hand
close by itself, so every caller (E, `/close`, parked drivers) gets the rule.

## Event keys

Each event is a world sound whose label is `door:KIND:EVENT`. An event with no sound yet is
still fired (the server drops it before sending, as it does any empty sound).

| Key | When it fires | Sound now |
|---|---|---|
| `door:knob:latch-retract` | a shut door starts to open, either side | `KnobDoor` model, opening (`knobdoor:open:...`; from the push side `...:push`, turned and pushed) |
| `door:knob:push` / `door:knob:pull` | the leaf starts moving open by hand, from the push or the pull side | none (in the model's opening) |
| `door:knob:swing` | it starts moving shut by hand, or open with no side known | none |
| `door:knob:latch` | it arrives shut | `KnobDoor` model, closing (`knobdoor:close:...`) |
| `door:pushbar:bar` | a shut door is opened from the push side: the bar pushed in | `PushBarDoor` model, opening (`pushbardoor:open:...`) |
| `door:pushbar:latch-retract` | a shut door is opened from the pull side: the handle's trim draws the latch | `PushBarDoor` model, the trim's lever pulled (`pushbardoor:open:...:pull`) |
| `door:pushbar:push` / `door:pushbar:pull` | the leaf starts moving open by hand | none |
| `door:pushbar:swing` | it starts moving shut by hand | none |
| `door:pushbar:closer` | the closer starts to shut it | none |
| `door:pushbar:latch` | it arrives shut | `PushBarDoor` model, closing (`pushbardoor:close:...`) |
| `door:glass-pushbar:key-insert` | opened from the keyed side: the key goes in | `LockCylinder`, the whole key: in, turned at 0.45 s, latch drawn by 0.7 s (`lockcylinder:unlock:aluminium:V`) |
| `door:glass-pushbar:key-turn` | the key turns, 0.45 s later | none |
| `door:glass-pushbar:unlock` | the latch is drawn back, 0.7 s after the key went in | none (in the key's render) |
| `door:glass-pushbar:bar` | opened from inside, the push side | `GlassDoor`, the bar shoved (`glassdoor:bar:open:push:...`) |
| `door:glass-pushbar:latch-retract` | opened from outside with no key (only if a map unkeys it) | `GlassDoor`, pulled with the latch held back (`glassdoor:bar:open:pull:...`) |
| `door:glass-pushbar:push` / `door:glass-pushbar:pull` | the leaf starts moving open by hand | after the key: `GlassDoor`, pulled with the key held, the key let go (`glassdoor:bar:open:key:...`); otherwise none |
| `door:glass-pushbar:swing` | it starts moving shut by hand | none |
| `door:glass-pushbar:closer` | the closer starts to shut it | none |
| `door:glass-pushbar:latch` | it arrives shut | `GlassDoor`, the closer into the seal and the latch (`glassdoor:bar:close:...`) |
| `door:glass-pull:push` / `door:glass-pull:pull` | a shut door starts to open by hand (no latch: the hand is the first sound) | `GlassDoor`, the handle shoved or pulled (`glassdoor:pull:open:push|pull:...`) |
| `door:glass-pull:swing` | it starts moving shut by hand | none |
| `door:glass-pull:closer` | the closer starts to shut it | none |
| `door:glass-pull:latch` | it arrives shut | `GlassDoor`, the closer onto the seal (`glassdoor:pull:close:...`) |
| `door:KIND:key-insert`, `key-turn`, `unlock` | any kind opened from a keyed side | `LockCylinder` at `key-insert` (aluminium stile, steel door or wooden door by kind); `key-turn` and `unlock` none |
| `door:auto-slide:motor-start` | the motor starts, opening or closing | the whole run, `SlidingDoor` (`slidingdoor:auto:...`) |
| `door:auto-slide:rollers` | the leaf starts travelling (lasts the travel) | none |
| `door:auto-slide:stop` | it arrives fully open | none |
| `door:auto-slide:reopen` | it reverses for someone while closing | none |
| `door:auto-slide:shut` | it arrives shut | none (in the run) |
| `door:patio-slide:latch-retract` | a shut door starts to open (the thumb latch) | the whole opening, `SlidingDoor` (`slidingdoor:patio:open:...`) |
| `door:patio-slide:rollers` | the leaf starts travelling, either way | shutting: the whole shut, roll to latch, `SlidingDoor` |
| `door:patio-slide:stop` | it arrives fully open | none |
| `door:patio-slide:latch` | it arrives shut | none (in the run) |
| `door:elevator:motor-start` | the motor starts, opening or closing | the whole run, `ElevatorDoor` (`elevatordoor:open|close:...:op|leaf`; the leaf with HingeSide 1 carries the operator) |
| `door:elevator:rollers` | the leaves start travelling | none |
| `door:elevator:stop` | it arrives fully open | none |
| `door:elevator:reopen` | it reverses for someone while closing | none |
| `door:elevator:shut` | it arrives shut | none (in the run) |

The old event `key` is gone; `DoorEvents.Key` is kept in code as another name for `KeyTurn`.

Order for one open and shut (tested in `DoorTypeTests.EachKindNamesItsEvents` and `DoorSidesTests`):

- knob: `latch-retract push` (or `pull`) ... `swing latch`
- pushbar: `bar push` from inside, `latch-retract pull` from outside ... `closer latch`
- glass-pushbar: `key-insert key-turn unlock pull` from outside, `bar push` from inside ... `closer latch`
- glass-pull: `push` from inside, `pull` from outside ... `closer latch`
- auto-slide, elevator: `motor-start rollers stop` ... `motor-start rollers shut`
- patio-slide: `latch-retract rollers stop` ... `rollers latch`

A bi-parting pair sends each event from each leaf.

## For the synthesis session

- The keys are built by `DoorEvents.Of(kind, event)` in `OpenFPS.Common/Doors.cs`, and the
  sounds for each are chosen in `DoorSystem.Events`, `DoorSystem.OpenStart` and
  `DoorSystem.TurnTheKey`.
- Push or pull: `DoorSystem.KnobDoorSound` and `DoorSystem.PushBarSound` take `pulled` (true from
  the pull side). They do not pass it on yet; to give the models a push and a pull opening, pass it
  into `KnobDoor.Key` / `PushBarDoor.Key`. The same fact is `DoorComponent.OpenedFrom` (-1 pulled).
- The key's sounds: send `key-insert`, `key-turn` and `unlock` with a `SynthKey` in
  `DoorSystem.TurnTheKey`. Their times are `KeyTurnSeconds`, `UnlockSeconds` and
  `KeySequenceSeconds`; change those if the recorded sequence is longer or shorter.
- To give an event its own model, send it with a `SynthKey` (as `CarDoor` does) and route that
  key in `WorldAudioPlayer.RenderOne`. A `SynthKey` is cached as one buffer per seed, so it
  must name one fixed sound, not a family.
- What each leaf is made of, its size and the speed it arrives at are known where the event is
  fired (`DoorSystem.Sounds`). Every door sound goes out through `DoorSystem.OnTheLeaf`.
