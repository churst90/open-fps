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
  stands out of the water (h·y/(h + y)). The water there runs at v_l = √(v² + 2g·drop).
- Air driven under goes as the flow through the site times the jet's excess speed over an onset (the
  plunging-jet law, fitted coefficient).
- Bubble sizes: the Deane-Stokes spectrum below the site's own Hinze scale (from the jet's dissipation,
  ε^-2/5, held to 0.5-3 mm), up to the size of the cavity the jet opens; and a share of each site's
  bubbles about its own characteristic radius, drawn once, so each stone repeats its own few notes.
- Time: a steady trickle of bubbles plus bursts at the site's shedding rate (St = 0.2), each burst's
  air log-normally uneven; a big burst may close on a pocket of air, a large bubble whose note climbs
  (ξ 0.35); the spilling crest throws spray (the fountain's splash, at the jet's speed).
- Each bubble's loudness for its size and depth, and the splash's efficiency, are the fountain's
  (`FallingWaterSynth`, fitted there, not refitted).

### 5.4 Falls

Each fall becomes a `WaterFallSpec` for the fountain's own physics (drops, lumps, splashes, plunge
bubbles), its events written into this source's places through `FallingWaterSynth.Placer`, its rates
following the flow (`FlowScale`). The sheet's thickness at the lip (weir law) decides how it arrives:
under 2 mm it fingers into strands that bead into drops; from 6 mm it falls coherent, in lumps the
size of its strands.

### 5.5 Cavities

A gully pot or a downpipe is a tube: a delay line of the round trip with the ends' reflections (an
open end -1, a water surface +1), the open end's loss above ka ≈ 1 in the loop, and a high-pass for
what the opening radiates. Normalised on white noise, so it colours and does not add level. What lands
inside is heard through it.

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

- Rain falling onto the running water itself (the rain model plays the surface it lands on).
- The pipe wall radiating along its length, and structure-borne noise through clamps.
- The vortex and gurgle where a gutter enters its downpipe, and trap seals clearing.
- Fall sizes changing with flow (only the rates follow the flow).
- Snowmelt.

## 8. Results

(Filled in after fitting.)

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
