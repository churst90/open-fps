#!/usr/bin/env bash
# Build + run the OpenFPS server WITHOUT hitting the ntfs3 build hang.
#
# Builds with --artifacts-path on tmpfs (a plain `dotnet run` writes obj/bin onto the ntfs3 volume and
# hangs), then runs the prebuilt DLL with the working directory set to OpenFPS.Server so it finds its
# data (maps/, prefabs/, openfps.db — all resolved relative to cwd).
#
# Usage:
#   ./run-server.sh              speedway — the map claiming IsDefault (UDP 33288; Ctrl-C to stop)
#   ./run-server.sh rooms        the rooms/doors/material-lab map (maps/default.json)
#   ./run-server.sh speedway     the speedway, named explicitly
#   ./run-server.sh <map-id>     any other map id in maps/
#   ./run-server.sh --port 34288 ... and anything else goes straight to the server
#
# The map named here is the landing map: where every player arrives at login. Players move to another
# map in play with F6 or /join.
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

# ── Is one already running? ─────────────────────────────────────────────────────────────────────
#
# The client script rebuilds on every launch, so client changes always take. A server is
# long-running, and one left up keeps serving the maps and presets it read when it started.
#
# And the failure is quiet: a second server on the same port binds nothing, logs "Address already in
# use" in the middle of a normal-looking startup (maps load, vehicles spawn) and then serves no one.
# So say so, loudly, and stop. Starting a second one silently is never wanted.
#
# The port is --port if given, else OPENFPS_PORT, else 33288. OPENFPS_PORT alone is passed on to
# the server as --port.
PORT=""
for ((i = 1; i <= $#; i++)); do
  if [ "${!i}" = "--port" ]; then j=$((i + 1)); PORT="${!j}"; fi
done
if [ -z "$PORT" ]; then
  PORT="${OPENFPS_PORT:-33288}"
  [ -n "${OPENFPS_PORT:-}" ] && set -- "$@" --port "$PORT"
fi
EXISTING="$(ss -lunp 2>/dev/null | grep -E "[:.]${PORT}\b" | head -1)"
if [ -n "$EXISTING" ]; then
  OLDPID="$(printf '%s' "$EXISTING" | grep -oE 'pid=[0-9]+' | head -1 | cut -d= -f2)"
  echo "!! A server is ALREADY running on UDP $PORT${OLDPID:+ (pid $OLDPID)}." >&2
  if [ -n "$OLDPID" ]; then
    STARTED="$(ps -o lstart= -p "$OLDPID" 2>/dev/null | sed 's/^ *//')"
    [ -n "$STARTED" ] && echo "   It started $STARTED and has the map it read THEN." >&2
  fi
  echo "   Starting another would bind nothing and serve nobody, which looks like success." >&2
  echo "   Stop it first:  kill ${OLDPID:-<pid>}" >&2
  echo "   (or run this one elsewhere: ./run-server.sh $* --port 33289)" >&2
  exit 1
fi

echo "Building server to tmpfs ($ART) ..."
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" build "$REPO/OpenFPS.Server" --artifacts-path "$ART" \
  -nodeReuse:false -p:UseSharedCompilation=false -v minimal

if [ ${#MAPARGS[@]} -gt 0 ]; then echo "Launching server on map '${MAPARGS[1]}' (cwd=OpenFPS.Server, port $PORT) ..."
else echo "Launching server (cwd=OpenFPS.Server, port $PORT) ..."; fi
cd "$REPO/OpenFPS.Server"     # so maps/, prefabs/, openfps.db resolve
exec "$DOTNET" "$OUT/OpenFPS.Server.dll" "${MAPARGS[@]}" "$@"
