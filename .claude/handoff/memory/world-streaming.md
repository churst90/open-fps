---
name: world-streaming
description: 10-06 Cody's direction: one continuous real world in tiles, generated on demand as players travel, stored on the server; user maps on the same machinery; stage 1 agent started
metadata:
  type: project
---

Cody 2026-10-06: real-world tiles generated from open data the first time anyone approaches, stored on
the server for later recall; travel continuously between places; user-created maps use the same tiles
and streaming but are built from assets, not real data.
Stages: (1) stream tiles within one map, coarse layer far (Magnolia/Albany as test) — agent started 10-06,
design doc docs/WORLD_STREAMING.md first; (2) one world keyed by lat/lon, server runs the importer on
demand, stores tiles (needs Python + internet on the VPS; Geofabrik/Overture bulk, not Overpass);
(3) seamless travel with traffic/players; (4) user maps on it. Report stage 1 before starting stage 2.
See [[magnolia-map]], [[tests-on-github]].

**PAUSED 10-06, RESUMED 10-06 later on Cody's word (merging main first).** Branch worktree-agent-a0605929c27ad00d6: 755b2e8d design doc
(docs/WORLD_STREAMING.md) + a WIP commit of stage-1 code (client/server tile sending, acoustics, settings)
stopped as it was starting tests — not build-checked. Resume by messaging that agent or a new one on
that branch; do not restart unless Cody says.

**Stage 1 MERGED+pushed 10-06.** 250 m tiles for maps with TileMetres>0 (Magnolia, Albany); full/coarse radii
/detail low 150/500, medium 300/800 (default), high 500/1200 m; 50 m hysteresis; re-check every 8 m; dynamic
entities only in loaded tiles; acoustic map + SA scene rebuilt in background; EntityDefinitionPack Brotli for
ALL maps (city join 5.0 MB -> 186 KB); new msgs TileStreamUpdate 38, EntityDefinitionPack 39 (wire change).
Open: SA scene rebuilds ~1/s when driving (per-tile SA meshes, stage 3), server still simulates whole map,
43 doors at the full-detail edge open onto nothing, no menu control. Decisions asked of Cody (radii, coarse
trees, UTM tile key, per-tile SA meshes before stage 2, menu control).
Cody 10-06 decisions: (1+4) fix SA whole-scene rebuild -> per-tile SA static meshes now (stage 1b, agent resumed);
(2) coarse outline keeps audio realism: trees, fences, garden walls, static vehicles stay; (3) stage 2 keys
tiles to the UTM grid; (5) menu control for detail unanswered (/detail only).
**Stage 1b MERGED+pushed 10-06 (1ac232f1):** Embree everywhere on the game's SA context (OPENFPS_EMBREE=0 falls
back; Embree results vary 0.1-1.8 % run to run), TileSceneSet (2 sub-scenes per tile: open ground + rest; two
top-level pairs used in turn), scene work 492-561 -> 32-46 ms/s driving; coarse layer = shells, front doors,
woods/trunks, solids >=0.8 m x 2 m (tree wind sources NOT in coarse: their 90 m range); door+rooms grouped per
house; UTM recorded. Open for Cody: tree wind in coarse? ARM falls back; route rebuild ~90 ms/s next?
Cody 10-06: keep woods' wind in the outline (streaming agent: aggregate crowns into extended wood sources,
inbox/distant-woods-2026-10-06); ARM fallback OK for now; per-tile doorway routes -> geometry stage-1 agent.
Distant woods MERGED+pushed 10-06 (WoodChorus: crowns grouped per 200 m square+species, within 70 m own voice,
past 110 m in the wood; sum within 0.4 dB). Audible only to ~350 m (40 trees ~12 dB SPL at 500 m) and map crowns
are sparse (1 per 80 m). Two-player streaming test tagged Timing. Asked Cody: tree counts per crown?, cost +0.5 core?, city?

2026-10-09: terrain (2 m heightfield, Heightfield.cs) + one world (OpenFPS.Server/OneWorld, gzip JSON tiles under
world/, 20 GB cap, LRU eviction to 90 %, /join world ..., F6 The world / Maps) MERGED to main (unheard).
Cody's decisions: copy Magnolia/Albany into world tiles first; 8 m terrain for the far ring; re-export places'
elevation at 2 m (map vs world was 0.9 m apart); store in server folder by default. Cody: the player must NOT
wait at an unbuilt edge - build ahead (arrival waits on the loading screen for the ring; build along heading by
time-to-reach; prebuild world_places at start). Agent started on these 10-09 ~15:40.
10-09 evening: that agent's work MERGED locally (main 7280d5b8, unpushed, unheard): loading-screen arrival with
WorldLoading msg id 45 (Common changed), tiles ordered by seconds-to-reach (N=30 s), places prebuilt at start,
car braked short of unbuilt ground; Magnolia/Albany copied into tiles (placed tiles pinned against the cap);
places at 2 m on the UTM grid (maps now grid-north, up to 1.4 deg off true; spawns moved <1 m); far ring
7.8 m ground (32 cells/tile). Open for Cody: N/parallel-requests above ~65 m/s, grid north OK?, pin rule for
player-built tiles, skirts for the coarse/full hairline crack.
