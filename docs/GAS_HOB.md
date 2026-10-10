# The gas hob

Cody, 2026-10-10: "New model, gas stove, tick tick tick tick whoosh ... let me hear a good physical model
of this, realistic."

A domestic gas hob as a physical model: the knob and its valve, the gas through the injector and the mixing
tube, the spark module, the light-up, the flames, the flame safety, and turning it off. Nothing is a
recording. Recordings of real hobs were used only as the measure (section 9).

Code:
- `OpenFPS.Sound/GasHob.cs`: the gases, the burners, the hob, its presets, the knob's valve law, the
  state key (`HobKey`) and what the interact key does (`HobControls`).
- `OpenFPS.Sound/Core/Stove/GasHobSynth.cs`: the hob, pascals at a metre.
- `OpenFPS.Client.Core/AudioEngine/Fmod/StoveVoiceState.cs`: the hob as a voice.
- Prefabs `gas_hob` (four burners, natural gas, no flame safety), `gas_hob_flame_safety`,
  `gas_hob_reignition`, `gas_hob_propane`, `gas_burner` (one burner). Presets `hob4`, `hob4_ffd`,
  `hob4_reignite`, `hob4_propane`, `hob1`.
- AudioLab `--stove levels|render|game`.

Round 2 (2026-10-10, after Cody's first listen) is section 11: the cook now lets go as soon as the flame
catches, the spark was corrected to its anchor (12 dB quieter in 1-4 kHz), and the kitchen in the renders
was furnished.

Tags on facts, as in docs/FIRE.md: **[ft]** read in the full text, **[sec]** read in a secondary source,
**[recalled]** general knowledge not checked here, **[estimate]** a judgement made here, **[fitted]** set
by measuring the recordings in section 9.

## 1. One law for most of it

A flame, a spark and a light-up are all heat released unsteadily. Heat put into air expands it, so a
region whose heat release Q changes is a monopole:

    p(r, t) = (γ - 1) / (4π r c²) · dQ/dt

the law docs/FIRE.md 1.1 gives for flames (Dowling and Mahmoudi 2015 [ft]). At a metre that is
2.7 × 10⁻⁷ Pa for every watt per second of change. The model uses it for:
- each spark's crack (a millijoule or two in tens of microseconds);
- the light-up's thump (the gathered gas burning in a tenth of a second);
- the flames' roar (their heat release wavering);
- the pop when a flame goes out (its last few hundred watts quenched in a few milliseconds).

The other two sources are the gas jets' mixing noise (Lighthill) and the parts of the mechanism being
struck.

## 2. The knob and the valve

- A plug cock. Off at 0°, full at 90° anticlockwise (the big flame mark, where a hob is lit), on round to
  the small flame mark at 240°, where only the bypass screw passes gas [recalled: the usual European
  hob].
- The port in the plug opens between 20° and 80° (`GasHobSpec.FlowShare`). Past full a tapered groove
  closes it down to the bypass. Medium (165°) is half the gas.
- The knob must be pushed in to turn from off. Pushing it does two things:
  - on a hob with flame safety (section 7), it holds the safety valve open by hand;
  - at the bottom of its travel it closes the ignition switch (a snap-action microswitch), which runs
    the spark module.
- Sounds, all struck banks of four modes, levels at a metre [estimate: small switches, plastic on steel]:
  - the switch snapping over, 56 dB peak (and again, 3 dB softer, when the knob comes back out);
  - the knob dropping into its detent at full, 52 dB;
  - the stops at off and at the small flame, 58 dB;
  - the spindle springing back when the knob is let go, 50 dB.
- Turning a burner off from low passes the knob back through full. The flame flares for a moment, as a
  real one does.

## 3. The gas

- Natural gas G20 at 20 mbar (2.0 kPa); propane G31 at 37 mbar (EN 437 normal pressures [recalled]).
  Densities 0.68 and 1.90 kg/m³; net heating values 34.02 and 88.00 MJ/m³; gross 37.78 and 101.74
  [recalled].
- The injector's jet: U = Cd √(2ΔP/ρ). Cd = 0.80 reproduces the manufacturers' flows exactly:
  - the Whirlpool AKT 300 manual's 286 l/h through the 1.28 mm rapid injector, 3.00 kW gross [ft];
  - 157 l/h through 0.95 mm, 1.65 kW [ft];
  - the model gives 284 and 157 l/h. The small burner's 0.72 mm (IKEA HGA4K, 1.0 kW [ft]) gives 90 l/h,
    0.94 kW.
  - So natural gas leaves the injector at 61 m/s. At a share F of full flow the injector takes F² of the
    pressure and the valve the rest, so the injector's jet is F times as fast.
  - Propane injectors are about two thirds the bore (CDA HCG301's set: 0.65-0.95 mm against 0.97-1.35
    [ft]); the model's 0.87, 0.65 and 0.49 mm give the same heat. The jets leave at 50 m/s.
- The hiss: Lighthill's eighth power with K = 1e-4, peaking at Strouhal 0.2 on the bore: 28 dB at a
  metre for the large burner on full, peaking at 9.6 kHz; 17 kHz for the small one. Textbook, no trim
  (`JetTrimDb` 0). The valve's own throttling jet, where it takes the pressure at lower settings, is
  rendered too, 10 dB down through the hob's case [estimate]; it peaks above 20 kHz and is barely
  heard. The hiss falls with the eighth power of the flow: on low it is gone.
- The jet draws air into the mixing tube: half what burning needs (`PrimaryAeration` 0.5; 40-70 % for
  a cooker burner [recalled]). The mixture is 17.4 % gas, richer than methane's 15 % upper limit, so it
  burns only where it meets the room's air: a partly premixed flame.
- The first gas has to sweep the air out of the tube and head (12-35 cm³ [estimate]): about 30 ms on
  full. The mixture leaving the ports follows the jet with a 30 ms lag [estimate].
- The ports: a port loading near 9 W/mm² [estimate], 333 mm² for the large burner, 1.4 mm bores
  (smaller than methane's 2 mm quenching distance, so a flame cannot run back into the head
  [recalled]). The mixture leaves at 1.3-1.4 m/s on full, cold.

## 4. The spark module

- One module sparks every electrode at once [recalled: four-outlet modules; every electrode on a hob
  ticks when one knob is pushed]. Two ways of switching it (`Module`):
  - while a knob is held in (`WhileHeld`, the default): the switch at the bottom of the knob's travel. It
    stops the moment the knob is let go. European hobs, and the "Lite" position of a North American
    range's knob.
  - auto re-ignition (`Reignition`, preset `hob4_reignite`): its switches close at any on position of a
    knob, and it sparks until it senses the flame on that burner by rectification. A flame passes
    current one way between the electrode and the grounded burner, so the module sees the AC it puts on
    the electrode rectified, and holds its firing capacitor down; if the flame goes out it sparks again
    (US patent 5,169,303 [ft, its description]; parts suppliers' and service forums' accounts [sec]).
    No timing is published; the model takes the flame as seen once it has been on the electrode
    0.1 s (`FlameSenseSeconds` [estimate: a few mains cycles of the sense filter]). Higher-end North
    American ranges have these [sec].
- Rate: 3-5 sparks a second on a working hob [sec: cookerspareparts.com]; 3.2-5.6 in the recordings
  (section 9). A mains module charges a capacitor a step each mains cycle and fires it through a
  breakover device, so the rate is the mains over a whole number of cycles: 50 Hz over 12, 4.17 a second.
  The threshold is set half way between two cycles' charges, with 1 % noise on it, so a slip of one cycle
  is rare. Measured: intervals 220-260 ms, CV 2.7-3.4 % (the clean recordings: 0.4-3 %).
- Each spark's energy at its burner: more than 15 mJ (a Robertshaw re-ignition module [sec]). It goes as
  the square of the breakover voltage, so it varies a little spark to spark. It decides what the spark can
  light (section 5).
- The crack. A spark heats the air in its gap in microseconds: a monopole by the law in section 1.
  - The anchor: a piezo lighter's 3 mm spark, 250 Pa at 12 cm, an N-wave of 2.2 µs half-duration,
    falling as r^-1.6 out to a metre, spark to spark within 5 % (Scheuer and DeCorby 2024 [ft]). Its
    low-frequency content (the N-wave's first moment, (2/3) Ps T², carried out to a metre as 1/r) is what
    0.36 mJ of heat gives by the law.
  - A hob's spark is taken as the same, 0.36 mJ (`SparkHeatMj`): the shock comes from the breakdown,
    the gap's own capacitance emptying (about 1 mJ for 15 pF at 12 kV [estimate]), and the gaps are
    alike; the rest of a mains spark's 15 mJ goes in slowly, in its arc, and makes no crack. Measured
    against the recordings octave by octave (section 9) this is within 2 dB of their median on average
    over 1-8 kHz. It was 1.5 mJ, "four times the lighter" [estimate], until round 2: 12 dB too loud.
  - Its heat goes in as E t/τ² e^(-t/τ) with τ = 3 µs (`SparkMicroseconds`): the lighter's N-wave,
    2.2 µs a half at 12 cm, stretched a little on its way out to a metre [estimate]. Its spectrum
    peaks at 1/(2πτ) = 53 kHz, so through the whole of hearing the crack rises 6 dB an octave: a
    differentiated impulse. It was 30 µs, fitted to the recordings' third-octave peak at 6.3-10 kHz; that
    peak is where their microphones and the MP3 previews roll off, not the spark's. The 30 µs crack
    peaked at 5.3 kHz and was only 3 dB down at 2 kHz: it put its energy in the presence region.
  - Rendered from band-limited kernels (a windowed sinc at 0.45 of the rate), three durations (τ and
    15 % either side, as the spark's path along the cap's edge differs) and sixteen sub-sample phases.
  - The hob's steel top gives back each crack from its image 18 mm below (`SparkHeightMm`), and each
    electrode's crack reaches you by its own path. Four electrodes across 22-30 cm comb the tick at
    about 1.6 kHz spacing, as the recordings do.
  - Measured at a metre: 84-90 dB peak, sound exposure 46 dB. 43 dB over the large burner's roar on
    full; the recordings' median is about 40.
- The cap: a free disc of enamelled steel. Its first four modes from plate theory (λ² 5.253, 9.084, 12.23,
  20.52 for a free circular plate [recalled]), 2.1-8 kHz for these caps, stopped in 40 ms where it sits on
  the crown [estimate]. Nothing strikes it but the spark's blast. Integrating the blast's impulse over the
  cap's face near the spark (about 0.015 Pa·s at 3 mm falling as 1/r, out to 2 cm) gives about 5 µN·s; on
  a 160 g disc (modal mass about a quarter of it) that is 0.1 mm/s, which a disc of 85 mm radiates at
  about 0 dB at a metre even at a radiation efficiency of one: some 80 dB under the crack. `CapRingDb` is
  -80. It was -24 dB [estimate, read from the recorded ticks' tails, which were their rooms']; its ring
  added 2-8 kHz to every tick for 40 ms.
- The module's own tick (its pulse transformer), 52 dB peak at a metre in the open [estimate]. The module
  sits under the hob behind its steel tray, so it is heard through the tray's gaps, 15 dB down
  (`ModuleCaseLossDb` [estimate]), and above 1 kHz falling 6 dB an octave as a mass law does. It was
  heard unmuffled until round 2; it was 27 dB under the crack by energy then and is about 45 dB under now.
- What else could make the tick "present", checked:
  - The line from the cook's ear to the sparks. Standing at the hob the ear is 40-50° above the hob top.
    An electrode on the near side of its cap is in plain view; one on the far side is seen grazing the
    cap's rim, a few dB down above a few kHz [estimate]. Which side the electrodes are on differs by
    maker, so it is not modelled; the octave comparison in section 9 does not call for it.
  - The hob top's reflection (`SparkHeightMm`, 18 mm): the image doubles the crack below a few kHz and
    notches it where the path difference is half a wavelength, 10.7 kHz at the lab's 27° and about
    6.5 kHz at the cook's 45°. It is the reason the model's 2 kHz octave still sits 6 dB over the
    recordings' median against 8 kHz (a single burner shows the same, so it is not the four
    electrodes). The image is kept: it is physics, but its coherence is idealised (the burner body
    breaks the plane under the spark), the first thing to look at if the tick is still too present.

## 5. Lighting

- A spark lights a burner when the richest mixture it crosses needs no more energy than it has.
  - The channel crosses the mixing layer at the edge of a port's jet, which holds every concentration
    between nothing and the richest that reaches the electrode. Past stoichiometric it finds the easiest
    mixture: methane's least ignition energy, 0.29 mJ [sec: combustion handbook table].
  - Short of it, the energy rises steeply to the lean limit (5 % for methane, 2.1 % for propane
    [sec, recalled]); fifty times the least at half stoichiometric [estimate, the shape of the
    published curves]. So a 15 mJ spark lights down to about the lean limit.
- Two things carry gas to the electrode:
  - the ports' jets. They reach it as u²/(u² + 0.6²) with u the port velocity [estimate]: most of the way
    on full (1.37 m/s), hardly at all on a trickle. This is why a hob is lit on full: barely opened it
    never lights (`GasHobTests.AKnobBarelyOpenNeverLights`); turned to low it passes full on the way and
    lights there.
  - the gas gathered round the burner: what has left the ports unlit, lingering 1.5 s (natural gas
    rises away) or 4 s (propane pools in the burner well) [estimate], in a layer 3 cm deep over the crown
    and a hand's width round it.
- So whether a spark fails is the gas's doing, not chance:
  - Turned straight to full: the first spark comes 240 ms after the knob goes in, while the knob is still
    turning, and usually fails; the next lights. About a quarter of a second of gas, 240 J of it gathered
    for the large burner.
  - Turned only a third of the way (36°) and held there: the jets do not reach, the gathered gas stays
    under the lean limit, and thirteen sparks fail while 18 cm³ of gas gathers. Turned on to full, it
    lights and burns 618 J at once.
- The light-up:
  - the flame runs round the ring of ports both ways from the electrode at the burning velocity times the
    burnt gas's expansion: 0.37 m/s × 7.4 = 2.7 m/s for methane [sec: Mitu et al. 2022, 0.353-0.375 m/s;
    expansion recalled], 49 ms round the large burner, 26 ms round the small one. As it goes it burns
    the gas in the ports' mixing layers (the last 50 ms of flow) and lights the ring;
  - the leaner gas gathered round the burner burns after it at the lean mixture's own pace, at about
    the lean limit where it is leaner than that on average [estimate: it is stratified, richest by the
    ports]: about 0.3 m/s through a cloud 4-6 cm across, 120-180 ms;
  - each part's heat release is one smooth pulse; its dQ/dt is the thump;
  - its front is a turbulent premixed flame, with acoustic power 1e-8 of its heat release: the low end
    of the 1e-8 to 1e-7 measured on small turbulent premixed burners (Shivashankara 1973 [sec:
    docs/FIRE.md 1.4]). That is the whoosh. It fits the recordings without a trim (section 9).

## 6. The flames

- Heat release: the gas reaching the ports times the net heating value, through the flame's own lag
  (two poles at 25 Hz [estimate]), times how much of the ring is burning. The large burner is 2.69 kW net
  on full.
- The roar. The steady flames are laminar flamelets wrinkled only by the mixing tube's turbulence.
  - Acoustic power η (u/1.5 m/s)² Q, with u the port velocity: the square of the stirring.
  - η = 1.1e-10 [fitted: the large burner's roar on full 40 dB under its sparks' peaks, the recordings'
    median], a hundred times under small turbulent premixed burners, as laminar flames should be.
  - Spectrum: the turbulent combustion tail, power falling as f^-2.2 above a peak (2.1-3.4 measured on
    turbulent flames, docs/FIRE.md 1.3; 2.2 fitted to the recordings' median slope of -3.5 dB an octave
    from 250 Hz to 2 kHz).
  - The peak is the time the mixture takes through the flames, port velocity over flame height
    (12 mm [estimate]): 110 Hz. A diffusion flame's height goes with its flow, so the peak does not
    move with the knob.
  - Wavering with the room's air: 15 % at 8 Hz [estimate].
- Levels at a metre, measured (`--stove levels`):

| | low | medium | full |
|---|---|---|---|
| large burner | 22.4 dB | 34.0 dB | 43.3 dB (38.1 dB(A)) |
| every burner on full (four-burner hob) | | | 47.0 dB (41.4 dB(A)) |
| propane hob, every burner on full | | | 45.3 dB |
| single middle burner | 19.9 dB | | 40.1 dB |

- So low is barely there, as a simmering hob is: the roar falls with the cube of the flow (power) and the
  hiss with its eighth power.

## 7. Flame safety, and turning off

- A thermocouple in the flame drives a magnet that holds the gas valve open once the knob is let go
  (EN 30-1-1 allows a hob up to 10 s to open [recalled]). Heating 2.5 s, cooling 15 s [estimate]. It
  holds at 55 % of full voltage and lets go under 35 %.
- Only a hob with flame safety has this (`FlameSafety`, preset `hob4_ffd`, prefab
  `gas_hob_flame_safety`). European hobs have it on every burner (EN 30-1-1 [recalled]); the top
  burners of North American ranges do not [recalled], nor do older or cheaper hobs elsewhere. The
  default `hob4` has none: the plug valve alone decides the gas.
- The cook's hand. Everyone holds the knob in until the flame catches (the sparks are what light it).
  - Without flame safety nothing needs it held after that, so the cook lets go as soon as they see the
    flame: `ReleaseAfterLightSeconds` 0.6 s, drawn each light between 0.36 and 0.84 s [estimate: a visual
    reaction is about 0.25 s, and letting go]. The module stops with the knob: 1-3 sparks after the
    flame catches, the last 0.5-0.8 s after.
  - With flame safety the knob must be held until the thermocouple can hold the valve, and the module
    ticks on through the flame until then, as it does on a real European hob. The thermocouple heats as
    a first-order lag of 3.75 s (`ThermocoupleHeatSeconds`) and holds at 55 % of its voltage, 3.0 s after
    the flame catches; the cook, who knows the hob, holds 1 s more (`HoldMarginSeconds`): 4 s, which is
    what Bosch's manuals ask [sec: Bosch's instructions and FAQ, "hold for 4 seconds after lighting"].
    Others ask for more: about 8 s (Miele), at least 10 (AEG, Zanussi, Electrolux) [sec: their support
    pages]; Smeg "a few seconds" and longer if it goes out. EN 30-1-1 allows a hob up to 10 s
    [recalled]. So 3-10 s of ticks after the flame on these hobs is real; 4 s is the short end.
  - With a re-ignition module the hand does the same as without flame safety, but the module has already
    stopped by itself, 0.1 s after the flame reached its electrode.
  - Let go too soon on a flame safety hob, the magnet cannot hold and the valve snaps shut.
- Turning off: the plug closes as the knob turns, the injector stops, the head drains with the tube's
  lag. When the mixture leaves the ports slower than about a third of the burning velocity the flame
  sinks into them and is quenched [estimate] in the port bore over the burning velocity (4 ms). Its last
  200 W or so go in that time: a soft pop, 66 dB peak at a metre for the large burner (61 dB for the
  single burner, 69 dB on propane). Then the knob's stop.
- About 16 s later, on a hob with flame safety, the thermocouple has cooled and the armature drops: a
  faint click, 44 dB peak, as on real hobs a quarter of a minute after they are turned off.

## 8. In the game

- The prefab is one entity with one voice. Its sound key is its state: `stove:hob4/0000>3000@<time>`,
  one digit a burner (0 off, 1 low, 2 medium, 3 high), before and after the last change, and when on
  the shared clock (`WindField.Now`). The server keeps nothing else.
- The interact key at a hob (within 1.3 m, as a tap): with a burner off, it lights the next one (front
  left, front right, back left, back right) on full, "You light the front left burner."; with every
  burner lit, it turns them all off, one knob after another, "You turn every burner off." The prefab
  `gas_hob` is the default `hob4`: the cook lets go as soon as it catches.
- Each client plays the cook's hand from the key: push, turn, the ticks, the light-up, the hold, the
  release, or the turn back to off. A change heard within a second of being made plays from its start;
  a voice made later runs what it missed silently, so a player arriving at a lit hob hears it burning.
  Every client hears the same light-up: whether a spark fails is the gas's doing, and the module's
  jitter is seeded.
- A hob with every burner off is given no voice 40 s after its last change (`HobKey.QuietAfterSeconds`).
- The room is the game's. Through the game (`--stove game`) in a 3.6 × 3.2 × 2.6 m kitchen, standing at
  the hob with the ear a metre from it, at the default /levels: the sparks peak at -1.2 dBFS, nothing
  clips. The light scene is -31.7 LUFS (round 1, -28.1, with 16 sparks and the old crack).
- The kitchen in the lab. Round 1's was a bare box: tiled floor and back wall, plaster elsewhere, the
  base units as one block. Its Sabine time is 1.0 s, and measured in the render, from the decay after
  the sparks at 8 kHz (the band clear of the flame), 0.93 s. Fifty measured kitchens averaged 0.68 s at
  1 kHz (Jackson and Leventhall, "A proposed method for assessing the noise of domestic appliances",
  British Acoustical Society, 1973 [sec: a 2021 review in Applied Sciences 11(6) 2709]); furnished rooms
  under 55 m³ in Poland measured under 0.5 s (Nowicka et al. [sec, the same review]). So round 2's
  kitchen has its units along two walls, wall cupboards and a hood over the hob, and a table: by Sabine
  0.76 s, and the game's own ray-traced RT60 for it is 0.67 s (logged), measured 0.68 s at 8 kHz.
- That was the lab's room, not the game's room model: the game gave the bare box what its surfaces
  say. Nor is the game wetter than a real kitchen. Direct to reverberant at 1 m, measured from the
  sparks (the first 7 ms against the rest of each period, the flame taken out): +1.3 to +5 dB at
  2-8 kHz in the bare box, +1.5 to +7 in the furnished one. A real kitchen of 30 m³ at 0.68 s has a
  critical distance of 0.057 √(Q V / T) = 0.54 m for a source on the worktop (Q = 2), so -5 dB at a
  metre: the game is 6-10 dB drier, as the reflections' -6 dB trim settled by ear in 2026-09 makes it.
  Nothing was changed in the game's room acoustics.
- Cost: a whole hob alight renders at about 1-2 % of a core (`--stove levels` prints it per scene).

## 9. Fitting

No recording of a hob was in inbox/. Fifteen Freesound previews of hobs being lit were measured as the
measure only, never played: 194709, 765145, 751151, 714949, 390159, 390160, 539875, 251814, 740092,
256510, 240077, 697229, 249776, 426225, 254307 (freesound.org/s/<id>). Uncalibrated, so only
ratios and shapes were used. Measured with the same scripts on the recordings and on the model (the
table is round 1's; round 2's spark is below it):

| | recordings | model |
|---|---|---|
| sparks a second | 3.2-5.6 | 4.17 |
| interval CV, clean recordings | 0.4-3 % | 2.7-3.4 % |
| tick third-octave peak | 6.3-10 kHz | 5-6.3 kHz, falling to a notch at 10.6 kHz from the hob's image |
| tick peak over the steady flame | 20-56 dB, median 29.7 (round 1 read it as about 40) | 39-43 dB |
| steady flame, octaves re 1 kHz, 125 / 250 / 500 / 2k / 4k | median +8.6 / +7.8 / +3.7 / -3.2 / -0.1 | +4.7 / +5.6 / +3.2 / -3.4 / -6.4 |
| light-up's first 150 ms over the steady flame, 31-125 Hz / 125-2000 Hz | median about +16 / +17 dB | +15 / +12 (through the game), +21 / +23 for the slow light |
| light-up length, 125-2000 Hz over steady +3 dB | 60-480 ms, median about 220 | 140-160 ms; 210-360 ms for the slow light |

The recordings' 4 kHz octave varies by 25 dB between hobs (how much hiss each injector makes); the model
sits at the quieter end with the textbook jet law. Their 63 Hz octave is mostly the rooms and the
handling.

The spark, round 2. A tick's broadband peak depends on how much of the spark's ultrasound a microphone
and an MP3 keep, so it was measured again by octave: each tick's energy in 6 ms by octave, over the
steady flame's Leq (fourteen recordings with ticks; the model raw at a metre, the four-burner
hob lighting its large burner):

| tick SEL over flame Leq, dB | 1 kHz | 2 kHz | 4 kHz | 8 kHz | 2 kHz against 8 kHz |
|---|---|---|---|---|---|
| recordings, median | -26.4 | -22.9 | -11.7 | -6.7 | -13.8 (-1.5 to -24.7) |
| round 1 (1.5 mJ, 30 µs, cap ring) | -18.6 | -7.8 | -2.7 | -6.7 | -1.2 |
| round 2 (0.36 mJ, 3 µs) | -25.2 | -16.3 | -9.7 | -9.0 | -7.3 |
| round 2 through the game, against round 1 through the game | -9.2 | -12.7 | -11.3 | -0.8 | |

Round 1 had 8-15 dB too much in 1-4 kHz, where the ear is most sensitive: the "too present" tick. Round
2 sits within 2 dB of the median on average over the four octaves; 2 kHz is still 6.6 dB over (the hob
top's image, section 4). Above 11 kHz the model now has more than before (+2.4 dB through the game in the
16 kHz octave), as a real spark does; the recordings cannot say, their MP3s stop at 16 kHz. The previous
"median about 40" for the tick peak over the flame came from a subset; over all fourteen it is 29.7,
interquartile about 25 to 46.

## 10. Open

- Heard by Cody: round 1 (2026-10-10): liked; "a little too present", "kind of loud", and the clicking went
  on long after the burner lit. Round 2 answers those (section 11): approved by ear 2026-10-10 ("sounds great
  now"; merged c3a09466). Not yet on any map: a `gas_hob` in the city's kitchens (tools/gen_city.py) is open.
- A pan on the burner (it shields the cap, changes the roar, and boils).
- An oven and a grill under the hob. A North American range's own details (60 Hz mains, 1.0 kPa manifold
  pressure, its injectors): the hob's switching and flame safety are there now, the rest is European.
- Changing the heat in the game: the knob's settings are in the state and the model, but the interact
  key only lights and turns off.
- What could later be shared with the fire model (docs/FIRE.md): the monopole law and PowerLawNoise are
  already shared; a light-up is a small deflagration, as a fire's flare-ups are.

## 11. Round 2 (2026-10-10)

Cody, on round 1: "the tick from the burner is ... ok but a little too present. Also, you kept the clicking
going long after the burner was lit ... the click sound, it's kind of loud too ... but it sounds good
though I like it otherwise."

- The sparks after the light. Round 1's cook held every knob 3.5 s after the flame caught, a European
  flame safety hob's hold: in the light render the sparks ran 3.3 s after it caught, 14 sparks (3.6 s,
  16, through the game). Now:
  - the default hob (`hob4`, the prefab `gas_hob`, what the interact key lights) has no flame safety, and
    the cook lets go 0.36-0.84 s after the flame catches: 3 sparks after it, the last 0.72 s after;
  - a flame safety hob (`hob4_ffd`) is held until its thermocouple holds and 1 s more: the last spark
    4.0 s after (17 sparks), Bosch's 4 s;
  - an auto re-ignition hob (`hob4_reignite`) stops by itself: in the render the first spark lit it
    and there was no second.
- The tick. Its anchor was re-checked against the recordings by octave (section 9): two estimates had
  made it loud in the presence region, the spark's heat four times the lighter's and a 30 µs duration
  fitted to the microphones' roll-off. Both now come from the measured lighter spark itself. The cap's
  ring was found to be some 80 dB under the crack, not 24 (section 4), and the module's own tick is
  heard through the hob's tray. Through the game the tick is 9-13 dB lower at 1-4 kHz, the same at
  8 kHz, 2 dB more above 11 kHz.
- The room: the lab's kitchen was a bare box, 0.93-1.0 s; now furnished, 0.67 s (section 8). The game's
  room model was not at fault.
- Renders: inbox/gas-stove-2026-10-10/round2.
