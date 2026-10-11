#!/usr/bin/env bash
# The one way to run OpenFPS.Tests on this machine (2026-10-07). Several test runs at once (agents in
# parallel) took the 24-core desk machine to a load of 350 and froze the desktop and the screen reader.
#
#   tools/test-local.sh ARTIFACTS_DIR "FILTER" [more dotnet test args]
#   tools/test-local.sh /tmp/openfps-build "FullyQualifiedName~FireTests|FullyQualifiedName~Waves"
#
# - Takes one of two machine-wide slots: a third run waits until one has finished, whoever started it.
# - Waits for the load to fall under 24 before starting.
# - Runs at low priority, Category!=Timing, four test classes at once, pinned to the last twelve
#   cores (half the machine) and told it has twelve. The tests fan out on their own, so capping
#   classes alone still reached 2,171 % CPU.
# - Refuses to run without a filter: the whole suite runs on GitHub Actions, not here.
set -euo pipefail
ART="${1:?artifacts dir}"; FILTER="${2:?a --filter expression: the full suite runs on GitHub, not here}"; shift 2
# The checkout the tests come from: the current directory (an agent's worktree), or OPENFPS_REPO.
REPO="${OPENFPS_REPO:-$PWD}"
[ -d "$REPO/OpenFPS.Tests" ] || { echo "Run from a checkout's root (no OpenFPS.Tests in $REPO)." >&2; exit 2; }
# Two runs at once (Cody, 2026-10-11): both on the same twelve cores, so the rest of the machine is never
# touched however many there are. Slot 1 keeps the old lock's name, so a worktree's older copy of this
# script, which knows only that one, still counts against the two.
SLOTS=(/tmp/openfps-tests.lock /tmp/openfps-tests.2.lock)
echo "Waiting for one of ${#SLOTS[@]} test slots..." >&2
while :; do
  for slot in "${SLOTS[@]}"; do
    exec 9>"$slot"
    if flock -n 9; then break 2; fi
    exec 9>&-
  done
  sleep 5
done
while [ "$(cut -d' ' -f1 /proc/loadavg | cut -d. -f1)" -ge 24 ]; do sleep 10; done
CORES=12
LAST=$(( $(nproc) - 1 )); FIRST=$(( LAST - CORES + 1 )); [ "$FIRST" -lt 0 ] && FIRST=0
DOTNET_PROCESSOR_COUNT=$CORES DOTNET_CLI_USE_MSBUILD_SERVER=0 taskset -c "$FIRST-$LAST" nice -n 10 "$HOME/.dotnet/dotnet" test "$REPO/OpenFPS.Tests" --artifacts-path "$ART" --no-build \
  --filter "Category!=Timing&($FILTER)" "$@" -- xUnit.MaxParallelThreads=4 RunConfiguration.MaxCpuCount=1
