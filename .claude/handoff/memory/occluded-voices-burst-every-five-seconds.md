---
name: occluded-voices-burst-every-five-seconds
description: FIXED 2026-09-23 — occluded live voices restarted every 5 s at full level; step 5 only asked paths for PLAYING voices
metadata:
  node_type: memory
  type: project
  originSessionId: 39299c77-65ae-4e42-a25c-f847e4540835
  modified: 2026-09-24T01:42:12.252Z
---

Cody: "some ambient sound seems to stutter and cut out". Log showed `Engine voice started` for one car
54 times and two `ac_window` machines every 5.0-5.3 s.

Loop: VoiceManager scores with (1 - occlusion) and stops a voice under `Loudness.SilenceGain`, while
ChooseLiveEngines/Machines (occlusion-blind) keep it live. Step 5 of ClientAudioSystem asked the
acoustic worker only about `GetActiveSpatialSoundIds()`, so the stopped voice's result expired after
`SaSourceTtlMs` = 5000, the emitter fell back to an UNOCCLUDED path (Occlusion 0), it was rebuilt,
and the next answer silenced it again.

Fix: step 5 asks for playing ∪ `_liveEngines` ∪ `_liveMachines` (`_pathIds`). Not heard yet.
**How to apply:** a periodic 5 s restart in the log = something falling off the worker's TTL.
Also seen: siren voice -905878 placed at a position 20 s old — FIXED 09-23: sirens now have their own per-frame pass (ClientAudioSystem.UpdateSirens), not the car's engine pass.
See [[audibility-ranking]], [[fmod-must-not-virtualise]].

**Second half, same evening (Cody at 400 -130, nearest car 276 m, "stuttering, probably reflections
of the buses"):** not reflections (engine echoes are off; synthesized sources get no worker
reflections). VoiceManager dropped any voice whose (1-occlusion) score went under SilenceGain, so a far
car passing behind buildings was stopped and REBUILT each time (45 rebuilds in 9 min). Now a playing
continuous voice is held at the floor instead (`AnEngineGoingBehindABuildingIsNotRebuilt`).
