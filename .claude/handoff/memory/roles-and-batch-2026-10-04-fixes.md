---
name: play-session-fixes-2026-10-04
description: 10-04 fixes from Cody+Sean playing on the VPS: item beacons, E pick-up, drop under map, inventory list, shots/speech trailing, voice relay log; patio door branch awaiting ear
metadata:
  type: project
---

From Cody and Sean playing on the VPS, 2026-10-04 (all pushed to main, NOT yet on the VPS):
- Dropped items silent: BeaconAids plays the NEAREST THREE items and carried items were always nearest.
  Held items now have no BeaconCategory (EntityDefinitionFactory) and HandsService.Carried re-sends
  definitions on take/drop/give (ee2e111a).
- E had no pick-up path at all (Program.HandleInteract); now TakeWithin 2 m before closing a door.
- Drop with no ground found put items at y = -1000; now at the dropper's feet.
- I = InventoryList menu (unions 36/37), items addressed by #id (draw/stow/drop/take accept #id).
- Gunshot report OnBody at muzzle; driver yells and pedestrian lines OnBody, followed in body frame
  (offset used to be server-pos-at-emit minus current pos = trailing) (85676111).
- Program.NoteVoice: "Voice: <user> sent N frames (kbit/s) on <map>, relayed to N player(s)" every 10 s.
- Voice not heard after Sean changed his audio device: undiagnosed. Cody's log: Sean (e6705) received
  only 02:38-02:40, jitter buffer at the 500 ms max. Need Sean's client log + the new server line.
- Patio door goblinjack rework: branch worktree-agent-ad88f3cb579329c74 head 2bb49cbf, renders in
  inbox/patio-door-goblinjack-2026-10-04; within 3-5 dB of the recording; 125 Hz +4.5 dB on standard
  still. Do NOT merge until Cody approves by ear.
See [[roles-and-batch-2026-10-03]], [[windows-client]].
