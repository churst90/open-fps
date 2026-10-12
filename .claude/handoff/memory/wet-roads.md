---
name: wet-roads
description: 10-06 wet roads agent: road water film from rain via Runoff, per-wheel wet tyre hiss/spray, puddles, wet grip + aquaplaning, cabin; inbox/wet-roads-2026-10-06
metadata:
  type: project
---

Cody 2026-10-06 asked for vehicles and tyres on wet pavement based on the rain. Agent started: water film
depth per road from rain + drainage (Runoff.cs) and drying; wet tyre noise per wheel (+4-7 dB >2 kHz,
Sandberg & Ejsmont), arch spray, puddle splash; wet grip and aquaplaning on server+client; cabin. Wipers later.
See [[running-water]], [[fan-clutch-tyres-and-squeal]].
**MERGED+pushed 10-06.** RoadWater.cs (texture/sheet/gutter+kerb puddles, Penman drying, Gallaway sheet, server
RoadWaterSystem -> clients, wire: wheel water byte + road water float[]), wet grip (Wong, PIARC, Gallaway
aquaplaning 88 km/h on 2 mm), tyre pressure/tread per preset, traffic -3/-8 % speed +12 % gap in rain (FHWA),
WetTyres.cs per wheel (ejection hiss, arch spray, swish, puddle splash, cabin). Wet-dry +4.7/+9.1/+11.9 dB at
2/4/8 kHz vs recordings +4.6/+10.2/+12.6. Open: cabin +12 dB (vs recalled +2-5), heavy vs light tyres same
(thin film; ruts next), squeal damping assumed, wipers next.
10-06: Cody: "the inside of the cab of the cars sounds mono" — cause: riding = ONE interior voice placed just ahead
(ClientAudioSystem ~2749). Agent started: interior split into firewall/arches per wheel/exhaust/roof/wind taps
at real places, independent signals, cabin early reflections; inbox/cabin-2026-10-06.
Cabin paths MERGED+pushed 10-06: CabinPaths (firewall, exhaust floor, per-wheel tyre/squeal/spray, tread tone under
floor, wind per A-pillar, bus door), taps aligned on the parent's DSP clock (else intake/exhaust cancellation broke
+6.5 dB), HRTF colouring returned per third-octave (BandEq/HrtfBands) after the room send, roof rain rides with the
head; OPENFPS_CABIN_PATHS=0 off. IACC 1-8k ~0 now (real cars ~0.1); 250 Hz-1 kHz and idle still too alike; cabin
traced reverb fed too little (needs per-band direct/reverb split, Cody's ear).
