---
name: sliding-doors
description: 10-03 patio and automatic sliding doors as physical models (branch sliding-doors, lab --sliding-door), round 1 sent unheard; what each fix taught
metadata:
  type: project
---

2026-10-03, Cody asked to hear sliding patio doors and automatic doors. `OpenFPS.Common/SlidingDoor.cs`
on branch `sliding-doors` (fa838949), lab `--sliding-door [out=] [only=] [stems=]`. Listening set
`inbox/doors-2026-10-03-sliding-round1/` (00-tour.wav, README). NOT in the game: the server already
sends `door:patio-slide:*` and `door:auto-slide:*` events (docs/DOOR_TYPES_EVENTS.md); wire it the way
the push bar was, after Cody approves. Game timings (auto opens 1.5 s, shuts 2.5 s) are faster than
the model's real speeds (about 3 s and 5 s).

Sources: research_notes/Sliding and automatic doors/notes.md (forces, ANSI A156.10, drive trains),
and 18 measured Freesound takes in the scratchpad (door/refs/sliding-refs.md; previews, SPEC only).

Lessons, each cost a render:
- Pile flutter is 0.3/sqrt(fibres) of the drag, not half of it (94 dBA hiss).
- A 7 g Port patch on 2e7 rang at 8.5 kHz and made the roll a hiss: rail patch ~1 g on its bedding.
  A 1 g patch under a damped urethane contact went unstable at the step (period-2, 1.4 M log lines,
  filled /tmp): 6 g there.
- A nylon tyre wraps grit smaller than its static sink (~50 um): only bigger grains click.
- A rigid leaf on a bumper is a 40 ms push with nothing in it: stiles on a 200 Hz spring, the IGU's
  air-coupled mode (~210 Hz), and the handle knocking in its play give the cluster and the brightness.
- Gear mesh forces are internal to the gearbox: they ring its housing; only the belt's ripple leaves it.
- Bracket damping sized on the wheel's mass let a steel-wheeled leaf hop for a second.
See [[clack-is-a-loose-bar]], [[doors-need-detail-and-level]], [[tmp-is-eight-gigabytes-of-ram]].

**Round 1 verdict (10-03):** patio "ok" (earlier: too metallic, hollow, smooth; kept as is), but "the
slide still plays after the door is shut" = leaf rebounding at 0.77 of its speed (stile spring zeta 0.05)
and rolling back 43 mm. Automatic: "metallic tube", "too much resonance", "too much hiss", "synthetic".
Round 2 (inbox/doors-2026-10-03-sliding-round2, 2nd commit after fa838949): header/frame dense fields
from 300 Hz (narrow walls, not one sheet), housings loss 0.1, worm TE = 10-harmonic profile (comb on
mesh rate, as freekit's ~103 Hz series), brush flutter removed, auto header fed by direct force (a Port
patch tilts +6 dB/oct up to its resonance: the hiss), belt teeth jittered and soft, belt clamp play
knocks at take-up, S-curve controller, PU flats. Patio: jamb lambda 3, stile zeta 0.2, hand holds home
80 N + 300 N s/m. Patio still uses the Port feed and 60 Hz frame/sill fields because Cody kept it.
Tool: scratchpad rollstats.py FILE T0 T1 (octaves, 50 ms sd, clicks/s, mod depth/rates) — the
reference write-up's own measures. Render with sdq.sh (strips per-contact log lines: one run wrote
456 MB and filled /tmp).

**Round 2 verdict (10-03):** "much better, not perfect". Old auto (07/08) synthetic+metallic; auto closes
grainy/static; "what's the clicking on the automatic doors"; patio 13/14 metallic. Round 3 (492208ef,
inbox/doors-2026-10-03-sliding-round3): clicking = commutator knocks at +-30 % per bar (now 4 %) and belt
teeth seated in 0.3 ms (now a quarter pitch); grain = flat spots felt at a point (AcrossPatch: Hann over
the contact length); old gear = TE as wear-squared per-tooth error + runout + motor speed hunt, not a
bigger comb; patio sill/frame from 300 Hz loss 0.03, pile flutter through a 2 kHz backing, old patio on
worn nylon. Direct force into the PATIO sill made the roll 5 dB under the impact (bedding takes it):
patio keeps a 3 g Port, auto header takes direct force. Find clicks with stems + rollstats clicks/s and
match the rate to a mechanism (motor rev, mesh, teeth, wheel turn).

**Round 3 REJECTED 10-03** ("patio worse, too gritty, too thin"); Cody: "just use the round 2 sounds in
the game. creating doors is extremely difficult." Round 2 restored (a77d23f4) and wired in, merged to
main 6043a596: server sends the whole run at motor-start (auto, both ways), latch-retract (patio open)
and closing rollers (patio shut; on arrival it played after the shut); auto leaves move in
SlidingDoor.AutomaticSeconds(width) (city 1.15 m: 3.3 s / 5.3 s). Client prewarms patio 1.0 m and auto
1.15 m. Same commit fixed prewarm sending push-bar keys to KnobDoor.RenderKey (16 samples of silence).
Approved set for listening: inbox/doors-approved-2026-10-03/. Packages build 6b62d4d287a3 in dist/.
Known gap: an automatic door that reopens mid-close keeps playing its close (no voice stop).

**10-03 night, the reference:** Cody named inbox/door-sounds-2026-10-02/patio-slide/ref-goblinjack-slides.wav
as THE iconic patio door (44.1 kHz mono, slides at -37..-42 dBFS, noise floor -71). Measured: slide
energy 250 Hz-1 kHz (63 Hz -22, 125 -11, 2k -9, 4k -15, 8k -17 dB re max); envelope beats at ~8 Hz
(roller rotation); stop thump rings 83-106 Hz, -30 dB in 0.25 s, a 3-4 hit cluster in ~50 ms.
Approved round 2 (inbox/doors-approved-2026-10-03 25-32) is broadband: lows 0..-2 and 8 kHz -2 dB,
i.e. 10-20 dB too much at both ends. Proposed physical changes (softer wheel contact, less low rumble,
check ~8 Hz beat, 90 Hz panel stop + bounce cluster) and analysis-by-synthesis against the recording,
NOT granular playback. Asked Cody before changing anything.
