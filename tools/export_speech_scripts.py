#!/usr/bin/env python3
"""Exports the scripted phone calls and two-person conversations to OpenFPS.Common/Speech/scripts.json.

    tools/export_speech_scripts.py [lines dir, default ~/npc-lines-2026-09-28]

The turn order lives in the line lists Cody generates from (phone.py CALLS, pairs.py PAIRS); the
recordings are in the game's catalogue (OpenFPS.Common/Speech/voices.csv). Each turn is matched to a
recording by its words, per voice. A call is kept for every voice that recorded all of its lines; a
conversation for the voice that recorded its A side and the one that recorded its B side, when every
spoken turn was recorded. Cues like "[laughs]" become a short pause until they are recorded.
"""
import csv, json, os, re, sys, importlib.util

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LINES = os.path.expanduser(sys.argv[1] if len(sys.argv) > 1 else "~/npc-lines-2026-09-28")


def load(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(LINES, name + ".py"))
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m); return m


def norm(t): return re.sub(r"[^a-z0-9]+", " ", t.lower()).strip()


takes = {}
with open(os.path.join(ROOT, "OpenFPS.Common/Speech/voices.csv")) as f:
    for r in csv.DictReader(f):
        takes.setdefault((r["category"], r["voice"]), {})[norm(r["text"])] = r["line"]

out = {"calls": [], "pairs": []}
for name, call in load("phone").CALLS.items():
    voices = {}
    for (cat, voice), bytext in takes.items():
        if cat != name: continue
        turns, ok = [], True
        for t in call["turns"]:
            if isinstance(t, (int, float)): turns.append({"pause": float(t)})
            elif norm(t) in bytext: turns.append({"line": bytext[norm(t)]})
            else: ok = False; break
        if ok: voices[voice] = turns
    if voices: out["calls"].append({"name": name, "voices": voices})

for name, pair in load("pairs").PAIRS.items():
    sides = {}
    for side in ("a", "b"):
        cat = f"{name}_{side}"
        for (c, voice), bytext in takes.items():
            if c == cat: sides[side] = (voice, bytext)
    if len(sides) < 2: continue
    turns, ok = [], True
    for t in pair["turns"]:
        who, text = t[0], t[1]          # a third field, "cut", marks an interruption
        side = who.lower()
        if text.startswith("["): turns.append({"who": who, "cue": text.strip("[]")}); continue
        line = sides[side][1].get(norm(text))
        if line is None: ok = False; break
        turns.append({"who": who, "line": line, **({"cut": True} if len(t) > 2 else {})})
    if ok: out["pairs"].append({"name": name, "a": sides["a"][0], "b": sides["b"][0], "turns": turns})

path = os.path.join(ROOT, "OpenFPS.Common/Speech/scripts.json")
with open(path, "w") as f: json.dump(out, f, indent=1)
print(f"{path}: {len(out['calls'])} calls ({sum(len(c['voices']) for c in out['calls'])} voice versions), "
      f"{len(out['pairs'])} conversations between {len({(p['a'], p['b']) for p in out['pairs']})} pairs of voices")
