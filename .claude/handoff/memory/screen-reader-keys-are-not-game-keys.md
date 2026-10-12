---
name: screen-reader-keys-are-not-game-keys
description: Control was the trigger — Cody fired a 159 dB rifle every time he silenced NVDA; five sessions lost to it
metadata:
  type: feedback
---

**Control and Alt may carry no gameplay or global action.** `ClientGameSession.ScreenReaderKeys`
names them and `ScreenReaderKeyTests` asserts it.

Control was bound to Fire. Control is how every screen reader — NVDA, JAWS, Orca, VoiceOver — stops
talking, so Cody presses it dozens of times a minute as punctuation, never as input. What it did was
fire an AKM at **159 dB**, with its bullet's impact echoing off the buildings.

Reported across five sessions as *"random banging... bang, wait a few seconds, bang, like someone
closing a cabinet or banging on something, I have no clue what the noise is"* — and chased through
the reverb model, the room equation, the movement engine, the landing code and the reflection
machinery before the `[WAUDIO]` trace named it: `26 × recv 'AKM' from e417: Knock 0 Hz 159 dB`,
in bursts minutes apart.

The trigger is **Enter** now: findable by touch without counting from a landmark, under the right hand
already on J K L O for turning, and unclaimed by any reader in a focused game window.

**Why:** a game action on a screen reader's key is not a key that is awkward to use. It is a key that
**fires by itself**, and the player cannot tell it is them.
**How to apply:** before hunting an unexplained sound, check the BINDINGS. And when adding any
binding, check it against `ScreenReaderKeys`. See [[instrument-must-walk-like-the-game]] —
the `[WAUDIO]`/`[FOOT]` traces are what finally named this; add the trace before the fourth hypothesis,
not after it.
