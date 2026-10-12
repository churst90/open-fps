# Special cases in the audio, acoustics and sound code

A read-only audit of special-casing: places where the code tells sounds apart by an id range, a name or
a sound-id prefix, where one voice has more than one writer, and where each source starts and places its
own voice by its own rules. Cody asked for it on 2026-10-11, after a train horn played through Marlow
Tower's brick. Trains' voice ids are not entity ids, so the per-frame path pass skipped them and they kept
the path each voice started with (fixed by `ClientAudioSystem.Trains.cs` handing each voice its path).
His two questions: "shouldn't all mobile sound sources like vehicles, trains, sirens, etc use the same
path so there's consistency?", and "are there any other situations in the audio where an issue like this
is present?"

- Surveyed at main `700cb635`, 2026-10-11. No code was changed, built or run. Every file:line below was
  read at that commit.
- Section 1 is the findings, ranked. Section 2 groups the fixes into parts, each sized for one agent
  session. Section 3 places the parts against the sound library stages (docs/SOUND_LIBRARY_BOUNDARY.md).
  Section 4 is what looks special but is physics and stays.
- CAS below is `OpenFPS.Client.Core/ClientAudioSystem.cs`; paths without a project are under
  `OpenFPS.Client.Core/`.

Where the parts stand (2026-10-11):

| Part | Status |
|---|---|
| P0, wider guards | done 2026-10-11 ("P0 as built"); tests only. The replay's backwards vehicles it found were fixed the same day (e5e3d0f6), all eight streams regenerated |
| P1, the pops and the worker's answers | done 2026-10-11, unheard. The Main Street pavement's pops fixed; Marlow Tower's floor 1 stairwell's are the barrier model's |
| P2, one table of model kinds | done 2026-10-11 ("P2 as built"), its leftovers (the server's prefixes, S26, S27's model fallbacks) the same day; no stream changed. Two questions for Cody there |
| P5, names and materials to properties | under way |
| P8, laws in one place | under way |
| The barrier model (a way round one thing that passes through another), found by P1 | under way, a part of its own (todo.md, "Acoustics") |
| P3a, the registry for moving things | done 2026-10-11 ("P3a as built"), unheard: intakes and borrowed cars take their path, one writer per voice, the rumble follows the speed (S11). S21 closed as deferred the same day (Cody): the bus keeps its own law until geometry stage 6 |
| P3b, every other voice | done 2026-10-11 ("P3b as built"), unheard: every voice an entry, one writer each; woods take their path, engine echoes carry theirs; P6 for talkers and S23. Blocked engines heard round the edge (Cody, the same day) |
| P3c, seeds then ids | done 2026-10-11 ("P3c as built") |
| P3d, one start and declared levels | steps 1 to 5 built 2026-10-11, step 6 (the reseed) 2026-10-12 ("P3d as built"), unheard. Left: the rain and bump start levels (a decision). Rain patches under probes: done with P6. The two default ranges: one now (S24) |
| P4, one ranking | the choosers built 2026-10-12 ("P4 as built"), unheard: one rank, cars and places by loudness, a render's headroom out of the rank. Three decisions for Cody there. The provider's orderings (S20) built 2026-10-12 ("S20 as built"), unheard |
| P6, one path model for every asker | done: talkers and S23 in P3b; beacons and perched birds in P3d; rain patches 2026-10-12 ("P6, the rain, as built"), unheard |
| P7, machine state from the server | last; a wire change, shipped with a server deploy |

That is the order agreed with Cody on 2026-10-11 (todo.md, "Next, in order", item 2). It puts sound library
stage 5's `AudioClock` slice after the registry; section 3 below, written first, put P3 after it. The agreed
order stands.

## Contents

1. Findings
2. The parts
3. Where each part sits against the library stages
4. What stays as it is
5. Appendix: every voice band

---

## 1. Findings

### 1.1 How a voice is placed today

Everything below follows from four facts about the plumbing, so they come first.

1. **Two calls write a voice's path, and they write different fields.** `SetAcousticPath`
   (`AudioEngine/Fmod/FmodAudioProvider.cs:3288-3311`) sets occlusion, apparent position, effective
   distance, aperture, bleed, air, region, band gains and room gain. `UpdateSpatialAttributes`
   (`FmodAudioProvider.cs:3132-3286`) always sets position, apparent position, effective distance,
   occlusion, aperture and bleed (`:3146-3147`, `:3281-3282`), and the band gains, air and region only
   when the emitter says `CarriesPath` (`:3151-3158`). A re-submission without `CarriesPath` "leaves the
   tone alone" (`AudioEngine/Data/SpatialEmitter.cs:54-59`).
2. **The last writer is decided by queue order on the audio thread, not by call order on the game
   thread.** Each audio tick (`AudioEngine/Core/AudioEngineFacade.cs:141-211`) drains stops, then every
   `SetAcousticPath`, then submissions, refreshes, direct plays, then every `UpdateSpatialAttributes`,
   and only then runs `VoiceManager.Process`, which re-applies the emitter it holds for each voice it owns
   (`AudioEngine/Core/VoiceManager.cs:221`, `:263`). The tick runs at 250 Hz (`AudioEngineFacade.cs:102`).
   So for every field both calls write, the owner's emitter beats the path pass, and VoiceManager's held
   emitter beats any game-thread update of a voice it owns.
3. **The per-frame path pass works only on entity ids and on the ids it has a branch for.** CAS:640-830
   asks the acoustic worker about every playing voice, live engine, live machine, horn, siren car,
   borrowed car and train source (`:646-658`), then skips every id under -5000 (`:663`). For each entity
   it hands the shadowed direct path to the entity's own voice, and to five derived voices by name: horn
   (`:723`), held horn (`:724`), siren with its own apparent position (`:727-732`), borrowed distant voice
   (`:734`) and the outer places of a spread source (`:737-741`). Any other voice gets no path from it.
4. **A voice id is a formula, and the formulas overlap.** There are 34 id constants and one literal formula in
   nine files (the appendix). Most are `Base - |entityId|` or `Base - counter % pool`, chosen by hand to sit apart.
   Nothing allocates them, and one test checks three of the bands (`OpenFPS.Tests/VoiceBudgetTests.cs:304-327`).

`PlayPhysicalSoundDirect` has 17 call sites, 16 outside VoiceManager, each building its own emitter with
its own level, offset, path and lifetime, and each the same four lines: `if (IsPlaying) Update else Play`.
Started on an id that is already playing, the provider only moves the live voice for any synth, granular
voice or same-file recording, and hard-stops it for anything else (`FmodAudioProvider.cs:2525-2547`). So
two kinds that land on one id move or cut each other without a word in the log.

### 1.2 The ranked table

Severity is likelihood times how bad it is to the ear: **A** heard now or almost certain on the next
change, **B** latent on the maps in use, **C** latent or small. Past bugs are from git log, changes.md,
docs and the project notes.

| Id | Where | What | Bug it has caused or would cause | Sev | Fix in a sentence |
|---|---|---|---|---|---|
| S1 | CAS:663, :714-741; `FrontVoice` CAS:1998-2036; woods `ClientWorldState.cs:1240`, `OpenFPS.Sound/WoodChorus.cs:30` | The path pass serves only entity ids and five named derived voices. The front outlet (intake, -800,000 band) has no branch: its band gains, air and region are frozen at what its voice started with. The stored replay streams show it: every `-800000` voice has `Place` lines and no `Path` line in walk, drive and traffic_rain. Woods (`WoodChorus`, ids from -20,000,000) are entities with negative ids, so `id < -5000` skips them altogether; their band gains are frozen too, and their outer places have no path at all. | The same fault as the train horn through Marlow Tower's brick (2026-10-10). A car within 20 m turning behind a wall keeps its intake's open-air tone; a wood heard first from indoors keeps its through-the-wall tone outdoors, and the reverse. Unheard so far. | A | One registry of voices (part P3): every voice is an entry with an owner, and one writer hands each its path every frame. |
| S2 | `AudioEngineFacade.cs:172-210`, `VoiceManager.cs:221`, `FmodAudioProvider.cs:3146-3158`, `:3281-3282`, `:3298-3307`; writers CAS:723-741 against `UpdateHorns` CAS:2410-2471, `HeldHornVoice` :2617-2656, `SirenVoice` :2664-2721, `DistantEngine` :2783-2830, `ProcessAudioEmitter` :2832-3191 | Two writers per voice, and one path made of two answers. The path pass writes the worker's answer, with `VehicleShadow` in the band gains; the owner writes its own reading (`VehiclePath`, CAS:2298-2320, for horns, sirens and trains: a staleness test, then the hand-rolled tracer; the cached answer or `FirstAnswer` for an entity's own voice), without the shadow, and its write wins for apparent position, occlusion, aperture and bleed. For engines and physical models the owner's apparent position is the true position (CAS:3039-3040), so the diffracted bearing the path pass writes is overwritten within the same tick. Horns and held horns still get two different apparent positions: `SirenApparent` from the owner, the path's own point from the path pass. | The siren image pulled between two bearings by two writers, fixed for sirens only (`SirenApparent`, CAS:2741-2757; docs/CLIENT_NOTES.md). A borrowed car with two Dopplers (2026-09-16, `BorrowedVoiceDopplerTests`). A horn blocked by a bus: band gains shadowed, level and bearing not. | A | One writer per voice per frame (P3): the path, the shadow and the apparent position are worked out once and handed over in one call with `CarriesPath`. |
| S3 | `DistantEngine` CAS:2804-2826; `FmodAudioProvider.cs:2888-2891`, `:4147-4155`, `:3936-3939` | (Corrected 2026-10-11 by P0, "P0 as built": the emitter's defaults for `ApertureFactor` and `TransmissionBleed` are 1, so the 400 Hz low-pass below does not happen; occlusion 0 against the path's 0.95 stands.) A borrowed distant voice is re-submitted every frame with `ApertureFactor`, `Occlusion`, `TransmissionBleed` and `EffectiveDistance` at their defaults (0) and `TargetRegionId` the global region. Its update beats the path pass (S2), so its aperture target is 0 every tick. A non-reflection voice carries the diffraction low-pass, which below aperture 0.98 runs at `max(400, 22000·a²)` Hz: 400 Hz at aperture 0. Its spread is `a·90/d`: none. Its bearing is the car, never the edge it is heard round. | Probable: every borrowed car (up to 12 on the city) low-passed at 400 Hz whatever its path, and never occluded. Unheard as such; it would read as distant traffic sounding muffled and placed through walls. Verify first with a replay scenario with more cars than the engine budget (P0) and a look at the `-700000` `Place` lines. | A | Folded into P3; a one-line stop-gap is to give `DistantEngine` the car's path fields as `FrontVoice` does. |
| S4 | `AsyncAcousticWorker.cs:532-560`, `:1345-1361`, `:296-336` | The source pool's serving and lending. Staging skips a pending source not served this tick (`:538`), but the read-back loop reads every pending source that still holds a Steam Audio source (`:558-560`): its direct result was simulated from inputs staged on an earlier tick, against this tick's listener, and is stored as this tick's answer, labelled with this tick's source position. A lent source (`:1351-1360`) goes to its new owner without `ClearSource`. A moving source is the only kind for which a stale input differs. | Candidate for the open pops: a quarter second, 20 to 30 dB, 3 to 6 a minute with 40 cars round Marlow Tower's floor 1 stairwell, each a moving car on Dock Street whose transmission jumps from -80/-100 to -35/-52/-80 dB; no still source gives it, and 32 transmission surfaces instead of 8 changed nothing (docs/GEOMETRY.md 12.6, "Marlow Tower's leaks"; todo.md, Acoustics). The pool overrunning has popped before (2026-10-03, `--pop-hunt extra=40`). | A | P1: read back only what was staged this tick, clear a lent source, tag every result with its provenance, and measure with `--pop-hunt` before and after. |
| S5 | `UpdateSirens` CAS:2269-2290 with `VehiclePath` CAS:2298-2320 and `SirenVoice` CAS:2676-2680; CAS:650 | Every vehicle whose model has a siren asks the worker every frame, siren on or off, and is added to the path pass: `VehiclePath` is called before the mode is read, and a car whose engine has no voice enqueues a request each call. Off and no fresh answer, it also runs the hand-rolled tracer on the game thread for nothing. | Pool pressure on the city that feeds S4; game-thread cost per patrol car. | B | P1: ask only for a siren that is sounding; in P3 a voice that is not sounding is not in the registry. |
| S6 | Appendix; CAS:57-58; `AudioEngine/Acoustics/EngineReflections.cs:91`, `:254`, `:302`; `ClientAudioSystem.Trains.cs:38`, `:66`, `:89` | Voice-id bands overlap. A reflection slot's id is `-30000 - id·5 - slot`: it enters the transients' band (-100,000 to -499,999) at entity id 14,000, and Albany joins with 18,024 entities, Magnolia with 13,590 (docs/WORLD_STREAMING.md:150-151). The siren band `-900,000 - id` covers the driving aids (-961,000 to -967,105), the beacons (-967,000 to -967,023) and the birds (-970,000 to -970,047) for vehicle ids 61,000 to 70,047. Borrowed, intake, horn and talker bands run into the next band at ids of 100,000 to 200,000. Engine echo ids count down from -600,000 and are never reused: at the city's 2.6 echoes a second (CAS:188-191) they reach the borrowed band in about 10 hours, then the intakes, sirens and horns. Train voice ordinals grow the same way. `IsEchoVoice` (`id <= -600000`) is true for every band below the echoes; nothing calls it. | A one-shot (a step, a door) taking over a recorded loop's reflection on Albany, or the reverse; a siren moving a driving-aid tone or a bird call; after a long session, a new engine echo moving a borrowed car. Past: transient ids hashed into the echo and borrowed bands, "a footstep could take over a car's reflection or distant voice" (VoiceBudgetTests.cs:304). | B | P3: the registry allocates every id from one space and frees it; one test proves no two live voices share an id. Done in P3c ("P3c as built"). |
| S7 | `LookUpPhysicalLevel` CAS:1124-1214, `PhysicalHeadroom` CAS:1088-1122, the provider's factory `FmodAudioProvider.cs:2716-2756`, `NaturePlace` `:2281-2293`, `ExtendedSources.Layout` `OpenFPS.Sound/Core/Nature/ExtendedSources.cs:82-147`, `ModelKinds.TryModelOfSound` `OpenFPS.Sound/Editing/ModelKinds.cs:327` | One kind of physical model is named by its `kind:` prefix in five or six separate ladders, each its own list: the level and size, the headroom, the synth, the places, the editor's kinds. `ModelKinds` misses aircraft, rail, gate, siren, horn, wood and rain. `LookUpPhysicalLevel` and `PhysicalHeadroom` swallow every exception (`catch { }`) and return "not a model", which drops the source from both the ranking and the submit. | The level-crossing bell silent twice: missing from the level lookup, then from a second prefix list in the emitter branch (2026-09-22, project note "level crossings"). Any new kind is silent until it is in every list. | A | P2: one table of model kinds (prefix to level, size, headroom, places, factory, body kind), read by all of them; an unknown or broken spec fails loudly once. |
| S8 | `"engine:"` tested in 15 places, 9 of them re-implementing `CabinWalls.Vehicle` (`AudioEngine/Acoustics/CabinWalls.cs:45-48`): CAS:87, :544, :1688-1690, :2275-2277, :2599-2600, :2735-2736, :2944-2945, `DrivingAids.cs:198`, `:301`, `RainField.cs:674`, `Scope.cs:34`, `OtherBodies.cs:71-72`, `OpenFPS.Common/AudioEmission.cs:33`, `OpenFPS.Sound/Fuel.cs:235`, `OpenFPS.Server/Core/OccupancyService.cs:297-298`; `rail:` 6, `aircraft:` 4, `machine:` 3 more | Whether something is a vehicle, an aircraft, a train or a machine is read from the sound id's spelling, place by place. `OtherBodies.cs:75-79` gives everything with a physical model no legs except `machine:mower_push`, by its exact id; `Scope.cs:40` makes every NPC without `machine:` a person, so the two disagree about mowers. `PlaysCopies` (CAS:81-89) says it decides by `IsSynth` and then tests two prefixes, one of them (`ENGINE/`) a spelling that is never on a snapshot (`OtherBodies.cs:68-70`). | Cars trailing footsteps, sixteen a second at 30 km/h, until the `engine:` test went in (OtherBodies.cs:66-70); then the resolved spelling let cars walk again. A new walked machine (a hand cart, a pushed pram) would be silent underfoot or walk with no one there. | B | P2: a body kind from the model table (vehicle, aircraft, train, machine, walker) and a declared operator on a machine spec, read through one helper. |
| S9 | `Scope.cs:166-168`, `OpenFPS.Common/EntityGeometry.cs:100`, `MapTracker.cs:292-299`, `OpenFPS.Sound/Glass.cs:78-96`, `SpatialService.cs:687`, `OpenFPS.Sound/RainSurfaces.cs:41-42`, `BirdLife.cs:117`, `RainField.cs:664`, `OpenFPS.Sound/Fuel.cs:237-239`, `OpenFPS.Sound/Footsteps.cs:225-291` | Names and material strings decide physics. Standing on a roof is the thing under you named "...roof", while `SurfaceFlags.Roof` exists and mesh roofs set it from their shape. That flag is itself derived from the name for boxes. Glass type is decided by prefab id and name substrings ("window", "shop", "entrance"...), and anything unnamed is tempered. An entrance is a name containing "entrance". Rain on water or a canopy, a hedge for birds, a tree for fire and a material's footstep span and deck all switch on material names. `SurfaceFlags.Foliage` is declared and never set. | A roof whose name lacks the word, or a room called "Roof garden", is misread; a pane named "Office 3" breaks as tempered. Each is one mis-named map entity away from a wrong sound, and the real-place generators name things from OSM. | B | P5: the facts become properties: surface flags set from what a thing is (shape, prefab, generator), glazing as a field, entrances as a door or portal property, material behaviours as fields on the material table. |
| S10 | `WorldAudioPlayer.cs:292-320`, `:77-80` | Door and glass renders are dispatched by key prefix in two lists that must agree (`RenderDoorKey`, `IsDoorModelKey`) and a third for lateness (`LatenessFor`). An unknown prefix falls through to the knob door's renderer. | A push bar's key through the knob door's renderer gave sixteen samples of silence (the comment at `:288-289`). The next door model is silent the same way if one list is missed. | B | P5: one table of door models (prefix, renderer, lateness), and no fallback renderer. |
| S11 | `DrivingAids.cs:601-621`, `VoiceManager.cs:145-151`, `:221` | The lane rumble is submitted to VoiceManager once and then updated from the game thread. VoiceManager re-applies the emitter it holds after every game-thread update (fact 2), so the rumble's pitch and offset stay at their first values, and its range cull measures from where it started: past 1.5 × 80 m it fades, stops, and is submitted again. | Unheard by Cody as such ("lane ticks/tones, 09-22, unheard"): a rumble whose rate does not follow the speed, cut and restarted every 120 m along a strip. | B | P3: a voice has one owner; the aid either re-submits or plays direct, never both. |
| S12 | `ChooseLiveEngines` CAS:1684-1700, `ChooseLiveMachines` CAS:1443-1449, `ChoosePlaces` CAS:2137-2165, `VoiceManager.Audibility` `VoiceManager.cs:366-383`; unranked: `UpdateSirens`, `UpdateHorns`, `UpdateHeldHorns`, `UpdateTalkers` | Four rankings by four laws. Engines by distance squared alone; machines by heard loudness at the distance, without the path; extended-source places nearest first; VoiceManager by rendered gain times one minus occlusion. Sirens, horns, held horns and talkers take a voice each with no budget at all. | A 132 dB stock car outranked by a quiet electric car a metre nearer; a machine behind a wall holding a voice an audible one wanted. The project note "audibility ranking": rendered level, not priority, and extent paid down. | B | P4: one rank, the rendered level at the ear with the path's gains, for every voice that competes for the mixer. |
| S13 | `VehiclePath` CAS:2298-2320, `ResultIsForThisVoice` CAS:133-137, `ProcessAudioEmitter` CAS:2846-2848, `FirstAnswer` CAS:2329-2350, `UpdateTalkers` CAS:3246-3255 | Four rules for when the worker's answer is fresh. `VehiclePath` refuses an answer for a source moved more than 1 m plus half a second of its speed, or asked from more than 3 m away. The path pass accepts any answer for an entity. `ProcessAudioEmitter` takes any cached answer. A talker accepts any answer and asks under the player's own entity id, at the mouth, the id the path pass asks about at the feet. | The horn that began on the answer the last horn left, minutes earlier and a street away (2026-10-10). A talker's path and the same player's emitter path overwriting one cache entry. | B | P3: one freshness rule in the worker's read (`TryGetResult` returns fresh or nothing), and every question under its own probe id (`WorldAudioPlayer.SourceProbeId` already does this for one-shots). |
| S14 | `BirdLife.cs:325`, `BeaconAids.cs:199`, `RainField.cs:866-887`, `WorldAudioPlayer.cs:647` | Several sources are placed by the hand-rolled tracer (`CalculateAcousticPath`, id -1 for birds and rain) and never by the simulator: birds, beacons, rain patches; one-offs start from it and take the simulator's answer when it has one. | The hand-rolled tracer and Steam Audio have disagreed by 48 dB on one car (-15 against -63 dB, 2026-10-03). A bird behind a building is heard on other terms than the car beside it. | C | P6: they ask the worker under probe ids, as one-offs do; the tracer stays the stand-in until the answer comes. |
| S15 | CAS:2797, :2956 with `EngineSourceLevelDb` CAS:163; `Grille` CAS:2732-2738; CAS:2425-2427; `BeaconAids.cs:184`; `DrivingAids.cs` (all literal gains) | Levels and points guessed where the model is silent. A vehicle with no declared level plays at 116 dB, written twice. A vehicle the client cannot name has its grille 1.9 m ahead. Any `rail:` horn sits 4.2 m above the entity's origin. Beacons have a 40 m range literal instead of the audible range of their level. | Past: a police car given a pace car's 132 dB open pipes (project note "the police car was a pace car"); three undeclared duplicate constants in front of paths that had declared themselves (block × 0.4, `FrontMix` 0.35, the intake with no airbox; project note "three undeclared duplicates"). | B | P3 and P2: a voice entry carries its declared level and its offset on the owner from the model, and a model without them fails to load. |
| S16 | CAS:2934-2940 and `ClientAudioSystem.Trains.cs:150-159`; `FlightPower` CAS:1248-1268; `SirenModeFor` CAS:2759-2775 | Machine state guessed on the client from motion: a train's notch from its acceleration (the same formula written twice), an aircraft's power lever from its climb angle, a patrol car's siren mode from its speed and braking. The server drives all three. | The two notch formulas drift apart the first time one is tuned. A train braking on a down grade read as coasting; an aircraft descending at climb power read as idle. | C | P7: the server sends the state it already has (append-only, positionally, docs/COMPONENT notes); the client reads it. |
| S17 | CAS:684-688 | How often a source's path is asked for is set by distance alone: every frame inside 15 m, every second frame to 50 m, every tenth beyond. | A car at 30 m/s at 60 m moves 5 m between answers; its answer is then used as fresh (S13). | C | P3: ask by how far the source and the listener have moved since the last answer, not by distance. |
| S18 | `RainField.cs:906` against `:956-984`; `WorldAudioPlayer.cs:927-931`, `VoiceManager.cs:101`, `:193-197`; `BirdLife.cs:328`, `BeaconAids.cs:169` | Smaller lifecycle gaps from each source keeping its own: a rain slot that drops to one part leaves its other parts playing; a followed spoken line re-submitted in the tick it ends starts again from the top; pooled one-shot ids (birds 48, beacons 24, rain drops 48, step echoes 48) move the voice still playing under a reused id instead of starting a new one. | A doubled rain patch after a lever change; a pedestrian repeating the start of a line; a bird call moved mid-call. | C | P3: the registry owns stop, fade and reuse; a pool id is free only when its voice has stopped. |

### 1.3 Duplicated laws, restated constants, statics and one-map numbers

The same class at a smaller grain: a law written twice, a default restated, a cache or static that two
worlds would share, a number tuned to one map. Ranked the same way.

| Id | Where | What | Bug it has caused or would cause | Sev | Fix in a sentence |
|---|---|---|---|---|---|
| S19 | `ChooseLiveMachines` CAS:1443-1449 against the emitter CAS:2900-2902; `docs/THE_CITY.md:252` | The machine ranking leaves out the headroom gain the emitter applies and reads the timbre by `SoundId` where the emitter uses `VoiceKey`. The engine ranking by distance (S12) contradicts THE_CITY.md's "The budget is ranked by LEVEL, not by distance". | A source picked on one set of numbers and played at another: the reason `PhysicalLevel` was memoised for both (CAS:1043-1047). | B | P4: the ranking reads the entry's placed gain, the same number the emitter plays. |
| S20 | Provider orders: `FmodAudioProvider.cs:749` (render-ahead, nearest first by `EffectiveDistance`), `:4160-4182` (the 4 nearest region buses), `:1512`, `:1665`, `:1761` (engine detail, late field, echo candidates by `BaseVolume × RenderedGain`, each with its own hysteresis: `:1450`, `:1529`, `VoiceManager.cs:388`) | More rankings inside the provider, each by its own law. A voice fed `EffectiveDistance` 0 (S3, engine echoes) sorts first in the render pool. | When the render pool is short, a loud far engine starves before a quiet near one. | C | P4: the same audibility, computed once per voice per tick, read by every ordering. Done 2026-10-12 ("S20 as built"). |
| S21 | `AudioEngine/Acoustics/VehicleShadow.cs:16`, `:22`, `:62-66` against `OpenFPS.Acoustics/Diffraction.cs:27-40` | Two diffraction laws. `VehicleShadow` has its own Maekawa (0 dB at grazing, 15 dB cap, bands 150/1000/4000 Hz, its own detour), the shared one 5 dB at grazing, a 24 dB cap, bands 200/1250/8000. | A bus and a building of the same height and detour shadow a source by different amounts. | B | Closed as deferred (Cody, 2026-10-11): the bus keeps its own law until geometry stage 6 puts vehicles' bodies in the acoustic scene and retires `VehicleShadow`. |
| S22 | `OpenFPS.Sound/BulletImpact.cs:76-107`, `OpenFPS.Sound/Ricochet.cs:89-110`, `Services/SoundMappingService.cs:35-55`, `:74` against `OpenFPS.Acoustics/AcousticRegistry.cs:491-493`, `:556-566` | Second and third material tables beside the registry, restating its densities and without its aliases: "Steel", "MildSteel", "Stainless", "Aluminium" become Generic (brittle) for impacts and ricochets. `RainField.cs:323` and `RainSurfaces.cs:44`, `:216` test `IsKnown` first, so they reach Generic without the registry's warning. | The project note "registry falls back silently": unknown material is Generic, and steel is "Metal". A steel post struck by a bullet chips like plaster. | B | P5: one material table with aliases; impact and ricochet behaviour as its fields; no caller bypasses its warning. |
| S23 | `AudioEngine/Acoustics/SpatialAcoustics.cs:31`, `:324` | "The local player" for the hand-rolled path is the first Player entity within 2 m of the listener. | Two players standing together: the wrong body is left out of the occlusion rays, so your own body can block what you hear, or theirs is ignored. | B | P6: the caller passes `OwnEntityId` (CAS already knows it, CAS:40-46). |
| S24 | CAS:3411 (`FootstepRange = 15`), `:3527`, `:3850` (step copies 25), `:4056` (landings 20), `:787` (a copy's `Range = authored × 0.8`), `:3652` (own-voice room 30), `:3463-3464`; `RainField.cs:1083`, `:1172`; `SpatialEmitter.cs:248`, `:259` against `OpenFPS.Common/Components.cs:245`, `:254` | Ranges as literals instead of `Loudness.AudibleRange` of the declared level. Another person's steps are culled at 15 m (their audible range at 68 dB is hundreds of metres). A reflection copy takes 0.8 of the authored range, 50 by default, while its direct voice gets the audible range: copies of a loud source stop at 40 m. Two defaults for range and reference (100/3 and 50/3). The comment at CAS:3510 says a step is about 55 dB; `FootstepDb` is 68 (`OpenFPS.Sound/Loudness.cs:46`). | A loud PA's wall answer vanishing at 40 m while the PA plays on; footsteps across a quiet yard that should be heard are not. | B | P3d: every range is the audible range of the entry's level, unless a stated physical reason says otherwise. |
| S25 | `FmodAudioProvider.cs:2936`, `:3273`, `:3929` against `:15-21` and `:3995-3999` | A voice that falls back to FMOD 3D (no HRTF voice free) uses FMOD's inverse roll-off between reference and range, which holds level past the range instead of fading; only the binaural path uses `RenderedGain`. The comment says they agree. | The "HRTF pool ran dry" case (project note): the same source at a different level and with no edge fade when the pool is short. | C | P8: the fallback channel sets its own volume by `RenderedGain`, as the binaural path does. |
| S26 | `OpenFPS.Common/AudioEmission.cs:43-50`; `ClientAudioSystem.cs:1982` with `ModelChanged` CAS:424-427 | Caches that never forget. `AudioEmission._presetOffsets` stores `Vector3.Zero` for a preset not yet known (asked before `MachineRegistry` loads, or another map's id) and keeps it. `_outletSeparation` is a static keyed by preset name that a model edit does not clear (the level and headroom memos are cleared). | An exhaust offset of zero puts the occlusion probe at the contact patch, half buried: about half the samples blocked on open road (CAS:666-668, the past bug that made `PointFor`). | B | P2: both become fields of the model table, rebuilt with it. |
| S27 | `Sound/Honk.cs:71`, `:74` (110 dB), `Sound/HandOverSounds.cs:58` (50), `Sound/TeleporterSounds.cs:48` (56), `RainField.cs:1064` (NaN taken as 40 dB), `WallBumps.cs:162` (85 before the render), CAS:2880 (`"SYNTH"`), CAS:1222 (2 m span), CAS:1335 (8 m length), `Sound/Core/Engine/CabinPaths.cs:172` (`catch { }`), `ExtendedSources.cs:156` | Fallbacks that hide a missing declaration: an unknown horn kind or preset is 110 dB, an unmeasured rain patch 40 dB, an aircraft with no size 2 m, and so on, mostly without a log line. | The police car with a pace car's pipes, the bell with no level: each found by ear, late. | B | P2 and P3d: a missing declaration is an error at load and in tests; a value measured at run time (the rain level, the bump's render) holds the voice back until it is in, as door renders already do. |
| S28 | CAS:3081 (`(0, -0.4, 0.6)`) restating `CabinPaths.OnePlace` (`Sound/Core/Engine/CabinPaths.cs:82`); CAS:3043 (`EffectiveDistance 0.7`) against `CabinPaths.PlacedMetres` 0.72 (`:79`); ear and eye heights 1.7 (`LocalPlayerController.cs:19`, `WorldAudioPlayer.cs:119`, `Sound/Core/Thunder.cs:53`), 1.6 (`OtherBodies.cs:27`, `BeaconAids.cs:59`), 1.0 riding any vehicle (`LocalPlayerState.cs:39`); horn 0.6 m and siren mouth 0.4 m above the origin (CAS:2427, :2621, :2725) | Points and heights restated or guessed, not read from the body or the model. | Small level and bearing errors that differ by which code placed the sound; the siren comment says its head is "where the horn is" and it is 0.2 m lower. | C | P3d: offsets on the owner from the model (vehicle body, train cab, the person's body), one ear height from the body. |
| S29 | `OpenFPS.Common/Messages.cs:274` (`OcclusionFloor = 0.2`), restated at `Session/ClientGameSession.cs:68` and `ClientWorldState.cs:959`, against `OpenFPS.Common/EntityAcoustics.cs:82` and `OpenFPS.Acoustics/Systems/AcousticVolumeGenerator.cs:21` (0.05) | One default with two values. | A map generated without the manifest's value gets a quarter of the floor the client assumes. | C | P8: one constant, referenced. |
| S30 | `AudioEngine/Acoustics/SpatialAcoustics.cs:40`, `VehicleShadow.cs:64`, `OpenFPS.Acoustics/ImageSource.cs:437`, `OpenFPS.Acoustics/Systems/FaceOpenings.cs:41`, `OpenFPS.Common/DistantMotion.cs:53`, default arguments in `Diffraction`, `EarlyReflections`, `Thunder` | 343 m/s written where `AudioPhysics.CurrentSpeedOfSound` (set from the map's temperature) is meant. The hand-rolled path's reflection delays use 343 while the one-offs' and steps' use the current speed. | A reflection's delay and a diffraction's Fresnel number off by the temperature (2 % at 0 °C), and differing between the two path models. | C | P8: pass the world's atmosphere; 343 only as the documented default of the constant. |
| S31 | `OpenFPS.Client.Core/AudioEngine/Fmod/RainVoices.cs:50` (`RainFeeds.Feed`), `OpenFPS.Geometry/Triangles/MeshAssets.cs:77` (`MeshLibrary.Shared`), `Shapes.cs:214` (`Shapes.Made`), `FmodAudioProvider.cs:1454` (`ReducedFarEngines`, a console lever), `OpenFPS.Sound/GroundWater.cs:45` (`SoilGroup`), `Sound/Core/Nature/FireSynth.cs` (12 public static tunables), `Sound/StruckThings.cs:230`, `:686`, `:865`, `Sound/Loudness.cs:194` (`RankOnPlayedGain`), `Sound/Machines.cs:270`, `:552`, `Sound/ModelLibrary.cs:93`, `OpenFPS.Common/Speech/Speech.cs:135-172` (lazy caches without locks), `ClientSettings.cs:193-219`, `:258` | Statics not on stage 5's list (docs/SOUND_LIBRARY_BOUNDARY.md 4.1, 8, 18.3, appendix C), or filed there wrongly. `RainFeeds.Feed` is filed as a table (appendix C) but is live world state: `RainField` writes each feed's patch, falling and rate (`RainField.cs:820`, `:839`, `:1199`), and the rain voices write their level back. | Two worlds in one process (two clients, the lab and the game, a test pair) share one rain, one mesh library, one soil and one set of fire tunables. | B | Stage 5: add them to its list; `RainFeeds` goes with the weather (`WorldWeather`), `MeshLibrary` with the geometry instance, the tunables into `LabLevers`, the model generations into `ModelCatalog`. |
| S32 | `WorldAudioPlayer.cs:221-275` (`PrewarmKeys`: door sizes the city builds), `OpenFPS.Sound/Vehicles.cs:329-331` (`RaceSlick.PeakGripG = 3.1`, "on a banked oval"), `MapTracker.cs:21-30` (range 100 m, storey 3 m from `gen_city.py`), `StrideAccumulator.cs:73`, `:83`, `BeaconAids.cs:39-40`, `:298-300`, `Sound/Core/Thunder.cs:96-99` (one ground reflection, 0.9), `FmodAudioProvider.cs:761` (makeup set on the speedway), `OpenFPS.Common/Roads.cs:137-138` (`DowntownCorners`) | Numbers tuned to one map. No code tests a map's name; the dependence is in these constants. | Albany's and Magnolia's doors not prewarmed (silent on first swing, the project note "door render cache"); a storey count wrong in a 4 m-storey building; thunder answering the same off grass and glass. | C | P5 for the map-read ones (prewarm from the loaded map's door prefabs, storey from the building), each of the rest either read from the place or stated as a physical default with its source. |

Two more provider facts from the sweep, small but in the class: a stop and a submit of one id in the same
audio tick lose the stop (`AudioEngineFacade.cs:172-187`, `VoiceManager.cs:122-131`); and a replacement play
stops the old voice before the binaural pool is asked, so a refusal leaves silence
(`FmodAudioProvider.cs:2545`, `:2553-2558`). Both go with P3's one way to start and stop.

---

## 2. The parts

### 2.1 The mechanism the parts share

S1, S2, S3, S6, S11, S13, S15, S17, S18, S24, S27 and S28 are one fault seen from different sides: nothing in the client
knows what voices exist, whose they are, or who places them. Each source starts its own voice under an id
it computes, places it with its own emitter, and leaves the path to a pass that recognises voices by
their ids. The fix is one mechanism with three parts, built once and used by every source:

- **A registry of voices.** Each entry: its id (allocated, never computed), its owner (an entity, the
  listener, a rain patch, a train and slot), its kind, where on the owner it sounds from (an offset in
  the owner's frame, or a point the owner works out: grille, siren mouth, exhaust, intake, a bogie tap,
  a mouth), its declared level and size, its path rule, and its lifetime (until, fade, hold).
- **One writer a frame.** For every entry, once a frame: the owner's pose, the point, the worker's
  answer for that point (fresh or not used), the shadow of what moves, the apparent position by one rule
  (`SirenApparent`'s, which is the general one: the owner's point, turned toward the path's edge when
  blocked), and one `UpdateSpatialAttributes` with `CarriesPath`. `SetAcousticPath` is no longer called for
  registry voices, so no field has two writers.
- **One way to start.** `Voices.Start(entry)` decides budgeted (through VoiceManager) or direct, places
  by `Loudness.Place(level, size)` once, and is the only caller of `PlayPhysicalSoundDirect` and `Submit`
  for world voices. Stop and reuse go through it too.

The path pass becomes the writer's first half; the horn, siren, distant and spread branches and the
trains' separate writer go. This is todo.md's "one path pass for every voice of a moving thing", widened
to every voice range, as the todo asks.

Two things to keep straight while building it:

- **Seeds.** The provider seeds every physical synth from the voice id (`FmodAudioProvider.cs:2723-2751`:
  machine `id·31+7`, aircraft `id·17+3`, bell `id·13+5`, stove `id·59+29`, horn `id·29+1`). Renumbering
  ids would change every machine's random detail. The entry carries a seed from its owner and kind, and
  that goes in before any id changes.
- **The guards.** `EmitterStreamReplayTests` logs every call by voice id. A renumbering changes every line
  that names an id even when nothing else changes, so ids are renumbered in a commit of their own, after
  the behaviour has moved, with a check that the new stream equals the old under the id map.

### 2.2 The parts

Every part runs the guards through `tools/test-local.sh` (two runs at most on the machine since 2026-10-11), filtered to
the three guard classes and the tests of what it touched; the whole suite runs on GitHub Actions. A part
that changes a stream regenerates it in the same commit and says which voices changed and why
(`OPENFPS_REPLAY_WRITE=1`, `EmitterStreamReplayTests.cs:27-28`).

The stored scenarios today are walk, drive and traffic_rain (`EmitterStreamReplayTests.cs:276-281`): cars,
their echoes and intakes, a one-off horn, the cabin, and rain. Nothing in them exercises sirens, held
horns, borrowed cars, trains, spread sources, woods, talkers, birds, beacons or driving aids, or entity
ids high enough to collide. That is why P0 comes first. (P0 added five scenarios that do; see "P0 as built".)

#### P0. Widen the guards (1 session)

- **Changes.** Tests only. New replay scenarios, each a few seconds:
  - `sirens_and_far_traffic`: a patrol car with its siren on, passing behind a wall; more cars than the
    engine budget, so some are borrowed; a held horn.
  - `train_behind_wall`: a train passing behind a building, sounding its horn for a crossing
    (`Replay.RailSource`, `EmitterStreamReplayTests.cs:380-398`, exists for this).
  - `places_and_woods`: a tree with spread places, a fountain, a wood heard as one, a machine; the
    listener walks from inside a room to outside.
  - `talker_and_aids`: a talker walking out from behind a wall; the driving aids on a lane.
  - `high_ids`: the walk scenario with entity ids over 14,000 and 61,000.
  - `TheVoiceIdBandsDoNotOverlap` widened to every band in the appendix, with the entity-scaled bands
    checked at the largest entity count a map has (Magnolia's 32,598).
- **Files.** `OpenFPS.Tests/EmitterStreamReplayTests.cs`, `OpenFPS.Tests/LibraryBoundary/streams/`,
  `OpenFPS.Tests/VoiceBudgetTests.cs`.
- **Conflicts.** None with code parts; every later part regenerates these streams, so P0 lands first.
- **Guards show.** The old three streams unchanged; five new streams written. The widened band test fails
  today (S6), so it goes in marked as the known failure P3 fixes, or with the bands as they are and the
  overlaps listed.
- **Also.** The new streams are the evidence for S1, S2, S3 and S11: an agent can read them before any fix
  (a `-700000` voice's aperture, a `-20000000` wood with no `Path` line, the rumble's frozen pitch).

**P0 as built (2026-10-11).** Tests only, no game code. Recorded at `22423a5b` from the code as it was, so
the streams hold today's bugs; each scenario's summary in `EmitterStreamReplayTests.cs` names the lines.
Each runs 120 frames (2 s), the shortest that shows its behaviour. The old three streams came out
byte-identical. The five add 260 KB gzipped (the old three are 441 KB). Read a stream with
`OPENFPS_REPLAY_DUMP=<dir>`; "frame" below is a line's first number.

| Scenario | KB | What | What its lines show today | Expected to change in |
|---|---:|---|---|---|
| `sirens_and_far_traffic` | 40 | Listener in front of a brick building; patrol car 501 (`police_interceptor`, siren switched on, wail) driving behind it at 15 m/s; seven `v8_muscle` cars (502 to 508) with the engine budget shed to 2 (the `_adaptiveBudget` a mixer at its ceiling leaves), so all seven borrow; parked car 509 whose driver holds the horn from frame 20 to 60. | S3: `-700502` to `-700508` are played and re-placed with `Occlusion=0`, `EffectiveDistance=0`, `TargetRegionId=-1`, and `ApertureFactor=1` and `TransmissionBleed=1` (the emitter's defaults, not 0), while their `Path` lines say otherwise: `-700504`, behind the building, `Occlusion=0.95 TransmissionBleed=0.00393809 EffectiveDistance=48.862286 EqMid=0.0009898634` (frame 2). S2: `-2100509` has the exhaust's apparent position in its `Path` lines, `(6,0.8,7.77)`, and the grille's in its `Place` lines, `(6,1.1,11.2)`; at frame 110 it is stopped, then handed a `Path` and a whole `Place`. S1: `-800501` and `-800509` have `Place` lines and no `Path`. | P3a: the `-700xxx` `Place` lines carry the path's occlusion, bleed, distance and region; `-800xxx` gain the path; the `-2100509` and `-900501` `Path` lines fold into `Place`, with one apparent position. P3d: no `Path`/`Place` after the `Stop`. P4: which cars are live. P1's S5 change does not touch it (the siren is on throughout). |
| `train_behind_wall` | 54 | Listener in front of a brick building; a two-car `light_rail` set (sources 601 up) at 12 m/s behind it, signalling for a crossing at frame 20. | The train's six voices (`-2300000` to `-2300005`) each get a `Path` line (the 2026-10-10 fix) and a `Place` line every frame; the source entities 603 to 612 get `Path` lines and no voice (asked for through `_trainPathIds`). | P3a: the train voices' `Path` lines fold into `Place`; the source entities' `Path` lines go. P7: the notch (`PowerLever`) from the server. |
| `places_and_woods` | 50 | Walking north out of a 6 m roofed brick room through its doorway; outside, `foliage:park_tree` 701, `water:park_fountain/elm_park/0` 702 and `machine:ac_condenser` 703; nine `foliage:pine` 170 to 200 m off, made into four woods by `RefreshWoods`. | S1: the woods (`-20000000`, `-20000001`, `-20000003`; `-20000004` starts and stops at once) and their places (`-245000001` down) have `Play` and `Place` lines and never a `Path`; their band gains stay as they started, indoors. The tree's places (`-5008413` to `-5008418`) and the tap's (`-5008425` to `-5008427`) get `Path` lines from the spread branch. | P3b: the woods and their places gain path-carrying `Place` lines. P4: the place budget's order. |
| `talker_and_aids` | 55 | Riding car 801 (`i4_economy`, driven) north up a 6 m two-way asphalt carriageway with one side's wheels over the centre line, 4 to 6 m/s; somebody on voice chat (802, packets through `ReceiveVoice`) walks out from behind a garden wall ahead on the right. | S11: the centre rumble `-964003` is re-placed twice a frame: the game thread's line with the pitch the speed asks for, then VoiceManager's held emitter putting `Pitch=0.41666666` and its first position back (frame 120: `Pitch=0.62326664`, then `Pitch=0.41666666`). The talker `-1400802` carries its own path (occlusion 0.79 at frame 2, 0.20 at 21, 0 at 53) and has no `Path` line; its wall copies are `-1700000` (from frame 23), and `-1700001` and `-1700002` (frames 65 to 72). | P3b: one `Place` a frame for `-964003`, its pitch following the speed. P6: the talker asked under a probe id (the replay has no Steam Audio, so its values may not move). |
| `high_ids` | 60 | Riding car 32,598 (all of Magnolia) across a 30 m concrete-walled yard whose asphalt is a junction to the aids, so the guide beeps; a recorded loudspeaker 18,024 (Albany's join); parked patrol car 61,001 with its siren on. | S6: the loudspeaker's copies are `-120120` and `-120121`, in the transients' band. The siren's voice is `-961001`, the guide beep's second id: at frame 38 `Play -961001 ... SoundId="SYNTH/drive_guide"`, and from then on the one voice is re-placed every frame as the siren and as the beep in turn. | P3c: ids from the allocator; the copies leave the transient band and the siren and the beep become two voices (the stream equal to today's under the id map, except those lines). |

- **The band test.** `OpenFPS.Tests/VoiceIdBandTests.cs` (its own file; `VoiceBudgetTests`' three-band check
  stays as it is). It reads every band in the appendix from the code's own constants and formulas (the
  private ones by reflection), except the source probes, which are questions to the worker and never a
  voice. Entity ids are checked to 250,000, past the last overlap S6 names; counters never reused are
  checked unbounded. `NoTwoVoiceIdBandsShareAnId` is the must-pass, skipped as the known failure with S6's
  id, the codebase's pattern for a known failure; `TheVoiceIdBandsOverlapWhereS6Says` runs and holds
  today's 47 overlaps exactly, each with the entity id or count it starts from, so a part that moves a band
  is told. Among them: reflection copies into the transients from entity id 14,000; sirens onto the
  driving aids, beacons and birds from 61,000, 67,000 and 70,000; engine echoes into the borrowed band
  after 100,002; trains into rain after 16,667 admissions. P3 updates the list as it moves bands, and
  P3c empties it and un-skips the must-pass.
- **S3 is narrower than written.** The emitter's constructor sets `ApertureFactor` and `TransmissionBleed`
  to 1, so a borrowed voice's aperture is 1, not 0, and the 400 Hz diffraction low-pass does not happen.
  What the stream does show is occlusion 0, bleed 1, effective distance 0 and the global region on every
  re-placement, against a path that says 0.95, 0.004 and 48.9 m; the band gains come from the path pass.
- **The replay's vehicles faced backwards. Fixed 2026-10-11**, tests only. `Replay.Facing` made -Z forward;
  the server's vehicles face +Z (heading `atan2(x, z)`, `CreateFromYawPitchRoll(heading, 0, 0)`: `RaceLine`,
  `VehicleSystem`, `RailSystem`, the walkers in `VehicleSystem.Parking.cs:324`), and its traffic never
  pitches. So in every stream a moving car's grille, siren and horn were at its tail and its exhaust at its
  nose. `Replay.Listener` had the same sign: it turned the head by minus the yaw where the client turns it by
  plus (`ClientPhysicsSystem`), so a listener riding east faced west, and it set the yaw in degrees where the
  client keeps radians. Both now turn as the game does. All eight streams were regenerated; the voice lines
  that changed are the moving vehicles' outlets and what follows from them (engine, intake, horn, siren and
  cabin positions and their paths, the engine echoes, the riding car's driving aids, the trains' `Direction`),
  and the walking talker, who now faces the way they walk; the listener's fix changes only the `Listener`
  and `EarWind` lines. Parked cars are unchanged. It hid none of the findings above; the figures quoted
  here are the regenerated streams'.

#### P1. The pops: the source pool (1 session)

- **Changes.** In `AsyncAcousticWorker`: read back only the sources staged this tick and keep the rest on
  their held answer; `ClearSource` a lent source before its new owner's inputs; record each result's
  provenance (served, held, lent, hand-rolled) with `TraceProvenance`, which exists. Measure with
  `--pop-hunt ear=-11.05,4.9,-100.84` and 40 cars before and after, and say which mechanism it was. In
  CAS, ask about a siren car only while its siren sounds (S5).
- **Files.** `AudioEngine/Acoustics/AsyncAcousticWorker.cs`; `ClientAudioSystem.cs` (`UpdateSirens` and
  `VehiclePath` only); worker tests (`AsyncAcousticWorkerTests`, `AcousticSourcePoolTests`).
- **Conflicts.** P3 rewrites `UpdateSirens`; land P1 first or leave S5 to P3. P6 touches the worker's read.
  Runs alongside P2 and P5.
- **Guards show.** Replay streams unchanged (the replay's worker answers on the test thread without Steam
  Audio, `EmitterStreamReplayTests.cs:22-25`), except `sirens_and_far_traffic` if S5 is done here: the
  siren car's requests while off are gone. Render fingerprints unchanged. The proof is the pop count, by
  ear and by `--pop-hunt`.
- **Done 2026-10-11 (unheard).** S4 was not the pops. A full pool is all served sources, so nobody turned
  away holds one and the read-back never took a stale answer (0 in 60 s with 70 and with 120 sources); a
  lent source's inputs were replaced at once. Both are closed anyway: only what was staged this tick is
  read back, and a lent source is cleared. Every answer carries what traced it and the source position and
  ear it was traced for (`AcousticPathData.SimulatedFrom`, `ListenerPosition`, `Provenance`), every asker
  takes it by one rule (`AcousticPathData.Answers`), and a tick traces from the newest ear. S5 done: a
  siren that is off asks for nothing. The pops themselves are geometry and the barrier search, found with
  still sources on `--path-probe`: on the Main Street pavement (41-56 a minute) the way over the top gave up
  after four boxes (`OpeningRoutes.OverTheTopPasses`, unbounded in practice now: 0 a minute); in Marlow
  Tower's floor 1 stairwell (4-11 a minute, unchanged) a 1.07 m stair guard is charged as a whole wall
  because the 3 cm way over it runs on through the brick front (todo, Acoustics). Path lag (a car against
  its answer) the same within the machine's own noise: two rounds run in turn, medians 400 and 333 ms
  before and 383 and 367 after on the pavement, 567 and 417 before and 400 and 500 after in the stairwell.

#### P2. One table of model kinds (1 session)

- **Changes.** A `ModelKinds` table in the library: for each `kind:` prefix, its level and size, headroom,
  places layout, synth factory, and body kind (vehicle, aircraft, train, machine, walker, fixture), plus a
  declared operator on machine specs (the push mower is walked behind). `LookUpPhysicalLevel`,
  `PhysicalHeadroom`, the provider's factory switch and `NaturePlace` ladder, `ExtendedSources.Layout`'s
  prefix tests and `ModelKinds.TryModelOfSound` read it. Every `engine:`, `rail:`, `aircraft:`, `machine:`
  test in S8 becomes a read of the body kind. A spec that fails to load is logged once and is an error in
  tests, not "not a model".
- **Files.** CAS (the lookups, 1040-1214, and the prefix tests listed in S8), `FmodAudioProvider.cs`
  (2240-2345, 2709-2760), `OpenFPS.Sound/Core/Nature/ExtendedSources.cs`, `OpenFPS.Sound/Editing/ModelKinds.cs`,
  `OtherBodies.cs`, `Scope.cs` (`Classify`), `DrivingAids.cs`, `RainField.cs` (674, 712),
  `OpenFPS.Common/AudioEmission.cs`, `OpenFPS.Sound/Fuel.cs`, `OpenFPS.Server/Core/OccupancyService.cs`, the
  machine specs.
- **Conflicts.** CAS with P3 and P4: P2 touches the lookups and single prefix tests, P3 the voice methods;
  land P2 first, or run them in sequence. `Scope.cs` and `RainField.cs` with P5: give `Scope.StandsOnRoof`
  and `RainField.cs:664` to whichever runs second. Runs alongside P1.
- **Guards show.** No change in any stream or fingerprint. One new test: every kind a map file names
  resolves in the table. Also here: the two caches that never forget (S26) become fields of the table, and
  the silent fallbacks of S27 that belong to a model (horn kind, aircraft size, cabin door port) fail at
  load.

**P2 as built (2026-10-11).** `OpenFPS.Sound/ModelKindTable.cs` (namespace `OpenFPS.Common`), next to
`MachineRegistry` and `ModelLibrary`. One row per prefix (engine, machine, aircraft, rail, siren, bell, gate,
water, fire, foliage, wood, flow, shore, stove, horn, rain): its `ModelSynth` (which constructor the
provider uses), its `ModelBody` (Fixed, Walks, Rolls, Flies, Rails), the `ModelLibrary` kind the editor
edits it as, and how an id of it declares its level and size, headroom and places. `KindOf`, `TryLevel`,
`Headroom`, `BodyOf`, `IsVehicle`, `VehiclePreset`, `KnownVehiclePreset`, `Vehicle` and `Resolves` are the
questions; `TryRailSource` and `TryWaterTap` are the two keys' parsers, which the client's own now call.

- Read by: `LookUpPhysicalLevel` and `PhysicalHeadroom` (their memos stay in CAS, cleared by
  `ModelChanged`); the provider's factory switch and `NaturePlace`; `ExtendedSources.Make` (each kind's layout
  is a method the row points at); `ModelKinds.TryModelOfSound`; the S8 tests: CAS `PlaysCopies` (the
  `ENGINE/` test goes), the ride-in preset, `ChooseLiveEngines`, `UpdateSirens` (its prefix lines only),
  `SilentSignal`, the horn's mount, `UpdateHeldHorns`, `Grille`, `PhysicalAircraft`, the emitter pass's
  shore, aircraft, rail and engine tests, `VoiceKey` and `Dry`; `CabinWalls.Vehicle`, `DrivingAids` (both
  chassis reads and the crossing bell), `RainField` (674 and 712), `Scope.Classify`, `OtherBodies`,
  `AudioEmission.LocalOffset`, `Fuel.ForThing`, `OccupancyService`'s door chime.
- The push mower declares `SmallMachineSpec.WalkedBehind`; `OtherBodies` gives legs to a body whose sound
  names no model or a model whose body walks, and no longer names `machine:mower_push`.
- Fails loudly: a model that will not load, an unknown vehicle preset and a synthesised id that names no
  kind are each logged once by id (`ModelKindTable.Fail`, `Failures`). `ModelKindTableTests` loads every
  shipped map (city, speedway, the test map, Albany and Magnolia, with traffic, trains and crossings
  spawned) and every prefab: every synthesised id resolves, no recording wears a model's prefix, and on
  every body placed the footsteps and the scope's answer equal the replaced lists'.
- Disagreements and what was chosen, none changing behaviour:
  - Scope and OtherBodies on mowers. OtherBodies walked the push mower by its id; Scope's comment said
    "somebody pushing a push mower is a person" but its code made every NPC with a `machine:` sound not a
    person. The table says a small machine's body is Fixed (its spec's doc: "stands still and runs") and the
    push mower's Walks. OtherBodies keeps its footsteps; Scope picks out a person only when the sound names
    no model, so the push mower stays unpicked as before. Open for Cody: whether the scope should call it a
    person (one line in `Scope.Classify`).
  - The riding mower moves on the city but its body is Fixed, as every small machine's is: the scope did not
    call it a vehicle and still does not. A `Rolls` body for it would make it a vehicle to the scope.
  - IsSynth. OtherBodies asked it for aircraft, rail and machine but not engine; the table reads the id
    alone. No shipped recording wears a model's prefix (tested), so nothing changes.
  - A body whose sound is a fixed model (a fire or a fountain on an NPC) walked under the old list and does
    not now. Nothing shipped is one (tested).
  - `ModelKinds.TryModelOfSound` misses rail, wood, horn, aircraft, gate, siren and rain. Kept as it was:
    answering for rail and wood would let the editor restart and edit trains and woods through their sound,
    a change in the editor. Their rows have no `LibraryKind` until that is decided.
- The leftovers, done later the same day:
  - The server's own prefixes read the table. Writers: `ModelKindTable.IdOf(synth, rest)` in `VehicleSystem`
    (engine, machine, aircraft), `CrossingSystem` (bell, gate), `CompositeService` (a parked car's engine),
    `DrainageNetwork.Voice` (flow), and `ModelKindTable.RailSource`, beside its parser, in `RailSystem`.
    Readers: `WorldEditor.Menus.CategoryOf`, `MapDrainage.PlacedWater`, `MapTiles`' foliage, `Program.IsTap`
    (whose silent catch now logs by id) and `ClientWorldState`'s foliage. The server reaches the table through
    OpenFPS.Common, so no reference was added. Left: `CompositeService.AircraftPrefix` is a map record's
    template id, not a sound id; the client's own voice keys in CAS and the provider (`rail:...@k`, a water
    tap's feature key, a siren's `PhysicalKey`) and the library's key owners (`Fire.Key`, `GasHob.Prefix`,
    `Honk.Prefix`, `WoodChorus.Prefix`, `Waves`) still spell theirs.
  - S26: neither cache is kept. `AudioEmission.LocalOffset` and CAS `OutletSeparation` read the vehicle from
    `MachineRegistry.VehicleFor`, which is memoised already and renewed by a model edit or a registry load,
    so a preset asked for before it was known, or an edited exhaust, is right once it is. An unknown preset
    is logged once by the table (`KnownVehiclePreset`).
  - S27, the model fallbacks: each keeps its value and is logged once by id (`ModelKindTable.Fail`). A horn
    with no model (`Honk.LevelDb`, 110 dB), an aeroplane with no propeller, fan or nozzle to size it
    (`AircraftLevel`, 2 m), and an air-braked vehicle's door with no air system or no door port
    (`CabinPaths`, its `catch { }` gone; a metre from the front, kerb side). `ModelKindTableTests` checks no
    shipped horn, aeroplane or door falls back. The other S27 items (the rain patch, the bump, the hand-over
    and teleporter levels, CAS's `"SYNTH"`, span and length) are measured or voice values, for P3d.
- Guards: all eight replay streams and the render fingerprints unchanged, not regenerated; the boundary
  ratchet gains the one library file and no crossing.

#### P3. The registry, one writer, one start (3 to 4 sessions, in order)

The heart of it. Four sessions, each mergeable on its own.

- **P3a. Moving things.** The registry with today's id formulas kept (allocation moves, numbers do not).
  The writer for the voices of a moving thing: engine, intake, cabin paths, siren, horn, held horn,
  borrowed voice, train slots and signals. `VehiclePath`'s freshness rule becomes the worker's read for
  all (S13), asked by movement (S17). The path pass's derived-voice branches (CAS:723-741), the trains'
  writer (`ClientAudioSystem.Trains.cs:310-327`) and `DistantEngine`'s bare emitter go. Engines' and
  machines' own voices keep their emitter through VoiceManager but carry their path (`CarriesPath`), so
  the path pass no longer writes them either.
  - Files: CAS (Update's path pass, `FrontVoice`, `CabinVoices`, sirens, horns, held horns,
    `DistantEngine`, `ProcessAudioEmitter`'s emitter), `ClientAudioSystem.Trains.cs`, a new
    `Voices.cs` (or `VoiceRegistry.cs`) beside them, `AudioEngine/Acoustics/AsyncAcousticWorker.cs`
    (`TryGetResult` with freshness).
  - Guards: streams change, and only in named ways. Intake (`-800000`) voices gain their path every frame
    (were frozen). Borrowed (`-700000`) voices gain aperture, occlusion, bleed and apparent position from
    their path (were 0 and the true position). Horn and held-horn `Path` lines go and their `Place` lines
    carry the band gains, with one apparent position. Sirens and trains: their `Path` lines fold into
    `Place`. The car's own voice: `Path` lines fold into `Place` with the same values. Fingerprints
    unchanged.
- **P3b. Everything else that stays.** Woods and their places, spread places, talkers and their copies,
  own voice, reflections' copy slots, step echoes, engine echoes, rain slots and parts, near drops, birds,
  beacons, driving aids, the reference voice, transients: each allocates and stops through the registry,
  with ids still as today. The `id < -5000` test, `IsWood`'s range and `IsEchoVoice` go: what a voice is
  comes from its entry. The rumble and tones have one owner (S11).
  - Files: CAS (the rest), `ClientWorldState.cs` (wood ids), `WorldAudioPlayer.cs` (transient ids and
    `SourceProbeId`), `RainField.cs`, `BirdLife.cs`, `BeaconAids.cs`, `DrivingAids.cs`,
    `AudioEngine/Acoustics/EngineReflections.cs`.
  - Guards: woods gain `Path`-carrying `Place` lines (were none); the rumble's pitch follows speed
    (`talker_and_aids`); everything else unchanged.
- **P3c. Seeds, then ids.** First the seed: every synth is seeded from its entry (owner and kind), with
  values equal to today's, so the fingerprints and streams do not move. Then, in its own commit, ids from
  one allocator with reuse (S6, S18), and the streams regenerated with a test that the new stream equals
  the old under the id map. The band test becomes "no two live voices share an id".
  - Files: the registry, `FmodAudioProvider.cs` (factory seeds), `SpatialEmitter.cs` (a seed field), the
    tests.
  - Guards: fingerprints unchanged; streams equal under the id map.
- **P3d. One start and declared levels.** `Voices.Start` is the only way to start a world voice; one
  level law; the 116 dB fallback, the 1.9 m grille, the 4.2 m rail horn and the 40 m beacon range read
  from the model or fail (S15); every range is the audible range of the entry's level (S24); offsets
  and heights from the owner's body (S28); the remaining fallbacks of S27; the facade's lost stop and
  silent replacement (1.3, last paragraph).
  - Files: CAS, `ClientAudioSystem.Trains.cs`, `BeaconAids.cs`, `OpenFPS.Sound/Vehicles.cs` and the train
    profiles (mount points), the registry.
  - Guards: streams unchanged for the five presets in the scenarios (they declare their levels), a named
    change where a fallback was standing in.
- **Conflicts.** P3 owns CAS for its whole run: P2 and P4 do not run beside it. P3b touches
  `WorldAudioPlayer.cs` and `RainField.cs`, which P5 and P2 touch at other lines. P6 is easiest as part of
  P3b.

**P3a as built (2026-10-11).** Unheard. Ids keep today's formulas; `VoiceIdBandTests`' 47 overlaps are all
still there. The wire hash does not change (only `OpenFPS.Client.Core` and the tests).

- **The registry.** `VoiceRegistry` (`OpenFPS.Client.Core/VoiceRegistry.cs`). An entry per voice id: its
  owner (entity id and `World`, one more each time the listener leaves a map), its `VoiceKind` (Engine,
  Aircraft, Intake, Borrowed, Horn, HeldHorn, Siren, TrainField, TrainSignal, Cabin), its `Mount` (where on
  the owner, in the owner's frame), its declared level and the frame it was last declared in. An owner
  declares each of its voices every frame it sounds; an entry not declared in a frame is swept at its end.
  How a kind is heard from is a table (`HeardFromOf`): at its point (engine, aircraft, intake, borrowed:
  dead-reckoned from where the car is now), toward the edge its path comes round when blocked (horns, held
  horns, sirens, a train's voices: `SirenApparent`), or inside (the cabin paths, and the engine of the
  vehicle you sit in).
- **The pass.** `ClientAudioSystem.Voices.cs`. `PathOf(owner)` is worked out once a frame per owner: the
  worker's answer under P1's one rule (`TryGetDirectFor`, `AcousticPathData.Answers`), with `VehicleShadow`;
  with no answer, `FirstAnswer` stands in and the worker is asked now. The path pass seeds it for every id it
  serves. `HandPath` declares a voice and hands it, in the one emitter that places it, its position, apparent
  position, occlusion, band gains, air, aperture, bleed, distance, region and room gain, with `CarriesPath`
  when the answer is fresh; a stand-in places the voice and leaves its tone as it was (refused, a voice keeps
  what it has). `SpatialEmitter.RoomGain` is new, part of the path with `CarriesPath` and taken at a start.
  Owners still start and stop their own voices (P3d).
- **Asked.** The path pass asks about every owner of an entry (last frame's), with the horns' and borrowed
  cars' own sets, on the one cadence every source has (every frame within 15 m, every second to 50 m, every
  tenth beyond). Before, a horn's, a siren's or a train source's owner without a voice of its own was asked
  every frame (`VehiclePath`).
- **Replaced.** The path pass's branches for horns, held horns, sirens (with its copy of `SirenApparent`)
  and borrowed voices; `VehiclePath`; the trains' writer (`SetAcousticPath` in `PlaceTrainVoice`) and
  `_trainPathIds`; `DistantEngine`'s bare emitter (S3); `FrontVoice`'s path, frozen at its start (S1); the
  engine's and the aeroplane's own path fields. The path pass still serves the fixed machines, recordings,
  trees and their spread places, and reflection copies (P3b).
- **Writers removed.** No registered voice is handed a path by `SetAcousticPath` (S2): an engine's or an
  aeroplane's own voice carries its path through the budget, so VoiceManager's held emitter is the
  registry's output, re-applied unchanged. The path pass no longer hands a path to an id with no voice (a
  far car's own id, a train's sources: no-ops). S11: the lane rumble is a voice of your car, and the aid now
  renews the emitter the budget holds (`Refresh`) instead of re-placing it past the budget, which then put
  the first pitch and place back.
- **Streams.** Each voice compared by what the mixer holds at the end of each frame, old stream against new
  (a model of the provider's fields: `Play` takes all, `Place` the band group only with `CarriesPath`,
  `Path` all but on an interior engine). Changed:
  - The intakes take their path every frame (S1). traffic_rain's `-800305` started behind the brick with the
    mid band at -60 dB and kept it after its car came out at frame 200 (now 0 dB); every intake's air loss
    followed its distance from then on (`-800101`, `-800102` in walk, `-800202` in drive, `-800301` to
    `-800305`, `-800501`).
  - The borrowed voices carry their car's path (S3): distance 0 to 48.9 to 160 m for `-700502` to `-700508`,
    and behind the building `-700504` and `-700505` occlusion 0.95 and bleed 0.004 where they were re-placed
    as 0 and 1; their first frame's bands and air are the stand-in's, not the emitter's defaults.
  - train_behind_wall: the six train voices' distance and air are up to a frame older, from the one cadence
    (their sources at 25 m are asked every second frame, not every frame); their tone the same.
  - talker_and_aids (its own commit): the centre rumble `-964003` has one Place a frame, its pitch following
    the speed (0.417 at 4 m/s to 0.623 at 6), its place riding with the car.
  - Everything else the same, its `Path` lines folded into its `Place` lines: the engines, the drive's horn
    `-1200202`, the held horn `-2100509` (its one apparent position, the grille's), the sirens `-900501` and
    high_ids' `-961001`, the outlet `-800509`, the cabin. Every `Play` line gains `RoomGain=1`.
    places_and_woods and high_ids are the same voice for voice.
  - All eight are the same at 4 processors as at 12.
- **Tests.** `VoiceRegistryTests`: one of every kind in a replay, none handed a `Path`, each carrying its
  own; a borrowed car behind a building occluded and at its distance; the rumble following the speed; how
  each kind is heard from; an entry's life and world. Tests that read a vehicle voice's path from
  `SetAcousticPath` read it from its emitter (`RecordingMixer.Carried`); `SirenApparentTests` asserts the
  siren's one writer.
- **Left, and why.**
  - S21 not folded in (closed as deferred the same day: Cody kept the bus's own law until geometry stage 6). On
    the shared law a bus's loss starts at 5 dB at the shadow's edge (Maekawa at a path
    difference of zero, where `VehicleShadow` starts at 0) and is read at 200, 1250 and 8000 Hz, so under
    the 15 dB cap the broadside bus of `VehicleShadowApplyTests` takes 11/15/15 dB: the mid and high bands
    both at the cap, where the test (and the law's purpose) has most off the top. The call is three lines;
    the cap and the grazing step need deciding first. No replay scenario has a vehicle in another's way.
  - S17 not done: one cadence now, but by distance, not by movement.
  - A blocked engine is heard from its point, as before, not round the edge as its horn is. The path pass
    meant the edge, and the engine's own emitter has overwritten it since 2026-09-14 so a moving car is
    dead-reckoned. Kept; one row of `HeardFromOf` if it should change. (Changed in P3b, Cody's decision: "P3b
    as built".)
- **Next.**
  - P3b: every other voice an entry with one writer: woods and their places, spread places, talkers and their
    copies, own voice, reflection copy slots, step echoes, engine echoes (`EngineReflections.ApplyPath` still
    calls `SetAcousticPath`), rain slots and parts, near drops, birds, beacons, the driving aids' tones and
    beeps, the reference voice, transients; the fixed machines' and recordings' own voices onto `HandPath`, so
    the path pass's `SetAcousticPath` and its spread branch go; then the `id < -5000` test, `IsWood` and
    `IsEchoVoice`. P6 with it (talkers under a probe id).
  - P3c: seeds from the entry's owner and kind (both on the entry now), with values equal to today's; then ids
    from one allocator, freed when a voice stops, streams equal under the id map, and the band test becomes
    "no two live voices share an id".
  - P3d: one way to start and stop (owners still write `IsPlaying ? Update : Play` each, and the held horn is
    still placed after its stop at frame 110 of sirens_and_far_traffic); the entry's `Mount` and level read
    from the model, with the 116 dB, 1.9 m grille, 4.2 m rail horn and interior 0.7 m (now in `HandPath`)
    fallbacks failing loudly; ranges from the entry's level; the facade's lost stop and silent replacement.

**P3b as built (2026-10-11).** Unheard. Ids keep today's formulas; `VoiceIdBandTests`' 47 overlaps are all
still there (no band moved). The wire hash does not change (only `OpenFPS.Client.Core`, the lab and the tests).

- **Kinds.** `VoiceKind` gains twenty, and `HeardFrom` three; how each is heard is `HeardFromOf`, one table:

  | Heard from | Kinds | Its path |
  |---|---|---|
  | `Point`, its own point on the owner | `Machine` (a standing machine, a tree, a fire, a fountain's tap, a bell), `Wood` | the owner's, handed by the pass (`HandPath`) |
  | `TowardEdge` (`SirenApparent`) | the horns, sirens and trains of P3a, and since the second commit `Engine`, `Aircraft`, `Intake`, `Borrowed` | the owner's |
  | `Edge`, where the owner's path arrives from | `Recording` (an authored recording, a loudspeaker's render) | the owner's |
  | `Spread`, its own point and its middle's bearing moved by its offset | `Place` (the outer places of trees, fires, taps and woods) | its middle's, carried in the copy of the middle's emitter |
  | `Inside`, with the listener | `Cabin`, `OwnVoice` (your voice's send into your room), `OwnStep` (your steps and landings), `Aid`, `Reference` | none: its room is yours |
  | `Own`, its own point by a path it works out there and carries | `Reflection` (a source's wall copies), `EngineEcho`, `Talker`, `TalkerCopy`, `OwnVoiceCopy`, `Footstep` (somebody else's, and their landings), `StepEcho`, `Rain`, `RainDrop`, `Bird`, `Beacon`, `Transient` | its own, in its emitter |

  Only kinds heard by their owner's path have their owner asked about (`VoiceRegistry.TakesOwnersPath`): a
  talker's player, a step's walker and a bird are not. A one-off (`DeclareEvent`: steps, landings, step echoes,
  near drops, birds, beacons, the aids' beeps, the reference voice, the transients and their copies) is an
  entry until its voice has stopped, after 30 frames to start (`EventStartFrames`); a voice its owner places
  only now and then (the copies of a talker's and of your own voice between searches, a source's wall copies in
  a frame with no answer) is kept as declared (`Keep`). `EngineReflections` and `RainField` are library files,
  so they say what they voiced by plain ids (`Voiced`) and the host declares it; the boundary gains nothing.
- **One writer each.**
  - The standing machines, trees, fires, fountains and bells, the woods, and the recordings and loudspeakers:
    their own voice through `HandPath`, its path in the emitter the budget holds (`CarriesPath`). The pass's
    `SetAcousticPath` for them is gone; it lost to the emitter on every field both wrote.
  - The places: the copy of the middle's emitter now carries the middle's path. The spread branch is gone.
  - The engine echoes: the echo's path in its own emitter (`EngineReflections.CarryPath`). `ApplyPath`'s
    `SetAcousticPath` is gone; the emitter it lost to set the distance to 0 and the bleed to 1.
  - Footsteps and landings had a second writer nobody named: the pass asked the worker about every voice id
    above -5000 that was not an entity at the voice's own position (`GetSoundPosition`) and handed it the
    answer. That was your own steps (-100 band), everybody else's (-300 band) and the landings (-50). Nothing
    asks about a voice's own id now. Somebody else's step already carried its path; their landing carried none
    and had only that one, so it carries its path as their steps do (`CarryThePath`). Your own steps and
    landings are with you (`Inside`): nothing asks about them.
  - The reflection copies, talkers and their copies, your own voice and its copies, step echoes, rain, drops,
    birds, beacons, the aids, the reference voice and the transients had one writer already (each carries its
    path or has none); now each is an entry with its kind.
- **Gone.** The pass's two `SetAcousticPath` calls and its spread branch; the `id < -5000` test and the
  `GetSoundPosition` fallback (the pass asks only about things in the world, `world.Entities`);
  `ClientWorldState.IsWood`'s range (an instance set of the woods it made, and the audio reads the snapshot's
  `WoodChorus`); `EngineReflections.IsEchoVoice`; `MovingThingKind` (now `OwnVoiceKind`, for every entity's own
  voice). No game code calls `SetAcousticPath` now; the provider keeps it for the lab's spikes. Every replay
  scenario is checked: every voice written in a frame is an entry at its end, and no `Path` line is written
  (`VoiceRegistryTests`).
- **P6.** A talker is asked under `WorldAudioPlayer.SourceProbeId(id)`, not the player's own id (S13), with
  their body left out of the hand-rolled tracer's rays (`AcousticRequest.LeaveOut`; the worker's own fallback had
  traced a one-off's source under its probe id with the speaker's body in the way, and one-offs now say whose it
  is too). S23: the hand-rolled path leaves out the body the client says is the listener's
  (`SpatialAcoustics.ListenerBodyId`, set with `OwnEntityId`), not the first player within 2 m of the ear. In
  `AsyncAcousticWorker` only the request's new field and the line that reads it. Not done, and why:
  - Beacons: whether a blip sounds at all is decided on its path (`BeaconAids.Reaches`: no more than half
    blocked unless in sight), tuned by ear (2026-10-10, the barn). On the simulator's answer which beacons are
    heard through which walls moves; that wants Cody's ear, and is a small change after it: ask under
    `SourceProbeId(sourceId)` each blip, decide on the answer when there is one, the tracer until then.
  - Birds: a bird is no entity, so a probe needs an id of its own, which is P3c's allocator; and a call is
    over before an answer comes, so only the next call from the same perch gains. Plan: a probe per perched
    group, asked as a group wakes, its answer moved to each bird.
  - Rain: a patch is heard by the paths to what it falls on (up to four points a slot, eight slots), one path a
    frame. Asked of the worker that is up to 32 sources on the pool P1 sized, and traffic_rain's stream would
    change by the simulator against the tracer. Plan: after P4, the slot's loudest point asked under a probe,
    the rest on the tracer, measured with `--pop-hunt` for the pool.
- **A blocked engine round the edge (second commit; Cody, 2026-10-11, "yes").** Engines, aeroplanes, intakes
  and borrowed cars take the horns' rule: their own point while the way is clear; blocked, `SirenApparent`'s
  point on the edge's bearing at their own distance, so a change of edge turns round the head (the provider's
  `TurnToward`, 120 degrees a second while blocked) and never through it, the bearing flips of 2026-09-28.
  The interior engine is still heard inside. No stream changed and none was regenerated: the replay's worker
  answers with the hand-rolled tracer and its worlds have no opening routes, so every answer is from the source's
  own point and `SirenApparent` hands each engine its own point as before. The rule is tested on an answer.
- **Streams** (first commit), each voice compared by what the mixer holds at the end of each frame, old stream
  against new (the P3a model of the provider's fields). The other calls (listener, reverb, fades, stops,
  registrations) are line for line the same, and every `Play` count too. Changed:
  - The engine echoes, every one in walk (`-600000` to `-600003`), drive (`-600000`, `-600001`), traffic_rain
    (`-600000` to `-600003`), sirens_and_far_traffic (`-600000`) and high_ids (`-600000` to `-600004`): two
    writers. Their `EffectiveDistance` is the echo's own path (12.9 to 47.1 m) where the emitter put 0, and
    their `TransmissionBleed` 0 where the emitter's default put 1; at its first frame each starts with its air
    (1.47 dB in the high band at 12.9 m, walk's `-600000`) where the path handed before the voice existed was
    dropped and the air came a frame later. For a reflection in the mixer the distance and bleed are inert
    (bleed only pulls an apparent position toward the source's, and an echo's are one point; the distance is
    read for a one-off's overload and the order of engines and machines in the render pool).
  - The woods and their places in places_and_woods (`-20000001` with `-245000013` to `-245000018`, `-20000002`
    with `-245000025` to `-245000030`, `-20000004` with `-245000049` to `-245000054`): stale (S1). Never asked
    about, they kept `FirstAnswer`'s stand-in all their lives; from frames 12 to 15 they take the worker's answer
    (distance 176.60 to 176.41 m, high-band air 18.59 to 18.57 dB for the first). Their band gains are the same:
    clear either way in this scene. `-20000000` is stopped at frame 3, before an answer.
  - Everything else the same: the talker `-1400802` (one writer already; on its probe id the same answer),
    the trees' and the tap's places, the machine, the loudspeaker and its copies, the trains, the aids.
  - All eight are the same at 4 processors as at 12.
- **Tests.** `VoiceRegistryTests`: every voice written in each of the eight scenarios is an entry, and none is
  handed a `Path`; how every kind is heard and which have their owner asked; a one-off's life and `Keep`; the
  blocked engine's rule. Tests that read an echo's path from `SetAcousticPath` read it from its emitter; the
  step test asserts your own step is handed no path; two tests that leaned on the tracer's 2 m guess say whose
  body the listener is.
- **Left.**
  - A voice fading out after its owner stopped declaring it (an engine, a machine, a place or an outlet
    retiring) is not an entry while it fades, and the budget places one voice once more in the frame it is
    stopped (places_and_woods, `-20000000` at frame 3: the facade's lost stop); the registry test allows the
    second by name.
  - The lab's spikes still hand paths to their own voices with `SetAcousticPath`.
- **Next.**
  - P3c: first the seed, from the entry's owner and kind (both on every entry now), equal to today's so the
    fingerprints and streams do not move. Then, in its own commit, ids from one allocator, with the probe ids
    beside them (birds and rain need probes of their own for P6): a pool id is free when its entry is gone,
    which for a one-off is when its voice has stopped (`Sweep` already knows), and a retiring voice holds its id
    until its fade ends. The woods' ids move from `ClientWorldState` into it. Streams equal under the id map;
    the band test becomes "no two live voices share an id", on top of the test that every voice written is an
    entry.
  - P3d: one way to start and stop (owners still write `IsPlaying ? Update : Play`; the held horn is still
    placed after its stop, and the budget places a stopped voice once more); the fades owned by the registry,
    so a retiring voice stays an entry; the entry's `Mount` and level from the model, with the 116 dB, 1.9 m
    grille, 4.2 m rail horn and interior 0.7 m fallbacks failing loudly; ranges from the entry's level (S24);
    the remaining fallbacks of S27; the facade's lost stop and silent replacement.

**P3c as built (2026-10-11).** Unheard; nothing heard changes. S6 is closed: no two live voices share an id. The
wire hash does not change (only `OpenFPS.Client.Core`, the lab and the tests). Two commits.

- **Seeds** (first commit). `SpatialEmitter.Seed` is new, and the mixer draws every voice's chance from it, never
  from its id: the engines, machines, aircraft, bells, stoves, horns, water, fires, trees, woods and shores, an
  engine echo's smear and a recording's copy's wash. The host sets it from `VoiceRegistry.SeedOf(kind, owner,
  part)`: the table of the ids voices had, kept as seeds so nothing rendered moves (an entity's own voice is
  seeded by its entity id). Every voice's id is asked of the registry by the same three (`IdFor`), which was
  then still that table. The rain and the engine echoes, library files, are handed both by their host
  (`RainField.SlotVoice` and `DropVoice`, `EngineReflections.VoiceOf`); the aids name their voices by
  `AidVoice`. The lab's spikes seed their synths with the ids they use. Streams: every `Play` line gains
  `Seed=`, equal to its `EntityId` on every line; with the field taken out all eight are line for line the
  stored ones. `VoiceIdBandTests` read off the seed table found the same 47 overlaps.
- **Ids** (second commit). `VoiceRegistry.IdFor` gives the id: an entity's own voice (engine, aircraft,
  machine, wood, recording) is under its entity id; every other voice, and every question to the acoustic
  worker (`ProbeFor`: a one-off's source, somebody talking; `WorldAudioPlayer.SourceProbeId` asks it), gets an
  id from `-1000` down, above the woods' entity ids (`WoodChorus.FirstId`), keyed by kind, owner and part and
  by world. An id is held while its voice is an entry, in the frame it was asked for, and while the mixer has a
  voice under it; `Sweep` lets the rest go, and they are taken again nearest zero first, so the order things
  were let go in does not matter. So a retiring place, outlet, cabin path or train voice keeps its id until its
  fade has ended without being an entry (the allocator holds it; the fades become the registry's in P3d), and a
  one-off's id is free once its voice has stopped. A probe is held as long as the worker keeps a source nobody
  asks about (`AsyncAcousticWorker.SaSourceTtlMs`, 5 s, 300 frames). `Hold` keeps an id for whoever refers to a
  voice before it starts or after it stops (the tests). The band constants and formulas are gone
  (`*VoiceBase`, `ReflectionVoiceId`'s arithmetic, `PlaceVoiceId`'s, the trains', rain's, the aids', beacons',
  birds', step echoes', footsteps', the landing's `-50`, the reference voice's, `EchoVoiceIdBase`,
  `TransientVoiceId`, `SourceProbeBase`); their numbers are the seed table only.
- **Kept as it was, so nothing heard moves.** One-offs of a pooled kind take turns as before
  (`VoiceRegistry.TurnsOf`: transients 400,000, beacons 24, birds, step echoes and drops 48, your steps 12,
  everybody else's 64, the aids' beeps 6): the 49th drop's turn is the first's, and replaces it if that one is
  still held. With each its own voice instead, traffic_rain's stream grew from 34,461 lines to 37,206: drops that
  cut each other off all play. The cabin's paths are counted by path whichever vehicle you sit in, a talker's
  copies by the room they were given, and a landing is one at a time.
- **Streams** (second commit), regenerated for the new ids. `EmitterStreamReplayTests.UnderTheIdMapTheStreamIsTheOneBefore`
  runs each scenario, puts every id the registry gave back to the one its voice had (its seed, by kind, owner and
  part) from the moment it was given, writes every line whole (`Expand`) and compares with the streams of the
  first commit (`OPENFPS_REPLAY_BEFORE=<folder>`). All eight are equal line for line, at the default processor
  count and at `DOTNET_PROCESSOR_COUNT=4`: walk (18 ids given), drive (24), traffic_rain (94),
  sirens_and_far_traffic (52), train_behind_wall (34), places_and_woods (69), talker_and_aids (25), high_ids
  (34). One difference, in high_ids: frames 113 to 120, the three guide beeps' 48 re-placings come in another
  order within their frame (the budget orders voices that nothing else tells apart by their ids, and the beeps'
  ids now come in the order they were given); every line is the same line. In high_ids the siren of car 61,001
  and the guide beep are two voices now (both were `-961001`, re-placed in turn as each), and the loudspeaker's
  wall copies are out of the one-offs' ids. Written whole, the stream is the same either way: what the mixer was
  told for each is the same, and only the real mixer, which kept one voice under the one id, would have played
  the two as one.
- **The band test**, must-pass: `VoiceIdBandTests.NoTwoLiveVoicesShareAnId`, over all eight scenarios: no id is
  given while a voice sounds under it or it is an entry, and at every frame's end every voice the mixer holds and
  every entry is under its entity's id or an id the registry holds for it. With it: every voice an entity can have
  at each entity id S6 named (1 to 250,000, Albany's and Magnolia's counts among them) and every voice nobody
  owns, all at once, each its own id; a million engine echoes one after another run on in six ids (S6: never
  reused, they reached the borrowed cars' band after about ten hours); a fading voice's id held until it stops,
  then the next voice's. The skipped twin and the 47-overlap list are gone.
- **Tests.** A test asks for a voice by kind, owner and part (`ClientAudioHarness.Voice`, held for the test),
  by what it started as (`RecordingMixer.StartedAs`), or by its seed, never by where its id falls.
- **Left.**
  - The woods' ids are not in the allocator. A wood is an entity of `ClientWorldState`'s and its own voice is
    under its entity id like every entity's, and that id seeds its synth (`PlacedNatureVoice`): given an id
    by the allocator, it would render differently. The allocator's ids stop above them, so the two never meet.
    Moving them wants the woods' seed carried on the wood (a field of `WoodChorus`, which is in the wire hash)
    or a reseed.
  - Birds' asking (P6): a perch group's probe can be had now (`IdFor(Probe, NoEntity, group)`), but asked of
    the worker the calls are placed by the simulator's answer, not the tracer's: the bird lines of the streams
    move, and whether a call through a hedge or a wall sounds right is Cody's to hear, as for the beacons.
  - Voices that nothing else tells apart are ordered by their ids, as since the 2026-10-11 fix; which comes
    first can differ from before (high_ids' beeps).
- **Next.**
  - P3d: one way to start and stop; the fades owned by the registry, so a retiring voice stays an entry until
    its fade has ended (the allocator holds its id meanwhile); whether a one-off of a pooled kind is its own
    voice (no turns: drops, steps and beeps no longer cut each other off; a named stream change, traffic_rain
    above); the entry's `Mount` and level from the model, with the 116 dB, 1.9 m grille, 4.2 m rail horn and
    interior 0.7 m fallbacks failing loudly; ranges from the entry's level (S24); the remaining fallbacks of
    S27; the facade's lost stop and silent replacement. With a reseed (renders may move) the seed table can go,
    and the woods' ids with it.

**P3d as built (2026-10-11), steps 1 to 5 of 6.** Unheard. Branch `worktree-agent-a59cc2655299daa49`, not merged.
OpenFPS.Sound changed (step 2), so the wire hash does.

- **One way to start and stop** (step 1). `VoiceRegistry.Sound` (outside the budget: the audio thread starts it
  if it is not sounding, places it if it is), `Submit`, `Sustain` (the lane rumble), `Refresh`, `Retire` (a fade,
  then a stop) and `Stop`. No owner writes `IsPlaying ? Update : Play`. The registry owns the fades (`Fades`, once
  a frame before the sweep): a retiring voice is an entry, and holds its id, until its fade ends; one wanted again
  is brought back, not restarted. The facade numbers every call, and nothing asked of a voice before its stop is
  done after it (the held horn placed after its stop, sirens_and_far_traffic frame 110; the wood placed by the
  budget's held emitter after its stop, places_and_woods frame 3). A replacement play asks the binaural pool
  before stopping the old voice and is logged once by id. S18's rain parts and the followed line, in passing.
- **Mounts, levels and ranges** (step 2). `VehicleProfile.GrilleZ`, `HornMount`, `SirenMount`; a train's horn is
  its horn source; the interior at `CabinPaths.PlacedMetres` and `OnePlace`; one ear height
  (`LocalPlayerState.StandingEyeHeight`, `SeatedEyeHeight`) and breath from `Speech.MouthHeight`. The 116 dB, the
  1.9 m grille, a train with no horn source, an unnamed synth and an unknown teleporter sound are logged once by
  id (`ModelKindTable.Fail`). Ranges are the audible range of the level (S24): others' steps (were 15 m), step
  copies, landings, your own voice's room and copies, beacons (40), the rain's floors, a recording's wall copies
  (its source's range, not 0.8 of the authored one).
- **Every one-off its own voice** (step 3): no pools taken in turn; traffic_rain 34,461 to 37,206 lines.
- **Beacons on the worker's path** (step 4, P6): each beacon in reach asked every frame under
  `IdFor(Probe, entity, BeaconProbe)`; the blip, its place and level and the tracker's "behind a wall" follow the
  worker's answer (the tracer until it has one). City, every one of 514 door beacons from five spots
  (`--beacon-count`): Marlow floor 3 corridor 10 and 10, Brandt Court 30B 1 and 1, Main Street 4 and 4, Marlow
  floor 4 10 and 10, the garage level 0 32 and 40 (the 8 changed are 175 to 334 m off across open ground, past a
  door's 12 m reach). Magnolia not counted: it found the next item.
- **Found and fixed, own commit:** `SteamAudioScene.AddPieceMesh` overran its 64-corner buffer on a Magnolia
  solid, so every simulation tick threw and every source on Magnolia fell back to the tracer.
- **Birds on the worker's path** (step 5, P6): a probe per perched group (`IdFor(Probe, NoEntity, perch)`), asked
  as it wakes and twice a second; a call takes its group's answer moved to the bird. The geese keep the tracer.
- **Streams** (each step's commit says voice by voice): step 1 sirens_and_far_traffic, places_and_woods,
  train_behind_wall, high_ids (no-op revives and cancels gone); step 2 drive, talker_and_aids, high_ids (interior
  0.7 to 0.72 m; loudspeaker copies' range 320 to 400); step 3 traffic_rain; steps 4 and 5 none (no beacon or bird
  in any scenario). All eight equal at DOTNET_PROCESSOR_COUNT=4.
- **The reseed** (step 6, 2026-10-12, made on Windows and sent back as patches). Unheard.
  Only `OpenFPS.Client.Core` and the tests changed, so the wire hash does not.
  - **Seeds.** `VoiceRegistry.SeedOf(kind, owner, part)` is a hash of the three: FNV-1a over the kind's name (so a
    kind added between two others moves no seed), then MurmurHash3's finaliser over the owner and over the part,
    the top bit dropped. The same on every machine and in every run; never below zero, as an entity's id never
    was while that seeded its engine (`CoolingSystem` takes its seed's remainder). The seed table is gone from
    the game, and `TurnsOf` with it: a one-off's seed is its own, as its id has been since step 3. Neighbours are
    far apart now. A model makes its parts' seeds by adding small numbers to its own (wet tyres its seed and 131,
    the rear axle one more), so seeded by entity ids car 102's front tyres drew the numbers car 101's rear tyres
    did.
  - **The woods' voices** are under ids the registry gives (`IsOwnVoice` without `Wood`): a wood is an entity the
    client made, and nothing needs its voice under its entity id now that the id seeds nothing. The machine
    budget retires, brings back and revives a machine by its voice's id (`MachineVoiceId`); a place names its
    middle by its middle's voice id (`SpatialEmitter.PlaceOfEntity`); a wood forgotten has its voice stopped
    under that id. The woods' entity ids stay `ClientWorldState`'s, under the ids given out (the worker is asked
    about a wood by its entity id).
  - **The old table** is `OpenFPS.Tests/OldVoiceIds.cs`, names only. `UnderTheIdMapTheStreamIsTheOneBefore`
    writes each scenario with every voice under its old id and seeded by it (`OPENFPS_REPLAY_DUMP`) and compares
    with a folder written so before a change (`OPENFPS_REPLAY_BEFORE`).
  - **Streams**, all eight regenerated. Written so before and after on one machine, all eight are equal line for
    line, with no line out of order. In the streams themselves: walk 8 lines changed, drive 14, traffic_rain
    94, sirens_and_far_traffic 14, train_behind_wall 6, talker_and_aids 19, high_ids 23, each a `Play` line in
    which only `Seed=` changed. places_and_woods: 6 such lines, and 4,027 in which an id changed (40 `Play`,
    3,508 `Place`, 471 `ReviveEngine`, 7 `FadeEngine`, 7 `Stop`): its five woods (one of park trees, four of
    pines) are under `-1015`, `-1036`, `-1037`, `-1058` and `-1059` instead of `-20000000` to `-20000004`, their
    places name them so (`PlaceOfEntity`), and the thirty ids given after the first wood's are one, three or
    five further down. 74 ids given, 69 before. They were regenerated on Windows
    (maths `8;8f85b41491a557ea448dbf82`, the streams before `8;e39fea91ef91332c21b478ff`), so on the Linux
    machines they compare within a part in a thousand until they are regenerated there.
  - **The render check by band** (`SeedBandLevelTests`; the fingerprints render with seeds of their own and do
    not move). Each model the mixer seeds, made as the mixer makes it, rendered as voice 101 with the seed it had,
    with the seed it has, and as voice 102, and compared in nine bands. dB between the old seed and the new, the
    whole and the worst band: engine (v8_muscle, 15 m/s) 0.09 and 0.43; push mower 0.06 and 0.14; airliner 0.02
    and 0.52; crossing bell 0.23 and 0.23; fountain 0.03 and 0.77; park tree 0.39 and 0.63; a wood of pines 0.11
    and 0.39; fire pit 0.44 and 0.71; creek 0.34 and 1.13; electric horn 0.03 and 0.04; air horn 0.00 and 0.02; a
    wall's smear 0.00 and 0.06. Between two neighbours under the new seeds the whole is within 0.46 dB for all
    of them; one band of few events is a seed's own draw (the fountain's 125 to 250 Hz 3.0 dB, the fire's crackle
    3.2, the creek's 250 to 500 Hz 3.2). The test holds the whole to 1 dB and a band to 4.
    A sea's edge takes minutes, since its waves come in groups: over 40 s the old seed and the new were 6.4 dB
    apart, over five minutes 0.47 dB as a whole and 3.0 dB at 60 to 125 Hz (rendered on request,
    `OPENFPS_SEED_BANDS_SHORE=1`: it takes about eight minutes).
    Not rendered: the gas hob (its key carries the wall clock) and an air conditioner (its thermostat's phase is
    drawn from its seed, so which units are running at a given moment has changed, as it would for any reseed).
  - **Also in the patches:** on a machine with other maths the stream replay compared a rendered sound's hash,
    which no tolerance forgives; it compares its name, length and rate there now (own commit).
- **Next (not done):**
  - The rain's 40 dB start level and the bump's 85 dB stand until measured; the rain voice measures itself by
    running, so holding it back silent would leave the replay's rain silent. Decide, then do.
  - Rain patches asked under probes (P6's last asker): done 2026-10-12 ("P6, the rain, as built"). The pool,
    measured on Linux with Steam Audio (`--pop-hunt map=city`, Marlow Tower stairwell floor 3, 60 s, seeds 1 and 2):
    30 still sources against 38, the rain's eight more. No more excursions with 38 (1 and 0, against 0 and 1; the
    one is the same car at -80 to -62 dB in both); the start tick's fallback to the tracer 10 or 11 of 78 sources
    against 2 or 3 of 70 (64 a tick), recovered within the run. So the heaviest surface only: all four (32 more)
    would keep the pool over.
  - SpatialEmitter's two default ranges (100/3 against 50/3, S24).
  - Cody to hear: the listening list in the report of 2026-10-11.

#### P4. One ranking (1 session)

- **Changes.** Every voice that competes for the mixer ranks by its rendered level at the ear with its
  path's gains (`Loudness.RenderedGain` and `HeardGain` over the registry's entry and its path): engines,
  machines, places, borrowed voices, sirens, horns, talkers. The budgets stay as budgets (counts), with
  one order. The keep bias and minimum hold stay as they are. The ranking reads the number the emitter
  plays (S19), and the provider's orderings (render-ahead, region buses, detail, late field, echo
  candidates) read the same audibility (S20).
- **Files.** CAS (`ChooseLiveEngines`, `ChooseLiveMachines`, `ChoosePlaces`), `VoiceManager.cs`
  (`Audibility`), the registry.
- **Conflicts.** After P3. Nothing else in CAS at the same time.
- **Guards show.** walk, drive and traffic_rain unchanged (fewer cars than the budget). `sirens_and_far_traffic`
  changes in which cars are live: named in the commit. Must be heard by Cody on the city with traffic.

**P4 as built (2026-10-12), the choosers; the provider's orderings (S20) not yet.** Made on Windows and sent
back as patches. Unheard. Only `OpenFPS.Client.Core` and the tests changed, so the wire
hash does not. Three commits.

- **The one rank.** `VoiceManager.HeardLevel(volume, reference, range, distance, occlusion, level, key,
  physical)`: the budget's own law taken out of `Audibility` so that others can ask it. Cars
  (`ChooseLiveEngines`), standing machines (`ChooseLiveMachines`) and the places of trees and fires
  (`ChoosePlaces`) ask it with the numbers their voices are played with, and `Audibility` is it with the
  playing voice's 2 dB and the essential pin. `OneRankTests` holds each chooser's number and the budget's for
  the emitter then played to a part in a thousand, for four kinds of car and eight kinds of standing model.
- **A render's headroom is not loudness** (S19, the other way round). S19 said the machine ranking left out the
  headroom gain its emitter applies. It was right to: a voice that renders with more headroom than the shared
  16 dB is played that much up to give it back, and is no louder at the ear. The budget was wrong to count it.
  It ranked on the emitter's volume, so a rain patch or a fire rendering 45 dB under full scale ranked 29 dB
  over a machine as loud, a train's voice 20, a crossing bell 16, a fountain or a tree 6.
  `SpatialEmitter.HeadroomGain` says how much of a voice's volume is that return, and `Audibility` divides it
  out. The other half of S19 was so and is mended: the machine ranking reads the key its voice is built and
  measured under (`PhysicalKeyOf`), not `SoundId`.
- **Cars by loudness** (S12). Engines go to the cars loudest at the ear, and the cars left over borrow in the
  same order. The keep bias is what it was in decibels (a sounding car as if at three quarters of its distance,
  2.5 dB) and the hold is 2.5 s. Which cars have a front outlet of their own is still by distance: whether two
  ends can be told apart is geometry.
- **Places by loudness** (S12): to the loudest source at the ear first, not the nearest.
- **Found and fixed:** the held cars (an engine started within 2.5 s) were all alike in the order and so came
  by id. A car out of the budget borrows the ring of the first car of its kind with an engine, so with two of a
  kind held the first was the one with the lower id and, when the hold ended, the louder: every borrowed voice
  of the kind was stopped and started on another ring, twice. The held are loudest first among themselves.
- **Not built, each a decision for Cody:**
  - **The path at the choosers.** The spec says "with its path's gains". The worker is asked only about what
    already has a voice, so only those have a path to count. Counted for them alone, a voiced machine behind a
    wall ranks under an unvoiced one behind the same wall and gives it its voice; the newcomer's path then
    comes back as blocked too, and the two change places each time the worker lets the loser's answer go
    (5 s). So the choosers count no path (`ClientAudioSystem.UnaskedOcclusion`), and the budget counts it among
    the voices it holds, as before. To count walls at the choosers the worker has to be asked about every
    contender, voiced or not: more sources against the Steam Audio pool. The echo candidates' ranking already
    leaves the path out for a reason heard ("ranking on them flickered the trace").
  - **Sirens, horns, held horns and talkers** still take a voice each with no budget (S12's last line). They
    have no count to be ranked into; giving them one is a new budget.
  - **The provider's orderings (S20).** Render-ahead was still nearest first by `EffectiveDistance`; engine
    detail, the late field and the echo candidates ranked on `BaseVolume × RenderedGain`, which carries the
    headroom's return. Built 2026-10-12 on Cody's machine ("S20 as built", below).
- **Streams** (each commit says line by line). The headroom field: all eight, every `Play` line gains
  `HeadroomGain=` and no other line changes. Cars by loudness: sirens_and_far_traffic, 3,490 lines to 3,729
  (the engines were the horn car and the patrol car, the two nearest; they are the horn car and car 504, a
  muscle car 48 m off and louder from here than the patrol car at 35 m, and the patrol car keeps an engine as
  the only one of its kind; six cars borrow where seven did); walk (28 lines) and traffic_rain (852),
  `ReviveEngine` lines in another order within their frames, the same lines in every frame. Places: none. All
  regenerated on Windows.

**S20 as built (2026-10-12), the provider's orderings.** Unheard. Made on Cody's machine with FMOD and Steam
Audio. Only `OpenFPS.Client.Core`, the lab and the tests changed: no stream changes and the wire hash does not.
Two commits: the instrument first (`--driving set=ranks`, measured on the old code), then the change.

- **One rank, worked out once an update.** A voice carries the numbers the budget ranks it by
  (`ActiveSound.RankAs`: the headroom gain, the declared level, the key its spectrum is measured under,
  whether it is physical). `FmodAudioProvider.HeardHere` asks `VoiceManager.HeardLevel` with them, less the
  headroom's return and with no path, for every voice once an update (`RankVoices`, into `ActiveSound.Heard`),
  and the four orderings read that number. Ties go to the lower id (`LoudestFirst`). It is the budget's number
  for the same voice to a part in ten thousand (`ProviderOneRankTests`); the provider's gain carries the take
  gain of a recording as well, which the budget does not know.
- **Render-ahead**: loudest first, so a short pool starves the quietest voice. A train's lanes go just ahead
  of its loudest voice (they went ahead of whichever of its voices was listed first).
- **Engine detail**: against the loudest machine by the heard level; the 15 and 12 dB thresholds stay.
- **The late field**: its sixteen sources by the heard level. Its lean is weighted by the heard level too
  (`LeanWeight`; Cody, 2026-10-12): by the level as played, a fire pit's place pulled it 29 dB harder than heard.
- **Traced echoes**: by the heard level at the source's own position (it was the distance to where it is heard
  from, the diffracted bearing's point); the path stays out, the 4 dB hold and the 30 dB floor stay. The rig's
  input gain is a signal level and keeps the headroom.
- **Region buses**: unchanged, they order rooms.
- **Measured** in the real mixer (`--driving set=ranks`: ten cars idling on a street at 8 to 160 m, FMOD and
  Steam Audio, 0 direct voices without HRTF), the last of six samples a second apart:

  | Scene | | Before | After |
  |---|---|---|---|
  | Cars alone | full detail | 6 of 10 | the same 6 |
  | | render order | nearest first | 8, 22, 14, 105, 32, 45, 60, 80, 130, 160 m |
  | Fire pit 3 m off | full detail | none | the same 6 as alone |
  | | late field | 12 fire places, 4 car voices | 10 fire places, 6 car voices |
  | | render order | the 12 fire places first | the 4 loudest cars, then the places and the rest |
  | Fire pit 35 m off | full detail | 4 (the bus at 32 m, the car at 45 m reduced) | the same 6 as alone |
  | | traced echoes | the fire pit takes the 160 m car's rig | the six cars from 45 m out |
  | | render order | the fire pit fifth | the fire pit last |
  | Moderate rain | full detail | none | the same 6 as alone |
  | | late field | 15 rain patches, 1 car | 9 rain patches, 7 car voices |
  | | render order | the 12 nearest patches first | the 5 loudest cars, then the patches and the rest |

  The fire's places 3 m off rank between the bus at 32 m and the car at 45 m, so they still hold ten slots.
  The cars-alone render order changes because the ear model is in the rank now: an idling car ranks by its
  declared level (the budget's law, P4), so the glasspack V8 at 105 m comes before the bus at 32 m.
- **Tests.** `ProviderOneRankTests`, six, each seen to fail with the code broken: the headroom counted (five
  fail), ties to the higher id and lanes after their voice (two), the lanes with the quietest voice (one).
- **Questions for Cody.** The late field's lean, the way the tail leans toward its sources, is weighted by each
  traced source's level as played, so a fire pit's or a rain patch's place pulls it 29 dB harder than it is
  heard. Weighting it by the heard level is the same fix as these, but it changes what is heard (the lean), so
  it waited. Cody decided 2026-10-12: by the heard level, done.

#### P5. Names and materials to properties (1 to 2 sessions)

- **Changes.** `Scope.StandsOnRoof` reads `SurfaceFlags.Roof`; generators and prefabs set the roof flag
  where they make a roof, and the name test in `EntityGeometry` becomes the fallback for old maps only,
  logged. Glazing becomes a field (prefab, door kind, generator); `GlassKind` reads it, with no silent
  default. An entrance is a property of a door or portal (`MapTracker.IsEntrance`, the server's `Haunts`).
  Rain surface kind, bird perches, fuel and the footstep span and deck read fields of the material table
  (`AcousticRegistry`), not names; `SurfaceFlags.Foliage` is set or removed. `SpatialService`'s sight
  test reads `SurfaceFlags.Glass`. One table of door models for `WorldAudioPlayer` (S10). One material
  table with aliases, impact and ricochet behaviour as its fields (S22). Door prewarm from the loaded
  map's door prefabs; storeys from the building (S32).
- **Files.** `Scope.cs`, `MapTracker.cs`, `SpatialService.cs`, `BirdLife.cs`, `WorldAudioPlayer.cs`
  (render dispatch), `OpenFPS.Common/EntityGeometry.cs`, `OpenFPS.Common` material table,
  `OpenFPS.Sound/Glass.cs`, `RainSurfaces.cs`, `Fuel.cs`, `Footsteps.cs`, `BulletImpact.cs`, prefabs,
  `tools/gen_city.py` and `tools/gen_osm.py` (city.json is generated: regenerate, never edit), server
  `CombatService.cs`, `Haunts.cs`.
- **Conflicts.** None with P1. With P2 on `Scope.cs` and `RainField.cs` lines (see P2). With P3b on
  `WorldAudioPlayer.cs`. The map regeneration conflicts with any other generator work in flight.
- **Guards show.** Streams and fingerprints unchanged (no voice's emitter changes, the door renders are
  the same calls). Map tests: the roof, glass and entrance answers for every entity on the three maps
  equal today's except where a name was misleading, listed.

**P5 as built (2026-10-11).** No map file changed and no generator; nothing heard changed on a shipped map.

- **One material table (S22).** `MaterialProperties` carries a round's strike (`StrikeBehaviour`: kind, crater
  energy, chip, chip contact, a debris density where the debris is something else, the spray's low band) and its
  ricochet (`RicochetFace`, null where a round goes in), and `Leaves` (a volume of leaves) and `Underfoot` (deck
  span and thickness, grit, loose pieces). `BulletImpact.StuffOf`, `Ricochet.TryFace` and Footsteps' span, deck,
  texture and loose-piece tables read them through `GetProperties`, so an alias is its material ("Steel" strikes,
  skips and rings as Metal) and a material made "like" another strikes as its family does with its own density
  (stainless, aluminium, cast iron as metal; oak, pine, plywood as wood). Debris weighs what the material does; the
  tables' densities were the registry's but for grass (1,500, the soil under the turf, kept as its debris density).
  Every material has a strike (an error at load if not; a test); ice, snow, fabric and foam were given theirs
  (ice brittle and skipping as concrete, snow a soil, both EST). The registry's unknown-name message is an error
  now, and rain no longer tests `IsKnown` to keep it quiet. `MaterialStrikeTests` holds every number of the old
  three tables.
- **Glazing (S9).** `PrefabTemplate.Glazing` (Annealed, Tempered, Laminated), in the schema and so in the editor;
  the validator rejects glass without it and anything else with it. The five glass prefabs say Tempered. `GlassKind`
  reads it (a car's glass is still chosen by where it is in the car); a pane that declares none is an error once by
  prefab and breaks as tempered. Every pane on the four shipped maps is the glass the names made it
  (`GlassKindTests`). Glass is the material's family (`AcousticRegistry.IsGlass`) in combat, `EntityGeometry`'s
  flag and the sight test, so laminated glass breaks and is seen through too.
- **Roofs (S9).** `Scope.StandsOnRoof`: the surface under you is a roof shape (`SurfaceFlags.Roof`, from the
  shape library), or you are under the open sky, in no room, with a room's air within 3 m under what you stand on.
  The name test in `EntityGeometry` is gone, not kept as a fallback: nothing else read the flag. Over every top
  wide enough to stand on: the city 1,439 the same, the two bus shelters' canopies (metal_roof, no room under)
  no longer "on a roof"; Albany 13,605 the same; Magnolia 10,768 the same and one more, 15969 North Cooter Street's
  ceiling, which its roof form leaves under the sky. The landings called "to the roof" are inside their stairwells
  and were never stood on as roofs.
- **Entrances (S9).** `MapTracker.IsEntrance`: an exit beacon, or an opening joining a room and the open air (its
  portal's two sides), a door because it is made to be walked through and an opening with no leaf where a floor
  lies within half a metre of its sill on both sides. An opening is said by the room it is the way into ("Hangar
  entrance"). City: 143 the same; the wall over the hangar's front ("Hangar front entrance") is not one and the
  opening under it is; the tunnel's two mouths and the garage's street-level east side are; the garage's sides
  over their parapets and its upper decks' east sides are not. Albany 1,521 the same, Magnolia 1,136: the 22
  concrete landings named for their entrance are floors, and Magnolia's three barn doorways (no leaf) are
  entrances. The server's `Haunts` still finds front entrances by name: every house door joins a room and the
  open air, and Alex would wait at all of them, so which doors are front entrances is Cody's to say.
- **Door models (S10).** One table in `WorldAudioPlayer` (`DoorModels`: prefix, renderer, lateness); a key no row
  names is not a door model, and `RenderDoorKey` throws for one instead of handing it to the knob door.
- **Door prewarm (S32).** `DoorSystem.RenderKeysOf` builds every key a door can send as its sounds build them
  (every opening, every close and knob shut, the key in its lock), `RenderKeys` a map's, commonest first and glass
  last; `MapManifest.DoorRenderKeys` (appended) carries them and the client renders them as the map arrives, with
  the cars' windows after. City 87 (the old list made 140 for the city), Magnolia 120, Albany 105. The old list
  missed every door on the places (knob doors 0.9 to 1.05 m wide, glass pull doors 1.1 m). `--prerender-doors`
  reads every shipped map.
- **Leaves and families.** Rain's pool and canopy, the birds' hedges, the crown that burns as a tree and the
  foliage flag read `Leaves`; a pool is a liquid's family; the fuel trunk is any wood. Nothing shipped is made of
  anything these widen to.
- **Left.** The storey in `MapTracker.Floors` from the building (S32); the glass door's key still says tempered
  in `DoorSystem` rather than reading the prefab; a declared roofing field if the shelters' canopies should be roofs
  (it would have to reach the client); the server's `Haunts` (above).
- **Guards.** Render fingerprints, the eight replay streams, `VoiceIdBandTests` and `ModelKindTableTests`
  unchanged. The wire hash changes, as any change to the libraries' sources does, and the protocol by one appended
  manifest field. `Glass.cs` and `AcousticRegistry.cs` are door model sources, so the door render cache's name
  changes and each door is rendered once more; the renders are the same.

#### P6. One path model for every asker (0.5 session, or inside P3b)

- **Changes.** Birds, beacons and rain patches ask the worker under probe ids, as one-offs do
  (`WorldAudioPlayer.cs:139-164`); the hand-rolled tracer stays the stand-in until an answer comes.
  Talkers ask under a probe id, not the player's entity id. The hand-rolled path is told the listener's
  own entity instead of guessing it (S23).
- **Files.** `BirdLife.cs`, `BeaconAids.cs`, `RainField.cs`, CAS (`UpdateTalkers`).
- **Conflicts.** P1 (worker load: more askers on the same pool; measure after P1). P3b.
- **Guards show.** Streams change for birds, beacons and talkers in the new scenarios, where the
  simulator and the tracer disagree; named.

**P6, the rain, as built (2026-10-12).** Made on Windows and sent back as a patch. Unheard.
Only `OpenFPS.Client.Core` and the tests changed, so the wire hash does not.

- **What is asked.** A ring patch behind walls is heard by the paths to the things it falls on, up to four a
  patch (`RainSurvey.Result.Sources`, heaviest first). The heaviest of each is asked of the worker under a probe
  of the patch's own (`VoiceRegistry.RainProbe(slot)`, nobody's probe; the host gives `RainField.SlotProbe`), at
  the cadence a voice that far off is asked about (every frame within 15 m, every second frame within 50), and
  the patch takes the answer when it is one for that surface and this ear. At most eight sources more on the
  worker's pool, while it rains and only for patches not in plain view. The roof over the ear and patches in
  plain view ask nothing, as before.
- **The stand-in.** Until an answer comes, and for a patch's other surfaces, the hand-rolled tracer, one
  surface a frame, as before.
- **Streams.** None changes: in traffic_rain every patch is in plain view, so nothing there asks. The replay
  has no scenario with rain behind walls; `RainProbeTests` is that scene (a closed room in the rain: eight
  patches, eight probes, the answer taken every frame from the second).
- **Not measured here:** the pool. This machine has no Steam Audio, so the worker's answers in the tests are the
  tracer's. `--pop-hunt` with eight more still sources (`extra=38` against `extra=30`) is the measure, where
  Steam Audio runs.

#### P7. Machine state from the server (1 session)

- **Changes.** The train's notch, the aircraft's power lever and a patrol car's siren mode come down the
  wire with the entity state, appended (positional wire format); the client reads them and the guesses
  go (S16).
- **Files.** `OpenFPS.Common` (EntityState), server `RailSystem`, the aircraft and traffic systems, CAS.
- **Conflicts.** CAS with P3 and P4; the wire hash and the Windows zip (a deploy with the server).
- **Guards show.** Streams change in the scenarios with a train or a siren: the lever values. Heard by Cody.

#### P8. Laws in one place (1 session)

- **Changes.** The small duplicated laws outside `ClientAudioSystem`: `VehicleShadow` calls
  `Diffraction` (S21); the FMOD fallback channel takes its volume from `RenderedGain` (S25); one occlusion
  floor default (S29); the world's speed of sound where 343 is written (S30).
- **Files.** `AudioEngine/Acoustics/VehicleShadow.cs`, `AudioEngine/Acoustics/SpatialAcoustics.cs`,
  `FmodAudioProvider.cs` (the fallback roll-off only), `OpenFPS.Acoustics/ImageSource.cs`,
  `Systems/FaceOpenings.cs`, `Systems/AcousticVolumeGenerator.cs`, `OpenFPS.Common/EntityAcoustics.cs`,
  `DistantMotion.cs`.
- **Conflicts.** None with P1, P2, P5 (different files and lines). With P3a only if the shadow's call
  moves at the same time; land P8 first.
- **Guards show.** walk, drive and traffic_rain change where a car is shadowed by another (drive: a car
  passes another) and in reflection delays at 12 °C (traffic_rain sets 12 °C): named. Fingerprints
  unchanged. `VehicleShadowApplyTests` and `VehicleShadowMutationTests` updated to the one law.
- **Done 2026-10-11 (unheard), except S21.** S21 (`VehicleShadow` calls `Diffraction`) is waiting on the
  barrier model, which is changing `Diffraction`, `OpeningRoutes` and the way over the top now; until it
  lands `VehicleShadow` keeps its own Maekawa and its own 343. (The barrier model has landed; S21 now waits
  on a decision about the cap and the grazing step: "P3a as built". Closed as deferred 2026-10-11, Cody: the bus
  keeps its own law until geometry stage 6 replaces vehicle shadows with real bodies.) The rest:
  - S25: a voice FMOD pans (no HRTF voice free) takes `RenderedGain` by hand, from the source's position,
    as the binaural voices do; FMOD's own distances are set out of reach (`Rolloff.NoneMin`, 1000 km).
    Reference 1 m, range 100 m: 90 m -39.1 dB before, -47.0 after; 100 m -40.0 before, silent after;
    200 m -40.0 (held) before, silent after; nearer than three quarters of the range unchanged. Its Doppler
    now runs at the world's speed too (FMOD's is 340 m/s times its Doppler scale; the scale is set from the
    weather): a car closing at 30 m/s at 20 °C, +159.9 cents before, +158.4 after.
  - S29: one default, `AcousticConstants.DefaultOcclusionFloor` (0.2), in all seven places. No law reads
    the floor (walls decide), so nothing heard changes.
  - S30: the world's speed (`AudioPhysics.CurrentSpeedOfSound`) in the hand-rolled path's reflection
    delays, `ImageSource`'s Fresnel zone and the tailpipe's and bay's radiation aim. At 12 °C delays
    +1.3 %, a face smaller than the zone +0.11 dB, ka -1.3 %; at 0 °C +3.5 %, +0.30 dB, -3.4 %. One
    reference, `AcousticConstants.SpeedOfSound` (343, 20 °C), named by `AudioPhysics.SpeedOfSound` and
    `WallTransmission.SoundSpeed`, for the defaults (`EarlyReflections.Find`, `Thunder`, `LightningPhysics`,
    the two `Aim`s) and for what must not follow the weather: `FaceOpenings.MinimumGapMetres` (a map's
    openings), `DistantMotion.VelocityTolerance` (a wire tolerance), `FlutterRangeMetres`, `ReportMeasure`.
    `Diffraction`'s defaults are left to the barrier model.
  - One law for the speed of sound itself: the mix took the linear fit 331.3 + 0.606 T and the bullets
    the ideal gas; `AudioPhysics.SpeedOfSoundAt` now reads `Air.SpeedOfSound`. 20 °C 343.42 to 343.21 m/s,
    40 °C 355.54 to 354.73. `thunder.ground_1500` regenerated (2.5 ms longer, total energy -0.06 dB).
  - Streams: all eight unchanged. The changes named above for walk, drive and traffic_rain were S21's
    (held) and the 12 °C delays, which the replay does not see: it holds the current speed at 343, and
    the provider is not in it.
  - Left to physics stage 2 (docs/PHYSICS_BOUNDARY.md, stage 2): gravity, air density (the 1.2 and 1.21
    in the walls, doors, glass, pipes and hob, with the 343 beside each, which the sources' own mechanisms
    keep: one air from the world's temperature and pressure), brakes, rolling resistance and drag, and
    `WindModel` against `WindField`.

#### Statics (inside library stage 5)

S31's statics join stage 5's list there (docs/SOUND_LIBRARY_BOUNDARY.md section 8): `RainFeeds.Feed` with
the weather into `WorldWeather`, `MeshLibrary.Shared` and `Shapes.Made` into the geometry instance,
`ReducedFarEngines` into `MixSettings`, the fire and struck-thing tunables into `LabLevers`, the model
generations into `ModelCatalog`, the speech caches made thread-safe or per world. Nothing here is a
separate part.

### 2.3 What can run together

| Wave | Parts | Why together |
|---|---|---|
| 1 | P0 | Every later part's evidence and guard. |
| 2 | P1, P2, P5, P8 | Different files, except the two `Scope.cs` / `RainField.cs` lines (assign them to P2). Three at once is the most worth running; P8 is small and can follow any of them. |
| 3 | P3a | Owns CAS. |
| 4 | P3b (with P6), then P3c, P3d | Sequential: each changes what the next renumbers or reads. P7 can run beside P3c if its CAS lines are kept apart (better after). |
| 5 | P4 | After the registry; one ranking over its entries. |

Recommended order: P0, then P1 (the pops are heard now) with P2 and P5 beside it and P8 after whichever
finishes first, then P3a to P3d, then P4, then P7. The stage 5 additions go with stage 5's next slice.

---

## 3. Where each part sits against the library stages

docs/SOUND_LIBRARY_BOUNDARY.md section 8: stage 5 turns statics into instances, stage 6 gives the
library a world input (`IAcousticWorld`) and creates `OpenFPS.Audio`, stage 7 splits `ClientAudioSystem`,
`WorldAudioPlayer` and `BirdLife` into a library `AudioWorld` and open-fps's adapter. Geometry stage 6
(docs/GEOMETRY.md 7) puts vehicles' bodies in the acoustic scene, with their sources at their real
emission points.

| Part | Sits | Why |
|---|---|---|
| P0 | Before everything | Stage 7's own risk is "behaviour drift in the big methods", and its only check is the replay; the replay does not cover half the voice kinds today. |
| P1 | Now, independent | The worker moves in stage 6; fix it before it moves, so the move is a move. |
| P2 | Before or with stage 5's `ModelCatalog` | The kind table is the catalog's index (`MachineRegistry`, `ModelLibrary` into one catalog). Done separately, the prefix ladders would be moved into the library first and replaced after. |
| P3 | After stage 5's next slice (`AudioClock`), before stage 6, and before geometry stage 6 | Stage 7's first step is "replace each snapshot read with a read of a per-frame source table the adapter fills once a frame" (section 8, stage 7). The registry is that table for voices: owner, kind, point, level, path. Built now inside CAS, stage 7 moves it instead of untangling five writers. Geometry stage 6 needs every vehicle source at its real emission point on the outside of the shell, which is the entry's offset on its owner. |
| P4 | After P3, before stage 7 | The budgets and the ranking go to `AudioWorld` in stage 7 (section 7.6); one ranking moves more easily than four. |
| P5 | Before stage 6 | Stage 6 moves `SpatialService`'s acoustic queries into Acoustics over the triangle world; they should move as flag reads, not name tests. The material fields belong in the library's material table. |
| P6 | With stage 6, or in P3b | Stage 6 makes `BirdLife` and `RainField` take the world input; their asking the worker is the same edit. |
| P7 | Any time after P3; ship with a server deploy | The wire changes (wire hash, Windows zip). |
| P8 | Before stage 6, and before geometry stage 6 | `VehicleShadow` is retired by geometry stage 6; until then it should be the shared law. The laws move into the library in stage 6 and should move once. |
| Statics | Inside stage 5 | Same mechanism as stage 5's list. |

---

## 4. What stays as it is

Things that look special-cased but are physics, or the right shape already.

- **Echo copies for one-off sounds only.** Copies of copies give a shot its flutter down a street; on a
  sustained source the same copies were ghosts (project note "higher-order reflections"). The rule
  follows what the sound is (an event against a loop), not a name.
- **Walls pass highs per band by the panel model.** Each wall's transmission is worked out from its mass,
  stiffness and construction band by band (`WallTransmissionTests`); that is materials and geometry.
- **Two outlets within 20 m.** A vehicle's exhaust and intake are two places the sound comes from; close
  enough to tell them apart they are two voices, further off one between them (CAS:2969-2983). Physics
  of the source and of the ear. What changes in P3 is only that the intake gets its path.
- **A siren is its own voice.** 130 dB at a metre against the car's 95: one voice cannot carry both on
  one full-scale reference (`SirenVoiceState`). Its own level, mouth and directivity; P3 gives it the
  shared writer, not a shared voice.
- **Trains as a few voices.** `TrainVoicing` groups a train's sources by what the ear can tell apart from
  where the listener stands. That is the ear's resolution, not a per-kind rule; P3 keeps the grouping
  and changes only who writes the voices.
- **Borrowed voices for far cars.** Past a couple of hundred metres one engine of a kind is like another;
  reading a near car's ring at an offset is an economy that changes nothing heard, as long as the
  borrowed voice is placed and pathed as the car it stands for (S3 is the part that is not).
- **Interior voices skip the path.** Sitting in a car, the engine's voice has already rendered what gets
  through the body (`FmodAudioProvider.cs:3295-3297`); a path traced through the same body would take it
  away twice. In P3 this is the entry's path rule ("none: inside its owner"), not an exception.
- **`CarriesPath` for sources whose point the worker does not know.** A rain patch heard by the paths to
  what it falls on, a footstep, a talker: the owner works out the path where the sound is. P3 makes this
  the rule for every voice instead of the exception.
- **`SynthRunning` and the crossing bell on the wire.** A bell rings because of a train a kilometre away;
  the client cannot work it out, so the server says it. That is the direction of knowledge, not a special
  case (project note "level crossings"). P7 extends the same reasoning.
- **`PlaysCopies` by `IsSynth`.** A synthesised source has no file to copy; deciding by the flag is right.
  Only its two leftover prefix tests go (S8).
- **Reflections never send to the reverb bus**, and the traced tail carries the late field: the room
  answering is not a second room (project notes "a reflection is the room answering", "traced reverb").
- **One loudness law** (`Loudness.Place`, `Widen`, `AudibleRange`, `RenderedGain`): already shared; the
  findings are about callers that bypass it or guess its inputs, not the law.
- **Doors and openings from geometry** (rooms by flood, openings where floods meet, door leaves in the
  scene): already the model; nothing here changes it.

---

## 5. Appendix: every voice band

As of `700cb635`. Since P3c these are not ids: every voice's id is given by `VoiceRegistry.IdFor` (an entity's own
voice its entity id), and the formulas below were each voice's seed ("P3c as built") until the reseed, when a
seed became a hash of the voice's kind, owner and part ("P3d as built"). They are kept as names for voices in
the tests (`OpenFPS.Tests/OldVoiceIds.cs`). Since P3b every band is a `VoiceRegistry` entry and its "Path from" is its kind's
(`VoiceRegistry.HeardFromOf`, "P3b as built"). "id" is the entity id as the client sees it (`EntitySnapshot.Id`), which reaches 18,024
on Albany's join and 32,598 over all of Magnolia.

| Band | Constant (file:line) | Formula | Started by | Path from | Stopped by | Ranked |
|---|---|---|---|---|---|---|
| entity voices | (entity id) | `id` | `ProcessAudioEmitter` → `Submit` (CAS:3158) | path pass (CAS:721) and the held emitter | VoiceManager; `ForgetEntity` (CAS:453-482) | engines CAS:1598-1800, machines CAS:1417-1557, VoiceManager |
| reflection copies | `ReflectionVoiceId` (CAS:57-58) | `-30000 - id·5 - slot` | path pass (CAS:814) | its own emitter (path pass) | fade in path pass (CAS:819-824); `ForgetEntity` | no |
| transients | `TransientVoiceBase` (`WorldAudioPlayer.cs:990`) | `-100000 - n % 400000` | `WorldAudioPlayer` → `Submit` (:748, :967) | hand-rolled, then probe answer | VoiceManager (event) | VoiceManager |
| engine echoes | `EchoVoiceIdBase` (`EngineReflections.cs:95`) | `-600000 - n`, never reused | `EngineReflections.Update` (:261) | `ApplyPath` (:419) | `EngineReflections` (:279, :298) | its own floor (:36-62) |
| borrowed far cars | `DistantVoiceBase` (CAS:215) | `-700000 - id` | `DistantEngine` (CAS:2829) | path pass branch (CAS:734), overwritten (S3) | ChooseLiveEngines (CAS:1761-1796); `ForgetEntity` | distance |
| front outlets | `IntakeVoiceBase` (CAS:218) | `-800000 - id` | `FrontVoice` (CAS:2035) | its start only (S1) | `ChooseFrontVoices` (CAS:1957-1962) | front budget |
| sirens | `SirenVoiceBase` (CAS:220) | `-900000 - id` | `SirenVoice` (CAS:2720) | path pass branch (CAS:727-732) and `VehiclePath` | `SirenVoice`, `UpdateSirens` | none |
| driving aids | `DrivingAids.cs:35-36, 66, 115, 138, 148, 160` | fixed and pools of 6, -961000 to -967105 | `DrivingAids` (`Submit` :539, :620; direct :576) | none (UI) | `DrivingAids` | Essential |
| beacons | `BeaconAids.cs:25` | `-967000 - n % 24` | `Submit` (:167) | hand-rolled (:199) | VoiceManager (event) | VoiceManager |
| birds | `BirdLife.cs:64` | `-970000 - n % 48` | `Submit` (:326) | hand-rolled (:325) | VoiceManager (event) | VoiceManager |
| horns | `HornVoiceBase` (CAS:2352) | `-1200000 - id` | `UpdateHorns` (CAS:2464) | path pass branch (CAS:723) and `VehiclePath` | `UpdateHorns` (:2469) | none |
| own voice | `OwnVoiceBase` (CAS:3603) | `-1300000`, copies `-1300001 - k` | `UpdateOwnVoice` (CAS:3662, :3755) | `CarriesPath` | `StopOwnVoice` (CAS:3769-3774) | none |
| talkers | `TalkerVoiceBase` (CAS:3197) | `-1400000 - id` | `UpdateTalkers` (CAS:3301) | `CarriesPath`, asked under the player's id | `UpdateTalkers` | none |
| cabin paths | `CabinVoiceBase` (CAS:2040) | `-1600000 - path` | `CabinVoices` (CAS:2083) | none (rides with the head) | `UpdateCabinVoices` | no |
| talker copies | `TalkerCopyBase` (CAS:3334) | `-1700000 - block·copies - k` | `AnswerTalker` | `CarriesPath` | `StopTalkerRoom` | no |
| step echoes | `STEP_ECHO_BASE_ID` (CAS:3593) | `-1800000 - n % 48` | `SubmitStepCopy` | `CarriesPath` | VoiceManager (event) | VoiceManager |
| held horns | `HeldHornVoiceBase` (CAS:2577) | `-2100000 - id` | `HeldHornVoice` (CAS:2655) | path pass branch (CAS:724) and `VehiclePath` | `UpdateHeldHorns` (:2613) | none |
| trains | `TrainVoiceBase` (`ClientAudioSystem.Trains.cs:21`) | `-2300000 - ordinal·6 - slot`, ordinals never reused | `PlaceTrainVoice` (:320) | `PlaceTrainVoice` (:323-326) | `StopTrainVoice`, `UpdateTrains` | machine budget, as a train |
| rain | `RainField.cs:761, 914, 1126` | `-2400000 - s`, parts `-2400100 - s·8 - k`, drops `-2401000 - n % 48` | `RainField` (:907, :980, `Submit` :1187) | `CarriesPath`, hand-rolled per surface | `RainField` | none (slots); VoiceManager (drops) |
| source probes | `SourceProbeBase` (`WorldAudioPlayer.cs:139`) | `-3000000 - id` | never a voice: a worker question | | | |
| places | `PlaceVoiceBase` (CAS:2113) | `-5000000 - id·MaxPlaces - k` | `PlaceOuter` (CAS:2217) | path pass branch (CAS:737-741) | `StopOuter`, `RetireSpreading` | place budget, nearest first |
| reference | `ReferenceVoiceBase` (CAS:3442) | `-7900000`, `-7900001` | `PlayReferenceVoice` | `CarriesPath` | `StopReferenceVoice` | no |
| woods | `WoodChorus.FirstId` (`OpenFPS.Sound/WoodChorus.cs:30`) | entity ids `-20000000 - n` | `ProcessAudioEmitter` (as an entity) | its start only: skipped by the path pass (S1) | `RefreshWoods` | machine budget |

Overlaps at the entity counts the maps have: reflection copies with transients from id 14,000; sirens with
driving aids, beacons and birds for ids 61,000 to 70,047; engine echoes with every band below -700,000
after 100,000 echoes; trains with rain after 16,667 train admissions.
