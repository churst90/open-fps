# HANDOFF: roles-scope (delete before merging)

Branch roles-scope, from main 3fb886b2. Task: docs/PLAN_2026-10-05.md sections 1-2 with Cody's decisions.

State: DONE. Targeted tests pass (RolesScope, StaffGate, CommandCatalog, SocialTravel, AdminCommand,
CommandFeedback, PrefabSpec: 179/179). docs/SERVER_SECURITY.md updated. Full suite is the
coordinator's, on merged main.

What is in it:
- Permissions.cs: table to the plan; scope flag (OnOwnMap); powers give-premium, tp-free, grant-any,
  perms-any, protected, maps-any; `move` its own permission; `tp` no longer gated (needs the item).
- CommandHandler partials: .Scope, .Maps (/map, /maps), .Teleport (/tp), .Spawning (/spawn walker,
  vehicle, train, helicopter; /give NAME vehicle PRESET).
- MapAccessRepository (map_access.json), maps/players/, MapManager.CreateMap, MapTemplates.Flat.
- GameServer.After (delayed actions), UserSession.ArriveAt, VehicleSystem/RailSystem.SpawnOne.
- prefabs/teleporter.json (Premium).

Test command:
DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet test OpenFPS.Tests --artifacts-path ~/.cache/openfps-agent-roles -nodeReuse:false -p:UseSharedCompilation=false --filter "FullyQualifiedName~RolesScopeTests|FullyQualifiedName~StaffGateTests|FullyQualifiedName~CommandCatalogTests"
