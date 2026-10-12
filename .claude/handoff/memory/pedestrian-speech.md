---
name: pedestrian-speech
description: "09-26 — city pedestrians talk (greet/part/sorry/help/phone/stranger exchanges) from Cody's Qwen3-TTS lines; built and walked by MUD, Cody heard one 09-26 and asked for more people"
metadata:
  node_type: memory
  type: project
  originSessionId: 8c38609c-3ece-49a0-b277-9e2bd4a20c8b
  modified: 2026-09-26T14:40:23.567Z
---

Cody's lines: `inbox/npc-voices-2026-09-26` (18 voices x 86 lines, manifest.csv, -20 dBFS RMS).
`tools/import_npc_voices.py` → `OpenFPS.Client/ASSETS/SOUNDS/VOICES/<voice>/*.ogg` (Vorbis 48k mono,
~19 MB) + `OpenFPS.Common/Speech/voices.csv` (embedded resource). ben/jimdale/seanterry are clones
of real people; **Cody chose (09-26) to ship seanterry and jimdale, NOT ben ("isn't great")** —
`--clones=seanterry,jimdale`. 17 voices, 1,462 lines.
- City has 92 walkers (09-26: 46, then "need like double"): two each way on every pavement,
  going the other way, Main St north, estate streets, Market Square (x -112..-36; a building stands
  in the plaza's east end). Main St walks END at the bus shelters (3.2 m deep on a 3.5 m pavement;
  the old walkers passed through the glass). Route checker: scratchpad routecheck.py tested every
  walker line against solid prefab boxes — recreate if needed.

- Server: `PedestrianSpeech` (Systems/), server-only `Pedestrian { Voice }` component on walkers and
  parked-car drivers, voices round-robin per map. `StreetLines` picks lines from game clock + weather
  (WorldEnvironmentSystem GameTime/Temperature/Precipitation/scenario). Weekday line ("already
  Friday") unused: no weekday in the world.
- Wire: WorldAudioEvent, SynthKey `voice:<voice>/<line>`, Label `speech: <text>`, Character Hiss,
  DecaySeconds = line length. Client `WorldAudioPlayer.SpokenLine` decodes via FMOD
  (`GranularBank.TryDecode`, uncached), rescales to `Speech.BufferRmsDbfs` -26 (takes range -25..-20
  because the generator peak-limited; max crest 23.8 dB), and FollowSpeakers re-submits the voice at
  the moving body until the line ends (stops if not playing after 0.5 s so a budget-dropped line is
  not restarted late).
- Level: ANSI S3.5 Normal 62.35 / Raised 68.34 at 1 m; LevelDb = effort + 26.
- Server log: `SPEECH e<id> <voice> at <pos>: "<text>"`. MUD players see words within 10 m.
- Phone calls only run with a player within 40 m (`HeardMetres`) — the mixer's AudibleRange is
  hundreds of metres and made every call on the map fire.
- Instruments: `--speech-lines` (lab), `OPENFPS_TRACE_SHUTTLE=Pedestrian` + MUD `move` 9 m ahead.
- Crowd verdict: chatter lines make babble (8+ talkers: envelope spread 3 dB, no gaps); no reaction
  lines (cheers/gasps) in the set. Test mixes in `inbox/npc-babble-test-2026-09-26/`.

See [[street-life]], [[mud-walkthrough]], [[no-ambience-beds]], [[restart-the-server-always]].
- 09-26 "the girl passing me sounds like she's in a building": speech had discrete echo copies
  (first- and higher-order, fixed at the start position) — removed for speech. Also found
  `UpdateSpatialAttributes` never updates EQ/air/region on re-submit; new `SpatialEmitter.CarriesPath`
  flag makes it do so (speech only). Mouth directivity `Speech.Directivity` (-2/-6/-13 dB behind).
  PreloadAll skips VOICES/ (was ~350 MB decoded at startup). Stranger exchanges gated to 40 m of a player.
- 09-27 round: Cody pruned voices (deleting FOLDERS = removal; manifest still lists them) and added
  angry_* (yell-only, 25 lines), 40 new chatter lines, stories in <voice>_preview (texts from
  ~/qwentts/*/stories.py, rest transcribed with ~/qwentts/venv faster-whisper large-v3 on CPU —
  no CUDA libs; saved as inbox/.../stories.json). Clones: seanterry, jimdale, joeb, joel, tim; NOT ben
  (folder is still there with new previews — ask if he wants him back). Favourites joel/seanterry/joeb
  weighted x2. 310 walkers generated from footway() records in gen_city.py (WALKER_SPACING 30).
  Drivers: VehicleSystem.Drivers.cs (hard stop / give-way cross traffic / player in road → brake+honk+
  yell / pull-out / crossing wait). No traffic lights → "it's green" lines unused. BufferRmsDbfs -28
  (a yell has 25.5 dB crest). Server tick 1.9 ms with 310 walkers.
- 09-27 later: Cody added alec, fluke, camel, tyler and wants BEN BACK IN. 25 voices (20 walkers, 5
  angry), 2,533 lines, 23 stories (texts from ~/qwentts new4_stories.py / tyler_stories.py /
  joeb/stories2.py, nested {voice: {story: text}}). Favourites x2: joel, seanterry, joeb, ben, alec,
  fluke, camel. Peakiest take now 26.3 dB crest → 1.7 dB spare at -28. Cody is still looking for voices;
  re-run the importer with the --clones list in its docstring.
- 09-27 "not hearing my own footsteps outside": own + others' steps shared FOOTSTEP pool of 12 ids
  (-100..-111), taken round-robin; 310 walkers' steps overwrote the own step before it played. Others
  now -300..-363 (64), made only within 15 m, Essential only for own, no step echoes for others.
  FootstepPoolTests (harness now takes a soundsPath; SoundMappingService.Initialize(path)).
- 09-28: voice folder now lives in approved/voices/npc-voices-2026-09-26 (Cody's generator still
  writes to inbox/npc-voices-2026-09-26 — merge files + manifest rows into approved, then import).
  glenn + louis added (clones list in import_npc_voices.py docstring). 27 voices, 11,053 lines.
  New: Pick() takes categories — named lines + the voice's any-time lines in the category
  (StreetLines.AnyTime regex excludes time/weather words), so new voices without the old named lines
  still speak. Remarks (mutter/think_aloud/read_text/waiting/weather_*/time_* when true, within 15 m),
  reactions (Program.EmitWorldAudio → PedestrianSpeech.Heard: weapon SynthKey = shot near/far, label
  "horn"), phone_voicemail/phone_signal/phone_listen/phone_end, stranger smalltalk + directions.
  Scripts: tools/export_speech_scripts.py (reads ~/npc-lines-2026-09-28 phone.py/pairs.py) →
  OpenFPS.Common/Speech/scripts.json (embedded): 32 calls in 12 voices, 47 conversations for 10
  voice pairs. Scripted calls 50 %; walking PAIRS: gen_city makes ~1 in 4 wide-pavement walkers a
  pair (VehicleData.Pair, companion 0.6 m inside), PedestrianSpeech.PairVoice gives the pair a
  conversation's two voices; Converse() plays a script when a player is within 40 m. 352 walkers,
  48 pairs. Characters (gang etc.) not generated yet.
