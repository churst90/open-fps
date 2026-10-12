---
name: sound-library
description: 10-06 Resonance asks open-fps to become a shared sound/acoustics/geometry library (inbox/resonance-sharing-2026-10-06/README.txt); read-only survey agent started (docs/SOUND_LIBRARY_BOUNDARY.md)
metadata:
  type: project
---

Resonance (Sean Terry's single-player engine) ported ~49k lines of open-fps audio line-by-line; every open-fps
change gets re-ported at 500-700k tokens per agent. Their note: make open-fps's sound + acoustics (+ geometry,
library-first) a library with no client/server/network/entity references, a host interface instead of
WorldSnapshot (split ClientAudioSystem 4,096 lines), shared values out of Networking (WheelState), no statics
(AudioClock, EarModel.Enabled, Loudness, WindField.Weather, Runoff), host-given data paths, options where engines
differ (makeup/trim, recorded-gain placement, tails), consumed as submodule/package.
Open items from them: AskRoutes cached answers keep old OpeningGraphs alive (~400 MB/km driven) -> sent to geometry
stage-2 agent; echo search O(n^2) on game thread (bird in woods 35-80 ms); 104 of Magnolia's buildings cut by tile
edges (keep whole in their middle's tile in gen_osm.py); light single wheel spin-up 0.3 s vs 1/20 s; train horn/
whistle/bell outlets never played; MachineVoiceState control unused (no load wander/thermostat/ground speed).

**Survey DONE + merged 10-06 (8e1176ce): docs/SOUND_LIBRARY_BOUNDARY.md, tools/sound_boundary (Roslyn + classify.py,
run.sh rewrites crossings.tsv).** 733 types ~101k lines: 672 library (~89k: Sound 47.9k, Audio 24k, Native 7.8k,
Acoustics 6.1k, Geometry 3k), 27 shared values, 34 host. 600 crossings from 26 files (394 snapshot reads in 10 files;
worst AcousticVolumeGenerator 120, RainField 104, SpatialAcoustics 70). 311 mutable statics. Plan 17-24 sessions,
stages 0 guards, 1 Native, 2 Geometry, 3 Sound + half Acoustics, 4 Room/Opening values, 5 statics->instances,
6 IAcousticWorld + OpenFPS.Audio, 7 split ClientAudioSystem/WorldAudioPlayer/BirdLife, 8 versions (submodule+tags).
10 decisions for Cody (5 projects recommended). Geometry stage-2 agent told to keep Geometry entity-free.
Cody's decisions 10-06: five projects yes; vehicle physics in Sound "as long as it adheres to realism"; start stages
0-2 right after geometry stage 2 lands; Resonance takes a git submodule pinned to tags.

Cody 2026-10-07: "we're separating concerns in the code not just for resonance's use, but because it's just good coding
practice." **How to apply:** frame and judge the split (boundaries, project layout, dependencies) on its own merits as
architecture for open-fps, not only on what Resonance consumes; it is not optional if Resonance stops needing it.

2026-10-07 stages 0-2 DONE + merged locally (branch library-stages-0-2): guards = LibraryBoundaryTests (Roslyn
binding ratchet vs crossings.tsv, 608 now), RenderFingerprintTests (17 offline renders; bit-exact on matching
libm, else level per 4096 samples within 1 dB; CI glibc differs), EmitterStreamReplayTests (3 stored streams,
430 KB; regenerate ONLY in a commit that changes sound on purpose, reason in the message). OpenFPS.Native and
OpenFPS.Geometry projects; WireContract.Hash now covers Geometry too (551d1fe5906e). Pending Cody: ColliderShape
stays in Common (rec.), CI by tolerance (rec. yes), 430 KB per stream regeneration (rec. yes).
Tests had been writing into Cody's real ~/.local/share/OpenFPS/rendercache (pruning other builds' folders) -
now a scratch XDG_DATA_HOME; that empty cache's door prewarm was what starved the rain test.
