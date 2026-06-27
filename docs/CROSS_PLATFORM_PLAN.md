# OpenFPS — Cross-Platform (Linux) Plan

_Authored 2026-06. Companion to `docs/ROADMAP.md` (Priority 4). Verified by building on Gentoo Linux with the .NET 10 SDK (10.0.301)._

## TL;DR

The client is **not** hard-tied to Windows. The Windows lock is at the *edges* (UI, key capture, screen-reader, mic), not in the game logic — and the FMOD **audio engine is already fully portable**. A Linux client is a focused refactor (extract ~4 platform interfaces + a GTK head), not a rewrite.

## Verified build status on Linux (net10.0)

| Project | TFM | Builds on Linux | Notes |
|---|---|---|---|
| `OpenFPS.Common` | net10.0 | ✅ | |
| `OpenFPS.Server` | net10.0 | ✅ | Runs on Linux today. ⚠ transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 has a known high-severity advisory (GHSA-2m69-gcr7-jv3q) — bump EF Core / pin SQLitePCLRaw. |
| `OpenFPS.AudioLab` | net10.0 | ✅ | Compiles the audio engine source directly; needs `libfmod.so` to *run*. |
| `OpenFPS.Tests` | net10.0 | ✅ | 36/36 pass on Linux. |
| `OpenFPS.Client` | **net10.0-windows** | ❌ | WinForms + System.Speech force Windows. This is the port target. |

### How to build/run on this machine
The .NET 10 SDK is installed at `~/.dotnet` (not on `PATH`). Prefix commands:
```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
dotnet build OpenFPS.Server/OpenFPS.Server.csproj
dotnet test  OpenFPS.Tests/OpenFPS.Tests.csproj
dotnet run   --project OpenFPS.AudioLab          # needs libfmod.so (see below)
```
FMOD native libs: the binding uses `DllImport("fmod")`, which resolves `fmod.dll` on Windows and **`libfmod.so` on Linux** automatically. Download FMOD Engine (Linux) free from <https://www.fmod.com/download> and drop `libfmod.so` / `libfmodL.so` into the repo-root `lib/` folder; the AudioLab csproj copies `..\lib\*.so` to its output.

## Portability inventory (client, 40 source files)

- **~27 files already platform-neutral** — game loop, networking, prediction/physics, world state, and the entire `AudioEngine/` + `FmodNative/` (verified: zero Windows usings, depends only on `OpenFPS.Common` + Serilog; `SpatialService` in `Core/` is also neutral).
- **~6 files need a mechanical fix** — they only touch Windows via `System.Windows.Forms.Keys`: `InputStateBuffer`, `ClientSimulationSystem`, `InputCommandMapper`, `Input/SystemInputProcessor`, `Input/AccessibilityProcessor`, `Input/CombatProcessor`. Replace with a neutral `GameKey` enum.
- **~7 files are genuinely Windows-specific** (need a Linux impl):
  - UI: `UI/MainWindow`, `UI/MenuWindow`, `UI/LoadingWindow`, `Services/ClientNavigationService` (WinForms `ApplicationContext`).
  - Input capture: `Core/GlobalKeyboardHook` (Win32 `SetWindowsHookEx`).
  - Screen reader: `Services/TolkService` (NVDA P/Invoke + `System.Speech`).
  - Mic: `Services/VoiceCapture` (NAudio).

## Target architecture: one neutral core + two thin heads

Keep the WinForms head on Windows; add a GTK head on Linux.

```
OpenFPS.Client.Core    (net10.0)          all logic + audio engine + platform INTERFACES
OpenFPS.Client.Windows (net10.0-windows)  WinForms UI + Windows impls (NVDA/SAPI, hook, NAudio)
OpenFPS.Client.Gtk     (net10.0)          GTK UI + Linux impls (speech-dispatcher, GTK keys, FMOD mic)
```

The job hinges on **four interfaces** in Core, implemented per head:

| Interface | Purpose | Windows impl | Linux impl |
|---|---|---|---|
| `ISpeechOutput` | `Speak(text, interrupt)`, `Interrupt()` | NVDA + System.Speech (existing `TolkService`) | **speech-dispatcher** (libspeechd / `spd-say`) — what Orca uses |
| `IInputSource` (+ `GameKey` enum) | abstract key up/down | WinForms focused key events | GTK `key-press`/`key-release-event` |
| `IClientShell` | ShowMenu / ShowLoading / UpdateLoadingStatus / EnterGame / dialogs | WinForms windows | GTK windows (auto-exposes AT-SPI to Orca) |
| `IMicrophoneCapture` | PCM mic frames for voice chat | (replace NAudio) | **FMOD `recordStart`** — cross-platform; ideally unify both heads on this |

## Phase A — De-Windows the core (~1 week; valuable on Windows too)

1. **Define a neutral `GameKey` enum** in Core; replace `System.Windows.Forms.Keys` across the ~6 input files. Each head maps its native key codes → `GameKey` at the boundary.
2. **Extract the four interfaces** into Core.
3. **Create `OpenFPS.Client.Core` (net10.0)** and move the neutral code into it (game loop, systems, audio engine, input mapping, `SpatialService`, services that don't touch Win32). It must compile with **no** `UseWindowsForms`.
4. **Refactor existing Windows code into `OpenFPS.Client.Windows`** implementing the interfaces (the current `TolkService`, WinForms windows, NAudio capture become the Windows impls).
5. **Drop the global keyboard hook** in favor of focused-window key events — more portable *and* better-behaved with screen readers (Wayland forbids global hooks). Keep `InputStateBuffer` + `InputCommandMapper` (already clean).
6. **Regression-test the Windows client** still works end-to-end.

## Phase B — GTK Linux head (~1 week; thin once Phase A is done)

1. Ship FMOD Linux `.so`; make the startup DLL-presence check platform-aware (`libfmod.so` vs `fmod.dll`) — see `ClientRunner.RequiredNativeDlls`.
2. **`OpenFPS.Client.Gtk` (net10.0)** — GtkSharp (GTK3, most mature a11y) or Gir.Core (GTK4). A window that holds focus + a few menu/login dialogs + key events implementing `IClientShell`/`IInputSource`.
3. **speech-dispatcher `ISpeechOutput`** impl (P/Invoke libspeechd, or shell `spd-say`).
4. **Mic capture** via FMOD `recordStart` (or stub voice chat initially).

## Toolkit note: GTK vs Avalonia

- **GTK** (chosen): best, most mature AT-SPI/Orca support on Linux; downside is a *second* UI codebase alongside WinForms.
- **Avalonia**: one shared cross-platform UI codebase, AT-SPI support is newer/less battle-tested.
- Either way, **Phase A is identical** — the interface extraction is toolkit-agnostic, so the toolkit is purely a Phase-B decision and doesn't block starting.

## The AudioLab spike (already in repo)

`OpenFPS.AudioLab` is a `net10.0` console app that compiles the audio engine source directly (no file moves, Windows build untouched) and runs the orbiting-mono-source HRTF test (`AudioDiagnostics`). It both (a) proves the audio engine ports to Linux and (b) lets the spatial-audio test run on Linux once `libfmod.so` is present. It is effectively the first slice of `OpenFPS.Client.Core`.

## Related audio bugs found in passing (track in ROADMAP P1)
- Existing `PlayVoice` / `PlayUiBeep` use `MODE.OPENMEMORY` for raw PCM **without `OPENRAW`** — likely silently broken; the diagnostic source uses the correct `OPENMEMORY | OPENRAW`.
- `materials.json` overwrites hardcoded frequency-band material properties with zeros (occlusion EQ collapse) — `AcousticRegistry.Initialize`.
