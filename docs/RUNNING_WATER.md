# Running water

Cody, 2026-10-06: "start on the running water model so fountains and rain and such sound realistic."

Until now only falling and splashing water existed: the Elm Park fountain (`FallingWaterSynth`) and
the rain (`RainSynth`, `RainPatch`, `NearDrops`). Nothing flowed. This document is the research and
the design for a model of water that runs: creeks, street gutters, drain grates, downpipes, a
fountain basin's overflow, run-off after rain. Code: `OpenFPS.Common/RunningWater.cs`,
`OpenFPS.Common/Runoff.cs`, `OpenFPS.Client.Core/AudioEngine/Core/Nature/RunningWaterSynth.cs`.

Tags on facts below: **[ft]** read in the full text, **[abs]** read in the abstract only,
**[recalled]** general knowledge not checked against a source. Section 9 lists the sources.

## 1. Where the sound comes from

### 1.1 Bubbles

Almost all of the sound of running water in air comes from air bubbles ringing just under the
surface (Minnaert 1933; Leighton and Walton 1987 [abs]: the sound of a brook is "predominantly a
succession of Minnaert-like oscillations").

- A bubble of radius R rings at f = (1/2πR) √(3γP₀/ρ). With γ = 1.4 at sea level, f·R ≈ 3.3 m/s;
  isothermal, 2.8 m/s. The usual figure is 3.26 m/s (Phillips et al. 2018 [ft]; van den Doel 2005
  [ft] uses 3/R). A 1 mm bubble rings at 3.3 kHz, a 5 mm bubble at 650 Hz.
- It is set ringing at birth, when the neck joining it to the air above collapses (Deane and Czerski
  2008; Czerski and Deane 2010 [ft]).
- It dies in a few dozen cycles. Radiation damping is a constant δ ≈ 0.014; thermal damping adds
  0.06 at 0.2 mm, 0.02 at 1 mm, 0.01 at 5 mm (Zheng and James 2009 [ft], appendix A). Amplitude decay
  rate d = 0.13/R + 0.0072 R^-1.5 s⁻¹, R in metres (van den Doel 2005 [ft], eq. 5). That is 360/s at
  1 mm (a 19 ms ring to -60 dB) and 49/s at 5 mm (140 ms). `FallingWaterSynth.BubbleDecay` already
  uses this law.
- Its note rises as it nears the surface. van den Doel models f(t) = f₀(1 + ξ d t): ξ ≈ 0.1 suits drop
  bubbles, ξ = 0.5-1 sounds like bubbles from a nozzle, and the rise is audible only on bubbles of
  about 4 mm and up [ft]. A bubble right at the surface rings √2 higher than deep (Strasberg 1953, via
  van den Doel [ft]); Zheng and James saw 2-3× in complex geometry [ft].
- Loudness for size: at a fixed fractional wall amplitude ε the pressure goes as R (Longuet-Higgins
  1992, as used by Moss et al. 2010 [ft]: A₀ = ε R, ε 0.01-0.1). Nelli et al. 2024 [abs] measured
  bubbles from a stream into a pool: p goes as about D^0.55. van den Doel's energy argument gives
  R^1.5. The fountain model uses R (fitted against recorded fountains, 2026-10-04).
- Into air. A bubble just under the surface radiates as a dipole (Pumphrey and Crum 1990 [abs]).
  Phillips, Agarwal and Jordan 2018 [ft] measured a dripping tap's bubble at 24 Pa in water against
  0.28 Pa in air at similar distances, about 40 dB down, and showed the airborne "plink" is the
  bubble driving the floor of its cavity like a baffled piston. Capillary ripples (0.5 m/s) and
  flying droplets are effectively silent. Blocking the bubble (a rod, washing-up liquid) removed the
  sound. Sources within a fraction of a wavelength of the surface pass far more power into air than
  deep ones (Godin 2006 [ft], "anomalous transparency"). Running water's bubbles are within
  centimetres of the surface.

### 1.2 How many and how big

- Under a breaking surface the count goes as R^-3/2 below the Hinze scale (about 1 mm radius) and as
  R^-10/3 above it, the large ones made by turbulent fragmentation (Deane and Stokes 2002 [abs];
  Garrett, Li and Farmer 2000 [abs]). The Hinze scale falls as the turbulence is stronger, as ε^-2/5
  (Hinze 1955 [recalled]).
- Under spilling breakers radii run from 50 µm to 7.4 mm, most around 150 µm (Medwin and Daniel 1990
  [abs]).
- In a plunging jet's cone the bubbles are under 1-1.4 mm; secondary bubbles about 3-4 mm, the largest
  seen about 7 mm (review in Smit 2007 [ft]).
- Synthesis rates by ear (van den Doel 2005 [ft]): up to 10³ bubbles a second sounds like dripping or
  a small intimate stream, up to 10⁴ dense, 10⁵ a waterfall or heavy rain; more makes no difference.
  Removing the 0.2 mm bubbles always hurt stream sounds.

### 1.3 Where air gets in

Air enters only where the surface breaks. Each kind of running water has its own place for that.

- **Behind obstacles (creeks, riffles, gutters).** Flow over or round a stone drops into its lee and
  forms a small standing wave or jump. A jump is undular, with no roller and no air, for Froude
  number Fr₁ under about 1.4, and forms a breaking roller from Fr₁ 1.5-3 (Wüthrich, Shi and Chanson
  2022 [ft]). Air flux in a fully developed jump reaches 24 % of the water flow in its shear layer and
  50-60 % in total at Fr₁ over 6.6 (Wang and Chanson 2018 [ft]). No acoustic study of a single stone in
  shallow flow was found.
- **Plunging jets and sheets (steps, weirs, grates, downpipe outlets).** A falling jet entrains air
  above an onset speed of 0.8-1 m/s (review in Smit 2007 [ft]; Ervine et al. 1980). Air flux grows with
  (V - V₀): Chanson's re-analysis gives Q_a/Q_w ∝ Fr² under 5 m/s with Fr = (V - V₀)/√(g d_j).
- **Drops.** Medwin et al. 1992 [abs], by drop diameter: under 0.8 mm almost silent; 0.8-1.1 mm an
  impact and a microbubble near 15 kHz; 1.1-2.2 mm the impact only; over 2.2 mm the impact and a "type
  II" bubble at 2-10 kHz. Entrainment happens only in a restricted range of size and speed (Oguz and
  Prosperetti 1990 [abs]). Phillips et al. [ft]: a 4 mm drop falling 86 mm (1.3 m/s) traps a 0.36 mm
  radius bubble ringing at 8 kHz.
- **Thin films and sheets** (rain run-off across a road, water down a wall or a pipe): no acoustic
  literature was found. Physically a film has no mechanism that radiates: capillary waves are far
  slower than sound, and the gentle Mesler entrainment makes almost nothing (Pumphrey and Elmore 1990
  [abs]). A film is heard only where it breaks: an obstacle, a lip it falls off, the drain it pours into.

### 1.4 Drips

- A pendant drop leaves a lip when its weight beats the surface tension round the lip: m g = 2π r γ f,
  with f ≈ 0.6 (Tate's law with Harkins and Brown's correction [recalled]). A lip of a few millimetres
  lets go drops of a few tenths of a millilitre.
- Dripping turns to a jet at a Weber number of about 4 at the lip (Clanet and Lasheras 1999
  [recalled]), a few millilitres a second for a small lip.
- Dripping is close to periodic (Shaw 1984 [recalled]).
- The sound of a drip into a puddle is the trapped bubble (Phillips et al. 2018 [ft]).

### 1.5 Pipes

- Water down a vertical pipe runs as an annular film on the wall. It reaches a terminal speed after a
  few metres: Wyly and Eaton 1961 [ft], V_t = 3.0 (Q/D)^2/5 computed, 4.4 (Q/D)^2/5 measured (ft/s, gpm,
  inches). Aoki, Öhler and Kaltbeitzel 2025 [ft] predict 3.9 m/s at 1 L/s in a 110 mm pipe, 5.1 m/s at
  3 L/s. Scaled by (Q/D)^0.4 a 68 mm downpipe carrying 0.06 L/s (a house roof in moderate rain) runs
  at about 1.5 m/s, at 0.3 L/s about 3 m/s.
- The noise is the turbulent film plus impacts at the inlet, bends and tees; above 2 kHz the film
  dominates (Aoki et al. 2025 [ft], DN110 pipe to EN 14366). In buildings it is mostly structure-borne
  through the clamps (Däuble et al. 2023 [abs]).
- A pipe's air column rings at its own modes: a tube open at both ends at multiples of c/2L', one
  closed by water at odd multiples of c/4L', L' the length plus 0.6 of the radius at each open end
  (Levine and Schwinger 1948 [recalled]).
- The "glug" of a vessel emptying or a trap clearing is the gas compressibility oscillating (Clanet
  and Searby 2004 [abs]).

### 1.6 Drain grates and gully pots

No acoustic study was found. A road gully is a grate over a pot about 0.45 m across, its water seal a
few tenths of a metre below the bars. The gutter's water pours between the bars and falls into the
pot: a plunging jet into a pool, inside a closed air space with one opening. Nearest analogues:
plunging jets (1.3) and Watts et al. 2009 [ft], where water falling into brick cavities gave the most
energy at 250 Hz and water onto one boulder about 20 dB less there.

## 2. What each case is made of

| Case | Where the air gets in | What it sounds like | Model parts |
|---|---|---|---|
| Creek, riffle | jumps in the lee of stones, Fr locally over 1.5 | babble: repeated bubble notes from each stone, in bursts; hiss of small bubbles and spray | breaking sites |
| Creek, step or cascade | plunging sheet | splash and low gurgle | breaking sites, falls |
| Street gutter | small lips, leaf dams, grit; mostly too slow to break | a quiet trickle; the film itself is silent | breaking sites |
| Drain grate | the gutter's water plunging between the bars into the pot | gurgling pour, coloured by the pot | falls inside a cavity, drips |
| Downpipe | the film striking the shoe's bend; the stream out of the shoe; drips after rain | hollow rush in the pipe, splatter at the foot, then drips | falls inside a tube, an outside fall, drips |
| Basin overflow, weir | the sheet plunging into a sump | steady pour | falls inside a cavity |
| Run-off sheeting | none | silent, except where it falls | (the gutter, the grate) |
| Rain on running water | drops onto a moving surface | rain | the rain model already |

## 3. Flow, depth and slope against level and spectrum

- A small weir (10 cm wide, 30 cm fall, 1.1 L/s): about 67 dB(A) near 1 m; halving the flow lowered
  it about 6 dB(A) (Watts et al. 2009 [ft]).
- Fifteen waterfalls: LAeq 50-81 dB(A) close to the falls; across their flow range most within ±3 dB of
  the median-flow level. Cascades change more with flow than free falls, because more flow paths come
  into play (Schalko and Boes 2021 [ft]).
- Sub-aerial river sound tracks stage (R² 0.73-0.99) and comes from white water at roughness elements;
  taller elements give a cleaner relation (Osborne et al. 2021, 2022 [abs]).
- Underwater (hydrophones; in-air figures are rarer): riffles peak at 0.5-2 kHz, step-pools at
  0.125-0.5 kHz, larger obstructions lower; relative roughness, velocity over depth and Froude number
  explain most of the variance (Tonolla et al. 2010 [abs], 2011 [ft]). Bedload raises 2-16 kHz.
- In air, small water features are bright: at equal dB(A) water matches traffic at 1-2 kHz, is much
  higher at 4-8 kHz and about 10 dB lower at 63-125 Hz (Watts et al. 2009 [ft]).
- Spectral slope of breaking-wave noise: about -5 dB an octave from 1 to 20 kHz (Medwin and Beaky
  1989 [abs]), f^-11/6 from a closed-form model (Deane and Stokes 2010 [abs]).

## 4. The parameters a model needs

- The flow, L/s: its own (a creek's base flow, a pump) and what the rain brings (catchment area,
  run-off coefficient, how fast the catchment responds).
- The channel: its shape (stream bed, kerb gutter, none), width, slope, cross-fall, roughness
  (Manning's n), the length heard.
- What breaks the surface: how many obstacles a metre, how far the water drops behind them (with
  their spread), how wide a strip each gathers.
- Where it falls: the share of the flow, the height, the lip width, the number of strands, onto
  water or onto stone, inside a cavity or not.
- The cavity: length, diameter, whether the far end is open.
- The lip it drips from, and how far the drips fall onto what.

Depth and speed follow from these; nothing in a preset is a level or an equaliser setting. The one
level, `SourceLevelDb`, is measured from the model (AudioLab `--running-water levels`) so the mixer
can place it.

## 5. The model

### 5.1 Hydraulics (`Hydraulics`)

- Stream bed: Manning's law in a rectangle, v = R^2/3 S^1/2 / n, solved for depth.
- Kerb gutter: Izzard's form for a triangle against a kerb, Q = (0.376/n) Sx^5/3 S^1/2 T^8/3 in SI
  (FHWA HEC-22, 4th ed., eq. 4-2), T the spread from the kerb, depth at the kerb T·Sx.
- Weir: Q ≈ 1.83 b h^1.5 (sharp crest, Cd ≈ 0.62); the sheet leaves the lip at critical speed.

Example: 40 L/s down a 2.5 m cobble bed at 2 % is 4.3 cm deep at 0.38 m/s (Fr 0.58). A street gutter
draining 285 m² in moderate rain (5 mm/h) carries 0.27-0.36 L/s, 9 mm deep at the kerb, 30 cm wide,
0.2 m/s.

### 5.2 Run-off (`Runoff`)

- The rational method: Q = C i A (1 mm/h on 1 m² is 1/3600 L/s).
- Each catchment is a linear reservoir: outflow follows the rain through dO/dt = (i - O)/τ (Nash 1957
  [recalled]). It fills over a few τ when rain starts and runs on after it stops, falling by e every τ.
- τ is the catchment's: about a minute for a roof, three for a street. The field keeps one reservoir
  per rung of a ladder of time constants (30 s to an hour) fed by the world's rain; a source reads
  between the two rungs either side of its own. A voice made mid-shower starts already running, and
  every source on the map agrees. A player arriving in rain finds the reservoirs settled at the rain's
  rate. Snow is not run-off.
- A source fed only by rain with under 0.02 mL/s in it is dry and is given no voice.

### 5.3 Breaking sites (`RunningWaterSynth`)

- One site per obstacle (up to 40 rendered; more are stood for by rate).
- At each: the surface drops into the lee by the obstacle's height, or by the depth when the obstacle
  stands out of the water (h·y/(h + y)). The water there runs at v_l = √(v² + 2g·drop), as a sheet
  of thickness q/v_l.
- The lee jet meets the slower water behind the obstacle in a small jump. Air driven under goes as the
  flow through the site times how far the jet's Froude number stands over 1.4, the undular jump's
  limit (section 1.3). The coefficient is fitted (0.016). Deeper water drowns small obstacles: their
  jets fall under the limit, so a gutter or a creek does not simply grow louder with more water.
- Each bubble is heard through its image in the surface: a dipole, its pressure going as 2kz for a
  bubble z deep (Pumphrey and Crum 1990; Medwin and Beaky 1989), z drawn down to how far the jet drives
  its air. Small shallow bubbles carry further than their size says, which is why small water features
  are bright in air (Watts et al. 2009). The fountain's plunge drives bubbles deep, where the factor
  flattens; its law is unchanged.
- Bubble sizes: the Deane-Stokes spectrum below the site's own Hinze scale (from the jet's dissipation,
  ε^-2/5, held to 0.5-3 mm), up to the size of the cavity the jet opens; and a share of each site's
  bubbles about its own characteristic radius, drawn once, so each stone repeats its own few notes.
- Time: a steady trickle of bubbles plus bursts at the site's shedding rate (St = 0.2), each burst's
  air log-normally uneven; a big burst may close on a pocket of air, a large bubble whose note climbs
  (ξ 0.35), heard as a crater's bubble (the fountain's law, not the dipole: a cavity closing at the
  surface drives it like a piston, Phillips et al. 2018); the spilling crest of a jet over 0.8 m/s
  throws spray (the fountain's splash, at the jet's speed).
- Fitted (2026-10-06, section 8): the air coefficient 0.016, a quarter of the air in bursts, the
  bursts' spread 0.7, three tenths of a site's bubbles its own notes, the Hinze scale's reference
  dissipation 15 W/kg (the Hinze scale held to 0.5-3 mm), a glug in six bursts of ten.
- Each bubble's loudness for its size and depth, and the splash's efficiency, are the fountain's
  (`FallingWaterSynth`, fitted there, not refitted).

### 5.4 Falls

Each fall becomes a `WaterFallSpec` for the fountain's own physics (drops, lumps, splashes, plunge
bubbles), its events written into this source's places through `FallingWaterSynth.Placer`, its rates
following the flow (`FlowScale`). The sheet's thickness at the lip (weir law) decides how it arrives:
under 2 mm it fingers into strands that bead into drops; from 6 mm it falls coherent, in lumps the
size of its strands. A film down a pipe's wall arrives whole at its terminal speed (section 1.5,
(Q/D)^0.4), as if it had fallen v²/2g.

Onto wet stone (paving under a downpipe, the bars of a grate, a pipe's bend) a drop meets the film of
the same water first: its blow builds over 0.2 r/v, not in the first-contact microseconds, and a
share of its energy comes back as the rain's spray off a wet street (RainSynth, fitted to recorded
rain). Without it the lone drops on stone were one-sample ticks (10 ms kurtosis 9-10). The fountain
keeps its own impacts (`FallingWaterSynth.HardCushion` is zero there).

### 5.4a Rain on a gutter's water

A gutter's strip of water is not a surface the rain survey knows (it sees the asphalt), so the gutter
renders the rain on its own wetted strip: the rain model's Pool layer (clicks and, for 0.8-1.1 mm
drops, the regular bubble near 14 kHz) over its width along its length, at the rain falling now. A
creek's water is a surface of the map and its rain is the survey's.

### 5.5 Cavities

A gully pot or a downpipe is a tube: a delay line of the round trip with the ends' reflections (an
open end -1, a water surface +1), each open end keeping e^(−(ka)²/2) of what reaches it (section 11.1),
and a high-pass for what the opening radiates. Normalised on white noise, so it colours and does not
add level. What lands inside is heard through it.

### 5.6 Drips

Below the jet onset a lip drips: one drop every V/Q (Tate's law), nearly periodic (6 % jitter); each
an impact and, into a puddle, usually a bubble about a fifth of the drop's radius (Phillips et al.).

### 5.7 Heard from places along it

A creek or a gutter is heard from places along its length (`ExtendedSources`, the same rules as the
trees and the fire): each site belongs to the nearest place and its events are heard there with the
spread, otherwise at the middle. A grate, a downpipe or an overflow is heard from a ring of places
round where its water lands. Every event goes to one place: no place is a copy of another.

## 6. Fitting

- Yardstick recordings (never shipped) in `~/openfps-scratch-archive/running-water-2026-10-06/refs`,
  with `SOURCES.txt`.
- Fitted on the same statistics as the fountain and rain rounds: the cochlear band-envelope statistics
  (McDermott and Simoncelli 2011; `tools/texture_stats.py`, `TextureStatistics`), the 10 ms 4-16 kHz
  waveform kurtosis and crest, octave balance, and level against the anchors in section 3.
- Results: section 8.

## 7. Not modelled yet

- The pipe wall radiating along its length, and structure-borne noise through clamps.
- The vortex and gurgle where a gutter enters its downpipe, and trap seals clearing.
- Fall sizes changing with flow: only the rates follow the flow, so a fall grows about 3 dB a doubling
  where Watts et al. measured 6 for a weir. The film's terminal speed is set at the reference rain.
- The tail after rain is one exponential per catchment; real roofs drip longer (water held in gutters).
- Snowmelt.
- Taps and sinks (recordings gathered; no preset yet).

## 8. Results

Recordings: 29 (CC0, CC-BY, CC-BY-SA), in `~/openfps-scratch-archive/running-water-2026-10-06/refs`
with `SOURCES.txt`: 8 creeks and riffles, 3 gutters, 4 drains, 7 downpipes and spouts, 4 overflows and
weirs, 2 taps, 1 weak far creek. None binaural except creeks and rivers; no recording of a light
gutter flow exists that we found. Their texture statistics (20 s pieces) are in `TextureStatistics`
as "stream", "drain", "downpipe" and "overflow".

At a metre (AudioLab `--running-water levels`), the 14 fitted statistics inside the recordings'
spread, and the 4-16 kHz kurtosis in 10 ms windows:

| model | inside | kurtosis | recordings' kurtosis |
|---|---|---|---|
| creek 10 / 40 / 160 L/s | 14 / 14 / 13 | 3.25 / 3.12 / 3.15 | 2.95-6.96 |
| drain, 5 / 25 mm/h | 14 / 14 | 5.42 / 4.14 | 2.98-9.46 |
| downpipe, 5 / 25 mm/h | 13 / 12 | 4.82 / 3.70 | 3.44-11.24 |
| basin overflow, 2 L/s | 12 | 4.11 | 2.98-5.05 |
| gutter, 5 / 25 mm/h | 5 / 6 | 3.76 / 5.00 | 3.29-4.22 |

The creek's octaves from 250 Hz to 16 kHz sit inside the recordings' (2 kHz at their brightest). The
downpipe is darker than every recorded downpipe at 4-8 kHz. The gutter's two recordings are larger
flows than a 0.27 L/s trickle, both in heavy rain.

Levels at a metre (point-equivalent): creek 67.3 dB at 40 L/s (59 at 10, 73 at 160); gutter 51 dB,
drain 58 dB, downpipe 52 dB in moderate rain (39 / 47 / 34 at 0.5 mm/h, 58 / 70 / 63 at 70 mm/h);
overflow 67 dB. The drain's 58.4 dB(A) at 0.36 L/s against Watts's weir law: 57.

Through the game (client, mixer, HRTF, ear model, loudness law): no clipping, no gaps, the creek
heard wide (IACC 0.1-0.3 at 500 Hz-4 kHz from its bank), the drain and downpipe near a point. Renders
and the full table: `inbox/running-water-2026-10-06/README.txt`.

Cost of one core: creek 7.6 %, gutter 1.6 %, drain 2.3 %, downpipe 1.5 %, overflow 1.2 %.

Not connected to the Elm Park fountain: an overflow at 2 L/s adds 4.6 dB from 4 m south of its kerb
and dominates that side. A before/after pair is in the inbox for Cody to decide.

## 9. Sources

- Aoki, Öhler, Kaltbeitzel 2025. Forum Acusticum/EuroNoise, 533-540. doi:10.61782/fa.2025.0699
- Clanet, Lasheras 1999. Transition from dripping to jetting. J. Fluid Mech. 383, 307-326.
- Clanet, Searby 2004. On the glug-glug of ideal bottles. J. Fluid Mech. 510, 145-168.
- Czerski, Deane 2010. J. Acoust. Soc. Am. 128, 2625-2634.
- Däuble et al. 2023. Forum Acusticum. doi:10.61782/fa.2023.1088
- Deane, Czerski 2008. J. Acoust. Soc. Am. 123, EL126.
- Deane, Stokes 2002. Scale dependence of bubble creation mechanisms in breaking waves. Nature 418, 839-844.
- Deane, Stokes 2010. J. Acoust. Soc. Am. 127, 3394-3410.
- Ervine, McKeogh, Elsawy 1980. Effect of turbulence intensity on the rate of air entrainment by plunging water jets. Proc. ICE 69, 425-445.
- FHWA 2013. Urban Drainage Design Manual, HEC-22, 3rd/4th ed., ch. 4.
- Garrett, Li, Farmer 2000. J. Phys. Oceanogr. 30, 2163-2171.
- Godin 2006. Anomalous transparency of water-air interface for low-frequency sound. Phys. Rev. Lett. 97, 164301.
- Leighton 1994. The Acoustic Bubble. Academic Press.
- Leighton, Walton 1987. An experimental study of the sound emitted from gas bubbles in a liquid. Eur. J. Phys. 8, 98-104.
- Longuet-Higgins 1992. J. Acoust. Soc. Am. 91, 1414-1422.
- McDermott, Simoncelli 2011. Sound texture perception via statistics of the auditory periphery. Neuron 71, 926-940.
- Medwin, Beaky 1989. J. Acoust. Soc. Am. 86, 1124-1130.
- Medwin, Daniel 1990. J. Acoust. Soc. Am. 88, 408-412.
- Medwin, Nystuen, Jacobus, Ostwald, Snyder 1992. J. Acoust. Soc. Am. 92, 1613-1623.
- Minnaert 1933. On musical air-bubbles and the sounds of running water. Phil. Mag. 16, 235-248.
- Moss, Yeh, Hong, Lin, Manocha 2010. Sounding liquids. ACM Trans. Graph. 29(3).
- Nelli, Zhu, Ooi, Manasseh 2024. J. Acoust. Soc. Am. 156, 350-358.
- Oguz, Prosperetti 1990. J. Fluid Mech. 219, 143-179.
- Osborne et al. 2021. Earth Surf. Process. Landf. 46, 2656-2670. doi:10.1002/esp.5199
- Osborne et al. 2022. Water Resour. Res. 58. doi:10.1029/2021WR031567
- Phillips, Agarwal, Jordan 2018. The sound produced by a dripping tap is driven by resonant oscillations of an entrapped air bubble. Sci. Rep. 8, 9515.
- Prosperetti, Oguz 1993. Annu. Rev. Fluid Mech. 25, 577-602.
- Pumphrey, Crum 1990. J. Acoust. Soc. Am. 87, 142-148.
- Pumphrey, Elmore 1990. J. Fluid Mech. 220, 539-567.
- Schalko, Boes 2021. Water Resour. Res. 57. doi:10.1029/2021WR030980
- Smit 2007. MSc thesis, TU Delft (review of plunging-jet entrainment).
- Tonolla, Lorang, Heutschi, Tockner 2010. Hydrol. Process. 24, 3146-3156.
- Tonolla et al. 2011. Limnol. Oceanogr. 56, 2319-2333.
- van den Doel 2005. Physically based models for liquid sounds. ACM Trans. Appl. Percept. 2(4), 534-546.
- Wang, Chanson 2018. Can. J. Civ. Eng. 45, 105-116.
- Watts, Pheasant, Horoshenkov, Ragonesi 2009. Acta Acust. united Ac. 95, 1032-1039.
- Wüthrich, Shi, Chanson 2022. Environ. Fluid Mech. 22, 789-818.
- Wyly, Eaton 1961. Capacities of stacks in sanitary drainage systems. NBS Monograph 31.
- Zheng, James 2009. Harmonic fluids. ACM Trans. Graph. 28(3), 37.

## 10. Round 2: gurgles, taps and sinks

Cody, 2026-10-06, after hearing round 1: "all of the samples sound good for the running water, I like
them... continue down this path with the additional work for the gurgle and sinks." Code:
`RunningWaterSynth.Basin.cs`, new parts of `RunningWater.cs`. Sources in 10.9.

### 10.1 Water leaving through a hole

A sink's waste, a shower's grid and a roof gutter's outlet into its downpipe all sound by how deep the
water stands over the hole.

- Shallow, the water spills over the rim as a weir round an open air core and slides down the pipe.
  Deeper, it closes over the hole and the hole runs as an orifice. BS 6367's gutter-outlet forms, as
  back-calculated from HR Wallingford's measured outlets (Escarameia and May 1996, SR463/SR473 [ft]):
  weir Q = D h^1.5 / 7000, orifice Q = D² h^0.5 / 13200 (L/s, mm). They meet at h ≈ 0.53 D. In SI:
  Q = 1.44 L h^1.5 (L the rim length, π D for a plain outlet) and Q = 0.69 A √(2 g h).
- At the switch an outlet surges: "intermittent surging ... transition from weir to orifice flow and
  vice versa" (SR463 [ft]); a drop shaft's transitional regime pulses between full and weir flow
  (Water 5:1380, 2013 [abs]). Once submerged, a small rise in flow gives a steep rise in head.
- Over the hole a free-surface vortex draws air down while the water is shallower than its critical
  submergence. Gordon (1970) [recalled]: S = C v √D (SI), C 0.54 for a symmetric approach, 0.72 for an
  asymmetric one. The air core forms, strengthens, weakens and vanishes repeatedly near the critical
  submergence before it settles (LES, 2023 [abs]): the intermittent slurp.
- A bathtub vortex needs rotation to reach the drain: bubbles detach from the core's tip once its
  downflow beats their rise (Andersen, Lautrup and Bohr 2003 [ft]). A basin draining from rest has
  little circulation, so its vortex reaches the hole only when shallow.
- A bottle glugs about six times a second through a 20-40 mm hole, a little faster for a wider one;
  air enters in a quarter of each cycle, and each glug lets in about 17 mL, a bubble of about 1.6 cm
  radius ringing near 200 Hz (Perez, Monnet, Vidal and Joubaud 2026 [ft]; Clanet and Searby 2004 [abs]:
  the trapped gas is the spring and the liquid column the mass). The rhythm and the note are separate.
- A bubble let go from a nozzle or a hole rings from the collapse of its neck as it pinches off
  (Czerski and Deane 2010 [ft]); its note climbs as it rises at the surface (van den Doel 2005).
- A downpipe usually runs with an air core all the way down; the outlet joint is not sealed (SR463
  [ft]). A trap's water column sloshes like a U-tube, f = (1/2π)√(2g/L), about 1-1.5 Hz.

### 10.2 Taps

- Flows: kitchen 6-8.3 L/min, efficient basin 4.5-5.7, shower 5-9.5 (US EPA WaterSense [ft]; Adeyeye,
  She and Meireles 2020 [ft]).
- A smooth jet from a contoured nozzle drives no air under even at high Reynolds number; crests on a
  disturbed jet open cavities that close into bubbles (Zhu, Oguz and Prosperetti 2000 [abs]). The onset
  speed rises with free-jet length: 0.5 m/s for a 5 mm jet, 1.6 m/s for 100 mm, 2.1 m/s for 200 mm
  (Chanson and Manasseh 2003 [ft]). Their bubbles: 1.8 mm diameter (3.6 kHz) at 2.3 m/s, 3.8 mm
  (1.7 kHz) at 4.4 m/s. A tap falling 20-30 cm arrives at 2-2.5 m/s, near the onset.
- An aerator mixes air into the stream: it comes out white and lands soft.
- A falling stream thins as it speeds up, r = r0 (1 + 2 g z / v0²)^-1/4 (continuity).

### 10.3 Sinks and basins

- A stainless bowl is 0.7-1.2 mm steel with anti-drum pads. A flat simply supported panel's modes are
  (π/2) √(B/m″) ((m/a)² + (n/b)²); a pressed bowl is stiffer than a flat panel of its size (its radiused
  sides and the strainer's boss), so what drums is the flat between them. Steel's coincidence is near
  14 kHz at 0.9 mm: it radiates from its edges across the band. Water on it adds mass and lowers its
  modes (Lamb 1920 [recalled]), and damps them.
- A ceramic basin is thick and stiff: a hard surface, it barely rings.
- Filling a vessel raises its air column's note, f = c / 4(l + 0.62 R) (Bagad et al. 2025 [ft]; Cabe
  and Pittenger 2000 [abs]: listeners fill to the brim by ear). In a wide basin the end correction
  dominates and the rise is small.
- Washing-up liquid kills the plinks (Phillips et al. 2018 [ft]): not modelled.

### 10.4 The model

- **Inlet** (`FlowInlet`): diameter, open share (strainer, leaf guard), rim length, swirl (Gordon's C:
  0.72 for a gutter outlet fed from one side; 0.25 for a basin draining from rest, a judgement), the
  pipe below. Capacity: the lesser of the weir and the orifice above. A gutter's outlet stands at the
  depth that passes its flow; a basin's at its level.
- **Gurgle**: none while the water is shallower than 0.08 of the hole's open diameter, rising to full
  at 0.35 D, falling to none at the critical submergence (Gordon's S = C v √D, C the inlet's swirl).
  Air is drawn down at 0.8 of the water's volume at the full gurgle (fitted). Six tenths of it is shed
  steadily from the vortex's tip as small bubbles (0.05-0.18 D); the rest goes in gulps every 3.3 √(D/g)
  ± 70 % (about 6 Hz for a 30 mm hole, as a bottle). A gulp: pockets about 1.3 times the hole's radius,
  never over a bottle's glug (1.7 cm; log spread 0.35), each ringing at its Minnaert note with a steep
  climb (ξ 0.8) at 0.4 of the fountain's law for its size (fitted: a pocket drawn through a rim pinches
  off more gently than a drop's bubble), three smaller bubbles, the rush of air through the closing gap
  (band noise round c / 4π r), and a knock into the pipe below, which rings at its own modes.
- **The aerated stream**: the air the aerator mixes in (0.5 of the water, fitted) comes out where the
  stream lands; a quarter of it rings, as bubbles of 0.12-1 mm (fitted): the hiss of a running tap.
- **Basin** (`FlowBasin`): a level that rises with the tap and falls through the inlet. The first water
  stands in the dish over the waste (0.012 m²) until it is deeper than the bottom falls (6 mm). With
  the plug in only the overflow lets water go. `PlugWhileRunning`: the interact key that turns the tap
  on puts the plug in, and turning it off pulls it: a person filling the basin and letting it go.
- **The tap's jet** (`FlowTap`): onto the bare bottom while the water there is thinner than the jet
  (e^(-h/d), the circular jump of a tap into a sink still meets the steel), into the water once deeper.
  From the spout's height less the water's, already moving at the spout's speed. A plain spout's stream
  lands whole, in lumps the stream's size; an aerated one as millimetre drops round a core; a shower
  rose's jets as drops.
- **The plate**: a steel bottom's flat panel (0.2 m) as up to 20 two-pole modes, struck near its
  middle, weighted by the mode shape there and the edge radiation efficiency, coupled by the point
  mobility; water lowers and damps them. Its blows are the fall's own events.
- **Drips**: a leaking tap drips by Tate's law onto the bare bottom (and a steel one rings) or into the
  water standing in it.
- **Falls follow the flow** (round 1's open item): a fall's shape is worked out again as its flow
  changes, so a thicker sheet comes down more coherent, in bigger lumps that drive air under.
- **The downpipe's foot**: water leaving the shoe is already moving at the film's terminal speed.
- **Two-stage drainage**: a share of a roof's run-off (0.12) goes through a slow store (30 minutes):
  the long drip after rain.

### 10.5 Presets

`flow:kitchen_sink`, `flow:dripping_sink`, `flow:washbasin`, `flow:shower`, `flow:gutter_outlet`.
Taps start off (`SynthRunning` false in the prefab); the interact key standing within 1.3 m of one
turns it on or off (server `ToggleTapInReach`, before doors). A shut tap whose basin has drained is
given no voice.

### 10.6 On the city

Every flat in the five towers has a kitchen sink, a washbasin and a shower against its outer wall at
the far end from its sofa (333 flats, 999 fixtures). The Union Building's first-floor flat 11F has a
dripping kitchen tap. Every house on the estate has a downpipe at the garden end of its back wall and
its gutter outlet at the eaves (64 each). All appended at the end of `tools/gen_city.py`.

### 10.7 Not modelled

- Toilet flush and cistern refill: not built (they would need a timed sequence and a valve's hiss).
- The plug as its own control; a person standing in the shower; washing-up liquid.
- A filling vessel's rising note (the basin is too wide for it to matter); a kettle or a bucket.
- People in the flats using their taps (NPC use).

### 10.8 Results

Recordings: 26 (CC0, CC-BY, CC-BY-SA), in `~/openfps-scratch-archive/running-water-round2-2026-10-06/refs`
with `SOURCES.txt`: 7 taps into sinks (steel, ceramic, a bottle and a kettle filling), 7 gurgling drains
and bottles, 4 gutter and downpipe gurgles (none recorded at a downpipe's top with its position
stated), 3 drips, 2 showers, 3 toilets. Measured as round 1 (20 s pieces, the 14 texture statistics,
the 10 ms 4-16 kHz kurtosis, octaves).

At a metre (AudioLab `--running-water cycle` and `levels`):

| model | inside (of 14) | kurtosis | recordings' kurtosis |
|---|---|---|---|
| kitchen sink, running | 14 | 3.38 | 3.01-7.59 |
| kitchen sink, draining | 13 | 5.58 | 3.20-5.15 |
| washbasin, running / draining | 14 / 12 | 3.35 / 4.71 | as above |
| shower | 12 | 3.95 | 3.69-7.65 |
| gutter outlet, heavy / violent rain | 13 / 12 | 5.65 / 5.07 | 3.09-4.82 |

- Fitted: the aerator's air (0.5 of the water, a quarter of it ringing, 0.12-1 mm), the gulped air
  (0.8), the shedding share (0.6), the gulp's excitation (0.4 of the fountain's law), the pocket
  (1.3 hole radii, at most 1.7 cm), a looser rhythm (± 70 %), the slurp's band.
- Not fitted: the showers (the recordings are 11-22 dB brighter at 4-16 kHz, close to the spray); the
  violent-rain outlet is 4-6 dB heavy at 250-500 Hz (the gulp's air is a compromise between the drains
  and the gutters); the dripping tap rings steel where the recorded drips plink into water.
- Levels at a metre: kitchen sink 56.8 dB running, washbasin 51.9, shower 59.8, dripping tap 21.4
  (declared at the drip, so the mixer lifts it as a quiet source), gutter outlet 48 dB in moderate rain
  and 73.5 dB in violent, downpipe 53.3 moderate (round 1: 51.7), 66.0 heavy (58.9).
- A fall now grows 6.7, 4.6, 4.0 and 3.6 dB per doubling from 0.25 to 4 L/s (the basin overflow):
  steeper through the drop-to-sheet transition, as Watts et al.'s weir (6 dB(A)); 3 dB above it.
- After heavy rain a house's downpipe still carries 25 mL/s at 10 minutes and 15 at 25 (round 1: dry).
- Through the game (client, mixer, HRTF, ear model, loudness law): no clipping; draining is 2.7 dB over
  the kitchen sink's running and 9.2 dB over the washbasin's. Renders and tables:
  `inbox/running-water-round2-2026-10-06/README.txt`.
- Cost of one core: kitchen sink 2.0 %, washbasin 1.3 %, shower 2.4 %, gutter outlet 0.9 %, downpipe
  1.4 %.

### 10.9 Sources (round 2)

- Andersen, Lautrup, Bohr 2003. Anatomy of a bathtub vortex. Phys. Rev. Lett. 91, 104502.
- Adeyeye, She, Meireles 2020. Environ. Sci. Pollut. Res. 27, 4640.
- Bagad, Tapaswi, Snoek, Zisserman 2025. The Sound of Water. ICASSP; arXiv:2411.11222.
- Cabe, Pittenger 2000. Human sensitivity to acoustic information from vessel filling. J. Exp. Psychol. HPP 26, 313.
- Chanson, Manasseh 2003. Air entrainment processes in a circular plunging jet. J. Fluids Eng. 125, 910.
- Clanet, Searby 2004. On the glug-glug of ideal bottles. J. Fluid Mech. 510, 145-168.
- Czerski, Deane 2010. J. Acoust. Soc. Am. 128, 2625.
- Escarameia, May 1996. HR Wallingford reports SR463 (gutter outlets) and SR473 (flat roof outlets).
- Gordon 1970. Vortices at intakes. Water Power 22.
- Perez, Monnet, Vidal, Joubaud 2026. Emptying bottles filled with suspensions. J. Fluid Mech. 1036, A58.
- Phillips, Agarwal, Jordan 2018. Sci. Rep. 8, 9515.
- US EPA WaterSense specifications (faucets, showerheads).
- Wyly, Eaton 1961. NBS Monograph 31.
- Zhu, Oguz, Prosperetti 2000. J. Fluid Mech.; Phys. Fluids 12.

## 11. The ground under running water, 2026-10-06

Cody, hearing round 2: "house_downpipe_and_gutter_outlet_heavy.wav has a flanging/very fast repeating
sound to it ... there are other samples like it." Besides that the model was approved by ear.

The comb was the ground. Every synthesised voice carries its own ground reflection
(`GroundReflection`): its output again one path difference later, off the surface between the source
and the listener. Heard through the game, running water was one stream heard twice:

| scene | source height | listener | extra path | autocorrelation before |
|---|---|---|---|---|
| gutter outlet | 2.8 m | 1.8 m away | 8.1 ms (a comb every 125 Hz) | 0.34 at 8.08 ms |
| downpipe's shoe | 0.15 m | 1.5 m away | 0.69 ms | 0.26-0.33 |
| downpipe, from the house scene's garden | 0.15 m | 2.2 m away | 0.54 ms | 0.21-0.29 |
| basin overflow | 0.3 m | 1.5 m away | 0.92 ms | 0.36 |
| kitchen sink, washbasin | 0.9 m over the floor | 0.6 m away | 4.8-5.0 ms | 0.16-0.27 |

A texture of thousands of independent events spread over moving water is not one signal heard twice:
where on the water an event happens, the water's own movement and the listener's head all move the
reflection's phase. `GroundReflection.Texture` (set by running water's voices): below c / 4Δ, where the
two paths are within a quarter period wherever the event is, the ground lifts the bass by its full
pressure as before; above it the reflection adds its power, √(1 + g²) per band, with no delay. That is
Nord2000's incoherent term, |p_d + F p_r|² + (1 − F²)|p_r|², with the coherence F at zero. The
corner is under 30 Hz for the outlet and 360 Hz for the downpipe's shoe. Engines, machines, recorded
impulses and the shores keep the copy.

After (the game's path, `inbox/water-smoothing-2026-10-06/README.txt`): the gutter outlet's 8 ms peak
is gone (largest left 0.20 at 2.2 ms, its gurgle's own pitch, cepstral z 9.0 to 3.4); the downpipes
0.03-0.05; the house scene 0.07-0.08; the overflow 0.16 at 2.1 ms; the washbasin's 4.8 ms cepstral peak
gone. The creek, the kerb gutter and the drain had no comb (their water is at the ground); the kerb
gutter's 0.41 at 0.67 ms and the drain's 0.21 at 0.46 ms are each one broad spectral peak, the bubbles
of shallow water, and are unchanged. Filling the comb's notch makes the downpipes 1-3 dB louder, the
house scene 0.7-1.2 dB and the shower 1.9 dB; the others are within a decibel.

### 11.1 The pipe's round trip, 2026-10-07

Cody, hearing the after files: "the down pipe drains still flange." Measured per ear (the cepstrum of
8192-sample frames, 0.3-40 ms, a frame counting when its peak stands 6 standard deviations over the
median), the downpipe scenes had two kinds of peak left:

- 0.29-0.65 ms, in a third to all of the frames, wandering between a few lags. The same lags are in
  white noise played from the downpipe's own places through the same voices (AudioLab `--running-water
  game set=downpipes noise=1`: 80-100 % of frames), and in none of them with the HRTF off
  (`OPENFPS_HRTF=0`: 0 %), and the water's dry render has none. They are the HRTF's own notches (the
  pinna's), one set for each of the source's three places, and every binaural render has them (the
  approved waves: 25-55 % of frames at 0.31-0.42 ms). Not the water.
- 32.3 ms: the round trip of the downpipe's 5.5 m of air, in up to a third of the frames of the
  gutter outlet through the game and 55 % dry. The loop let each open end keep a single pole's worth
  at ka = 1, so a fifth to a third of a splash came back every 32 ms up to 6 kHz: a flutter of the
  splashes, the "very fast repeating" left once the ground's comb had gone.

An unflanged pipe's open end keeps e^(−(ka)²/2) of what reaches it [recalled; Levine and Schwinger
1948 to within about a tenth below ka 1.5]: a third at ka 1.5 (2.4 kHz in a 68 mm pipe), almost nothing
by ka 3. Round a downpipe that is e^(−(ka)²), as four one-pole low-passes in the loop (their (1 +
(f/f1)²)^-2 is e^(−2 (f/f1)²) where it matters), the line shortened by their delay so the modes stay
where the length puts them. The low modes are unchanged (the loop's 0.8 below a few hundred hertz), and
so is the level: the cavity is still normalised on white noise. The gully pot, the sump and the sinks'
wastes have the same law with their one open end.

Dry (`--running-water render`, heavy rain): frames with the 32.3 ms peak, gutter outlet 55 % to 1 %,
downpipe 14 % to 2 %; level within 0.3 dB (the drain 0.8 dB up). Through the game, per ear: see
`inbox/water-smoothing-2026-10-06/round2/README.txt`.
