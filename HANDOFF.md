HANDOFF (admin-gun branch, agent work in progress)

Task: fire selector (X / Shift+X), admin gun (modes kill/vaporize/freeze/inspect, calibre Y / /calibre,
unlimited ammo, admin only), its designed sounds, teleporter sounds, hand-over (give:PREFAB) sounds,
AudioLab --admin-gun renders to inbox/admin-gun-2026-10-05, client keys, tests, catalogue.

Done:
- Common: Weapons.cs FireMode + FireSelector + per-weapon Selector; WeaponHandling selector:/action: keys;
  DesignedSoundKit.cs, AdminGun.cs, TeleporterSounds.cs, HandOverSounds.cs.
- Client.Core: AudioEngine/Core/AdminGunSynth.cs, routed from WorldAudioPlayer.RenderOne.
- AudioLab: Spikes/AdminGunSpike.cs, --admin-gun (renders to scratch with out=DIR).

Next:
- Server: admin-gun permission, admin_gun prefab, CombatService partials (selector, safe, auto + cease,
  admin gun fire and effects), FrozenComponent checks in MovementSystem and VehicleSystem, commands
  selector/calibre/admingun/cease + CommandCatalog, Held() reports "admingun", refuse non-admin take/give.
- Client: X/Shift+X selector, Y/Shift+Y calibre, Enter release sends cease, HoldsGun accepts admingun, KeyHelp.
- Tests (new file AdminGunTests.cs), render final set into inbox, README.

Build/test: DOTNET_CLI_USE_MSBUILD_SERVER=0 nice ~/.dotnet/dotnet build OpenFPS.Tests/OpenFPS.Tests.csproj
  --artifacts-path ~/.cache/openfps-agent-admingun -nodeReuse:false -p:UseSharedCompilation=false
Renders: cd ~/.cache/openfps-agent-admingun/bin/OpenFPS.AudioLab/debug && ./OpenFPS.AudioLab --admin-gun
