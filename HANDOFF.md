# Handoff: doors-behaviour (2026-10-05)

Branch `doors-behaviour`, based on da47b2df. Do not merge to main or push.

## Done
- `DoorComponent` appended: `PushSide`, `OpenedFrom`, `HandId`, `KeySeconds`. Prefab and map-entity
  fields `PushSide` (and map `KeyedSide`).
- Push/pull swing (`DoorSystem.SwingSign`), events `push`/`pull`, bar only on the push side.
- Key sequence `key-insert`, `key-turn`, `unlock`, then `pull`/`push` (`DoorSystem.TurnTheKey`).
- Obstruction for every kind and every mover (`DoorSystem.InTheWay`, `Sweeps`, `FirstInTheWay`);
  hand close refused in `Set`; narration in Program.cs (E) and CommandHandler (/open, /close).
- gen_city.py turns hinged leaves +Z away from room `a`; city.json regenerated (byte-identical).
- docs/DOOR_TYPES_EVENTS.md rewritten; tests in OpenFPS.Tests/DoorSidesTests.cs; DoorTypeTests updated.

## State
- Door tests pass (DoorTypeTests, DoorSidesTests, DoorSoundPlacementTests, SteelDoorSoundTests).
- BusStopTests.TheDoorBeeperSoundsOnlyAtTheStop hung > 5 min in one run: check whether it hangs on
  da47b2df too before blaming door changes.

## Next
- Full suite. Final report.

## Test command
DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet test OpenFPS.Tests --artifacts-path ~/.cache/openfps-agent-doors -nodeReuse:false -p:UseSharedCompilation=false --filter "FullyQualifiedName~DoorSidesTests"
