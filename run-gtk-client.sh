#!/usr/bin/env bash
# Build + run the OpenFPS GTK client WITHOUT hitting the ntfs3 build hang.
#
# The repo lives on an ntfs3 volume whose kernel driver stalls MSBuild's output-write phase, so a plain
# `dotnet run` (which writes obj/bin onto ntfs3) hangs. This builds with --artifacts-path on tmpfs and then
# runs the prebuilt DLL directly. The build copies ASSETS, materials.json and the FMOD/Steam-Audio .so libs
# next to the DLL, so the tmpfs output is self-contained.
#
# Usage:
#   ./run-gtk-client.sh                 # normal run
#   OPENFPS_AUDIO_DEBUG=1 ./run-gtk-client.sh   # with per-source occlusion/EQ trace
set -e

REPO="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
ART=/tmp/openfps-gtk
OUT="$ART/bin/OpenFPS.Client.Gtk/debug"

echo "Building GTK client to tmpfs ($ART) ..."
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" build "$REPO/OpenFPS.Client.Gtk" --artifacts-path "$ART" \
  -nodeReuse:false -p:UseSharedCompilation=false -v minimal

echo "Launching GTK client from $OUT ..."
cd "$OUT"                 # cwd so materials.json (loaded relative to cwd) resolves

# Mirror all console output to a log file so it can be inspected after the session (handy for
# screen-reader users). Set OPENFPS_AUDIO_DEBUG=1 before running to add the per-source occlusion trace.
LOG=/tmp/openfps-client.log
echo "(logging to $LOG)"
"$DOTNET" OpenFPS.Client.Gtk.dll "$@" 2>&1 | tee "$LOG"
