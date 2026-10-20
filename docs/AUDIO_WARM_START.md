# Sound that starts clean: the load burst and its fix

The city crackles and breaks up for the first few seconds after a load, then settles. This is why, measured, and
the plan that fixes it at its cause rather than by fading sounds in. Written 2026-10-18. The speedway's load
dropouts of 2026-09-14 (docs/AUDIO_LOAD_DROPOUTS.md) were a different set of faults, all fixed; read that first
for the engine render pool's rules.

## 1. Who runs what

No vehicle has a thread of its own, and none is made on the game thread:
- Engine sound is made on `EngineRenderPool`: dedicated threads, half the machine's cores up to twelve (twelve on
  a 24-core machine). Each car's engine is one serial integration; the workers take the next car from a list
  sorted loudest at the ear first, render a quarter to two thirds of a second ahead, and the mixer only copies it
  out. A thread per car would be forty threads contending for the cores, and the operating system choosing which
  car waits.
- Where each sound is and how loud is worked out on the placement thread (`ClientAudioSystem.Update`,
  `AudioWorld.Tick`).
- FMOD's mixer thread does the binaural rendering, reverb and the mix.

## 2. What the city load does (Cody's client log, 2026-10-17, 03:31)

The spawn at Brandt Court stairwell came at 03:31:09.6. Then:

| When | What the log says |
|---|---|
| 03:31:10 to :13 | 33 engine voices, 25 window air conditioners and a light-rail train start, nearly all in two seconds |
| 03:31:09.9 | game loop 0 Hz, worst iteration 273 ms |
| 03:31:14.7 | placement stalled: every source held still up to 1,320 ms; one pass took 1,287 ms, 1,194 of it placing emitters, of which only echoes 26, ground 5, birds 143 and horns 2 are named |
| 03:31:14.7 | game loop 7 Hz, worst iteration 1,441 ms |
| 03:31:15.4 | mixer 104.5 %, 92 real channels, garbage collection paused 81 ms |
| 03:31:17.6 | mixer 91 % |
| from 03:31:20 | mixer 55 to 70 %, placement normal |

The engine render pool was never short: 0 starves in every line, at most 27 % busy. Three things are heard:
- the mixer over 100 %: it cannot finish a block in time, and that is the crackle and break-up;
- a placement or game-loop stall: every moving sound holds its place, then jumps;
- garbage collection pausing the mixer's managed effects for 80 to 106 ms in each five-second line.

All three come from one shape: every sound in earshot is created in the same two seconds, each for the first
time, while code that has never run is still being compiled. About a second of the placement pass is not named
by any part of the log. It settles because by then the first-time work is done.

## 3. The rule

A sound is not heard until it is warm, and the program never makes in one moment more than it can make in a
moment.

Fading sounds in only hides the burst: the same work still lands in the first seconds, and a source that fades
in where it was already sounding is wrong. Nothing about how many sources there are, or how detailed each is,
changes.

The same burst comes wherever the set of sources changes at once, so the fix covers all of them:
- loading a map;
- `/join` to another map;
- a `/tp` across the city;
- a world tile streaming in;
- a train or a crowd spawning;
- a slower machine (a Windows laptop with eight cores gets four render workers).

## 4. The fix, in six parts

1. **An instrument first: `--city-load` in AudioLab.**
   - What it does: loads the real city, spawns at Brandt Court as the client does and captures the mix.
   - What it reports, second by second: mixer blocks over their time, gaps in the capture, the worst placement
     pass and game-loop iteration (with every millisecond given to a named part), voices started, garbage
     collection pauses.
   - Its first job is to name the missing second of the placement pass and the 1.4 s game-loop iteration. Every
     part below is judged against it, before and after.
   - **Measured (2026-10-19).** `--city-load out=… sec=30 repeat=2`, Release, the build script's twelve
     cores (`DOTNET_PROCESSOR_COUNT=12`), spawn at the map's Brandt Court stairwell region (feet −14.20, 0.10,
     157.10; `/tp` order −14.2 157.1 0.1). Each load arrives as 8,870 definitions, bakes its acoustic map in
     0.32–0.54 s and spawns 0.34–0.57 s after the manifest. Steam Audio's binaural stages were in use: 88–91
     binaural places free, no voice without HRTF, none refused.
     - Load 1's worst placement pass was 561, 659 and 601 ms in the three runs. Its emitters stage cost 491, 589
       and 533 ms: birds 332, 411 and 375; paths 85, 95 and 95; echoes 37, 46 and 37; budget 42–43; listener+map
       25–26; voices 7–16; ground 9; trains 4–14; rain 2–3; sweep 3–4. Unnamed was 0.2 ms. Load 2's worst pass was
       126, 129 and 150 ms (paths 111, 100 and 133; echoes 11–14). The first-time cost is therefore 435, 530 and
       451 ms, and 126–150 ms is the placement left when nothing is compiled for the first time. Placement per
       pass over the first ten seconds fell from 29.0–30.0 ms to 15.1–15.6 ms.
     - The worst client loop iteration was 908, 944 and 1,100 ms. `OtherBodies.Update`'s first pass was 841, 744
       and 764 ms of it; the audio update 32, 168 and 297; messages 6–7; interpolation 11–13; snapshot 6; pump
       1–8. Load 2's worst iteration was 235, 258 and 181 ms, with `OtherBodies` 104–239 and audio 11–74. Every
       millisecond is named.
     - Sources held their position up to 1,535, 1,542 and 1,453 ms in load 1 (465–564 ms in load 2). The captured
       mix was 70,228, 74,668 and 79,304 frames short (1.46–1.65 s at 48 kHz) in load 1 and 19,070–23,289
       (0.40–0.49 s) in load 2; 1–2 % of each steady second is short in both, so about a second of the first
       load's shortfall is the burst's own gap. The mixer peaked at 69–73 % in load 1 and 53–55 % in load 2, and no block reached 100 % (the
       2026-10-17 log's 104.5 % did not reproduce). The stall and the capture gap did reproduce.
     - Load 1 started 101–103 voices: 38 engines, 30–32 borrowed, 22 machines, 4 horns, 3 trains, 3 transients,
       1 intake. Load 2 started 102: 39 engines, 25 borrowed, 23 machines, 7 transients, 3 intake, 3 horns, 1
       train, 1 siren. Garbage collection ran 357–363 times in load 1 (15–16 gen2, 592–656 ms paused) and
       404–418 times in load 2 (12 gen2, 666–744 ms paused) over 30 s; the longest pause was 4–5 ms.
     - What part 3 should move first: the birds' first pass, the largest named part of the placement pass
       (332–411 ms of 561–659), then the paths (85–95 ms) and the budget (42–43 ms); on the game loop,
       `OtherBodies.Update`'s first state for every body (744–841 ms of 908–1,100) outweighs the audio update
       (32–297 ms). Both are first-time work: load 2's same passes are 126–150 ms and 181–258 ms. The mixer
       over-100 % block did not reproduce here; the 2026-10-17 log's 1,287 ms placement pass came back at about half,
       561–659 ms, and every millisecond of it is now named. One later 10 s sanity run segfaulted in native code while the second
       load's acoustics were built; the repeat of that run completed, and all three measured 30 s runs completed.
       A separately probed reuse of the client world, as `/join` does, made the second registration 534 ms instead
       of 25–37 ms because `Clear` leaves the old acoustic map in place and every region copied it; the instrument
       gives each load a fresh world so that real `/join` cost does not pollute the warm-login comparison, and
       part 3 should look at it.
2. **No first-time cost.**
   - The Windows client is compiled ahead (`PublishReadyToRun` in `publish-windows.sh`), so code does not run
     slow while it is being compiled. ReadyToRun applies only to a publish, so the GTK client that
     `run-gtk-client.sh` builds relies on the warm-up below.
   - At the login screen, a warm-up renders each kind of voice once on a background thread and throws the sound
     away: an engine, a machine, rail, water, fire, birds, the binaural and reverb effects. This covers the
     GTK client and development runs, which are not compiled ahead.
3. **Nothing heavy where sounds are placed or in the game loop.** Whatever the instrument names in the missing
   second is moved off those threads, the way the train horn and car-door freezes were on 2026-10-12
   (`VoiceBuilds`, `PlacementThread.Heavy()`). Likely candidates:
   - creating 50 to 60 voices' FMOD effects at once;
   - the birds' first pass (143 ms);
   - a model still built in line.
4. **A settled start, heard in one go.** After a load or a jump of the listener, the mix stays silent while
   every voice in earshot is started and rendered silently: engines turning, machines running, birds placed,
   echoes found.
   - The mix opens when all of these hold:
     - every voice in earshot is primed;
     - the placement pass has stayed under 20 ms for half a second;
     - the mixer has stayed under 80 % for half a second.
   - A cap of eight seconds opens it anyway and logs which test failed.
   - The arrival line ("You're in city, at ...") is spoken when it opens.
   - Loading takes that much longer; the first thing heard is the city already running. Nothing else is
     faded.
5. **A start budget during play.** In ordinary play, new voices are admitted by what they cost:
   - Each voice kind's mixer and placement cost is measured once, in AudioLab.
   - Each tick admits the loudest waiting voices up to a share of the headroom left.
   - The rest wait a tick or two. New sources arrive at the edge of hearing, where tens of milliseconds are not
     heard.
   - The existing voice budget keeps shedding what is quietest when the mixer is over its ceiling; this stops
     the ceiling being overrun in the first place.
6. **No collection in the first seconds.**
   - A full garbage collection runs at the end of loading, while the mix is still silent.
   - The steady state allocates nothing per block on the placement and mixer threads, checked by the
     instrument's allocation count.

## 5. What keeps it fixed

- A test in the suite drives a headless city load through `AudioWorld` and fails if:
  - any placement pass takes over 50 ms;
  - any tick starts more voices than the start budget allows;
  - the settle gate opens before every voice in earshot is primed.
- `--city-load` is run before any push that touches voices, with its numbers in changes.md. FMOD's mixer is not
  in the test suite, so the mixer's overruns are measured there.
- The client log gains one line at the end of every settle: how long it took, the worst mixer block, and what
  held it.

## 6. How big

Six to nine agent sessions, on two tracks that touch different files:

| Track | Parts | Sessions |
|---|---|---|
| A | 1, the instrument and attribution | 1 |
| A | 3, moving what it names | 1 to 2 (unknown until named) |
| A | 4, the settled start | 1 to 2 |
| B | 2, compiled ahead and the warm-up | 1 |
| B | 6, no collection | 0.5 |
| B | 5, the start budget | 1 to 2 |
| both | the guard test, then measuring on a Windows machine | 0.5 to 1 |

Part 1 comes first and decides part 3's size. Parts 2 and 6 can start beside it.

What changes:
- No sound is respecified, and no render changes.
- No wire change: all of it is in the client.
- What Cody hears: a load a second or few longer, then the city without the crackle, and no jumps in the first
  seconds. The same on `/join` and long teleports.
