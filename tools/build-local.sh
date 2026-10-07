#!/usr/bin/env bash
# The one way to build on this machine (2026-10-07): pinned to the last six cores and at low priority, so a
# build beside Cody's desktop and screen reader never takes the whole machine. The repo is on ntfs3, so the
# output goes to tmpfs (--artifacts-path) and `dotnet run` is never used.
#
#   tools/build-local.sh ARTIFACTS_DIR PROJECT [more dotnet build args]
#   tools/build-local.sh /tmp/openfps-build OpenFPS.Tests
set -euo pipefail
ART="${1:?artifacts dir}"; PROJ="${2:?project}"; shift 2
CORES=6
LAST=$(( $(nproc) - 1 )); FIRST=$(( LAST - CORES + 1 )); [ "$FIRST" -lt 0 ] && FIRST=0
DOTNET_PROCESSOR_COUNT=$CORES DOTNET_CLI_USE_MSBUILD_SERVER=0 taskset -c "$FIRST-$LAST" nice -n 10 \
  "$HOME/.dotnet/dotnet" build "$PROJ" --artifacts-path "$ART" -nodeReuse:false -p:UseSharedCompilation=false -m:$CORES "$@"
