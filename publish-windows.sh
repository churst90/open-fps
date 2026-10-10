#!/usr/bin/env bash
# Builds the Windows client as a self-contained zip, from Linux, for someone who has nothing installed.
#
#   ./publish-windows.sh            -> dist/OpenFPS-windows-<build>.zip
#
# Release, always: engines are synthesised inside the mixer callback, and a Debug build renders about
# a third as fast, which is heard as dropouts. Self-contained: the zip carries the .NET runtime.
#
# <build> is WireContract.Hash, the hash of OpenFPS.Common and the library it is built on (Geometry,
# Acoustics, Sound but its synthesis) that the server checks at login. A zip only talks to a server
# built from the same sources, so rebuild this whenever the server is updated, and the name says which
# server it is for.
set -e

REPO="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
# Not /tmp: that is 8 GB of RAM shared with every other build and log, and this output (the
# assets twice, plus the zip) filled it and broke the GTK build. Not the repo either: MSBuild's
# output phase hangs on the ntfs3 volume. The home volume has neither problem.
ART="${OPENFPS_PUBLISH_DIR:-$HOME/.cache/openfps-publish}/win"
OUT="$ART/publish/OpenFPS.Client/release_win-x64"

for f in fmod.dll fmodstudio.dll phonon.dll nvdaControllerClient64.dll; do
  [ -f "$REPO/lib/$f" ] || { echo "!! lib/$f is missing; the zip would not start." >&2; exit 1; }
done

rm -rf "$OUT" "$ART/obj/OpenFPS.Common" "$ART/obj/OpenFPS.Sound"   # one WireContract.g.cs and one DoorModelFingerprint.g.cs, this build's
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" publish "$REPO/OpenFPS.Client/OpenFPS.Client.csproj" -c Release -r win-x64 --self-contained true \
  --artifacts-path "$ART" -nodeReuse:false -p:UseSharedCompilation=false -v minimal

BUILD=$(grep -rho '"[0-9a-f]\{12\}"' "$ART"/obj/OpenFPS.Common/*/WireContract.g.cs | head -1 | tr -d '"')

# What must be there, or the zip is not worth sending.
for f in OpenFPS.Client.exe fmod.dll phonon.dll nvdaControllerClient64.dll machines ASSETS/SOUNDS; do
  [ -e "$OUT/$f" ] || { echo "!! $f is missing from the publish output." >&2; exit 1; }
done
# Every door model sound the client renders at start, rendered here and shipped: a door's simulation
# takes seconds (a glass door up to forty), and a first hearing that is not ready is silent. The lab is
# built from the same sources, so its renders are this build's (DoorRenderCache checks the name).
LAB_ART="$ART/lab"
rm -rf "$LAB_ART/obj/OpenFPS.Common" "$LAB_ART/obj/OpenFPS.Sound"   # the same here
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  "$DOTNET" build "$REPO/OpenFPS.AudioLab/OpenFPS.AudioLab.csproj" -c Release \
  --artifacts-path "$LAB_ART" -nodeReuse:false -p:UseSharedCompilation=false -v minimal
# The renders are named by the door models' fingerprint (DoorRenderCache.Name), not the build, so a
# zip made after a change elsewhere reuses the renders already made here.
# OpenFPS.Sound writes it, beside the door models (stage 3 of docs/SOUND_LIBRARY_BOUNDARY.md).
DOORHASH=$(grep -rho '"[0-9a-f]\{12\}"' "$ART"/obj/OpenFPS.Sound/*/DoorModelFingerprint.g.cs | head -1 | tr -d '"')
LAB_DOORHASH=$(grep -rho '"[0-9a-f]\{12\}"' "$LAB_ART"/obj/OpenFPS.Sound/*/DoorModelFingerprint.g.cs | head -1 | tr -d '"')
[ -n "$DOORHASH" ] || { echo "!! no DoorModelFingerprint.g.cs under $ART/obj/OpenFPS.Sound." >&2; exit 1; }
[ "$LAB_DOORHASH" = "$DOORHASH" ] || { echo "!! the lab's door models are $LAB_DOORHASH, the client's $DOORHASH: renders would not be used." >&2; exit 1; }
DOORVER=$(grep -o 'const int Version = [0-9]*' "$REPO/OpenFPS.Client.Core/AudioEngine/Core/DoorRenderCache.cs" | grep -o '[0-9]*$')
DOORS="$DOORHASH-v$DOORVER"
CACHES="${OPENFPS_PUBLISH_DIR:-$HOME/.cache/openfps-publish}/rendercache"
find "$CACHES" -mindepth 1 -maxdepth 1 -type d ! -name "$DOORS" -exec rm -rf {} + 2>/dev/null || true
CACHE="$CACHES/$DOORS"
(cd "$LAB_ART/bin/OpenFPS.AudioLab/release" && nice ./OpenFPS.AudioLab --prerender-doors "out=$CACHE")
mkdir -p "$OUT/ASSETS/rendercache/$DOORS"
cp "$CACHE"/*.pcm "$OUT/ASSETS/rendercache/$DOORS/"

# The logging builds are for debugging here, not for players.
rm -f "$OUT/fmodL.dll" "$OUT/fmodstudioL.dll"
cp "$REPO/docs/WINDOWS_README.txt" "$OUT/README.txt"
echo "$BUILD" > "$OUT/BUILD.txt"

mkdir -p "$REPO/dist"
ZIP="$REPO/dist/OpenFPS-windows-$BUILD.zip"
rm -f "$ZIP"
STAGE="$ART/stage"
rm -rf "$STAGE"; mkdir -p "$STAGE"
ln -s "$OUT" "$STAGE/OpenFPS"
(cd "$STAGE" && zip -qr -9 "$ZIP" OpenFPS/)
echo
echo "Windows client, build $BUILD:"
echo "  $ZIP ($(du -h "$ZIP" | cut -f1))"
echo "It will log in only to a server built from the same OpenFPS.Common (build $BUILD)."
