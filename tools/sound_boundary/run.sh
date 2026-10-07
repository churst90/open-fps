#!/usr/bin/env bash
# The sound library boundary survey (docs/SOUND_LIBRARY_BOUNDARY.md), from scratch.
#
#   tools/sound_boundary/run.sh [out-dir]
#
# Builds the Roslyn reader on tmpfs (the repo is on ntfs3: never build in place), reads every project's
# sources, then sorts the types and writes tables.md and crossings.tsv into out-dir
# (default /tmp/openfps-wt-survey/out). Nothing in the repository is built or changed.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
art="${SURVEY_ARTIFACTS:-/tmp/openfps-wt-survey}"
out="${1:-$art/out}"
mkdir -p "$out"
DOTNET_CLI_USE_MSBUILD_SERVER=0 ~/.dotnet/dotnet build "$here/SoundBoundary.csproj" --artifacts-path "$art" \
    -nodeReuse:false -p:UseSharedCompilation=false -v:q
~/.dotnet/dotnet "$art/bin/SoundBoundary/debug/SoundBoundary.dll" "$repo" "$out"
python3 "$here/classify.py" "$out" "$out"
echo "tables: $out/tables.md  crossings: $out/crossings.tsv"
