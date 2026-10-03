# Procedural recipes for door sounds and multi-part mechanical impacts

Scope: how practitioners and researchers build procedural door sounds (creak, latch, click, slam, rattle), with structures and numbers that can be reimplemented. Five key questions follow. Book page numbers refer to Farnell, *Designing Sound* (MIT Press 2010); the "Practical N" numbering is the book's (Practical 9 = Ch. 32 Creaking, Practical 19 = Ch. 42 Switches, Practical 20 = Ch. 43 Clocks, Practical 7 = Ch. 30 Bouncing, Practical 8 = Ch. 31 Rolling).

Primary sources used: the Farnell book text, as hosted at [ufpel.edu.br PDF](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf) (publisher page: [MIT Press](https://mitpress.mit.edu/9780262014410/designing-sound/)); Farnell's Pd patches at [aspress.co.uk/sd](https://aspress.co.uk/sd/); the SuperCollider port on [Wikibooks](https://en.wikibooks.org/wiki/Designing_Sound_in_SuperCollider/Print_version); Lloyd, Raghuvanshi & Govindaraju (I3D 2011) [PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/10/5.pdf); Rocchesso & Fontana (eds.), *The Sounding Object* (2003) [PDF](https://www.soundobject.org/SObBook/SObBook_JUL03.pdf); van den Doel, Kry & Pai, *FoleyAutomatic* (SIGGRAPH 2001) [PDF](http://www.cs.ubc.ca/~kvdoel/publications/foleyautomatic.pdf); Cook's SIGGRAPH 2000 course notes [PDF](https://www.cs.princeton.edu/~prc/CookSig00.pdf); STK `Shakers.cpp`/`Shakers.h` [GitHub](https://github.com/thestk/stk/blob/master/src/Shakers.cpp).

---

## 1. Andy Farnell, *Designing Sound*: the chapters that bear on doors (creak, latch/click, impacts, bouncing, rolling) and their parameters

### Takeaway
Farnell builds every mechanical sound the same way. An event generator fires short excitations (single impulses for stick-slip; band-passed noise bursts or a pair of sines for clicks), and these drive a fixed formant bank (parallel band-pass filters) plus a short delay-line "body" resonator tuned to the housing or panel. The realism comes from the timing of the events and from the body, not from detailed excitation. His creaking door is stick-slip impulses → 6 wooden formants (62.5–790 Hz) → 8 parallel delay resonators (4.52–16 ms). His switch is 3–4 noise clicks a few ms apart (3, 4, 5, 7 kHz, 1 ms attack, ~20 ms decay) into a body.

### Cited Findings

**Creaking door (Practical 9 / Ch. 32, pp. 395–400)**
- Model: "A squeaky door hinge is a two-part coupling where friction causes a series of stick-slip impulses, and the door ... acts as an amplifier for these pulses." In a door, the base is one hinge plate coupled to a large wooden or metal sounding board, and the mover is the other half, rotating with the door's weight as the normal force. — [Farnell, *Designing Sound*, p. 397](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Stick-slip: "The longer the time interval between steps, the greater force has accumulated and the higher the peak velocity. Impulses are generated when the mover quickly lurches forwards, and in practice these tend to come in short clusters with a large movement followed by several smaller ones." Slip frequency is proportional to applied force. A resonant system coupled to the interface makes slip become periodic, as in a bowed string. — [Farnell pp. 396–397](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Excitation choice: "Each burst of movement may itself consist of noisy excitations, but in creaking objects they are generally short and can be treated as single impulses." — [Farnell p. 397](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Stick-slip event generator (book text, Fig. 32.4):
  - The control input is the applied force, 0.0–1.0. It is smoothed with a 100 ms line lag to give the mover mass and momentum.
  - A metronome starts once the force passes a threshold of 0.3, mimicking static friction.
  - Metronome period = (1 − force) × 60 + 3 ms. A random number proportional to the period is added on every cycle, so slow creaks are more irregular.
  - Each slip's amplitude is proportional to the time since the previous slip, capped at 100 ms and normalised. A square root is applied twice for amplitude and once for decay time.
  - Each event has a square-law decay.
  — [Farnell pp. 398–399](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- SuperCollider port of the same model: static-friction threshold 0.1, slip rate `linlin(0.1, 1 → 1–333 Hz)` (333 Hz is the 3 ms minimum period), event amplitude ∝ √(force build-up), 1 ms attack, decay scaled by 0.01 × √amplitude. — [Wikibooks, Designing Sound in SuperCollider: Creaking door](https://en.wikibooks.org/wiki/Designing_Sound_in_SuperCollider/Creaking_door); [Print version](https://en.wikibooks.org/wiki/Designing_Sound_in_SuperCollider/Print_version)
- Wooden-door formant bank (Fig. 32.3): six parallel band-pass filters at 62.5, 125, 250, 395, 560 and 790 Hz with Q = 1, 1, 2, 2, 3, 3. 62.5 Hz is "a subharmonic given for a little extra weight; the proper harmonic series starts on 125 Hz". The frequencies are chosen for an unsupported rectangular membrane, and 0.2 of the direct signal is passed in parallel. — [Farnell p. 398](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf); Q values from the Pd patch [aspress Practical 9](https://aspress.co.uk/sd/practical09.html) and [Wikibooks](https://en.wikibooks.org/wiki/Designing_Sound_in_SuperCollider/Print_version)
- Panel resonator (Fig. 32.5): eight parallel recirculating delay elements of 4.52, 5.06, 6.27, 8, 5.48, 7.14, 10.12 and 16 ms. This is a rectangular-membrane series with a 125 Hz fundamental (8 ms) expressed as periods. The outputs are averaged and then high-passed at 125 Hz. The SC port applies 4× gain after the high-pass. — [Farnell p. 399](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf); [Wikibooks](https://en.wikibooks.org/wiki/Designing_Sound_in_SuperCollider/Creaking_door). (The aspress page summary labels these values "Hz"; the book and the SC port treat them as delay periods in ms.)
- Chain order: stick-slip → formant bank → delay resonator, "that gives the sound some life". To change brightness, change the direct-signal share or narrow the filters. For other door sizes or materials, recalculate the resonator and formants. — [Farnell p. 399](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Farnell's own suggested extensions:
  - Several stick-slip sources in parallel, one per hinge.
  - "adding another excitation source such as a door handle or lock to the same resonator to get an integrated effect".
  - Squealing leather or sponges need many simultaneous stick-slip sources.
  — [Farnell p. 400](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)

**Switches: the latch and click building block (Practical 19 / Ch. 42, pp. 485–490)**
- Parts: actuator (lever, plunger or tab), poles and throws (springy phosphor-bronze contacts that bounce "only a few milliseconds", which "the ear can pick ... up as a metallic ringing sound, or short 'chatter'"), a biased spring or tapered guide that snaps the throw past mid-point, an optional locking mechanism, and "the resonance of the switch housing or the panel on which the switch is mounted". — [Farnell pp. 485–486](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Event signatures by design:
  - Momentary: a single short ping.
  - Rocker: "two clicks and clunk as actuator hits stop".
  - Rotary: multiple clicks and a rotary slide.
  - Slide: friction slide before contact.
  - Latching push-button: "double click and latch, slightly different sounds switching on than off".
  — [Farnell p. 486](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Basic click: white noise → band-pass 5 kHz, Q 12, with an envelope rising to 1.0 in 1 ms and decaying in 20 ms. On its own it "lacks complexity". "Sequencing a few of these several milliseconds apart creates a nice clicking sound." — [Farnell p. 487](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf); patch values confirmed at [aspress Practical 19](https://aspress.co.uk/sd/practical19.html)
- Four-click switch: clicks centred at 3, 4, 5 and 7 kHz. "Those at 3kHz and 4kHz correspond more to plastic tones while the others tend towards a metal texture". The two plastic clicks come first, then a metal click shifted by 10 ms. A body is added as "a short delay with a little feedback through a low-pass filter". There is backwards masking: "it is hard to pick out their sequence order. A big difference in the total effect occurs once you have three or four clicks in close time proximity." — [Farnell pp. 487–488](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf). The Pd patch also shows a `bp~ 700 3` body and a 50 ms delay with 0.1 feedback ([aspress Practical 19](https://aspress.co.uk/sd/practical19.html); taken from an automated patch summary, moderate confidence).
- Problem with noise clicks: "it's hard to find the balance between a wide bandwidth that sounds crunchy or noisy, and having them too tight which produces a nasty overresonant ring." Also, short band-filtered white-noise bursts vary randomly in level, because a short noise segment may not contain the band's energy. — [Farnell pp. 488](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Fixes:
  - (a) A metal "ping" of two sine oscillators near 10 kHz with a square decay of about 50 ms, mixed onto a noise click: "really boosts the presence ... much more solid and focused".
  - (b) "Additive cluster noise": ring-modulate three phasors and take the cosine. This gives a band of noise-like partials of guaranteed constant strength. The noise colours used were 3345, 2980 and 4790 Hz, chosen by hand.
  — [Farnell pp. 488–489](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Slide-and-clunk envelope ("ssshh-Tunk"): rises 0 → 0.46 over 100 ms, jumps to 1.0, then decays to 0 in 50 ms, through a quartic function, with two cascaded filters colouring the noise. The second click is delayed 200 ms, and the ping lands at 300 ms to coincide with the second click's peak. The sum goes into "a single delay body waveguide, which accentuates low-frequency peaks from 40Hz, to 400Hz", with direct signal in parallel. — [Farnell p. 489](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Conclusion: "Each click corresponds to a mechanical event. The surface on which the switch is mounted strongly influences the sound." — [Farnell p. 489](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)

**Clocks: dense mechanical click clusters (Practical 20 / Ch. 43, pp. 491–497)**
- On slowing down a real tick: "Each tick is actually a fine microstructure, always the same, consisting of dozens of distinguishable events ... movements of cog teeth against other cog teeth, of levers against ratchets and of the hands ... moving and bouncing." — [Farnell p. 492](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Timing must be deterministic and synchronous: "We don't have parts of a clock sound shifting around in phase, which sounds completely wrong; each tiny detail must appear in its correct place within each tick." — [Farnell p. 493](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Metal click: square-law decay envelope with a 1 ms rise. Three band-pass filters (Q 30) in parallel on white noise, each with its own decay envelope, output × 3. Metal formants are in the 4–9 kHz range. The filters are placed *before* the envelope so that very narrow bands can still have very short decays; filters placed after the envelope ring on. — [Farnell pp. 493–494](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Escapement spring: a pair of sines at 8 and 10 kHz, alternating, gives "a chattering, brush-like noise of a tiny spring". — [Farnell p. 495](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Body: "two delays in partial feedback with band-pass filters in both signal paths", tuned to a box of length l and width w, with filter frequency = 1/delay period. Q 3 with feedback 0.3 suits "hard plastic or well-damped thin metal sheet". Example: a 10 ms delay with a 1 kHz filter. The mechanism parts "sound dry and lifeless as they are"; the body "bring[s] the sound to life". — [Farnell p. 496](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)

**Bouncing (Practical 7 / Ch. 30, pp. 383–386) and rolling (Practical 8)**
- Bouncing pattern: the period starts at 300 ms and falls linearly to 0 over 3 s, with amplitude starting at 1.0 and decay at 200 ms. Each event rises in 1 ms and has a square-law decay. Pitch: a fixed 120 Hz oscillator plus an FM carrier sweep from 210 to 80 Hz on a 4th-power decaying curve, with modulation depth scaled by 70 Hz × height, so energetic bounces are richer. "Mapping timbre, amplitude, and decay time to the bounce energy provides the correct effect." Farnell notes the linear-period approximation fails for small initial heights. — [Farnell pp. 384–386](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf); [Wikibooks SC port](https://en.wikibooks.org/wiki/Designing_Sound_in_SuperCollider/Print_version)
- Rolling tin can (SC port): four ring filters at 359, 426, 1748 and 3150 Hz with 0.2 s decay, excited by 10 summed regular impulse trains plus Dust-triggered asymmetric bumps (envelope values [0, 0, −1, 1.5, −1, 1, 0]). — [Wikibooks SC port](https://en.wikibooks.org/wiki/Designing_Sound_in_SuperCollider/Print_version)

### Inferences
- A door in Farnell's idiom is several excitation sources sharing one body. Hinge stick-slip impulses, handle and latch click clusters (noise clicks 3–7 kHz plus a ~10 kHz ping) and the slam (a broadband impulse) all feed the same wooden or metal formant bank and the same panel delay resonator. His Exercise 1 says exactly this.
- The numbers are tuned by ear for one generic door. The formants and delays are rectangular-membrane ratios on a 125 Hz fundamental, not values derived from door dimensions. A geometry-driven implementation would recompute them from the leaf's size, thickness and material, as Farnell himself recommends.
- His two warnings carry over directly to a latch: keep the click micro-timing deterministic within an event, and avoid band-passed white noise for very short clicks (level lottery) by using a deterministic excitation or putting the filters before the envelope.

### Gaps
- Farnell has no dedicated door-slam, latch or lock practical. The latch recipe above is inferred from his switch and clock chapters plus his creak exercise.
- The aspress pages give patches, not prose. The Pd body values for the push-button switch (`bp~ 700 3`, 50 ms delay, 0.1 feedback) come from an automated summary of the patch file and were not checked by opening the .pd file.

---

## 2. Nemisindo / Josh Reiss (Queen Mary): procedural models and listening-test results

### Takeaway
I found no public Nemisindo door model; its listed models are fire, explosions, engines, footsteps, weather and so on. QMUL's evaluations show that procedural sound can approach recordings for some classes and fails badly for others. Two findings matter for doors: (1) post-production cues such as reverb, compression and EQ measurably raised realism ratings, and (2) practitioners preferred layering procedural output with recorded material.

### Cited Findings
- Nemisindo's public model listing (Night Scene, Car Engine, Helicopter, Stormy Day, Footsteps, Water, Wind, Spray, Adaptive Footsteps, Nature Pack, Engine) shows no door, creak, latch or impact model. — [nemisindo.com](https://nemisindo.com/); [Nemisindo products](https://nemisindo.com/products)
- Moffat & Reiss, "Perceptual Evaluation of Synthesized Sound Effects" (ACM TAP 15(2), 2018) tested five techniques: additive, statistical modelling with two feature sets, physically inspired, concatenative, and sinusoidal modelling. It used 8 sound classes and 66 samples. Additive synthesis was the only method not significantly different from the recorded reference across all classes. For applause the recording was rated far more realistic than every synthetic version (p < 0.0001). Bees, rain and wind came close to recordings. — [ACM DL](https://dl.acm.org/doi/abs/10.1145/3165287); [ResearchGate record](https://www.researchgate.net/publication/324460753_Perceptual_Evaluation_of_Synthesized_Sound_Effects) (details via search-result summaries; full text not retrieved)
- QMUL blog summary of the same work: "some synthesis techniques are indistinguishable from a recorded sample, in a fixed medium environment." — [Intelligent Sound Engineering blog, 2018](https://intelligentsoundengineering.wordpress.com/2018/04/11/sound-synthesis-are-we-there-yet/)
- Selfridge et al.'s aeroacoustic models (sword swings, bats, broom handles) were rated "as plausible as actual recordings" for most objects. Propellers were rated below recordings. — [blog 2017](https://intelligentsoundengineering.wordpress.com/2017/11/16/applied-science-journal-article/); [blog 2018](https://intelligentsoundengineering.wordpress.com/2018/09/14/aeroacoustic-sound-effects-journal-article/)
- Nemisindo footsteps outperformed the other traditional synthesis approaches in a listening test against real recordings (AES 152nd Convention study); the blog gives no numbers. — [blog 2022](https://intelligentsoundengineering.wordpress.com/2022/04/21/adaptive-footstep-sound-effects/)
- QuAP (DAFx 2026), built on six Nemisindo models (Fire, Explosion, Jet, Rocket, Helicopter, Gun; "inspired by the design principles of Farnell"):
  - A feature-driven bottleneck found the Essentia features that best discriminate recorded from synthetic audio per class, using the 6KSFX dataset.
  - The fix applied was mostly *post-production*: reverb, compression, distortion and EQ.
  - MUSHRA with 20 participants showed significant gains in 5 of 6 classes. Rocket reached only p = 0.08 ("post-production effects alone are insufficient ... synthesis-level modifications may be required"). Jet was excluded as "perceptually too synthetic".
  - Mean scores sit roughly in the 29–56/100 range. My PDF text extraction scrambled the table columns, so per-model pairings are uncertain.
  - Users emphasised "the utility of layering synthetic outputs with recorded library material".
  — [arXiv 2606.00629](https://arxiv.org/pdf/2606.00629)

### Inferences
- Among the things that separate synthetic from recorded audio in QMUL's analysis are the recording chain and the room (reverb, compression, band EQ). A dry procedural door played without a room will be judged against recordings that always carry one. This fits the car-door finding in Q4 that room acoustics strongly change judgements.
- Classes that are dense textures of many similar events (applause) are the hardest. A door is a handful of distinct events, closer to the classes where synthesis did well. Rattles that turn into dense textures are the risky part.

### Gaps
- No Nemisindo or QMUL door, latch or creak model or listening test was found.
- The full Moffat & Reiss TAP paper could not be retrieved (TLS and HTML-redirect errors on eecs.qmul.ac.uk), so the list of 8 classes and the per-class scores are not given here.

---

## 3. Other implementations: Lloyd/Raghuvanshi/Govindaraju, SoundSeed Impact / Impacter, van den Doel, Avanzini/Serafin/Rocchesso friction, Cook PhISEM

### Takeaway
The production-proven approach for game impacts is modal analysis of one recording plus a residual. It uses sampled per-mode envelopes rather than ideal exponentials, randomised mode gains for variation, random initial phases, and a random-dip filter for non-modal sounds. The research approach for creaks is a dynamic (elasto-plastic) friction exciter driving hand-tuned modal resonators. For impacts, contact duration conveys hardness, and very hard contacts need micro-collision bursts or they sound "too clean".

### Cited Findings

**Lloyd, Raghuvanshi & Govindaraju, "Sound synthesis for impact sounds in video games" (I3D 2011; shipped in Crackdown 2 on Xbox 360)**
- Signal model: x(t) = Σ gₘ Aₘ(t) sin(2πfₘt + φ₀,ₘ) + r(t). It uses the *extracted* amplitude envelope Aₘ(t) per mode instead of e^(−αt), plus a residual r(t). "All of these modal synthesis techniques assume ideal exponential decay ... the sounds generated by these techniques often sound too 'clean'." With the residual, "the synthesized sounds in most cases are indistinguishable from recorded clips". — [Lloyd et al. 2011 PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/10/5.pdf); [MSR page](https://www.microsoft.com/en-us/research/publication/sound-synthesis-impact-sounds-video-games/)
- Analysis: STFT peaks that persist across a user-selected set of slices near onset become modes. The residual is the input minus the resynthesised modes. "Modal synthesis also fails to reproduce some sounds that lack strong modal components (e.g. a footstep)." Short high-frequency modes are left in the residual, which keeps the residual short with no loss of fidelity. For strongly modal sounds the residual is "mostly noise concentrated near the attack. The tail is empty and can be clipped." — [Lloyd et al.](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/10/5.pdf)
- Mode counts from their table: brass bell 6, plastic barrel 23, wooden box 65, rock 120. — [Lloyd et al., Fig. 5](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/10/5.pdf)
- Variation:
  - Randomise mode gains: gₘ(v) = lerp(1, ξ, α(v)) / c(v), with ξ ~ U[0,1], α(v) = bias(v, 0.9), c(v) = √(1 − α + α²/3) for power normalisation, and bias(x, b) = x / ((1/b − 2)(1 − x) + 1) (Schlick).
  - Physically, striking near an edge attenuates low modes ("hitting a box near its edge results in a higher pitched sound than in the center").
  - Gains scale with the physics impulse.
  — [Lloyd et al. §3.3](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/10/5.pdf)
- Simultaneous impacts: "we randomize the initial phases φ₀,ₘ" so identical waveforms' peaks do not line up and clip. They also "add a bit of random delay to spread the impacts out slightly in time" when the exact collision time is unknown. — [Lloyd et al. §3.2](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/10/5.pdf)
- Non-modal variation filter: a cascade of about 10 biquad dip (cut) filters at random log-spaced centre frequencies, with user ranges for random gain and Q. It works best on broadband sounds such as "Rock" and footsteps. — [Lloyd et al. §4](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/10/5.pdf)
- Engineering:
  - Envelopes are stored in log scale, clamped at a −81 dB noise floor, simplified 3–4× and interpolated linearly in log space.
  - Amplitudes are quantised to 8 bits with "very little or no perceptual difference".
  - A mode quota drops the lowest-power modes, which mainly loses highs.
  - The memory budget was 25 MB for all audio and 2 MB for impacts. Dozens of simultaneous impacts fit in 10% of one CPU.
  — [Lloyd et al.](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/10/5.pdf)

**Audiokinetic SoundSeed Impact and Impacter**
- SoundSeed Impact: the offline Impact Modeler outputs a residual WAV ("the impact sound with all resonant frequencies stripped out") and a text file of the frequency, bandwidth and amplitude of each mode. At runtime the resonances are reapplied to the residual by filtering, and the modes are varied. — [SoundSeed overview (search summary)](https://www.audiokinetic.com/learn/videos/c2QuOZFwBZc/); [MCV Tool Focus](https://mcvuk.com/development-news/tool-focus-soundseed-impact-2008-1/)
- Impacter (its successor) splits a sound into an "impact" (excitation) and a "body" (filter bank plus sinusoids). "The most successful cross synthesis result was found to be the excitation of one file ... combined with the filter bank and sinusoids ... of another." Its parameters:
  - Mass stretches or compresses both length and pitch.
  - Velocity changes timbre and amplitude.
  - Position changes timbre as if struck at a different place.
  Impact and body are randomised on every play. — [Audiokinetic Impacter](https://www.audiokinetic.com/en/wwise/plugins/impacter/); [launch blog](https://blog.audiokinetic.com/en/impacter-launch-blog/) (via search-result summaries; the blog returned 403 to direct fetch)

**van den Doel, Kry & Pai, FoleyAutomatic (SIGGRAPH 2001)**
- Impact force: "1 − cos(2πt/τ) for 0 ≤ t ≤ τ, with τ the total duration of the contact". "The exact details of the shape is relatively unimportant and the hardness is conveyed well by the duration." — [FoleyAutomatic PDF §4.1](http://www.cs.ubc.ca/~kvdoel/publications/foleyautomatic.pdf)
- Hard contacts: a single-sample impulse sounds "too 'clean' in practice". Real hard impacts contain fast sequences of separations and re-collisions driven by the modes. They modelled this as "a short burst of impulse trains at the dominant modal frequencies". "The microcollisions take place within 15ms ... An impact audio-force composed of impulses at the first 4 modal resonance frequencies sounded very convincing." — [FoleyAutomatic §4.1](http://www.cs.ubc.ca/~kvdoel/publications/foleyautomatic.pdf)
- Scraping and sliding: fractal noise with power ∝ ω^β (roughness; fractal dimension D = β/2 + 2), passed through a reson whose frequency scales with contact velocity. Narrow resonances sound more pitched. Audio-force level is tied to frictional power, ∝ √(v·F_normal); the extracted text is partly garbled here. Their rolling model was "not as convincing as the scraping and sliding sounds". — [FoleyAutomatic §4](http://www.cs.ubc.ca/~kvdoel/publications/foleyautomatic.pdf)

**Avanzini, Serafin & Rocchesso: friction-driven door squeaks**
- An elasto-plastic (dynamic, bristle-state) friction model between two modal resonators reproduces "rubbing, braking, and squeaky doors". — [Avanzini, Serafin, Rocchesso, IEEE TSAP 2005](https://avanzini.di.unimi.it/downloads/publications/avanzini_sap05.pdf); [DAFx 2002](https://www.dafx.de/papers/DAFX02_Avanzini_Serafin_Rocchesso_rubbed_surfaces.pdf)
- Door setup: "two exciter-resonator pairs, one for each of the shutters" (swinging double door). "The modal frequencies of the objects have been chosen by hand and ear tuning on the basis of recorded sounds. The results are especially convincing in reproducing complex transient and glissando effects which are typically found in real door squeaks." Control: the mouse x-axis sets the external (tangential) force fₑ and the y-axis sets the normal force f_N. — [The Sounding Object, Ch. 8, pp. 162–164](https://www.soundobject.org/SObBook/SObBook_JUL03.pdf)
- Phenomenological parameter guide (Table 8.1):
  - Bristle stiffness affects the evolution of mode lock-in.
  - Bristle dissipation affects sound bandwidth.
  - Viscous friction affects the speed of timbre evolution and pitch.
  - The noise coefficient affects perceived surface roughness.
  - Dynamic and static friction coefficients: high values reduce bandwidth, and they affect the smoothness of the attack.
  - Stribeck velocity affects the smoothness of the attack.
  - Normal force: "high values give rougher and louder sounds".
  — [The Sounding Object, Table 8.1](https://www.soundobject.org/SObBook/SObBook_JUL03.pdf)

**Cook: PhISEM (particle rattles) — algorithm and STK constants**
- Algorithm: system energy decays exponentially. Each sample, a collision happens with probability based on the number of particles. A collision adds to a fast-decaying particle-sound level ("sum of exponentially decaying noises is an exponentially decaying noise"). The result is filtered by "net system resonances". — [Cook SIGGRAPH 2000 course notes, pp. 28–29](https://www.cs.princeton.edu/~prc/CookSig00.pdf)
- STK per-sample loop:
  - `shakeEnergy *= systemDecay`
  - `if (rand(0..1024) < nObjects) { sndLevel += shakeEnergy; input = sndLevel; optionally jitter resonance freqs }`
  - `sndLevel *= soundDecay`
  - The input is summed through 2-pole resonators (`a1 = −2r·cos(2πf/fs)`), then a 3-tap FIR equaliser.
  - A shake (noteOn) adds `amplitude × 0.1` to shakeEnergy, capped at 1.0, and processing stops below 0.001.
  — [STK Shakers.h](https://github.com/thestk/stk/blob/master/include/Shakers.h); [Shakers.cpp](https://github.com/thestk/stk/blob/master/src/Shakers.cpp)
- STK presets (per-sample constants):

  | Preset | Objects | Sound decay | System decay | Resonances (Hz) | Pole radii | Resonance gains |
  |---|---|---|---|---|---|---|
  | Maraca | 25 | 0.95 | 0.999 | 3200 | 0.96 | — |
  | Tambourine | 32 | 0.95 | 0.9985 | 2300 / 5600 / 8100 | 0.96 / 0.99 / 0.99 | 0.1 / 0.8 / 1.0 |
  | Sleighbells | 32 | 0.97 | 0.9994 | 2500 / 5300 / 6500 / 8300 / 9800 | all 0.99 | 1.0 / 1.0 / 1.0 / 0.5 / 0.3 |
  | Coke can | 48 | 0.97 | 0.999 | 370 (Helmholtz) + 1025 / 1424 / 2149 / 3596 | 0.99 / 0.992 ×4 | 1.0 / 1.8 ×4 |

  — [STK Shakers.cpp](https://github.com/thestk/stk/blob/master/src/Shakers.cpp)

### Inferences
- For a latch or strike-plate clank, Lloyd's findings suggest three things. Modelling a small number of strong modes and putting everything short and high in a noise residual at the attack is enough for "indistinguishable" quality. Variation should come from gain randomisation, not from re-pitching. Random phases matter when several voices of the same door fire together.
- van den Doel's 15 ms burst of impulses at the first four mode frequencies is a cheap, physically motivated way to make a hard steel-on-steel latch strike less "clean" than a single impulse. Contact duration τ is the material/hardness control: shorter for steel on steel, longer for wood or rubber stops.
- The STK constants are per sample. With probability nObjects/1024 per sample, 25 beans is ≈2.4% per sample, or about 1,000 events/s at 44.1 kHz. A port to another sample rate must rescale both the probability and the decays (decay^(fs_old/fs_new)). STK's default rate is 44.1 kHz; this is from general knowledge of STK, not checked in these files.
- Avanzini et al. publish no numeric friction parameters for the door. What they show is that hand-tuned modes from recordings plus a dynamic friction law give the glissandi a static stick-slip clock (Farnell) cannot.

### Gaps
- No Unreal MetaSounds or FMOD door example with documented internals was found; searches returned only sample libraries.
- No sources were retrieved for Rob Hamilton or Dylan Menzies' Phya, so their door, impact or rattle specifics are not covered.
- No numeric elasto-plastic parameter sets for the door squeak were found (σ₀, σ₁, σ₂, μs, μd, vs and modal frequencies). They may exist in the SDT (Sound Design Toolkit) help patches ([SDT handbook](https://www.soundobject.org/SDT/downloads/SDT_handbook_Max6.pdf)), which were not opened.

---

## 4. How sound designers layer door sounds (handle, latch, swing, impact, rattle, room), and what a real door contains in time

### Takeaway
Practitioner guidance breaks a door slam into a sharp latch transient, a low-mid body "thump" from the leaf, and a tail of rattle and room, with the latch slightly offset from the body. It recommends recording the handle, latch and swing separately for control. The only measured timelines I found are for car doors:
- latch impact at about 45 ms after the motion starts;
- secondary events at about 15 ms spacing;
- everything over within about 160–200 ms;
- a low-frequency pattern repeating every 20 ms, eight times.
Listeners' judgements of door sounds depend strongly on the room and on context.

### Cited Findings
- Foley component list (vendor and practitioner blogs, low authority):
  - "door body, latch, handle, hinge, room tone, rattle, and reverb around the impact".
  - The slam is described as "The Transient (The Crack) ... the metal latch hitting the strike plate", "The Body (The Boom) ... the door itself resonating and pushing a big pocket of air", and "The Tail ... reverberation and rattle that lingers".
  - "Recording each part separately—the handle turn, the latch click, and the door swing as isolated events—gives you way more control".
  - "A slight offset between the latch click and impact thud mimics how the sound occurs in the real world."
  — [sfxengine blog: door slam](https://sfxengine.com/blog/door-slam-sound-effect); [sfxengine: door opening](https://sfxengine.com/blog/door-opening-sound); [add.app door slam](https://add.app/sound-effects/door-slamming-sound-effects/). These are commercial blogs, so treat them as convention, not evidence.
- Ric Viers (*Sound Effects Bible*): find the "source sound" and aim the mic at it, e.g. at the hinges for a creak, because the creak comes from stress on the hinges. — [Blinkist summary](https://www.blinkist.com/en/books/the-sound-effects-bible-en) (secondary summary)
- Car door slam timeline (beamforming study):
  - The first major impulsive event (latch contact on the striker) excites all frequencies at about 45 ms.
  - Secondary events follow at about 15 ms intervals, and the major events finish within about 160 ms; total duration is under 200 ms.
  - From about 20 ms a high-frequency broadband event without lows (door movement just before sealing) appears.
  - Below 200 Hz, a pattern repeats every 20 ms, eight times, with decreasing strength.
  - A 200–600 Hz event is centred at about 80 ms.
  — ["Visualization of door-slam sound by using beamforming" (ResearchGate)](https://www.researchgate.net/publication/297155286_Visualization_of_door-slam_sound_by_using_beamforming); [figure captions](https://www.researchgate.net/figure/A-typical-door-slam-noise-with-secondary-events-The-top-plot-depicts-sound-pressure-Pa_fig2_297155286) (from abstract and caption snippets, full text not read)
- Car door force sources: the latch closing around the striker, and the door rim hitting the rubber seal. Seal stiffness matters: "A too soft sealing would result in a hard, metallic noise when the car door hits the car body". A good closing sound "should be dominated by low frequencies, ... medium loudness ..., there should be no rattles". — [Chalmers MSc thesis, Experimental investigation of mechanisms affecting the door closing sound](https://publications.lib.chalmers.se/records/fulltext/142743.pdf)
- Context effects (Bézat et al., Euronoise 2006, PSA/LMA/McGill):
  - "The room influences the quality evaluation and decreases drastically the discrimination of the sounds."
  - Handling the door does not change quality ratings but improves discrimination.
  - The car's image accounted for 17% of the quality judgement with video and 64% in situ.
  — [Bézat et al. 2006, arXiv 1003.4908](https://arxiv.org/pdf/1003.4908)

### Inferences
- The practitioner layers line up with the physical sources: the latch and strike transient (metal, broadband, first), the leaf's body modes (low-mid, the "boom"), the frame or stop contact, secondary rattles of loose parts (handle, latch bolt in its keeper, glass), and the room. The latch-to-body offset is a real effect of the bolt reaching the keeper before the leaf seats on the stop. For a hinged room door, the size of that offset is not documented in any source found.
- The car data gives a plausible order of magnitude for a slam's internal timeline: tens of ms between latch, seal or stop, and secondary events, with the whole event under about 0.2 s before the room tail. Car doors have seals and much stiffer latches than building doors, so treat these numbers as an analogy only.
- QMUL (Q2) and Bézat both show that the room changes how a door sound is judged. A dry procedural door compared against reverberant recordings will lose; it needs the same room treatment as everything else.

### Gaps
- No primary Foley text (Viers, Sonnenschein, Ament) giving a timed breakdown of a building-door slam or open was retrieved. David Sonnenschein's *Sound Design* was not found discussing doors.
- No measured timeline for a residential or commercial hinged door (latch strike → leaf on stop → rattle) was found.

---

## 5. Modelling rattles and jiggle after an impact (stochastic impact trains, PhISEM, bouncing with restitution)

### Takeaway
Three recipes appear in the sources:
1. **Bouncing.** Each impact has a constant energy-loss fraction C, so intervals and velocities shrink geometrically. Randomise below those maxima to mimic irregular shapes, and map each impact's energy to amplitude, brightness and decay.
2. **PhISEM.** Collisions are Poisson events whose rate is set by object count. Each collision adds to a fast-decaying excitation level that rides on a slower system energy, and the result is filtered by a few fixed resonances.
3. **Breaking and settling.** As in (1) but with high randomness, a quickly falling density and a short noise burst on the attack.

Perceptually, the temporal pattern alone identifies bouncing versus breaking.

### Cited Findings
- Warren & Verbrugge, as summarised in *The Sounding Object*: listeners identify bouncing versus breaking from temporal patterning. Bouncing was synthesised by superimposing four damped, quasi-periodic pulse trains. Breaking was synthesised as multiple damped quasi-periodic sequences with different damping, and "a short initial noise impulse is shown to contribute to a 'breaking' impression". — [The Sounding Object, Ch. 1 & 9.3](https://www.soundobject.org/SObBook/SObBook_JUL03.pdf)
- Rath & Rocchesso "bouncer" (Ch. 9.2):
  - Energy after each reflection is E_post = C·E_pre (C < 1), so E(n) = Cⁿ·E₀, v(n) = (√C)ⁿ·v₀, and t_int(n) = (√C)ⁿ·t_int(0), where t_return = 2v₀/g. "The implementation of this basic scheme in fact delivered very convincing results."
  - For non-spherical objects, energy transfer between vertical, horizontal and rotational motion gives "quasi randomly — shorter temporal intervals between bounces, bounded by the exponential decay behavior", and impact velocities that deviate below the spherical maxima.
  - Impact position also varies, "especially of the latter effect through respective modulation of modal weights, shows to be of strong perceptual significance".
  — [The Sounding Object pp. 182–185](https://www.soundobject.org/SObBook/SObBook_JUL03.pdf)
- Bouncer control parameters:
  1. time between the first two reflections;
  2. initial impact velocity;
  3. acceleration factor (ratio of successive maximum intervals);
  4. velocity factor;
  5. two random-deviation ranges below the maxima (for intervals and velocities), modelling irregularity of shape;
  6. a stop threshold that emits a "terminating bang" to start a following stage (e.g. a final rolling or rocking stage, as with a falling coin's "initial 'random' and a final regular stage").
  — [The Sounding Object p. 185](https://www.soundobject.org/SObBook/SObBook_JUL03.pdf)
- The end of a physical bounce differs from retriggering: "the two interacting objects finally stay in constant contact, a clear difference to simply retriggering". A "sticky" contact surface produces "a complex and acoustically characteristic transient". — [The Sounding Object Figs. 9.5, 9.7](https://www.soundobject.org/SObBook/SObBook_JUL03.pdf)
- Breaking or "dropper" model: bouncer with high randomness and a time factor > 1 (density falling quickly). Fragments mutually collide with "a massive initial density" that rapidly decreases. Further findings:
  - "Even sounds realized with only one impact-resonator pair can produce a clear breaking-impression."
  - A short noise impulse on the attack "fortified the breaking character".
  - Metallic modal tuning made patterns "less identifiable as a breaking event".
  - "Extreme mass relations of 'striker' and struck resonator ... led to more convincing results".
  — [The Sounding Object pp. 186–187](https://www.soundobject.org/SObBook/SObBook_JUL03.pdf)
- Farnell's bouncing simplification: a linear decrease in period from 300 ms to 0 over 3 s, with amplitude, decay time and spectral richness all mapped to remaining energy. — [Farnell pp. 384–386](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- PhISEM rattle algorithm and constants: see Q3. The tambourine is modelled as "fixed shell + 2 moving cymbal resonances" (2300, 5600, 8100 Hz) and the coke can as "Helmholtz + 4 metal resonances". Per-collision random jitter of resonance frequency is available (`varyFactor`). — [STK Shakers.cpp](https://github.com/thestk/stk/blob/master/src/Shakers.cpp)
- Contact-bounce chatter in switches is "perhaps only a few milliseconds" yet audible as "a metallic ringing sound, or short 'chatter'". — [Farnell p. 486](https://wp.ufpel.edu.br/labcomp/files/2022/09/PureData-Designing_Sound-Andy_Farnell.pdf)
- Car doors: a good closure has "no rattles", and secondary events trail the latch impact at about 15 ms intervals (see Q4). — [Chalmers thesis](https://publications.lib.chalmers.se/records/fulltext/142743.pdf); [beamforming study](https://www.researchgate.net/publication/297155286_Visualization_of_door-slam_sound_by_using_beamforming)

### Inferences
- A loose part on a door (lever handle on its spindle, a latch bolt in an oversized keeper, a pane in its glazing bead) is closer to Rath & Rocchesso's *irregular bouncer* than to PhISEM. It has one or two bodies, a constrained gap, and a short train of impacts with geometric decay, random shortening below the bound and random modal-weight changes per hit. PhISEM fits many-particle textures (loose screws in a box, chain, glass fragments).
- The bouncer's own parameters cover a slam-induced rattle:
  - first interval from the gap and the impulse delivered;
  - an acceleration factor (√C) from the restitution of metal on metal;
  - random deviation for the irregular shape;
  - a stop threshold where the part comes to rest in contact. That is a damped final state, not a last retrigger.
- Per Warren & Verbrugge and Rath & Rocchesso, a rattle that comes out too regular will read as "bouncing ball", and one too dense at onset with falling density will read as "breaking". The timing statistics need checking against recordings or physics as much as the timbre does.

### Gaps
- No source gave measured restitution coefficients or interval sequences for door-hardware rattles (handles, bolts, glazing).
- Cook's original PhISEM paper (CMJ 1997) was not read; the algorithm comes from his course notes and the current STK source. In the current STK `tick()`, the excitation on a collision frame is `sndLevel` (an impulse into the resonators) rather than noise × sndLevel. That differs from the course-note wording ("exponentially decaying white noise") and from older STK versions as I recall them; this was not checked.
