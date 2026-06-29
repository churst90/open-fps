#!/usr/bin/env bash
# Build + run the OpenFPS server WITHOUT hitting the ntfs3 build hang.
#
# Builds with --artifacts-path on tmpfs (a plain `dotnet run` writes obj/bin onto the ntfs3 volume and
# hangs), then runs the prebuilt DLL with the working directory set to OpenFPS.Server so it finds its
# data (maps/, prefabs/, openfps.db, users.json — all resolved relative to cwd).
#
# Usage:  ./run-server.sh      (listens on UDP 33288; Ctrl-C to stop)
set -e

REPO="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
ART=/tmp/openfps-srv
OUT="$ART/bin/OpenFPS.Server/debug"

echo "Building server to tmpfs ($ART) ..."
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" build "$REPO/OpenFPS.Server" --artifacts-path "$ART" \
  -nodeReuse:false -p:UseSharedCompilation=false -v minimal

echo "Launching server (cwd=OpenFPS.Server, port 33288) ..."
cd "$REPO/OpenFPS.Server"     # so maps/, prefabs/, openfps.db, users.json resolve
exec "$DOTNET" "$OUT/OpenFPS.Server.dll" "$@"
