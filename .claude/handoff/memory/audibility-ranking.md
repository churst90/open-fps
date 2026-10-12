---
name: audibility-ranking
description: "Voices rank on rendered level, not authored priority; extent must pay the gain down"
metadata:
  type: project
---

Session 10, commit `ca7b193` (step 2 of [[next-order-voices-machines]]).

- `SpatialEmitter.Priority` **no longer exists**. Ranking is `Loudness.RenderedGain(volume, minDistance,
  range, distance) * (1 - occlusion)` — the mixer's own law, so the score IS the balance. Do not
  reintroduce a per-object importance number; the only override is `SpatialEmitter.Essential`, a short
  pinned list (own footsteps, own landing, speech).
- **Continuous fades, one-shot drops.** `IVoiceSink.FadeOut` / `CancelFade`; the provider slews
  `ActiveSound.FadeGain` (80 ms) inside its own per-update volume pass. Both halves are required —
  a voice that wins its slot back mid-fade must be brought round.
- **The budget is asked of the mixer every frame**: `VoiceManager.MaxVoices = PlayingCount +
  IAudioProvider.SpatialVoicesFree` (96 HRTF slots, shared with the voices that bypass VoiceManager).
  A voice with no HRTF slot plays FLAT, which is worse than not playing it.
- **Extent is not a discount.** Widening a reference distance without paying the gain down makes a
  louder source, not a bigger one — the inverse law carries the PRODUCT. `Loudness.Widen` does it;
  `MathF.Max(reference, 3f)` used to do it wrong and was worth +8 dB to every quiet vehicle. The lab's
  older spikes still contain that fudge.

Open: nothing headless can check the `without HRTF` count through a crowd reaction, and
`AudioEngineFacade` (which sets the budget) needs FMOD to construct, so neither is tested.

See [[machines-as-parts]], [[a-source-has-a-size]], [[no-special-cases]].
