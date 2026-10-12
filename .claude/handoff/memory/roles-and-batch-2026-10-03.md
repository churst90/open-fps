---
name: roles-and-batch-2026-10-03
description: "10-03 night: roles as permissions DONE (d4545486); four parallel agents on presence, beacons+teams, weapons+hit chimes, car windows; merge plan and union indices"
metadata:
  node_type: memory
  type: project
  originSessionId: e3233dbc-96e4-4267-8826-c87e85309fd2
  modified: 2026-10-04T03:49:46.410Z
---

Cody's 10-03 list (asked while playing): roles player/moderator/dev/admin with per-user grants; admins
move users and give items; player beacons with a teammate tone (needs teams); finish inventory,
weapon draw/reload; R contextual (reload with a gun, windows in a vehicle); synthetic window sound and
glass acoustics both ways (a driver heard as they drive by); presence notices on All (online, offline,
logged out, away) with sounds that can be turned off; hit chime and kill chime.

DONE by me, d4545486: OpenFPS.Server/Core/Permissions.cs (permission = command's canonical name, plus
fire-any, join-private, edit-any, move-player); one gate at the top of CommandHandler.Execute;
UserRole.Moderator appended; Users.Permissions column; /grant /revoke /perms /bring /kick /mute
/unmute /give, /tp NAME, /goto, /move NAME x y z | to OTHER. Moderator = people, Dev = world, Admin =
roles/accounts/move-player. StaffGateTests rewritten around the table.

Parallel agents (worktrees, artifacts in ~/.cache/openfps-agent-*): presence (no new union index),
teams+player beacons (union 33-34, PlayerComponent.Team appended, ChatChannel.Team), weapons (union
31-32, HealthComponent/Ammo new structs, owns the R/Enter bindings: R in vehicle sends "window"),
windows (implements server "window" command, cabin glass both ways, union 34 only if needed).
Agents must not edit changes/todo/MANUAL; I merge and write docs. Common changed => after merging,
new Windows zip + VPS deploy (see [[windows-client]]).

Also 10-03: "car left running inaudible" was Cody's /levels at 100 % (literal): the idle lift is
0 by design there; /levels default restores. See [[parked-car-idle-silent]].

Progress: custom roles + /give counts (6591da6c), command box /help + CommandCatalog + did-you-mean
(07a4ef3c; CommandCatalogTests fails if a switch case lacks a catalogue entry: add one for every new
command at merge). Presence merged; teams + player beacons merged (f9682c9d; conflicts in UiSounds
cues, CueFor, GameServer Roles/Teams). Waiting: weapons (union 31-32), windows. Then: full suite at
nice/low CPU when Cody is not playing, new Windows zip, VPS deploy.
Open design questions put to Cody: raw material units (bag of concrete 25 kg etc., realistic volume
vs one unit per piece); door sounds by analysis-by-synthesis from the patio recording (recommended)
vs granular playback (still the recording).
Presence sounds APPROVED by Cody 10-03 ('good, I like them, use them'). Teammate beacon: same G4-E4 call, triangle-ish timbre (f44588ac), awaiting his ear.
10-04: Cody APPROVED the teammate beacon (triangle timbre, f44588ac) and the presence chords on a triangle wave (d4684eec): 'I love the new beacon and game sounds with the chords and triangle'. Patio door: agent working toward goblinjack ref on a branch, NOT to merge until Cody hears it. Dry-fire clicks are one shared contact (todo 9).

10-04: gun handling clips dereverbed into inbox/weapons-dry (tools/dereverb_wpe.py: WPE + late
suppression; -40 dB tail median 132 -> 65 ms, attacks unchanged); Cody: ignore the outdoor firing
clips and cadreforensics. Asked whether the dry clips may be played in game (so far recordings are
spec only). Scope agent started: numpad layout (* scope, 8246 aim scaled by magnification, 5
describe, 7/9 targets, +/- zoom, 1/3 turret, . rangefinder, 0 hold breath, / fire), Num Lock must be
ON (NVDA/Orca own the numpad when it is off), guidance tone + target list, full ballistics, a .308
bolt rifle. Branch only until Cody hears it.
10-04: stairs agent started (Cody: stairs now, lifts later): W/S walk stairs, roof flight + door + parapet in gen_city.py, stairs beacon wired at flight ends, 'Stairs up, N steps, to floor X' on approach, up/down footsteps differ. Lifts later with PgUp/PgDn floor choice. Branch only until merged by me.
10-04 ~06:07 UTC: full suite 1902/1906 (2 load-timing failures: EnclosureCost, OwnClap fixed df22ee27). VPS deployed bb1e1f39f924 (roles, presence, teams, weapons, windows), DB upgraded with backup openfps.db.before-20261004-060708, login proven; ~/OpenFPS-windows-bb1e1f39f924.zip replaces the 074d zips. Stairs, scope, patio door still on agent branches: their merges will change the hash again.
10-04 06:40 UTC: VPS on 7c9e394b2c9b (scope + hit words); ~/OpenFPS-windows-7c9e394b2c9b.zip. Stairs + patio still on branches.
10-04: stairs merged + pushed (dog-leg stairwells, roof flights + steel door + parapet on 5 towers, stair markers/beacon/lines, up/down footsteps; StairsTests 13). Not deployed to VPS yet; patio door agent still running.
