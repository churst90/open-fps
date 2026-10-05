# Windows client and a public server

How to give someone the Windows client and run a server they can reach.

## Client and server must be the same build

Messages and components serialise positionally, so a client and a server built from different
versions of `OpenFPS.Common` misread each other with no error. The build hashes the sources of
`OpenFPS.Common` into `WireContract.Hash`. The client sends it with its login, and the server
refuses a mismatch by name: "This client does not match the server. Your build is X, the
server's is Y."

The hash is made at build time by an MSBuild task in `OpenFPS.Common.csproj` (generated file
`WireContract.g.cs`). It covers `OpenFPS.Common` only. A change to the client or the server alone does not
change it; a change to anything in Common does. Both publish scripts put the hash in their file
names and in `BUILD.txt`.

The Linux client sends it too, so a GTK client left running from before a Common change is
refused as well. Restart it.

## The Windows zip

```
./publish-windows.sh
```

Builds `dist/OpenFPS-windows-<build>.zip`: Release, self-contained (no .NET install needed),
with FMOD 2.03.09, Steam Audio 4.8.1 and NVDA's controller client from `lib/`. Send the zip.
`docs/WINDOWS_README.txt` goes in it as `README.txt`.

What the Windows client has, against the GTK client:

- The same session, audio and menus in game (all of `OpenFPS.Client.Core`). Connecting, logging
  in, creating an account, reconnecting after a drop, the game menu and logging out are in the
  session too, so both clients behave the same.
- Main menu: Connect, Create account, Saved Servers, Settings, Quit, as on Linux. Open log folder is
  in Settings. Settings and
  servers are `%APPDATA%\openfps\client.json`, the same format as `~/.config/openfps/client.json`.
  Beacon choices are `%APPDATA%\openfps\beacons.json`.
- Voice chat through NAudio, on the microphone chosen in Settings. Linux records through FMOD
  and sends the same Opus packets (64 kbit/s, 20 ms frames, numbered). The server relays each one
  to everyone on the sender's map as it arrives.
- Keys from the game window only, cleared on every focus change. Alt on its own does not open
  the system menu.
- Speech through NVDA's controller client, checked per line; SAPI when NVDA is not running.
  JAWS and Narrator users get SAPI (Tolk would reach them, and Tolk.dll is not shipped).
- Logs in `logs\` next to the exe (or `%LOCALAPPDATA%\openfps\logs`), kept 10 days. A hang
  of 8 s writes `openfps-hang.<pid>.dmp` beside them; read it with `dotnet-dump analyze`.
  A native crash leaves no closing line, the same as on Linux.

It is compile-checked from Linux (`EnableWindowsTargeting`) and has never been run here. It is
played on Windows with NVDA against the VPS; that player's logs (Settings, Open log folder) are the
only record of how it behaves.

## The server on a VPS

```
./publish-server.sh            # dist/openfps-server-linux-x64-<build>.tar.gz
```

Self-contained, with `maps/`, `prefabs/`, `composites/`, `machines/` and `motd.txt`. It does not
carry the server's own state: `openfps.db` (accounts, roles, grants), `friends.json`,
`teams.json`, `roles.json`, `map_access.json` and `maps/players/` (maps players made with
`/map new`; the script deletes the local ones from the package). Unpacking an update over the old
folder leaves them alone.

On the VPS:

```
tar -xzf openfps-server-linux-x64-<build>.tar.gz     # -> openfps-server/
cd openfps-server
OPENFPS_ADMIN_PASSWORD='something long' ./OpenFPS.Server --map city
```

The server reads everything from its working folder, so start it from inside `openfps-server/`.

**The admin password.** A new database is seeded with `admin` / `admin123`, which is written in
this repository. `OPENFPS_ADMIN_PASSWORD` sets it on the first run, and on a later run resets it
to whatever the variable says. Until it has been set, the server logs a warning at every start.

**Ports.** UDP 33288 is the game. TCP 33289 is the MUD gateway (plain-text telnet, passwords in
the clear). Block 33289 in the VPS firewall unless you want it.

**Security.** Roles, login limits, lockouts, what is logged about connections and accounts, and the
admin commands that show it (`/sessions`, `/user`, `/throttled`, `/unlock`, `/setrole`) are in
`SERVER_SECURITY.md`. The first start of a server from 2026-10-02 or later upgrades an older
`openfps.db` in place and leaves a copy of the old file beside it.

### As a service

`/etc/systemd/system/openfps.service`:

```
[Unit]
Description=OpenFPS server
After=network-online.target

[Service]
WorkingDirectory=/home/USER/openfps-server
ExecStart=/home/USER/openfps-server/OpenFPS.Server --map city
Environment=OPENFPS_ADMIN_PASSWORD=something long
User=USER
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

```
sudo systemctl daemon-reload
sudo systemctl enable --now openfps
journalctl -u openfps -f
```

After the first start you can take `OPENFPS_ADMIN_PASSWORD` out; the password stays.

### Updating

1. `./publish-server.sh` and `./publish-windows.sh` from the same commit. The two file names
   carry the same build.
2. Copy the tarball up, `sudo systemctl stop openfps`, unpack over the old folder,
   `sudo systemctl start openfps`.
3. If the build in the name changed, send the new zip. Old clients are refused until they update.

What needs what:

- A change in `OpenFPS.Common`: a new build hash, so a server update AND a new zip.
- A server-only change (`OpenFPS.Server`, maps, prefabs, composites, machines): a server update.
  Same hash, so the zip still connects.
- A client-only change (`OpenFPS.Client.Core`, `OpenFPS.Client`): a new zip only. Same hash, so an
  old zip still connects and simply lacks the fix.

### Cody's VPS

- `ssh debian@codyhurst.com` (key, passwordless sudo). `cody@` and `root@` do not work.
- Service `openfps` (systemd), folder `/opt/openfps-server`, owned by user `openfps`. The admin
  password is in `/etc/openfps/admin.env`.
- Update:
  1. `scp` the tarball to `~debian` and unpack it there.
  2. Back up the running folder:
     `sudo tar -czf ~/openfps-server-backup-<old build>.tar.gz -C /opt openfps-server`.
  3. `sudo systemctl stop openfps`.
  4. `sudo cp -a openfps-server/. /opt/openfps-server/`, then
     `sudo chown -R openfps: /opt/openfps-server`.
  5. `sudo systemctl start openfps`.
  6. Check `/opt/openfps-server/BUILD.txt` and `journalctl -u openfps`.
- The copy in step 4 adds and replaces files only, so the accounts, `maps/players/` and
  `map_access.json` on the server stay.
