# OpenFPS: Issue Tracking

## Issue: The "Negative Falling / Phantom Floor"
### Status: RESOLVED (March 8, 2026)
### Root Cause: The "Archetype Identity Crisis"
The issue was identified as a C# method overload ambiguity in the `Arch` ECS library. When calling `world.Create(object[] components)`, the compiler/runtime was treating the array as a single component of type `object[]` rather than a list of individual components. Consequently:
*   Entities like "Ground Floor" had no `Transform` or `ColliderComponent` registered in the ECS sense.
*   Queries for `WithAll<Transform, ColliderComponent>` returned zero results.
*   The `SpatialGrid` was never populated, and physics probes returned `-1000f`.

### Solution
Implemented the **Explicit Component Registration** pattern across all spawning systems (`MapManager`, `PrefabRepository`, and `GameServer` player spawning).
*   **Action:** Switched from `world.Create(components)` to:
    ```csharp
    var entity = world.Create();
    foreach (var c in components) { world.Add(entity, c); }
    ```
*   **Result:** This forces `Arch` to evaluate each component type individually, establishing the correct Archetype for queries.

---

## Issue: Map Boundary "Infinite Fall" at Edges
### Status: OPEN (March 8, 2026)
### Description: 
The map is currently 100x100. When a player walks past `X=50` or `Z=50`, they fall into the void. The `MovementSystem` safety clamp (`Math.Max(groundY, mapMinimumY)`) then "springs" them back up to the safety floor, but they remain stuck in a falling state because there is no physical floor to stand on.

### Proposed Fix:
Implement a "Hard Stop" boundary in `MovementSystem.cs`.
1.  Query the `ZoneComponent` for map dimensions.
2.  In the horizontal movement phase, clamp the `nextPos` to the map half-extents (e.g., `[-50, 50]`).
3.  This prevents players from exiting the defined physical area of the map until a "Map Streaming/Transition" system is implemented.
