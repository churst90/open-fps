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

# ── Is one already running? ─────────────────────────────────────────────────────────────────────
#
# This is the trap that cost a whole session. The CLIENT script rebuilds every launch, so client
# changes always take; the SERVER is long-running, and a server left up overnight goes on serving
# the map and the presets it read when it started. A day's worth of work — new vehicles, new
# levels, bus stops, platforms — was all in files a process from yesterday had no reason to re-read,
# and the report was "nothing seems to be different on the city map", which was exactly true.
#
# Worse, the failure is quiet: a second server binds nothing, logs "Address already in use" in the
# middle of a successful-looking startup — maps load, vehicles spawn, every line reads correctly —
# and then sits there serving no one. Two smoke tests were read as passing on that output.
#
# So: say so, loudly, and stop. There is no case where silently starting a second one is wanted.
PORT="${OPENFPS_PORT:-33288}"
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
  echo "   (or run this one elsewhere: OPENFPS_PORT=33289 ./run-server.sh $* --port 33289)" >&2
  exit 1
fi

echo "Building server to tmpfs ($ART) ..."
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" build "$REPO/OpenFPS.Server" --artifacts-path "$ART" \
  -nodeReuse:false -p:UseSharedCompilation=false -v minimal

if [ ${#MAPARGS[@]} -gt 0 ]; then echo "Launching server on map '${MAPARGS[1]}' (cwd=OpenFPS.Server, port 33288) ..."
else echo "Launching server (cwd=OpenFPS.Server, port 33288) ..."; fi
cd "$REPO/OpenFPS.Server"     # so maps/, prefabs/, openfps.db, users.json resolve
exec "$DOTNET" "$OUT/OpenFPS.Server.dll" "${MAPARGS[@]}" "$@"
