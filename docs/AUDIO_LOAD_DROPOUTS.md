# Why the speedway drops out on load — diagnosis (2026-09-14, session 4)

Read this before touching `EngineRenderPool`, `EngineProcessor` or the voice budget again.

> **All six are now fixed (session 5).** What was measured, and what is left, is at the bottom
> under *What was done*. The ranked diagnosis below is kept as written because it is the reasoning,
> and because every one of these traps is easy to walk back into.

## Verdict

The load-time dropouts are **not** proof that live synthesis cannot scale, and they will not go
away by pre-rendering engines alone. They come from four mechanical faults, two of them measured
in this session, that stall the **FMOD mixer thread itself**. Ring depth cannot help when the
*consumer* is the thing that is frozen — which is exactly why "buffer depth, thread priority and
dedicated threads all helped and none fixed it" (todo.md). Fix these first; the sample-bank plan
is still the right long-term answer for 30+ cars, but it is a separate decision.

Ranked by how much of the symptom each one explains.

## 1. The mixer callback blocks on the producer lock (the big one)

`EngineProcessor.cs`, `EngineVoiceState.Consume` / `Produce`.

- `Produce` takes `_produce` and holds it for the **entire** top-up: the 0.1 s warm-up plus the
  whole `while (lead < want) Synthesize(512)` loop, where `want` is the global `_lead`
  (0.25 s, growing to 0.7 s under stress).
- `Consume`, called on the FMOD mixer thread, does `lock (_produce)` whenever a *primed* voice has
  fewer samples than the block. That is a **blocking wait inside the audio callback** for however
  long the worker's current top-up takes.
- Cost of one top-up at load-time speed (see §2): 0.7 s of audio × 25 % of a core ≈ **175 ms**
  of wall time, plus 25 ms of warm-up. FMOD's default Linux DSP buffer is 1024 × 4 = 93 ms of
  tolerance. One such wait is a dropout. Several voices doing it in the same second is "cuts out
  for seconds".
- This is also why **deeper buffering made it worse, not better**: every time `_lead` grows, the
  top-up the mixer may have to wait for gets longer.
- Related positive feedback in `EngineRenderPool.TopUp`: one underrun multiplies `_lead` by 1.35
  **globally**, so every voice immediately owes 35 % more audio while the workers were already
  behind. Nothing distinguishes a cold ring from a starved one.

**Fix:** the callback must never block and never synthesize.
- In `Consume`: `Monitor.TryEnter(_produce, 0)`; on failure or shortfall, write silence (or hold
  the last sample and ramp to zero over the block) and bump a `Starves` counter. Delete the
  inline-synthesis path entirely; the pool is the only producer.
- In `Produce`: take and release the lock per 512-sample chunk so nothing else can ever wait more
  than one chunk (~0.3 ms). With inline synthesis gone the lock can go altogether: single
  producer, single consumer, `Volatile` positions are enough.
- Grow the lead per voice, not globally, and never on a voice younger than one lead.
- Sort the snapshot so workers prime the **nearest** cars first; today it is `_activeSounds` order.

## 2. The JIT runs the synthesis at tier-0 during a load — measured 2× cost

.NET tiered compilation starts every method at unoptimized tier-0 and promotes it after 30 calls
**plus a 100 ms quiet period that restarts whenever other methods are being jitted**. A map load
jits code continuously for seconds, so the engine loop stays at tier-0 for the whole load
window. Measured with the existing spike (`--engine-cost nascar_v8 sec=2`, release lab build):

| condition | realtime × | core share per voice |
|---|---|---|
| default, after the spike's 1 s settle | 7.8 | 12.8 % |
| `DOTNET_TC_CallCountingDelayMs=100000` (pinned at tier-0 = load condition) | 4.0 | 25 % |
| `DOTNET_TieredCompilation=0` | 8.3 | 12 % |

Note `EngineCostSpike` renders one second before starting the stopwatch, so it has **never**
shown this; add a "first 500 ms" column.

**Fix (pick one, verify with the env pin above reading ~8×):**
- `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on the hot path:
  `EngineVoiceState.Synthesize`, `EngineSynth` step, `Waveguide`, `ExhaustNetwork`,
  `IntakeNetwork`, `Driveline.Step`, `VirtualDriver.Apply`, `VehicleSynth.Tyre`. Targeted; no
  startup penalty elsewhere.
- or `<TieredCompilation>false</TieredCompilation>` in the client csproj (whole app fully
  optimized from the first call; slower startup, simplest).
- or ReadyToRun publish.

## 3. `ThreadPriority.Highest` does nothing on Linux

CoreCLR on Unix accepts `Thread.Priority` and silently does not apply it (raising priority needs
`CAP_SYS_NICE`). The render workers, the acoustic bake, the sample decodes, the JIT threads and
FMOD's own mixer thread (which also fails to get real-time scheduling unprivileged) all run at
the same priority. Comments in `EngineRenderPool` that credit priority for anything are
describing a placebo.

**What actually works unprivileged:** you may *lower* your own threads. P/Invoke
`setpriority(PRIO_PROCESS, gettid(), +10)` on the loader side — the acoustic bake
(`ClientGameSession.GenerateAcoustics`, currently `Task.Run` on the shared pool), sample
ingestion, scene build — so the mixer and producers win contention. Raising the producers needs
`rtkit` or `ulimit -r`; not worth it.

## 4. GC suspensions freeze the mixer thread at every managed DSP

Every custom DSP is a managed callback entered from FMOD's native mixer thread: engine, echo,
synth, granular, boundary, Steam Audio binaural (`SteamAudioDsp`), ambisonic bed, master tap.
A native thread entering managed code while a GC suspension is in progress **waits for the GC**.
Workstation GC blocks all managed threads for gen0/gen1 and for the compacting phases of gen2;
a map load (entity snapshot, JSON, voxel acoustics from `AcousticVolumeGenerator`) grows the heap
fast and triggers a run of gen1/gen2 collections that can each cost tens to hundreds of ms.
The ring buffers are full during that time and it does not matter.

Unverified in-session (needs the client running); **instrument before fixing**: add
`GC.CollectionCount(2)` and `GC.GetTotalPauseDuration()` to the 5 s "Mixer load" line and to a
1 s line during the first 10 s after map load. If pause time jumps when the audio cuts, this is
confirmed.

**Fixes, in order of cheapness:** `GCSettings.LatencyMode = SustainedLowLatency` at client start
(gen2 becomes background; short pauses remain); `GC.TryStartNoGCRegion(~200 MB)` around the map
load window and `EndNoGCRegion` after; cut allocation in the bake and snapshot paths. There is
no runtimeconfig or csproj GC setting in the client today — everything is at defaults.

## 5. The budget loop makes load-time churn

`ClientAudioSystem.ChooseLiveEngines` reads FMOD's `dsp %` (`MixerLoad`). During the stalls in
§1 and §4 that reading pins at 100 %, so after `BudgetSettleSeconds` (1 s) the loop sheds echoes,
then borrowed voices, then engines; a second later, load having dropped, it adds them back. Each
re-added engine is a **new** `EngineVoiceState` (new `EngineSynth`, 0.1 s warm, 0.25 s fill ≈
90 ms of CPU per car at tier-0) plus an FMOD graph rebuild. That is more load, applied while the
mixer is already struggling, and it is heard as cars appearing and vanishing — "voices not
playing consistently".

**Fix:** hold the budget for ~3 s after a map load; require the ceiling to be exceeded on three
consecutive 0.25 s samples (or use a median) before shedding; keep retired `EngineVoiceState`
objects for reuse by the same entity instead of reconstructing them.

## 6. Two FMOD knobs never set

`FmodAudioProvider.Initialize` calls `init(512, …)` with no `setDSPBufferSize` and no
`setSoftwareChannels`.
- **DSP buffer**: default 1024 × 4. `setDSPBufferSize(1024, 8)` (before `init`) doubles the
  stall tolerance to ~185 ms for ~90 ms more latency. Insurance, not a fix.
- **Software channels**: default 64 real voices; 512 is only the *virtual* count. The speedway
  with engines + echoes + wall reflections + samples + ambience can exceed 64, at which point the
  quietest voices go virtual and silent. Log `getChannelsPlaying(out playing, out real)` on the
  Mixer load line; if `real` sits at 64, set `setSoftwareChannels(128)` or more.

## What I would do first, in order

1. §1: lock-free `Consume`, no inline synthesis, per-chunk lock in `Produce`. Half a day, biggest
   payoff, removes the stall that scales with buffer depth.
2. §2: `AggressiveOptimization` on the synthesis hot path; confirm with the env pin.
3. Instrument: GC gen2 count + pause total, FMOD real channels, pool underruns delta, a
   max-`Consume`-wait counter — all on the existing Mixer load line. Run the speedway with
   `OPENFPS_AUDIO_CAPTURE` and line the log up with the WAV.
4. §4 and §3: `SustainedLowLatency` + nice the loader threads.
5. §6: buffer 1024 × 8, software channels 128.
6. §5: budget hold after load.

Then re-raise `FIELD_SIZE` in `tools/gen_speedway.py` and measure again. Only if the *steady
state* with 30 cars is the limit does the sample-bank plan in `todo.md` become the next step —
and §4, §5 and §6 apply to that design just as much, because they have nothing to do with how
the engine sound is made.


## What was done (2026-09-14, session 5)

All six, in the order above.

1. **The ring is lock-free.** `Consume` never blocks and never synthesizes: it takes what the ring
   has, ramps out of the last sample over up to 64 samples so the gap has no step in it, and counts
   a starve. `Produce` claims a single-producer slot with a non-blocking CAS — a second worker that
   finds it taken leaves, because whatever the first is doing is the work it came to do. The lead is
   per voice (`EngineVoiceState.LeadSeconds`), grown only by that voice's own starvation and only
   once it has been playing for at least one lead; the global ratchet is gone. `SnapshotEngineVoices`
   sorts nearest-first and the workers' stride preserves it.

   One consequence worth knowing: during a starve the play position does not advance, so an echo
   reading that ring repeats its last block rather than advancing. The source is ramping to silence
   at the time, so it is much the quieter of the two artefacts, and starves are now rare.

2. **`AggressiveOptimization` on the per-sample path** — `EngineVoiceState.Synthesize`,
   `EngineSynth.Step`/`SolveValve`/`Lift`/`Wiebe`/`DecideCharge`, `ExhaustNetwork.Step`,
   `IntakeNetwork.Step`/`Orifice`, `Waveguide` (`WaveLine.Write`/`Read`/`Deposit`, `Junction.Scatter`
   /`Two`, `OpenEnd.Process`/`Radiate`, `JetNoise.Process`), `Driveline.Step`, `VirtualDriver.Apply`,
   `VehicleSynth.Tyre`, `ClickVoice.Process`. Small helpers are left alone: an optimized caller
   inlines them, and marking them only stops that.

   | condition | before | after |
   |---|---|---|
   | settled (default) | 7.8–8.5x | 7.8–8.6x |
   | `DOTNET_TC_CallCountingDelayMs=100000` (the load condition) | 4.0x | 6.8–7.1x |

   25 % of a core per voice during a load, down to about 14.5 %. `--engine-cost` grew the
   **first 500 ms** column; it reads roughly half the steady column for the first (genuinely cold)
   preset in a run, which is the effect made visible.

3. **`BackgroundPriority`** (`OpenFPS.Client.Core/Platform`). `setpriority(PRIO_PROCESS, gettid(), +10)`
   on the calling thread, and `RunLowered` to start a loader thread already niced and off the shared
   pool. The acoustic bake uses it. `EngineRenderPool`'s `ThreadPriority.Highest` is kept for Windows
   with a comment saying plainly that it does nothing here.

4. **`GCSettings.LatencyMode = SustainedLowLatency`** in `FmodAudioProvider.Initialize` — set there
   rather than in either head's `Program` because it is a property of having managed DSPs at all, and
   both heads have them.

5. **The budget holds.** `ClientAudioSystem.NoteSceneLoading()` is called at `MapLoadComplete` and
   again at `PlayerSpawned`; for 3 s after either, nothing is shed. After that the ceiling must be
   exceeded for 0.75 s together (`OverCeilingSeconds`), not in a single sample.

6. **`setDSPBufferSize(1024, 8)`** before init, **`setSoftwareChannels(128)`** after. The suspicion
   about real channels was right: the 30-car speedway measures **59–62 real channels**, so the
   default 64 was being reached and the quietest voices were going silent with no error anywhere.

**The Mixer load line** now reads:

```
Mixer load: dsp 12.7%, update 0.2%, stream 0.0% — 61 engine/echo voice(s) of 61 active,
            61/61 real channel(s), 0 starve(s), gc 0 gen2 / 4 ms paused
```

starves, gen2 collections and total GC pause are **deltas** since the last line, and the line drops
from its 5 s cadence to 1 s whenever either is non-zero — a five-second average is useless at
exactly the moment it matters.

**`FIELD_SIZE` is back to 30.** On the speedway spike: 30 cars, 60 voices, dsp 12–16 %, starves only
in the first two seconds (47 then 138, then zero), and the capture is continuous from 0.2 s with no
clipping and a steady −20 to −25 dBFS.

### Still to do

- **Confirm in the real client.** The spike has no map load, no acoustic bake and no GC pressure —
  the three things this was all about. Run the speedway, watch the starve / gen2 / pause / real-channel
  figures over the first ten seconds, and line them up against an `OPENFPS_AUDIO_CAPTURE` WAV.
- **Voice reuse** (keep retired `EngineVoiceState` objects for the same entity) was deliberately not
  done. With the budget held and the existing keep-bias the churn it fixes is no longer measurable.
- The **sample-bank plan** is now a separate decision about a field of a hundred, not a forced answer
  to a broken load.


## The follow-up: what a starved block must NOT do (2026-09-14, session 5, second pass)

Reported after a few minutes of standing at the speedway: cars "pitch correcting" as they crossed in
front, then "the pitch over all of the cars dropped as if they slowed down", and the field sounding
less distinct — "maybe they blend together".

All three were **one bug, introduced by the §1 fix above**, and none of them sounds like a dropout,
which is why the list above would never have caught it.

`Consume` took only the samples the ring actually had and left the play position behind them. That is
the obvious way to write it and it is wrong: a voice handed 900 samples of a 1024-sample block that
keeps its place **is playing at 88 % speed** — a tone and a half flat. So:

- a voice that starves in bursts dips in pitch and recovers → "pitch correcting", loudest on the car
  crossing in front because that is the one you are listening to;
- a burst that catches many voices at once (a GC pause, a scheduling hiccup) pitches the **whole
  field** down together and back → "as if they slowed down";
- and every starve leaves that car's audio permanently further behind where the car actually is, up
  to one lead — fifty metres at 250 km/h — with each car lagging by a different amount, so thirty
  separate cars smear into a wash → "they blend together".

**A real-time voice keeps wall clock.** The play position now advances by the whole block whether or
not there was audio to fill it; the producer resyncs `_written` to `_played` when it finds the
consumer has run past. A starve is a GAP — a few milliseconds of ramped silence — never a delay and
never a slowdown. Tests: `TheMixerNeverSynthesizesAndNeverWaitsOnTheProducer` and
`AStarvedVoiceLosesAudioButNeverFallsBehindWallClock`.

A latent crash went with it: once the play position could pass the write position, `avail` went
negative and the ramp indexed the span at a negative offset. It was swallowed by the callback's
`catch { mono.Clear(); }`, so it would have presented as a voice that silently stopped.

### And two new instruments, because neither symptom was visible anywhere

- **`Audio thread: N Hz of 250`** (`AudioEngineFacade.ReportTickRate`). The 250 Hz attribute loop is
  what makes a pass-by glide instead of step, and its period is `max(1 ms, 4 ms - tick)` — an
  outcome, not a setting. The tick walks every active voice, and eight cars is 17 voices while thirty
  is 60. It warns under 200 Hz and says what pitch step that implies. Measured cost of the attribute
  pass itself at 62 voices: **0.06–0.13 ms mean**, so the walk is not the problem — but now it says so
  rather than being assumed.
- **`Cars: N on the map — L synthesized, B borrowed, S out of budget`** every 5 s. "Are there really
  thirty cars out there?" should not have to be answered by counting engines by ear.


## Session 5, third pass: it was the RACING LINE, not the audio

"Cars sounded like they were slowing down" survived both of the fixes above, because the cars were
slowing down. `RaceLine` capped corner speed at `sqrt(mu*g*R)` — flat-track physics — while
`tools/gen_speedway.py` builds 24 degrees of banking into the turns and the centreline carries the
elevation. A centreline cannot describe a CROSS-slope, so the bank angle has to be declared:
`TrackData.BankingDegrees`, emitted as `BANK_DEG`, used by `RaceLine.CorneringSpeed` as
`v^2 = R*g*(mu + tan@)/(1 - mu*tan@)`.

Measured over a lap, before and after:

| car | flat (before) | drop | banked (after) |
|---|---|---|---|
| Stock car | 231-296 km/h | 22 % — **4.2 semitones** | 296-296 |
| Formula | 273-327 km/h | 16 % — 3.1 semitones | 327-327 |
| Big block | 145-189 km/h | 23 % — 4.6 semitones | 189-189 |

Twice a lap, every car, and the grandstand is at start/finish where they brake for turn 1. The old
`CorneringG` values (2.90, 4.00) were a fudge for the missing banking and are now real tyre grip.

And `FormulaCar.DragArea` was 1.35 m^2 — a road-course wing package — so the car could reach only
**230 km/h** against a map that commanded 327. Ten of the thirty cars were permanently flat out, a
hundred short, droning at 10,500 of 15,500 rpm and never revving out. Oval trim is 0.85: measured
1.35 -> 230, 1.10 -> 270, 0.95 -> 285, 0.85 -> 296 km/h at 13,450 rpm against a 13,500 torque peak.

**The lesson for next time:** `IAudioProvider.TryGetEngineTelemetry` and the `Cars:` census print told
speed vs driveline speed vs rpm vs gear for the nearest cars. Two sessions were spent on the audio
plumbing for a fault that one line of that would have placed immediately.

## And the crash: a managed DSP callback must not throw

`BoundaryProximityProcessor.ReadCallback` threw `IndexOutOfRangeException` and **killed the client**.
An exception in a managed DSP does not fault a voice — it unwinds into FMOD's native mixer thread and
takes the process with it. Six of the seven custom DSPs in this engine had no guard at all; only
`EngineProcessor` did. All seven are wrapped now (`ReadCallback` -> try/catch -> `ReadCallbackCore`),
each logging once and silencing one block rather than ending the session. The boundary unit passes the
input THROUGH on a fault rather than clearing, because it sits at the tail of the master bus and
clearing would mute the entire game.

Hardening in the same unit, since the exact index was not recoverable from the trace: the sample count
is now bounded by what the INPUT span holds as well as the output, the write head is range-checked,
and `ReadInterpolated`'s clamps are written as `if (!(d >= 1f))` rather than `if (d < 1f)` — NaN
answers false to every comparison, so the original clamps let a NaN delay walk straight into the index.

## Phantom footsteps on spawn

Reported the same session: a few footsteps on being dropped onto the map, dying away. The stride
accumulator rejected any single move over a metre as a teleport — but a spawn RECONCILIATION is not
one jump, it is thirty corrections of a few centimetres, each individually "plausible" and together
nine metres of banked walking. `LocalPlayerController.Update` already received the player's own
`velocity` and threw it away; it now requires `> MinStrideSpeed` (0.5 m/s) of the player's own
horizontal speed before banking anything, because being dragged does not move your legs.
`Teleported()` clears the accumulator at `PlayerSpawned` as well. Test:
`ASpawnReconciliationDoesNotSoundLikeFootsteps` — 30 corrections of 30 cm at zero velocity, which
produces one phantom step under the old rule and none now.


## Session 5, fourth pass: the pitch jumpiness was FMOD virtualising voices

"Everything sounded great for the first couple of minutes, then suddenly all of the cars started
doing that extreme pitch shifting as they come past." Two faults, both caused by raising the field
from eight cars to thirty, and both found by the instrumentation added two passes earlier.

### 1. `setSoftwareChannels` was called after `init` and refused

```
[17:12:05 ERR] FMOD setSoftwareChannels failed: ERR_INITIALIZED
               — Cannot call this command after System::init.
```

It is a PRE-init setting. The call added in the first pass was placed after `init`, FMOD refused it,
the error was logged, and nothing else happened — so the real-voice limit silently stayed at the
default 64. The Mixer load line's `real/playing` column shows the whole story:

```
17:12   61/61 real     — all real
17:13   71/82, 77/82   — going virtual
17:14   64/87, 65/88   — a quarter of the mix not being rendered
```

Thirty cars are thirty engines plus up to sixty reflections, and it takes a minute or two for
reflections to be found for all of them — which is exactly when the count crosses 64 and exactly
when the symptom arrived.

Past the limit FMOD picks the QUIETEST playing voices and stops rendering them. On a racetrack the
quietest voices are the cars furthest away, so the virtualised set changes continuously as cars
approach and recede: every car crossing in front pushes another out and is itself pushed out on the
way past. For a sampled voice that is inaudible. For a SYNTHESIZED one it is not — the DSP callback
simply stops being called and later starts again, and the ring it reads from has moved on, so the
voice resumes somewhere else in its own waveform. Heard as extreme pitch jumpiness on every pass-by.

Fixed: before `init`, raised to 256, and the granted figure is now logged
(`FMOD software channels: 256 real voices`) so a refused limit can never again be invisible.

### 2. The world-state broadcast was too big for an unreliable packet

```
LiteNetLib.TooBigPacketException: Unreliable or ReliableSequenced packet size
exceeded maximum of 1023 bytes
```

47 of them in three minutes. LiteNetLib does not fragment unreliable sends; past the peer's limit it
throws, `BroadcastWorldState` catches and moves on, and **the entire tick's world state is lost for
that client** — every entity. Eight cars fitted in 1023 bytes and thirty do not.

A dropped tick freezes every entity until one gets through, and the packet is only SOMETIMES over the
line (it depends how many cars are in range of that player), so it is intermittent and lands on
whichever cars happened to be moving. Reported as "some of the cars seem to stop for a second in
front of me before they keep going. not all the cars, but some of them" — which is precisely what a
dropped snapshot looks like from the grandstand.

Fixed in `NetworkService.SendStateUpdate`: serialize, and if it does not fit the peer's
`GetMaxSinglePacketSize`, halve the state list and recurse, every chunk keeping the same `Tick`.
Split by halving rather than by a per-entity size constant — the serializer decides how big a state
is and the peer decides how big a packet may be, and neither is ours to predict.

`ClientWorldState.SyncState` reassembles: a packet whose tick is already buffered is MERGED into it
by entity id rather than filed as a second snapshot. Filing them separately would be worse than the
original bug — the interpolator brackets playback time between two buffered snapshots and divides by
the time between them, so two entries with the same tick is a zero denominator, and each holds only
half the world.

### Worth noting

Both of these were found by reading one log. The `real/playing` column, the `Cars:` census, the
`Audio thread: N Hz` line and `TryGetEngineTelemetry` were all added in the previous two passes to
stop this project diagnosing audio faults by ear — and the first thing they did was catch a mistake
made while adding them.


## The cars that stop: the playback clock was never synchronised

The packet-size fix above was real — 47 dropped ticks in three minutes, now zero — but it was not
what made cars stop. That was a second, independent fault in the same symptom, and it is the one
that had been there all along.

`ClientWorldState.UpdateInterpolation` places remote entities by interpolating between two buffered
server snapshots that BRACKET a playback time. That playback time was initialised once, from the
newest snapshot, and thereafter advanced by the CLIENT's own frame delta — for ever, with no
reference to the stream it was supposed to be following. Two clocks with no correction between them
drift; when the drift exceeded the buffer, the bracket search found no pair, and the interpolator's
response to that was to update nothing at all. Every entity in the world holds its position until a
pair exists again. Silently: no clamp, no resync, nothing in the log.

The buffer made it worse: ten snapshots at the 30 Hz tick is 333 ms of history, and playback sits
100 ms behind the newest, so there were **233 ms of margin** before the snapshot being read FROM was
pruned out from under it.

Why "some of the cars" and not all: the freeze is global, but a car passing a few metres in front of
the grandstand swings its bearing enormously while it moves and a car across the infield barely
changes at all. The near ones are the only ones whose stopping you can hear.

**Fixed** in three parts:
- The playback clock is corrected by RATE, not by jumping — it runs up to 10 % fast or slow to close
  the gap to `newest - InterpolationDelay`. Setting the time directly would move every entity in the
  world at once, which is the artefact the whole mechanism exists to avoid.
- A gap too large to ease out (> 0.5 s) is a stall rather than drift — the stream stopped and
  restarted — and is snapped, counted in `InterpolationStalls`, and logged at Debug.
- History raised from 10 snapshots to 30: a full second at 30 Hz. It is a list of references.

Test: `AClientClockThatRunsFast DoesNotFreezeTheWorld` runs the client 6 % fast for twenty seconds —
an ordinary amount for two unsynchronised clocks. Against the old code the car froze for **567
consecutive frames (20 s)**, never recovering once the drift passed the buffer. Now it never stalls
more than four frames.


## The "auto-tune": the 250 Hz loop was recomputing Doppler from a 45 Hz position

Reported as cars doing "that weird auto-tune thing, high pitch, low pitch" on a pass, and — the
detail that solved it — "it only happens to ones that are close to me, the further away they are
they pass just fine".

The attribute loop runs at 250 Hz specifically so a pass-by GLIDES: Doppler is applied as a channel
pitch and a channel pitch changes the instant it is set, so the loop period is the resolution of
every pass. But the loop recomputes the Doppler from `active.Position`, and that only changes when
the game thread resubmits the emitter — measured by the new instrument at **22 ms**, about 45 Hz. So
the loop was writing the same pitch five or six times over and then jumping. Everything the 250 Hz
bought was being thrown away one layer down.

The step size goes as v^2/d, which is why it is purely a near-field fault:

| miss distance | step per update, before | after |
|---|---|---|
| 5 m | 8.03 % — **1.34 semitones**, 45 times a second | 0.24 semitones |
| 10 m | 4.15 % — 0.70 semitones | 0.12 |
| 25 m | 1.70 % | 0.05 |
| 50 m | 0.86 % — already a glide | 0.02 |

A semitone and a third, forty-five times a second, is not a Doppler shift — it is a pitch quantiser,
and it sounds like one.

**Fixed** with `FmodAudioProvider.DeadReckon`: the source is carried forward on its own velocity from
the moment the game thread last placed it (`ActiveSound.LastAttributeAt`, capped at 50 ms), so the
listener-to-source vector varies continuously at the rate the loop actually runs. Used for both the
Doppler and the panning target. A reflection carries no velocity of its own — deliberately, see
`EngineEchoState` — so it is a no-op for one. Test:
`AClosePassGlidesInPitchInsteadOfStepping`, which also asserts the far case was never broken, so the
test cannot quietly start measuring the wrong thing.

**What the instruments said, and why that mattered:** game loop 190 Hz, worst iteration 2 ms, audio
placement gap 22 ms, worst pass 1-3 ms, no stale-voice warnings, 90/90 real channels. Everything
healthy — which is what pointed at the one number that was not a problem in itself (22 ms) being
consumed by something that assumed it was 4 ms.

## And "it's like I'm hearing only the reflections"

Every live engine got two reflections, so thirty cars meant **sixty reflection voices against thirty
direct ones** — and the probe shows that on this map they nearly all come off the same surface, the
grandstand's back wall two metres behind the listener. Sixty near-full-gain copies of the whole field
arriving from behind outweigh the field itself two to one.

Each reflection is individually right (`ImageSource` already applies spreading loss, absorption and a
finite-panel aperture term). The SUM is not: one wall returns one room response however many cars are
in front of it, and thirty cars do not make a grandstand thirty times more reverberant.

The walls now answer the nearest `EchoCarLimit` (6) cars rather than all thirty — twelve voices
instead of sixty. A near car's reflection is a cue you can point to; a car three hundred metres away
contributes a copy of a whisper at a delay nobody can resolve, and thirty of those are the wash.
Cars that drop out of the set fade through the existing half-second release rather than being cut.
The census line reports it: `walls answering N of them (nearest first), N reflection voice(s)`.
