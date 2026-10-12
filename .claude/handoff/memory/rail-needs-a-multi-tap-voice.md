---
name: rail-needs-a-multi-tap-voice
description: BUILT session 19 — trains run on the city loop; one TrainSynth per set, one FMOD voice per source, server places each source at head − along
metadata:
  type: project
---

**BUILT 2026-09-20 (session 19).** Two light rail sets run `rail_loop` on the city. Option (2) from
the note that used to live here — *the server places each radiator* — is what was built:

- `OpenFPS.Common.TrainLayout.Sources(profile)` is the list both sides agree on: index, kind, label,
  along, height, extent, level. It MIRRORS `TrainSynth.BuildVehicle`'s order and
  `RailAndSignalTests.TrainLayoutMatchesTheSynthSourceForSource` holds them together. Horn/whistle/
  bell entries are listed (indices must match) but `IsSignal` and are not spawned.
- Server `RailSystem` (`MapData.Trains`: Name, Preset, Track, TopSpeedKmh, StartOffsetMetres, accel,
  brake): one NPC entity per source, SoundId `rail:<preset>/<train>/<index>`, placed by
  `RaceLine.Sample(head − along)`; speed from the loop's curvature at 0.1 g like a car's.
- Client `RailVoice.cs`: `TrainVoiceState` = one `TrainSynth` + one ring PER SOURCE (2^17); any
  `TrainTapState` (a `PhysicalVoiceState`, so the provider/pool/FMOD treat it as an ordinary machine
  voice) that asks for a sample not yet rendered steps the synth under a lock in 512-sample blocks.
  Provider keeps `_trains` by `preset/train`; telemetry: `RotorWake` slot carries SPEED, `PowerLever`
  carries notch/8 (from the bogie's acceleration in ClientAudioSystem).

**Not done:** stations (no stops, no door hiss), horn at the level crossing, the reverb of a
cutting. See [[rail-model]], [[physical-voice-path]], [[machines-as-parts]].
