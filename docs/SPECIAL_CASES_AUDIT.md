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
| S3 | `DistantEngine` CAS:2804-2826; `FmodAudioProvider.cs:2888-2891`, `:4147-4155`, `:3936-3939` | A borrowed distant voice is re-submitted every frame with `ApertureFactor`, `Occlusion`, `TransmissionBleed` and `EffectiveDistance` at their defaults (0) and `TargetRegionId` the global region. Its update beats the path pass (S2), so its aperture target is 0 every tick. A non-reflection voice carries the diffraction low-pass, which below aperture 0.98 runs at `max(400, 22000·a²)` Hz: 400 Hz at aperture 0. Its spread is `a·90/d`: none. Its bearing is the car, never the edge it is heard round. | Probable: every borrowed car (up to 12 on the city) low-passed at 400 Hz whatever its path, and never occluded. Unheard as such; it would read as distant traffic sounding muffled and placed through walls. Verify first with a replay scenario with more cars than the engine budget (P0) and a look at the `-700000` `Place` lines. | A | Folded into P3; a one-line stop-gap is to give `DistantEngine` the car's path fields as `FrontVoice` does. |
| S4 | `AsyncAcousticWorker.cs:532-560`, `:1345-1361`, `:296-336` | The source pool's serving and lending. Staging skips a pending source not served this tick (`:538`), but the read-back loop reads every pending source that still holds a Steam Audio source (`:558-560`): its direct result was simulated from inputs staged on an earlier tick, against this tick's listener, and is stored as this tick's answer, labelled with this tick's source position. A lent source (`:1351-1360`) goes to its new owner without `ClearSource`. A moving source is the only kind for which a stale input differs. | Candidate for the open pops: a quarter second, 20 to 30 dB, 3 to 6 a minute with 40 cars round Marlow Tower's floor 1 stairwell, each a moving car on Dock Street whose transmission jumps from -80/-100 to -35/-52/-80 dB; no still source gives it, and 32 transmission surfaces instead of 8 changed nothing (docs/GEOMETRY.md 12.6, "Marlow Tower's leaks"; todo.md, Acoustics). The pool overrunning has popped before (2026-10-03, `--pop-hunt extra=40`). | A | P1: read back only what was staged this tick, clear a lent source, tag every result with its provenance, and measure with `--pop-hunt` before and after. |
| S5 | `UpdateSirens` CAS:2269-2290 with `VehiclePath` CAS:2298-2320 and `SirenVoice` CAS:2676-2680; CAS:650 | Every vehicle whose model has a siren asks the worker every frame, siren on or off, and is added to the path pass: `VehiclePath` is called before the mode is read, and a car whose engine has no voice enqueues a request each call. Off and no fresh answer, it also runs the hand-rolled tracer on the game thread for nothing. | Pool pressure on the city that feeds S4; game-thread cost per patrol car. | B | P1: ask only for a siren that is sounding; in P3 a voice that is not sounding is not in the registry. |
| S6 | Appendix; CAS:57-58; `AudioEngine/Acoustics/EngineReflections.cs:91`, `:254`, `:302`; `ClientAudioSystem.Trains.cs:38`, `:66`, `:89` | Voice-id bands overlap. A reflection slot's id is `-30000 - id·5 - slot`: it enters the transients' band (-100,000 to -499,999) at entity id 14,000, and Albany joins with 18,024 entities, Magnolia with 13,590 (docs/WORLD_STREAMING.md:150-151). The siren band `-900,000 - id` covers the driving aids (-961,000 to -967,105), the beacons (-967,000 to -967,023) and the birds (-970,000 to -970,047) for vehicle ids 61,000 to 70,047. Borrowed, intake, horn and talker bands run into the next band at ids of 100,000 to 200,000. Engine echo ids count down from -600,000 and are never reused: at the city's 2.6 echoes a second (CAS:188-191) they reach the borrowed band in about 10 hours, then the intakes, sirens and horns. Train voice ordinals grow the same way. `IsEchoVoice` (`id <= -600000`) is true for every band below the echoes; nothing calls it. | A one-shot (a step, a door) taking over a recorded loop's reflection on Albany, or the reverse; a siren moving a driving-aid tone or a bird call; after a long session, a new engine echo moving a borrowed car. Past: transient ids hashed into the echo and borrowed bands, "a footstep could take over a car's reflection or distant voice" (VoiceBudgetTests.cs:304). | B | P3: the registry allocates every id from one space and frees it; one test proves no two live voices share an id. |
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
| S20 | Provider orders: `FmodAudioProvider.cs:749` (render-ahead, nearest first by `EffectiveDistance`), `:4160-4182` (the 4 nearest region buses), `:1512`, `:1665`, `:1761` (engine detail, late field, echo candidates by `BaseVolume × RenderedGain`, each with its own hysteresis: `:1450`, `:1529`, `VoiceManager.cs:388`) | More rankings inside the provider, each by its own law. A voice fed `EffectiveDistance` 0 (S3, engine echoes) sorts first in the render pool. | When the render pool is short, a loud far engine starves before a quiet near one. | C | P4: the same audibility, computed once per voice per tick, read by every ordering. |
| S21 | `AudioEngine/Acoustics/VehicleShadow.cs:16`, `:22`, `:62-66` against `OpenFPS.Acoustics/Diffraction.cs:27-40` | Two diffraction laws. `VehicleShadow` has its own Maekawa (0 dB at grazing, 15 dB cap, bands 150/1000/4000 Hz, its own detour), the shared one 5 dB at grazing, a 24 dB cap, bands 200/1250/8000. | A bus and a building of the same height and detour shadow a source by different amounts. | B | P8 now (call `Diffraction`, keep the 15 dB cap as the stated reason: a gap under it and two ends); retired by geometry stage 6. |
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

Every part runs the guards through `tools/test-local.sh` (one run at a time on the machine), filtered to
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
| `sirens_and_far_traffic` | 40 | Listener in front of a brick building; patrol car 501 (`police_interceptor`, siren switched on, wail) driving behind it at 15 m/s; seven `v8_muscle` cars (502 to 508) with the engine budget shed to 2 (the `_adaptiveBudget` a mixer at its ceiling leaves), so all seven borrow; parked car 509 whose driver holds the horn from frame 20 to 60. | S3: `-700502` to `-700508` are played and re-placed with `Occlusion=0`, `EffectiveDistance=0`, `TargetRegionId=-1`, and `ApertureFactor=1` and `TransmissionBleed=1` (the emitter's defaults, not 0), while their `Path` lines say otherwise: `-700504`, behind the building, `Occlusion=0.95 TransmissionBleed=0.00393809 EffectiveDistance=47.84478 EqMid=0.0009898634` (frame 2). S2: `-2100509` has the exhaust's apparent position in its `Path` lines, `(6,0.8,7.77)`, and the grille's in its `Place` lines, `(6,1.1,11.2)`; at frame 110 it is stopped, then handed a `Path` and a whole `Place`. S1: `-800501` and `-800509` have `Place` lines and no `Path`. | P3a: the `-700xxx` `Place` lines carry the path's occlusion, bleed, distance and region; `-800xxx` gain the path; the `-2100509` and `-900501` `Path` lines fold into `Place`, with one apparent position. P3d: no `Path`/`Place` after the `Stop`. P4: which cars are live. P1's S5 change does not touch it (the siren is on throughout). |
| `train_behind_wall` | 54 | Listener in front of a brick building; a two-car `light_rail` set (sources 601 up) at 12 m/s behind it, signalling for a crossing at frame 20. | The train's six voices (`-2300000` to `-2300005`) each get a `Path` line (the 2026-10-10 fix) and a `Place` line every frame; the source entities 603 to 612 get `Path` lines and no voice (asked for through `_trainPathIds`). | P3a: the train voices' `Path` lines fold into `Place`; the source entities' `Path` lines go. P7: the notch (`PowerLever`) from the server. |
| `places_and_woods` | 50 | Walking north out of a 6 m roofed brick room through its doorway; outside, `foliage:park_tree` 701, `water:park_fountain/elm_park/0` 702 and `machine:ac_condenser` 703; nine `foliage:pine` 170 to 200 m off, made into four woods by `RefreshWoods`. | S1: the woods (`-20000000`, `-20000001`, `-20000003`; `-20000004` starts and stops at once) and their places (`-245000001` down) have `Play` and `Place` lines and never a `Path`; their band gains stay as they started, indoors. The tree's places (`-5008413` to `-5008418`) and the tap's (`-5008425` to `-5008427`) get `Path` lines from the spread branch. | P3b: the woods and their places gain path-carrying `Place` lines. P4: the place budget's order. |
| `talker_and_aids` | 55 | Riding car 801 (`i4_economy`, driven) north up a 6 m two-way asphalt carriageway with one side's wheels over the centre line, 4 to 6 m/s; somebody on voice chat (802, packets through `ReceiveVoice`) walks out from behind a garden wall ahead on the right. | S11: the centre rumble `-964003` is re-placed twice a frame: the game thread's line with the pitch the speed asks for, then VoiceManager's held emitter putting `Pitch=0.41666666` and its first position back (frame 120: `Pitch=0.62326664`, then `Pitch=0.41666666`). The talker `-1400802` carries its own path (occlusion 0.79 at frame 2, 0.20 at 29, 0 at 61) and has no `Path` line; one wall copy, `-1700000`. | P3b: one `Place` a frame for `-964003`, its pitch following the speed. P6: the talker asked under a probe id (the replay has no Steam Audio, so its values may not move). |
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
  re-placement, against a path that says 0.95, 0.004 and 47.8 m; the band gains come from the path pass.
- **The replay's vehicles face backwards.** `Replay.Facing` makes -Z forward; the server's vehicles face +Z
  (`VehicleSystem.Parking.cs:323`, `CreateFromYawPitchRoll(atan2(x, z))`). So in every stream a moving car's
  grille, siren and horn are at its tail and its exhaust at its nose. It hides none of the findings above.
  Fixing it changes walk, drive and traffic_rain too, so it is a commit of its own with the streams
  regenerated.

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

#### P6. One path model for every asker (0.5 session, or inside P3b)

- **Changes.** Birds, beacons and rain patches ask the worker under probe ids, as one-offs do
  (`WorldAudioPlayer.cs:139-164`); the hand-rolled tracer stays the stand-in until an answer comes.
  Talkers ask under a probe id, not the player's entity id. The hand-rolled path is told the listener's
  own entity instead of guessing it (S23).
- **Files.** `BirdLife.cs`, `BeaconAids.cs`, `RainField.cs`, CAS (`UpdateTalkers`).
- **Conflicts.** P1 (worker load: more askers on the same pool; measure after P1). P3b.
- **Guards show.** Streams change for birds, beacons and talkers in the new scenarios, where the
  simulator and the tracer disagree; named.

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

As of `700cb635`. "id" is the entity id as the client sees it (`EntitySnapshot.Id`), which reaches 18,024
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
