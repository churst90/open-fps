---
name: mud-walkthrough
description: How to drive a real OpenFPS server by text (MUD gateway) to walk a change end to end
metadata:
  type: project
---

The server's MUD gateway listens on `port + 1` (TCP), speaks line-by-line, and reaches the SAME
`CommandHandler` the game client does — so any command can be walked end to end without a GUI or audio.
Credentials come from `SqliteUserRepository` reading `openfps.db` **relative to the working directory**,
not from `users.json` (which is vestigial): with no db present it seeds **admin / [password removed]** as Admin.

Recipe that avoids touching the repo's data:

1. Copy `OpenFPS.Server/{maps,prefabs,composites}` into a scratchpad dir.
2. Run the tmpfs-built dll with that dir as cwd on a spare port:
   `cd <scratch>/srv && ~/.dotnet/dotnet /tmp/openfps-srv/bin/OpenFPS.Server/debug/OpenFPS.Server.dll --port 34288`
   (the gateway is then on 34289; a stale server of the user's may already hold 33288/33289).
3. Talk to it with a small socket script: `login admin [password removed]`, `ready`, then commands without the
   leading slash (`take crowbar`, `inv`, `fire`). `move x y z` teleports.

**A player lands on the map claiming `IsDefault`, which is `speedway`, and there is NO runtime map
change** — so `default.json` (the rooms, doors, megaphone, material lab) is unreachable in play. To walk
it, flip `IsDefault` in the SCRATCHPAD copies only.

**Why:** the held-items work passed twenty tests and then failed on a live server in the first minute
(see [[component-wire-format]] for the sibling trap). Live text walkthroughs catch what harnesses cannot,
because a test spawns its own props and a map does not.
**How to apply:** build per [[build-on-tmpfs]], then walk every new command before calling it done.
