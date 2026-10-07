using System.Numerics;

namespace OpenFPS.Client.Core;

/// <summary>
/// Whether the zone you are in has held long enough to say: a crossing is walking over a line and
/// staying on the far side, not a flicker or a body thrown about. Cody, 2026-10-04, after a /tp over
/// Brandt Court: bouncing for three seconds between the roof and 7 to 13 m above it gave "Brandt Court
/// roof" and "sidewalk" eighteen times; held for <see cref="SettleSeconds"/> with no jump over
/// <see cref="JumpMetres"/>, the roof was said once.
/// </summary>
public sealed class ZoneSettle
{
    /// <summary>How long a zone must hold before it is said, seconds: about two strides at a walk,
    /// longer than any flicker at a boundary or bounce off a landing.</summary>
    public const double SettleSeconds = 0.4;

    /// <summary>How far the body may move between two updates and still be moving under its own feet,
    /// metres: past this it was carried, corrected or thrown, and where it is now is not settled.
    /// A sprint at thirty updates a second is a quarter of a metre.</summary>
    public const float JumpMetres = 1.0f;

    private int _held = int.MinValue;
    private double _since;
    private Vector3? _last;

    /// <summary>When the zone now held began to be held, on the clock <see cref="Update"/> is given:
    /// when you crossed into it, or last jumped while in it.</summary>
    public double HeldSince => _since;

    /// <summary>
    /// One update: the zone the listener is in, where the body is, and the time in seconds. True when
    /// that zone has held, with the body moving no faster than a body moves, for <see cref="SettleSeconds"/>.
    /// </summary>
    public bool Update(int zone, Vector3 position, double now)
    {
        bool jumped = _last is { } last && Vector3.Distance(last, position) > JumpMetres;
        _last = position;
        if (zone != _held || jumped)
        {
            _held = zone;
            _since = now;
        }
        return now - _since >= SettleSeconds;
    }

    /// <summary>Forgets the zone held, for a new map.</summary>
    public void Reset()
    {
        _held = int.MinValue;
        _last = null;
    }
}
