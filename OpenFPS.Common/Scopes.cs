using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A rifle scope: the magnifications it turns through, how wide it sees at each, and its elevation
/// turret.
/// </summary>
public sealed record ScopeDefinition
{
    public required string Id { get; init; }
    /// <summary>What it is called aloud: "4-12 power scope".</summary>
    public required string Name { get; init; }
    /// <summary>The powers the zoom ring stops at, lowest first.</summary>
    public required float[] Magnifications { get; init; }

    /// <summary>
    /// The field of view at one power, degrees: divided by the magnification it gives the scope's view
    /// at each power. 24 makes a 4-12x what a real one is, about 6 degrees at 4x (26 feet at a hundred
    /// yards, the usual published figure), 3 at 8x and 2 at 12x.
    /// </summary>
    public float FieldOfViewAtOnePowerDegrees { get; init; } = 24f;

    /// <summary>One click of the elevation turret, milliradians: a tenth, the usual for a mil scope.</summary>
    public float ClickMil { get; init; } = 0.1f;
    /// <summary>The most the turret dials up, milliradians: 25, a common figure, past 1000 m for a .308.</summary>
    public float MaxElevationMil { get; init; } = 25f;
    /// <summary>The sight's centre above the bore, metres: 38 mm, the inch and a half tables assume.</summary>
    public float SightHeightMetres { get; init; } = 0.038f;
    /// <summary>The distance the rifle is zeroed at with the turret at nothing.</summary>
    public float BaseZeroMetres { get; init; } = 100f;

    /// <summary>The full field of view at a magnification, degrees.</summary>
    public float FieldOfViewDegrees(float magnification) => FieldOfViewAtOnePowerDegrees / MathF.Max(1f, magnification);
}

/// <summary>The scopes that exist.</summary>
public static class ScopeRegistry
{
    public static readonly ScopeDefinition Scope4To12 = new()
    {
        Id = "scope_4_12",
        Name = "4-12 power scope",
        Magnifications = new[] { 4f, 8f, 12f },
    };

    private static readonly Dictionary<string, ScopeDefinition> _byId = new(StringComparer.OrdinalIgnoreCase)
    {
        [Scope4To12.Id] = Scope4To12,
    };

    public static bool TryGet(string? id, out ScopeDefinition scope) => _byId.TryGetValue(id ?? "", out scope!);
}

/// <summary>
/// The arithmetic of looking through a scope, and the words for what is seen. Shared, so the client
/// that speaks it and the tests that hold it are reading one set of rules.
/// </summary>
public static class ScopeMath
{
    /// <summary>A tap of an aim key at one power, degrees. Divided by the magnification, so a tap
    /// moves the crosshair the same distance across what you see whatever the power: half a degree at
    /// 1x is an eighth at 4x and a twenty-fourth at 12x, about 44 cm at 600 m, one body's width.</summary>
    public const float TapDegreesAtOnePower = 0.5f;
    /// <summary>A held aim key at one power, degrees a second, likewise divided.</summary>
    public const float SweepDegreesPerSecondAtOnePower = 5f;

    /// <summary>How far a person can be made out by eye: about 150 m, where a figure stops being more
    /// than a shape. Through a scope that goes out with the magnification.</summary>
    public const float RecognitionMetresByEye = 150f;

    public static float TapDegrees(float magnification) => TapDegreesAtOnePower / MathF.Max(1f, magnification);
    public static float SweepDegreesPerSecond(float magnification) => SweepDegreesPerSecondAtOnePower / MathF.Max(1f, magnification);

    /// <summary>How far things can be made out at a magnification, metres, never beyond <paramref name="cap"/>.</summary>
    public static float RecognitionMetres(float magnification, float cap = float.MaxValue)
        => MathF.Min(cap, RecognitionMetresByEye * MathF.Max(1f, magnification));

    /// <summary>The direction an eye looks with this yaw and pitch: the game's own rotation, so
    /// increasing pitch looks DOWN (CreateFromYawPitchRoll turns +Z toward -Y).</summary>
    public static Vector3 Forward(float yaw, float pitch)
        => Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f));

    /// <summary>
    /// Where a point is against the crosshair, radians: right of it (positive) and above it (positive).
    /// Measured in the eye's own frame, so "a little left" means left in the picture, whatever the
    /// pitch.
    /// </summary>
    public static (float Right, float Up, float Ahead) Offset(Vector3 eye, float yaw, float pitch, Vector3 point)
    {
        var rot = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rot);
        Vector3 up = Vector3.Transform(Vector3.UnitY, rot);
        // Facing +Z, right is +X (increasing yaw swings forward toward +X, which is right).
        Vector3 right = Vector3.Cross(up, forward);
        Vector3 to = point - eye;
        float ahead = Vector3.Dot(to, forward);
        float r = MathF.Atan2(Vector3.Dot(to, right), MathF.Max(1e-4f, ahead));
        float u = MathF.Atan2(Vector3.Dot(to, up), MathF.Max(1e-4f, ahead));
        return (r, u, ahead);
    }

    /// <summary>Whether a point is inside the scope's round field of view.</summary>
    public static bool InView(float right, float up, float ahead, float fovDegrees)
        => ahead > 0f && MathF.Sqrt(right * right + up * up) <= 0.5f * fovDegrees * MathF.PI / 180f;

    // ── Words ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A distance as a person would say it: to the metre close in, to five metres out to two
    /// hundred, to ten beyond. A spoken "347 metres" is false precision for a glance.</summary>
    public static string DistanceWords(float metres)
    {
        if (metres < 1.5f) return "1 metre";
        int rounded = metres < 50f ? (int)MathF.Round(metres)
                    : metres < 200f ? 5 * (int)MathF.Round(metres / 5f)
                    : 10 * (int)MathF.Round(metres / 10f);
        return $"{rounded} metres";
    }

    /// <summary>The rangefinder's reading: to the metre, as a laser reads.</summary>
    public static string RangeWords(float metres) => $"{(int)MathF.Round(metres)} metres";

    /// <summary>"2 metres below", "on a roof 9 metres up", "level with you".</summary>
    public static string HeightWords(float metresAboveYou, bool onRoof)
    {
        int n = (int)MathF.Round(MathF.Abs(metresAboveYou));
        string roof = onRoof ? "on a roof " : "";
        if (n < 1) return onRoof ? "on a roof level with you" : "level with you";
        string metres = n == 1 ? "1 metre" : $"{n} metres";
        return metresAboveYou > 0f ? $"{roof}{metres} up" : $"{roof}{metres} below";
    }

    /// <summary>
    /// Where something is against the crosshair, in words: "on the crosshair", "a little left",
    /// "far right and below", "a little left and above". Each axis is graded against the half width of
    /// the view: inside a body's width of the centre it says nothing, then "a little" out to a third of
    /// the way to the edge, plain out to two thirds, and "far" beyond.
    /// </summary>
    public static string RelativeWords(float right, float up, float fovDegrees, float onTarget)
    {
        float half = 0.5f * fovDegrees * MathF.PI / 180f;
        static string Grade(float off, float half, float onTarget)
        {
            float a = MathF.Abs(off);
            if (a <= onTarget) return "";
            float f = a / MathF.Max(1e-6f, half);
            return f < 0.33f ? "a little " : f < 0.67f ? "" : "far ";
        }
        string gh = Grade(right, half, onTarget), gv = Grade(up, half, onTarget);
        bool h = MathF.Abs(right) > onTarget, v = MathF.Abs(up) > onTarget;
        string hw = right > 0f ? "right" : "left";
        string vw = up > 0f ? "above" : "below";
        if (!h && !v) return "on the crosshair";
        if (h && !v) return gh + hw;
        if (!h) return gv + vw;
        return gh == gv ? $"{gh}{hw} and {vw}" : $"{gh}{hw} and {gv}{vw}";
    }

    /// <summary>
    /// How something is moving, seen from where you are looking: "walking right", "running towards
    /// you", "standing still", "driving away at 40 km/h". Across the view or along it, whichever is
    /// more of the movement.
    /// </summary>
    public static string MovementWords(Vector3 velocity, Vector3 lookForward, bool isVehicle)
    {
        Vector3 flat = new(velocity.X, 0f, velocity.Z);
        float speed = flat.Length();
        if (speed < (isVehicle ? 0.5f : 0.3f)) return isVehicle ? "stopped" : "standing still";
        Vector3 f = Vector3.Normalize(new Vector3(lookForward.X, 0f, lookForward.Z) + new Vector3(0f, 0f, 1e-6f));
        Vector3 right = new(f.Z, 0f, -f.X);
        float across = Vector3.Dot(flat, right), along = Vector3.Dot(flat, f);
        string way = MathF.Abs(across) >= MathF.Abs(along)
            ? (across > 0f ? "right" : "left")
            : (along > 0f ? "away" : "towards you");
        if (isVehicle) return $"driving {way} at {(int)MathF.Round(speed * 3.6f / 5f) * 5} km/h";
        return $"{(speed > 2.6f ? "running" : "walking")} {way}";
    }

    /// <summary>"zeroed for 400 metres", or past what the round can be zeroed for.</summary>
    public static string ZeroWords(float? metres)
        => metres is { } m ? $"zeroed for {5 * (int)MathF.Round(m / 5f)} metres" : "beyond where it can be zeroed";
}

/// <summary>
/// The crosshair's wander: a body breathing, slower and larger the harder it has been working, and
/// stilled for a few seconds when the breath is held.
///
/// The breath moves the rifle in a slow ellipse, more up and down than across, once each breath. A
/// held breath stills it inside about half a second and keeps it still for <see cref="HoldSeconds"/>;
/// after that the body wants air, and the hold turns into a shake that grows until the breath is let
/// go. Letting it go costs a recovery, longer the longer it was held past the limit. Moving or having
/// just run makes everything bigger. There is no crouching or lying down in the game yet; when there
/// is, it belongs in <see cref="Amplitude"/>.
///
/// Applied as an offset to the aim at the moment of the shot, and to the guidance tone, so the tone
/// steadies when the breath is held and a shot taken mid-breath goes where the crosshair was.
/// </summary>
public sealed class ScopeSway
{
    /// <summary>How long a breath can be held still, seconds.</summary>
    public const float HoldSeconds = 5f;
    /// <summary>A rested shooter's sway at the top of a breath, milliradians across and up.</summary>
    public const float RestingAcrossMil = 0.6f, RestingUpMil = 1.0f;

    private double _phase;
    private float _holdFor;        // seconds the current hold has lasted, 0 when breathing
    private float _recovery;       // seconds of laboured breathing still owed
    private float _stillness;      // 0 breathing freely .. 1 fully stilled
    private float _shake;          // grows past the hold limit
    private double _time;

    /// <summary>Whether the breath is being held now.</summary>
    public bool Holding => _holdFor > 0f;
    /// <summary>Seconds the current hold has lasted.</summary>
    public float HeldFor => _holdFor;

    /// <summary>The sway at the top of a breath, milliradians, for how hard the body is working.</summary>
    public static float Amplitude(float exertion, float speed)
        => 1f + 2.5f * Math.Clamp(exertion, 0f, 1f) + (speed > 0.5f ? 2f : 0f);

    /// <summary>
    /// Moves the sway on by <paramref name="dt"/> and returns the offset now, radians: right (positive)
    /// and up (positive). <paramref name="hold"/> is whether the hold-breath key is down.
    /// </summary>
    public (float Right, float Up) Update(float dt, float exertion, float speed, bool hold)
    {
        _time += dt;
        if (hold) _holdFor += dt;
        else if (_holdFor > 0f)
        {
            // Let go: the longer past the limit, the longer it takes to settle.
            _recovery = MathF.Max(_recovery, MathF.Max(1f, 2f * (_holdFor - HoldSeconds) + 1.5f));
            _holdFor = 0f;
        }
        _recovery = MathF.Max(0f, _recovery - dt);

        bool still = hold && _holdFor <= HoldSeconds;
        _stillness += ((still ? 1f : 0f) - _stillness) * Math.Clamp(dt / 0.15f, 0f, 1f);
        float wantShake = hold && _holdFor > HoldSeconds ? MathF.Min(3f, 0.6f * (_holdFor - HoldSeconds)) : 0f;
        _shake += (wantShake - _shake) * Math.Clamp(dt / 0.3f, 0f, 1f);

        // A breath every four seconds at rest, faster when winded.
        float rate = 0.25f + 0.5f * Math.Clamp(exertion + _recovery / 6f, 0f, 1f);
        _phase += dt * rate * 2.0 * Math.PI;
        float amp = Amplitude(exertion + _recovery / 6f, speed) * (1f - 0.92f * _stillness);
        float across = RestingAcrossMil * amp * (float)Math.Sin(_phase);
        float up = RestingUpMil * amp * (float)Math.Sin(_phase * 1.0 + Math.PI / 2.0);
        // The shake of a held breath gone on too long: a quick tremor, not the slow ellipse.
        if (_shake > 0f)
        {
            across += _shake * (float)Math.Sin(_time * 2 * Math.PI * 5.3);
            up += _shake * (float)Math.Sin(_time * 2 * Math.PI * 6.1 + 1.3);
        }
        return (across / 1000f, up / 1000f);
    }

    /// <summary>Back to breathing freely, as when the scope is lowered.</summary>
    public void Reset() { _holdFor = 0f; _stillness = 0f; _shake = 0f; }
}
