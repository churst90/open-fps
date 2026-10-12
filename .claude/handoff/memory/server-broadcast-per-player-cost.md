---
name: server-broadcast-per-player-cost
description: "10-04 tick overrun FIXED 60d2fc4e; bandwidth 5.7 -> ~1.5-1.75 Mbit/s per player DONE 10-05 (steps 1-4); steps 5+ (rate by distance etc.) wait on Cody, todo Now 8"
metadata:
  node_type: memory
  type: project
  originSessionId: e3233dbc-96e4-4267-8826-c87e85309fd2
  modified: 2026-10-04T09:03:29.014Z
---

Cody + Sean on the VPS 10-04 07:36-07:57 UTC: "Server loop fell behind" ~165 times a minute from the
second spawn on, almost none with one player. Cause: BroadcastWorldState asked the grid for everything
within earshot per player per tick; city earshot = whole map (3000 m clamp), 86,000 grid entries for
7,500 entities, 16 ms/player/tick on the desktop. Fixed by one world gather per map per tick and
skipping known, undirty statics: 0.39 ms/player, 3 players 3.2 ms tick (was 49), identical states.
This was a large part of "voices trail the cars, shots trail me" on Sean's side; the client-side half
was the echo cost budget (830e4251).

How to measure: scratchpad loadbots (N bots join SECONDS apart) + loadrun.sh against a local
`--map city` server with OPENFPS_PROFILE=1; probes server.grid/movement/traffic/broadcast.scan/send,
net.state-bytes. Read the VPS with `ssh [login removed] sudo journalctl -u openfps`.

Still open: each player receives ~480 dynamic states a tick = 5.7 Mbit/s, ~580 packets/s, because
earshot is the whole city. Distance-based update rates would cut it; not started. Also "Input queue
full" warnings for both players during the overrun (should go with the fix; check after deploy).
Windows mic now logs device/first audio/stop and V presses (685f0493) — read Sean's next log.

UPDATE 2026-10-05 (found stale 10-07): resting states, packed positions and stats-on-change took the city from
5.8 to 1.75 Mbit/s for three players (about 1.5 per player); see docs/PLAN_2026-10-05.md section 6 and todo
"Now 8". Remaining options (rate by distance 50-70 %, byte budget, deltas, client-side traffic, voice culling)
wait on Cody.
