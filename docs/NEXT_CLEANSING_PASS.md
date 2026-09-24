# Next: the cleansing pass, the audio engine tune-up, and gunfire

Written 2026-09-24 at the end of a session, as the starting point for the next one. Everything below
comes from Cody's instructions in that session. Work top to bottom unless Cody says otherwise.

## Cody's words (2026-09-24)

> We should very likely do a thorough cleansing pass. that means doing sabotage testing, cleaning up
> dead/unused/irrelevant code and code comment and tightening everything up as needed. Then, we'll
> need to make sure the documentation including the normal docs readme/todo/changes are up to date.
> do not use mannered pros in these documents. todo should have work we plan to get to in a
> structured clear format of priorities. changes should have the content of the recent commits.
> Readme an overview of what open fps is, the strengths and features, etc. We need a user manual as
> well, likely one for the server and one for the client? or both in one manual? During our code
> pass, we may come across bugs or inconsistencies that may help us iron out any detected audio bugs,
> especially for the vehicles, which sound great, but all vehicles should share a similar
> configuration so the sound per vehicle is predictable, not overly quiet on one/loud on the other
> because something with the engine wasn't configured properly, things like that.....then tell me,
> does everything on the city map have a unique shape or is everything a block or do we have curves?
>
> now for the feedback on the air horns you just made: no the truck ones sound, not crackly but
> breaky, maybe it's the air effect you're trying to add or something. the car horns sound perfect,
> keep them in this new set. They are clear and crisp. It's like the air ones are weak sounding, it
> cuts out on the fade in and fade out, make those horns smooth like the car horns.....
>
> guns: I want you to take this on and do the best most realistic job you can.
>
> I want to do a coverage pass on the audio engine. I want to get the audio engine as fine tuned and
> as accurate as possible.....

## Where things stand

- Last commits: `d9e7409` (street life, birds, echoes, stutter fixes), `dfab2d3` (chat channels,
  MOTD, menus, saved servers, settings, sidewalks, horns), `43a3996` (F-key lists, /join map travel,
  friends). Full suite at `43a3996`: 926 passed, 1 skipped, about 17.5 minutes.
- Nothing from those three commits has been heard or tried in the game by Cody except the birds
  ("sound real good") and the horn WAVs (verdict below).
- The full test suite takes about 17 minutes. Always build with `--artifacts-path` on tmpfs (see
  memory: build-on-tmpfs) and filter tests while iterating.

## Order of work

### 1. Air horns (short; do first)

Verdict on `inbox/car-horns-2026-09-24/`:
- Car horns (`car_*`): perfect, clear and crisp. Keep them exactly as they are.
- Truck and bus air horns (`air_truck_dual_*`, `air_bus_horn_*`): "breaky", "weak sounding", "cuts
  out on the fade in and fade out". They must be as smooth as the car horns.

Leads to check, measuring before changing anything:
- `ChimeHorn` bends the pitch up over `RiseSeconds` and has reed jitter. While the valve is still
  opening, the reed cannot beat cleanly, and the model produces a growl or dropouts. That matches
  "cuts out on the fade in and fade out". The last session left `RiseSeconds` and the jitter alone.
- The leak hiss now scales with the reed's lift (changed 2026-09-24). Check it is not the "air
  effect" Cody hears as breaking.
- `ReedOpenFraction` 0.30 for truck/bus was set 2026-09-24. Check whether the short pulse leaves gaps
  at low valve opening.
- Look for per-cycle gaps with a short-window RMS, not only the long-term spectrum.
- Target: onset and release as smooth as `ElectricHorn`'s relay envelope, with no gap or break
  anywhere in the envelope. Render into a new dated folder in `inbox/` for Cody to judge. Do not
  change the approved train horns (K5LA, RS3L, two_tone).

### 2. Coverage pass on the audio engine

- `coverlet.collector` is already referenced in `OpenFPS.Tests`. Run the suite with
  `--collect:"XPlat Code Coverage"` and produce a per-file report for `OpenFPS.Client.Core/AudioEngine`,
  `OpenFPS.Common` (acoustics, loudness, image sources, early reflections, enclosure, signals,
  vehicles) and `OpenFPS.Client.Core/ClientAudioSystem.cs`.
- List uncovered and weakly covered code, grouped by subsystem.

### 3. Sabotage (mutation) testing

- Nothing systematic has ever been done. Tests were proven one fault at a time, and at least one test
  (the applause quantiser count) is recorded as passing whatever the code did.
- Install Stryker.NET as a local dotnet tool. Mutate the core audio and acoustics maths first:
  `Loudness`, `Enclosure`, `ImageSource`, `EarlyReflections`, `VoiceManager`, `VehicleShadow`,
  `EchoDiffuser`, `TyreFriction`, `BeaconAids`, `Honk`, `BirdLife`. Use per-test coverage analysis so
  each mutant runs only the tests that touch it; the full suite is too slow to run per mutant.
- For every surviving mutant: write the test that kills it, or record why it is equivalent.
- This is an overnight job; run it in the background.

### 4. Cleansing pass

- Dead, unused and irrelevant code and comments: remove them. Examples to check: `PlayUiBeep`,
  which releases its sound as it starts it (replaced by `PlayUiSound`); `ChatManager` sender-prefix
  leftovers; `DescribeMaps` (replaced by the F6 list); lab spikes nothing uses; the
  `OPENFPS_*` diagnostic switches that are no longer needed.
- Comments must describe what the code does now. Remove history that belongs in `changes.md`.
- Tighten where the same thing is done twice.

### 5. Vehicle consistency audit

Goal: every vehicle configured the same way, so its loudness is predictable and none is too quiet or
too loud because of a configuration slip.
- Build one table for every vehicle preset: declared `SourceLevelDb`, measured live-voice level at
  7.5 m pass-by and at idle, `EngineBayLeakage`, extent, `CompensateLevel` lift, air system, horn.
- Flag outliers and find the cause in the configuration, not by adding a trim.
- Memory notes with past faults of this kind: the-block-had-no-anchor, three-undeclared-duplicates,
  idle-engine-under-the-loudness-law, live-voice-is-not-the-bench, the-police-car-was-a-pace-car.

### 6. Gunfire: the most realistic job possible

Cody: "take this on and do the best most realistic job you can." The current gunshot is fully
synthetic (`WeaponSynth`: filtered noise, body resonance, thump) and does not sound right. Plan:
- Source: close dry recordings of each weapon firing (`inbox/weapons/firing`, and the AKM, AR15,
  Glock, pistol and Shotgun folders). Handling, casing and casing bounce from the same folders.
- Physics after the muzzle: distance loss and air absorption, the muzzle blast's strong forward
  directivity, the ground reflection, the supersonic crack (N-wave, Mach cone geometry, arriving
  before the report downrange), and the existing reflections and reverb.
- Calibration: `inbox/weapons/cadreforensics` is the NIJ gunshot audio dataset (Cadre Research,
  2018): 20 firearms, each from 20 positions (several angles, 20 m and 40 m) in open desert, Zoom H4N
  recordings. Use it to measure directivity per weapon and to check the distance modelling against a
  real recording. `AudioDatafileDescription.pdf` gives the geometry codes (e.g. `ZM_041A_S02` is 90
  degrees, 40 m, shot 2).
- Measure first, then render WAVs for Cody before anything goes into the game.

### 7. Documentation

Plain language throughout. No mannered prose. Nothing written to sound clever.
- `readme.md`: what OpenFPS is, its strengths, its features.
- `docs/todo.md`: the planned work, as a structured list in priority order.
- `docs/changes.md`: the content of the recent commits.
- User manual. Recommendation: one manual in two parts, "Playing" (client: menus, keys, chat,
  lists, settings) and "Running a server" (starting it, maps, MOTD, admin commands, data files).
  Ask Cody to confirm one manual or two before writing.

## Answer given to Cody: the city's shapes

Everything on the city map is a rectangular box: all 5,866 objects. 72 are turned at angles other
than 0, 90 or 180 degrees; there are no true curves. The only curves are the vehicle and train
routes, which are smooth paths the traffic follows, not geometry. The engine defines Sphere,
Cylinder, Cone and Polygon colliders, but the acoustic scene (`SteamAudioScene.BoxesFromWorld`) and
the room survey only use boxes. Possible later work: curved kerbs and corners, round columns and
trees, and acoustics that handle non-box shapes.

## Still open from earlier sessions

- Windows client: adopt `ClientSettings`, saved servers, settings and the F-key lists (Linux first).
- Mac client. Web version: possible as a port; the hard part is audio (no Steam Audio for the web,
  threaded synthesis).
- Linux microphone (the input device setting is stored but not used yet).
- A real mourning dove recording; wing-flap takeoff sounds for startled flocks.
- Footsteps: Cody will source more materials. Turn the bird cutting script into a tool in `tools/`
  so new footstep material is cut the same way (the script is at
  `/tmp/claude-1000/birds/cut.py` only until the machine restarts).
