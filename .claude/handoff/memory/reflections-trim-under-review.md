---
name: reflections-trim-under-review
description: 09-29 Cody thinks /reflections -24 is a band-aid; it was tuned with the old materials.json overrides active, which are now gone
metadata:
  type: project
---

2026-09-29: the client merged `OpenFPS.Client/materials.json` (June; Concrete absorption 0.2 vs the
table's 0.02, Carpet 0.95 vs 0.6, Wood 0.35 vs 0.15, scattering too) over AcousticRegistry. Tests, lab
and server never loaded it. Removed in 1c4f2e21; copy at `docs/retired/materials-2026-06-22.json`.
Cody's ruling: "if the technically correct values are in the materials table, stick with that".

`/reflections -24` (d8a031a9) was found by ear WITH those overrides in effect. Cody's view: needing 24 dB
of trim everywhere means the model is counting something wrong, not that reflections want trimming.
A deep-research agent was started that day to find the real cause (double counting, gain staging vs
the loudness law, distance law, energy vs pressure coefficients, Steam Audio IR level, perception).
**Research answer (09-29):** at /reflections 0 the levels are physical or a few dB DRY (flat 01F clap:
copies -14.3 dB, tail -16.6 re direct; room acoustics says ~-10 and -13.6..-19). So -24 is ~24 dB under
physics and IS a band-aid for: (1) copies being sample-identical replicas of a dry click (heard as
separate echoes; biggest, perceptual); (2) real double counts: traced echoes of far sources count the
ground a second time (~0 dB comb, likely the first -24 on 09-26), and BoundaryProximityProcessor on the
MASTER bus adds up to six copies of the whole mix outside the trim; (3) copy gain
`GainMid*PathLength/direct` is wrong inside the loudness reference distance (flat there): loud sources'
copies +8..+11 dB hot; fix = RenderedGain(d)/RenderedGain(L). Also keep=1-alpha used as amplitude
(should be sqrt), carpet table high vs published. materials.json explains almost none of it.
Proposed: split trim (tails vs copies), copies as reflections (specular sqrt((1-a)(1-s)) + diffuse),
fix copy law, no-ground trace for far echoes, retire/trim the boundary comb; target 0..-6 dB.
Scratch measurement code: session scratchpad refl/ (Program.cs, Chain.cs).

**Built 09-29 (de51c767, 6095b45e, 86838480, unheard):** step 1 copy law PlacedCopyGain, far echoes
trace the ground-free scene, boundary copies under the trim; step 2 /tail (default 0) and /copies
(default -24) replace the one /reflections; step 3 Keep = sqrt(1-a), mirror share sqrt(1-s)^order,
first-order wash, max 4 second-order copies. Protocol for Cody: /copies -80, judge the tail at 0, then
copies up in 6 dB steps. If copies still want much below -6, something is still wrong.
**Heard 09-29 night: /tail 0 REJECTED.** "a general wash, like a mask over where the reflections are
coming from", tunnel and flat; it masked the copies so /copies -6..-80 made no audible difference.
Tail default back to -24 (11e26d0d). So the level measurement was not the answer for the TAIL: at its
physical level it hides direction. Open question: why the traced tail masks direction (diffuse
decode? no early/late gap? spectrum?), not just "how loud". **Settled by ear 09-29 late: /copies 0 dB (physical), /tail -24** (de97fac8 defaults). So the COPIES'
-24 was a band-aid for the replicas (fixed by step 3); the TAIL's -24 is still unexplained (it masks
direction at its physical level) and is the open question.
Also fixed: leaving a room cut its ring in 25 ms (per-pass 0.15 lerp); now decays at the room's RT60
(6c0ea010). Tunnel regions have NO portals to outside, so from outside its reverb bus target is 0.

Same day: diffraction made reciprocal and exact (978a64fc, closed-form edge minimum, 20 us/call),
so doorways and low walls lose less; open-fps-patches patch 5 (walls block their full mass-law
figure, Steam Audio transmission per face to the 2/3) is NOT applied, awaiting Cody's ear.
See [[never-normalise-the-room-away]], [[soundfield-faced-backwards]], [[a-reflection-is-the-room-answering]].

**Tail answered (09-29 research, code claims verified):** the "tail" is NOT the traced response at its
level. TailOnly renders SA's PARAMETRIC reverb (TracedReverbDsp ~:347), an FDN that takes only the three
RT60s and has no level input; SA levels it only in HYBRID mode via eq/delay, and TracedReverb.cs:188 sets
hybridReverbTransitionTime = DurationSeconds so eq = 0. Measured vs source-side traces: tail +14..+20 dB
too loud in the tunnel, +8..+11 street, 0..+3 flat (+5 dB late, decays 0.93 s vs 0.66-0.74). Time shape:
silent to 60 ms, 7 dB step, plateau to ~150 ms = "echo over the room". Isotropic (DiffuseTail 8 equal
corners) where tunnel/street late energy is directional (|I|/E 0.4-0.85, flat 0.07); tunnel distance
falloff 1.8 dB over 5-45 m vs 7 physical (d^enclosure send, enclosure 0.9). IACC 0.60 @2k (order-2 decode).
Fix plan: (1) our own convolution of the traced late part (impulse through a private effect to get the IR,
raised-cosine 50-100 ms), retiring the parametric path; (2) per-source late gain + direction from
source-side traces (extend TracedEchoes, energy only); (3) diffuse-field coherence decode. Then /tail 0.
Scratch instrument: session scratchpad refl/Tail.cs (AudioLab `tail place=flat|tunnel|street`).
Door (same day): SA scene built once with every door leaf SHUT; InsulationFactor blankets enclosures.
Proposed: doors as instanced meshes, retire the blanket, patch 5 with it. Awaiting Cody.
**Tail rebuilt 2026-09-30 (22e99c9a, 0804206b, 67c00d0d, 2c2195cb), UNHEARD:** (1) LateTailIr/
LateTailConvolver: the listener trace read back each run (ExtractReader = MaxReaders-1, stage readers
capped below it), late W faded in 50-100 ms, own partitioned convolution (70 us/block at 2 s);
OPENFPS_TAIL_PARAMETRIC=1 = old. (2) LateField: 16 loudest sources + reference at the listener,
energy + first-order direction (axes calibrated via SA encoder); send = d*sqrt(ratio), untraced =
fitted d^-k law; DiffuseTail leans (1+3cos). --late-field matches source traces within ~1 dB.
(3) DiffuseTail: 20 dodeca directions (tilted 17/11 deg), velvet branches, per-direction binaural,
ears decorrelated >400 Hz (LR4, interleaved-slot velvet EarPair); --tail-iacc vs 200-dir reference.
/tail default 0 again. Mixer 4.8 %. If Cody still hears a wash at 0, something non-physical remains.
**Heard 09-30 night: /tail 0 still "way too much, boomy, centralised, metallic"; Cody's by-ear: tail -24,
copies -6 (defaults, ca028f09).** Found: (a) my EarPair interleaved slots were a 5.4 ms comb (fixed,
free-jitter velvet); (b) the synth clap itself rings at 1.9 ms (0.67) — not the room; (c) --tail-bands:
the flat's trace decays 0.68-0.82 s in EVERY octave vs Sabine 0.72/0.65/0.52 (Eyring 0.64/0.57/0.44)
from the same materials: the top hangs on — NEXT FAULT (SA band absorption? scattering? bounces?);
(d) < 120 Hz of the tail is identical in both ears (in-head) — suspect for "centralised".
Cody asked what the tail is: it IS traced from the walls; the 20-direction/velvet rendering is ours.
**SDM built 09-30 (e3fb40f1, unheard):** Cody: reverb "in front of me no matter which way I turn, a
mass". Literature: SDN (one node per wall, De Sena 2015), SDM (Tervo 2013), generic-HRTF front/in-head
collapse cured most by head movement (Begault & Wenzel 2001). Built SDM: tracer reads first-order channels,
SdmTailIr splits 50-350 ms per sample by DOA (AmbiAxes-calibrated) into the 20 directions, own HRTF each,
no ear decorrelation; diffuse after 0.25-0.35 s. Flat 01F: 70 % of that part from OVERHEAD (ceiling),
net pull 0.64 up — generic HRTFs localise overhead poorly (often front/in-head). Mixer 7.9 %.
OPENFPS_TAIL_SDM=0 = single channel. Defaults still tail -24, copies -6.
**Heard 09-30: SDM "now we're getting somewhere ... a world of difference". Levels by ear: tail -12,
copies -6 (5820958e); 0 "not bad, maybe a little too much".** Remaining physics gaps: trace HF decays as
long as LF (0.79 vs Sabine 0.52); diffuse remainder after 0.3 s spread evenly (could lean along the
trace's own late direction for long spaces — offered). Cody asked if a hallway turned sideways sounds
longer: yes, SDM places the end reflections at the ends.**Settled 2026-09-30 (b8642531, 1e7a57af):** after SA's own bands (400/2500/15000 interpolation) and the
remainder weighted by the trace's late direction, Cody: "I am happy with -6 and -6 now". Defaults tail -6,
copies -6. The HF decay gap was re-read: the flat's absorption is nearly all carpet floor, so Fitzroy
(0.66/0.88/1.02) is the right comparison, not Sabine; the trace sits between Eyring and Fitzroy = physical.
todo.md "Now 1" still talks about -24 and is STALE.
