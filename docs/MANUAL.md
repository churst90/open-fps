# OpenFPS manual

Part 1 is for players. Part 2 is for people running a server.

This manual describes the Linux (GTK) client. The Windows client has the same keys, lists, menus
and dialogs, with these differences:
- Its settings are in `%APPDATA%\openfps\client.json`. Its logs are in a `logs` folder beside the
  game, or in `%LOCALAPPDATA%\openfps\logs` if the game folder cannot be written to.
- It speaks through NVDA when NVDA is running, and through Windows' own voice (SAPI) when it is not.
  With NVDA running, the game leaves NVDA to read menus and dialogs and only plays the interface
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
  It has only Create account and Cancel. A password must be at least 8 characters, a username 3 to
  20. Some names are reserved (such as admin, server and staff).
- The account already saved for that server is not used or changed. Once the new account is made
  you are logged in with it, and it is saved as a second entry for that server; choose which entry
  is preferred in Saved Servers. With no saved server, Saved Servers opens instead. If the account
  cannot be made, the reason is said, the cursor goes to the status line (which repeats it), and
  nothing stays connected.
- **Create account** in the Connect dialog makes a new account with the username and password you
  typed.
- After you log in by hand, the server is saved for you. The first server you save becomes your
  preferred one.

Login refusals you may hear:
- "This client does not match the server": your client was built from a different version than the
  server. Get a client built from the same version. The message gives both build numbers.
- "You are already logged in as NAME": an account can be in the game once at a time.
- "Too many attempts": wait a few seconds. Ten wrong passwords in a row lock a name for 15 minutes.

### Saved servers

A list of servers. Move with the arrow keys; Enter on a server connects to it. The buttons are
Connect, Set as preferred, Add, Edit, Remove and Close. Each server is read as its name, address,
user name, and "preferred" if it is your preferred server.

### Entering the world

While the map loads, a soft note sounds at every tenth of the way, rising an octave from start to
finish; the loading window shows the same progress. Then a rising chord plays, the world fades in
over about a second, and the client says the map and the place you are in.

When you log out, the server keeps where you were, your health and what you carry. You come back
there next time.

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

A quick tap moves you a short step (about 15 cm). There is no crouch. Walking into the edge of the
map says "Edge of the map".

### Turning and looking

| Key | Action |
|---|---|
| J / L | Turn left / right |
| O / K | Look up / down |
| Shift with J, L, O or K | Fine turn: 1 degree per tap |

A tap turns you to the next 45-degree mark (north, north-east, east and so on). Holding the key
for more than a third of a second turns you smoothly at 180 degrees a second; with Shift held, 20
degrees a second. You cannot turn while riding in a vehicle: you face the way it faces.

### Finding out where you are

| Key | Says |
|---|---|
| C | Your coordinates: east, north, height, in metres |
| F | The way you are facing |
| Z | The area you are in. When driving: road, heading, lane and speed |
| P | What is ahead of you: name, material and distance, up to 20 m. With nothing ahead, the area and your facing |
| Shift+P | Named things within 20 m, with directions in words (in front, right, left behind and so on) |
| Comma, period | Step to the previous or next thing of the chosen kind, nearest first: its name, direction, distance and floor, and its beacon once |
| Shift+comma, Shift+period | Change the kind: doors, entrances, stairs, items, people, vehicles, places |
| N | Narration of what is ahead on or off (on by default) |
| H | Health, in percent (in the driver's seat H is the horn; Shift+H is your health there) |
| B | How out of breath you are |
| I | Your inventory as a list (see Lists) |
| Shift+I | What you are carrying, in one sentence |

- Comma and period look up to 100 m away. A thing one floor up or down counts as further than one
  on your floor. The kind you chose is saved.
- Narration (N): as you turn, look and walk, the client says what is now in front of you, and what
  passes in front of you. `/narrate on|off` does the same as N.
- Bumps: walking into something plays a knock from where you touched it and says its name. Only you
  hear it. `/bumps on|off` switches it.
- The client also says the name of each area as you walk into it, and the name of certain objects
  when you come within 3 m of them.

### Doing things

| Key | Action |
|---|---|
| E | Interact (see below) |
| Shift+E | Knock on the nearest door |
| G | Pick up the nearest thing within reach |
| Q | Drop what is in your hands |
| R | With a gun in your hands: reload. In a vehicle: wind the windows. Otherwise: sling what you hold onto your back |
| Shift+R | Draw: take the first thing on your back into your hands |
| Enter | Fire the gun in your hands. With no gun, Enter interacts, like E |
| X / Shift+X | Move the fire selector on the gun in your hands on / back |
| Y / Shift+Y | The admin gun's calibre on / back (administrator only) |
| T | On foot: clap your hands. In the driver's seat: engine on |
| Shift+T | Engine off (driver's seat) |
| V | Voice chat on / off |
| Escape | Game menu: Keep playing, Main menu, Quit |

What E does, in order:
1. A thing on the ground within 2 m: picks it up. It takes the one you chose with comma or period,
   else the one in front of you, else it opens a list to choose from. It then says if others are
   still within reach.
2. A shut door within reach: opens it.
3. In a seat: gets you out (if your door is shut, the first press opens it).
4. Beside a vehicle: gets you in (the first press opens its door).
5. An open door within reach: shuts it.

Doors and vehicles are within reach up to about 5 m.

**Voice chat (V).** It uses the microphone chosen in Settings. While it is on, everyone on your map
can hear you, from where you stand: closer is louder, and walls muffle you as they would anyone
talking. You hear other players the same way. You also hear your own voice in the room you are in:
the surfaces round you answering it and the room's reverberation, never your voice itself. Use
headphones.

### Guns

- Only the AKM, the Glock 17 and the M700 exist as things you can hold. Staff give them with
  `/give`.
- Enter fires. On automatic, hold Enter; letting go stops it.
- X moves the fire selector; Shift+X moves it back. It says the setting. The AKM has safe, auto and
  semi. The M700 has a safety. The Glock has none ("This gun has no selector"). On safe, Enter does
  not fire.
- R reloads. It takes as long as the real thing, and you cannot fire until it is done. An empty gun
  clicks.
- After a hit you hear its chime and what you hit and how far: "Hit pedestrian at 17 metres.",
  "Killed sean at 40 metres.", "Hit concrete at 17 metres."
- Aim assistance: a shot from the hip near somebody in plain view is turned onto them. It is on by
  default. `/aimassist off` or the Settings checkbox turns it off.

**The admin gun** is the administrator's alone. It never runs dry and is never reloaded.
- X and Shift+X set what it does to what it hits: kill, vaporize, freeze or inspect.
  - Kill: a lethal round.
  - Vaporize: removes the thing hit. A vehicle goes whole, unless somebody is in it. Floors and
    ground are refused. A player is killed instead. Something from the map file comes back when the
    server restarts, unless the map is saved after.
  - Freeze: a person or vehicle cannot move for 10 seconds. A frozen player is told.
  - Inspect: says what the thing is, whose it is, and its id.
- Y and Shift+Y change the calibre: which gun's rounds and report it fires. `/calibre NAME` picks
  one by name. The calibres are the AKM, AR-15, Glock 17, service pistol (.45), pump shotgun, .357
  revolver and M700.
- `/admingun` says how it is set. `/admingun report 1` to `5` picks one of five report sounds;
  `/admingun report 0` goes back to the calibre's own.

### The scope

Only a scoped rifle has one; the M700 does. Turn Num Lock on: with it off your screen reader uses
the keypad. Raising the scope says so if Num Lock is off.

| Key | While the scope is up |
| --- | --- |
| Numpad * | Raise or lower the scope |
| Numpad 8 / 2 / 4 / 6 | Aim up / down / left / right. A tap is a small step, a held key sweeps; both get smaller as you zoom in. Without a keypad, J L K O do the same |
| Numpad 5 | What the crosshair is on: what, how far, how high, how it is moving |
| Numpad 7 / 9 | Previous / next person or vehicle in view; the aim does not move |
| Numpad + / - | Zoom: 4, 8 or 12 power |
| Numpad 1 / 3 | Turret down / up a click; says the distance you are zeroed for |
| Numpad . | Rangefinder |
| Numpad 0, held | Hold your breath: the crosshair steadies for about five seconds |
| Numpad / or Enter | Fire |

With the scope down, Numpad / opens the command line, like `/`.

The guidance tone pulses faster and higher as you near someone and holds a note when you are on
them; `/scope tone off` silences it. Bullets fall and drift with the wind and take about a second
to reach 600 metres: set the turret for the range and aim ahead of anyone walking. Commands:
`/scope`, `/zoom in|out|N`, `/range`, `/zero N`.

### Doors

Doors come in kinds, and each works as the real one does.

- A door with a knob or lever (houses, flats): E opens it and E shuts it. It stays as you leave it.
- A steel door with a push bar (fire, stair and service doors): E opens it. Its closer shuts it
  3 seconds after the doorway is clear, slowly and then quickly for the latch.
- A glass front door (the towers' street doors): locked from the street. E unlocks it with your key
  and you pull it open. From inside you push the bar. It swings out over the pavement. Everyone has
  the key for now. A closer shuts it 3 seconds after the doorway is clear, and it locks again.
- A glass door you pull (some shops; none on the city yet): pulled from outside, pushed from
  inside; a closer shuts it after 3 seconds.
- An automatic sliding door (the airport terminal's entrances): it opens by itself when anyone
  comes within 2.5 m of it, from either side, and shuts 2 seconds after they have gone. E does
  nothing to it.
- A patio door (each house's garden side): E slides it open and E slides it shut.
- A lift's doors: moved by the lift, never by hand. There are no lifts yet.

A hinged door is pushed from one side and pulled from the other. Room doors open into the room;
exit doors (push-bar doors, the towers' front entrances, the roof and terminal doors) open outward.
A push bar is only on the inside. E says how it opened: "You push the door open", "You pull the
door open", "You push the bar and the stair door swings open", "You unlock the front entrance with
your key and pull it open", "The patio door slides open".

No door moves into a person. A closer waits, an automatic door opens again, and you cannot shut a
door while someone is in the way of it ("Someone is in the way of the door"). If you are standing in
the doorway yourself, you are told to step out of it first. A door pushed open into someone stops
against them. People in the street use doors the same way.

### Lists

| Key | List |
|---|---|
| F5 | Players on the server |
| Shift+F5 | Players on this map |
| F6 | Maps |
| Shift+F6 | Your maps |
| F8 | Friends |
| I | Your inventory |

Inside a list:
- Up and Down move. Enter or Right chooses. Escape, Left or Backspace goes back.
- A letter jumps to the next item starting with that letter.
- You stand still while a list is open. F5, F6 and F8 switch straight to another list.
- Choosing a **player** or **friend** gives: Private message, View profile, and Add or Remove
  friend. Staff also get Where is.
- Choosing a **map** takes you there. A map is read as its name, how many players are on it,
  "private" if it is private, and "you are here" if you are on it.
- The **inventory** reads each thing and where it is: "AKM, 30 rounds, on your back". Choose one
  for what to do with it: take it in your hands or sling it on your back, or drop it. Ten rifles of
  the same name are ten entries.
- When E finds several things on the ground and none in front of you, it opens a **Take** list.

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
  private message to you, three notes up and back down for your team, two falling notes for the
  server, a bright chord for an admin.
- Voice chat switched on: one short note. Switched off: the same note, then a lower one.
- Your shot hits someone: two high notes struck together. Your shot kills them: three quick rising
  notes. Only you hear these.
- Another player online: a chord rolling upward, on a hollow triangle tone. Logged out: the same
  chord rolling downward. Lost connection: a chord stopped short, then two broken stabs. Away: a soft
  chord sinking lower. Back: a soft chord rising.

You can turn these off or change their volume in Settings. "Online and offline sounds" turns off
only the presence sounds; the notices are still read in the All buffer.

## Chat

Press `/` to open the chat line. Type a message and press Enter.

- Plain text goes to everyone on **your map**.
- `/all message` goes to **everyone on the server**.
- `/pm name message` is a **private message**.
- `/t message` goes to **your team**, on any map.
- `/motd` repeats the message of the day. The server sends it to you once when you arrive.

There are four chat buffers: **All**, **Map**, **Private** and **Server**. Team messages go in
Private. Each keeps the last 500 messages. Use `[` and `]` to read messages in the current buffer
and Shift with them to change buffer. Private, team and server messages are always spoken. Other
messages are spoken when you are in their buffer or in All, which is the default.

## Commands

Type these on the chat line. The `/` is optional and case does not matter.

- `/help` (or `/commands`, `/?`) lists the commands you can use. `/help command` says how to type
  one and what it does. `/help settings` lists your own sound settings.
- A mistyped command is answered with the nearest one: "Did you mean /give?"
- A player can be named by the start of their name when only one player's name starts that way.
- `/afk` (or `/away`) marks you away; everyone hears "cody is away". After five minutes of doing
  nothing you are marked away anyway. Anything you do marks you back.

### Around you
- `/scan`: named things within 20 m (the same as Shift+P).
- `/doors`: doors within 20 m, whether each is open or shut, which way and how far.
- `/open [name]`, `/close [name]` (or `/shut`): a door.
- `/knock`: knock on the nearest door (the same as Shift+E).
- `/clap`: clap your hands (the same as T on foot).
- `/room [radius]`: whether the walls around you form a room, and what is missing if not. The
  radius is 12 m unless you give one.

### Vehicles
- `/enter [seat]` (or `/board`, `/getin`): get in. `/exit` (or `/getout`): get out.
- `/seats`: the seats and who is in them.
- `/ignition on|off` (or `/key on|off`): the engine, from the driver's seat.
- `/window [down|up|half]` (or `/windows`): the side windows. On its own it winds them the other way.
- `/horn`: a half-second blast on the horn, from the driver's seat (H held sounds it for as long as you hold it).
- `/siren [on|off|wail|yelp|phaser|hilo|next]`: the siren, on a vehicle that has one (U and Shift+U).
  On its own it switches the siren on or off. Naming a tone switches it on in that tone.
- `/drivecues`: which driving sounds are on. `/drivecues on` or `off` switches all of them (Shift+K);
  `/drivecues guide|lines|clicks|brake|speed [on|off]` switches one. Saved.

### Carrying
- `/take [name]` (or `/get`, `/grab`, `/pickup`).
- `/drop [name|left|right|all]` (or `/putdown`).
- `/stow [name|left|right|all]` (or `/sling`): put on your back.
- `/draw [name]` (or `/equip`, `/wield`, `/unsling`): take into your hands.
- `/hands`: what is in your hands. `/inv` (or `/i`, `/inventory`): everything you carry.
- `/hand [name]` (or `/offer`): give what you hold to the person beside you, if they will take it. On
  the city that is Alex, who asks everybody for something; he keeps it and thanks you.

### Guns
- `/fire` (or `/shoot`): fire the gun in your hands.
- `/reload`, `/ammo`: reload; what is in your gun and the spare rounds you carry.
- `/selector [next|back|safe|semi|auto]`: the fire selector (X and Shift+X).
- `/cease`: let go of the trigger (letting go of Enter sends it).
- `/aimassist [on|off]`: aim assistance from the hip. Saved.
- `/scope [tone on|off]`, `/zoom in|out|N`, `/range`, `/zero [metres]`: the scope (see The scope).
- `/calibre NAME|next|back` (or `/caliber`), `/admingun [report N]`: the admin gun (administrator).

### People
- `/friend add name`, `/friend remove name` (or `/unfriend name`), `/friends`.
- `/profile [name]` (or `/whois`): a player's role, real name, whether they are online, away or
  idle, and which map they are on.
- `/realname [name]`: the real name your profile shows. `/realname clear` removes it.
- `/perms`: what you can use. `/perms all` lists every permission.

### Teams
- `/team`: your team, who leads it, and how many are online.
- `/team create NAME`: start a team; you lead it. Names are 2 to 20 letters, digits, `-` or `_`.
- `/team invite PLAYER`: any member can invite.
- `/team join NAME`: join a team that invited you, or an open one.
- `/team leave`: if the leader leaves, the longest member leads.
- `/team list [NAME]`: members and who is online.
- `/team kick PLAYER`, `/team open`, `/team close`: the leader only.
- `/t MESSAGE` (or `/team chat MESSAGE`): talk to your team.

A team holds up to 16 players. Your team's player beacons sound on a different instrument.

### Maps
- `/join map` (or `/travel`): go to another map. With no map named, it lists the maps you can
  enter: public maps, your own maps, maps you are invited to, and, if you are a moderator or
  developer, any map.
- `/map`: the map you are on, whose it is, and whether it is public.
- `/map new NAME`: make a map of your own, flat ground 100 m square, private, and go there. A name
  is 2 to 24 lower-case letters, digits, `_` or `-`, starting with a letter. Up to 3 each.
- `/map public`, `/map private`: let anybody in, or only you and the people you invite.
- `/map invite NAME`, `/map uninvite NAME`. The person is told.
- `/maps`: the maps you can go to. `/maps mine`: your own, and who is invited.
- `/detail low|medium|high`: how much of a large map is loaded round you. The maps of real places
  (magnolia tx, albany or) are sent in 250 m tiles: everything within 150, 300 or 500 m, and the
  what sound notices from further off (the ground, roads, the outsides of buildings with their front
  doors, woods and trees, fences and hedges) out to 500, 800 or 1,200 m. Tiles load and drop as you
  move.
  Medium is the default. Saved. Other maps are always loaded whole.
- On a map you own you have the building commands: `/spawn`, `/move x y z`, `/savemap`, the
  building verbs and the sound tools (see Staff commands).

### Teleporter
- `/tp PLACE`, `/tp PLAYER`, `/tp MAP`, `/tp x y z` (or `/goto`, `/teleport`): needs a teleporter in
  your hands or on your back. Without one: "You don't have a teleporter."
- A place is a named area or room, on your map or another you may enter. Part of the name is
  enough.
- It has unlimited uses. It charges for two seconds, and people hear you leave and arrive.
- A teleporter is a premium item: only the administrator can give one.

### Building
- `/prefabs`: the objects that can be placed. `/composites`: saved groups of objects.

## Dying

- A player who is killed falls where they were. Their body stays there as an item: "body of NAME".
- Beside it is a bag of everything they carried: "NAME's belongings".
- You are told "You are dead. You come back in 60 seconds", and again at 10 seconds. You come back
  at the map's start with nothing.
- E near someone's belongings takes what is in the bag: into your hands, then onto your back while it
  takes the weight, and spare rounds into your pockets. What will not fit stays in the bag.
- E near a body lifts it over your shoulder. It takes both hands, will not go on your back, and you
  walk at 1 metre a second while you carry it. `/drop` or Q sets it down. A body is an item beacon.
- A pedestrian who is killed leaves a body too, and somebody else comes walking along later.
- A body or bag nobody carries is taken away after 30 minutes. A map keeps at most 30 of each.

## Travelling between maps

Choose a map in the F6 list, or type `/join` and the map name. The client says "Travelling to" the
map, loads it and puts you in. You stay logged in.

## Stairs

Walk up and down stairs with W and S; there are no special keys. In a block of flats the stairs are a
dog-leg: at the top of a flight, turn round and the next flight is beside you. When you reach the foot
or the top of a flight facing along it you hear "Stairs up, 10 steps, to floor 3" or "Stairs down, 10
steps, to floor 2", once, and again only after you walk away and come back. The last flight goes "to
the roof": keep walking at the top, through the steel door, and you are on the roof. A parapet runs
round the edge, so you cannot walk off it.

## Beacons

Beacons are sounds that mark useful things near you.

| Kind | Sound | Heard |
|---|---|---|
| Door | Two soft notes rising (a gentle ding-dong, upward) | The 3 nearest within 12 m |
| Item | One small ring that dies away | The 3 nearest within 10 m |
| Vehicle | A low warm note, twice | The 2 nearest within 25 m |
| Stairs | Four quick rising notes | The way up or down from your floor, within 15 m |
| Player | Two soft notes going down; your team on a hollower instrument | The 4 nearest within 30 m |

- Each keeps sounding from the thing itself, every 1.6 seconds unless you choose otherwise. A door's
  hangs on the door at face height, on your side of it.
- You never hear your own player beacon. A dead player has none; their body is an item.
- A beacon that is mostly hidden behind a wall is not played.
- Maps can also place beacons for exits and waypoints.

Commands (all saved):
- `/beacons` (or `/beacon`): each kind of beacon, whether it is on, and why.
- `/beacons door`: switch door beacons on or off. `/beacons door on` or `off` sets it. The same works
  for `exit`, `stairs`, `item`, `vehicle`, `waypoint` and `player`.
- `/beacons every 3`: the gap between soundings in seconds, from half a second to ten.
- `/beacons louder` and `/beacons quieter`: every beacon 2 dB up or down. They start 4 dB above a
  doorbell's level.

Each map decides, for each kind, whether it starts on or off, is always on, or is not allowed.
Within that, use `/beacons` to choose.

A vehicle's beacon sounds from the side of it facing you. The car you are sitting in has none.
Only parked cars you can get into have a beacon; moving traffic is heard by its engine.

## How loud the world is

Real sounds differ a lot in loudness: a hot rod is about 20 dB louder than an economy car, and
flooring an engine raises it by 15 dB or more. `/levels` sets how much of that difference reaches
you. Higher values let loud things carry further and make quiet things fade sooner. Lower values
squeeze loud and quiet together. It applies to every sound in the game.

- `/levels`: the current setting.
- `/levels default`: 45 percent, the setting the game ships with. Use this.
- `/levels 70`: set it to 70 percent. Any number from 20 to 100. Above the default, quiet sounds
  such as footsteps and an idling car get hard to hear on headphones.

The setting is saved.

The levels are worked out from how loud each sound is to the ear, not from its level in decibels:
an idling engine is mostly deep bass the ear hardly hears, and a beep is quieter to the ear than a
voice of the same level. Each sound also keeps its tone at the level it plays at: a loud car played
quieter than in life gets back the bass and treble a quiet sound loses.

## How loud your headphones are

The game assumes your headphones play a person talking normally an arm's length away about as loud
as a real one. If yours play louder or quieter, tell it once:

- `/listening`: a person talks to you from one step in front, again and again. Set your volume, or
  press Up (louder) and Down (quieter), until they sound like someone talking to you normally at
  arm's length. Shift with Up or Down moves five steps. Space says it again, R goes back to the
  default, Enter saves, Escape cancels. You stand still while it is open.
- `/listening 58`: set it directly, in decibels (40 to 90). `/listening default` is 62.
- `/ear off` and `/ear on`: the ear model off and on, to hear what it does. Not saved.

This changes no volume in the game: your volume is yours. It changes only how much bass and treble
the game gives back to sounds that play quieter or louder than they really are. It is saved.

## Your own sound settings

These change only what you hear. `/help settings` lists them. Each on its own says its current
value.

- `/tail [dB]`: everything traced, against the direct sound: every room's and street's tail and the
  echoes of far sources. Zero is the physical level; the default is -6.
- `/copies [dB]`: every reflection placed as a copy of the sound: the walls' first answers to a clap
  or a step, facade echoes, and the nearest walls and ceiling. Zero is the physical level; the
  default is -6.
- `/reflections [dB]`: sets both. -80 is off.
- `/cabin [dB]`: the inside of the vehicle you are riding in, against its traced level (default 0).
- `/echoes on|off`: the traced echoes of far, loud sources. `/echoes -12` turns them on and sets the
  tail level.
- `/reverb`: how the tracing is doing. The tail is traced from the geometry everywhere.
- `/valveflow on|off`: the rush of gas through each exhaust valve as it opens, on every engine.

`/tail`, `/copies`, `/reflections`, `/cabin`, `/echoes` and `/valveflow` last until you quit.
`/levels`, `/listening`, `/beacons`, `/narrate`, `/bumps`, `/aimassist`, `/detail`, `/track` and `/drivecues` are saved.

`/track KIND` chooses what comma and period step through, like Shift+comma and Shift+period.

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

### Windows
- Press R in a vehicle, or type `/window`, to roll the side windows down; again to roll them up.
  `/window down`, `/window up` and `/window half` also work. Anyone in the vehicle can do it.
- With the windows down you hear the street, the wind and your engine much more, and people outside
  hear you talking much more clearly. With them up, a person talking in a car sounds muffled from
  outside.

### Controls

| Key | Action |
|---|---|
| W or Up | Accelerate |
| S or Down | Brake; when stopped, reverse (up to 8 m/s) |
| A / D, or Left / Right | Steer |
| Space | Brake (with neither W nor S held) |
| T / Shift+T | Engine on / off |
| H, held | Horn, for as long as you hold it |
| U | Siren on / off (vehicles that have one: the police car) |
| Shift+U | Next siren tone: wail, yelp, phaser |
| J / L | Left / right indicator on or off |
| K | Lane assist on / off |
| Shift+K | All driving sounds on / off (the spoken road stays) |
| R | Windows down / up |
| Z | Road, heading, lane, speed and limit, and the junction and crossing ahead |
| Shift+H | Your health |

Let go of the keys to coast.

- **Horn.** Every vehicle has its own: a car's electric horn, an air horn on a truck or a bus.
  Everyone near hears it, from the front of the vehicle. From inside you hear it through the glass.
- **Siren.** It keeps sounding when you get out, until you switch it off. Wail is the long sweep,
  yelp the fast one, phaser the fastest.
- **Indicators.** The relay ticks in the dashboard. The indicator tells the driving sounds which
  way you mean to turn at the next junction. It switches itself off once you have turned through
  45 degrees and straightened up.

### Driving sounds

- **Guide beep** (high): placed on the middle of your lane ahead. Steer towards it. It beeps faster
  the faster you go.
- **Centre line** (middle pitch): beeps from that side as you get within 1.5 m of it, faster as
  you get closer. A steady tone means you are over the line.
- **Kerb** (low, buzzy): the same for the edge of the road. A steady tone means you are over it.
- **Turn clicks**: a click for every 15 degrees the car turns; six clicks make a right angle. A
  rising chime when you are lined up with the road.
- **Guide beep through turns**: on a map with road data the guide beep sits on the line you should
  drive: down your lane, and at a junction round the turn your indicator points to (straight on
  without one). Round a tight turn it sits closer to you.
- **Brake cue**: a short falling note from the direction of whatever you need to slow for: a bend,
  the turn you have indicated, a give-way line, a closed level crossing, the end of the road. It is
  silent while nothing ahead needs more than coasting. As the braking you need grows, the notes come
  faster and higher:
  - slow, low notes: lift off;
  - quicker notes: brake now, as for a junction;
  - fast, high notes: brake hard;
  - a near-continuous run of notes: you are near the limit of the tyres' grip and may not make it.

  It takes the road's wetness into account: on a wet road it starts sooner.
- **Rumble**: with a wheel on the centre line you hear raised markers clacking from that side; with
  a wheel on the kerb-side edge, a rumble strip. Both go faster the faster you drive. Standing still
  on a line you hear the steady tone as before.
- **Speed limit**: two falling notes when you are more than 5 km/h over the limit, and again every
  10 seconds while you stay over.
- **Rails**: crossing a level crossing you hear each tyre strike each rail.

### What is spoken
- The road name and your heading when you join a road.
- "Junction in" some metres, and which ways you can go.
- "Road ends in" some metres.
- "Off the road", then the nearest road and "Follow the beep".
- The speed limit, with the road name, and again when it changes ("Limit 40").
- "Junction in N metres, give way" when your road gives way there, and "Turning right onto ..." when
  your indicator is on.
- "Level crossing in N metres", and "closed. Stop before it." while its bells ring.

**Lane assist** is on when you start. It keeps you in the middle of your lane unless you steer.
Press K to switch it off or on.

## Settings

Choose Settings from the main menu.
- **Output device**: where the sound goes. "System default" uses the system's.
- **Input device, for voice chat**: the microphone voice chat uses.
- **Interface sounds**: on or off.
- **Online and offline sounds**: the presence chords, on or off.
- **Say what is ahead as you turn and move**: the narration N switches.
- **Bump and name what you walk into**: the knock and name when you walk into something.
- **Aim assistance from the hip**: see Guns.
- **Driving sounds**, and one switch each for **Driving: guide beep**, **line beeps and rumble**,
  **turn clicks**, **brake cue** and **speed limit warning**. Shift+K in the driver's seat switches
  them all; `/drivecues` switches one.
- **Interface sound volume, percent**: 0 to 100.
- **Open log folder**: opens the folder of the log file the client is writing (set by
  `run-gtk-client.sh`).
- **Save** and **Cancel**.

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
- The log says at startup whether Steam Audio's scenes use Embree ("Steam Audio scenes use Embree")
  or the default tracer. `OPENFPS_EMBREE=0` before starting the client forces the default tracer,
  which rebuilds the whole scene for every change of tiles or doors: slower, but a way to tell whether
  a fault comes from Embree.

---

# Part 2: Running a server

## Starting and stopping

Run `./run-server.sh` from the OpenFPS folder. It builds the server and starts it.

- `./run-server.sh` lands players on the speedway.
- `./run-server.sh city` lands players on the city. Any map name works the same way.

Every map in the maps folder is loaded either way; the name only chooses where players arrive.

Other options go straight to the server:
- `--port N`: the game port (UDP). Default 33288.
- `--map name`: the same as naming the map.

The server is ready when it prints:
- `MapManager: N map(s) loaded; players will land on 'name'`
- `MUD Gateway listening on TCP port 33289`
- `NetworkService started on port 33288`

Stop it with Ctrl-C. It tells every player the server is shutting down, stores what each player is
carrying, closes its connections, and prints "Server stopped cleanly".

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

### Client and server builds must match

A client logs in only to a server built from the same `OpenFPS.Common`. The build is a 12-character
hash; a client that does not match is refused with both hashes in the message. After updating the
server, give players a new client.

- `./publish-windows.sh` makes `dist/OpenFPS-windows-<build>.zip`, with `BUILD.txt` and this
  folder's `WINDOWS_README.txt` inside.
- `./publish-server.sh` makes `dist/openfps-server-linux-x64-<build>.tar.gz`. It leaves out
  `openfps.db`, `friends.json` and the players' own maps, so unpacking an update keeps them.
- Setting up a server on a VPS: [WINDOWS_AND_SERVER.md](WINDOWS_AND_SERVER.md).

## Maps

Maps are JSON files in `OpenFPS.Server/maps/`. Every `.json` file there, and in `maps/players/`,
is loaded at start.

| Map | What it is |
|---|---|
| `speedway` | A 1.25-mile banked oval with named zones and a race. Players land here by default. |
| `city` | Towers, avenues, houses with gardens, a parking garage, a plaza, a tunnel, a light rail loop with three stations, an airport, Elm Park with its fountain, traffic and aircraft. |
| `default` | Rooms, doors and materials to try, with a car driving past the start. |

- The map with `"IsDefault": true` is where players arrive. If more than one says so, the first
  wins and the server warns you.
- Files may contain comments and trailing commas.
- A file that cannot be read is skipped with an error. Unknown fields are reported and ignored.
- If the folder has no maps, the server makes an empty one called `default`.
- Each map can be public or private and can have an owner. Players can enter public maps, their
  own, and maps they are invited to. Moderators, developers and the administrator can enter any
  map. All shipped maps are public.

- A map with `"TileMetres"` above 0 (the real places, made by tools/gen_osm.py) is streamed: each
  client is sent the tiles near it and the rest as it moves (docs/WORLD_STREAMING.md). The server
  log says what each join and each set of tiles cost ("Join of ...", "Tiles for ...").

### Players' own maps

- Any player can make up to 3 maps with `/map new NAME`; the server holds 100 player maps in all.
- They are kept in `OpenFPS.Server/maps/players/`. `/savemap` on one writes it there.
- Who owns each map, whether it is public, and who is invited are kept in `map_access.json`.

### The city

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
- `BeaconPolicy`: for each beacon kind (door, exit, stairs, item, vehicle, waypoint, player), one of
  `default_on`, `default_off`, `forced_on` or `forbidden`. A kind not listed is on.
- `Entities`: the objects, each with `PrefabId`, `Position`, `Rotation`, `Scale`, `Name`, and room
  and material settings.
- `Vehicles`, `Trains`, `Tracks` (routes with waypoints, width, banking and stops), `Crossings`.
  A crossing is a point on a rail line; the server works out the rest. Each road through it gets a
  gate on each approach (on the right of the traffic, 3.7 m before the nearest rail); the gates go
  down 4 s after the bells start, in 12 s, and rise in 9 s once the train has cleared.
- An aircraft vehicle whose road comes down from the air to within 5 m of the ground is an
  approach: it lands there, rolls out down the runway on its type's landing roll, turns round,
  waits `WaitSeconds`, and takes off back up the same line. The runway must be long enough for the
  type: about 300 m for the light single, 1,000 m for the turboprop, 1,900 m for the airliner. A
  helicopter, and an aircraft flying level, turn round at the end of their line as before.
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
| `maps/players/*.json` | Maps players made with `/map new`. |
| `motd.txt` | The message of the day. |
| `openfps.db` | Accounts (SQLite), with each player's place, health, belongings and single permissions. |
| `friends.json` | Friends lists. |
| `teams.json` | Teams. |
| `roles.json` | Custom roles. |
| `map_access.json` | Map owners, public or private, and invitations. |
| `logs/` | Server logs, one file per day. |

### Pedestrian voices

The recorded lines are in `OpenFPS.Client/ASSETS/SOUNDS/VOICES/<voice>/`, and the list the server
picks from is `OpenFPS.Common/Speech/voices.csv`. Both are written by the importer from a folder with
a `manifest.csv`:

    tools/import_npc_voices.py inbox/npc-voices-2026-09-26 --clones=seanterry,jimdale,joeb,joel,tim,ben,alec,fluke,camel,tyler

It encodes the lines to Ogg Vorbis. Voices cloned from real people are left out unless named with
`--clones`. A voice whose folder is removed from the inbox is removed from the game. The recordings
in a `<voice>_preview` folder become that voice's stories, with their words from `stories.json`.
Rebuild the server and client afterwards, since the list is built into both.

## Accounts

- Accounts are stored in `openfps.db`, created on first start if it is missing.
- If there are no accounts, the server creates `admin` with the password `admin123`. **Change this
  before letting anyone else connect.** Start the server once with `OPENFPS_ADMIN_PASSWORD` set:
  on a new database that is the admin's password, and on an existing one it resets the admin's
  password. After that you can leave it unset; the password stays. The server warns at every start
  while the password is still `admin123`.
- Anyone can register; new accounts are players.
- User names are stored in lower case. A name that is taken or reserved is refused.
- Logins and registrations are limited per address: 6 quick tries, then one every 5 seconds. New
  accounts: 3, then one every 20 minutes.
- Ten wrong passwords in a row lock a name for 15 minutes. The address that last logged in to it
  successfully is not locked out.
- An account can be logged in once at a time.

### Roles

There are four roles:
- **Player**: the game, and building on maps of their own.
- **Moderator**: looks after people, never the world.
- **Dev** (developer): builds and tests the world, on any map. No power over people.
- **Admin** (administrator): everything.

On a map you own, every player has the building commands. The administrator changes roles with
`/setrole NAME player|moderator|dev|admin`, and can make custom roles with `/role`. The full table of
permissions is in [SERVER_SECURITY.md](SERVER_SECURITY.md).

## The message of the day

Put it in `OpenFPS.Server/motd.txt`. Each player hears it once when they arrive. Players can hear
it again with `/motd`. Moderators can change it with `/setmotd text`; `/setmotd` on its own clears
it. Changes take effect straight away.

## Staff commands

A player who tries a command without the permission gets "You do not have permission to execute
this command." `/help` lists only the commands you may use.

### Moderators (and the administrator)
- `/announce message`: a message to everyone, from "Server".
- `/setmotd [text]`.
- `/where NAME` (or `/locate`): which way and how far, if they are on your map; which map otherwise.
  `/where alex` finds Alex, says where he is (x east, y north, height) and what he is doing.
- `/bring NAME`: bring a player to you.
- `/kick NAME [reason]`: disconnect a player.
- `/mute NAME [minutes]`: stop a player chatting, 10 minutes if not said. `/unmute NAME`.
- Go to any map, private or not (developers too).

The administrator cannot be kicked or muted by a moderator.

### Developers (and the administrator), on any map; everyone on maps they own

Moving:
- `/move x y z`: go to a point. x is east, y is north, z is height. It refuses a point inside
  something solid.
- `/move NAME`: go to a player (developers only, not map owners).

Spawning:
- `/spawn walker [NAME]` (or `person`, `pedestrian`): a person who walks back and forth in front of
  you. `/savemap` keeps them.
- `/spawn vehicle PRESET` (or `car`), `/spawn helicopter`, `/spawn aircraft PRESET`: parked beside
  you. On your own map it is yours. Aircraft cannot be flown yet. No airliner.
- `/spawn train PRESET`: onto the nearest track that already has a train.
- `/spawn Box|Cylinder MATERIAL sx sy sz`: an object 3 m in front of you.

Building:
- `/place id [yaw]`: place a composite. It lasts until the server stops unless you save the map.
- `/origin`, `/at ...`, `/put prefab [turn N] [run N [dir]]`, `/undo`: place prefabs in rows.
- `/group name [radius=12] [free]`, `/ungroup`, `/saveas id`: make a composite from what is around
  you and save it to `composites/`.
- `/addseat name [drive]`, `/removeseat name`, `/drivable preset`: make something into a vehicle you
  can sit in or drive.
- `/savemap`: write the current map back to its JSON file. This removes any comments in the file,
  so keep a copy of a hand-written map first.
- Changing what somebody else built needs `edit-any` (developers), or owning the map.

Sound on the nearest object:
- `/set_sound SoundId [Volume]`
- `/set_audio_mode LoopOne|LoopFolder|OneShot|StateMachine`
- `/play_folder FolderId`
- `/start_state StartId LoopId`

### Developers only (and the administrator)
- `/give [NAME] ITEM [COUNT]`: ordinary items, up to 50 at once. They go into the hands, then onto
  the back, then at the feet. Items: the AKM, Glock 17, M700, crowbar, sword and torch.
- `/give [NAME] [ammo] KIND [COUNT]`: spare rounds: 9mm, .45, .357, 5.56, 7.62x39, .308, 12 gauge.
- `/fire WEAPON`: fire a named weapon from where you stand, with empty hands.
- `/grant NAME PERMISSION`, `/revoke NAME PERMISSION`: only to players, and only permissions you
  have yourself.
- `/perms NAME`: a player's permissions.
- `/weather`: the weather and the wind now. `/weather clear`, `rain`, `snow` or `storm` sets that
  weather; `/weather wind 8 north west gusty` sets the wind (metres a second, where it blows from,
  and steady, gusty or very gusty, the last two optional). What you set reaches everybody in a few
  seconds and stays until `/weather auto`, which lets the weather change on its own again.

### The administrator only
- `/setrole NAME ROLE`: player, moderator, dev, admin, or a custom role.
- `/role list|create|add|remove|show|delete ...`: custom roles, kept in `roles.json`.
- `/grant` and `/revoke` of any permission, for anybody.
- `/give [NAME] teleporter`, `/give [NAME] vehicle PRESET` (parks one beside them, theirs): premium
  items.
- The admin gun: `/give admin_gun`, then its keys and commands (see Guns). Nobody else can hold it.
- `/tp` without a teleporter.
- `/move NAME x y z`, `/move NAME to OTHER`: move somebody else.
- `/map public|private|invite|uninvite` on a map that is not yours.
- `/sessions`: who is connected, from where.
- `/user NAME` (or `/account`): an account's details.
- `/throttled` (or `/ratelimit`): addresses being held back. `/unlock NAME|ADDRESS`: lift a lock.

## The text (MUD) interface

The server also listens for plain text connections on the game port plus one (33289 by default),
for testing a server without the game client. Connect with a tool such as `telnet` or `nc`.

- `login name password`
- `say text`, `who`, `who_map`, `maps`, `mymaps`, `friends`
- Anything else is treated as a command from the lists above. The scope works only in the game
  client.

You cannot register through it. It is plain, unencrypted text on every network interface, so do
not expose it on a public network.

## Logs

- The server logs to the terminal and to `OpenFPS.Server/logs/server` plus the date `.log`.
- If the server fails to start it prints `[FATAL ERROR] Server failed to start:` and the reason.
- Admin gun vaporizations, kicks and mutes are logged with who did them.

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
| `OPENFPS_LEVEL_COMPRESSION=0.45` | Sets `/levels` for this run only (0.2 to 1); it is not saved |
| `OPENFPS_TAIL_DB=-6`, `OPENFPS_COPIES_DB=-6` | Start `/tail` and `/copies` at these levels |
| `OPENFPS_ECHOES=off` | Start with the traced echoes off |
| `OPENFPS_EAR_WIND=0` | No wind at your ears (for listening without it) |
| `OPENFPS_EAR_MODEL=0` | Start with the ear model off (`/ear on` turns it on) |
| `OPENFPS_LISTENING_LEVEL=58` | Sets `/listening` for this run only (40 to 90); it is not saved |
| `OPENFPS_CRASHDIR=folder` | `run-gtk-client.sh` only: where crash dumps go |

The rest of the client's switches are for tracking down audio faults; they are listed in
`OpenFPS.Client.Core/DiagnosticSwitches.cs`.
