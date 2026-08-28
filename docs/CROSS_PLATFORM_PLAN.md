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
| `OpenFPS.Client` | **net10.0-windows** | ✅ *(compiles)* | WinForms + System.Speech mean it only **runs** on Windows, but `EnableWindowsTargeting` lets it be compile-checked from Linux. Since audit step 7 it contains only the Windows implementations of the four seams. |
| `OpenFPS.Client.Core` | net10.0 | ✅ | All the client's game logic, incl. `ClientGameSession`. |
| `OpenFPS.Client.Gtk` | net10.0 | ✅ | The Linux head: GTK4 windows + speech-dispatcher. |

### How to build/run on this machine
The .NET 10 SDK is installed at `~/.dotnet` (not on `PATH`). Prefix commands:
```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
dotnet build OpenFPS.Server/OpenFPS.Server.csproj
dotnet test  OpenFPS.Tests/OpenFPS.Tests.csproj
dotnet run   --project OpenFPS.AudioLab          # needs libfmod.so (see below)
```
FMOD native libs: the binding uses `DllImport("fmod")`, which resolves `fmod.dll` on Windows and **`libfmod.so` on Linux** automatically. Download FMOD Engine (Linux) free from <https://www.fmod.com/download> and drop `libfmod.so` / `libfmodL.so` into the repo-root `lib/` folder; the AudioLab csproj copies `..\lib\*.so` to its output.

## Portability inventory — _superseded by the Phase-A completion below (kept for the record)_

The original count was 40 client source files: ~27 already neutral, ~6 touching Windows only through
`System.Windows.Forms.Keys`, ~7 genuinely Windows-specific. As of audit step 7 the split is settled:
**everything neutral lives in `OpenFPS.Client.Core`**, and `OpenFPS.Client` is down to nine files, every
one of which is a Windows implementation of a seam:

- UI: `UI/MainWindow`, `UI/MenuWindow`, `UI/LoadingWindow`, `Services/ClientNavigationService`
  (WinForms `ApplicationContext`) + `Services/WinFormsClientShell` (the `IClientShell` impl).
- Input capture: `Core/GlobalKeyboardHook` (Win32 `SetWindowsHookEx`) + `Core/WinFormsKeyMap`.
- Screen reader: `Services/NvdaSpeechOutput` (NVDA P/Invoke + `System.Speech`).
- Mic: `Services/VoiceCapture` (NAudio, implementing `IMicrophoneCapture`).
- Bootstrap: `Program`, `Core/ClientRunner`, `Services/PersistenceService`.

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
| `ISpeechOutput` | `Speak(text, interrupt)`, `Interrupt()` | NVDA + System.Speech (`NvdaSpeechOutput`) | **speech-dispatcher** (libspeechd / `spd-say`) — what Orca uses |
| `IInputSource` (+ `GameKey` enum) | abstract key up/down | WinForms focused key events | GTK `key-press`/`key-release-event` |
| `IClientShell` | ShowMenu / ShowLoading / UpdateLoadingStatus / EnterGame / dialogs | WinForms windows | GTK windows (auto-exposes AT-SPI to Orca) |
| `IMicrophoneCapture` | PCM mic frames for voice chat | (replace NAudio) | **FMOD `recordStart`** — cross-platform; ideally unify both heads on this |

## Phase A — De-Windows the core: **DONE ✅** (audit step 7, 2026-08-28)

1. ✅ **Neutral `GameKey` enum** in `Core/Platform`. Each head maps its native codes at the boundary —
   `WinFormsKeyMap` (`System.Windows.Forms.Keys`) and `GtkKeyMap` (GDK keyvals). No head-specific key type
   reaches game logic.
2. ✅ **The four interfaces are in Core**: `ISpeechOutput`, `IClientShell`, `IMicrophoneCapture`,
   `IInputSource` (+ the shared `InputStateBuffer` both heads write into).
3. ✅ **`OpenFPS.Client.Core` (net10.0)** holds the neutral code — and, since step 7, `ClientGameSession`:
   the *whole* of the client's game logic (netcode, prediction, reconciliation, acoustics, shelter,
   bindings, every spoken announcement). Both heads run it verbatim. It compiles with no `UseWindowsForms`.
4. ✅ **The Windows head implements the interfaces** rather than reimplementing the logic:
   `NvdaSpeechOutput`, `WinFormsClientShell` (over `ClientNavigationService`), `VoiceCapture`,
   `WinFormsKeyMap`. `ClientSimulationSystem`, `InputHandler`, the Windows `InputCommandMapper` /
   `InputStateBuffer` and the three input processors are deleted; so are the GTK head's `GameSession` and
   `GameInput`. (The head kept the name `OpenFPS.Client` rather than being renamed
   `OpenFPS.Client.Windows` — a rename buys nothing now that it contains only the Windows impls.)
5. ⏳ **The global keyboard hook is still in place** on Windows, behind the key-map seam. Dropping it for
   focused-window key events is the right end state (Wayland forbids global hooks, and they fight screen
   readers) but it is a behaviour change that needs a live Windows session to verify; the seam makes it a
   one-file swap when that happens.
6. ⏳ **Windows regression test outstanding.** The head now **compiles from Linux**
   (`EnableWindowsTargeting` is set in its csproj, so `dotnet build OpenFPS.Client` works on Gentoo) — a
   shared session class only one of its two heads can be built against is exactly how they drifted apart.
   A live logged-in walkthrough on Windows is still needed.

## Phase B — GTK Linux head (Gir.Core / GTK4)

Milestones landed (build-green on Gentoo; `OpenFPS.Client` Windows head left untouched):
- **m1** — accessible main menu + speech (`SpeechDispatcherOutput` → speech-dispatcher/Orca).
- **m2** — connect + login, speaking the result.
- **m3** — enter the world: shared Core game loop, GTK key input, FMOD/Steam-Audio spatial sound.

### m3 strategy — Linux-first, keep Windows green _(historic; completed by step 7)_
Rather than the full Phase-A move (which at the time would have forced untestable edits to the WinForms
head), the shared-but-neutral game code was first relocated to `OpenFPS.Client.Core` with identical public
APIs — `LocalPlayerState`, `ClientWorldState`, `ClientPhysicsSystem`, `LocalPlayerController`,
`ClientAudioSystem`, `SoundMappingService` — while the GTK head kept its own `Game/GameSession` and
`Game/GameInput` as a parallel implementation of the Windows `ClientSimulationSystem`.

That parallel pair is what step 7 removed. `ClientGameSession` in Core replaced both; `GameSession`,
`GameInput` and the `SoundMappingService` compat constructor are gone. The GTK head is now
`Program` + `Game/GtkClientShell` + `Game/GameWindow` + `Game/GtkKeyMap`, and nothing else. What made
the full move safe in the end was discovering `EnableWindowsTargeting`: the Windows head can be compiled
on Linux, so "untestable" was only ever true by default.
- `Program.cs` runs net poll + fixed-step sim on one `GameLoop` thread; the game window is created
  on the GTK main thread via the captured `SynchronizationContext`. FMOD degrades gracefully when
  `libfmod.so` is absent (`FmodLibraryPresent`).

**Verified (at the time of m3):** all Linux projects build (0 warnings); 37/37 tests pass; client launches, connects to
speech-dispatcher, and enables spatial audio (libfmod.so + libphonon.so present).
**Not yet exercised this session:** a live logged-in walkthrough (needs a running server + GUI login).
**Windows head:** as of audit step 7 it is compile-checked on Linux every build (`EnableWindowsTargeting`),
and the whole solution builds clean; a live run on Windows is still the outstanding confirmation.

### Phase B remaining
- **Mic capture / voice chat on Linux** via FMOD `recordStart` (the `IMicrophoneCapture` seam). Until then
  the GTK head takes `NullMicrophoneCapture`, and pressing the transmit key *says* voice is unavailable
  rather than appearing to transmit into nothing.
- Friend / player-list UI in the GTK head (the list itself is spoken today via F5/F6/F7).
- ~~Chat/command console in the GTK head~~ — **done** in step 7: `GtkClientShell` provides the command
  entry, the quit confirmation and a loading window, and chat scrollback works on both heads now that
  `ChatManager` lives in Core on `ISpeechOutput`.
- ~~Complete the Phase-A interface extraction and remove the compat shim~~ — **done** in step 7. The
  `SoundMappingService` three-argument shim is gone. The Windows head deliberately keeps its own
  NVDA + SAPI backend rather than moving to `TolkSpeechOutput`: `Tolk.dll` is not in this repo's `lib/`
  and `nvdaControllerClient64.dll` is, so the swap would trade a working screen reader for silence. Ship
  Tolk.dll and it is a one-line change in `ClientRunner`.

## Toolkit note: GTK vs Avalonia

- **GTK** (chosen): best, most mature AT-SPI/Orca support on Linux; downside is a *second* UI codebase alongside WinForms.
- **Avalonia**: one shared cross-platform UI codebase, AT-SPI support is newer/less battle-tested.
- Either way, **Phase A is identical** — the interface extraction is toolkit-agnostic, so the toolkit is purely a Phase-B decision and doesn't block starting.

## The AudioLab spike (already in repo)

`OpenFPS.AudioLab` is a `net10.0` console app that compiles the audio engine source directly (no file moves, Windows build untouched) and runs the orbiting-mono-source HRTF test (`AudioDiagnostics`). It both (a) proves the audio engine ports to Linux and (b) lets the spatial-audio test run on Linux once `libfmod.so` is present. It is effectively the first slice of `OpenFPS.Client.Core`.

## Related audio bugs found in passing (track in ROADMAP P1)
- Existing `PlayVoice` / `PlayUiBeep` use `MODE.OPENMEMORY` for raw PCM **without `OPENRAW`** — likely silently broken; the diagnostic source uses the correct `OPENMEMORY | OPENRAW`.
- `materials.json` overwrites hardcoded frequency-band material properties with zeros (occlusion EQ collapse) — `AcousticRegistry.Initialize`.
