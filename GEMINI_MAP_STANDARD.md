# OpenFPS Map Specification (v1.0)

## 1. Directory Structure
Maps MUST be placed in the `maps/` directory relative to the server executable.
Each map is a `.json` file named after its `Id`.

## 2. Root Properties
- `Id` (string): Unique identifier (e.g., "default").
- `Size` (Vector3): Overall dimensions of the play area.
- `MinBound` / `MaxBound` (Vector3): Axis-aligned boundaries for spatial partitioning.
- `SpawnPoint` (Transform): The initial position and rotation for players.
- `Entities` (Array): List of all static objects, beacons, and volumes.

## 3. Mandatory Entities
- **The Floor**: At least one entity MUST have a `Collider` with `IsSolid: true` located beneath the `SpawnPoint`.
- **Zone Metadata**: (Optional) A `ZoneComponent` can define gravity and safety floor height.

## 4. Component Requirements
- **Transform**: Mandatory for all entities.
- **Collider**: Required for physical presence and reflection surfaces.
  - `Size`: Must be non-zero.
  - `IsSolid`: True for walls/floors.
- **Acoustic**: Required for echo/reverb logic.
  - `Absorption`: 0.0 (full bounce) to 1.0 (silence).
- **Material**: Used for footstep sounds and reflection filtering.
- **Region**: Defines a room volume.
- **Portal**: Must connect two `Region` IDs (use -1 for "The Outside").

## 5. Entity ID Standards
- `1-99`: Reserved for system entities (Floor, Sky, etc).
- `100-499`: Reserved for static geometry and rooms.
- `500-999`: Reserved for beacons and interactables.
- `1000+`: Dynamically assigned by server (Players, NPCs).
