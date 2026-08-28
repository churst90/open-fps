using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// One player's memory of where the floor was.
///
/// The server probes the ground once per INPUT, not once per tick: a client that sends four sub-tick inputs
/// in a tick pays for four vertical probes, and each probe walks every grid cell within 50 m and tests five
/// points against every collider it finds. Standing still costs exactly as much as sprinting. Yet between
/// two sub-tick inputs the player has moved centimetres and the geometry has not moved at all, so the four
/// answers are the same answer.
///
/// This remembers the last one and hands it back while three things hold: the static geometry has not
/// changed (<see cref="SpatialGrid{T}.StaticVersion"/>), the probe position has not moved beyond
/// <see cref="ToleranceMetres"/>, and the answer is younger than <see cref="MaxAgeMs"/>.
///
/// The age bound is the honest part. Dynamic colliders — an NPC, a spawned crate — are not in the version
/// count, so an entry that never expired could describe a floor that walked away. 150 ms is under a fifth of
/// the interpolation delay a client already tolerates, and it still removes the great majority of the
/// probes: a player standing still drops from thirty-plus probes a second to under seven.
/// </summary>
public struct GroundProbeMemo
{
    /// <summary>How far the probe point may move before the remembered answer stops applying.</summary>
    public const float ToleranceMetres = 0.05f;

    /// <summary>How stale a remembered answer may get. Bounds how long a moved dynamic collider can lie.</summary>
    public const long MaxAgeMs = 150;

    private const float ToleranceSquared = ToleranceMetres * ToleranceMetres;

    private bool _valid;
    private Vector3 _position;
    private int _staticVersion;
    private long _stampMs;
    private float _height;
    private string? _material;

    /// <summary>Probes answered from memory since this memo was created. Diagnostic only.</summary>
    public long Hits { get; private set; }

    /// <summary>Probes that had to be computed. Diagnostic only.</summary>
    public long Misses { get; private set; }

    public bool TryGet(Vector3 position, int staticVersion, long nowMs, out float height, out string material)
    {
        height = 0f;
        material = "Generic";

        if (!_valid || staticVersion != _staticVersion) { Misses++; return false; }
        if (nowMs - _stampMs > MaxAgeMs) { Misses++; return false; }
        if (Vector3.DistanceSquared(position, _position) > ToleranceSquared) { Misses++; return false; }

        height = _height;
        material = _material ?? "Generic";
        Hits++;
        return true;
    }

    public void Store(Vector3 position, int staticVersion, long nowMs, float height, string material)
    {
        _valid = true;
        _position = position;
        _staticVersion = staticVersion;
        _stampMs = nowMs;
        _height = height;
        _material = string.IsNullOrEmpty(material) ? "Generic" : material;
    }

    /// <summary>Forgets the remembered floor — after a respawn or a teleport, where it describes nowhere.</summary>
    public void Invalidate() => _valid = false;
}
