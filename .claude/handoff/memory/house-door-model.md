---
name: house-door-model
description: 09-30 HouseDoor fitted to "Door Opening Sound Effect" (opening only; closing built from its parts); on branch archive/house-door-model-rejected-2026-09-30 (fc68ad93, renamed 10-05), NOT merged until Cody hears inbox/door sounds/synth-2026-09-30
metadata:
  type: project
---

Cody 09-30: non-car doors must imitate his inbox door clips, not the car door. Built `OpenFPS.Common/HouseDoor.cs`
(octave-band noise like CarDoor; latch ticks steel on every door, leaf body decay from loss factor, ring via
DoorAcoustics rule, swing air ~edge speed^6), key `door:open|close:mass:loss:speed:swing:sealed:ringHz`,
DoorSystem sends it, WorldAudioPlayer.RenderOne routes it, lab `--house-door`. Fit 3.05 dB RMS (Python),
3.4-3.8 C#; tonality ~recording. The reference has NO closing — the shut is assembled (blow 15 dB over the
tick is the old DoorAcoustics figure, unfitted). Steel comes out duller/softer, not clangy (fit says leaf
gives its ring to the frame). DoorKnock may be 20 dB short at 1-2 kHz vs both knock clips (not changed).
Fit tools: tools/house_door_fit/. Listening set: inbox/door sounds/synth-2026-09-30 (01-03 ref, 04 model
of ref, 05-10 wood/steel). Merge the branch only after Cody approves. See [[car-door-fit]].

**REJECTED by Cody 2026-10-01:** "the synthetic ones all sound the same, they are not good, too scratchy,
grainy sounding; wood and steel both sound the same." Branch NOT merged. Octave-band noise worked for the
car door (a big cabin boom + thud) but not for a house door, whose latch and wood knock are short and
pitched. Same lesson as footsteps: band metrics passed, the ear failed. Ask Cody before a next attempt
(recorded takes as a bank vs a modal/contact model).
Branch renamed 2026-10-05: archive/house-door-model-rejected-2026-09-30 (the agent worktree was removed).
