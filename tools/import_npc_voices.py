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

Levels are not touched here; the client rescales every line to Speech.BufferRmsDbfs when it loads it.
A line is re-encoded when its .wav is newer than its .ogg, so a regenerated voice replaces the old one.
"""
import csv
import json
import os
import shutil
import subprocess
import sys
import wave

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "OpenFPS.Client", "ASSETS", "SOUNDS", "VOICES")
CATALOGUE = os.path.join(ROOT, "OpenFPS.Common", "Speech", "voices.csv")


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

    encoded = 0
    for r in kept:
        out = os.path.join(ASSETS, r["voice"], os.path.splitext(r["file"])[0] + ".ogg")
        if os.path.exists(out) and os.path.getmtime(out) >= os.path.getmtime(r["path"]):
            continue
        os.makedirs(os.path.dirname(out), exist_ok=True)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", r["path"],
                        "-ac", "1", "-ar", "48000", "-c:a", "libvorbis", "-q:a", "4", out], check=True)
        encoded += 1

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
