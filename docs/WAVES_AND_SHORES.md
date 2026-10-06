# Waves and shores

Cody, 2026-10-06: "How about waves on rivers ... waves lapping up onto shore, or water sloshing against
the side of a boat if you are on the water?"

Until now the game's water either fell (`FallingWaterSynth`: fountains, the rain) or ran
(`RunningWaterSynth`: creeks, gutters, drains). Standing water was silent: a pond on a real-place map
was a hard flat box. This document is the research and the design for the sound of waves at an edge:
a lake lapping on its beach, the sea's surf, a shingle beach's backwash, wavelets slapping a wall or a
moored boat's hull, a big river's bank. Code: `OpenFPS.Common/Waves.cs` (the sea and the specs),
`OpenFPS.Client.Core/AudioEngine/Core/Nature/ShoreSynth.cs` (the sound), the lab's `--waves`.
Underwater listening, swimming and boats themselves come later (section 9).

Tags on facts below: **[ft]** read in the full text, **[abs]** read in the abstract only, **[sec]** read
in a secondary source (named), **[recalled]** general knowledge not checked against a source,
**[estimate]** a judgement made here. Section 10 lists the sources.

## 1. The waves

### 1.1 What the wind raises

The sound at an edge starts with the waves that reach it, and on a lake, a pond or a river those are
raised by the wind over the water upwind of the edge: the fetch.

- Fetch-limited growth in deep water, JONSWAP's own laws (Hasselmann et al. 1973 [ft], eqs. 2.4.3,
  2.4.7), with χ = g F / U10²: g Hm0 / U10² = 1.6e-3 χ^½ and g Tp / U10 = 0.286 χ^0.33.
- The Coastal Engineering Manual (CEM, EM 1110-2-1100 Part II-2 [ft], eq. II-2-36) writes the same in
  the friction velocity: g Hm0 / u*² = 4.13e-2 (g F / u*²)^½, g Tp / u* = 0.651 (g F / u*²)^⅓, with
  C_D = u*² / U10² = 0.001 (1.1 + 0.035 U10). At a lake's fetch it gives a period a quarter shorter than
  JONSWAP's, so waves nearly twice as steep; steepness decides how waves break (section 1.3), so
  JONSWAP's own laws are used.
- The fully developed sea caps both: g Hm0 / u*² = 211.5, g Tp / u* = 239.8 (CEM II-2-37 [ft]).
  Pierson and Moskowitz (1964) [sec: CEM II-1] give lower heights at high winds; no map here reaches them.
- In shallow water the depth limits growth: Young and Verhagen 1996 [recalled],
  g Hs / U² ≤ 0.241 tanh(0.493 (g d / U²)^0.75)^0.87 and g Tp / U ≤ 7.519 tanh(0.331 (g d / U²)^1.01)^0.37.
- Time to grow: t = 77.23 F^0.67 / (U^0.34 g^0.33), U the wind speed (CEM II-2-35 [ft]): about the
  time the waves' energy takes to cross the fetch at their group speed. A 1 km lake at 5 m/s takes half
  an hour; a 150 m pond ten minutes.
- No waves under a ten-metre wind of about 1.5 m/s: wind waves need a friction velocity of a few
  centimetres a second to start (Kahma and Donelan 1988 [recalled]).
- The spectrum: JONSWAP, E(f) = α g² (2π)⁻⁴ f⁻⁵ exp(−(5/4)(f/fp)⁻⁴) γ^exp(−(f−fp)²/(2σ²fp²)), γ = 3.3,
  σ = 0.07 below the peak and 0.09 above (Hasselmann et al. 1973 [ft], eq. 2.4.5).

| water | wind | Hs | Tp |
|---|---|---|---|
| pond, 150 m, 1.5 m deep | 5 m/s | 3.1 cm | 0.56 s |
| lake, 1 km, 3 m deep | 5 m/s | 8.1 cm | 1.05 s |
| lake, 1 km | 8 m/s | 12.9 cm | 1.23 s |
| river, 120 m across | 5 m/s straight across | 2.8 cm | 0.52 s |

### 1.2 The angle of the wind

A shore source knows the fetch straight out from it and which way its water lies (section 6). For a wind
at an angle φ to straight onshore:

- On a round body (a pond, a lake) the water upwind is the chord of a circle, F cos φ [estimate: the
  shape assumed].
- On a strip (a river, a canal) the wind crosses it slantwise, F / cos φ, until the reach runs out.
- The waves reaching the edge carry the energy flux across it, E cg cos φ; refraction turns them toward
  the shore and keeps that flux, so their height is taken as H √(cos φ) [estimate].
- An offshore wind leaves the edge still: the lee side of a pond is calm.

### 1.3 Groups

A sea is a sum of many components with random phases. Its narrow peak (γ = 3.3) makes the heights rise
and fall in groups, a few big waves every five to ten. A swell's spectrum is narrower still and its
groups longer. Nothing here makes groups on purpose: the model's surface at each place is a sum of the
spectrum's components (16 for a wind sea, 12 for a swell), and each wave is found where the surface
crosses zero upward, with its own height and period.

### 1.4 Swell

The sea's surf is mostly swell from storms far away, which arrives whatever the local wind. A preset
for the sea says its swell's height, period and the angle its crests come in at. The crests are long:
every stretch of the beach sees the same swell, a little later along the beach by that angle, so a break
runs along it.

### 1.5 A river's eddies

A big river's current sheds eddies off the bank's roots, snags and riprap at a Strouhal number of about
0.2 (f = 0.2 U / D) [recalled]. Each rocks the water at the bank by about its velocity head, U² / 2g
[estimate]: 3 cm at 0.8 m/s. The model treats them as a narrow train of waves at the bank, whatever the
wind, so a big river's bank slaps and gurgles in a calm while a still pond is silent.

## 2. What a wave does at the edge

### 2.1 Breaking on a beach

- Breaker type by the Iribarren number ξ0 = tan β / √(H0 / L0) (Battjes 1974 [ft]): spilling under 0.5,
  plunging 0.5 to 3.3, surging or collapsing over 3.3. Waves begin to break at about ξ0 = 2.3. Battjes
  treats the boundaries as orders of magnitude.
- Breaker height: Hb = 0.39 g^⅕ (T H0²)^⅖ (Komar and Gaughan 1972 [recalled]), in a depth of Hb / 0.78
  (McCowan 1894 [recalled]).
- Run-up: R = ξ0 H0 for 0.1 < ξ0 < 2.3 (Hunt 1959 [sec: Battjes 1974]). Stockdon et al. (2006) [sec:
  Coastal Wiki] fit natural beaches with R2 = 1.1 (⟨η⟩ + S/2); the model keeps Hunt's simpler law.
- The swash front runs up as a thrown body of water, u0 = √(2 g R) [recalled; ballistic swash].
- How long uprush and backwash take: no sourced formula was found. The model takes 35 % and 65 % of the
  wave's period [estimate].

### 2.2 The air a breaker folds under

- Lamarre and Melville (1991) [sec: Lamarre 1993 thesis abstract]: 30-50 % of the energy a breaker
  dissipates goes into pushing its bubble plume under against buoyancy. Plumes lose 95 % of their air
  within one wave period. Field void fractions reach 24 %. Loewen and Melville (1994) [abs]: the air
  volume goes as the share of energy dissipated.
- Laboratory breakers of about a quarter of a metre fold under 0.02-0.05 m² of air per metre of crest
  [recalled, Lamarre and Melville's figures]: about half of Hb². The model: V = 0.5 Hb² per metre of
  crest, times (1 − 0.8 / c_b), c_b = √(g hb) the crest's speed. 0.8 m/s is a plunging jet's onset of
  entrainment (Ervine et al. 1980), the running water's (docs/RUNNING_WATER.md 1.3). A wavelet of a few
  centimetres breaks without air (microbreaking).
- Bubble sizes: R^-3/2 under the Hinze scale of about 1 mm (radius) and R^-10/3 over it (Deane and
  Stokes 2002 [abs]); Deane (1997) [abs] measured a^-2.5 and a^-4.5 in the surf zone.
- Only a thin outer shell of a plume radiates; above 500 Hz the sound is individual bubbles' oscillations
  (Deane 1997 [abs]). The plume's void fraction is 0.3-0.4. The model hears a share of the plume's bubbles
  (`PlumeHeardShare`, fitted).
- Below about 500 Hz, plunging breakers raise the level and spilling ones do not; the low sound lags the
  breaking by up to a third of a period (Loewen and Melville 1994 [abs]). Collective oscillations of a
  bubble cloud run 10-300 Hz (Lamarre and Melville 1994 [abs]; Prosperetti 1988 [abs]). The model has the
  plunger's air tube pulsing once as a large bubble (a twelfth of Hb across) that breaks up within a few
  cycles; the cloud's own modes are not modelled.
- Breaking noise underwater falls about 5 dB an octave (f^-11/6, Deane and Stokes 2010 [abs]).

### 2.3 The swash

- The front of the swash is thin and fast. It breaks like the lee jet of a stone in a creek, folding air
  under by how far its Froude number stands over 1.4 (`RunningWaterSynth.BreakingAirShare`, fitted on the
  creeks and not refitted). A surging wave rises without breaking and does not.
- Its spray: lumps of half the front's thickness at the front's speed, each a splash by the fountain's law
  (`FallingWaterSynth.Splash`).
- Sand: water forced into the dry sand above the water table drives the pore air out (Emery 1945 [abs]:
  sand holes, domes and "cavernous" sand). No acoustic study of it was found. The model: a share of the
  swash soaking in (a tenth) lets out its pore air (porosity 0.35) as bubbles pinching off at the sand's
  face, each ringing as it leaves (the fizz of a receding wave).
- Foam: a tenth of a breaker's air rides into the swash and bursts there. A bubble bursting at the
  surface rings as a Helmholtz resonator while its film's hole opens, its note climbing to about c / 10R
  (Poujol et al. 2021 [ft]): 34 kHz for a millimetre bubble, so only foam bubbles of a few millimetres are
  heard, as a sizzle.
- Mud and grass: the swash neither drains nor rattles.

### 2.4 Shingle and gravel

- The backwash drags the stones down the beach: those a stone of their size is lifted by, at
  u_c ≈ 1.4 √(g (s − 1) D) [estimate, a Shields number of 0.05 for a thin fast sheet], move; a share of
  the surface layer for each unit (u / u_c)² stands over one (`StoneShare`, fitted).
- Two stones meeting: a rigid sphere stopped in its Hertz contact time τ radiates its acceleration as a
  dipole, p ≈ ρ0 a³ Δv / (2 c r τ²) [recalled; Koss and Alfredson 1973]. For 3 cm flint closing at 0.3
  m/s τ is 130 µs and the click about 0.1 Pa at a metre, peaking near 5 kHz.
- Thorne's measured peak frequency underwater, f = 224 / D^0.9 (D in m) [sec: Geay et al. 2017], gives
  5.3 kHz for 3 cm: the contact-time estimate agrees. Rigid-body radiation matches Thorne's data
  (Thorne 1990 [abs]).
- No airborne measurement of a shingle beach's backwash was found.

### 2.5 A wall, a rock, a hull

- A wave meeting a vertical face is thrown back; at the face the crest stands up to twice the incident
  amplitude, rising and falling at 2π H / T [recalled; a standing wave's antinode].
- Where the face is rough the rising crest is thrown up as a sheet that tears into lumps, and what was
  thrown falls back from the height it reached [estimate]. A crest steep enough to break against the
  face (H / L over 0.1) strikes it at √(g H).
- Some crests close on air in a crack, between boulders, under an overhang or the flare of a bow: a
  pocket. Topliss, Cooker and Peregrine (1993) [ft] model a semicircular pocket against a wall:
  ω² = 2γp / (ρ r² × a log term), close to Minnaert's note for its size; 83 Hz for a 15 mm pocket in a
  flume, 100-300 Hz at Plymouth. The model rings each pocket at Minnaert's note for its radius (a
  twentieth to a quarter of the wave's height) by the fountain's law for a cavity closing at the surface,
  damped to vent within a few cycles. `ShoreSpec.TrapShare` says how often a face catches air: a smooth
  wall a few in a hundred, a jumble of rocks or a bow most of them.
- No study of the sound of wavelets slapping a moored boat was found.

### 2.6 The hull rings

- A hull is planking between frames: a plate. The bay's modes are RainPlate's (the rain's plate physics,
  `RainSurfaces.cs`): simply supported, (π/2) √(B/m″) ((m/a)² + (n/b)²), with the radiation efficiency and
  losses there.
- Its wetted share is loaded by the water's mass: for a bending wave of wavenumber k a fluid half-space
  adds ρw / k per square metre [derived; the standard result], lowering a mode by √(m″ / (m″ + w ρw / k)).
  Lamb (1920) [ft] found a clamped circular plate's fundamental lowered to 0.54 of its dry value in water.
- Driven two ways: the crest's blow (its momentum over the time it takes to pass its own thickness,
  through the rain's blow spectrum, `RainPlate.BlowMagnitude`) and a pocket's pressure ringing at its own
  note, which drives the modes near that note.
- A wooden clinker boat (12 mm planks, ribs at 0.3 m): 6 kg/m², the lowest wet modes 160, 390 and 580 Hz.
  An aluminium boat (2 mm, frames at 0.4 m): 5.4 kg/m², 19, 53 and 63 Hz, its modes far denser and
  ringing longer (loss 0.002 against wood's 0.02).

### 2.7 Out on the water

- Whitecaps: the share of the dominant waves breaking goes as the peak's steepness ε = Hp kp / 2, Hp from
  the variance between 0.7 and 1.3 fp (0.87 Hs for a JONSWAP sea). Below ε = 0.055 they do not break
  (Banner, Babanin and Young 2000 [ft]); above, b = 22 (ε − 0.055)^2.01 [recalled: the coefficients are
  in a figure legend not read].
- No whitecaps under 3 m/s (Monahan and Ó Muircheartaigh 1980 [ft]: W = 3.84e-6 U10^3.41 above it).
- The model counts whitecaps over a ten-metre strip of water in front of the edge [estimate: what a
  listener at the edge hears of the open water], each breaking along a third of a wavelength.

## 3. How loud

- Airborne surf: Bolin and Åbom (2010) [abs] measured 60 dB at 0.4 m wave height rising to 78 dB at
  2.0 m on the Baltic; their model scales with the dissipated wave power and the surf similarity. The
  distance and weighting were not read. Quoted elsewhere [sec: a citing paper]: 62 dB at 40 m, 58 dB at
  75 m, 53 dB at 150 m.
- That puts the acoustic efficiency of surf at about 1e-7 of the wave power dissipated: 62 dB at 40 m
  from a straight beach is 5e-4 W a metre of beach from about 5 kW a metre of wave power [derived here,
  half-cylindrical spreading].
- No measurement of lake waves lapping in air was found. At the same efficiency a lake's 8 cm waves (7 W
  a metre) would make about 50 dB two metres from the edge. Small waves break without air (section 2.2),
  so a lake is likely quieter than that.
- Each preset's `SourceLevelDb` is the model's own output at its reference wind, measured (`--waves
  levels`), as for running water.

## 4. What each case is made of

| Case | Waves | At the edge | Model parts |
|---|---|---|---|
| Lake, sandy beach | wind sea over its fetch | small spillers; swash fizzing into the sand | plume, front, spray, foam, vent, whitecaps |
| Lake, rocky edge | wind sea | thrown back; slaps; pockets between stones | spray, slap, pocket, whitecaps |
| Pond bank, reedy edge | a pond's wavelets (reeds take their share) | lapping on mud and grass | front, spray, pocket |
| River bank | the chop over the river, and the current's eddies at the bank | lapping, pockets in roots and riprap | front, spray, pocket |
| Sea, sandy beach | swell and wind sea | breaking out on the bar, bore running up the sand | plume, crash, front, spray, foam, vent |
| Sea, shingle | swell | plunging at the foot, backwash dragging the stones | plume, crash, front, spray, stones |
| Harbour wall | the harbour's chop and some swell | thrown back; slaps; pockets in the joints | spray, slap, pocket |
| Moored boat | the chop | slaps and pockets under the flare; the planking rings | spray, slap, pocket, hull |

## 5. The model

### 5.1 The sea at each place (`ShoreSynth`)

- The wind is the weather's mean wind (`WindField.Weather`), not the gusts: a gust is too short to
  raise waves. The sea glides toward what the wind would raise with a third of the growth time
  (section 1.1), never faster than ten seconds; a voice made mid-storm starts with its sea already up.
- Each place along the edge is its own column of water: its own wind sea, the shared swell at its own
  lag, its own eddies. Each component's frequency wanders inside its slice of the spectrum so the sum
  never repeats.

### 5.2 Each wave

Found at the zero up-crossing, with its height and period, then by the face (section 2):

- Beach: breaks or not by ξ0; a breaker with its crest over 0.8 m/s folds air under as a plume at the
  break line (a second row of places for surf breaking far out); a plunger's jet lands as spray and its
  air tube pulses once; the bore runs in (at √(g hb / 2)) and up; its front breaks and sprays; foam
  bursts; sand vents; shingle clicks; a rough edge catches an occasional pocket.
- Wall and hull: spray thrown up, falling back, pockets; on a hull the blow and the pockets drive the
  planking.

### 5.3 Crowds and singles

A surf plume is millions of bubbles; a lake's lap a few. Every population (bubbles by octave of size,
bursting bubbles, stone clicks, spray) is rendered event by event while there are no more than three to
a 128-sample block in a band, and as band noise of the same power above that: a crowd too dense to tell
apart is noise, and its envelope still moves with the waves and their groups. Its rate is uneven from one
40 ms to the next, log-normally (`Flicker`, fitted), as turbulent intermittency is (Kolmogorov 1962).

### 5.4 Heard from places along it

A stretch of edge is heard from places along it (`ExtendedSources`), each its own column, so its waves
arrive at different places at different times; a surf beach also from a row of places on its break
line. Every event belongs to one place, heard there with the spread, otherwise at the middle.

## 6. On the maps

- `tools/gen_osm.py` cuts the edge of every pond and lake into stretches of about 20 m along its real
  outline (OSM / Overture water areas) and places one source per stretch. Its box says what the waves at
  it are made from: X the stretch's length, Z the fetch straight out (a ray from its middle across the
  real outline to the far side), its +Z turned to the water.
- The client reads that into the voice's key, `shore:<preset>/<fetch>/<bearing of the water>/<length>`
  (`ShoreSpec.KeyFor`), and the voice works the fetch at the weather's wind from it.
- Which bank it is comes from the land cover just ashore: a reed bed where WorldCover has herbaceous
  wetland, a grassed bank (`pond_bank`) otherwise. Nothing open says what a pond's edge is made of.
- Swimming pools are skipped (a pool's edge is a skimmer); streams are the running water's.
- Albany: 219 stretches round its ponds and stormwater basins (fetch 7-185 m, median 28 m). Magnolia: 38
  round its pond. Albany has no river wider than Oak Creek, so no river bank is placed; the city has no
  water body and gets none.
- Still water is given no voice (`ShoreSpec.CalmAt`): no wind over 1.5 m/s, no swell, no current.

## 7. Fitting

- Yardstick recordings (never shipped) in `~/openfps-scratch-archive/waves-2026-10-06/refs` with
  `SOURCES.txt`.
- Fitted as the fountain, the rain and the running water were: the 14 cochlear band-envelope statistics
  (McDermott and Simoncelli 2011; `tools/texture_stats.py`, `TextureStatistics`), the 10 ms 4-16 kHz
  waveform kurtosis, octave balance, and level against section 3.
- Results: section 8.

## 8. Results

(Filled in after the fit.)

## 9. Not modelled yet, and what comes next

- The bubble cloud's own low modes (10-300 Hz) beyond the plunger's one pulse.
- Infragravity swash on wide dissipative beaches (swash periods of tens of seconds).
- Boat wakes on a river; wind over a river along the reach meeting the current (waves steepen against
  a current).
- Rain on the water is the rain's (the survey sees the water surfaces); ice is not modelled.
- Underwater listening: the same events heard from below (their sound in water is 30-40 dB stronger
  than in air, Phillips et al. 2018), through the water's own propagation, and the surface's reflection
  and Snell's window. Needs the listener to be under the surface: swimming, and the triangle geometry and
  terrain work for a bed to stand on.
- Boats: a hull that moves with the waves (its relative speed to the crests, not the crest's own),
  slaps from its own motion, the sound inside a hull as a cavity, oars, a mooring's creak. The hull model
  here is the plate those will ring.

## 10. Sources

- Banner, Babanin, Young 2000. Breaking probability for dominant waves on the sea surface. J. Phys. Oceanogr. 30, 3145-3160. doi:10.1175/1520-0485(2000)030<3145:BPFDWO>2.0.CO;2
- Battjes 1974. Surf similarity. Proc. 14th ICCE, 466-480. doi:10.9753/icce.v14.26
- Bolin, Åbom 2010. Air-borne sound generated by sea waves. J. Acoust. Soc. Am. 127, 2771-2779. doi:10.1121/1.3327815
- Deane 1997. Sound generation and air entrainment by breaking waves in the surf zone. J. Acoust. Soc. Am. 102, 2671-2689. doi:10.1121/1.420321
- Deane, Stokes 2002. Scale dependence of bubble creation mechanisms in breaking waves. Nature 418, 839-844. doi:10.1038/nature00967
- Deane, Stokes 2010. Model calculations of the underwater noise of breaking waves. J. Acoust. Soc. Am. 127, 3394-3410. doi:10.1121/1.3419774
- Emery 1945. Entrapment of air in beach sand. J. Sediment. Petrol. 15, 39-49.
- Ervine, McKeogh, Elsawy 1980. Proc. ICE 69, 425-445.
- Geay et al. 2017. J. Geophys. Res. Earth Surf. 122, 528-545. doi:10.1002/2016JF004112
- Hasselmann et al. 1973. Measurements of wind-wave growth and swell decay during the Joint North Sea Wave Project (JONSWAP). Dtsch. Hydrogr. Z. Ergänzungsheft A 8, 12.
- Hunt 1959. Design of seawalls and breakwaters. J. Waterways Harbors Div. ASCE 85, 123-152.
- Kahma, Donelan 1988. J. Fluid Mech. 192, 339-364.
- Komar, Gaughan 1972. Airy wave theory and breaker height prediction. Proc. 13th ICCE.
- Koss, Alfredson 1973. Transient sound radiated by spheres undergoing an elastic collision. J. Sound Vib. 27, 59-75. doi:10.1016/0022-460X(73)90035-7
- Lamarre, Melville 1991. Air entrainment and dissipation in breaking waves. Nature 351, 469-472. doi:10.1038/351469a0
- Lamarre, Melville 1994. J. Acoust. Soc. Am. 95, 1317-1328.
- Lamb 1920. On the vibrations of an elastic plate in contact with water. Proc. R. Soc. A 98, 205-216. doi:10.1098/rspa.1920.0064
- Loewen, Melville 1994. J. Acoust. Soc. Am. 95, 1329-1343. doi:10.1121/1.408573
- McDermott, Simoncelli 2011. Neuron 71, 926-940.
- Monahan, Ó Muircheartaigh 1980. Optimal power-law description of oceanic whitecap coverage dependence on wind speed. J. Phys. Oceanogr. 10, 2094-2099.
- Pierson, Moskowitz 1964. J. Geophys. Res. 69, 5181-5190. doi:10.1029/JZ069i024p05181
- Poujol et al. 2021. Sound of effervescence. Phys. Rev. Fluids 6, 013604. doi:10.1103/PhysRevFluids.6.013604
- Prosperetti 1988. J. Acoust. Soc. Am. 84, 1042-1054. doi:10.1121/1.396740
- Stockdon, Holman, Howd, Sallenger 2006. Coastal Eng. 53, 573-588. doi:10.1016/j.coastaleng.2005.12.005
- Thorne 1990. Seabed generation of ambient noise. J. Acoust. Soc. Am. 87, 149-153. doi:10.1121/1.399307
- Topliss, Cooker, Peregrine 1993. Pressure oscillations during wave impact on vertical walls. Proc. 23rd ICCE (1992), 1639-1650. doi:10.1061/9780872629332.124
- US Army Corps of Engineers 2002 (change 4, 2015). Coastal Engineering Manual, EM 1110-2-1100, Part II-2.
- Young, Verhagen 1996. Coastal Eng. 29, 47-78.
