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
import math
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


# ── Weapons ─────────────────────────────────────────────────────────────────────────────────────
#
# Two completely different jobs live in this drop, and conflating them is the mistake to avoid.
#
# The HANDLING sounds — charging, magazines, selectors, the dry trigger — are already what we want.
# They were recorded close, in a quiet room, on a source that is quiet to begin with, so the room they
# carry is negligible next to the sound itself. Those get resampled, levelled and filed.
#
# The FIRING recordings are not. Every one of them is four milliseconds of muzzle blast followed by
# two and a half seconds of the field it was recorded in — and the measured first reflection lands
# about 30 ms after the peak, which is a surface roughly five metres away that is not in our map. Ship
# that whole and the player hears two rooms at once: ours, built by Steam Audio out of the geometry
# they are standing in, and the recordist's, baked in and immovable. So the firing takes are cut to
# the DIRECT SOUND ONLY, ending before that first reflection arrives. What is left is a dry transient
# the engine can put anywhere, which is the same contract WeaponSynth renders to.

# How far after the peak we will look for the first reflection, and the window we will accept.
#
# The upper bound is a judgement and it is deliberately tight. Outdoors, a rifle's direct blast has
# spent its meaningful energy inside twenty milliseconds; past thirty you are listening to the ground
# bounce and whatever was standing nearby, which is the recordist's site and not our map. Two of these
# six takes never show a distinct arrival at all — they just decay smoothly into their own site noise —
# so without a cap they would ship that noise. The cap is what makes the rule safe when the detector
# finds nothing.
MIN_TRANSIENT = 0.010       # below this there is no body left, only the click
MAX_TRANSIENT = 0.030       # beyond this we are into the recordist's site whether we heard it or not
ENVELOPE_BLOCK = 0.002      # RMS window. At 0.5 ms broadband noise fluctuates ±7 dB on its own and
                            # every take reads as a wall of reflections; at 2 ms the decay is smooth.
REFLECTION_RISE_DB = 4.0    # a rise this far above the running decay floor is an arrival, not decay
REFLECTION_SUSTAIN = 2      # ...and it has to hold for this many blocks. A single block is noise.
PRE_PEAK = 0.004            # most we keep before the peak, trimmed back to where the attack begins

# Anything at or above this is on the rail. The firing takes are all clipped — between 0.15% and 2.4%
# of their samples — which is exactly the part we are here for.
CLIP_LEVEL = 32700

# Cutting a 20 ms window out of a blast leaves an enormous offset behind, and in these takes it is not
# a filter artefact — it is in the recording. The handgun take spends eight milliseconds pinned against
# the NEGATIVE rail, so the extracted window has a mean of -0.40 against a peak of 0.72: more than half
# the headroom spent on an excursion too low to hear, and a step at both ends of the buffer, which is a
# click on every shot. A second-order high-pass at 30 Hz takes the mean to 0.03. Thirty is chosen to sit
# below the thump fundamentals the weapon profiles use (78-95 Hz) so it removes the rail and not the gun;
# at 60 Hz the handgun loses a third of its RMS and starts sounding like a different weapon.
TRANSIENT_HIGHPASS_HZ = 30


def read_wav_mono(path: Path):
    """Sample data as floats in [-1, 1], plus the rate. Mono-sums anything wider.

    Read here rather than through sox because the next two steps — finding the reflection and
    repairing the clipped peaks — both need the individual samples, and neither is something sox can
    be asked to do.
    """
    import wave
    with wave.open(str(path), "rb") as w:
        if w.getsampwidth() != 2:
            return None, 0
        n, ch, rate = w.getnframes(), w.getnchannels(), w.getframerate()
        raw = w.readframes(n)
    import struct
    s = struct.unpack("<%dh" % (len(raw) // 2), raw)
    if ch > 1:
        s = [sum(s[i:i + ch]) / ch for i in range(0, len(s) - ch + 1, ch)]
    return [x / 32768.0 for x in s], rate


def write_wav16(path: Path, samples, rate):
    import wave, struct
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(struct.pack("<%dh" % len(samples),
                                  *[max(-32768, min(32767, int(x * 32767))) for x in samples]))


def normalize_in_place(path: Path, peak_db=ONESHOT_PEAK_DB):
    """Normalize to a peak, as its own pass over the finished file.

    `gain -n` inside an effect chain normalizes against the peak sox knew about when the chain STARTED,
    not the peak arriving at the gain effect, so a chain that filters or fades first lands wherever it
    lands: these came out between -3.4 and -0.0 dBFS when every one of them had asked for -3. One of
    them at full scale is a one-shot with no headroom at all, which is the thing the target exists to
    prevent. A second pass measures what is actually there.
    """
    tmp = path.with_suffix(".norm.wav")
    if run(["sox", str(path), str(tmp), "gain", "-n", str(peak_db)]).returncode != 0:
        tmp.unlink(missing_ok=True)
        return False
    tmp.replace(path)
    return True


def declip(samples):
    """Round off the flat tops the recorder left behind.

    Every firing take in this drop hit the rail. A clipped peak is a horizontal line where a pressure
    transient should be, and the ear hears that as a spit or a buzz laid over the bang — a square edge
    is a stack of high harmonics that were never in the room. The original amplitude is gone and no
    amount of processing brings it back, but the SHAPE can be: fit a parabola through the last good
    sample either side of the flat run and let the run follow it, which restores a rounded peak that
    overshoots the rail a little. Everything is scaled back under full scale afterwards.

    Returns the repaired samples and how many were touched, because a run this fails on should say so
    rather than quietly pass the square edge through.
    """
    out = list(samples)
    thresh = CLIP_LEVEL / 32768.0
    n = len(out)
    repaired = 0
    i = 0
    while i < n:
        if abs(out[i]) < thresh:
            i += 1
            continue
        j = i
        while j < n and abs(out[j]) >= thresh:
            j += 1
        run = j - i
        # A single sample on the rail is a peak that just touched it; nothing to reconstruct. A very
        # long run is not a clipped transient, it is a recording that was mastered into the ceiling,
        # and inventing a curve across it would be fiction.
        if 2 <= run <= 64 and i > 0 and j < n:
            sign = 1.0 if out[i] > 0 else -1.0
            a, b = out[i - 1], out[j]
            # Height of the reconstructed arc above the rail. A longer flat top was clipped harder.
            lift = thresh * 0.35 * min(1.0, run / 24.0)
            for k in range(run):
                u = (k + 1) / (run + 1)
                base = a + (b - a) * u
                out[i + k] = base + sign * lift * 4.0 * u * (1.0 - u)
            repaired += run
        i = j
    return out, repaired


def limiting_ms(samples, peak_i, rate):
    """How long the recording sits pinned at full scale after the peak.

    Worth measuring and worth saying out loud. A muzzle blast decays; these takes do not, because they
    were mastered into a limiter before being uploaded. Their peak envelope is a flat line at 0 dBFS
    for thirty milliseconds and more, which means the natural decay that would have told us where the
    direct sound ends is simply not in the file. It is also why a recorded transient alone sounds
    flat: the shape that makes a blast read as a blast has been squeezed out of it, and the engine has
    to supply the decay instead. Reported per take so the number is a known property rather than a
    surprise when someone wonders why these need synthesis layered under them.
    """
    thresh = CLIP_LEVEL / 32768.0
    blk = max(1, int(rate * 0.001))
    last = peak_i
    at = peak_i
    while at < len(samples) - blk:
        seg = samples[at:at + blk]
        if max(abs(x) for x in seg) < thresh:
            if at - last > int(rate * 0.005):     # 5 ms clear: the pinning is genuinely over
                break
        else:
            last = at
        at += blk
    return (last - peak_i) / rate * 1000


def transient_window(samples, rate, peak_i=None):
    """(start, end) of the direct sound: the blast, ending before the recording's site gets in.

    <paramref name="peak_i"/> is supplied by the caller and must come from the ORIGINAL signal.
    Declipping lifts the repaired runs above the rail, which can move the loudest sample tens of
    milliseconds later than the actual attack — the first version of this searched for the peak
    itself, found one inside a repaired plateau, and anchored every window twenty milliseconds late.

    Walks a 2 ms RMS envelope forward from the peak, tracking the lowest level seen so far. A blast
    decays, so that floor keeps dropping; when the envelope climbs back above it by
    REFLECTION_RISE_DB and STAYS there, something has arrived — and in an outdoor gunshot recording
    the first thing to arrive is the nearest hard surface. Cut two milliseconds before it.

    Both halves of that rule earn their place. Without the sustain requirement, broadband noise
    supplies a 4 dB rise every few blocks and every take gets cut at the minimum; without the rise
    detector, the one take here whose reflection comes back at FULL LEVEL and holds for eighteen
    milliseconds would ship all of it. When nothing rises, MAX_TRANSIENT is the answer.

    The start backs off the peak only as far as the attack actually begins — a blast rises in well
    under a millisecond, and anything before that is the room the microphone was sitting in.
    """
    import math
    if not samples:
        return 0, 0
    if peak_i is None:
        peak_i = max(range(len(samples)), key=lambda i: abs(samples[i]))
    peak = abs(samples[peak_i])
    blk = max(1, int(rate * ENVELOPE_BLOCK))

    def block_db(at):
        seg = samples[at:at + blk]
        if not seg:
            return None
        r = (sum(x * x for x in seg) / len(seg)) ** 0.5
        return 20 * math.log10(r) if r > 1e-9 else None

    floor = None
    rising = 0
    cut = peak_i + int(rate * MAX_TRANSIENT)
    earliest = peak_i + int(rate * MIN_TRANSIENT)
    limit = min(len(samples) - blk, peak_i + int(rate * MAX_TRANSIENT))
    at = peak_i
    while at < limit:
        db = block_db(at)
        if db is not None:
            if floor is None or db < floor:
                floor, rising = db, 0
            elif db > floor + REFLECTION_RISE_DB:
                rising += 1
                if rising >= REFLECTION_SUSTAIN and at >= earliest:
                    # Back up to where the rise started, then a little more.
                    cut = at - (REFLECTION_SUSTAIN - 1) * blk - int(rate * 0.002)
                    break
            else:
                rising = 0
        at += blk

    # Walk back from the peak to the start of the attack: the last point at least 30 dB down.
    start = max(0, peak_i - int(rate * PRE_PEAK))
    gate = peak * 0.03
    for i in range(peak_i, start, -1):
        if abs(samples[i]) < gate:
            start = i
            break

    end = max(start + int(rate * MIN_TRANSIENT), min(len(samples), cut))
    return start, end


# What a firing take is a shot FROM. The drop labels these by action, not by model, which is honest of
# it — a "semiauto rifle" transient is a rifle-calibre blast and it is the right starting point for
# both the AR15 and the AKM until takes of each turn up. Where a weapon has no take of its own the
# weapon definition points at one of these and says so, rather than us filing a copy under its name
# and losing the fact that it is a stand-in.
FIRING_POOLS = {
    "semiauto-rifle": "rifle",
    "singleshot-rifle": "rifle_single",
    "semiauto-handgun": "handgun",
}

# The handling sounds, by what the filename says they are. Longest key first when matching, because
# "ejecting-empty-magazine" must win over "magazine". Anything unmatched is listed, never guessed:
# a reload that plays a safety selector is a lie told in the one channel this game has.
WEAPON_ACTIONS = [
    ("set-selector", "SAFETY"),
    ("safety-selector", "SAFETY"),
    ("shotgun-safety", "SAFETY"),
    ("pulling-trigger", "TRIGGER"),
    ("trigger-pull", "TRIGGER"),
    ("trigger", "TRIGGER"),
    ("charging", "CHARGE"),
    ("ejecting-empty", "MAG_OUT_EMPTY"),
    ("ejecting-an-empty", "MAG_OUT_EMPTY"),
    ("ejecting-loaded", "MAG_OUT_LOADED"),
    ("ejecting-a-loaded", "MAG_OUT_LOADED"),
    ("inserting-empty", "MAG_IN_EMPTY"),
    ("inserting-an-empty", "MAG_IN_EMPTY"),
    ("inserting-loaded", "MAG_IN_LOADED"),
    ("inserting-a-loaded", "MAG_IN_LOADED"),
    ("locking-the-bolt-open", "BOLT_OPEN"),
    ("locking-slide-back", "BOLT_OPEN"),
    ("locking-slide", "BOLT_OPEN"),
    ("open-shotgun-action", "BOLT_OPEN"),
    ("closing-bolt", "BOLT_CLOSE"),
    ("releasing-slide", "BOLT_CLOSE"),
    ("close-shotgun-action", "BOLT_CLOSE"),
    ("cocking-hammer", "HAMMER_COCK"),
    ("decocking-hammer", "HAMMER_DECOCK"),
    ("pump-shotgun", "PUMP"),
    ("load-shotgun", "LOAD_SHELL"),
]

# The AKM was recorded with both magazine types and they do not sound alike — steel rings, polymer
# knocks. Same reasoning as the footstep footwear variants: pooled together, a player would change
# magazine at random between reloads. So they are separate pools and the weapon definition names one.
MAG_VARIANTS = ("metal", "polymer")


def classify_weapon_file(stem: str):
    """(action, variant) for a handling take, or None when the filename does not say."""
    n = stem.lower()
    action = None
    for key, act in sorted(WEAPON_ACTIONS, key=lambda kv: len(kv[0]), reverse=True):
        if key in n:
            action = act
            break
    if action is None:
        return None
    variant = ""
    if action.startswith("MAG_"):
        for v in MAG_VARIANTS:
            if v in n:
                variant = v
                break
    return action, variant


def ingest_weapons(src_dir: Path, out_dir: Path, manifest: Manifest, dry, force, per_pool):
    """Firing takes to dry transients; handling takes filed by weapon and action; casings pooled."""
    written = 0
    unreadable = []
    tmp = Path(tempfile.mkdtemp(prefix="openfps-weapons-"))

    def emit(folder: Path, index: int, src: Path, cut: Path, note: str, highpass=False):
        """Resample, level and fade one prepared file into its pool."""
        nonlocal written
        folder.mkdir(parents=True, exist_ok=True)
        out = folder / f"ingest_{index:02d}.wav"
        chain = ["sox", str(cut), "-b", "16", "-r", str(TARGET_RATE), "-c", "1", str(out), "remix", "-"]
        if highpass:
            chain += ["highpass", "-2", str(TRANSIENT_HIGHPASS_HZ)]
        chain += ["fade", "t", str(FADE), "0", str(FADE)]
        r = run(chain)
        if r.returncode != 0:
            print(f"    sox failed on {src.name}: {r.stderr.strip().splitlines()[-1:] or ''}")
            return
        normalize_in_place(out)
        manifest.add(src, [out], note)
        written += 1

    try:
        # --- Firing: transient only -------------------------------------------------------------
        firing_dir = src_dir / "firing"
        if firing_dir.is_dir():
            pools = {}
            for src in sorted(firing_dir.glob("*.wav")):
                pool = None
                for key, name in FIRING_POOLS.items():
                    if key in src.stem.lower():
                        pool = name
                        break
                if pool is None:
                    unreadable.append(src)
                    continue
                pools.setdefault(pool, []).append(src)

            for pool, srcs in sorted(pools.items()):
                folder = out_dir / "WEAPONS" / "_FIRING" / pool
                if dry:
                    print(f"  WEAPONS/_FIRING/{pool}: {len(srcs)} take(s) would be cut to transients")
                    continue
                if folder.exists() and list(folder.glob("*.wav")) and not force:
                    print(f"  WEAPONS/_FIRING/{pool}: already present, skipping (--force to replace)")
                    continue
                if force and folder.exists():
                    for old in folder.glob("ingest_*.wav"):
                        old.unlink()
                for i, src in enumerate(srcs[:per_pool], start=1):
                    samples, rate = read_wav_mono(src)
                    if not samples:
                        unreadable.append(src)
                        continue
                    peak_i = max(range(len(samples)), key=lambda k: abs(samples[k]))
                    pinned = limiting_ms(samples, peak_i, rate)
                    samples, repaired = declip(samples)
                    a, b = transient_window(samples, rate, peak_i)
                    ms = (b - a) / rate * 1000
                    cut = tmp / f"{pool}_{i}.wav"
                    write_wav16(cut, samples[a:b], rate)
                    print(f"  WEAPONS/_FIRING/{pool}: {src.name[:42]:<42} "
                          f"transient {ms:5.1f} ms, {repaired:>4} clipped sample(s) repaired, "
                          f"peaks pinned for {pinned:4.0f} ms")
                    emit(folder, i, src, cut,
                         f"muzzle-blast transient only ({ms:.0f} ms), cut before the recording's "
                         f"first reflection; {repaired} clipped samples reconstructed; source was "
                         f"limited flat at full scale for {pinned:.0f} ms; high-passed at "
                         f"{TRANSIENT_HIGHPASS_HZ} Hz to remove the rail excursion",
                         highpass=True)

        # --- Handling: per weapon, per action ---------------------------------------------------
        for weapon_dir in sorted(p for p in src_dir.iterdir() if p.is_dir()):
            name = weapon_dir.name
            if name in ("firing", "casing bounce"):
                continue
            pools = {}
            for src in sorted(weapon_dir.glob("*.wav")):
                # The drop has the AR15's trigger sitting in the AKM folder as well. Filing it under
                # both would give the AKM a trigger it was never recorded with; the folder it is in
                # does not outrank the model named in the file.
                model = re.match(r"^\d+__[a-z_]+__((?:ar-?15|akm|glock|pistol|shotgun))", src.stem.lower())
                if model and name.lower() not in model.group(1).replace("-", ""):
                    other = model.group(1).replace("-", "").upper()
                    if other.rstrip("0123456789") not in name.upper().replace("-", ""):
                        print(f"    {src.name[:52]} names {other}, not {name} — left for review")
                        unreadable.append(src)
                        continue
                klass = classify_weapon_file(src.stem)
                if klass is None:
                    unreadable.append(src)
                    continue
                action, variant = klass
                pools.setdefault((action, variant), []).append(src)

            for (action, variant), srcs in sorted(pools.items()):
                rel = f"WEAPONS/{name}/{action}" + (f"/{variant}" if variant else "")
                if dry:
                    print(f"  {rel}: {len(srcs)} take(s)")
                    continue
                folder = out_dir / Path(rel)
                if folder.exists() and list(folder.glob("*.wav")) and not force:
                    print(f"  {rel}: already present, skipping (--force to replace)")
                    continue
                if force and folder.exists():
                    for old in folder.glob("ingest_*.wav"):
                        old.unlink()
                print(f"  {rel}: {len(srcs)} take(s)")
                for i, src in enumerate(srcs[:per_pool], start=1):
                    emit(folder, i, src, src, f"weapon handling sound: {name} {action}"
                                              + (f" ({variant} magazine)" if variant else ""))

        # --- Casings ----------------------------------------------------------------------------
        casing_dir = src_dir / "casing bounce"
        if casing_dir.is_dir():
            srcs = sorted(casing_dir.glob("*.wav"))
            rel = "WEAPONS/_CASINGS"
            if dry:
                print(f"  {rel}: {len(srcs)} take(s)")
            else:
                folder = out_dir / Path(rel)
                if folder.exists() and list(folder.glob("*.wav")) and not force:
                    print(f"  {rel}: already present, skipping (--force to replace)")
                else:
                    if force and folder.exists():
                        for old in folder.glob("ingest_*.wav"):
                            old.unlink()
                    print(f"  {rel}: {len(srcs)} take(s)")
                    for i, src in enumerate(srcs[:per_pool], start=1):
                        emit(folder, i, src, src, "ejected casing striking the ground")

        if unreadable:
            print(f"  {len(unreadable)} take(s) left for review (NOT guessed at)")
            if not dry:
                listing = out_dir / "WEAPONS" / "UNSORTED_TAKES.txt"
                listing.parent.mkdir(parents=True, exist_ok=True)
                listing.write_text(
                    "Weapon takes whose action could not be read from the filename, or which name a\n"
                    "different model from the folder they were dropped in.\n\n"
                    "Add the action to WEAPON_ACTIONS in tools/ingest_audio.py, or move the file to\n"
                    "the folder for the weapon it actually is, and re-run.\n\n"
                    + "\n".join(str(p.relative_to(REPO)) if p.is_relative_to(REPO) else str(p)
                                for p in unreadable) + "\n")
                print(f"    -> {listing.relative_to(REPO)}")
        return written
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ── The Cadre Forensics gunshot dataset ─────────────────────────────────────────────────────────
#
# NIJ grant 2016-DN-BX-0183, collected in rural Arizona in 2017 and released free for research. Twenty
# firearms, each recorded from twenty positions on four devices; we want the ZOOM H4N recordings,
# which are 96 kHz and — measurably — unclipped and unlimited at every position. That is the thing no
# amount of processing can put back, and the thing every library we had measured was missing.
#
# The geometry table below is straight out of AudioDatafileDescription.pdf. A filename is
# ZM_<id><letter>_S<shot>.wav, where the numeric id falls in a block that names the microphone's angle
# and distance, and the LETTER picks between the two geometries that block covers. The id's last digit
# identifies which firearm it is, which is why the block has to be read as a RANGE rather than by
# taking the first two characters — Glock9's ids are 008/048/108, not 018/058/118 like the M16's.

CADRE_GEOMETRY = [
    # (id_low, id_high, A geometry, B geometry)   distances in metres
    (1, 20,    (-30, 20),  (-30, 40)),
    (41, 60,   (90, 40),   (90, 20)),
    (61, 80,   (130, 40),  (130, 20)),
    (81, 100,  (180, 20),  (180, 40)),
    (101, 120, (180, 10),  (180, 3)),
    (121, 140, (130, 10),  (90, 10)),
    (141, 160, (30, 9),    (90, 3)),
    (161, 180, (60, 20),   (180, 0.5)),
    (181, 200, None,       (0, 3)),
    (201, 220, (40, 10),   (20, 20)),
]

# Which Cadre firearm stands in for which of ours. Only the four we actually have weapons for; the
# dataset has no shotgun at all, which is why the pump gun keeps its old take.
CADRE_WEAPONS = {
    "M16": "AR15",          # 5.56x45 — the take the AR-15 never had
    "WASR": "AKM",          # 7.62x39 AK pattern
    "Glock9": "Glock",      # 9x19
    "Colt1911": "pistol",   # .45 ACP, hammer-fired
}

# The distance buckets we keep per weapon. Recording the same shot at five ranges is the most valuable
# thing in this dataset: a blast that has genuinely travelled forty metres through air has lost its top
# end to the air rather than to a filter we applied, and no amount of EQ reproduces what that does to
# the envelope.
CADRE_DISTANCES = [0.5, 3, 10, 20, 40]
# How far from the nominal distance a recording may be and still fill the bucket.
CADRE_DISTANCE_TOLERANCE = 1.0

# ONE angle, held constant across the whole distance series.
#
# A muzzle blast is strongly directional — far louder and far brighter ahead of the muzzle than behind
# it — so a bucket that mixes 90-degree and 180-degree takes is measuring directivity and calling it
# distance. Mixed, the AKM came out BRIGHTER at forty metres than at three, which is not what air does
# to sound; it is what standing beside the muzzle rather than behind it does.
#
# 90 degrees — side-on — and NOT 180, which was the first choice and was wrong.
#
# 180 is behind the muzzle, and for a rifle that is the extreme of the directivity pattern rather than
# a representative sample of it. Measured on the M16 at three metres: from the side, the low band sits
# +17.3 dB relative to the mids; from behind, MINUS 2.8. Twenty decibels of body, gone. That is what
# was making the AR-15 sound like a tick no matter what was done downstream — the weight was never in
# the file. The AKM survived it because a 7.62 has enough low end to spare; the 5.56 did not.
#
# 90 degrees is also the more honest default for a game: most shots a player hears are other people's,
# from the side, and the dataset has 90 degrees at 3, 10, 20 and 40 m — a complete series, same as 180.
# Directivity away from side-on is still the engine's job (SpatialEmitter's cone); it just now starts
# from the middle of the pattern instead of one end of it.
CADRE_ANGLE = 90
CADRE_ANGLE_TOLERANCE = 5


def cadre_geometry(stem):
    """(angle_degrees, distance_metres) for a Cadre filename, or None if it is not one."""
    m = re.match(r"^ZM_(\d{3})([A-Z])_S(\d+)$", stem)
    if not m:
        return None
    num, letter = int(m.group(1)), m.group(2)
    for lo, hi, a_geo, b_geo in CADRE_GEOMETRY:
        if lo <= num <= hi:
            geo = a_geo if letter == "A" else (b_geo if letter == "B" else None)
            return geo
    return None


def ingest_cadre(src_dir: Path, out_dir: Path, manifest: Manifest, dry, force, per_pool):
    """Unpacks the per-firearm zips, picks the useful geometries, and cuts each to its transient."""
    import zipfile

    zips = sorted(src_dir.glob("*_Zoom.zip"))
    if not zips:
        print(f"  no *_Zoom.zip in {src_dir}")
        return 0

    written = 0
    tmp = Path(tempfile.mkdtemp(prefix="openfps-cadre-"))
    try:
        for z in zips:
            cadre_name = z.stem.replace("_Zoom", "")
            ours = CADRE_WEAPONS.get(cadre_name)
            if ours is None:
                print(f"  {cadre_name}: no weapon uses it, skipped")
                continue

            work = tmp / cadre_name
            if not dry:
                work.mkdir(parents=True, exist_ok=True)
                with zipfile.ZipFile(z) as zf:
                    zf.extractall(work)

            # Bucket the takes by how far away the microphone was.
            buckets = {d: [] for d in CADRE_DISTANCES}
            for wav in sorted(work.rglob("*.wav")) if not dry else []:
                geo = cadre_geometry(wav.stem)
                if geo is None:
                    continue
                angle, dist = geo
                if abs(abs(angle) - CADRE_ANGLE) > CADRE_ANGLE_TOLERANCE:
                    continue
                for d in CADRE_DISTANCES:
                    if abs(dist - d) <= CADRE_DISTANCE_TOLERANCE:
                        buckets[d].append(wav)
                        break

            if dry:
                print(f"  {cadre_name} -> WEAPONS/_FIRING/{ours}/<3|10|20|40>m")
                written += 1
                continue

            for d, takes in buckets.items():
                if not takes:
                    print(f"    {ours} {d} m: no take at that distance")
                    continue
                label = f"{d:g}".replace(".", "_")
                rel = f"WEAPONS/_FIRING/{ours}/{label}m"
                folder = out_dir / Path(rel)
                if folder.exists() and list(folder.glob("*.wav")) and not force:
                    print(f"  {rel}: already present, skipping (--force to replace)")
                    continue
                folder.mkdir(parents=True, exist_ok=True)
                if force:
                    for old in folder.glob("ingest_*.wav"):
                        old.unlink()

                kept = 0
                for i, srcwav in enumerate(takes[:per_pool], start=1):
                    samples, rate = read_wav_mono(srcwav)
                    if not samples:
                        continue
                    peak_i = max(range(len(samples)), key=lambda k: abs(samples[k]))
                    pinned = limiting_ms(samples, peak_i, rate)
                    samples, repaired = declip(samples)
                    a, b = transient_window(samples, rate, peak_i)
                    cut = tmp / f"{ours}_{d}_{i}.wav"
                    write_wav16(cut, samples[a:b], rate)

                    out = folder / f"ingest_{kept + 1:02d}.wav"
                    chain = ["sox", str(cut), "-b", "16", "-r", str(TARGET_RATE), "-c", "1", str(out),
                             "remix", "-", "highpass", "-2", str(TRANSIENT_HIGHPASS_HZ),
                             "fade", "t", str(FADE), "0", str(FADE)]
                    if run(chain).returncode != 0:
                        continue
                    normalize_in_place(out)
                    manifest.add(srcwav, [out],
                                 f"{cadre_name} muzzle blast at {d} m, transient only "
                                 f"({(b - a) / rate * 1000:.0f} ms); source 96 kHz Zoom H4N, "
                                 f"{repaired} clipped samples, {pinned:.0f} ms limited; "
                                 f"NIJ grant 2016-DN-BX-0183")
                    kept += 1
                    written += 1
                print(f"  {rel}: {kept} take(s) from {len(takes)} available")

        return written
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ── Glass ───────────────────────────────────────────────────────────────────────────────────────

# The tinkle texture is a hundred and five seconds of shards moving. Two different things are wanted
# from it and they need different treatment: individual FALLS, sliced out as one-shots for a fragment
# landing, and a continuous BED for the granular engine to read from when a whole pane comes down.
# Measured, not guessed: this file's RMS is -26 dBFS and its quiet passages never fall below -44, so a
# threshold set for a footstep take finds no boundaries at all and returns zero slices. It is a
# continuous texture — the name says so — and the threshold has to sit near its own RMS to find the
# individual falls standing out of it.
GLASS_SLICE_THRESHOLD = "3%"
GLASS_SLICE_GAP = 0.12
GLASS_MIN_SLICE = 0.05
GLASS_MAX_SLICE = 1.2
MAX_GLASS_BED_SECONDS = 30


def ingest_glass(src_dir: Path, out_dir: Path, manifest: Manifest, dry, force):
    written = 0
    tmp = Path(tempfile.mkdtemp(prefix="openfps-glass-"))
    try:
        for src in sorted(p for p in src_dir.iterdir() if p.is_file()):
            info = probe(src)
            if not info:
                continue
            n = src.stem.lower()
            if "tinkle" in n or "shard" in n or info["duration"] > 10:
                # Slices for individual shards, and a bed for the granular engine.
                rel_pool, rel_bed = "GLASS/TINKLE", "GLASS/BED"
                if dry:
                    print(f"  {rel_pool} + {rel_bed}: from {src.name} ({info['duration']:.0f}s)")
                    written += 1
                    continue
                pool = out_dir / Path(rel_pool)
                bed = out_dir / Path(rel_bed)
                if pool.exists() and list(pool.glob("*.wav")) and not force:
                    print(f"  {rel_pool}: already present, skipping (--force to replace)")
                    continue
                if force:
                    for d in (pool, bed):
                        if d.exists():
                            for old in d.glob("ingest_*.wav"):
                                old.unlink()

                work = tmp / "tinkle"
                work.mkdir(parents=True, exist_ok=True)
                r = run(["sox", str(src), "-b", "16", "-r", str(TARGET_RATE), "-c", "1",
                         str(work / "cut_.wav"), "remix", "-",
                         "silence", "1", "0.01", GLASS_SLICE_THRESHOLD,
                         "1", str(GLASS_SLICE_GAP), GLASS_SLICE_THRESHOLD,
                         ":", "newfile", ":", "restart"])
                good = []
                if r.returncode == 0:
                    for c in sorted(work.glob("cut_*.wav")):
                        i2 = probe(c)
                        if i2 and GLASS_MIN_SLICE <= i2["duration"] <= GLASS_MAX_SLICE:
                            good.append(c)
                        else:
                            c.unlink(missing_ok=True)
                pool.mkdir(parents=True, exist_ok=True)
                outs = []
                for i, c in enumerate(good[:24], start=1):
                    out = pool / f"ingest_{i:02d}.wav"
                    if run(["sox", str(c), str(out), "fade", "t", str(FADE), "0", str(FADE)]).returncode == 0:
                        normalize_in_place(out)
                        outs.append(out)
                        written += 1
                print(f"  {rel_pool}: {len(good)} slice(s) found, {len(outs)} written")

                bed.mkdir(parents=True, exist_ok=True)
                bed_out = bed / "ingest_01.wav"
                if run(["sox", str(src), "-b", "16", "-r", str(TARGET_RATE), "-c", "1", str(bed_out),
                        "remix", "-", "trim", "0", str(MAX_GLASS_BED_SECONDS)]).returncode == 0:
                    normalize_in_place(bed_out)
                    outs.append(bed_out)
                    written += 1
                    print(f"  {rel_bed}: {MAX_GLASS_BED_SECONDS}s mono bed for the granular engine")
                manifest.add(src, outs, "shard tinkle: sliced into one-shot falls plus a granular bed")
            else:
                rel = "GLASS/SHATTER"
                if dry:
                    print(f"  {rel}: {src.name}")
                    written += 1
                    continue
                folder = out_dir / Path(rel)
                if folder.exists() and list(folder.glob("*.wav")) and not force:
                    print(f"  {rel}: already present, skipping (--force to replace)")
                    continue
                folder.mkdir(parents=True, exist_ok=True)
                if force:
                    for old in folder.glob("ingest_*.wav"):
                        old.unlink()
                existing = len(list(folder.glob("ingest_*.wav")))
                out = folder / f"ingest_{existing + 1:02d}.wav"
                if run(["sox", str(src), "-b", "16", "-r", str(TARGET_RATE), "-c", "1", str(out),
                        "remix", "-", "fade", "t", "0.001", "0", str(FADE)]).returncode == 0:
                    normalize_in_place(out)
                    manifest.add(src, [out], "pane/container shattering, one-shot")
                    written += 1
                    print(f"  {rel}: {src.name[:50]}")
        return written
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ── Vetting a candidate download ────────────────────────────────────────────────────────────────

def measure_takes(folder: Path, limit=40):
    """Report the three things that decide whether a firing recording is usable.

    Written because "is this library any good?" is answerable in about a second per file, and the
    alternative is ingesting a few hundred megabytes and finding out by ear afterwards. The three
    defects that matter all have numbers:

      * CLIPPED. Samples pinned to the rail. The transient is the part we are here for and it is the
        part that clips first, and nothing recovers what was squared off.
      * LIMITED. How long the peak envelope stays flat at full scale after the peak. A muzzle blast
        decays; a mastered one does not, and the decay that makes it read as a gunshot is simply not
        in the file. This is the defect that is invisible in a waveform thumbnail and fatal in use.
      * DARK. Energy above 2 kHz against energy below it. A gunshot's character lives between 2 and
        8 kHz, and a take recorded at distance or through a rolled-off microphone has none of it. The
        drop we started from measured -34 dB here, which is why it sounded like a tick.

    Datasets collected for machine-LEARNING are worth measuring before trusting: a classifier does not
    care whether its input is clipped, so a great many of them are.
    """
    import wave, struct, cmath

    files = [p for p in sorted(folder.rglob("*")) if p.suffix.lower() in (".wav", ".flac", ".aiff")]
    if not files:
        print(f"  nothing to measure in {folder}")
        return 1

    print(f"  {len(files)} file(s); showing up to {limit}\n")
    print(f"  {'file':<40}{'rate':>8}{'clip%':>8}{'limited':>9}{'bright':>9}  verdict")

    verdicts = {"good": 0, "usable": 0, "poor": 0}
    for src in files[:limit]:
        conv = None
        try:
            if src.suffix.lower() != ".wav":
                conv = Path(tempfile.mkdtemp()) / "x.wav"
                if run(["sox", str(src), "-b", "16", str(conv)]).returncode != 0:
                    continue
                read = conv
            else:
                read = src
            with wave.open(str(read), "rb") as w:
                sr, n, ch = w.getframerate(), w.getnframes(), w.getnchannels()
                if w.getsampwidth() != 2:
                    print(f"  {src.name[:39]:<40}{sr:>8}   (not 16-bit; skipped)")
                    continue
                raw = w.readframes(min(n, sr * 3))
            smp = struct.unpack("<%dh" % (len(raw) // 2), raw)
            if ch > 1:
                smp = smp[0::ch]
            if not smp:
                continue

            clip_pct = 100.0 * sum(1 for x in smp if abs(x) >= CLIP_LEVEL) / len(smp)
            peak_i = max(range(len(smp)), key=lambda i: abs(smp[i]))
            limited = limiting_ms([x / 32768.0 for x in smp], peak_i, sr)

            # A fixed TIME window and no decimation, so files at different sample rates are measured
            # the same way. Taking a fixed SAMPLE count and decimating by 4 (which is what this did
            # first) aliases everything above sr/8 down into the band being measured: at 48 kHz that
            # folded content from above 6 kHz into the 2-8 kHz band and inflated it, at 96 kHz it did
            # not, and the two datasets could not be compared at all. The comparison is the entire
            # purpose of the tool.
            N = int(sr * 0.020)
            seg = [x / 32768.0 for x in smp[peak_i:peak_i + N]]
            seg += [0.0] * (N - len(seg))
            win = [seg[i] * (0.5 - 0.5 * math.cos(2 * math.pi * i / N)) for i in range(N)]

            def power(f0, f1, step):
                tot, cnt = 0.0, 0
                for fr in range(f0, f1, step):
                    k = fr * N / sr
                    acc = complex(0)
                    for i in range(N):
                        acc += win[i] * cmath.exp(-2j * math.pi * k * i / N)
                    tot += abs(acc) ** 2
                    cnt += 1
                return tot / max(1, cnt)

            lo = power(150, 1500, 150)
            hi = power(2000, 8000, 500)
            bright = 10 * math.log10(hi / lo) if lo > 0 and hi > 0 else -99

            # Clipping and limiting are the disqualifying defects, because nothing downstream
            # recovers them. Darkness is a handicap rather than a disqualification: synthesis can
            # supply top end, but it cannot supply dynamics that were squashed out.
            if clip_pct > 0.3 or limited > 30 or bright < -30:
                v = "poor"
            elif clip_pct > 0.02 or limited > 8 or bright < -18:
                v = "usable"
            else:
                v = "good"
            verdicts[v] += 1

            print(f"  {src.name[:39]:<40}{sr:>8}{clip_pct:>7.2f}%{limited:>7.0f}ms"
                  f"{bright:>+8.1f}dB  {v}")
        finally:
            if conv is not None:
                shutil.rmtree(conv.parent, ignore_errors=True)

    print(f"\n  good {verdicts['good']}   usable {verdicts['usable']}   poor {verdicts['poor']}")
    print("  good   = unclipped, unlimited, plenty above 2 kHz. Use it as-is.")
    print("  usable = layer it under synthesis, as the current drop is.")
    print("  poor   = the transient is already destroyed; nothing downstream recovers it.")
    return 0


# ── Fitting the engine synthesis to a real recording ────────────────────────────────────────────

def measure_engine(path: Path, rpm_hint=None, quiet=False):
    """Measure one recording and RETURN its properties. See match_engine for what they are for."""
    return _measure(path, rpm_hint, quiet)


def match_engine(path: Path, rpm_hint=None, against: Path = None):
    """Measure a real exhaust recording and report what to set the synthesis to.

    The vehicle synthesis is built from physics — firing angles, pipe lengths, chamber lengths — and
    those parts are not guesses. The LEVELS are: how lossy the pipes are, how much turbulent noise sits
    between the harmonics, how much of each pulse is bulk gas rather than valve edge. Those were
    estimated, and estimating them is how the thing ended up sounding like a sawtooth.

    So this measures the four things that actually distinguish a real exhaust from a synthesizer, and
    says which constant to move. It does NOT need a good recording: a phone at a car meet is plenty,
    because every measurement here is a RATIO within the clip and is insensitive to the microphone, the
    level, and most of the room. Nothing from the recording is used in the game — the output is numbers.
    """
    ref = _measure(path, rpm_hint, quiet=False)
    if ref is None:
        return 2

    if against is None:
        print("\n  These are the REFERENCE's properties. Pass --compare <our render> to get the")
        print("  parameter changes that move ours toward it.")
        return 0

    print(f"\n  --- and ours: {against.name} ---\n")
    ours = _measure(against, None, quiet=False)
    if ours is None:
        return 2

    print("\n  REFERENCE vs OURS:\n")
    print(f"    harmonic-to-noise   {ref['hnr']:+6.1f}   {ours['hnr']:+6.1f}  "
          f"(ours is {ours['hnr'] - ref['hnr']:+.1f} dB too {'clean' if ours['hnr'] > ref['hnr'] else 'noisy'})")
    print(f"    rolloff dB/harmonic {ref['slope']:+6.1f}   {ours['slope']:+6.1f}")
    print(f"    low vs mid band     {ref['lowmid']:+6.1f}   {ours['lowmid']:+6.1f}")

    print("\n  SET THESE in VehicleSynth:\n")
    # Noise: close the measured HNR gap. 26 dB per decade of level is the empirical slope from our own
    # two-point calibration (flow 0.95 -> 34.1 dB, flow 3.96 -> 4.9 dB).
    scale = max(0.15, min(8.0, 10 ** ((ours['hnr'] - ref['hnr']) / 26.0)))
    print(f"    FlowNoiseLevel   {2.10 * scale:.2f}      (x{scale:.2f} to close a "
          f"{ours['hnr'] - ref['hnr']:+.1f} dB gap)")
    print(f"    PulseNoiseLevel  {1.32 * scale:.2f}")
    # Damping: ours should roll off as fast as the reference does.
    dslope = ours['slope'] - ref['slope']
    damp = max(0.08, min(0.85, 0.19 - dslope * 0.03))
    print(f"    HeaderDamping    {damp:.2f}      (rolloff differs by {dslope:+.1f} dB/harmonic)")
    print(f"    SystemDamping    {damp * 0.6:.2f}")
    hump = max(0.6, min(3.0, 1.32 * 10 ** ((ref['lowmid'] - ours['lowmid']) / 30.0)))
    print(f"    HumpLevel        {hump:.2f}      (low-vs-mid differs by "
          f"{ref['lowmid'] - ours['lowmid']:+.1f} dB)")
    return 0


# ── The exhaust's own transfer function ─────────────────────────────────────────────────────────

def _fft(re, im):
    """Iterative radix-2 FFT, in place. Pure Python because this machine has no numpy, and a
    per-bin DFT over a 32k window costs minutes where this costs about a second."""
    n = len(re)
    j = 0
    for i in range(1, n):
        bit = n >> 1
        while j & bit:
            j ^= bit
            bit >>= 1
        j |= bit
        if i < j:
            re[i], re[j] = re[j], re[i]
            im[i], im[j] = im[j], im[i]
    length = 2
    while length <= n:
        ang = -2 * math.pi / length
        wr, wi = math.cos(ang), math.sin(ang)
        for i in range(0, n, length):
            cr, ci = 1.0, 0.0
            half = length >> 1
            for k in range(i, i + half):
                ur, ui = re[k], im[k]
                vr = re[k + half] * cr - im[k + half] * ci
                vi = re[k + half] * ci + im[k + half] * cr
                re[k], im[k] = ur + vr, ui + vi
                re[k + half], im[k + half] = ur - vr, ui - vi
                cr, ci = cr * wr - ci * wi, cr * wi + ci * wr
        length <<= 1


def _spectrum(seg, sr, size=32768):
    """One Hann-windowed power spectrum, in dB, and the Hz per bin."""
    n = min(size, len(seg))
    n = 1 << (n.bit_length() - 1)          # down to a power of two
    re = [seg[i] * (0.5 - 0.5 * math.cos(2 * math.pi * i / n)) for i in range(n)]
    im = [0.0] * n
    _fft(re, im)
    half = n // 2
    mag = [10 * math.log10(re[i] * re[i] + im[i] * im[i] + 1e-20) for i in range(half)]
    return mag, sr / n


def _steady_passages(smp, sr, want=6):
    """Several steady passages spread across the clip's ENGINE SPEEDS, not just the steadiest one.

    One passage measures one operating point. What separates a fixed filter — the pipes, the mic, the
    room, the boom — from something tied to the firing is that a fixed filter sits at the same
    FREQUENCIES however fast the engine is turning, while the harmonics move. That separation needs
    more than one engine speed, and a clip with an idle, a rev and a take-off in it has several.
    """
    win = int(sr * 0.75)
    step = int(sr * 0.25)
    scored = []
    for i in range(0, max(1, len(smp) - win), step):
        blocks = []
        for j in range(i, i + win - int(sr * 0.05), int(sr * 0.05)):
            seg = smp[j:j + int(sr * 0.05)]
            blocks.append(math.sqrt(sum(x * x for x in seg) / len(seg)) + 1e-9)
        mean = sum(blocks) / len(blocks)
        var = sum((b / mean - 1) ** 2 for b in blocks) / len(blocks)
        scored.append((i, var, mean))

    loudest = max(m for _, _, m in scored)
    usable = [p for p in scored if p[2] >= loudest * 0.35 and p[1] < 0.25]
    usable.sort(key=lambda p: p[1])

    # Spread them out: two passages a quarter second apart measure the same thing twice.
    chosen = []
    for at, var, _ in usable:
        if all(abs(at - c) > sr * 1.5 for c in chosen):
            chosen.append(at)
        if len(chosen) >= want:
            break
    return sorted(chosen), win


def _fire_rate(mag, hz_per_bin, lo=20.0, hi=420.0):
    """The firing rate: the fundamental whose HARMONICS are also strong, so a loud second harmonic
    cannot win and report the engine an octave fast."""
    best, best_score = 0.0, -1e9
    f = lo
    while f < hi:
        score = 0.0
        for h in (1, 2, 3, 4, 5, 6):
            b = int(round(f * h / hz_per_bin))
            if 0 < b < len(mag):
                score += mag[b]
        if score > best_score:
            best, best_score = f, score
        f += 0.25
    return best


def _harmonics(mag, hz_per_bin, fire, ceiling=7000.0):
    """Level of each firing harmonic, dB, with its frequency. Peak-picked over a small
    neighbourhood, because a real engine's speed drifts a little even when it is being held."""
    out = []
    h = 1
    while fire * h < ceiling:
        f = fire * h
        centre = f / hz_per_bin
        lo = max(1, int(centre - 2))
        hi = min(len(mag) - 1, int(centre + 3))
        if hi > lo:
            out.append((h, f, max(mag[lo:hi])))
        h += 1
    return out


def _fit_tilt(points, order=3):
    """Fit a smooth curve in log-frequency through (Hz, dB) — the BROADBAND TILT.

    This is the whole reason a boomy clip is still usable. A phone's proximity, a car park, a
    platform's loudness normalisation and a codec's top-end all act as a SMOOTH multiplicative curve
    across the spectrum; an exhaust's character is STRUCTURE — sharp peaks at the firing harmonics
    and notches where the muffler's chambers cancel. Those are different shapes, and shape is
    something arithmetic can separate. Fit the smooth part, subtract it, and what is left is the
    engine.

    Doing this by ear instead — EQ-ing the clip until it sounds right — removes by judgement exactly
    what this removes by measurement, and leaves nobody able to say how much of the engine went too.

    Least squares on a Vandermonde system, solved by Gaussian elimination. No numpy on this machine.
    """
    xs = [math.log10(max(1.0, f)) for f, _ in points]
    ys = [d for _, d in points]
    n = order + 1

    a = [[sum(x ** (i + j) for x in xs) for j in range(n)] for i in range(n)]
    b = [sum(y * x ** i for x, y in zip(xs, ys)) for i in range(n)]

    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(a[r][col]))
        if abs(a[piv][col]) < 1e-12:
            return lambda f: sum(ys) / len(ys)
        a[col], a[piv] = a[piv], a[col]
        b[col], b[piv] = b[piv], b[col]
        for r in range(n):
            if r == col:
                continue
            factor = a[r][col] / a[col][col]
            for c in range(col, n):
                a[r][c] -= factor * a[col][c]
            b[r] -= factor * b[col]
    coef = [b[i] / a[i][i] for i in range(n)]

    def curve(f):
        x = math.log10(max(1.0, f))
        return sum(c * x ** i for i, c in enumerate(coef))
    return curve


def engine_envelope(path: Path, rpm_hint=None, quiet=False):
    """What the exhaust SYSTEM does to the engine's harmonics, measured across several engine speeds.

    `match_engine` reports a single rolloff slope, and the vehicles notes already record why that is
    the wrong shape: damping and noise interact through it, so chasing the slope moved two metrics
    backwards. A slope is one number standing in for a curve. This measures the curve.

    Returns the residual transfer function sampled at the harmonics, pooled across passages — which
    is exactly the thing the impulse-response discussion wanted and could not get from one passage.
    """
    smp, sr = _decode_mono(path)
    if smp is None:
        return None

    print(f"  {path.name}: {len(smp) / sr:.1f}s at {sr} Hz")

    starts, win = _steady_passages(smp, sr)
    if not starts:
        print("  no steady passage found — the whole clip is moving")
        return None

    pooled = []
    report = []
    for at in starts:
        seg = smp[at:at + win]
        mag, hz_per_bin = _spectrum(seg, sr)
        fire = _fire_rate(mag, hz_per_bin)
        if fire <= 0:
            continue
        harm = _harmonics(mag, hz_per_bin, fire)
        if len(harm) < 6:
            continue
        top = max(d for _, _, d in harm)
        report.append((at / sr, fire, fire / 4 * 60, len(harm)))
        for _, f, d in harm:
            pooled.append((f, d - top))

    if not pooled:
        print("  found passages but no usable harmonic series in them")
        return None

    print(f"\n  {len(report)} steady passage(s), {len(pooled)} harmonic samples:")
    print("     at        firing      rpm (V8)   harmonics")
    for t, fire, rpm, nh in report:
        print(f"    {t:5.1f}s    {fire:6.1f} Hz    {rpm:6.0f}      {nh:3d}")

    tilt = _fit_tilt(pooled)
    grid = _residual_grid(pooled, tilt)

    print("\n  Residual transfer function — the clip's own tilt removed, so this is the SYSTEM.")
    print("  A band is only believable where the passages AGREE: a fixed filter sits at the same")
    print("  frequency however fast the engine is turning, so disagreement across engine speeds")
    print(f"  means that band is not measuring one. Bands spreading more than {AGREE_DB:.0f} dB are marked.")
    print("\n       Hz     dB   spread   n")
    for f, mean, spread, n in grid:
        flag = "" if spread <= AGREE_DB else "   <- passages disagree; not the system"
        bar = "#" * max(0, min(30, int(15 + mean)))
        print(f"    {f:7.0f}  {mean:+6.1f}  {spread:6.1f}  {n:3d}  {bar}{flag}")

    firm = [g for g in grid if g[2] <= AGREE_DB]
    dev = [abs(d - tilt(f)) for f, d in pooled]
    print(f"\n  Tilt removed spans {tilt(60):+.1f} dB at 60 Hz to {tilt(4000):+.1f} dB at 4 kHz.")
    print(f"  Residual is {sum(dev) / len(dev):.1f} dB mean absolute — that is the engine's structure.")
    if firm:
        print(f"  {len(firm)} of {len(grid)} bands agree across engine speeds, "
              f"{firm[0][0]:.0f} Hz to {firm[-1][0]:.0f} Hz.")
    return {"passages": report, "pooled": pooled, "tilt": tilt, "grid": grid}


AGREE_DB = 12.0


def _residual_grid(pooled, tilt):
    """The residual binned by twelfth-decade: (Hz, mean dB, spread dB, count)."""
    grid = {}
    for f, d in pooled:
        grid.setdefault(round(math.log10(f) * 12), []).append(d - tilt(f))
    out = []
    for band in sorted(grid):
        vals = grid[band]
        if len(vals) < 2:
            continue
        out.append((10 ** (band / 12), sum(vals) / len(vals), max(vals) - min(vals), len(vals)))
    return out


def compare_envelopes(ref_path: Path, ours_path: Path, rpm_hint=None):
    """Hold our render against a real recording, band by band, where both can be believed.

    Only where BOTH agree across their own engine speeds — comparing a band the reference cannot
    measure against one we render perfectly is how a fitting run talks itself into moving a constant
    that was already right.
    """
    print(f"\n=== REFERENCE: {ref_path.name} ===")
    ref = engine_envelope(ref_path, rpm_hint)
    if ref is None:
        return 2
    print(f"\n=== OURS: {ours_path.name} ===")
    ours = engine_envelope(ours_path)
    if ours is None:
        return 2

    rg = {round(math.log10(f) * 12): (m, s) for f, m, s, _ in ref["grid"]}
    og = {round(math.log10(f) * 12): (m, s) for f, m, s, _ in ours["grid"]}

    print("\n\n  WHERE WE DIFFER, in the bands both can measure:")
    print("       Hz     ref     ours     diff")
    diffs = []
    for band in sorted(set(rg) & set(og)):
        (rm, rs), (om, os_) = rg[band], og[band]
        if rs > AGREE_DB or os_ > AGREE_DB:
            continue
        f = 10 ** (band / 12)
        d = om - rm
        diffs.append((f, d))
        print(f"    {f:7.0f}  {rm:+6.1f}  {om:+6.1f}  {d:+7.1f}")

    if not diffs:
        print("    no band is measurable in both — the reference is too degraded to fit against.")
        return 0

    mean = sum(d for _, d in diffs) / len(diffs)
    print(f"\n  Mean difference {mean:+.1f} dB over {len(diffs)} comparable band(s).")
    print("  Positive means OURS has more there than the real car does.")
    return 0


def _decode_mono(path: Path):
    """Any format ffmpeg reads, as mono float samples."""
    import wave, struct
    conv = None
    try:
        if path.suffix.lower() != ".wav":
            conv = Path(tempfile.mkdtemp()) / "ref.wav"
            if run(["ffmpeg", "-v", "error", "-y", "-i", str(path), "-ac", "1",
                    "-ar", "44100", str(conv)]).returncode != 0:
                print(f"  could not decode {path.name}")
                return None, 0
            read = conv
        else:
            read = path
        with wave.open(str(read), "rb") as w:
            sr, ch, n = w.getframerate(), w.getnchannels(), w.getnframes()
            if w.getsampwidth() != 2:
                print("  need 16-bit audio")
                return None, 0
            raw = w.readframes(n)
        smp = struct.unpack("<%dh" % (len(raw) // 2), raw)
        if ch > 1:
            smp = smp[0::ch]
        return [x / 32768.0 for x in smp], sr
    finally:
        if conv is not None:
            shutil.rmtree(conv.parent, ignore_errors=True)


def _measure(path: Path, rpm_hint=None, quiet=False):
    import wave, struct, cmath

    conv = None
    try:
        if path.suffix.lower() != ".wav":
            conv = Path(tempfile.mkdtemp()) / "ref.wav"
            if run(["ffmpeg", "-v", "error", "-y", "-i", str(path), "-ac", "1",
                    "-ar", "44100", str(conv)]).returncode != 0:
                print(f"  could not decode {path.name}")
                return None
            read = conv
        else:
            read = path

        with wave.open(str(read), "rb") as w:
            sr, ch, n = w.getframerate(), w.getnchannels(), w.getnframes()
            if w.getsampwidth() != 2:
                print("  need 16-bit audio")
                return None
            raw = w.readframes(min(n, sr * 30))
        smp = struct.unpack("<%dh" % (len(raw) // 2), raw)
        if ch > 1:
            smp = smp[0::ch]
        smp = [x / 32768.0 for x in smp]
        print(f"  {path.name}: {len(smp) / sr:.1f}s at {sr} Hz\n")

        # Pick the steadiest second in the clip — a passage where the revs are not moving, because a
        # sweeping fundamental smears every harmonic measurement into meaninglessness. (Learned the
        # hard way on our own render: a settling idle measured as though it were pure noise.)
        win_len = int(sr * 1.0)
        step = int(sr * 0.25)
        passages = []
        for i in range(0, max(1, len(smp) - win_len), step):
            blocks = []
            for j in range(i, i + win_len - int(sr * 0.05), int(sr * 0.05)):
                seg = smp[j:j + int(sr * 0.05)]
                blocks.append(math.sqrt(sum(x * x for x in seg) / len(seg)) + 1e-9)
            mean = sum(blocks) / len(blocks)
            var = sum((b / mean - 1) ** 2 for b in blocks) / len(blocks)
            passages.append((i, var, mean))

        # Steady AND loud. Steadiness alone picks SILENCE, which is perfectly steady and says nothing —
        # run against our own render it chose the moment the engine had been switched off, then
        # confidently reported the harmonic-to-noise ratio of a dying idle. Anything below half the
        # loudest passage is not the engine running.
        loudest = max(m for _, _, m in passages)
        usable = [p for p in passages if p[2] >= loudest * 0.5]
        if not usable:
            usable = passages
        best_at, best_var, _ = min(usable, key=lambda p: p[1])
        seg = smp[best_at:best_at + win_len]
        print(f"  steadiest passage at {best_at / sr:.1f}s (level varies {best_var * 100:.1f}%)")

        N = len(seg)
        win = [seg[i] * (0.5 - 0.5 * math.cos(2 * math.pi * i / N)) for i in range(N)]

        def mag(fr):
            k = fr * N / sr
            acc = complex(0)
            for i in range(0, N, 2):
                acc += win[i] * cmath.exp(-2j * math.pi * k * i / N)
            return abs(acc)

        # 1. The firing rate. For a V8 it is RPM/60*4, so it also gives the revs.
        lo, hi = (20, 400) if rpm_hint is None else (rpm_hint / 60 * 4 * 0.8, rpm_hint / 60 * 4 * 1.25)
        cands = [(f, mag(f)) for f in [x * 0.5 for x in range(int(lo * 2), int(hi * 2))]]
        # The true fundamental is the one whose HARMONICS are also strong - otherwise a strong 2nd
        # harmonic wins and the answer comes out an octave high.
        scored = []
        for f, m in cands:
            if f < 15:
                continue
            scored.append((f, sum(mag(f * h) for h in (1, 2, 3, 4))))
        fire = max(scored, key=lambda c: c[1])[0]
        print(f"  firing rate {fire:.1f} Hz  ->  {fire / 4 * 60:.0f} rpm if it is a V8\n")

        # 2. Harmonic rolloff: how fast the harmonics fall. This is the PIPE DAMPING.
        harm = [(h, mag(fire * h)) for h in range(1, 25) if fire * h < 8000]
        mx = max(v for _, v in harm)
        db = [(h, 20 * math.log10(v / mx + 1e-12)) for h, v in harm]
        print("  harmonic rolloff (dB below the strongest):")
        print("    " + "  ".join(f"x{h}:{d:+.0f}" for h, d in db[:10]))
        # Slope over the first eight, in dB per harmonic.
        use = [(h, d) for h, d in db[:8]]
        mh = sum(h for h, _ in use) / len(use)
        md = sum(d for _, d in use) / len(use)
        num = sum((h - mh) * (d - md) for h, d in use)
        den = sum((h - mh) ** 2 for h, _ in use)
        slope = num / den if den else 0
        print(f"    slope {slope:+.1f} dB per harmonic")

        # 3. Harmonic-to-noise: how much turbulence sits between the harmonics.
        hv = [v for _, v in harm]
        fv = [mag(fire * (h + 0.25)) for h in range(1, 25) if fire * (h + 0.25) < 8000]
        fv += [mag(fire * (h + 0.75)) for h in range(1, 25) if fire * (h + 0.75) < 8000]
        hp = sum(v * v for v in hv) / len(hv)
        fp = sum(v * v for v in fv) / len(fv)
        hnr = 10 * math.log10(hp / fp) if fp > 0 else 99
        print(f"\n  harmonic-to-noise {hnr:+.1f} dB")

        # 4. Band balance.
        def band(f0, f1, st):
            tot, c = 0.0, 0
            for fr in range(f0, f1, st):
                tot += mag(fr) ** 2
                c += 1
            return 10 * math.log10(tot / c + 1e-20)
        bands = [band(40, 120, 10), band(120, 300, 20), band(300, 800, 50),
                 band(800, 2500, 150), band(2500, 6000, 350)]
        bm = max(bands)
        print("  band balance   40-120  120-300  300-800  0.8-2.5k  2.5-6k")
        print("                " + "".join(f"{b - bm:+9.1f}" for b in bands))

        return {"fire": fire, "rpm": fire / 4 * 60, "slope": slope, "hnr": hnr,
                "bands": bands, "lowmid": bands[0] - bands[2]}
    finally:
        if conv is not None:
            shutil.rmtree(conv.parent, ignore_errors=True)


# ── Plumbing ────────────────────────────────────────────────────────────────────────────────────

def slug(name: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")
    return s or "unnamed"


# The two that take --per-pool are marked, because they slice or choose from a pool rather than
# converting one file to one file.
CATEGORIES = {
    "ambisonic": ("ambisonic ambiance", ingest_ambisonic, False),
    "stereo": ("outdoor ambiance non ambisonic", ingest_stereo_beds, False),
    "footsteps": ("foot steps sounds", ingest_footsteps, True),
    "weapons": ("weapons", ingest_weapons, True),
    "glass": ("glass breaking", ingest_glass, False),
    "cadre": ("weapons/cadreforensics", ingest_cadre, True),
}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--inbox", type=Path, default=INBOX)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--dry-run", action="store_true", help="say what would happen, write nothing")
    ap.add_argument("--force", action="store_true", help="replace outputs that already exist")
    ap.add_argument("--only", choices=sorted(CATEGORIES), help="run one category")
    ap.add_argument("--engine-match", type=Path, metavar="FILE",
                    help="measure a real exhaust recording and print the synthesis constants it "
                         "implies. Any format ffmpeg reads; a phone clip is good enough.")
    ap.add_argument("--engine-envelope", type=Path, metavar="FILE",
                    help="measure the exhaust SYSTEM's transfer function across every steady passage "
                         "in a clip, with the recording's own broadband tilt fitted and removed")
    ap.add_argument("--rpm", type=float, help="rough engine speed in the clip, if you know it")
    ap.add_argument("--envelope-compare", type=Path, metavar="OURS",
                    help="with --engine-envelope: our render, to hold against the reference")
    ap.add_argument("--compare", type=Path, metavar="FILE",
                    help="our own render, to compare against the reference and get the corrections")
    ap.add_argument("--measure", type=Path, metavar="FOLDER",
                    help="report clipping, limiting and brightness for a folder of candidate takes "
                         "and exit, without ingesting anything. For vetting a library before you "
                         "commit to it.")
    ap.add_argument("--per-pool", type=int, default=10,
                    help="how many variants to keep per material+variant folder (default 10; the "
                         "takes hold over a hundred each and the pool only needs enough that a "
                         "repeat is not noticeable)")
    args = ap.parse_args()

    for tool in ("sox", "ffprobe"):
        if not have(tool):
            print(f"error: {tool} is not on PATH. This needs sox and ffmpeg.", file=sys.stderr)
            return 2

    if args.engine_envelope:
        if not args.engine_envelope.is_file():
            print(f"error: no file at {args.engine_envelope}", file=sys.stderr)
            return 2
        if args.envelope_compare:
            return compare_envelopes(args.engine_envelope, args.envelope_compare, args.rpm)
        print(f"\nmeasuring {args.engine_envelope}:")
        return 0 if engine_envelope(args.engine_envelope, args.rpm) else 2

    if args.engine_match:
        if not args.engine_match.is_file():
            print(f"error: no file at {args.engine_match}", file=sys.stderr)
            return 2
        print(f"\nmatching {args.engine_match}:")
        return match_engine(args.engine_match, args.rpm, args.compare)

    if args.measure:
        if not args.measure.is_dir():
            print(f"error: no folder at {args.measure}", file=sys.stderr)
            return 2
        print(f"\nmeasuring {args.measure}:")
        return measure_takes(args.measure)

    if not args.inbox.is_dir():
        print(f"error: no inbox at {args.inbox}", file=sys.stderr)
        return 2

    manifest = Manifest()
    total = 0

    for name, (subdir, handler, pooled) in CATEGORIES.items():
        if args.only and args.only != name:
            continue
        src = args.inbox / subdir
        if not src.is_dir():
            print(f"{name}: nothing at {src}")
            continue
        print(f"\n{name} ({src.name}):")
        if pooled:
            total += handler(src, args.out, manifest, args.dry_run, args.force, args.per_pool)
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
