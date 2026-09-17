#!/usr/bin/env python3
"""
Cut a recording of somebody walking into one file per footstep.

A recording of a walk is fifty steps in a row, and a game needs them one at a time. Doing that by
hand is an afternoon of dragging in an editor; doing it by energy is a minute, and it is more
consistent than a person would be because it puts the cut in the same place relative to every step.

What it does NOT do is normalise each step to the same peak. That is the obvious thing to write and
it throws away the most valuable property the recording has: real steps vary by several decibels from
one to the next, and that variation is exactly what stops a corridor sounding like a list being
played. One gain is applied to the whole set, so the loud ones stay loud.

    tools/split_footsteps.py <input audio> <output dir> [--prefix concrete] [--dry-run]

Needs ffmpeg on the path. Everything else is the standard library.
"""

import argparse
import math
import os
import struct
import subprocess
import sys
import tempfile
import wave

# How far the energy has to rise above the noise floor before it counts as a step. A fraction of the
# loudest step in the file rather than an absolute, so a quiet recording and a loud one behave alike.
ONSET_FRACTION = 0.16

# Two heel strikes cannot be closer than this. A walking pace is about two steps a second and a hard
# run is four, so 150 ms is comfortably under the fastest real gait and well over the gap between a
# heel and the toe-off of the SAME step, which must not be split into two files.
MIN_GAP_S = 0.15

# How much to keep before the onset. The detector fires once the energy is already climbing, and a
# transient with its own attack cut off is a click rather than a footstep.
PRE_ROLL_S = 0.012

# A step is over when it has been this far under its own peak for a while.
TAIL_DB = -34.0
MAX_STEP_S = 0.55

# How far under the loudest step a step may be and still be worth keeping.
#
# A recording of somebody walking usually has them approaching and going away again, and the far ones
# are the same foot heard across a room — quieter, duller, and carrying that room with them. As a
# game sample that is wrong twice over, because the engine is about to apply its own distance and its
# own room to it. Twenty decibels keeps the spread that makes a walk sound human and drops the ones
# that are really recordings of somewhere else.
KEEP_WITHIN_DB = 20.0

# Long enough that nothing clicks, short enough that it cannot eat the attack.
FADE_IN_S = 0.002
FADE_OUT_S = 0.020


def decode(path, rate=44100):
    """Anything ffmpeg can read, as mono float samples."""
    tmp = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
    tmp.close()
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", path, "-ac", "1", "-ar", str(rate), tmp.name],
        check=True,
    )
    with wave.open(tmp.name, "rb") as w:
        n, sr = w.getnframes(), w.getframerate()
        raw = w.readframes(n)
    os.unlink(tmp.name)
    return [v / 32768.0 for v in struct.unpack("<%dh" % n, raw)], sr


def envelope(x, sr, hop_s=0.002, win_s=0.010):
    """Short-term RMS. Fine enough to place a heel strike to a couple of milliseconds."""
    hop, win = int(sr * hop_s), int(sr * win_s)
    out = []
    for i in range(0, max(1, len(x) - win), hop):
        acc = 0.0
        for v in x[i:i + win]:
            acc += v * v
        out.append(math.sqrt(acc / win))
    return out, hop_s


def onsets(env, hop_s):
    """Where the energy crosses upward through the threshold, no two too close together."""
    peak = max(env) if env else 0.0
    if peak <= 0:
        return []
    thr = peak * ONSET_FRACTION
    found, last = [], -1e9
    for i in range(1, len(env)):
        t = i * hop_s
        if env[i] > thr and env[i - 1] <= thr and (t - last) >= MIN_GAP_S:
            # Walk back to where it actually started rising, so the attack is not clipped.
            j = i
            while j > 0 and env[j - 1] < env[j] and (i - j) * hop_s < 0.05:
                j -= 1
            found.append(j * hop_s)
            last = t
    return found


def cut(x, sr, start_s, next_s):
    """One step: from just before the onset to where it has died away or the next one begins."""
    a = max(0, int((start_s - PRE_ROLL_S) * sr))
    limit = int(min(next_s - 0.01 if next_s else 1e9, start_s + MAX_STEP_S) * sr)
    limit = min(limit, len(x))
    if limit <= a:
        return None

    seg = x[a:limit]
    peak = max((abs(v) for v in seg), default=0.0)
    if peak <= 1e-5:
        return None

    # Trim the tail: the last place it was above the floor, plus a little room to breathe.
    floor = peak * (10 ** (TAIL_DB / 20.0))
    end = len(seg)
    while end > 1 and abs(seg[end - 1]) < floor:
        end -= 1
    end = min(len(seg), end + int(sr * 0.02))
    seg = seg[:end]

    fi, fo = int(sr * FADE_IN_S), int(sr * FADE_OUT_S)
    for i in range(min(fi, len(seg))):
        seg[i] *= i / fi
    for i in range(min(fo, len(seg))):
        seg[len(seg) - 1 - i] *= i / fo
    return seg


def write_wav(path, samples, sr):
    pcm = b"".join(
        struct.pack("<h", int(max(-1.0, min(1.0, v)) * 32767)) for v in samples
    )
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(pcm)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input")
    ap.add_argument("outdir")
    ap.add_argument("--prefix", default=None, help="base name for the files (default: the input's)")
    ap.add_argument("--dry-run", action="store_true", help="say what it would cut, write nothing")
    ap.add_argument("--peak", type=float, default=0.89, help="peak of the LOUDEST step in the set")
    args = ap.parse_args()

    x, sr = decode(args.input)
    env, hop = envelope(x, sr)
    ons = onsets(env, hop)
    if not ons:
        print("no steps found — the file may be silent or the threshold too high", file=sys.stderr)
        return 1

    steps = []
    for i, t in enumerate(ons):
        seg = cut(x, sr, t, ons[i + 1] if i + 1 < len(ons) else None)
        if seg is not None and len(seg) > sr * 0.03:
            steps.append((t, seg))

    # Drop the ones that are really the same walker heard from across the room.
    loudest = max(max(abs(v) for v in s) for _, s in steps)
    floor = loudest * (10 ** (-KEEP_WITHIN_DB / 20.0))
    kept = [(t, s) for t, s in steps if max(abs(v) for v in s) >= floor]
    dropped = len(steps) - len(kept)
    steps = kept

    # ONE gain for the whole set. Per-file normalisation would flatten the natural spread between
    # steps, which is the part of a recording a synthesiser finds hardest to imitate.
    gain = args.peak / loudest if loudest > 0 else 1.0

    prefix = args.prefix or os.path.splitext(os.path.basename(args.input))[0]
    print(f"{len(steps)} step(s) from {len(x) / sr:.1f}s of {os.path.basename(args.input)}"
          + (f"  ({dropped} dropped as too distant)" if dropped else ""))
    levels = []
    for _, seg in steps:
        levels.append(20 * math.log10(max(max(abs(v) for v in seg) * gain, 1e-6)))
    print(f"  levels span {min(levels):.1f} to {max(levels):.1f} dBFS "
          f"({max(levels) - min(levels):.1f} dB of natural variation, kept)")
    durs = [len(s) / sr for _, s in steps]
    print(f"  lengths {min(durs) * 1000:.0f} to {max(durs) * 1000:.0f} ms")

    if args.dry_run:
        return 0

    os.makedirs(args.outdir, exist_ok=True)
    for i, (_, seg) in enumerate(steps, 1):
        write_wav(os.path.join(args.outdir, f"{prefix}_{i:02d}.wav"),
                  [v * gain for v in seg], sr)
    print(f"  -> {args.outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
