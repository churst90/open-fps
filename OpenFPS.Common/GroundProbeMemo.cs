using System.Numerics;
using OpenFPS.Geometry;

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
}
