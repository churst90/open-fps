---
name: freight-thrash-2026-10-07
description: "10-07 Cody's \"cutting out, gritty freight, no train horn, dead city\" = the 50-wagon freight overran the engine render pool; voices restarted 912 times"
metadata:
  node_type: memory
  type: project
  originSessionId: 568c4779-136f-4fbd-b7f7-35b87bd6f916
  modified: 2026-10-07T20:41:29.614Z
---

Cody's session 10-07 14:44-15:40 on the VPS (log saved: ~/openfps-scratch-archive/session-2026-10-07-trains/client.log).
`/spawn train freight` (two EMDs, fifty wagons) at 15:31:28 -> "engines starving" 17 -> 3,224 a second, mixer DSP
60 % -> 89 % mean / 104 % peak, rail:freight voices started 912 times in 9 min (stop/restart = cutting out + grit),
cars out of budget 5 -> 16 of 41 (the dead city). Fountain 30 starts (5 places x 6), crossing gong 6 restarts.
Train signal arrives ("Train ... sounds: horn ..., bell 18 s") but the horn was not heard near the crossing.
Baseline mixer ~60 % before any train; 0 reflections per engine.
Agent sent: bound a long train's cost (aggregate far wagons), stop thrash, graceful shedding, horn trace, bump
"clack not hollow", city-detail budget analysis -> inbox/trains-and-city-2026-10-07/.
**How to apply:** "cutting out" -> count `Physical voice started` per key and read "engines starving" first.
15:41 "stepped into the bus shelter and the sound went mono": freight holds ~130 voices; 190-215 active, 0 of 160
binaural free, 60-84 voices without HRTF (= mono); no command removes a spawned train (server restart does, since
spawns on shipped maps are not saved). Told the agent: bounded voices/HRTF per train, shed cleanly, /spawn train out.
FIXED + merged locally (branch trains-and-city; full suite running; NOT pushed/deployed, awaiting Cody's ear on
inbox/trains-and-city-2026-10-07): a train = at most 6 voices (angle-grouped field voices + horn + bell, no locks,
lanes in the render pool); every bogie its own TrackResponse (shared state ran modes 150x fast = the static);
RollingReferenceDb back to 92; budget restores under ceiling-8 for 3 s with backoff (old rule needed < 45 %, never
reached, so machine slots sat at 1 for the hour = fountain/gong cut-outs, dead city); faded voices released after
the fade plays; < 6 binaural free sheds, no flat voices; crossing clack band-passed slap; /spawn train out; lab
--train-scene; MixerProfile "Mixer time" line (traced echoes ~12 points = biggest; move off the mixer thread next).
Level changes for Cody: light rail -3 dB, crossing clack peak -6 dB (a guess).
Test fixes merged (67f1a353): machine floor 6 (MinMachineVoices), restore after 3 s low + 15 s since give-up; rail
sources voiced through their train's field voice. Full suite earlier: 3,242 + these 2 now pass (126/126 in the
affected classes). Local main ready; push/VPS/zip wait for Cody's ear on inbox/trains-and-city-2026-10-07.
LESSON: an agent's "cleanup" deleted the session scratchpad and loose files in /tmp/claude-1000 - every brief now
says: delete only your own /tmp/openfps-<you> folders.
2026-10-08 Cody: "sounds good, no real difference from before, but it is fine now" -> shipped 3fc38430512b.
