#!/usr/bin/env bash
# The one way to run OpenFPS.Tests on this machine (2026-10-07). Several test runs at once (agents in
# parallel) took the 24-core desk machine to a load of 350 and froze the desktop and the screen reader.
#
#   tools/test-local.sh ARTIFACTS_DIR "FILTER" [more dotnet test args]
#   tools/test-local.sh /tmp/openfps-build "FullyQualifiedName~FireTests|FullyQualifiedName~Waves"
#
# - Takes a machine-wide lock: a second run waits until the first has finished, whoever started it.
# - Waits for the load to fall under 16 before starting.
# - Runs at low priority, Category!=Timing, four test classes at once, pinned to the last twelve
#   cores (half the machine) and told it has twelve. The tests fan out on their own, so capping
#   classes alone still reached 2,171 % CPU.
# - Refuses to run without a filter: the whole suite runs on GitHub Actions, not here.
set -euo pipefail
ART="${1:?artifacts dir}"; FILTER="${2:?a --filter expression: the full suite runs on GitHub, not here}"; shift 2
# The checkout the tests come from: the current directory (an agent's worktree), or OPENFPS_REPO.
REPO="${OPENFPS_REPO:-$PWD}"
[ -d "$REPO/OpenFPS.Tests" ] || { echo "Run from a checkout's root (no OpenFPS.Tests in $REPO)." >&2; exit 2; }
exec 9>/tmp/openfps-tests.lock
echo "Waiting for the test lock..." >&2
flock 9
while [ "$(cut -d' ' -f1 /proc/loadavg | cut -d. -f1)" -ge 16 ]; do sleep 10; done
CORES=12
LAST=$(( $(nproc) - 1 )); FIRST=$(( LAST - CORES + 1 )); [ "$FIRST" -lt 0 ] && FIRST=0
DOTNET_PROCESSOR_COUNT=$CORES DOTNET_CLI_USE_MSBUILD_SERVER=0 taskset -c "$FIRST-$LAST" nice -n 10 "$HOME/.dotnet/dotnet" test "$REPO/OpenFPS.Tests" --artifacts-path "$ART" --no-build \
  --filter "Category!=Timing&($FILTER)" "$@" -- xUnit.MaxParallelThreads=4 RunConfiguration.MaxCpuCount=1
