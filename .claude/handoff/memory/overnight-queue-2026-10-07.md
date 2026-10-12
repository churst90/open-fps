---
name: overnight-queue-2026-10-07
description: AGREED overnight order after hardening (Cody 10-07): server bugs, housekeeping, todo audit, CI hang, Windows, editor leftovers, water perf
metadata:
  node_type: memory
  type: project
  originSessionId: 568c4779-136f-4fbd-b7f7-35b87bd6f916
  modified: 2026-10-07T08:35:55.346Z
---

Pushed 99ce1541 10-07 ~03:45 (library stages 0-2, editor phase 2, ranking, downpipe, test limits); CI 37595864603.

AGREED ORDER (Cody 10-07), one agent at a time, builds/tests on 12 cores via tools/*-local.sh:
1. server bugs (after hardening; same files): /savemap writes spawned walkers/trains into city.json; logout
   while dead skips wait; things given to the dead stay; frozen client still predicts; /spawn aircraft not
   saved; huge look turn unbounded (+ any left from hardening).
2. housekeeping, ONLY when nothing else is in flight (after library split + hardening + server bugs merged):
   dead/unused code removed (proved by build + tests), comments in Joanie style ([[comment-style-joanie]]),
   keep warnings/measured facts, move histories to docs. No sound/behaviour change (library guard tests).
3. todo.md audit (after housekeeping, so the lookahead is clean).
4. CI: PresenceTests login hang (suspect SqliteConnection.ClearAllPools in other Dispose).
5. Windows client parity: messages 40/41/42/44, keys H/U/J/L, /editorkeys; publish-windows.sh builds; no deploy.
6. editor leftovers: rebuild resets doors/fires; machines/ vehicles not in library; held weather vs lightning;
   groups not composites; prefab list fields.
7. water performance (surf 1.5x, shingle 4x), renders proving no audible change.

PROGRESS 10-07: hardening merged locally (push HELD, see [[hardening-2026-10-07]]); server bugs merged locally
d866bceb (492 tests pass; wire: ServerStateUpdate.Held appended). Housekeeping rules in docs/HOUSEKEEPING.md;
chain, one agent at a time: (a) Server + Geometry/Native/Gtk/WinForms [started], (b) Common, (c) Client.Core,
(d) AudioLab + Tests. Each runs the whole suite once through tools/test-local.sh at its end (pushes held, so no CI).
Then: todo audit, CI hang, Windows parity, editor leftovers, water perf.
(a) Server housekeeping MERGED (whole suite 3,182 passed, guards unchanged): Server comments 6,874 -> 4,966;
CommandHandler 2,669 -> 574 + partials; docs/SERVER_NOTES.md new. For Cody: Windows game menu plays no UI cues
(GTK does) -> Windows parity agent; /move NAME is staff-only (owner can't on own map) - intended?; ReachingFrom's
two branches return the same point; KnownDynamic eviction on tiled maps - confirm; OccupancyService.Moving 2.0 vs
1.0 m/s; nullable warnings left (CS8602 Spawning/MovementSystem, CA2014 SolidContact, CS8714 SpatialGrid).
(b) Common housekeeping started.
Cody 10-07 morning: "no vps, only after this entire batch of work is done" -> push + VPS + zip all wait for the
END of the batch. He allowed a 2nd agent in parallel within the shared 12 cores: CI hang + Windows parity agent
started beside the Common housekeeping (stays out of OpenFPS.Common).
CI hang SOLVED + Windows parity MERGED locally: the hang was every test ClientGameSession prewarming 164 doors on
the shared thread pool (login's Task.Run waited 71 s on 12 cores, >30 min on CI's 4); PrewarmDoors now on 4 own
threads, no prewarm without audio, TestDeadline.cs (60 s) on awaited logins. Windows: [SAY] logging, quiet
reconnect, keypad keys with Num Lock off dropped, key help lists driving keys; menu cues were already there.
Windows zip b8c37cbc3bf8 built in that worktree only (stale by the end; rebuild then). Water perf agent started.
(b) Common housekeeping MERGED locally (whole suite passed on branch; comments 15,800 -> 10,701; docs/COMMON_NOTES.md).
For Cody: Footsteps.Key round(2*speed) bug (1.4 m/s parses as a run; AudioLab only); ElevatorDoor.faceAtFoot and
PushBarDoor.railHit kept for the RNG sequence; SharedMovementEngine stepped=true unread; WindModel vs WindField;
TransientSound lacks APPEND ONLY marker; PhysicsUtils.GroundMaterial identity.
(c) Client.Core housekeeping MERGED locally (whole suite 3,193 passed on branch; comments 18,668 -> 12,994;
docs/CLIENT_NOTES.md new). PROBABLE BUGS it found (sound-changing, need Cody's ear): WorldAudioPlayer.Clear never
called on /join (one-shots/thunder carry over); ProcessAudioEmitter physicalKey != null always true -> EVERY emitter
gets ApplyGround (probably meant Length > 0); SubmitLanding force 0 -> landings never use LANDING bank;
SetSimulatedReverbDecay hf/lf ratios ignored (room colour never reaches the reverb); StruckBell clapper damping
restored after 0.1-0.3 ms; AggressiveOptimization on QueueStrikes not Synthesize; SA pathing never runs
(enablePathing false); AcousticPathData.ReflectionId/Scattering never read. Fire fizz tied to CracklePart.
Remaining chain: (d) water files housekeeping after water perf merges, (e) AudioLab + Tests, then todo audit.
Editor leftovers MERGED locally (unheard): LiveState through Remake; moving a door moves its doorway (bug fix,
behaviour change); parts-list vehicles in the library; per-map held-sky lightning; prefab list fields
(/edit model add); /edit select group + /edit held move|nudge|turn. Open: map entries' Form dropped by Remake and
Clone (recommend carrying it).
(e) AudioLab+Tests housekeeping MERGED (whole suite 3,193 passed). Water perf MERGED (sound unchanged, surf/shingle
~5x cheaper). Running: left-out files housekeeping (+NatureSpike/NatureTests/TextureSpike + water usage lines),
probable-bugs branch (NOT to merge before Cody hears, inbox/probable-bugs-2026-10-07), tests-that-cannot-fail +
lab bugs. Then: todo audit, one whole-suite run on final main, summary for Cody; then push, VPS, Windows zip in that
order ONLY when Cody says the batch is done (he said: VPS only after this entire batch).
Housekeeping COMPLETE (all projects + water slice merged). Todo audit MERGED (todo.md has "Next, in order" at the
top; docs/DONE_2026-10.md). New probable bug: TracedReverbDsp averages stereo input (v /= inCh) -> tail ~3 dB
drier than the approved -6 trim; sent to the probable-bugs agent. Still running: honest tests + lab arg bugs,
probable bugs (branch, inbox/probable-bugs-2026-10-07, merge only after Cody hears). Then: full suite on final
main, then push -> VPS -> Windows zip when Cody says.
FULL SUITE on final main (after honest tests merge): 3,201 passed, 0 failed, 4 skipped, all 5 heads build.
Cody heard inbox/probable-bugs-2026-10-07: "the probable bugs after clips sound better" -> merge the probable-bugs
branch when its agent finishes (4-reverb-colour was not a bug). Then the batch is complete -> push/VPS/zip on his word.
Probable bugs MERGED (Cody approved the after clips). Open for Cody from it: own landing has no 8 dB
bone-conduction lift (a landing at your own feet ~ as loud as a step); tram gong 4.5 dB lower (sharper blows hit
the limiter at 5 m); traced tail now 3 dB wetter at the same -6 trim - keep -6 or go -9?; WorldEditor Restore
also drops KeyedSide and RoomMaterials (not fixed); new lab --probable-bugs. Final full suite on the whole batch running.
BATCH SHIPPED 10-07: final suite 3,222 passed; VPS 4c10b4b921e5; pushed 246a4e1c, CI 37666520784 ALL GREEN;
zip ~/OpenFPS-windows-4c10b4b921e5.zip. Inbox: 55 demo folders to desktop Trash (gio trash), 7.0 -> 4.3 GB.
Proposed next round (awaiting Cody): geometry 3 + streaming 2 (needs 4 answers: who builds tiles, VPS disk,
travel radius/generation wait, edge of Magnolia), library stage 3 alongside, small fixes; then measured sound
fixes with renders; then editor phase 3. Asked: delete the stale maps/default.json on the VPS?
