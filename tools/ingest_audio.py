#!/usr/bin/env python3
"""
Turn a drop folder of downloaded audio into the shape the engine actually reads.

Nothing arrives usable. Ambisonic beds come at 96 kHz and seven minutes long, which is 650 MB of RAM
once the bed DSP loads one as float. Footstep "samples" are ninety-second studio takes with a hundred
and seventy steps in them, not one-shots. Stereo ambiences are 128 kbps MP3 at whatever level the
uploader felt like. And the engine wants one sample rate, mono for anything it will spatialize, and a
specific folder layout it resolves by name.

So this is the step between "downloaded" and "committed". It reads `inbox/`, writes into the ASSETS
tree, and writes a manifest saying where every output came from — because a CC-BY file whose author you
can no longer name is a file you cannot ship.

Nothing is destructive: the inbox is only ever read, and existing outputs are left alone unless --force.

Usage:
    tools/ingest_audio.py --dry-run          # say what would happen, touch nothing
    tools/ingest_audio.py                    # do it
    tools/ingest_audio.py --only footsteps   # one category
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
INBOX = REPO / "inbox"
DEFAULT_OUT = REPO / "OpenFPS.Client" / "ASSETS" / "SOUNDS"

# The mixer runs at 44.1 kHz (see FmodAudioProvider.Initialize). Anything above it is resampled down on
# playback anyway, so shipping 96 kHz costs memory and disk and buys nothing at all.
TARGET_RATE = 44100

# An ambisonic bed is held in memory as float PCM for its whole length. 90 seconds of first order at
# 44.1 kHz is about 63 MB; the seven-minute 96 kHz original is 647 MB.
MAX_BED_SECONDS = 90
MAX_STEREO_BED_SECONDS = 120

# Peak target for one-shots. Short of full scale so the mix has room before the master limiter.
ONESHOT_PEAK_DB = -3.0

# Slicing. The takes have a floor around -94 dBFS between steps, so the threshold can be low and tight.
SILENCE_THRESHOLD = "0.2%"
SILENCE_GAP = 0.08          # a step every 0.13 s at a run, so the gap has to be shorter than that
MIN_SLICE = 0.06            # shorter than this is a click, not a footstep
MAX_SLICE = 1.5             # longer is two steps and a pause
FADE = 0.003                # 3 ms in and out: kills the edge click without audibly softening the attack

# Which surface a filename is talking about. Only the ones actually present; anything unrecognised goes
# to _unsorted for a human, because a wrong guess here is worse than no guess — it puts gravel under a
# player walking on carpet and nothing ever says so.
MATERIALS = {
    "concrete": "Concrete",
    "cement": "Cement",
    "tile": "Tile",
    "dirt": "Dirt",
    "grass": "Grass",
    "gravel": "Gravel",
    "leaves": "Leaves",
    "creaky wood": "Wood",
    "wood floor": "Wood",
    "wood": "Wood",
    "carpet": "Carpet",
}

# Footwear becomes the VARIANT, not another sample in the same pool. SoundMappingService picks randomly
# within <Material><Variant>, so mixing barefoot and high heels into one folder would have the player
# randomly changing shoes between steps.
FOOTWEAR_VARIANTS = {
    "sneakers": 0, "sneaker": 0, "shoes": 0, "shoe": 0,
    "barefoot": 1,
    "high heals": 2, "high heels": 2, "heels": 2,
    "flitflops": 3, "flipflops": 3, "flip flops": 3,
}


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def have(tool):
    return shutil.which(tool) is not None


def probe(path):
    """Channels, sample rate and duration, or None if it is not audio we can read."""
    r = run(["ffprobe", "-v", "error", "-select_streams", "a:0",
             "-show_entries", "stream=channels,sample_rate:format=duration",
             "-of", "default=nw=1:nk=1", str(path)])
    if r.returncode != 0:
        return None
    parts = [p for p in r.stdout.split() if p]
    if len(parts) < 3:
        return None
    try:
        return {"rate": int(parts[0]), "channels": int(parts[1]), "duration": float(parts[2])}
    except ValueError:
        return None


class Manifest:
    """Where every shipped file came from. A CC-BY recording whose author you can no longer name is a
    recording you cannot ship, and the moment to record that is the moment it is converted."""

    def __init__(self):
        self.entries = []

    def add(self, source: Path, outputs, note=""):
        self.entries.append({
            "source": str(source.relative_to(REPO)) if source.is_relative_to(REPO) else str(source),
            "outputs": [str(p.relative_to(REPO)) if p.is_relative_to(REPO) else str(p) for p in outputs],
            "licence": "TODO — fill in before shipping",
            "author": "TODO",
            "url": "TODO",
            "note": note,
        })

    def write(self, out_dir: Path):
        if not self.entries:
            return None
        path = out_dir / "INGEST_MANIFEST.json"
        existing = []
        if path.exists():
            try:
                existing = json.loads(path.read_text()).get("entries", [])
            except json.JSONDecodeError:
                pass
        by_source = {e["source"]: e for e in existing}
        for e in self.entries:          # keep any licence fields a human has already filled in
            if e["source"] in by_source and by_source[e["source"]].get("author") != "TODO":
                e["licence"] = by_source[e["source"]]["licence"]
                e["author"] = by_source[e["source"]]["author"]
                e["url"] = by_source[e["source"]]["url"]
            by_source[e["source"]] = e
        path.write_text(json.dumps({"entries": list(by_source.values())}, indent=2) + "\n")
        return path


# ── Ambisonic beds ──────────────────────────────────────────────────────────────────────────────

def ingest_ambisonic(src_dir: Path, out_dir: Path, manifest: Manifest, dry, force):
    """Resample and trim a soundfield WITHOUT touching its channel order.

    sox, not ffmpeg, and deliberately: ffmpeg gives a 4-channel WAV the 'quad' speaker layout and is
    entitled to reorder or downmix it during a format conversion. To sox the channels are opaque and
    numbered, which is exactly what an ambisonic signal needs — W, Y, Z, X are not speakers and must
    arrive in the order they left.
    """
    dest = out_dir / "AMBIENCE"
    count = 0
    for src in sorted(src_dir.glob("*")):
        if not src.is_file():
            continue
        info = probe(src)
        if not info:
            print(f"  SKIP {src.name}: not readable as audio")
            continue

        order = round(info["channels"] ** 0.5) - 1
        if (order + 1) ** 2 != info["channels"] or order < 1:
            print(f"  SKIP {src.name}: {info['channels']} channels is not a full-sphere ambisonic "
                  f"layout (4, 9 or 16)")
            continue

        out = dest / f"{slug(src.stem)}.wav"
        trimmed = min(info["duration"], MAX_BED_SECONDS)
        note = (f"first-order ambisonic, {info['rate']} Hz -> {TARGET_RATE} Hz, "
                f"{info['duration']:.0f}s -> {trimmed:.0f}s")
        print(f"  {src.name}: {info['channels']}ch {info['rate']}Hz {info['duration']:.0f}s -> "
              f"{out.name} ({note})")

        if dry:
            count += 1
            continue
        if out.exists() and not force:
            print(f"    exists, skipping (use --force to replace)")
            continue

        dest.mkdir(parents=True, exist_ok=True)
        # `gain -n -3` normalizes the whole FILE by one factor, not per channel. Per-channel
        # normalization would rescale W against X/Y/Z and tilt the entire soundfield.
        # 16-bit, not 24: an ambience bed is diffuse background at low level, the bed DSP holds it as
        # float32 in memory either way, and 24-bit only doubles what sits in the repository.
        r = run(["sox", str(src), "-b", "16", "-r", str(TARGET_RATE), str(out),
                 "trim", "0", str(trimmed), "gain", "-n", "-3"])
        if r.returncode != 0:
            print(f"    FAILED: {r.stderr.strip().splitlines()[:1]}")
            continue

        check = probe(out)
        if not check or check["channels"] != info["channels"]:
            print(f"    FAILED: channel count changed ({info['channels']} -> "
                  f"{check['channels'] if check else '?'}). The soundfield would be wrong; removing.")
            out.unlink(missing_ok=True)
            continue

        manifest.add(src, [out], note)
        count += 1
    return count


# ── Stereo ambience beds ────────────────────────────────────────────────────────────────────────

def ingest_stereo_beds(src_dir: Path, out_dir: Path, manifest: Manifest, dry, force):
    """Head-locked diffuse beds. Legitimate for content with nothing localizable in it — distant
    traffic, crickets, wind wash — and wrong for anything a player might try to point at."""
    dest = out_dir / "AMBIENCE" / "stereo"
    count = 0
    for src in sorted(src_dir.glob("*")):
        if not src.is_file():
            continue
        info = probe(src)
        if not info:
            print(f"  SKIP {src.name}: not readable as audio")
            continue

        # Stays compressed. These sources are 128 kbps MP3; decoding one to WAV inflates it tenfold
        # and recovers nothing, and AudioBank indexes .ogg as happily as .wav.
        out = dest / f"{slug(src.stem)}.ogg"
        trimmed = min(info["duration"], MAX_STEREO_BED_SECONDS)
        print(f"  {src.name}: {info['channels']}ch {info['rate']}Hz {info['duration']:.0f}s -> {out.name}")
        if dry:
            count += 1
            continue
        if out.exists() and not force:
            print(f"    exists, skipping (use --force to replace)")
            continue

        dest.mkdir(parents=True, exist_ok=True)
        r = run(["ffmpeg", "-v", "error", "-y", "-i", str(src), "-t", str(trimmed),
                 "-ar", str(TARGET_RATE), "-ac", "2",
                 "-af", "loudnorm=I=-23:TP=-2:LRA=7",
                 "-c:a", "libvorbis", "-q:a", "4", str(out)])
        if r.returncode != 0:
            print(f"    FAILED: {r.stderr.strip().splitlines()[:1]}")
            continue
        manifest.add(src, [out], f"stereo bed, head-locked; {info['rate']} Hz -> {TARGET_RATE} Hz")
        count += 1
    return count


# ── Footsteps ───────────────────────────────────────────────────────────────────────────────────

def classify_footstep(name: str):
    """(material, variant, is_landing) from a filename, or None when it cannot be read confidently."""
    n = name.lower()
    is_landing = bool(re.search(r"\bland", n))

    material = None
    for key in sorted(MATERIALS, key=len, reverse=True):   # "creaky wood" before "wood"
        if key in n:
            material = MATERIALS[key]
            break
    if material is None:
        return None

    variant = 0
    for key, v in FOOTWEAR_VARIANTS.items():
        if key in n:
            variant = v
            break
    return material, variant, is_landing


def slice_take(src: Path, work: Path):
    """Split one long take into individual hits on the silence between them, returning the good ones.

    sox does the splitting rather than a hand-rolled onset detector: it is C-speed, and these takes
    have a floor around -94 dBFS between steps, so 'below the threshold for long enough' is a completely
    reliable boundary here. Slices outside the plausible length of a footstep are discarded — a very
    short one is a click and a very long one is two steps with a pause in the middle.
    """
    work.mkdir(parents=True, exist_ok=True)
    stem = work / "cut_.wav"
    r = run(["sox", str(src), "-b", "16", "-r", str(TARGET_RATE), "-c", "1", str(stem),
             "remix", "-",
             "silence", "1", "0.01", SILENCE_THRESHOLD, "1", str(SILENCE_GAP), SILENCE_THRESHOLD,
             ":", "newfile", ":", "restart"])
    if r.returncode != 0:
        return []

    good = []
    for cut in sorted(work.glob("cut_*.wav")):
        info = probe(cut)
        if not info or not (MIN_SLICE <= info["duration"] <= MAX_SLICE):
            cut.unlink(missing_ok=True)
            continue
        stats = run(["sox", str(cut), "-n", "stat"])
        peak = 0.0
        for line in stats.stderr.splitlines():
            if "Maximum amplitude" in line:
                try:
                    peak = abs(float(line.split(":")[1]))
                except (IndexError, ValueError):
                    pass
        if peak < 0.02:      # below this it is room tone that crossed the gate, not a step
            cut.unlink(missing_ok=True)
            continue
        good.append((cut, peak, info["duration"]))

    # Keep the most representative, not the loudest: an outlier-loud slice is usually a stumble or two
    # steps landing together, and it would stand out every time the pool served it.
    good.sort(key=lambda g: g[1], reverse=True)
    return good


def ingest_footsteps(src_dir: Path, out_dir: Path, manifest: Manifest, dry, force, per_pool):
    pools = {}      # (action, material, variant) -> list of (slice path, source)
    unsorted_srcs = []

    takes = [p for p in sorted(src_dir.rglob("*")) if p.is_file()]
    print(f"  {len(takes)} take(s) to examine")

    tmp = Path(tempfile.mkdtemp(prefix="openfps-ingest-"))
    try:
        for src in takes:
            klass = classify_footstep(src.stem)
            if klass is None:
                unsorted_srcs.append(src)
                continue
            material, variant, is_landing = klass
            action = "LANDING" if is_landing else "FOOTSTEPS"
            key = (action, material, variant)

            if dry:
                pools.setdefault(key, []).append((None, src))
                continue

            work = tmp / f"{len(pools)}_{src.stem}".replace(" ", "_")
            slices = slice_take(src, work)
            if not slices:
                print(f"    {src.name}: no usable slices")
                continue
            for cut, _peak, _dur in slices:
                pools.setdefault(key, []).append((cut, src))

        # Write the pools out.
        written = 0
        for (action, material, variant), items in sorted(pools.items()):
            folder = out_dir / action / material / f"{material}{variant}"
            chosen = items[:per_pool]
            if dry:
                # Nothing has been sliced yet in a dry run, so report takes — saying "slices" here
                # would promise a number this run has not actually measured.
                print(f"  {action}/{material}/{material}{variant}: {len(items)} take(s) would be sliced")
                continue
            print(f"  {action}/{material}/{material}{variant}: {len(items)} slice(s) available, "
                  f"writing {len(chosen)}")

            folder.mkdir(parents=True, exist_ok=True)
            existing = len(list(folder.glob("*.wav")))
            if existing and not force:
                print(f"    {existing} file(s) already there, skipping (use --force to replace)")
                continue
            if force:
                for old in folder.glob("ingest_*.wav"):
                    old.unlink()

            outputs, sources = [], set()
            for i, (cut, src) in enumerate(chosen, start=1):
                out = folder / f"ingest_{i:02d}.wav"
                r = run(["sox", str(cut), str(out),
                         "fade", "t", str(FADE), "0", str(FADE),
                         "gain", "-n", str(ONESHOT_PEAK_DB)])
                if r.returncode != 0:
                    continue
                outputs.append(out)
                sources.add(src)
                written += 1
            for src in sources:
                manifest.add(src, outputs, f"sliced from a take into {action}/{material}/{material}{variant}")

        if unsorted_srcs:
            # LISTED, not copied. They are already in the inbox; duplicating a hundred megabytes of
            # MP3 into the asset tree to mark them "to do" would be the worst of both. A wrong guess
            # is worse still — it puts gravel under a player walking on carpet and never says so.
            print(f"  {len(unsorted_srcs)} unlabelled take(s) listed for review (NOT guessed at)")
            if not dry:
                listing = out_dir / "UNLABELLED_TAKES.txt"
                listing.parent.mkdir(parents=True, exist_ok=True)
                lines = [
                    "Takes whose surface could not be read from the filename.",
                    "",
                    "Rename each with the surface in it (e.g. 'shoes on slate') and re-run the ingest;",
                    "add the surface to MATERIALS in tools/ingest_audio.py if it is a new one.",
                    "",
                ]
                lines += [str(p.relative_to(REPO)) if p.is_relative_to(REPO) else str(p)
                          for p in unsorted_srcs]
                listing.write_text("\n".join(lines) + "\n")
                print(f"    -> {listing.relative_to(REPO)}")
        return written
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ── Plumbing ────────────────────────────────────────────────────────────────────────────────────

def slug(name: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")
    return s or "unnamed"


CATEGORIES = {
    "ambisonic": ("ambisonic ambiance", ingest_ambisonic),
    "stereo": ("outdoor ambiance non ambisonic", ingest_stereo_beds),
    "footsteps": ("foot steps sounds", None),   # handled separately: it takes an extra argument
}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--inbox", type=Path, default=INBOX)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--dry-run", action="store_true", help="say what would happen, write nothing")
    ap.add_argument("--force", action="store_true", help="replace outputs that already exist")
    ap.add_argument("--only", choices=sorted(CATEGORIES), help="run one category")
    ap.add_argument("--per-pool", type=int, default=10,
                    help="how many variants to keep per material+variant folder (default 10; the "
                         "takes hold over a hundred each and the pool only needs enough that a "
                         "repeat is not noticeable)")
    args = ap.parse_args()

    for tool in ("sox", "ffprobe"):
        if not have(tool):
            print(f"error: {tool} is not on PATH. This needs sox and ffmpeg.", file=sys.stderr)
            return 2

    if not args.inbox.is_dir():
        print(f"error: no inbox at {args.inbox}", file=sys.stderr)
        return 2

    manifest = Manifest()
    total = 0

    for name, (subdir, handler) in CATEGORIES.items():
        if args.only and args.only != name:
            continue
        src = args.inbox / subdir
        if not src.is_dir():
            print(f"{name}: nothing at {src}")
            continue
        print(f"\n{name} ({src.name}):")
        if name == "footsteps":
            total += ingest_footsteps(src, args.out, manifest, args.dry_run, args.force, args.per_pool)
        else:
            total += handler(src, args.out, manifest, args.dry_run, args.force)

    if args.dry_run:
        print(f"\nDRY RUN — nothing written. {total} item(s) would be produced.")
        return 0

    path = manifest.write(args.out)
    print(f"\n{total} file(s) written under {args.out}")
    if path:
        print(f"Manifest: {path.relative_to(REPO)} — fill in the licence fields before shipping.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
