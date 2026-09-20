#!/usr/bin/env python3
"""
Rebuilds OpenFPS.Client/ASSETS/SOUNDS/FOOTSTEPS from the walking recordings in the inbox.

WHY THIS IS A SCRIPT. The bank that was here before was forty folders deep and mostly one file each,
in three formats, with `ingest_*`, `peaks/` and bare numbers side by side, and nothing anywhere said
which recording any of it came from. A sample bank with no provenance cannot be judged: when a
footstep sounds wrong there is no way back to the thing it was cut from. So the mapping below IS the
bank — run this again after changing it, and the folder is an output.

WHAT IS IN HERE AND WHAT IS NOT. Twelve recordings of somebody WALKING, one surface each. That is
all they are: no running, no landing, no jumping. LANDING/ is a different action with its own tree
and is deliberately left alone.

The registry has twenty-two materials and this has twelve recordings, which is the normal state of
affairs and not a gap to be filled by duplicating files under new names. A material with no
recording of its own borrows the nearest one that has, and that borrowing is written down once, in
SoundMappingService.RecordedStandIn, where it can be read. It is NOT written here by copying wavs.

    tools/rebuild_footstep_bank.py [--dry-run] [--keep 48]
"""

import argparse
import os
import shutil
import subprocess
import sys

INBOX = "inbox/foot steps sounds"
OUT = "OpenFPS.Client/ASSETS/SOUNDS/FOOTSTEPS"
SPLITTER = "tools/split_footsteps.py"

# recording -> the material folder it becomes.
#
# The folder name is a material in AcousticRegistry wherever one matches, because that is the name
# the client asks for. Cement, Sand and Snow are not registry materials: Cement and Sand are here
# because they are surfaces the recordings are OF and other materials stand in on them, and Snow
# because the weather substitution asks for it by name when it is freezing and raining.
SOURCES = {
    "95.mp3":                "Sand",     # shoes on sand — the file cannot be renamed, so it is named here
    "shoes in snow.wav":     "Snow",
    "shoes on carpet.mp3":   "Carpet",
    "shoes on cement.mp3":   "Cement",
    "shoes on concrete.mp3": "Concrete",
    "shoes on dirt.mp3":     "Dirt",
    "shoes on gravel.mp3":   "Gravel",
    "shoes on leaves.mp3":   "Leaves",
    "shoes on metal.mp3":    "Metal",
    "shoes on mud.mp3":      "Mud",
    "shoes on tile.mp3":     "Tile",
    "shoes on wood.mp3":     "Wood",
}


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--keep", type=int, default=48,
                    help="most files to keep per material, spread evenly through the recording so "
                         "the approach and the walk away are both still in there")
    args = ap.parse_args()

    missing = [f for f in SOURCES if not os.path.exists(os.path.join(INBOX, f))]
    if missing:
        print(f"not in {INBOX}: {', '.join(missing)}", file=sys.stderr)
        return 1

    if not args.dry_run and os.path.isdir(OUT):
        # The whole tree, not a merge. A bank half of one thing and half of another is the state
        # this is replacing.
        shutil.rmtree(OUT)
        os.makedirs(OUT)

    total = 0
    for recording, material in sorted(SOURCES.items(), key=lambda kv: kv[1]):
        dest = os.path.join(OUT, material)
        cmd = [sys.executable, SPLITTER, os.path.join(INBOX, recording), dest,
               "--prefix", material.lower()]
        if args.dry_run:
            cmd.append("--dry-run")
        print(f"── {material:9} ← {recording}")
        r = subprocess.run(cmd, capture_output=True, text=True)
        sys.stdout.write("   " + r.stdout.replace("\n", "\n   ").rstrip() + "\n")
        if r.returncode != 0:
            sys.stderr.write(r.stderr)
            return r.returncode
        if args.dry_run:
            continue

        # Thin it, evenly through the recording. Taking the first N instead would keep only the
        # approach, and the spread between a near step and a far one is the thing that stops a
        # corridor sounding like a list being played.
        files = sorted(os.listdir(dest))
        if len(files) > args.keep:
            step = len(files) / args.keep
            keep = {files[int(i * step)] for i in range(args.keep)}
            for f in files:
                if f not in keep:
                    os.unlink(os.path.join(dest, f))
            files = sorted(os.listdir(dest))
        # Renumber, so the names are contiguous after the thinning.
        for i, f in enumerate(files, 1):
            os.rename(os.path.join(dest, f),
                      os.path.join(dest, f"{material.lower()}_{i:02d}.wav.tmp"))
        for f in sorted(os.listdir(dest)):
            os.rename(os.path.join(dest, f), os.path.join(dest, f[:-4]))
        total += len(files)
        print(f"   kept {len(files)}")

    print(f"\n{OUT}: {len(SOURCES)} materials, {total} samples")
    return 0


if __name__ == "__main__":
    sys.exit(main())
