---
name: play-session-2026-10-04-afternoon
description: "10-04 fixes + bullets, aim assist, narration, tracker, stairs zones, saved players: DEPLOYED 89f615b6391d (main 3eebe496); patio round 3 on branch awaiting ear"
metadata:
  node_type: memory
  type: project
  originSessionId: e3233dbc-96e4-4267-8826-c87e85309fd2
  modified: 2026-10-04T11:39:31.321Z
---

From Cody + Sean's 10-04 session on VPS 8afc79f8d2dd (logs: Cody /tmp/openfps-sa-on-client.log,
Sean inbox/client-20261004.log — Windows head logs no [SAY]):

Merged to main, unheard, not deployed (Common changed → new Windows zip + VPS):
- Dropped items silent: broadcast only chose entities with a ColliderComponent; items have none. Now
  items are candidates; carried items go unreliable like bodies (c63e4ff3, 5dab1802).
- Reflections "left on the first floor": own step copies (ids -200..) were traced by the path pass as real
  sources through their own wall; talkers had no copies (397bff5c).
- Stairs to IBC code (17 x 176 mm risers), lines once per real arrival, beacon only bottom/roof,
  ZoneSettle 0.4 s on every zone line (9fae7850).
- Doors: render peak-normalised but declared LAFmax (14-29 dB low) plus LevelCalibrationDb; now each door
  plays at its own render's peak (fbbc5a96). Open: hard close quieter than normal on /levels real
  (ceiling cap), knob opening as loud as closing; footsteps (34 dBA at 1.5 m), car doors, knock have the
  same declared-vs-peak fault — fix after Cody hears doors. Files inbox/doors-level-2026-10-04.
- Sean "teleported" 489 m to the map edge: landing a fast fall a tick deep in the 1 km dirt box pushed him
  out sideways to its edge. Fixed in SharedMovementEngine; gravity 15 → 9.81, jump 0.5 m (53f93e6f).
- Scan skips carried things (de0e4d13); server logs on-foot >12 m/s (73f6e38b).

Also merged: wall bump + turn narration (N, /narrate, /bumps; 3c19ea24) and semantic part names for all 6,476 city parts + scan skips ground (4aeb02d9).
Cody's open questions: XP for pedestrians, what levels unlock,
bullet sounds (crack/whizz/impacts/penetration) vs hit zones + XP first. See
[[server-broadcast-per-player-cost]], [[doors-need-detail-and-level]].

Evening: also merged and DEPLOYED to VPS 89f615b6391d (main 3eebe496, zip ~/OpenFPS-windows-89f615b6391d.zip):
directions in words, comma/period tracker + E picks the tracked/front item, sight narration while moving,
stair flight/landing named places + one stairs beacon per floor, saved belongings/place/health (Users.Belongings,
PlayerState; city spawn Foundry Street sidewalk 60,122), every shot flown + crack/whizz + hip aim assist
(/aimassist), Earth gravity. NOT merged: patio round 3 (worktree-agent-a8fdf0b2b8847af92 head 5b083a97,
inbox/patio-door-round3-2026-10-04) — Cody's rule, merge only after his ear approves.

Approved by Cody's ear 10-04 evening: patio door round 3 (merged 249b5a55, "a win, keep it"), car window v3
character for every window (e717d23b), scope sounds (inbox/scope-2026-10-04: breathing, guidance tone,
M700 report/bolt/reload/dry-fire — already in main since 01e12b00). Not yet deployed: patio + windows.
Push-bar round 8 APPROVED + merged 7ad360b1 (steel stops, loose cranks; leaf 182 Hz mode still strongest — next if "hollow"). Bullets round 2 whizz/ricochet/impacts "good" except water "tinkling" (agent redoing splash + impact levels). Glass model agent started. Running: knob round 4; bullets round 2 (whizz was a
"laser" = pure tone sweep; ricochets; impacts per material).
APPROVED later 10-04: knob door round 4 ("much better, not perfect, I approve"; merged ee8faa81; open
gaps: turn clicks ~30 dB under release, soft knocks after a close, hard close 17-20 dB over normal) and
the water splash (bullets round 2 merged 5ce9ec3a). Glass model agent still running.
Glass model APPROVED by ear + merged bc68404d (GlassFracture.cs; GlassKind.Of picks annealed/tempered/
laminated). DEPLOYED 4d3c607b0096 (patio, windows v3, push-bar 8, knob 4, bullets 2 + water, glass).
OPEN, Cody to decide: the loudness law places by PEAK, so anything over ~126 dB peak at 1 m (gunshots, a
window crash, a thin steel plate) plays at full scale out to the 40 m clamp — 5 m and 20 m sound the same.
That is Loudness.Place's reference-distance rule, not the models.
