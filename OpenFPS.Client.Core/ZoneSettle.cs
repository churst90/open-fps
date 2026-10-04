using System.Numerics;

namespace OpenFPS.Client.Core;

/// <summary>
/// Whether the zone you are in has been where you are long enough to say. A zone is announced as you
/// cross into it, and a crossing is something a body does: it walks over a line and stays on the far
/// side. A zone that changes back within a fraction of a second, or changes while the body is being
/// thrown about faster than anybody walks, is not a crossing, and saying it makes the names flip.
///
/// Cody, 2026-10-04, after a /tp to 100 m over Brandt Court: landing on the roof, his position
/// bounced for three seconds between the roof and seven to thirteen metres over it, and the zone with
/// it — "Brandt Court roof" and, in the air above the roof's box where only the concrete under him
/// named the place, "sidewalk" — eighteen lines in three seconds. Held for <see cref="SettleSeconds"/>
/// with no jump of more than <see cref="JumpMetres"/> between updates, the roof was said once, when
/// he stopped bouncing.
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
