---
name: an-aeroplane-has-more-than-one-engine
description: Aircraft got Engines, a wingspan, an approach speed and an undercarriage on 2026-09-20; the twins are +3 dB and the extent is the engine separation
metadata:
  type: project
---

Cody, 2026-09-20: "do planes have models that encompass wingspan, tires screech on touchdown, how
many engines do the jets have, at least 2?" They had one engine, no dimensions and no wheels.

- **`AircraftProfile.Engines`.** Every engine is BUILT, not counted: N blade rows with their own
  scatter and a ±0.5 % speed trim, because no two are synchronised exactly and the beat between two
  fans is the throb of a twin. The broadband halves (jets, combustor) are independent streams and
  add as power — √N in pressure. Airliner 2, turboprop 2 (a regional turboprop IS a twin), piston
  single 1, helicopter 1. Anchors +3 dB: airliner 142→145, turboprop 126→129. Flyover at the ear
  went 71/66/73/64 → **71/69/76/64** — the singles unchanged to the decibel, which is the check.
- **`EngineSpanMetres`** (airliner 11.6, turboprop 8.1) is the voice's EXTENT now, not the fan disc.
  Same rule as a bus's nose-to-tail. `WingspanMetres`/`LengthMetres` give the server its collider,
  which used to be guessed from CRUISE SPEED.
- **`ApproachSpeedMps`** declared, not a fraction of cruise: 0.62 × cruise put the airliner over the
  fence at 277 knots. Jets cruise 4× a light single and land at barely 2×.
- **`LandingGearSpec` + `AircraftSynth.Touchdown`.** A wheel in the air is not turning; the runway
  spins it up and the patch slides at 100 % slip until the wheel's inertia is paid for:
  `t = Iω / μWr`. The trap: at touchdown the WING is still carrying the aeroplane, so only
  ~15 % of the weight is on the wheels (`WeightOnWheelsAtTouchdown`) — with the full weight the jet
  chirps in 90 ms instead of scrubbing for 300. Detected client-side by the same rule the power
  lever uses: belly within 0.15 × length of the ground, read off position and a gated ground probe.
- **Not done:** the aircraft on the city map still REVERSE at the runway end rather than rolling
  out, so the touchdown fires but there is no landing roll. Renders in `inbox/aircraft-2026-09-20/`.

Instruments added: `--landing`, `--spool`. See [[aircraft-model]], [[jet-noise-law]].
