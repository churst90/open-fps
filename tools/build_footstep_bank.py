#!/usr/bin/env python3
"""
Builds the footstep bank from the two Foley packs in approved/footsteps ("16 - Foley #1 Footsteps"
and "17 - Footsteps"), as labelled by ear in tools/footstep_sets.json.

What the packs are (worked out 2026-09-28, confirmed by Cody's ear): sets of three files, one shoe on
one surface each.
  - a LANDING file: one landing, then a jog (about 0.35 s a step), then a run (about 0.27 s);
  - two WALKING files: about 30 s of walking (0.55-0.7 s a step), then short scuffs, which the
    game can use for turning on the spot.
Some sets have only walking files, a few are one long file, and the files the packs named
themselves are grouped by their names. Nothing is labelled inside the files; footstep_sets.json is
the only record of what each one is, and a set whose material is null is skipped.

Output, under --out (default a staging folder; --install writes to the game's ASSETS):
  FOOTSTEPS/<Material>/<shoe>/<walk|jog|run|scuff>/<material>_<shoe>_<gait>_NN.ogg
  LANDING/<Material>/<shoe>/<material>_<shoe>_land_NN.ogg
  FOOTSTEPS/<Material>/*.ogg   ordinary walking in the default shoe, the folder the game reads today
  LANDING/<Material>/*.ogg     the default shoe's landings, likewise

Every step in every set is kept (Cody, 2026-09-28: "use all of them"). Levels follow tools/split_footsteps.py: one gain per set and gait, so the loudest step peaks at
-1 dB and the rest keep their natural spread. The game evens takes out at playback (TakeLevels).
Steps are Vorbis at 44.1 kHz mono: the bank ships with the client.

    tools/build_footstep_bank.py [--out DIR] [--install] [--dry-run]

Needs ffmpeg, and numpy, scipy and soundfile (~/qwentts/venv/bin/python).
"""
import argparse, json, os, re, shutil, subprocess, sys, tempfile
import numpy as np, soundfile as sf
from scipy.signal import butter, sosfilt

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKS = {"16": "approved/footsteps/16 - Foley #1 Footsteps", "17": "approved/footsteps/17 - Footsteps"}
SR = 44100
LONGEST = {"walk": 0.55, "jog": 0.40, "run": 0.32, "scuff": 0.9, "land": 1.2}
DEFAULT_SHOES = ["shoes", "sneakers", "dress", "boots", "heels", "flipflops", "barefoot"]


def load(pack, track):
    path = os.path.join(ROOT, PACKS[pack], track + ".mp3")
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).astype(np.float64)


def events(x):
    """Footfalls: rises well over the recording's floor."""
    hp = sosfilt(butter(2, 60, "high", fs=SR, output="sos"), x)
    hop = int(0.005 * SR); n = len(hp) // hop
    db = 20 * np.log10(np.sqrt(np.array([np.mean(hp[i * hop:(i + 1) * hop] ** 2) for i in range(n)]) + 1e-12))
    live = db[db > -100]
    floor = np.percentile(live, 20) if len(live) else -100
    peak = np.percentile(db, 99.5)
    thr = floor + max(12, (peak - floor) * 0.4)
    ev = []; i = 0
    while i < n:
        if db[i] > thr:
            j = i
            while j < n and db[j] > max(floor + 8, db[i:j + 1].max() - 28): j += 1
            a = i
            while a > 0 and db[a - 1] > floor + 8 and i - a < 30: a -= 1
            ev.append([a * hop / SR, j * hop / SR])
            i = j + int(0.08 * SR / hop)
        else:
            i += 1
    merged = []
    for e in ev:
        # Two footfalls cannot be closer than 0.15 s (a hard run is four a second): anything nearer is
        # the same step, its toe, or on gravel and leaves the crunch that follows the heel.
        if merged and e[0] - merged[-1][0] < 0.15: merged[-1][1] = max(merged[-1][1], e[1])
        else: merged.append(e)
    return merged


def bursts(ev, gap=0.9):
    b = []
    for e in ev:
        if b and e[0] - b[-1][-1][1] < gap: b[-1].append(e)
        else: b.append([e])
    return b


def interval(b):
    return float(np.median(np.diff([e[0] for e in b]))) if len(b) > 1 else 0.0


def period(x, b):
    """A burst's step period, s: the strongest repeat in its loudness between 0.2 and 1.0 s. Onset
    spacing alone halves it on gravel and leaves, where the crunch after each heel fires again."""
    a, e = int(b[0][0] * SR), int(b[-1][1] * SR)
    hop = int(0.005 * SR)
    env = np.sqrt(np.array([np.mean(x[i:i + hop] ** 2) for i in range(a, e - hop, hop)]) + 1e-12)
    env = np.log(env) - np.log(env).mean()
    ac = np.correlate(env, env, "full")[len(env) - 1:]
    lo, hi = int(0.2 / 0.005), min(len(ac) - 1, int(1.0 / 0.005))
    if hi <= lo: return interval(b)
    return (lo + int(np.argmax(ac[lo:hi]))) * 0.005


def strongest(x, b, p):
    """One onset per step: the loudest within each stretch of 0.6 of the period."""
    lvl = [np.abs(x[int(s * SR):int(min(t, s + 0.05) * SR) + 1]).max() for s, t in b]
    order = sorted(range(len(b)), key=lambda i: -lvl[i]); keep = []
    for i in order:
        if all(abs(b[i][0] - b[k][0]) >= 0.6 * p for k in keep): keep.append(i)
    return [b[i] for i in sorted(keep)]


def sections(x):
    """(gait, [steps]) for one file, from the step period of each burst."""
    bs = [strongest(x, b, period(x, b)) if len(b) >= 10 else b for b in bursts(events(x))]
    out = []
    big = [b for b in bs if len(b) >= 8]
    if bs and len(bs[0]) <= 2 and big and interval(big[0]) < 0.45:          # a landing file
        out.append(("land", bs[0][:1]))
        runs = [b for b in bs[1:] if len(b) >= 8]
        if len(runs) == 1: out.append(("run", runs[0]))
        elif runs: out += [("jog", runs[0]), ("run", runs[-1])]
        return out
    walked = False
    for b in bs:
        iv = interval(b)
        if len(b) >= 8:
            gait = "walk" if iv >= 0.45 else ("jog" if iv >= 0.33 else "run")
            out.append((gait, b)); walked = walked or gait == "walk"
        elif walked:
            out.append(("scuff", b))                                           # after the walk
    return out


def cut(x, steps, gait):
    """One array per step: from just before its onset to the next one, no longer than the gait allows."""
    takes = []
    for k, (t, _) in enumerate(steps):
        a = max(0, int((t - 0.01) * SR))
        nxt = steps[k + 1][0] - 0.01 if k + 1 < len(steps) else t + LONGEST[gait]
        b = min(len(x), int(min(nxt, t + LONGEST[gait]) * SR))
        y = x[a:b].copy()
        if len(y) < int(0.05 * SR): continue
        f = min(len(y) // 4, int(0.015 * SR))
        y[-f:] *= np.linspace(1, 0, f)
        y[:int(0.002 * SR)] *= np.linspace(0, 1, int(0.002 * SR))
        takes.append(y)
    return takes


def impact_db(y):
    """A take's loudest 20 ms, dB full scale: the figure the game levels takes by (TakeLevels)."""
    w = int(0.020 * SR)
    if len(y) <= w: return float(20 * np.log10(np.sqrt(np.mean(y * y)) + 1e-12))
    p = np.convolve(y * y, np.ones(w) / w, "valid")
    return float(10 * np.log10(p.max() + 1e-24))


def write(path, y):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with tempfile.NamedTemporaryFile(suffix=".wav") as tmp:
        sf.write(tmp.name, y.astype(np.float32), SR)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", tmp.name, "-c:a", "libvorbis", "-q:a", "5", path], check=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="/tmp/openfps-footstep-bank")
    ap.add_argument("--install", action="store_true", help="write into OpenFPS.Client/ASSETS/SOUNDS")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    out = os.path.join(ROOT, "OpenFPS.Client/ASSETS/SOUNDS") if args.install else args.out
    sets = json.load(open(os.path.join(ROOT, "tools/footstep_sets.json")))

    bank = {}                                           # (material, shoe, gait) -> takes
    for s in sets:
        if not s.get("material") or not s.get("shoe") or s["shoe"] == "unknown":
            print(f"skip {s['set']}: not labelled"); continue
        got = {}
        for tr in s["tracks"]:
            x = load(s["pack"], tr)
            for gait, steps in sections(x):
                takes = cut(x, steps, gait)
                if takes:
                    peak = max(np.abs(t).max() for t in takes)
                    got.setdefault(gait, []).extend(t * (10 ** (-1 / 20) / peak) for t in takes)
        for gait, takes in got.items():
            bank.setdefault((s["material"], s["shoe"], gait), []).extend(takes)
        print(f"{s['set']:34} {s['material']:20} {s['shoe']:9} " + " ".join(f"{g} {len(t)}" for g, t in sorted(got.items())))

    if args.dry_run: return 0
    levels = {}
    for key, takes in list(bank.items()):
        # A "step" 18 dB under its folder's median is a quiet stretch the detector took for a footfall,
        # and more than the game will lift (TakeLevels.MaxCorrectionDb). 1.4 % of takes, 2026-09-28.
        med = float(np.median([impact_db(t) for t in takes]))
        bank[key] = [t for t in takes if impact_db(t) >= med - 18]
    for (mat, shoe, gait), takes in sorted(bank.items()):
        for i, y in enumerate(takes, 1):
            if gait == "land":
                p = os.path.join(out, "LANDING", mat, shoe, f"{mat.lower()}_{shoe}_land_{i:02d}.ogg")
            else:
                p = os.path.join(out, "FOOTSTEPS", mat, shoe, gait, f"{mat.lower()}_{shoe}_{gait}_{i:02d}.ogg")
            write(p, y)
            levels.setdefault(os.path.dirname(p), {})[os.path.basename(p)] = round(impact_db(y), 2)
    # The folders the game reads today: the default shoe's walk and landing, straight under the material.
    for mat in sorted({m for m, _, _ in bank}):
        for kind, gait in (("FOOTSTEPS", "walk"), ("LANDING", "land")):
            shoe = next((sh for sh in DEFAULT_SHOES if (mat, sh, gait) in bank), None)
            if shoe is None: continue
            src = os.path.join(out, kind, mat, shoe) if gait == "land" else os.path.join(out, kind, mat, shoe, gait)
            dst = os.path.join(out, kind, mat)
            for f in os.listdir(dst):
                if os.path.isfile(os.path.join(dst, f)): os.unlink(os.path.join(dst, f))
            for f in sorted(os.listdir(src)):
                if f.endswith(".ogg"): shutil.copy(os.path.join(src, f), os.path.join(dst, f))
            levels[dst] = dict(levels.get(src, {}))
    for d, lv in levels.items():
        with open(os.path.join(d, "levels.json"), "w") as fh: json.dump(lv, fh, indent=0, sort_keys=True)
    n = sum(len(t) for t in bank.values())
    print(f"\n{out}: {len({m for m, _, _ in bank})} materials, {len(bank)} shoe-gait folders, {n} takes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
