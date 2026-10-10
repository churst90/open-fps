# World editor

The design agreed with Cody on 2026-10-06, and how it is built. Phase 1 is the foundation: the menu,
selecting things, moving and turning them, settings generated from what each kind of thing says about
itself, undo and redo, and edits that are kept.

## 1. What it is

- F12 opens the world editor dialog, in the game, while you play (section 16; until 2026-10-09 it
  opened the menus of section 3, which `/edit` typed on its own still does). There is no build mode:
  you walk, you collide, you can be hurt. Close the dialog (F12 or Escape) to move.
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
| Moderator, other players | nowhere; F12 does nothing (no sound, no words); `/edit` typed says "The world editor is for this map's owner, the people they ask to edit it, and developers." |

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

The dialog F12 opens is described in section 16. This section is the menu that `/edit` typed on its own
opens, which the MUD gateway also shows as numbered lines; it holds everything the dialog leaves out.

### Keys

- `/edit`: open the World Editor at its first menu.
- In the menu: Up and Down move, Enter or Right chooses, Escape, Left or Backspace go back, a letter
  jumps to the next item starting with it. These are the keys of every other in-game list (F5, F6, F8).
- Items that change something and that you will want again (nudges, a step up or down on a setting)
  keep the menu open, so Enter can be pressed again.
- Items that need a number or a name open a dialog with one labelled text box (section 14). Enter
  applies, Escape cancels. The menu is still open after.
- No editor action is on Control, Alt or Insert, or on the numeric keypad (screen readers use it for
  review with Num Lock off). Phase 2 adds direct keys on Shift while an editor list is open, off unless
  a player turns them on (section 11.8).

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
- **Test tools**: what is around me (`/scan`), map information; listen here (2).
- Delete asks first ("Delete Fountain?": Yes, or Escape). Yes closes the editor, since what it was
  showing has gone; undo puts it back.
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
| vehicle, engine | VehicleSpec (over MachineRegistry), EngineProfile | ModelLibrary, VehicleSpec.cs, Engines.cs |
| prefab | PrefabTemplate, described from prefab-schema.json | prefabs/*.json (PrefabKind, the server's) |
| group | GroupSpec | made in the editor (GroupKind, the server's) |

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
    the text of an /edit command to send), Input (a dialog asks for a value and sends Command with it
    on the end; section 14).
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
      "WasRotation": { ... }, "WasScale": { ... },
      "Position": { ... }, "Rotation": { ... }, "Scale": { ... },
      "Settings": { "Volume": "0.8", "Name": "Wharf Avenue" } }
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
- A change entry that is undone back to where, how turned and how big the map file has the thing, with
  no settings, is dropped, so the file holds only real differences.
- Laying the overlay twice changes nothing (positions and settings are absolute, an addition whose id is
  already there is skipped), so a map that has been /savemap'd with its edits in it loads the same.
- /savemap still writes the map file as it was in memory. On a generated map that is the existing
  hazard of writing a generated file, unchanged by the editor; the editor itself never calls it.
  Walkers, trains, vehicles and aircraft spawned on a shipped map are not recorded in its data
  (2026-10-07), so /savemap does not write them there; they last until a restart.

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

### Phase 2 (built 2026-10-07: sections 11 and 12)

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

### Phase 3 (built 2026-10-10: section 18)

- People: characters, walker density. Roads and routes (roads as data). Map versions and restoring one.
- Baking an overlay into a hand-written map file on request.
- Also: a list of the things from the map file that were changed or removed, each put back as the map
  has it (the leftover of section 17).

## 10. Decisions for Cody

1. Overlay for every map, including player maps and the speedway, rather than writing player maps
   directly. One rule, and a map file is never rewritten by the editor. Baking is phase 3.
2. maps/overlays/ and model_versions/ are in .gitignore (decided 2026-10-07): they are the running
   server's data, and a deploy from the repository must not overwrite edits made on the live server.
3. Invited editors may use the editor on that map and nothing else (not /spawn or /savemap). Changing
   models needs edit-models (developers and admins), because a model is shared by every map.
4. Undo history is lost on a restart; the edits themselves are kept.
5. A solid thing placed "at your feet" is put just in front of you, so you are not inside it.
6. Owners and named editors may place at most 5,000 things on a map with the editor; staff are not
   limited. Things to carry need the give permission to place, and premium items cannot be placed.

Decided (Cody, 2026-10-07): "go with your recommendations on the editor". All of the above stand, with 2 as
amended: the editor's data is ignored by git.

## 11. Phase 2: the plan

Written before the work (2026-10-07), in the order it is done. What was built is section 12.

### 11.1 Every kind describes itself, and vehicles, engines and prefabs are kinds

- `[Tunable]` on the physical parameters of water, fire, foliage, flow, shore, horn, whistle, bell, air,
  train, rail vehicle and track, with units, physical ranges that hold every built-in value, one line of
  help and the source where the code already names one. Render trims, headroom, mixing shares and seeds
  are left without it: shown, read only. The realism rule decides, not convenience.
- **Engines** become a library kind, `engine`, whose models are EngineProfile.Presets. An edited engine
  is used by every vehicle that has it: MachineRegistry takes the engine through the library
  (`MachineRegistry.EngineFor`), and forgets what it assembled when the library changes.
- **Vehicles** become a library kind, `vehicle`, edited as a `VehicleSpec`: the engine (by name), the
  chassis (mass, drag area, rolling resistance, axles, size, level at one metre, tyre count), where the
  exhaust and intake are, and the tyres, body and gearbox records as they are. Everything else (siren, air
  system, fan, starter) comes from the base vehicle, as MachineRegistry's own parts lists do. Editing one
  changes what MachineRegistry.VehicleFor gives on the server (physics) and on every client (sound).
- **Prefabs** become a kind, `prefab`, described from prefab-schema.json: its types, enums, minimum and
  maximum, and its descriptions as help. Units come from the field's name and description. The prefab's
  JSON is checked by PrefabValidator before it is a version. A prefab is the server's alone: its versions
  are not sent to clients; instead every thing made from it on a loaded map is made again from the new
  version, where it stands, with its own settings.
- Choices can name the library: `Choices = "models:horn"` lists the horns, so a train's horn is chosen,
  not typed.
- Where a kind's models are heard: `engine:<vehicle>` sounds name a vehicle, and an engine is used by
  the vehicles built on it.

### 11.2 Pins and per-map versions

- A map's overlay pins a model to a version: `"Pins": { "small_machine:ac_condenser": 1 }`. Pinning
  is a map setting, so it needs `edit` on that map (an owner may pin on their own map), not edit-models.
- What a player is sent depends on the map they are on: on arrival (the manifest), every changed
  model at the version that map uses; when a model changes, only players on maps that do not pin it;
  when a pin is set or lifted, the players on that map get the version they now use. The client's
  library is one, and it always holds the versions of the map it is on.
- The server's own library holds the current versions. Where the server simulates with a model (a
  vehicle's mass and gearbox in DrivingSystem, a train), it uses the current version even on a map that
  pins an older one: pins are for what is heard. Said in the versions menu.
- Versions menu: each version with its note, author and date, and for each: "use on every map"
  (edit-models; undoable), "pin on this map", and "lift the pin". Where used: by map, with counts.

### 11.3 Library

- New from template (`/edit model new KIND TEMPLATE NEWID`, the new id last so the menu can type the
  rest of the command for you): a built-in model as built (version 0) under a new id. Copy (`/edit model copy KIND ID NEWID`): any model at the version in use. Both need
  edit-models, make version 1 of the new model, and undo by retiring it.
- Replace everywhere (`/edit model replace KIND ID WITH OTHER [here|everywhere]`): every thing playing
  one model plays another; on this map needs `edit` here, everywhere needs edit-models. One undo puts all
  of them back. The model a thing plays is a setting of its own (Model), kept in the overlay.
- Retire (`/edit model retire KIND ID`, `/edit model restore KIND ID`): a retired model is not offered
  for new things or as a template, and says so in the library; things already using it keep it.

### 11.4 Place

- Search (`/edit find WORDS`): prefabs whose name or id has every word, as a menu to place from.
- Preview by ear (`/edit preview PREFAB`): the prefab's sound made at your feet for six seconds, sent to
  you alone as a definition with an id of its own and taken away after; nothing on the map changes. No
  new message.
- Rows (`/edit row COUNT [SPACING]`): copies of the selected thing in a line the way you face, spacing
  its own width unless given; one undo takes the row away. Repeat (`/edit again`): the last place,
  again, where you stand now.
- At the build cursor (`/edit place PREFAB at cursor`): where /build's cursor is, facing the build
  heading, for things that cannot be walked to (a roof).

### 11.5 Groups

- A selection of more than one thing: `/edit select add` (the nearest, or NAME, or #ID) and
  `/edit select clear`; the selected-thing menu says how many are held.
- Group (`/edit group NAME`): the held things become a group, kept with the models as kind `group`
  with versions, and the things stay where they are. A group is placed from Place, Groups, as its
  things. (Built this way rather than as CompositeService composites: see section 12.)
- Edit a group's parts: its parts listed with prefab and offset; change a part's place or turn, take a
  part out. A new version of the group.

### 11.6 Doors, rooms, named places and regions

- Settings of a placed thing, when it has the part: door keyed side and push side; a room's six
  materials (floor, ceiling, north, south, east, west) chosen from AcousticRegistry; a region's name
  (named place). A changed room or door goes out to clients as its definition, as a move does.
- Streaming: a map-file thing moved into another tile has its tile membership worked out again
  (MapTiles), so a client holding the new tile is sent it.

### 11.7 Map settings

- Weather and time: a map may hold the sky (a fixed weather, a fixed hour) or follow the server's; kept
  in the overlay, applied in WorldEnvironmentSystem.GetStateForMap.
- Beacon rules: each beacon category's policy (default on, default off, forced on, forbidden); sent to
  the map's players at once.
- Ground: the natural ground laid where the map has none (dirt, grass, concrete, asphalt, gravel).

### 11.8 Direct keys

Designed, and OFF by default (a client setting): while the editor menu is open, keys that act on the
selected thing without going through the list. They must be tried with Orca and NVDA before they are
turned on for anybody. Never Control, Alt, Insert or the numeric keypad.

## 12. Phase 2 as built

Built 2026-10-07 on branch worktree-agent-a325b79569e36d170, in the order of section 11. Everything in
11.1 to 11.8 is in; where it was built differently from the plan it says so here. Unheard: nothing
here has been tried in the game client yet, only by tests and through the MUD gateway.

### Kinds

- Described, attributes only on the model records: water (WaterFeatureSpec, WaterFallSpec,
  WaterTapSpec), fire (FireSpec, now in Fire.cs), foliage, flow (RunningWaterSpec and its inlet, falls,
  basin, taps, cavity, obstacles), shore (ShoreSpec, HullSpec), horn, whistle, bell (with their bells),
  air (AirSystemSpec, its ports), train (TrainProfile, its consist), rail vehicle (with wheelsets,
  traction, electric drive, steam), track, engine (EngineProfile with cams, valves, exhaust, mufflers,
  intake, mechanical), and the vehicle parts (TyreProfile, VehicleBody, Gearbox). About 460 fields.
- Left read only on purpose, as not physical: every PeakHeadroomDb (render headroom), the number of
  places a source is heard from, AirSystemSpec.JetTrimDb, a port's Name (code finds ports by it), an
  engine's IdleRoughness, Steepening, FlowLoss and its level scales (JetNoiseLevel, PortNoiseLevel,
  intake Level and FlowNoiseLevel, the mechanical level scales), MufflerSpec.ShellLevel,
  VehicleBody.Coupling and MaxModes, a gearbox's derived shift speeds, an engine's
  RevolutionsBeforeFiring (NaN, worked out from the fuel), ElectricDriveSpec.SyncFromHz (1,000,000 is
  its "no inverter").
- Judgement calls left editable, each with help that says what it is: a chime bell's LevelTrimDb (the
  bell's level against the others), PitchBend, Breathiness, a track's StructureDb, a rail vehicle's
  BodyDrumDb, ExhaustSpec.WallLossMultiplier, OverrunPopRate, IdleGovernorGain, VehicleBody.CabinLeak,
  and the conditions a level was measured at (ReferenceRainMmPerHour, ShoreSpec.ReferenceWind).
- A test walks every built-in model of every kind and fails if a value lies outside its declared range
  (WorldEditorTests). It passes for all of them.
- `Choices = "models:horn"` lists a kind's models: a rail vehicle's horn, bell, whistle and engine are
  chosen, not typed.
- Engines are kind `engine`; EngineProfile.ByName takes an edited engine, so a vehicle, a small
  machine, a train and an aircraft engine all have the change. Vehicles are kind `vehicle`
  (VehicleSpec, OpenFPS.Common/VehicleSpec.cs); MachineRegistry.VehicleFor builds an edited one on its
  base and forgets what it assembled whenever the library changes. A client restarts the voices of the
  vehicles and machines on an engine that changed.
- Prefabs are kind `prefab` (ModelCatalog.cs, PrefabKind): fields from prefab-schema.json (vectors are
  groups of X, Y and Z; its lists are editable since section 13), checked by PrefabValidator. Id,
  Type, IsItem, Premium, WeaponId and Hands are shown, not changed. A version is the server's alone and
  makes every thing made from the prefab again where it stands (Remake), keeping its name, settings, a
  doorway's rooms, a door's sides and a room's materials, and joining doorways to a room made again.
- The editor knows kinds through ModelCatalog (EditorKind: fields, ids, JSON, check, apply). ModelStore
  keeps every kind's versions, puts them in use through the kind, and keeps made and retired models.

### Pins and versions

- `/edit model pin|unpin|use|versions|where`. A map's pins are in its overlay. A player arriving is sent
  every changed model at the version the map uses (Program.SendManifest, ModelStore.UpdatesFor); a new
  version goes only to maps that do not pin the model; a pin set or lifted is sent to that map's players.
- The server's own library holds the current versions, so what it simulates (a vehicle's mass) is the
  current version everywhere. The versions menu says so. Prefabs and groups cannot be pinned (the
  server's alone).

### Library

- `/edit model new KIND TEMPLATE NEWID`, `copy KIND ID NEWID`, `retire`, `restore`, `replace KIND ID
  with OTHER [here|everywhere]`, and `remove KIND ID LIST[N]` (an item out of a list: a fountain's fall,
  a group's part). A made model is named from its id. Undoing a making retires the model: versions are
  never deleted.
- A thing's model is a setting of its own (Model), kept in the overlay. Changing it makes the thing
  again, so every client hears the new model at once (a definition sent again does not restart a
  voice). Replace is that setting on every thing, one undo for all; for prefabs it is a delete and a
  place for each thing.

### Place

- Search (`/edit find`), preview (`/edit preview`: the prefab made in a world of its own, its definition
  sent to you alone under an id from 1,950,000,000, removed after six seconds; no new message), rows
  (`/edit row COUNT [SPACING]`, one undo), again (`/edit again`), and the build cursor
  (`/edit place PREFAB at cursor`, after /origin and /at).
- The Place menu has a mode: what choosing a prefab does (place at your feet, at the build cursor, or
  preview). Each item still sends a plain command, so the MUD gets the same.

### Groups (built differently from the plan)

- Holding: `/edit select add nearest|NAME|#ID`, `/edit select clear`, the Held menu.
- `/edit group NAME` keeps the held things as a group, kind `group`: each part's prefab, name, place
  (right, forward and up of the group's origin, in the frame the builder faced), turn, scale and settings.
  `/edit place group NAME` puts the parts down in front of you, turned the way you face, as editor
  additions, one undo for all.
- Not CompositeService composites: those are one root carrying its members (ParentComponent), kept in
  the map file's Composites, with no authored ids for the editor to select or the overlay to keep. A
  group placed as separate things uses everything the editor already has (overlay additions, undo,
  every setting). The cost: a placed group is not one thing to move, and a new version of a group
  changes what is placed next, not what was placed. See decision 2 below.

### Doors, rooms, places, tiles

- Settings: KeyedSide (neither, front, back), PushSide (front, back), Indoor, and Floor, Ceiling, North,
  South, East and West for a room, from AcousticRegistry's names; a region's Name is the place's name.
  The menus "Places and rooms" and "Doors near you" select them.
- A thing from the map file on a map streamed in tiles is placed in its tiles again when it moves or is
  made again (MapTiles.Place), and sent to every player on the map who holds a tile it is now in. Things
  the editor adds are not tiled (as in phase 1): they are sent by where they stand.

### Map settings

- `/edit map settings`, `/edit map set weather|time|ground|beacon ...`, the Settings and Beacon rules
  menus. Kept in the overlay's Settings and laid on the map's data at load (MapData.HeldWeather,
  HeldHour, GroundPrefab, BeaconPolicy).
- A held weather or hour is applied in WorldEnvironmentSystem.GetStateForMap (MapAtmosphere.Of), so the
  map's players and the server's own systems on that map read it at once; the server's sky goes on
  everywhere else. A held weather is the front's settled values (SetScenario's targets).
- Natural ground: the loader lays MapData.GroundPrefab where a map has no ground of its own; changing it
  lays the new ground at once. A map that lays its own ground says so.
- Beacon rules go to the map's players at once in MapSettingsUpdate (union tag 42, new).
- Size (2026-10-09): `/setmapsize EAST NORTH HEIGHT [force]`, and "Change the size, typed" in the Map
  menu for the owner or `maps-any`. Kept as the overlay setting `Size` ("200 300 40", the order players
  type it) and laid on MaxBound at load; MinBound never moves, so no coordinates change and the
  client's acoustic grid keeps its corner. Live, it updates the ZoneComponent, lays the natural ground
  again under the new bounds (or takes it up where the map's own ground now covers them), recomputes
  the earshot, and sends MapSettingsUpdate with the play area (HasPlayArea, PlayMin, PlayMax,
  appended) so the client's prediction has the server's edges. Refused on shipped (generated) maps.
  Undo is a MapSetOp whose before is always a size, never "unset".

### Direct keys

- `/editorkeys on|off` (client, saved in the client's settings, off by default). While an editor list
  is open: Shift+arrows nudge forward, back, left, right; Shift+] and Shift+[ nudge up and down;
  Shift+period and Shift+comma turn 15 degrees; Shift+slash says where the selected thing is; Shift+D
  duplicates; Shift+Delete asks to delete; Shift+Z undoes; Shift+Y redoes. Shift only; never Control,
  Alt, Insert or the keypad.
- **They must be tried with Orca and NVDA before anybody is told to turn them on.** NVDA uses
  Shift+arrows for selection in browse mode and Orca has Shift+keypad commands; neither should reach
  the game window in focus mode, but that is to be heard, not assumed.

### Wire changes

- New message MapSettingsUpdate { MapId, BeaconPolicy[] }, union tag 42. Nothing else on the wire
  changed: ModelUpdate now also carries kinds "engine" and "vehicle", and previews are ordinary
  EntityDefinition and EntityRemoved messages. Client and server must be rebuilt together.

### Tests

- WorldEditorPhase2Tests (22): kinds described, vehicles and engines through MachineRegistry, prefab
  versions remaking things, pins and the versions sent, use, new, copy, retire, replace, a thing's
  model, removing a list item, search, preview, rows, again, the build cursor, groups, door sides and
  room materials, tiles, map settings and their reload, the ground, the new message.
- WorldEditorClientTests: direct keys off by default and Shift only; vehicle and shore sounds name
  their models. ModelLibraryTests: engines and vehicles survive the round trip.

### What is left

Five leftovers from this list were built the same day: section 13. Still left:

- Groups as one thing (CompositeService composites with a root), and a new group version changing the
  copies already placed. Not to be built unless building houses shows the need (decision 2 below); a
  placed group can be held and moved as one instead (section 13).
- Previews are for things with a sound of their own; there is no preview of a wall being struck.
- Phase 3 as in section 9.

### Decisions for Cody

1. **Pins are for what is heard.** The server simulates with the current version on every map (a
   pinned school bus sounds as pinned but weighs what the current version says). Recommendation: keep;
   a per-map server library is a lot of machinery for a difference nobody hears.
2. **Groups place as separate things, not as one composite.** Recommendation: keep for now; build
   composite groups (one root, moved as one) only if building houses shows the need.
3. **Direct keys stay off until tried.** Recommendation: try `/editorkeys on` with Orca first, then
   NVDA on the Windows client; if either takes a key, move that key, and only then mention them in
   the manual as ready.

Phase 2 decided (Cody, 2026-10-07): "go with your recommendations on the editor phase". Pins change what players
hear; the server's simulation uses the current version on every map. Groups place as separate things until
building houses shows one-piece composites are needed. Direct keys stay off until Cody has tried them with Orca
and then NVDA, and any key a screen reader takes is moved first.

## 13. Phase 2 leftovers as built

Built 2026-10-07, after section 12, each with a test in `OpenFPS.Tests/WorldEditorLeftoversTests.cs`
that failed before it. Unheard: tried by tests only.

### A thing made again keeps what it is doing

- `WorldEditor.Remake` (a prefab's new version, a thing's new model) carries the running state of the
  thing across (`LiveState`, OpenFPS.Server/Editor/LiveState.cs). What the model says comes from the new
  version; what the thing is doing comes from the old one.
- A door: its openness, where it is swinging to, its closer's and motor's clock, which hand has it, the
  key being turned and whether a key was used. Its locked side and push side were already settings. An
  open door is made shut at its doorway (its shut pose, not where the leaf has swung to) and swung open
  again at once (`DoorSystem.Settle`), so a new version does not move the doorway or slam the door.
- A fire: the moment it was lit (`fire:PRESET/lit=SECONDS`), and so its stage of growth, kept across a
  new model or a new prefab version.
- An emitter's running state: on or off (`SynthRunning`: a machine, a tap, a crossing), standing at a
  stop, the windows. Health, kept within the new maximum.
- Found on the way and fixed: moving or turning a door with the editor moved its leaf but not its
  doorway (the shut pose DoorSystem took when it first saw it), so the leaf went back to the old place
  the next time it swung, and a new version would have made it there. `ApplyPose` now moves and turns
  the shut pose and the doorway's opening with the leaf, and what the overlay keeps of a door, and what
  a copy, a deletion or a group is made from, is its doorway, not where its leaf hangs open
  (`RestOf`).

### Parts-list vehicles are library vehicles

- `ModelLibrary` lists every vehicle in `machines/` (MachineRegistry.Authored) as a vehicle model, with
  the built-in presets: `Ids`, `Knows`, `BuiltInModel` and `Get` ask MachineRegistry each time, since its
  folder is loaded after the library is made. Version 0 is `VehicleSpec.Of(id)`: the parts list
  assembled on its base.
- Edited through the same `VehicleSpec` path as a built-in: an edited one is built on its parts list
  (`MachineRegistry.Unedited`), so what the editor does not show (its engine's place, its siren) stays
  what the file says. New, copy, versions, use, pin and where all work, and clients restart its voices.

### A held weather holds its lightning

- `LightningSystem` keeps the server's storm for every map that follows the server's sky, and a storm of
  its own for each map that holds a weather, advanced from that map's held sky (`HeldSky`). A map that
  stops holding a weather loses its storm.
- `GameServer.EmitStrike(strike, mapId)`: a flash of the server's storm goes to every map that does not
  hold a weather; a flash of a held storm goes to that map only. A map holding a storm flashes while the
  server is clear; a map holding a clear sky has none while the server storms.

### A prefab's lists

- `PrefabSchema` describes an array as a list: RoomMaterials (materials, from AcousticRegistry) and
  MissingFaces (the face names) are lists of values (`FieldNode.IsValueList`, with the item's
  description as its `Field`). A list of objects would be a list of groups.
- `/edit model set KIND ID LIST[N] VALUE` changes one item; `/edit model add KIND ID LIST VALUE...`
  puts one or more values on the end, making the list if the model has none (a room's six materials go
  in together); `/edit model remove KIND ID LIST[N]` takes one out, and taking the last one out of a list
  of values leaves the list out. Every change is checked whole by PrefabValidator (PrefabRepository
  .FromJson), so five room materials are refused with its reason.
- `/edit model add KIND ID LIST` on a list of records (a fountain's falls, a group's parts) adds a copy
  of the last item, to change after. Any kind, not only prefabs.
- Menus: a list of values shows each item with its value and opens it as a field (choose a value, or
  take it out); a short list of choices (the faces) offers "Add North" and so on, a long one (materials)
  a typed add. The list's help is said in it (the room's face order).

### A placed group, held and moved as one

- Each thing placed by `/edit place group` keeps which placing it came from (`OverlayAddition.Placement`,
  "yard@900000004": the group and the number of its first part), in the overlay, through undo and redo.
  Copies and rows are not part of it.
- `/edit select group`: with one part selected, every part of that placing still on the map is held. The
  Selected menu offers it as "Hold its whole group".
- `/edit held move EAST NORTH UP`, `/edit held nudge DIRECTION [METRES]`, `/edit held turn DEGREES`:
  everything held moves or turns together (turning is about their middle, across the ground), all or
  none: if one would go through a player or out of reach, nothing moves. One undo for all. Works on any
  held things, not only a group's. The Held menu has them, with nudge and turn submenus. Each costs 5
  against the editor's limit (as placing a group does).
- Still separate things (Cody's decision): no composite, no root; a new version of the group changes the
  next placing only.

### Wire

No change. No message, member or union tag was added; the overlay file gained `Placement` on an
addition (a file, not the wire, and absent on old files). `ModelUpdate` now also carries parts-list
vehicles, which old clients read as any vehicle model. The server and client should still be rebuilt
together for the vehicle library to agree (both read machines/).

### For Cody

1. **Map-file `Form` (a ramp's shape on a map entry) is not carried by Remake or by duplicate.** Found
   while doing item 1: `MapOverlayStore.Clone` and `Remake` copy every other map-entry field but not
   `Form`. Recommendation: carry it; no map in the repository uses `Form` on an entry yet.

## 14. Typed values in a dialog

Built 2026-10-09 (Cody's list of 2026-10-08, item 7). Untried with Orca and NVDA: tests only.

### What it does

- An Input item ("Type a value", "By degrees, typed", "Move by numbers: east, north, up" and the rest)
  opens a dialog instead of the command line. The items are where they were, with the same labels.
- The dialog has one text box. Its label is what the value is, with its unit ("Compressor hum level,
  in dB"); it holds the value now, all selected, so typing replaces it; its description says the value
  now, the range and the field's help ("Now 63 dB. From 30 to 90 dB. The magnetic hum ...").
- Enter (or Apply) checks what was typed. A good value closes the dialog and sends the command, as if
  typed (`/edit model set small_machine ac_condenser Compressor.HumDb 66`). A refused one is said and
  shown under the box; the dialog stays open with the text kept and the focus in the box. The value it
  opened with is not sent again ("Unchanged."), since that would make an undo step and a new version
  of a model.
- Escape (or Cancel) closes it and says "Cancelled."
- Checks: a number against its range, a whole number where the field is one, the right count of
  numbers for a move (three, apart by spaces or commas), a decimal comma read as a point. Words are only
  checked for being there; the server checks the rest, as before, and says why if it refuses.

### Screen readers

- GTK (Orca): a real GtkEntry. The label is its mnemonic widget, which makes it the entry's accessible
  name (labelled-by); the tooltip is its accessible description. The game also says one line as it
  opens (label, value, range, keys), as the command console does.
- Windows (NVDA): a TextBox with AccessibleName and AccessibleDescription set. The game says nothing on
  opening while NVDA runs (NVDA reads the dialog and the field); without NVDA it says the same line as
  on Linux. A refusal is always said, through NVDA when it runs.

### Code and wire

- `EditorValuePrompt` (OpenFPS.Client.Core): the label, description, the line said, and the check
  (`FieldDescriptor.TryParse`, the server's own) that turns the text into the command. Both heads use
  it through `IClientShell.AskForValue`; a head without the dialog falls back to the command line.
- `EditorMenuItem` gained, appended after `Stay`: Prompt, Value, ValueType, Unit, Min, Max, Help,
  Count. OpenFPS.Common changed, so the build hash changed: a new Windows zip and a server update go
  together. The MUD gateway shows Input items as before.
- Server: `Typed`, `TypedNumber` and `TypedField` (WorldEditor.Menus.cs) fill them in. A float's stored
  value is put in the box as typed (0.800000011920929 is 0.8).


## 15. Building quickly: Control+B

Built 2026-10-09 (Cody: "I press ctrl b ... a window with a first dropdown of type of thing ... set the
size of the tile/entity ... tab through, type in my values and click place"). Untried with Orca and
NVDA: tests only.

### The dialog

- Control+B opens it, in both clients, on a map you may edit (the same rule as F12). On any other map
  Control+B does nothing at all: the client asks the server for the dialog's form (`/edit build form`),
  and the server does not answer a player who may not edit. Control on its own still does nothing.
- The controls, top to bottom, in Tab order; focus starts on What:
  1. What: Floor, Wall, Roof, Door, Window, Prefab.
  2. For a prefab, Category and Prefab (the Place menu's categories).
  3. The size, in metres, each a labelled text box with the range and a line of help as its
     description. Floor: width (left to right as you face it), length (away from you), thickness.
     Roof: height above the floor, width, length, thickness. Wall: length, height, thickness. Door:
     width and height (0.9 by 2.1). Window: width, height, height above the floor (the sill). Prefab:
     width, height and depth, in use only for a plain box (a wall, a floor); a machine is its own size.
  4. Material, for a floor, wall or roof: the materials a plain floor, wall or roof prefab of the
     library is made of and that the acoustic registry knows, each said with its prefab ("Brick, Brick
     Wall", "Wood, Siding Wall"). Choosing one puts its own thickness in the thickness box. For a door,
     Door type: the hand-opened kinds (knob, push bar, glass push bar, glass pull, patio slide).
  5. Where: In front of you, At your feet, At the build cursor (/origin, /at). Distance in front of you
     (in use only for In front of you). Facing: the way you face (squared to north, east, south or
     west), or north, east, south, west.
  6. For a door or window, Fit into the wall in front of you (on by default). While it is on, Where,
     Distance and Facing are not in use.
  7. Place and Cancel.
- Unused controls are dimmed and skipped by Tab.
- Enter places from anywhere in the dialog except the Cancel button; with a drop-down list open, Enter
  chooses in the list. Up and Down change a closed drop-down's choice (on GTK the game says the new
  choice; on Windows NVDA does). Space opens a drop-down's list.
- Placing keeps the dialog open with its values, and the focus goes back to What, so another can be
  placed at once (Cody, 2026-10-09). The game says what was placed: "Placed: floor, 6 by 8 metres,
  concrete, at your feet." A refusal (no wall in reach, a value out of range, somebody in the way) is
  said and shown in the dialog, which stays open.
- Escape, Cancel or Control+B again close it.
- The values of each kind, and the last kind, are kept for the session: the next Control+B opens on
  the kind last placed, as it was.
- While the dialog is open you do not move, so "in front of you" is from where you stood.

### Where a piece goes

- In front of you: its near edge the distance ahead of you, centred on you left to right, squared to
  north, east, south or west.
- At your feet: a floor centred under you, a roof centred over you; anything solid just clear of you.
- At the build cursor: centred on the cursor.
- A floor's top is at your feet (or the cursor); a roof's underside is "above" over that; a wall, door
  or prefab stands on it; a window's bottom is "above" over it.

### Fitting a door or window

1. The wall: the nearest solid box straight ahead of you within 3 metres, at the height of the
   opening's middle. It must be a plain box (a wall prefab, not a door, a machine or a room), upright,
   and at most 1 metre thick. Otherwise the reason is said and nothing changes.
2. The opening: as wide as the door or window, centred where your line of sight meets the wall, moved
   along the wall if it would pass an end. A door's bottom is your floor; a window's is the sill. If
   the wall is not tall enough the refusal says how tall it is.
3. The wall is taken away and made again as up to four pieces of the same prefab, with the same name,
   turn and thickness: left and right of the opening (full height), over it, and under it (a window).
   A piece thinner than 1 cm is left out.
4. The door or window goes in the opening, centred in the wall's thickness, running along it. A door
   leaf is 10 cm wider than the opening (5 cm into each jamb, as tools/gen_city.py's DOOR_LAP), and is
   turned so it opens away from you (a door pushed from its front has its front toward you). A window
   is the glazing prefab at its own thickness.
5. A door joins the named places either side of it, as a generated map's door does: RegionAId is the
   place behind its front, RegionBId the one in front, kept in the overlay as the map's numbers. With
   the same place (or the outside) on both sides it joins nothing, like an unauthored door.
6. One undo takes it all back and puts the wall up whole.

### /edit build

`/edit build KIND [FIELD VALUE ...]`, KIND one of floor, wall, roof, door, window, prefab. Fields:

| Field | Kinds | Value |
|---|---|---|
| width | floor, roof, door, window, prefab | metres |
| length | floor, roof, wall | metres |
| height | wall, door, window, prefab | metres |
| thickness | floor, roof, wall | metres; unsaid, the material's own |
| depth | prefab | metres |
| above | roof, window | metres above the floor |
| material | floor, roof, wall | a material offered for that kind |
| type | door | knob, pushbar, glass-pushbar, glass-pull, patio-slide |
| category, prefab | prefab | a category word, a prefab id; an unsized prefab is its own size |
| where | all | ahead, here, cursor |
| distance | all | metres in front of you |
| facing | all | me, north, east, south, west |
| fit | door, window | yes or no |

Short forms: `here`, `cursor`, `ahead METRES`, `fit`, `free`. A field not said takes the dialog's
default. A choice may be shortened to its start (`material br`). Examples:

```
/edit build floor width 6 length 8 material concrete here
/edit build wall length 6 height 2.7 material brick ahead 2
/edit build door width 1 type pushbar
/edit build window width 1.5 height 1.2 above 0.9
/edit build roof width 6 length 8 above 2.7 cursor
```

The dialog sends the same command with every field and the word `dialog` on the end, which makes the
server answer with an editor menu ("build.placed" or "build.refused") rather than a line of chat, so the
dialog can tell its answer from anything else said.

### Code and wire

- Server: WorldEditor.Build.cs. Pieces are the library's prefabs placed and scaled through the
  editor's own path (overlay additions, PlaceOp, undo), so the collider and every client's acoustic
  geometry are the size asked, as a scaled city wall's are. A fitted door is a BatchOp: the wall's
  DeleteOp, then a PlaceOp for each piece and the leaf.
- `Restore` (every placing and undo) now joins a door's places from its map entry's RegionAId and
  RegionBId, as the loader does.
- The form is an EditorMenu with Path "build.form": the kinds as Action items, each kind's fields as
  Input items (Command the kind, Label the field word), and each choice as an Info item (Command
  "KIND.FIELD", Value sent, Help a prefab's category, Prompt the "field=value" pairs it sets, Count 1
  for a prefab that can be sized). No new message and no new member: the wire is unchanged.
- Client.Core: BuildCatalog (the form read), BuildForm (fields, what is in use, the check, the
  command, the remembered values), BuildDialog, and ModalDialog: a dialog that stays open while it is
  used and closes on Escape or the key that opened it. The F12 dialog (section 16) uses
  ModalDialog the same way. Heads: GtkClientShell.Build.cs and MainWindow.Build.cs.
- Rooms are measured at load (regions measure themselves): a wall built into a room changes how the
  room sounds after the map is loaded again, not at once. The wall itself is heard at once (it is
  geometry), as any placed thing is.

## 16. The F12 dialog

Built 2026-10-09 (Cody: the editor should be a dialog, not a menu, and do nothing without
permission). Untried with Orca and NVDA: tests only.

### Opening and closing

- F12 opens it, in both clients, on a map you may edit (`edit` here: the same rule as `/edit` and
  Control+B). The client asks the server (`/edit dialog open TAB`) and the server does not answer a
  player who may not edit, so on any other map F12 does nothing: no sound, no speech.
- F12 again, Escape or Close shuts it. It opens on the tab last used this session.
- It is modal. While it is open you do not move, so "in front of you" is from where you stood.
  Actions that need you to move or listen (nudging by the step, walking somewhere) are not in it; the
  menus of section 3 (`/edit` typed) still have them.
- Placing, applying and the other buttons keep it open. The game says what happened, as the server
  words it ("Placed: Concrete Wall, 2 by 0.5 by 3 metres high, 0.65 metres in front of you, facing
  north. It is selected."). The focus stays where it was.

### Keys

- Control+Tab and Control+Shift+Tab change tab, as do Control+Page Down and Control+Page Up; the focus
  goes to the first control of the tab. Control or Alt on their own do nothing.
- Tab and Shift+Tab move through a tab's controls in the order listed below, then to Undo, Redo and
  Close. Tab leaves a list rather than stepping through its rows; the arrows move in it.
- Enter in a box, drop-down or list presses the button it belongs to (Place, Apply changes, Find,
  Set ...). Enter on a button presses it.
- Space ticks or unticks a row of Things near you. On other lists Space does nothing.
- Up and Down change a closed drop-down's choice (on GTK the game says the new choice, as in the
  Control+B dialog). Space opens its list.

### The tabs, in Tab order

Undo, Redo and Close are under every tab. Undo and Redo say what they would undo or redo ("Undo:
moved Fountain"); with nothing to undo the button says so and is dimmed.

**Place**

- Place a prefab: Search (words in a prefab's name or category; the list shows those with every
  word); Category (All categories, then the editor's categories in the order of section 17, with
  Buildings and Vehicles first and Groups last);
  Prefabs (each "Name, width by depth by height high: its description"); Where it goes (at your feet,
  or just in front of you if it is solid; at the build cursor; preview only); Place; Preview (things
  with a sound of their own); Place again (names the last thing placed).
- Build a piece: the Control+B form (section 15) without its Prefab kind, since the list above places
  prefabs: What (floor, wall, roof, door, window), its fields, and Place the piece. The same form,
  values and checks as Control+B, which stays as the quick way to it.

**Edit** (what used to be Select, Selected and Held)

- Placed on this map (section 17): a filter, the list of everything placed with the editor wherever
  it is, and Remove it, Go to it, Edit it, Tick all shown. It comes after Things near you.
- Things near you: what is within 20 metres (up to 40), what you stand on or in first, then nearest;
  each "Name, distance and direction". Arrowing chooses one (`/edit select #ID dialog`, said by
  nobody: the screen reader reads the row). Space ticks it (`/edit select add #ID dialog` or
  `/edit select drop #ID dialog`). Find by name or number, and Find (`/edit select NAME` or `#ID`): the
  nearest match is chosen and put in the list.
- Chosen: NAME: Position (east, north and up in metres, as F1 says where you are; `/edit move to`),
  Facing (degrees clockwise from north; `/edit face DEGREES`), then the thing's settings as the
  Settings menu builds them (name, width, height, depth, model, volume, range, door sides, room
  materials ...): a box for a number or words, a drop-down for a choice, a tick for on and off. The
  first box's description starts with the thing's summary. Apply changes; Bring to me; Duplicate;
  Row of copies: how many, and spacing in metres; Row of copies; Delete (asks "Delete NAME?", No
  first).
- N ticked (only when something is ticked): Move them together (east, north, up), Move them; Turn
  them together (degrees), Turn them; Group name, Group them, Save as a building; Delete the ticked
  things (asks first); Untick all.

**Build** (the Library)

- Library: a line that says "Changes apply to every map that uses it, so duplicate first to try
  things." (also the description of Kind); Kind (Prefabs first, then the other kinds, each with how
  many); Category (prefabs only); Models ("id: Name, version N"). Arrowing chooses one
  (`/edit dialog model KIND ID`).
- The chosen model: what it is and where it is used; Id for the copy (suggested ID_copy) and
  Duplicate (`/edit model copy`, edit-models). The copy becomes the one shown, ready to change.
- Fields: every field of the model, groups and lists opened out ("Compressor hum level, 63 dB"),
  then the value of the field chosen (a box, a drop-down or a tick, read only without edit-models)
  and Set (`/edit model set`).
- Versions: newest first, with Use on every map, Pin on this map and Lift this map's pin.
- Where it is used: one row per loaded map.
- Replace: Replace it with, Replace on this map, Replace everywhere (edit-models).

**World** (what used to be Map)

- Weather, time and ground: Weather, Time of day, Natural ground; Apply changes; Set spawn here.
- Map size (the owner, or `maps-any`, and not on the server's own maps): east, north and height;
  Change the size (`/setmapsize`).
- Rooms and areas (what used to be "Places and rooms", renamed so that "place" means only the
  action): the named places and rooms, nearest first; Edit it chooses one and shows the Edit tab with
  its name and materials.
- Beacon rules: one drop-down per kind of beacon (on unless a player turns it off, off unless turned
  on, always on, never); Apply beacon rules.
- Editors: who may edit the map; for the owner, Player name, Add editor, Remove the chosen editor.
- Model versions pinned to this map; Lift the chosen pin.
- Map information (what used to be Test tools): name and owner, size, tiles, how many things, the
  spawn point; What is around me (`/scan`).

### Screen readers

- GTK (Orca): standard GTK 4 widgets. Each box, drop-down and list has its label as its mnemonic
  widget (its accessible name) and its help as a tooltip (its description). Each tab page and each
  titled part is a frame named for it, so Orca says the name as the focus moves into it. The game
  says nothing Orca reads (the window, the tab, the focused control); it speaks a refusal, a
  drop-down's new choice on Up and Down, and the server's answers.
- Windows (NVDA): a TabControl, each part a GroupBox, controls with AccessibleName and
  AccessibleDescription; the things list is a CheckedListBox, so NVDA says "checked" and Space ticks.
  With NVDA running the game says nothing NVDA reads; without it, it says the dialog and the tab as
  they open. A refusal and the server's answers are always said.

### Code and wire

- Server: WorldEditor.Dialog.cs. `/edit dialog open TAB` sends the editor menu "dialog" with every
  tab; `/edit dialog tab TAB` sends "dialog.TAB" (Refresh); `/edit dialog model KIND ID` chooses the
  Build tab's model; `/edit dialog close`. While the dialog is open, whatever would open or refresh a
  menu (a placing, a change, an undo) sends the current tab instead, so the dialog is kept up to date
  by the same Refresh every operation already makes. `/edit menu` clears it.
- Each item carries a Section ("place.prefab", "edit.thing", "edit.field", "build.version",
  "world.beacon", "foot.undo" ...); fields are Input items as the value dialog has them (section 14),
  their choices Info items with the field's Command. The Place tab's piece form is the build form's
  items (section 15) with Section "piece".
- New on the server for the dialog, and typed too: `/edit move to EAST NORTH UP`, `/edit face
  DEGREES`, `/edit select drop #ID`, and `dialog` on the end of a select (said by nobody) or a hold
  (said as "Ticked"). `/edit place` now says "Placed: NAME, SIZE, where, facing". A deleted thing is let
  go of. A copy made while the dialog is open becomes its Build tab's model.
- Wire: `EditorMenuItem` gained `Section` and `Checked`, appended after `Count`. No new message.
  OpenFPS.Common changed: a new Windows zip and a server update go together.
- Client.Core: EditorDialog (tabs, sections and controls; what the player typed is kept when the
  server's values come again; the checks of EditorValuePrompt; the commands), EditorDialogMemory (the
  tab, the category, where, the kind, for the session). Heads: GtkClientShell.Editor.cs and
  MainWindow.Editor.cs draw any section from its controls, update a section in place when its
  controls are the same, and draw it again (keeping the focus) when they are not.
- Tests: EditorDialogTests.

## 17. Buildings, vehicles, and everything placed on a map

Built 2026-10-09 (Cody: "aren't vehicles considered prefabs too? What about buildings?" and "can I get a
list of items I placed manually on the map and filter them down to remove them without being near the
item?"). Untried with Orca and NVDA: tests only.

### Place's categories

In this order: Buildings, Vehicles, Walls and fences, Floors roads and roofs, Doors, Stairs and ramps,
Furniture and seating, Machines, Water, Fire, Trees and plants, Sounds, Places and markers, Things to
carry, Other, Groups. A category with nothing in it is not listed. `CategoryOf` decides a prefab's from
what it is, never from a list of ids, and the first rule that holds wins:

1. carried (IsItem): Things to carry; a door (IsDoor): Doors;
2. its sound: `machine:` Machines; `water:`, `flow:`, `shore:` (or "water", "shore_" in the id) Water;
   `fire:` Fire; `foliage:` (or tree, foliage, hedge) Trees and plants;
3. a room, a region, a doorway, a name or a trigger: Places and markers (a beacon is not: it sounds);
4. "building" in the id: Buildings; "stair" or "ramp": Stairs and ramps; the Audience material (seats
   and upholstery) or "furniture" or "seat": Furniture and seating;
5. floor, road, roof, ground, ceiling: Floors, roads and roofs; wall, fence, pillar, arch, boulder:
   Walls and fences;
6. a sound of its own, a beacon, or "emitter" in the id: Sounds; anything else: Other (no prefab today).

Moved by the audit of the 88 prefabs: building_box to Buildings (was Walls and fences); the four stairs
and ramps to Stairs and ramps (were Other); furniture_soft and grandstand_seating to Furniture and
seating (were Other); brick_arch to Walls and fences (was Other); sound_emitter to Sounds (was Other);
pa_speaker, space_megaphone and chirp_beacon to Sounds (were Places and markers, by their beacon type).
The rest stayed where they were.

### Vehicles

- Vehicles are not prefabs: a vehicle is a composite shell built from its profile (VehicleShell), parked
  by CompositeService. Place lists every preset `/spawn vehicle` takes (the parkable aircraft, then
  MachineRegistry's road vehicles, the parts lists in machines/ included; retired ones are left out), by
  the vehicle's own name ("1.6 hatchback", "Helicopter") and size. Their place id is `vehicle:PRESET`:
  `/edit place vehicle:v8_muscle`, or `/edit place vehicle v8_muscle` typed.
- Where it goes follows the place mode: at your feet means clear ground beside you, facing your way,
  found as `/spawn vehicle` finds it (`CommandHandler.ClearGroundBeside`, open sky for an aircraft); at
  the build cursor means there, facing the build heading. Preview says a parked vehicle's engine is off.
- It belongs to the map (no owner), so anyone may drive it. It costs one against the 5,000 placed by
  owners, and one undo takes it away.
- Kept in the overlay as an addition whose prefab is `vehicle:PRESET`, with where it was put. The map
  loader leaves such additions out of the map's entities, so `/savemap` never writes a vehicle into the
  map file; `WorldEditor.ParkKeptVehicles`, called once at start after the map's composites
  (Program), parks each again where it was put. The editor's index (AuthoredEntities) names its root,
  so it is listed, removed, undone and redone like any placed thing. A car somebody drove off goes back
  to where it was put after a restart.
- A vehicle somebody is sitting in is not removed, and its undo or redo is refused while they are in it.
  An undo or a removal counts a parked vehicle as unmoved within a metre across the ground, since it
  settles on its wheels.
- A drivable vehicle is not selectable or tickable (it has a velocity, as in phase 1), so it cannot be
  moved or grouped; a parked aircraft is a still body and can be.

### Buildings

- building_box, and any group saved as a building. `GroupSpec.Building` (a JSON field, false on every
  group made before) files a group under Buildings instead of Groups. `/edit building NAME` makes one
  from what you hold, as `/edit group NAME` does (edit-models); the dialog's ticked section has Save as a
  building beside Group them. `/edit model set group ID Building on` refiles an existing group.
- A house built with Control+B: stand in it, Filter placed things "within 20", Tick all shown, type a
  name, Save as a building. Place, Buildings puts it down again in front of you.
- In the dialog's Place list a group's value is `group:ID`, and `/edit place group:ID` places it as
  `/edit place group ID` does.

### Placed on this map

- The overlay's additions on the map you are on that are still there, nearest first, up to 200 (the
  filter finds the rest). Each row: name (and kind, when the name was changed), distance and a compass
  word ("104 metres north east"; "6 metres up" when well above you), who placed it and when ("placed by
  cody, 9 October 14:02", the server's local time). Additions kept before 2026-10-09 have no one
  recorded and say "placed earlier".
- `OverlayAddition.PlacedBy` and `PlacedAt` (UTC) are new JSON members, absent on old files. A new
  placing (any place, copy, row, group, build, replace) records the editor and the time; an undone
  removal keeps what it had.
- Filter: every word must be in the name, the kind, the prefab id, or who placed it ("earlier" for the
  old ones); `within 20` (or `20m`) keeps what is within 20 metres. The filter is the server's, held per
  editor, so the dialog, `/edit placed` and Tick all shown agree.
- Dialog (Edit tab, section "Placed on this map", after Things near you): Filter placed things (a box;
  Enter or Filter sends `/edit placed WORDS dialog`), the list Placed on this map (its description
  starts with the count: "12 things placed on this map, nearest first."), Remove it, Go to it, Edit it,
  Tick all shown. Space ticks a row (the same held things as Things near you); Delete in the list presses
  Remove it. Remove it asks "Remove Megaphone?" (No first); once it has gone the row after it is chosen
  (the one before if it was the last), so the focus stays in the list and Delete again goes on down it.
  Go to it is dimmed, with "Needs the move permission on this map." as its description, for a player who
  may not go. With nothing placed the list's one row says so and the buttons are dimmed.
- Removing is a DeleteOp each (a BatchOp for several), undone as any deletion, told to the other editors
  ("cody removed Megaphone.") and logged by the server ("WorldEditor: cody removed Megaphone
  (#900000012) on 'mine'."). The answer is spoken: "Removed Megaphone, 104 metres north east. Undo puts
  it back."
- Go to it (`/edit goto #ID`): a spot beside it, out from its side nearest you first and then round it,
  on whatever floor is there, facing it. Allowed as `/move` is (your own map, or the move permission) or
  with tp-free; an invited editor may not.
- Menus: the root menu has "Placed on this map, N"; each row opens Remove it, Go to it, Select it.

### Commands

| Command | |
|---|---|
| `/edit placed [WORDS]` | the list (a menu; numbered lines for a text client); WORDS become the filter |
| `/edit remove #ID [#ID ...]` | remove things wherever they are, all or none, one undo |
| `/edit remove held` | remove every held (ticked) thing |
| `/edit goto #ID` | stand beside a thing |
| `/edit select add placed [WORDS]` | hold everything the list shows |
| `/edit place vehicle:PRESET [at cursor]` | park a vehicle |
| `/edit building NAME` | a group listed under Buildings |

`/edit remove` with no number is `/edit delete`, as before. Every one is checked as `/edit` is: `edit`
here, or the map's owner, or an editor the owner named.

### Code and wire

- Server: WorldEditor.Placed.cs (the list, removing, going), WorldEditor.Vehicles.cs (parking and
  keeping), Menus.cs (`PlaceRows`: prefabs, vehicles and groups as one ordered list for the menus,
  Find and the dialog). CommandHandler's editor is given the CompositeService.
- Client.Core: EditorDialog's placed section, `DialogControl.Delete` (the button the Delete key presses
  on a control); both heads press it (GtkClientShell.Editor.cs, MainWindow.Editor.cs) with no modifier.
  GTK takes the main Delete only, not the keypad's; WinForms cannot tell them apart, so on Windows the
  keypad's Delete (Num Lock off), if NVDA lets it through, also asks to remove. Ticks on either list go
  through the server.
- Wire: unchanged. The new rows are EditorMenuItems with Sections "edit.placedinfo" and "edit.placed"
  (Help is the thing's name, for the question); Place's rows are all "place.prefab" now, groups
  included, so "place.group" is no longer sent. The overlay file gained two members.

### Things changed or removed from the map file

The list holds what the editor added. Things from the map file that were moved, changed or removed are
listed beside it in "Changed on this map" (section 18, built 2026-10-10).

## 18. Phase 3: changed things, versions, roads and railways, people

Built 2026-10-10 (todo item 6; Cody's list of 2026-10-08, section 7), in this order of value. Untried
with Orca and NVDA: tests only (OpenFPS.Tests/WorldEditorPhase3Tests*.cs). Every new control is one of
the dialog's existing kinds (a box, a drop-down, a list, a ticking list, a button, a line of words), so
the two heads draw them as they draw the rest. The one head change: a line of words now changes its
words where it stands (the route form's "Laying ..." line), the same in GTK and WinForms.

### Changed on this map

- The overlay's Changed and Removed: the things from the map file the editor moved, turned, resized,
  renamed, set or removed. Listed nearest first, up to 200 (the filter finds the rest). A row: name
  (and kind, when the name differs), what was done, and where it is from you: "Old Wall, Concrete Wall,
  moved 2 metres east, turned 90 degrees clockwise, name Old Wall, 7 metres north east". A removed one:
  "Brick Wall, removed, it was 15 metres north". A change the map file no longer matches (a generator
  moved the thing) is counted in the headline, not listed: "...; 1 change the map file no longer
  matches, kept in case it does again".
- Filter: words in the name, the kind, the prefab, or what was done (moved, turned, removed, a setting's
  name); "within 20". The filter is the server's, per editor.
- Put it back as the map has it (`/edit putback #ID [#ID ...]`): the thing is made again from the map
  file as it is on disk now (read again whenever it changes), with the file's place, turn, size, name,
  door sides, room materials and indoors, and the overlay keeps nothing for it. A removed one is brought
  back. One undo changes it again (a DeleteOp and a PlaceOp in one BatchOp, so nothing new had to be
  undone). Refused, with the reason said, when somebody stands where it would go, somebody sits in it,
  or the map file no longer has it.
- Go to it: beside it; for a removed thing, beside where it was.
- Edit it: chooses it on the Edit tab; for a removed one it says "Brick Wall has been removed. Put it
  back first to change it."
- Dialog (Edit tab, section "Changed on this map", after "Placed on this map"): Filter changed things
  (a box; Enter filters), Filter, the list "Changed on this map" (its description starts with the
  headline), "Put it back as the map has it" (asks "Put Old Wall back as the map has it?" or "Bring
  back Brick Wall as the map has it?", No first), Go to it, Edit it. When a row goes, the row after it
  is chosen (the one before if it was the last), as in the placed list. Not a ticking list: a removed
  thing cannot be held.
- Menus: the root menu has "Changed on this map, N"; each row opens Put it back, Go to it, Select it.
- Found on the way and fixed: a thing made again from its map entry (undoing a deletion, putting one
  back) now gets the entry's door sides, room materials and indoors (MapManager.ApplyEntryExtras), as
  the loader gives them; before, an undone deletion of a door lost its locked side.
- The overlay's removals keep the name the thing had (`OverlayRemoval.Name`; absent on old files, which
  say the prefab's name).

### Versions of a map

- A version is a copy of the whole overlay, with a number, a name, who saved it and when. One file per
  map: `maps/overlays/versions/MAPID.json` (ignored by git with the overlays). At most 100 per map; past
  that the oldest saved by the editor itself goes first.
- `/edit map save NAME`: "Saved version 1 of this map, before the market: 12 placed, 4 changed, 1
  removed, 1 map setting." Any editor of the map.
- `/edit map versions`: newest first, "Version 2, before restoring version 1, by cody, 10 October 14:02:
  12 placed, 4 changed, 1 removed".
- `/edit map restore NUMBER|NAME`: the map's edits become what the version has. Done with the editor's
  own operations (things taken away and put down, things from the file put as the version has them,
  roads and railways taken up and laid, the spawn point, the map's settings, pins), all in one BatchOp,
  so one undo takes the whole restore back, and a failure part way puts back what was done. What the
  map had is saved first, as "before restoring version N". The map's size is changed only for its owner
  (or maps-any), as /setmapsize; otherwise it is left and the answer says so.
- `/edit map bake [now]`: the map's edits written into the map's own file. Without "now" it says what
  it will do. Only the map's owner or maps-any, and only for a map players make (maps/players/). Every
  map that ships with the server is written by a program in tools (city.json by gen_city.py, which it
  must stay byte for byte; the speedway; the real places by gen_osm.py), and is refused: "city is
  written by a program in tools and must stay byte for byte what that program writes, so its edits stay
  beside it in the overlay, where they are laid over it every time it loads. To make them part of the
  map, change the program."
  - What a map file holds is written: places, turns, sizes, removals, additions (with their ids),
    names, door sides, indoors, room materials, the spawn, the weather, hour, ground, beacon rules and
    size, roads and railways, people.
  - What it does not hold stays in the overlay, laid on the baked places: a sound's volume, range,
    minimum distance and model; parked vehicles; pins; the walkers setting.
  - The file as it was is kept beside it as `NAME.json.before-bake-YYYYMMDD-HHMMSS` (not read as a
    map), a version "before baking" is saved, and every editor's undo history on that map is cleared,
    since it was about the overlay. Undo cannot take a bake back: the answer says so and the dialog asks
    first.
- Dialog (World tab, section "Versions of this map", after the map size): Name for a new version (a
  box; Enter saves), Save a version, the list "Versions of this map" (Enter restores), Restore the chosen
  version (asks "Restore version 1, before the market? What the map has now is saved as a version
  first."), Write the edits into the map file (dimmed where it may not be done, with the reason as its
  description; asks "Write this map's edits into its file? Undo cannot take it back; the file as it was
  is kept beside it.").
- Menus: Map, "Versions of this map, N"; each version opens Restore this version; "Write the edits into
  the map file" opens its explanation and "Yes, write them into the file".

### Roads, paths and railways

A road, a path or a railway is laid by walking it or by typing its points, and becomes two things:

1. Pieces: ordinary things placed with the editor, listed in "Placed on this map" with everything else.
   A road or path: its surface, one piece per straight stretch, its width, 2 cm proud of the ground so
   it is what you stand on, the joints overlapping by half a width so a bend has no gap. A path's
   pieces are called "NAME, pavement", which makes them pavement to the people who walk
   (Pavements.IsPavement) and to the characters. A railway on the ground: a gravel bed ("NAME, track
   bed"). Raised: a concrete deck half a metre thick ("NAME, deck") on pillars at most 20 metres apart
   ("NAME, pillar"). Underground: a concrete tunnel of its own, floor, two walls and roof, 5 metres high
   inside and at least 5 wide, with no digging ("NAME, tunnel floor", "tunnel wall", "tunnel roof"). A
   station on the ground or raised gets a platform beside the line, 40 metres long and 3 wide, a train's
   floor high ("STATION, platform"). A pillar or platform where somebody stands is left out and said.
2. Data, at load (MapOverlayStore.Lay, before the map is built): a road is a RoadData (as many 3-metre
   lanes each way as fit, at its speed; residential below 50 km/h, collector from 50), so the road
   network, wet roads and pedestrian crossings use it; a railway is a TrackData (its line at the rail
   head, its stations as TrackStopData of kind platform, 30 seconds), a LevelCrossingData for each
   crossing, and a TrainData for its train, so RailSystem and CrossingSystem run it. Live, the data goes
   into the map's data at once, and a railway's train is put on its track at once (RailSystem.SpawnOne);
   taking it up takes the train off (RailSystem.RemoveNamed, new).

Laying:
- `/edit route start road|path|railway [FIELD VALUE ...]`: the first point where you stand. Close the
  editor and walk: a point is dropped every metre (WorldEditor.Tick, four times a second from the
  server's tick), and every 20 metres you are told how far it has come ("40 metres."). `/edit route new
  ...` starts with no point, for typing.
- `/edit route points EAST NORTH [UP]; EAST NORTH ...`: points typed, as F1 says where you are; a point
  without a height is on the ground there, found from your own level.
- `/edit route point` (one where you stand), `/edit route back` (the last taken back), `/edit route set
  FIELD VALUE`, `/edit route station [NAME]` and `/edit route crossing [NAME]` (a railway's, at the point
  of the line nearest you), `/edit route cancel`, `/edit route` (what is in hand).
- `/edit route finish [FIELD VALUE ...]`: laid. The straight stretches are joined first (a point is
  kept only where the way turns more than half a metre off the line, Douglas and Peucker), so a walked
  road is a few points, not hundreds. A road or path needs two points, a railway three not in a line:
  a railway is a loop, its last point joined to its first, and a train must be able to run it (a
  RaceLine is made from it first). One undo takes all of it up.
- Fields: width (0.5 to 60 m; usually 7 road, 2 path, 4.2 railway), surface (a material a thin floor
  or road prefab is made of: Asphalt, Concrete, Gravel and the rest; usually asphalt, concrete,
  gravel), level (ground, raised, underground), height or depth (3 to 60 m; usually 6 raised, 8
  underground), train (a train preset, or none), speed (km/h: a road's limit, usually 40; a train's top
  speed, usually 60), name.
- `/edit routes` lists what was laid; `/edit route remove NAME` takes one up with its pieces and train,
  one undo; `/edit route goto NAME` stands you beside its nearest point.
- Answers: "Laid a railway, Loop line: 4 points, 160 metres round, 4.2 metres wide, gravel, on the
  ground, 1 station, 1 level crossing, a light rail train at up to 60 km/h. Loop line train runs it
  now. One undo takes it up."
- Kept in the overlay's Routes (OverlayRoute: id, kind, name, the points at the ground, width, surface,
  level and metres, speed, stations by metres round, crossings, train, the pieces' numbers, who and
  when). A version restores them; a bake writes their data into the file.
- Dialog (Place tab, section "Lay a road, path or railway", after "Build a piece"): What (Road, Path,
  Railway), Name, Width in metres, Surface ("The usual for it" first), A railway runs (On the ground,
  Raised on pillars, Underground, in a tunnel of its own), Height or depth in metres, Train on it (None
  first), a line saying what is being laid ("Laying a railway, Loop: 4 points, ..."), Start here, then
  walk it; Points, typed (a box; Enter adds); Add the points; Drop a point where you stand; Add a
  station where you stand; Add a level crossing where you stand; Take back the last point; Lay it;
  Cancel: lay nothing. What does not apply is dimmed (level, depth and train for a road; the laying
  buttons when nothing is being laid). World tab, section "Roads, paths and railways": the list of what
  was laid, Go to it, Take it up (asks "Take up Loop line?"; the next row is chosen after).
- Menus: the root menu has "Roads, paths and railways, N" (or "...: laying NAME").

What the existing road and rail data cannot carry, so is not done:
- A road is not joined to other roads: the tool makes no JunctionData, so traffic driving the map's
  routes does not turn onto it, and RoadNetwork logs its ends as dead ends. No traffic is put on it.
  The network is built at load, so a new road is in it from the next load.
- A railway is a loop: RaceLine and RailSystem run a train round a closed line. A line with two ends
  that a train runs back and forth on is not in the rail code.
- A level crossing's bells and gates are made at load (CrossingSystem.Spawn), so they come with the
  next load; the line's bed is not cut where a road crosses it (gen_city.py leaves a gap).
- An underground station has a stop but no platform, stairs or way down from the street; nobody can
  walk into an underground line yet. A tunnel has no named place inside it, so it is not a room.
- One train per railway, with the line's speed as its top speed.
- A road's surface is one material its whole length; RoadData can hold stretches, the tool does not.

### People

- Characters (CharacterSystem): `/edit person add NAME [voice VOICE]` puts somebody on the map, with the
  kind of life CharacterSystem has (homeless; the only one written so far) and a voice from the speech
  catalogue (alex, the one that recorded a character's lines, first). "Sam lives on this map now,
  homeless, voice alex, goes to any place the map has. The map has 2 places for their day; /edit person
  place add PLACE keeps them to some."
- Their places: `/edit person choose NAME`, then `/edit person place add|drop PLACE`. The places offered
  are those CharacterSystem would find for them (HauntFinder: bus stops, the pavement outside front
  entrances, lobbies, squares), by the names it gives them ("the bus stop, Main Street"). Kept as
  CharacterData.Places (new: a list of those names); with none, any place the map has. CharacterSystem
  keeps their day to the named places (if none of them is on the map it logs so and they go anywhere).
  A change finds their places again; where they are now they finish.
- `/edit person voice VOICE`, `/edit person remove NAME`. Each is a PersonOp, undone as one step.
- Kept in the overlay's People and laid into the map's Characters at load; live, through
  CharacterSystem.AddLive, RemoveLive and Change (new). Only the people the editor put on are changed;
  the map's own (Alex on the city) are listed "from the map file".
- Walkers: `/edit walkers NUMBER` is how many people walk the map's pavements, per 100 metres, besides
  the map's own (0 to 20; the city's generator puts about 3). Kept as the map setting Walkers. They are
  VehicleSystem walkers, as /spawn walker makes and the city's are: spread evenly along every pavement
  25 metres or longer (anything named a sidewalk or pavement, a path laid with the editor included),
  half each way, each at their own pace from 4.2 to 5.4 km/h, started part way along. Changing the
  number makes them again; undo is the setting's. Made again at start (WorldEditor.WalkKeptWalkers,
  after the map's own vehicles, in Program). "2 people walk this map's 48 metres of pavement now, 4 per
  100 metres, besides its own." At most 500 on a map.
- Dialog (World tab, section "People", after the roads): Walkers per 100 metres of pavement (a box; its
  description says how many walk now on how much pavement) and Set walkers; People on this map (each
  "Sam, homeless, voice alex, goes to any place the map has"; the map's own say "from the map file");
  Places the chosen person goes (a ticking list: Space ticks, through the server, which says "Sam goes
  to the bus stop, Main Street: 1 place in their day."); Name of a new person, Voice, Put the person on
  the map; Take the chosen person off the map (asks "Take Sam off the map?").
- Menus: the root menu has "People": the walkers box, a typed "Put a person on the map", and each
  person, whose menu has each place to start or stop going to, each voice, and Take them off the map.

### Wire

No change: no message, member or union tag. The new dialog rows are EditorMenuItems with Sections
"edit.changedinfo", "edit.changed", "world.versioninfo", "world.version", "place.routeinfo",
"place.routesurface", "place.routetrain", "world.route", "world.walkers", "world.personinfo",
"world.person", "world.place", "world.voice". The overlay file gained Routes, People and
OverlayRemoval.Name; a map file's characters gained Places. OpenFPS.Common is unchanged, so the build
hash is the same; the client heads changed (a line of words updated in place), so both clients and the
server should be rebuilt together.

## 19. Shapes in the quick build

Built 2026-10-10 with geometry stage 4 (docs/GEOMETRY.md 12). Untried with Orca and NVDA: tests only.

### The Shape kind

Control+B's What list, and the F12 dialog's Build a piece, have **Shape** after Window: stairs, a ramp,
a round column, a cone, a ball, a dome, an arch or a roof, made of a wall's material at the size given,
placed as any piece is (in front of you, at your feet, at the build cursor; facing the way you face or a
compass direction). Its fields, top to bottom: Shape, Width, Length, Height, Steps, Landing at the top,
Top, Thickness of the arch, Roof style, Roof over, Height above the floor, Rise to the ridge, Material,
then Where, Distance and Facing.

- Choosing a shape puts its own sizes in the fields (stairs: 14 steps, 1 by 3.92 by 2.52 metres) and
  brings its own fields into use; the rest are dimmed and skipped by Tab, as a prefab's size is: stairs
  use width, length, height, steps and landing; a column width and height; a cone those and Top; a ball
  its width; a dome width and height; an arch width, length, height and thickness; a roof its style,
  what it is over, the height above the floor, the rise, and width and length when it is over the size
  given. Both clients take this from the same form (BuildForm), so GTK and Windows behave alike.
- Stairs climb the way they face. Each rise is the height over the steps and may not be over 0.4 m.
- A column, a cone, a ball and a dome are as deep as they are wide.
- A roof **over the floor you stand on** takes that floor's own outline and turn (a footprint's outline,
  or a box's rectangle), its eaves the height above the floor over the floor's top. Its rise is the one
  given, or a quarter of its narrow side (6 in 12). Gable, hip, shed or flat.
- The game says what was made: "Placed: stairs, 14 steps of 18 centimetres on 28 centimetres goings, 1
  wide, concrete, 1 metre in front of you, facing north." A shape that cannot be made is refused and said
  ("Not built: each rise is 0.667 m, over the 0.4 m a body can step."), and the dialog stays open.
- One undo takes it away: "Undid: built stairs, 14 steps."

### Said as a phrase

`/edit build` takes a shape's word first and its numbers in the order it is said:

```
/edit build stairs 14 steps up north          14 steps of 18 cm on 28 cm, climbing north
/edit build column 0.3 by 3                   0.3 m across, 3 m high
/edit build ramp 1.5 by 6 by 0.5 wood         across, along, high
/edit build cone 1 by 2 top 0.5
/edit build ball 0.5 here
/edit build arch 3 by 0.6 by 3.5 thickness 0.5
/edit build roof gable over the floor
/edit build roof hip 8 by 10 by 2 cursor      across, along and the rise, at the build cursor
```

Words a phrase reads: a number before "steps"; "up NORTH" or a compass word for which way it faces; a
roof style; "over the floor"; here, cursor, ahead METRES; any field word and its value; a material's
name (or its start). "roof" with no style is the roof slab it always was. Stairs said with a count and
no sizes are a comfortable flight: 18 cm rises on 28 cm goings, a metre wide.

### Code and wire

- Server: WorldEditor.Shapes.cs (the fields, choices, phrases and building), with WorldEditor.Build.cs
  (the Shape kind) and PlaceOne (a thing placed with a form). The overlay keeps the form (EntityData.Form;
  its copy is deep now, so an outline is not lost when a thing is made again).
- Client.Core: BuildForm.IsEnabled reads which fields a shape uses from its choice's category.
- Wire: no new message or member. The shape choices are Info items of the "build.form" menu (Command
  "shape.shape", Prompt the fields they set, Help the fields they use).
- Tests: EditorShapeBuildTests.
