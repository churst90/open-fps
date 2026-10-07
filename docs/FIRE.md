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
- Fitted to three campfire recordings (texture round 1, 2026-10-06). It keeps all of that; its roar's
  power is kept and its spectrum is now the f^−α tail, refitted in section 8.

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

- The fire is heard from up to eight places (`ExtendedSources`).
- Along a front (width over 2.5 times depth): the middle and pairs out to either side, evenly.
- Round anything else: a ring at three-quarters of its half-widths.
- Each body is heard from the places near it, weighted by a Gaussian of the places' spacing. A single body
  (a fire pit) is heard from all of its places at once, as before.
- Each place has its own roar noise, its own fizz and its own crackle noise. Their powers are the sums of
  its bodies' shares. Each crackle, fall, crack and burst goes to one place.
- Merged (the source too narrow at the listener), everything is heard from the middle. In between, a share
  of each body's power moves to the middle by the spread.

### 7.3 Parts

- Roar: per place, noise shaped by `PowerLawNoise` (f^−α above 20 Hz), its power the sum of its bodies'
  roar at 100 Hz times each body's puff.
- Fizz: gas and steam through the char, 5 kHz, its power the heat release (as before at the fire pit).
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

To be written with the fit.

## 9. On the maps and in the game

- The fire pit at 58 Alder Street stays as it was. Nothing else is placed on a map.
- `/spawn fire PRESET` (the spawn permission: staff anywhere, a map's owner on their own map) lights a fire
  a few metres ahead of you; a crown fire's front is placed 100 m ahead. It grows from the moment it is lit.
- `/spawn fire out` puts out the nearest one lit that way within 400 m. `/savemap` does not keep them.

## 10. Next

- Smoke explosions and backdraft cycles in a closed building.
- Spotting ahead of a crown fire.
- Fire spreading on a map from one thing to the next: a car to the house beside it, a crown fire through
  the trees the map has.
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
