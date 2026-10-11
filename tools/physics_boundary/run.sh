#!/usr/bin/env bash
# The physics boundary survey (docs/PHYSICS_BOUNDARY.md), from scratch.
#
#   tools/physics_boundary/run.sh [out-dir]
#
# Builds the sound boundary's Roslyn reader (tools/sound_boundary: the same reader, one parse of every
# project) on tmpfs with tools/build-local.sh (the repo is on ntfs3: never build in place), reads every
# project's sources, then sorts the physics inventory and writes physics_tables.md and
# physics_crossings.tsv into out-dir (default /tmp/openfps-physurvey/out). Nothing in the repository is
# built or changed. Delete the artifacts directory afterwards.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
art="${SURVEY_ARTIFACTS:-/tmp/openfps-physurvey}"
out="${1:-$art/out}"
mkdir -p "$out"
"$repo/tools/build-local.sh" "$art" "$repo/tools/sound_boundary/SoundBoundary.csproj" -v:q
taskset -c "$(( $(nproc) - 12 ))-$(( $(nproc) - 1 ))" nice -n 10 \
    ~/.dotnet/dotnet "$art/bin/SoundBoundary/debug/SoundBoundary.dll" "$repo" "$out"
python3 -I "$here/classify.py" "$out" "$out"
echo "tables: $out/physics_tables.md  crossings: $out/physics_crossings.tsv"
