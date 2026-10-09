#!/usr/bin/env bash
# Builds the server as a self-contained folder to copy to a VPS, with the data it reads from its
# working folder (maps, prefabs, composites, machines, the message of the day).
#
#   ./publish-server.sh                 -> dist/openfps-server-linux-x64-<build>.tar.gz
#   ./publish-server.sh win-x64         -> the same for a Windows server
#
# Deliberately NOT included: openfps.db (accounts) and friends.json. The VPS keeps its
# own accounts; overwriting them on every update would wipe everyone's. Unpack an update over the
# old folder and they stay.
#
# <build> must match the client's: see publish-windows.sh.
set -e

REPO="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
RID="${1:-linux-x64}"
# Not /tmp: that is 8 GB of RAM shared with every other build and log, and this output (the
# assets twice, plus the zip) filled it and broke the GTK build. Not the repo either: MSBuild's
# output phase hangs on the ntfs3 volume. The home volume has neither problem.
ART="${OPENFPS_PUBLISH_DIR:-$HOME/.cache/openfps-publish}/server"
OUT="$ART/publish/OpenFPS.Server/release_$RID"

rm -rf "$OUT" "$ART/obj/OpenFPS.Common"   # one WireContract.g.cs, so the name is this build's
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" publish "$REPO/OpenFPS.Server/OpenFPS.Server.csproj" -c Release -r "$RID" --self-contained true \
  --artifacts-path "$ART" -nodeReuse:false -p:UseSharedCompilation=false -v minimal

for d in maps prefabs composites machines; do cp -r "$REPO/OpenFPS.Server/$d" "$OUT/"; done
# Maps players made on this machine (/map new) stay here: the server's own are on the server.
rm -rf "$OUT/maps/players"
# Nor the world editor's overlays from this machine: unpacked over the VPS they would replace the edits
# made there (model_versions/ is in the working folder, never copied).
rm -rf "$OUT/maps/overlays"
cp "$REPO/OpenFPS.Server/motd.txt" "$OUT/"
# The places in the world to arrive at. Not the world's tiles (world/): each server makes its own.
cp "$REPO/OpenFPS.Server/world_places.json" "$OUT/"
BUILD=$(grep -rho '"[0-9a-f]\{12\}"' "$ART"/obj/OpenFPS.Common/*/WireContract.g.cs | head -1 | tr -d '"')
echo "$BUILD" > "$OUT/BUILD.txt"

mkdir -p "$REPO/dist"
TGZ="$REPO/dist/openfps-server-$RID-$BUILD.tar.gz"
tar -C "$ART/publish/OpenFPS.Server" -czf "$TGZ" --transform "s|^release_$RID|openfps-server|" "release_$RID"
echo
echo "Server, build $BUILD: $TGZ ($(du -h "$TGZ" | cut -f1))"
echo "See docs/WINDOWS_AND_SERVER.md for setting it up on the VPS."
