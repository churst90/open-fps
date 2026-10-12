#!/usr/bin/env bash
# The engine packages survey (docs/ENGINE_PACKAGES.md), from scratch.
#
#   SURVEY_ARTIFACTS=~/.cache/openfps-build/pkgsurvey/art tools/engine_packages/run.sh [out-dir]
#
# Builds the sound boundary's Roslyn reader (tools/sound_boundary: the same reader, one parse of every
# project) with tools/build-local.sh (never into the checkout), reads every project's sources, then sorts
# every file and type into a proposed package and writes packages_tables.md, packages_files.tsv and
# packages_crossings.tsv into out-dir (default $SURVEY_ARTIFACTS/out). Nothing in the repository is built
# or changed. Set SURVEY_SKIP_READER=1 to sort an earlier run's tables again.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
art="${SURVEY_ARTIFACTS:-$HOME/.cache/openfps-build/pkgsurvey/art}"
out="${1:-$art/out}"
mkdir -p "$out"
if [ "${SURVEY_SKIP_READER:-0}" != 1 ]; then
    "$repo/tools/build-local.sh" "$art" "$repo/tools/sound_boundary/SoundBoundary.csproj" -v:q
    taskset -c "$(( $(nproc) - 12 ))-$(( $(nproc) - 1 ))" nice -n 10 \
        "$HOME/.dotnet/dotnet" "$art/bin/SoundBoundary/debug/SoundBoundary.dll" "$repo" "$out"
fi
python3 -I "$here/classify.py" "$repo" "$out" "$out"
echo "tables: $out/packages_tables.md  files: $out/packages_files.tsv  crossings: $out/packages_crossings.tsv"
