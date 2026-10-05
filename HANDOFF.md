HANDOFF (admin-gun branch): DONE, awaiting Cody's ear

Built: fire selector (X / Shift+X, server holds it, safe, auto held on Enter with "cease" on release),
admin gun (admin_gun prefab, admin-gun permission, modes kill/vaporize/freeze/inspect, /calibre and Y,
/admingun report N, unlimited rounds), FrozenComponent honoured by MovementSystem, VehicleSystem and
DrivingSystem, designed sounds (AdminGun.cs, TeleporterSounds.cs, HandOverSounds.cs) rendered by the
client through AdminGunSynth, AudioLab --admin-gun.

Renders: inbox/admin-gun-2026-10-05 (00-tour.wav, README.txt).

Open: Cody picks a report variant (default 1, AdminGun.DefaultReport); roles-scope must emit
TeleporterSounds.Sound(kind, pos) and HandOverSounds.Sound(prefab, feet) for its events.

Tests: dotnet test ... --filter "FullyQualifiedName~AdminGunTests|FullyQualifiedName~StaffGate|FullyQualifiedName~Weapons|FullyQualifiedName~CommandCatalog"
(build: DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet build OpenFPS.Tests/OpenFPS.Tests.csproj
 --artifacts-path ~/.cache/openfps-agent-admingun -nodeReuse:false -p:UseSharedCompilation=false)
