---
name: knob-door-pilot
description: "10-03 physical knob door (KnobDoor.cs, branch knob-door, lab --knob-door) sent to Cody unheard; what each fix taught, the open faults, the instruments"
metadata:
  node_type: memory
  type: project
  originSessionId: f81a1dbe-770b-4ab5-8b47-4dc3748f9cdb
  modified: 2026-10-03T10:43:12.373Z
---

2026-10-03: the knob-door pilot Cody asked for ([[model-the-thing-not-the-sound]]). Branch `knob-door`
(3add79d3), `OpenFPS.Common/KnobDoor.cs`, lab `--knob-door [only=] [stems=DIR] [pins]`. NOT wired into
the game. Listening set: `inbox/knob-door-2026-10-03/` (00-tour.wav has spoken labels, README).
Waiting on his verdict before push bar / sliders.

What it is: leaf = rigid rotation + hinged-free plate modes (Rayleigh far field, power-averaged level
with phase from one listener direction), 3 hinges (LuGre pin friction on a knuckle-on-screws resonator,
~1.2 kHz, plus friction straight into the frame), sprung bevel latch + strike + keeper as small parts on
mounts (Mount class), stop moulding strips (50 g, 1.5e8 N/m) hit Hertz-hard by the leaf, stud+jamb+board
strip as the frame, knob on cam/rose with brass shell modes, hand as PD tracking min-jerk paths. 192 kHz.

Lessons (each cost a round):
- Summing modes with the power-average level and the SAME sign = +20 dB coherent; give each mode a phase.
- A free 12 g faceplate took the bolt's whole blow and rang at 114 dB: parts set in wood are damped
  masses on mounts, not free plates.
- A soft "spring" stop removes the crack; the crack is the leaf hitting the moulding's own mass.
- Squeak: LuGre bristle 1e6/N kept the oscillation inside pre-slide (pure tone); 1e7/N gives real
  stick-slip (trace shows grip 29->60 N per cycle). A two-knuckle "free pin" model never squealed:
  at kHz the sliding contact acts as a spring, so the Stribeck fall pumps nothing.
- WAVs at a fixed 20 Pa full scale CLIPPED the shuts; the lab now puts one gain on a run.
Open faults (in the README): shut tail low-heavy (125-500 Hz ring), 3-5 latch bounces, squeak too pure
(harmonics filtered by the knuckle resonator; real ones have strong overtones and glide, see refs
sq-*.wav fetched from Freesound 389550/535530/451806), absolute level ~10-20 dB hot (2.5 % of KE radiated;
normal shut 107 dBA, slam 117, knob release 76, dry squeal 93).
Instruments: scratchpad alevel.py (LAFmax/LAE from a WAV with a full-scale in Pa), bands.sh (octave
balance of a cut), first-8-ms comparison vs refs is the fair one (refs' tails are their rooms).

**Round 2 APPROVED 10-03** ("I'd go with these sounds for these types of doors for now"): "wine glass"
latch = knob modelled as open cylinder (now closed ball + annulus ~10 kHz); "plastic" = loss 0.07 (now
wood's 0.015-0.03); slam still "small". He wants opens AUDIBLE in a corridor: hand pull 12 N, and the
game declares model LAFmax minus `LevelCalibrationDb` 12 (open 71, close 104). Merged to main 945ce730:
server sends knobdoor:open on latch-retract and the whole close on the closing SWING (impact at swing
end); door id picks 1 of 4 characters (1 in 4 squeaky: "every door creaking sounded haunted" rule);
client prewarms 0.9/1.1/1.4 m widths (city scales the door prefab) 4 at a time. Unheard in game.
Shared parts now in `DoorPhysics.cs` (refactor checked bit-identical by md5).
**Push bar** (`PushBarDoor.cs`, branch pushbar-door, lab `--pushbar-door`): steel sandwich leaf with
steel-stiffener core (honeycomb shear = 16k modes, 120 s renders), palm-compliant hand, urethane bar
bumpers, skin patches, thinned skin-panel modes (Plate minHz/keepEvery), bar thrown by slam (lowpassed
leaf accel, same points or it feeds back). Round 1 in inbox/pushbar-door-2026-10-03: shut is 15-20 dB
short at 2-8 kHz vs recordings, bounces ~10 times. Not integrated into the game yet.
