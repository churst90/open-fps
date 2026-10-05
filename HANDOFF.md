# Handoff: door-models-glass (2026-10-05)

Branch `door-models-glass`, from da47b2df, with `doors-behaviour` merged in. Never merge to main or push;
Cody approves by ear.

## Done (unheard)
- OpenFPS.Common/GlassDoor.cs: glass-pushbar and glass-pull, openings Push/Pull/Key, close on the closer.
- OpenFPS.Common/LockCylinder.cs: key ring, pins over the bitting, shoulder, turn, cam, latch drawn; the
  game render keeps DoorSystem's key timeline (insert 0, turn 0.45 s, drawn by 0.7 s).
- OpenFPS.Common/ElevatorDoor.cs: one landing-door leaf's whole run, open or close.
- KnobDoor push opening (key suffix `:push`), PushBarDoor trim pull (`:pull`); approved keys unchanged.
- DoorSystem: models wired onto the doors-behaviour events (key-insert, push/pull, bar, motor-start).
- WorldAudioPlayer: routes glassdoor:/lockcylinder:/elevatordoor:, prewarms glass doors and keys.
- AudioLab `--door-models [out=] [only=] [stems=] [refs]`; OpenFPS.Tests/DoorModelTests.cs.

## Next
- Final renders + README into /home/cody/external-rescue/Github/open-fps/inbox/door-models-2026-10-05/.

## Build / run
DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet build OpenFPS.AudioLab/OpenFPS.AudioLab.csproj --artifacts-path ~/.cache/openfps-agent-doormodels -nodeReuse:false -p:UseSharedCompilation=false
cd ~/.cache/openfps-agent-doormodels/bin/OpenFPS.AudioLab/debug && ./OpenFPS.AudioLab --door-models out=DIR
Tests: dotnet test OpenFPS.Tests ... --filter "FullyQualifiedName~DoorModelTests|FullyQualifiedName~DoorTypeTests|FullyQualifiedName~DoorSides"
