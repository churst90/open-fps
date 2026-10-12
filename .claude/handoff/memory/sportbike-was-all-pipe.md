---
name: sportbike-was-all-pipe
description: 09-28 sports bike "huge V8": 119 dB glasspack, block 29 dB under; now stock silencer + Anderton block law above 6000 rpm; pickups side exit
metadata:
  type: project
---

Cody 2026-09-28: sports bike "sounds like a diesel truck / huge V8", engine size not in the sound,
wants "an octave up". `--voice-levels sportbike parts`: exhaust 118.8, intake 93.6, block 89.7 — all
pipe. A stock litre bike passes ~80 dB(A) at 7.5 m (~98-100 at 1 m); Lu & Jen (Inter-noise 2014):
engine radiated 49 % of a motorcycle's noise at 5,000 rpm, exhaust 43 %.
Done: MufflerSpec.Stock with 2 short chambers (exhaust 100.6), block × (rpm/6000)² above 6000 for
petrol (Anderton 50 log N vs the mechanisms' 10 log N; `BlockLawReferenceRpm`), declared 107.
Clip "B, rider revs higher" was IDENTICAL to A (median 3,929 vs 4,095 rpm): a litre bike's 1st only
reaches 5,400 by 50 km/h, so shift points cannot raise its town pitch. Cody caught it. RULE: measure
(`--ride` prints rpm percentiles and firing Hz) before claiming two clips differ.
Fix: city "Sports bike" = new `supersport600` (CBR600RR: 67x42.5, 66 Nm @ 11,250, 15k, primary
2.111, 2.615/2.0/1.666/1.444/1.304/1.208, final 2.625): 6,100 rpm cruising at 50 km/h. Variants 2-5 earlier "all sound the same": knock,
valves, steepening are inaudible next to a 119 dB pipe — change the balance, not the details.
Twin-turbo pickups: 5"/6" + Steepening 1.5 + WallLoss 0.8 approved; quiet pass-by was the rear-exit
pipe shadowed by the 6 m body until it passed (abreast: highs -13.5, mids -5, bass intact = Cody's "engine heavy, exhaust light") → side exit ahead of rear wheel, kerb side (+X).
Cody runs `/levels real` (compression 1, ceiling ~89 dB). `--ride` prints SPL at cruise.
See [[traffic-driver-lurch]], [[air-blocking-and-pipe-radiation]], [[the-block-had-no-anchor]].

Later 09-28: Cody's CBR600RR recordings (inbox, Mivv slip-on + onboard) measured with a hand FFT
tracker (sox to 16 kHz, HPS + band balance): idle 43-45 Hz (1,300 rpm, ours matched); revving
strongest 400-770 Hz (12-13k); road riding centroid 200-500 Hz, 1-4 kHz 15-35 dB down. Ours had 1-4k
at -5 (stock silencer let block/intake set the tone). Now: Chambered40 exp 12 baffle 0.5, pops 20,
CruiseUpshiftRpm 11000, city top 90 (accel capped 2.65 by ACCEL_SHARE), declared 114, SportBikeRoad
tyre (68 car tread blocks sang 859/1718 Hz at 90). Open: 8k light-throttle cruise still bright.

END 09-28: Cody: 600 "sounds like a car stuck in first gear" — removed from city AND registry.
Pickups "all rumble, no exhaust": the Turbine in ExhaustNetwork was a one-pole 260 Hz low-pass
passing 0.34; now 2500 Hz corner, pass 0.5 (Tiikoja & Abom: flat 5-10 dB TL). Turbo presets
re-declared (i4_turbo 111, diesel_cummins 113, duramax 118, cummins_compound 120). Front-vs-rear:
`--tap-balance`. The exhaust was already 17-20 dB over the bay in SPL but all below 250 Hz, which the
ear discounts ~19 dB at 100 Hz. `--voice-levels` NaN = never reached the WOT window, not a NaN voice.
Cars/buses checked 09-28 (after pickups approved "much much much better"): centroids 270-560 Hz, no
turbine-type fault; at 12 m/s a stock car's front tap is ~81 dB of pure tyre and the rear 81-87.
Cody chose "loud cars in traffic": 4 of 19 downtown cars swapped (i4_sport_street, i4_turbo,
pickup_v8_flowmaster, v8_mild). Not a global exhaust lift, not a tyre cut.
