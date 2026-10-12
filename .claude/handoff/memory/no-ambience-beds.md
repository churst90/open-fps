---
name: no-ambience-beds
description: No map lays a recorded ambience loop over the world — Cody's decision 2026-09-18, and the test now asserts it
metadata:
  type: feedback
---

Rooms-map listen, 2026-09-18: *"the ambience track is super loud and frankly I don't think I want to
use ambience tracks... all I hear is the outdoors ambiance loop which is loud and needs to come out, it
doesn't add anything."*

**Why:** a bed is the one sound in the engine that is not made by anything. No source, no distance, no
geometry — so it cannot be occluded, cannot be walked around, and tells a listener nothing about where
they are, while sitting on top of everything that would. The speedway never had one; `default.json`
was the last map that did.

**How to apply:** `AmbienceId: ""` on every map. The MACHINERY stays and is still tested end to end —
a REGION may name an ambience (a hum, a machine room, running water) because that is a sound with a
place in it. `AmbienceAndWeatherTests.NoShippedMapLaysARecordedBedOverTheWorld` asserts the decision;
it used to assert the opposite, which was a decision written down as a fact.

Same session, same principle, three faults where something was heard that nothing had made:
- **A car is not a body that walks.** `VehicleSystem` spawns vehicles as `EntityType.NPC`, and
  `OtherBodies` accepts Player|NPC — so every car got a `StrideAccumulator`. Half a metre per stride at
  30 km/h is sixteen footfalls a second: *"the car driving by sounds like footsteps are being drug
  behind it"*. The filter is now the machinery, not the AI type — an emitter whose SoundId starts
  `ENGINE/` has no legs, which is the same test `ClientAudioSystem` uses to decide something is a car.
- **The camera's smoothing is not walking.** `ContinuousUpdate` fed `Position + VisualOffset` to the
  stride generator. `VisualOffset` is the server correction being eased out over ~0.2 s — up to 25 m/s
  of phantom motion in steps small enough to look plausible: *"when I /tp myself or land in the map, I
  hear a few footsteps before it settles"*. Footsteps now come from `_state.Position`, and
  `ApplyServerCorrection` returns whether it SNAPPED so a `/tp` can forget the stride (there is no
  "you were moved" message — only a position that could not have been walked to).
- **A decay time cannot be rewritten under a running tail.** The measured RT60 was written onto the
  SFXREVERB DSP every audio update, resizing its delay network 60 times a second: *"lots of popping and
  clicking from the reverb as I walk near the walls"*. Slewed now, and only written when it moves more
  than 2 ms. The median filter rejects a bad READING; this rides out a real CHANGE — different jobs.

See [[reverb-needs-enclosure]], [[no-special-cases]], [[synthesis-by-listening]].

**Cody 2026-10-10:** "correct, we aren't using ambiance beds anymore" — region beds too. Every map and prefab sets
AmbienceId "" (checked 10-10); hums, machine rooms and water are physical sources now. The machinery
(ClientAudioSystem SetMapAmbience/region bed, PlayAmbientBed, PrefabTemplate.AmbienceId) is dead code: remove it in
the regions cleanup after rooms-from-walls is switched on; RegionComponent/MapManifest fields stay (positional wire,
[[component-wire-format]]) unless the wire version is bumped.

**2026-10-11, Cody: "remove those ambiences completely we simply don't need them".** The recordings (3 ambisonic +
6 stereo, 90 MB, gitignored so deleted for good) and every copy (repo bin dirs, /tmp/openfps-gtk, the Windows
publish cache) are deleted; AmbisonicBedDsp, AmbisonicFormat, the lab's --ambisonic and ingest_audio.py's
ambisonic/stereo categories removed. Do not reintroduce a bed or a bed asset. PhononAmbisonics (traced reverb) stays.
