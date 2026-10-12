---
name: world-editor
description: 10-06 world editor agreed: F12 menu (role/map-owner gated), no fly/no-clip build mode, one model library with schema-driven generic settings editor, model versions with per-map pinning, live server-side edits with undo
metadata:
  type: project
---

Cody 2026-10-06: F12 opens the World Editor menu (admin/dev anywhere; a map's owner + invited editors on their
own maps); NO separate build mode (no flying through walls, no invulnerability); editing a model updates every map
using it, with version history and per-map pinning of older versions. Live editing while players are present
(decided earlier). Design: one library of models, each kind self-describing (fields, units, ranges, help,
source) so menus are generated (no per-thing screens); menus Map / Place / Select & edit / Library / People /
Roads & routes / Places & rooms / Test tools; server-side edit ops with undo/redo; spoken feedback; ListMenu.
Order: undo + selection + generic settings editor first, then library manager, then map settings/roads/people;
shape tools as geometry stages land. New city (docs/NEXT_CITY_10KM.md) only "when it is time" (after streaming
stage 2 + real terrain). See [[geometry-system]], [[world-streaming]].
Phase 1 MERGED + pushed e2b5b693 (10-06), CI 37572732859. 32 WorldEditor tests; two expected "Concrete Floor" -> "Ground" after
geometry stage 2 dirt. Cody not yet told how to try it or asked its 7 decisions (overlay every map, gitignore overlays and
model_versions, named editors editor-only, undo lost on restart, solid placed in front, 5,000 placement cap, nearest
excludes what you stand on).
2026-10-07 Cody: "go with your recommendations on the editor" -> all decisions as recommended; overlays and
model_versions now .gitignored (agent had left them committable; I had told Cody "out of git") and
publish-server.sh leaves maps/overlays out so a VPS update never replaces live edits (05c53101).
Phase 2 agent started 10-07 (pins/versions, remaining kinds, library, place search+preview, groups, rooms/doors,
map settings; direct keys designed but OFF until tried with Orca/NVDA).
2026-10-07 phase 2 MERGED c61876e9, decisions as recommended; direct keys (/editorkeys on, Shift+arrows/brackets/,./ D Delete Z Y) await Cody's try with Orca then NVDA.
2026-10-07 leftovers merged locally (see docs/WORLD_EDITOR.md section 13). Unheard.
