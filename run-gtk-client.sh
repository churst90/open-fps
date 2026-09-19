#!/usr/bin/env bash
# Build + run the OpenFPS GTK client WITHOUT hitting the ntfs3 build hang.
#
# The repo lives on an ntfs3 volume whose kernel driver stalls MSBuild's output-write phase, so a plain
# `dotnet run` (which writes obj/bin onto ntfs3) hangs. This builds with --artifacts-path on tmpfs and then
# runs the prebuilt DLL directly. The build copies ASSETS, materials.json and the FMOD/Steam-Audio .so libs
# next to the DLL, so the tmpfs output is self-contained.
#
# Usage:
#   ./run-gtk-client.sh            normal run — Steam Audio simulation ON (it is ON unless turned off)
#   ./run-gtk-client.sh on         the same, plus the acoustic trace, logged to /tmp/openfps-sa-on.log
#   ./run-gtk-client.sh off        the OLD hand-rolled spatializer + trace, to /tmp/openfps-sa-off.log
#   ./run-gtk-client.sh capture    as `on`, and tap the mix to /tmp/openfps-capture.wav while it plays
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

echo "Launching GTK client from $OUT ..."
[ -n "$MODE" ] && echo "  mode: $MODE"
cd "$OUT"                 # cwd so materials.json (loaded relative to cwd) resolves

# Mirror all console output to a log file so it can be inspected after the session (handy for
# screen-reader users).
echo "(logging to $LOG)"
"$DOTNET" OpenFPS.Client.Gtk.dll "$@" 2>&1 | tee "$LOG"
