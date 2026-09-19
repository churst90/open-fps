# Mowers and air conditioners, from the mechanism (2026-09-19)

Two of the things `docs/NEXT_THE_CITY.md` says a city needs and the engine did not have: *"crowds,
air conditioners, lawn mowers"*. Neither needed a new kind of sound. A mower is an engine under a
governor with a blade in a pan; a condenser unit is a fan and a compressor in a box. Everything they
are made of already existed for cars, aircraft and trains — what was missing was the combination, a
governor, and the broadband half of a slow blade.

Hear them: `--yard [preset ...] [levels] [pass] [sec= dist=]`.
Presets: `mower_push`, `mower_riding`, `ac_condenser`, `ac_window`.

## What is new, and why each of it had to be

**A governor** (`GovernorSpec`). A car engine is asked for a throttle and does as it likes; a mower
is not. Flyweights pull against a spring, and when the engine slows the spring opens the throttle.
It is a proportional controller with no integral term, so it *cannot* return to its setting under
load — it can only trade speed for throttle, and a mechanical one trades five to ten per cent. That
droop is the bog you hear going into thick grass, and the recovery after it is the flyweights
catching up. Two numbers and a response rate; there is no fourth.

Measured, on the push mower: 2,800 rpm free, 2,640 in thick grass, back inside a second, governor
nearly wide open at the bottom of it (`--yard mower_push` prints the rpm a second at a time).

**Broadband self-noise on a blade row** (`BladeRowSpec.SelfNoiseDb`). The aircraft's `BladeRow` made
only the tonal half — one pulse per blade per revolution. That is the right half for a propeller at
Mach 0.8, whose passage compresses by 1/(1−M) and concentrates everything into harmonics. It is the
*wrong* half for a mower blade at Mach 0.26 and hopeless for a condenser fan at Mach 0.06, where
almost all of what you hear is turbulence off the trailing edge and the tip. Added as a band placed
by a Strouhal number on the blade's own thickness (f = 0.2 U / t) and scaling with the cube of tip
speed — dipole radiation, power as the sixth. A thin fast fan hisses at 670 Hz; a blunt slow mower
blade roars at 1.6 kHz.

The aircraft presets declare none and are unchanged, bit for bit: at their tip speeds it would be
twenty decibels under the tone.

**Two small engines** (`mower_single`, `mower_twin`). A 163 cc OHV single, and a 500 cc 90-degree
V-twin. The single fires once every two revolutions — 24 Hz at its governed speed, below pitch,
which is why a push mower *chuffs*; the twin fires twice, 53 Hz, which is why a lawn tractor
*drones*. The twin's two bangs are 270 and 450 degrees apart because that is what a 90-degree vee on
one crankpin does, and the lope comes out of the firing table rather than being added.

**A deck** (`MowerDeckSpec`). A cylindrical pan, open at the bottom: a depth mode at a quarter wave
(903 Hz on a 21 inch deck) and one across it at a half wave (324 Hz). It **adds** — a resonator
gives energy back at its own frequencies, it does not consume it. Writing it as a blend of direct
and filtered made the deck a five-decibel *loss*, which is the opposite of why mower decks are loud.

**A compressor** (`CompressorSpec`). The hum is at **twice the line frequency** — 120 Hz in North
America, 100 Hz in Europe — because magnetic pull does not care which way round the field is. It has
nothing to do with how fast anything is turning, which is why every unit on a street hums the same
note. The pump is separate and lower: a two-pole motor under load turns near 3,450 rpm, so a scroll
compresses at 57 Hz, and the beat between 57 and 120 is why a compressor sounds restless rather than
steady. Both are inside a steel can, which passes them (it is far smaller than a wavelength) and
adds its own ring at 520 Hz.

## What it measures

`--yard levels`, each machine doing its job, RMS at one metre:

| preset | measured | engine | blade | cutting | compressor | casing |
|---|---|---|---|---|---|---|
| mower_push | **92 dB** | 91 | 85 | 47 | — | — |
| mower_riding | **96 dB** | 95 | 89 | 55 | — | — |
| ac_condenser | **65 dB** | — | 62 | — | 62 | 28 |
| ac_window | **59 dB** | — | 53 | — | 58 | 35 |

92 dB at a metre for a walk-behind is right: published figures for the operator's position are 92 to
96 dB(A), and the operator is a little over a metre from it. 65 dB at a metre for a condenser is a
modern quiet unit — manufacturers quote a sound *power* near 72 dB(A), and 72 less ten log of a
hemisphere at a metre is 64.

## The one counter-intuitive answer

**The cutting hiss is nearly inaudible, and that is correct.** A clipping weighs about five
milligrams; at the blade's eighty metres a second that is sixteen millijoules, which through the
same impact constant as every other struck thing here (`PanelAcoustics.ImpactReferenceDb`: one joule
is 74 dB at a metre) is a 57 dB tick. Ten thousand a second is a hiss in the fifties against a
machine in the nineties — forty decibels down.

That contradicts the obvious guess, so it is worth saying plainly what the difference you actually
hear between a mower in grass and a mower on a path is: **the engine bogging**. The load path, not a
hiss. The model is allowed to be as quiet as the arithmetic says instead of being propped up to meet
the expectation, and `SmallMachineTests.CuttingIsAsQuietAsTheEnergyArithmeticSaysItIs` holds it
there.

## A trap, paid for once

`AcousticRegistry.GetProperties("Steel")` returns **Generic** — 1,200 kg/m³ and 5 GPa, which is a
plastic — and says nothing about it. Steel is spelled `"Metal"`. Both the deck and the cabinet were
built out of a plastic until the mode series came out wrong (`0.8 x 0.9 m of 0.8 mm` ringing at one
mode instead of six). `SmallMachineTests.TheMetalPartsAreActuallyMetal` catches the next one.

## Not in the game yet

Same position the aircraft are in. `SmallMachineSpec` is in the `ModelLibrary` under
`small_machine`, so a map can name one or write its own; what is missing is the client voice path —
a stationary machine needs the same treatment a vehicle's engine gets in `ClientAudioSystem`
(a render-pool voice, a place, an extent, a level). That is the next step, and it belongs with the
city block rather than before it.
