---
name: door-render-cache
description: "10-05: doors silent the first few opens = model renders (seconds each) not ready in 0.12 s; now prewarm list = every city door key, kept on disk per build, shipped in the Windows zip"
metadata:
  type: project
---

Cody 2026-10-05: "it doesn't play a sound for the first few times I open it, then it works". Cause: door
model renders take seconds (knob) to 10-40 s (glass); a first hearing waits MaxRenderLateness 0.12 s and
is dropped. Prewarm missed knob PUSH openings (since push/pull sides), gentle/hard shuts (45 %), and
rendered glass front doors at 1.0 m while the towers' are 1.9 m (never matched); nothing persisted.

Fixed 94428d20: `WorldAudioPlayer.PrewarmKeys()` (164 keys), `DoorRenderCache` (16-bit + peak, named by
WireContract.Hash; shipped ASSETS/rendercache/BUILD then LocalApplicationData/OpenFPS/rendercache/BUILD;
OPENFPS_RENDER_CACHE=off|DIR), AudioLab `--prerender-doors out=DIR`, publish-windows.sh renders and ships
them (~2.5 min, 30 MB). `DoorPrewarmTests` fails if any city door's key is not prewarmed — add new door
kinds/sizes to PrewarmKeys when the map changes.

Cody asked whether door sounds are "generated on the fly": they are simulations run once per key (far
slower than real time) and replayed; placement/occlusion/reverb are live. Offered more variants or a
real-time model if he wants per-opening variety.

**10-06 (5454db81): cache named by DoorModelFingerprint (hash of DoorModelSource files in Common.csproj:
8 models + DoorPhysics, Doors, Glass, AcousticRegistry, VehicleCabin, VehicleBody) + "-v" + Version,
not WireContract.Hash.** Cody: "generate them once and keep them". A new door model file, or one that
starts using another file, must be added to DoorModelSource; bump DoorRenderCache.Version otherwise.
Cody's cache rendered at b9dfebdd55c8-v2 (164).
