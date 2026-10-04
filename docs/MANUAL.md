# OpenFPS manual

Part 1 is for players. Part 2 is for people running a server.

This manual describes the Linux (GTK) client. The Windows client has the same keys, lists, menus
and dialogs, with these differences:
- Its settings are in `%APPDATA%\openfps\client.json`. Its logs are in a `logs` folder beside the
  game, or in `%LOCALAPPDATA%\openfps\logs` if the game folder cannot be written to.
- With NVDA running, the game leaves NVDA to read menus and dialogs and only plays the interface
  sounds.

---

# Part 1: Playing

## Starting the client

Run `./run-gtk-client.sh` from the OpenFPS folder. It builds the client and starts it. Speech goes
to Orca when Orca is running, and to speech-dispatcher otherwise.

The main menu has five items: **Connect**, **Create account**, **Saved Servers**, **Settings** and
**Quit**. Use Tab or the arrow keys to move and Enter to choose.

### Connecting

- **Connect** logs in to your preferred server. If its password is saved, you go straight in.
  Otherwise the Connect dialog opens with the cursor in the password field. If you have no saved
  server yet, Saved Servers opens instead.
- The **Connect dialog** has: a status line (it repeats the last message), Server address (for
  example `127.0.0.1:33288`), Username, Password, a "Remember password" checkbox, Connect, Create
  account and Cancel. Escape closes it. If the login fails, the cursor returns to Username.
- **Create account** on the main menu makes a new account on your preferred server. The Create
  Account dialog opens for that server with Username and Password blank and the cursor in Username.
  It has only Create account and Cancel. A password must be at least 8 characters, a username 3 to 20.
  The account already saved for that server is not used or changed. Once the account is made you are logged in with it, and it is saved as a second entry for
  that server; choose which entry is preferred in Saved Servers. With no saved server, Saved Servers
  opens instead. If the account cannot be made, the reason is said, the cursor goes to the status
  line (which repeats it), and nothing stays connected.
- **Create account** in the Connect dialog makes a new account with the username and password you
  typed.
- After you log in by hand, the server is saved for you. The first server you save becomes your
  preferred one.

### Saved servers

A list of servers. Move with the arrow keys; Enter on a server connects to it. The buttons are
Connect, Set as preferred, Add, Edit, Remove and Close. Each server is read as its name, address,
user name, and "preferred" if it is your preferred server.

### Entering the world

While the map loads, a soft note sounds at every tenth of the way, rising an octave from start to
finish; the loading window shows the same progress. Then a rising chord plays, the world fades in
over about a second, and the client says the map and the place you are in.

### Leaving, and losing the connection

- **Escape** in game opens the game menu: **Keep playing**, **Main menu** and **Quit**. The cursor
  starts on Keep playing, and Escape closes the menu. Main menu and Quit log you out first; the
  world fades out over half a second.
- If the connection drops, the client says "Disconnected from the server", stops every sound in the
  world, and tries to log back in every 3 seconds with a quiet tick. When it gets back in it says
  "Reconnected" and loads the map again. After a minute without success it says so and goes back
  to the main menu. Escape during this opens the game menu.

## Keys

No game key uses Control or Alt, so your screen reader keys keep working. Control still silences
speech as usual.

### Moving

| Key | Action |
|---|---|
| W, S, A, D or the arrow keys | Walk forward, back, left, right |
| Shift with a movement key | Run (1.6 times walking speed) |
| Space | Jump |

A quick tap moves you a short step (about 15 cm). There is no crouch.

### Turning and looking

| Key | Action |
|---|---|
| J / L | Turn left / right |
| O / K | Look up / down |
| Shift with J, L, O or K | Fine turn: 1 degree per tap |

A tap turns you to the next 45-degree mark (north, north-east, east and so on). Holding the key
for more than a third of a second turns you smoothly at 180 degrees a second; with Shift held, 20
degrees a second. You cannot turn while riding in a vehicle: you face forward.

### Finding out where you are

| Key | Says |
|---|---|
| C | Your coordinates: east, north, height, in metres |
| F | The way you are facing |
| Z | The area you are in. When driving: road, heading, lane and speed |
| P or comma | What is ahead of you (name, material, distance, up to 20 m) |
| Shift+P | Named things within 20 m |
| H | Health |
| B | How out of breath you are |
| I | What you are carrying |

The client also says the name of each area as you walk into it, and the name of certain objects
when you come within 3 m of them.

### Doing things

| Key | Action |
|---|---|
| E | Interact: open a door, get into or out of a vehicle, close a door |
| Shift+E | Knock on the nearest door |
| G | Pick up |
| Q | Drop |
| R | Put away (sling onto your back) |
| Shift+R | Draw: take the first thing on your back into your hand |
| Enter | Fire what you are holding (only works with a weapon) |
| T | Clap your hands. In the driver's seat: engine on |
| Shift+T | Engine off (driver's seat) |
| V | Voice chat on / off, on the microphone chosen in Settings. While it is on, everyone on your map can hear you, from where you stand: closer is louder, and walls muffle you as they would anyone talking. You hear other players the same way. You also hear your own voice in the room you are in: the surfaces round you answering it and the room's reverberation, never your voice itself. Use headphones. |
| Escape | Game menu: Keep playing, Main menu, Quit (the cursor starts on Keep playing) |

E works on things within about 3 m. For a door: E opens it; E again when it is open closes it.

### Doors

Doors come in kinds, and each works as the real one does.

- A door with a knob or lever (houses, flats): E opens it and E shuts it. It stays as you leave it.
- A steel door with a push bar (fire, stair and service doors): E opens it. Its closer shuts it
  3 seconds after the doorway is clear, slowly and then quickly for the latch.
- A glass front door (the towers' street doors): opened with a key from the street and by its
  push bar from inside. Everyone has the key for now. A closer shuts it like a steel door.
- A glass door you pull (some shops, none on the city yet): E opens it; a closer shuts it.
- An automatic sliding door (the airport terminal's entrances): it opens by itself when anyone
  comes within 2.5 m of it, from either side, and shuts 2 seconds after they have gone. E does
  nothing to it.
- A patio door (each house's garden side): E slides it open and E slides it shut.
- A lift's doors: opened by the lift. There are no lifts yet.

No door shuts by itself on anyone standing in the doorway. A closer waits; an automatic door
opens again. People in the street use doors the same way.

### Lists

| Key | List |
|---|---|
| F5 | Players on the server |
| Shift+F5 | Players on this map |
| F6 | Maps |
| Shift+F6 | Your maps |
| F8 | Friends |

Inside a list:
- Up and Down move. Enter or Right chooses. Escape, Left or Backspace goes back.
- A letter jumps to the next item starting with that letter.
- You stand still while a list is open. F5, F6 and F8 switch straight to another list.
- Choosing a **player** or **friend** gives: Private message, Where is, View profile, and Add or
  Remove friend.
- Choosing a **map** takes you there. A map is read as its name, how many players are on it,
  "private" if it is private, and "you are here" if you are on it.

### Chat keys

| Key | Action |
|---|---|
| / | Open the command and chat line |
| [ and ] | Read the previous / next chat message |
| Shift+[ and Shift+] | Switch chat buffer |

## Interface sounds

- Moving in a list: a tick. At either end of a list: a low knock.
- Choosing: two rising notes. Going back: two falling notes.
- Loading a map: one soft note per tenth of the load, rising.
- Entering the world: a rising chord.
- Trying to reconnect: a quiet low tick every 3 seconds.
- Chat: one soft note for your map, two rising notes for everyone, three rising notes for a
  private message to you, two falling notes for the server, a bright chord for an admin.
- Voice chat switched on: one short note. Switched off: the same note, then a lower one.
- Another player online: a rising arpeggio. Logged out: the same, falling. Lost connection: a
  broken falling figure. Away: two soft notes down. Back: two soft notes up.

You can turn these off or change their volume in Settings. "Online and offline sounds" turns off
only the presence sounds; the notices are still read in the All buffer.

## Chat

Press `/` to open the chat line. Type a message and press Enter.

- Plain text goes to everyone on **your map**.
- `/all message` goes to **everyone on the server**.
- `/pm name message` is a **private message**.
- `/motd` repeats the message of the day. The server sends it to you once when you arrive.

There are four chat buffers: **All**, **Map**, **Private** and **Server**. Each keeps the last 500
messages. Use `[` and `]` to read messages in the current buffer and Shift with them to change
buffer. Private and server messages are always spoken. Other messages are spoken when you are in
their buffer or in All, which is the default.

## Commands

Type these on the chat line. The `/` is optional and case does not matter.

- `/help` lists the commands you can use. `/help command` says how to type one and what it does.
  `/help settings` lists your own sound settings.
- A mistyped command is answered with the nearest one: "Did you mean /give?"
- A player can be named by the start of their name when only one player's name starts that way.
- `/afk` marks you away; everyone hears "cody is away". After five minutes of doing nothing you are
  marked away anyway. Anything you do marks you back.

### Around you
- `/scan`: named things within 20 m.
- `/doors`: doors within 20 m. `/open [name]`, `/close [name]` (or `/shut`).
- `/knock`: knock on the nearest door (the same as Shift+E).
- `/clap`: clap your hands (the same as T on foot).
- `/room [radius]`: whether the walls around you form a room, and what is missing if not. The
  radius is 12 m unless you give one.

### Hearing
- `/tail [dB]`: everything traced, against the direct sound: every room's and street's tail and the
  echoes of far sources. Zero is the physical level; the default is -6, by ear.
- `/copies [dB]`: every reflection placed as a copy of the sound: the walls' first answers to a clap
  or a step, facade echoes, and the nearest walls and ceiling. Zero is the physical level; the default is -6, by ear.
- `/reflections [dB]`: sets both. Plain `/tail`, `/copies` or `/reflections` reads them back; -80 is off.
- `/cabin [dB]`: the inside of the vehicle you are riding in, against its traced level (default 0).
- `/reverb`: how the tracing is doing (the tail is traced from the geometry everywhere).
- `/echoes on|off`: the traced echoes of far, loud sources. `/echoes -12` turns them on and sets
  the tail level.
- `/valveflow on|off`: the rush of gas through each exhaust valve as it opens, on every engine.

### Vehicles
- `/enter [seat]` (or `/board`, `/getin`): get in. `/exit` (or `/getout`): get out.
- `/seats`: the seats and who is in them.
- `/key on` / `/key off` (or `/ignition`): the engine, from the driver's seat.

### Carrying
- `/take [name]` (or `/get`, `/grab`, `/pickup`).
- `/drop [name|left|right|all]` (or `/putdown`).
- `/stow [name|left|right|all]` (or `/sling`): put on your back.
- `/draw [name]` (or `/equip`, `/wield`, `/unsling`): take into your hands.
- `/hands`, `/inv` (or `/i`, `/inventory`).
- `/fire` (or `/shoot`): fire the weapon you are holding.

### People
- `/friend add name`, `/friend remove name` (or `/unfriend name`), `/friends`.
- `/profile name` (or `/whois`).
- `/where name` (or `/locate`): distance, clock direction and place.

### Teams
- `/team`: your team, who leads it, and how many are online.
- `/team create NAME`: start a team; you lead it. Names are 2 to 20 letters, digits, `-` or `_`.
- `/team invite PLAYER`: any member can invite.
- `/team join NAME`: join a team that invited you, or an open one.
- `/team leave`: if the leader leaves, the longest member leads.
- `/team list [NAME]`: members and who is online.
- `/team kick PLAYER`, `/team open`, `/team close`: the leader only.
- `/t MESSAGE` (or `/team chat MESSAGE`): talk to your team, on any map. It has its own sound and is
  read whatever chat ring you are in.

A team holds up to 16 players.

### Maps
- `/join map` (or `/travel`): go to another map. With no map named, it lists the maps you can
  enter. You can enter public maps, your own maps, and, if you are staff, any map.

### Beacons

**Player beacons.** Other players near you make a beacon sound: two soft notes going down. If they are
in your team, the same two notes are higher. You never hear your own. `/beacons player` turns them
off or on.
- `/beacons` (or `/beacon`): each kind of beacon, whether it is on, and why.
- `/beacons door`: switch door beacons on or off. `/beacons door on` or `off` sets it.
- `/beacons louder` and `/beacons quieter`: every beacon 2 dB up or down, saved. They start 4 dB above
  a doorbell's level.

### Building
- `/prefabs`: the objects that can be placed. `/composites`: saved groups of objects.

## Travelling between maps

Choose a map in the F6 list, or type `/join` and the map name. The client says "Travelling to" the
map, loads it and puts you in. You stay logged in.

## Beacons

Beacons are sounds that mark useful things near you.

| Kind | Sound | Heard |
|---|---|---|
| Door | Two soft notes rising (a gentle ding-dong, upward) | The 3 nearest within 12 m |
| Item | One small ring that dies away | The 3 nearest within 10 m |
| Vehicle | A low warm note, twice | The 2 nearest within 25 m |

Each keeps sounding from the thing itself, every 1.6 seconds unless you choose otherwise. A door's
hangs on the door at face height, on your side of it, and rings the room you are in.
`/beacons every 3` sets the gap in seconds (half a second to ten), and it is saved. A beacon that is
mostly hidden behind a wall is not played. Maps can also place beacons for exits, stairs and
waypoints.

Each map decides, for each kind, whether it starts on or off, is always on, or is not allowed.
Within that, use `/beacons` to choose. Your choices are saved.

## How loud the world is

Real sounds differ a lot in loudness: a hot rod is about 20 dB louder than an economy car, and
flooring an engine raises it by 15 dB or more. `/levels` sets how much of that difference reaches
you. At 100 percent it is the real difference, so loud things carry much further and quiet things
fade sooner. Lower values squeeze loud and quiet together. It applies to every sound in the game.

- `/levels`: the current setting.
- `/levels 70`: set it to 70 percent. Any number from 20 to 100.
- `/levels real`: 100 percent.
- `/levels default`: 45 percent, the setting the game ships with.

The setting is saved.

## People in the street

The pedestrians on the city talk. Each has a voice of their own.

- Walk towards one and they may say hello. Some say nothing. The same person greets you once, then
  not again for a minute and a half.
- Some add a goodbye as you part.
- Walk into one and they apologise, or tell you to watch it.
- Stand in front of someone who has stopped and they ask if they can help you.
- Now and then someone takes a phone call and you hear their half of it.
- Two pedestrians passing each other may greet each other.
- Some people tell a story on the phone.
- Drivers yell when something goes wrong: a hard stop, a car crossing in front of them at a junction,
  a car pulling out, a long wait at the level crossing. Stand in the road in front of a car and it
  brakes, honks, and the driver yells at you.

What they say fits the game's clock and weather: "Morning" only in the morning, "Looks like rain"
only when rain is coming in, "Cold out here today" only when it is cold.

In a text (MUD) session you are told the words of anything said within 10 metres.

## Driving

### Getting in and out
- Stand beside a car and press E to open its door, then E again to get in. You take the nearest
  free seat you are allowed, within 5 m.
- In the seat, E opens your door and E again gets you out.
- You cannot get into anything that is moving.
- Buses stop at bus stops. Press E or type `/enter` to board. You cannot get on or off while the
  bus moves faster than walking pace.

### Controls

| Key | Action |
|---|---|
| W or Up | Accelerate |
| S or Down | Brake; when stopped, reverse (up to 8 m/s) |
| A / D | Steer |
| Space | Brake |
| T / Shift+T | Engine on / off |
| K | Lane assist on / off |
| Z | Road, heading, lane and speed |

Let go of the keys to coast. There is no horn key yet.

### Driving sounds

- **Guide beep** (high): placed on the middle of your lane ahead. Steer towards it. It beeps faster
  the faster you go.
- **Centre line** (middle pitch): beeps from that side as you get within 1.5 m of it, faster as
  you get closer. A steady tone means you are over the line.
- **Kerb** (low, buzzy): the same for the edge of the road. A steady tone means you are over it.
- **Turn clicks**: a click for every 15 degrees the car turns; six clicks make a right angle. A
  rising chime when you are lined up with the road.

### What is spoken
- The road name and your heading when you join a road.
- "Junction in" some metres, and which ways you can go.
- "Road ends in" some metres.
- "Off the road", then the nearest road and "Follow the beep".

**Lane assist** is on when you start. It keeps you in the middle of your lane unless you steer.
Press K to switch it off or on.

## Settings

Choose Settings from the main menu.
- **Output device**: where the sound goes. Empty means the system default.
- **Input device, for voice chat**: the microphone voice chat uses. Empty means the system default.
- **Interface sounds**: on or off.
- **Interface sound volume**: 0 to 100.
- **Open log folder**: opens the folder of the log file the client is writing (set by
  `run-gtk-client.sh`).

Settings and saved servers are kept in `~/.config/openfps/client.json`. The file can only be read
by you. A password is saved only if you ticked "Remember password". Beacon choices are kept in
`~/.config/openfps/beacons.json`.

## If something goes wrong

- The client's log is `/tmp/openfps-client.log`. The client also writes its own copy to
  `/tmp/openfps-client-client.log`, which survives the terminal closing.
- If the client crashes, a crash dump goes to `~/openfps-crashes` (`OPENFPS_CRASHDIR` moves it).
  When the client stops, the script says how: a clean exit, or the signal that killed it.
- `./run-gtk-client.sh capture` records what you hear to `/tmp/openfps-capture.wav` while it plays.
  Use it to catch a click, pop or dropout.
- Other modes for tracking down a fault: `on` and `off` (the Steam Audio simulation on or off,
  with an acoustic trace), `foot` (every footstep logged), `fmodlog` (FMOD's own logging build),
  `nohrtf`, `noecho`, `nophys`, `quiet`, `bare` and `nosplit`. The top of the script says what each
  one does.

---

# Part 2: Running a server

## Starting and stopping

Run `./run-server.sh` from the OpenFPS folder. It builds the server and starts it.

- `./run-server.sh` lands players on the speedway.
- `./run-server.sh city` lands players on the city. Any map name works the same way.
- `./run-server.sh rooms` (or `demo`) lands players on the rooms map (`default.json`).

Every map in the maps folder is loaded either way; the name only chooses where players arrive.

Other options go straight to the server:
- `--port N`: the game port (UDP). Default 33288.
- `--map name`: the same as naming the map.

The server is ready when it prints:
- `MapManager: N map(s) loaded; players will land on 'name'`
- `MUD Gateway listening on TCP port 33289`
- `NetworkService started on port 33288`

Stop it with Ctrl-C. It tells every player the server is shutting down, closes its connections,
and prints "Server stopped cleanly".

### A server is already running

Before starting, the script checks whether something is already listening on the port. If so, it
prints that process and stops. Stop the old server first (`kill` and its process number), or run
the new one on another port:

    ./run-server.sh city --port 33290

`OPENFPS_PORT=33290 ./run-server.sh city` does the same: the script passes it on as `--port`.

### Always restart after editing

Maps, prefabs and machines are read once, when the server starts. After changing any of them,
stop the server and start it again. The message of the day is the exception: it is read each time
it is needed.

## Maps

Maps are JSON files in `OpenFPS.Server/maps/`. Every `.json` file there is loaded at start.

| Map | What it is |
|---|---|
| `speedway` | A 1.25-mile banked oval with named zones and a race. Players land here by default. |
| `city` | Towers, avenues, a parking garage, a plaza, a tunnel, a light rail loop with three stations, an airport, a housing estate, traffic and aircraft. |
| `default` | Rooms, doors and materials to try, with a car driving past the start. |

- The map with `"IsDefault": true` is where players arrive. If more than one says so, the first
  wins and the server warns you.
- Files may contain comments and trailing commas.
- A file that cannot be read is skipped with an error. Unknown fields are reported and ignored.
- If the folder has no maps, the server makes an empty one called `default`.
- Each map can be public or private (`IsPublic`) and can have an owner (`OwnerId`). Players can
  enter public maps and their own; staff can enter any map. All shipped maps are public. Players
  cannot create their own maps yet.

The city map is generated by `tools/gen_city.py`. Anyone can rebuild it, and the result is the same
file byte for byte (a test checks this):

    python3 tools/gen_city.py                  # from the repository folder; writes OpenFPS.Server/maps/city.json
    python3 tools/gen_city.py --out=city.json  # somewhere else

Change the generator, not `city.json`: a hand edit is undone the next time the city is generated.

### What a map file contains

- `Id`, `Description`, `IsDefault`, `IsPublic`, `OwnerId`.
- `Size`, `MinBound`, `MaxBound`, `MinimumY`, `SpawnPoint`.
- Weather and air: `Temperature`, `Humidity` (0 to 1), `AirPressure` (millibars),
  `AirAbsorptionMultiplier`, `Gravity`. Values out of range are corrected with a warning.
- `BeaconPolicy`: for each beacon kind (door, exit, stairs, item, vehicle, waypoint), one of
  `default_on`, `default_off`, `forced_on` or `forbidden`. A kind not listed is on.
- `Entities`: the objects, each with `PrefabId`, `Position`, `Rotation`, `Scale`, `Name`, and room
  and material settings.
- `Vehicles`, `Trains`, `Tracks` (routes with waypoints, width, banking and stops), `Crossings`.
- `Roads` (centreline, type, lanes with direction, width and speed limit, surface stretches) and
  `Junctions` (where roads meet: a point, a radius, a control, the `PriorityRoads` whose traffic
  does not give way, and `GiveWaySeconds`). The server works out the lanes between junctions and
  which turns each can take, and logs any problem at load.
- `RoadStops`: places on the roads where vehicles stop (a bus stop: `Position`, `Kind`,
  `DwellSeconds`, `ForPreset`). Every vehicle whose way passes one, and whose preset matches, stops.
- A vehicle drives the roads with a `Route` instead of a `Track`: `Via` (junction ids in order, back to
  the first: a bus route), or a wander (`StartRoad`, `Direction`, `Seed`, `WanderMetres`: turns chosen
  at random, the same every time the map loads). It gives way where its road does not have priority,
  stops at the road stops it passes and at level crossings on its way, and keeps a gap to the vehicle
  in front on the same lane.
- `StreetLife`: how drivers behave.
  - How often, on average, a horn sounds (`HornEverySeconds`), a car brakes hard
    (`HardBrakeEverySeconds`) and a car parks (`ParkEverySeconds`). 0 turns one off.
  - The gap they keep to the vehicle in front (`FollowHeadwaySeconds`, `FollowMinGapMetres`).
  - How they give way at junctions: the critical gaps (`CriticalGapRightSeconds`,
    `CriticalGapStraightSeconds`, `CriticalGapLeftSeconds`, `CriticalGapMajorLeftSeconds`), the
    speed they arrive at a give-way line (`GiveWayApproachKmh`), and how long they wait before going
    anyway when everyone is waiting (`GiveWayPatienceSeconds`).
- `Composites`: saved groups of objects.

Map and prefab authoring is described in more detail in [AUTHORING.md](AUTHORING.md).

## Other data files

All in `OpenFPS.Server/`:

| File or folder | What it is |
|---|---|
| `prefabs/*.json` | Object types. Checked against `prefab-schema.json`; a bad prefab is rejected at start with the reason. |
| `composites/*.json` | Saved groups of objects, written by `/saveas`. Not meant to be edited by hand. |
| `machines/*.json` | Extra vehicle and engine presets. |
| `motd.txt` | The message of the day. |
| `openfps.db` | Accounts (SQLite). |
| `friends.json` | Friends lists. |
| `logs/` | Server logs, one file per day. |

### Pedestrian voices

The recorded lines are in `OpenFPS.Client/ASSETS/SOUNDS/VOICES/<voice>/`, and the list the server
picks from is `OpenFPS.Common/Speech/voices.csv`. Both are written by the importer from a folder with
a `manifest.csv`:

    tools/import_npc_voices.py inbox/npc-voices-2026-09-26 --clones=seanterry,jimdale,joeb,joel,tim,ben,alec,fluke,camel,tyler

It encodes the lines to Ogg Vorbis. Voices cloned from real people are left out unless named with
`--clones`. A voice whose folder is removed from the inbox is removed from the game. The recordings in
a `<voice>_preview` folder become that voice's stories, with their words from `stories.json`. Rebuild the server
and client afterwards, since the list is built into both.

## Accounts

- Accounts are stored in `openfps.db`, created on first start if it is missing.
- If there are no accounts, the server creates `admin` with the password `admin123`. **Change this
  before letting anyone else connect.** Start the server once with `OPENFPS_ADMIN_PASSWORD` set:
  on a new database that is the admin's password, and on an existing one it resets the admin's
  password. After that you can leave it unset; the password stays. The server warns at every start
  while the password is still `admin123`.
- Anyone can register; new accounts are players.
- User names are stored in lower case. A name that is taken is refused.
- Logins and registrations are limited to 6 quick tries, then one every 5 seconds, per address.
- There are three roles: Player, Dev and Admin. Dev and Admin can use every staff command. To
  change a role, edit `openfps.db`.

## The message of the day

Put it in `OpenFPS.Server/motd.txt`. Each player hears it once when they arrive. Players can hear
it again with `/motd`. Staff can change it with `/setmotd text`; `/setmotd` on its own clears it.
Changes take effect straight away.

## Staff commands

Only Dev and Admin accounts can use these. A player who tries gets "You do not have permission to
execute this command."

### Talking to everyone
- `/announce message`: a message to everyone, from "Server".
- `/setmotd [text]`.

### Moving
- `/tp x y z` (or `/move`): go to a point. x is east, y is north, z is height. It refuses a point
  inside something solid.

### Building
- `/spawn Box|Cylinder Material sx sy sz`: make an object 3 m in front of you.
- `/place id [yaw]`: place a composite. It lasts until the server stops unless you save the map.
- `/origin`, `/at ...`, `/put prefab [turn N] [run N [dir]]`, `/undo`: place prefabs in rows.
- `/group name [radius=12] [free]`, `/ungroup`, `/saveas id`: make a composite from what is around
  you and save it to `composites/`.
- `/savemap`: write the current map back to its JSON file. This removes any comments in the file,
  so keep a copy of a hand-written map first.

### Vehicles
- `/addseat name [drive]`, `/removeseat name`, `/drivable preset`: make something into a vehicle
  you can sit in or drive.

### Sound on the nearest object
- `/set_sound SoundId Volume`
- `/set_audio_mode LoopOne|LoopFolder|OneShot|StateMachine`
- `/play_folder FolderId`
- `/start_state StartId LoopId`

### Other
- `/fire weapon`: fire a named weapon from where you stand. (Anyone can `/fire` a weapon they are
  holding.)

## The text (MUD) interface

The server also listens for plain text connections on the game port plus one (33289 by default),
for testing a server without the game client. Connect with a tool such as `telnet` or `nc`.

- `login name password`
- `say text`, `who`, `who_map`, `maps`, `mymaps`, `friends`
- Anything else is treated as a command from the lists above.

You cannot register through it. It is plain, unencrypted text on every network interface, so do
not expose it on a public network.

## Logs

- The server logs to the terminal and to `OpenFPS.Server/logs/server` plus the date `.log`.
- If the server fails to start it prints `[FATAL ERROR] Server failed to start:` and the reason.

## Environment switches

Set these in the environment before starting the server or the client. The client logs a warning
for each of its switches that is set, so a forgotten one shows up in the log.

### Server

| Variable | Effect |
|---|---|
| `OPENFPS_ADMIN_PASSWORD=...` | Sets the admin password (see Accounts) |
| `OPENFPS_WEATHER=Clear\|Rain\|Snow\|Storm` | Fixes the weather; no fronts roll in |
| `OPENFPS_TRACE_SHUTTLE=name` | Logs where matching vehicles are, twice a second |
| `OPENFPS_TRACE_CROSSING=name` | Logs a matching level crossing's state once a second |
| `OPENFPS_PROFILE=1` | Logs a performance summary every 30 seconds (the client too) |
| `OPENFPS_PORT=N` | `run-server.sh` only: the port to check and to pass on as `--port` |

### Client

| Variable | Effect |
|---|---|
| `OPENFPS_AUDIO_DEBUG=1` | Logs the acoustic trace, your footsteps (`[FOOT]`) and world sounds (`[WAUDIO]`) |
| `OPENFPS_AUDIO_CAPTURE=file.wav` | Records the mix to a file while it plays |
| `OPENFPS_CRASHDIR=folder` | `run-gtk-client.sh` only: where crash dumps go |
