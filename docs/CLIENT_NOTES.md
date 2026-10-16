# Client notes

The reasoning behind OpenFPS.Client.Core code that has no other doc of its own. The code points here.

## Stair cues

What the client says about stairs ("Stairs up, 17 steps, to floor 3") comes from the map's stair
markers (prefabs/stair_marker.json): each end of a flight is a marker standing a metre above the
landing a step back from the end riser, facing the way you walk to take the flight, named with its
line. Nothing works out where a flight is from the geometry.

Said once per arrival, never about the flight you came by. This replaced a rule of reach and leave
radii a metre and a half apart (Cody, 2026-10-04: "I hear indicators repeating several times"). On a
landing the top of one flight and the foot of the next stand side by side facing the same way, so a
sidestep from one lane to the other left one and reached the other, and "Stairs up, Stairs down,
Stairs up" came out of shuffling on one spot; any two-metre walk away and back said it again, nine
times in three minutes; and arriving off a flight walking backwards (facing up it) announced the
flight just walked down. Now a marker is quiet until you have been somewhere else: another zone,
another floor, or `LeaveMetres` across a big one; and both ends of the flight under your feet are
quiet.

On a flight. Where the map names its flights and landings (NamedPlaces) the flight is a zone of its
own, "Marlow Tower stairs, floor 2 to 3". Where it does not, a storey's zone stops at its ceiling and
the next starts at its floor, so at eye height the name would change halfway up the stairs; the zone
announcer holds a room's name while `OnFlight` is true and says where you are when you step off.

The cue speaks for the stairs. Walking up to a flight, the cue is said half a metre before the first
riser, and a moment later you step into the flight's zone (and you crossed the landing's zone to get
there). The cue says more (which way, how many steps, to where), so a flight's or landing's name is
not said when the cue has just been, or for a flight whose end the cue told you about (`CoversZone`,
`FlightAnnounced`). The zone is said when the cue was not: stepping on from the side, or backwards,
or arriving on a landing off a flight.

One beacon a floor (Cody, 2026-10-04: a beacon only at the bottom and top left "the levels in
between" to be found without seeing where the stairs are). Each floor's beacon is the foot of its
flight up; on the roof, the top of the flight down. Which end of a flight a marker is comes from the
markers alone, paired from the bottom up: in a dog-leg every other flight is in the same lane, so
the top of one flight faces, along one line, both its own foot a storey down and the foot of the
flight two up a storey up. The lowest marker can only be a foot, and pairs with the nearest marker
above facing back down its line (that flight's top); in order of height, every marker not already a
top is the foot of the flight above it, or the top of the whole stair if nothing above faces back.
The same stacking is why `OnTreads` looks for the other end on the side of the marker the feet are.

## Breathing is not played

Breathing is the only sound a body still makes once it has stopped moving, and so the only way to find
somebody who has stopped to listen for you. It was judged by ear and rejected: "I don't like the
breathing, remove it." Not a bug: the model and the synthesis were both repaired first (BreathTests and
the AudioLab's --breath), and what was left was a breath that sounded like a breath and was still not
wanted. A sound nobody wants to hear is not information, however correct it is.

The model stays and keeps running: Breathing (LocalPlayerController) drives the exertion readout on B
("Breathing hard", "Winded"), which is the useful half. Only the voice is gone. Bringing it back means
subscribing the controller's and the other bodies' OnBreath to a handler that submits the breath as a
world sound; ClientAudioSystem.OnBreath, which did that, was removed as dead code on 2026-10-07 (it is
in git history before commit d3944776).

## Facing while riding

Reported: "when the bus turns, the bus turns around my head, which is wrong. My head should stay facing
the direction of the bus, and when I press F it should tell me the correct direction." A first attempt
carried the player's own heading round with the vehicle's turns and let the server's copy correct it;
but the server's copy arrives a network trip late, so half way through every corner the two disagreed by
more than the correction threshold and the head was snapped back to where the bus had been, the bus
swinging round the listener. Now, while riding, the heading is the vehicle's, set every frame from the
vehicle (ClientGameSession.FollowRide) and never corrected (PredictionReconciler skips the look while
Riding). The ears, the compass on F and the way you face when you step off all read the same number.

In the driver's seat the look keys do nothing: every driving cue (the guide ahead, the centre line on
your left) is placed relative to the car, and a head turned away with J or L would move them all.

## Turn key signs

Which way each key turns, as a sign on what the physics applies:

- Yaw -= LookDelta.X * RotationSpeed * dt. Forward is (sin yaw, 0, cos yaw), so increasing yaw swings
  forward toward +X, which is right: a positive LookDelta.X decreases yaw and turns left.
- Pitch += LookDelta.Y * RotationSpeed * dt. Rotation is built by
  Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0), whose pitch is a right-handed rotation about +X,
  taking forward (+Z) toward -Y: increasing pitch looks down.

J and L were the wrong way round: J emitted a negative X, which increases yaw and turns right ("turning
left seems to turn me right"). Only findable by following the sign to the forward vector, because every
step is individually plausible. K and O were wrong for the same reason, and the comment there was part of
it: it said a positive pitch looks up, the intuitive reading of the word and the opposite of what
CreateFromYawPitchRoll does ("k and o seem to be swapped"). The F readout had the pitch backwards too
("when I press O and I'm looking up, pressing f says looking down"). TurnKeyTests holds the signs.

A key that turned for as long as it was down could not be aimed: at the old rate a press held a tenth of
a second swung you twenty-six degrees ("it seems jumpy when I turn and then press f, it's like sometimes
I overshoot"). A tap is now one step whatever the frame rate, and holding sweeps after 0.35 s.

A coarse tap snaps to the grid rather than adding forty-five degrees to wherever you are: once a fine
nudge or a sweep left you at 47 degrees every tap landed on 92, 137, 182, and walking "straight" then
changed both coordinates ("if I press j or l to go facing north and I walk straight, both the x and the y
change when they shouldn't"). Facing north, east, south or west with exactly one coordinate moving is
what the key is for. Shift stays one degree, off the grid on purpose.

## Shift, Control and taps in GatherInput

Shift once stopped movement dead, so a Shift chord could never walk the player. That made Shift+W
unusable, and Shift+W is where a run belongs. The meanings do not collide: Shift with a turn key is a
one-degree nudge, Shift with a movement key is a run, and holding both does both. Alt still suppresses
everything, as do the console keys.

Held state is sampled once per fixed tick, 33 ms apart, and a quick tap is shorter: pressed and released
inside one interval, the key was never in `held`, so the press did nothing (no movement, no footstep, no
packet). A press is a player asking to move, and the least this simulation can move is one tick, so that
is what a press already over is worth; the just-pressed set is consumed by the same drain, so it is paid
once however the two rates line up.

## Footsteps and the smoothed position

VisualOffset slides the listener to a corrected position over about two tenths of a second so the world
does not jump. Fed to the stride generator, that slide became walking: a correction of a few metres
decays at up to twenty-five metres a second in plausible steps, and if the player was moving when it
landed every step banked distance ("when I /tp myself or land in the map, I hear a few footsteps before
it settles"). A stride is something a body did; the smoothing is done to the camera, so the stride reads
the body's position only.

## Speech has no ground reflection

A one-off sound gets a ground reflection of its own only when it is an impulse (a shot, a door, a knock),
whose bounce lands inside the attack and is heard as part of it; on anything that lasts it is a comb that
stands still. Not an echo copy, which is already a path off a surface; not a source with a size, whose
parts are at every height and distance at once, so their bounces arrive spread out and add up to no comb.

Not speech. A voice three metres off on asphalt has a bounce 4 ms late at two thirds of its pressure, and
that is what the physics says (Acta Acustica 2024, doi 10.1051/aacus/2024002: below 800 Hz it is stronger
still). Rendered, it flanges, summed into the voice's direction and again from its own direction below. A
real talker on a pavement does not sound like that, so something the ear uses is missing: the torso's
shadow on sound from below, the talker's own vertical radiation, or the small movements that keep a comb
from standing still. Until one is measured, a voice has none (WorldAudioPlayer.HearsTheGround).

## The ground by its impedance

Built 2026-10-16 (todo, "The ground reflection, done properly"). A voice's ground was only ever a copy of
itself, the surface's absorption in two bands: over grass it added a little less than over asphalt, and
never took anything away. Real soft ground grazed from far off sends the middle of the range back turned
over and cancels part of the direct sound, which is a large part of why traffic fades across fields.

- The ground's materials carry a flow resistivity (Nord2000's classes; the sources are on
  `AcousticRegistry.AddGroundImpedance`): lawn 200, forest floor (Foliage) 31.5, dirt and gravel 500, sand 300,
  snow 12.5, clay 2,000, asphalt 200,000, concrete 200,000 kPa s/m², water and ice rigid. Everything else has
  none and answers by its absorption, as before.
- Asphalt is dense asphalt, Nord2000's class H (Cody, 2026-10-16); it was G's 20,000, "most normal asphalt".
  Measured with AudioLab `--ground-answer` on Foundry Street, a standing ear along the road: at G the gutter,
  the drain's grate and a runnel 1 cm over the road lost 1.6 to 3.1 dB of their top at 25 m, 3.1 to 4.4 at 50,
  5.4 to 6.3 at 100 and 8.1 to 9.7 at 200 (and 0.4 of the middle at 200); at H they lose nothing at 25 to 100 m
  and at most 0.2 dB of the top at 200. The survey's last row for the drain, 15 dB of the middle at 200 m, is
  the dirt verge behind the kerb, where the bounce lands for an ear to the south. A car (i4_midsize at 50 km/h,
  exhaust 0.3 m up) heard along the road is the same to 0.05 dB at 4.5 to 50 m, and at 100 and 200 m keeps 0.1
  and 0.4 dB more of its top and 0.2 more of its middle at 200 m than at G.
- `GroundAnswer` (OpenFPS.Acoustics): Delany and Bazley's impedance; the spherical wave's reflection,
  Q = Rp + (1 - Rp) F(w), with Humlicek's W4 for the Faddeeva function; Clifford and Lataitis' coherence as
  before. Sound and real ground carry 1 + |Qs|² + 2 C Re(Qs e^(ikΔ)) over free field, judged over four
  stretches of the voice's bands (50-400, 400-1,000, 1,000-4,000, 4,000-14,000 Hz), each over three thirds
  of itself with the mean of e^(ikΔ) in closed form, so it moves smoothly as a source does. No band falls
  more than 20 dB under free field: unevenness and scattering are not modelled, and the floor stands in.
- The copy Cody approved stays as it was, bit for bit (the stored streams moved only by the three new
  fields, all zero). The impedance only takes away: in each of the voice's bands, sound and copy (reckoned
  as GroundReflection plays it, its one-pole split and its delay) are brought down to what sound and the
  real ground carry where that is less, as a gain on the band (`SpatialEmitter.GroundLowDb` and the rest,
  glided in the mixer's THREE_EQ with the path). Where the real ground carries more it is left alone. So
  the far top over any ground, and water and concrete in the middle at a hundred metres, stay as they were
  though the law would lift them 1 to 4 dB (turbulence makes the top add as power, and the old law gave
  every surface asphalt's fall with range): a question for Cody.
- Roads, car parks and hard floors: asphalt (now concrete's class) and concrete unchanged for a source 0.3 m and
  more up and a standing ear from 2 to 200 m, and water too (GroundAnswerTests holds 0.75 dB); water running at a
  road's face, 1 to 10 cm up, within a decibel from 10 to 200 m (`ARoadKeepsTheTopOfWaterRunningInIt`). Tile, marble,
  wood, brick and carpet have no flow resistivity and are exactly as before.
- Where it shows: the bounce lands near the lower end of the path, so it is the ground under a low source
  that matters. A car on Alder Street heard from Elm Park bounces on the pavement's concrete: no change.
  A car's exhaust 0.3 m over Elm Park's lawn (AudioLab `--ground-answer car=i4_midsize`, the live voice at
  50 km/h): the middle band 2.2 dB down at 25 m, 6.8 at 50, 9.4 at 100 and 9.1 at 145, the low band 0.4
  and 0.8 down at 100 and 145, the top as it was. A source at the ground (a rill, a downpipe's splash) over
  grass loses 3.6 dB of its middle at 10 m and 8.5 at 25, and its top as well.
- Cost: 3.3 µs a voice on ground with a flow resistivity, 0.12 µs without (AudioLab `--ground-answer cost`),
  on the placement thread with the rest of ApplyGround; the rays are cached as before.
- Not seen by the law: a car's tyre share (GroundReflection.NearGroundShare), which makes the copy carry
  more than reckoned, so the ground takes less from a car than it should; and a texture's ground, reckoned
  as a comb (a source at the ground has its copy in phase either way).
  The ground is judged at the one bounce point, so it changes as that point crosses a kerb (Nord2000's
  Fresnel-zone mixing would blend it). μ² is fixed at 5e-6, not taken from the weather: the weather's would
  move a lawn's middle band up to 4.5 dB at 100 m ("The air's stir from the surface layer", below).

## Sound bent by the wind and the air's layers

Built 2026-10-16, unheard (todo, "Refraction past 150 m"; Cody's decision on the Resonance survey,
inbox/2026-10-14-resonance-acoustics-beyond-open-fps, section 2). Sound goes faster where the air is warmer
and where the wind carries it, so a ray curves toward the slower air. Upwind, or on a sunny afternoon, the
lowest ray from a low source lifts off the ground and past some distance the listener is in shadow; downwind,
or on a clear night, sound arcs down and its ground bounce comes in steeper, so a lawn cancels less.

- The air's layers (`AirLayers`, OpenFPS.Sound, bound for Environment): Monin-Obukhov similarity with
  Businger's profile functions. Over land the heat the ground gives the air comes from the net radiation
  (`RoadWaterLaw.NetRadiationWm2`: the hour, the day of the year, a 45° latitude and the cloud) by Holtslag
  and van Ulden's scheme by day and van Ulden and Holtslag's θ* = 0.09 (1 − 0.5 N²) K at night; over open
  water from the air against the water (Large and Pond's bulk coefficients), the water following the
  season's air a month behind. The client works it out at each weather broadcast; the server now sends the
  day of the year with the hour (`WorldStateUpdate.DayOfYear`, appended).
- The rule (`Refraction`, OpenFPS.Acoustics): the effective speed of sound along the way is Harmonoise's
  lin-log profile, the wind's log law along the way plus the temperature's two terms. Harmonoise's two
  radii (Nota et al., Inter-noise 2004, eqs. 6 and 7) make one curvature per path, the log term read at the
  height its ray climbs to, or at the higher end. The shadow starts √(2R)(√hs + √hr) off (Fleagle 1949);
  past it the sound dies as a creeping wave round a rigid curved ground (curved-ground analogy, Almgren 1987;
  Keller; Berry and Daigle 1988), the treble faster than the bass (as f^1/3), held 20 to 30 dB down by the
  air's stir (Embleton 1996), 30 in a calm, 10 less for each tenfold stir. Bent down, each end of the
  ground's bounce is lifted d²/2R (at most 30 m) and `GroundAnswer.Reflect` judges the ground with the ends
  so raised: the same law, so a lawn's dip fills and nothing is counted twice.
- The flicker: past 100 m a far sound rises and falls with the stir, the Rytov law
  χ² = (√π/2) μ² k² L L0 with L0 = 1.1 m, at most 3 dB (one standard deviation), mean power kept. The stir
  signal is the wind field's two smallest eddy layers at the way's middle (`WindField.Stir`); μ² is the
  surface layer's since the same day ("The air's stir from the surface layer", below).
- Where: the mixer works it out for every voice in the world every 0.1 s (`FmodAudioProvider.Bend`,
  `Bending.Along`, 1 µs a voice), from where the voice is placed (a sound heard over a roof comes from
  above it, and is bent less), with the wind where the way's middle is at that moment (gusts move the
  shadow's edge), over the ground its bounce found or the listener's, and glides it as the path's bands are.
  Speech, birds and one-offs bend as engines do; the voice's own ground copy, where it has one, takes the
  raised ends in `ApplyGround` (`SpatialEmitter.OverWater` says whether that ground is water). Not bent: the
  listener's own sounds, the vehicle you ride, rain and wind, and the thunder, which carries its own.
- Measured (AudioLab `--refraction car=i4_midsize`, exhaust 0.5 m, ear 1.7 m, across grass, 4.5 m/s, change
  from before, low/middle/high dB). Afternoon (15:00, clear): upwind 150 m −7.6/−16/−28, 300 m −13/−27/−29,
  600 m −19/−29/−29, the edge at 75 to 150 m; across 150 m −2/−4/−8, 300 m −5/−11/−21 (the sun's lapse
  alone); downwind 150 m −1/+3/−0.5 and 300 m −1.4/+0.9/−1.1 (the lawn's dip fills; the bass loses a little
  because the raised ends take its image out of step too; the treble's −1 is the flicker's mean). Night (03:00, clear): upwind about as the afternoon; downwind and across +1 to +3 in the
  middle. Still neutral air: no shadow inside 700 m. Within 50 m at most 1.7 dB of treble upwind on a sunny
  afternoon; at 100 m upwind in that breeze 5/11/20 already (the edge is at 62 m).
- Thunder: unchanged (`--thunder` measured before and after, every row the same). The shared rule would
  move it: its still-air edge agrees with the thunder's to under 1 %, but the wind's log law aloft and the
  creeping wave's depth by band would move far thunder by up to 25 dB (`--refraction thunder`), so the
  thunder keeps its own profile and its single 25 dB until Cody hears it (a question).
- Known short of the whole: land or water is the bounce point's (a shore path flips); the log term's height
  is the higher end's for raised sources, an extension of Harmonoise's relation; fences and buildings do not
  yet shadow less under sound bent down.

## The air's stir from the surface layer

Built 2026-10-16, unheard (Cody approved the task that day). The flicker past 100 m and the shadow's floor read
the variance of the refractive index, μ². It was the thunder's judgement (0.5 K of temperature swing and 0.2 m/s
of wind in still air), so a far sound's treble swung about 3 dB, one standard deviation, on the stillest night.
Now it is the air's own, from the surface layer the bending already works out.

- The eddies (`AirLayers.Layer.EddiesAt`), by Monin-Obukhov scaling from the layer's u*, θ* and L, which the
  layer now keeps. The wind swings 2.4 u* along the mean and 1.9 u* across it (Panofsky and Dutton,
  "Atmospheric Turbulence", 1984; their ratio is the wind field's `CrossShare`) and 1.25 u* up, the vertical
  growing as (1 − 3 z/L)^1/3 in unstable air (Panofsky, Tennekes, Lenschow and Wyngaard 1977). The temperature
  swings 2 |θ*| in neutral and stable air and 2 |θ*| (1 + 9.5 |z/L|)^-1/3 in unstable air, which tends to
  Tillman's free-convection 0.95 |θ*| (−z/L)^-1/3 (Kaimal and Finnigan, "Atmospheric Boundary Layer Flows",
  1994, ch. 1; Tillman 1972). The along and across swings stay at their neutral values: in unstable air they
  grow with the boundary layer's depth, which the weather does not carry, and eddies that large bend sound
  rather than scatter it.
- μ² (`Refraction.StirVariance`, read through `Bending.StirVariance`): the Gaussian model's
  σ_T²/(4T²) + σ_v²/c² (Ostashev and Wilson, "Acoustics in Moving Inhomogeneous Media", 2nd ed. 2015, ch. 6;
  Salomons, "Computational Atmospheric Acoustics", 2001, appendix I), σ_v² the mean of the three components'
  variances (the model is isotropic; the same turbulent energy). It is taken at the way's mean height over its
  ground, land or water. The eddies' outer scale is that height (`Refraction.OuterScale`, at most 10 m): for a
  car's exhaust heard standing, 1.1 m, the figure Daigle, Piercy and Embleton (1983) fitted near the ground.
- What it comes to 1.1 m up (day 172, 45°, clear): still neutral air 0; a calm (0.5 m/s) clear night 1.2e-7
  (u* 0.02 m/s, θ* 0.09 K); a calm sunny afternoon 3.7e-6 (the sun's heat, σ_T 1.1 K); a 4.5 m/s breeze 7.5e-6
  by day and 4.9e-6 by night; 10 m/s 2.9e-5. The judgement gave 1.1e-6 to 4.4e-6 for the same air. Daigle et
  al. measured 1e-6 to 1e-5 near the ground.
- The shadow's floor still reads 30 dB at the judged still-air stir (`CalmStirVariance`, 1.1e-6) and ten less
  for each tenfold: a calm night keeps 30, a calm sunny afternoon 25, a breeze 21.5, a wind 20.
- The thunder keeps its judged stir (`Refraction.JudgedStirVariance`, which `Thunder.TurbulenceVariance`
  reads): its way runs kilometres up, out of the surface layer, and it was approved with it. `--thunder`
  before and after: every band of every case the same (only the render times and Steam Audio's traced street
  tail, which differs from run to run by about a decibel, moved).
- The ground's coherence (`GroundAnswer.Coherence`) keeps its 5e-6. With the surface layer's μ² in its
  place (measured on flat ground, a source 0.3 m and 0.05 m up and a standing ear, 10 to 100 m): concrete,
  asphalt and water within 0.7 dB; a lawn within 0.1 dB at 10 and 25 m, but at 100 m a car's middle band 3.5 dB
  lower on a calm night and 4.5 dB higher in a 10 m/s wind, and a rill's treble 4.3 lower and 5.4 higher. That
  moves an approved sound, so it waits for Cody.
- Measured (AudioLab `--refraction`, exhaust 0.5 m, ear 1.7 m, across grass, a west wind; the change in the
  mean and the swing's standard deviation over ten minutes, before and after, low/middle/high dB):

| air | way | m | mean change | swing before | swing after |
|---|---|---|---|---|---|
| calm night (0.5 m/s, 03:00) | downwind | 150 | 0.0 / +0.3 / +0.3 | 0.1 / 1.2 / 2.7 | 0.0 / 0.4 / 2.4 |
| | | 300 | 0.0 / +1.4 / 0.0 | 0.3 / 2.3 / 2.6 | 0.1 / 0.7 / 2.6 |
| | | 600 | +0.1 / +1.4 / 0.0 | 0.4 / 2.3 / 2.2 | 0.1 / 1.1 / 2.2 |
| | upwind | 600 | +0.1 / +1.1 / 0.0 | 0.5 / 3.0 / 2.9 | 0.1 / 1.4 / 2.9 |
| calm sunny afternoon (0.5 m/s) | downwind | 150 | 0.0 / −0.6 / 0.0 | 0.2 / 1.2 / 2.8 | 0.3 / 2.2 / 2.8 |
| | | 300 | −0.1 / −0.3 / +5.2 | 0.3 / 2.2 / 2.6 | 0.4 / 2.5 / 2.6 |
| | | 600 | −0.1 / +5.2 / +5.2 | 0.5 / 2.3 / 2.3 | 0.8 / 2.3 / 2.3 |
| breezy afternoon (4.5 m/s) | downwind | 150 | 0.0 / −0.7 / 0.0 | 0.2 / 1.6 / 2.9 | 0.4 / 2.9 / 2.9 |
| | | 600 | −0.2 / 0.0 / 0.0 | 0.6 / 2.9 / 2.9 | 1.2 / 2.9 / 2.9 |
| | upwind | 300 | −0.1 / +4.3 / +6.3 | 1.1 / 3.5 / 2.9 | 1.3 / 2.9 / 2.9 |
| | | 600 | −0.2 / +6.4 / +6.4 | 1.5 / 2.9 / 2.9 | 1.8 / 2.9 / 2.9 |
| windy afternoon (10 m/s) | downwind | 300 | −0.3 / 0.0 / 0.0 | 0.6 / 3.0 / 2.9 | 1.5 / 3.0 / 2.9 |
| | | 600 | −0.6 / 0.0 / 0.0 | 0.9 / 2.9 / 2.9 | 2.3 / 2.9 / 2.9 |
| | upwind | 300 | −0.2 / +4.0 / +4.1 | 2.3 / 3.0 / 3.0 | 2.7 / 3.0 / 3.0 |
| still neutral air | any | 300 | +0.1 / +1.8 / +2.3 | 0.3 / 2.3 / 2.7 | 0 / 0 / 0 |

  Still air swings nothing and loses nothing to the flicker's mean. A calm clear night's middle band swings a
  third as much as before. A sunny afternoon, a breeze or a wind swing as much as before or more (the law was
  already at its 3 dB cap for most of them), and a sound deep in shadow there keeps 4 to 6 dB more, the
  stirrier air scattering more into it. Across the wind (not in the table) the shadow's edge moving with the
  swing dominates, as before. `Bending.Along` still costs about 2 µs a voice.
- Known short: a calm clear night still swings the treble (the high band, about 7 kHz) 2.4 to 2.9 dB past
  150 m, since the Rytov law grows as the frequency squared and is near its cap there even at 1.2e-7. The
  afternoon's convective eddies are left out. The way is one height: a source high over a low ear reads the
  mean of the two.

## Where the wind comes from

Built 2026-10-16, unheard (the Resonance survey, inbox/2026-10-14-resonance-acoustics-beyond-open-fps, section 3,
approved by Cody that day with his rule: "wind sound should actually follow the wind directions, not random"). The
wind never changed direction except when a front came, and every front blew from a fixed quarter.

- The direction is the weather's. Each front's wind blows from round the prevailing direction
  (`WindWeather.PrevailingFromDegrees`, 250, the west-south-west), turned as the northern depression turns it
  (`WorldEnvironmentSystem.FrontTurnDegrees`): rain, ahead of a warm front, backed 60 to the south; a storm, at a
  cold front, veered 45 to the west-north-west; snow, in the polar air behind it, veered 60 to the north-west; a fair
  day not turned. The strengths are as they were (a fair day's still drawn, 0 to 7 m/s). Then the day turns it
  (`DiurnalTurnDegrees`): ten degrees either way, veered most at 14:00 when the sun mixes the upper wind down, backed
  most at 02:00. The turns are a judgement of the textbook figures. A wind set by hand (`/weather wind`) is held as
  said, without the day's turn. Nothing about a direction is drawn: two servers with different luck have the same
  direction for the same front at the same hour. The server eases toward it as before (a front's veer takes about half
  a game hour) and sends it; no message changes (the wire hash does, the wind field being in it).
- The swing is the field's. At a place the eddies push the air across the mean as well as along it
  (`WindField.Swing`): the same five eddy sizes as the gusts on lattice layers of their own, carried the same way,
  four fifths of the lengthwise turbulence (Panofsky and Dutton), so the direction swings about 8 degrees either way
  (one standard deviation) in a steady wind and 13 in the gustiest, more in a lull, over the time the eddies take to
  pass (seconds to a minute). In still air, or with no turbulence, nothing swings: Resonance's light-air meander
  (2.5 m/s per unit of turbulence, laid on even with no wind) is left out, since it would blow in still air.
  `SpeedAt` is now the length of `VelocityAt`.
- Who reads the vector: the ears (`EarWind.Hear` takes the field's velocity at the head, so the wind moves from ear
  to ear as it swings), the bending of far sounds (`Bending.Along`, already `VelocityAt`), and the fire's spread
  (already `VelocityAt`; the server's spread now carries the pattern as far as the server has, so it feels the same
  gust and swing the clients hear; it read a pattern standing still). The trees, the flames and the fountain's spray
  read the speed, the vector's length, at each of their own places. The shore reads the mean direction (the waves
  are raised over the fetch, not by a gust), as does the thunder and the storm cell's drift. Rain does not read the
  wind in sound at all yet.
- Hearing it: `/weather` says where the wind comes from now, and while it is going round to another point of the
  compass (a front arriving) says so: "Wind 7 metres a second from the south west, gusty. Turning to come from the
  north west."
- Measured before and after (AudioLab, band changes in dB, 32 Hz to 16 kHz octaves). `--nature levels` at 4.5 m/s
  (turbulence 0.25): park tree +0.8 Leq (0 to +1.0 by band), fire pit +0.1 (+0.3 in the bass, −0.5 to −0.7 above
  1 kHz), fountain −0.1. At 1.5 m/s: tree +0.3, fire and fountain unchanged. `--weather-wind` trees through the
  weather path: calm −0.2, breezy +0.7, stormy 0.0, a storm arriving +0.2. The ears in a calm (1.5 m/s) and a breeze
  (5 m/s from the north east), standing, walking and sprinting: within 1.5 dB, most within 0.5; in a storm, now from 295 rather than 304, the ears 2 to 3 dB lower and their one-second
  range 7 dB wider at the bottom. `--refraction`: upwind and downwind unchanged; a way straight across a 4.5 m/s
  wind now feels the swing: on a sunny afternoon at 600 m it averages 2 to 6 dB less shadow and swings 3.7/9.0/10.8
  dB (low/middle/high, one standard deviation) where it swung 0.5/2.7/2.7; at night at 300 m, −1.4/−0.3/−3.1 against
  −1.0/+2.0/−0.4 with a swing of 1.2/4.8/6.2. `Bending.Along` still costs 1.7 µs a voice.
- Over game hours (`--weather-wind direction`, a reading every ten game minutes in Elm Park): a fair day held from
  10:00 turns from 254 to 256 in the hour, from 258 at noon to 260 at 14:00 and back to 259 by 16:00; the place's
  ten-minute direction wanders within about ten degrees of it with the slow eddies. A storm front arriving at 10:00
  veers it from 255 to 295 inside ten game minutes (the light fair wind gives way at once to the strong one) and the
  day takes it on to 301 by 11:00. A wind set by hand stays at 270. Rolling on its own from 06:00, the
  fair morning turned 245 to 250, a snow front at 08:30 took it to 314 within the hour, and the clearing after it
  backed it to 258 by noon.

## One-off sounds: why the echoes are as they are

- Render once, play by name. Before WorldAudioPlayer the server's whole vocabulary for sound was "this
  entity carries a looping emitter", so glass breakage, gunfire and collisions were written, tested and
  silent. Bridging at the sound-id layer, rather than playing buffers directly, is what lets a rendered
  latch heard through a wall be muffled by the same code that muffles a recorded one.
- A reflection of a one-shot is the one-shot, delayed, quieter and from somewhere else, so it is queued
  as another pending sound at its mirror image. A cheer off the back of a grandstand is most of what makes
  a stand sound occupied rather than a loudspeaker hung in the air; the same machinery gives a gunshot its
  slapback off the building opposite.
- The delay is not added when an echo is queued: the facade delays every submission by its own distance
  over the speed of sound, and the echo is submitted at its image, the whole path away. An earlier version
  added the delay as well, from when FMOD read the wrong clock and no delay was honoured; once that was
  fixed both applied, and every echo came twice as late as its wall (a facade's slapback at 180 ms instead
  of 90; a knock a second after a footstep instead of a tenth, the difference between a room and a canyon).
- The level is the direct sound's, times what the surface kept, times the extra spreading undone: the
  engine applies the spreading from the image itself, and leaving it in applied it twice, a wall that
  answered a near source and went silent for a far one.
- Echo copies were once the sound itself at the mirror point everywhere, a clean second gunshot off a
  brick wall. Then all were smeared through the diffuser, and a shot off steel or concrete (the shortest
  all-pass delays) came back a ringing, processed copy. Now the mirror share, (1 - scattering) of what the
  face returns, is the clean crack, and only the scattered taps across the face are washed.
- Transient voice ids were once a hash of the sound's parameters modulo a million, -1,000 to -1,001,000,
  straight across the engine echo band (-600,000) and the borrowed-voice band (-700,000): a door or a
  footstep that landed there took over a car's reflection or a distant car's voice, heard as a car going
  quiet or a bike's reflection standing still and repeating. The id was also shared by a sound and its own
  reflection, and under one id the last written won: a grandstand was heard only as its echo.
- A one-off's copies are found for where the ears are as it begins. Each keeps the last point it came
  off and is faded out while something solid stands between that point and the ears (`KeepCopiesTrue`):
  one ray a copy, again when the ears have moved 0.25 m or a door or the tiles have. Without it a ringing
  telephone was heard in full by its copies through the door shut on it.

## Rain: a drop's click and its spray

From RainSynth (Click, the spray section), 2026-10-06, texture rounds 1 and 2.

The air hears the rate of change of the drop's push on a solid surface: a dipole at a rigid boundary
radiates dF/dt. It has the blow's peak and fall (RainPlate.BlowShape) but not the blow's length. What
moves the air is the drop's water going from a falling sphere to a sheet spreading over the wet ground,
and that takes the spreading time, about 8/3 D / v (RainSurfaces.SplashSeconds), two and a half times the
time the drop takes to stop. So the click rises to a broad top near 1 / (2π · 0.2 · 8/3 D / v), 0.5-1.5 kHz
for the drops that carry the energy, and falls gently above.

Two earlier time scales were measured against recordings of rain on streets, a garden and a wood (the
lab's `--rain levels` and `compare=`): the fountain's pool click, a 16 µs spike, was 15 dB too bright above
4 kHz; the drop's stopping time, D / v, still 8-13 dB too bright there and 7-20 dB short at 250-500 Hz,
and its 10 ms windows 2.4 dB too peaky (the "grain" figure). The spreading time brings both within a few
dB. The click's energy is the fountain's, fitted against measured falling water (Watts et al. 2009); only
where in the spectrum it sits is the drop's own.

Rain round 1's street measured right on its band envelopes but wrong inside them: its 4-16 kHz waveform
had a kurtosis of 9-10 in 10 ms windows (moderate rain) where every recording of rain is 3.0-4.4, a few
needle-sharp clicks in each window, which Cody heard as "low bit rate, crunchy". The click was the force
on a dry wall, F ∝ √t to its peak, whose slope is infinite at first contact: a single-sample spike
carrying the whole top end. A wet surface does two things instead:

- The drop meets the water film first, and the force on the ground builds as the film is driven out
  from under it, over a good part of the time to its peak (Gordillo, Sun and Cheng 2018, J. Fluid Mech.
  840, 190-214; Mitchell et al. 2019, J. Fluid Mech. 867, 300-322: the force peaks near 0.2 D / v and its
  rise is set by the spreading sheet, not a point). So the click's force rises smoothly (sin²,
  ClickShape) and its spectrum falls 12 dB an octave above a few kilohertz.
- What a listener hears above that is the splash: a crown off the film that throws secondary droplets,
  which land round it over the next milliseconds, and the micro-bubbles of the film bursting (Cossali,
  Coghe and Marengo 1997; Okawa, Shiraishi and Mori 2006: the ejected droplets are a tenth of the drop
  and smaller, tens to hundreds of them). Together, per drop, a short burst of noise in the top octaves,
  rendered as one noise per voice whose power follows the sum of every drop's spray: rising over a
  millisecond and dying over a few.

The spray's share of the impact energy, its band and how long it lasts were fitted on 2026-10-06 against
the rain recordings (the 10 ms waveform, the envelope statistics, and the octave balance round 1
matched). Even the smoothed onset is not a point: the air under the drop is squeezed out and a thin disc
of it trapped (Thoroddsen et al. 2005, J. Fluid Mech. 545, 203-212; Mandre, Mani and Brenner 2009, Phys.
Rev. Lett. 102, 134502), and the contact spreads over the drop's tip in some microseconds, so the onset is
smoothed over the fountain's own first-contact time, ImpactRise.

## The bearing follows the level

Losing the line of sight is not on its own a reason to move a source. Sound past an obstacle takes the
better of two routes, and they arrive from different directions: over the top of a barrier, essentially
still the source's own bearing, or through an opening somewhere else, which is not. BuildSimPath lets the
two compete for the level (that is what stopped a knee-high pit wall silencing a car). The direction was
once decided separately, on visibility alone, so the two halves disagreed: the level said "it came over
the wall" and the bearing "it came from a probe seven metres to your left".

On the speedway that was heard, and reported, as a near car stopping. The pathing probes lie on a uniform
floor grid sized to the map, 7.3 m apart over a 900 x 480 m track, so a redirected bearing is quantised to
that grid: four degrees at a hundred metres, nearly thirty at fifteen, held still while the car crosses
the cell and then jumping.

So the routes compete for the bearing as they do for the level: whichever delivers more energy decides
where it came from. A 0.9 m wall gives a few centimetres of detour, loses about five decibels, and wins:
the car keeps its own direction. A grandstand gives a detour the barrier ceiling flattens to 24 dB down,
and any real opening beats it. Nothing in it knows what a wall or a doorway is.
(AsyncAcousticWorker.RunSteamAudio.)

## Steam Audio pathing is off (a decision for Cody)

The worker makes its simulator with `enablePathing: false`, so Steam Audio's pathing never runs: no probe
grid, no bake, no route search. The code to bake and stage it is still in SteamAudioSimulator
(BeginProbeBake, CommitPendingProbes, the pathing inputs in SetSourceInputs); the reader of its answer
(GetPathing) went in the 2026-10-07 housekeeping, since nothing called it. What it would take and cost,
for deciding whether to turn it on. Not turned on.

What it does: a grid of probes on the floors, a baked visibility graph between them, and each tick, for
each source, the shortest routes between the probes nearest the source and the listener, given as a
per-band level (its eq) and an arrival direction (first-order SH).

What already does that job here, without it:
- The level and bearing of a blocked source: the barrier search over the edges in the way
  (BarrierPathDifference) and the routes through openings (OpeningRoutes, the portal graph). The two
  compete for both (see "The bearing follows the level" above).
- Engines and physical models are placed at their emitter whatever a path says; only recordings and
  one-offs take an apparent position from the path.

What turning it on would take:
1. A reader again: the eq and direction into AcousticPathData, and a rule for how it competes with
   OpeningRoutes and the barrier search for the level and the bearing. Without that rule the three
   disagree and a bearing flips between them (the speedway's "car stopping", above).
2. Probes fine enough to mean something: the grid is sized to the map and capped at 8,192 probes, so the
   speedway's was 7.3 m apart (thirty degrees of bearing quantisation at fifteen metres) and a city's
   coarser. Two metres everywhere is 75,000 probes on a 720 x 420 m map. Streamed maps would need a probe
   batch per tile, baked when the tile arrives and removed when it goes.
3. Doors: the bake sees the scene it was given; a door leaf swinging changes the routes. Either rebake
   round each door or turn on validation (`enableValidation`), which casts rays
   along every route every tick.

What it would cost, measured before it was turned off (docs/AUDIO_GHOSTS_AND_STUTTERS.md, issues 1 and
2): the bake on one thread took about 100 s at 2 m over the speedway, during which every source played
unoccluded until the bake was moved off the worker; at 6.8 m it was 0.5 s for 1,719 probes. The bake
grows worse than linearly in the probe count. The per-tick route search was never measured on its own.
Making it safe also took a fix of its own (a source staged without probes in the tick the batch arrived
crashed the native library; Postscript 2 there).

## The worker's answers: the pool, and what each answer says

The simulator holds 64 Steam Audio sources, all added to it at the start, and traces every one of them on
every run from the inputs it was last given, staged this tick or not. Each tick the first 64 requests in
line are served (last tick's turned-away first); a served source without one takes a free source, or one
held by somebody not served this tick, the one asked about longest ago. So (audit S4, 2026-10-11):
- Only what was staged this tick is read back. A source held but not staged would read back an answer
  traced from where it was then. In practice a full pool is all served sources, so this never fired (0
  stale read-backs in 60 s of `--pop-hunt` with 70 and with 120 sources), but nothing else kept it so.
- A lent source is cleared (`ClearSource`) before its new owner's inputs, as a released one is.
- A request the pool has no room for keeps its last simulated answer for up to a second, moved with the
  source, and is first in line next tick. Not the hand-rolled tracer: that is another model, and the switch
  between them was a pop.
- One ear a tick, the newest request's: the simulator runs against one listener.

Each answer says what traced it (`AcousticPathData.Provenance`: simulated, held, hand-rolled), where the
source was (`SimulatedFrom`; older than `SourcePosition`, the request's, for a held one) and where the ear
was (`ListenerPosition`). Every asker takes one only if it is for here (`AcousticPathData.Answers`):
traced within 1 m of the source and 3 m of the ear, plus 1.5 s of each one's speed. On the city the worker
trails a car by 380 to 470 ms at the median and up to 1.5 s on a loaded machine (28 of 128,000 reads
refused, --pop-hunt in Marlow Tower's stairwell), so 1.5 s keeps the answers that are only late and
refuses one left by a pooled id's last occupant, one asked from before a teleport, and a horn's left from
the horn before. Refused, the path pass keeps the voice's path as it is; a new voice starts on an answer
for a source near it or the one-shots' path; a horn, siren, train or talker takes the one-shots' path.

## Engine render pool

EngineRenderPool removed the ceiling on how many vehicles a map may carry. A physical engine is a
serial integration (each sample depends on the one before), so one engine cannot be split across
threads; but thirty engines are thirty independent integrations. Run one after another inside the FMOD
callback, as they first were, a twenty-four core machine did all of it on one core with the mixer's
deadline running down, and every symptom that followed (the rationing, the shedding, the borrowed
voices, cars dropping out of the world) traced back to that one thread. The work is the same; only
where it happens changed: each engine fills its own ring from a worker and the mixer copies out of it.
The ring already existed because echoes read back through it.

Why not the GPU: the sample axis is a recurrence and cannot be parallelised, the valve solver's
iteration count is data-dependent (a lockstep warp pays for its worst lane), and the GPU is not
real-time scheduled, so a graphics hitch would become an audio dropout. Thirty independent lanes are a
poor fit for a device that wants thousands and a good fit for a CPU with twenty-four cores.

The first version used Parallel.For on the .NET thread pool. At a map load that pool is saturated by
the acoustic bake, the Steam Audio scene build and several dozen sample decodes, and it grows by a
thread or two a second when starved; the producers got no time exactly when thirty had just been
created, fell behind, and the load fell back onto the mixer callback. Heard as the audio cutting out
and going choppy for the first seconds in a map. A real-time producer cannot share a scheduler with
background work, so the pool owns dedicated threads for its life. A quarter of the machine capped at
six was the size when an engine cost a twentieth of a core.

The workers share one sweep down the list (nearest first) instead of a fixed share each (2026-10-07).
With fixed shares a worker held by one slow voice starved every voice in its share while the others
had time: a fifty-wagon freight's 160 taps all took one train's lock, and each worker that reached
one waited, so every car on that worker starved too (up to 3,224 starved blocks a second). A train
renders without a lock now (docs/TRAINS.md, "Voicing a train"), and a slow voice holds up only itself.

A voice let go fades on its envelope, and is released only once the mixer has played the fade. The
fade is rendered with the rest of the ring, up to 0.7 s ahead; released when it was rendered, as it was
until 2026-10-07, the voice was stopped at full level and the fade never heard. That was the fountain
and the crossing bell "cutting out" each time the budget gave them up.

The workers treat subnormal numbers as zero (FlushToZero, 2026-10-16), as FMOD's mixer thread already did.
A bell or a resonator ringing down to silence sits on them, and an Intel processor works on them about a
hundred times slower; a voice that did would starve the others. Nothing a float sample can hold is that
small, so no sound changes (docs/AUDIO_QUALITY_2026-10-06.md, row 14).

An engine heard 15 dB or more under the loudest machine runs at reduced detail (EngineDetail, 2026-10-09;
FmodAudioProvider.ChooseEngineDetail), and goes back to full within 12 dB. Reduced is a twin of the engine
at half the rate, interpolated back up (EngineSynth.Detail.cs); the voice's tyres, fan and body stay at
the full rate. A change is a hand-over, never a cut: the new engine takes the old one's state, runs beside
it for 0.15 s with its crank held to the old one's while its pipes fill, and the two are crossfaded at a
level that keeps their measured power. A reduced voice costs about a third less, and while it runs
steadily it replays its own last six cycles instead of integrating them (CycleCache):
taken up again at the same crank angle when an input moves, and every 4 s regardless. The engine you ride in
is always full; /enginedetail off keeps every engine full.

## The budgets: giving voices up and taking them back

The engine, machine, place and reflection budgets (ClientAudioSystem.ChooseLiveEngines) give a voice
up when the mixer stays over 70 % for 0.75 s, when the render pool starves, and when fewer than six
binaural voices are free; they take one back when the mixer has stayed 8 points under its ceiling for
3 s, one step at a time. A voice costs the mixer about 0.3 % (Cody's city: 60 voices at 60 %, 200 at
100 %). Most of the mixer is not voices: in `--train-scene cars=24` (31 voices, the mixer at about
30 %) the six traced-echo rigs took 12 points, the binaural stages of all 31 voices 5, FMOD's own units
and mixing 6. The "Mixer time" line (MixerProfile) says it for every second of a session.

Until 2026-10-07 a voice was taken back only under 45 %, which the city's mixer never reached, so
whatever was given up in the first thirty seconds after the map loaded (while its bake and decodes
held the mixer at 74-108 %) stayed given up: the machines went from ten to one, the fountain, the
crossing bell, the trees and every train shared that one voice for the hour, and sixteen cars stayed
out of budget. Now the budget is held thirty seconds after a load, and one voice taken back and given
up again within 15 s makes the next restore wait twice as long, up to four minutes, so the budget
settles rather than churning at the ceiling. The machine budget counts voices (24), not things: a
fountain is five voices and a train up to six.

A voice the binaural pool has no stage for is not started (FmodAudioProvider.PlaySpatialSound),
rather than played flat in the middle of the head; the budgets see it not playing and give voices up.

## Why an echo is smeared

A reflection read straight out of the source's ring is the source's own waveform, sample for sample,
a few milliseconds late, and a signal added to a delayed copy of itself is a comb filter: evenly spaced
notches that sweep as either end moves. That phasing makes a source sound inside out or like a narrow
beam, and it is not what a wall does. A real wall hands the sound back from a patch a few metres across
(the Fresnel zone), every part of it a slightly different distance away, and what faces the wall is not
what faces you (the tailpipe points one way, the intake another). So the copy that comes back is the
same sound but not the same waveform, and its notches, if any, fall at no regular spacing.

EngineEchoState models this with EchoDiffuser, a short cascade of Schroeder all-passes: flat in level,
so the echo is exactly as loud as the image-source method says, with a phase that wanders with
frequency, spread over a time that grows with the roughness (about a millisecond for polished steel or
glass, up to twenty or so for a brick facade or a crowd). The delays differ for every voice so no two
walls smear alike. The arrival time, and so the direction and the slapback, are untouched. A borrowed
voice (a distant car voiced from a near car's ring) is a different car, not an echo, and is not smeared.

The borrowed voice keeps its own read cursor for a related reason: it is placed at its own position and
pitched by its own Doppler, so reading relative to the source's play position (which moves at the
source's Doppler) gave it two Dopplers belonging to two cars going different ways: a car at the redline
that sounded like it was cruising, worse the more of the field was borrowing.

## An impact is not one resonance

TransientSynth.RenderKnock. A single pole struck by an impulse is a cork coming out of a bottle, and
that is what listeners called it, twice, about two different sounds: a door shutting and a car hitting
a wall. Both were one resonance. A struck object answers on many modes at once, inharmonically spaced
(a plate or a panel or a car wing is not a string), and the high ones die first because they radiate
faster. That spread is the difference between "something was struck" and "a note was played", and no
moving of the one note produces it.

The contact burst under the modes is what says two things touched. Unfiltered it is flat to 20 kHz,
and a 1,500 kg car meeting a wall came out with a third of its energy above 4 kHz: a tiny hard tap laid
over a low crunch. Its cutoff is tied to the sound's own pitch (six times it), so a latch stays bright
and a crash is low and gravelly.

## A breath is turbulence

TransientSynth.RenderHiss was noise through one resonator at Q 0.9 with an attack a twelfth of its
length. Measured, an exhale peaked at 250-1000 Hz with 2-4 kHz 23 dB down and 4-8 kHz 32 dB down, and the
top is where a breath lives. A sharp attack on a 300 ms noise burst is a transient, the difference
between a breath and a soft bang. Cody reported it over six sessions: "random banging... it is 2
different bangs so it makes me think it's breathing in and out... I don't hear the breathing either".

Turbulence through a narrow opening radiates over decades, rolling off gently either side of a centre
set by the aperture and the flow. So the noise is shaped by a wide band (two poles down at the top, one
up at the bottom) around `hz` rather than resonated at it, which keeps the 2-6 kHz that carries the
character. An inhale is drawn through a narrower opening, so its `hz` is higher and the band moves up.
And it swells: a third of its length rising on a raised cosine, no corner for the ear to hear as an
onset. That change alone is most of the difference between air and a knock.

## Air absorption: what ISO 9613-1 replaced

AudioPhysics.AirAttenuationDbPerMetre is ISO 9613-1 in closed form, with no fixed coefficients. It
replaced a straight-line "muffle" that reached its limit at about 135 m and took 32 dB off the high band
and 16 off the mid there, where the standard says about 14 and under 1: six times too much, so a hot rod a
block away arrived as a dull rumble with its crackle gone. It also replaced a fixed-coefficient version
that added an "urban excess" of up to 9 dB per 100 m for obstacles the game already models on their own
(walls, vehicles, diffraction), and that halved the loss indoors. Air is air indoors too.

## The near-boundary reflection

BoundaryModel turns "a wall half a metre to my left" into the reflection that makes it audible. A
nearby surface returns a delayed copy of everything you hear, and direct plus delayed is a comb filter:
the extra path is twice the distance, so the delay is 2d/c and the notches sit at odd multiples of
c/4d. Half a metre away that is a first notch near 170 Hz and peaks every 340 Hz, the "boxy" colour of
walking along a corridor wall. Closer, the pattern slides up; further, it slides down and fades. The
sliding is the cue that tells a player how far they are from a wall, which is why this cannot be a
fixed-delay effect.

It replaced a fixed-ish 0.1-1.2 ms FMOD echo with 45 % feedback: the wrong delay (2d/c at 1.5 m is
8.7 ms, seven times longer), the wrong topology (feedback makes a resonator ringing on one pitch, not a
single reflection tracking the geometry), and every direction collapsed into one scalar, so a ceiling
and a wall behind you sounded the same.

A copy is only as whole as the surface round the point the probe met (`BoundaryProbe.Share`). A wall gives
a whole copy when it covers about the first Fresnel zone at 1 kHz round that point, a radius of
sqrt(c/1000 x d/2): 0.29 m at half a metre, 0.40 m at one. Each probe that meets something (not the floor,
which makes no copy) looks again at eight points on the plane of the surface it met, half and a whole radius
each way on two axes across it, and per axis the copy keeps (1 + the looks that landed) / 5. A look lands
when it stops no more than 10 cm behind that plane; nearer counts, since a corner reflects too. The looks
are on the surface's plane, not across the probe's line, because the probes turn with the head: judged
across the line, a flat wall met at 30 degrees came out a fifth of one. A wall of any number of pieces
stays whole; a door's jamb a foot deep, met between the ears, is a fifth of a wall; a lamp post is little.
Found in Resonance's copy (a shop's open doorway, 2026-10-14). The six probes and their looks cost 0.01 to
0.03 ms a frame (`--near-walls`).

The set's budget (`MaxBoundaryReflectionSum`) is taken on the surfaces as whole walls, and each copy then
pays its share. Taken on the shared copies, a doorway's jamb and head fell under the budget, the trim let go,
and the wall that stayed whole beside them came out 1.4 dB louder than before (Marlow Tower flat 00B's door).
Measured with `--near-walls` on the city: in Marlow Tower's front entrance (door open) the copies were 4.4
to 5.7 dB over the lobby's and are now 2.4 dB under to 2.9 dB over; in flat 00B's doorway 4.8 dB over,
now 0.4 to 3.7 dB over. At 3,837 room standing spots and headings, 3,820 are unchanged; the other 17 are
on pavements beside something narrow, 2.4 to 6.4 dB down.

## Gain staging and the master makeup

Every source is rendered at its true level relative to every other: Loudness.Place turns a source's dB SPL
at a metre into a gain and a reference distance, and the engine attenuates with 1/r. That must not be
fiddled with per sound: it is how a listener tells a rifle at two hundred metres from a pistol at twenty.

The cost is that the mix sits low, by design: the law plays a sound as loud as it is at a playback where
0 dBFS is 100.8 dB SPL (Loudness.DesignFullScaleDb), so a normal voice a metre away (62.35 dB) is about
-38 dBFS RMS and an outdoor scene spends another 30 dB on distance. Nothing is lost on the way: a
synthesised voice's 16 dB of headroom over its RMS is given back by the law in loudness units
(Timbre.DigitalRmsDb), the HRTF is level over the sphere (+-0.4 dB; 1.4 dB down straight ahead and up at
the sides, as a head is), and the binaural stage takes each voice at its own level (SteamAudioDsp).

That range is taken back once, on the master, for everything: not by making sounds louder (which
destroys the relative levels) and not per map (nobody knows what a map will carry). The makeup lifts the
whole mix and the look-ahead true-peak limiter catches what goes over, so adding a hundred sources
changes what you hear and never how loud the master is.

The makeup is six dB: the loudness chosen by meter and by ear in September 2026 (ten for the speedway at
-19 to -23 LUFS short-term; three back for the ground's energy; a two-decibel trim for the street), less
the 3.01 dB the binaural stage was losing on every voice and no longer does. Every voice through the HRTF
plays as loud as before; the wind at the ears and the interface, which never passed through it, play
3 dB quieter, level with the voices again. A player who calibrates (ListeningCalibration) sets their
volume 6 dB lower, so 0 dBFS at the output is 94.8 dB SPL.

## Traced reverb everywhere

Reverb is not an effect added on top: it falls out of the geometry. Every reverb bus gets a traced stage
right after its SFXREVERB unit. The SFXREVERB passes its input through dry and is kept only as the point
the stage is inserted at; the stage convolves its input with a traced impulse response
(SteamAudio.TracedReverb): the listener's own, traced from where they stand, for the room they are in;
each other audible room's own, traced from its middle, so a sound through a doorway rings with the room
it is in. The materials of every surface are in the trace, so carpet, concrete and tile tell themselves
apart, and the reverb is the late part of what comes back. There is no parametric room algorithm: no
SFXREVERB tail, no Sabine or enclosure estimate, no wet-level loop, no room-equation send.

One rule for every place: a one-off sound's first 80 ms are placed voices mirrored through the surfaces
round it (WorldAudioPlayer.QueueEarlyEchoes and the steps' SubmitRoomStepEchoes); the listener's traced
stage plays only the late tail (TailOnly, as a diffuse field); and every reflected path (placed copies,
tails, traced echoes) sits at one trim against the direct sound (TailDb and CopiesDb). Nothing decides by
"indoors" except where physics does: past the window a room's copies are dense and are the tail; a
street's are sparse and stay separate events (QueueHigherOrderEchoes).

Traced echoes: mirror-image echoes of a moving source jump from facade to facade as it passes, so the
loudest few sustained sources at the ear are traced from their own positions (TracedEchoes), every order
of reflection, crossfaded as they move. They are chosen by the level they render at, which a passing car
reaches as it comes close and loses as it goes, so only sources loud enough to be heard reflecting pay
for a trace.

The late field: how much of the listener's tail each source raises is traced from where the source is, for
the loudest sixteen in the listener's place (LateField), and the place's distance law is fitted to those
answers for every source not traced. Both are the place's they were traced in: each answer carries it, and
is used only while the listener stands there (FmodAudioProvider.SpeaksFor). In a small room the law is 0,
a far source as loud as a near one; taken through the doorway into the open air it sent every car on the
road into the tail at full until the first trace from outside (10 to 13 dB over the mix, measured in
Resonance's copy on a busy highway). Until a place is traced the send is the stand-in, distance raised to
the enclosure.

## The reflections trim

Every trimmed path is a copy of the source arriving a few tens of milliseconds late. What the ear does
with those in life (fuse them, suppress them) it does less of through a generic HRTF in headphones, where
a copy at its physical level is heard as an event. The room's identity (its decay, its colour, where its
walls are) survives the trim; only its weight against the direct sound is set for the listener.

Two numbers, because the two kinds are heard differently. TailDb is everything traced: the listener's and
other rooms' stages and the far sources' traced echoes, convolved through the traced response and so
already reflections, not copies (`/tail <dB>`, OPENFPS_TAIL_DB). CopiesDb is everything placed as a copy
of the source: early echoes, facade and higher-order echoes, your own steps' echoes, the master-bus
boundary copies (`/copies <dB>`, OPENFPS_COPIES_DB). `/reflections <dB>` sets both. Zero is the traced and
image-source level, physical to within a couple of decibels wherever measured (--clap-room,
--traced-reverb).

Both are -6, set by ear in a flat, a tunnel and a street (settled 2026-09-30). The tail was then 3 dB
under that: the traced stage averaged its bus's two channels, and FMOD puts a mono send into a stereo bus
at -3.01 dB a channel. Summed at constant power since 2026-10-07 (TracedReverbDsp.DownmixGain), so -6
now means what it says and is 3 dB wetter than what was approved; Cody to judge. A trim further down was
hiding faults, not setting a level: with the tail parametric it was 14-20 dB too loud in the tunnel; with
the tail spread evenly it was "centralised"; with sample-identical copies the ear heard separate events.
If the tail sounds like a wash at a level near 0, look for something non-physical before trimming. One is
known: the trace rings as long at 4 kHz as at 250 Hz (0.79 s, where Sabine from the same materials says
0.52), so the top hangs on.

## Screen reader keys are not game keys

No gameplay action may be bound to a key a screen reader owns. Control is how a screen reader user
silences speech (every reader stops talking when it is pressed) and Alt belongs to the window manager; a
blind player presses both dozens of times a minute as punctuation, so a game action on one of them is a
key that fires by itself. Control was the trigger. The cost is on record: five sessions chasing a report
of "random banging... bang, wait a few seconds, bang, like someone closing a cabinet, I have no clue what
the noise is", through the reverb model, the room equation, the movement engine and the reflection
machinery, and the answer was twenty-six rifle shots at 159 dB the player had fired himself by shutting
his screen reader up. It was found in an audio trace, not by reading the bindings, so ScreenReaderKeyTests
asserts the bindings.
