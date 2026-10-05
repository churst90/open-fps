# HANDOFF: roles-scope (delete before merging)

Branch roles-scope, from main 3fb886b2. Task: docs/PLAN_2026-10-05.md sections 1-2 with Cody's decisions.

Done (code written, server builds):
- Permissions.cs: table re-done to the plan; scope flag (OnOwnMap); powers give-premium, tp-free,
  grant-any, perms-any, protected, maps-any; `move` its own permission; `tp` no longer gated.
- CommandHandler partials: .Scope (MayHere/OwnsHere/GrantCeiling/SendGiveEvent), .Maps (/map, /maps),
  .Teleport (/tp with teleporter, charge/leave/arrive events), .Spawning (/spawn walker|vehicle|train|
  helicopter, /give NAME vehicle PRESET).
- Maps: MapAccessRepository (map_access.json), player maps in maps/players/, MapManager.CreateMap,
  MapTemplates.Flat, CanEnter honours invites, MapManifest OwnerId/IsPublic filled.
- GameServer.After (delayed actions in DrainCommandBuffer), ArriveAt on spawn, Vehicles/Rail accessors.
- VehicleSystem.SpawnOne / RailSystem.SpawnOne (filtered Spawn).
- teleporter.json prefab (Premium), PrefabTemplate.Premium + schema + validator.
- Tests: StaffGateTests, CommandCatalogTests, SocialTravelTests, AdminCommandTests updated;
  new RolesScopeTests.cs.

Next: run the targeted tests, fix failures, run the full suite, update docs/SERVER_SECURITY.md table,
write the final report (changes.md + MANUAL text in the report, not in those files).

Test command:
DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet test OpenFPS.Tests --artifacts-path ~/.cache/openfps-agent-roles -nodeReuse:false -p:UseSharedCompilation=false --filter "FullyQualifiedName~RolesScopeTests|FullyQualifiedName~StaffGateTests|FullyQualifiedName~CommandCatalogTests"
