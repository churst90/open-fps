# Door types and their sound events

Cody's decision, 2026-10-02. Each door has a kind. The kind decides how it is opened, how it
moves, what it does by itself, and which mechanical events it sends.

Each event has its own sound, synthesised from measured recordings (`DoorMechanisms`,
docs/DOOR_TYPES.md). A swing on dry hinges and a steel door's sealed closer make none.

## The kinds

| Kind (`DoorKind`) | Prefab | Moves | Opened by | By itself |
|---|---|---|---|---|
| `knob` | `door` | swings 90°, 0.9 s | hand (E, `/open`) | nothing |
| `pushbar` | `steel_door` | swings 90°, 1.4 s | hand | closer: 3 s after the doorway is clear, sweep 4.5 s, latch at 1.4 s |
| `glass-pushbar` | `glass_front_door` | swings 90°, 1.1 s | hand: key outside, bar inside | closer: 3 s, sweep 4.5 s, latch at 1.1 s |
| `glass-pull` | `glass_pull_door` | swings 90°, 1.0 s | hand | closer: 3 s, sweep 4.0 s, latch at 1.0 s |
| `auto-slide` | `auto_sliding_door` | slides its width, 1.5 s | anyone within 2.5 m in front, either side | shuts 2 s after clear, over 2.5 s; reverses for anyone in the doorway |
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

| Key | When it fires | Sound |
|---|---|---|
| `door:knob:latch-retract` | a shut door starts to open | handle turned, end stop, bolt snaps out (-16 dB) |
| `door:knob:swing` | the leaf starts moving, open or shut, by hand | none |
| `door:knob:latch` | it arrives shut | bolt rides the strike, leaf and bolt hit, settle (0 dB) |
| `door:pushbar:bar` | a shut door starts to open (the bar pushed in) | three press contacts, bar return (-1 dB) |
| `door:pushbar:swing` | it starts moving by hand | none |
| `door:pushbar:closer` | the closer starts to shut it | none |
| `door:pushbar:latch` | it arrives shut | ride, slam, rattle (0 dB) |
| `door:glass-pushbar:key` | opened from the keyed side: the key turned | cam contacts, bolt thrown (-3 dB) |
| `door:glass-pushbar:latch-retract` | opened from the keyed side, after the key | latch drawn, 0.36 s after the key (-12 dB) |
| `door:glass-pushbar:bar` | opened from the other side | aluminium bar press and return (-1 dB) |
| `door:glass-pushbar:swing` | it starts moving by hand | none |
| `door:glass-pushbar:closer` | the closer starts to shut it | brush seal, the last 0.6 s before the shut (-28 dB) |
| `door:glass-pushbar:latch` | it arrives shut | ride, slam, glass rattle (0 dB) |
| `door:glass-pull:pull` | a shut door starts to open | handle and stile contact (-8 dB) |
| `door:glass-pull:swing` | it starts moving by hand | none |
| `door:glass-pull:closer` | the closer starts to shut it | brush seal, the last 0.6 s (-28 dB) |
| `door:glass-pull:latch` | it arrives shut | leaf through the seal, stile on the stop (0 dB) |
| `door:auto-slide:motor-start` | the motor starts, opening or closing | belt taking up (-3 dB) |
| `door:auto-slide:rollers` | the leaf starts travelling (lasts the travel) | rolling, gear tone, 700 Hz line, for the travel (-8 dB) |
| `door:auto-slide:stop` | it arrives fully open | stop bump, leaf settling 0.45 s later (0 dB) |
| `door:auto-slide:reopen` | it reverses for someone while closing | belt taking up and the opening travel (-3 dB) |
| `door:auto-slide:shut` | it arrives shut | stop bump, lock 0.5 s later (0 dB) |
| `door:patio-slide:latch-retract` | a shut door starts to open (the thumb latch) | lever flipped, hook lets go (-14 dB) |
| `door:patio-slide:rollers` | the leaf starts travelling, either way | rolling with grit ticks, for the travel (-12 dB) |
| `door:patio-slide:stop` | it arrives fully open | leaf on the stop, bounce (0 dB) |
| `door:patio-slide:latch` | it arrives shut | leaf on the jamb, bounce, lever and hook (0 dB) |
| `door:elevator:motor-start` | the motor starts, opening or closing | clutch opening (-6 dB), take-up closing (-16 dB) |
| `door:elevator:rollers` | the leaves start travelling | sheaves on the track, operator tones, for the travel (-12 dB) |
| `door:elevator:stop` | it arrives fully open | small bumps (-30 dB) |
| `door:elevator:reopen` | it reverses for someone while closing | take-up and the opening travel (-12 dB) |
| `door:elevator:shut` | it arrives shut | rubber edges meet, rebounds, steel rings (0 dB) |

Order for one open and shut (tested in `DoorTypeTests.EachKindNamesItsEvents`):

- knob: `latch-retract swing` ... `swing latch`
- pushbar, glass-pull: `bar swing` (or `pull swing`) ... `closer latch`
- glass-pushbar: `key latch-retract swing` from outside, `bar swing` from inside ... `closer latch`
- auto-slide, elevator: `motor-start rollers stop` ... `motor-start rollers shut`
- patio-slide: `latch-retract rollers stop` ... `rollers latch`

A bi-parting pair sends each event from each leaf.

## How the sounds are sent

- The keys are built by `DoorEvents.Of(kind, event)` in `OpenFPS.Common/Doors.cs`; each event's
  sound is made in `DoorSystem.Mech` as one `TransientSound` whose `SynthKey` is
  `doorsnd:KIND:EVENT:MATERIAL:W:H:T:SKIN:PANE:TRAVEL:DIR` (`DoorMechanisms.Key`), routed in
  `WorldAudioPlayer.RenderOne`. The level in dB is the door's main hit (its mass arriving at the
  speed it shuts at) plus the event's measured step (`DoorMechanisms.RelativeDb`).
- Sounds that last the travel (rollers, a reopen, the closer's seal) are rendered for the whole
  travel when it starts. The client fades one out if the same door sets off again, and gives them
  no echo copies.
