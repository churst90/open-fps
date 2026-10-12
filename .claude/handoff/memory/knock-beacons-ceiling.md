---
name: knock-beacons-ceiling
description: 09-29 terminal acoustic ceiling (AcousticTile), Shift+E door knock (DoorKnock, fitted noise), sine beacon set; docs/LISTENING_SPOTS.md
metadata:
  type: project
---

- AcousticTile material + prefab acoustic_ceiling; terminal ceiling at 5.85-5.9 m (gen_city). Sabine est.
  ~1.2 s mid / 2.4 s low (was 7-10 s traced). Unheard.
- DoorKnock (OpenFPS.Common): octave-band noise hits via CarDoor.BandNoise/AddHit (now internal), Shape
  fitted by $S/fitknock.py to "Heavy Door Knocks" 0-120 ms octave levels (rms 0.93 dB). Dry: the
  recording's slow tail is its room. Key "knock:N"; server `knock` command; Shift+E. `--door-knock`.
- BeaconAids.Tone: door C5→F5, vehicle C4×2, item C6 ring, exit C-E-G, stairs G4-A4-B4-C#5, waypoint A4
  swell. All < 1.2 kHz (crossing-chirp rule). Only door/item/vehicle are client-generated; exit/stairs/
  waypoint are map-authored emitters — designs rendered only. `--beacon-tones`.
- Clips: inbox/beacons-and-knocks-2026-09-29/. Awaiting Cody's ear on all three.
See [[car-door-fit]], [[beacons]], [[synthesis-by-listening]].

09-29 later: Cody KEEPS the sine beacons. Added `/beacons every N` (0.5-10 s, BeaconPreferences.Every,
saved as "every" in beacons.json beside the bools). Indoor "reflections fluttering centrally": capture
IACC 0.8-0.99 in the room tail; Steam Audio's traced IR is ~all W (Y/Z/X 18-26 dB under W late, vs -5
for diffuse) → identical in both ears. Fix: EarDecorrelator per ear after the decode (LR2 split at
300 Hz, 6 all-passes above): IACC 0.07 >1 kHz, 0.87 <150 Hz. Measure with $S/iacc.py on a capture
(sox trim around [WAUDIO] clap times; capture starts at the client's start time).
