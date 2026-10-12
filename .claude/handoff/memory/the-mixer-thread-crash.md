---
name: the-mixer-thread-crash
description: SOLVED session 19 — DropSend disconnected a reverb send through the wrong reverb unit; FMOD does not check, the owner's list goes one short of its count, the next honest disconnect faults at 0x7c
metadata:
  type: project
---

**SOLVED 2026-09-20 (session 19).** Full write-up: `docs/THE_MIXER_THREAD_CRASH.md` (top section).
Committed as the fix plus the instruments that found it.

**The bug:** `UpdateReverbRouting` looked the fade-out bus up again from a region id
(`TryGetReverbInput(active.CurrentRegionId)`), and since f43df69 (2026-09-19 16:56) that id holds
`crossRegionId` — the GLOBAL id whenever a source is in the listener's own room — while the send was
made into `listenerRegionId`'s bus. A city names its outdoors, so the global id resolved to the
outdoor bus, and `DropSend` asked the OUTDOOR reverb to disconnect a connection into a ROOM reverb.

**What FMOD does with that (read from the disassembly, proved by `AudioLab --foreign-disconnect`):**
`DSP::disconnectFrom(target, conn)` is queued as `(this, target, conn)`; the executor checks only
`conn->source == target`, unlinks the node from WHICHEVER list holds it, and decrements the count on
`this`. Owner: list one short of its count, silently. Wrong unit: count one too low. Logging build
says nothing — every handle is valid. The owner's next honest disconnect reaches count 1 / empty list
and the "cache my single input" step reads the head's null data at 0x7c. Same walk with a list that
no longer closes = the hang. Both reproduced in the lab (`--send-churn ownroom` hung in < 1 s).

**The fix:** the unit each live send feeds is stored beside the connection (`ReverbBus`,
`SourceReverbBus`) and is what it fades out of; `DropSend` asks `DSPConnection::getOutput` for the
owner and counts disagreement — `N send drop(s) on the wrong bus` on the `Mixer load` line, must be 0.
Region ids remain a change detector only. Nothing audible changed.

**Why the lab never saw it:** no lab map had a bus for the global id, so `TryGetReverbInput(-1)`
was false and the branch never ran. The harness now has one (`ownroom`).

**Session-18 readings corrected:** `+0x1a8` is `mNumInputs` (FMOD's own assertion string
`"mNumInputs != 32767"` in `addInputInternal` names it), not a kind tag; `+0x78` is the INPUT list;
a connection's `+0x58` is source, `+0x60` sink. `tools/read_core_dsp.py <dump> [libfmodL.so]` prints
the queued record, the units BY NAME (`+0xf0` → the library's strings), count-vs-list for each.

**How to apply next time:** disassemble the whole faulting FUNCTION and ask what invariant it keeps
(here: a count decremented beside an unlink — they can only disagree if something unlinks without
counting). Walk the stack above the fault to learn who called it (a queued command WE issued, not a
channel end). Then test the disassembly's claim directly before theorising about our code — the
first `--foreign-disconnect` was flawed (the wrong unit had no inputs, and the executor returns early
then); the second version died in a second. Six FMOD-side teardown scenarios (`--send-drift`) and 69
million reconnects (`--send-window`) were all clean: **the bug was in an argument, not in FMOD.**
See [[mixer-callbacks-must-not-free]], [[why-did-the-client-stop]], [[audio-lab-instruments]].
