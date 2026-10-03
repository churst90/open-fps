# Sliding and automatic doors: construction, motion and sound sources

Research notes for a physical door-sound model in open-fps. Written 2026-10-03.

- **Part A**: residential sliding glass patio doors (aluminium and vinyl, 2-panel, operating panel on a bottom track), plus lift-and-slide.
- **Part B**: commercial automatic sliding doors (Stanley Dura-Glide, dormakaba ES 200/ESA, ASSA ABLOY SL500/UniSlide, Tormax iMotion, record STA 20, GEZE Slimdrive, Horton).
- **Part C**: rolling contact noise of small wheels (railway theory scaled down), castor and linear-guide data, friction and brush noise, gear, motor and toothed-belt noise, and published rolling-synthesis models.

Tags: **[P]** published or measured, with its URL beside it. **[E]** estimate or calculation, with the working shown. **[R]** recalled from handbooks and not re-checked in this session; treat as [E]. Section numbers (§n) refer to the same Part unless another Part is named.

The web-search budget ran out before every item was found. Each Part ends with its gaps. The biggest one: **no independent acoustic measurement of either kind of sliding door was found.** Every sound level here is an estimate or a vendor ceiling.

---

## Numbers to build from

| # | Fact | Value | Where |
|---|---|---|---|
| 1 | Patio operating panel mass | glass is 2.5 kg/m² per mm. 6 ft builder vinyl (3+3 mm) ≈ 30 kg; 8 ft aluminium (4+4 to 5+5) 45-65 kg; commercial 1/4"+1/4" 90-215 kg; lift-and-slide 150-400 kg | A §1, A §4 |
| 2 | Patio rollers | 2 tandem assemblies per panel = 4 wheels, usually 1-1/4" (31.8 mm), concave groove r = 0.125" on a crowned rail. Nylon on aluminium track, steel only on stainless (steel galls aluminium). Truth: 2 assemblies carry a 500 lb door on steel wheels, 250 lb on nylon | A §2 |
| 3 | Hertz contact, 1-1/4" wheel at about 200 N | nylon on Al: patch half-width a ≈ 0.9-1.1 mm, sink-in 40-55 µm, k ≈ 5.5-7 MN/m. Steel on stainless: a ≈ 0.3 mm, 4-5 µm, k ≈ 60-75 MN/m | A §2, C §2 |
| 4 | Contact filter | roll-off begins at V/(2πa) and the first null is at V/(2a). At 0.5 m/s: acetal/nylon ≈ 100 Hz roll-off (300 Hz null), PU ≈ 30 Hz, steel ≈ 250 Hz (≈ 800 Hz null). A door wheel can only make a low rumble from roughness; everything bright is impacts, grit, bearings or the drive | C §2.5, A §8.3 |
| 5 | Wheel-locked rates | f_rot = V/(πD): 5 Hz for 1-1/4" at 0.5 m/s, 3.5 Hz for the 64 mm automatic-door wheel at 0.7 m/s. 608 bearing BPFO ≈ 2.6 × f_rot, BPFI ≈ 4.4 × f_rot, cage ≈ 0.6 × f_rot. All sub-audio; heard as tick rates and modulation | A §8, B §3, C §2.8 |
| 6 | Grit signature | each grain: two clicks s/V apart (tandem wheels 40-60 mm, about 0.1 s at 0.5 m/s), then the other tandem L/V later (1.4-2.1 s). Hertz impact of the 30 g wheel lasts 0.3-0.6 ms, content to 2-3 kHz; wheel-on-contact-spring resonance 1.7-3 kHz | A §8.4, C §2.4, C §2.9 |
| 7 | Patio drag | measured keep-moving forces of 42-53 N on residential and light-commercial doors, 78 N on a 215 kg panel. Clean rollers are only Crr 0.002-0.005, so 30-45 N is pile and seal drag, roughly constant. Breakaway is only 1.03-1.4 × running. Limits: AAMA/NAFS Class R 135 N to start, 90 N to keep moving; CW 180/115 N; ADA 22 N | A §5 |
| 8 | Patio coasting and hand speeds | a released 40 kg panel with 35 N drag decelerates at about 0.9 m/s² and stops in about 0.4 m. Hand speeds [E]: gentle close 0.1-0.3 m/s, casual 0.3-0.7, slam 1-2 m/s | A §5 |
| 9 | Patio end of travel | pile/bulb cushion k ≈ 50 kN/m, but pile is only 6-10 mm tall, so above about 0.7 m/s it bottoms out and the hit becomes frame on frame: 10-25 ms, several kN. Restitution e ≈ 0.1-0.5. Opening end is a snap-in rubber bumper in the head track. The latch is a hook thrown by a 90° lever after the panel is home, not self-latching (anti-slam hook cams back); measured latching force 4-38 N | A §3, A §6 |
| 10 | Glass "door" tone | IGU breathing (mass-air-mass) mode ≈ 170-200 Hz on a real lite (4-16-4 ≈ 172 Hz, 3-16-3 ≈ 198 Hz), nearly independent of lite size. In-phase plate modes start at 10-30 Hz. Coincidence 12 500/t(mm) Hz (3.1 kHz for 4 mm). Installed loss factor 0.01-0.05, so T60 at 170 Hz is 0.3-1 s | A §7 |
| 11 | ANSI A156.10 (1999 and 2005 read in full) | opening speed unlimited. Closing ≤ 1 ft/s (0.305 m/s) up to 160 lb (71 kg) per leaf, √(160/W) ft/s above, which is a constant 3.4 J. Latch check ≥ 51 mm from closed. ≤ 133 N to stop a closing door, breakout ≤ 222 N, hold-open ≥ 1.5 s after loss of detection. EN 16005 low energy: ≤ 1.69 J, ≤ 67 N | B §4 |
| 12 | Installer speed settings | open 0.10-0.70 m/s per leaf (Tormax up to 1.0, default 0.70); close default 0.40 (Tormax) or ≤ 0.30 (US); creep/check 15-100 mm/s (Tormax default 21 mm/s) over the last 0-300 mm; closed hold force 30-60 N | B §5 |
| 13 | Automatic drive train | Stanley: 1/4 hp PM DC motor → jaw coupling → worm gearbox → toothed belt. dormakaba ES 200 (reseller data): Dunker GR63x55 brushed, 3350 rpm, 15:1 worm, S8M belt 12 mm, so about 24T / 61 mm pulley for 0.7 m/s. Tormax: gearless external-rotor PMSM. record: brushless with a bevel-worm gearbox and a motor brake as the lock | B §1, B §2 |
| 14 | Drive tones (all scale with leaf speed) | at 0.7 m/s: motor ≈ 55 Hz, belt mesh V/p = 87.5 Hz (8 mm pitch) or 140 Hz (5 mm pitch) whatever the pulley, worm mesh 110-165 Hz, commutator 0.4-1.4 kHz on brushed motors. Closing at 0.3 m/s puts every line about 1.2 octaves lower; in creep the belt mesh is 3-8 Hz, heard as separate tooth ticks. Belt spans ring at (1/2L)√(T/μ) and glide in opposite directions as the carriage moves | B §2, C §5, C §6 |
| 15 | Automatic-door events and levels | wheels 64 mm urethane (Stanley minimum) or nylon, 2-4 per leaf. Anti-riser gap 0.5 mm. Stops are reached at creep, so impacts are only 0.01-0.3 J (damped thup). The lock engages only after closing, mostly in NIGHT/OFF; SL500: unlocking is 2 clicks (bistable) or 1 (fail-safe), motor start delayed 0-9.9 s. Published noise: record "< 45 dB"; Tormax "< 70 dB(A)" is only a declaration ceiling. Estimates: 40-55 dB(A) at 1 m running; lock and impact transients 10-20 dB above | B §3, B §6, B §7, B §9 |

## Where the three Parts disagree, and which to use

- **Contact-filter "cut-off".** Part A §8.3 tabulates V/(2a); Part C uses V/(2πa) for the start of the roll-off and V/(2a) for the first null. Use Part C: a low-pass of about 3rd order with its corner at V/(2πa), a taken from the instantaneous load.
- **Belt mass and span frequency.** Part B assumes 0.06-0.08 kg/m for a 12-14 mm PU belt (S8M is about 5 mm thick). Part C derives about 0.018 kg/m for a 10 mm T5 (2.2 mm thick). Both scale from the same SDP mass factors (about 3 g/m per mm of width at 3.8 mm thickness), so pick by profile. Span fundamentals then range from 5-14 Hz (S8M, 2-4 m span) to 30-230 Hz (T5, 1.5-0.2 m span).
- **Opening-speed limit.** Part A originally recalled a 2.5 ft/s ANSI opening limit; Part B read the standard and found none. That line in Part A has been corrected.
- **Plain-bearing rollers.** Part C notes that many cheap rollers are a polymer wheel on a steel pin with no balls, which gives a squeak rather than ball-pass modulation. Truth's catalogue rollers (Part A) all have ball bearings. Model both.

---

# Part A. Residential sliding glass patio doors: construction and sound sources

Research notes for a physical door model. Every number carries a tag:

- **[P]** published or measured value, with the source URL next to it.
- **[E]** my own estimate or calculation. The working is shown so it can be checked or changed.
- **[R]** recalled from general engineering references and not re-verified in this session. Treat these as [E] until checked.

The session's web-search budget ran out partway through. Some items (EN 13115 sash-force classes, Hawa/Schüco damper specs, pile drag per metre, track-cap catalogue pages) could not be pinned to a source, and they are marked as such. The Part A calculations are in `patio_calc.py` beside this file.

---

### 0. Summary numbers (one screen)

| Quantity | Value | Tag |
|---|---|---|
| Operating panel, 6 ft (72") 2-panel door, 3+3 mm IGU, vinyl | ~30 kg (glass 22 kg + frame ~8 kg) | [E] |
| Operating panel, 8 ft (96") door, 4+4 mm, aluminium | ~50 kg | [E] |
| Operating panel, big LC/CW panel (1.2 x 2.1 m, 3/16"+3/16") | ~64 kg | [E] from [P] dims |
| Operating panel, 1/4"+1/4" IGU, 1.14 x 2.27 m glass (CRL R-class) | ~90 kg | [E] from [P] dims |
| Lift-and-slide sash | 200-400 kg (hardware limits 300/400 kg; Schüco up to 400-500 kg) | [P] |
| Rollers per operating panel | 2 assemblies (one per bottom corner), tandem = 4 wheels | [P] |
| Wheel diameters offered | 1", 1-1/8", 1-1/4", 1-1/2" (31.8 mm most common) | [P] |
| Wheel groove radius (concave wheel) | 0.125" (3.18 mm) +0.010/-0.000 | [P] Truth |
| Roller rating, Truth side-adjust tandem, 2 per door | 500 lb door steel wheels, 250 lb nylon | [P] |
| Roller rating, SWISCO 81-149 stainless 1-1/4" tandem | 150 lb per roller | [P] |
| Load per wheel, 30-90 kg panel on 4 wheels | 75-220 N | [E] |
| Hertz contact, nylon 1-1/4" wheel on Al crown, 200 N | a ≈ 0.9-1.1 mm, δ ≈ 40-55 µm, p0 ≈ 80-130 MPa, k ≈ 5.5-7 MN/m | [E] |
| Same, steel wheel on stainless cap | a ≈ 0.3 mm, δ ≈ 4-5 µm, p0 ≈ 0.8-1.3 GPa, k ≈ 58-75 MN/m | [E] |
| Wheel rotation rate, 1-1/4" wheel | 5.0 Hz at 0.5 m/s, 10 Hz at 1 m/s | [E] |
| Old AAMA 101-97 SGD-R15/LC25/C30 force limits | 30 lbf (135 N) to open, 20 lbf (90 N) to keep in motion | [P] |
| NAFS-11 Class R sliding door limits | initiate 135 N, maintain 90 N | [P] |
| NAFS-11 Class CW/AW sliding door limits | initiate 180 N, maintain 115 N; latch 100 N | [P] |
| NAFS-22 Class LC (as printed in one report) | 155 N initiate, 155 N maintain | [P] |
| Measured maintain-motion forces in lab reports | 42 N, 53 N, 78 N (220 kg panel), 40 N (lift-and-slide) | [P] |
| Effective friction coefficient (maintain force / panel weight) | 0.035-0.085 | [E] from [P] |
| ADA 404.2.9 sliding/folding door | 5 lbf (22.2 N) continuous force, excluding inertia and latch | [P] |
| EN 12217 force to commence motion | Class 1: 75 N, 2: 50 N, 3: 25 N, 4: 10 N, 5: 50 N | [P] |
| IGU mass-air-mass resonance, 4-16-4 | 212 Hz (infinite panel); the (1,1) antiphase "breathing" mode on a door lite is ≈ 172 Hz | [E] |
| 3-16-3 (Pella 250 standard) | 244 Hz; breathing mode ≈ 198 Hz | [E] |
| Glass coincidence frequency | 12 500 / t(mm) Hz: 3 mm 4.2 kHz, 4 mm 3.1 kHz, 5 mm 2.5 kHz, 6 mm 2.1 kHz | [P] formula |
| Lowest in-phase plate mode of a door lite (0.8 x 1.85 m, 4 mm) | ≈ 18 Hz simply supported, ≈ 33 Hz clamped | [E] |
| Lift-and-slide handle lift | handle turned 180°, leaf raised 6-8 mm | [P] secondary source |

---

### 1. Operating panel: geometry and mass

#### Takeaway

A two-panel residential slider has one operating panel and one fixed panel. Each is about half the frame width (0.9 m for a 6 ft door, 1.2 m for an 8 ft door) and about 1.95-2.0 m tall for a 6'8" door, or about 2.35 m for an 8'0" door. Glass is most of the mass. A builder-grade 6 ft vinyl door with 3+3 mm tempered glass has an operating panel of about 30 kg. An 8 ft aluminium door with 4+4 or 5+5 mm glass is 45-65 kg. Commercial-grade panels glazed 1/4"+1/4" reach 90-220 kg. The whole 6068 unit (frame, two panels and screen) weighs 79-109 kg, which agrees with 3 mm glass.

#### Cited findings

- Pella 250 Series vinyl slider: the standard dual-pane IGU is 7/8" overall with **3 mm + 3 mm** tempered glass (optional 5+5 in the NFRC table). The panel profile is a 2-3/4" x 1-3/4" uPVC extrusion with internal aluminium reinforcement, mitred and heat-fused. Vent panels sit on "two adjustable rollers" on an "anodized aluminum track". Weatherstripping is "fin-type pile around perimeter of panels". STC 28 (standard), 30 (premium), OITC 23. Frame sizes for a 2-panel door run 60-96" wide by up to 96" tall. Panel height = FH - 3". XO panel width = FW / 2. Glass height = FH - 7.25". XO glass width = FW / 2 - 4.1875". Max operating force is **12 / 10 lb** (initiate / maintain). https://media.pella.com/professional/adm/Vinyl250/Pella-250Series_Std-PrmSlidingDoor.pdf [P]
- Pella Lifestyle (clad wood) slider: panel thickness 2-1/16" (52 mm). Vent panels have "two adjustable ABEC 5 sealed electroplated steel ball-bearing rollers with organic coating, set on stainless steel track". Weatherstrip is dual-durometer extruded polymer with a bulb at the sill and interlock. IGU is 3/4" double-pane. Operating force rating is **30 / 20 lb**. https://media.pella.com/professional/adm/Clad-Wood-LS/Pella-LifestyleSeries-SlidingDoor.pdf [P]
- Pella Reserve Traditional slider: operating force rating **30 / 20 lb** initiate / maintain. https://media.pella.com/professional/adm/Clad-Wood/Pella-Reserve_SlidingDoor.pdf [P]
- All Weather Architectural Aluminum 8100 (thermally broken aluminium, CW-PG30). Overall 2400 x 2111 mm. Active panel **1220 x 2070 mm**. 1" IG with **3/16" + 3/16" tempered** glass (4.76 mm). Daylight opening 1060 x 1860 mm, glass bite 1/2". Rails and stiles are aluminium with a glass-filled nylon thermal break. **2 roller assemblies** in the bottom rail of the active panel. Sill roller track is "slip fit", with a snap-fit track filler and a snap-fit head track clip. https://www.allweatheraa.com/wp-content/uploads/2021/Testing_Information/8100-XXO-Sliding-Door-with-tubing-CW-PG-30-11.pdf [P]
- C.R. Laurence Series 3000 OXXO (aluminium, R-PG15, 4877 x 2438 mm overall). Active-panel daylight opening **1105 x 2242 mm**. 1" IG with **1/4" + 1/4" tempered** glass, channel glazed with a gasket, 5/8" bite. "1-13/16" Roller Housing", **2** roller assemblies in the active panel bottom rail. https://prod.sd02.cloud.opentext.com/ImConvServlet/imconv/d97e438aae96ee415f646c6a3e43aa18abce95d0/original?hybrisId=cyd3v0u8ig-oldcastle1-p1&assetDescr=Series+3000+-+Sliding+Glass+Door+OXXO+Test+Report+G7206 [P]
- Fleetwood 4070-T (aluminium, LC-PG50, 3-panel 5486 x 3708 mm). Panels **1861 x 3661 mm**. 1.25" IG with **1/4" + 1/4" tempered** glass. Daylight opening 1725 x 3495 mm. "Wheel assembly 6: 2 on the bottom corners of all 3 panels". https://www.fleetwoodusa.net/Documents_Guide/Products/4070-T/Test_reports/Structural/030325+A440+4070-T_XXX+216x146+s0187.02.pdf [P]
- AAMA 101/I.S.2-97 minimum test panel sizes for sliding glass doors: SGD-R15 2'10" (860 mm) panel width, 6'6" (1980 mm) frame height. LC25 3'6" x 6'8" (1070 x 2032 mm). C30 3'10" x 6'10". HC40/AW40 4'10" x 7'10" (1470 x 2390 mm). https://law.resource.org/pub/us/cfr/ibr/001/aama.101-IS2.1997.pdf [P]
- Retail shipping weights for whole 72 x 80 in two-panel units, taken from search-result snippets and not opened directly: JELD-WEN V-2500 vinyl 175 lb (79 kg); Silver Line 6/0 x 6/8 vinyl 178 lb (81 kg); MasterPiece composite 240 lb (109 kg). https://www.homedepot.com/p/JELD-WEN-72-in-x-80-in-V-2500-White-Vinyl-Right-Hand-Full-Lite-Sliding-Patio-Door-Sierra-LE-6068-RH/202035966 , https://schillings.com/products/windows-and-doors/exterior-doors/patio-doors/silver-line-6-0-x-6-8-left-hand-gliding-patio-door/ , https://www.homedepot.com/p/MasterPiece-72-in-x-80-in-Smooth-White-Left-Hand-Composite-Sliding-Patio-Door-G6068L00201/202339612 [P, snippet]
- Aluminium lift-slide / premium panoramic IGUs are often 5+27A+5 mm or 6 mm double tempered (search-result summary of a door-size guide). https://www.fabglassandmirror.com/blog/sliding-glass-door-sizes-dimensions/ [P, snippet]

#### Inferences [E]

Glass is 2500 kg/m³, so each mm of glass is 2.5 kg/m². Glass sizes below use the Pella 250 XO formulas, which are representative of vinyl doors. Aluminium doors have narrower sightlines and so slightly more glass.

| Frame (in) | Panel (m) | Glass per panel (m) | Glass area | 3+3 | 4+4 | 5+5 | 6+6 |
|---|---|---|---|---|---|---|---|
| 72 x 80 (6068) | 0.914 x 1.956 | 0.808 x 1.848 | 1.49 m² | 22.4 kg | 29.9 kg | 37.3 kg | 44.8 kg |
| 96 x 80 (8068) | 1.219 x 1.956 | 1.113 x 1.848 | 2.06 m² | 30.8 | 41.1 | 51.4 | 61.7 |
| 72 x 96 (6080) | 0.914 x 2.362 | 0.808 x 2.254 | 1.82 m² | 27.3 | 36.4 | 45.5 | 54.6 |
| 96 x 96 (8080) | 1.219 x 2.362 | 1.113 x 2.254 | 2.51 m² | 37.6 | 50.2 | 62.7 | 75.3 |

Frame mass of the panel. The panel perimeter is 5.7-7.2 m.
- uPVC with steel or aluminium reinforcement: about 0.9-1.3 kg/m, giving **6-9 kg**.
- Thermally broken aluminium: about 1.2-2.0 kg/m, giving **8-14 kg**.
- Clad wood: **10-18 kg**.
- Add about 1-2 kg for the IGU spacer and sealant, glazing gaskets, two roller assemblies (~0.15 kg each), handle and mortise lock (~0.5-1 kg), and pile.

Resulting operating-panel masses [E]:

- 6068 vinyl, 3+3: **≈ 30-33 kg** (weight ≈ 300 N).
- 6068 aluminium, 4+4: ≈ 40-45 kg.
- 8068 vinyl, 3+3: ≈ 40 kg. 8068 aluminium, 5+5: ≈ 62-66 kg.
- 8080 aluminium, 6+6: ≈ 85-90 kg.
- Lab specimens (glass computed from daylight opening plus bite; frame estimated):
  - CRL R-class: 78 kg glass, **≈ 90 kg** panel.
  - All Weather CW: 49 kg glass, **≈ 64 kg**.
  - Fleetwood LC: 185 kg glass, **≈ 215 kg**.
  - Eurotek lift-slide: 6.35 laminated + 4.76 mm on about 1.65 x 2.16 m of glass, 99 kg glass, **≈ 125 kg**.

Check against the unit weights. A 6068 vinyl unit with 3+3 has two panels of glass (45 kg), two panel frames (~15 kg), a main frame (~12-15 kg), a screen (~3 kg) and hardware (~2 kg). That totals ≈ 77-80 kg, which matches the 175-178 lb retail weights. With 4+4 glass the unit would be about 92 kg (203 lb). Most builder vinyl 6068 doors therefore carry 3 mm glass, and their operating panel is ~30 kg. The heavier 240 lb composite unit implies about 4-5 mm glass or a heavier frame.

Modelling defaults [E]:

| Door | Panel mass M | Panel W x H |
|---|---|---|
| Builder vinyl 6 ft | 30 kg | 0.91 x 1.96 m |
| Mid aluminium/vinyl 8 ft | 45 kg | 1.22 x 1.96 m |
| Premium 8 ft x 8 ft | 70-90 kg | 1.22 x 2.36 m |
| Lift-and-slide | 150-300 kg | — |

---

### 2. Rollers and track

#### Takeaway

Each operating panel sits on **two roller assemblies**, one in each bottom corner of the bottom rail. Most are **tandem** assemblies, with two wheels in one steel or stainless housing, so the panel rides on four wheels. Wheels are 1" to 1-1/2" in diameter, and **1-1/4" (31.8 mm)** is the common size. The wheel has a **concave groove** of 0.125" radius that rides on a **crowned rail**: a raised rib, or "hat", extruded into the aluminium sill. The wheel either is the outer race of a ball bearing (steel and stainless wheels) or has a nylon/acetal tyre moulded over a ball bearing. The material rule is: **nylon wheels on aluminium track; steel or stainless wheels only on a stainless track or cap**. Steel on aluminium galls the track. Height is set by an adjustment screw on the panel end or face, which drives a cam or gear in the housing and raises or lowers the panel. Truth's adjuster self-locks under load.

#### Cited findings

- Truth Hardware patio door hardware catalogue (AmesburyTruth). http://truth.com/technical-support/Patio-Door-Hardware.pdf [P]
  - "Wheel diameter: 1", 1-1/8", 1-1/4", 1-1/2"". Wheels come in "steel, stainless steel, or nylon rolling surfaces", or "steel with nylon tires".
  - "Truth's standard groove radius is .125 (+/- .010/.000)". The specification inputs include "Radius of the track crown".
  - Bearings are "full race, permanently lubricated radial ball bearings that are labyrinth-shielded to resist contamination by dust and moisture". Raceways are "machined from solid steel bar stock ... heat treated ... plated". The steel wheel is the bearing outer race.
  - Case and carrier are cold-rolled steel or stainless. The adjusting gear mechanism is high-pressure die-cast zinc.
  - Side-adjust tandem rollers: "support up to a 500 lb. door with two roller assemblies per door (nylon wheels rated up to a 250 lb. door)". Per-assembly maximum door weights in the comparison chart:
    - 39.10/39.13/39.14/39.20/39.24, 1.25" wheel: 400 lb (182 kg).
    - 39.11/39.22/39.23, nylon wheels: 250 lb (114 kg).
    - 38.10.00.xxx, 1.50" wheel: 450 lb (205 kg).
    - 1973-series, 1.25"/1.50": 500 lb (227 kg).
    - SD659/ND659, 1.25": 150 lb (68 kg).
  - End-adjust tandem rollers: "up to a 350 lb. door with two roller assemblies (nylon wheels rated up to a 150 lb. door)". Chart values: 1.00" wheels 140 lb (64 kg); 1.12" 220 lb (100 kg); 1.25" 260 lb (118 kg) typical, 350 lb (159 kg) for 1951-4968; 1.50" 280 lb (127 kg).
  - "When a sliding Patio Door system has an aluminum track, a patio door roller with a nylon wheel must be used. If a steel wheel is used, gauling will occur, resulting in short track life."
  - Designed to meet AAMA 906.3 for load, life cycle and corrosion. Truth advises against adjusting with power screwdrivers because they can over-torque the adjuster.
- Truth 13262 tandem roller: two 1-1/4" (31.8 mm) steel ball-bearing wheels in a housing 11/16" (17.5 mm) wide and 1-5/16" (33.3 mm) tall. "Roller Edge Type: Concave". Mounted with a 1/4-20 machine screw. https://www.truthparts.com/products/tandem-roller-1-1-4-wheel-1-1-4-tall-housing [P]
- SWISCO 81-149: stainless tandem, side adjust, **1-1/4" stainless concave wheels**, 5-3/4" between mounting-hole centres, "weight capacity is 150 lbs per roller". Fits Weather Shield, Andersen, Pella-era and Jeld-Wen doors among others. "The typical patio sliding glass door uses two roller assemblies, one on each bottom corner." https://www.swisco.com/Sliding-Glass-Door-Stainless-Steel-Tandem-Roller-Assembly/pd/Patio-Glass-Door-Double-Wheel-Roller-Assemblies/81-149 [P]
- SWISCO 81-095: end-adjustable housing, two steel concave ball-bearing wheels of 1-1/2" diameter. A customer Q&A on the page describes an old Pella tandem as 3-7/8" long, 1-1/8" high and 5/8" wide. https://www.swisco.com/Patio-Sliding-Glass-Door-Roller/pd/Patio-Glass-Door-Double-Wheel-Roller-Assemblies/81-095 [P]
- WRS 1-7/16" concave patio door roller, sold "with a nylon wheel or a steel wheel". https://windowhardwaredirect.com/products/wrs-right-hand-roller-nylon-or-steel-wheel [P]
- Prime-Line part names, from listings only; I did not obtain Prime-Line load ratings: D 1688 1-1/4" steel ball-bearing assembly; D 1740 1-1/2" steel ball bearing; D 1840 1" steel BB (Lupton); D 1513 1-1/4" nylon ball bearing; D 1586 1-1/2" steel BB tandem; D 1799 1-1/2" nylon concave "grooved" kit. https://www.amazon.com/Prime-Line-Products-1688-Sliding-Assembly/dp/B000I1EC2I , https://www.amazon.com/Prime-Line-Products-1586-Sliding-Assembly/dp/B00DPH7IBE [P, listing]
- Patent US 4,194,266 (Truth, 1980). The tandem carrier holds a pair of rollers on rivets that also act as axles. Height is adjusted by a cam driven through a "modified planocentric gear" (one input revolution moves the cam one gear lobe), which is "automatically self-locking in any position" and works under load. Parts are die-cast. https://patents.google.com/patent/US4194266A/en [P]
- AAMA 101-97 §2.2.19.3: rollers must conform to AAMA 906.3 and be "designed to provide easy movement and to adequately support the panel during extended usage without deforming or developing flat spots". "Rollers and locking devices shall be adjustable". https://law.resource.org/pub/us/cfr/ibr/001/aama.101-IS2.1997.pdf [P]
- JELD-WEN vinyl patio door installation guide: "Adjust rollers just high enough to clear sill track and still roll smoothly". Builder doors have the adjustment screw on each lower end of the panel; premium doors have access holes on the panel face, behind small vinyl caps. "Take weight off panel when making adjustments." The keeper is set 1/16" relative to the header; a shoot-bolt keeper snaps into the head track. https://cmd-jeld-wen.s3.us-east-2.amazonaws.com/assets/documents/1141432098.pdf [P]
- Silver Line 5800 assembled patio door guide: retract the rollers before removing the panel by turning the side screws counter-clockwise. Each roller assembly is held by two screws in the bottom rail. https://www.silverlinewindows.com/wp-content/uploads/2018/09/SLIN060-1018-Install-5800-series-v3-assembled-2-panel-patio-door.pdf [P]
- Track materials in specs: anodised aluminium track (Pella 250 vinyl). Stainless steel track (Pella Lifestyle). Sill "roller track, slip fit" plus "track filler, snap-fit" (All Weather 8100). "Rollers rested on an integral extruded aluminum roller track on the sill" with "dual nylon wheel roller assemblies" (Eurotek vinyl lift-and-slide). Sources as cited above. [P]

#### Inferences [E]

**Wheel load.**

| Panel | Load per wheel | Load per tandem assembly |
|---|---|---|
| 30 kg | 74 N | 147 N |
| 60 kg | 147 N | 294 N |
| 90 kg | 220 N | 441 N |

On a racked or badly adjusted door, one corner can carry 60-70% of the weight. Truth's 250 lb "nylon door" rating works out to 1110 N per tandem, or 280 N per wheel. Residential panels therefore run at 25-50% of the nylon rating and 10-25% of the steel rating.

**Rail geometry.** The wheel groove radius is 3.18 mm. The crown radius is not published for any product I found. I assume a crown radius of 2.5-3.0 mm, slightly smaller than the groove so the contact centres. The rib is 4-6 mm wide and stands 4-8 mm above the sill floor. A replacement stainless "track cap" is a thin (≈ 0.4-0.6 mm) stainless channel that snaps over a worn aluminium rib. That is common practice, but I could not reach a catalogue page this session.

The contact is a non-conformal ellipse. In the rolling direction the curvature radius is the wheel radius, Rx = 12.7-19 mm. Across the rail, the two near-conformal curves (crown + 3.0 mm, groove - 3.18 mm) give Ry = 1 / (1/3.0 - 1/3.18) ≈ 54 mm; for a 2.5 mm crown, Ry ≈ 12 mm. I use the equivalent sphere radius Re = √(Rx·Ry).

**Material constants [R].**

| Material | E | ν | Note |
|---|---|---|---|
| Nylon 6/6 | ~2.8-3.0 GPa dry; ~1.5-2.0 GPa when moisture-conditioned (50% RH) | 0.40 | |
| POM (Delrin) | 2.7 GPa; Wikipedia gives "tensile modulus 2700 MPa", density 1.41 g/cm³, friction on steel 0.31-0.37 | 0.35 | [P] https://en.wikipedia.org/wiki/Polyoxymethylene |
| Aluminium 6063 | 69-70 GPa | 0.33 | |
| Steel | 205 GPa | 0.29 | |
| Stainless | 193 GPa | 0.29 | |

The contact modulus is E* = 1 / [(1 - ν1²)/E1 + (1 - ν2²)/E2]:
- nylon on aluminium: 3.2 GPa (1.9 GPa for wet nylon at 1.6 GPa);
- POM on aluminium: 3.2 GPa;
- steel on aluminium: 58 GPa;
- steel on stainless cap: 109 GPa.

**Hertz results for a 1-1/4" wheel** [E]. The formulas are a = (3 F Re / 4E*)^(1/3), δ = a² / Re, p0 = 3F / (2π a²), and tangent stiffness k = 2 E* a.

| Pair, crown r | F | a (mm) | δ (µm) | p0 (MPa) | k (MN/m) |
|---|---|---|---|---|---|
| nylon/Al, 3.0 mm | 100 N | 0.88 | 27 | 61 | 5.7 |
| nylon/Al, 3.0 mm | 200 N | 1.11 | 42 | 77 | 7.1 |
| nylon/Al, 3.0 mm | 400 N | 1.40 | 67 | 97 | 9.0 |
| nylon/Al, 2.5 mm | 200 N | 0.86 | 54 | 128 | 5.5 |
| steel/Al, 3.0 mm | 200 N | 0.42 | 6 | 533 | 49 |
| steel/Al, 2.5 mm | 200 N | 0.33 | 8 | 888 | 38 |
| steel/stainless, 3.0 mm | 200 N | 0.34 | 4 | 808 | 75 |
| steel/stainless, 2.5 mm | 200 N | 0.27 | 5 | 1347 | 58 |

The 1" and 1-1/2" wheels change a by only about ±3%, because Re is dominated by Ry. Contact pressure explains the galling rule. Steel on 6063-T5/T6 aluminium (yield ~110-215 MPa) gives p0 = 0.5-0.9 GPa, several times the aluminium yield, so the rail work-hardens, flakes and galls. Nylon on aluminium gives 60-130 MPa. That is near or above nylon's own compressive yield (~ 80-100 MPa [R]). Nylon tyres therefore creep, take a set (flat spots) when a heavy panel stands still for months, and crack. That is the documented failure mode AAMA warns about ("without deforming or developing flat spots").

**Vertical bounce of the panel on its rollers** [E].
- Four nylon contacts at ~6 MN/m each give 24 MN/m. In series with the roller housing and adjuster (stamped steel carrier, die-cast cam, screw seat in a vinyl or aluminium rail), which I estimate at 2-10 MN/m per corner, the total is ≈ 6-20 MN/m.
- A 30-60 kg panel on that spring has f = (1/2π)√(k/M) ≈ **50-130 Hz**.
- Steel wheels raise the contact stiffness eight-fold, but the housing then dominates, so expect 80-200 Hz.
- This is the "carrier" resonance that grit impulses excite. Wheels that hop over grit pump energy into it.

**Rolling resistance** [E]. Hysteretic rolling of a sphere-like contact gives Crr ≈ (3/16) α (a/R) (Tabor's form), with α the hysteresis loss fraction.
- Nylon: α ≈ 0.05-0.2, a/R ≈ 0.07. That gives Crr ≈ 0.0007-0.003.
- Ball bearing torque reflected to the rim adds ≈ 0.0005-0.002 for a clean shielded bearing. Wikipedia lists hardened steel ball bearings at Crr 0.0010-0.0015 (https://en.wikipedia.org/wiki/Rolling_resistance [P]).
- Clean rollers total **Crr ≈ 0.002-0.005**, which is 0.6-3 N on a 30-60 kg panel.
- The measured maintain-motion forces in §5 are 10-30 times larger. **Seals and pile dominate the steady drag of a healthy door, not the rollers.**
- A dry or corroded bearing, a cracked tyre or a gritty track can raise roller drag to Crr 0.02-0.1. Pushing it to 10-40 N per panel is how an old door becomes "hard to slide".

---

### 3. Head guide, weatherstrip, interlock, bumpers, latch, handle

#### Takeaway

The panel has no load-bearing hardware at the top. Its top rail runs inside a channel in the head jamb, guided by the channel walls and pile, with a few millimetres of play; a lift-out panel needs that vertical clearance. Weather sealing comes from rows of **pile weatherstrip** (woven polypropylene or wool pile on a backing strip, usually with a polymer **centre fin**), sometimes with vinyl or Q-lon bulbs at jambs and sill. The fixed and operating panels overlap at the meeting stiles through hooked **interlock** extrusions, which carry pile and interlock only in the last few centimetres of closing travel.

Closing travel ends when the lock stile meets the jamb, cushioned by pile and bulb. Opening travel ends at a **bumper or stopper** in the head track, there to keep the handle off the fixed panel.

The latch is a **mortise hook lock** in the lock stile, worked by a thumb lever or handle. A 90° hub rotation swings a laminated steel hook up, or sideways, behind a **keeper** screwed to the jamb. Residential doors are not self-latching in normal use: you slide the door shut, then flip the lever. Many locks have an "anti-slam" feature, either a hook that cams back if the door is shut with the hook thrown, or a trigger that blocks throwing the hook until the panel is home.

#### Cited findings

- **Pile with centre fin, rows and locations**:
  - All Weather 8100: "Polypile with center fin": 1 row on the interlock stiles, **5 rows on the sill**, 2 rows on the jambs, 3 rows at the active-panel interlock-stile sill corner. https://www.allweatheraa.com/wp-content/uploads/2021/Testing_Information/8100-XXO-Sliding-Door-with-tubing-CW-PG-30-11.pdf [P]
  - Fleetwood 4070-T: pile gaskets at the meeting stiles and interlocks, **12 rows of pile on the sill, 6 rows on the head**, rubber gaskets at the meeting stiles, Q-lon bulb gaskets in 2 rows on each jamb. https://www.fleetwoodusa.net/Documents_Guide/Products/4070-T/Test_reports/Structural/030325+A440+4070-T_XXX+216x146+s0187.02.pdf [P]
  - CRL 3000: "Plain Pile W029330012" in the head and sill struts and interlock; "**0.270 x 0.250 Tri Fin Pile**" (backing 0.270" = 6.9 mm wide, pile 0.250" = 6.35 mm high) on the fixed interlock; "2 Finger Vinyl" at head, jambs and sill; "Super Bug Strip" on the interlock isolator. CRL test-report URL above. [P]
  - Eurotek 85 mm vinyl lift-and-slide: "Woolpile with integral center fin(s), 9.65 mm (0.380") high pile". Soft vinyl bulb/fin seal ≈ 9.9 mm bulb. Soft vinyl bulb seal ≈ 12.2 mm diameter. https://eurotek.valuewds.com/wp-content/uploads/2021/09/Eurotek-Lift-and-Slide-Structural.pdf [P]
  - Pella 250: "Fin-type pile around perimeter of panels". Pella Lifestyle: "Dual-durometer extruded polymer with bulb at sill and interlocker". Pella URLs above. [P]
- **Panel bumper / stopper**:
  - Silver Line 5800: "4" Panel bumper ... Snap panel bumper into place in interior track of head jamb next to side jamb to prevent handle from contacting stationary panel during operation." Silver Line URL above. [P]
  - American Craftsman (Ply Gem) 2-panel: a "stopper bracket" screwed to the head, "firmly seated against the stationary panel", with a head "draft plug" whose pile faces the moving panel. https://homedepot.plygemwindows.com/wp-content/uploads/2020/02/misc-5390-ac-unassembled-2-panel-door-installation-instructions-1018-1.pdf [P]
  - Truth: "Bumper guards (Truth item number 22082.XX), to keep door sash from contacting the opposite jamb during its travel, are highly recommended." Truth catalogue URL above. [P]
- **Mortise hook lock** (Truth catalogue, mortise lock section):
  - "Heat treated laminated steel bolt", all-steel case and mechanism except a cast Zamac zinc hub. Keepers are extruded 6061 aluminium.
  - "The adjustable reach hook bolt is actuated by a **90 degree rotation of the hub**". Reach adjusts from 0.040" to 0.310" (#32435/32461) or 0.140" to 0.420" (#32436/32439).
  - "Hook bolt has an 'anti-slam' feature that will retract the bolt if the door is closed with the bolt extended".
  - Nexus multi-point: "two opposing locking points (hooks)", or three with upper and lower points "to prevent the door from being lifted". Its anti-slam works by "not allowing the hooks to be extended unless the door is closed against the frame". Hook adjustment ranges are 6.3-9.6 mm.
  - Single-point mortise lock faceplate is 302.8 mm (11.92") long.
  - http://truth.com/technical-support/Patio-Door-Hardware.pdf [P]
- AAMA 101-97 §2.2.19.3.2: "Bolt and/or strike shall be designed so that no damage will result if the door is closed with the unit in locked position". §2.2.19.1: panels "shall lock or interlock with each other or shall contact a jamb member", and must be built so "panel to panel contact between horizontal members moving relative to one another does not occur". https://law.resource.org/pub/us/cfr/ibr/001/aama.101-IS2.1997.pdf [P]
- Handle position in lab specimens: All Weather latch on the lock stile, handle 3" below the latch. CRL handle 42" from the sill. Fleetwood locks 46" from the sill. Siegenia lift-slide door handle 1010 mm from the sash bottom. Sources above. [P]
- Pella 250: "Anti-Slam and double-point lock" with interior handle and thumb-lock. Pella URL above. [P]

#### Inferences [E]

- **Head clearance and rattle.** The top rail engages the head channel 10-20 mm deep, with ~1.5-4 mm side play taken up by pile. A panel set on its rollers has a vertical gap of several millimetres to the head, so it can be lifted out. The panel is therefore a tall plate pinned at the bottom by two grooved wheels and loosely held at the top by soft pile. It can rock about the rail axis through ±0.1-0.2° (2-6 mm at the top). This **rocking freedom** is where rattle comes from. A gust, a push on the handle or a hard stop makes the top rail knock across the head channel. The pile cushions it into a muffled tap, and on worn pile it becomes a plastic or aluminium click.
- **Pile drag.** None of the pages I reached publishes drag per metre. Inferred from §5: a healthy residential slider needs 40-55 N to keep moving, and of that only 1-3 N is rollers. The rest is pile and fins dragging along the head and sill (and the jamb and interlock pile near the end of travel), plus scrubbing of wheels whose axes are not parallel to the rail. The All Weather door has about 1.2 m of panel length × (5 sill rows + head rows), roughly 8-10 row-metres of pile sliding under compression. That gives **≈ 3-6 N per row-metre** for a fin pile compressed 1-2 mm. This is a fitted estimate, not a measurement.
- **Pile as a sound source.** Pile drag is broadband brush noise: thousands of fibres stick and slip on aluminium or vinyl. It is quiet, a soft "shhh" at the panel speed, filtered by the hollow sill as a resonator. A centre fin (a thin polypropylene membrane) gives a faint squeak or whisper as it bends over.
- **Interlock engagement.** The interlock overlap is ~ 15-30 mm deep. During the last 30-60 mm of closing travel the interlock pile and fin (and sometimes a rubber bumper or "Super Bug Strip") wipe along the fixed panel's interlock. Drag rises and makes a short, higher-pitched brush sound just before the stile reaches the jamb.
- **Latch event sequence (closing):**
  1. Panel slides home. The lock stile meets the jamb pile and bulb, and the panel stops with a thud.
  2. The user turns the thumb lever 90°. The hook swings up, rides the keeper's lead-in, and its spring or detent snaps it over centre. This is a sharp, small-mass steel click of a few milligrams to grams, 2-8 kHz content, under 10 ms.
  3. The hook draws the panel 0-3 mm further into the jamb, compressing the bulb.
  4. If the user slams the door with the hook already thrown, an anti-slam hook cams back over the keeper with a ratchety clack, rather than the hook hitting the keeper face.

---

### 4. Lift-and-slide doors

#### Takeaway

Lift-and-slide (German HS, "Hebe-Schiebe") panels weigh 150-400 kg and run on bogies (multi-wheel carriages) in a low threshold rail. Turning the handle through 180° drives a gear and connecting rod that raise the bogies, lifting the sash off its compressed seals by a few millimetres so it can roll. Turning the handle back lowers the sash onto its seals, which locks and seals it. Because the sash rolls with the seals unloaded, its maintain-motion force is low despite its mass: one 125 kg lab sash took 40 N. Premium systems add end-of-travel brakes and soft-close (Schüco SmartStop/SmartClose).

#### Cited findings

- Siegenia PORTAL HS 300/400 catalogue: sash weight max 300 kg (HS 300) or 400 kg (HS 400). Sash width 720-3335 mm, sash height 1175-2690 mm, frame width up to 6700 mm. Gear backset 37.5 mm. Door handle height 1010 mm. Comfort gear recommended at ≥ 200 kg and required at ≥ 300 kg. Bogie wheels are front (V) and rear (H), with additional middle bogies (M) for 300-400 kg. "Stop" and "bag stop" parts are listed per sash. https://catalog.siegenia.com/portal_pk_hs300_en/pubData/source/portal_pk_hs300_en.pdf [P]
- Siegenia PORTAL HS 400 product page: "300-400 kg" set in motion, **8 rollers** distributing the load, bogie 12 mm flatter than standard, ~3 mm height-adjustment reserve, opening widths up to 12 m. https://www.siegenia.com/en/products/sliding-door-systems/lift-and-slide/portal-hs-400kg [P]
- Schüco ASS 70.HI: vent weights up to 300 kg, special design up to 400 kg. Optional "Schüco SmartStop and Schüco SmartClose technology for safe and easy handling of the mobile sash". Interlock 48 mm. https://germansystemwindows.com/schueco-products/schuco-sliding/ass-70-hi-2/ [P]
- Secondary source (vitrums.co.uk review): ASS 70.HI manual up to 400 kg leaf, 250 kg with e-slide motor. ASS 77 PD up to 500 kg per sash. "Rotating the handle through 180° drives a cam mechanism that physically lifts the leaf 6-8 mm off its weather seal". https://www.vitrums.co.uk/blog/schuco-lift-and-slide-doors-review-uk [P, secondary]
- Eurotek 85 mm vinyl lift-and-slide, CW-PG35, NAFS-11:
  - sash 1803 x 2311 mm; glass 6.35 mm laminated + 4.76 mm annealed with a 12.76 mm stainless spacer;
  - "Dual nylon wheel roller assemblies" near each end of the bottom rail on an integral extruded aluminium roller track;
  - "The swing handle also actuated the rollers, lifting the panel for operation";
  - measured **initiate 44 N, maintain 40 N opening / 36 N closing, latches 18 N** (allowed 180 / 115 / 100 N).
  - https://eurotek.valuewds.com/wp-content/uploads/2021/09/Eurotek-Lift-and-Slide-Structural.pdf [P]

#### Inferences [E]

- The Eurotek sash is ≈ 125 kg, and 40 N maintain gives μ_eff ≈ 0.033. That is lower than the standard sliders in §5 because the sash rolls with its seals unloaded.
- Lift-and-slide sound events, in order:
  1. Handle rotation: gear and rack whir, a rising cam load, then a dull "unseat" as the sash lifts 5-8 mm.
  2. Low-speed rolling of a heavy mass on 4-8 nylon or steel wheels: low-frequency rumble, very little rattle because the mass is large.
  3. Arrival at an end stop or damper.
  4. Handle return: the sash drops onto its seals with a soft, heavy "settle" of 150-400 kg falling ~5 mm onto EPDM bulbs.
- At 200 kg and 0.5 m/s the kinetic energy is 25 J. That is why hardware makers sell dampers and brakes for these doors.

---

### 5. Operating forces: standards and measured values

#### Takeaway

North American standards cap the **force to start the panel moving** ("initiate") and the **force to keep it moving** ("maintain"), measured with a force gauge per ASTM E2068.

| Standard / class | Initiate | Maintain |
|---|---|---|
| AAMA 101-97, residential through commercial SGD | 135 N (30 lbf) | 90 N (20 lbf) |
| AAMA 101-97, HC/AW | 180 N (40 lbf) | 115 N (25 lbf) |
| NAFS-11 Class R | 135 N | 90 N |
| NAFS-11 Class CW and AW | 180 N | 115 N |
| NAFS-11 latching | 100 N max | — |

NAFS-17 harmonised the US and Canadian requirements and added initiating-force criteria; maintain force was unchanged. Pella advertises 12/10 lb (53/44 N) for its vinyl door and 30/20 lb for its wood doors. Measured lab values on real doors are far below the limits: 42-78 N maintain, even for a 215 kg panel.

Europe classifies doorsets by EN 12217, tested per EN 12046-2: force to commence motion of 75 / 50 / 25 / 10 N for Classes 1-4. ADA requires ≤ 5 lbf (22 N) continuous force on sliding doors, excluding inertia and latch release.

#### Cited findings

- AAMA/NWWDA 101/I.S.2-97 §2.2.19.5.1: "Maximum Force to Open / Force to Keep in Motion":
  - SGD-R15, SGD-LC25, SGD-C30: 30 lbf (135 N) / 20 lbf (90 N).
  - SGD-HC40, SGD-AW40: 40 lbf (180 N) / 25 lbf (115 N).
  - "Each movable panel shall be adjusted before any tests ... No further adjustment that would affect the operating force shall be made".
  - Deglazing loads are 70 lbf (320 N) on vertical rails and 50 lbf (230 N) on others.
  - https://law.resource.org/pub/us/cfr/ibr/001/aama.101-IS2.1997.pdf [P]
- CRL Series 3000 OXXO, NAFS-11 R-PG15: **initiate 46.7 N (10.5 lbf), allowed 135 N; maintain 42.3 N (9.5 lbf), allowed 90 N; locks 4.4 N, allowed 100 N.** CRL test-report URL in §1. [P]
- All Weather 8100, NAFS-11 CW-PG30: **initiate 75 N (17 lbf), allowed 180 N; maintain 53 N (12 lbf), allowed 115 N; latches 36 N (8 lbf), allowed 100 N.** All Weather URL in §1. [P]
- Fleetwood 4070-T, NAFS-22 LC-PG50, 1861 x 3661 mm panels: **initiate 80 N (18.0 lbf), maintain 78 N (17.5 lbf)**, each "155 N (35.0 lbf) max" as printed; locks 37.8 N, report only. Fleetwood URL in §1. [P]
- Eurotek lift-and-slide (CW, NAFS-11): 44 / 40 / 36 N, against 180 / 115 N allowed (§4). [P]
- WDMA NAFS equivalency memo: "Operating forces have changed. In certain cases, products rated under NAFS-17 cannot claim compliance with previous editions". "Operating force for US and Canada have been combined and include initiating force criteria. Force to maintain motion unchanged." Class R hung windows went from 30 to 35 lb and LC hung from 35 to 40 lb. https://www.wdma.com/assets/docs/TechnicalCenter/tb-equivilancy-memo_nafs_201.pdf [P]
- RDH: NAFS-17 operating forces for windows and sliding doors of all classes are in Table 5.4 (R, LC) and Table 7.3 (CW, AW). https://www.rdh.com/nafs-2017-public-review-in-progress-until-february-5-2017/ [P]
- Pella 250 vinyl: 12 / 10 lb (53 / 44 N). Pella Lifestyle and Reserve wood: 30 / 20 lb. Pella URLs in §1. [P]
- EN 12217:2015 Table 1 (tests per EN 12046-2). Scope: "hinged/pivoted and sliding doorsets with latches, for pedestrian use". https://cdn.standards.iteh.ai/samples/41864/a5f1317267634277bb1a6028625213b3/SIST-EN-12217-2015.pdf [P]

  | | Class 1 | Class 2 | Class 3 | Class 4 | Class 5 |
  |---|---|---|---|---|---|
  | Closing force or force to commence motion, max (N) | 75 | 50 | 25 | 10 | 50 |
  | Hand-operated hardware, max torque (Nm) | 10 | 5 | 2.5 | 1 | 5 |
  | Hand-operated hardware, max force (N) | 100 | 50 | 25 | 10 | 50 |
  | Finger-operated hardware, max torque (Nm) | 5 | 2.5 | 1.5 | 1 | 1.5 |
  | Finger-operated hardware, max force (N) | 20 | 10 | 6 | 4 | 6 |

- EN 12046-2 test apparatus: rigid frame, force applied by actuator or weights and pulleys, ±5% accuracy, 1 N / 0.1 Nm resolution (summary from https://standards.iteh.ai/catalog/standards/cen/fcfd8ba8-56c6-48ca-beb2-75698cd60cc2/en-12046-2-2025). [P, summary]
- EN 13115 (windows): Jansen gives the hardware classes as "class 1 = 100 Nm, class 2 = 30 Nm". These are probably 100 N / 30 N force limits with the unit mistranscribed. https://www.jansen.com/en/building-systems-profile-systems-steel/topics/design/operation.html [P, questionable]. I could not retrieve EN 13115's sash-movement force limits for sliding windows. From memory [R], they are of the order of 150 N (Class 1) and 100 N (Class 2) to put the sash in motion. Unverified.
- ADA 2010 §404.2.9: sliding or folding doors 5 lbf (22.2 N) max; the force "pertains to the continuous application of force necessary to fully open a door", excluding latch retraction and inertia. https://www.corada.com/documents/2010ADAStandards/404-2-9 [P]

#### Inferences [E]

**Effective friction coefficient**, μ_eff = maintain force / panel weight, with panel masses from §1:

| Door | Panel | Maintain | μ_eff | Initiate | μ_init |
|---|---|---|---|---|---|
| CRL R, 1/4+1/4 | ≈ 90 kg | 42 N | 0.048 | 47 N | 0.053 |
| All Weather CW, 3/16+3/16 | ≈ 64 kg | 53 N | 0.085 | 75 N | 0.12 |
| Fleetwood LC, huge | ≈ 215 kg | 78 N | 0.037 | 80 N | 0.038 |
| Eurotek lift-slide | ≈ 125 kg | 40 N | 0.033 | 44 N | 0.036 |
| Pella 250 vinyl, 3+3 (rating, not measurement) | ≈ 35 kg | ≤ 44 N | ≤ 0.13 | ≤ 53 N | ≤ 0.15 |

Model consequences:

1. **The steady resistance is mostly a constant seal drag, F_seal ≈ 30-50 N, plus a small load-proportional term**, μ_roll ≈ 0.003-0.01 for a healthy door. For a 30-60 kg residential panel, use F_drag ≈ 30-45 N, giving μ_eff 0.06-0.12.
2. **Breakaway is only 5-40% above running drag.** Pile and ball bearings have little static/kinetic difference; the larger breakaway on the All Weather door, 75 vs 53 N, is pile and fin set plus the latch area. There is no strong stick-slip on a healthy door. On a dirty door, grit and a dry bearing add a ratchety breakaway.
3. **Coasting.** Released at speed v, the panel decelerates at a = F_drag / M. For 35 N on 40 kg that is 0.9 m/s², so from 0.8 m/s it stops in 0.9 s over 0.37 m. For 40 N on 90 kg it is 0.44 m/s², stopping in 1.8 s over 0.73 m. A heavy door glides; a light vinyl door stops quickly once you let go.
4. **Hand speeds.** I found no published measurement. My estimates: a normal opening covers the 0.75-1.1 m stroke in 1-2 s, with peak panel speed 0.6-1.0 m/s. A gentle close arrives at the jamb at 0.1-0.3 m/s. A casual push-and-let-go close arrives at 0.3-0.7 m/s. A slam is 1.0-2.0 m/s. A person can push 100-200 N briefly with one hand, which gives a 40 kg panel 1.5-4 m/s² of net acceleration. A 1 m/s slam is therefore easy within 0.3 m of travel.
5. For comparison (ANSI/BHMA A156.10, power sliding doors, read in full in Part B §4): closing speed is limited to 1 ft/s (0.305 m/s) for leaves up to 71 kg; opening speed is not limited by the standard, and installers set 0.5-0.7 m/s per leaf. That brackets "comfortable" hand speeds.

---

### 6. End of travel: stops, bumpers, rebound, soft-close

#### Takeaway

On a residential slider, closing travel ends when the lock stile hits the jamb. The impact goes through the jamb pile, any bulb seal, and the hook-and-keeper geometry. Opening travel ends at a snap-in plastic or rubber bumper in the head track, or at a stopper bracket against the fixed panel. Neither is a damper: they are elastomer pads, so a fast panel thuds and rebounds. Premium aluminium and lift-and-slide systems offer end-of-travel brakes or soft-close: Schüco SmartStop/SmartClose, and Hawa/Siegenia-type damper and catch units. I did not find their damper specifications this session.

#### Cited findings

- Panel bumper in the head track (Silver Line); stopper bracket against the stationary panel (Ply Gem/American Craftsman); Truth bumper guards. See §3 URLs. [P]
- Schüco SmartStop and SmartClose are offered on ASS 70.HI. See §4 URL. [P]
- Siegenia lists "Stop" and "Bag stop" parts in every HS hardware set. See §4 URL. [P]

#### Inferences [E]

The panel is effectively rigid in-plane for this purpose. Its impact against a pad of stiffness k is a half-sine of duration T = π√(M/k) and peak force F = v√(Mk):

| M, v | KE | pile/bulb, k ≈ 50 kN/m | rubber bumper, k ≈ 200 kN/m | hard frame, k ≈ 2 MN/m |
|---|---|---|---|---|
| 40 kg, 0.3 m/s | 1.8 J | 89 ms, 424 N | 44 ms, 849 N | 14 ms, 2.7 kN |
| 40 kg, 0.7 m/s | 9.8 J | 89 ms, 990 N | 44 ms, 2.0 kN | 14 ms, 6.3 kN |
| 40 kg, 1.5 m/s | 45 J | 89 ms, 2.1 kN | 44 ms, 4.2 kN | 14 ms, 13 kN |
| 70 kg, 0.7 m/s | 17 J | 118 ms, 1.3 kN | 59 ms, 2.6 kN | 19 ms, 8.3 kN |
| 120 kg, 0.7 m/s | 29 J | 154 ms, 1.7 kN | 77 ms, 3.4 kN | 24 ms, 10.8 kN |

What this means for the sound:

- A gentle close (0.1-0.3 m/s) is absorbed by the pile/bulb over 50-150 ms. That is a soft, low "thup" with almost no structural ring.
- Pile is only 6-10 mm high. At 0.7 m/s and above it bottoms out (the 50 kN/m spring compresses 30-60 mm, more than the pile is tall), and the impact becomes **frame on frame**: aluminium or vinyl stile on jamb, ~10-25 ms, kilonewtons. That excites the glass "breathing" mode at ~170-250 Hz (§7), aluminium stile and jamb modes from hundreds of Hz into kHz, and a rattle of the latch hook and loose hardware.
- **Rebound.** Pile and bulbs have a coefficient of restitution of roughly e ≈ 0.1-0.3. A hard stop on a rubber bumper gives e ≈ 0.3-0.5, so the panel bounces back 10-50 mm at low speed. It may come back to touch the stop a second time, 0.1-0.4 s later, after the seal drag has stopped it.
- **Soft-close dampers** generally catch the leaf in the last 50-100 mm and decelerate it to a few cm/s. Without a source I would model one as an engagement "tick" (the catch picking up the leaf), then a 0.5-1.5 s viscous approach, then a soft final contact.

---

### 7. Glass panel acoustics: IGU modes, mass-air-mass, coincidence, damping

#### Takeaway

An insulated glass unit is two thin glass plates coupled by an air spring. When struck at the frame, it has three relevant families of motion:

1. **In-phase plate modes**, where both panes move together and the air is barely compressed. They start very low, 8-30 Hz for a door lite, and are dense. They radiate poorly at low frequency, but they set the overall "boom".
2. **The antiphase "breathing" mode**, where the panes move against each other and compress the cavity. This is the **mass-air-mass resonance**. It is ≈ 212 Hz for 4-16-4 as an infinite panel, and ≈ 170 Hz for the (1,1) breathing mode of a real door lite. For the 3-16-3 glass of builder vinyl doors the numbers are 244 Hz and ≈ 198 Hz. This is the low, hollow "bomp" tone of a slammed patio door.
3. **High-order bending modes up to and above the coincidence frequency**, 12 500 / t Hz (3.1 kHz for 4 mm). There the plate radiates efficiently, which is the "ring" or "tink" when the glass itself is knocked.

The edge glazing gaskets and tapes provide most of the damping.

#### Cited findings

- Saint-Gobain Glass UK, "Factors influencing glazing performance". Critical frequency "f_CRIT (Hz) = 12500 / t", with t in mm. Mass-air-mass "f_res = (1/2π) √(ρ0 c0² / d · (m1 + m2)/(m1 m2))". Worked values for 4 mm + 6 mm air-filled: d = 6 mm 316 Hz; 12 mm 223 Hz; 16 mm 193 Hz; 20 mm 173 Hz; 24 mm 158 Hz. Identical pane thicknesses overlap their coincidence dips and "cause the glass to vibrate together". https://www.saint-gobain-glass.co.uk/wp-content/uploads/2024/02/Acoustics-3B-Factors-Influencing-Glazing-Performance-17-09-2018.pdf [P]
- J. D. Quirt, "Sound transmission through windows", Canadian Building Digest (NRC), 1988:
  - coincidence near 5 kHz for 2 mm glass, and in the 500 Hz band for 18 mm;
  - mass-air-mass resonance "falls within the frequency range of 200 to 400 Hz for typical factory-sealed double glazing";
  - STC rises about 3 per doubling of airspace;
  - "soft resilient seals (such as neoprene gaskets) can increase the low frequency TL by several dB";
  - laminated glass damping is temperature dependent.
  - Quirt's formula, standard form f = 1200 √[(t1 + t2)/(t1 t2 d)] with t and d in mm, is not extractable from the PDF; it agrees with my numbers to 1 Hz.
  - https://nrc-publications.canada.ca/eng/view/ft/?id=5468d3d7-d326-4d2a-9e80-ecc8b5bec651 (doi:10.4224/20330959) [P]
- Pella 250 slider with 3+3 mm, 7/8" IGU: STC 28-30, OITC 23. https://media.pella.com/professional/adm/Vinyl250/Pella-250Series_Std-PrmSlidingDoor.pdf [P]

#### Inferences [E]

Constants: ρ0 = 1.21 kg/m³, c = 343 m/s, so ρ0c² = 1.424 × 10⁵ Pa. Glass m' = 2.5 kg/m² per mm, E = 72 GPa, ν = 0.22.

**Mass-air-mass, infinite panel.** The 4-16-4 calculation:
- m1 = m2 = 10 kg/m², so (m1 + m2)/(m1 m2) = 0.2 m²/kg.
- f = (1/2π) √(1.424e5 × 0.2 / 0.016) = (1/2π) × 1334 = **212 Hz**.

| Make-up | f_mam |
|---|---|
| 3-12-3 | 283 Hz |
| 3-16.2-3 | 244 Hz |
| 4-12-4 | 245 Hz |
| 4-16-4 | 212 Hz |
| 5-12.7-5 | 213 Hz |
| 4.76-15.9-4.76 (3/16" in a 1" unit) | 195 Hz |
| 6-19-6 (1/4" in a 1.25" unit) | 159 Hz |

**Finite lite: the (1,1) breathing mode.** Take a simply supported plate with mode shape sin(πx/a) sin(πy/b). Its volume displacement per unit amplitude is (4/π²)·A. The generalised modal mass per pane is m'A/4. With both panes antiphase, the cavity pressure is p = ρ0c² · 2(4/π²) w0 / d. The added generalised stiffness per pane is then 2 ρ0c² (16/π⁴) A / d. That gives:

- ω²_breath = ω²_11 + (128/π⁴) ρ0c² / (m' d) = ω²_11 + **0.657** ω²_mam (for equal panes).

| Lite size | Glass | In-phase f11 | Breathing f |
|---|---|---|---|
| 0.81 x 1.85 m (6 ft door) | 3-16-3 | 13.6 Hz | 198 Hz |
| 0.81 x 1.85 m | 4-16-4 | 18.1 Hz | 173 Hz |
| 0.81 x 1.85 m | 5-12.7-5 | 22.7 Hz | 174 Hz |
| 0.81 x 1.85 m | 6-19-6 | 27 Hz | 132 Hz |
| 1.11 x 1.85 m (8 ft door) | 4-16-4 | 11.0 Hz | 172 Hz |

The breathing mode barely depends on lite size, because the air spring dominates. That makes it a good material-and-geometry-derived constant for each glass make-up. Higher breathing modes ((1,3), (3,1), ...) have little net volume change, so the air couples them only weakly. They fall back toward the in-phase plate frequencies, and the cavity's own acoustic modes start at c/2L ≈ 90-200 Hz along the lite and at c/2d ≈ 10 kHz across the gap.

**In-phase plate modes, simply supported**, f_mn = (π/2)√(D/m')·[(m/a)² + (n/b)²] with D = E t³ / [12(1 - ν²)]:
- 4 mm, 0.81 x 1.85 m: f11 = 18 Hz, f12 = 27, f13 = 41, f21 = 64 Hz.
- 3 mm on the same lite: 13.6 / 20 / 31 / 48 Hz.
- 6 mm: 27 / 40 / 62 / 96 Hz.
- Clamped edges raise f11 by about 1.8×. Real glazing (gasket or tape in a 1/2-5/8" bite) is between simply supported and clamped.
- Mode density per pane is n(f) = A √(m'/D) / 2 ≈ A·√3 / (c_L t), constant in f. For 4 mm and 1.5 m² that is ≈ 0.12 modes/Hz, one every ~8 Hz. Above a few hundred Hz the plate response is effectively continuous.
- Bending wave speed at 1 kHz: 173 m/s (3 mm), 200 m/s (4 mm), 245 m/s (6 mm). Coincidence, where it equals 343 m/s, is at 4.2 / 3.1 / 2.1 kHz.

**Radiation.** Below coincidence, a single pane radiates poorly except at its edges and corners. The breathing mode radiates like a monopole pair, one pane on each side of the door, each pushing air outward in turn. The tone you hear on each side is the outer pane's breathing-mode radiation. It is efficient at 170-250 Hz on a 1.5-2 m² lite (ka ≈ 3-5).

**Damping.** Tempered soda-lime glass has material loss η ≈ 0.001-0.003 [R]. Installed lites are dominated by edge losses into gaskets and glazing tape, η ≈ 0.01-0.05 [R/E]. Decay time T60 = 2.2 / (f·η):

| f | η = 0.005 | η = 0.02 | η = 0.05 |
|---|---|---|---|
| 170 Hz | 2.6 s | 0.65 s | 0.26 s |
| 1 kHz | 0.44 s | 0.11 s | 0.044 s |
| 3 kHz | 0.15 s | 0.037 s | 0.015 s |

A dry-glazed aluminium door with hard gaskets (η ≈ 0.01) rings audibly at the breathing mode for about 1 s. A vinyl door with foam tape and silicone-backed glazing (η ≈ 0.03-0.05) gives a short "bomp" of 0.2-0.4 s. Laminated glass (Eurotek exterior pane) raises η to 0.05-0.2 above ~500 Hz at room temperature, so a laminated lite does not "tink".

**Two identical panes.** The panes of equal-thickness IGUs share their coincidence frequency, and Saint-Gobain notes they "vibrate together". Their breathing and in-phase modes are cleanly separated, so a single impact gives a clean two-component sound: a low in-phase thump below 50 Hz, mostly felt rather than heard, plus a breathing tone near 170-250 Hz. Mixed thickness (4 + 6) splits and smears these.

---

### 8. What makes the sound: source inventory with numbers

#### Takeaway

A sliding patio door's sounds, roughly in order of how often you hear them:

1. **Rolling rumble.** Wheel-on-rail roughness and grit, filtered through the contact patch, the roller housing spring and the panel. The level and character depend strongly on the wheel material: nylon is a soft low rumble, steel on stainless a brighter "zing" or hiss.
2. **Pile and seal brush.** Broadband, quiet, constant through travel. It is louder at the interlock in the last few centimetres.
3. **Periodic wheel defects** at the wheel rotation rate, **5 Hz per 0.5 m/s for a 1-1/4" wheel**. Flat spots, cracked tyres and wobble are heard as a "clack-clack" at 3-15 Hz, in groups: two wheels per tandem, two tandems per panel.
4. **Grit events.** Each grain under the rail is crushed or climbed four times, as a pair of quick clicks (the two tandem wheels, about 50 mm apart), then a gap of panel length divided by speed, then another pair.
5. **Bearing noise.** Ball-pass rates of tens of Hz modulating a broadband hiss. Worse when dry or corroded.
6. **End-of-travel impact.** A cushioned thup at low speed. At higher speed, frame-on-frame, the IGU breathing tone at 170-250 Hz, aluminium ring, rebound and a second touch.
7. **Panel rattle** in the head channel and at loose glazing beads, triggered by impacts or by handling.
8. **Latch.** The thumb-lever rotation (plastic or zinc detent), the hook striking the keeper, and an over-centre snap.

#### Cited findings

- AAMA 101-97: rollers must not develop flat spots under extended use, which identifies the flat-spot failure mode. URL in §2. [P]
- Truth: steel wheels on aluminium track gall, giving short track life. Bearings are labyrinth-shielded "to resist contamination by dust and moisture". URL in §2. [P]
- A SWISCO customer report of a failing roller that "appears that it started to lose its ball bearings" after about one year on a heavy 50" double-pane door. https://www.swisco.com/Sliding-Glass-Door-Stainless-Steel-Tandem-Roller-Assembly/pd/Patio-Glass-Door-Double-Wheel-Roller-Assemblies/81-149 [P, anecdote]
- Patent US 11,220,838 (door hardware noise reduction): names latchbolt impact on the strike, rattle, impact and sliding engagement of parts as noise sources, and evaluates hardware noise with a test system. No numbers are published. https://patents.google.com/patent/US11220838B2/en [P]
- Rolling resistance: railway steel wheels on steel rail have Crr 0.0003-0.0004; hardened steel ball bearings 0.0010-0.0015 (b ≈ 0.1 mm). https://en.wikipedia.org/wiki/Rolling_resistance [P]
- **No published acoustic measurement of residential sliding-door rolling noise was found** in the material I could reach. All levels below are estimates.

#### Inferences [E]

**8.1 Wheel rotation and periodicities.**

| Wheel | Circumference | v = 0.2 m/s | 0.5 m/s | 1.0 m/s | 1.5 m/s |
|---|---|---|---|---|---|
| 1" | 79.8 mm | 2.5 Hz | 6.3 Hz | 12.5 Hz | 18.8 Hz |
| 1-1/4" | 99.7 mm | 2.0 | 5.0 | 10.0 | 15.0 |
| 1-1/2" | 119.7 mm | 1.7 | 4.2 | 8.4 | 12.5 |
| 1-3/4" | 139.6 mm | 1.4 | 3.6 | 7.2 | 10.7 |

- A flat spot of depth 0.05 / 0.1 / 0.3 mm on a 1-1/4" nylon tyre has a chord of 2.5 / 3.6 / 6.2 mm. Each pass drops the corner by up to the flat depth, filtered by the Hertz spring, giving one dull "tock" per revolution per wheel.
- Four wheels whose diameters differ by tolerance (±0.05 mm, about ±0.2%) produce four nearly equal rates that drift in and out of phase over tens of revolutions. The result is an irregular "clack...clack-clack" rather than a clean metronome.
- A cracked nylon tyre (radial crack) gives a sharper click per revolution, because the crack edges snap together as they leave the contact.
- Wheel wobble (a loose rivet axle, as in US 4,194,266 rivets) makes the groove rub the crown sideways once per revolution: a squeaky scrub at f_r and its harmonics.

**8.2 Bearing frequencies.** On Truth-style wheels the wheel itself is the outer race. Assume the balls run on a pitch diameter of ~15-20 mm, with 8-14 balls of ~2.5-3.5 mm (not published). The ball-pass frequency over the rotating outer race is ≈ (n/2)(1 - d/Dp) f_r ≈ 3.5-6 × f_r, which is **≈ 18-30 Hz at 0.5 m/s**. Cheap rollers with uncaged ("full complement") balls and stamped races add random ball-on-ball clatter. A dry or contaminated bearing produces a rough, gravelly broadband noise of 1-6 kHz, amplitude-modulated at that ball-pass rate. A "ball lost" bearing adds an irregular knock and a wheel tilt.

**8.3 Roughness excitation and the contact filter.** As in railway rolling noise, roughness of wavelength λ excites f = v/λ, but wavelengths shorter than the contact length 2a are averaged out. The table gives f = v/(2a), which is the contact filter's **first null**; the roll-off starts about π times lower, at v/(2πa) (Part C §2.5, which also tabulates the attenuation). Read these as "by here the roughness input is gone":

| Pair | 2a | v = 0.2 m/s | 0.5 m/s | 1.0 m/s |
|---|---|---|---|---|
| Nylon on aluminium | ≈ 2-3 mm | ≈ 70 Hz | ≈ 170 Hz | ≈ 330 Hz |
| Steel on stainless/aluminium | ≈ 0.6-0.8 mm | ≈ 330 Hz | ≈ 830 Hz | ≈ 1.7 kHz |

This is the physical reason nylon wheels sound like a muffled low rumble and steel wheels on a stainless cap have a bright metallic "zing". Steel passes roughness components up to the kHz range at walking speed, where the aluminium sill, the rail, and the panel's stile and glass modes radiate well. The contact patch scales as a ∝ (F R / E*)^(1/3), so it is set by load, wheel size and material, not tuned per door.

**8.4 Grit.** A grain of height h under a wheel of radius R is approached over x = √(2Rh). For a 1-1/4" wheel:

| h | x | Duration at 0.5 m/s |
|---|---|---|
| 0.05 mm | 1.3 mm | 2.5 ms |
| 0.2 mm | 2.5 mm | 5 ms |
| 0.5 mm | 4.0 mm | 8 ms |

- A nylon tyre partly envelops grains smaller than about 2δ (≈ 0.1 mm) and squeezes over them. A steel wheel climbs them or crushes them: sand (quartz, much harder than steel) gets pressed into the aluminium crown and leaves pits that click on every later pass.
- Each grain event is a vertical displacement impulse on the panel's carrier spring (50-200 Hz bounce), plus a crushing crack in the kHz.
- The **tandem structure** gives each grain a signature. Wheel 1 and wheel 2 of the same assembly hit it Δt₁ = s/v apart, with s ≈ 40-60 mm (assemblies are 3-7/8" to 5-3/4" long), so **0.08-0.12 s at 0.5 m/s**. The second assembly reaches the grain Δt₂ = L/v later, where L is the distance between assemblies ≈ panel width - 0.1-0.2 m ≈ 0.7-1.05 m, so **1.4-2.1 s at 0.5 m/s**. Sweeping grit along the track makes it a moving pattern.

**8.5 Pile brush.** The drag is ≈ 30-45 N on a residential panel (inferred in §5), spread over 5-15 row-metres of pile. The sound is broadband hiss with a soft spectrum centred maybe 2-8 kHz, at a level well below the rolling noise, perhaps 30-40 dBA at 1 m [E]. It is constant in amplitude with speed above ~0.1 m/s. A dry fin can squeak at the interlock.

**8.6 Panel rattle and the head channel.** The panel is a tall rigid body on a line support (two wheels in grooves) with a loose top restraint. Its rocking mode about the rail, with the top-channel pile as the spring, is low, ≈ 3-10 Hz. When the panel is jolted (grit hop, end stop, latch), the top rail knocks side to side in the channel: one to three soft knocks, 50-150 ms apart.

**8.7 Glass ringing.** Covered in §7. Roller events below 300 Hz couple into the panel's in-phase and breathing modes. Impacts at the stile excite both. The breathing tone is the single most identifiable "glass door" colour: **≈ 170-250 Hz**, set by glass thickness and gap and nearly independent of lite size.

**8.8 Thud into jamb and interlock.** See the impact table in §6. With pile bottomed out, the frame-to-frame contact is 10-25 ms. That excites the stile and jamb aluminium extrusions; a free-free hollow stile 2 m long has its first bending mode near 80 Hz, with wall panel modes of the extrusion in the kHz. It also excites the IGU breathing mode, and the hook and keeper rattle.

**8.9 Latch click.**
- Measured force to latch is 4-38 N; allowed 100 N (§5).
- The lever and hub turn 90° in about 0.1-0.2 s.
- The hook is a laminated steel blade of a few grams. When it seats on the keeper (extruded 6061 aluminium), or snaps over centre, it makes a 1-3 ms metallic click with energy at 2-8 kHz, transmitted into the stile and audibly into the glass.

---

### 9. Model recipe (derived, not prescribed)

Every number here follows from geometry and materials listed above. None is a per-door constant.

- **Inputs:**
  - Panel: W, H. Glass: t1, t2, gap d, laminate flag. Frame material.
  - Wheels: number of assemblies (2), wheels per assembly (2), diameter D, wheel material, rail material, crown and groove radii, assembly length s.
  - Pile: rows and length per rail. Seal type at the jamb (pile, bulb). Bumper type. Latch type.
  - Contaminants: grit density per metre and size distribution. Wheel defects: flat depth, crack, bearing state.
- **Derived:**
  - Panel mass M = glass (2.5 kg/m²/mm × area × Σt) + frame (perimeter × kg/m).
  - Wheel load F = Mg/4.
  - Hertz a, δ, k_c from E*, Re and F.
  - Contact-filter cutoff v/2a.
  - Rolling rotation rate v/(πD).
  - Carrier bounce √(4k_eff/M)/2π.
  - Drag F_drag = F_seal + μ_roll·Mg, with F_seal ≈ 3-6 N per row-metre of compressed pile.
  - Breakaway ≈ 1.05-1.4 × F_drag.
  - IGU breathing frequency √(f11² + 0.657 f_mam²).
  - Coincidence 12 500/t.
  - Impact half-sine T = π√(M/k), peak v√(Mk), with k switching from pile/bulb (~50 kN/m) to frame (~1-5 MN/m) once the pile compression exceeds its height.
  - Rebound with e ≈ 0.1-0.5.
- **Checks against published numbers:**
  - Maintain force for a 30-90 kg panel lands at 30-55 N (measured 42-53 N on residential/light-commercial doors).
  - Initiate ≤ 1.4 × maintain (measured 1.03-1.4).
  - The 6068 vinyl unit weighs about 80 kg with 3 mm glass (retail 175-178 lb).
  - The IGU dip sits at 200-300 Hz (Quirt: 200-400 Hz typical).

---

### 10. Gaps and open items

- Pile drag per metre: not found. Inferred at 3-6 N per row-metre from operating-force reports.
- Prime-Line per-roller ratings: not found. Use Truth (140-500 lb per assembly) and SWISCO (150 lb per roller) instead.
- Track crown radius and rail height: no published values reached. Truth confirms the crown radius is a design parameter; I assumed 2.5-3.0 mm crown, 4-8 mm rib height.
- EN 13115 sash-movement classes for sliding windows: not verified. EN 12217 doorset classes are verified.
- NAFS-17/-22 Table 5.4 values for Class R sliding doors: not seen directly. Fleetwood's NAFS-22 LC report prints 155 N for both initiate and maintain. Older NAFS-11 R values are 135/90 N.
- Soft-close damper specs (Hawa, Schüco SmartClose, Siegenia): not found.
- No acoustic measurement of sliding-door rolling noise was found. A recording session with a contact mic on the sill and a measurement mic at 1 m, run at 0.2/0.5/1.0 m/s with clean, gritty and flat-spotted wheels, would settle the levels.

---

# Part B. Commercial automatic sliding doors: construction, motion and sound

Research notes for a physical door-sound model. Values marked **[P]** are published (with URL). Values marked **[E]** are my estimates or calculations. Where a figure came only from a reseller or a search-engine summary rather than a manufacturer document, it says so.

Sources were mostly downloaded as PDFs and read as text. Some manufacturer sites (geze.com, stanleyaccess.com) block scripted fetches. The web-search budget ran out partway through, so the GEZE ECdrive/Powerdrive, Portalp and BEA sensor datasheets were never retrieved. Those gaps are listed at the end.

---

### 1. What the machine is

#### Takeaway
Every operator in this survey has the same layout. It is an extruded aluminium header, roughly 100-200 mm high and 150-200 mm deep. Inside it, a motor turns a toothed-belt pulley at one end. The belt runs the length of the header round an idler (tension) pulley at the other end. Each leaf hangs from a carriage with two (sometimes three or four) load wheels rolling on a track profile, plus one anti-riser roller per carriage that sits just under the track lip. A bi-parting door clamps one leaf to the upper run of the belt and the other leaf to the lower run, so the two leaves move in opposite directions. At the floor, a pin or roller guide runs in a channel, or a fork guide straddles a fin.

Most brands use a brushed or brushless PM DC motor with a worm gearbox. The exception is Tormax, whose external-rotor PM synchronous motor drives the belt pulley directly with no gearbox. Control is closed-loop on an encoder. The leaf accelerates, cruises, brakes into a slow "check" or "creep" zone, then is pushed against a rubber end stop or the opposite leaf and held there with a small force. An optional electromechanical lock drops a bolt into the carriage, the belt pulley or the motor brake.

#### Cited findings
- Stanley Dura-Glide 2000/3000: "1/4 HP DC Motor, Gear Drive, Toothed belt". Header 8 in (203 mm) high x 6 in (152 mm) deep. Panels up to 220 lb (100 kg) each. Twin 1/4 HP motors are optional. Supply is 120 VAC, 5 A. Closing speeds 0.5-1.5 ft/s, opening speeds 0.5-2.5 ft/s. [P] https://www.stanleyaccess.com/products/dura-glide-20003000/specs-and-assets
- Dura-Glide gearbox is a worm gear coupled to the DC motor. The belt is "fiberglass reinforced toothed". There are four "cast urethane load-bearing wheels". [P, search summary of Stanley literature] https://www.stanleyaccess.com/products/dura-glide-20003000/specs-and-assets and https://www.autodoorandhardware.com/Stanley-Duraglide-2000-3000-Automatic-Door-Parts-s/552.htm ("heavy duty mechanical gearbox features a worm-gear design"; motor = "electric DC motor" with "hall encoder")
- Stanley architectural spec, Dura-Glide 2000/3000 [P] https://www.stanleyaccess.com/media/9321/download:
  - "minimum of 1/4 horsepower, permanent-magnet DC motor with gear reduction drive"
  - "Drive System: Synchronous belt type"
  - encoder of "not less than 1024 counts per revolution"
  - "Minimum two ball-bearing load wheels and two anti-rise rollers for each active leaf. Minimum load wheel diameter shall be 2 1/2 inch (64 mm); minimum anti-rise roller diameter shall be 2 inch (51 mm)"
  - wheels are "urethane with precision steel lubricated ball-bearing wheels, operating on a continuous roller track"
  - carrier vertical adjustment ≥ 1/8 in (3 mm)
  - hold-open adjustable 0-30 s
  - "Closed loop speed control with active braking and acceleration", "Self-adjusting stop position", "Self-adjusting closing compression force"
- Stanley parts breakdown, 3000 slider [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/stanley/3000%20SLIDER%20PARTS%20BREAKDOWN.pdf:
  - drive: "MOTOR AND GEARBOX ASSY", "GEARBOX (Only)", "PULLEY (GEARBOX)", "SPIDER (COUPLING)" (an elastomer jaw coupling between motor and gearbox), "BELT", "IDLER WHEEL ASSY", "BUMPER STOP ASSY - RH/LH"
  - carriage: "HANGER WHEEL", "ANTI RISE WHEEL", "DETENT - HANGER PORTION" (the breakout detent), "BOTTOM FORK GUIDES"
  - lock: "LOCK ASSY SOLENOID RH/LH FAIL SAFE / FAIL SECURE", built from a "Pawl", "Latch", "Cam" and torsion springs
  - Dura-Glide 5300 SS&BP: "Magnet with Screws"
- Stanley installation sheet, item list [P] https://s3.amazonaws.com/s3-absupply-net/pdf/stanley_dura_glide_2000-3000_installation_instructions.pdf: "LOADWHEELS (4)", "BELT TENSION ADJUSTMENT", "IDLER PULLEY", "ANTIRISER ADJUSTMENT (4)", "DRIVE BELT", "BELT CLAMP", "MOTOR/ENCODER", "DRIVE PULLEY", "END STOPS (2)"
- Stanley Sweets catalogue [P] http://sweets.construction.com/swts_content_files/1743/491083.pdf: header 8 in x 6 in for Dura-Glide and 7 in (177 mm) x 6 in on another series. "1 or 2 1/4 HP DC motor(s) on one continuous drive belt"; "Lock each door in its track with adjustable anti-riser wheels"; "Prevent slippage and uneven closing with toothed drive belt"
- ASSA ABLOY (Besam) SL500 [P] https://www.assaabloyentrance.com/in/en/product-assets/automatic-doors/sl500/product-drawing/installation_manual_assa_abloy_sl500_operator_en_in.pdf:
  - "The motor and gear box transmit movement to the door leaves by means of a tooth belt. The door leaf is fitted to a door adapter/carriage wheel fitting and hangs on a sliding track."
  - recommended max leaf: bi-parting SL500-2 200 kg/leaf, single SL500-R/L 240 kg, low-energy 150 kg/leaf
  - operator weight 11-26 kg
- SL500 wheels by load [P, same manual]:
  - "Plastic 0-90 kg/leaf (bogie)"
  - "Steel 90-120 kg/leaf (bogie), 120-240 kg/leaf (double bogie)"
  - double bogie if leaf height/width > 3.5
  - motor options "Normal Duty / Heavy Duty / Extra Heavy Duty", max motor power parameter 30-150 W, power supply types 50/75/150 W
- Older SL500 US manual [P] https://s3.amazonaws.com/s3-absupply-net/pdf/besam/besam-sl500-installation-and-service-manual.pdf: carriers have "either two or four wheels... two wheels... up to 265 lbs (120 kg) and... four wheels... up to 530 lbs (240 kg)". The parts list includes "Replaceable aluminium track" (1010591), "Tension wheel", "Tooth belt", "Belt clamp" and "Anti-riser".
- dormakaba ES 200 [P] https://my.dormakaba.com/medias/dormakaba-slidingdoor-operator-ES200-technical-folder-en.pdf (and the GB edition https://dormakaba-res.cloudinary.com/image/upload/v1745431487/dormakaba-prod/120000008737-052768-51532-0313-es200-gb.pdf):
  - 1 x 200 kg or 2 x 160 kg
  - height 100 or 150 mm, depth 180 mm
  - 250 W
  - parts list: "Motor", "Pulley with integrated locking device and belt tensioning device", "End stop", "Belt connection", "Track rail and mounting profile"
- dormakaba ES 200 EASY [P] https://my.dormakaba.com/medias/dormakaba-sliding-door-operator-ES200EASY-technical-brochure-en.pdf: 1 x 120 kg or 2 x 100 kg; 180 W; height 100/150 mm, depth 180 mm; parts list includes a "Deflection roller".
- ES 200 motor (reseller listings, not dormakaba) [P-reseller]:
  - Dunkermotoren GR 63x55, 30 V, 100 W, 3350 rpm, brushed, with worm gearbox SG80(K) 15:1 and RE20S encoder. Motor 1.7 kg, assembly 2.5 kg. https://www.caesardoor.com/product/motor-for-es200-sliding-door-operator and https://doordynamic.com/products/dunkermotoren-gr-63-55-30v-motor
  - A 24 V GR63x55 catalogue variant: 100 W, 3350 rpm, 27 N·cm rated torque, 4.9 A, SG80K 10:1. https://www.dena-de.com/en/products/dunkermotoren/gr63x55-sg80k-wl2-me52-12-dc-motor-24v-100w-3350rpm/
- ES 200 belt (reseller) [P-reseller]:
  - S8M profile, 8 mm pitch, 12 mm wide, 5 mm thick, PU with cord. https://doordynamic.com/products/es200-es200e-s8m-toothed-belt
  - Track rail is extruded aluminium, 3.62 kg/m, with a rubber strip on the running surface (the reseller says this replaced an aluminium strip "to reduce noise"). https://doordynamic.com/products/es200-track-rail
  - 4 carriages, "high molecular polymer" wheels with sealed bearings. https://doordynamic.com/products/es200 and https://doordynamic.com/products/dorma-es200-wheels
  - End stop is "a high-strength metal bracket and an impact-resistant rubber pad", adjustable along the track. https://doordynamic.com/products/es200-end-stop-door-buffer
- dormakaba ESA II controller (US ESA100/200 family) [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/dorma/ESAII%20FULL%20MANUAL.pdf: power supply 35 V DC from 120 VAC; max 250 W. Wear parts: "Track rollers every 2 years", "Rubber end stops at every service check", "Track rail every 5 years", "Toothed belt every 1,000,000 opening/closing cycles", "Floor guides at every service check".
- Tormax iMotion 2301/2401 [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/tormax/SLIDER%20I%20MOTION.pdf:
  - "The synchronous motor is attached with permanent magnet and external rotor, which drives the toothbelt directly."
  - 2301: rated voltage 17 V (Y), 10 A (S3), torque 4.4 ft·lb (≈6.0 N·m). 2401: 22 V (Y), 7.3 ft·lb (≈9.9 N·m).
  - toothbelt 9/16 in (≈14 mm) on the 2301, 25/32 in (≈20 mm) on the 2401
  - a "brake module limits the door speed on power interruption"
  - max leaf (2301 / 2401): single slide 265 / 530 lb; bi-part 220 / 440 lb; telescopic single 176 / 265 lb; telescopic bi-part 132 / 220 lb
  - durability: DIN 18650-1 class 3, "1,000,000 test cycles with 4,000 cycles per day"
- Tormax user instructions [P] https://s3.amazonaws.com/s3-absupply-net/pdf/Instruction%20Use%20For%20iMotion%202202-2301-2302-2401.pdf: "direct drive... AC permanent magnet synchronous motor"; 190 W max (2301), 310 W (2401); "Guide system with noise-absorbent guide rail"; "Noise emission level < 70 db (A)".
- Tormax operating instructions [P] https://d3eu1jnerk19h.cloudfront.net/documents/products/2301-2401-Operating-Instructions-FE-10.08.pdf: "AC permanent magnet synchronous motor with external rotor". Also "The tension of the rubber cord must be relaxed carefully" (an elastic energy store in the header).
- record STA 20 (Australian booklet) [P] https://www.recorddoors.com/master-blueprint/en/downloads/english/sliding-doors/standard-sliding-doors/record-sta-20-sliding-door/flyer-(australia)/record-booklet.pdf:
  - "up to 0.7 m/sec"
  - housing 160 x 166 mm, low profile 110 x 166 mm
  - 90 W max, 25 W idle; static drive force max 150 N
  - leaf limits STA 20 1 x 175 / 2 x 150 kg; DUO 1 x 200 / 2 x 175 kg
  - "Running carriage with hardened nylon wheels with sealed bearings", "Motor and guide pulleys with sealed bearings", "Low momentum toothed belt drive", "Low noise, long life, replaceable track system", "Failsafe electric motor brake locking with battery backup"
- record STA 20 dealer page [P-dealer] https://godoors.com.au/automatic-doors/record-doors/record-sta20/: "D.C. brush-less motor with monitoring electronics", "integrated, beveled worm drive gearbox, in metal casing", "generator brake", "Anti-shock dynamic braking".
- record system 20 user manual [P] https://www.recordukdirect.co.uk/media/Sliding%20Doors/STA20/STA20Manual.pdf:
  - "3 carriages required for door weight per wing > 90 kg; 4 carriages required for door weight per wing > 125 kg"
  - "Noise emission: < 45 dB"
  - D-STA (bi-parting) 0.7 m per 1 s; E-STA (single) 0.7 m per 1.5 s, quoted "for max. 75% of authorized door weight"
  - rated power 85-120 W by product line
- GEZE Slimdrive SL NT (GEZE UK product page) [P] https://www.geze.co.uk/en/products-solutions/sliding_doors/automatic_sliding_doors/slimdrive/slimdrive_sl_nt_fr/p_89290:
  - height 70 mm, depth 190 mm; leaf max 125 kg (1- and 2-leaf); opening width 700-3000 mm (1-leaf) or 900-3000 mm (2-leaf); -15 to 50 °C, IP20
  - GEZE data sheet: "Very quiet running, low-wear direct current drive with a height of only 7 cm", "Self-cleaning roller carriage". https://api.geze.com/online/v3/assets/... (Slimdrive SL NT product data sheet, linked from geze.com)
  - NBS listing: "brushless D.C. motor". http://source.thenbs.com/en/gb/product/geze-slimdrive-sl-nt/cRGtSR2r23e1iM6SQwjbBN
  - Max speed 0.7 m/s per leaf, from a search-engine summary of the GEZE product page; I could not open the page itself. https://www.geze.com/en/products-solutions/sliding_doors/automatic_sliding_doors/slimdrive/slimdrive_sl_nt/p_89291
- Besam UniSlide (telescopic) [P] https://d3eu1jnerk19h.cloudfront.net/documents/UniSlide-Telescopic-Install-Manual-Rev-E-8-8-11.pdf: max 250 W; 24 V DC aux; bi-parting up to approx. 1.4 m/s (4.5 ft/s); hold-open 0-60 s. Ambient -20 to +50 °C, or -35 °C "with silicone belt" (a cold-climate belt material option).
- Horton: the C4190 is a swing-door control ("Series 4000" swing operators, with open/close check cams on the output shaft and "Control supports 1/8 or 1/4hp motors"). It is not a slider control. [P] https://s3.amazonaws.com/s3-absupply-net/pdf/c4190-control-setup-05.pdf
  - Horton 2000-series sliders use the C2150/C2160 controls. The 2000-series manual summary mentions a "1/4 HP motor", supply rails "27 to 35 VDC" and a lock solenoid "25 to 33 VDC". [P, manual summary] https://www.manualslib.com/manual/1763659/Horton-2000-Series.html

#### Inferences [E]
- **Leaf masses.** Typical retail leaves are 40-100 kg: a framed aluminium leaf about 1.0 x 2.2 m with 6 mm tempered glass is about 45-60 kg, and with 25 mm insulated glass about 70-90 kg. Ratings of 100-240 kg per leaf are ceilings. Use 60 kg as the default bi-parting leaf and 90 kg for a big single slide.
- **Motor sizing.** 1/4 hp = 186 W, so the US "1/4 HP" motors and the European 90-250 W supplies are the same class.
- **Peak drive force.** 150-230 N (record 150 N static, SL500 190-230 N, ESA II up to 70 lb = 311 N). For a 60 kg leaf plus about 10 kg of reflected rotor and belt inertia, 150 N gives a maximum acceleration of about 2 m/s². Controllers ramp more gently, so expect 0.7-1.5 m/s².
- **Header depth.** 150-200 mm (ES 200 180 mm, Slimdrive 190 mm, record 166 mm, Stanley 152 mm). Height runs 70 mm (Slimdrive) to 203 mm (Stanley). The header is a long, thin aluminium box (about 2-5 m by 0.1-0.2 m by 0.15-0.2 m) with a clip-on or hinged cover. It is the main radiator of drive noise and has many panel modes above about 200 Hz.

---

### 2. Motor, gearbox and belt numbers

#### Takeaway
At full opening speed, the main periodic events are these:
- motor shaft rotation, about 50-60 Hz with a 15:1 worm
- commutator ripple, a few hundred Hz to about 1.4 kHz, on brushed motors only
- worm mesh, about 55-170 Hz
- belt-tooth mesh, v/p: 87.5 Hz for an 8 mm pitch belt at 0.7 m/s, 140 Hz for 5 mm pitch
- PWM switching, usually 16-20 kHz and above hearing; at 4-8 kHz on older or cheaper drives

Every one of these scales linearly with door speed. A door run therefore produces a family of tones that rises during acceleration, holds during cruise, and slides down into the creep zone, where the belt mesh drops to a few Hz and becomes individual ticks. On the direct-drive Tormax there is no gear mesh, and the motor's electrical frequency is only tens of Hz.

#### Cited findings
- Dunkermotoren GR 63x55: 3350 rpm rated, 100 W, 30 V (ES 200 build) or 24 V catalogue (27 N·cm, 4.9 A). Worm gearbox SG80(K) at 15:1 (ES 200) or 10:1. [P-reseller] https://www.caesardoor.com/product/motor-for-es200-sliding-door-operator ; https://www.dena-de.com/en/products/dunkermotoren/gr63x55-sg80k-wl2-me52-12-dc-motor-24v-100w-3350rpm/ ; a search summary also lists a GR63x55 planetary PLG52 64:1 variant (not door-specific). https://www.vitechparts.com/dunkermotoren-gr-63x55-plg52.html
- ES 200 belt is S8M, 8 mm pitch, 12 mm wide. [P-reseller] https://doordynamic.com/products/es200-es200e-s8m-toothed-belt
- Tormax 2301 belt is 9/16 in (about 14 mm) wide, driven directly by the motor. Motor torque is 6.0 N·m (S3). "Force at the tooth belt 18.4-250" (unit printed as foot-pounds; it is presumably lbf: 82-1112 N, adjustable). [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/tormax/SLIDER%20I%20MOTION.pdf
- Tormax error E55: "Position drift >9mm, toth belt jumping". Tooth-jump is a recognised fault event, an audible clack. [P, same manual]
- Stanley drive is a DC motor with encoder, then a spider (elastomer jaw) coupling, then a worm gearbox, then the drive pulley. [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/stanley/3000%20SLIDER%20PARTS%20BREAKDOWN.pdf
- record: brushless DC with a "beveled worm drive gearbox". [P-dealer] https://godoors.com.au/automatic-doors/record-doors/record-sta20/
- SL500 belt tension: tension-wheel adjustment gap 1-2 mm; fixing screws 10 N·m, centre screw 30 N·m. Troubleshooting: "If the belt is making noise against the beam or cover check that there is the right belt tension... distance shall be 47 mm." [P] https://www.assaabloyentrance.com/in/en/product-assets/automatic-doors/sl500/product-drawing/installation_manual_assa_abloy_sl500_operator_en_in.pdf
- Siemens patent on door mass estimation: incremental encoder; friction measured in a creep run below 10 cm/s; example door masses 200 and 300 kg; example motor force constant 18.4 N/A (force at the leaf per amp); 100 mm run-up before measuring. [P] https://patents.google.com/patent/US8183815B2/en

#### Inferences [E]
**Pulley size.** Dunker at 3350 rpm through 15:1 gives 223 rpm (3.72 rev/s) at the pulley. To reach the 0.7 m/s maximum of the ES 200, the pulley circumference must be 0.188 m, a pitch diameter of 60 mm. A 24-tooth S8M pulley has a pitch diameter of 61.1 mm and gives 0.714 m/s. **Model the ES 200-class drive as 24T S8M, PD 61 mm.** For 5 mm pitch belts (HTD 5M / T5 / AT5), the same speed suggests about 36-40 teeth (PD 57-64 mm). Belt widths: 12 mm (ES 200), 14 mm (Tormax 2301), 20 mm (Tormax 2401). T10 (10 mm pitch) and 25 mm widths appear on heavy operators. I found no published Stanley or GEZE figures.

**Kinematics for that geared drive (v = leaf speed in m/s).**

| quantity | formula | 0.7 m/s (open cruise) | 0.4 m/s (close cruise) | 0.3 m/s (ANSI close) | 0.025 m/s (creep) |
|---|---|---|---|---|---|
| pulley rev/s | v / (π·0.0611) | 3.65 | 2.08 | 1.56 | 0.13 |
| motor rev/s (15:1) | ×15 | 54.7 Hz (3280 rpm) | 31.3 | 23.4 | 1.95 |
| S8M tooth mesh | v / 0.008 | 87.5 Hz | 50 | 37.5 | 3.1 |
| 5 mm pitch mesh | v / 0.005 | 140 Hz | 80 | 60 | 5 |
| 10 mm pitch mesh | v / 0.010 | 70 Hz | 40 | 30 | 2.5 |
| worm mesh, 2-start/30T | 2 × motor | 109 Hz | 63 | 47 | 3.9 |
| worm mesh, 3-start/45T | 3 × motor | 164 Hz | 94 | 70 | 5.9 |
| commutator, k = 8 | 8 × motor | 438 Hz | 250 | 187 | 16 |
| commutator, k = 12 | 12 × motor | 656 Hz | 375 | 281 | 23 |
| commutator, k = 13 (odd, 2 brushes, ripple 2k) | 26 × motor | 1422 Hz | 814 | 608 | 51 |

Pulley tooth-entry frequency equals belt mesh frequency (v/p) whatever the tooth count, because each tooth on the belt enters a groove once per pass over the pulley. The idler adds the same frequency if it is toothed. A smooth back-side idler adds only roller noise. Belt mesh is often the most audible tonal component of a belt drive: a buzzy "zzz" whose pitch tracks speed. Polyurethane belts with steel or glass cord, as used here, are quieter than rubber HTD belts, but the mesh line is still there at roughly 35-140 Hz plus harmonics.

**Gearing.** The worm stage reduces motor-shaft and commutator noise transmission. Worm gears are sliding-contact and relatively quiet, giving a soft whine rather than a spur-gear scream. Expect the motor/gear "whir" at 50-60 Hz fundamental with strong harmonics, and brush hiss (broadband 2-8 kHz) only on brushed motors. The Dunker brochure line "low cogging torque" suggests a smooth start.

**Brushless and direct drive.** Brushless motors (GEZE, record, Tormax) have no commutator hiss, but their electrical frequency and PWM ripple can be audible. For the Tormax direct drive, assume pitch diameter 50-65 mm. At 0.7 m/s the rotor turns at 3.4-4.5 rev/s. With an assumed 10-15 pole pairs (typical external-rotor hub-type motor), the electrical frequency is 34-67 Hz and the 6th-harmonic torque ripple 200-400 Hz. That explains the "silent" marketing: there is little tonal motor content, and the belt mesh and rollers dominate.

**Belt dynamics.** Mass per length is about 0.06-0.08 kg/m for a 12-14 mm PU belt with cord. With static tension T ≈ 100-200 N, transverse wave speed c = √(T/μ) ≈ 35-55 m/s. On a 2-4 m free span, the first transverse mode f₁ = c/(2L) is about 5-14 Hz. When slack or speed-dependent excitation lets the span hit the cover or beam, you get a low flapping or slapping knock at the span rate (the SL500 manual explicitly lists "belt making noise against the beam or cover"). On reversal and hard braking, the drive-side span unloads and the slack span tightens. The belt "snaps over", which is one source of the soft clunk at direction change.

**Coupling and backlash.** The jaw (spider) coupling plus worm backlash give roughly 0.5-2° of play at the motor and about 0.1-0.4 mm at the leaf. Whenever torque reverses (start of braking, reversal, the hold-force push at closed), the play is taken up with a small tick or knock in the header.

---

### 3. Carriage, wheels, track, anti-riser, floor guide

#### Takeaway
Load wheels are about 50-70 mm in diameter. They are polyurethane, nylon or polymer tyres on sealed ball bearings; steel wheels are used for heavy leaves (SL500 > 90 kg). They roll on an aluminium track that is often replaceable or has a rubber or polymer running strip. At 0.7 m/s a 64 mm wheel turns at only 3.5 rev/s, so wheel noise is broadband rolling roughness plus once-per-rev thumps from flats, not a tone. The anti-riser roller is set with about 0.5 mm clearance and touches the track only when the leaf is lifted or rocked. Floor guides are plastic pins, rollers or forks in an aluminium channel, and they are the main source of scraping and grit noise.

#### Cited findings
- Stanley: load wheel diameter ≥ 2.5 in (64 mm), anti-rise roller ≥ 2 in (51 mm); urethane on steel ball bearings; ≥ 2 load + 2 anti-rise per leaf. [P] https://www.stanleyaccess.com/media/9321/download
- SL500: plastic wheels 0-90 kg/leaf, steel 90-240 kg/leaf; 2-wheel carriers up to 120 kg, 4-wheel (double bogie) to 240 kg. "Replaceable aluminium track". Anti-riser clips onto the wheel bracket. Owner maintenance: "Dirt on the sliding track should be removed with methylated spirits. If necessary replace the sliding track... None of the parts need lubrication." [P] https://www.assaabloyentrance.com/master-blueprint/en-us/product-assets/automatic-doors/sl500-cgl/user-manuals/ASSA%20ABLOY%20-%20SL500%20Owner's%20Manual%20Rev%20US-2.0.pdf and https://s3.amazonaws.com/s3-absupply-net/pdf/besam/besam-sl500-installation-and-service-manual.pdf
- SL500 troubleshooting: "The motor starts but stops — Clean the floor guide." SL500 lists both pin guides and roller guides ("Roller Guide", "T-Block") for fixed-sidelite installs. [P] owner's manual above.
- Tormax anti-riser: "Adjust anti-riser... for a gap of .020 in (approximately the thickness of a credit card) between the roller and the track" (0.5 mm). [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/tormax/SLIDER%20I%20MOTION.pdf
- Tormax: "Guide system with noise-absorbent guide rail". Owner checks: "Check the noises made while the door moves — No unusual and noticeable movement noises can be heard in the drive, guide system or floor guides." [P] https://s3.amazonaws.com/s3-absupply-net/pdf/Instruction%20Use%20For%20iMotion%202202-2301-2302-2401.pdf
- record: "hardened nylon wheels with sealed bearings"; 3 carriages above 90 kg/leaf, 4 above 125 kg; "Low noise, long life, replaceable track system". [P] record booklet and manual above.
- dormakaba ESA II: track rollers replaced every 2 years, track rail every 5 years, floor guides checked every service. [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/dorma/ESAII%20FULL%20MANUAL.pdf
- record 5100 (US): "for full breakout units (so-sx-sx-so) the standard bottom guide is a pin guide track"; guide pin height locked by set screw. [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/record/5100%20SYSTEM%2019.pdf
- Stanley uses "BOTTOM FORK GUIDES". [P] Stanley parts breakdown above.

#### Inferences [E]
**Wheel rotation.** f = v / (π·D).
- D = 64 mm: 3.48 Hz at 0.7 m/s; 1.99 Hz at 0.4; 1.49 Hz at 0.3; 0.12 Hz at creep.
- D = 50 mm: 4.46 Hz at 0.7 m/s.

A flat spot on a PU tyre (from standing loaded for months) gives a soft "thup" at that rate, 2-4 per second at cruise. A small chip or debris on the tread gives a click at the same rate.

**Bearings.** Assume a 608-size bearing (7 balls, ball 3.97 mm, pitch diameter about 15 mm) at 3.48 Hz shaft speed:
- BPFO = (7/2)·3.48·(1 − 3.97/15) ≈ 9 Hz
- BPFI ≈ 15 Hz

A worn bearing therefore adds a gritty modulation or roughness at 9-15 Hz, not a tone.

**Rolling noise.** The spectrum is set by track and tread roughness. Roughness wavelength λ maps to frequency v/λ: at 0.7 m/s, λ = 1 mm gives 700 Hz and λ = 0.2 mm gives 3.5 kHz. Grit gives impulses at rate (grains per metre) × v; 20 grains/m at 0.7 m/s gives 14 clicks/s. A rubber or PU strip on the track (dormakaba, Tormax "noise-absorbent") pushes this down and damps it. Steel wheels on bare aluminium (heavy SL500) are the loudest combination: a hiss-rumble plus header radiation.

**Wheel count.** Wheels per leaf: 2 for light leaves, 3-4 for leaves over 90-125 kg. Each passes the same track defects at a fixed spacing (carriage spacing about 0.6-0.9 m on a 1 m leaf), so a track defect produces a pair or quartet of clicks separated by spacing/v (about 1 s at 0.7 m/s).

**Anti-riser contact.** A 0.5 mm gap means contact only when the leaf rocks (someone pushes it, wind load, or hitting the end stop with momentum that pitches the leaf). Contact gives a short plastic-on-aluminium tick, or a scuff during motion.

**Floor guide.** A plastic shoe or pin in an aluminium U-channel, with clearance about 1-3 mm. The noise is stick-slip friction, scrape (broadband 1-6 kHz) modulated by leaf sway, plus knocks when the leaf swings across the clearance at start and stop. Its level grows with debris in the channel. Trackless (pivot or roller guide at the jamb, "TL" packages) avoids most of it.

---

### 4. Motion profile: what the standards allow

#### Takeaway
ANSI/BHMA A156.10 (US) does not limit how fast a full-energy slider opens. It limits closing speed to 1 ft/s (0.305 m/s) for leaves up to 160 lb (71 kg), and to v = √(160/W) ft/s for heavier leaves. That is a constant closing kinetic energy of about 2.5 ft·lbf (3.4 J) per leaf. The standard also requires:
- latch check (slowdown) at least 2 in (51 mm) from closed
- stall force ≤ 30 lbf (133 N)
- breakaway ≤ 50 lbf (222 N)
- hold-open ≥ 1.5 s after loss of detection (2.5 s in some beam-only layouts)

EN 16005 (Europe) protects full-energy doors with sensors plus dynamic force limits rather than a speed cap. Low-energy doors without sensors are limited to 1.69 J kinetic energy and 67 N static force (150 N allowed in the last 50 mm).

#### Cited findings
- **ANSI/BHMA A156.10-1999** [P] https://law.resource.org/pub/us/cfr/ibr/003/bhma.a156.10.1999.pdf
  - 10.9: "A stopped sliding or folding door shall not require more than 30 lbf (133 N), measured at the leading edge, to prevent it from closing at any point in the closing cycle."
  - 10.10: "A sliding door shall be adjusted so that the closing speed is one foot per second maximum for doors weighing up to and including 160 lbs (71 kg) per leaf. For doors weighing more than 160 lbs (71 kg): V = √(160/W)" (V in ft/s, W in lb)
  - 10.6: "Swing, sliding and folding doors utilizing sensors or control mats shall remain open a minimum of 1.5 seconds after loss of detection."
  - 12.1: "Latch check shall occur... for sliding and folding doors at no less than 2 inches (51 mm) from the closed position."
  - 12.4: "Sliding doors provided with a break away device shall require no more than a 50 lbf (222 N) applied 1 inch (25 mm) from the leading edge of the lock stile for the break out panel to open."
  - 12.5: breakaway endurance test of 300,000 cycles at 5-8 cycles/min, with 1,000 breakout cycles every 50,000
  - 9.1.4: knowing-act doors stay open ≥ 5 s
  - 8.2: presence beams active "from fully open to within 6 inches (150 mm) of closed"; lower beam 6-28 in (150-710 mm), upper beam 45-55 in (1145-1400 mm); activation zone 43 in (1090 mm) deep, effective to within 5 in (125 mm) of the door face
  - swing-door clauses (1.5 s to back check, latch-check time table) do not apply to sliders
- **ANSI/BHMA A156.10-2005** [P] https://www.automateddoorsolutions.com/wp-content/uploads/2018/08/ANSI-A156-10-2005.pdf
  - 10.1.1 keeps the same 1 ft/s and √(160/W) closing rule but adds "to latch check"
  - 10.1.2: latch check at ≥ 2 in "of each sliding door leaf"
  - 10.1.3: 30 lbf; 10.1.4: 50 lbf breakaway; 10.1.5: 1.5 s hold after loss of detection
  - 8.3.2.1 and 8.3.2.4: 2.5 s minimum hold when photo-beams carry presence (four beams alternating sides, lowest 6-28 in, spacing 6-12 in, top 45-55 in)
  - 8.3.2.2-3: 1.5 s with overhead presence sensors
- **Later editions (2011/2017/2024).** The trade summary for later editions still states the 2 in latch check, the 1 ft/s rule up to 160 lb, and 30 lbf. https://www.autodoorandhardware.com/Understanding-ANSI-A156.10-Automatic-Sliding-Door-Requireme-s/123399.htm and https://www.constructionspecifier.com/power-operated-doors-maintaining-safe-egress-with-updated-standards/ I could not read the 2017/2024 text. The 1.25 lbf·ft (1.69 J) kinetic-energy figure that search engines attach to A156.10 actually belongs to A156.19 (low-energy swing doors) and to the low-energy provisions. [P] https://www.automateddoorsolutions.com/wp-content/uploads/2018/08/ANSI-A156-19-2007.pdf
- **Manufacturer US settings that mirror ANSI:**
  - ESA II "Closing speed (up to 190 lb) 4-12 in/s" (max 12 in/s = 1 ft/s)
  - record 5100 "Closing Speed (12 inches per sec. max.)"
  - Stanley "Closing Speeds: 0.5'-1.5 per sec per ANSI" (the 1.5 ft/s top of the range exceeds the 1 ft/s ANSI cap for any leaf weight, so it is an adjustment range, not a compliant setting)
  - Besam owner checklist: "Measure / Adjust Speeds — Measure to ANSI/BHMA A156.10... (Open time - 1.5 seconds or longer)"
  - [P] ESA II manual, record 5100 manual https://www.addisonautomatics.com/wp-content/uploads/manuals/record/5100%20SYSTEM%2019.pdf, Stanley spec page, SL500 owner's manual (URLs above)
- **EN 16005 (EDSF Guideline 06)** [P] https://www.edsf.com/fileadmin/inhalte/edsf/download/edsf_guideline_06_EN_16005.pdf
  - low energy: stall force ≤ 67 N "at any point in the opening or closing cycle"; "kinetic energy of a doorset in motion shall not exceed 1,69 J in the case of low energy movement doorsets without safety sensors"
  - safety distances: fingers ≤ 8 or ≥ 25 mm, head ≥ 200 mm, body ≥ 500 mm
  - electrical drives to EN 60335-2-103
  - safety parts to EN ISO 13849-1 PL c (PL d on escape routes)
  - closing cycle: "The use of light barriers (photoelectric cells) is no longer permitted" (as the closing-edge protection)
- **BFT EN 16005 installer guide** [P] https://www.bft-automation.com/index.php?eID=dumpFile&t=f&f=695599&token=b711a51ae7e61f31a6cf0d1ca8bd0a5bfeedb6a2
  - low energy: 1.69 J; hold force ≤ 67 N; manual opening on power failure ≤ 90 N
  - "a static closing force of up to 150 N is allowed in the last 50 mm of travel"
  - the minimum-time table uses v = √(2E/m) and t = 0.9·travel/v
- **EN 16005 full-energy force limits** (secondary sources, wording differs) [P-secondary] https://ukdoorsandshutters.uk/2026/08/05/automatic-door-force-testing/ and search summary of the same:
  - dynamic peak 1400 N where the gap is > 500 mm, 400 N for crush zones
  - must fall below 150 N within 0.75 s, and below 25 N (some sources say 80 N) within 5 s
  - the UK article warns these are "often drawn from adjacent guidance" (gates and industrial doors, EN 12453), so treat them as indicative
- **DIN 18650 durability:** class 3 = 1,000,000 cycles at 4,000 cycles/day. [P] Tormax manual above.
- **AAADM:** the daily safety check is reflected in the Besam owner checklist ("Open time 1.5 seconds or longer"); I did not retrieve AAADM documents directly.

#### Inferences [E]
**ANSI closing energy.**
- For W ≤ 160 lb: KE = ½·(W/g)·v². At W = 160 lb, v = 1 ft/s this is ½·(160/32.17)·1 = 2.49 ft·lbf = **3.37 J per leaf**.
- For W > 160 lb: v² = 160/W, so KE stays exactly 2.49 ft·lbf.

**Closing speed under ANSI.**
- 60 kg leaf: up to 0.305 m/s
- 100 kg (220 lb) leaf: √(160/220) = 0.853 ft/s = 0.26 m/s, KE = 3.4 J
- 200 kg (441 lb) leaf: 0.60 ft/s = 0.18 m/s

**EN 16005 low-energy speed.** For a 60 kg leaf, v = √(2·1.69/60) = 0.237 m/s. For 100 kg, 0.184 m/s. A low-energy 0.9 m leaf travel therefore takes 0.9·0.9/0.237 ≈ 3.4 s (60 kg) to 4.4 s (100 kg).

**Full-energy opening.** Unrestricted under ANSI; the safety comes from presence sensing. Typical settings are 0.5-0.7 m/s per leaf, and up to 1.0 m/s single (Tormax) or 1.4-1.7 m/s quoted for SL500/UniSlide. The SL500/UniSlide quotes are bi-parting, and I read them as the closing speed of the gap between the leaves (2 × 0.7 m/s); the per-panel UniSlide range is 0.10-0.70 m/s.

---

### 5. Motion profile: what installers actually set

#### Takeaway
Use this default bi-parting profile:
- open: accelerate to 0.6-0.7 m/s per leaf, brake about 100-300 mm before full open, creep the last 0-300 mm at 25-100 mm/s into the open stop
- hold: 1.5-5 s after the last detection (settable 0-180 s)
- close: accelerate to 0.25-0.4 m/s, brake into the latch/close check at least 51 mm from closed, creep at 15-60 mm/s, contact, then hold 30-60 N closed force

A complete pedestrian cycle is about 8-12 s.

#### Cited findings
- **dormakaba ES 200:** opening 10-70 cm/s, closing 10-50 cm/s, hold-open 0-180 s, max force 150 N. ES 200 EASY: 10-50 / 10-40 cm/s, hold 0.5-30 s. Creep speed 1.5-5 cm/s (reseller copy of the ES 200 manual). [P] https://my.dormakaba.com/medias/dormakaba-slidingdoor-operator-ES200-technical-folder-en.pdf ; https://my.dormakaba.com/medias/dormakaba-sliding-door-operator-ES200EASY-technical-brochure-en.pdf ; https://doordynamic.com/blogs/news/dorma-es-200-manual-technical-data
- **dormakaba ESA II (US)** [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/dorma/ESAII%20FULL%20MANUAL.pdf
  - opening 4-30 in/s (0.10-0.76 m/s)
  - closing 4-12 in/s (0.10-0.30 m/s) up to 190 lb
  - creep opening and closing 1-4 in/s (25-100 mm/s)
  - creep distance 0-12 in (0-305 mm) each way
  - brake ramps and accelerations on 1-9 scales
  - hold-open 1.5-180 s
  - force limitation 11-70 lb (49-311 N)
  - the learn cycle runs at creep speed
- **ASSA ABLOY SL500** [P] https://www.assaabloyentrance.com/in/en/product-assets/automatic-doors/sl500/product-drawing/installation_manual_assa_abloy_sl500_operator_en_in.pdf
  - High Speed Opening 10-70 cm/s, High Speed Closing 10-70 cm/s, Low Speed 5-70 cm/s ("self adjusting... if set to max")
  - Low Speed Distance Opening 0-99 cm, Closing 0-99 cm ("creep speed distance")
  - Run Program 1 "Smooth" to 5 "Max Performance" (accel/brake aggressiveness)
  - Hold Force 0-60 N in the closed position (Normal Duty max 30 N, HD/EHD max 60 N)
  - Close Kick Force 20-230 N
  - hold-open 0-60 s
  - obstruction behaviour: "doors reverse immediately... then resume their interrupted movement at low speed"
  - Push & Close ("poor man's lock") makes the motor fight a manual push for up to 990 s
- **Besam UniSlide (per panel)** [P] https://d3eu1jnerk19h.cloudfront.net/documents/Besam-Unislide-Wiring-and-Troubleshooting.pdf: HSO 0.10-0.70 m/s, HSC 0.10-0.70 m/s, LS 0.05-0.70 m/s, hold 0-60 s.
- **Tormax iMotion 2301 (US manual)** [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/tormax/SLIDER%20I%20MOTION.pdf
  - Opening speed codes 0-9: 3.93-39.37 in/s (0.10-1.00 m/s). **Default code 6 = 27.56 in/s = 0.70 m/s.**
  - Closing speed: 3.15-31.5 in/s (0.08-0.80 m/s). **Default code 4 = 15.75 in/s = 0.40 m/s.**
  - Close check speed: 0.59-2.36 in/s (15-60 mm/s). **Default code 3 = 0.82 in/s = 21 mm/s.**
  - Hold-open (Auto 1/2): 0, 0.5, 1, 2, 3, 5, 7.5, 10, 12.5, 15 s; default code 2 = 1 s (installer must meet ANSI's 1.5 s)
  - motor force opening/closing 5-100 % (default 55 %); closed-position force 0-100 N (default 50 N); braking distances opening/closing on a 0-9 scale
- **record:**
  - STA 20 "up to 0.7 m/sec". D-STA (bi-parting) "1.0 s / 0.7 m", E-STA (single) "1.5 s / 0.7 m". [P] https://www.recordukdirect.co.uk/en/products/247/record-sta-20-operator/techdata ; STA20 manual
  - record 5100 (US): close ≤ 12 in/s, hold ("Time Delay Open") ≤ 60 s, partial open ≥ 8 in. [P] record 5100 manual
- **Stanley:** opening 0.5-2.5 ft/s (0.15-0.76 m/s), closing 0.5-1.5 ft/s (0.15-0.46 m/s), hold 0-30 s. The obstruction recycle "reduc[es] door closing speed prior to the previously encountered obstruction location, and will continue to close in check speed". [P] Stanley spec page and arch spec

#### Inferences [E]
**Bi-parting cycle model** (60 kg leaves, 1.8 m clear opening, so 0.9 m travel per leaf).

*Opening:*
- accelerate at 1.2 m/s² to 0.7 m/s: 0.58 s, 0.20 m
- cruise 0.55 m: 0.79 s
- brake at 1.2 m/s² to 0.05 m/s: 0.54 s, 0.20 m
- creep 0.05 m/s over the remaining 0.05 m (≈1.0 s), or stop directly with a self-learned stop and no creep (SL500 "Low Speed Distance" can be 0)
- **total 1.9-2.9 s.** The leaves reach 0.35 m each (0.7 m gap, enough to pass) about 0.9 s after motion starts.

*Hold:* 1.5-5 s after the last detection.

*Closing:*
- accelerate at 0.8 m/s² to 0.30 m/s: 0.38 s, 0.06 m
- cruise 0.74 m: 2.5 s
- brake to 21 mm/s check speed by about 60 mm from closed: 0.4 s, 0.06 m
- creep 50 mm at 21 mm/s: 2.4 s
- contact
- **total about 5.5-6 s**

*Whole cycle:* about 9-14 s.

A record-style "1.0 s per 0.7 m" figure implies a faster profile (peak about 0.9-1.0 m/s with short ramps).

**Single slide** (1.0 m travel, 90 kg): opening about 2.5-3 s at 0.6 m/s; closing at ANSI's √(160/198) = 0.90 ft/s (0.27 m/s) takes about 5-6 s.

**Acoustic consequence.** The opening run is short, loud and pitch-rising-then-falling (about 0.6 s ramp up, about 0.8 s cruise, about 0.5 s ramp down). The closing run is longer, lower in every speed-locked frequency (factor 0.3/0.7 = 0.43, more than an octave below opening), and ends in seconds of very slow creep in which the belt mesh is 3-8 Hz: separate tooth ticks and gear "grumble" rather than a tone.

**At closed:** contact, then 30-60 N of hold force. The closed-position hold keeps the motor energised, giving a faint PWM or holding hum on some drives. Tormax warns: reduce closed-position force "if H73 after 10s" (overheat/overload).

---

### 6. Locks and breakout

#### Takeaway
The electromechanical lock is a solenoid or small motor that drops a bolt or hook into a keeper on the carriage, or blocks the belt pulley or motor. It engages only after the door has reached the closed position, normally only in NIGHT/OFF/EXIT modes, not in everyday AUTOMATIC operation. Variants:
- fail-safe (LDP: locked while powered)
- fail-secure (LD: locked without power)
- bistable (LDB: two coils or a pulsed latching solenoid; stays in either state without power)

Unlocking is one click (LDP) or two clicks (LDB), and the controller can delay motor start for the lock to clear: SL500 0-9.9 s, typically a few hundred ms. Breakout panels are held by ball catches, magnets or adjustable detents, released by ≤ 222 N (ANSI).

#### Cited findings
- SL500 owner check: "Set the operation mode selector to EXIT. The door should open and close without any sound from the lock... When the operation mode selector is set back to EXIT, **two clicking sounds (LDB) or one clicking sound (LDP)** indicate that the lock is unlocked." [P] https://www.assaabloyentrance.com/master-blueprint/en-us/product-assets/automatic-doors/sl500-cgl/user-manuals/ASSA%20ABLOY%20-%20SL500%20Owner's%20Manual%20Rev%20US-2.0.pdf
- SL500 lock parameters [P] https://www.assaabloyentrance.com/in/en/product-assets/automatic-doors/sl500/product-drawing/installation_manual_assa_abloy_sl500_operator_en_in.pdf:
  - Lock Configuration: no lock / LDP fail safe / LD fail secure / LDB bistable / LDE espagnolette
  - "Opening Delay For Lock 00-99 — The time the opening is delayed (0.0-9.9 sec) after an opening impulse is given in operation mode selections OFF and EXIT"
  - "Remain Locked at Stop"
  - "Exit Lock" (locked in EXIT)
  - lock geometry: "Max distance = the distance a door leaf can move when the door is in locked position" = **2 mm** (old lock, some new) or **9 mm** (new lock with standard wheel holder)
  - also a "Lock indication switch (LIS)" and a "Manual Opening Lock device, MOLD"
- Tormax: lock module bolted to the header, with "locking cams" on top of the trolleys; set "a minimum clearance of 1/32 in between locking plate and cam(s)" (0.8 mm). "The door is pushed close by the motor and is locked by the electromechanical door lock" (OFF mode). "The door can still be used for 5 seconds after selecting operating mode OFF. The door then locks." Fault text: "Lock produces periodic switching noise" (E11, lock jammed; it retries). There is also a "holding magnet" option. [P] https://www.addisonautomatics.com/wp-content/uploads/manuals/tormax/SLIDER%20I%20MOTION.pdf ; https://d3eu1jnerk19h.cloudfront.net/documents/products/2301-2401-Operating-Instructions-FE-10.08.pdf ; https://s3.amazonaws.com/s3-absupply-net/pdf/Instruction%20Use%20For%20iMotion%202202-2301-2302-2401.pdf
- dormakaba ES 200: the "Pulley with integrated locking device and belt tensioning device" means the lock acts on the return pulley, i.e. on the belt. "Electro-mechanical locking device (bistable)" option, plus "Manual lock release". [P] ES 200 technical folder above
- dormakaba ESA II: in NIGHT/BANK "the door closes and locks in the closed position"; Panic Close repeats closing "until the door closes and locks successfully", and if overloaded the motor switches off for 10 s after 10 s of constant operation. [P] ESA II manual
- Stanley: "Electric Solenoid Lock (Fail Safe/Fail Secure)", built from a pawl, latch, cam and torsion springs; mechanical "Key/thumb turn hook bolt"; optional 3-point locking and delayed egress with shear magnets. The spec calls for a deadbolt with "minimum 1 inch (25 mm) long throw bolt" (ANSI A156.5 Grade 1), and on bi-parting a "two-point" lock that "automatically extends a flush bolt into overhead carrier assembly". [P] Stanley spec page, arch spec, parts breakdown
- record: "Failsafe electric motor brake locking with battery backup" (the lock is the motor brake) and "inbuilt multi-point electronic locking". Lock types in the US 5100 menu: "Motor powered, BiStable, MPU, Magnet, Fail Secure, Fail Safe". The STA 20 manual: "Single — 1 keystroke unlocks the system... Once closed, system locks again"; "System remains locked even in case of power failure". [P] record booklet; record 5100 manual; STA20 manual
- Horton 2000: lock solenoid energised at "25 to 33 VDC". [P, summary] https://www.manualslib.com/manual/1763659/Horton-2000-Series.html
- Breakout:
  - ANSI ≤ 50 lbf (222 N) at 1 in from the lock stile, endurance tested over 300,000 cycles [P, A156.10-1999 12.4-12.5]
  - Stanley "DETENT - HANGER PORTION" and, on the 5300, "Magnet with Screws"; the spec describes an "adjustable detent device mounted in the top of each breakaway panel" [P] Stanley parts and spec
  - record 5100: "locate the ball catch at the top of the vertical lock stile and rotate it counterclockwise to increase" (detent force) [P] record 5100 manual
  - ANSI: swinging breakaway panels must "interrupt actuation of the operator" or self-close [P, A156.10]

#### Inferences [E]
**Locking sequence**, as I reconstruct it from the parameters above:
1. Door arrives at closed and the creep contact is detected (encoder stall).
2. The motor applies the hold force (30-60 N).
3. The lock fires about 0.1-0.5 s after the stop is confirmed: solenoid pull-in 20-50 ms, then the bolt drops by spring or gravity onto the carriage keeper, giving a click-clack 30-80 ms after the coil click.
4. The motor force drops.

**Unlocking sequence** (from OFF/EXIT with an authorised impulse):
1. Coil click.
2. For LDB, a second coil or reset pulse about 50-200 ms later: the "two clicks".
3. Often a brief push in the closing direction first, to unload the bolt from the keeper (because the leaf can sit 2-9 mm off when locked, someone may have pulled it). A common practice I did not verify for any one brand.
4. Opening delay 0.1-0.5 s typical.
5. Motor start.

**Solenoid and bolt sound.** Plunger stroke about 5-10 mm and plunger mass about 20-60 g. Impact at the end of stroke at 0.5-1.5 m/s gives a bright metallic click (energy concentrated at 1-6 kHz), radiated through the aluminium header. The header is a good radiator, so the lock is often the loudest event of the cycle, despite having about 0.01-0.05 J of mechanical energy.

**Locked-door rattle.** A leaf that can move 2-9 mm when locked gives a clunk when pulled: bolt against keeper, metal-on-metal, plus a header and glass response.

**Motor-brake "lock" (record).** A spring-applied brake releasing gives an armature click at the motor end of the header at every motion start, and engaging gives one at every stop. In AUTOMATIC it may stay released while powered, depending on configuration.

**Breakout.** The panel swings out on its top and bottom pivots after a ball detent or magnet lets go. The sound is a snap release (detent) or a soft "tuk" (magnet break), then a pivot squeak or rumble. Re-latching on return gives a click.

---

### 7. Stops, centre meeting, seals

#### Takeaway
The open position ends against adjustable rubber end stops on the track (dormakaba: metal bracket plus rubber pad; Stanley "BUMPER STOP ASSY"), or electronically just short of them on self-learning drives. At closed, bi-parting leaves meet at the centre on rubber or brush meeting-stile seals, or a single leaf meets a jamb with a weather strip. Because both ends are reached at creep speed (15-100 mm/s), the impact energy is tiny, about 0.01-0.3 J, and the impact is a damped thud. It is not a bang unless the stop/creep calibration is wrong or the door is pushed by hand.

#### Cited findings
- dormakaba ES 200 parts list: "End stop" in the header; the reseller describes it as a metal bracket with "impact-resistant rubber pad" that "absorb[s] kinetic energy... to reduce running noise". [P] ES 200 technical folder; https://doordynamic.com/products/es200-end-stop-door-buffer
- ESA II: "Rubber end stops at every service check" (wear part). [P] ESA II manual
- Stanley: bumper stop assemblies RH and LH; installation step "Manually slide & set bumper stops". [P] Stanley parts breakdown and installation sheet
- Stanley spec: "Self-adjusting stop position" and "Self-adjusting closing compression force" (it compresses the meeting seals by a controlled force). [P] https://www.stanleyaccess.com/media/9321/download
- UniSlide: "Locate the door stops at each end of the beam & latch adapter". [P] UniSlide install manual
- Tormax: "Check the glass door fillings, door edges and rubber profiles for damage." [P] Tormax user instructions
- Stanley installation sheet: "WEATHER STRIP (4)". [P]

#### Inferences [E]
**Contact energies**, ½mv²:

| leaf mass | speed | energy |
|---|---|---|
| 60 kg | 0.021 m/s (Tormax default check) | 0.013 J |
| 60 kg | 0.05 m/s | 0.075 J |
| 60 kg | 0.1 m/s (max ESA creep) | 0.3 J |
| 60 kg | 0.3 m/s (no check, a fault) | 2.7 J |
| 60 kg | 0.7 m/s (open stop with no braking) | 14.7 J |
| 100 kg | 0.05 m/s | 0.125 J |

**Bumper dynamics.** For a rubber bumper of stiffness k ≈ 1-5×10⁵ N/m and m = 60 kg, the contact half-period is π·√(m/k) ≈ 35-77 ms. The impact force is a smooth pulse with little energy above about 100 Hz. What you hear is mostly the leaf, glass and header ringing in response, plus belt and drive backlash taking up.

**Glass leaf as radiator.** A 1.0 x 2.1 m pane of 6 mm tempered glass (E = 70 GPa, ρ = 2500 kg/m³), simply supported:
- D = E·h³ / (12·(1−ν²)) ≈ 1330 N·m
- ρh = 15 kg/m²
- f₁₁ = (π/2)·√(D/ρh)·(1/a² + 1/b²) ≈ 18 Hz
- mode density rises steeply from there
- coincidence frequency f_c = c²/(1.8·c_L·h) ≈ 12.6/h Hz with h in metres (c_L ≈ 5200 m/s): about 2.1 kHz for 6 mm, 1.3 kHz for 10 mm

In a framed leaf the glass is set in rubber gaskets, so the modes are damped (Q about 10-30). Impacts produce a dull "thunk" with a short glassy shimmer around 1-3 kHz.

**Centre meeting (bi-parting).** Two leaves close on each other at a combined closing speed of 2 × check speed (about 40-100 mm/s). The meeting-stile seal is a rubber bulb or brush; the brush gives a soft "fsst" and the bulb a muted thud. Then the hold force compresses the seal. If one leaf lags (slack belt, uneven friction), the leaves meet off-time, giving two contacts.

**Hand-pushed impacts.** People push leaves. With the drive back-driven through the worm gearbox (the Stanley and record worms are back-drivable at low ratios such as 10-15:1), the leaf meets a 30-60 N controller response and the encoder-detected "Push & Close" fight. That gives a strained motor hum or buzz plus a gear grumble.

---

### 8. Sensors and the activation delay

#### Takeaway
Activation is a microwave Doppler radar on the header (K-band, 24 GHz class; direction-sensitive models open only for approach). Presence/safety uses active infrared curtains in the header (for example 2 x 24 spots) and/or threshold photo-beams. A walker at normal pace is detected about 1.5-2.5 m out, and the door starts opening about 0.1-0.5 s after detection. Sensors themselves are silent, except that some programme switches and keypads beep. The only "sensor sound" is the relay click inside the header (a faint tick), and on some models an audible alarm or buzzer in special modes.

#### Cited findings
- Besam owner test: "When walking towards the door opening at moderate speed, the door should start opening when you are about 5 ft (1.5 meter) from the door... Move slowly through the door (about 6 in/s (15 cm/s)). The door shall remain open." One-way doors must reopen for an object at ≥ 8 in (20 cm) from the door. [P] SL500 owner's manual (URL above)
- BEA IXIO-DT1: "Radar & Active Infrared", "Two 24-spot, high-density, infrared safety curtains". The successor to the BEA EAGLE (radar) is the EAGLE ARTEK, which has direction sensing. [P] https://us.beasensors.com/en/product/ixio-dt1/ ; https://us.beasensors.com/en/product/eagle-artek/
- dormakaba accessories: "Prosecure" active infrared curtain to EN 16005 / DIN 18650; "MAGIC SWITCH" proximity radar switch. [P] ES 200 technical folder
- ANSI A156.10 activation zone ≥ 43 in (1090 mm) deep, effective to within 5 in of the face; presence detection active "from fully open to within 6 inches (150 mm) of closed", which is why the last 150 mm is covered by latch-check speed rather than sensing. [P] A156.10-1999 8.2.1-8.2.2
- Stanley spec: sensor relays "Form C, 50 V at 0.3 A... Hold time of less than 0.5 seconds" (relay hold time, not detection latency); non-resettable cycle counter. [P] https://www.stanleyaccess.com/media/9321/download
- EN 16005 / EN 12978: protective devices must be tested once per cycle (the "test" signal); SL500 has presence and impulse monitoring parameters. [P] EDSF guideline; SL500 manual
- STA 20 program panel "BDE-D" has a keypad with a menu; Besam OMS selectors have key-lock features. [P] record manual; SL500 manual

#### Inferences [E]
**Microwave.** Typical operating frequency is 24.125 GHz. At walking speed 1.4 m/s the Doppler shift is 2·v·f/c = 2·1.4·24.125e9/3e8 ≈ 225 Hz, internal to the sensor and inaudible. The sensor decides in about 50-200 ms and closes a relay (click about 5 ms).

**Controller and motion delay.** The controller reacts within one control tick (≤ 10 ms). If a lock is engaged, unlock plus opening delay adds 0.1-0.5 s. Motor current then rises to accelerate in tens of milliseconds, giving the first audible "clunk" of backlash take-up and belt tightening.

**Arrival timing.** A walker at 1.4 m/s detected at 1.5-2.0 m arrives about 1.1-1.4 s later. The door must be about 0.7 m open by then, which the 0.9 s figure from section 5 meets. Total detection-to-motion latency is about 0.1-0.3 s unlocked and 0.3-0.8 s locked.

**Reopen on obstruction.** Presence while closing makes the door reverse "immediately" (SL500): a hard deceleration from about 0.3 m/s at about 2-3 m/s². That reversal is a distinct sound: backlash clunk, belt slap and a rising whine as it reopens. Afterwards the next close runs at creep speed past the obstruction point (Stanley, SL500, ESA II).

---

### 9. Published noise figures and what people hear

#### Takeaway
Manufacturers rarely publish sound levels:
- record STA 20: "< 45 dB" (no weighting or distance stated)
- Tormax iMotion: "< 70 dB(A)", which is the Machinery Directive declaration threshold (any machine under 70 dB(A) may simply declare "< 70"); it is a ceiling, not a measurement
- GEZE Slimdrive: "very quiet running" with no number; I did not find a published GEZE dB figure
- dormakaba ES 200, Stanley Dura-Glide, Horton: no figures found

For a model, use about 40-50 dB(A) at 1 m for the drive in cruise on a good operator, 50-60 dB(A) for a worn or steel-wheel unit, and lock and impact transients 10-20 dB above the running level for tens of milliseconds.

#### Cited findings
- record STA 20 manual: "Noise emission: < 45 dB". The marketing says "The absence of noise is the most striking feature of our automatic [doors]" and "whisper quiet operation". [P] https://www.recordukdirect.co.uk/media/Sliding%20Doors/STA20/STA20Manual.pdf ; record booklet (URL above); https://godoors.com.au/automatic-doors/record-doors/record-sta20/
- Tormax: "Noise emission level < 70 db (A)". [P] https://s3.amazonaws.com/s3-absupply-net/pdf/Instruction%20Use%20For%20iMotion%202202-2301-2302-2401.pdf
- GEZE Slimdrive: "Very quiet running, low-wear direct current drive". [P] GEZE Slimdrive SL NT data sheet (api.geze.com link above) and https://www.geze.co.uk/en/products-solutions/sliding_doors/automatic_sliding_doors/slimdrive/slimdrive_sl_nt_fr/p_89290
- Noise sources named in manuals:
  - belt against beam or cover (SL500)
  - "noise-absorbent guide rail" and the user check for "unusual and noticeable movement noises... in the drive, guide system or floor guides" (Tormax)
  - "Lock produces periodic switching noise" when jammed (Tormax E11)
  - toothed-belt jumping (Tormax E55)
  - the lock's one or two clicks on unlock, and silence from the lock in EXIT (SL500)
  - the "rubber strip" track surface to reduce running noise (ES 200 reseller)
  - "check motor and gear box for leakage and noise" in the Besam planned maintenance
  - [P] manuals above
- Stanley planned-maintenance list (in the SL500 owner's manual's US checklist): "Inspect drive pulleys and belt for proper alignment. Clean hanger rollers... Inspect anti-riser for damage and/or binding." [P] SL500 owner's manual

#### Inferences [E]
The event-by-event sound of one cycle, in order.

**1. Detection.**
- Optional relay tick in the header, about 30-40 dB(A) at 1 m.
- If locked: coil click(s) at 50-65 dB(A) peak, 1-6 kHz, 5-30 ms each; LDB gives two, 50-200 ms apart.

**2. Start.**
- Backlash take-up "tok" (low-mid, 100-500 Hz, the header ringing).
- Belt tension change.
- Leaves begin to move. The floor guide shoe may knock across its clearance.

**3. Acceleration (about 0.5 s).**
- Motor and gear whir rising from 0 to about 55 Hz fundamental (geared drive).
- Belt mesh rising to 87.5 Hz (S8M) or 140 Hz (5 mm pitch).
- Commutator and brush noise rising on brushed motors.
- Possibly a faint PWM whine fixed in pitch (if under 16 kHz).
- On brushless direct drive, mostly the belt and rollers.

**4. Cruise (about 0.5-1 s).**
- Steady tone complex plus broadband rolling rumble from 2-8 wheels (polymer on aluminium: soft hiss-rumble, 200 Hz-3 kHz).
- Floor-guide scrape.
- Header and cover radiation (it is an aluminium tube that buzzes at belt-mesh harmonics).

**5. Braking.** All tones glide down. The worm drive back-drives a little and the belt unloads. Some drives produce a "regen" hum change.

**6. Open creep and stop.**
- Tones down to a few Hz: tooth ticks, roller "rrr".
- Soft rubber-stop "thup" (or none if stopped electronically).
- Leaf and glass small ring.

**7. Hold.** Silent apart from the closed or open holding current, which some drives hold as a faint hum.

**8. Closing.** As steps 3-5, but about 0.4-0.6 x the frequencies and a longer cruise.

**9. Latch check.** Long slow creep (2-3 s) with a ticking texture.

**10. Meeting.** Seal "thup" or brush "fsst", then a small push (hold force).

**11. Locking.**
- Optionally, after a delay, coil click plus bolt drop "clack".
- For record, the brake engaging.

**Faults that make doors loud:**
- worn or flat-spotted PU wheels: once-per-rev thump at 2-4 Hz during cruise
- dirty track: crackle
- slack belt: slap at 5-15 Hz, tooth jump clack under load
- dry or failed floor guide: squeal or scrape
- loose header cover: rattle at mesh frequency
- misadjusted anti-riser touching: continuous scuff
- end stops worn away: metal-on-metal bang at open, 1-3 J
- lock misaligned: repeated buzzing or retry clicks (Tormax "periodic switching noise")

---

### 10. Numbers to use in a simulation (defaults)

All values here are **[E]**, derived from the cited ranges above.

**Leaf**
- mass: 60 kg (bi-parting), 90 kg (single)
- size: 1.0 x 2.2 m framed glass, 6 mm tempered; first plate mode about 18 Hz, gasket-damped Q about 20

**Travel and speeds**
- travel: 0.9 m per leaf (bi-parting 1.8 m opening)
- open speed: 0.7 m/s per leaf (Tormax default 0.70 m/s; ES 200 max 0.7; SL500 max 0.7 per leaf; record 0.7)
- close speed: 0.30 m/s (ANSI cap for ≤ 71 kg; Tormax default 0.40 m/s in EN markets)
- check/creep: open 50 mm/s over the last 50-100 mm; close 21 mm/s (Tormax default) over the last 51-60 mm (ANSI ≥ 51 mm)
- acceleration: opening 1.0-1.5 m/s², closing 0.6-1.0 m/s², braking up to 2 m/s², emergency reversal 2-3 m/s²
- hold-open: 2-3 s after last detection (ANSI ≥ 1.5 s)
- closed hold force: 50 N (Tormax default; SL500 0-60 N)
- activation-to-motion: 0.2 s unlocked; add 0.3 s and the lock clicks when locked

**Drive (geared)**
- 24-30 V brushed PM DC, 100 W, 3350 rpm rated
- worm 15:1
- 24T S8M pulley, PD 61 mm, 12 mm PU belt
- motor 54.7 Hz, belt mesh 87.5 Hz at 0.7 m/s; scale everything with v

**Drive (direct)**
- external-rotor PMSM, 6 N·m, pulley PD about 60 mm, 14 mm belt; motor 3.7 rev/s at 0.7 m/s
- belt mesh at v/p (5 or 8 mm pitch)

**Wheels**
- 2 per leaf (≤ 90 kg) or 4 (heavier); 64 mm PU (Stanley minimum) or 50 mm nylon; sealed 608-class bearings
- rotation 3.5 Hz at 0.7 m/s
- anti-riser 51 mm with 0.5 mm gap

**Floor guide:** plastic pin or fork in an aluminium channel, 1-3 mm clearance.

**Lock:** solenoid 24 V, about 20-50 ms pull-in, bolt stroke about 5-10 mm, leaf play when locked 2-9 mm (SL500).

**Stops:** rubber pad, k about 2×10⁵ N/m; impact energy 0.01-0.3 J under normal control.

---

### 11. Gaps and cautions

- **ANSI/BHMA A156.10-2017 and -2024.** I read the 1999 and 2005 texts in full. The 2017/2024 changes for sliders (for example any kinetic-energy table, any limit on opening speed, monitored sensors) were not verified. Trade summaries of the later editions still state the 1 ft/s up to 160 lb, √(160/W), 2 in latch check, 30 lbf and 50 lbf rules.
- **EN 16005 full-energy dynamic force limits** (1400 N / 400 N / 150 N in 0.75 s / 25 or 80 N in 5 s) come from secondary sources that disagree on the 5 s value. Only the low-energy 1.69 J / 67 N / 150 N-in-last-50 mm / 90 N figures are from a manufacturer-federation guideline (EDSF) and an installer guide (BFT).
- **Not retrieved:**
  - GEZE ECdrive, Powerdrive, Slimdrive speeds and motor data (geze.com blocks fetches; 0.7 m/s is from a search summary)
  - dormakaba ESA100/200/500 US fact sheets (only the ESA II controller manual)
  - Portalp
  - Horton 2000-series slider G200 installation data
  - BEA radar datasheets (frequency, response time)
  - any third-party acoustic measurement of an automatic door
- **Reseller data.** The ES 200 belt (S8M, 12 mm), wheel and track details are from doordynamic.com, an aftermarket parts seller, not dormakaba. The S8M, 15:1 and 3350 rpm figures are mutually consistent with dormakaba's 0.7 m/s maximum (section 2), which supports them.
- **Not found anywhere:**
  - wheel diameters for any brand except Stanley's spec minimum (64 mm load, 51 mm anti-rise)
  - pulley tooth counts for any brand (24T S8M is a calculation)
  - commutator segment counts (the table gives k = 8, 12, 13 as brackets)

---

# Part C. Rolling and drive-train sound for sliding doors: theory, measurements, worked numbers

Scope: manual patio doors on nylon/acetal/steel rollers running on an aluminium sill track, and automatic sliding doors (brushed DC motor, gearbox, toothed belt, hanging carriage rollers). The aim is to list what the literature lets a simulation compute from the parts, with numbers.

Labels: plain statements under "Cited findings" are published values with the URL they came from. "[E]" marks my own estimate or calculation. Where a source was read only through a search snippet or from memory and not fetched, that is stated on the bullet.

Research limits: the web search budget for this session ran out partway through. Items I could not find a source for in time (the suitcase-on-pavement study, drawer-slide noise data, Colson/Tente/Blickle dB claims, Faulhaber/Dunkermotoren gearbox dB(A) specs, door operator dB(A) specs, belt unit-mass tables) are marked as gaps rather than filled from memory.

---

### 0. The numbers that matter most

| Quantity | Value | Status |
|---|---|---|
| Roughness-to-frequency mapping | f = V/λ | published (Thompson) |
| Rolling noise vs speed (roughness excited) | ~30 log10 V | published (Thompson) |
| Impact noise (wheel flats/joints) vs speed | ~20 log10 V | published (Thompson) |
| Rolling noise vs wheel load | "relatively insensitive"; flats +3 dB for 50 to 100 kN | published (Thompson) |
| Railway contact patch length | 10-15 mm | published |
| Contact filter: roll-off / first null | ka ≈ 1 / ka ≈ π (λ = 2a) | published (Remington via Thompson) |
| ISO 3095 rail roughness limit | +17.1 dB re 1 µm at λ = 400 mm down to -11.0 dB at 3.15 mm | published (22-value table) |
| 32 mm acetal wheel, 150 N, on aluminium, crowned (R_e = 16 mm) | a = 0.81 mm, 2a = 1.62 mm, δ0 = 41 µm, k_H = 5.5 MN/m, p0 = 109 MPa | [E] |
| Same wheel, flat 5 mm tread (line contact) | 2a = 0.85 mm, p0 = 45 MPa, k ≈ 7 MN/m | [E] |
| Contact filter roll-off at 0.5 m/s (crowned) | ka = 1 at 98 Hz; first null 308 Hz | [E] |
| Wheel/axle (30 g) on contact spring | ≈ 2.15 kHz (15 g: 3.0 kHz) | [E] |
| 15.3 kg panel share on contact spring | ≈ 95 Hz (in practice set by the roller housing compliance) | [E] |
| Wheel rotation, 32 mm at 0.5 m/s | 4.97 Hz | [E] |
| 608 bearing in that wheel | BPFO 12.8 Hz, BPFI 22.0 Hz, BSF 8.7 Hz, cage 3.1 Hz | [E] |
| T5 belt at 0.7 m/s | mesh 140 Hz (any pulley size) | [E] |
| T5 10 mm free span, 150 N, 1.0 m | ≈ 46 Hz fundamental (≈ 30 Hz at 1.5 m) | [E] |
| Belt noise slope vs speed (Gates/SDP chart) | 3 mm HTD ≈ 32 log V, 3 mm GT3 ≈ 25 log V, 5 mm HTD ≈ 17 log V | read from published chart |
| Linear guide noise slope | HIWIN HG25 ≈ 36 log V; NSK #25 ≈ 28 log V (8.5 dB per doubling) | read from published charts |
| Castor vendor claims at 3 mph on VCT | rubber 38-44, PU 85A 42-48, PU 95A 48-54, phenolic 60-68, steel 65-75 dB | vendor blog, not a standard test |

The central conclusion [E]: for a 25-40 mm polymer wheel at 0.3-1 m/s, the contact filter cut-off sits at about 60-200 Hz. Linear roughness excitation (the railway mechanism) can therefore only make a low rumble. Everything audible above a few hundred hertz has to come from mechanisms that the linear railway model leaves out: grit and debris impacts (non-linear, loss of contact), track joints and steps, the bearing, out-of-roundness and moulding marks on the wheel, flange and seal friction, and in automatic doors the motor, gears and belt. The railway theory still supplies the structure: a contact spring between two receptances, roughness as a displacement input, f = V/λ, and a patch-size low-pass.

---

### 1. Rolling contact noise theory (railway: Remington, Thompson, TWINS)

#### Takeaway

Rolling noise is a displacement-driven problem. The combined roughness r(x) of wheel and rail, scanned at speed V, imposes a relative displacement at frequency f = V/λ. The contact patch averages out wavelengths shorter than its length (the contact filter). The Hertzian contact spring sits in series between the wheel receptance and the rail receptance. Whichever structure is most mobile at a given frequency takes most of the motion and radiates. Radiated power is ρc·S·σ·⟨v²⟩. For roughness excitation the total A-weighted level rises about 30 log V and is nearly independent of load. Discrete defects (flats, joints) behave as impacts, rise about 20 log V, and depend on load.

#### Cited findings

- Excitation frequency f = V/λ. Relevant wavelengths about 5-200 mm with amplitudes of the order of microns. Wavelengths short compared with the contact patch length (typically 10-15 mm) are attenuated: the contact filter. Remington first derived an analytical contact filter and later a numerical model of discrete point-reacting springs, which gives a smaller filter effect with measured roughness than the analytical one. Thompson, ICSV21 keynote 2014: https://generic.wordpress.soton.ac.uk/track21/wp-content/blogs.dir/sites/4/2014/08/Thompson_DJ_keynote_ICSV21.pdf
- Dynamic interaction, single degree of freedom at angular frequency ω: v_R = iω r Y_R / (Y_R + Y_W + Y_C) and v_W = -iω r Y_W / (Y_R + Y_W + Y_C), where Y_R, Y_W and Y_C are the rail, wheel and contact-spring mobilities and r is the roughness amplitude. The rail has the largest mobility over most of 100-1000 Hz, so v_R ≈ iωr there. The wheel dominates at high frequency, where lightly damped resonances with strong radial components occur. Same source.
- Radiated power W = ρc·S·σ·⟨v²⟩, with σ the radiation efficiency. Simple monopole or dipole directivity is adequate for pass-by averages. Same source.
- An EMU at 120 km/h on track with resilient pads: the rail is the dominant source and the wheel contribution is about 7 dB lower overall. Soft pads raised noise by 3-4 dB(A) through lower track decay rates. Track decay-rate change gives ΔL = 10 log10(DR_u/DR_d). Same source.
- Measured high-speed train data fit a 30 log10 V line for rolling noise plus an 80 log10 V line for aerodynamic noise, crossing at about 370 km/h. Same source.
- Hertzian contact: F = C_H δ^(3/2) for δ > 0 and F = 0 otherwise (loss of contact), with δ = x_w - x_r - r. Linearised: dF ≈ (3/2) C_H δ0^(1/2) dδ = k_H dδ. Rolling noise from roughness rises about 30 log10 V. Wheel-flat noise rises about 20 log10 V once loss of contact occurs: above 30 km/h for a new 2 mm flat and above 50 km/h for a rounded one. Flat impact noise rises about 3 dB from 50 to 100 kN load, while roughness rolling noise is "relatively insensitive to wheel load". Contact patch "typically 10-15 mm long". The analytical contact filter gives too much attenuation at high frequency compared with the DPRS model. Without a contact filter, predicted power above about 1 kHz at 160 km/h is overestimated, "correspondingly lower frequencies at lower speeds". Thompson, "Wheel/rail rolling noise: the effects of non-linearities in the contact zone", ICSV10: https://www.southampton.ac.uk/assets/imported/transforms/content-block/UsefulDownloads_Download/223F9A5BFBAD4A0885B7790738D08585/icsv10_240.pdf
- Typical rail/wheel roughness amplitudes 0.1-30 µm over wavelengths of roughly 5-500 mm. The Hertzian spring is the common normal contact model. Frequency-domain (linear) models fail with severe roughness or low preload, which cause loss of contact. Time-domain models are the only option for flats and joints. Pieringer, DAGA 2013: https://publications.lib.chalmers.se/records/fulltext/176336/local_176336.pdf
- Remington analytical contact filter for a circular patch of radius a and roughness wavenumber k = 2π/λ: |H(k)|² = [4/(α (ka)²)] ∫₀^{arctan α} J₁²(ka·sec ψ) dψ. Here α describes how well the roughness is correlated across the width of the contact. The filter rolls off near ka = 1 and has minima near ka = π (λ = 2a) and its multiples, whatever α is. (Formula as given on the ScienceDirect "Contact filter" topic page, read through a search snippet because the page returned 403: https://www.sciencedirect.com/topics/engineering/contact-filter. The original is Remington, JASA 81(6), 1987, "Wheel/rail rolling noise I/II", and Thompson's book "Railway Noise and Vibration", Elsevier 2009, ch. 5. I did not fetch either.) I checked that the formula tends to 1 as ka → 0.
- Simplified contact filters: Ford & Thompson, JSV 2006, "Simplified contact filters in wheel/rail noise prediction": https://www.sciencedirect.com/science/article/abs/pii/S0022460X05007704 (title only, not fetched).
- Acoustic roughness level: L_r = 20 log10(r/r0), with r the RMS surface height in a 1/3-octave wavelength band and r0 = 1 µm. EN 15610 covers wavelengths 3-100 mm (optionally 250 mm). EN 15610:2019 catalogue page: https://standards.iteh.ai/catalog/standards/cen/cb8b7c18-6b28-4952-85ed-4ad47fbf8c0e/en-15610-2019 ; the ISO 3095:2013 sample defines "acoustic roughness spectrum ... in dB re 1 µm": https://cdn.standards.iteh.ai/samples/55726/f31611a39187419ebad380e6fc44e56d/ISO-3095-2013.pdf
- ISO 3095:2013 Figure 2 rail roughness limit (the same 22 values as the "2007 TSI limit" in EN 15610:2009 Annex B.9.2), in dB re 1 µm against 1/3-octave wavelength. These are transcribed in the open-source `phonometry` library: https://github.com/jmrplens/phonometry/blob/main/src/phonometry/environment/sources/rolling_stock_noise.py

  | λ (mm) | 400 | 315 | 250 | 200 | 160 | 125 | 100 | 80 | 63 | 50 | 40 |
  |---|---|---|---|---|---|---|---|---|---|---|---|
  | L_r (dB) | 17.1 | 15.0 | 13.0 | 11.0 | 9.0 | 7.0 | 4.9 | 2.9 | 0.9 | -1.1 | -3.2 |

  | λ (mm) | 31.5 | 25 | 20 | 16 | 12.5 | 10 | 8 | 6.3 | 5 | 4 | 3.15 |
  |---|---|---|---|---|---|---|---|---|---|---|---|
  | L_r (dB) | -5.0 | -5.6 | -6.2 | -6.8 | -7.4 | -8.0 | -8.6 | -9.2 | -9.8 | -10.4 | -11.0 |

  The slope is 2 dB per 1/3 octave (amplitude ∝ λ^2) above 40 mm and 0.6 dB per 1/3 octave (amplitude ∝ λ^0.6) below 31.5 mm.
- The prEN ISO 3095 draft set one rail limit curve. ODS proposed roughness classes A-E (class C ≈ the limit). Roughness was measured on 3 lines along the railhead. A measuring system with a 0.5 m to 1 mm wavelength range had about 0.4 dB standard deviation from 36 lines. Thrane, DAGA: http://pub.dega-akustik.de/DAGA_1999-2008/data/articles/000994.pdf

#### Hertz formulas used below (textbook; Johnson, "Contact Mechanics", 1985)

- Effective modulus: 1/E* = (1-ν₁²)/E₁ + (1-ν₂²)/E₂. Effective radius: 1/R_e = 1/R₁ + 1/R₂ for each principal direction. For non-equal principal curvatures use √(R_x R_y) as a first approximation.
- Point contact (sphere-equivalent): F = (4/3) E* √R_e · δ^(3/2), so C_H = (4/3) E* √R_e.
- Patch radius: a = (3 F R_e / (4 E*))^(1/3). Approach: δ0 = a²/R_e. Peak pressure: p0 = 3F/(2πa²).
- Linearised stiffness: k_H = dF/dδ = (3/2) C_H^(2/3) F^(1/3) = 2 E* a = 3F/(2δ0). Stiffness grows as F^(1/3), patch as F^(1/3), approach as F^(2/3).
- Line contact (cylinder of radius R, tread width b): half-width a = √(4 F R / (π b E*)), p0 = 2F/(π a b). The approach depends logarithmically on the reference depth. For a cylinder on a rigid flat: δ ≈ [2(F/b)(1-ν²)/(πE)]·[ln(4R/a) - 1/2].
- Hertz impact duration for a mass m* striking at speed v: t_c = 2.868 (m*² / (R_e E*² v))^(1/5).

#### Inferences [E]: how the railway numbers scale down

- The 30 log V law is not a law of physics. It comes from the shape of the roughness spectrum (falling towards short wavelengths) combined with frequency-shifting through a fixed system response and A-weighting. Over 0.3 to 1 m/s it predicts +15.7 dB. For a sliding door I would use 20-30 log V for the "smooth" rolling component and about 20 log V for debris and joint impacts, as Thompson found for flats.
- Railway contact: 2a ≈ 12 mm and V ≈ 30 m/s give a filter cut-off near V/(2πa) ≈ 800 Hz, so rolling noise fills 500-2000 Hz. Door contact: 2a ≈ 1-2 mm and V ≈ 0.5 m/s give a cut-off near 100 Hz. The ratio V/a sets everything, and it is about 8 times lower for the door.
- The door also has an advantage the railway lacks: patio-door track debris (sand, grit, hair, leaf fragments) has heights of 50-500 µm. That is comparable to or larger than the static approach δ0 ≈ 40 µm, so contact is lost and the event is an impact, not a linear roughness response. In railway terms a dirty sill track is a rail covered in small wheel flats.

---

### 2. Worked example: 32 mm acetal (Delrin) wheel at 0.5 m/s under 150 N on aluminium

Material data [E, from common handbook values]: acetal homopolymer E ≈ 3.1 GPa, ν ≈ 0.35, ρ = 1420 kg/m³, loss factor ≈ 0.02-0.05 at audio frequency. Aluminium 6063-T5 E = 69 GPa, ν = 0.33.

#### 2.1 Effective modulus [E]

1/E* = (1 - 0.35²)/3.1e9 + (1 - 0.33²)/69e9 = 2.831e-10 + 0.129e-10 = 2.960e-10, so **E* = 3.38 GPa**. The acetal does 96% of the deforming. The aluminium is effectively rigid at the contact, though not as a structure; see 2.6.

#### 2.2 Contact patch [E]

Case A, crowned tread with transverse radius about 16 mm, so the contact is sphere-like, R_e = 16 mm:

- a = (3·150·0.016 / (4·3.38e9))^(1/3) = (5.33e-10)^(1/3) = **0.81 mm**. Patch diameter **1.62 mm**.
- δ0 = a²/R_e = **41 µm**.
- p0 = 3F/(2πa²) = **109 MPa**. This is at or above the compressive yield of acetal (roughly 100-120 MPa), so a real 150 N roller of this size has to be grooved, wider or conformal. Expect a permanent flat track worn into a crowned acetal wheel.
- C_H = (4/3)E*√R_e = 5.70e8 N/m^1.5.

Case B, flat cylindrical tread 5 mm wide on a flat track (line contact):

- a = √(4·150·0.016/(π·0.005·3.38e9)) = 0.425 mm. Contact length **2a = 0.85 mm** along the rolling direction and 5 mm across.
- p0 = 45 MPa, which is acceptable for acetal.
- Approach (wheel side only, aluminium rigid): δ ≈ 2(30 000 N/m)(0.8775)/(π·3.1e9)·(ln(4·16/0.425) - 0.5) = 5.41e-6 × 4.52 ≈ **24 µm**.

Case C, concave-grooved wheel on a round-topped rib (the usual patio-door "rail"): the contact is nearly conformal across the groove. R_e is large across and 16 mm along, so the patch is long across and short along. Treat it as Case B with b equal to the wetted arc width.

#### 2.3 Contact stiffness [E]

- Case A: k_H = 2E*a = 2·3.38e9·8.11e-4 = **5.5 MN/m**. Check: 3F/(2δ0) = 225/4.11e-5 = 5.48e6.
- Case B: k = (dδ/dF)^-1 ≈ [3.60e-8·(4.52 - 0.5)]^-1 ≈ **6.9 MN/m**.
- Other wheel materials, same geometry as Case A, 150 N [E]:

  | Wheel | E* | a (mm) | k_H (MN/m) | ka = 1 at 0.5 m/s | wheel 30 g on k_H |
  |---|---|---|---|---|---|
  | Acetal (POM) | 3.38 GPa | 0.81 | 5.5 | 98 Hz | 2.15 kHz |
  | Nylon PA6 dry | 3.17 GPa | 0.83 | 5.3 | 96 Hz | 2.1 kHz |
  | Nylon PA6 moisture-conditioned (E ≈ 1.4 GPa) | 1.63 GPa | 1.03 | 3.4 | 77 Hz | 1.7 kHz |
  | Polyurethane 95A (E ≈ 60 MPa, assumed) | 78 MPa | 2.85 | 0.44 | 28 Hz | 0.61 kHz |
  | Steel | 58 GPa | 0.31 | 36 | 253 Hz | 5.5 kHz |

  Polyurethane is quiet because its patch is 3.5 times larger (contact filter cut-off 3.5 times lower) and its contact resonance sits about 2 octaves lower. It also has high damping. Steel on aluminium is the opposite: a small patch, a stiff spring, and a resonance in the most audible band. This ranking matches the vendor castor ranking in section 3.

#### 2.4 Contact resonances [E]

Two masses sit on the contact spring.

- **Local (unsprung) mass**: the wheel (acetal ring OD 32 / ID 22 / 8 mm wide ≈ 4.8 g), a 608 bearing (≈ 12 g), the axle and part of the roller housing. Total 15-50 g.
  - f = (1/2π)√(k_H/m): 15 g → **3.0 kHz**, 30 g → **2.15 kHz**, 50 g → **1.67 kHz**.
  - This is the counterpart of the railway "P2" resonance. Debris impacts ring here, and it is the upper limit of what the contact transmits efficiently.
- **Sprung mass**: 150 N corresponds to 15.3 kg per wheel, a 30 kg panel on two rollers. Panel mass on k_H gives f = (1/2π)√(5.48e6/15.3) = **95 Hz**.
  - In practice the roller housing, height-adjust screw and sash bottom rail (a thin aluminium or PVC extrusion) are much softer than 5.5 MN/m. The sprung resonance is therefore set by the housing, probably 20-80 Hz. The panel is effectively decoupled from the contact above a few hundred hertz except through the housing.
- **Hertz impact duration** (Case A) for debris, using m* as the unsprung mass:
  - 30 g at 0.05 m/s: t_c = 0.45 ms.
  - 30 g at 0.2 m/s: t_c = 0.34 ms.
  - The whole 15.3 kg falling at 0.044 m/s (100 µm drop): t_c = 5.6 ms.
  - So a grit hit on a free-running wheel has spectral content up to about 1/t_c ≈ 2-3 kHz, the same scale as the local contact resonance. That is consistent.

#### 2.5 Contact filter cut-off [E]

Case A, a = 0.81 mm:

- Roll-off begins at ka = 1, so λ = 2πa = 5.09 mm and **f = V/λ = 0.5/0.00509 = 98 Hz**.
- First null at ka = π: λ = 2a = 1.62 mm, **f = 308 Hz**.

Attenuation of |H|² from the Remington formula (my own numerical integration):

| ka | α→0 (fully correlated across track) | α = 0.5 | α = 1 | α = 2 |
|---|---|---|---|---|
| 0.5 | -0.3 dB | -0.3 | -0.4 | -0.6 |
| 1 | -1.1 | -1.2 | -1.5 | -2.5 |
| 1.5 | -2.6 | -2.8 | -3.4 | -5.4 |
| 2 | -4.8 | -5.2 | -6.4 | -9.1 |
| 3 | -12.9 | -14.4 | -17.0 | -18.1 |
| 6 | -20.7 | -22.7 | -24.5 | -26.2 |
| 10 | -41.2 (near a null) | -35.3 | -32.7 | -33.9 |
| 20 | -43.5 | -38.3 | -39.5 | -41.5 |

The envelope falls at about 9 dB per octave of wavenumber at large ka. For α → 0, |H|² → [2J₁(ka)/ka]², whose envelope is 8/(π(ka)³).

At 0.5 m/s with a = 0.81 mm, ka = 2πf a/V = 0.0102·f:

- 100 Hz: ka = 1.0, about -1.5 dB.
- 300 Hz: ka = 3, about -15 dB.
- 1 kHz: ka = 10, about -35 dB.
- 3 kHz: ka = 30, about -45 dB.

Case B (line contact, 2a = 0.85 mm): roll-off at about 190 Hz along the track. Across the 5 mm tread, uncorrelated short-wavelength roughness adds a further averaging loss (large α).

For the simulation [E]: filter the roughness displacement through a low-pass of about 3rd order whose corner is f_c = V/(2πa), with a computed from the instantaneous load. Notch structure is not worth modelling, because real patches are not uniform and the DPRS results show shallower filtering than the analytical model.

#### 2.6 Receptances at the contact [E]

- **Contact mobility**: Y_C = iω/k_H. At 1 kHz, |Y_C| = 6283/5.48e6 = 1.15e-3 m/(N·s).
- **Wheel as a 30 g mass**: |Y_W| = 1/(ωm) = 5.3e-3 at 1 kHz and 2.65e-3 at 2 kHz. It crosses Y_C at about 2.15 kHz, which is the resonance above.
- **Track**: a thin aluminium web 1.5 mm thick behaves locally like a plate.
  - Infinite-plate point mobility Y = 1/(8√(B m'')).
  - B = Eh³/(12(1-ν²)) = 21.8 N·m and m'' = 4.05 kg/m², so **Y_T ≈ 0.013 m/(N·s)**, frequency-independent.
  - A rib supported on a solid sill is stiffer than this. A hollow multi-chamber sill is about this soft.
- Consequence:
  - Below about 2 kHz, Y_T > Y_W > Y_C. The thin track takes most of the imposed roughness displacement (v_T ≈ iωr), as the rail does in 100-1000 Hz.
  - The track extrusion (and the floor or sill it is screwed to) is a major radiator. The sound should come from along the sill, not only from the roller.
  - The wheel's own motion is mass-controlled and small until the contact resonance.
  - The glass panel, excited through the housing, radiates efficiently above its coincidence frequency. That is about 12/h kHz with h in mm: about 2 kHz for 6 mm glass, 3 kHz for 4 mm.

#### 2.7 Rotation-locked components [E]

- Wheel rotation: f_r = V/(πD) = 0.5/(π·0.032) = **4.97 Hz**.
- Eccentricity or out-of-roundness gives a once-per-revolution load and height modulation at 5 Hz. For moulded acetal, run-out of 20-50 µm is plausible; compare δ0 = 41 µm.
- Polygonal harmonics (orders 2-20) fall at 10-100 Hz.
- A moulding gate mark or parting-line flash gives a tick once per revolution, an impact train at 5 Hz.
- Rath and Conan (section 7) both found this periodic modulation to be the strongest perceptual cue for "rolling" as opposed to "sliding", and the cue that conveys speed and size.

#### 2.8 Bearing frequencies, 608 deep-groove bearing in the wheel [E]

Geometry: 7 balls of d = 3.969 mm, pitch diameter D ≈ 15 mm, contact angle 0. Relative race speed f_r = 4.97 Hz (outer race turns with the wheel, inner race fixed on the axle).

- Ball pass, outer race: (N/2) f_r (1 - d/D) = **12.8 Hz**.
- Ball pass, inner race: (N/2) f_r (1 + d/D) = **22.0 Hz**.
- Ball spin: (D/2d) f_r (1 - (d/D)²) = **8.7 Hz**.
- Cage, ground frame (outer race rotating): (f_r/2)(1 + d/D) = **3.1 Hz**.

All are sub-audio. They appear as the repetition rate of clicks when a race is dented or contaminated, as amplitude modulation of the bearing hiss, and as a 3.1 Hz cage modulation. The audible carrier is the ring and housing resonance (kHz). Many patio-door rollers have no ball bearing: an acetal or nylon wheel runs on a steel pin as a plain bearing. Then there are no ball-pass rates, and the dry-pin failure mode is a squeak (section 4).

#### 2.9 Debris event [E]

A 100 µm grain on the track, in Case A:

- The wheel climbs it over a horizontal distance √(2 R h) = 1.79 mm, which takes 3.6 ms at 0.5 m/s. This is effectively a raised-cosine displacement pulse already low-passed by the wheel radius, the geometric filter in Thompson's wheel-flat work.
- Coming down, a free unsprung 30 g wheel pushed by 150 N could fall at up to 1 m/s; a rigidly attached 15 kg panel falls at 0.044 m/s. Real hardware is in between, set by the housing spring.
- Use t_c = 0.3-0.6 ms impacts (energy to about 2-3 kHz) exciting the wheel/housing resonance and the track.
- A softer grain is crushed or embedded instead. That gives crunch: a burst of small impacts.

#### 2.10 Level sanity check [E]

- Sliding doors under AAMA/WDMA/CSA 101/I.S.2/A440 must keep moving with ≤ 90 N for most classes (≤ 115 N for heavy commercial).
- Test reports show 42-53 N to keep moving on residential and light-commercial patio doors and 78 N on a 215 kg commercial panel, and a Pella 250 spec shows 12/10 lbf (53/44 N) to start and keep moving. Sources: AAMA 101-97 https://law.resource.org/pub/us/cfr/ibr/001/aama.101-IS2.1997.pdf ; Fleetwood 4070-T report https://www.fleetwoodusa.net/Documents_Guide/Products/4070-T/Test_reports/Structural/030325+A440+4070-T_XXX+216x146+s0187.02.pdf ; Pella 250 https://media.pella.com/professional/adm/Vinyl250/Pella-250Series_Std-PrmSlidingDoor.pdf (see Part A §5).
- At 44 N and 0.5 m/s the mechanical dissipation is 22 W. Most of it is pile-seal friction and rolling hysteresis, not sound.
- A radiated sound power of 1-10 µW (L_W 60-70 dB) would mean an acoustic efficiency of about 1e-7 to 1e-6. That is plausible for rolling and friction, and gives about 50-60 dB(A) at 1 m for a gritty door. A clean door would be about 15-25 dB lower.

---

### 3. Small wheels, castors, trolleys, linear guides, bearings

#### Takeaway

Published small-wheel data are thin. The usable quantitative pieces are vendor castor tables (material ranking and dB ranges at 3 mph), linear-guide noise curves against speed (slope 28-36 log V for steel balls in recirculating guides), and the general rule that a soft tyre, a larger diameter and a smooth floor each cost several dB. Joints and seams are the dominant event noise for hard wheels.

#### Cited findings

- Castor noise at 3 mph (1.34 m/s) on VCT floor, Type 2 meter at operator ear height (distance not stated):

  | Tyre | Hardness | Level |
  |---|---|---|
  | Neoprene rubber | 65A | 38-44 dB |
  | PU | 85A | 42-48 dB |
  | TPR | 75A | 44-50 dB |
  | PU | 95A | 48-54 dB |
  | Phenolic | — | 60-68 dB |
  | Forged steel | — | 65-75+ dB |

  Other claims from the same source: "85A polyurethane is 6 to 8 dB quieter than 95A on most floors". Doubling diameter from 4 to 8 inch "typically cuts noise 3 to 5 dB". Hard wheels (phenolic, nylon, steel) "transmit floor seam impacts as audible click-click-click at 40 to 65 dB". Retrofit reductions: wheel 6-9 dB, bearing 3-5 dB, isolator mounts 5-10 dB on resonance. This is a vendor blog and not a standardised test. CasterHQ: https://casterhq.com/blogs/caster-university/noise-vibration-control-casters
- Qualitative: PU castors are quieter than nylon; nylon is noisier "particularly when travelling over uneven surfaces". https://www.castor-wheels.co.uk/blogs/news/polyurethane-vs-nylon-castor-wheels-which-is-best
- Linear guide, HIWIN HG25 against QH25, no grease. Read from the chart on catalogue p. 93; microphone distance not stated on that page.
  - HG25: about 47.5 dB(A) at 100 mm/s (probably the floor), 53 at 400, 59 at 600, 64 at 800, 67 at 1000, 78 at 2000 mm/s.
  - QH25 (ball separators): about 5 dB lower throughout.
  - The spectrum is broadband from 1 to 20 kHz, about 20-30 dB per band on their scale.
  - RG20 against QR20 roller guides: 3 dB difference.
  - Slope from 400 to 2000 mm/s: 25 dB over a factor of 5, **≈ 36 log V**, about 11 dB per doubling.
  - HIWIN Linear Guideway technical information: https://www.hiwin.com/wp-content/uploads/Linear_Guideway-E-1.pdf
- Linear guide, NSK size #25 rail alone, microphone 500 mm above the block. Read from the chart:
  - NH series: 56.5 dB(A) at 90 m/min (1.5 m/s) and 65 dB(A) at 180 m/min (3 m/s).
  - LH series: 60 and 68.5 dB(A).
  - That is 8.5 dB per doubling of speed, **≈ 28 log V**. Redesigning the ball circulation path gave about 3 dB.
  - NSK NH/NS brochure p. 3: https://www.nsk.com/content/dam/nsk/am/en_us/documents/precision-americas/Linear-Guides-NH-NS-Series.pdf
- Bearing defect frequencies (BPFO, BPFI, BSF, FTF) as used in 2.8 are the standard kinematic formulas: Randall & Antoni, "Rolling element bearing diagnostics: a tutorial", MSSP 25 (2011) 485-520, https://doi.org/10.1016/j.ymssp.2010.07.017 (cited from memory, not fetched). They note real defect impacts jitter by 1-2% because of slip, so the impact train is cyclostationary rather than strictly periodic.

#### Gaps

- I could not locate within budget the rolling-suitcase-on-pavement study, hospital or shopping trolley papers, office-chair castor measurements, Colson/Tente/Blickle published dB values, drawer-slide noise data, or ISO 15242 small-bearing vibration grades for 608/626. Do not treat any number for these as sourced.

#### Inferences [E]

- Extrapolating the NSK curve (28 log V) down to door speed: 56.5 - 28·log10(1.5/0.5) ≈ **43 dB(A) at 0.5 m** for a steel ball guide at 0.5 m/s. HIWIN's curve flattens below about 300 mm/s at around 47 dB(A), which is likely the background. A steel-ball system at door speed is therefore in the low 40s at half a metre. A polymer wheel on aluminium should be quieter than that on a clean track.
- The vendor castor spread (rubber 38-44 to steel 65-75 dB at 1.34 m/s) is about 25-30 dB between soft and hard tyres. Section 2.3 gives part of the reason: k_H rises 80 times from PU to steel, and the contact filter corner rises 9 times. For a door, scaling 1.34 to 0.5 m/s with 20-30 log V takes off 9-13 dB. That puts acetal on a smooth track at about 35-45 dB at ear height, with joints and grit adding transient peaks of 10-20 dB.
- Diameter: the vendor's 3-5 dB per doubling agrees with patch length growing as R^(1/3) (the filter corner falls 21% per doubling) plus the geometric smoothing of steps over √(2Rh) (√2 longer per doubling).
- A rule for simulation: hard tyre means tonal ringing on impacts (high k_H, high contact resonance, low damping); soft tyre means thumps (low resonance, high damping, large patch).

---

### 4. Friction and brush noise: pile weatherstrip, polymer on aluminium

#### Takeaway

No published measurements of pile weatherstrip sliding noise were found. The physics needed is the general friction-noise framework: rubbing as dense random micro-impacts, stick-slip when friction falls with speed and the system is soft, and squeal when a stick-slip cycle locks onto a structural mode. For a pile seal the sound is a broadband hiss whose level grows with speed and with how hard the pile is compressed. A squeak needs a stiff polymer pad, a dry pin or a flange rubbing.

#### Cited findings

- Scraping/sliding force model: fractal noise with power ∝ ω^β, passed through a reson whose centre frequency scales with contact speed. Fractal dimension D = β/2 + 2, with D = 1.17-1.39 reported for machined surfaces at 1e-6 m scale. Under Coulomb friction F = μF_N, and assuming acoustic energy proportional to frictional power, the scraping audio-force amplitude is ∝ √(v F_N). van den Doel, Kry & Pai, FoleyAutomatic, SIGGRAPH 2001: https://www8.cs.umu.se/kurser/TDBD12/HT01/papers/foleyautomatic.pdf
- Perceptually, rubbing and scratching sit on one continuum of impact density: high impact density reads as rubbing and low density as scratching. In synthesis, rubbing is white noise (one impact per sample, Gaussian amplitudes) and scratching uses exponentially distributed inter-impact intervals. Conan et al., CMJ 2014: http://kronland.fr/wp-content/uploads/2015/05/2014_CMJ_Conan.pdf
- Review of friction acoustics (stick-slip, sprag-slip, mode coupling, squeal): Akay, "Acoustics of friction", JASA 111(4), 2002, https://doi.org/10.1121/1.1456514 (cited from memory, not fetched).

#### Inferences [E]

- **Stick-slip criterion.** For a contact of normal load N, static/kinetic friction difference Δμ, tangential stiffness k and effective mass m, the slip phase oscillates with velocity amplitude about 2Δμ N/√(km). If the drive speed V is above this, the contact never re-sticks.
  - Example, a pile strip: k ≈ 1e4 N/m per contact length, m ≈ 1 g, N ≈ 5 N, Δμ ≈ 0.1. Then √(km) = 3.2 N·s/m and v_crit ≈ 0.3 m/s.
  - So a door eased open slowly (< 0.3 m/s) can chatter and creak, and a door pushed briskly runs smooth. That matches ordinary experience with sticky patio doors.
  - The chatter frequency is roughly the contact natural frequency √(k/m)/2π, about 500 Hz in that example. Stiffer polymer pads or flanges reach 1-4 kHz squeaks.
- **Pile brush hiss.**
  - Model it as a dense impact process, the Conan "rubbing" end. Fibre tips release independently.
  - Density = (fibres per metre of strip) × V / (mean slip length). Amplitude scales as √(v F_N) per FoleyAutomatic.
  - Spectral shape: white force, coloured by the sash and frame extrusions.
  - The level falls when the pile is siliconised. Fin-seal piles (a plastic fin in the middle) add a continuous stiffer rub.
- **Polymer on aluminium**: acetal on anodised aluminium has low and nearly velocity-independent dynamic friction (μ ≈ 0.15-0.3). Nylon is more humidity-dependent. Squeal is more likely with dirty, dry or oxidised (unanodised) aluminium, and with PU (high friction, falling μ-v). Treat these as tendencies.
- **Rolling resistance as a level control**: the force to keep moving (44-90 N in AAMA-class doors) is mostly seal drag. A simulation can carry a "seal drag" parameter that sets both the hiss level (∝ √(drag·v)) and the operating effort.

---

### 5. Gear trains and brushed DC motors

#### Takeaway

Gear and motor noise is tonal and locked to shaft speed:

- Gear mesh: f_mesh = z·n/60 for a gear of z teeth at n rpm, plus harmonics.
- Sidebands at ±k·f_shaft from run-out and eccentricity. Planetary sets add sidebands at the planet-pass rate.
- Brushed motor commutation: f_comm = N_seg·n/60, doubled for an odd segment count with two brushes. Commutation also brings broadband brush and arc noise.
- Magnetic slot ripple at multiples of the slot count.

In small gearmotors the input stage, where tooth sliding speed is highest, is the main noise source. Worm stages and plastic input gears are the quiet options.

#### Cited findings

- maxon: "Noise is primarily generated in the input stage of the gearhead." Measures: smaller input speeds (smaller relative tooth-flank velocity), an input stage with plastic gears, or a Koaxdrive (worm first stage driving three planet wheels in an internal gear), described as "low noise, high reduction ratio in the first stage". Ceramic components improve life. maxon "Gear technology short and to the point": https://www.maxongroup.com/medias/sys_master/root/8815461728286/gear-Technology-short-and-to-the-point-14-EN-36-37.pdf
- Small brushed DC motor acoustic noise identified and reduced through semi-anechoic measurements: "Experimental Identification and Reduction of Acoustic Noise in Small Brushed DC Motors": https://www.researchgate.net/publication/260721193_Experimental_Identification_and_Reduction_of_Acoustic_Noise_in_Small_Brushed_DC_Motors (abstract-level only; page returned 403, no numbers obtained).
- A search summary attributed "64-65 dB" noise specs to some maxon planetary series, with no stated distance or speed (GlobalSpec aggregate, https://www.globalspec.com/ds/121/areaspec/gear_planetary). I do not trust it as a spec and list it only so it is not mistaken for a sourced value.

#### Gaps

- No Faulhaber, Dunkermotoren, Bühler or door-operator (dormakaba, GEZE, Record, Assa Abloy) dB(A) specifications were retrieved.

#### Inferences [E]

Formulas for the simulation:

- **Spur or helical stage**: f_mesh = z_pinion·f_pinion = z_gear·f_gear. Harmonics 2f, 3f... Sidebands at f_mesh ± k·f_pinion and ± k·f_gear, from eccentricity and run-out (amplitude modulation) and pitch errors.
  - Steel spur gears: harmonics fall slowly, about 6-10 dB per harmonic.
  - Plastic or helical gears: the first harmonic dominates and the band is narrower.
  - Tooth transmission error of a few µm is the excitation. It is the gear counterpart of rail roughness.
- **Worm stage**: f_mesh = starts × f_worm = z_wheel × f_wheel. Contact is sliding and several teeth share load, so tonality is weak and broadband sliding noise dominates. That is why door operators and Koaxdrive use worms.
- **Planetary**: f_mesh = z_ring·f_carrier = z_sun·(f_sun - f_carrier). Planet-pass rate N_p·f_carrier gives sidebands.
  - Example: sun 12, ring 39, 3 planets, input 50 Hz. Ratio 4.25, carrier 11.76 Hz, mesh 459 Hz, planet-pass sidebands every 35.3 Hz.
- **Brushed DC motor**:
  - Rotation f_rot = n/60.
  - Commutation (current ripple, torque ripple, brush bar-pass): f_comm = N_seg·f_rot when the segment count is even, 2·N_seg·f_rot when odd (two brushes commutate alternately). This is the ripple-counting rule.
  - Cogging and slot ripple at LCM(slots, poles)·f_rot.
  - Brush friction: broadband, roughly 2-10 kHz, AM-modulated at f_comm.
  - PWM: if the controller switches below about 16 kHz, there is an audible whine at f_PWM.
  - Under load the motor slows (the speed-torque line), so all tones drop in pitch as the door accelerates its mass or meets the seals, then recover. This droop is a strong cue of effort.
- **Worked automatic-door train** (illustrative parts, not a specific product):
  - 24 V motor at 3000 rpm (50 Hz) with a 12-segment commutator: commutation at 600 Hz, brush hiss AM at 600 Hz.
  - Single-start worm into a 15-tooth wheel: worm mesh 50 Hz (weak), output 3.33 Hz = 200 rpm.
  - T5 drive pulley, 42 teeth (67 mm pitch diameter): belt speed 42·5 mm·3.33 Hz = 0.70 m/s, belt mesh 140 Hz (section 6).
  - With a spur first stage instead (12-tooth pinion on the motor): mesh 600 Hz, which coincides with the commutation line. Real designs avoid such coincidences, and a simulation should allow them to beat.
- **Levels**: I have no sourced number. A small gearmotor of this class, worm or plastic-planetary, at 1 m is plausibly 40-55 dB(A) running and rises about 6 dB per doubling of speed. Measure or tune by ear.
- **Speed profile**: an automatic door accelerates to about 0.5-0.7 m/s, cruises, then creeps the last few hundred millimetres. Every tone above scales with motor speed, so the profile is audible as a pitch contour. Model the motor's speed-torque line and the load (door inertia, seal drag, track grade), not a fixed pitch curve.

---

### 6. Toothed (timing) belts

#### Takeaway

Belt noise is tonal at the tooth meshing frequency f_mesh = V/p, the same for every pulley in the drive. It has harmonics and comes from tooth-land impact, flank collision, air pumping as each tooth seats, fabric or PU friction, and the transverse vibration of the free spans.

- Level rises with speed, width and tension, and falls with larger pulley diameter.
- Polyurethane belts are noisier than neoprene (rubber) belts.
- Curvilinear GT-type profiles are several dB quieter than HTD and trapezoidal profiles.
- Free spans ring at f_n = (n/2L)√(T/μ), typically 20-150 Hz for door-operator spans. In a door operator the carriage clamp divides one run into two spans whose lengths change with door position.

#### Cited findings

- Mechanism list:
  - impact of the belt tooth on the pulley bottom land at the start of meshing;
  - impact of the pulley tooth tip on the belt land;
  - flank collision;
  - transverse and torsional belt vibration;
  - pulley vibration;
  - airflow between belt and pulley ("air is compressed and forcibly evacuated, making a sound similar to air escaping from a balloon");
  - friction between belt fabric and pulley.

  Further statements from the same note:
  - Meshing frequency = grooves × rpm / 60.
  - The driver pulley, where the belt enters at its highest tension, is usually the main generator. High tension plus tight tooth fit makes the belt "resonate like a plucked guitar string".
  - Noise is directly related to speed, width and pitch.
  - "Use of polyurethane (plastic) timing belts cause a higher timing belt noise level than rubber timing belts."
  - Guards can amplify the noise.
  - Spraying soapy water on a running belt identifies it as the source.

  Pfeifer Industries, Timing Belt Noise: https://www.pfeiferindustries.com/documents/Pfeifer%20Industries%20%20Timing%20Belt%20Noise.pdf
- More qualitative rules:
  - "The frequency of the noise level increases proportionally with the belt speed. The higher the initial belt tension, the greater the noise level. The belt teeth entering the pulleys at high speed act as a compressor."
  - Sound pressure rises with speed and width and as pulley diameter decreases.
  - "Polyurethane belts generally produce more noise than neoprene belts."
  - Steel pulleys are quietest, aluminium close, polycarbonate noisier, and machined pulleys quieter than moulded ones.
  - Angular misalignment matters more than parallel offset.

  SDP/SI (Gates-derived) Handbook of Timing Belts, Technical Section, §4.3, §9.4 and Fig. 9: https://sdp-si.com/D820/PDFS/Technical-Section.pdf
- Measured dB(A) against speed, same source, Fig. 9. Microphone midway between the pulleys, 100 mm from the belt edge. Values read from the chart:

  | Belt | Teeth | Width | Pulleys | 1000 rpm | 4500 rpm | Slope |
  |---|---|---|---|---|---|---|
  | 3 mm HTD | 188 | 15 mm | 26/26 grooves | ≈ 55 dB(A) | ≈ 76 | ≈ 32 log V |
  | 3 mm GT3 | 188 | 15 mm | 26/26 grooves | ≈ 50 | ≈ 66 | ≈ 25 log V |
  | 5 mm HTD | 118 | 30 mm | 20/20 grooves | ≈ 75 | ≈ 86 | ≈ 17 log V |
  | 5 mm GT3 | 118 | 30 mm | 20/20 grooves | ≈ 69 | ≈ 78 | ≈ 14 log V |

  Belt speeds: the 3 mm drive runs 1.3-5.85 m/s (mesh 433-1950 Hz); the 5 mm drive starts at 1.67 m/s at 1000 rpm (mesh 333 Hz).
- The same handbook gives minimum static span tension for T5 10 mm as 5.62 lbf (25 N) per span (Table 9). The mass factor m for T5 was "not available at press time". 5 mm HTD 9 mm has m = 0.163 and 3 mm GT3 6 mm has m = 0.078, in the centrifugal term m·S² (lbf, S = belt speed/1000 ft/min). Same source.
- Sonic tension meters infer static tension from the free-span natural frequency (plucked belt, ranges 10-600 Hz standard). That confirms span frequency as a usable, tension-set quantity. Same source, §10.
- A belt drive noise-source study using a microphone array on a three-pulley, one-belt system: Shi et al. 2020, Measurement and Control: https://journals.sagepub.com/doi/10.1177/0020294020944974 (title only).
- From a search snippet only, unverified: the belt strum frequency is "typically in the 40-80 Hz range", well below mesh frequency.

#### Gaps

- No ContiTech, Optibelt or Mulco T5/AT5 noise figures, and no T5 unit mass, were retrieved.

#### Inferences [E]

- **T5 at 0.7 m/s: f_mesh = V/p = 0.7/0.005 = 140 Hz.** Harmonics 280, 420, 560 Hz. Independent of pulley size; pulley size changes only the rotation rate and the run-out sidebands.
  - A 42-tooth drive pulley turns at 3.33 Hz; an 18-tooth idler at 7.8 Hz. Each adds AM sidebands at its own rotation rate, so they beat differently.
  - AT5 (wider, stiffer tooth) and HTD/GT profiles at the same pitch give the same 140 Hz with a different harmonic balance: trapezoidal T5 has a sharper impact and stronger harmonics.
- **Unit mass of T5 10 mm.** Converting the SDP mass factors: μ = m/277.8 slug/ft. 5 mm HTD 9 mm gives 0.028 kg/m, about 3.1 g/m per mm of width at 3.8 mm total thickness. Scaling to T5's 2.2 mm thickness gives about 1.8 g/m per mm, so **μ ≈ 0.018 kg/m for T5 10 mm** (steel-cord PU). Treat as ±30%.
- **Span frequencies** f₁ = (1/2L)√(T/μ), μ = 0.018 kg/m:

  | T (N) | L = 0.3 m | L = 0.5 m | L = 1.0 m | L = 1.5 m | L = 2.0 m |
  |---|---|---|---|---|---|
  | 100 | 124 Hz | 75 | 37 | 25 | 19 |
  | 150 | 152 | 91 | 46 | 30 | 23 |
  | 300 | 215 | 129 | 65 | 43 | 32 |

  - The wave speed c = √(T/μ) = 91 m/s at 150 N, against a belt speed of 0.7 m/s. The axially-moving-string correction f_n ∝ (1 - V²/c²) is negligible (6e-5). Belt speed does not detune the spans in a door.
  - In a door operator the carriage is clamped to one run. That run's two spans are L₁ = x and L₂ = L_run - x, so their fundamentals glide in opposite directions as the door moves. Close to the end pulley the short span rises steeply: L = 0.2 m at 150 N gives 228 Hz. The return run has a fixed length.
  - The 140 Hz mesh excitation therefore sweeps through span harmonics (n·f₁) as the door travels and as speed ramps. Each crossing gives a brief resonant swell. Such swells are characteristic of belt-driven sliding doors and are worth modelling with 2-4 string modes per span, Q about 20-50.
- **Level at door speed.** Scaling the SDP 5 mm HTD curve (≈ 75 dB(A) at 100 mm, 1.67 m/s, 30 mm wide) down to 0.7 m/s at 17-25 log V gives 65-68 dB(A). Width 30 → 10 mm at about 10 log(width) gives -4.8 dB, so about 60-63 dB(A) at 100 mm. At 1 m inside a header box, with the belt acting as a line source and enclosure effects, expect about 40-50 dB(A). A PU T5 belt is several dB above a neoprene HTD. The trapezoidal tooth adds impact harmonics.
- **Air pumping.** The volume trapped per tooth groove is tiny, and the escape velocity scales with V. At 0.7 m/s air pumping is negligible; impact and friction dominate. Air pumping matters only above several m/s.
- **Tension.** Higher tension raises mesh-impact noise (SDP, Pfeifer) and raises all span frequencies as √T. A slack belt lowers span frequencies and lets teeth ratchet or jump under the start-up torque peak. That ratchet is a distinctive loud slap at the tooth pitch. Belt tension is a good "maintenance state" parameter.

---

### 7. Published procedural and physical synthesis of rolling

#### Takeaway

Three lines of work give directly usable parameters:

- van den Doel/Kry/Pai: a rolling force is a scraping force (speed-scaled fractal noise) with an extra speed-dependent low-pass. The force also couples more strongly to the object's modes, like a γ = 2 gammatone.
- Rath & Rocchesso: a Hunt-Crossley non-linear impact model driven by a "surface offset" signal derived from a band-passed noise profile, plus a sinusoidal force modulation at the rotation rate from asymmetry.
- Conan et al.: the rolling force is an impact series with autocorrelated amplitudes and intervals (one-pole/one-zero AR filters, Gaussian marginals), raised-cosine impacts whose duration falls with amplitude (t₀ = ζ·A^(-0.29)), and AM at ν_m ∝ V/R.

These amount to a phenomenological version of the railway chain (roughness → contact filter → non-linear Hertz spring → modal structure). The railway chain can supply their parameters from the parts.

#### Cited findings

- **van den Doel, Kry & Pai, FoleyAutomatic** (SIGGRAPH 2001): https://www8.cs.umu.se/kurser/TDBD12/HT01/papers/foleyautomatic.pdf
  - Scraping: fractal noise ∝ ω^β through a reson, centre frequency scaled with contact velocity; reson width controls perceived randomness, narrow means pitched; D = β/2 + 2. Fractal model valid up to about 1000 Hz for smooth plastic, measured D ≈ 0.88 from a linear fit. Audio-force amplitude ∝ √(v F_N).
  - Rolling: the ball "only sees the large scale surface structure", and collisions occur just ahead of the contact so they are "very soft, i.e. drawn out in time". Hence the same model as scraping plus "an additional low-pass filter with adjustable cutoff frequency". Collision velocity v_c ≈ d·v/R (Fig. 6).
  - The rolling force "couples stronger to the modes" than sliding, so its spectrum is enhanced near resonances. A γ = 2 gammatone driven by noise is better than a pure modal model; spectral envelope S(ω) = 1/√((ω - ρ)² + d²) per mode.
  - The paper is the phenomenological statement of the contact filter: the low-pass corner should be V/(2πa) with a from Hertz.
- **Rath & Rocchesso**, interactive sonification workshop 2004: https://interactive-sonification.org/files/RathRocchesso2004-ISF.pdf
  - Force f(x, ẋ) = -k x^α - λ x^α ẋ for x > 0, and 0 for x ≤ 0 (Hunt-Crossley). k is the elasticity, the main hardness control; α depends on the local geometry; λ weighs dissipation.
  - The rolling geometry is reduced to one dimension. A time-varying offset signal is added to the compression x. It is not the raw surface difference but the trajectory of the rolling body touching only peaks ("contact at peaks") without bouncing, computed by an efficient recursive algorithm. Band-passed white noise replaced fractal noise as the profile, because low- and high-pass smoothing made β unimportant.
  - Asymmetry: the centre-of-mass height c(t) = (c₂+c₁)/2 + (c₂-c₁)/2·sin(ωt) gives an additional force ∝ c̈ = -(c₂-c₁)/2·ω² sin ωt. The amplitude grows with the square of angular velocity and the frequency is tied to speed and radius.
  - Periodic patterns from object irregularities or surface periodicity (for example tiles) are "a strong perceptual cue of rolling" and important for perceived size and speed.
  - Control runs at about 100 Hz, DSP in Pd externals.
- **Rocchesso, physically based models of everyday sounds** (Forum Acusticum 2005), overview of the Sounding Object family (impact, rolling filter, friction): https://dael.euracoustics.org/confs/acoustics2008/data/fa2005-budapest/paper/792-0.pdf
- **Conan, Thoret, Aramaki, Derrien, Gondre, Ystad, Kronland-Martinet**, "An intuitive synthesizer of continuous-interaction sounds: rubbing, scratching and rolling", Computer Music Journal 38(4), 2014: http://kronland.fr/wp-content/uploads/2015/05/2014_CMJ_Conan.pdf
  - Physics reference model: Hunt-Crossley f = k x^α + λ x^α ẋ, with α = 3/2 (Hertz), k = 1e7 N/m^1.5, λ = 1e7 N·s/m^2.5. A 5 g ball at 0.5 m/s on a fractal surface with β = 1.2 and a maximum asperity of 1e-9 m, integrated with RK4. Listeners heard "a small, hard marble".
  - Force as an impact series: f(t) = Σ Aₙ φₙ(t - Tₙ). Intervals ΔTₙ and amplitudes Aₙ are strongly autocorrelated and cross-correlated. Whitening needs only one pole and one zero per series. The whitened marginals are Gaussian for rolling.
  - Synthesis: a single uniform white process W drives both series through inverse-CDF and colouring filters.
  - Impact shape: raised cosine φ(t) = ½(1 + cos(2πt/t₀ₙ)) for |t| < t₀ₙ/2. Duration t₀ₙ = ζ·Aₙ^(-θ), with θ = 0.29 from simulations of the impact model; ζ depends on ball mass and stiffness and maps to perceived size. Amplitude-dependent durations "clearly produced the most realistic evocations".
  - AM: s(t) = 1 + m sin(2π ν_m t), m ∈ [0, 1] for asymmetry, ν_m ∝ v/R; in the prototype ν_m = 3V/S with S the perceived size and V the perceived velocity, both in [0, 1].
  - Rubbing prototype: AR coefficients 0, one impact per sample, Gaussian amplitudes, θ = 0.
  - Scratching prototype: the same with exponential ΔT (low density).
  - Morphing: prototypes on a unit circle at 0, 2π/3, 4π/3, interpolated by angle.
  - The force then goes through a resonant filter bank of exponentially decaying sinusoids (the object), with material morphing between wood, metal and glass.
  - The detailed rolling parameter values are in Conan et al. 2014, IEEE/ACM TASLP, "A synthesis model with intuitive control capabilities for rolling sounds" (cited there; not fetched).

#### Inferences [E]: mapping the parts onto these models

- **Hunt-Crossley k from Hertz.** For a door wheel, k = C_H = (4/3)E*√R_e, which is 5.7e8 N/m^1.5 for the 32 mm acetal crowned wheel, with α = 3/2.
  - That is 57 times Conan's marble value of 1e7, which is a soft cartoon value. With x ≈ 41 µm static, it gives the k_H of section 2.3.
  - λ from restitution: the Hunt-Crossley approximation λ/k ≈ 3(1 - e)/(2 v_in) links λ to the coefficient of restitution e at impact speed v_in. Choose λ so that impacts at 0.05-0.2 m/s give e ≈ 0.6-0.8 for acetal.
- **Surface offset spectrum.** Use the railway form, a 1/3-octave roughness level against λ.
  - Track: an extruded aluminium sill, mill or anodised finish. At 1-30 mm wavelengths take about -15 to 0 dB re 1 µm, the ISO 3095 limit shifted down 0-5 dB with the same 0.6 dB per third-octave slope, plus discrete joints and steps.
  - Wheel: moulded acetal; out-of-roundness at λ = πD (100 mm) of 20-50 µm (about +26 to +34 dB re 1 µm, mostly once per revolution and low orders).
  - Debris: a Poisson process of grains, height distribution 20-300 µm, rate set by a "dirt" parameter, each a non-linear impact (section 2.9).
- **Contact filter = FoleyAutomatic's extra low-pass.** Corner f_c = V/(2πa) with a from instantaneous load, about 3rd order. At 0.5 m/s that is about 100 Hz for acetal, 30 Hz for PU and 250 Hz for steel.
- **Impact series parameters (Conan) from the parts.**
  - ζ should give t₀ about equal to the Hertz t_c for the unsprung mass: 0.3-0.6 ms for acetal at debris impact speeds, θ = 0.29 (Hertz alone gives t_c ∝ v^(-1/5), so θ = 0.2 in velocity).
  - ν_m = V/(πD) exactly (4.97 Hz for 32 mm at 0.5 m/s), not a perceptual scale. m from run-out/δ0.
- **Resonator bank.**
  - Wheel/axle on contact spring: about 2-3 kHz, Q set by acetal damping, about 10-20.
  - Track extrusion modes: dense; use a modal density appropriate to a 2 m aluminium extrusion with fixings.
  - Glass panel: modes from plate theory, radiating well above coincidence, about 2 kHz.
  - Housing and sash rail: about 0.3-1 kHz, low Q.
  - Following van den Doel's observation, rolling should drive these modes more strongly than sliding does. Physically that is because the force is applied through a stiff contact spring rather than a friction interface.
- **Automatic door additions.** Motor and gear tones (section 5) and belt mesh plus span strings (section 6) are all tied to the same motor speed state, so they rise and fall together. Carriage wheels (typically 40-50 mm nylon or PU on an aluminium hanging track) use the same rolling model at 0.5-0.7 m/s: f_r = 3.2-5 Hz for 45 mm.

---

### 8. Suggested part-to-sound signal chain [E]

Per roller, at control rate (about 100-1000 Hz) compute:

- V and the load F (panel weight share + seal reaction + inertia when accelerating);
- a = (3FR_e/4E*)^(1/3), k_H = 2E*a, f_c = V/(2πa);
- the wheel rotation phase (for run-out and gate tick) and the bearing phases (BPFO/BPFI/cage if a ball bearing is fitted).

At audio rate:

1. Roughness displacement r(t). Sum of:
   - (a) the stationary track roughness: noise shaped to L_r(λ) mapped to frequency by f = V/λ, through the contact filter low-pass;
   - (b) a once-per-revolution run-out sinusoid and its low harmonics, amplitude from the run-out spec;
   - (c) discrete events from position-indexed lists, so they recur at the same place: track joints (step height h → raised-cosine over √(2Rh)), and debris grains (Poisson in position, fixed to the track so the same grit clicks at the same spot on the way back).
2. Non-linear contact. Hunt-Crossley spring between the unsprung mass (wheel + axle) and the track point mobility, with loss of contact allowed. Or, cheaper: linear k_H with an impact generator (raised-cosine force, t₀ from Hertz) for events whose height exceeds δ0.
3. Contact force into a modal bank: wheel/housing (2-3 kHz), track extrusion (broad, many modes), sash and glass (through the housing transfer, low-passed above about 1 kHz).
4. Friction channel: pile-seal rubbing noise ∝ √(drag·V); stick-slip creak when V < v_crit; optional squeak resonator (1-4 kHz) when the "dry" parameter is high.
5. Automatic door only: motor speed state from the speed-torque line and the load, then commutation line + brush noise + gear mesh (+ sidebands) + belt mesh at V/p + span strings with lengths from carriage position. All levels scale with speed using the slopes above (gears about 20 log n, belt 17-32 log V).

Level calibration targets, all [E] and to be checked by ear and measurement:

- clean manual door: 35-45 dB(A) at 1 m at 0.5 m/s;
- gritty track: transients +10-20 dB;
- automatic operator, running: 40-55 dB(A) at 1 m;
- rolling component slope 20-30 log V, impacts 20 log V.

---

### 9. Source list

- Thompson, ICSV21 keynote 2014 (rolling noise model, interaction equation, 30 log V, decay rates): https://generic.wordpress.soton.ac.uk/track21/wp-content/blogs.dir/sites/4/2014/08/Thompson_DJ_keynote_ICSV21.pdf
- Thompson, ICSV10 (Hertz non-linearity, flats 20 log V, load sensitivity, contact filter models): https://www.southampton.ac.uk/assets/imported/transforms/content-block/UsefulDownloads_Download/223F9A5BFBAD4A0885B7790738D08585/icsv10_240.pdf
- Pieringer, DAGA 2013 (roughness 0.1-30 µm, 5-500 mm; linear vs time-domain): https://publications.lib.chalmers.se/records/fulltext/176336/local_176336.pdf
- Thrane, DAGA (prEN ISO 3095 roughness, classes): http://pub.dega-akustik.de/DAGA_1999-2008/data/articles/000994.pdf
- ISO 3095:2013 limit table, transcribed in phonometry: https://github.com/jmrplens/phonometry/blob/main/src/phonometry/environment/sources/rolling_stock_noise.py
- ISO 3095:2013 sample: https://cdn.standards.iteh.ai/samples/55726/f31611a39187419ebad380e6fc44e56d/ISO-3095-2013.pdf ; EN 15610:2019: https://standards.iteh.ai/catalog/standards/cen/cb8b7c18-6b28-4952-85ed-4ad47fbf8c0e/en-15610-2019
- Contact filter formula (Remington), ScienceDirect topic page, read via search snippet: https://www.sciencedirect.com/topics/engineering/contact-filter
- Ford & Thompson 2006, simplified contact filters (title only): https://www.sciencedirect.com/science/article/abs/pii/S0022460X05007704
- CasterHQ castor noise (vendor): https://casterhq.com/blogs/caster-university/noise-vibration-control-casters
- HIWIN linear guideway catalogue: https://www.hiwin.com/wp-content/uploads/Linear_Guideway-E-1.pdf
- NSK NH/NS linear guides: https://www.nsk.com/content/dam/nsk/am/en_us/documents/precision-americas/Linear-Guides-NH-NS-Series.pdf
- SDP/SI timing belt handbook technical section: https://sdp-si.com/D820/PDFS/Technical-Section.pdf
- Pfeifer Industries timing belt noise: https://www.pfeiferindustries.com/documents/Pfeifer%20Industries%20%20Timing%20Belt%20Noise.pdf
- maxon gear technology note: https://www.maxongroup.com/medias/sys_master/root/8815461728286/gear-Technology-short-and-to-the-point-14-EN-36-37.pdf
- Brushed DC motor noise paper (abstract only): https://www.researchgate.net/publication/260721193_Experimental_Identification_and_Reduction_of_Acoustic_Noise_in_Small_Brushed_DC_Motors
- FoleyAutomatic: https://www8.cs.umu.se/kurser/TDBD12/HT01/papers/foleyautomatic.pdf
- Rath & Rocchesso 2004: https://interactive-sonification.org/files/RathRocchesso2004-ISF.pdf
- Rocchesso, Forum Acusticum 2005: https://dael.euracoustics.org/confs/acoustics2008/data/fa2005-budapest/paper/792-0.pdf
- Conan et al., CMJ 2014: http://kronland.fr/wp-content/uploads/2015/05/2014_CMJ_Conan.pdf
- Belt noise source identification, Shi et al. 2020 (title only): https://journals.sagepub.com/doi/10.1177/0020294020944974
- From memory, not fetched: Randall & Antoni 2011 bearing tutorial https://doi.org/10.1016/j.ymssp.2010.07.017 ; Akay 2002 "Acoustics of friction" https://doi.org/10.1121/1.1456514 ; Thompson, "Railway Noise and Vibration", Elsevier 2009 ; Remington, JASA 81(6) 1987 ; Johnson, "Contact Mechanics", CUP 1985 ; Conan et al., IEEE/ACM TASLP 2014 rolling model.
