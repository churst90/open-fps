---
name: ear-test-harness
description: How to run a live ear test — the two script words, the map flag, and the log lines to read afterwards
metadata:
  type: project
---

Set up session 14 (2026-09-18) so Cody can run an A/B by ear without typing env vars (he cannot
copy-paste, and a screen reader makes long command lines expensive).

- `./run-server.sh` — the map claiming `IsDefault` (speedway). (`rooms`/default.json was REMOVED from the game 10-05; it is a test fixture now)
  the rooms/doors/material-lab map. Any other word is a map id. Backed by a real **`--map <id>`** server
  argument (`MapManager.RequestedMapId`), because **there is no runtime map change**: the landing map is
  the only map a session can ever be on, so every map but the default used to be unreachable in play and
  the only way in was editing `IsDefault` in the JSON. F6 in the client LISTS maps; it cannot move you.
- `./run-gtk-client.sh on` — sim on + trace → `/tmp/openfps-sa-on.log`.
  `./run-gtk-client.sh off` — `OPENFPS_STEAMAUDIO_SIM=0` + trace → `/tmp/openfps-sa-off.log`.
  Separate files on purpose: the two halves of one A/B sit side by side afterwards.
- **Steam Audio sim is ON by default** — only `=0` disables it. A plain run is already the new path.
- `/tp x y z` (admin) is how to reach a waypoint repeatably; `/` opens the client's command box.

**Read afterwards:** `[SASUMMARY]` once a second — `sources=`, `blocked=`, `viaEdge=` / `viaProbe=`
(which mechanism supplied each bearing, see [[diffracted-bearing]]), `sceneBoxes=`, `reverb=NNNms`.
`[SAWORKER]` is per source, rate-limited to 1 Hz — it used to print per source per tick, which on the
speedway is >1000 synchronized `Console.WriteLine`s a second and slows the thread it measures.
Health lines that say the run was clean: `Mixer load` (dsp %, starves, real channels) and `Cars:`
(synthesized vs borrowed). His 19:03 run: dsp 10-14%, 0 starves, 21/21 synthesized.

Speedway waypoints (walls are `ColliderSize x Scale` = FULL size, position = centre):
`0 0.6 0` infield spawn · `0 0.6 -140` inside the 0.9 m pit wall · `0 0.6 -190` outside the 3.5 m
outer wall · `-180 1 -240` behind the grandstand (12 m deck + tall back wall) · `-180 13 -218` on the
deck in the crowd. Rooms map: `7 1.6 5` in front of the wood-room door (megaphone 320 beams OUT of it,
rotation 0,1,0,0) · `0 1.6 5` seven metres to the side, door is the only route · `5 1.6 35` / `-5 1.6 35`
the gym and carpet halves of one open room.

See [[build-on-tmpfs]], [[speedway-map]], [[mud-walkthrough]], [[synthesis-by-listening]].
