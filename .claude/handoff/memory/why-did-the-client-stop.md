---
name: why-did-the-client-stop
description: Four ways the client ends look identical from the chair; what each leaves behind, and the instruments that tell them apart
metadata:
  type: project
---

**Session 17.** "It crashed" was reported four times and was not the same thing twice. What a
screen-reader user perceives is the sound stopping; the four causes need completely different fixes,
and until these instruments existed there was nothing to tell them apart.

| what happened | log ends | core/dump | exit status |
|---|---|---|---|
| **Native crash (SIGSEGV)** | mid-line | dump in `~/openfps-crashes` + `[createdump]` lines | 139 |
| **Stall** (not a crash at all) | keeps going, slowly | none | still running |
| **SIGPIPE** — the terminal's reader died | mid-line | **nothing at all** | 141, and the shell often dies too |
| **Clean exit** — a window closed | mid-line | none | 0 |

**The SIGPIPE one is the trap.** The client logged to the console and `run-gtk-client.sh` captured it
with `| tee`. .NET installs no SIGPIPE handler and the default action is TERMINATE — so if whatever
reads stdout goes away (terminal closed, or an emulator that stops reading after you alt-tab), tee
dies, the client takes SIGPIPE on its next log line, and the process is gone: no exception, no core,
no dump, log ending mid-sentence. **Indistinguishable from the native crash we were actually
hunting.** Fixed two ways: `Program.Main` ignores SIGPIPE, and Serilog writes its own file
(`OPENFPS_LOG`) so a dead console cannot take the log with it.

**What the launcher does now:** prints free space on /tmp and warns under 512 MB; `ulimit -c 0`
(the raw cores were 1-2 GB EACH, written into the client's cwd on the tmpfs — see
[[tmp-is-eight-gigabytes-of-ram]]); minidumps to `$HOME/openfps-crashes`; and it ALWAYS reports the
exit status with what the signal means, not only on failure.

**What the client does now:** logs `ProcessExit`, `UnhandledException`, `UnobservedTaskException`,
the GTK main loop returning, and a window close request. Silence in that file is now itself evidence
— it means a signal, not a shutdown.

**How to apply:** read the exit line first, then look for a dump, and only then start reading audio
logs. See [[the-empty-grid-answer]] for the stall and [[mixer-callbacks-must-not-free]] for the
segfault.
