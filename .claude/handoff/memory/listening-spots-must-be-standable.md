---
name: listening-spots-must-be-standable
description: 10-10 /tp lists from agents put z at mid-room and spots inside walls/ceilings ("area is solid"); verify every spot with the server's collision check, z = floor top
metadata:
  type: feedback
---

Cody 2026-10-10: "those cords you gave me for the city other than the tunnel said cannot move there, area is solid"
and at 19617 Turtle Dove "i can't /move or /tp to those cords". Cause: agents gave room centres (z = mid-room, so
the body hit the ceiling) and once the centre of a 4 cm sliver room beside a wall.

**Why:** Cody tests by /tp; a spot he cannot stand on wastes his listening session.
**How to apply:** every /tp handed to Cody: z = the floor's top, checked with the server's own collision check
(MovementSystem.CheckCollision / teleport path) before sending; ask agents for verified spots in every brief.
Related: [[player-coordinates]].
