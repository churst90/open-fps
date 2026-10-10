# The gas hob

Cody, 2026-10-10: "New model, gas stove, tick tick tick tick whoosh ... let me hear a good physical model
of this, realistic."

A domestic gas hob as a physical model: the knob and its valve, the gas through the injector and the mixing
tube, the spark module, the light-up, the flames, the flame safety, and turning it off. Nothing is a
recording. Recordings of real hobs were used only as the measure (section 9).

Code:
- `OpenFPS.Common/GasHob.cs`: the gases, the burners, the hob, its presets, the knob's valve law, the
  state key (`HobKey`) and what the interact key does (`HobControls`).
- `OpenFPS.Client.Core/AudioEngine/Core/Stove/GasHobSynth.cs`: the hob, pascals at a metre.
- `OpenFPS.Client.Core/AudioEngine/Fmod/StoveVoiceState.cs`: the hob as a voice.
- Prefabs `gas_hob` (four burners, natural gas), `gas_hob_propane`, `gas_burner` (one burner).
- AudioLab `--stove levels|render|game`.

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
  - it holds the flame safety valve open by hand;
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

- One module sparks every electrode at once while any knob is held in [recalled: four-outlet modules;
  every electrode on a hob ticks when one knob is pushed].
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
  - A mains module's spark carries about four times that [estimate]: 1.5 mJ (`SparkHeatMj`).
  - Its heat goes in as E t/τ² e^(-t/τ) with τ = 30 µs [fitted: the recorded ticks' third-octave peak is
    at 6.3-10 kHz]. Rendered from band-limited kernels (a windowed sinc at 0.45 of the rate), three
    durations (τ and 15 % either side, as the spark's path along the cap's edge differs) and sixteen
    sub-sample phases.
  - The hob's steel top gives back each crack from its image 18 mm below (`SparkHeightMm`), and each
    electrode's crack reaches you by its own path. Four electrodes across 22-30 cm comb the tick at
    about 1.6 kHz spacing, as the recordings do.
  - Measured at a metre: 84-90 dB peak, sound exposure 46 dB. 43 dB over the large burner's roar on
    full; the recordings' median is about 40.
- The cap: a free disc of enamelled steel. Its first four modes from plate theory (λ² 5.253, 9.084, 12.23,
  20.52 for a free circular plate [recalled]), 2.1-8 kHz for these caps, struck by every spark, ringing
  24 dB under the crack [estimate] and stopped in 40 ms where it sits on the crown [estimate].
- The module's own tick in the panel, 52 dB peak [estimate].

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
- The cook keeps the knob in for 3.5 s after the flame catches (manuals ask for a few seconds), and the
  sparks tick on through the flame until then. This is the European model: the module stops when the
  knob is let go, not when it senses the flame (an American re-ignition module would stop at once).
  Let go too soon, the magnet cannot hold and the valve snaps shut.
- Turning off: the plug closes as the knob turns, the injector stops, the head drains with the tube's
  lag. When the mixture leaves the ports slower than about a third of the burning velocity the flame
  sinks into them and is quenched [estimate] in the port bore over the burning velocity (4 ms). Its last
  200 W or so go in that time: a soft pop, 66 dB peak at a metre for the large burner (61 dB for the
  single burner, 69 dB on propane). Then the knob's stop.
- About 16 s later the thermocouple has cooled and the armature drops: a faint click, 44 dB peak, as on
  real hobs a quarter of a minute after they are turned off.

## 8. In the game

- The prefab is one entity with one voice. Its sound key is its state: `stove:hob4/0000>3000@<time>`,
  one digit a burner (0 off, 1 low, 2 medium, 3 high), before and after the last change, and when on
  the shared clock (`WindField.Now`). The server keeps nothing else.
- The interact key at a hob (within 1.3 m, as a tap): with a burner off, it lights the next one (front
  left, front right, back left, back right) on full, "You light the front left burner."; with every
  burner lit, it turns them all off, one knob after another, "You turn every burner off."
- Each client plays the cook's hand from the key: push, turn, the ticks, the light-up, the hold, the
  release, or the turn back to off. A change heard within a second of being made plays from its start;
  a voice made later runs what it missed silently, so a player arriving at a lit hob hears it burning.
  Every client hears the same light-up: whether a spark fails is the gas's doing, and the module's
  jitter is seeded.
- A hob with every burner off is given no voice 40 s after its last change (`HobKey.QuietAfterSeconds`).
- The room is the game's. Through the game (`--stove game`) in a 3.6 × 3.2 × 2.6 m kitchen with a tiled
  floor and back wall, standing at the hob with the ear a metre from it, at the default /levels: the
  sparks peak at -1.1 to -1.3 dBFS, the large burner on full is about -42 dBFS RMS, the light scene
  -28 LUFS. Nothing clips.
- Cost: a whole hob alight renders at about 1-2 % of a core (`--stove levels` prints it per scene).

## 9. Fitting

No recording of a hob was in inbox/. Fifteen Freesound previews of hobs being lit were measured as the
measure only, never played: 194709, 765145, 751151, 714949, 390159, 390160, 539875, 251814, 740092,
256510, 240077, 697229, 249776, 426225, 254307 (freesound.org/s/<id>). Uncalibrated, so only
ratios and shapes were used. Measured with the same scripts on the recordings and on the model:

| | recordings | model |
|---|---|---|
| sparks a second | 3.2-5.6 | 4.17 |
| interval CV, clean recordings | 0.4-3 % | 2.7-3.4 % |
| tick third-octave peak | 6.3-10 kHz | 5-6.3 kHz, falling to a notch at 10.6 kHz from the hob's image |
| tick peak over the steady flame | 20-56 dB, median about 40 | 39-43 dB |
| steady flame, octaves re 1 kHz, 125 / 250 / 500 / 2k / 4k | median +8.6 / +7.8 / +3.7 / -3.2 / -0.1 | +4.7 / +5.6 / +3.2 / -3.4 / -6.4 |
| light-up's first 150 ms over the steady flame, 31-125 Hz / 125-2000 Hz | median about +16 / +17 dB | +15 / +12 (through the game), +21 / +23 for the slow light |
| light-up length, 125-2000 Hz over steady +3 dB | 60-480 ms, median about 220 | 140-160 ms; 210-360 ms for the slow light |

The recordings' 4 kHz octave varies by 25 dB between hobs (how much hiss each injector makes); the model
sits at the quieter end with the textbook jet law. Their 63 Hz octave is mostly the rooms and the
handling.

## 10. Open

- Heard by Cody: none of it yet.
- A pan on the burner (it shields the cap, changes the roar, and boils).
- An oven and a grill under the hob; American ranges with re-ignition modules and no flame safety.
- Changing the heat in the game: the knob's settings are in the state and the model, but the interact
  key only lights and turns off.
- What could later be shared with the fire model (docs/FIRE.md): the monopole law and PowerLawNoise are
  already shared; a light-up is a small deflagration, as a fire's flare-ups are.
