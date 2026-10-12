---
name: soundfield-faced-backwards
description: 09-29 03:30 — flat 01F "hallway in front, follows me when I turn": SA world was unmirrored while ListenerFrame was (Phonon.World fixes it, --sa-frame proves it); far walls dropped for a close source (HeardReference); others' steps had no path at submission; the room really is 8.65×17.86 m bare plaster
metadata:
  type: project
---

Cody (2026-09-29 ~03:00): "/tp -14 -85 0.5, clap: reverb like a hallway in front of me but the room
is carpeted; walkers outside through the wall; reflections piled in front and follow me when I turn".
Capture /tmp/openfps-capture.wav + /tmp/openfps-sa-on.log (02:52-02:53, 20 claps at 7 headings).

**Measured in the capture** (scratchpad clapcap.py / iacc.py; recreate: strip 44 bytes, s16 stereo
44.1 k, clap times = `recv 'clap'` minus the `Capturing the mix` line, onset = first sample within
12 dB of the peak, windows re the 0-5 ms direct):
- Direct L=R. 5-50 ms (placed echoes) swing 4-9 dB L/R with heading: they DO move.
- 50-200 ms: -22 dB in BOTH ears to 0.1 dB at every heading, flat (no decay), then ~1.1 s decay.
  Level RISES 3-8 dB from the 20-50 window to the 50-100 window: a hole, then a late omni blob.
  That is the "hallway in front". IACC per band ~1.0 <300 Hz → 0.2 >2 kHz (physical for a diffuse
  field, so width is NOT the fault). Tail leads left by 5 samples at 150-300 Hz (open, small).
- Lab `SA_PARAMETRIC=1 --traced-reverb` flat 01F: convolution windows -6 -15 -21 -25..., parametric
  -135 -17 -19 -22 (silent 50 ms, then flat), reverb times 0.68/0.79/0.80 s, W-only.

**Three code faults, all fixed and tested:**
1. MIRROR: SteamAudioScene vertices, TracedReverb/TracedEchoes/SteamAudioSimulator `Coord` origins
   and the probe box were raw game z; `ListenerFrame` flips z. Every traced IR decoded facing the
   wrong way along z (wall ahead = behind; facing west, north wall in the LEFT ear). Now everything
   goes through `Phonon.World` (z negated) and `PathingWorldDirection` un-flips. Proof: lab
   `--sa-frame` (X/W -0.49 → +0.49); `SA_MIRROR=0` shows the old world.
2. FAR WALLS: `EarlyReflections` judged audibility on keep×direct/path; for a 0.5 m clap anything
   past a 12 m round trip was dropped → flat 01F's end walls never placed → hole 25-50 ms. Now
   `HeardReference(direct)=max(direct,1)` for the audibility test only (gains unchanged). Test
   `TheEndWallOfALongRoomAnswersAClapAtArmsLength` (end wall at 34 ms, 22 arrivals <80 ms).
   `QueueRoomEchoes` now voices the 12 LOUDEST (Find returns surface order).
3. OTHERS' STEPS: `SubmitFootstep` submitted with no occlusion; the worker's answer for the pooled
   id came after the step ended. `ClientAudioSystem.CarryThePath` seeds Occlusion/EQ/aperture/
   bleed/region from `TryGetNearby` else `CalculateAcousticPath` (as WorldAudioPlayer does).

**Not a code fault:** flat 01F is 8.65 × 17.86 × 2.73 m, plaster walls+ceiling, carpet, one sofa
(city.json region 1098; walls z -93.79/-75.93, corridor wall x -18.68 with the door). A bare room
that size IS ~1 s. Furnishing belongs in gen_city.py. Written in todo.md.
**Round 2 (03:45), Cody after the first three: "better, more slapback, but still a consolidating
of reverb in front of me."** Cause: TailOnly = SA parametric = ONE channel (W) → same in both ears →
in the head/front, head-locked. Built `DiffuseTail` (TracedReverbDsp.cs): tail → 2-pole split at
300 Hz; the low goes straight to both ears; the high through 8 `DiffuseBranch` all-pass chains
(delays 89-431 samples, 14-18 ms each — SHORT delays left the branches coherent below 1 kHz), each
encoded (iplAmbisonicsEncodeEffect) from a cube corner in the WORLD (Phonon.World), summed, decoded
with the listener frame. Two calibrations measured at Create, never assumed: encoder W = 1/√(4π) of
input (`WScale`), and the decoder's coherent gain on a same-everywhere signal is +10 dB <300 Hz (the
OLD mono tail had 10 dB of extra bass = the boom); `DecodeTrim` from noise through the whole path.
Lab: `--sa-encode` (per-band level and IACC of the field vs W-alone), `--clap-room` now prints tail
L/R and IACC per band and alternates the heading per clap. Result: IACC 0.90/0.22/0.20/0.56/0.30/0.29
(real head in diffuse field ≈0.9/0.5/0.2/~0), level within 3 dB flat. `OPENFPS_DIFFUSE_TAIL=0` = old.
Unheard by Cody at the time of writing. See [[room-was-too-wet]],
[[decorrelator-was-a-reverb]], [[traced-reverb]], [[diffracted-bearing]].

**Round 3 (04:00), Cody with the diffuse-tail build (03:50 run): "reverb should be all around me
as I walk; it's concentrated in front".** Two more, both about walking (own footsteps):
- `SubmitStepReflections` returned at once in traced mode ("the trace carries these") — but the
  listener's stage is TailOnly (parametric, silent 50 ms), and QueueRoomEchoes only serves
  WorldAudioPlayer sounds. Own steps had direct + late tail, NO walls. Now `SubmitRoomStepEchoes`
  (ClientAudioSystem): EarlyReflections.Find order 3, 12 loudest < 80 ms, no DelayMs (facade delays
  by image distance), CarriesPath with wall EQ, pool 48.
- `SetReverbDirection` inside: blend = survey anisotropy → mono copy of the whole bus HRTF-placed at
  the return centroid. Now 0 in traced mode (room mode keeps it).
- DiffuseTail.SplitHz 300 → 120 (carpet steps are all bass; identical bass = in the head).
Client rebuilt 04:03 (release). Unheard. If still "in front": measure the capture per band with
clapcap.py/iacc.py (scratchpad) BEFORE touching anything; the loudest early copies in a 2.7 m room
are ceiling/floor/end-wall = median plane = L≈R by physics, and a generic HRTF cannot externalise
the median plane. Traffic tests (CrosswalkTests, CarFollowingTests) fail from earlier uncommitted
server work in VehicleSystem.Junctions.cs — not audio.

**Round 4 (04:14), Cody: "the send of all the reflections is way too hot... turn it down ~50%".**
Added `FmodAudioProvider.RoomTrimDb` (default -6, env OPENFPS_ROOM_DB, chat `/room -6`): multiplies
the placed room echoes (QueueRoomEchoes + SubmitRoomStepEchoes) AND the TailOnly stage Gain. Rooms
only; outdoors/open shelters untouched. Physics owed: flat answer 2-3 dB over the room equation,
diffuse tail bass +5 dB — so the default can rise toward 0 once those are fixed. Client 04:13.

**Round 5 (04:27), Cody: "/room -24 is where it's at, just like outdoors... everything sounds great
and accurate" and "outdoors now sounds way better too".** RoomTrimDb default is now -24 (client
04:27). APPROVED BY EAR: the mirror, placed far walls, others' steps, diffuse tail, /room -24.
What -24 twice tells us: copy/direct arithmetic checked within 1.6 dB of physics, flat tail within
3 dB of the room equation — the level is right; the two trimmed paths (placed room echoes, traced
per-source echoes) are COHERENT copies from a point, the untrimmed outdoor listener tail (accepted at
0 dB) is diffuse. Next experiment: scatter placed room copies by the wall's Scattering (Plaster 0.15,
Brick 0.45; WorldAudioPlayer.Diffuse / EchoDiffuser exist, EchoScattering on the emitter) and see how
far /room can come back up. Do NOT raise the default without Cody's ear.

**Committed & pushed 09-29 ~04:50: 9bc1da4 (the session) + the search prefilter commit.** HELD BACK,
uncommitted: five NEW voice dirs (glenn, steve, louis, ethan, presidents_kid, ~10 MB each) — the
clone-of-real-people question ([[next-cleansing-pass]]); asked Cody in the same message. The 10
already-public voices' new lines were pushed. `EarlyReflections.Find(maxExtraPathMetres:)` prefilters
to an ellipsoid; own-step search was 73-90 ms/step on the game thread before (five frames!).

**Round 6 (05:30): Cody confirmed the TUNNEL also wants -24 → not the room, not scattering (concrete
0.1). Asked for "the correct way" and to retire room mode. DONE, all built, 287 audio tests pass:**
- ONE RULE: QueueEarlyEchoes (was QueueRoomEchoes) runs everywhere (no enclosure gate); QueueReflections
  (facade first-order) and QueueHigherOrderEchoes skip arrivals inside the 80 ms window; steps get
  SubmitRoomStepEchoes everywhere; worker first-order reflections for sustained sample sources no longer
  skipped indoors; the listener's stage is TailOnly EVERYWHERE (`TailEverywhere`, env OPENFPS_TAIL=full
  = old outdoor behaviour for A/B); EnableReverb = !IsReflection.
- ONE TRIM: `FmodAudioProvider.ReflectionsDb` (default -24, env OPENFPS_REFLECTIONS_DB, chat
  `/reflections`, `/room` and `/echoes -N` alias) on placed copies (all four queues), every traced
  stage's Gain, and the echo rigs (TracedEchoTrimDb deleted).
- RETIRED: TracedOutdoors/OPENFPS_REVERB/`/reverb room`, SFXREVERB tail (unit kept as dry passthrough
  insert point: wet -80, dry 0 at creation), ApplySimulatedReverb body (now just AdvanceListenerRoom
  for _listenerEnclosure), _listenerWetDb & co, _dryReverbBuses, overrides, SetListenerReverbField
  (interface+facade+CAS; test fakes keep a harmless method), anisotropy steering (inside → blend 0),
  old 3-tap step echoes, labs --open-air-reverb/--tailcheck/--reverb-route (files deleted).
- Lab: --clap-room at -24: tail -39 to -43 dB re direct; at 0: -15 to -19. --sa-frame 4 PASS.
UNHEARD by Cody. Full suite running; two traffic tests fail from earlier server work (not audio).

