using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// Whether a set of parts encloses a room, and what its six faces are made of, measured from the parts.
/// A room is big enough to be inside (<see cref="MinimumRoomDimension"/>), mostly empty
/// (<see cref="MaxSolidFraction"/>) and mostly covered (<see cref="MinimumCoveredFaces"/>; four is a
/// roofless yard, which sounds closer to a room than a field). Each face takes the material of the part
/// that covers most of it; a face nothing covers is open.
/// </summary>
public static class CompositeAcoustics
{
    /// <summary>The smallest a room can be in any direction, metres: a fence is never a room.</summary>
    public const float MinimumRoomDimension = 1.0f;

    /// <summary>How much of its own bounding box a composite may be made of and still have an inside.</summary>
    public const float MaxSolidFraction = 0.6f;

    /// <summary>How much of a face has to be covered for that face to count as a wall.</summary>
    public const float FaceCoverage = 0.35f;

    /// <summary>How many of the six faces have to be walls. Four is a roofless yard.</summary>
    public const int MinimumCoveredFaces = 4;

    /// <summary>What the six faces are called, in the order the whole codebase uses them.</summary>
    public static readonly string[] FaceNames = { "floor", "ceiling", "north wall", "south wall", "east wall", "west wall" };

    /// <summary>
    /// What the parts were found to enclose, and when not a room, which rule said no and by how much:
    /// "its ceiling is open" is what a builder who cannot see the missing roof needs to be told.
    /// </summary>
    public readonly struct RoomSurvey
    {
        public required Vector3 Size { get; init; }
        public required float SolidFraction { get; init; }
        /// <summary>0..1 per face, in <see cref="FaceNames"/> order.</summary>
        public required float[] Coverage { get; init; }
        /// <summary>What each face is mostly made of, in <see cref="FaceNames"/> order.</summary>
        public required string[] Materials { get; init; }
        public required int PartCount { get; init; }

        public float SmallestDimension => MathF.Min(Size.X, MathF.Min(Size.Y, Size.Z));
        public bool BigEnough => SmallestDimension >= MinimumRoomDimension;
        public bool Hollow => SolidFraction <= MaxSolidFraction;
        public int Walls { get { int n = 0; foreach (float c in Coverage) if (c >= FaceCoverage) n++; return n; } }
        public bool Covered => Walls >= MinimumCoveredFaces;
        public bool IsRoom => PartCount > 0 && BigEnough && Hollow && Covered;

        /// <summary>One or two sentences a person can act on.</summary>
        public string Explain()
        {
            if (PartCount == 0) return "There is nothing here to enclose anything.";

            string shape = $"{Size.X:F1} by {Size.Z:F1} metres and {Size.Y:F1} high";
            if (IsRoom)
                return $"It encloses a room {shape}, with {Walls} of its six faces walled "
                     + $"({string.Join(", ", Open())} open). The floor is {Materials[0]}.";

            if (!BigEnough)
                return $"It is {shape} — only {SmallestDimension:F1} metres through at its narrowest, "
                     + $"so there is no inside to it. A room needs {MinimumRoomDimension:F0} metres in every direction.";
            if (!Hollow)
                return $"It is {shape} but {SolidFraction * 100f:F0} per cent solid, so there is nowhere in it "
                     + "to stand. Spread the parts out or hollow it.";
            return $"It is {shape}, and only {Walls} of its six faces are walled — {MinimumCoveredFaces} are needed. "
                 + $"Open: {string.Join(", ", Open())}.";
        }

        /// <summary>The faces that are not walls, named.</summary>
        public IEnumerable<string> Open()
        {
            for (int i = 0; i < Coverage.Length; i++)
                if (Coverage[i] < FaceCoverage) yield return FaceNames[i];
        }
    }

    /// <summary>The face order the whole codebase uses: floor, ceiling, north, south, east, west.</summary>
    private static readonly (int Axis, float Side)[] Faces =
    {
        (1, -1f),   // Floor
        (1, +1f),   // Ceiling
        (2, +1f),   // North
        (2, -1f),   // South
        (0, +1f),   // East
        (0, -1f),   // West
    };

    /// <summary>
    /// The room a set of parts makes, if they make one. <paramref name="centre"/> is in the composite's
    /// own frame: its origin is at the ground, and a room placed there has its ceiling at your knees.
    /// </summary>
    public static bool Derive(World world, List<Entity> parts, string name,
                              out RegionComponent room, out Vector3 centre)
    {
        room = default;
        var survey = Survey(world, parts, out centre);
        if (!survey.IsRoom) return false;

        var materials = FaceMaterials(survey);

        room = new RegionComponent
        {
            FriendlyName = string.IsNullOrWhiteSpace(name) ? "Inside" : name,
            IsIndoor = true,
            RoomSize = survey.Size,
            ReverbTimeScale = 1.0f,
            Materials = materials,
            AmbienceId = "",
        };
        return true;
    }

    /// <summary>Measures a set of parts against all three rules, never stopping at the first failure, so a
    /// builder hears everything wrong at once.</summary>
    public static RoomSurvey Survey(World world, List<Entity> parts, out Vector3 centre)
        => Survey(Pieces(world, parts, loose: false), out centre);

    /// <summary>The same survey of entities not yet grouped, in the world's frame: /room's dry run.</summary>
    public static RoomSurvey SurveyLoose(World world, List<Entity> entities)
        => Survey(Pieces(world, entities, loose: true), out _);

    /// <summary>
    /// The same survey of a box that is already known, as a map's region is: its walls are shared with
    /// its neighbours, so a box derived from them would be the whole building. Coverage is clamped, so a
    /// wall longer than the face is fine.
    /// </summary>
    public static RoomSurvey SurveyBox(World world, List<Entity> parts, Vector3 centre, Vector3 size)
        => SurveyBox(Pieces(world, parts, loose: true), centre, size);

    /// <summary>
    /// The same survey of a box that is turned: every part is taken into the box's own frame first, so
    /// its faces are its own sides. Face names (north, east...) are then the box's own +Z and +X.
    /// </summary>
    public static RoomSurvey SurveyBox(World world, List<Entity> parts, Vector3 centre, Vector3 size, Quaternion rotation)
    {
        var pieces = Pieces(world, parts, loose: true);
        var inverse = Quaternion.Inverse(Quaternion.Normalize(rotation));
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            pieces[i] = p with
            {
                Position = centre + Vector3.Transform(p.Position - centre, inverse),
                Rotation = Quaternion.Normalize(inverse * p.Rotation),
            };
        }
        return SurveyBox(pieces, centre, size);
    }

    /// <summary>
    /// A composite's room when the box is known (a vehicle's cabin) and only the materials are measured.
    /// Derived from the parts, a hatchback's room was the whole 4.1 m car, not its 2.4 m cabin.
    /// </summary>
    public static bool DeriveInBox(World world, List<Entity> parts, string name, Vector3 centre, Vector3 size,
                                   out RegionComponent room)
    {
        room = default;
        var survey = SurveyBox(Pieces(world, parts, loose: false), centre, size);
        if (!survey.IsRoom) return false;
        var materials = FaceMaterials(survey);
        room = new RegionComponent
        {
            FriendlyName = string.IsNullOrWhiteSpace(name) ? "Inside" : name,
            IsIndoor = true,
            RoomSize = survey.Size,
            ReverbTimeScale = 1.0f,
            Materials = materials,
            AmbienceId = "",
        };
        return true;
    }

    /// <summary>
    /// What each face of a surveyed room is made of: the material of the parts that cover it, or open
    /// (<see cref="RoomAcoustics.OpenFaceMaterial"/>) where they do not. Never open for the floor: the
    /// ground is a surface. An uncovered face given a material made a shed with one open side a sealed box.
    /// </summary>
    public static int[] FaceMaterials(in RoomSurvey survey)
    {
        var materials = new int[6];
        for (int f = 0; f < 6; f++)
        {
            if (f != 0 && survey.Coverage[f] < FaceCoverage) { materials[f] = RoomAcoustics.OpenFaceMaterial; continue; }
            materials[f] = AcousticRegistry.TryGetResonanceIndex(survey.Materials[f], out int index)
                ? index
                : AcousticRegistry.TryGetResonanceIndex("Generic", out int fallback) ? fallback : 0;
        }
        return materials;
    }

    private static RoomSurvey SurveyBox(List<Piece> pieces, Vector3 centre, Vector3 size)
    {
        float boxVolume = size.X * size.Y * size.Z;

        // Volume clipped to the box, so a wall that runs past the room is not counted as filling it.
        float solid = 0f;
        Vector3 lo = centre - size * 0.5f, hi = centre + size * 0.5f;
        foreach (var piece in pieces)
        {
            var half = AxisAlignedHalfExtents(piece.Size * 0.5f, piece.Rotation);
            var pLo = Vector3.Max(lo, piece.Position - half);
            var pHi = Vector3.Min(hi, piece.Position + half);
            var overlap = Vector3.Max(pHi - pLo, Vector3.Zero);
            solid += overlap.X * overlap.Y * overlap.Z;
        }

        var coverage = new float[6];
        var materials = new string[6];
        for (int f = 0; f < Faces.Length; f++)
            coverage[f] = Face(pieces, size, centre, Faces[f].Axis, Faces[f].Side, out materials[f]);

        return new RoomSurvey
        {
            Size = size,
            SolidFraction = boxVolume > 0f ? solid / boxVolume : float.MaxValue,
            Coverage = coverage,
            Materials = materials,
            PartCount = pieces.Count,
        };
    }

    private readonly record struct Piece(Vector3 Position, Quaternion Rotation, Vector3 Size, string Material);

    private static List<Piece> Pieces(World world, List<Entity> parts, bool loose)
    {
        var pieces = new List<Piece>(parts.Count);
        foreach (var part in parts)
        {
            if (!world.Has<ColliderComponent>(part)) continue;
            Vector3 position;
            Quaternion rotation;
            if (loose)
            {
                if (!world.Has<Transform>(part)) continue;
                var t = world.Get<Transform>(part);
                position = t.Position;
                rotation = t.Rotation;
            }
            else
            {
                if (!world.Has<ParentComponent>(part)) continue;
                var parent = world.Get<ParentComponent>(part);
                position = parent.LocalPosition;
                rotation = parent.LocalRotation;
            }
            pieces.Add(new Piece(position, rotation, world.Get<ColliderComponent>(part).Size,
                                 world.Has<MaterialComponent>(part) ? world.Get<MaterialComponent>(part).Material ?? "Generic" : "Generic"));
        }
        return pieces;
    }

    private static RoomSurvey Survey(List<Piece> pieces, out Vector3 centre)
    {
        var size = Bounds(pieces, out centre);

        float boxVolume = size.X * size.Y * size.Z;
        float solid = 0f;
        foreach (var piece in pieces) solid += MathF.Abs(piece.Size.X * piece.Size.Y * piece.Size.Z);

        var coverage = new float[6];
        var materials = new string[6];
        for (int f = 0; f < Faces.Length; f++)
            coverage[f] = Face(pieces, size, centre, Faces[f].Axis, Faces[f].Side, out materials[f]);

        return new RoomSurvey
        {
            Size = size,
            SolidFraction = boxVolume > 0f ? solid / boxVolume : float.MaxValue,
            Coverage = coverage,
            Materials = materials,
            PartCount = pieces.Count,
        };
    }

    /// <summary>
    /// How much of one face is covered, and by what. A part counts if it is near the face's plane
    /// (within a quarter of the box's depth or 0.5 m) and is credited only its overlap with the face's
    /// rectangle: a slab six metres to one side is not your ceiling (docs/THE_CITY_BLOCK.md).
    /// </summary>
    private static float Face(List<Piece> pieces, Vector3 size, Vector3 centre,
                              int axis, float side, out string material)
    {
        material = "Generic";
        int a = axis, b = (axis + 1) % 3, c = (axis + 2) % 3;
        float faceArea = Component(size, b) * Component(size, c);
        if (faceArea <= 0f) return 0f;

        float plane = Component(centre, a) + side * Component(size, a) * 0.5f;
        float depth = MathF.Max(0.5f, Component(size, a) * 0.25f);

        float bLo = Component(centre, b) - Component(size, b) * 0.5f;
        float bHi = Component(centre, b) + Component(size, b) * 0.5f;
        float cLo = Component(centre, c) - Component(size, c) * 0.5f;
        float cHi = Component(centre, c) + Component(size, c) * 0.5f;

        float covered = 0f, best = 0f, bestGap = float.MaxValue, bestReach = float.MinValue;
        foreach (var piece in pieces)
        {
            var half = AxisAlignedHalfExtents(piece.Size * 0.5f, piece.Rotation);

            // Measured to the part's nearest edge, zero if the plane runs through it: to its outer edge,
            // the tunnel's 3.5 m walls were not walls and it read as open sky.
            float lo = Component(piece.Position, a) - Component(half, a);
            float hi = Component(piece.Position, a) + Component(half, a);
            float gap = plane < lo ? lo - plane : plane > hi ? plane - hi : 0f;
            if (gap > depth) continue;

            float area = Overlap(Component(piece.Position, b), Component(half, b), bLo, bHi)
                       * Overlap(Component(piece.Position, c), Component(half, c), cLo, cHi);
            if (area <= 0f) continue;

            covered += area;

            // Which part is the face: the biggest area; on a tie the nearer (else the flat above's carpet
            // made every ceiling carpet); on a tie of both the one reaching further into the room (else a
            // carpeted floor measured as the slab under it).
            float reach = -side * (Component(piece.Position, a) - side * Component(half, a));
            bool wins = area > best * 1.05f
                     || (area > best * 0.95f && (gap < bestGap - 0.05f
                                              || (gap < bestGap + 0.05f && reach > bestReach)));
            if (wins) { best = MathF.Max(best, area); bestGap = gap; bestReach = reach; material = piece.Material; }
        }
        return MathF.Min(1f, covered / faceArea);
    }

    /// <summary>How much of [lo, hi] a box of this centre and half-extent covers.</summary>
    private static float Overlap(float centre, float half, float lo, float hi)
        => MathF.Max(0f, MathF.Min(centre + half, hi) - MathF.Max(centre - half, lo));

    /// <summary>The bounding box of a set of parts in their composite's own frame, and its centre; a turned
    /// part counts by the box that contains it.</summary>
    public static Vector3 Bounds(World world, List<Entity> parts, out Vector3 centre)
        => Bounds(Pieces(world, parts, loose: false), out centre);

    private static Vector3 Bounds(List<Piece> pieces, out Vector3 centre)
    {
        centre = Vector3.Zero;
        if (pieces.Count == 0) return Vector3.Zero;

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var piece in pieces)
        {
            var half = AxisAlignedHalfExtents(piece.Size * 0.5f, piece.Rotation);
            min = Vector3.Min(min, piece.Position - half);
            max = Vector3.Max(max, piece.Position + half);
        }
        if (min.X > max.X) return Vector3.Zero;

        centre = (min + max) * 0.5f;
        return Vector3.Max(max - min, new Vector3(0.01f));
    }

    /// <summary>Half-extents of the axis-aligned box that contains a rotated one.</summary>
    public static Vector3 AxisAlignedHalfExtents(Vector3 half, Quaternion rotation)
    {
        var x = Vector3.Transform(new Vector3(half.X, 0f, 0f), rotation);
        var y = Vector3.Transform(new Vector3(0f, half.Y, 0f), rotation);
        var z = Vector3.Transform(new Vector3(0f, 0f, half.Z), rotation);
        return new Vector3(
            MathF.Abs(x.X) + MathF.Abs(y.X) + MathF.Abs(z.X),
            MathF.Abs(x.Y) + MathF.Abs(y.Y) + MathF.Abs(z.Y),
            MathF.Abs(x.Z) + MathF.Abs(y.Z) + MathF.Abs(z.Z));
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
