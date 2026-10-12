---
name: aircraft-model
description: The aircraft synthesis (blades, jets, core; piston aircraft reuse EngineSynth) approved by ear 2026-09-18 — what is pinned, what is open, and that it is ON THE MAP since session 17
metadata:
  type: project
---

Built 2026-09-18: `OpenFPS.Common/Aircraft.cs` (BladeRowSpec / GasTurbineSpec / AircraftProfile,
presets `piston_single`, `turboprop`, `airliner`, `helicopter`), `AudioEngine/Core/Aircraft/AircraftSynth.cs`
(`BladeRow`: pulse per blade passage, width chord/tip × (1 − M_toward), thickness ∝ M³ in-plane,
loading ∝ M²·load off-plane, per-blade scatter → shaft harmonics and the supersonic buzz-saw, BVI slap
1.1 ms; `JetStream`: Lighthill via the shared `JetNoise.LighthillPressure`; combustor rumble; one
whine), `--aircraft` flyover renderer (arrival-time deposit, air corner 4 kHz·(100/r)^0.59, ground bounce
0.7), `docs/AIRCRAFT.md`. New engine preset `aero_flat4` for the piston single.

**Cody's verdict on the first four flyovers: "the jets and planes sound really really good — please
save that."** So that render is the baseline. Trap I nearly fell into the same hour: I then unified
the aircraft jets onto the exhaust's Lighthill law, which would have raised the airliner's jets ~15 dB
over what he approved. The approved balance is pinned as `GasTurbineSpec.CoreJetTrimDb = −16` /
`BypassJetTrimDb = −21` (dB against K = 1e-4), verified by the at-the-ear figures matching the approved
run exactly (71 / 66 / 73 / 64 dB). WAVs: `inbox/aircraft-demo-2026-09-18/`.

**Not in the game.** No emitter, no machine parts, no flight path. Order of work is in the doc: parts
`rotor`/`jet`/`core` on `MachineDefinition`, an `AircraftVoiceState` on the render pool with
`SetListener` like the exhaust, a server `AircraftSystem` route. Levels are anchors from certification-
style figures, never fitted to a recording. See [[synthesis-by-listening]], [[dont-respec-what-was-approved]],
[[machines-as-parts]], [[f1-two-pipes-and-the-shock-front]].

**Two engines, a wingspan, an approach speed and an undercarriage since 2026-09-20** — see
[[an-aeroplane-has-more-than-one-engine]]. The twins' anchors moved +3 dB for the second engine and
nothing else changed.

**ON THE MAP since 2026-09-19** via [[physical-voice-path]]. Five of them on the city: an airliner on
final for runway 18, one crossing overhead at 800 m, a turboprop inbound, a light single in the
circuit, a helicopter across the city at rooftop height. They are ordinary SHUTTLES — a straight line
between two points in three dimensions, which is what a departure and an approach are, so one entry
gives both on alternate legs. The power lever is read off the CLIMB ANGLE and nothing scripts it.
