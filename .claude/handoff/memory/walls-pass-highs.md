---
name: walls-pass-highs
description: 09-30 walls were flat 55 dB filters (MaxWallLossDb cap), speech/one-shots never reach Steam Audio, FMOD low band over-blocked, stud walls solid; per-band panel model is the fix
metadata:
  type: project
---

Cody 2026-09-30: "highs were getting through walls; if anything walls should let lows and rumble
through ... no patch work, only real solutions." Measured (flat 01F --path-probe + scratch harness):
- AcousticRegistry.MassLawTransmission clamps at MaxWallLossDb 55 (:282/:304): every wall heavier than
  ~50 kg/m2 is a FLAT -55 dB filter (brick, plaster partitions, slabs). Physics: 25-40 dB more loss high
  than low. Fix: per-band panel model (mass law below fc/2, Cremer/Sharp above fc with eta_tot), flanking
  per band (EN 12354-1), no flat cap.
- Speech and one-shots (transient ids <= -100000) are skipped by the SA request loop (ClientAudioSystem
  ~613 `id < -5000`); they use the hand-rolled tracer (SpatialService 221-245, OcclusionFloor 0.1, old
  2.5*log10(t+1) table): 10-20 dB more leak, two walls quieter than one, slab transparent straight
  overhead (zero ray component breaks hit test).
- FMOD THREE_EQ crossovers 400/4000 get SA's 400 Hz figure in the low band: 12-18 dB too much loss
  at 50-100 Hz. Evaluate each band at the FMOD band it drives.
- SteamAudioScene.Box carries only material name: "plasterboard on studs" partitions are solid
  280 kg/m2 slabs (30 dB too heavy at 125 Hz). Needs double-leaf model (mass-air-mass f0).
- Partial visibility v+(1-v)T leaks flat near edges (SteamAudioSimulator 60-68). glass_wall 6 cm thick.
Traced echoes/tails leak nothing (sends are after the wall EQ). See [[doors-are-geometry]], [[a-fence-is-not-a-wall]].
**FIXED and merged 09-30 (7d966d98, unheard):** OpenFPS.Common/WallTransmission.cs (Sharp single panel,
double leaf with stud bridging, EN 12354-1 flanking), AcousticBands holds the FMOD crossovers; tracer uses
the same model, OcclusionFloor unread; speech/one-shots asked by source id (-3,000,000 - entityId).
Brick 35 cm -40/-62/-87, stud partition -18/-41/-50. Open items in todo.md section 1 (corner joint padding,
stacked floor slabs + carpet as barrier, cavity leaks, SA (2n+1)/3, diffraction bands). Lab --wall-tl.
