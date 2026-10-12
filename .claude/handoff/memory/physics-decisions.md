---
name: physics-decisions
description: Cody 2026-10-11 on docs/PHYSICS_BOUNDARY.md - all 10 recommendations approved; soft bodies wanted; gravity a world property (default 9.80665) read by everything; engine choice open (Jolt recommended)
metadata:
  type: project
---

Cody 2026-10-11: "I approve everything you said here", plus: "I will want soft bodies as well as solid bodies";
"Gravity should be set as a property of the world, such that adjusting the gravity ... will effect everything on
the world....default is 9.80...for earth" (use 9.80665); asked whether to use a library or write our own.
Recommended Jolt Physics (MIT; soft bodies, ragdolls, vehicles, constraints, heightfield + mesh, deterministic,
x64+ARM64) via JoltPhysicsSharp (MIT, .NET 9/10) inside OpenFPS.Physics; keep ours: geometry/BVH as truth, the
approved walk, tyre model, ballistics, air/wind, fire, water and all sound coupling. Bepu (Apache-2.0, pure C#)
has no soft bodies. Recorded at the end of docs/PHYSICS_BOUNDARY.md. Related: [[engine-in-packages]].
**DECIDED 2026-10-11: Jolt Physics through JoltPhysicsSharp** (Cody: "yes record that"), behind our own types in OpenFPS.Physics.
