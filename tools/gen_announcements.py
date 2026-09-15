#!/usr/bin/env python3
"""
Renders spoken announcements to WAV with espeak-ng, into OpenFPS.Client/ASSETS/SOUNDS/ANNOUNCE.

Spoken audio that a MAP plays is not the same thing as the screen reader. The screen reader speaks
to you; a public-address system speaks into the world, from a position, off the walls, with the
distance and the delay that implies — so it has to be a sound file like any other, placed like any
other. Rendering it here rather than hand-recording it keeps the words in version control next to
the map that says them, and makes a new announcement one line.

Run after editing the table. The WAVs are OUTPUTS.
"""
import os, subprocess, shutil, sys

OUT = os.path.join(os.path.dirname(__file__), "..", "OpenFPS.Client", "ASSETS", "SOUNDS", "ANNOUNCE")

# id -> what it says. The id is what a map's SoundEmitter.SoundId refers to: "ANNOUNCE/<id>".
LINES = {
    "st_louis_welcome": "Welcome to the Saint Louis raceway.",
}

# A PA is not a person in the room: slower, lower and flatter than conversational speech, because
# that is what survives a horn on a pole and two hundred metres of open air.
VOICE, SPEED, PITCH, AMP = "en-us", 145, 40, 180

def main():
    exe = shutil.which("espeak-ng") or shutil.which("espeak")
    if not exe:
        print("espeak-ng is not installed; cannot render announcements.", file=sys.stderr)
        return 1
    os.makedirs(OUT, exist_ok=True)
    for name, text in LINES.items():
        path = os.path.normpath(os.path.join(OUT, name + ".wav"))
        subprocess.run([exe, "-v", VOICE, "-s", str(SPEED), "-p", str(PITCH), "-a", str(AMP),
                        "-w", path, text], check=True)
        print(f"{path}: {os.path.getsize(path)} bytes  \"{text}\"")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
