# OpenFPS manual

Part 1 is for players. Part 2 is for people running a server.

This manual describes the Linux client. The Windows client is behind: it does not yet have saved
servers, the settings dialog or the F-key lists.

---

# Part 1: Playing

## Starting the client

Run `./run-gtk-client.sh` from the OpenFPS folder. It builds the client and starts it. Speech goes
to Orca when Orca is running, and to speech-dispatcher otherwise.

The main menu has four items: **Connect**, **Saved Servers**, **Settings** and **Quit**. Use Tab or
the arrow keys to move and Enter to choose.

### Connecting

- **Connect** logs in to your preferred server. If its password is saved, you go straight in.
  Otherwise the Connect dialog opens with the cursor in the password field. If you have no saved
  server yet, Saved Servers opens instead.
- The **Connect dialog** has: a status line (it repeats the last message), Server address (for
  example `127.0.0.1:33288`), Username, Password, a "Remember password" checkbox, Connect and
  Cancel. Escape closes it. If the login fails, the cursor returns to Username.
- After you log in by hand, the server is saved for you. The first server you save becomes your
  preferred one.
- The Linux client cannot create a new account yet. Ask the server's admin for one, or register
  with the Windows client.

### Saved servers

A list of servers. Move with the arrow keys; Enter on a server connects to it. The buttons are
Connect, Set as preferred, Add, Edit, Remove and Close. Each server is read as its name, address,
user name, and "preferred" if it is your preferred server.

### Entering the world

After you log in the client says "Logged in as" your name and "Loading world", reads the loading
progress every quarter, says "Generating acoustics" and "Acoustics ready", plays a rising chord and
says "You have entered the world".

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
| G | Pick up |
| Q | Drop |
| R | Put away (sling onto your back) |
| Enter | Fire what you are holding (only works with a weapon) |
| T / Shift+T | Engine on / off (driver's seat) |
| V | Voice chat on / off (not available on Linux yet) |
| Escape | Quit dialog ("Quit" or "Keep playing"; the cursor starts on Keep playing) |

E works on things within about 3 m. For a door: E opens it; E again when it is open closes it.

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
- Entering the world: a rising chord.
- Chat: one soft note for your map, two rising notes for everyone, three rising notes for a
  private message to you, two falling notes for the server, a bright chord for an admin.
- Voice chat switched on: one short note.

You can turn these off or change their volume in Settings.

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

### Around you
- `/scan`: named things within 20 m.
- `/doors`: doors within 20 m. `/open [name]`, `/close [name]` (or `/shut`).
- `/room [radius]`: whether the walls around you form a room, and what is missing if not.

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

### Maps
- `/join map` (or `/travel`): go to another map. With no map named, it lists the maps you can
  enter. You can enter public maps, your own maps, and, if you are staff, any map.

### Beacons
- `/beacons`: each kind of beacon, whether it is on, and why.
- `/beacons door`: switch door beacons on or off. `/beacons door on` or `off` sets it.

### Building
- `/prefabs`: the objects that can be placed. `/composites`: saved groups of objects.

## Travelling between maps

Choose a map in the F6 list, or type `/join` and the map name. The client says "Travelling to" the
map, loads it and puts you in. You stay logged in.

## Beacons

Beacons are sounds that mark useful things near you.

| Kind | Sound | Heard |
|---|---|---|
| Door | A wooden knock | The 3 nearest within 12 m |
| Item | A small bell | The 3 nearest within 10 m |
| Vehicle | A low double tone | The 2 nearest within 25 m |

Each repeats every 1.6 seconds. A beacon that is mostly hidden behind a wall is not played. Maps can
also place beacons for exits, stairs and waypoints.

Each map decides, for each kind, whether it starts on or off, is always on, or is not allowed.
Within that, use `/beacons` to choose. Your choices are saved.

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
- **Input device, for voice chat**: stored, but voice chat is not working on Linux yet.
- **Interface sounds**: on or off.
- **Interface sound volume**: 0 to 100.

Settings and saved servers are kept in `~/.config/openfps/client.json`. The file can only be read
by you. A password is saved only if you ticked "Remember password". Beacon choices are kept in
`~/.config/openfps/beacons.json`.

## If something goes wrong

- The client's log is `/tmp/openfps-client.log`.
- If the client crashes, a crash dump goes to `~/openfps-crashes`.

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

    OPENFPS_PORT=33290 ./run-server.sh city --port 33290

`OPENFPS_PORT` only tells the script which port to check. `--port` is what sets the port.

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

The city map is generated by `tools/gen_city.py`.

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
- `StreetLife`: how often, on average, a horn sounds (`HornEverySeconds`), a car brakes hard
  (`HardBrakeEverySeconds`) and a car parks (`ParkEverySeconds`). 0 turns one off.
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

A `materials.json` in the server folder would override the built-in acoustic materials. None ships
with the server.

## Accounts

- Accounts are stored in `openfps.db`, created on first start if it is missing.
- If there are no accounts, the server creates `admin` with the password `admin123`. **Change this
  before letting anyone else connect.** There is no command for it yet: edit `openfps.db` with a
  SQLite tool.
- Anyone can register; new accounts are players.
- User names are stored in lower case. A name that is taken is refused.
- Logins and registrations are limited to 6 quick tries, then one every 5 seconds, per address.
- There are three roles: Player, Dev and Admin. Dev and Admin can use every staff command. To
  change a role, edit `openfps.db`.
- `users.json` is not used.

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
- `/fire weapon`: fire a named weapon from where you stand.

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

## Diagnostic switches

Set these in the environment before starting the server.

| Variable | Effect |
|---|---|
| `OPENFPS_WEATHER=Clear\|Rain\|Snow\|Storm` | Fixes the weather |
| `OPENFPS_TRACE_SHUTTLE=name` | Logs where matching vehicles are, twice a second |
| `OPENFPS_TRACE_CROSSING=name` | Logs a level crossing's state once a second |
| `OPENFPS_PROFILE=1` | Logs a performance summary every 30 seconds |
