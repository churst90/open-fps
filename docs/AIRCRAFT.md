# Aircraft: turbines, turboprops, pistons and rotors, from the mechanism

_Specified and first built 2026-09-18. `OpenFPS.Common/Aircraft.cs` (profiles, four presets),
`OpenFPS.Client.Core/AudioEngine/Core/Aircraft/AircraftSynth.cs` (the synthesis),
`--aircraft` in the AudioLab (flyovers to WAV). Not yet wired into the game: nothing on a map
flies, and there is no `aircraft:<preset>` emitter. This document is the spec that the code
follows and the list of what it does not do yet._

## The rule, as for engines

Nothing here is a tone. Every aircraft is described as parts with dimensions — blade counts,
diameters, chords, rpm, nozzle sizes, exit velocities — and the sound is integrated from them.
The only numbers that are not physics are the level anchors (`ReferenceDb`, `CombustorDb`,
`WhineDb`): the SHAPE of each sound and the way it changes with rpm, Mach, lever and where you
stand come from the mechanism; the absolute level at one stated condition is a measured figure,
the same way a tyre's `ReferenceDb` is, because the constants in the radiation integrals are not
worth pretending to know to a decibel.

## Three mechanisms, and which aircraft is made of which

| | blades | jet | core |
|---|---|---|---|
| piston single | propeller (+ the car engine itself) | – | – |
| turboprop | propeller, constant speed | small hot core jet | rumble, whine |
| turbofan | ducted fan, supersonic tips | core AND bypass jets | rumble, whine |
| helicopter | main rotor + tail rotor, blade slap | small core jet | whine |

### Blades (`BladeRow`)

A propeller, a rotor, a fan and a tail rotor are the same object: a row of blades sweeping past
the listener, heard as a pulse train at the blade-passing rate `blades × rpm / 60`.

Each passage is one pulse made of the two classical terms. **Thickness noise** — the blade's own
volume pushing air aside — is the second time derivative of the swept section and so a symmetric
pulse; it scales with the tip Mach cubed and with the section's thickness ratio, and radiates in
the plane of the disc. **Loading noise** — the lift the blade carries — is the first derivative,
antisymmetric, scales with tip Mach squared and with the load, and fills in off the plane.

The pulse's **width** is the blade passage time, `chord / tip speed`, compressed by `1 / (1 − M_r)`
where `M_r` is the tip Mach *toward the listener* — the Doppler of a source swinging at you. This
is the single most important thing in the model, because it is what makes a propeller's harmonics
appear: at Mach 0.5 in the plane a passage is a soft thump and the sound is nearly a sine at the
blade rate; at Mach 0.8 it is a crack and the harmonics climb for an octave or two. That is why a
light aircraft buzzes at full power and hums at cruise, why a turboprop's six-blade note is harder
than a two-blade's, and why you hear the harmonics change as an aircraft passes overhead: the
in-plane component of the tip speed changes with your elevation. Nothing declares any of it.

**Blades are not identical.** A per-blade scatter of a per cent or two in pitch and track puts
energy at the SHAFT rate and its multiples under the blade-passing tone — the once-per-revolution
wobble under every real propeller note. On a fan whose tips go supersonic (the airliner at 5,200
rpm N1: Mach 1.24) the scatter is in the per-blade shock strengths and is large, and the result is
the **buzz-saw**: a comb of shaft harmonics with dozens of teeth. It emerges from blade differences;
there is no buzz-saw generator.

**Blade-vortex interaction** is a third, much shorter pulse a fraction of a revolution behind each
passage: a rotor's blade cutting through the tip vortex the blade ahead left in the air. A
helicopter descending or in fast forward flight slaps; hovering it does not. `Descending` (0..1)
on the synth is the one input that is a flight state rather than a part.

A **ducted** fan is heard forward out of the inlet, barely behind, and the duct will not carry
anything below about half the blade-passing rate.

### Jets (`JetStream`)

Lighthill: acoustic power `W = K ρ₀ (ρ_jet/ρ₀) U⁸ D² / c⁵` with `K = 10⁻⁴`, the same function the
exhaust's `JetNoise` uses; pressure at one metre from `p² = W ρ₀ c / 4π`, then the per-stream anchor
(`CoreJetTrimDb` −16, `BypassJetTrimDb` −21) that pins the balance the ear approved. Twice the velocity is 24 dB, which is why an
engine that is a roar at takeoff is a hiss at idle. The spectrum peaks at Strouhal 0.2 on the nozzle
— 160 Hz for the airliner's 0.6 m core at 480 m/s, 41 Hz for its 1.45 m bypass at 300 — and is
two decades wide, so two band sections an octave apart. It is loudest thirty to forty degrees off
the axis behind the engine and about 9 dB quieter straight ahead (`JetStream.Directivity`). A slow
amplitude breathing at a few hertz is the rumble of a big jet.

### The core

Combustion is a low broadband rumble (two poles at 140 Hz) whose level follows fuel flow, taken
as spool speed cubed. Compressor and turbine blade-passing tones are mostly above hearing; the one
or two that are not are `WhineHz` at `WhineDb`, scaled with spool speed cubed and radiated forward.
What you hear of a helicopter's turboshaft is that whine and nothing else of the engine.

The spool follows the lever with `SpoolSeconds` (5 s for a big fan, 2 for a turboshaft), so a
lever movement is heard as a rise, never a step.

### Piston aircraft reuse the car engine

`EngineKey = "aero_flat4"` is a new `EngineProfile`: a 5.2 litre flat-four (130 × 98 mm, 8.5:1,
2,700 rpm, short stubs into a small absorptive muffler each side, exits 0.9 m apart). The same
`EngineSynth` integrates it, with the propeller as a fan-law load (`torque ∝ rpm²`, sized to run out
at the redline at full throttle) and `PropInertiaKgM2` on the crank. At 2,700 rpm its firing rate
is 90 Hz, exactly the two-blade prop's blade-passing rate, which is why a light aircraft sounds like
one thing from the ground.

## Presets

| key | what | the numbers that decide the sound |
|---|---|---|
| `piston_single` | light single | 2 blades × 1.93 m at 2,700 rpm: BPF 90 Hz, tip Mach 0.80 |
| `turboprop` | regional turboprop | 6 × 3.93 m at 1,200 rpm: BPF 120 Hz, tip Mach 0.73; core 0.35 m, 220 m/s |
| `airliner` | high-bypass turbofan | fan 24 × 1.55 m, N1 1,200–5,200: BPF 2,080 Hz, tip Mach 1.24; core 0.6 m 480 m/s (133 dB anchored), bypass 1.45 m 300 m/s (123 dB) |
| `helicopter` | light turbine helicopter | main 2 × 10.16 m at 394 rpm: 13 Hz; tail 2 × 1.65 m at 2,550: 85 Hz; BVI 0.55 |

## The flyover renderer (`--aircraft`)

The machine is integrated in ITS time, and every sample is deposited at the moment it ARRIVES —
emission time plus path length over the speed of sound — with the inverse-distance gain and the
air's absorption for that path (a corner near 4 kHz at 100 m, 2 kHz at 300, 1 kHz at a
kilometre). Doppler is not applied; it happens, because the path is shortening. A second arrival
off the ground (70 %, duller) gives the flyover its slow comb. The listener is passed into the
aircraft's frame every 64 samples so the directivities and the tip Mach toward the ear follow the
geometry. Output is normalised for playback; the console prints the level at the ear at the closest
point (64–73 dB for the four default passes) and the peak.

```
dotnet OpenFPS.AudioLab.dll --aircraft [preset ...] [alt=m] [speed=m/s] [offset=m] [sec=s] [lever=0..1] [descend=0..1]
```

## Not done, in the order it should go

1. ~~**Ear-validation.**~~ Done 2026-09-18: *"the jets and planes sound really really good."* That
   render is the baseline. Its jet balance sat 16 dB (core) and 21 dB (bypass) under Lighthill with
   K = 1e-4 and is pinned as `CoreJetTrimDb` / `BypassJetTrimDb`; the WAVs are kept in
   `inbox/aircraft-demo-2026-09-18/`. Change the mechanisms, not that balance, unless a listening
   test says so.
2. **Into the game as a machine.** `MachineDefinition` parts `rotor`, `jet`, `core` alongside
   `engine`; an `AircraftVoiceState` on the render pool like `EngineVoiceState`, with
   `SetListener` for the directivities exactly as the exhaust has it; a server-side flight path
   (`AircraftSystem`: a route of waypoints with altitude, the way `VehicleSystem` follows a racing
   line). The renderer's arrival-time deposit is what the mixer already does with distance delay
   and Doppler, so nothing in the flyover code is needed in the game.
3. **Distance is the aircraft's whole character.** Air absorption strips the fan tone and the
   whine over a few kilometres and leaves the jet's low roar — "a faint roar with no whine" falls
   out of the mechanisms already in `AudioPhysics`. The rotor's 13 Hz and its first harmonics are
   what a helicopter is heard by from blocks away, and they diffract round buildings.
4. **What the model does not carry yet:** the ground-effect and turbulence modulation of a rotor
   in a hover; fan inlet distortion tones on the ground; afterburners (a low-bypass military engine
   is `K` higher, `U` past the sonic saturation, and crackle — a skewness the noise generator does
   not have); propeller–fuselage and rotor–tail interaction tones; the piston engine's exhaust
   directivity under a cowl; the turboprop's exhaust stacks as two sources like the F1's pipes.
5. **Levels are anchors and were set from certification-style figures, not measured against
   recordings.** When clips arrive, `--engine-match`-style fitting per octave, the way the
   footsteps were done — measure before playing.
