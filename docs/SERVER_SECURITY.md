# Server security

Roles, logins, limits, what the server records about connections and accounts, and the admin
commands that show it. For running a server on a VPS, see `WINDOWS_AND_SERVER.md`.

## Roles

There are three roles: Player, Dev (developer) and Admin. A new account is a Player. An Admin
changes a role with `/setrole`. The seeded `admin` account is an Admin.

Dev and Admin build the world. Only Admin sees addresses and changes accounts.

| Command | Player | Dev | Admin |
| --- | --- | --- | --- |
| Moving, looking, chat (`/all`, `/pm`), `/motd`, `/scan`, `/room`, `/prefabs`, `/composites` | yes | yes | yes |
| Doors, seats, driving, carrying, `/clap`, `/knock` | yes | yes | yes |
| `/fire` with a weapon in your hands | yes | yes | yes |
| `/friend`, `/friends`, `/profile`, `/join` to a public map or your own, `/afk`, `/realname` | yes | yes | yes |
| `/fire <weapon>` with empty hands | no | yes | yes |
| `/join` to somebody else's private map | no | yes | yes |
| `/where` (`/locate`) | no | yes | yes |
| `/setmotd`, `/announce` | no | yes | yes |
| `/tp` (`/move`), `/spawn` | no | yes | yes |
| `/set_sound`, `/set_audio_mode`, `/play_folder`, `/start_state` | no | yes | yes |
| `/group`, `/ungroup`, `/saveas`, `/place`, `/addseat`, `/removeseat`, `/drivable` | no | yes | yes |
| `/origin`, `/at`, `/put`, `/undo`, `/savemap` | no | yes | yes |
| `/sessions`, `/user` (`/account`), `/throttled` (`/ratelimit`), `/unlock`, `/setrole` | no | no | yes |

`StaffGateTests` runs every gated command as a Player and checks it is refused and changes nothing.
It reads `CommandHandler.cs` and fails if a gated command is missing from its list.

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
- Admin actions: `/unlock` and `/setrole`.
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
- `/setrole <name> player|dev|admin`: changes a role. An online player gets the new role at once.
  You cannot change your own.

## Upgrading an older accounts database

The first start of a server from 2026-10-02 or later on an older `openfps.db`:

1. copies it to `openfps.db.before-<UTC date and time>` beside it;
2. adds the new columns to `Users`. Every account, password and role is kept.

Nothing else is needed. Delete the copy once the server is running normally.

## Not done yet

- No command to change a password; `OPENFPS_ADMIN_PASSWORD` resets only `admin`.
- No kick or ban.
- The MUD port has no encryption.
- LiteNetLib puts a fragmented message back together before the 16 KB check sees it, so one
  connection can still make the server hold a large message for a moment. The per-address
  connection limit bounds how many.
