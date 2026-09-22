# The Mixer Thread Crash: What We Know

**2026-09-20, session 19. SOLVED — reproduced in the lab, fixed, verified.** The investigation
below it is kept as written, because most of it is correct and the wrong turns are the useful part.

---

## The answer: a valid FMOD call with the wrong `this`

`DropSend` disconnected a reverb send by asking a reverb unit that did not own the connection:

```csharp
target.disconnectFrom(source, conn);   // target: the bus a REGION ID resolved to, not the bus the send was made into
```

FMOD queues `DSP::disconnectFrom(target, connection)` to the mixer as `(this, target, connection)`.
The executor — `DSPI::disconnectFrom`, the function every dump faulted in — checks exactly one
thing: that the connection's source DSP is `target`. It then unlinks the connection's node from
**whichever input list holds it** and decrements the input count on **`this`**. Given the wrong
unit, the real owner's list is one shorter than its count, silently, and the wrong unit's count is
one too low. The logging build says nothing: every handle is valid. The next honest disconnect on
the owner takes its count to 1 with an empty list, and the executor's last step — *"if I have
exactly one input, cache its source"* — reads the list head's null data pointer:

```
145dd7:  cmpw   $0x1,0x1a8(%rbx)     ; mNumInputs == 1 ?      (the "kind tag" of session 18 — it is the INPUT COUNT)
145de1:  mov    0x78(%rbx),%rax      ; inputs.head.next       (== &head: the list is empty)
145de5:  mov    0x10(%rax),%rax      ; node->connection       (the head's data slot: 0)
145de9:  testb  $0x4,0x7c(%rax)      ; FAULT at 0x7c
```

Read out of the three logging-build dumps, by name (`+0xf0` of a DSP is its name pointer):

| | victim `this` | its input count / list | the record's `target` | target's outputs |
|---|---|---|---|---|
| 3783706 | `FMOD Reverb` | 1 / **0** | `Channel Fader` | its group, a second `FMOD Reverb`, (the victim) |
| 3815189 | `FMOD Reverb` | 1 / **0** | `Channel Fader` | its group, the same `FMOD Reverb` **twice** |
| 3826938 | `FMOD Reverb` | 1 / **0** | `Channel Fader` | its group, a second `FMOD Reverb`, (the victim) |

Each was a room's reverb unit being asked to drop its last send, in a batch of ~800 queued
commands that contained **one** disconnect. The graph was already broken before it ran.

The hang is the same structure: a mixer walking a list that no longer closes. The lab reproduction
below hangs more often than it crashes.

### Why only the client, and why only since 2026-09-19 16:56

Commit `f43df69` ("Your own footsteps were reverberating outdoors") added `crossRegionId` to
`UpdateReverbRouting`: the listener-side send records `CurrentRegionId = crossRegionId`, which is the
**global id** whenever the source is in the listener's own room — while the send itself is still made
into `listenerRegionId`'s bus. The two were only ever used together as a change detector, which is
fine. But when the listener then left the room, the bus to fade the old send out of was looked up
again **from that id**:

```csharp
if (active.ReverbConnection.hasHandle() && TryGetReverbInput(active.CurrentRegionId, out var oldListenerReverb))
    active.FadingReverbBus = oldListenerReverb;     // the OUTDOOR unit, for a send made into the ROOM
```

A city names its outdoors, so the global id resolves to a bus (`bus=-1(Outside)` in every client
log), the lookup succeeds, and the room's send is handed to the outdoor reverb to disconnect. The
lab maps never had a global-id bus, so `TryGetReverbInput` was false there and the wrong-bus branch
was never taken — which is why 42,000 harness voices could not reproduce what 38 client voices did in
eighteen seconds. Every crash dump is from the evening of the 19th onward.

Own-room sources are the commonest voice there is — your own footsteps — and `/tp` moves the
listener out of every room at once, which is why it was a near-deterministic reproduction.

### The fix (FmodAudioProvider.cs)

- The unit each live send feeds is stored **beside the connection** (`ReverbBus`, `SourceReverbBus`),
  set at every `addInput`, and is what a send fades out of. Region ids stay what they were: a change
  detector. Nothing about the routing, the mixes or the fades changed, so nothing audible did.
- `DropSend` asks the connection itself (`DSPConnection::getOutput`) which unit owns it, disconnects
  through that unit, and counts any disagreement. The count is on the `Mixer load` health line:
  `N send drop(s) on the wrong bus`. **It must read 0.**

### Proof, in the lab

| Instrument | Before | After |
|---|---|---|
| `AudioLab --foreign-disconnect` | one deliberate wrong-unit disconnect between two reverb units, then an honest one: **SIGSEGV in under a second**, logging build silent. Left in deliberately — it documents the mechanism and dies by design. | (the mechanism is FMOD's; the probe still dies) |
| `AudioLab --send-churn ownroom` | the provider, twelve rooms **plus a global-id "Outside" region**, one-shots mostly in the listener's own room, the listener flipping rooms and going outdoors: **hung inside its first second**, one thread at 100 % in `DSPI::disconnectFrom` | survived 60 s, 126,800 one-shots, 0 wrong-bus drops |
| `--send-churn`, `--reap-churn`, tests | | survived; 786 passed, 2 skipped, 0 failed |

### What the earlier sessions had right and wrong

- **Right:** the fault site, the method of reading it out of a core, "the hang and the segfault are
  the same broken structure", the ChannelGroup-attached releases, the 2D/3D refused calls, and the
  suspicion that the reverb routing mutation was the client-only ingredient.
- **Wrong:** the word at `+0x1a8` is not a kind tag, it is `mNumInputs` — FMOD's own assertion string
  `"mNumInputs != 32767"` in `addInputInternal` names it. `+0x78` is the **input** list; a
  connection's `+0x58` is its source and `+0x60` its sink. "A DSP with zero connections that FMOD is
  nonetheless walking" was the symptom described backwards: a DSP with **one** connection in its count
  and none in its list.
- **Wrong, and the lesson:** the open lead was "pooled DSPs get API calls while detached". The
  `--send-window` probe (69 million disconnect/reconnect pairs on 20,000 naturally-ending voices) and
  six teardown scenarios with a count trip-wire (`--send-drift`) cleared every FMOD-side path in an
  hour. What none of them varied was the **argument** we passed. The bug was in a line that read as
  obviously correct; the probe that found it was the one written to test the disassembly's claim
  rather than a theory about our code.

### How it was found, in order

1. Disassemble the fault function, not just the faulting instruction: it was `disconnectFrom`, and
   the "kind tag" was a counter that the same function decrements next to the unlink. Count and list
   can only disagree if something unlinks without counting.
2. Walk the crashing thread's stack (`tools/read_core_fault.py` gives the frame; the return addresses
   above it name the chain): mixer thread → command-queue dispatcher → `disconnectFrom`. So the
   disconnect was one **we queued**, not a channel-end.
3. Read the queued record off the stack (`rbp` of the dispatcher frame; records are `op | size<<8`
   headers), then the DSP objects it names, then their **names** through the library file. Reverb,
   fader, last send.
4. Ask what makes a wrong-unit disconnect: the executor's checks, read from the disassembly. Then
   test that claim directly (`--foreign-disconnect`). Then find who passes the wrong unit.

Steps 2–3 are `tools/read_core_dsp.py <dump> [libfmodL.so]` — it prints the record, the units by
name with count-against-list for each, and the batch's opcode mix. `tools/read_core_fault.py`
remains the entry point for the fault itself.

---

## The investigation as it stood at the end of session 18

The GTK client dies with SIGSEGV on an FMOD mixer thread on the city map, or occasionally hangs
instead. This is the state of the investigation, including the parts that were got wrong.

A companion copy of this document lives at
<https://claude.ai/code/artifact/c8eb24b5-fd16-48b1-a7ce-ed4affb9ea14>.

---

## The fault: FMOD takes the first entry of an empty list

*(Session 18's reading. Correct about where; see the top of this file for why.)*

Every crash is identical: `si_addr = 0x7c`, `SEGV_MAPERR`, trapno 14, err 4 (a user-mode read), on
an FMOD mixer thread, with no managed frames and no Phonon frames on the stack.

The faulting instruction, in `FMOD::DSPI`:

```
12427d:  cmpw   $0x1,0x1a8(%rbx)     ; if this DSP's kind tag is 1
124288:  mov    0x78(%rbx),%rax      ;   rax = outputs.next
12428c:  mov    0x10(%rax),%rax      ;   rax = that entry's owner   <-- NULL
124290:  testb  $0x4,0x7c(%rax)      ;   FAULT: read at 0x7c
12429f:  mov    %rax,0x70(%rbx)      ; cache the result on the DSP
```

The faulting object's own memory says why:

```
dsp+0x70 = 0x0000000000000000     <- the cache slot, just cleared
dsp+0x78 = self+0x78              <- list head: next points at itself
dsp+0x80 = self+0x78              <- list head: prev points at itself
dsp+0x88 = 0                      <- the head sentinel's owner slot
dsp+0x1a8 low word = 1            <- the kind tag, so the branch is taken
```

`next == prev == &head` is the canonical empty intrusive doubly-linked list. FMOD takes
`outputs.first()` without checking that the list is empty, gets the head sentinel back, reads the
sentinel's owner slot — zero — and dereferences it.

**The primary problem is a DSP unit with zero connections that FMOD is nonetheless walking.**

Both symptoms are that one broken structure. The 8-second `GAME LOOP STALLED` hang, dumped
separately, had exactly one running thread — the mixer — entering `libfmod+0x145a20`. The segfault
lands at `+0x145de9`, inside that same function. A null in that walk is the crash; going round for
ever is the hang.

### Reading a fault out of a core

Two traps cost a session each.

- **`createdump`'s ELF cores carry no `NT_SIGINFO` note**, and their `NT_PRSTATUS` registers are
  recorded at dump time — it prints `Target process is alive`. Those registers are the abort
  machinery, not the fault. What is present is the `rt_sigframe` on the crashing thread's stack:
  scan for a `siginfo_t` (signo 11, errno 0, si_code 1 or 2, si_addr at +16), find the `ucontext_t`
  immediately before it by its CR2 (== si_addr) at gregs offset 216, and read the real RIP at
  offset 168.
- **An offset is only meaningful against the lowest mapping of the library.** `NT_FILE` lists
  libfmod as four separate mappings; subtracting the wrong one gave `libfmod+0xac290`, which
  disassembles to a `cmovge` that cannot fault. The true offset was `+0x124290`.

Both are in `tools/read_core_fault.py`. `tools/read_core_threads.py` lists every thread's RIP and
syscall — 202 is futex (idle), a thread with syscall −1 is running.

---

## What is not the problem

**The city map is not too big, and it isn't close.** Measured at the moment of a crash:

| Measure | At the crash | Limit / context |
| --- | --- | --- |
| Active voices | 33–41 | `maxchannels=512` |
| Real channels | 38/38 | none virtualised |
| Mixer DSP load | 13.7% | one eighth of a core |
| Engine/echo voices | 22–28 | of those 38 |
| Run length before death | 18–26 s | 404 s on one build |

We are using 8% of the channel budget. Nothing about the map needs to shrink, and a game of this
shape is entirely reasonable to build on FMOD.

**FMOD no longer reports any API misuse.** Its validating build (`libfmodL.so`) found real faults at
the start of the session and, after they were fixed, reports **0 ERROR and 0 WARNING in 137,000
trace lines** — across all four harnesses and in the live client run that still crashed. So whatever
remains is not a call FMOD considers invalid.

**There is no sign of a heap overwrite.** The faulting object's fields are internally consistent: a
healthy empty list, a cleared cache slot, a valid kind tag. What is wrong is *state*, not bytes — a
unit is detached and something still traverses it.

**The sound cache is not leaking.** 867 distinct files created, each logged twice by FMOD, 1,734
lines total. One creation per file, as intended.

---

## What was genuinely found and fixed

FMOD's validating build found all of these in single runs. None of them stopped the crash; all of
them are real.

### Units released while still attached to a ChannelGroup

```
124x  DSPI::release WARNING. Failed to release because unit is still attached.
                             Use removeDSP function first.
  1x  closeInternal assertion: 'connectionsRemaining == 0' failed
```

**A refused release is not a no-op.** FMOD returns without freeing, and the unit stays in the graph
while the group it is attached to gets released out from under it.

The key distinction: **an attachment to a ChannelControl is not a connection to another DSP.**
`disconnectAll` satisfies the second and does nothing about the first. Only `removeDSP` — or, for a
Channel but *not* a ChannelGroup, `stop()` — satisfies the first.

Three places had it, none in the voice path everyone suspected:

- `ReleaseReverbUnits` — one reverb unit per room, 124 of them, never freed for the life of the
  process.
- The three master-group units: boundary probe, loudness meter, master limiter.
- `MasterTap`, the capture tap, which never stored the group it was added to.

Count went **125 → 0** and stays there.

### 113,228 refused 3D calls in 25 seconds

91% of FMOD's entire trace was one line:

```
fmod_channelcontrol.cpp:715  FMOD_RESULT = 40 --
  Tried to call a command on a 2d sound when the command was meant for 3d sound.
```

A voice with a Steam Audio binaural stage is switched to 2D **on purpose** — a 3D channel downmixes
the finished HRTF pair back to mono. Then `set3DMinMaxDistance` and the 3D cone were called on it
anyway: once at creation, and again **every frame for every voice**. About 4,500 refused calls a
second, each taking FMOD's lock on the game thread against a mixer wanting the same lock.

None of it ever did anything — the HRTF path applies min/distance and the cone by hand in
`ApplyAcousticFilters`, which is *why* the channel is 2D. Guarded on `SaState == null`; count is 0.

This is the most likely explanation of the 8-second game-loop stall, though that is not proven.

### Teardown order

Now: `removeDSP` every pooled DSP off the live channel → `stop()` → `setUserData(0)`,
`disconnectAll(true,true)`, `release()` the owned DSP → clear the handle.

---

## Two confident theories, both wrong

Both were argued from the code and both were killed by measuring. Write the probe before the theory.

### "A pooled DSP stays attached to a Channel FMOD recycled"

This survived two rounds of work and was wrong. `AudioLab --ended-channel` asks FMOD directly — play
a one-shot with a DSP on it, let it **finish**, then look:

```
while playing               inputs=1  outputs=1  active=True
isPlaying -> False (ERR_INVALID_HANDLE)
after it ended on its own   inputs=0  outputs=0  active=False
removeDSP on the ended channel: ERR_INVALID_HANDLE
disconnectAll:                  OK
release:                        OK
```

**FMOD detaches the unit itself when a Channel ends.** So `removeDSP` failing with
`ERR_INVALID_HANDLE` on a finished voice is harmless, the "N DSP(s) stuck on a dead channel" counter
measures a benign condition, and pooling was never the bug.

What *was* real in the same area is the ChannelGroup case above — a group does not auto-detach the
way a Channel does.

### "The voice reaper releases a DSP still wired into the graph"

The reaper genuinely did release without stopping the channel, and fixing it took a run from 16–20 s
to **404 s** — which read as confirmation. It was not: the next run crashed in 26 s, and the
underlying claim (that the DSP was still wired in) is exactly what the probe above disproved.

A large change in time-to-crash is not proof of cause when the failure is load-dependent and
probabilistic.

### Also investigated and cleared

- **Collected callback delegates.** Every `DSP_READ_CALLBACK` is a `static readonly` field —
  properly rooted. Checked all ten processors.
- **Managed frames on the crashing stack.** None. The mixer is inside FMOD's own code, not inside
  ours.
- **Steam Audio / Phonon.** No Phonon frames on the crashing stack either.

---

## Instruments

| Instrument | What it answers |
| --- | --- |
| `./run-gtk-client.sh fmodlog [all]` | FMOD's validating build names its own complaints; swaps `libfmodL.so` in and restores it on exit via a trap, even on SIGSEGV |
| `tools/read_core_fault.py <dump> <tid>` | The real fault address and faulting RIP, out of the `rt_sigframe` on the stack |
| `tools/read_core_threads.py <dump>` | Every thread's RIP and syscall — finds the one running thread in a hang |
| `AudioLab --ended-channel` | What FMOD actually does to a DSP when its Channel ends |
| `AudioLab --reap-churn` | Voices that end on their own and are collected by the reaper |
| `AudioLab --scene-churn` / `--provider-churn` / `--physical-churn` | Voices, machines, aircraft, reverb buses under load |

### FMOD ships a validating build and nobody looked for it

`libfmodL.so` sits next to `libfmod.so` in the SDK and is a drop-in replacement. **Three sessions
went into reading page-fault addresses out of core files before anyone tried it.** It found real
faults in one 45-second run.

Two things about arming it are easy to get wrong, and both cost a run:

- **`FMOD.Debug.Initialize` must run before `System::create`** or it does nothing at all. It now
  lives in `FmodDebugLog.ArmFromEnvironment()`, called from each program's entry point. The first
  attempt put it down among AudioLab's mode handlers, and every mode above it silently got FMOD's
  default TTY logging instead. `RESULT.OK` means the logging build is loaded; `ERR_UNSUPPORTED`
  means the plain one is.
- **Log through the CALLBACK, never FILE mode.** FMOD's file output is buffered, and the run this
  exists for ends in a SIGSEGV. The first client run under it produced a **zero-byte log**, which is
  indistinguishable from "FMOD found nothing wrong".

### A harness that only tests the safe path proves nothing

Every churn in `ProviderOrbit` stopped its voices with `StopSound`, which stops the channel itself —
the safe path. That is why 42,000 voices never reproduced what the client hit in sixteen seconds:
**no voice in any harness had ever simply ended.** `--reap-churn` was written to close that gap.

---

## Why it stays elusive

**Nothing in the lab reproduces it.**

| Harness | Voices | Result |
| --- | --- | --- |
| `--scene-churn` (40 s) | 27,465 | survived |
| `--reap-churn` (20 s) | 22,608 | survived |
| `--provider-churn` | 14,320 | survived |
| `--physical-churn` | 3,664 | survived |
| **GTK client** | **38** | **SIGSEGV in 18 s** |

42,000 voices survive headless; 38 voices in the client die in eighteen seconds. `--reap-churn` was
written specifically to cover the one path the others missed, and it survives with the fix in **and**
with the old behaviour restored (`OPENFPS_OLD_DETACH=1`).

Something is present in the client and absent in every harness, and **isolating that difference is
the unfinished work.** Candidates not yet eliminated:

- The client runs a full Steam Audio simulator (occlusion, pathing, probe bake) on a 4,663-box
  scene; the harnesses do not.
- The client mutates reverb routing every frame as the listener moves between regions; the harnesses
  route statically.
- The client has GTK, GL, PulseAudio and a game loop competing for the same cores; the harnesses are
  near-idle.
- The client teleports (`/tp 0 30 1`) — the fastest reproduction available, crashing almost
  immediately — which re-evaluates the whole voice set in one frame. No harness does anything like
  it.

The last point is the most useful fact of the session: **`/tp` is a near-deterministic
reproduction.** Any future bisect should use it rather than walking around.

---

## The open lead, and the next experiment

*(Session 18. Tested in session 19 and cleared — see the top of this file.)*

Our pooled DSPs receive FMOD API calls **while detached with zero connections**:

```csharp
// FmodAudioProvider.cs:218 — taken out of the pool, still detached
if (_threeEqPool.TryPop(out var dsp)) { dsp.setBypass(false); return dsp; }

// ReleaseThreeEqDsp — put back in the pool, already detached
dsp.setBypass(true);
_threeEqPool.Push(dsp);
```

That is the exact shape of the fault: *a DSP with no connections being asked about its connections.*
`DSP::setBypass` plausibly walks `outputs.first()` to work out the channel format it must bypass
into.

**The `--ended-channel` probe did not cover this.** It called `getNumOutputs`, `getActive`,
`removeDSP`, `disconnectAll` and `release` on a detached unit — all fine — but never `setBypass` or
`setParameterFloat`, which are the two we actually call. That is a gap in the test, not evidence of
safety.

### Next experiment (read-only, no game changes)

1. Extend `--ended-channel`: after the channel ends, call `setBypass(true)`, `setBypass(false)` and
   `setParameterFloat` on the detached DSP. Then repeat **while the mixer is concurrently running**
   other voices — the race is the likely trigger, since a deterministic fault would crash everyone
   who pools DSPs.
2. If it faults: stop touching pooled units while they are out of the graph. Set bypass and
   parameters only after `addDSP`.
3. If it comes back clean: **stop pooling custom DSPs entirely** as a bisect — one DSP per voice,
   created and released with it. One run with `/tp` tells us whether reuse is involved at all. If it
   still crashes, pooling is cleared and the remaining suspects are the simulator and the per-frame
   reverb-routing mutation.

### One configuration worth trying either way

FMOD's docs: `DSPBufferPoolSize` defaults to **eight** buffers, and "a large graph might require more
if the aim is to avoid real-time allocations from the FMOD mixer thread." We call
`setDSPBufferSize(1024, 8)` but never `setAdvancedSettings`. Our graph is roughly 40 voices × 5–6
units plus 24 reverb buses — far past eight.

---

## State at session close

**Tests: 786 passed, 2 skipped, 0 failed.** The two skips are the deliberate bus-shelter gate and the
pre-existing `SoftGroundIsQuieterThanHardGround`.

**Nothing is committed.** The working tree carries this session's changes to `FmodAudioProvider.cs`,
`MasterTap.cs`, `Client.Gtk/Program.cs`, `run-gtk-client.sh`, `ProviderOrbit.cs`,
`AudioLab/Program.cs`, plus the new `FmodDebugLog.cs`, `tools/read_core_fault.py` and
`tools/read_core_threads.py`.

The client is built Release with the **plain** `libfmod.so` in place, verified by checksum. The
server is running the city map with `OPENFPS_WEATHER=Clear`.

**Crash dumps:** `~/openfps-crashes/` — roughly 16 GB in `kept/` plus five newer ones. They all say
the same thing and can be deleted.

### Separate items still open

- **The light rail** has infrastructure and a `rail_loop` track, but nothing runs on it. Needs a
  multi-tap voice.
- **The bus-shelter survey** fix is specified and gated; `EnclosureTests.AStreetShelterIsNotACathedral`
  stays skipped until it lands.
- **A region lookup can return plain geometry.** In one run the listener's "region" resolved to
  entity 5811, a `plaster_wall`, for 25 seconds. Not investigated.
- **The open-deck garage** still measures long. Unrelated to the crash, and part of the older
  enclosure-vs-absorption work.
- **Vehicle audio feedback** from earlier in the session (skidding, level spread, gearing, aircraft
  range) was addressed but never heard back on, because every run so far has ended in a crash.

## Sources

- [FMOD Threads and Thread Safety](https://www.fmod.com/docs/2.01/api/white-papers-threads.html)
- [FMOD Plug-in API: DSP](https://www.fmod.com/docs/2.03/api/plugin-api-dsp.html)
- [FMOD Core API: Using DSP Effects](https://www.fmod.com/docs/2.03/api/using-dsp-effects-in-the-core-api.html)
- The same manual ships in the SDK at
  `~/Downloads/fmod_extract/fmodstudioapi20314linux/doc/FMOD API User Manual/`.
