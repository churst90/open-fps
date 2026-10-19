using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Geometry;
using OpenFPS.Physics;

namespace OpenFPS.Common;

/// <summary>
/// One player's memory of the triangle world's floor under them. The server probes the ground once per
/// input, and a body standing still asks the same point again and again. Handed back only for the same point
/// while the static geometry is unchanged (<see cref="SpatialGrid{T}.StaticVersion"/>, which door leaves move
/// too): the answer a fresh probe would give, to the bit. What moves is never remembered; the probe asks it
/// every time (<see cref="PhysicsUtils"/>).
///
/// Until physics stage 3 it handed back a whole answer for a point up to 5 cm away and 150 ms old, by the
/// wall clock: a client, which probes every time, could read another floor (docs/PHYSICS_BOUNDARY.md 3.7,
/// item 2).
/// </summary>
public struct GroundProbeMemo
{
    // The probe runs on the server tick thread (and the tests' threads); per-thread scratch keeps it
    // allocation-free without anyone waiting on anyone.
    [ThreadStatic] private static List<PhysicsBody>? _bodies;

    private static List<PhysicsBody> Bodies => _bodies ??= new List<PhysicsBody>(64);

    private bool _valid;
    private Vector3 _position;
    private int _staticVersion;
    private float _height;
    private string? _material;

    /// <summary>Probes answered from memory since this memo was created. Diagnostic only.</summary>
    public long Hits { get; private set; }

    /// <summary>Probes that had to be computed. Diagnostic only.</summary>
    public long Misses { get; private set; }

    public bool TryGet(Vector3 position, int staticVersion, out float height, out string material)
    {
        height = 0f;
        material = "Generic";

        if (!_valid || staticVersion != _staticVersion || position != _position) { Misses++; return false; }

        height = _height;
        material = _material ?? "Generic";
        Hits++;
        return true;
    }

    public void Store(Vector3 position, int staticVersion, float height, string material)
    {
        _valid = true;
        _position = position;
        _staticVersion = staticVersion;
        _height = height;
        _material = string.IsNullOrEmpty(material) ? "Generic" : material;
    }

    /// <summary>Forgets the remembered floor — after a respawn or a teleport, where it describes nowhere.</summary>
    public void Invalidate() => _valid = false;

    /// <summary>
    /// A body's floor on the server, the triangle world's part remembered (a session's memo): asked
    /// again only when the point or the static geometry changed. What moves and the statics the triangles do
    /// not hold are asked every time, so the answer is the one a fresh probe and a client's give.
    /// </summary>
    public static float GetGroundHeight(World world, SpatialGrid<Entity> grid, Vector3 pos, int own,
        ref GroundProbeMemo memo, out string material)
    {
        // The view first: handing out the triangle world places its movers, which may move the version.
        var view = ServerPhysicsView.Of(world, grid);
        if (memo.TryGet(pos, grid.StaticVersion, out float floor, out string under))
            PerfProbe.Count("server.groundprobe.hit");
        else
            using (PerfProbe.Measure("server.groundprobe"))
            {
                floor = Footing.TriangleGround(view, pos, own, null, out under);
                memo.Store(pos, grid.StaticVersion, floor, under);
            }
        return Footing.GroundOn(view, pos, own, null, floor, under, Bodies, out material);
    }
}
