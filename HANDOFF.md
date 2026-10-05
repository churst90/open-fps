# Handoff: bodies branch

Done (committed on `bodies`):
- Death leaves a body item (Bodies.cs: Corpse, Lay, 70 kg, 2 hands, item beacon) and, for anyone carrying things, a bag of belongings beside it (BelongingsBag, LeaveBelongings). Taking a bag empties it into hands/back/pockets; leftovers stay in it.
- Carrying a body: both hands, cannot stow, speed capped at PhysicsConstants.CarryingSpeed (1.0 m/s) on server and client (StatsUpdate.SpeedLimit appended: wire change).
- Respawn: players 60 s (told at death and at 10 s), walkers retired at once and replaced after 60 s (VehicleSystem.RetireWalker/ReplaceWalker). Dead players are no player beacon.
- Cleanup: 30 min uncarried, 30 bodies and 30 bags per map.
- Parked-driver bug fixed (VehicleSystem.Casualties DriverKilled; MapManager.DestroyEntity stale-id guard).
- Tests: OpenFPS.Tests/BodiesTests.cs (+ StreetBodiesTests), WeaponsTests and PlayerPersistenceTests updated. Targeted runs pass.

Next:
- Full suite not run here (coordinator runs it on merged main). Targeted run: 128/128 passed (Bodies, Weapons, PlayerPersistence, DroppedItem, StreetLife, Hands, Inventory, BulletFlight, CommandCatalog).
- Final report to the coordinator (changes.md text and MANUAL lines).
- Delete this file before merging.
