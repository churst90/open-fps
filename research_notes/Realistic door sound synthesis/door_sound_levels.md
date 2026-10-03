# Measured sound levels of door events (for calibrating door sounds in dB SPL at 1 m)

Conventions in these notes:
- **[M]** = a measured value as reported by the source. **[E]** = my estimate or conversion, with the assumptions stated.
- Converting to 1 m: free-field point source, `L(1 m) = L(r) + 20·log10(r)`. Hemispherical source over a hard floor, from sound power: `Lp(1 m) = LWA − 8 dB`. Indoors at 2–3 m the reverberant field adds to the direct sound, so a direct-field-only correction back to 1 m **overstates** the 1 m level by a few dB. Treat all indoor conversions as upper bounds.
- From the definition of Fast time weighting (τ = 125 ms) [E]: for a click much shorter than 125 ms, LAFmax ≈ SEL + 9 dB, and Lpeak ≈ SEL − 10·log10(Te), where Te is the effective duration (energy / peak²). For Te = 5–20 ms this gives **Lpeak ≈ LAFmax + 8 to 14 dB**, before any A- vs C/Z-weighting difference. This matters if the game calibrates by sample peak and the literature reports LAFmax.

## Summary table

| Event | Door / hardware | Metric | Value | Distance | 1 m equivalent | Kind | Source |
|---|---|---|---|---|---|---|---|
| Car driver door closed, 10 cars from 2005–2018, 123 closings | Passenger cars | LAFmax, energetic mean | 72.4 dB (per-car means 66.5–77.3; single closings 59.6–80.8) | 3 m, 1.5 m high, reflecting ground | ≈ 82 dB (+9.5) | [M] then [E] | Styria (2023) |
| Same, sound power (ISO 3744, 28 points on one car) | Car | LWA | 90.8 dB (recommended 91 dB) | – | ≈ 83 dB (LWA − 8, hemisphere) | [M] then [E] | Styria (2023) |
| Car door, Bavarian Parking Area Study (cars from 1990–98) | Car | LAFmax / LWA | 72.0 dB at 7.5 m; LWA 97.5 dB | 7.5 m | ≈ 89.5 dB | [M] then [E] | quoted in Styria (2023) |
| Car boot lid, Parking Area Study | Car | LAFmax | 74 dB | 7.5 m | ≈ 91.5 dB | [M] then [E] | search summary of LfU/TÜV reports (see Q4) |
| Car door, VDI study, 291 closings | Car | LAFmax energetic mean | 65.1 dB (arithmetic mean 64.4); 73.1 dB scaled to 3 m; LWA 90.6 dB | 7.5 m, 1.6 m high | ≈ 82.6 dB | [M] then [E] | quoted in Styria (2023) |
| Interior bathroom door, "closed lightly" (enough to latch), no damping | Standard interior door, latch | "db" (weighting not stated; bedroom ambient 63 dB) | 84 | 7 ft (2.1 m) | ≤ 90.6 | [M], low quality, then [E] | US 11,674,342 B2 |
| Same, "forceful shut or slam" | same | same | 95 | 2.1 m | ≤ 101.6 | [M], low quality | same |
| Same with felt pads on the jamb | same | same | 81 light / 93 slam | 2.1 m | ≤ 87.6 / ≤ 99.6 | [M], low quality | same |
| Same with a latch-bolt control actuator | same | same | 72 light / 88 slam | 2.1 m | ≤ 78.6 / ≤ 94.6 | [M], low quality | same |
| Same with felt pads and actuator | same | same | 67 light / 76 slam | 2.1 m | ≤ 73.6 / ≤ 82.6 | [M], low quality | same |
| Conventional panic device (push bar) operation in a hospital setting | Exit device | "decibels over ambient" (weighting and distance not stated) | +29 to +35 dB over a mean ambient of 44.2 dB (41–46), so about 73–79 dB | not stated | – | [M], patent | US 10,907,377 B2 |
| Damped "quiet" panic device | Exit device with elastomer at the touch points | same | about +0.01 to +15 dB over ambient | not stated | – | [M], patent | US 10,907,377 B2 |
| Door closing: controlled / fast / wind-driven slam | unspecified | "dB" | 55–65 / 70–80 / 90–100+ | not stated | – | marketing claim, no method given | Waterson |

## 1. Interior residential doors (hollow-core and solid): gentle, normal and slammed closing; opening; latch clicks; knob or lever

### Takeaway
I found no peer-reviewed or standards-grade measurement of an interior residential door closing or slamming that gives a stated distance and weighting. The only usable numbers come from a door-silencer patent: one interior door at 7 ft, 84 dB for a normal latching close and 95 dB for a slam, weighting unstated. That puts a slam about 11 dB above a normal close, and jamb pads plus latch control take 17–19 dB off. I found no numbers for opening, for the latch click alone, or for turning a knob or lever.

### Cited Findings
- Patent US 11,674,342 B2 (Riel, 2023), "Decibel Level Testing": "Testing noise output with a decibel meter 7 feet away from a door being closed. The bedroom ambient noise ... is 63 db." Each result is the mean of 3 tests "at equal closing force", and results in each category "never differed more than 3 Decibels". The door is "a standard interior door of a bathroom as heard from a bed in the adjoining room 7 feet away". "Closed lightly" is described as "not a slow close, but rather one that was pushed with enough force and speed to definitely latch but not much excess; what many would view as a normal door closure." Results: no damping 84 light / 95 slam; felt pads on the jamb 81 / 93; latch control actuator 72 / 88; both 67 / 76 — [US 11,674,342 B2 (USPTO PDF, OCR'd)](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11674342)
- Patent US 10,907,377 B2 (Architectural Builders Hardware, 2021): "A conventional door latch and door handle assembly may generate over 30 additional decibels of noise when a door is opened", against a hospital ambient of "about 40-42 decibels" — [US 10,907,377 B2](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/10907377). The same "29 to about 35 decibels" over ambient for conventional latches appears in the related latch patent US20160130846A1 — [Google Patents](https://patents.google.com/patent/US20160130846A1/en)
- A door-closer vendor gives "controlled close ~55–65 dB", "fast close ~70–80 dB", "wind-assisted slam 90–100+ dB", with no distance, method or citation (verified by fetching the page) — [Waterson USA, wind tunnel effect](https://watersonusa.com/solutions/wind-tunnel-effect)
- A Japanese building-noise survey of an apartment corridor during a period with TV, washing-machine and door opening/closing sounds reports only a period maximum of 63.1 dB (A-weighted, Fast) and LAeq 40.5 dB. It does not isolate the door events — [SKK Lab report](https://www.skklab.com/%E9%A8%92%E9%9F%B3%E6%B8%AC%E5%AE%9A%E3%83%BB%E5%88%86%E6%9E%90%E5%A0%B1%E5%91%8A%E6%9B%B8%E4%BA%8B%E4%BE%8B/%E3%83%9E%E3%83%B3%E3%82%B7%E3%83%A7%E3%83%B3%E3%81%AB%E3%81%8A%E3%81%91%E3%82%8B%E9%9A%A3%E3%81%AE%E9%83%A8%E5%B1%8B%E3%81%8B%E3%82%89%E3%81%AE%E3%80%81%E3%80%8C%E3%83%86%E3%83%AC%E3%83%93%E3%81%AE)
- Popular sites state that "door slams typically measure around 80 to 100 decibels" and that "the average door slam has approximately 90 decibels", with no distance or method. Treat these as unreliable — [Answers.com](https://www.answers.com/physics/How_many_decibels_are_in_door_slams); [sounddbmeter.com](https://sounddbmeter.com/apartment-noise-complaint-level/)

### Inferences
- The patent's 63 dB "bedroom ambient" is high for a quiet bedroom, which suggests an unweighted or C-weighted reading, or a phone meter. Its absolute values are probably inflated by low-frequency content. The **differences** between conditions are more trustworthy than the absolute levels: slam minus normal ≈ +11 dB with an undamped jamb, +12 with felt, +16 with latch control, +9 with both. Jamb pads alone ≈ −2 to −3 dB. Controlling the latch bolt ≈ −7 to −12 dB. Both together ≈ −17 to −19 dB. So the latch bolt hitting and riding over the strike is a large share of a normal close.
- The wording "as heard from a bed in the adjoining room" is ambiguous: the meter may have been on the far side of the door. If it was, the 1 m level on the near side would be higher. If it was on the same side at 2.1 m in a reverberant room, a +6.6 dB direct-field correction overstates the 1 m level. On balance, ~85–90 dB for a normal latching close and ~95–100 dB for a slam at 1 m are **upper bounds** [E].
- For a game, a defensible working range at 1 m, A-weighted Fast, is [E]: gentle close with a soft latch ≈ 60–70 dB; normal latching close ≈ 70–82 dB; hard slam ≈ 85–98 dB. Add about 8–14 dB to get the sample peak (Lpeak) for short impacts (see conventions). This bracket is consistent with the car-door data (Q4), the panic-device data (Q3) and the patent deltas, but it is not a direct measurement.

### Gaps
- No measured LAFmax, SEL or Lpeak at a stated distance for hollow-core versus solid-core interior doors, separately, was found. No study comparing door mass and construction at equal closing speed was found.
- No data was found for door opening (unlatching), a latch click on its own, or knob or lever operation. Several academic pages (ScienceDirect, ResearchGate) returned 403 and could not be checked.
- The Noise Navigator database (Berger, Neitzel and Kladden, 3M; over 1,700 entries with distances) probably contains door-slam entries, but both copies tried were blocked (Navy PDF 403, Scribd): [Navy copy](https://www.med.navy.mil/Portals/62/Documents/NMFA/NMCPHC/root/Occupational%20and%20Environmental%20Medicine/Pages/OCCUPATIONAL%20AUDIOLOGY%20AND%20HEARING%20CONSERVATION%20DIVISION/HEARING%20CONSERVATION%20PROGRAM%20ADMINISTRATION/3.6_Berger%202006_Noise%20Navigator%20Sound.pdf?ver=tZKjIdj31IoPQ0HZU54EVw%3D%3D). This is the best next source to obtain by hand.

## 2. Hotel, hospital, office, nursing-home and student-housing corridor door noise; effect of soft-close devices and silencers

### Takeaway
Hospital and nursing-home studies consistently name door closing or slamming among the top avoidable peak-noise sources. None of the papers I could open, however, report a door-specific level at a known distance. They report only whole-room LAmax and LAeq, which are useful as an envelope. UK hospital guidance asks for soft-action closers but gives no numbers.

### Cited Findings
- MacKenzie and Galbrun (2007, BSERT), acute-care wards: talking and door closing were dominant noise sources. Door closing or squeaking, bins, chair scraping, cupboard doors and ring binders were "avoidable, high-level noise events". 24 h LAeq was 51.1–60.3 dBA. About 30 % of events were "totally avoidable" — [ResearchGate abstract](https://www.researchgate.net/publication/245383320_Noise_levels_and_noise_sources_in_acute_care_hospital_wards) (full text not accessible; the door LAmax values could not be read)
- Geriatric ward (Applied Acoustics 2018): the main sources were talking or voices, door closing or squeaking, and general activity. By median of maximum levels, talking was highest, then general activity, then door closing or squeaking — [ScienceDirect](https://sciencedirect.com/science/article/pii/S0003682X17308253) (search abstract only; full text 403)
- Night-time ward study (Open Nursing Journal 2020), ceiling microphones in two bays over 52 nights: night LAeq 49.2–51.6 dBA, night LAmax 102.6–106.4 dB, peaks 94–116 dB. Patients named "doors banging" and "doors and cupboards banging" as disturbances, but the doors were not measured separately — [Open Nursing Journal 14:80](https://opennursingjournal.com/VOLUME/14/PAGE/80/FULLTEXT/)
- Busch-Vishniac (Acoustics Today, 2019): hospital sound is "peaky". The transient sources listed include "motorized doors". The paper cites the Ryherd et al. (2008) time history showing LAeq, LAFmax and LCFpeak. Daytime Leq has risen about 0.38 dB per year since 1960 — [Acoustics Today PDF](https://acousticstoday.org/wp-content/uploads/2019/09/Hospital-Soundscapes-Characterization-Impacts-and-Interventions-Ilene-Busch-Vishniac.pdf)
- HTM 08-01 (NHS England) §2.85: "Doors should be fitted with soft-action closers when located in noise-sensitive areas such as speech and language therapy, audiology...". The design checklist includes "Door-closers minimise noise generation". No dB criterion for door events is given — [HTM 08-01](https://www.england.nhs.uk/wp-content/uploads/2021/05/HTM_08-01.pdf)
- Nursing homes: staff made most of the noises loud enough to wake residents, averaging about seven noises per hour between 22:00 and 06:00 that were at least as loud as conversation. Door, drawer and cupboard opening and closing were noted for their "impulsiveness causing high instantaneous peak noise level". Reported nursing-home levels were 24–89.6 dBA — search summaries of [Sound Levels in Nursing Homes (ResearchGate)](https://www.researchgate.net/publication/51044683_Sound_Levels_in_Nursing_Homes) and [Flanders nursing homes, Applied Acoustics](https://www.sciencedirect.com/science/article/abs/pii/S0003682X1930708X) (full texts not read)
- Hotels: industry articles describe door-slam noise as a guest complaint and discuss closers and acoustic seals, but give no measured slam levels — [US 7,360,804 patent (hotel door noise suppression)](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/7360804); [YK hotel door article](https://www.yunngd.com/2026/06/05/acoustic-seal-vs-fire-door-closer-conflicts-why-hotel-guest-room-doors-fail-in-real-use/)

### Inferences
- A night LAmax above 100 dB at ceiling microphones in multi-bed bays shows that impulsive events in hard-finished hospital rooms can reach about 100 dB at a few metres. Door bangs are among the named candidates, but none is attributed.
- The silencer evidence (Q1, Q3) points to the latch strike and the leaf-to-stop impact as the two separable sources. Damping both removes about 15–20 dB of a normal close (patent data).

### Gaps
- No hotel corridor door-slam measurement (LAFmax at a stated distance, or level in the adjacent room) was found. No student-housing door study was found.
- The door-specific LAmax values in MacKenzie and Galbrun (2007) and in the geriatric-ward paper are behind paywalls (403). Those two tables are the most likely sources of a measured hospital door-closing LAmax.

## 3. Steel fire and stair doors with closers and push bars; quiet exit devices

### Takeaway
The only quantitative data are from a quiet-panic-device patent. Conventional push-bar devices added 29–35 dB over a hospital ambient of about 44 dB, so roughly 73–79 dB at an unstated distance. Elastomer-damped devices added 0–15 dB. Von Duprin's "Quiet Electric Latch Retraction" publishes no dB figure, only "significantly reduced noise versus traditional solenoids". I found no measured slam level for a steel fire door with a closer.

### Cited Findings
- US 10,907,377 B2 "Quiet panic device having sound dampening materials": "The test results disclosed that the mean ambient sound level was 44.172 decibels, ranging from about 41 decibels to about 46 decibels. The mean sound level of the noise over ambient introduced by the present panic device 10 is about 0.01 decibels. In contrast, conventional panic devices introduced a mean sound level of noise over ambient from about 29 to about 35 decibels." The claimed range is "0.01 and 15 decibels over ambient noise", and a reduction of "approximately 20-50 decibels during operation of the latch mechanism". The sources damped are touch-bar depression, push-arm linkage, latch-bolt retraction, and the dead-lock shroud striking its stop on return. No distance or weighting is given — [US 10,907,377 B2 (OCR'd)](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/10907377)
- Schlage/Allegion patent family "Door hardware noise reduction and evaluation" (US20190376331A1, US 11,220,838, US 12,467,277, US 12,560,015): it says that "certain conventional exit devices nonetheless generate noise in excess of the maximum recommended levels set forth in industry guidelines". It describes dampers, buffers and damping grease, but the fetched text gives no dB values — [Google Patents US20190376331A1](https://patents.google.com/patent/US20190376331A1/en)
- Von Duprin QEL: motor-driven latch retraction "where limited operational noise is desired" (hospitals, libraries, theatres), sold as quieter than solenoids. No dB figure is published — [Von Duprin QEL](https://www.vonduprin.com/en/products/options-accessories/quiet-latch-retraction.html)
- HTM 08-01: soft-action closers in noise-sensitive areas, with no number — [HTM 08-01](https://www.england.nhs.uk/wp-content/uploads/2021/05/HTM_08-01.pdf)

### Inferences
- If the patent's meter was at a typical listening or test position of about 1 m, a conventional push-bar actuation and latch return is about **73–79 dB(A?)**. It is reasonable to treat this as roughly the 1 m LAFmax of bar push plus latch retraction plus the bar springing back [E]. A hard slam of a heavy steel leaf against its stop, unrestrained by a closer, should exceed this. The ~95 dB slam in Q1 is a plausible ceiling.
- Push-bar mechanisms have several metal-on-metal stops, each a separate click (patent figures 6B–6D). A synthesis should give each its own impulse, with the bar return and dead-lock reset after release.

### Gaps
- No measured LAFmax or Lpeak at a known distance for a steel fire or stair door closing under a closer, slamming, or latching. No Dorma/dormakaba or Von Duprin dB marketing figures were found.
- I found no ANSI/BHMA standard that sets a dB limit for exit devices. The Schlage patent alludes to "industry guidelines" without naming them.

## 4. Automotive door closing (for method comparison) and door slams in building-acoustics guidance (BS 8233, WHO, HTM 08-01, BB93)

### Takeaway
Car doors are the best-measured door events. The energetic-mean LAFmax for modern cars is 72.4 dB at 3 m (LWA ≈ 91 dB), about 82–83 dB at 1 m. Older cars measured 72 dB at 7.5 m (LWA 97.5), and the Bavarian study's own update recommends 65 dB at 7.5 m. Single closings of the same car vary by up to about 13 dB. Building guidance sets limits on what the receiver hears (45 dB LAFmax in bedrooms at night; 40 dB LAmax in hospital wards per WHO), not on source levels of doors.

### Cited Findings
- Styria (Austria) state government, "Schallemissionsdaten für das Schließen von PKW Türen" (2023): 123 single measurements on 10 cars (model years 2005–2018), driver's door, mic at 3 m and 1.5 m high, reflecting ground. Per-car energetic means were 66.5 dB (Opel Insignia 2018) to 77.3 dB (Opel Astra 2008); overall energetic mean Lp = 72.4 dB. Single values ranged from 59.6 to 80.8 dB, and the Nissan Qashqai alone spanned 59.6–75.3. ISO 3744 on one car with 28 points over a 75.8 m² surface gave LWA,ref = 93.2 dB, which corresponds to 74.8 dB at 3 m, a ΔL of 2.4 dB, so LWA = 72.4 + 18.4 = 90.8 dB. The recommended value for prediction is LWA = 91 dB, against 97.5 dB in the Bavarian Parking Area Study (6.7 dB lower). The same report quotes the VDI study: 291 closings, energetic mean LAFmax 65.1 dB at 7.5 m (arithmetic 64.4), 73.1 dB at 3 m, LWA 90.6 dB — [Styria report PDF](https://www.umwelt.steiermark.at/cms/dokumente/12873382_25545/03653fa2/ABT15-SEL_SchallEmi_PKWT%C3%BCren_V1-2023.pdf)
- Bavarian LfU Parking Area Study (Parkplatzlärmstudie): car door LAFmax 72 dB(A) at 7.5 m, boot lid 74 dB(A) at 7.5 m, LWA 97.5 dB (cars 1990–98). The LfU now recommends 65 dB(A) at 7.5 m for a normal door closing, 7 dB lower — search summary of [LfU Parking Area Noise, 6th ed.](https://www.bestellen.bayern.de/med/67e9bb62-b4a5-11f0-81ee-c3fc7d0a3316/4b0e6a70-1059-11d9-4c85-9d915831e9eb/0/lfu_lae_00045.pdf) and [TRID: "Türen- und Kofferraumschlagen von Pkw"](https://trid.trb.org/view/1999808). The 72.0 dB and 97.5 dB figures are confirmed in the Styria report.
- BS 8233:2014 Note 4 to Table 4: "Regular individual noise events ... can cause sleep disturbance. A guideline value may be set in terms of SEL or LAmax,F ... Sporadic noise events could require separate values." The earlier specific value was removed — [Apex Acoustics, IOA 2019](https://apexacoustics.co.uk/wp-content/uploads/2019/08/Apex-Acoustics-Lmax-Noise-from-Events-p35_IOA-Conf-2019.pdf). The widely used criterion is that individual events in bedrooms should not normally exceed 45 dB LAFmax at night, and WHO says 45 dB LAFmax should not be exceeded more than 10–15 times a night — search summary of [NTi Audio](https://www.nti-audio.com/en/support/know-how/what-are-laeq-and-lafmax)
- WHO Guidelines for Community Noise (1999): hospital wards 30 dB LAeq and 40 dB LAmax at night; bedrooms 30 dB LAeq and 45 dB LAmax — [WHO Community Noise](https://docs.wind-watch.org/WHO-communitynoise.pdf) (values as summarised in the search results and in the hospital literature above)
- HTM 08-01: soft-action closers and a "door-closers minimise noise generation" check, with no source level — [HTM 08-01](https://www.england.nhs.uk/wp-content/uploads/2021/05/HTM_08-01.pdf)

### Inferences
- Car door at 1 m [E]: LWA 91 dB, hemispherical, gives LAFmax ≈ 83 dB at 1 m for a modern car. 72.4 dB at 3 m plus 9.5 dB gives ≈ 82 dB. The older-car value is ≈ 89.5 dB. Note that 1 m is inside the near field of a body about 1 m across, so this is a nominal "equivalent point source at 1 m".
- A car door (≈ 20–30 kg, sealed against rubber, closed at ≈ 1–1.5 m/s) giving ≈ 82 dB LAFmax at 1 m is a sanity anchor. A normal latching close of a building door being in the same 75–85 dB band is consistent with the patent figures.
- The spread of single closings (about 59.6–80.8 dB at 3 m, i.e. a range of about 21 dB across people and cars, and up to about 15 dB within one car) shows that a game should randomise or physically drive closing level over at least ±5 dB around the nominal value.

### Gaps
- No BB93 or ISO source data for door slams as an impact source was found. Neither BB93 nor Approved Document E text was checked directly.
- No measured car-door LCpeak or SEL at a stated distance was confirmed. A CEQA report snippet giving LCpeak 78.7–98.3 dB could not be verified (the fetched PDF did not contain it).

## 5. How level scales with closing speed or energy

### Takeaway
I found no controlled measurement of door-closing level against measured closing speed. The only paired data are "light versus forceful" closings of the same door: +11 dB undamped and +9 to +16 dB with various dampers. A marketing source claims that wind makes closing 2–4× faster with 4–10× the impact force.

### Cited Findings
- Paired light-versus-slam closings on one door at 7 ft: 84→95 (no damping), 81→93 (felt), 72→88 (latch actuator), 67→76 (both). "Closed lightly" was the minimum force to latch reliably — [US 11,674,342 B2](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11674342)
- Wind-driven closing: closing speed "2–4×" calm conditions and impact force "4–10×" a controlled close. The same page gives levels of 55–65 (controlled), 70–80 (fast) and 90–100+ (wind slam). There is no method or citation, so this is a marketing claim — [Waterson USA](https://watersonusa.com/solutions/wind-tunnel-effect)
- In car engineering, door closing velocity, closing effort and closing sound are treated as linked quality indicators. Seals, latch and air bind set the closing velocity — [Numerical Analysis of Door Closing Velocity for a Passenger Car (ResearchGate)](https://www.researchgate.net/publication/276457979_Numerical_Analysis_of_Door_Closing_Velocity_for_a_Passenger_Car) (abstract-level only)

### Inferences
- A physics-based starting point [E]: for an elastic impact, the radiated pressure amplitude scales roughly with impact velocity, so the level changes by about 20·log10(v2/v1). Impact energy scales with v², so equivalently by 10·log10(E2/E1). A "slam" at about 3–4× the velocity of a minimal latching close gives +10 to +12 dB, which matches the patent's undamped +11 dB. Hertzian contact also shortens the contact time as velocity rises, which shifts energy upward in frequency and raises the A-weighted level a little faster than 20·log v. So +11 to +14 dB for a 3–4× speed ratio is a reasonable bracket.
- Dampers compress the range more at the light end (felt −3 dB light, −2 dB slam) and latch control removes more at the light end (−12 light, −7 slam). This fits a picture in which, at low speed, the latch click dominates the event and, at high speed, the leaf hitting the stop and frame dominates.

### Gaps
- No study was found that reports SPL against measured leaf angular velocity for building doors, and none for door mass (hollow versus solid core) at fixed speed. Car-door literature on closing-velocity–sound relationships exists (sound quality of door closing, BEM prediction), but no numeric dB-versus-velocity law was retrieved: [Sound Quality Prediction of Vehicle Door Closing (ResearchGate)](https://www.researchgate.net/publication/341739704_Sound_Quality_Prediction_of_Vehicle_Door_Closing_Based_on_Experiment_and_Boundary_Element_Method).
