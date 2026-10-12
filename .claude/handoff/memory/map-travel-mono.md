---
name: map-travel-mono
description: "10-05 FIXED (uncommitted at write): after /join every sound went mono = reverb buses returned pooled HRTF stages at SpatialBlend 0; --map-travel proves it"
metadata:
  node_type: memory
  type: project
  originSessionId: 568c4779-136f-4fbd-b7f7-35b87bd6f916
  modified: 2026-10-05T23:52:23.683Z
---

Cody 2026-10-05 evening: after switching maps (city -> speedway -> default -> city) sound "went mono",
fountain did not move left/right. Log /tmp/openfps-sa-on-client.log: [ADBG] L/R exactly equal for
every physical voice after the return, half the speedway engines; engines started before travel fine.

Cause: a room's reverb bus borrows a SteamAudioVoiceState from the SAME pool as point sources
(TryCreateSteamAudioVoice, SaDirectReserve 24) and sets SpatialBlend 0 (stereo pass-through).
SetAcousticMap -> ClearReverbBuses -> ReturnReverbVoices pushed them back still at blend 0; the
pop reset Dir, RMS, ground but not SpatialBlend -> next point source played dry mono in both ears.
Never happens without a map change (only SetAcousticMap clears buses).

Fix: pop sets SpatialBlend = 1; ReturnReverbVoices pools only after removeDSP/disconnectAll succeeds.
Instrument: `AudioLab --map-travel [from=city] [to=speedway]` — 22/40 mono before, 40/40 placed after.
Trap: with too many first-map sources the pool is empty before buses borrow (no repro); 50 is right.

Same evening: default.json moved to OpenFPS.Tests/maps (test fixture, not served); `run-server.sh rooms` gone.
Scope "Nobody in view" from Selby roof was 4x = 6 degree cone, level look from 21 m: not a travel bug.

**Committed 1ac978d5 (main, not pushed at write).** Cody 10-05 later: proceed with water/wind grain,
car fronts (EngineBayLeakage 0.15 too low, no car fans), weather wind -> WindField + ear wind + /weather;
three worktree agents; then merge, full suite, commit, PUSH. HOLD VPS update and Windows zip. Scope hint: not now.
Cody added 10-05: rain by intensity on surfaces (roofs, roads, grass, glass) + thunder/lightning simulation -> two more worktree agents (rain-2026-10-05, thunder-2026-10-05 inbox folders). Merge all five, full suite, push.
