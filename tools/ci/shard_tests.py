#!/usr/bin/env python3
"""Picks one shard of the test suite for a CI runner.

Reads `dotnet test --list-tests` output on stdin and prints a `--filter` expression naming the test
classes of shard INDEX out of COUNT. Classes are kept whole (they share fixtures) and dealt out by
their number of tests, largest first, each to the lightest shard so far, so the shards come out
about even. Every class lands in exactly one shard, so the shards together are the whole suite.

    dotnet test ... --list-tests | python3 tools/ci/shard_tests.py 3 8
"""
import sys


def classes(lines):
    counts = {}
    for line in lines:
        line = line.strip()
        if not line.startswith("OpenFPS."):
            continue
        name = line.split("(", 1)[0]          # a theory's arguments may hold dots
        cls = name.rsplit(".", 1)[0]
        counts[cls] = counts.get(cls, 0) + 1
    return counts


def shard(counts, index, count):
    loads = [0] * count
    mine = []
    for cls, n in sorted(counts.items(), key=lambda kv: (-kv[1], kv[0])):
        lightest = min(range(count), key=lambda i: (loads[i], i))
        loads[lightest] += n
        if lightest == index:
            mine.append(cls)
    return mine


def main():
    index, count = int(sys.argv[1]), int(sys.argv[2])
    mine = shard(classes(sys.stdin), index, count)
    if not mine:
        sys.exit(f"shard {index} of {count} has no test classes")
    # The trailing dot keeps DoorTests from also matching DoorTestsElsewhere.
    print("|".join(f"FullyQualifiedName~{cls}." for cls in sorted(mine)))


if __name__ == "__main__":
    main()
