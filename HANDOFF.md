# Handoff: door-models-glass (2026-10-05)

Branch `door-models-glass`, from da47b2df. Never merge or push; Cody approves by ear.

## Task
Physical door models in the style of KnobDoor/PushBarDoor (DoorPhysics): glass front door (glass-pushbar,
key outside + pull handle, bar inside), glass pull door, lift door, the key in a lock (LockCylinder), and push
openings for KnobDoor and PushBarDoor. Renders + README into
/home/cody/external-rescue/Github/open-fps/inbox/door-models-2026-10-05/ with 00-tour.wav.

## Done (WIP, unheard)
- OpenFPS.Common/GlassDoor.cs: both kinds, openings Push/Pull/Key, close on closer. Seals per character
  (bulb, bulb set, pile, none). Hand on handle is a mass+palm spring. Settles 0.3 s before an opening.
- OpenFPS.Common/LockCylinder.cs: keyring pendulums, pins over bitting, shoulder, turn/cam/hub, host leaf.
- OpenFPS.AudioLab/Spikes/DoorModelsSpike.cs, `--door-models [out=] [only=] [stems=] [refs]`.

## Next
1. LockCylinder: cam/hub-stop chatter (switch ContactRestitution -> Contact), make key contacts two-way with
   the key's modes (energy), start hand nearer. Levels were 20 dB hot (LAF 80-98 at 1 m).
2. GlassDoor: re-render after the fixes (openings had never left the stop: fixed hand dynamics); check
   levels (closes were LAF 102-120), remove GD_TRACE hack.
3. ElevatorDoor.cs (not started), push openings in KnobDoor/PushBarDoor (key field appended, approved keys
   unchanged), DoorSystem/WorldAudioPlayer wiring + prewarm, tests, README + renders.

## Build / run
DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet build OpenFPS.AudioLab/OpenFPS.AudioLab.csproj --artifacts-path ~/.cache/openfps-agent-doormodels -nodeReuse:false -p:UseSharedCompilation=false
cd ~/.cache/openfps-agent-doormodels/bin/OpenFPS.AudioLab/debug && ./OpenFPS.AudioLab --door-models only=glass-pull out=DIR
