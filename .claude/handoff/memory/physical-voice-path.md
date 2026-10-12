---
name: physical-voice-path
description: Machines and aircraft can be placed on a map now — PhysicalVoiceState, ranked by rendered level, not distance
metadata:
  type: project
---

**Built session 17, 2026-09-19.** `OpenFPS.Client.Core/AudioEngine/Fmod/MachineProcessor.cs`.

The thing that was missing for months: a voice for any approved model that is NOT a vehicle engine.
`PhysicalVoiceState` is one output rendered ahead of the mixer into a ring, on the SAME worker pool
the engines use — `EngineRenderPool` now takes `IRenderedVoice`, not `EngineVoiceState`.

- `MachineVoiceState` — `machine:<preset>` (ac_window, ac_condenser, mower_push, mower_riding).
- `AircraftVoiceState` — `aircraft:<preset>` (airliner, turboprop, piston_single, helicopter).
- A map places one by giving a **prefab** `HasEmitter + IsSynth + SoundId: "machine:ac_window"` —
  the emitter comes from the PREFAB, never from the map entity, which is why the four machine
  prefabs exist.
- The emitter carries `PhysicalKey` — the WHOLE prefixed id — so exactly one place turns a name into
  a model. `SpatialEmitter.EngineKey` stays separate: that one buys a whole vehicle.

**The budget ranks by RENDERED LEVEL, never distance** (`ChooseLiveMachines`). Ten voices for
126 sources: a 92 dB mower three gardens away beats a 59 dB window unit at the same distance,
and a 142 dB airliner beats both. Aircraft share the machine budget for that reason — two budgets
would mean deciding in advance how many aeroplanes are worth how many machines. See
[[audibility-ranking]]. No borrowed-voice fallback: outside the budget is not heard, because forty
air conditioners are not one air conditioner heard from further away.

**What nothing on the wire carries, and why it is client-side:** a mower's load (how thick the grass
is) and an air conditioner's thermostat, both seeded from the ENTITY ID so every client hears the
same machine and forty units on one wall are forty machines rather than a chorus. And an aircraft's
**power lever comes from its climb angle** (`ClientAudioSystem.FlightPower`): up is full power, level
is cruise, down is idle — so one shuttle gives an approach and a climb-out on alternate legs and
nothing scripts either.

Held by `OpenFPS.Tests/MachineVoiceTests.cs`: level within 6 dB of declared, nothing on the soft
ceiling, placement order preserved at 3/10/30 m, two of the same machine uncorrelated.

**Why:** [[small-machines]] and [[aircraft-model]] were both "approved by ear, not in the game".
**How to apply:** a TRAIN still cannot use this — it is a line of bogies, not one point. See
[[rail-needs-a-multi-tap-voice]]. [[engine-render-pool]]'s rules are repeated verbatim in the file.
