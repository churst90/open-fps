#!/usr/bin/env python3
"""Imports a folder of NPC voice lines into the game.

    tools/import_npc_voices.py inbox/npc-voices-2026-09-26 \
        --clones=seanterry,jimdale,joeb,joel,tim,ben,alec,fluke,camel,tyler

Reads the folder's manifest.csv (voice, kind, category, file, text, seconds, whisper_match) and:

- encodes every line to OpenFPS.Client/ASSETS/SOUNDS/VOICES/<voice>/<name>.ogg (Vorbis, mono, 48 kHz,
  the mixer's rate, so nothing is resampled at play time);
- writes OpenFPS.Common/Speech/voices.csv, the list the server chooses lines from.

Only voices whose folder is still in the inbox are imported: deleting a voice's folder takes it out
of the game, and its recordings are removed from ASSETS. A manifest row whose file is gone is skipped.

Cloned voices (kind "cloned") copy real people, so they are left out unless named with --clones.
Cody's choice on 2026-09-27: seanterry, jimdale, joeb, joel, tim, ben, alec, fluke, camel, tyler.

Stories: every .wav in a <voice>_preview folder is imported as category "story" for that voice. The
words come from stories.json in the source folder ({"<file name>": "<text>"}), for text players.

Voicing: the low end of each voice is brought down to where a real talker at its pitch has it. The
lines are close-miked and carry more low end than a person across a street does; measured on
2026-09-27, tim and frank had 3-4 dB too much below 150 Hz for a man and linda, Vivian and Sohee 7-9 dB
too much for a woman. The reference is the long-term average speech spectrum (Byrne et al. 1994, JASA
96, Table II: male and female third-octave levels), interpolated by the voice's median fundamental.
Each band below 500 Hz is compared with the voice's own 500 Hz - 2 kHz, and only an excess is cut, up
to 12 dB, smoothed over neighbouring bands. Everything above is left alone: that is the person, and
how hard they are talking. What was measured and applied is written to
OpenFPS.Common/Speech/voicing.csv; a voice whose row changes is re-encoded.

Levels are not touched here; the client rescales every line to Speech.BufferRmsDbfs when it loads it.
A line is re-encoded when its .wav is newer than its .ogg, so a regenerated voice replaces the old one.
"""
import csv
import json
import os
import shutil
import subprocess
import sys
import math
import tempfile
import wave
from array import array

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "OpenFPS.Client", "ASSETS", "SOUNDS", "VOICES")
CATALOGUE = os.path.join(ROOT, "OpenFPS.Common", "Speech", "voices.csv")
VOICING = os.path.join(ROOT, "OpenFPS.Common", "Speech", "voicing.csv")

# Byrne et al. 1994, Table II: third-octave levels normalised to 70 dB overall, averaged over languages.
BANDS = [63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500,
         3150, 4000, 5000, 6300, 8000, 10000]
LTASS_MALE = [38.6, 43.5, 54.4, 57.7, 56.8, 58.2, 59.7, 60.0, 62.4, 62.6, 60.6, 55.7, 53.1, 53.7,
              52.3, 48.7, 48.9, 47.0, 46.0, 44.4, 43.3, 42.4, 41.9]
LTASS_FEMALE = [37.0, 36.0, 37.5, 40.1, 53.4, 62.2, 60.9, 58.1, 61.7, 61.7, 60.4, 58.0, 54.3, 52.3,
                51.7, 48.8, 47.3, 46.7, 45.3, 44.6, 45.2, 44.9, 45.0]
# Typical median fundamentals, men and women, Hz: the voice's own pitch places it between the two.
F0_MALE, F0_FEMALE = 120.0, 210.0
LOW_BELOW_HZ = 500           # the bands that are corrected
CORE_HZ = (500, 2000)        # the bands they are measured against
MAX_CUT_DB = 12.0


def _power_sum_db(levels):
    return 10 * math.log10(sum(10 ** (l / 10) for l in levels))


def _band_rms_db(wav, lo, hi):
    out = subprocess.run(["sox", wav, "-n", "sinc", f"{lo:.1f}-{hi:.1f}", "stat"],
                         capture_output=True, text=True).stderr
    for line in out.splitlines():
        if line.startswith("RMS     amplitude"):
            return 20 * math.log10(max(float(line.split()[-1]), 1e-9))
    raise RuntimeError("sox gave no RMS for " + wav)


def _median_f0(wav):
    """Median fundamental of the voiced frames, by normalised autocorrelation at 8 kHz."""
    raw = subprocess.run(["sox", wav, "-t", "raw", "-r", "8000", "-e", "signed", "-b", "16", "-c", "1", "-",
                          "sinc", "-900"], capture_output=True).stdout
    x = array("h"); x.frombytes(raw[: len(raw) // 2 * 2])
    frame, lo_lag, hi_lag = 320, 8000 // 400, 8000 // 70
    starts = list(range(0, len(x) - frame - hi_lag, frame))
    energy = sorted(((sum(v * v for v in x[s:s + frame]), s) for s in starts[::max(1, len(starts) // 600)]), reverse=True)
    f0s = []
    for e, st in energy[: len(energy) // 2][:200]:
        seg = x[st:st + frame + hi_lag]
        best, best_lag = 0.0, 0
        for lag in range(lo_lag, hi_lag + 1):
            num = sum(seg[i] * seg[i + lag] for i in range(frame))
            den = math.sqrt(e * sum(seg[i + lag] * seg[i + lag] for i in range(frame))) or 1.0
            if num / den > best:
                best, best_lag = num / den, lag
        if best > 0.5 and best_lag:
            f0s.append(8000.0 / best_lag)
    f0s.sort()
    return f0s[len(f0s) // 2] if f0s else 150.0


def voicing_for(paths):
    """(median f0, [correction dB per band]) for one voice's lines."""
    with tempfile.TemporaryDirectory() as tmp:
        joined = os.path.join(tmp, "all.wav")
        subprocess.run(["sox"] + paths[:120] + ["-c", "1", "-r", "44100", joined], check=True)
        f0 = _median_f0(joined)
        measured = [_band_rms_db(joined, f / 2 ** (1 / 6), f * 2 ** (1 / 6)) for f in BANDS]
    w = min(1.0, max(0.0, (f0 - F0_MALE) / (F0_FEMALE - F0_MALE)))
    target = [m + w * (fe - m) for m, fe in zip(LTASS_MALE, LTASS_FEMALE)]
    core = [i for i, f in enumerate(BANDS) if CORE_HZ[0] <= f <= CORE_HZ[1]]
    t_core = _power_sum_db([target[i] for i in core])
    m_core = _power_sum_db([measured[i] for i in core])
    # How far each low band stands above where a real talker at this pitch has it, against the
    # voice's own middle. Only an excess is taken off.
    raw = [min(0.0, (t - t_core) - (m - m_core)) if f < LOW_BELOW_HZ else 0.0
           for t, m, f in zip(target, measured, BANDS)]
    smooth = [sum(raw[max(0, i - 1): i + 2]) / len(raw[max(0, i - 1): i + 2]) for i in range(len(raw))]
    gains = [round(max(-MAX_CUT_DB, g) * 2) / 2 + 0.0 for g in smooth]
    return round(f0), gains


def _filter(gains):
    entries = [f"entry(20,{gains[0]})"] + [f"entry({f},{g})" for f, g in zip(BANDS, gains)] \
              + [f"entry(20000,{gains[-1]})"]
    return "firequalizer=gain_entry='" + ";".join(entries) + "'"


def _read_voicing():
    if not os.path.exists(VOICING):
        return {}
    with open(VOICING, newline="") as f:
        return {r["voice"]: r for r in csv.DictReader(f)}


def seconds(path):
    with wave.open(path) as w:
        return w.getnframes() / w.getframerate()


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    clones = set()
    for a in sys.argv[1:]:
        if a.startswith("--clones="):
            clones |= {c.strip() for c in a[len("--clones="):].split(",") if c.strip()}
    if len(args) != 1:
        print(__doc__)
        sys.exit(1)
    src = args[0]
    with open(os.path.join(src, "manifest.csv"), newline="") as f:
        rows = list(csv.DictReader(f))
    texts = {}
    if os.path.exists(os.path.join(src, "stories.json")):
        with open(os.path.join(src, "stories.json")) as f:
            texts = json.load(f)

    kinds = {r["voice"]: r["kind"] for r in rows}
    kept, skipped, missing = [], set(), set()
    for r in rows:
        if not os.path.isdir(os.path.join(src, r["voice"])):
            missing.add(r["voice"])
            continue
        if r["kind"] == "cloned" and r["voice"] not in clones:
            skipped.add(r["voice"])
            continue
        path = os.path.join(src, r["voice"], r["file"])
        if os.path.exists(path):
            kept.append(dict(r, path=path))

    voices = {r["voice"] for r in kept}
    for voice in sorted(voices):
        preview = os.path.join(src, voice + "_preview")
        if not os.path.isdir(preview):
            continue
        for name in sorted(os.listdir(preview)):
            if not name.endswith(".wav"):
                continue
            stem = os.path.splitext(name)[0]
            path = os.path.join(preview, name)
            kept.append({"voice": voice, "kind": kinds[voice], "category": "story",
                         "file": "story_" + stem.removeprefix(voice + "_").removeprefix("story_") + ".wav",
                         "text": texts.get(stem, "(a story)"), "seconds": f"{seconds(path):.2f}", "path": path})

    previous = _read_voicing()
    voicing, changed = {}, set()
    for voice in sorted(voices):
        paths = sorted(r["path"] for r in kept if r["voice"] == voice and r["category"] != "story")
        f0, gains = voicing_for(paths)
        voicing[voice] = (f0, gains)
        row = previous.get(voice)
        if row is None or [float(row[str(b)]) for b in BANDS] != gains:
            changed.add(voice)
        print(f"  {voice:12s} f0 {f0:3d} Hz   " + " ".join(f"{g:+.1f}" for g in gains[:8]) + " ...")

    encoded = 0
    for r in kept:
        out = os.path.join(ASSETS, r["voice"], os.path.splitext(r["file"])[0] + ".ogg")
        if r["voice"] not in changed and os.path.exists(out) and os.path.getmtime(out) >= os.path.getmtime(r["path"]):
            continue
        os.makedirs(os.path.dirname(out), exist_ok=True)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", r["path"], "-af", _filter(voicing[r["voice"]][1]),
                        "-ac", "1", "-ar", "48000", "-c:a", "libvorbis", "-q:a", "4", out], check=True)
        encoded += 1

    with open(VOICING, "w", newline="") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["voice", "f0"] + [str(b) for b in BANDS])
        for voice in sorted(voicing):
            f0, gains = voicing[voice]
            w.writerow([voice, f0] + gains)

    removed = []
    if os.path.isdir(ASSETS):
        for d in sorted(os.listdir(ASSETS)):
            if d not in voices and os.path.isdir(os.path.join(ASSETS, d)):
                shutil.rmtree(os.path.join(ASSETS, d))
                removed.append(d)
    # A line dropped from a voice that is still here.
    wanted = {(r["voice"], os.path.splitext(r["file"])[0]) for r in kept}
    for voice in voices:
        for name in os.listdir(os.path.join(ASSETS, voice)):
            if (voice, os.path.splitext(name)[0]) not in wanted:
                os.remove(os.path.join(ASSETS, voice, name))

    os.makedirs(os.path.dirname(CATALOGUE), exist_ok=True)
    with open(CATALOGUE, "w", newline="") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["voice", "kind", "category", "line", "text", "seconds"])
        for r in kept:
            w.writerow([r["voice"], r["kind"], r["category"], os.path.splitext(r["file"])[0],
                        r["text"], r["seconds"]])

    print(f"{len(kept)} lines from {len(voices)} voices; {encoded} encoded.")
    if skipped:
        print("Left out (cloned voices not named with --clones): " + ", ".join(sorted(skipped)))
    if missing:
        print("In the manifest but no folder (removed): " + ", ".join(sorted(missing)))
    if removed:
        print("Removed from the game: " + ", ".join(removed))


if __name__ == "__main__":
    main()
