#!/usr/bin/env bash
# Build + run the OpenFPS server WITHOUT hitting the ntfs3 build hang.
#
# Builds with --artifacts-path on tmpfs (a plain `dotnet run` writes obj/bin onto the ntfs3 volume and
# hangs), then runs the prebuilt DLL with the working directory set to OpenFPS.Server so it finds its
# data (maps/, prefabs/, openfps.db, users.json — all resolved relative to cwd).
#
# Usage:
#   ./run-server.sh              speedway — the map claiming IsDefault (UDP 33288; Ctrl-C to stop)
#   ./run-server.sh rooms        the rooms/doors/material-lab map (maps/default.json)
#   ./run-server.sh speedway     the speedway, named explicitly
#   ./run-server.sh <map-id>     any other map id in maps/
#   ./run-server.sh --port 34288 ... and anything else goes straight to the server
#
# There is NO runtime map change — the map a player lands on is the only map that session can ever be
# on, which is why the landing map is chosen HERE and not in the client. F6 in the client lists the
# maps; it cannot move you to one.
set -e

REPO="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
ART=/tmp/openfps-srv
OUT="$ART/bin/OpenFPS.Server/debug"

# A bare first word that is not a flag is a map name. "rooms" is spelled out because the map's id is
# "default", which reads as "the usual one" and is the opposite of what it means now that the speedway
# is what you land on.
MAPARGS=()
if [ $# -gt 0 ] && [[ "$1" != -* ]]; then
  case "$1" in
    rooms|demo) MAPARGS=(--map default) ;;
    *)          MAPARGS=(--map "$1") ;;
  esac
  shift
fi

echo "Building server to tmpfs ($ART) ..."
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" build "$REPO/OpenFPS.Server" --artifacts-path "$ART" \
  -nodeReuse:false -p:UseSharedCompilation=false -v minimal

if [ ${#MAPARGS[@]} -gt 0 ]; then echo "Launching server on map '${MAPARGS[1]}' (cwd=OpenFPS.Server, port 33288) ..."
else echo "Launching server (cwd=OpenFPS.Server, port 33288) ..."; fi
cd "$REPO/OpenFPS.Server"     # so maps/, prefabs/, openfps.db, users.json resolve
exec "$DOTNET" "$OUT/OpenFPS.Server.dll" "${MAPARGS[@]}" "$@"
