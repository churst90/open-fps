#!/usr/bin/env bash
# Build + run the OpenFPS GTK client WITHOUT hitting the ntfs3 build hang.
#
# The repo lives on an ntfs3 volume whose kernel driver stalls MSBuild's output-write phase, so a plain
# `dotnet run` (which writes obj/bin onto ntfs3) hangs. This builds with --artifacts-path on tmpfs and then
# runs the prebuilt DLL directly. The build copies ASSETS and the FMOD/Steam-Audio .so libs
# next to the DLL, so the tmpfs output is self-contained.
#
# Usage:
#   ./run-gtk-client.sh            normal run — Steam Audio simulation ON (it is ON unless turned off)
#   ./run-gtk-client.sh on         the same, plus the acoustic trace, logged to /tmp/openfps-sa-on.log
#   ./run-gtk-client.sh off        the OLD hand-rolled spatializer + trace, to /tmp/openfps-sa-off.log
#   ./run-gtk-client.sh capture    as `on`, and tap the mix to /tmp/openfps-capture.wav while it plays
#   ./run-gtk-client.sh foot       as `on`, every footstep logged as [FOOT], to /tmp/openfps-foot.log
#   ./run-gtk-client.sh fmodlog    FMOD's logging build (libfmodL.so); `fmodlog all` adds its trace
#   ./run-gtk-client.sh noecho     as `on`, with engine reflections OFF
#   ./run-gtk-client.sh nophys     as `on`, with machines and aircraft OFF
#   ./run-gtk-client.sh quiet      both of the above off
#   ./run-gtk-client.sh bare       everything switchable off: FMOD panning of positioned sounds only
#   ./run-gtk-client.sh nohrtf     just the Steam Audio binaural stage off
#   ./run-gtk-client.sh nosplit    every machine on one voice close up (no front outlet), mix captured
#
# noecho, nophys, quiet, bare and nohrtf exist to bisect a fault: take a part of the audio path out
# and see whether the fault goes with it.
#
# `on` and `off` are the two halves of one A/B: walk the same route twice and the two logs sit side by
# side afterwards, which is why each names its own file instead of overwriting one. The trace is opt-in
# because it is not free; a normal run keeps the plain log.
set -e

REPO="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
ART=/tmp/openfps-gtk

# RELEASE by default, and it is not a detail. A vehicle's engine is synthesized sample by sample
# inside the FMOD mixer callback; measured with `--engine-cost`, one V8 renders about 9 seconds of
# audio per second of a core in release and about 3 in debug. Four cars is half a core built one way
# and more than a whole core built the other, and a mixer callback that misses its deadline is not
# slow, it is silence. CONFIG=Debug for a debugging session, and expect fewer cars.
CONFIG="${CONFIG:-Release}"
LOWER=$(echo "$CONFIG" | tr '[:upper:]' '[:lower:]')
OUT="$ART/bin/OpenFPS.Client.Gtk/$LOWER"

LOG=/tmp/openfps-client.log
MODE=""
if [ $# -gt 0 ]; then
  case "$1" in
    on)
      MODE="Steam Audio simulation ON, acoustic trace on"
      export OPENFPS_AUDIO_DEBUG=1
      LOG=/tmp/openfps-sa-on.log
      shift ;;
    off)
      MODE="Steam Audio simulation OFF (hand-rolled ray-tracer), acoustic trace on"
      export OPENFPS_STEAMAUDIO_SIM=0 OPENFPS_AUDIO_DEBUG=1
      LOG=/tmp/openfps-sa-off.log
      shift ;;
    foot)
      # Every footstep the client submits, with its material and position, so a place where the
      # steps go missing can be read back instead of described.
      export OPENFPS_AUDIO_DEBUG=1
      MODE="FOOTSTEP TRACE — every step logged as [FOOT] in the client log"
      LOG=/tmp/openfps-foot.log
      ;;
    fmodlog)
      # ── FMOD'S OWN LOGGING BUILD ────────────────────────────────────────────────────────────────
      #
      # libfmodL.so validates every call and NAMES what is wrong: a stale handle, an object still
      # connected, the wrong thread. A core file only gives a fault address.
      #
      # It is a drop-in replacement, so the swap is a file copy — the binding does [DllImport("fmod")]
      # and takes whatever libfmod.so is next to the executable. The original is put back on exit,
      # including on a crash, because a client left running the logging build is a slower client that
      # nobody remembers to change back.
      #
      # `fmodlog all` adds FMOD's own trace, which is enormous. Start without it.
      MODE="FMOD LOGGING BUILD — libfmodL.so; FMOD's own errors and warnings to /tmp/fmod-debug.log"
      export OPENFPS_AUDIO_DEBUG=1
      export OPENFPS_FMOD_DEBUG="${2:-errors}"
      export OPENFPS_FMOD_DEBUG_FILE=/tmp/fmod-debug.log
      : > "$OPENFPS_FMOD_DEBUG_FILE"
      FMODL=$(find "$HOME/Downloads" -path "*x86_64*" -name libfmodL.so 2>/dev/null | head -1)
      if [ -z "$FMODL" ]; then
        echo "!! libfmodL.so not found under ~/Downloads — cannot run the logging build." >&2
        exit 1
      fi
      SWAPPED="$OUT/libfmod.so"
      LOG=/tmp/openfps-fmodlog.log
      shift; [ $# -gt 0 ] && shift ;;
    bare)
      # Everything switchable, switched off: no machines, no aircraft, no engine reflections, no
      # Steam Audio simulator, no HRTF. What is left is FMOD playing positioned sounds.
      #
      # If a fault survives this, it is in the oldest part of the audio path. If it goes, bring the
      # parts back one at a time and the first one that brings it back is the answer.
      MODE="BARE: no machines/aircraft, no reflections, no Steam Audio sim, no HRTF — FMOD panning only"
      export OPENFPS_AUDIO_DEBUG=1 OPENFPS_MACHINE_VOICES=0 OPENFPS_ENGINE_ECHOES=0 \
             OPENFPS_STEAMAUDIO_SIM=0 OPENFPS_HRTF=0
      LOG=/tmp/openfps-bare.log
      shift ;;
    nosplit)
      # Every machine on ONE voice at every distance: no separate front outlet close up. The A/B for
      # "things that pass close sound inside out, a little further away they are fine". Captures the
      # mix too, so the pass can be measured afterwards.
      MODE="front/rear split OFF (OPENFPS_FRONT_VOICES=0), mix captured to /tmp/openfps-capture-nosplit.wav"
      export OPENFPS_FRONT_VOICES=0 OPENFPS_AUDIO_CAPTURE=/tmp/openfps-capture-nosplit.wav
      LOG=/tmp/openfps-nosplit.log
      shift ;;
    nohrtf)
      # Just the binaural stage out, everything else as normal.
      MODE="Steam Audio binaural OFF (OPENFPS_HRTF=0) — FMOD panning; everything else normal"
      export OPENFPS_AUDIO_DEBUG=1 OPENFPS_HRTF=0
      LOG=/tmp/openfps-nohrtf.log
      shift ;;
    nophys)
      # Every machine and aircraft voice out: no air conditioners, no mowers, no aeroplanes.
      # Everything else is untouched, so the city is still walkable and the traffic still runs.
      MODE="physical voices OFF (OPENFPS_MACHINE_VOICES=0) — no machines, no aircraft"
      export OPENFPS_AUDIO_DEBUG=1 OPENFPS_MACHINE_VOICES=0
      LOG=/tmp/openfps-nophys.log
      shift ;;
    quiet)
      # Machines, aircraft and engine reflections all off.
      MODE="physical voices AND engine reflections OFF"
      export OPENFPS_AUDIO_DEBUG=1 OPENFPS_MACHINE_VOICES=0 OPENFPS_ENGINE_ECHOES=0
      LOG=/tmp/openfps-quiet.log
      shift ;;
    noecho)
      # Engine reflections off. They are the highest-churn object in the audio system and the only
      # one holding a reference into another voice's ring buffer. A mode rather than a bare
      # environment variable, because a variable you have to remember is one that does not get set.
      MODE="engine reflections OFF (OPENFPS_ENGINE_ECHOES=0) — walls stop answering cars"
      export OPENFPS_AUDIO_DEBUG=1 OPENFPS_ENGINE_ECHOES=0
      LOG=/tmp/openfps-noecho.log
      shift ;;
    capture)
      # The mix, tapped to a WAV, while it keeps playing out of the speakers. For anything that can
      # only be found by ear: a click, a crackle, a dropout. "It pops" cannot be reasoned about from a
      # log, and the difference between a step discontinuity, a clipped peak and a starved buffer is
      # obvious in the samples and invisible from the chair. Walk until it happens, then quit.
      MODE="Steam Audio simulation ON, acoustic trace on, mix captured to /tmp/openfps-capture.wav"
      export OPENFPS_AUDIO_DEBUG=1 OPENFPS_AUDIO_CAPTURE=/tmp/openfps-capture.wav
      LOG=/tmp/openfps-sa-on.log
      shift ;;
  esac
fi

echo "Building GTK client ($CONFIG) to tmpfs ($ART) ..."
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" build "$REPO/OpenFPS.Client.Gtk" -c "$CONFIG" --artifacts-path "$ART" \
  -nodeReuse:false -p:UseSharedCompilation=false -v minimal

# ── Door sounds ready before the first door ─────────────────────────────────────────────────────
#
# A door's sound is a simulation that takes seconds to render (a glass door up to forty), and a door
# opened before its render is ready is silent. The Windows zip ships every door render the client
# makes at start (publish-windows.sh); here they go into the player's own render cache, rendered once
# on every core and then kept. The cache is named by the door models' own fingerprint
# (DoorRenderCache.Name: a hash of the door model sources, OpenFPS.Sound.csproj DoorModelSource, and
# the cache file version), so it survives every change that does not touch a door model.
DOORHASH=$(grep -ho '"[0-9a-f]\{12\}"' "$ART"/obj/OpenFPS.Sound/"$LOWER"/DoorModelFingerprint.g.cs 2>/dev/null | head -1 | tr -d '"')
DOORVER=$(grep -o 'const int Version = [0-9]*' "$REPO/OpenFPS.Client.Core/AudioEngine/Core/DoorRenderCache.cs" | grep -o '[0-9]*$')
DOORS="$DOORHASH-v$DOORVER"
if [ "${OPENFPS_RENDER_CACHE:-}" != "off" ] && [ -n "$DOORHASH" ] && [ -n "$DOORVER" ]; then
  RCACHE="${OPENFPS_RENDER_CACHE:-${XDG_DATA_HOME:-$HOME/.local/share}/OpenFPS/rendercache/$DOORS}"
  if [ -e "$RCACHE/.prerendered" ]; then
    echo "Door sounds: kept from an earlier launch ($DOORS)."
  else
    echo "Door sounds for these door models ($DOORS) are not rendered yet; rendering them once now (about a minute) ..."
    LAB_ART=/tmp/openfps-lab
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
      "$DOTNET" build "$REPO/OpenFPS.AudioLab" -c Release --artifacts-path "$LAB_ART" \
      -nodeReuse:false -p:UseSharedCompilation=false -v minimal
    LAB_DOORHASH=$(grep -ho '"[0-9a-f]\{12\}"' "$LAB_ART"/obj/OpenFPS.Sound/release/DoorModelFingerprint.g.cs 2>/dev/null | head -1 | tr -d '"')
    if [ "$LAB_DOORHASH" != "$DOORHASH" ]; then
      echo "  WARNING: the lab's door models ($LAB_DOORHASH) are not the client's ($DOORHASH); skipping, doors render as you play."
    elif (cd "$LAB_ART/bin/OpenFPS.AudioLab/release" && nice ./OpenFPS.AudioLab --prerender-doors "out=$RCACHE"); then
      touch "$RCACHE/.prerendered"
    else
      echo "  WARNING: some door renders failed; the rest are kept, and the missing ones render as you play."
    fi
  fi
fi

echo "Launching GTK client from $OUT ..."
[ -n "$MODE" ] && echo "  mode: $MODE"
cd "$OUT"                 # cwd so machines/ (loaded relative to cwd) resolves

# ── If it dies, leave something to read ──────────────────────────────────────────────────────────
#
# A native crash inside FMOD, Steam Audio or the runtime prints NOTHING into the log: no managed
# exception, no stack, the file simply stops mid-line. That is indistinguishable from "the client
# froze" and from "it quit", which is three different faults wearing one face, and diagnosing it from
# the chair is guesswork.
#
# So the runtime is told to write a dump on the way down, and the exit status is recorded after the
# pipeline — signal deaths come back as 128+N, which is the difference between a segfault (139) and a
# clean quit (0) stated in one number.
# NOT ON /tmp. It is a tmpfs, it is where every build and every log already lives, and a dump is
# hundreds of megabytes: a full /tmp means "No space left on device" and the stack is lost. The dump
# goes on real disk, which has room.
CRASHDIR="${OPENFPS_CRASHDIR:-$HOME/openfps-crashes}"
mkdir -p "$CRASHDIR"
# NO raw core file. The runtime's minidump above is the readable one and it goes to real disk; a
# core as well is a second copy of the same crash, one to two GIGABYTES of it, written into the
# client's working directory, which is on the tmpfs.
ulimit -c 0 2>/dev/null || true
export DOTNET_DbgEnableMiniDump=1
export DOTNET_DbgMiniDumpType=2                 # heap: big enough to hold the stacks, small enough to write
export DOTNET_DbgMiniDumpName="$CRASHDIR/openfps-crash.%d.dmp"

# ...and the log is on a tmpfs too. With the acoustic trace on, a city puts out about two megabytes a
# minute — one line per reverb bus per second, and there are a lot of buses — so a long listen can
# fill the same 8 GB the builds are in, and everything on it starts failing at once. Say what is left
# before starting, because "it crashed" and "the disk filled" look identical from the chair.
FREE_MB=$(df -Pm /tmp | awk 'NR==2 {print $4}')
echo "  /tmp has ${FREE_MB} MB free (the log and the builds live there)"
if [ "$FREE_MB" -lt 512 ]; then
  echo "  WARNING: under 512 MB. Clear old builds in /tmp before a long session."
fi

# Mirror all console output to a log file so it can be inspected after the session (handy for
# screen-reader users).
# TWO FILES, and they hold different things.
#
#   $LOG        everything on stdout and stderr — the client's own lines PLUS the native ones that
#               never go through Serilog: [SAWORKER], [SASUMMARY], and [createdump]'s account of a
#               crash. Written by the tee below, so it stops if the terminal does.
#   $CLIENTLOG  the client's own log, written by the CLIENT. Survives the terminal going away,
#               which is the point: closing the console does not lose the client's log.
CLIENTLOG="${LOG%.log}-client.log"
# One run per file, the run before kept beside it. The client APPENDS, and Serilog stops writing a
# file at 1 GB without a word: /tmp/openfps-sa-on-client.log reached that on 2026-10-04 at 19:36
# and every run after it, the 2026-10-05 session included, logged nothing at all.
if [ -e "$CLIENTLOG" ]; then mv -f "$CLIENTLOG" "${CLIENTLOG%.log}.prev.log"; fi
export OPENFPS_LOG="$CLIENTLOG"
echo "(logging to $LOG; the client also writes its own to $CLIENTLOG)"
START_EPOCH=$(date +%s)

# The logging build goes in, and comes back out however this ends — including a SIGSEGV, which is the
# case it exists for. A trap, not a line after the run, because a line after the run does not execute
# when the shell is killed.
if [ -n "${SWAPPED:-}" ]; then
  cp -f "$SWAPPED" "$SWAPPED.orig"
  trap 'mv -f "$SWAPPED.orig" "$SWAPPED" 2>/dev/null' EXIT INT TERM
  cp -f "$FMODL" "$SWAPPED"
  echo "(FMOD logging build in place: $(basename "$FMODL") -> $(basename "$SWAPPED"); FMOD's own log goes to $OPENFPS_FMOD_DEBUG_FILE)"
fi

set +e
"$DOTNET" OpenFPS.Client.Gtk.dll "$@" 2>&1 | tee "$LOG"
STATUS=${PIPESTATUS[0]}
set -e
# ALWAYS, not only on failure. A client that exits 0 and one that is killed look identical from the
# chair (the sound stops), so the clean case is stated too.
{
    echo ""
    if [ -s "${OPENFPS_FMOD_DEBUG_FILE:-/nonexistent}" ]; then
        echo "=== WHAT FMOD ITSELF SAID (last 25 lines of $OPENFPS_FMOD_DEBUG_FILE) ==="
        tail -25 "$OPENFPS_FMOD_DEBUG_FILE" | sed 's/^/    /'
        echo ""
    fi
    echo "=== CLIENT EXITED $STATUS ==="
    case "$STATUS" in
      0)   echo "    a CLEAN exit. Nothing crashed; the client was asked to stop." ;;
      139) echo "    killed by signal 11 (SIGSEGV) — a NATIVE crash, not a managed exception." ;;
      137) echo "    killed by signal 9 (SIGKILL) — something outside the client stopped it (OOM killer?)." ;;
      134) echo "    killed by signal 6 (SIGABRT) — the runtime gave up." ;;
      *)   if [ "$STATUS" -gt 128 ]; then
             echo "    killed by signal $((STATUS - 128)) — a NATIVE crash, not a managed exception."
           else
             echo "    exited with status $STATUS."
           fi ;;
    esac
    echo "    ran from $(date -d "@$START_EPOCH" '+%H:%M:%S' 2>/dev/null) for $(( $(date +%s) - START_EPOCH ))s"
    ls -la "$CRASHDIR"/openfps-crash.*.dmp 2>/dev/null && echo "    dump(s) above; read with: python3 tools/read_core_fault.py <file> <crashing tid hex>"
    echo "    /tmp now has $(df -Pm /tmp | awk 'NR==2 {print $4}') MB free"
} | tee -a "$LOG"
exit "$STATUS"
