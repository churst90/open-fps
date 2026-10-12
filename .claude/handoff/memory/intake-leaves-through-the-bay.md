---
name: intake-leaves-through-the-bay
description: "09-26 intake mouth radiated unshielded out of the front tap (10-18 dB of a floored V8's front); now bay-only; clatter +4.4 dB and brighter (unheard)"
metadata:
  node_type: memory
  type: project
  originSessionId: 53f7c168-2e82-4337-ae6c-6ef4f8dc6ae6
  modified: 2026-09-26T08:31:46.535Z
---

Cody 2026-09-26: diesels "choked ... lips tightly shut", clatter should be louder; "the muscle car front end, I can really hear the air intake ... the intake I don't think should be audible".

Found with `--tap-balance front` (front vs rear, idle/cruise/floored, intake worth, bay worth):
- the front tap carried `Engine.Intake * FrontMix` DIRECT at full orifice level plus 0.5 again through the bay. Floored intake worth at the front: glasspack 17.7 dB, vtwin 15.6, bigcam 14.1, v6 9.6.
- Research (agent, TRB SR152, ISMA 2014, ISO 362 review): the orifice LEVELS were roughly right (stock car −3..−6 dB under the exhaust at full load, truck −10); what was missing is the bonnet — the block dominates a car's front microphone.
- Fix: intake goes out only through the bay, `(Block + FrontMix * diffused Intake) * EngineBayLeakage`. Bikes now declare `EngineBayLeakage = 1` (no bay; they were at the 0.15 car default). After: cars 0–3.6 dB intake worth floored; vtwin 19 dB (bikes should have an audible intake).

Clatter (EngineSynth): StructureKnockGain 0.30 → 0.50, a 3.6 kHz structure mode added, structure LP 4.5 → 5.5 kHz. Cruise measurements: 2 kHz −3 to −7, 4 kHz −14 to −22, against targets of −5 and −18.

**How to apply:** if Cody still hears diesels as choked, the next suspect is the whole voice above 4 kHz: the "rest" drops about 19 dB from 4 kHz to 8 kHz. The FrontMix field now scales the intake inside the bay path, which the two-voice crossfade and the lab mutes both rely on. Related: [[the-block-had-no-anchor]], [[diesel-combustion-model]].

**Same day, the real "choked":** Cody: "the bus still sounds like the engine is smothered, the bus did not change". The cause was the exhaust SOURCE. Port pressure on every diesel fell 40–50 dB from 125 Hz to 1 kHz (`--tap-balance port` dumps port vs tail; `pipe` shows the muffler and wall loss were innocent).
- Added `EngineSynth.BlowdownJet`: an in-duct dipole for flow over the exhaust valve, K ρ U⁶ A / c³, with a pink band from 300 Hz to 5 kHz.
- `ValveFlowNoiseK = 1e-3` was calibrated on the straight-piped diesel_cummins_na at 2,100 rpm full load against Damkevala 1974 Fig 4.3. It gives 1k −9.4, 2k −10.4, 4k −15 re the peak.
- Muffler insertion loss measured: the bus 22–24 dB at 1–2 kHz, matching Donaldson's 17–20.5.
- The V8s change most: +14–27 dB at 1–2 kHz at full throttle, +2–2.6 dB total. So Cody judges by ear with `/valveflow on|off`.
- Donaldson US 6,082,487 is the reference for straight vs muffled truck spectra.

**Turbos (same day):** Cody: "I'm not hearing any twin turbos ... the whine from the turbos I don't hear". The old whistle was a sine at 2000+9000·spool Hz plus noise under 600 Hz, at a hand-set fraction of the block, and it went out only through the bay. Floored, it was 30 dB under the straight pipe.
- New `EngineSynth.Turbo()`:
  - compressor blade pass: 7 × shaft; 81 dB at 1 m at 60 krpm, ∝ N⁴; through the bay;
  - diesel whoosh: 1.5–3.5 kHz; 68 dB at 110 krpm;
  - turbine blade pass: 11 × shaft; 110 dB in the duct at 110 krpm, radiated at the tailpipe mouth (× radius/√2), minus 15 dB if there is a muffler.
  - shaft speed = max(22k, 115k × spool). TurboWhistleLevel stays as the multiplier.
- The waveguide kills everything above about 5 kHz: a 150 dB tone injected at the port barely reached the tailpipe. So the turbine is added after the network, in `Exhaust`.
- `--tap-balance turbo <keys>` dumps both ends at idle/cruise/floored, with and without the turbo.
- Bike exhausts were shortened about 25–30 % at Cody's request. semi 104, school_bus 100 and v10 120 were re-declared.

**Roster and audit (09-26, later):** Cody: no muscle cars; regular street cars, one city bus, twin-turbo pickups, and a few "more aggressive but nothing too crazy".
- New street presets (Vehicles.cs "Street cars"): i4_compact, i4_midsize, i4_sport_street, boxer4_street, i6_street, all 98–104 dB. The speedway presets are 117–119 dB.
- `transit_bus`: `EngineAtRear` puts the front voice at FrontAxleZ; the bay and fan go to the rear tap.
- `--tap-balance audit <keys>` prints each vehicle's wiring. It found:
  - bikes: `ExitPoint` pushed the pipe exit to the back of the bike's box and shaded it with that box. Open frames (CabinLengthM 0) now skip both.
  - the semi's exhaust was at 0.3 m and pushed to the rear bumper. It's now a 4 m stack pointing up.
- An upshift below 78 % of the redline makes `--voice-levels` print NaN. That means the metering window was never reached; it is not a model fault.

**Turbo pitch (09-26):** Cody: "the whistle/whine on the turbos is maybe an octave too high". Changes, from research:
- The dominant part-speed tone is tip-clearance noise at 0.5×BPF (Raitor and Neise 2008).
- The BPF is now secondary: −6 dB below 60 krpm, crossing to +6 dB at 120 krpm. That crossing is a setting, not a published figure.
- Shaft speed = 12k + 113k × spool² (truck logs: idle 12–15k, cruise 40–50k, full 120–130k).
- The turbine's tailpipe tone is 10 dB lower (100 dB in the duct). There is no evidence it is audible through the aftertreatment.
- Result: the cruise whistle is at 2–3 kHz, about +2.6 dB in its band on the compound Cummins; idle is 1.5 kHz.
