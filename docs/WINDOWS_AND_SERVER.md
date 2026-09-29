# Windows client and a public server

How to give someone the Windows client and run a server they can reach.

## Client and server must be the same build

Messages and components serialise positionally, so a client and a server built from different
versions of `OpenFPS.Common` misread each other with no error. The build hashes the sources of
`OpenFPS.Common` into `WireContract.Hash`. The client sends it with its login, and the server
refuses a mismatch by name: "This client does not match the server. Your build is X, the
server's is Y."

The hash covers `OpenFPS.Common` only. A change to the client or the server alone does not
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

- The same session, audio and menus in game (all of `OpenFPS.Client.Core`).
- Main menu: Connect, Saved Servers, Settings, Open log folder, Quit. Settings and servers are
  `%APPDATA%\openfps\client.json`, the same format as `~/.config/openfps/client.json`.
  Beacon choices are `%APPDATA%\openfps\beacons.json`.
- Create account on the connect form. The GTK client has no such button.
- Voice chat through NAudio, on the microphone chosen in Settings. Linux has no voice chat yet.
- Keys from the game window only, cleared on every focus change. Alt on its own does not open
  the system menu.
- Speech through NVDA's controller client, checked per line; SAPI when NVDA is not running.
  JAWS and Narrator users get SAPI (Tolk would reach them, and Tolk.dll is not shipped).
- Logs in `logs\` next to the exe (or `%LOCALAPPDATA%\openfps\logs`), kept 10 days. A hang
  of 8 s writes `openfps-hang.<pid>.dmp` beside them; read it with `dotnet-dump analyze`.
  A native crash leaves no closing line, the same as on Linux.

It is compile-checked from Linux (`EnableWindowsTargeting`) and has not been run here.

## The server on a VPS

```
./publish-server.sh            # dist/openfps-server-linux-x64-<build>.tar.gz
```

Self-contained, with `maps/`, `prefabs/`, `composites/`, `machines/` and `motd.txt`. It does not
carry `openfps.db` or `friends.json`: the VPS keeps its own accounts, and unpacking an update
over the old folder leaves them alone.

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
