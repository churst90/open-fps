# Server security

Roles, logins, limits, what the server records about connections and accounts, and the admin
commands that show it. For running a server on a VPS, see `WINDOWS_AND_SERVER.md`.

## Roles

There are four roles: Player, Moderator, Dev (developer) and Admin. A new account is a Player. Only
an Admin changes a role (`/setrole NAME player|moderator|dev|admin`). The seeded `admin` account is
an Admin.

- **Player**: the game, and building on maps of their own.
- **Moderator**: looks after people, never the world. Announcements, where somebody is, bringing
  them, kicking and muting, joining private maps to answer a report. Cannot build anywhere but their
  own maps, see addresses or change accounts.
- **Dev**: builds and tests the world, on any map. Spawning, firing any weapon, giving ordinary
  items, joining private maps, and granting a player permissions the developer holds. No power over
  people.
- **Admin**: everything, and alone changes roles, makes custom roles, grants anything to anybody,
  gives premium items (the teleporter, vehicles), moves other players, and sees and changes accounts
  and addresses.

A permission is a command's name, or one of the powers at the end of the table. Roles are sets of
permissions, in `OpenFPS.Server/Core/Permissions.cs`. The agreed table is
`docs/PLAN_2026-10-05.md` section 1.

**Scope.** Building, `/spawn`, `/move` yourself, `/savemap` and the sound tools are allowed to
everybody on a map they own. Holding the permission is what allows them on any map. A map owner may
also change what other people built on it (as `edit-any` does everywhere).

**Maps of your own.** `/map new NAME` makes a private map (flat ground, 100 m square) owned by you
and takes you there; at most 3 per person, 100 player maps per server. `/map public`, `/map private`,
`/map invite NAME`, `/map uninvite NAME` act on the map you are on (or name one after). `/maps` lists
the maps you can go to, `/maps mine` your own. Player maps are files in `maps/players/` (not in git,
not in the server package); owner, public flag and invitations are in `map_access.json` beside
`teams.json`. A private map lets in its owner, the people invited, and `join-private`.

**The world editor.** F12 and `/edit` (docs/WORLD_EDITOR.md) need `edit`: Dev and Admin on any map,
everybody on a map they own, and the people a map's owner names with `/map editor add NAME` on that map
only. Naming an editor also invites them; it gives them `edit` there and nothing else (not `/spawn`,
not `/savemap`). Editors are kept in `map_access.json` with the invitations. Changing a model in the
library changes it on every map, so it needs `edit-models` (Dev and Admin).

**Custom roles.** An Admin can make roles of their own, each a name and a set of permissions on top
of Player: `/role create builder move spawn put`, `/role add builder savemap`, `/role remove builder
put`, `/role show builder`, `/role list`, `/role delete builder` (its holders become players). Give
one with `/setrole sean builder`. Custom roles are kept in `roles.json` beside `friends.json`.

Whoever's role changes is told who changed it and what they can now use: "cody made you a builder.
You can now use: move, put, spawn."

**Single permissions.** `/grant sean give`, `/revoke sean give`. Grants are kept with the account.
An Admin (`grant-any`) grants anything to anybody. A Dev grants and revokes only permissions they
hold, only for players, and never for somebody whose custom role can do something the Dev cannot.
`grant`, `revoke`, `setrole`, `role` and `grant-any` cannot be granted. `/perms` lists your own,
`/perms NAME` (`perms-any`) someone else's, `/perms all` every permission.

| Permission | Player | Moderator | Dev | Admin |
| --- | --- | --- | --- | --- |
| Moving, looking, chat (`/all`, `/pm`), `/motd`, `/scan`, `/room`, `/prefabs`, `/composites`, `/perms` | yes | yes | yes | yes |
| Doors, seats, driving, carrying, `/clap`, `/knock`, `/fire` with a weapon in your hands | yes | yes | yes | yes |
| `/friend`, `/friends`, `/profile`, `/join` to a public map, your own or one you are invited to, `/afk`, `/realname` | yes | yes | yes | yes |
| `/map`, `/maps`: make a map, open or close it, invite people | yes | yes | yes | yes |
| `/tp` to a place, player, map or point with a teleporter in your hands or on your back | yes | yes | yes | yes |
| Building, `spawn`, `move`, `savemap`, sound tools on maps you own | yes | yes | yes | yes |
| `announce`, `setmotd`, `where` (`/locate`) | no | yes | no | yes |
| `bring` (a player to you), `kick`, `mute` (`/mute NAME [minutes]`, 10 by default), `unmute` | no | yes | no | yes |
| `join-private`: `/join` somebody else's private map | no | yes | yes | yes |
| `give` ordinary items (`/give [NAME] ITEM [COUNT]`: "You gave sean 1 AKM.", and sean hears "cody gave you 1 AKM.") | no | no | yes | yes |
| `fire-any`: `/fire <weapon>` with empty hands | no | no | yes | yes |
| `spawn` (`/spawn walker`, `vehicle`, `train`, `Box`), `set_sound`, `set_audio_mode`, `play_folder`, `start_state`, on any map | no | no | yes | yes |
| `group`, `ungroup`, `saveas`, `place`, `addseat`, `removeseat`, `drivable`, on any map | no | no | yes | yes |
| `origin`, `at`, `put`, `undo`, `savemap`, `move` (`/move x y z`, `/move NAME`), on any map | no | no | yes | yes |
| `weather`: `/weather` says and sets the weather and the wind for the whole server | no | no | yes | yes |
| `edit-any`: change things other people built | no | no | yes | yes |
| `edit`: the world editor (F12, `/edit`) on any map; everybody on maps they own or are editors of | no | no | yes | yes |
| `edit-models`: change a model in the library (`/edit model set`), which changes it on every map | no | no | yes | yes |
| `grant`, `revoke`: only what you hold, only for players | no | no | yes | yes |
| `perms-any`: `/perms NAME` | no | no | yes | yes |
| `give-premium`: give a premium item (a prefab with `"Premium": true`, today the teleporter), `/give NAME vehicle PRESET` | no | no | no | yes |
| `admin-gun`: hold, fire and set the admin gun (`/admingun`, `/calibre`); nobody without it can be given it or pick it up | no | no | no | yes |
| `tp-free`: `/tp` without a teleporter | no | no | no | yes |
| `move-player`: `/move NAME x y z`, `/move NAME to OTHER` | no | no | no | yes |
| `grant-any`: grant and revoke anything for anybody | no | no | no | yes |
| `protected`: kicked or muted only by somebody who has it too | no | no | no | yes |
| `maps-any`: `/map public`, `private`, `invite` on a map that is not yours | no | no | no | yes |
| `sessions`, `user` (`/account`), `throttled` (`/ratelimit`), `unlock` | no | no | no | yes |
| `setrole`, `role` (custom roles) | no | no | no | yes |

A mute lasts for the session or until it runs out.

`StaffGateTests` runs every gated command as a Player and checks it is refused and changes nothing,
then as a role that has it and checks it does something. It fails if the permission table and its
list disagree. It also checks that Moderators cannot build, Devs cannot moderate, grants work for one
account until revoked, and only an Admin moves another player. `RolesScopeTests` covers the scope,
the granting ceiling, maps, the teleporter, premium items and spawning.

`/profile` shows a player's role, the real name they set with `/realname`, whether they are online,
away (`/afk`) or idle (5 minutes or more without doing anything), and which map they are on. A
private map the asker could not enter is called "a private map". It never shows coordinates,
direction or distance; that is `/where`.

## Accounts

- Usernames: 3 to 20 characters, letters, digits, `_` and `-`, starting with a letter or digit.
  Stored in lower case, so `Cody` and `cody` are one account. Accounts made before 2026-10-02 keep
  their names, whatever they are.
- Reserved, cannot be registered: server, system, admin, administrator, moderator, mod, staff, dev,
  developer, owner, openfps, everyone, everybody, all, you, me, nobody, someone, player.
- Passwords: at least 8 characters, at most 72 bytes (bcrypt ignores the rest), no control
  characters, not the same as the username. Older passwords are not re-checked.
- Passwords are stored as bcrypt hashes with cost 12 and a random salt per password. The check is
  constant time. A hash made at a lower cost is rehashed at cost 12 the next time its owner logs in.
- Password checks run on the thread pool, not the game loop. At most 8 run at once; past that a
  login is told "The server is busy".

## Logging in

- A wrong password and an unknown name get the same answer, "Invalid Credentials". An unknown name
  costs a bcrypt check too, so the time taken does not tell them apart.
- Registration does say "That username is already taken". Its own limit (below) keeps this from
  being a way to list accounts.
- One session per account. Logging in again from somewhere else closes the old session and tells it
  why. An agent walking a live server over the MUD with an account somebody is playing on will close
  their session; use another account.
- A second login on a connection that is already logged in is refused.
- Before login, a connection may send only a login or a registration. Anything else is dropped; a
  typed command gets "You are not logged in".

## Limits

Per address. An IPv6 address counts by its /64, so one host cannot rotate addresses.

- Logins and registrations together: 6 at once, then one every 5 seconds.
- New accounts: 3, then one every 20 minutes.
- Lockout: 10 wrong passwords in a row for one name lock that name for 15 minutes. This counts
  names that do not exist too, so a lock says nothing about whether an account exists.
  - The address that last logged in to the account successfully is not locked out.
  - A successful login clears the count.
  - Locks are kept in memory and are lost on restart.
- The rate-limit table holds at most 10,000 addresses. Full addresses are pruned every minute.
  When the table is full of busy addresses, new addresses wait.

Game port (UDP 33288):

- 4 connections per address, 200 in all.
- New connections: 10 at once per address, then one every 2 seconds.
- A message over 16 KB is dropped. A voice packet over 4,000 bytes is dropped.
- A message in more than 64 fragments is dropped, and one sender may have at most 8 messages half
  arrived (FragmentGuard, before LiteNetLib holds any of it).
- 600 messages at once per connection, then 400 a second; the excess is dropped.
- Only the messages a client sends are read. Each is checked against its own layout first
  (WireCheck): a string that says it is longer than the message is refused.

Per account (per connection before login), so reconnecting does not refill them
(`MessageLimits`; see "Hardening, 2026-10-07" below):

- Chat (plain typing, `/all`, `/pm`, `/t`): 6 lines at once, then one every 2 seconds. "You are
  chatting too fast."
- Commands, including every world editor menu choice: 20 at once, then 5 a second.
- World editor changes: 20 at once, then 4 a second. Looking costs nothing (menus, select, find,
  settings); a change costs 1; a row 1 plus a tenth of its length; placing a group or replacing a model
  on one map 5; replacing everywhere 10. "Too many edits at once."
- `/join`: 3 at once, then one every 10 seconds.
- Voice: 100 packets at once, then 60 a second. Dropped without a word.
- Lists (F5, F6, F8, I), the interact key and scoped shots: 10 at once, then 4 a second. Dropped.
- The map's data is sent once per manifest.
- Chat is at most 512 characters; a command at most 1,024 characters and 64 words. Both lose control
  characters and the marks that reverse reading order.
- Each limit that refuses something is logged once a minute per account, with how many times since.
- A connection that has not logged in within 2 minutes is closed.
- Voice is sent on as coming from the sender's own body, whatever the packet says.
- Map data is sent only for the map the player is on.

MUD port (TCP 33289):

- 4 connections per address, 64 in all.
- Lines over 512 characters are discarded as they arrive.
- 5 commands a second per connection.
- A client that stops reading is closed after 256 queued lines.
- A connection that has not logged in within 2 minutes is closed.
- Passwords cross the network in plain text. Block this port in the firewall unless you need it.

## What is recorded

### The log

`logs/server.log` in the server's folder, a new file each day, the last 31 kept (Serilog's
default). The same lines go to the console, which is `journalctl -u openfps` under systemd.

- Connections: UDP peer connected and disconnected (with the reason), MUD connection opened (with
  the address) and closed.
- Refused connections, with the address and the reason, at most once a minute per address.
- Logins: success (user, UDP or MUD, address, map), rejection (name, address), refusal for the
  rate limit or a lock, and each lock as it starts.
- Accounts created, with the address. Registrations refused for the new-account limit.
- Sessions closed by the server: taken over by a new login, or no login within 2 minutes.
- Admin actions: `/unlock`, `/setrole`, `/grant`, `/revoke`, `/role create`, admin gun mode changes.
- Moderation: `/kick` (with the reason) and `/mute` (with the minutes).
- Chat sent with `/all` or typed on a map, with the sender's name. Private messages are not logged.

Passwords are never logged. A MUD login line that causes an error is logged as
`login <name> [password]`. Names and other text from the network have control characters replaced
and are cut at 64 characters.

### The accounts database

`openfps.db` in the server's folder, table `Users`. Per account:

- username, bcrypt hash, role;
- when it was created (empty for accounts made before 2026-10-02);
- last successful login: time and address;
- wrong passwords since that login, and the time and address of the last one;
- the real name the player set, if any.

### Only in memory

Lost on restart:

- each session's address, login time, last activity and away flag;
- the rate-limit counts;
- the name locks and the failures leading up to them.

## Admin commands

- `/sessions`: everyone connected. Name, role, UDP or MUD, address, map, whether they are in the
  world yet, when they logged in, how long idle (or away), ping for UDP, connection id. Then any
  connections that have not logged in, with transport, address and how long they have been open.
- `/user <name>`: role, when created, last login and from where, wrong passwords since and the last
  one, the name's lock if it has one, whether online and from where, real name. For a name with no
  account it still says how many failed logins there have been.
- `/throttled`: addresses over the login limit or the new-account limit, with the wait, and names
  that are locked, with the time left.
- `/unlock <name>`: lifts a name's lock and forgets its failures. `/unlock <address>` gives an
  address its limits back.
- `/setrole <name> player|moderator|dev|admin|<custom role>`: changes a role. An online player gets
  the new role at once. You cannot change your own.

## Upgrading an older accounts database

The first start of a server from 2026-10-02 or later on an older `openfps.db`:

1. copies it to `openfps.db.before-<UTC date and time>` beside it;
2. adds the new columns to `Users`. Every account, password and role is kept.

Nothing else is needed. Delete the copy once the server is running normally.

## Not done yet

- No command to change a password; `OPENFPS_ADMIN_PASSWORD` resets only `admin`.
- No ban. `/kick` disconnects, but the player can log straight back in.
- The MUD port has no encryption.
- LiteNetLib still holds the fragments of a message until it is whole: at most 8 messages of 64
  fragments per sender, about 700 KB, until the connection closes.
- Clients read the server's messages with the same MemoryPack, unchecked: a hostile server could crash
  a client. The server is trusted.
- Chat typed on the MUD (`say`) is handled on the gateway's thread, not the tick thread.
- model_versions/ keeps every version of every model in full and rewrites a model's file on each
  change, on the tick thread. Only edit-models can add versions. See decision 3 below.

## Hardening, 2026-10-07

An audit of what a modified client could do now that players change the live world, and the fixes.
Every fix has a test in `OpenFPS.Tests/ServerHardeningTests.cs` that failed before it (41 of the first
57 failed; the crash took the test host down). Also checked live through the MUD on a scratch server.

### Findings, worst first

Critical:

1. **One packet crashed the server, before login.** MemoryPack 1.21.4 reads a UTF-8 string by asking
   for `~header + 4` bytes. A header of `int.MinValue` to `int.MinValue + 3` overflows that to a
   negative size, its bounds check passes, and it reads 2 GB past the buffer: an
   AccessViolationException, which no `catch` stops. Five bytes in a LoginRequest ended the process.
   Not fixed upstream. Fixed: `WireCheck` walks every string of a client message before MemoryPack
   reads it, from the message types' own fields.
2. **`/saveas NAME` wrote anywhere.** The name was the file name as typed. Any player, on a map of their
   own, could write `composites/../roles.json`, `../map_access.json`, a map, a prefab, or any absolute
   path the server could write, as long as it ended `.json`. Fixed: a design's name is letters, digits,
   `_` and `-`, checked in the command, in CompositeService and in CompositeRepository.
3. **LiteNetLib held fragments without limit.** For each new fragment id it allocates an array of the
   claimed part count (up to 65,535) and keeps it until the message is whole or the connection closes:
   512 KB held per 1.4 KB packet, 65,536 ids per peer, for anybody connected. Fixed: `FragmentGuard`, a
   LiteNetLib packet layer, drops a fragment claiming more than 64 parts, a sender's ninth half-arrived
   message, and a merged packet whose parts run past the datagram.

High:

4. **A map's owner could point visitors' clients at any file.** `/set_sound`, `/play_folder` and
   `/start_state` (everybody's on their own map) took any text, and every client on the map opens a
   sound id as a file. `\\host\share\ASSETS\x` makes a Windows client connect to that host and send its
   login hash; `../` and absolute paths read outside the game. Volumes of NaN and 1e30 went to every
   visitor's mixer. Fixed: a sound id is a relative name under the sounds folder or a model id; a volume
   is 0 to 4. The client also refuses any sound path outside ASSETS.
5. **Premium items and things to carry could be made with the editor.** Only Place checked them.
   Duplicate, Row, placing a group and replacing a prefab did not: a teleporter or a rifle on an owner's
   map could be copied 5,000 times, and walls could be replaced with teleporters. Fixed.
6. **No limits on what a logged-in client sends.** Chat (to everybody, read aloud), commands, editor
   operations, voice (relayed to everybody on the map), list requests and the interact key could be sent
   as fast as the network allowed, each costing the tick thread. Fixed: the limits above, per account.
7. **Asking for the map's data again sent the whole map again.** A 20-byte request, repeated, made the
   server send the city's tiles over and over. Fixed: once per manifest.
8. **Editor batches were slow on the tick thread.** A row of 50 wrote the overlay file 50 times; a
   replacement on a full map, thousands of times; undoing a row refiled the map 50 times (the live check
   dropped 5 ticks). Fixed: one write and one refile per `/edit`.

Medium:

9. **The 5,000 cap could be got round.** Undoing a deletion and redoing a placing were not counted: with
   a second editor placing in between, an owner's map grew without end. Replacing map-file things made
   additions uncounted too. Fixed.
10. **Chat and commands had no length or character rules over UDP.** An ANSI escape in chat reached
    telnet players' terminals; a newline forged a line in the chat log. Fixed.
11. **The server read its own message types if a client sent them.** ServerStateUpdate unpacks its bytes
    as it is read. Fixed: only the 14 types a client sends are read, by their union tag.
12. **No cap on messages per connection.** The incoming queue had no bound. Fixed.
13. **Things could be moved anywhere.** 1,000 m per move, repeated without end; the held list and a
    group's parts had no limit. Fixed: 20 km from the map's middle; 500 held, 500 parts.
14. **Logs per packet.** The scoped-aim warning was one line per shot; a MUD registration that failed to
    parse was logged with its password. Fixed: once a minute per player; the password is not logged.

Low:

15. Model and group ids accepted a trailing newline (`$` matches before one). Fixed (`\z`).

### Checked and sound

- Every `/edit`, typed or from the menu, goes through one permission check on the tick thread
  (`edit`: the role, the owner, or a named editor). Model changes check `edit-models` in the command
  and again in undo and redo. Pins need `edit` on that map; replacing everywhere and making groups need
  `edit-models`. Model, group and map ids from players are looked up, never used as paths, except new
  model ids, which are file names (checked).
- Field values are checked against their `[Tunable]` ranges on the server; text fields have a length and
  no control characters.
- Input is finite, clamped and queue-bounded. Interact targets and boarding are on your own map and
  within reach. Voice is sent on as the sender's own. Map data is only for your own map.
- bcrypt at cost 12, constant time, a dummy hash for unknown names; logins off the tick thread, 8 at
  once; per-address and per-name limits as above. Passwords are not logged. One session per account.

### Wire

No change: no new message, no field added. A client from before talks to a server from after. The
client's sound path check is in the client only.

### Decisions for Cody

1. **The limits in play.** Chat 6 then one every 2 s; commands 20 then 5 a second; editor changes 20
   then 4 a second; `/join` 3 then one every 10 s. Recommendation: keep, and raise any one he or a
   friend runs into while playing honestly.
2. **Line lengths.** Chat 512 characters, a command 1,024. Recommendation: keep (the MUD's line is 512).
3. **model_versions.** Every change keeps the whole model again and rewrites the model's file on the tick
   thread; a long tuning session on one engine is hundreds of versions and a file of megabytes.
   Recommendation: no limit now (only developers can add versions); if a file passes 1,000 versions,
   write model files off the tick thread and cap versions per model then.

