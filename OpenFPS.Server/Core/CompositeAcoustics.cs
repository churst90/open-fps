using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// Working out whether a composite is a ROOM, and what kind.
///
/// Nobody should have to author the inside of a building they just built. If you put four walls, a
/// floor and a roof around yourself, you are indoors — that is a fact about the geometry, not a
/// property somebody remembered to tick. So the question "is this a room" is asked of the parts
/// themselves, and answered the same way for a shed, a cathedral and the cab of a lorry.
///
/// Three things have to be true, and each of them rules out a thing that is not a room:
///
///   IT HAS TO BE BIG ENOUGH TO BE INSIDE. A fence is a wall with more wall next to it; whatever its
///   footprint, one of its dimensions is a wall's thickness, and you cannot be inside that.
///
///   IT HAS TO BE MOSTLY EMPTY. A stack of crates the size of a garage is not a garage. If the parts
///   fill their own bounding box there is nowhere in it to stand.
///
///   MOST OF IT HAS TO BE COVERED. A room is a room because of what is between you and the sky. Four
///   faces out of six is a roofless courtyard, which is generous and deliberately so — the acoustics
///   of standing in a walled yard genuinely are closer to a room than to a field.
///
/// What the room is MADE of comes from the parts too: each of the six faces takes the material of
/// whichever part covers most of it. A glass-sided office is bright, a carpeted one is not, and a
/// car with metal panels rings — because of what they are built from, not because anyone said so.
/// </summary>
public static class CompositeAcoustics
{
    /// <summary>The smallest a room can be in any direction, metres. Under this you are not inside
    /// it, you are next to it.</summary>
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
    /// What was found when the parts were asked whether they enclose anything — and, when they do
    /// not, WHICH of the three rules said no and by how much.
    ///
    /// The diagnosis is not a debugging aid, it is the feature. A sighted builder can stand back and
    /// see that the roof is missing; a player who cannot has no way to tell a shed from four walls
    /// and a hole except by being told. "It is not a room" is a dead end. "Its ceiling is open" is
    /// an instruction.
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
    /// The room a set of parts makes, if they make one.
    ///
    /// <paramref name="centre"/> comes back in the composite's own frame, because a composite's origin
    /// is where it meets the GROUND and a room's centre is half its height above that. Putting the
    /// room volume at the origin would place its ceiling at your knees.
    /// </summary>
    public static bool Derive(World world, List<Entity> parts, string name,
                              out RegionComponent room, out Vector3 centre)
    {
        room = default;
        var survey = Survey(world, parts, out centre);
        if (!survey.IsRoom) return false;

        var materials = new int[6];
        for (int f = 0; f < 6; f++)
            materials[f] = AcousticRegistry.TryGetResonanceIndex(survey.Materials[f], out int index)
                ? index
                : AcousticRegistry.TryGetResonanceIndex("Generic", out int fallback) ? fallback : 0;

        room = new RegionComponent
        {
            FriendlyName = string.IsNullOrWhiteSpace(name) ? "Inside" : name,
            IsIndoor = true,
            Environment = AcousticEnvironmentType.Atmospheric,
            RoomSize = survey.Size,
            ReverbTimeScale = 1.0f,
            Materials = materials,
            AmbienceId = "",
        };
        return true;
    }

    /// <summary>
    /// Measures a set of parts against all three rules and reports everything it found, whether or
    /// not they make a room.
    ///
    /// Always measures all three rather than stopping at the first failure, because a builder who is
    /// told only the first thing wrong fixes it and is told the next thing, and building a shed
    /// becomes twenty round trips. One survey, everything that is wrong with it.
    /// </summary>
    public static RoomSurvey Survey(World world, List<Entity> parts, out Vector3 centre)
        => Survey(Pieces(world, parts, loose: false), out centre);

    /// <summary>
    /// The same survey, asked of entities that are not in a composite yet.
    ///
    /// `/room` is a DRY RUN — it answers "would this be a room if I grouped it" before anyone commits
    /// to grouping it, which is the difference between finding out your roof is missing now and
    /// finding out after you have saved it as a template. Loose entities have no parent to be
    /// relative to, so they are measured in the world's frame, which is exactly the frame a fresh
    /// grouping would put them in anyway.
    /// </summary>
    public static RoomSurvey SurveyLoose(World world, List<Entity> entities)
        => Survey(Pieces(world, entities, loose: true), out _);

    /// <summary>One part, reduced to the four things this file cares about.</summary>
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
    /// How much of one face is covered, and by what.
    ///
    /// A part belongs to a face if its OUTER edge is near that face — near meaning within a quarter of
    /// the box's depth, or half a metre, whichever is more forgiving. That tolerance is what lets a
    /// wall that is a little inboard of the corner still count as that wall, and it is why a pillar in
    /// the middle of a room covers nothing.
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

        float covered = 0f, best = 0f;
        foreach (var piece in pieces)
        {
            var half = AxisAlignedHalfExtents(piece.Size * 0.5f, piece.Rotation);

            float outer = Component(piece.Position, a) + side * Component(half, a);
            if (MathF.Abs(plane - outer) > depth) continue;

            float area = 4f * Component(half, b) * Component(half, c);
            covered += area;
            if (area > best) { best = area; material = piece.Material; }
        }
        return MathF.Min(1f, covered / faceArea);
    }

    /// <summary>
    /// The bounding box of a set of parts in their composite's own frame, and its centre.
    ///
    /// Rotated parts are measured by the box that CONTAINS them, not by their own dimensions: a wall
    /// turned ninety degrees is two metres of wall across, not half a metre, and measuring it the
    /// naive way makes every building that has corners come out the wrong shape.
    /// </summary>
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
