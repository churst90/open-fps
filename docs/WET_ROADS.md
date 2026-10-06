# Wet roads

Cody, 2026-10-06: "how about vehicles and tires on wet pavement too based on the rain?"

The rain leaves water on the roads; the water changes how much grip a tyre has and what it sounds
like. One state for the water, worked out on the server and sent to every client, so the grip the
server drives with and the hiss a client hears come from the same millimetres.

Code: `OpenFPS.Common/RoadWater.cs` (the water, the puddles, the grip laws),
`OpenFPS.Server/Systems/RoadWaterSystem.cs` (per map, every tick), `WheelDynamics` (wet grip per
wheel), `OpenFPS.Client.Core/AudioEngine/Core/WetTyres.cs` (the sound, in the engine voice).
Instrument: AudioLab `--wet-roads water|levels|game`, measured by `tools/wet_roads.py`.

## 1. The water

Three stores, each with a published law (RoadWater).

**The texture.** The sand-patch mean texture depth (MTD, ISO 10844 / ASTM E965) is by definition the
volume of the voids per area, so a road holds MTD millimetres of water before any film stands on it:
dense asphalt 0.5-1.0 mm (0.7 used), brushed concrete 0.6-1.2 (0.8). Rain fills it directly; only
evaporation empties it. This is the damp road that is still noisy an hour after the rain.

**The sheet.** While it rains, water runs across the road to the kerb. Its depth above the texture at
a distance L down the cross-fall is Gallaway et al.'s equation 16 (FHWA-RD-79-31, 1979, p. 79; checked
against the scanned report):

    WD = 0.00338 TXD^0.11 L^0.43 I^0.59 / S^0.42 - TXD      (inches, feet, in/h, ft/ft)

which in millimetres, metres and mm/h has the constant 0.01485 (the form used here). It is thin: over
a 0.5 mm texture on one 3.65 m lane, 0.33 mm in 25 mm/h and 0.75 in 50 mm/h.

It comes up to that over the kinematic-wave time to equilibrium of sheet flow,
t_e = (n L / sqrt S)^0.6 / i^0.4 (Woolhiser and Liggett 1967; HEC-22 eq. 3-4), one to three minutes,
and drains over the same once the rain stops. The rain is passed through the ladder of linear
reservoirs the running water uses (`Runoff.Rungs`), each map its own, read at t_e.

**The gutter and the puddles.** The flow collected along a kerb between two inlets (50 m apart, run-off
coefficient 0.9) spreads into the road by Izzard's equation (HEC-22 eq. 4-2, SI:
Q = (0.376 / n) Sx^1.67 SL^0.5 T^2.67), so in heavy rain the kerb-side wheels run in it. Puddles are
low spots along each kerb, drawn once from the road's id (`PuddleField`): one in every 30 m of kerb
with a chance of 0.4, 1.2-4 m long, reaching 0.6-1.8 m into the road, 6-25 mm deep when full. A puddle
is a shallow bowl; holding water to a fraction F of its depth, the water covers r² < F and is
D (F - r²) deep, so a filling puddle spreads as it deepens. Puddles fill from the gutter (8 m² of road
draining into each m²) and empty by evaporation and 0.3 mm/h of seepage, over hours.

**Drying.** Penman's (1948) combination equation with his wind function, as Shuttleworth gives it
(Handbook of Hydrology, 1993, ch. 4): E = (Δ Rn / λ + γ f(u) (es - ea)) / (Δ + γ), f(u) = 0.26 (1 + 0.54
u2) mm/day/hPa. The net radiation is the sun's height from the game hour and day (latitude 45°, maps
carry none yet), cut by cloud as Kasten and Czeplak (1980) found, an asphalt albedo of 0.1, and a
longwave loss of 90 W/m² clear and a sixth of that overcast. The weather has no clouds of its own yet:
it is overcast while anything falls and otherwise read off the humidity (clear at 50 %, overcast at 95 %).

What the water table (`--wet-roads water`) gives, settled in each rain, in the wheel path 2.5 m from
the crown of a 7 m street, and at 3.3 m (by the kerb):

| rain | mm/h | wheel path | by the kerb | gutter spread |
|---|---|---|---|---|
| drizzle | 0.3 | 0.70 mm | 0.70 | 0.12 m |
| light | 1.5 | 0.70 | 1.25 | 0.23 |
| moderate | 5 | 0.70 | 3.84 | 0.36 |
| heavy | 25 | 0.73 | 9.75 | 0.65 |
| violent | 70 | 1.34 | 15.89 | 0.96 |

So on a two-lane town street the wheel paths are wet (the texture full) in any rain, and only violent
rain stands a film on them; heavy rain is wetter at the kerb (the gutter spreads 0.65 m) and in the
puddles. After an hour of heavy rain stops, the texture dries over 1 to 1.5 hours on an overcast spring
afternoon (0.46 mm/h), in under an hour at a dry summer noon (0.9 mm/h), and not at all on a humid
night (it is still wet in the morning).

**On the wire.** `WorldStateUpdate.RoadWater` carries the whole state (rain, evaporation, the ladder,
the textures, the puddles; 21 floats) for every client on the map, and each wheel carries the water
under it (`WheelState.Water`, one byte on a square-root scale: 0.01 mm at 4, 1 mm at 40, 40 mm at 255).
Both appended: the server and the client must be rebuilt together and the server restarted.

## 2. Grip

`RoadWaterLaw.GripFactor`, per wheel, at the start of each step (`WheelDynamics.Wet`):

- **Damp to wet:** from dry toward Wong's wet ratio as the texture fills. Wong, Theory of Ground
  Vehicles, Table 1.3: asphalt and concrete dry 0.8-0.9, asphalt wet 0.5-0.7, concrete wet 0.8, earth
  road dry 0.68, wet 0.55. Gravel drains and keeps its grip; snow and ice are not the water model's.
- **Speed:** wet friction falls with slip speed by the PIARC International Friction Index,
  F(S) = F60 exp((60 - S) / Sp), Sp = 14.2 + 89.7 MPD km/h (Wambold et al. 1995), MPD from the sand
  patch by ASTM E1845; the slip speed at a tyre's peak force taken as 12 % of the road speed, and
  Wong's figures as read at 50 km/h.
- **Aquaplaning:** a film above the texture lifts the tyre as the water's dynamic pressure, which grows
  as V², approaches the load: the share lifted is (V / Vp)², of which the grooves swallow a film up to
  their own volume (lift scaled by h / (h + 0.3 x tread depth)). Vp is Gallaway's equation for treaded
  tyres (mph, psi, 32nds, inches):

      V = SD^0.04 P^0.3 (TD + 1)^0.06 A,  A = max(10.409 / WD^0.06 + 3.507, (28.952 / WD^0.06 - 7.817) TXD^0.14)

  For a car tyre at 220 kPa on 2 mm it gives 88 km/h, beside Horne's 6.34 sqrt(p kPa) = 94 km/h for a
  flooded smooth tyre (Horne and Dreher 1963, NASA TN D-2056: 10.35 sqrt(p psi) mph, which is 6.34 in
  km/h and kPa). Tyres declare their pressure and tread
  (`TyreProfile.InflationKPa`, `TreadDepthMm`): car 220 kPa and 5 mm, bike 270 and 3, truck and bus 760
  and 12, slick 160 and none.

So, a car on asphalt in the wheel path keeps 0.73 / 0.71 / 0.67 / 0.64 of its dry grip at 30 / 50 / 80
/ 110 km/h in any rain up to heavy, and 0.71 / 0.65 / 0.53 / 0.45 in violent rain; a bus 0.73 / 0.69 /
0.64 / 0.58 in violent rain. In a kerbside gutter or a puddle the film lifts it far more. Stopping from
50 km/h on the brakes: 10.5 m dry, 14.9 m wet (1.2 mm), 16.4 m on 4 mm of water.

**Squeal.** Water in the contact lubricates the tread's stick-snap that the squeal is made of: a wheel's
squeal is scaled by `RoadWaterLaw.SquealFactor`, falling to a fifth as the texture fills and to nothing
under a millimetre of film, and a full slide on a wet road is the broadband slide rather than the
screech. No measurement of squeal against water depth was found: an assumption, to be judged by ear.
The tyres reach their limit sooner on a wet road (the demand is against the wet grip), so a hard stop
slides sooner and longer; it is heard as a slide and a hiss, not a squeal.

## 3. Drivers

Traffic on its wheels reads the wet grip under every wheel. Corner speeds from the tyres' limit are
scaled by the root of the wet share (a steady turn's speed goes as the root of the friction); the
comfortable side friction drivers keep to (AASHTO) is usually lower and still decides. On a map with
street life drivers also give up a share of their speed and keep a longer headway in the IDM/ACC
following model (`StreetLifeData.WetSpeedReduction`, `HeavyRainSpeedReduction`, `WetHeadwayIncrease`,
`HeavyRainHeadwayIncrease`), blended from the wet road (the asphalt's texture full) to heavy rain:
3 % and 8 % slower, 12 % more headway, from FHWA's three-city loop-detector study (Rakha et al.,
FHWA-HOP-07-073, 2007: free-flow speed -2 to -3.6 % in light rain and -6 to -9 % at about 16 mm/h;
capacity -10 to -11 % in both, jam density unchanged, so a time headway about 12 % longer).

## 4. The sound

A tyre on a wet road must move the water out of its way: every second it sweeps a strip its own width
and its speed long, and the water in it is squeezed out of the contact patch through the grooves and
thrown off the tread. `WetTyres`, per wheel, in four parts:

- **Ejection:** the water leaving the grooves breaks into sheets, ligaments and drops: a dense hiss
  above a kilohertz. Its power is the kinetic energy of the water thrown, ½ q u² with q = ρ w u W, so it
  grows with the water W and the cube of the speed, and it pulses as each groove empties.
- **Impacts:** the larger drops striking the wheel arch, the underbody and the road beside the tyre
  (EventSum impacts, lognormal sizes): the crackle in the hiss, and most of what is heard inside.
- **The bow:** a film above the texture is pushed ahead and aside: a lower swish growing with its depth.
- **The splash:** entering a puddle the tyre throws a sheet up and out (a burst), which falls back over
  a few tenths of a second as drops and bubbles on the water.

Each wheel's water is its own (from the server), so a kerb-side wheel in the gutter or a puddle is
louder than the crown-side one, and each goes out through the tap at its end of the vehicle with the
same distance weighting as its squeal. Inside, the impacts and a share of the hiss reach the cabin
through the wheelhouses and the floor trim (a low-pass and a loss), into the cabin model.

Wipers are not modelled (next step), nor the spray thrown by other vehicles onto the windscreen.

## 5. Fitting

Yardsticks (never shipped): 47 recordings in `~/openfps-scratch-archive/wet-roads-2026-10-06/refs`
with `SOURCES.txt` (Freesound previews, CC0 and CC-BY): 11 wet and 10 dry car pass-bys (four wet/dry
pairs by the same recordist), 6 wet and 2 dry traffic, 3 wet trucks, 3 wet buses, 7 puddle splashes, 3
wet and 2 dry car interiors. Literature notes in `LITERATURE.txt` there. No per-band wet-against-dry
figure in the literature could be read (Descornet 2000 and Freitas 2009 are paywalled); the checked
ones are the overall 0 to 15 dB(A) (Sandberg and Ejsmont 2002, via Caltrans TeNS 2013, p. 2-33) and
the spectral peak moving from about 0.6 kHz dry to 0.8-1 kHz wet (Kongrattanaprasert et al. 2009).

Measured on the loudest 2 s of each pass, each normalised to its own 1 kHz octave, wet minus dry:

| | 2 kHz | 4 kHz | 8 kHz |
|---|---|---|---|
| recordings, mean of 11 wet against 10 dry | +4.6 | +10.2 | +12.6 |
| recordings, median | +3.8 | +7.7 | +7.2 |
| recordings, same-recordist pairs | +6.8 | +14.2 | +17.5 |
| the game, car at 50 km/h, light rain (0.7 mm) | +4.7 | +9.1 | +11.9 |
| the game, car at 30 km/h, light rain | +3.9 | +8.4 | +10.1 |

The game's A-weighted increase at 50 km/h: +4.4 dB(A) in light rain, +4.9 heavy (and the rain itself),
+2.3 at 30 km/h; the bus at 40 km/h +4.8. At a metre, with no rain, the increase shrinks with speed
(+4.6 / +3.7 / +2.8 dB(A) at 30 / 50 / 80 km/h on 0.7 mm), as the literature describes. The 250-500 Hz
octaves move by under a decibel in the recordings and by under 2 dB in the game.

Fine texture: the 10 ms 4-16 kHz kurtosis of the loudest 2 s is 2.7-3.25 in the wet recordings (Gaussian:
wet hiss is smooth noise, not clicks) and 3.0-3.3 in the game's wet pass-bys; puddle splashes 3.1-3.7
recorded, 3.2-3.4 in the game.

Inside the same car on a wet and a dry road (augustsandberg, one microphone; the wet take also has rain
on the roof), wet minus dry relative to the 125 Hz octave: 250 Hz +3.9, 500 +8.0, 1 kHz +13.8, 2 kHz
+16.9, 4 kHz +22.4, 8 kHz +19.7. The game's cabin at 50 km/h on a wet road with no rain: +4.8, +4.0,
+12.9, +19.2, +19.3, +14.2, and +12 dB(A). A recalled figure of +2-5 dB(A) inside cars with plastic
liners could not be checked; this is the number to judge by ear.

Constants (WetTyres): the ejection hiss is 83 dB at a metre for one 205 mm tyre at 50 km/h on 1 mm of
water, its power linear in the water and in the cube of the speed, band 0.9-3.5 kHz with two poles
above (brighter as the root of the speed); impacts 3000 a second per tyre at -6 dB of it; the bow 66
dB per mm of film; the splash 92 dB for a 10 mm step at 50 km/h with 120 drops falling back; the cabin
path a 2.5 kHz low-pass at -14 dB.

## 6. Not modelled yet

- Wipers, and the spray of other vehicles on your windscreen.
- Ruts: worn wheel paths hold water in heavy rain, which is what makes most roads splash more in a
  downpour than the thin sheet here allows. A rut depth per road would be the next datum.
- Porous asphalt (drains like gravel until it clogs), and concrete's joints.
- Puddles only along kerbs; none at junctions, drains or dips in the road, and the player's car finds
  its puddles by position while traffic finds them by lane.
- The spray in the air behind a vehicle (a mist that hisses on the next car), and its effect on sight.
- Squeal against water depth is an assumption (section 2).
- Cloud cover: the weather has none, so it is read off the humidity for the drying.

