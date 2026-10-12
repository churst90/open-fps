---
name: lab-renders-need-both-libs
description: renders from an agent worktree are PANNED (no HRTF) unless libphonon.so AND libfmod.so are copied from main lib/ into the lab output
metadata:
  type: feedback
---

2026-10-10: Cody heard engine pass-bys "phased", not sweeping right to left, not at 3 m. Cause: lib/ is gitignored,
so a worktree has no libphonon.so; the lab ran without Steam Audio ("0 binaural free" in the log) and every voice
fell back to FMOD's plain panning (50 dB between ears, ITD 0, a flip at the pass). The game itself was fine.

**Why:** a render that does not go through the game's binaural path misrepresents what Cody would hear and wastes his
listening time.
**How to apply:** every agent brief that renders for Cody must say: copy BOTH libphonon.so and libfmod.so from
/home/cody/external-rescue/Github/open-fps/lib/ into the AudioLab build output (never the repo), and check the capture
log shows every voice with HRTF ("0 direct voice(s) without HRTF") before sending. Renders made 10-10 by the fire,
loudspeaker and water agents may have been panned; Cody approved their character, not their spatial image.
See [[render-check-before-sending]], [[renders-at-game-level]].

Cody 10-10 on this: "if you know why the sound is strange in the samples you don't need to rerender them as long as
the game will be fine... you don't need to run more agents to fix the samples. I get the point." So: when a render
artifact is understood and the game is unaffected, say so and move on; never spend an agent re-rendering for it.
