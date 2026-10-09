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

### Phase 3

- People: characters, walker density. Roads and routes (roads as data). Map versions and restoring one.
- Baking an overlay into a hand-written map file on request.

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
  used and closes on Escape or the key that opened it. The F12 dialog being designed is to use
  ModalDialog the same way. Heads: GtkClientShell.Build.cs and MainWindow.Build.cs.
- Rooms are measured at load (regions measure themselves): a wall built into a room changes how the
  room sounds after the map is loaded again, not at once. The wall itself is heard at once (it is
  geometry), as any placed thing is.
