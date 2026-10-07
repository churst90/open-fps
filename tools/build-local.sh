#!/usr/bin/env bash
# The one way to build on this machine. Pinned to the last twelve cores (half the machine) at low
# priority, so Cody's desktop and screen reader keep the rest. Output goes to tmpfs: the repo is on
# ntfs3, where MSBuild's output step hangs.
#
#   tools/build-local.sh ARTIFACTS_DIR PROJECT [more dotnet build args]
#   tools/build-local.sh /tmp/openfps-build OpenFPS.Tests
set -euo pipefail
ART="${1:?artifacts dir}"; PROJ="${2:?project}"; shift 2
CORES=12
LAST=$(( $(nproc) - 1 )); FIRST=$(( LAST - CORES + 1 )); [ "$FIRST" -lt 0 ] && FIRST=0
DOTNET_PROCESSOR_COUNT=$CORES DOTNET_CLI_USE_MSBUILD_SERVER=0 taskset -c "$FIRST-$LAST" nice -n 10 \
  "$HOME/.dotnet/dotnet" build "$PROJ" --artifacts-path "$ART" -nodeReuse:false -p:UseSharedCompilation=false -m:$CORES "$@"
