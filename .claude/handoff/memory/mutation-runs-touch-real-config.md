---
name: mutation-runs-touch-real-config
description: A Stryker run in the real home switched Cody's door/vehicle beacons off via ~/.config/openfps/beacons.json; tests now isolate XDG_CONFIG_HOME
metadata:
  type: feedback
---

On 2026-09-24 at 13:39 a Stryker mutant over BeaconAids.cs made in-memory beacon preferences save to disk.
The tests that switch door and vehicle off wrote `{"vehicle":false,"door":false}` into Cody's real
~/.config/openfps/beacons.json, and he heard no door beacons for the rest of the day. Fixed by d3acc20:
a module initializer, OpenFPS.Tests/TestConfigIsolation.cs, gives each test process a scratch
XDG_CONFIG_HOME. The file was reset to {}.

**Why:** a mutant can turn any "in memory" path into a disk write, and the player's config sits under HOME.
**How to apply:** when Cody reports that a feature is "just silent", read ~/.config/openfps/*.json first.
Run Stryker only on a tmpfs copy with its own HOME. See [[beacons]].

10-12: on Windows the scratch XDG_CONFIG_HOME did nothing (`%APPDATA%` cannot be moved by a process), and the
tests read and wrote a real `%APPDATA%\openfps\beacons.json`. Now `SettingsFolder` is the one place that names
the settings folder, `OPENFPS_SETTINGS_DIR` moves it on every system, and TestConfigIsolation sets it (and
`OPENFPS_RENDER_CACHE`, `OPENFPS_MESH_CACHE`) before any test runs. A test holds the list of places the game
asks the system for a per-user folder.

09-27: `/tmp/openfps-test-config-<pid>` dirs are never removed (3,600 had piled up); todo item.
Engine campaign (OpenFPS.Client.Core, Engine/*.cs + VehicleSynth + Pneumatics, 2,827 mutants) started
09-25 12:17 via /tmp/openfps-stryker-run.sh; log /tmp/openfps-stryker-engines.log shows no progress
lines (no TTY). Cody: keep it running.
