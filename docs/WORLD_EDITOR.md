# World editor

The design agreed with Cody on 2026-10-06, and how it is built. Phase 1 is the foundation: the menu,
selecting things, moving and turning them, settings generated from what each kind of thing says about
itself, undo and redo, and edits that are kept.

## 1. What it is

- F12 opens the World Editor menu, in the game, while you play. There is no build mode: you walk, you
  collide, you can be hurt. Close the menu (Escape) to move, press F12 again to carry on.
- Everything you can place or tune is a **model** in one library: a prefab, a machine, a water
  feature, a fire, a tree, a horn, a train, later a vehicle, an engine, a character, a road. Each kind
  of model **describes itself**: its fields, their units, sensible ranges, a one-line help and, where
  the number is physical, where it came from. The editor builds its menus from that description. There
  is no screen written for one kind of thing; a new kind needs no new editor code.
- Editing a model changes it on every map that uses it, and keeps the old version. A map can pin an
  older version (phase 2).
- Edits are made by the server, one at a time, in the order they arrive. Everyone on the map hears the
  world change. Each editor has their own undo and redo.
- Edits are kept in an **overlay file** per map, laid over the map file when it loads. The map file
  itself is never written by the editor, so a generated map (city.json from tools/gen_city.py, the
  places from tools/gen_osm.py) stays byte for byte what its generator wrote.

## 2. Who may use it

| Who | Where |
|---|---|
| Admin, Dev | any map |
| A map's owner | their own map |
| People the owner asks to edit (`/map editor add NAME`) | that map |
| Moderator, other players | nowhere; F12 says "The world editor is for this map's owner, the people they ask to edit it, and developers." |

- The permission is `edit` (Permissions table: Dev, on your own map for everybody). Invited editors are
  the one addition to the scope rule: `Permissions.ForMapEditors("edit")` is true, so `edit` also holds
  on a map that lists you as an editor. No other permission is extended to invited editors.
- Changing a **model** changes it on every map, so it needs `edit-models` (Dev; Admin has everything).
  A map owner edits the things on their map and their settings, not the shared library.
- Map editors are kept with the owner and invitations in map_access.json (`Editors`), not in the map
  file. Adding an editor to a private map also invites them in.
- Every edit command is checked on the server. The client offering an item is a convenience, never the
  check.

## 3. Using it

### Keys

- F12: open the World Editor at its first menu. F12 again while it is open goes back to the first menu.
- In the menu: Up and Down move, Enter or Right chooses, Escape, Left or Backspace go back, a letter
  jumps to the next item starting with it. These are the keys of every other in-game list (F5, F6, F8).
- Items that change something and that you will want again (nudges, a step up or down on a setting)
  keep the menu open, so Enter can be pressed again.
- Items that need a number open the command line with the start of the command typed for you
  (`/edit move `, `/edit set Volume `). Type the number and press Enter. The menu is still open after.
- No editor action is on Control, Alt or Insert, or on the numeric keypad (screen readers use it for
  review with Num Lock off). Phase 1 has no direct keys outside the menu.

Every result is spoken, briefly: "Moved Fountain 0.5 metres north.", "Volume 0.8.", "Undid: moved
Fountain."

### Menu outline

Items marked (2) or (3) are later phases; phase 1 shows only what works.

- **Map**: name, owner, size and bounds, tiles, spawn point, how many things; set spawn here; who may
  edit (list); default weather and time (2); beacon rules (2); ground (2); versions of the map (3).
- **Place**: browse the prefab library by category, place at your feet. A solid thing is put just in
  front of you instead, so you are not inside it. Search (2), preview by ear (2), rows and repeat (2),
  place at the build cursor (2).
- **Select**: nearest things (the eight nearest, with distance and direction), things within 5, 10 or
  20 metres, by name (`/edit select NAME`), by number (`/edit select #ID`). Choosing one selects it and
  says what it is and where.
- **Selected thing** (once something is selected): what it is and where; move by numbers; nudge
  north, south, east, west, up, down, forward, back, left, right by the step; turn 15 or 90 degrees
  either way, or face a compass direction; bring to you; duplicate; delete; settings; its model.
  Resize by numbers is in settings (width, height, depth). Group into a prefab (2).
- **Library**: the kinds of model, the models of each, and each model's fields; change a field (needs
  edit-models). New from template or copy (2), versions and where used (versions listed in 1, pin in 2),
  replace everywhere (2), retire (2).
- **People** (3): characters such as Alex, walker density.
- **Roads and routes** (3).
- **Places and rooms** (2): named places, regions, what a room is made of.
- **Test tools**: what is around me (`/scan`); listen here (2).
- **Undo** and **Redo**, each saying what it would undo or redo.

## 4. Models and how a kind describes itself

### Kinds

A kind is a type of model with a name ("small_machine"), a spoken name ("machine") and a C# type. The
existing library already is this:

| Kind | Type | Where it comes from |
|---|---|---|
| small_machine | SmallMachineSpec | ModelLibrary, SmallMachines.cs |
| water, fire, foliage, flow, shore | WaterFeatureSpec, FireSpec, FoliageSpec, RunningWaterSpec, ShoreSpec | ModelLibrary, NatureModels.cs, RunningWater.cs, Waves.cs |
| horn, whistle, bell, air | ChimeHornSpec, WhistleSpec, StruckBellSpec, AirSystemSpec | ModelLibrary |
| train, rail_vehicle, track | TrainProfile, RailVehicleSpec, TrackSpec | ModelLibrary |
| vehicle, engine (2) | MachineRegistry's Describe/Assemble shapes | Machines.cs, Engines.cs |
| prefab (2 for versions) | PrefabTemplate | prefabs/*.json, prefab-schema.json |

`ModelKinds` (OpenFPS.Common/Editing) takes its list of kinds from `ModelLibrary.AllKinds`, so a kind
added to the library is in the editor with no further code.

### Fields

A field is described by a `FieldDescriptor`:

| Member | Meaning |
|---|---|
| Path | where the value is in the model: `Compressor.HumDb`, `Falls[0].HeightMetres` |
| Label | what is said: "compressor hum level" |
| Type | Number, Integer, Bool, Choice, Text |
| Unit | "dB", "Hz", "m", "s", "rpm" ... said after the value |
| Min, Max | the sensible range; a value outside it is refused with the range said |
| Step | how far one "up" or "down" moves it |
| Help | one line, said on request |
| Source | where a physical number comes from (a paper, a data sheet, a standard), empty if none |
| Choices | for a Choice: the allowed words (materials come from AcousticRegistry) |
| ReadOnly | shown, not changed |

A model type gives these by attributes on its properties:

```csharp
[Tunable("dB", 30, 90, "The magnetic hum at one metre, with the can around it.",
         Label = "hum level", Step = 1, Source = "twice the mains frequency; typical units")]
public float HumDb { get; init; } = 62f;
```

- A property that is itself a record (`Compressor`, `Casing`, `Blade`) is a group: a submenu.
- An array of records (`Falls`, `Taps`) is a list: a submenu of its items, each a group.
- A scalar property with no attribute is listed as read only, labelled from its name, with "not
  described yet". That shows what is still to be described rather than hiding it.
- Computed properties (no setter) are not listed.
- Labels without an attribute are made from the property name, and a unit at the end of the name
  becomes the unit (`ShellHz` is "shell" in "Hz", `ThicknessMm` is "thickness" in "mm").

Phase 1 describes SmallMachineSpec in full (with its governor, blade row, deck, cutting, compressor
and casing). The other kinds are listed with their fields read only until each is described; that is
work per kind of a few lines each, not editor work.

### Changing a field

The model is written to JSON, the value is set at the path, and the JSON is read back into the type.
That is the same round trip ModelLibrary already tests for every built-in model, so any kind that
round-trips can be edited without code of its own. The value is checked against the field's type and
range first.

### Settings of a placed thing

A thing on a map is an instance of a prefab. Its own settings are described the same way, by
`EntitySettings` (server): fields with a getter and a setter on the entity's components, shown only
when the entity has that component.

Phase 1 fields: name; width, height and depth (the prefab's size times the scale); a sound's volume,
range and minimum distance. Its model (a machine's `small_machine:ac_condenser`, a fountain's
`water:park_fountain`) is a link from its settings to the model's menu: the sound id names the model.

Phase 2: door sides (`KeyedSide`, `PushSide`), room materials, material, the prefab's own fields
through prefab-schema.json (which already says type and range for each).

## 5. Model identity and versions

- A model's id is `kind:id`: `small_machine:ac_condenser`, `water:park_fountain`. Prefabs will be
  `prefab:concrete_wall`.
- Version 0 is the built-in model (the C# preset, or an authored file in models/). Each change makes a
  new version: 1, 2, 3 ... Versions are never deleted.
- Storage: one file per edited model, `model_versions/<kind>.<id>.json`, in the server's folder beside
  map_access.json:

```json
{
  "Kind": "small_machine", "Id": "ac_condenser", "Current": 2,
  "Versions": [
    { "Version": 1, "Author": "cody", "SavedUtc": "2026-10-06T21:00:00Z",
      "Note": "Compressor.HumDb 63 to 66", "Spec": { ... } },
    { "Version": 2, ... }
  ]
}
```

- At start the server loads every file and puts each model's current version into ModelLibrary
  (`ModelLibrary.Add`), which is how an authored model already overrides a built-in.
- **A map references a model by id, not by version.** It uses the current version unless its overlay
  pins one: `"Pins": { "small_machine:ac_condenser": 1 }` (phase 2). Pin 0 is the built-in.
- **Where used** is counted, not stored: the sound ids of the things on each loaded map (`machine:`,
  `water:`, `fire:`, `foliage:`, `flow:`, `shore:`, `bell:`) name their models. Vehicles, trains and
  characters will add their presets in phase 2.
- **To clients**: `ModelUpdate { Kind, Id, Version, SpecJson }`, sent to everyone when a model changes
  and to each player as they log in, for every model whose current version is above 0. The client puts
  it into its own ModelLibrary and restarts the voices of that model, so the change is heard at once.
  In phase 2 a map with a pin sends the pinned version to the players on that map.

## 6. Edit operations

### The operations

| Op | What it does | Undo |
|---|---|---|
| place PREFAB | a new thing at your feet (in front of you if solid) | delete it |
| duplicate | a copy of the selected thing, one of its own widths along the way you face | delete the copy |
| delete | removes the selected thing | puts it back as it was, with its settings |
| move E N U | moves it by metres east, north and up | moves it back |
| nudge DIR [M] | moves it one step (default 0.5 m) north/south/east/west/up/down or forward/back/left/right of your facing | moves it back |
| turn DEG | turns it clockwise (negative: anticlockwise) | turns it back |
| face DIR | turns it to face north, north east ... | turns it back |
| bring | moves it to you (in front of you if solid) | moves it back |
| set FIELD VALUE | changes one of its settings (size included) | the old value |
| model set KIND ID FIELD VALUE | changes a model: a new version | the previous version becomes current again |
| spawn here | the map's spawn point to where you stand, facing your way | the old spawn |

Selecting is not an operation: it changes nothing and is per player.

### On the wire

- Client to server: the operations are text commands, `TextCommand { Command = "edit", Args = [...] }`,
  the same as typing `/edit nudge north`. The menu sends exactly what a person would type. So the MUD
  gateway, the command line and the menu are one path, checked in one place (CommandHandler, on the
  tick thread).
- Server to client, new messages (union tags 40 and 41; append only, never renumber):
  - `EditorMenu { Path, Title, Items[], Refresh }`: one menu. Each item is
    `EditorMenuItem { Label, Kind, Command, Stay }`. Kind is Info (choosing says it again), Menu
    (Command is the path of the menu it opens: the client sends `edit menu PATH`), Action (Command is
    the text of an /edit command to send), Input (Command is put on the command line for you to finish).
    Stay keeps the menu open after an action. Refresh replaces a menu with the same Path if it is the one
    open, without speaking, so a value shown in a label is current after you change it.
  - `ModelUpdate { Kind, Id, Version, SpecJson }`: a model's new current version.
- The server builds every menu, so the client has no knowledge of kinds or fields; it shows lists. A
  text player gets the same menus as numbered lines with the command to type for each.
- Results are `TextEvent`s, spoken as every other server message is.
- What the world does is carried by the existing messages: a moved thing goes out as its state and its
  definition is sent again (Program.SyncAudioComponent), a deleted one as `EntityRemoved`, a new one as
  its definition (MapManager.IndexEntity). The server's static grid and triangle world are rebuilt for
  the tiles that changed (MapManager.RefreshGrid, ServerGeometry through MapManager).

### Ids

- A thing on a map is known to the editor by its **authored id**, the `EntityId` in the map file. Every
  generated map gives every entity one. A hand-written map's entity without one is given the next free
  number, in file order, when the map loads (in memory only).
- Things the editor places get ids from 900,000,000 up (the overlay keeps the next one), so they never
  meet a generator's numbers.
- The runtime ECS id is never stored: it changes every load.

### Undo and redo

- One stack per editor per map, in memory on the server, up to 200 operations. Redo is cleared by a new
  operation. The stacks do not survive a server restart (decision 4).
- An undo applies only if the thing is as the operation left it. If somebody has changed it since, the
  undo is refused and says who: "Cannot undo: sean has moved Fountain since." If it has been deleted:
  "Cannot undo: Fountain has been deleted." A model undo applies only if its current version is still
  the one that operation made.

### Other players

- Edits are applied in arrival order on the tick thread, so two editors never see different worlds.
- A solid thing is never placed, moved, turned, duplicated or resized into a player's body, yours or
  anyone's: "Not moved: that would put Concrete Wall through sean."
- Things being carried, people, vehicles and walkers are not selectable in phase 1.
- Other editors on the map are told in a few words ("cody moved Fountain.") so a change behind them is
  not a mystery. Players who cannot edit are not told; the world simply changes.
- If what you had selected is deleted by somebody else, the next operation says so and clears it.

## 7. Keeping the edits: the overlay

- One file per map: `maps/overlays/<mapId>.json`. The map loader reads maps from maps/, maps/places/
  and maps/players/ only, so this folder is never mistaken for a map.
- It records state, not history: what each changed thing is now.

```json
{
  "MapId": "city",
  "Format": 1,
  "NextId": 900000002,
  "Spawn": { "Position": { "X": 60, "Y": 0.15, "Z": 122 }, "Rotation": { "X": 0, "Y": 0, "Z": 0, "W": 1 } },
  "Changed": [
    { "Id": 1002, "Prefab": "asphalt_road", "Was": { "X": -130, "Y": 0.025, "Z": 66 },
      "Position": { ... }, "Rotation": { ... }, "Scale": { ... }, "Name": null,
      "Settings": { "Volume": "0.8" } }
  ],
  "Removed": [ { "Id": 1003, "Prefab": "concrete_floor", "Was": { ... } } ],
  "Added": [ { "Entity": { "EntityId": 900000001, "PrefabId": "ac_condenser", "Position": { ... } },
               "Settings": { } } ],
  "Pins": { }
}
```

- When a map loads: its file is read, the overlay is laid over the data (spawn, removals, changes,
  additions), then the map is built from the result as if it had been written that way. Settings are
  applied to the entities just after they are made.
- **A generator may renumber.** Each changed or removed entry carries the base entity's prefab and its
  position in the map file (`Was`). If the id still names that prefab at that place, it applies. If not,
  the entity with that prefab at that place is looked for and used. If there is none the entry is skipped
  and the log says so by name and place. So regenerating city.json after a street is added does not
  move the wrong wall.
- Every operation and every undo writes the file at once (a temporary file, then a rename).
- Laying the overlay twice changes nothing (positions and settings are absolute, an addition whose id is
  already there is skipped), so a map that has been /savemap'd with its edits in it loads the same.
- /savemap still writes the map file as it was in memory. On a generated map that is the existing
  hazard of writing a generated file, unchanged by the editor; the editor itself never calls it.

## 8. Accessibility

- Built on the client's MenuStack, the same lists as F5, F6 and F8: the same keys, sounds and speech.
- Each list says its title, its length and its first item when it opens.
- Labels carry the value: "Volume, 0.8", "Hum level, 63 dB, 30 to 90". The help is one item down.
- Positions are said as distance and direction from you ("3 metres right in front"), or in player
  coordinates (x east, y north, z height), never in engine axes.
- Directions for nudges are compass words and your own forward/back/left/right; turning is clockwise
  as heard from above, the way a compass reads.
- Results are short and say the thing's name.
- No key a screen reader owns. F12 is free in Orca and NVDA.

## 9. Phases

### Phase 1 (this branch)

- docs/WORLD_EDITOR.md.
- F12 and the World Editor menu, role gated; `/edit` for the command line and the MUD.
- Map info; set spawn here; map editors (`/map editor add|remove NAME`, `/map editor`).
- Select nearest, within a radius, by name, by number, with spoken summaries.
- Selected thing: move by numbers, nudges, turn, face, bring, duplicate, delete, settings, its model.
- Settings generated from self-description: placed things (name, size, sound level and range) and
  one physical kind end to end: small machines (the condenser and window AC units, the mowers), whose
  changes are versioned, sent to every client and heard at once.
- Place: the prefab library by category, at your feet.
- Undo and redo per editor; conflicts refused; edits broadcast; the overlay per map.
- Tests: permissions, every op and its undo, overlay reload, generated maps byte-identical, ranges,
  wire round trips, menu navigation and speech, a model change re-voicing a machine.

### Phase 2

- Pins and per-map model versions sent to the players on that map; versions menu; where used per map.
- Describe the remaining kinds (water, fire, foliage, flow, shore, horns, bells, trains), vehicles and
  engines through MachineRegistry, prefabs through prefab-schema.json.
- Library: new from template, copy, replace everywhere, retire.
- Place: search, preview by ear (a short render of the model at your position, played to you only),
  rows and repeat, at the build cursor (BuildSession).
- Group selected things into a prefab or composite; edit a composite's parts.
- Door sides, room materials, named places and regions; streaming membership updated when a thing moves
  to another tile.
- Map settings: default weather and time, beacon rules, ground.
- Direct keys while the editor is open, if wanted (to be tried with Orca and NVDA first).

### Phase 3

- People: characters, walker density. Roads and routes (roads as data). Map versions and restoring one.
- Baking an overlay into a hand-written map file on request.

## 10. Decisions for Cody

1. Overlay for every map, including player maps and the speedway, rather than writing player maps
   directly. One rule, and a map file is never rewritten by the editor. Baking is phase 3.
2. maps/overlays/ and model_versions/ are not in .gitignore: edits made on your own server can be
   committed like any other map change. Say if they should be ignored instead.
3. Invited editors may use the editor on that map and nothing else (not /spawn or /savemap). Changing
   models needs edit-models (developers and admins), because a model is shared by every map.
4. Undo history is lost on a restart; the edits themselves are kept.
5. A solid thing placed "at your feet" is put just in front of you, so you are not inside it.
