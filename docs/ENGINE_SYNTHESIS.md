# Synthesizing a combustion engine

_Rewritten 2026-09-14. The previous model (a drawn blowdown pulse into a digital waveguide, with a
lope dealt out by a random walk) was replaced by a physical engine: cylinders with gas in them,
valves as flow boundaries, pipes in real units, a crank driven by the pressure on the pistons. The
earlier document's lessons about measurement still stand and are folded in below._

**The one thing to take from this document:** the sound is not designed, it is integrated. Every
number in `EngineProfile` is a physical quantity with a unit, and changing it changes the sound the way
changing the part would. When something sounds wrong, the first move is `--engine-trace` (the state of
the machine, quarter-second by quarter-second) and the second is `--engine-orders` (the spectrum in
engine orders). Six of the bugs found this session were invisible in the audio and obvious in the trace.

---

## What was researched, and what it said

Three lines of research went into the model, in `docs/` terms rather than as citations:

**engine-sim (ange-yaghi).** Its physics is a lumped 0-D gas model — no wave propagation at all. The
audio source is the gauge pressure of each cylinder's *runner* node (a small volume fed by
lift-shaped choked orifice flow), delayed per cylinder by `(L_primary + L_collector)/343`, summed per
exhaust, and then: a first-difference pre-emphasis weighted ~441:1, amplitude modulation by 2 kHz
low-passed white noise, ~0.2 ms of random delay jitter, a ≤227 ms recorded impulse response, and a
fast peak AGC. Its lope is per-cycle burn efficiency drawn uniformly from [0.24, 0.48] at idle. In
other words: the pulse SHAPE comes from a valve-flow model (which this engine now shares), and the
TONE comes from a recording plus four cheap tricks. That is the part this project does not do.

**The literature.** Farnell's *Designing Sound* engine (cosine pulses through a bell shaper, jitter
copied across cylinders with a 5 ms lag, a circular "warping" waveguide with anti-phase delay
modulation for the overdrive). Baldan/Delle Monache/Rocchesso's SDT motor (bidirectional waveguides
for every element, valve-modulated reflection 0.9 shut / 0.1 open, four unequal parallel muffler
lines, a per-cylinder phase asymmetry as the one "growl" knob) — and their own admission that it came
out "lower in energy in the medium-high frequencies". Doerfler & Wyse (2026): pulses should be
bipolar with a within-pulse downward glide because the hot gas leaves first. Selamet et al. (JSV
2004): a 0.5 m difference between Y-pipe branches moves the half-orders of a V6 by 5–15 dB. Lilly:
exhaust rises ~10 dB from no load to full load, and the firing rate is the strongest tone. The muffler
shootout data: a performance muffler costs 5–10 dB(A) at full throttle, a stock one 20–30, and idle to
full load is ~30 dB apart.

**The gas dynamics.** Port pulses of ~0.1 bar at idle and 0.5–1 bar at full load (Blair; US patent
10823593). Shock formation distance x_s = 1/(βεk): 0.7 m for a half-bar pulse in 600 °C gas — inside
any primary — and 7 m at idle, which is why an engine under load crackles and the same engine idling
does not (Matsumura et al., JSME 1990). Levine–Schwinger: a 63 mm tailpipe has |R| ≈ 0.9 at ka = 0.5
and 0.69 at ka = 1 (~2 kHz in warm gas), so the pipe is a mirror across the engine-order range and
radiates efficiently only above. Tailpipe jet noise goes as U⁶ (baffled, dipole regime) with a peak
at Strouhal 0.2; exit velocities of 2–3 m/s idling and 110–180 m/s at full load. Idle COV of IMEP
5–7 % stock, past 10 % with misfires for a big cam; within-cycle crank speed ripple of 40–60 rpm.

## The model

`OpenFPS.Common/Engines.cs` — the profile. `OpenFPS.Client.Core/AudioEngine/Core/Engine/`:

| file | what it is |
|---|---|
| `Waveguide.cs` | `WaveLine` (delay with finite-amplitude steepening), `Pipe` (bidirectional, gas-temperature tuned, viscothermal wall loss), `Junction` (Kelly-Lochbaum with a resistive flow-loss sink), `OpenEnd` (Levine–Schwinger reflection, end correction, monopole radiation `ρ/(4πr)·dU/dt`), `JetNoise` |
| `ExhaustNetwork.cs` | primaries → collectors → crossover (none / H / X / merged) → mufflers built from expansion chambers, absorptive sections and Helmholtz resonators → tailpipes → open ends |
| `IntakeNetwork.cs` | runners (waveguides) → a LUMPED plenum whose pressure is mass conservation between the throttle orifice and what the cylinders draw → airbox → snorkel → open end |
| `EngineSynth.cs` | cylinders (mass, energy, burnt fraction, piston kinematics), valve boundaries, combustion (Wiebe; diesel premixed spike), crank dynamics, idle governor, block noise |
| `Driveline.cs` | gearbox/clutch/mass/drag as one shaft; `Driver` for scripted orders; `VirtualDriver` follows a reported road speed |
| `../VehicleSynth.cs` | offline render for the lab |
| `../../Fmod/EngineProcessor.cs` | the FMOD DSP: the same engine, live, following an entity |

**The valve boundary is the heart of it.** The pipe end sees an incoming wave `a` and returns `b`;
its pressure is `p_mean + a + b` and its volume velocity `(b − a)/Z`. The valve passes a choked or
subsonic orifice mass flow set by the pressure ratio across it, and that flow over the port density
must equal the volume velocity. One monotone equation in `b`, solved by regula falsi. A shut valve is
`b = a` (a wall); an open one absorbs, feeds, or — during overlap — lets exhaust back into the
cylinder, which is the reversion that dilutes a big cam's idle. Two guards make it stable: the
exchange per sample is capped at what would equalise the cylinder with the port, and the volume
velocity is capped at Mach 0.3 of the pipe, because a choked valve into an empty cylinder asks a
linear pipe for more than any pipe can give and the equation then has no root.

**Steepening** is done by writing each sample of the forward wave at the arrival time its own
amplitude implies (`c0(1 + β p'/(γ p0))`) and interpolating between arrivals; a crest that would
overtake the foot is held to arrive just after it, so the front compresses to a shock and the
overtaken part is dropped — the weak-shock solution, without the clicks that overwriting produced. It
is amplitude-scaled from the real pulse, so it barely acts at idle and shocks hard at full throttle.

**Manifold pressure emerges.** The plenum is a mass at a temperature; the throttle is an orifice with
a 0.25 % leak and an idle bypass the governor sizes from the engine's own idle air need; the runners
draw from it. A single on a tiny plenum pulses the whole intake; a turbo raises the airbox pressure
and the plenum follows. `IdleMapBar` is now only used to size the bypass.

**Calibration.** Heat released per kilogram of charge is fitted (secant, closed cycle at the torque
peak) so full-load torque equals `PeakTorqueNm`; the pressure at exhaust valve opening then follows
from the cam and is reported. Friction is ~0.95 bar FMEP (7.6 Nm per litre) at idle, 1.5 for a diesel.

## What the bench says now

`--engine-orders v8_muscle` (2026-09-14, after the fixes below):

| rpm | MAP | port peak | level at 1 m | half/whole |
|---|---|---|---|---|
| 847 idle | 0.50 | 0.11 bar | 91.5 dB | +4.2 dB |
| 2069 WOT | 1.00 | 0.30 bar | 104.9 dB | −5.8 dB |
| 3540 WOT | 0.99 | 0.64 bar | 111.8 dB | −1.9 dB |
| 5312 WOT | 1.00 | 0.84 bar | 116.2 dB | −6.9 dB |

Against the research: port pulses 0.1 → 0.8 bar (target 0.1 / 0.5–1), idle-to-full-load rise 25 dB
(target ~30), levels in the loud-aftermarket band (105–120). The 1.6 stock four: 63 dB idling, 86–89
at full load, half/whole −10 dB.

## Bugs found by measuring (in the order they cost time)

1. **Runner mean pressure added twice at the intake valve.** Every cylinder saw a port at 0.2 bar
   instead of 0.6, filled to a quarter of its charge, and the engine could only idle by opening the
   throttle to 0.6 bar. Found by probing the valve residual in isolation (`--engine-solver`).
2. **A torque scale applied to compression too.** Calibrating heat to EVO pressure and then scaling
   torque ×2.16 doubled the compression resistance, and the starter could not get a big block over TDC.
3. **Explicit cylinder update at TDC.** A small cylinder with a wide-open valve overshot the port
   pressure every sample and rang the network up from silence at 90 dB with the crank stopped.
4. **Standing DC in the intake.** The mean breathing, carried as acoustic pressure between closed
   valves and a closed plenum stub, built half a bar of suction. Subtracting a running mean at the
   valve was tried and injected a false ram effect worth a quarter of the charge on a big single. The
   lumped plenum replaced both.
5. **The idle governor.** An integral-only loop hunted ±300 rpm; a bypass sized in pedal units opened
   4 % of the bore for 13 % "idle air"; sized from idle air need and given the spark as its fast term
   (as an ECU does), every preset idles within a few per cent of its target.
6. **Friction half of real.** All presets ran away to 1800+ rpm on a closed throttle until friction
   was set from displacement at 0.95 bar FMEP.
7. **Throttle-snap gulp.** The intake radiated the step in mean flow when the pedal moved, 22 dB
   over everything else. The mean now follows the pedal within 15 ms and the pedal itself moves in 40.

## The owner's first listen (2026-09-14)

"Way better ... slight artifacting ... maybe slightly more growly, but that's perfect honestly." Two
changes followed: the steepening delay no longer overwrites slots when a crest overtakes the foot (the
front is held to arrive just after it — the same shock, no click), and overrun pops are 1.5–3.5 ms
raised-cosine pulses rather than one-sample steps. For the growl, the big block's primaries went from
0.10 to 0.24 spread and its steepening to 1.15; the 800–3 kHz band at 3500 rpm full load rose from
−21 dB to −5 dB relative and the rumble (half-orders below order 3) from +2.8 to +4.1 dB. Growl is
half-order content plus mid-band rasp, and both come from real parts: unequal primaries and
finite-amplitude steepening. If more is wanted, those two are the knobs.

## Race engines, and the radiation bug they exposed (2026-09-14, session 3)

Two presets were added for the speedway map: `nascar_v8` (5.9 litre cross-plane pushrod V8, solid
roller cam, long equal-length 4-into-1 headers, **no muffler**, 9200 rpm) and `f1_v10` (3.0 litre
even-firing V10, pneumatic valves, ten trumpets in an airbox, open pipes).

**The crank is the difference, and it is the whole difference.** A 90-degree cross-plane V8 fires each
BANK at 90/180/180/270, so one pipe hears a pattern that only repeats every two revolutions and the
half orders carry real energy — measured half/whole of −4 to −7 dB, which is the rumble. The V10's
banks fire an even 144 degrees apart, so there are no half orders at all and what comes out is order
five on its own: at 15,000 rpm that is 1250 Hz, and it is the scream. Neither was designed; both fall
out of the firing angles.

Two things had to be fixed to get there.

**The intake was one throttle.** The V10 has ten 46 mm throttle bodies; entered as a single 46 mm one
it strangled above 12,000 rpm and the manifold never reached atmosphere at full throttle. The model
carries one throttle, so the right entry is the one of equivalent AREA — 145 mm. MAP then holds 1.00
bar to the limiter. Any engine with individual throttle bodies needs the same treatment.

**Radiation rose at 6 dB an octave for ever.** `OpenEnd.Radiate` was the monopole term,
`rho/(4*pi*r) * dU/dt`, which in the frequency domain is proportional to `omega * U`. That is right
only while the opening is small against the wavelength. Past `ka = 1` the radiation resistance has
already reached its asymptote of `rho c` per unit area, all of the wave leaves, and the radiated
pressure is the travelling wave — flat with frequency. Without the cap the V10 at 17,000 rpm put its
spectral CENTROID at 8.5 kHz, which is not a sound any engine makes, and the same mechanism is what
made the flat-plane V8, the single and the i4 sport read 10 dB high at full load.

The correction is one pole at `c/(2*pi*a)`: transparent below it, −6 dB an octave above, cancelling
the derivative's +6 and leaving the asymptote flat. About 2 kHz for a three-inch tailpipe in hot gas.

| preset | before | after |
|---|---|---|
| `f1_v10` at 15,000 WOT | 143.6 dB, centroid 3188 Hz | 138.1 dB, centroid 1001 Hz |
| `nascar_v8` at 9,000 WOT | 140.9 dB, centroid 9162 Hz | 135.9 dB, centroid 3014 Hz |
| `v8_muscle` at 3,540 WOT | 114.8 dB, centroid 849 Hz | 114.6 dB, centroid 777 Hz |

The big lazy V8 is untouched — its orders all live under a kilohertz — which is the test that the
cap is doing physics rather than EQ.

**Where the model runs out.** The V10 is capped at 15,500 rpm and the limit is the SYNTHESIS, not the
engine. At 44.1 kHz, 17,000 rpm is a firing every 26 samples; the blowdown transient is no longer
resolved, and the measurement says so plainly — half/whole goes to **+3.7 dB** on an engine that
fires evenly and can have no half orders at all. That is aliasing of the firing events. Fifteen five
measures −7.6 and is clean. Lifting it means oversampling the engine, and the engine is already the
most expensive voice in the mixer.

## What a car costs

`--engine-cost [preset ...] [kmh= sec=]` renders the GAME's voice — `EngineVoiceState`, the object
the FMOD DSP wraps — and reports how many seconds of engine one second of a core buys.

| preset | release | debug |
|---|---|---|
| `v8_muscle` | 9.2x realtime | 3.3x |
| `nascar_v8` | 8.9x | — |
| `f1_v10` | 7.9x | — |
| `i4_sport` | 18.9x | 6.9x |

So one V8 is about 12% of a core built for release and 30% built for debug. The mixer callback has a
hard deadline and still has HRTF, reverb and every other voice to do inside it, which is why the
client caps live engines at four (`OPENFPS_ENGINE_VOICES`) and why `run-gtk-client.sh` builds release
by default. A map may carry any size of field; the nearest few are the ones that sound.

## The speedway

`maps/speedway.json` — a one-mile banked oval, generated by `tools/gen_speedway.py` (the script is
the source; the JSON is the output). Eight cars, three kinds, no ambience bed.

A vehicle that names a `Track` laps it instead of shuttling a road. `RaceLine` resamples the map's
waypoints, offsets them onto that car's line, and gives every node a speed limit from the local
radius — `sqrt(g * 9.81 * R)` — then runs the brakes backwards round the loop so each limit is also
capped by what can still be shed before the slower node after it. The engine note pulling all the way
down the straight, lifting for the turn and settling is that profile, and nothing else.

Two things about it are worth remembering:

* **Curvature must be measured across a baseline longer than the waypoint spacing.** A map draws a
  turn as chords; measured across adjacent nodes, almost every triple is dead straight and the few
  that straddle a join are hairpins. The first field built this way lapped at 113 km/h in a turn
  they should have taken at 235, because one kink drags the whole braking profile down to it.
* **The grandstand's back wall runs from the ground, not from the deck.** The specular point for a
  car at 0.3 m and an ear at 14.3 m sits about eleven metres up, because the wall is fourteen metres
  from the ear and forty-five from the car. A wall starting at deck height answered only the cars far
  enough away to raise the bounce above it — the exact opposite of what a grandstand does.

From the spawn point a car on the front straight comes back off that wall 79 ms later at essentially
full level; one out of turn one, 32 ms; one on the back straight, 83 ms off the grandstand and 62 ms
off the far retaining wall. `--speedway speedway probe` prints exactly that table, and `--speedway`
plays the whole race without a server.

Reflections reach a live engine through `EngineReflections`, which was the missing piece: a
synthesized engine has no file to replay, so its echo is its own ring buffer read back at the
mirrored path's delay and placed at the image source. The read position is slewed rather than
stepped, so the echo Dopplers on its own as the car moves.

## Levels: where the headroom goes, and where it comes back

Three bugs in a row here, and they are worth writing down together because they look identical from
the listening chair — "overloaded, crackling, breaking up" — and are fixed in three different places.

**One. The synthesis had one clipping reference for every engine.** `EngineVoiceState` mapped a fixed
40 Pa (126 dB) to full scale and ran anything past it through a `tanh`. That is about right for a
road car and absurd for a race one: an unsilenced V10 peaks at 149 dB at a metre, thirteen times
over, so it did not arrive loud — it arrived square. Several existing presets were clipping too
(`v8_flatplane` 15x, `i4_sport` 3.8x). The reference is now `VehicleProfile.SourceLevelDb`, measured
per preset with `--engine-levels`, and `EngineSynthTests.DeclaredSourceLevelMatchesWhatThePresetMeasures`
fails if a declared level drifts from what the preset actually does.

**Two. Headroom is shared, not per voice.** The reference is the declared level plus
`PeakHeadroomDb` = 16, which is the largest SUSTAINED crest across the presets (the 99.9th
percentile against the mean; the absolute peaks go to 21 dB but those are individual backfires, and
rounding one transient is limiting where rounding all of them is clipping). It is deliberately ONE
number for every engine: normalising each voice by its own peak would let the crest factor leak into
the mix, so two equally loud cars would balance by the shape of their pulses.

**Three. The mix then sits low, and that is paid back at the master.** Every source in this game is
rendered at its true level relative to every other one — that is what `Loudness.Place` is for, and it
is the reason a listener can tell a rifle at two hundred metres from a pistol at twenty. The cost is
that a correct outdoor mix uses very little of the available range: sources leave room for their own
peaks and then spend another 30 dB on distance. Taking that back by making individual sounds louder
would destroy the very relative levels the game is built on, and it cannot be done per map anyway
because nobody knows what a map will carry.

So it is taken back once, for everything, on the master limiter's makeup gain, and the brick wall
catches what that pushes over. Metered on the speedway with the FMOD loudness meter now on the master:

| makeup | short-term | peak | |
|---|---|---|---|
| 0 dB | -29 to -33 LUFS | -14 dBFS | correct, and twelve decibels under where it belongs |
| 6 dB | -23 to -27 | -4.5 | clean, still quiet |
| **10 dB** | **-19 to -23** | **-3** | on target, limiter as a safety net |
| 12 dB | -17 to -21 | -0.5 | limiter engaging |
| 22 dB | -17 to -21 | -0.1 | no louder than 12, just squashed |

Ten is the last value that buys loudness rather than compression. **When a map sounds quiet, read the
"Mix loudness" line in the log before changing any level anywhere.** That, and "Mixer load", are also
how the two failure modes are told apart: a wrong level reference CLIPS (a continuous rasp, worse the
louder the source), a starved mixer TEARS (the same audio, stuttering). Both get called crackling.

While fixing this: the master limiter was being added at the DSP chain's TAIL alongside the boundary
reflections. FMOD runs a chain tail -> head -> output, so the limiter was running BEFORE the
reflections — the one thing it exists to prevent, a reflection pushing the master over the ceiling,
was the one case it could not catch. It is added at the HEAD now.

## Starting and stopping a voice

A synthesized engine has no zero-crossing to be stopped on. The waveform is wherever the crank
happens to be, so releasing a voice mid-cycle leaves a step, and a step is a click. On a track where
cars trade places in the voice budget every few seconds, that is a click every few seconds — heard as
"slight popping as they drive around", and easily mistaken for clipping.

Three things had to change, none of them a compressor (a compressor would have smeared the symptom
and left the discontinuity):

* **Starting.** A car entering the budget at 300 km/h used to start from a dead engine and a stopped
  driveline, with the virtual driver flooring it to catch up — a whole spin-up compressed into the
  80 ms the speed filter takes. `EngineVoiceState.PlaceAtSpeed` teleports the driveline, picks the
  gear the speed implies and spins the crank to match, so the first sample is already the right sound.
* **Stopping.** `TargetEnvelope` slews over about 60 ms and the voice reports `FadedOut`; the client
  keeps it rendering until then. A car that comes back into the budget mid-fade just has its envelope
  turned back up.
* **Reflections.** `EngineReflections` counted a wall's silence down for half a second and then cut
  the voice, which left the echo at its last gain and ended it on a step. The gain is now ramped to
  zero across the release, holding the last path — moving a dying echo would Doppler it as it faded,
  which is a whistle rather than a wall going quiet.

`EngineSynthTests.AVoiceFadesToSilenceWithoutAStepInTheWaveform` asserts the fade never introduces a
jump larger than the signal's own slew, which is the property that actually matters.

## Dead ends and cautions

- The linear waveguide plus a choked valve has no solution for a large sustained inflow; the Mach
  0.3 cap is the physical answer, not a fudge, but it means intake tuning effects above that are lost.
- `lope` in dB from the old instruments rewards regularity; read `chop` with it. Both are still
  in `--engine-orders`.
- `IdleGovernorGain` is the difference between a fuel-injected car and a carburetted one and it is
  audible: 0.4 hunts, 4 sits still.
- The block's combustion knock is scaled so a truck diesel radiates ~95 dB at a metre under load;
  without the soft limit a misfire's pressure jump came through as a 134 dB click.

## Tools

Build to tmpfs and run from there — a plain `dotnet run` hangs on this repo's volume, and the
levels and costs below are RELEASE figures:

```
dotnet build OpenFPS.AudioLab -c Release --artifacts-path /tmp/openfps-lab -p:UseSharedCompilation=false
cd /tmp/openfps-lab/bin/OpenFPS.AudioLab/release

dotnet OpenFPS.AudioLab.dll --engine-trace <preset> [idle|off] [rpm= thr= sec= dump dumpfrom=] [steep= wall= torque= rough= gov=]
dotnet OpenFPS.AudioLab.dll --engine-orders <preset> [rpm=a,b,c] [thr=] [wav]
dotnet OpenFPS.AudioLab.dll --engine-gallery [preset]
dotnet OpenFPS.AudioLab.dll --engine-cost [preset ...] [kmh= sec=]   # what a car costs a core
dotnet OpenFPS.AudioLab.dll --engine-live <preset> [kmh=30,60,90]    # the game's DSP, driven past you
dotnet OpenFPS.AudioLab.dll --engine-street [preset ...] [kmh=]      # a street, with the buildings answering
dotnet OpenFPS.AudioLab.dll --speedway [map] [seconds= voices=]      # the shipped race, from its own spawn point
dotnet OpenFPS.AudioLab.dll --speedway speedway probe                # what reflects off what, and when
dotnet OpenFPS.AudioLab.dll --vehicle-rev <preset> / --vehicle-live <preset>
```

Presets: `v8_muscle v8_sports v8_flatplane i4_economy i4_sport i6 v6 vtwin single diesel_i4
diesel_truck boxer4 v10 v12 nascar_v8 f1_v10`. A map places one with a `Vehicles` entry: give it
`RoadStart`/`RoadEnd` to shuttle a road (`maps/default.json`) or a `Track` to lap a circuit
(`maps/speedway.json`). The client hears `engine:<preset>` and runs it live.
