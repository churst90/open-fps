# Fire at any size

Cody, early on: "Does the sound of the fire scale with the size? A bonfire in the back yard vs a group of
trees on fire, a raging blaze."

Until now the game had one fire: the garden fire pit at 58 Alder Street (`FireSpec.GardenFirePit`,
`FireSynth`). It was one body of flame heard from four places round a one-metre bed. Its puffing rate and
roar followed its width and heat release, but nothing else grew with size: no crown fire, no flare-ups,
nothing falling, and a crackle stream that a big fire would have turned into one steady hiss.

This document is the research and the design for fire from a campfire to a forest's crown fire.
Code:
- `OpenFPS.Common/Fire.cs`: the specs, the presets, the keys.
- `OpenFPS.Client.Core/AudioEngine/Core/Nature/FireSynth.cs`: the sound.
- `PowerLawNoise.cs` (beside it): the roar's spectrum.
- AudioLab `--fire`: the lab.
- `/spawn fire PRESET`: lights a fire in the game for testing.

Tags on facts: **[ft]** read in the full text, **[abs]** abstract only, **[sec]** read in a secondary
source (named), **[recalled]** general knowledge not checked against a source, **[estimate]** a judgement
made here. Section 11 lists the sources.

## 1. The flames

### 1.1 The law

- A flame whose heat release rate Q changes radiates as a monopole: p(r, t) = (γ − 1) / (4π r c²) dQ/dt
  [ft] (Dowling and Mahmoudi 2015). It holds for premixed, diffusion and partly premixed flames.
- Marcillo et al. (2025) use the same law for a prescribed burn [ft].
- Hurle et al. (1968) showed that the far-field pressure follows the time derivative of the flame's
  chemiluminescence: the monopole mechanism [sec: Shivashankara 1973].
- So a fire's roar is how unsteadily it burns. A steady flame of any size is silent.

### 1.2 What makes a fire unsteady: puffing

- A buoyant fire necks near its base and sheds a puff at f ≈ 1.5 / √D (Hz, D in metres). The fuel and the
  heat release do not matter. Measured on gas, liquid and solid fuels, on pools up to 8 ft (Byram and Nelson
  1969), and on wood (Maynard 2013 [ft]; Cetegen and Ahmed 1993 [recalled]).
- A 3 m pile of logs pulsed at 1.3 Hz while its flames stood tall and 1.0 Hz once it was a wide bed of
  coals. That gives D = 1.3 to 2.3 m in the law (Johnson, Anderson and Yedinak 2025 [ft]).
- Puffing stops when a fire breaks into many separate flamelets (a mass fire, a low burning rate per area)
  or becomes jet-like [ft] (Maynard). A fire much wider than its flames are tall is therefore many puffing
  bodies side by side, not one. Its flame height over its width falls below one at Q* < 1 (Heskestad;
  Guo et al. 2023 [ft]).
- Walls and corners lower the puffing rate (Guo et al. 2023 [ft]). Wind changes it, by an amount not found
  [abs].
- How far the heat release swings with each puff was not found. Twenty to forty per cent is an estimate
  [estimate]. The model fits it (section 8).

### 1.3 The spectrum

- Each puff is the fire's dQ/dt at the puffing rate, so its pressure peaks there, in the infrasound.
- The 3 m log pile: under 0.1 Pa at 50 m, from 1 to 20 Hz with energy up to about 40 Hz. It was heard at
  600 m only in the 5-15 Hz band [ft] (Johnson 2025).
- A 5.5 km² prescribed burn: coherent broadband infrasound from 2 to 40 Hz (90 Hz at the nearest station),
  waveforms within ±1 Pa. Its spectrum falls about as f^−1.4 from 1 to 10 Hz [ft, read off a figure]
  (Marcillo 2025).
- Above that the spectrum is the turbulent combustion's: PSD ∝ f^−α.
  - Measured α = 2.1-3.4 on turbulent flames (Rajaram and Lieuwen 2009; Abugov and Obrezkov 1978: 5/2)
    [sec: Chadwick and James 2011].
  - Clavin and Siggia (1991) derive 5/2 from Kolmogorov turbulence [sec: Chadwick and James 2011].
  - Chadwick and James (2011) chose 3.0 by ear for a burning brick [ft].
  - Vegetation fires: f^−2.2 at 50-500 Hz in the laboratory and f^−2.9 at 25 Hz-1 kHz in the field
    [derived from Viegas et al. 2008, ft].
- The peak frequency of a turbulent jet flame goes as its flow speed over its flame length (Strouhal number
  0.5-2.2) [sec: Haghiri et al. 2018]. For a buoyant fire, the visible flame motion and the sound collapse
  on one Strouhal number (Forest Products Laboratory 2025 [abs]).
- Crown fire against surface fire, from video sound tracks (shapes only: uncontrolled microphones and
  codecs) [ft] (Zhang et al. 2019; Khamukhin et al. 2017):
  - A crown fire peaks at 25-60 Hz, with its content mostly under 400 Hz.
  - A surface fire peaks near 220 Hz, with content to 15 kHz.
  - The low-band to high-band power ratio grows with intensity: 1.15 for an incipient surface fire, 2.1
    for an intense one, 30 for a mature crown fire, 65 for a firestorm.

### 1.4 How loud

- Thermoacoustic efficiency (acoustic power over heat release) of small open burners is 10⁻⁹ to 10⁻⁷
  (premixed 10⁻⁸ to 10⁻⁷; a primus stove 3 × 10⁻⁹; diffusion flames up to 6 × 10⁻⁷; jets up to 2 × 10⁻⁵).
  It peaks at 250-1500 Hz for laboratory burners, with weak directivity (about 3 dB) [ft] (Shivashankara
  1973).
- No measured efficiency for a buoyant wood or vegetation fire was found. The log pile's infrasound gives
  about 3 × 10⁻⁸ for a 3 MW fire swinging 30 % at 1.2 Hz [estimate, research brief].
- Viegas et al. (2008) [ft], vegetation fires:
  - Laboratory tables, microphone at 2 m, twelfth-octave bands: 40-46 dB at 16-63 Hz, about 30 dB at
    250 Hz-1 kHz.
  - Shrubs rise again to 45 dB at 10 kHz from their crackle; straw falls to about 20 dB above 2 kHz.
  - Field plots, distance not stated: a shrub plot 63 dB at 25 Hz (third octave), 33 dB at 1 kHz,
    40-42 dB at 4-8 kHz.
- No calibrated level at a distance was found for a crown fire, a house or a car. "Like a freight train"
  has no measurement behind it.

### 1.5 What the model does with it

- Every body of fire (section 7.1) has a puffing rate 1.5 / √D. Its dQ/dt at a metre is
  (γ − 1) / (4π c²) 2π f_puff s Q, where s is the share of Q that swings with the puffs (`PuffSwing`).
- Its spectrum peaks at f_puff, falls as f^−1.4 to ten times f_puff and as f^−α above that.
- Under 20 Hz nothing is rendered. Headphones do not play it and it is not heard, and a big fire's
  infrasound would otherwise be most of its declared level.
- So the roar heard is the f^−α tail. Its PSD at a frequency f goes as Q² f_puff^(1+α) f^−α.
- Bodies are independent, so their powers add. For a fire of area A with heat release q″ per square metre,
  cut into bodies of width D, the roar's power goes as q″² A D^((3−α)/2): about the area, times the heat
  release per area squared [derived].
- From the fire pit (0.9 m, 80 kW) to a house (120 m², 30 MW) that is 30 dB more roar. To a crown fire's
  300 m front (15 000 m², 9 GW) it is 60 dB more.

## 2. The fire pit as it was

- One body, 0.9 m, 80 kW, a roar band at 70 / √D Hz.
- Crackle at 28 a second per 100 kW times moisture and resin factors, sizes on a power law of exponent 2.2.
- A fizz of gas through the char.
- Steam jets that sometimes whistle; a log settling every minute or two with a flare.
- Fitted to three campfire recordings (texture round 1, 2026-10-06). It keeps all of that except the
  roar, which is now the f^−α tail with its swing fitted (section 8): 9 dB more unweighted, nearly all of
  it under 125 Hz, and 3 dB more A-weighted.

## 3. Sizes, heat release and life

### 3.1 Laws

- Heskestad's flame height: L = 0.235 Q^(2/5) − 1.02 D (Q in kW, L and D in m) [ft] (Guo et al. 2023).
- Byram's fireline intensity: I = H w r. With H = 18 000 kJ/kg, I = 300 w ROS (w in kg/m², ROS in m/min).
  Flame length is L = 0.0775 I^0.46 [ft] (Werth et al. 2011).
- t² growth, α (kW/s²): slow 0.00293, medium 0.01172, fast 0.0469, ultrafast 0.1876; these reach 1055 kW at
  600, 300, 150 and 75 s [recalled].
- McCaffrey's centreline velocity in the intermittent flame: u = 1.93 Q^(1/5) m/s (Q in kW) [recalled].
- Wood burns at 17.5 ± 2.5 MJ/kg and chars at 0.4-0.8 mm/min [ft] (Bartlett, Hadden and Bisby 2019).

### 3.2 The presets

| preset | what | area | bodies | heat release | flames | life (growth, full, decay) |
|---|---|---|---|---|---|---|
| campfire | a few logs on the ground | 0.7 m | 1 of 0.7 m | 30 kW | 0.6 m | 5 min, 1 h, 20 min |
| fire_pit | the garden fire pit (unchanged) | 0.9 m | 1 | 80 kW | 0.8 m | always burning |
| bonfire | a pile of logs and brush | 3.5 m | 1 of 3.5 m | 4 MW | 5 m | 10 min, 1 h, 30 min |
| burning_car | a car | 1.8 × 4.5 m | 2 of 1.8 m | 5 MW at peak | 3 m | 20 min, 10 min, 70 min |
| house_fire | a two-storey timber house | 10 × 12 m | 12 of 3 m | 30 MW | 10 m | 15 min, 1 h, 2 h |
| burning_trees | a stand of conifers | 15 × 15 m | 9 of 5 m | 10 MW and torching | 20 m | 5 min, 30 min, 30 min |
| crown_fire | a crown fire's front | 300 × 50 m | 24 of 25 m | 9 GW at 10 m/s wind | 45 m | always running |

Where the numbers come from:
- Campfire and fire pit: 20-100 kW from Heskestad on their flames [estimate]; no calorimetry was found.
- Bonfire: the 3 m Johnson pile grew to full size within about 3 minutes and burned down to coals in about
  5 hours [ft]. Flames of 4-6 m give 2-5 MW [estimate]. Its pile puffs as one body (1.3-1.0 Hz measured).
- Car: 148 tests (Miechówka and Węgrzyński 2025 [ft]):
  - Peak heat release: median 3.7 MW for small cars and 5.9 MW for medium ones (5-95 %: 1.2-8.5 MW).
  - Time to peak: median 18-24 min (2.5-52 min).
  - Total heat 4.5-6.3 GJ; burnt out in 85-120 min.
  - Each event (a window, the fuel tank) is its own jump in heat release, so the curve has many peaks.
- House:
  - The Oakland Hills estimate: 45 MW for an hour, then 10 MW for three hours and 5 MW for three more,
    about 0.2 MW per square metre of plan at the peak (Rehm et al. 2002, citing Trelles and Pagni 1997
    [ft]).
  - A modern furnished room flashes over in 3-5 min and peaks at 7.5 MW (Kerber 2012 [ft]).
  - The preset is a smaller house at that rate.
- Trees:
  - Dry Douglas firs 3.7-6.2 m tall peak at 7.4-36.8 MW within 5-10 s of ignition and fall away in about
    10 s (NIST TN 2327 [ft]).
  - Dry Fraser firs 2-2.5 m peak at 3.2-4.3 MW, their needles gone within a minute (Madrzykowski 2008 [ft]).
  - Living trees (foliage moisture about 100 %) torch only when a fire heats them, and more slowly.
  - A burning stand is a steady fire of trunks, branches and ground fuel with trees torching over it in
    turn [estimate].
- Crown fire (Werth et al. 2011 [ft]; an experimental crown fire at 43 MW/m, Fire 2020 [abs]):
  - Active crown fires run at 15-45 m/min and reach 10-100 MW per metre of front; an intensity can
    quadruple within seconds as a fire crowns.
  - Flames stand 15-45 m, two to three times the stand. The flaming zone is about 45 m deep at 60 m/min.
  - The wind above the canopy is 2.5-6 times the wind in the stand.
  - The preset's 30 MW/m over 300 m is a middling active crown fire.

### 3.3 Life

- A fire lit at a known moment grows as t² to its full heat release, burns, decays linearly and then
  smoulders at 3 % (`FireSynth.LifeShare`).
- Its bodies catch one after another across the area as it grows, so a house's rooms go in turn.
- A map's fire has no lighting time and is always fully developed.
- A fire lit with `/spawn fire` carries the moment it was lit in its key, `fire:<preset>/lit=<seconds>` on
  the shared clock (`WindField.Now`). Every client hears it at the same point of its life, whenever it
  comes into range.

## 4. Crackle

- What pops [recalled; Martinsson et al. 2022 for the water, ft]:
  - Steam pressure in cells above the fibre saturation point.
  - Pitch and resin pockets (terpenes boil at about 150-180 °C).
  - The char checking, and bark coming away.
  - Spruce crackles more than oak under the same heating (Martinsson et al. 2022 [ft]). Dry hardwood coals
    are nearly silent.
- Needles and leaves: a crackle is water jetting out of the mesophyll as a needle ruptures. Live conifer
  branches gave impulses peaking near 3 and 7 kHz, falling off by 10 kHz with a tail to 20 kHz.
  Six branches of each species, burning about 80 s each, made 118-448 events, about 0.25-1 a second a
  branch (Yedinak, Forest Products Laboratory 2020 [ft]).
- Species and water stress show in the 6-15 kHz band (Yedinak et al. 2017 [abs]).
- Shrub fires carry strong 1-10 kHz crackle; straw and litter carry almost none (Viegas 2008 [ft]).
- Size laws: wood's acoustic emission under load follows power laws in energy and in waiting time
  (Mäkinen et al. 2015 [abs]). No fire crackle statistics were found. The model's law (sizes to the
  power −2.2 over a range of 150, a dozen to twenty pops a second from a fire pit) was measured on three
  campfire recordings in round 1.
- Green wood hisses and sings through its end grain. No measurement of that was found.

### 4.1 A crowd of crackles

- A small fire's crackles are separate pops. A big fire's are thousands a second, and a crowd that dense is
  a noise of the same power whose loudness follows the rate.
- So each place draws its crackles one by one up to 240 a second, from the loud end of the size law down.
  The rest of the law is that place's crackle noise: the crackles' own band, its power their summed energy
  (each size's energy measured by rendering one), its level following the rate as it clusters. The same
  device is used for the surf's bubbles (docs/WAVES_AND_SHORES.md 5.3).
- How many crackles a bigger body of fire makes: the recordings say fewer per kilowatt than its heat
  release, and as loud in total. Bonfires, house fires and burning trees all have more distinct pops in
  their 3-12 kHz bands (kurtosis 5-70) than a fire pit's rate scaled up by heat release would give
  (about 3, a steady noise), and as much energy up there.
- So a body's crackles come at (Q / 80 kW)^β of the pit's rate, β = 0.5 (fitted). Each is
  (Q / 80 kW)^((1 − β) / 2) louder, so their energy still goes as what burns. Bigger fuel holds its water
  and resin in fewer, bigger pockets [estimate: the reading of the fit]. The fire pit is unchanged by it.

## 5. Big fires

### 5.1 Wildland fire sound

- Crown fires are heard mostly under 400 Hz; the crackle carries the top of a surface fire (section 1.3).
- The low/high ratio grows with intensity: the roar's power goes as heat release per area squared, the
  crackle's as the heat release (section 7).
- Air takes the top off with distance (ISO 9613-1, in the game's own path).

### 5.2 Torching

- A tree catching all at once: its foliage burns in seconds to tens of seconds at many times its steady
  heat release (dry firs: 5-10 s to peak, gone in about 10 s more [ft]). Its needles crackle as they go.
- The model:
  - Each body of trees torches now and then (once every one and a half minutes, an estimate). Its heat
    release rises over 3 s to 4-12 times its steady share and falls over 10-40 s; its crackle rate is
    tripled at the peak.
  - At a crown fire's front the torching is the front's own surges: 1.5-4 times, the intensity quadrupling
    within seconds as a fire crowns (Werth 2011).

### 5.3 The fire's own wind and the weather's

- Indraft into large fires runs from about 2 to 40 m/s (Trelles and Pagni 1997 [sec: Werth 2011]).
- Inside crown fires:
  - FROSTFIRE: updrafts 32-60 m/s, inflow 12-28 m/s, and flame fingers bursting along the ground at
    28-48 m/s, about ten times the 3 m/s ambient wind (Coen et al. 2004 [abs]).
  - ICFME: eddies metre-sized, lasting fractions of a second (Clark et al. 1999 [abs]).
- The model:
  - The weather's wind (`WindField`) is read at each place, so a gust crosses a big fire as it crosses a
    wood.
  - A gust fans a small fire's flames (more roar, more crackle). How much is scaled by how fast the fire's
    own plume rises (McCaffrey, about Q^(1/5)): a gust that doubles a campfire barely moves a house fire.
  - A crown fire's heat release follows its rate of spread, about U10^0.9 (Cruz, Alexander and Wakimoto
    2005 [recalled]), against the 10 m/s its heat release is declared at. It follows the wind over twenty
    seconds.
  - The fire's own indraft is part of its roar as measured. It is not added again.

## 6. Events

### 6.1 Falls

- Burnt branches come down through a crown, striking the branches under them on the way. A building's
  burnt pieces come down inside it.
- No source gives how often. The model has one fall a body every 40 s at full burning [estimate].
- Masses are on a power law, 0.2-20 kg (twice that in a building), falling from 30-100 % of the fuel's
  height. Each strike is the game's impact law (`ImpactAcoustics.Between`, wood on the ground's material or
  on wood), at its speed and mass. The landing throws sparks and char.
- Strikes are queued in real time, so a branch from 14 m lands 1.7 s after it lets go.

### 6.2 Glass

- A pane cracks when its heated middle is 60-90 K over its shaded edge (Skelly, Roby and Beyler 1991 [ft];
  20-80 K in theory, Pagni 2003 [ft]). That is before flashover.
- 3 mm float glass cracks after about 200 s at 5.5 kW/m², 140 s at 7, 85 s at 9 and 60 s at 12.6, and not
  under about 5 kW/m² [ft] (Pagni 2003).
- The crack runs at about 1.5 km/s and branches [ft] (Pagni 2003).
- A piece falls only once two to four cracks have cut it free. Between none and 85 % of a pane falls (Pagni
  [ft]). Room-fire windows fail (a quarter gone) at 3.5-6 min for modern double glazing and 6.5-16 min for
  old single glazing (Kerber 2012 [ft]).
- The model:
  - Each pane belongs to a body of fire. It cracks 30-270 s after that body is fully going: a sharp
    impulse and the pane's own clamped-plate modes (`PanelAcoustics.Modes`), about 90 dB peak at a metre
    [estimate].
  - In a building it falls out 1-6 minutes later. Its landing is the glass model's own render
    (`GlassFracture`, a 1 × 1.2 m annealed pane from its sill height onto the ground), made once per kind
    of pane off the audio threads.
  - Air in through the hole makes that room flare.
  - A car's tempered side windows dice (the glass model's tempered break).

### 6.3 Collapse

- Floors under a fire, loaded (UL furnace tests, quoted by Kerber 2012 [ft]):
  - Unprotected engineered I-joists failed at 6 min and solid 2×10 joists at 18.5 min.
  - With gypsum under them: 26-29 min (I-joists, trusses), 44.7 min (2×10), 79 min (lath and plaster).
- No timing for a free-burning house's roof was found.
- The model: from 10-25 min after the house is fully alight, four collapses at 2-9 min intervals, each
  bigger than the last (ceilings first, the roof last). Each is a cascade of heavy strikes over one to two
  and a half seconds, a burst of sparks, and a flare.

### 6.4 A car

Test timelines [ft] (Miechówka and Węgrzyński 2025):
- Lit at the rear splash guard: flames at 10 min; a gasoline leak and a big flame at 13:20; the cabin
  breached at 15 min; the rear window at 32 min, and every window within the next 2-4 min.
- Another: a rear tyre burst at 12 min; the rear windows at about 40 min, seconds apart; the windscreen
  cracked at 44:50 and was gone within two minutes.
- Lit in a seat: the windscreen cracked at 1 min and broke at 4:30; at 48:30 a fuel leak burst both rear
  tyres.
- Lit from under the car with a 2 MW burner: the fuel tank ruptured at 5-6 min, the heat release's peak.
- A battery car: its airbags went off at 46 min.
- Every window that fails brings a jump in heat release.

Bursting vessels:
- Gas struts and bumper absorbers are known to fail violently [recalled]. No timing was found.
- A bursting vessel's blast: its isentropic expansion energy as TNT (4.6 MJ/kg) and Kinney and Graham's
  peak overpressure and positive phase at a metre [recalled].
  - A 2.5-bar tyre is about 2 g of TNT [estimate, research brief].
  - The model's hot tyre (3.3 bar absolute, 25 L) comes to about 175 dB peak at a metre. A gas strut
    (150 bar, 60 cm³) comes to about 170 dB.

The model:
- Four struts vent in the first quarter hour, each a blast and a hiss.
- Each tyre bursts with a chance of one in three, at 5-25 min.
- The fuel tank gives way at 7-17 min in a flare.
- The side windows crack and dice as their part of the car reaches full burning.
- Inside one voice the mixer cannot place a blast by its own level. Its excess over the fire's own level is
  compressed by the loudness law's compression (0.45 shipped), as the mixer would have placed it as a
  sound of its own.

### 6.5 Not modelled

- Smoke explosions: a wood-lined room relit and went out in cycles, flames wandering with audible pulsing
  for about 7 s, then an eruption out of the vent, 29 times in 13 tests (Fleischmann et al. 2024 [ft]).
- Spotting: embers landing ahead of a crown fire.
- Metal roofs ticking as they heat.
- A fire whirl's roar.

## 7. The model

### 7.1 Bodies of fire

- The burning area (width × depth) is cut into bodies of the preset's `BaseDiameterMetres`, at most 48
  (bigger ones if more would be needed), jittered off a grid.
- Each body has:
  - its share of the heat release (±30 %);
  - its own puffing (rate ±8 %, each puff's length and strength random);
  - its own slow vigour (tens of seconds);
  - its own crackle clusters;
  - its own torching;
  - its own catching time as the fire grows.
- Its heat release now is the preset's share times its life, its vigour, its torch, a flare, and (for a
  crown fire) the wind.

### 7.2 Places

- The fire is heard from nine places (`ExtendedSources`; a source may have up to twelve).
- Along a front (width over 2.5 times depth): the middle and pairs out to either side, evenly.
- Round anything else: the middle and a ring placed so that the places, each an equal share, spread as
  the area does.
  - A uniform w × d area has a variance of w²/12 across and d²/12 along.
  - The middle and m places on a ring of half-axes k w and k d have m k² w² / (2 (m + 1)) across.
  - So k = √((m + 1) / 6m): the ring at 0.87 of the half-widths for nine places, 0.94 for four.
- A single body of fire (a hearth, a pile) burns all over its bed and is heard from every place alike.
  A fire of many bodies hears each body from the places near it, weighted by a Gaussian of the places'
  spacing.
- Each place has its own roar noise, its own fizz and its own crackle noise. Their powers are the sums of
  its bodies' shares. Each crackle, fall, crack and burst goes to one place.
- Merged (the source too narrow at the listener), everything is heard from the middle. In between, a share
  of each body's power moves to the middle by the spread.

#### How wide it sounds (round 2, 2026-10-07)

Round 1's renders measured close to mono: the ears' correlation (IACC, best |r| within ±1 ms) was 1.00 at
125-500 Hz everywhere and 0.7-0.9 at 1-4 kHz in most. What was found:

- The places are independent at every frequency. The largest correlation between two places of the dry
  render is 0.003-0.03 in every band.
- Through the game, standing among a crown fire's front with its places to either side, the ears measure
  0.74 / 0.13 / 0.07 (125-500 Hz / 1-4 kHz / 4-12 kHz). Collapsing the house's places onto its middle
  gives 1.00 / 0.85 / 0.88, the same as one voice. Spread, the house measures 1.00 / 0.74 / 0.46.
- 125-500 Hz near 1 is right for a fire in front of the listener. On the game's own head (Steam Audio's
  HRTF, independent noises, nothing else; AudioLab `--fire hrtf`):
  - one source ahead 0.98
  - plus and minus 30 degrees 0.69
  - a ring round the head 0.44
  A rigid-sphere head gives the same.
- What was wrong: the places spread less than the fire. The ring sat at three-quarters of the
  half-widths, about 70 % of the fire's angle.
  - On a rigid-sphere head the places were 0.1-0.2 more alike at 1-4 kHz than a continuous area of the
    same size: campfire at 2 m 0.80 against 0.57, house at 30 m 0.72 against 0.60.
  - Nine places on the matched ring come within 0.03 of the area at 1-4 kHz (0.60, 0.63).
  - At 4-12 kHz a few discrete places always leave about their largest share's worth of correlation
    (0.1-0.25). The head itself leaves 0.35 for a house's places at 30 m.
- Round 1's scenes were narrow fires straight ahead, the car end-on, and the walk's first 15 s at a single
  point 140 m off. The ears are nearly alike for those by nature.
- Round 2's renders against each render's geometry on the game's head agree within 0.05 in most bands
  (inbox/fire-2026-10-06/round2/README.txt). Where they are more alike, it is at 4-12 kHz on fires whose
  top is a few loud events (a tree torching, a car's struts and windows): each is one point, as it is in a
  real fire.

### 7.3 Parts

- Roar: per place, noise shaped by `PowerLawNoise` (f^−α above 20 Hz), its power the sum of its bodies'
  roar at 100 Hz times each body's puff.
- Flicker: between the puffs each body's burning flickers. Noise above the puffing rate, flat to three
  times it and falling as f^−2 to ten times it (about the f^−1.4 measured over that decade). Its rms is
  20 % of the burning (fitted). The roar and the fizz follow it.
- Fizz: gas and steam through the char, 5 kHz, its power the heat release (as before at the fire pit).
  A car's is ten times wood's in power: molten plastics boiling and their gas jetting (fitted).
- Crackle: drawn singly and as noise (section 4.1). Big pops throw embers that tick where they land.
- Logs: steam jets, which sometimes whistle; a log settling; more of both in a bigger fire.
- Trees and crown: torching, falls through the crown.
- Structure: falls, panes cracking and falling out, collapses.
- Vehicle: struts, tyres, side windows dicing, the fuel tank flaring.

### 7.4 Cost

- All of it runs on the render workers off the mixer thread. Nothing allocates or throws per sample.
- The glass is rendered once per kind of pane on a background task; until it is ready a falling pane is
  silent.
- Measured cost: section 8.

## 8. Fitting and results

### 8.1 Recordings

- 31 yardstick recordings, never shipped: `~/openfps-scratch-archive/fire-2026-10-06/refs`, with
  `SOURCES.txt`. All are CC0, CC-BY or public domain.
  - 8 campfires (the three of round 1 among them)
  - 7 bonfires and burn piles
  - 7 trees and brush burning (two from the National Park Service's Maple Fire, Yellowstone)
  - 4 "wildfire": three prescribed burns of grass and litter, heard close; one forest fire heard across
    a drainage (Maple Fire, 12 pm)
  - 4 house fires
  - 2 cars
- Most are Freesound HQ previews (MP3 VBR, low-passed by the encoder at 16-19 kHz). The rest are NPS
  MP3s and Commons files, also lossy.
- Their texture statistics (20 s pieces), 10 ms kurtosis and octave ranges are in `FireReferences`.

### 8.2 How it was fitted

- As the fountain, the rain, the creeks and the shores were: the 14 cochlear band-envelope statistics
  (McDermott and Simoncelli 2011; `tools/texture_stats.py`, `TextureStatistics`), the 10 ms 4-16 kHz
  waveform kurtosis, and the octave balance.
- Each preset is heard as its kind's recordings were made (`--fire levels heard=D`): each place by its
  own distance, 1/r and ISO 9613-1 air. Campfire and pit at 2 m, bonfire 6 m, car 10 m, house 30 m,
  trees 20 m, crown fire 60 and 150 m.

| constant | value | what it says |
|---|---|---|
| `PuffSwing` | 0.35 | the share of a body's heat release that swings with its puffs (research: 20-40 %) |
| `RoarTailExponent` | 2.5 | α, the roar's PSD ∝ f^−α (research: 2.1-3.4; 5/2 from Kolmogorov) |
| `Flicker` | 0.2 | the burning's flicker between puffs, rms over its mean |
| `CrackleHeatExponent` | 0.5 | β: a body's crackle count goes as (Q / 80 kW)^β, each louder to keep the energy |
| `TreeCrackle` | 0.12 | burning foliage's crackles per kW over seasoned logs' |
| `CrownCrackle` | 0.03 | a crown fire's front's |
| `StructureCrackle` | 2 | a burning building's |
| `VehicleCrackle`, `VehicleFizz` | 0.3, 10 | a car's crackles, and its fizz in power |

- The fire pit's fitted crackle and fizz constants (round 1) are unchanged.
- The roar's swing and tail were fitted on the pit against round 1's three campfires (13 of 14 inside),
  then checked against the eight.
- Scans: α 1.8-2.5 against swing 0.09-0.35; β 1, 0.75, 0.5, 0.35; each fuel's factor at two or three
  values.
- Tried and left out: crackle avalanches (each crackle setting off others within 60 ms, branching 0.3-0.85)
  did not raise the 4-16 Hz modulation and cost kurtosis.

### 8.3 Results

Dry, heard as recorded, statistics inside the recordings' spread (of 14) and octaves inside (of 8):

| preset | statistics | octaves | 10 ms kurtosis | recordings' |
|---|---|---|---|---|
| campfire (2 m) | 12-14 | 7-8 | 5.5-5.9 | 3.37-7.08 |
| fire pit (2 m) | 13 | 8 | 7.0 | 3.37-7.08 |
| bonfire (6 m) | 14 | 8 | 10.9 | 3.10-10.23 |
| burning car (10 m) | 7 | 4 | 4.1 | 3.69-4.73 (2 recordings) |
| house (30 m) | 9 | 7 | 12.5 | 4.25-9.10 |
| stand of trees (20 m) | 14 | 8 | 9.5 | 3.75-31.08 |
| crown fire (60 m / 150 m) | 8 / 5 | 4 / 3 | 6.3 / 6.1 | 3.15-4.53 |

Through the game (client, mixer, HRTF, ear model, loudness law; `--fire game`), every file without
clipping, flat tops or gaps:
- campfire 2 m 14/14, fire pit 2 m 13/14, bonfire 5 m 14/14
- car 10 m 5/14, house 30 m 9/14, house 150 m 11/14
- trees 50 m 14/14, walking up to them 14/14
- crown fire 300 m 6/14, 1 km 8/14
- Full table: `inbox/fire-2026-10-06/README.txt`.

What does not fit:
- The crown fire: the recordings are mostly not crown fires.
  - Against the one forest fire (Maple Fire, across a drainage), its 63-250 Hz stand 5-10 dB higher over
    1 kHz.
  - Its 3-12 kHz crackle is steadier than the prescribed burns' (band kurtosis 4-5 against 6-15).
  - Zhang et al. (2019) put a crown fire's sound mostly under 400 Hz, as the model has it. No calibrated
    recording of a crown fire at a distance was found.
- The car: two recordings only. Its high bands move more than theirs (cv 0.17-0.19 against 0.12-0.17),
  it is too alike across bands (corr near 0.56 against 0.47), and 2-3 dB dark at 2-4 kHz.
- The house: corr octave just over and slow modulation just under the recordings'.
- The 4-16 Hz envelope modulation of most presets is a little under the recordings' (the fire pit's was
  before too).

### 8.4 Levels

At a metre, fully developed, every place summed (`SourceLevelDb`, measured; see each preset):

| preset | dB | dB(A) | headroom |
|---|---|---|---|
| campfire | 63 | 56 | 45 |
| fire pit | 67 | 60 | 45 |
| bonfire | 91 | 79 | 30 |
| burning car | 93 | 78 | 36 (its bursts) |
| house | 98 | 87 | 24 |
| stand of trees | 93 | 81 | 27 |
| crown fire's front | 123 | 107 | 17 |

- The fire pit was 59.5 dB (57.2 dB(A)). Its roar is now the f^−2.5 tail, whose bottom octaves carry
  the difference; A-weighted it is 3 dB more.
- In the game: the crown fire at 300 m plays about as loud as the bonfire at 5 m; the house at 30 m
  about 6 dB under that.
- No measured level of a big fire's audible sound was found to check against. The 3 m log pile's
  infrasound (under 0.1 Pa at 50 m) agrees with the 35 % swing [derived].

### 8.5 Cost

- One fire, every place, one core: campfire 1.3 %, fire pit 0.6 %, bonfire 1.4 %, car 1 %,
  house 2-2.7 %, stand of trees 2-2.4 %, crown fire 1.7-2 %.
- In the game: one HRTF voice when far away, 4-7 when it is heard as wide. Mixer DSP load in the renders
  was 2.5-11 %.
- Nothing allocates while a fire burns (`FireTests`).

## 9. On the maps and in the game

- The fire pit at 58 Alder Street stays as it was. Nothing else is placed on a map.
- `/spawn fire PRESET` (the spawn permission: staff anywhere, a map's owner on their own map) lights a fire
  a few metres ahead of you; a crown fire's front is placed 100 m ahead. It grows from the moment it is lit.
- `/spawn fire out` puts out the nearest one lit that way within 400 m. `/savemap` does not keep them.
- Since 2026-10-10 a fire lit with `/spawn fire` is a thing burning on the map, and what is near it can
  catch from it; `/spawn fire lightning` and `/spawn fire water` strike and douse (section 12).

## 10. Next

- Smoke explosions and backdraft cycles in a closed building.
- Fire spreading on a map from one thing to the next, and spotting: stage 1 built, section 12; its stage 2
  (a house as a zone, a crown fire through a forest, surface fire over the ground) is listed in 12.10.
- The fire's sound inside a burning building; a door opened onto a fire.

## 11. Sources

- Bartlett, Hadden, Bisby 2019. A review of factors affecting the burning behaviour of wood for application to tall timber construction. Fire Technol. 55, 1-49. doi:10.1007/s10694-018-0787-y
- Bedard, Nishiyama 2002. Infrasonic signatures of a fire. IGARSS 2002. doi:10.1109/IGARSS.2002.1025715
- Chadwick, James 2011. Animating fire with sound. ACM Trans. Graph. 30(4), 84.
- Clark et al. 1999. J. Appl. Meteorol. 38, 1401-1420.
- Coen et al. 2004. Infrared high-speed imaging of crown fire dynamics. J. Appl. Meteorol. 43, 1241-1259.
- Cruz, Alexander, Wakimoto 2005. Development and testing of models for predicting crown fire rate of spread in conifer forest stands. Can. J. For. Res. 35, 1626-1639.
- Dowling, Mahmoudi 2015. Combustion noise. Proc. Combust. Inst. 35, 65-100. doi:10.1016/j.proci.2014.08.016
- Fleischmann, Madrzykowski, Dow 2024. Fire Technol. 60, 1867. doi:10.1007/s10694-024-01553-5
- Forest Products Laboratory 2025. J. Acoust. Soc. Am. 158(4) Suppl., A351-A352. doi:10.1121/10.0041122, 10.1121/10.0041123
- Guo et al. 2023. Fire Saf. J. 136, 103755. doi:10.1016/j.firesaf.2023.103755
- Haghiri, Talei, Brear, Hawkes 2018. J. Fluid Mech. 843, 29-52. doi:10.1017/jfm.2018.115
- Hurle, Price, Sugden, Thomas 1968. Sound emission from open turbulent premixed flames. Proc. R. Soc. A 303, 409-427.
- Johnson, Anderson, Yedinak 2025. Appl. Acoust. 231, 110559. doi:10.1016/j.apacoust.2025.110559
- Kerber 2012. Analysis of changing residential fire dynamics and its implications on firefighter operational timeframes. Fire Technol. 48, 865-891. doi:10.1007/s10694-011-0249-2
- Khamukhin et al. 2017. J. Phys. Conf. Ser. 803, 012067.
- Madrzykowski 2008. NISTIR 7506.
- Mäkinen et al. 2015. Avalanches in wood compression. Phys. Rev. Lett. 115, 055501.
- Marcillo et al. 2025. Appl. Acoust. 235, 110657. doi:10.1016/j.apacoust.2025.110657
- Martinsson et al. 2022. Fire Technol. doi:10.1007/s10694-022-01307-1
- Maynard 2013. PhD thesis, University of California, Riverside.
- Miechówka, Węgrzyński 2025. Fire Technol. 61, 2651. doi:10.1007/s10694-025-01701-5
- NIST TN 2327, 2025. doi:10.6028/NIST.TN.2327
- Pagni 2003. Thermal glass breakage. Fire Safety Science 7. doi:10.3801/IAFSS.FSS.7-3
- Rajaram, Lieuwen 2009. J. Fluid Mech. 637, 357-385. doi:10.1017/S0022112009990681
- Rehm et al. 2002. NISTIR 6891.
- Shivashankara 1973. PhD thesis, Georgia Institute of Technology. hdl:1853/12333
- Skelly, Roby, Beyler 1991. J. Fire Prot. Eng. 3, 25-34.
- Viegas et al. 2008. WIT Trans. Ecol. Environ. 119. doi:10.2495/FIVA080181
- Werth et al. 2011. Synthesis of knowledge of extreme fire behavior, vol. I. PNW-GTR-854. doi:10.2737/PNW-GTR-854
- Yedinak et al. 2017. J. Acoust. Soc. Am. 141, 557-562. doi:10.1121/1.4974199
- Zhang et al. 2019. Sensors 19, 5093. doi:10.3390/s19235093

## 12. Fire that burns what is there (2026-10-10)

Stage 1 approved by Cody's ear 2026-10-10 ("new fire sounds good as well", inbox/fire-fuel-2026-10-10).

Cody, 2026-10-10: "should the fire be driven not by a predefined grid but rather by what is a fuel source,
not whether it is necessarily next to something else that can catch fire? For example, a series of stumps
next to each other, they'll catch, but if lightning strikes a tree, the next one should catch not because it
is merely close, but because it is itself flammable and could either have a spark land on it or wind catch
it, etc. The fires sound good but they need to be dynamic, change type based on what it is burning and have
the sound change accordingly." He agreed that a fire fills a shape it is given (a circle, a rectangle, an
outline, or a zone), with the crackle at the fuel bed and the roar at the right height in the flame, and that
zones carry properties, fuel among them. Later the same day he added water and the weather (12.6, 12.7).

Code:
- `OpenFPS.Common/FireShape.cs`: the shape a fire burns over.
- `OpenFPS.Common/Fuel.cs`: `FuelPart`, `FuelSpec`, `FuelCatalog`: fuel as a property of things.
- `OpenFPS.Common/FireSpread.cs`: things catching, burning, going out.
- `OpenFPS.Server/Systems/FireSystem.cs`: the spread on each map, and its emitters.
- `FireSynth`: the places at the bed and in the flames, water on the fire, cooling.
- AudioLab `--fire spread timeline|game`.

### 12.1 The idea

- Nothing is a grid of fire. Every thing that can burn carries its own fuel: what it is made of, how much,
  how wet, what it takes to catch, what it gives off once it has. It is derived from the thing's material,
  kind and size, never listed per map.
- A thing catches when the heat it has received is enough for its own fuel, or when a glowing brand lands
  on it and it is dry and fine enough to take it. Being near a fire is not itself a reason: a gap with no
  fuel in it stops a fire unless the wind carries brands across.
- Each burning thing burns its own fuel with its own heat release curve, as one or more bodies of the
  approved fire model with its fuel's character. As what is burning changes, the sound changes with it,
  because the sound is made from what is burning.
- The server decides what burns (deterministic, seeded); every client renders the sound.

### 12.2 A fire over a shape (built)

- A fire's ground is a shape (`FireShape`): a rectangle, a circle or an outline. Its bodies of fire are laid
  on a jittered grid over its bounds and only those inside it are kept, so a round pit has no corners and a
  bent hedge has no inside.
- Its places spread as the shape does, by the shape's own second moments: a rectangle w × d has variances
  w²/12 and d²/12, a circle of radius R has R²/4 each way, an outline its own (Green's theorem). An outline
  whose spread is not along its axes is laid along its principal axes and turned back. For a rectangle this
  is exactly the approved layout.
- Given a shape, a preset burns as hard per square metre as it was declared: heat release, power (10 log of
  the area ratio, docs/FIRE.md 1.5) and size follow the area. A one-body preset (a hearth, a pile) takes the
  bed's narrowest width as its body, up to 4 m (the widest bed measured puffing as one, Johnson et al. 2025),
  and its flames grow as Q^(2/5), Heskestad's leading term.
- A placed fire takes its size from its collider (`FireSpec.KeyForPlaced`): a box is its rectangle, a
  cylinder its circle. The fire pit prefab's collider is now its 0.9 m bed, so the pit at 58 Alder Street is
  exactly the approved fire; scaled to 1.8 m it is four times the fire (+6 dB); made round, a round bed.
- The key carries an explicit shape when it has one: `fire:<preset>/lit=<s>/shape=c0.9` (circle),
  `r3.5x2` (rectangle) or `p` and its corners `x,z;x,z;...`. A key without one is the preset's own shape.
- Crackle at the bed and roar in the flames. The places are now the bed's (nine, as approved, at the
  burning fuel's middle: crackle, fizz, steam and every event) and three in the flames, spread the same way,
  `FireSpec.RoarRiseMetres` above: the middle of the flames over the middle of the fuel, half of flame height
  less fuel height. The combustion noise is the turbulent flame's (section 1); the crackle is the fuel's
  (section 4). For the pit the roar sits 0.25 m above the crackle, for a torching crown 3.5 m, for a crown
  fire's front 12.5 m. Twelve places is the most a source has (`ExtendedSources.MaxPlaces`).
- The flame height used is the fully developed one. A growing fire's roar is placed as high as it will be;
  following the heat release (L ∝ Q^(2/5)) needs the client to move places at run time: stage 2.

### 12.3 Fuel as a property of things (built for trees, stumps, piles of logs and cars)

A thing is one or more `FuelPart`s. Each has:
- what it burns as (a `FireSpec` preset: heat release, life, sound) over its own footprint, and its bottom
  and top above the thing's ground;
- how it heats (`FuelHeating`): thick (logs, a stump, a car's panels and tyres) or thin (needles, leaves,
  grass);
- its critical flux, under which it never catches: wood piloted 12-13 kW/m² (Drysdale; Babrauskas 2003),
  needles and litter about 8-10, polymers 10-20 (a car: 15);
- its dose: thick, its flux-time product (π/4) kρc (T_ig − T₀)², about 17 000 (kW/m²)²s for wood
  (kρc ≈ 0.2, T_ig ≈ 350 °C), 8 000 for a car's polymers [estimate]; thin, its dry mass per area heated
  (needles 0.15-0.3 kg/m²);
- its moisture: living (foliage about 100 %, a living trunk 50 %) held by the plant; dead, following the air
  and the rain with its timelag (1 h needles and grass, 10 h twigs, 100 h branches and stacked logs, 1000 h
  stumps and logs: Fosberg 1970, the NFDRS classes), and its moisture of extinction (fine dead fuel about
  0.3: Rothermel 1972);
- its receptivity to brands and the brands it sends up per MJ (estimates by kind, 12.4);
- for a crown over surface fuel, its crown base height (Van Wagner 1977); for a bed of surface fuel, its
  load and surface-to-volume ratio (Rothermel 1972);
- whether water makes it flare instead of going out (oil and fat).

What things are (`FuelCatalog.ForThing`, by sound, material and size, never by name):
- a Foliage box standing at least a metre off the ground: a tree. Three parts: the needles shed under it
  (litter, dead, 1 h, 0.5 kg/m²), its crown (living needles, thin, from the box's bottom to its top) and its
  branches with the trunk among them (living wood, thick, in the crown);
- a Wood box on the ground, by its proportions: tall and slender a trunk, small a stump, bulky a pile of
  logs. Thin wood (a floor, a fence board) is part of a structure: stage 2;
- a vehicle (an `engine:` emitter): a car;
- a map's fire (a `fire:` emitter): a fire that is always burning, which can set things near it going and
  never goes out of itself;
- land cover (`FuelCatalog.ForLandCover`): the hook the world's tiles will give grass, meadow and woodland
  through. Not used yet.

A thing catches part by part: lightning or a brand starts the litter under a tree; the crown catches from it
only if the surface fire is intense enough (12.4); the branches catch from the crown's flames. Its sound
follows: the litter's light quick crackle, then the crown torching (the roar 3.5 m up), then the branches
and trunk burning on for most of an hour as wood does.

### 12.4 How things catch (built)

Every second, for every part burning, the heat it sends to every part near it (`FireSpread.Flux`):

- Radiation: a point source, q = χ_r Q / 4πR², χ_r = 0.35 (0.3-0.4 for wood and vegetation: Tewarson, SFPE
  Handbook; Drysdale), R from the middle of the flames to the nearest point of the other part, at most
  50 kW/m² (half a flame's emissive power: σT⁴ ≈ 118 kW/m² at 1200 K, Butler and Cohen 1998).
- Flame contact: the flame is its base's own shape carried up an axis as long as the flame (L ∝ Q^(2/5)
  from the preset's), leaning with the wind: cos θ = 1 for u* ≤ 1, u*^(−1/2) above, u* the wind at mid-flame over the plume's
  own buoyant velocity (g Q / ρ c_p T D)^(1/3) (the AGA correlation for wind-blown fires, SFPE Handbook,
  Beyler; its velocity scale taken from Q* [estimate]). A part inside the flame, out to a quarter of a flame
  radius beyond it, gets 100 kW/m² [estimate: the emissive power and the convection together], scaled by how
  much of its height the flame covers: knee-high flames at the foot of a twelve-metre trunk scorch it and do
  not set it burning. Slope tilts the flame the same way (Rothermel's slope factor is the same physics); the
  maps are flat so far: stage 2 with terrain.
- What a part receives from several flames is never more than being inside one (100 kW/m²).
- It keeps that heat by its own law: thick, ∫(q − q_cr)² dt against its flux-time product times the heat of
  preignition wet over dry, (250 + 1116 M) / 250 (Rothermel 1972); thin, ∫(q − q_cr) dt against warming its
  mass about 280 K (1.4 kJ/kgK) and boiling its water (2.59 MJ/kg). With nothing heating it, what it had
  gathered falls away over 90 s [estimate]. When it has had enough, it catches.
- Brands: a part burning sends up BrandsPerMJ × its MJ of brands (foliage 3, a building 2, logs 0.5-1, a car
  0.05 a MJ: estimates by kind; NIST's burning trees and structures send up hundreds to thousands). The plume
  lofts each until the plume's speed, 1.1 Q^(1/3) z^(−1/3) m/s (McCaffrey 1979), falls to its falling speed
  (2.5-7 m/s, Tohidi et al. 2015); most go only part of the way. The wind at their height carries them while
  they rise and fall, scattered sideways by a quarter of the drift [estimate]. A brand glows for about
  10 (v/2.5)² s [estimate]. One landing on a part with receptivity sets it going with a chance of its glow left
  times its receptivity times its dryness, (1 − M / M_x)² (Schroeder 1969: a brand's chance on fine dead fuel
  falls to nothing near 30 % moisture). Receptivity: dry litter 0.4, a pile of logs with its bark and
  splinters 0.25, a stump 0.08, a living crown 0.01, a car 0.005 [estimates]. A drawn brand stands for as many
  real ones as there are, so a torching crown's thousands cost eight a second.
- A crown over a surface fire (Van Wagner 1977): it catches when the surface fire's intensity reaches
  I₀ = (0.010 CBH (460 + 25.9 FMC))^1.5 kW/m (CBH m, FMC %): 476 kW/m for a crown 2 m up at 100 % foliar
  moisture. The surface fire's intensity is Byram's I = H w r, its rate of spread Rothermel's (1972): the calm,
  dry rate (needle litter 0.5 m/min, an estimate from the field's 0.3-1) times (1 + φ_w), φ_w = C U^B with C
  and B from the fuel's surface-to-volume ratio, times the moisture damping 1 − 2.59 r + 5.11 r² − 3.52 r³.
  A tree whose litter burns in still air (75 kW/m) keeps its crown; in a 6 m/s wind (about 500-700 kW/m) it
  torches.
- Lightning: the flash attaches to whatever its leader reaches first: the rolling sphere, radius
  10 I^0.65 m for a peak current of I kA (IEC 62305; Golde), 91 m at 30 kA, so the tallest thing within
  reach. Only a flash with a long continuing current sets fuel going (about a third of ground flashes:
  Latham and Williams 2001), at the foot of what it struck, by that fuel's receptivity and dryness.
  `/spawn fire lightning` strikes and always lights.

### 12.5 Fire over the ground (stage 2)

Grass and litter that cover the ground are not things, so they cannot be parts. The plan:
- Where fuel exists, sample the ground at about a metre where a surface fire's front is, not everywhere: the
  front is a line of points (marker points along the perimeter, as FARSITE and Prometheus do with Huygens'
  principle: Finney 1998), each moving outward at Rothermel's rate for its fuel, wind and slope, the line
  re-sampled as it grows. The fuel under each point is asked of the land cover (`FuelCatalog.ForLandCover`)
  and of zones; where there is none (a road, bare earth, water), that point stops.
- A burnt-out area is remembered as polygons, so fire does not run back over it.
- The front is heard as a long fire: its parts aggregated into stretches (12.8), each a `litter` or grass
  fire over its outline, the crackle at the ground and the roar a metre up.
- Zones (a field, a lawn, a yard of litter) carry a fuel the same way as land cover, with their outline: the
  world editor's zones gain a fuel property.

### 12.6 Weather and fire

Weather on fire (built):
- Wind: the flames lean with it (12.4), brands go where it goes, surface fires run with it (Rothermel's
  φ_w) and a crown torches over a surface fire it has driven hard enough. Read from the map's weather on the
  server; the same field the clients hear the gusts from.
- Humidity and temperature: dead fuel moves toward Simard's (1968) equilibrium moisture content at its
  timelag, so a hot dry afternoon dries the litter in an hour and the logs over weeks.
- Rain: wets dead fuel toward fibre saturation (0.35) about four times as fast as dry air dries it, faster in
  heavier rain [estimate]; living fuel keeps its own water. Wet litter will not take a brand, and wet wood
  needs more heat to catch (the heat of preignition). Rain on a fire: 12.7. The rain is the server's own
  (`WorldEnvironmentSystem.RainRate`), the one the roads get wet from.
- Lightning: 12.4. Every ground flash on a map (LightningSystem) is offered to its fire.

Fire on weather (stage 2):
- A big fire makes its own wind: air drawn in toward its base (2-40 m/s measured round large fires: Trelles
  and Pagni 1997), the plume rising, gusts. It is audible as wind round the listener and in the trees near
  the fire, and it leans nearby flames inward. The plan: a local wind field per burning cluster, its indraft
  at the base from the plume's entrainment (Morton, Taylor and Turner 1956: entrainment speed about α times
  the plume's speed, α ≈ 0.1) falling with distance, added to `WindField` for things near the fire (the
  trees' voices, the listener's ear wind, the brands, the flames' lean).
- Regional effects (a pyrocumulus cloud, its rain and lightning, a fire's smoke shading the ground) are out
  of scope for now.

### 12.7 Water on a fire (rain built; a hook for the rest)

- Water takes heat from the fuel: 4.18 kJ/kgK to 100 °C and 2.26 MJ/kg to boil, 2.59 MJ/kg in all. That
  cools the fuel's surface below where it gives off burnable gas, so the flames lose their fuel; the steam
  dilutes the oxygen near it; wet fuel nearby needs far more heat to catch.
- `FireSpread.Douse`: the water reaching the fuel (rain falling through the flames, e^(−L/3 m) of it getting
  through, a tall flame boiling most of it off first [estimate]; water by hand all of it) times 2.59 MJ/kg,
  against the heat the flames feed back to their own fuel (about 30 kW/m² for wood, Drysdale ch. 5), is the
  share of its burning that goes (`Quench`), over about ten seconds. Held over 85 % for half a minute it is
  out, and its fuel is wet.
- Light rain (2 mm/h) takes about 4 % of a campfire's burning and nothing of a torching crown. A heavy shower
  (20 mm/h) takes about 40 % of a campfire's. A cloudburst (60 mm/h) puts a campfire out in under a
  minute. Rain that has fallen for a while wets the fuel round a fire and stops it spreading.
- Oil and fat (`FuelPart.WaterFlares`): water sinks under the burning liquid, flashes to steam and throws it:
  a flare and brands, not an end. Nothing on the maps burns as oil yet.
- The interface for water a player or the world brings (a hose, a bucket, water running over the ground),
  needing nothing of whatever brings it: `FireSpread.AddWaterAt(point, kg a second, seconds)` gives water
  landing at a point to the thing whose plan it is in; `FireSpread.AddWater(thing, ...)` to a thing;
  `FireSpread.WaterOn(thing)` says how much water is reaching its fuel now (kg/m²s, rain through its flames
  and water by hand) and what share of its burning it takes. On the server, `FireSystem.Water(map, point,
  reach, kg/s, s)`. `/spawn fire water` puts a hose's 2 kg/s on the nearest thing for a minute.
- Heard (`FireSynth.Quench`, sent as `SoundEmitterComponent.Quench`): the flames lose the share the water
  takes, so the roar falls with the heat release squared and the crackle thins with it; the heat the water
  takes comes back as steam through the char (the fizz's band, four times the fizz's power for the same heat
  [estimate]) and as small pops of drops bursting (25 a second per 100 kW taken [estimate]). Out
  (`SynthRunning` false), the flames die over twenty seconds and the char ticks as it cools, a few a second
  at first, falling away over a few minutes [estimate]. The emitter stays three minutes after it is out.

### 12.8 The sound follows what is burning

- Each burning part is one emitter, its key its preset, its lighting moment and its shape. Its heat release
  and its life are its preset's (`FireSpec.LifeShare`, the one curve both the server's spread and the
  client's sound read), and so its character is: needles torching (`tree_crown`), wood burning on
  (`tree_trunk`, `stump`, `wood_pile`), litter running (`litter`), a car's plastics, struts, tyres and tank
  (`burning_car`).
- New presets, measured (`--fire levels`, wind 3 m/s, 60 s, every place summed, fully developed):

| preset | what | heat release | life | dB at 1 m | dB(A) |
|---|---|---|---|---|---|
| stump | a 0.6 m stump burning on top and in its cracks | 40 kW | 5 min, 1 h, 1 h | 66 | 57 |
| wood_pile | a stack of split logs 2 × 1 × 1 m | 1.5 MW | 7 min, 50 min, 40 min | 89 | 75 |
| tree_crown | a conifer's crown torching | 10 MW | 6 s, 8 s, 25 s, nothing left | 94 | 79 |
| tree_trunk | its branches and trunk burning on | 300 kW | 1.5 min, 25 min, 40 min | 78 | 70 |
| litter | needles on the ground under a tree | 400 kW | 40 s, 1 min, 1.5 min | 70 | 64 |

- Grass and litter (`FireFuel.Litter`): light crackle (0.35 of logs' per kW: straw and litter carry almost
  none, shrubs a strong one, Viegas et al. 2008), no steam jets, nothing settling or falling. Not yet fitted
  to the prescribed burns among the recordings: stage 2. Against the campfire recordings its statistics sit
  13 of 14 inside, which says little.
- Scale: the client ranks and budgets fire voices as it does every physical voice, so a forest burning is
  heard by its loudest parts. That is enough for a row of trees, not for a forest: stage 2 aggregates by
  audibility as distant traffic is. The server groups the parts burning within a cell (about 25 m, the
  places' spacing at a few hundred metres) into one emitter whose key lists its parts' presets, shares and
  lighting moments; the client builds one FireSynth whose bodies are those parts, each with its own fuel and
  life, heard from places spread over the cell. Near the listener the cell splits back into its parts. A
  forest of thousands of trees is then tens of emitters.

### 12.9 What changed in the approved presets

- Their heat release, life, bodies, crackle, fizz and events are unchanged.
- Their roar moved from the nine bed places to three places in the flames above them, spread as the area.
  The roar's power is the same; its places are fewer and higher. At 125-500 Hz, where the roar is, the ears
  were near one anyway for a fire in front of the listener (section 7.2).
- The places' random streams are drawn in a different order, so a render is not the same sample for sample.
- Before and after, four minutes of each preset, fully developed, every place summed (`--fire levels
  sec=240 wind=3`): see 12.11.

### 12.10 The server, the wire, how it scales and how it ends

- The server (`FireSystem`) reads a map's fuel once, the first time anything there is lit or struck (the
  city: every tree, stump, pile and parked car), steps its spread once a second in the map's own weather
  (wind, rain, temperature, humidity), and keeps one emitter for every part burning. `/spawn fire PRESET`
  lights a thing burning as that preset, so what is near it can catch. `/spawn fire out` puts out the
  nearest at once.
- The ground under a thing is taken as the map's datum (the maps are flat); stage 2 asks the terrain.
- On the wire: nothing new but one appended field, `SoundEmitterComponent.Quench`. Everything else is in the
  key (preset, lighting moment, shape) and in `SynthRunning`. Every client renders the same fire at the same
  point of its life.
- Cost: the spread visits only parts burning and the parts within their reach (an 8 m grid), and every part's
  moisture once a second. Brands are drawn at most eight a second a part and 400 a second in all, each standing
  for as many real ones as there are. A forest of 3,000 trees crowning steps in about 12 ms on average and
  43 ms at worst (12.11), so the server steps it off the tick thread; its sound needs the aggregation above.
- A fire ends when its fuel is used (a crown has nothing left; logs smoulder on), when water puts it out
  (12.7), or when a person does.

Stage 2, in order:
1. Aggregation by audibility (12.8), so a forest is tens of emitters.
2. Surface fire over the ground (12.5): fronts of marker points, land cover and zones with fuel.
3. Structures: a house as a zone with a fuel load (dwellings about 780 MJ/m² of floor, Eurocode 1 part 1-2,
   annex E) and its rooms, windows and roof as parts; fences and floors as its thin wood.
4. The fire's own wind (12.6), heard round big fires.
5. Flame height following the heat release in the places (12.2), and slope on terrain.
6. Litter and grass fitted to the prescribed burns among the recordings.
7. Water as a substance: hoses, buckets and water running over the ground through `AddWater`; a grease fire
   on a map.

### 12.11 Results

Renders through the game (client, mixer, HRTF, ear model, loudness law; AudioLab `--fire spread game`):
inbox/fire-fuel-2026-10-10, with README.txt (what to listen for), levels.txt and the spread's own timeline.
Nothing clips (loudest peak −1.0 dBFS). Mixer load stayed under 40 % with 42 HRTF voices while the row of
trees crowned.

The timelines (`--fire spread timeline`, deterministic):

- Lightning on the first of eight conifers in a row, crowns a metre apart, in a 6 m/s wind; 25 m of bare
  ground; three more trees; one upwind:
  - 00:00 the strike sets the litter at its foot going;
  - 00:40 its crown torches over its litter fire (885 kW/m against Van Wagner's 476);
  - 00:51-01:34 the crowns downwind catch one after another from the flames leaning onto them, the litter
    under them from brands;
  - 01:14 a brand crosses the gap and sets the litter under tree 9 going; 01:54 its crown torches over it
    (569 kW/m), and trees 10 and 11 follow from its flames by 02:08;
  - 01:19-02:47 the crowns burn out; branches and trunks burn on; the upwind tree never catches.
  - In still air the litter fire (about 75 kW/m) never takes a crown, and nothing crosses the gap.
- Six stumps 0.4 m apart, the first lit: in a 6 m/s wind each catches from the one upwind by its leaning
  flames, at 03:43, 07:40, 11:39, 15:16 and 19:01; in still air none does in an hour.
- A car 1.5 m from a pile of logs, a 3 m/s breeze toward the pile: the pile catches at 22:23 from the car's
  radiant heat (23 kW/m² at its face once the car had passed about 4 MW).
- A campfire in rain: light (2 mm/h) takes 4 % of its burning, heavy (20 mm/h) about 40 %, a cloudburst
  (60 mm/h) puts it out 43 s after it starts.

The approved presets before and after (`--fire levels sec=240 wind=3`, every place summed at a metre, same
seed; the places' random streams are drawn in a different order, so the two are not the same sample for
sample):

| preset | Leq before / after | dB(A) before / after | statistics inside, before / after |
|---|---|---|---|
| campfire | 63.4 / 63.4 | 57.5 / 57.6 | 14 / 14 |
| fire pit | 68.6 / 68.5 | 60.4 / 59.3 | 14 / 13 (mod mid 0.236 against 0.238) |
| bonfire | 91.4 / 91.2 | 79.9 / 79.8 | 14 / 14 |
| burning car | 93.6 / 93.4 | 78.0 / 77.9 | 14 / 14 |
| house | 97.7 / 97.8 | 87.3 / 87.2 | 11 / 11 |
| stand of trees | 93.7 / 92.7 | 82.0 / 81.3 | 14 / 12 (corr octave 0.682 against 0.668, mod mid 0.144 against 0.149) |
| crown fire | 116.7 / 116.8 | 101.8 / 101.9 | 8 / 9 |

The differences are those of one random stream against another, not of the change: the code before the change,
with seed 8 instead of 7, measures the fire pit at 59.2 dB(A) with mod mid 0.237 just outside, and the stand
of trees at 82.4 dB(A) with corr octave 0.682 and mod mid 0.140 outside, the same as after.

Cost of the spread (`--fire spread cost`): 3,000 trees six metres apart, struck in the middle in a 6 m/s
wind, crowning through the forest with up to 2,600 parts burning at once: a step of 12 ms on average and 43 ms
at worst, once a second (measured while other work ran on the machine). The server runs it off the tick
thread. Its sound would be hundreds of emitters, which is why aggregation is first in stage 2.

### 12.12 Sources for this section

- Albini 1979. Spot fire distance from burning trees: a predictive model. USDA Forest Service GTR INT-56 [recalled].
- Andrews 2018. The Rothermel surface fire spread model and associated developments. RMRS-GTR-371 [recalled].
- Babrauskas 2003. Ignition Handbook. Fire Science Publishers [recalled].
- Beyler, Fire hazard calculations for large, open hydrocarbon fires. SFPE Handbook of Fire Protection Engineering (the AGA flame tilt correlation) [recalled].
- Butler, Cohen 1998. Firefighter safety zones: a theoretical model based on radiative heating. Int. J. Wildland Fire 8, 73-77 [recalled].
- Drysdale 2011. An Introduction to Fire Dynamics, 3rd ed., ch. 5-6 [recalled].
- Eurocode 1, EN 1991-1-2, annex E (fire load densities) [recalled].
- Finney 1998. FARSITE: Fire Area Simulator. RMRS-RP-4 [recalled].
- Fosberg 1970. Drying rates of heartwood below fiber saturation. Forest Science 16, 57-63 [recalled].
- IEC 62305-3 (the rolling sphere); Golde 1977, Lightning, vol. 1 [recalled].
- Latham, Williams 2001. Lightning and forest fires. In Forest Fires: Behavior and Ecological Effects, 375-418 [recalled].
- McCaffrey 1979. Purely buoyant diffusion flames: some experimental results. NBSIR 79-1910 [recalled].
- Morton, Taylor, Turner 1956. Turbulent gravitational convection from maintained and instantaneous sources. Proc. R. Soc. A 234, 1-23 [recalled].
- Rothermel 1972. A mathematical model for predicting fire spread in wildland fuels. USDA Forest Service RP INT-115 [recalled].
- Schroeder 1969. Ignition probability. USDA Forest Service Office Report 2106-1 [recalled].
- Simard 1968. The moisture content of forest fuels. Canadian Dept. of Forestry FF-X-14 [recalled].
- Tewarson, Generation of heat and gaseous, liquid and solid products in fires. SFPE Handbook [recalled].
- Tohidi, Kaye, Bridges 2015. Statistical description of firebrand size and shape distribution from coniferous trees. Fire Safety J. 77, 21-35 [recalled].
- Van Wagner 1977. Conditions for the start and spread of crown fire. Can. J. For. Res. 7, 23-34 [recalled].
- Johnson, Anderson, Yedinak 2025; Trelles and Pagni 1997; Viegas et al. 2008: section 11.
