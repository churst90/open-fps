---
name: traffic-driver-lurch
description: 09-28 sports bike "shifting all weird" was the VirtualDriver (clutch dumps, never left 1st, idle creep); --shift-trace shows it
metadata:
  type: project
---

Cody 2026-09-28: sports bike "loud, sounds like a car when it passes, but the shifting is all weird".
Not the gearing alone. `--shift-trace <preset> [top=50] [every=0.1]` (AudioLab, ShiftTraceSpike)
drives the game's `VirtualDriver` through stop-go and prints gear/rpm/clutch/throttle. It showed:
- light-throttle upshift at 85% of torque peak = 9,350 on the bike, so it never left 1st in town;
- a light throttle took the "clutch out" branch, the engine revved free (7,000 on 5%), then the
  plain else closed the clutch in one step: +10 km/h in 0.1 s. Muscle car and dirt bike too;
- the speed integral wound up in the overshoot and held the throttle shut 2 s;
- idle in 1st drove the bike on at 12 km/h, it never stopped.
Fixed in VirtualDriver (clutch let-in 2/s on mismatch, launch roll-on, onPace blend, anti-windup,
stopping = clutch in) plus Gearbox.CruiseUpshiftRpm/LaunchRpm on the bike. Upshifts still close the
clutch at once — slow let-in on upshifts made DeclaredSourceLevelMatchesTheLiveVoice fail by 9.6 dB.
Compare new vs old by swapping in `git show HEAD:` Driveline.cs; cars/buses must not change.
Open: bike pull-away revs swing 2,800-4,200 (idle governor vs clutch bite). Unheard by Cody.
See [[live-voice-is-not-the-bench]], [[engine-bench-truth]], [[dont-respec-what-was-approved]].
