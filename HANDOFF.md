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
- Coordinator: run ONLY targeted tests, never the full suite (it runs it on merged main).
- A killed full run showed only EngineSynthTests real-time-budget failing, under load 60 (unrelated).

## Next
- Done: targeted run 247/247 passed. Only the report is left.

## Test command
DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet test OpenFPS.Tests --artifacts-path ~/.cache/openfps-agent-doors -nodeReuse:false -p:UseSharedCompilation=false --filter "FullyQualifiedName~DoorSidesTests"
