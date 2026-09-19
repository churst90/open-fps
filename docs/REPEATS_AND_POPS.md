# The megaphone that repeated, and the steps that popped — diagnosis (2026-09-18, session 15)

Three reports from the rooms map, after session 14 had put in image-source reflections, the
enclosure-driven reverb and the fusion gate:

1. *"The megaphone, in the wood room and the gym: I hear like 2 copies, one latent like it is
   echoing off something way far away. If I step outside I don't hear it reflecting off anything."*
2. *"Lots of popping and clicking when I walk around this room. Each time I step I hear a pop pop
   pop click. It's almost like there's too many reflections being piled on when I walk around."*
3. *"Nothing we've done has worked."*

Number 3 is the important one, and it is true: none of session 14's work touched the mechanism
behind 1, and the mechanism behind 2 is not one that a waveform detector finds.

## 1. The repeat was two more playbacks of the file, made by code nobody had looked at

`ClientAudioSystem.ProcessAudioEmitter` still carried two reflection generators from long before the
image-source model:

- **Floor slapback** (`-10000 - id`): cast a ray straight DOWN from the emitter, and at the hit start a
  second playback of its sound at 30 %, **pitched down 2 %**, high band cut to 70 %.
- **Cone reflection** (`-5000 - id`): for any directional emitter, cast a ray ALONG its beam, and at the
  hit start a second playback at 40 %, `EnableReverb`, never occluded (derived ids skip the acoustic pass).

Both rays went through `SpatialService.RaycastSingle`, which tests every entity with a collider —
**solid or not, including the emitter's own** — and `GeometryUtils.RayIntersectsAABB` returns distance
**zero** for a ray that starts inside a box. So the megaphone's "floor" and "wall" reflections both sat
AT the megaphone. The live log had it: `[BEACON] e-10019 'BEACONS/megaphone' PLAYING pos=(7,2,15)` and
`Voice -10019 was placed at a position 31636 ms old`.

Two extra reads of a 5.7 s loop at unrelated positions in it is "two copies". The pitched one drifts
113 ms per loop against the original, so the offset cycles through every value: sometimes fused,
sometimes a clear echo "off something way far away". And because they are never occluded, stepping
outside changes them differently from the direct voice. Everything in report 1, from two code paths
that session 14's fusion gate never saw, because they were not in the image-source pass.

**Both generators are deleted.** The floor is a box face and so is the wall a beam points at; the
image-source pass mirrors the source through both, with the material and the extra path, and makes a
voice only when the ear would hear a separate event.

## 2. The image-source voices had three faults of their own

- **A copy started from the top of the file.** `setDelay` schedules the START of a second read; the
  read began at sample zero while the source was mid-announcement. Now `SpatialEmitter.ReflectionOf`
  names the source, and `PlayPhysicalSoundDirect` sets the copy's PCM position to the source voice's
  before scheduling it — it lags by exactly the path's extra delay, which is what a reflection is.
- **A copy joined mid-file with no ramp** — a step from silence each time a wall started answering. The
  6 ms onset ramp the one-shots get now applies to reflections too, from their scheduled start.
- **Nothing stopped a copy whose surface stopped answering.** Once started, a slot's voice played on at
  its last position for as long as the source did. Slots not answering this frame are faded (the
  budget's 80 ms ramp) and stopped once silent; a slot that comes back cancels its fade.
- Also: the pass asked for a sample playback of `engine:v8_muscle` for every car, every frame ("not
  playing yet — Missing" ×2 per frame in the log). Engines have no file; their walls are
  `EngineReflections`' job. Skipped.

## 3. The pops are a per-step event the engine does on purpose, not a broken stream

Session 14 found and removed a real stream fault (`VOL0_BECOMES_VIRTUAL`). The five-minute capture Cody
made AFTER that fix (`/tmp/openfps-capture.wav`, 21:57–22:02) was measured again here: **no clipping,
no holes, one 2nd-order-residual discontinuity in 300 s, and none inside the rooms.** The stream is
clean. What is left is musical.

To bracket it, `AudioLab --room-walk` (new, `Spikes/RoomWalkSpike.cs`) drives the real provider
through the wood room's own boxes: megaphone with its cone, a listener walking a rectangle with
footsteps, the enclosure survey and the boundary probes exactly as the client sends them, mix written
by FMOD's file writer (silent). Seven variants — everything on, then cone / reverb survey / boundary
probes / footsteps / megaphone off, and standing still — all measure clean: worst residual ratio under
2, no steps, no holes. So the graph does not pop for any of the mechanisms the spike models.

What the spike does NOT model, and the live client does, is the acoustic worker. In Cody's capture the
footsteps in the wood room are **doublets**: a quiet onset at −32…−40 dBFS and then, 16 ms later —
one game frame — the real one 10–20 dB louder. The spike's footsteps have no such doublet.

Footstep voices cycle through twelve pooled ids. `AsyncAcousticWorker` keeps the last result per id
and `TryGetResult` hands it back every frame. So a fresh step was given the PREVIOUS occupant's result
for its first frames — the occlusion, the per-band EQ and the **apparent position** of a step taken six
seconds ago somewhere else in the room — and `UpdateSpatialPositioning` started gliding the voice toward
that place (and `Loudness.RenderedGain` attenuating it for that distance) until the worker's fresh
answer arrived and it snapped back. A quiet click from the wrong place, then the step. Per step.

**Results now carry `SourcePosition`** (stamped by the worker's `Store`), and `ClientAudioSystem` applies
a cached result to a NON-entity voice only if it was computed within a stride of where the voice is now
(`ResultIsForThisVoice`, `StaleAcousticResultTests`). Entities keep their last result between ticks, as a
moving car must.

Two more per-step hazards closed while there:

- `NotifyMaterialChange` rebuilt the whole acoustic map — every reverb bus, every binaural stage on
  them, every send, mid-tail — whenever the floor's Sabine time moved. It no longer does; the tail is
  surveyed from the boxes round the listener, and the floor is one of them.
- The per-frame filter writes (3-EQ, cone timbre, reverb decay/colour) were suspected and measured
  clean in the spike (cone on vs off: p99.9 residual ratio 1.30 vs 1.76 — both far below a step).

## How to hear it, and what to read

- `./run-server.sh rooms`, `./run-gtk-client.sh capture`. Stand by the megaphone at `7 1.6 12`: one
  announcement, not two. Walk the room: the reverb (≈400 ms, wood floor, concrete walls) and your
  steps, nothing that starts elsewhere. Outside at `7 1.6 5`: the room heard through its door.
- `AudioLab --room-walk [cone=off] [reverb=off] [boundary=off] [steps=off] [megaphone=off] [still]
  [echo=on]` from the lab's bin dir, then the detectors in the scratchpad (`artefacts.py`, `zipper.py`,
  `onsets.py`) or `tools/ingest_audio.py`. The recorded sound sets are not in the lab build: link
  `OpenFPS.Client/ASSETS/SOUNDS/{BEACONS,FOOTSTEPS}` into the lab's `ASSETS/SOUNDS` first — the
  spike says so and refuses to run silent.
- In the log: no `e-5xxx` / `e-10xxx` beacon lines, no `engine:… not playing yet — Missing`, and no
  `Voice -10019 was placed at a position N ms old`.

## What is deliberately not changed

- The reverb's level law (`Enclosure.ReverberantGainDb`) and send (`ReverbSendMix` 0.35) — the wood
  room sits at −1 dB wet, 400 ms, and that is a room, not a fault. If it is too much for a footstep,
  that is a critical-distance question (`todo.md`) and a measurement, not a constant.
- The master limiter: with footsteps at the feet the mix rides its ceiling (−1 dBFS) on every step.
  Real, and separate.


---

# Second pass, same evening: the clock, the feet, and which way the room answers

Cody, after the first pass: *"still loads of popping when I walk around... outside it sounded like
I was hearing footsteps in a room... in the half and half room, pops and clicks every footstep and I
thought I was hearing door open and close sounds... footsteps sounded like they were on carpet in a
metal room... when I move right, I hear the footsteps trailing to the left."* A new 12-minute capture
(22:40–22:53) and its log were measured the same way.

## The stream is still clean; the events are not

4 discontinuities and 2 dropped blocks in 764 s. So, again, not a broken waveform. The log had the
rest.

## 1. No scheduled delay in the engine had ever worked (`AudioLab --room-walk clock`)

`setDelay` and `addFadePoint` take the PARENT ChannelGroup's DSP clock. The provider read the
channel's OWN clock — the first out-param of `getDSPClock` — which for a channel that has not started
is not the parent's. Every start time and fade point was therefore in the past and FMOD honoured none:
a footstep asked for with 300 ms of delay began at +24 ms, the same as one with none; a reflection
scheduled 200 ms late began at once. Consequences, all of them old:

- every reflection voice played in exact sync with its source: a comb filter, heard as a metallic
  ring — "carpet in a metal room";
- the "delayed" copies were never delayed, so a slapback could not exist;
- the one-shot onset ramp never applied.

Fixed by reading the parent clock. Measured after: the 300 ms footstep starts at +308 ms, and a
reflection of the megaphone scheduled 100 ms late shows a clear autocorrelation peak at exactly 100 ms
where before there was none.

## 2. Your own feet now ride with your head

Own steps were placed at the physics `Position`; the listener stands at the smoothed `VisualPosition`.
Each step landed wherever the two disagreed at that instant, then was left behind as the listener
walked on: in the log one step's bearing went from straight down to 30° behind during its 300 ms.
That is "the footsteps slide all around me", and step by step it is a click from off to one side.
`SpatialEmitter.FollowsListener` + `ListenerOffset`: an own step (and landing) is placed at a fixed
offset under the listener's head every tick, un-eased. `ClientGameSession` wires the local controller
to `OnOwnFootstep`/`OnOwnLand`; other bodies' steps stay world-locked where they fell.

## 3. The "door opening and closing" was the reverb bus swapping roles at every crossing

`SetReverbDirection` decided per bus: inside → bypass the binaural stage; outside → engage it and aim
at the nearest portal, with the bypass toggled at the bottom of a ramp. The material lab's two halves
are two regions joined by an 8 m portal, so EVERY crossing of x = 0 swapped which bus was "inside",
re-engaged a stage on a stale tail and swung the other's direction to the join — a whoosh from the
join per crossing (12 crossings in the capture's timeline). Same at the wood room's door.

Replaced with one rule and no switch. The survey (`Enclosure.Look`) now also reports where its returned
energy came from — `Survey.ReturnDirection`, the energy-weighted mean of the ray directions, and
`Survey.Anisotropy`, how one-sided it is. The listener's own bus is steered to that direction with
blend = anisotropy; another room's bus is still aimed at its opening with blend 1. Both smoothed,
continuous as you turn and walk. The binaural stage is never bypassed: `SteamAudioDsp` crossfades its
own stereo input through at low blend inside the callback, so there is no toggle to click.

This is also the answer to "on carpet, facing the concrete half, the wash should come from in front":
carpet behind returns little, concrete in front returns nearly everything, the centroid points at the
concrete, the bus is placed there. `ReverbFieldDirectionTests` (4): a carpet/concrete hall from the
carpet side points east with anisotropy > 20 %; a uniform room < 15 %; one wall in a field > 60 %.

## 4. The engine spelling

The image-source pass skipped engines by `ENGINE/`; the snapshot says `engine:`. Both now.

## What this does NOT do, and why it is reported rather than tuned

"Thick carpet should be 100 % absorption, nothing." The registry's Carpet is 0.15 / 0.50 / 0.75
absorption low/mid/high — a thin office carpet. The wet level on the carpet side follows those
numbers through the same law as everywhere else, so raising them is a MATERIAL decision, not a rule.
Measure before changing (`AudioLab --sim-reverbfield` style, or a probe of `Enclosure.Look` at the
carpet centre) — and consider a `ThickCarpet` material rather than moving `Carpet` for every map.

Also unchanged: the wet/dry ratio has no distance term (see todo: critical distance). Outdoors near
the buildings the survey reads ~15–25 % enclosed and opens the bus a few dB — "footsteps in a room"
outside near walls, dissipating as you walk away — which is the law working, at a level that may be
too high for a source at your own feet. That is a critical-distance question, and the next one.


---

# Third pass: the level of a footstep, and the level of a room

Cody, after the second pass: *"the footsteps are still loud... lots and lots of popping every time I
take a step... like 4 or 5 copies of reflections piling up... outside it still sounds like I can hear
reflections from my footsteps... behind the megaphone I can hardly hear it from the other side of the
room... can't tell the carpet from the other side... sometimes my footsteps don't play."*

Measured with `--room-walk` (footsteps only, then with the megaphone), per step: energy in the first
120 ms, energy from 120 to 500 ms, and the peak. FMOD's own `Mix loudness` line alongside.

## 1. Every footstep was on the master limiter's brick wall

| build | step peak | mix peak |
|---|---|---|
| before, any setting, reverb on or off | −1.0 dBFS | −0.9 dBFS |
| before, maximizer makeup at 0 dB | −1.6 dBFS | −1.0 dBFS |
| after (below) | **−16.1 dBFS** | −6.8 dBFS |

Footsteps played at `Volume 1.0`, `MinDistance 1` — full scale, which on this engine's loudness scale
is a 112 dB source a metre from the ear (`Loudness.RenderCeilingDb`). A step is 55 dB
(`Loudness.FootstepDb`, already in the model and used by nothing). Even with no makeup a dry step
peaked at −1.6 dBFS; with the maximizer's 10 dB it hit the wall by nine decibels on every step, and
the limiter's 50 ms release pumped everything under it. That is "footsteps are loud" and a pop per
step, and it was independent of every reflection mechanism — which is why nothing done to reflections
changed it. Own and others' steps and landings are now placed by `Loudness.Place(FootstepDb)` like
every engine and transient: −26 dB, reference 1.2 m.

## 2. The reverb had no distance in it and the unit's gain was never measured

The wet level was `10·log10(e/(1−e))` — a room's ratio, with no source distance — sent at a constant
0.35 from every source, into an SFXREVERB whose steady-state gain turned out to be **+16 dB** (the
metering loop below measured it). So a footstep's reverberation exceeded the step.

Now: `Enclosure.ReverberantToDirectPower(e, MFP, r)` — the room equation with the surface and
absorption replaced by the survey's enclosure and mean free path — is applied per source in its
SEND (`sqrt` of the power ratio, post-fader, capped at 20 dB). The bus's wet level has one job: hold
the unit's gain at unity, by FMOD DSP metering of the unit's own input and output, slowly tracked. So
a step in the roofless wood room reads a few dB under itself, the same step in a sealed concrete cell
reads well over, and open ground reads nothing. `CriticalDistanceTests` (6).

The unit's own synthetic early reflections are off (`ReverbEarlyReflectionsPercent 0`) and its tail
starts after two mean free paths — early reflections are geometry's, per source. Measured: this made
no difference to the peaks (the limiter was the peak), but it is the right division.

Outdoors near walls: e is small and MFP short, so the field is a few percent of the direct at most
and gone a few metres from the wall. Open ground: e = 0, nothing — "outside should sound like outside".

## 3. Behind the megaphone

The send hung off the fader, which carries the cone attenuation: from behind, the room's reverberation
of the megaphone was down by the same 26 dB as its beam. A room is excited by what a source radiates
in every direction, so the send is now divided by the cone attenuation. From behind you hear it
through the room, as you would.

## 4. The floor tap of the near-field comb is gone

The comb's copy is the round trip from the HEAD to a surface — right for a wall beside you, wrong for
the floor under you, which every source stands on and every footstep is ON. It was a copy of every
sound ten milliseconds late at a fifth of its level, everywhere, including open ground. The
image-source pass already mirrors each source through the floor box at that source's own geometry.

## What is reported, not changed

- **"Sometimes my footsteps don't play."** `StrideAccumulator`: a step fires every 0.5 m of travel at
  more than 0.5 m/s. A tap of A or D that moves you 20 cm is a shuffle and makes no step, by design.
  If a tap should always answer, that is a stride-model decision, not a bug in the audio.
- **Carpet against the gym.** The survey's steer from the carpet half is real but mild (anisotropy
  25 % against 27 % for all-concrete; centroid pushed east by the carpet) because the registry's Carpet
  absorbs 0.15/0.5/0.75 — a thin carpet. A thick one is a material with higher numbers, and a map may
  say so. Nothing in the code should.
- **The rest of the near-field comb** (walls within 3 m) still runs, and it double-counts the
  image-source pass for sources near the listener. If walking along a wall still colours the mix,
  that is the next thing to retire.

## References

- Steam Audio C API guide — https://valvesoftware.github.io/steam-audio/doc/capi/guide.html —
  sections "Simulating reflections" / "Rendering reflections" (`iplReflectionEffect`, convolution or
  parametric, Ambisonic output decoded to binaural) and "Reverb" ("place the source at the listener
  position"). That is the designed way to get reflections that arrive from the right place at the
  right time: the reflections are convolved INTO the source's own signal, never a second playback.
  This engine's `SteamAudioSimulator` still supports the reflections stage; it was retired because only
  its parametric RT60 was used. Rendering per-source convolution reflections through it is the
  principled next step if image sources plus a tail are not enough.
- FMOD SFX Reverb parameters: `DSP_SFXREVERB` in `FmodNative/fmod_dsp.cs` (index order); early/late
  mix, late delay, wet/dry as used in `ApplySimulatedReverb`.
- Room equation / critical distance: reverberant-to-direct ratio (r/r_c)², r_c = √(R/16π),
  R = Sā/(1−ā). Kuttruff, *Room Acoustics*, ch. 5.
