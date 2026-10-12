---
name: npc-doors-rule
description: Cody 10-10 - doors act by their mechanism first (closer shuts ~1 s after the last person leaves); NPC door rule never bends for nearby players
metadata:
  type: feedback
---

Cody 2026-10-10: "doors should work based on their functionality if that takes priority, a door spring loaded like
those push bar doors should close on their own after a second of them leaving. otherwise the rule of what the npc
does should take precedence, regardless of a player is around or not that shouldn't matter."

**Why:** realism - a door's mechanism and a person's habit do not care who is watching. This overrides his 10-02
complaint (driver shut Brandt Court's door he had opened) and the agent's 8 m / 2 m player exceptions.
**How to apply:** door behaviour = mechanism first (closer, sensor), then the NPC's rule (outside door without closer
shut; inside: as found going in, shut going out). Only physical obstruction (a body in the doorway/leaf path) delays
a shut. Never add courtesy exceptions for players. See [[doors-are-geometry]].
