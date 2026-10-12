---
name: tmp-is-eight-gigabytes-of-ram
description: /tmp is an 8 GB tmpfs holding every build, every log and the crash dumps — fill it and everything fails at once
metadata:
  type: project
---

**Learned the hard way, session 17.** `/tmp` on this machine is a **tmpfs of 8 GB**, and because the
repo is on ntfs3 ([[build-on-tmpfs]]) it holds: every `--artifacts-path` build tree (the GTK client
alone is 1.2 GB), every client and server log, the capture WAV, and — until this was fixed — the
crash dumps.

It filled. The first real crash capture printed
`[createdump] Error writing data to dump file: No space left on device (28)` and the stack was lost.

**What fills it:**
- Stale build trees. There were ELEVEN `/tmp/openfps-*` artifact dirs; four are in use
  (`-lab`, `-srv`, `-gtk`, `-build`) and the rest were old experiments.
- Old session scratch under `/tmp/claude-1000/<project>/<session-uuid>/` — 2 GB of it from earlier
  sessions of this same project.
- The acoustic trace log. With `OPENFPS_AUDIO_DEBUG=1` the client writes one `[REVERB]` line PER BUS
  per second; at 185 buses that was about 2 MB a minute. (The bus cap in
  [[the-empty-grid-answer]] cut that to 24.)

**It filled again 2026-09-29:** the first `publish-windows.sh` (the 442 MB of assets copied twice,
plus a zip) went to /tmp and Cody's GTK build failed mid-launch. Publish output now goes to
`~/.cache/openfps-publish` (btrfs home, no ntfs3 hang). Anything that copies ASSETS must not go to
/tmp. Note `pkill -f <pattern>` inside a bash command matches that command's own line and kills it.

**How to apply:** `df -h /tmp` BEFORE a long listening session — `run-gtk-client.sh` now prints it
and warns under 512 MB. Crash dumps go to `$HOME/openfps-crashes` (override with
`OPENFPS_CRASHDIR`), never to /tmp. `dotnet-dump` is installed; read one with
`dotnet-dump analyze <file>`.

**Why:** a full tmpfs does not announce itself — builds, logs, dumps and the game all start failing
at the same moment and none of them says why.

10-10: with 7 agents building, /tmp hit 88 % and a test failed "No space left on device". The main session's merge
builds now go to ~/.cache/ofps-main (btrfs /home, 234 GB free; not the repo, so the ntfs3 rule is kept). Agents keep
/tmp/ofps-<name> and delete it when done. Cody's /tmp/openfps-gtk and /tmp/openfps-srv are his running builds: never touch.
