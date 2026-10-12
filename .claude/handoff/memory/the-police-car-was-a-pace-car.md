---
name: the-police-car-was-a-pace-car
description: police_v8 is a race pace car at 132 dB; the city was using it as a cruiser, which is why it was the loudest thing on the map. A road interceptor now exists at 95 dB.
metadata:
  type: project
---

Cody 2026-09-21: "The only thing that's loud is that police car, the exhaust is probably the
loudest thing on the map." Correct, and by construction.

`VehicleProfile.PoliceCar` ("police_v8") is a PACE car — open pipes, 320-degree cam, a cage, no
trim — built to run with a race field on the speedway. It measures **132 dB at 1 m**: fourteen over
a muscle car, thirty-seven over a bus. The city map was using it as a patrol car.

Added `police_interceptor`: the same engine family as a road car has it — 5.0 not 7.0, 274-degree
cams, cast manifolds, an H-pipe and a stock muffler. Measures **95.3 dB**, within a decibel of a
city bus and an economy hatchback, which is what a police car with the siren off actually is. The
pace car is untouched and still on the speedway.

**The airbox trap on the way:** with `IntakeSpec.Level = 0.55` the interceptor's intake measured
109 dB against its 86 dB exhaust, and the "quiet" police car came out louder than a muscle car.
Set to 0.12 — a sealed factory airbox with a resonator in a long snorkel, which is what that field
means. **This is a symptom of a bigger open item: the intake, like the block was, has no anchor.**
i4_economy measures intake 94.7 / exhaust 86.4 and v6 110.7 / 99.3 — both backwards for a silenced
road car. `FrontMix = 0.35` is a taste constant hiding 9 dB of it. Do not raise FrontMix until the
intake is anchored, or the v6 becomes an airbox with a car attached.

Aircraft got the same declaration audit the same day: airliner 145→138, turboprop 129→120,
helicopter 116→104, piston single 118→117, all measured with `--spool`. Over-declaring makes a
thing QUIETER (rendered ∝ live − 0.55·declared), which is most of "you have to be right on the
runway to hear the planes" — the rest is that an aeroplane on APPROACH is at idle, forty decibels
under its full-power figure, which is correct. Approved flyover figures unmoved at 71/69/76/64.

See [[live-voice-is-not-the-bench]], [[the-block-had-no-anchor]], [[sirens-are-a-horn-not-a-loop]].
