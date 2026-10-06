using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// The wind over the map: how fast the air is moving at a place and a moment.
///
/// One field, read by everything the wind moves — the leaves on a tree, the flames of a fire, the
/// spray off a fountain, the air past your own ears — so a gust is ONE event that arrives at each of
/// them in turn rather than a random wobble each of them makes up for itself. That is the whole reason
/// it is a field and not a per-source noise generator: stand in a park and a gust is heard coming,
/// through the trees upwind first, then the ones round you, then away downwind.
///
/// THE MODEL. Near the ground the wind is a mean speed with turbulence on top of it. The mean grows
/// with height as the log of it (the surface layer: u(z) = u* / κ ln(z / z0)), so a fire on the
/// ground feels about half of what the crown of a tree does. The turbulence is a pattern of eddies
/// carried along by the mean flow (Taylor's frozen turbulence): what a place feels at time t is what
/// a place upwind of it felt a little earlier. Its size is the turbulence intensity, the standard
/// deviation over the mean, which near the ground is about 0.1 in open country on a quiet day and
/// 0.3 to 0.4 in a gusty gale over buildings and trees; its eddies run from about 180 m (the gusts
/// you notice) down to 5 m (the flutter inside one), with less energy in the short ones, as a
/// Kolmogorov spectrum has.
///
/// WHERE THE WEATHER COMES IN. The mean wind, its direction and its gustiness are the server's
/// weather (WorldEnvironmentSystem), broadcast once a second, and <see cref="WindWeather"/> is what a
/// client makes of them: the latest broadcast, reached from the one before by a one-second ramp so a
/// broadcast never lands as a step. The eddy pattern is displaced by how far the air has TRAVELLED
/// (the integral of the wind over time), which the server keeps and broadcasts too. That is what lets
/// the wind turn and strengthen without the gusts racing: an eddy pattern fixed to a direction would
/// swing round the map's origin as the wind veered, and a tree a kilometre out would hear a minute
/// of gusts in ten seconds.
///
/// DETERMINISTIC. The eddies are hashed from a lattice in (position on the travelled pattern, slow
/// time), so two clients asking about the same place and second, with the same broadcast, get the
/// same answer, and a test can hold it. The clock is the caller's: UTC seconds keep clients together.
/// </summary>
public static class WindField
{
    /// <summary>The weather every reader without its own sees. Swapped whole, so a reader on the mixer
    /// thread never sees half of one broadcast and half of the next.</summary>
    public static WindWeather Weather
    {
        get => _held.Value ?? System.Threading.Volatile.Read(ref _weather);
        set => System.Threading.Volatile.Write(ref _weather, value ?? WindWeather.Default);
    }
    private static WindWeather _weather = WindWeather.Default;

    /// <summary>
    /// Holds a weather for the calling flow only (a test, a lab render) until the returned handle is
    /// disposed. The shared weather is one global that a game session writes on every broadcast, so a
    /// test reading the wind while a session test runs beside it heard that session's weather change
    /// under it (WideSourcesTests.ATreesPlaceVoicesAddUpToItsOneVoice failed now and then in a mixed
    /// run, 2026-10-06). The game never holds one: on the mixer thread this is one AsyncLocal read.
    /// </summary>
    public static IDisposable Hold(WindWeather weather)
    {
        var before = _held.Value;
        _held.Value = weather;
        return new Release(before);
    }
    private static readonly System.Threading.AsyncLocal<WindWeather?> _held = new();
    private sealed class Release(WindWeather? before) : IDisposable
    {
        public void Dispose() => _held.Value = before;
    }

    /// <summary>The mean wind at ten metres, m/s, now. A moderate breeze — Beaufort 3, leaves and
    /// small twigs in constant motion — is 3.4 to 5.4. Setting it (the lab does) makes a steady wind
    /// with the present direction and turbulence.</summary>
    public static float MeanSpeed
    {
        get => Weather.At(Now()).Speed;
        set { var a = Weather.At(Now()); Weather = WindWeather.Steady(value, a.FromDegrees, a.Turbulence); }
    }

    /// <summary>Where it blows FROM, degrees clockwise from north (the way a forecast says it).</summary>
    public static float FromDegrees
    {
        get => Weather.At(Now()).FromDegrees;
        set { var a = Weather.At(Now()); Weather = WindWeather.Steady(a.Speed, value, a.Turbulence); }
    }

    /// <summary>Standard deviation of the speed over its mean, now. Over ground of roughness z0 the
    /// surface layer gives about 1 / ln(z / z0) (EN 1991-1-4 with its turbulence factor 1): with the
    /// <see cref="RoughnessMetres"/> here, 0.24 at ten metres and 0.26 at a park tree's crown. The
    /// built-in wind uses 0.25; it was 0.3, the top of that range, and since a crown's sound goes as
    /// the wind to the 3.6, the lulls took the trees nearly silent and the gusts were heard as someone
    /// turning them up and down. The server's gustiness moves it a little either side of that
    /// (<see cref="WindAir.TurbulenceFor"/>).</summary>
    public static float Turbulence
    {
        get => Weather.At(Now()).Turbulence;
        set { var a = Weather.At(Now()); Weather = WindWeather.Steady(a.Speed, a.FromDegrees, value); }
    }

    /// <summary>The roughness length of the ground, m: about 0.1 for open grass with scattered trees,
    /// 0.5 to 1 for a suburb. It sets how fast the wind falls off toward the ground.</summary>
    public const float RoughnessMetres = 0.15f;

    /// <summary>The slowest the eddy pattern is ever carried, m/s. A dead calm still moves air about,
    /// and a pattern carried at nothing would freeze every leaf at whatever it last did.</summary>
    public const float MinimumCarryMetresPerSecond = 0.5f;

    /// <summary>The wind speed the eddy sizes were set at, m/s: at this speed an eddy of
    /// <see cref="Eddies"/>' timescale passes a point in that many seconds.</summary>
    public const float ReferenceSpeed = 4.5f;

    /// <summary>How long an eddy lives once it is not being carried anywhere, s. Long against the
    /// time it takes to pass anything, so the pattern a place downwind feels is the one upwind of it
    /// (frozen turbulence); finite, so in a calm the leaves still move.</summary>
    public const float EddyLifetimeSeconds = 600f;

    /// <summary>The eddies, longest first: (timescale s at <see cref="ReferenceSpeed"/>, share of the
    /// variance). Roughly a −5/3 law: most of the variance in the slow gusts. Their sizes are these
    /// times the reference speed: 180, 72, 29, 12 and 5 m.</summary>
    private static readonly (float Seconds, float Weight)[] Eddies =
    {
        (40f, 1.0f), (16f, 0.75f), (6.5f, 0.5f), (2.6f, 0.3f), (1.1f, 0.16f),
    };

    private static readonly float Norm;

    static WindField()
    {
        // Value noise in three dimensions with cubic easing, lattice values uniform in [-1, 1], has a
        // variance of 1/3 times 0.743 cubed (0.743 is the easing's loss per dimension: twice the
        // mean square of 3t^2 - 2t^3); summing the eddies adds their variances.
        double v = 0;
        foreach (var (_, w) in Eddies) v += w * w * (1.0 / 3.0) * Math.Pow(0.7429, 3);
        Norm = (float)(1.0 / Math.Sqrt(v));
    }

    /// <summary>The unit vector the air moves TOWARD, in the map's x (east), z (north), now.</summary>
    public static (float X, float Z) Downwind => Weather.At(Now()).Downwind;

    /// <summary>The mean speed at a height, by the log law, m/s. Never below a tenth of the
    /// ten-metre speed, because the law runs to zero at the roughness height and air does not.</summary>
    public static float MeanAt(float heightMetres) => MeanAt(Weather.At(Now()).Speed, heightMetres);

    /// <summary>The log law for a given ten-metre speed.</summary>
    public static float MeanAt(float tenMetreSpeed, float heightMetres) => tenMetreSpeed * HeightShare(heightMetres);

    /// <summary>What share of the ten-metre speed blows at a height.</summary>
    public static float HeightShare(float heightMetres)
    {
        float z = MathF.Max(RoughnessMetres * 1.5f, heightMetres);
        float share = MathF.Log(z / RoughnessMetres) / MathF.Log(10f / RoughnessMetres);
        return MathF.Max(0.1f, share);
    }

    /// <summary>The gust signal at a place and moment under the current weather: zero mean, unit
    /// standard deviation. Positive is a gust, negative a lull.</summary>
    public static float Gust(float x, float z, double seconds) => Gust(Weather, x, z, seconds);

    /// <summary>The gust signal under a given weather. Pure: what a test asks.</summary>
    public static float Gust(WindWeather weather, float x, float z, double seconds)
    {
        var (te, tn) = weather.TravelAt(seconds);
        // Where on the travelled pattern this place sits. Doubles: the air has gone a long way.
        double px = x - te, pz = z - tn;
        double slow = seconds / EddyLifetimeSeconds;
        float sum = 0f;
        for (int k = 0; k < Eddies.Length; k++)
        {
            var (period, weight) = Eddies[k];
            double size = period * ReferenceSpeed;
            sum += weight * Noise3(px / size, pz / size, slow + k * 0.37, k);
        }
        return sum * Norm;
    }

    /// <summary>The wind speed at a place, m/s: the mean at that height times one plus the
    /// turbulence, never stopping entirely (a lull in a breeze is still air moving).</summary>
    public static float SpeedAt(float x, float heightMetres, float z, double seconds)
        => SpeedAt(Weather, x, heightMetres, z, seconds);

    public static float SpeedAt(WindWeather weather, float x, float heightMetres, float z, double seconds)
    {
        var air = weather.At(seconds);
        float mean = MeanAt(air.Speed, heightMetres);
        return mean * MathF.Max(0.12f, 1f + air.Turbulence * Gust(weather, x, z, seconds));
    }

    /// <summary>The air's velocity at a place, m/s, east and north: the speed of
    /// <see cref="SpeedAt(WindWeather, float, float, float, double)"/> along the mean direction.</summary>
    public static Vector2 VelocityAt(WindWeather weather, float x, float heightMetres, float z, double seconds)
    {
        var air = weather.At(seconds);
        var (dx, dz) = air.Downwind;
        float speed = SpeedAt(weather, x, heightMetres, z, seconds);
        return new Vector2(dx, dz) * speed;
    }

    /// <summary>How fast the eddy pattern is carried for a mean wind, east and north, m/s: the wind
    /// itself, held to <see cref="MinimumCarryMetresPerSecond"/>.</summary>
    public static (float East, float North) Carry(float east, float north)
    {
        float s = MathF.Sqrt(east * east + north * north);
        if (s >= MinimumCarryMetresPerSecond) return (east, north);
        if (s < 1e-4f) return (MinimumCarryMetresPerSecond, 0f);
        float k = MinimumCarryMetresPerSecond / s;
        return (east * k, north * k);
    }

    /// <summary>The clock every voice reads the field with: UTC seconds, folded to keep a float's
    /// precision useful. Clients agree on it to within their clocks.</summary>
    public static double Now() => (DateTime.UtcNow - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    // ── Value noise ────────────────────────────────────────────────────────────────────────────

    private static float Noise3(double u, double v, double w, int layer)
    {
        double fu = Math.Floor(u), fv = Math.Floor(v), fw = Math.Floor(w);
        int iu = (int)(long)fu, iv = (int)(long)fv, iw = (int)(long)fw;
        float tu = Ease((float)(u - fu)), tv = Ease((float)(v - fv)), tw = Ease((float)(w - fw));
        float Plane(int k)
        {
            float a = Lattice(iu, iv, k, layer), b = Lattice(iu + 1, iv, k, layer);
            float c = Lattice(iu, iv + 1, k, layer), d = Lattice(iu + 1, iv + 1, k, layer);
            float top = a + (b - a) * tu, bottom = c + (d - c) * tu;
            return top + (bottom - top) * tv;
        }
        float p0 = Plane(iw), p1 = Plane(iw + 1);
        return p0 + (p1 - p0) * tw;
    }

    private static float Ease(float t) => t * t * (3f - 2f * t);

    private static float Lattice(int i, int j, int k, int layer)
    {
        uint h = (uint)i * 0x9E3779B1u ^ (uint)j * 0x85EBCA77u ^ (uint)k * 0x27D4EB2Fu ^ (uint)layer * 0xC2B2AE3Du;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFFFF) / (float)0x7FFFFF - 1f;
    }
}

/// <summary>
/// One state of the air as the server sends it: the mean ten-metre wind (east and north, the way it
/// moves), its turbulence intensity, and how far the eddy pattern had been carried at a moment.
/// </summary>
public readonly record struct WindAir(float East, float North, float Turbulence,
                                      double AnchorSeconds, double TravelEast, double TravelNorth)
{
    public float Speed => MathF.Sqrt(East * East + North * North);

    /// <summary>Where it blows from, degrees clockwise from north. A calm reads as from the west.</summary>
    public float FromDegrees
    {
        get
        {
            if (Speed < 1e-4f) return 270f;
            float deg = MathF.Atan2(-East, -North) * 180f / MathF.PI;
            return deg < 0 ? deg + 360f : deg;
        }
    }

    /// <summary>The unit vector the air moves toward, east and north.</summary>
    public (float X, float Z) Downwind
    {
        get
        {
            float s = Speed;
            if (s < 1e-4f) return (1f, 0f);
            return (East / s, North / s);
        }
    }

    /// <summary>How far the pattern has been carried at a moment: the anchor, carried on at this
    /// wind.</summary>
    public (double East, double North) TravelAt(double seconds)
    {
        var (ce, cn) = WindField.Carry(East, North);
        double dt = seconds - AnchorSeconds;
        return (TravelEast + ce * dt, TravelNorth + cn * dt);
    }

    /// <summary>A wind of a speed, from a direction (degrees clockwise from north).</summary>
    public static WindAir FromCompass(float speed, float fromDegrees, float turbulence,
                                      double anchorSeconds = 0, double travelEast = 0, double travelNorth = 0)
    {
        float a = fromDegrees * MathF.PI / 180f;
        return new WindAir(-MathF.Sin(a) * speed, -MathF.Cos(a) * speed, turbulence, anchorSeconds, travelEast, travelNorth);
    }

    /// <summary>The turbulence intensity for the server's gustiness (0..1): 0.18 for a steady wind,
    /// 0.24 at the middle, 0.30 for the gustiest. Turbulence intensity near the ground is set mostly
    /// by the ground (1 / ln(z / z0), 0.24 at ten metres here, see <see cref="WindField.Turbulence"/>),
    /// not by the weather, so the weather only moves it a little either side; 0.3 is the top of the
    /// range, where the trees were heard as somebody turning them up and down. Over land a gust
    /// factor (the three-second peak over the mean) is about one plus three times this: 1.55 on a
    /// steady day to 1.9 in a squally storm.</summary>
    public static float TurbulenceFor(float gustiness) => 0.18f + 0.12f * Math.Clamp(gustiness, 0f, 1f);

    /// <summary>The air as a weather broadcast describes it. <paramref name="windVelocity"/> is the
    /// server's sustained wind in map axes (x east, z north).</summary>
    public static WindAir FromBroadcast(Vector3 windVelocity, float gustiness, double clock, double travelEast, double travelNorth)
        => new(Finite(windVelocity.X), Finite(windVelocity.Z), TurbulenceFor(Finite(gustiness)), clock, travelEast, travelNorth);

    private static float Finite(float v) => float.IsFinite(v) ? Math.Clamp(v, -80f, 80f) : 0f;

    public static WindAir Lerp(in WindAir a, in WindAir b, float f)
        => new(a.East + (b.East - a.East) * f, a.North + (b.North - a.North) * f,
               a.Turbulence + (b.Turbulence - a.Turbulence) * f, a.AnchorSeconds, a.TravelEast, a.TravelNorth);
}

/// <summary>
/// The air a client hears: the weather it last received, reached from the one before by a ramp.
///
/// The server broadcasts once a second. Applying each broadcast as it lands would step the mean
/// wind every second, and every tree, fire and ear on the map would step with it. So each broadcast
/// becomes the end of a one-second ramp that starts wherever the air was when it arrived; the eddy
/// pattern's travel is blended the same way, from where it had got to toward the server's account of
/// it, so the gusts neither jump nor race.
/// </summary>
public sealed class WindWeather
{
    public readonly WindAir From, To;
    public readonly double RampStart, RampSeconds;

    /// <summary>True for the built-in wind nobody sent: the first broadcast replaces it outright.</summary>
    public readonly bool IsDefault;

    private WindWeather(WindAir from, WindAir to, double rampStart, double rampSeconds, bool isDefault)
    {
        From = from; To = to; RampStart = rampStart; RampSeconds = rampSeconds; IsDefault = isDefault;
    }

    /// <summary>A moderate west-south-westerly, the prevailing wind over most of the temperate world;
    /// what the lab renders with and what a client has before the server says otherwise.</summary>
    public static readonly WindWeather Default = new(WindAir.FromCompass(4.5f, 250f, 0.25f),
                                                     WindAir.FromCompass(4.5f, 250f, 0.25f), 0, 0, true);

    /// <summary>A wind that does not change, its pattern carried from the origin at time zero.</summary>
    public static WindWeather Steady(float speed, float fromDegrees, float turbulence)
    {
        var air = WindAir.FromCompass(MathF.Max(0f, speed), fromDegrees, Math.Clamp(turbulence, 0f, 1f));
        return new WindWeather(air, air, 0, 0, false);
    }

    /// <summary>A steady weather from a single state of the air, anchored where it says.</summary>
    public static WindWeather Steady(WindAir air) => new(air, air, 0, 0, false);

    /// <summary>The ramp's progress at a moment, 0 to 1.</summary>
    public float Progress(double seconds)
        => RampSeconds <= 0 ? 1f : (float)Math.Clamp((seconds - RampStart) / RampSeconds, 0.0, 1.0);

    /// <summary>The mean air at a moment (its anchor is meaningless; use <see cref="TravelAt"/>).</summary>
    public WindAir At(double seconds) => WindAir.Lerp(From, To, Progress(seconds));

    /// <summary>How far the eddy pattern has been carried at a moment.</summary>
    public (double East, double North) TravelAt(double seconds)
    {
        var b = To.TravelAt(seconds);
        float f = Progress(seconds);
        if (f >= 1f) return b;
        var a = From.TravelAt(seconds);
        return (a.East + (b.East - a.East) * f, a.North + (b.North - a.North) * f);
    }

    /// <summary>How far apart two accounts of the pattern's travel can be and still be blended, m.</summary>
    public const double SnapMetres = 200.0;

    /// <summary>The ramp a broadcast starts.</summary>
    public const double RampSecondsPerBroadcast = 1.0;

    /// <summary>
    /// What a client's air becomes when a broadcast lands at <paramref name="now"/>: a ramp from where
    /// it is to <paramref name="incoming"/>. A broadcast with no travel in it (an older server, whose
    /// clock reads zero) carries the pattern on from where it had got to. The first broadcast replaces
    /// the built-in wind outright: there is nothing playing yet to step.
    /// </summary>
    public WindWeather Following(WindAir incoming, double now, double rampSeconds = RampSecondsPerBroadcast)
    {
        var travelled = TravelAt(now);
        if (incoming.AnchorSeconds <= 0)
            incoming = incoming with { AnchorSeconds = now, TravelEast = travelled.East, TravelNorth = travelled.North };
        if (IsDefault) return new WindWeather(incoming, incoming, now, 0, false);
        // A pattern hundreds of metres from where this one had got to is another server's (a
        // reconnect): blending across that would race the gusts for a second. Take it as it is.
        var theirs = incoming.TravelAt(now);
        double apart = Math.Sqrt((theirs.East - travelled.East) * (theirs.East - travelled.East)
                               + (theirs.North - travelled.North) * (theirs.North - travelled.North));
        if (apart > SnapMetres) return new WindWeather(incoming, incoming, now, 0, false);

        // Where the air is now, carried on as it is going, is where the ramp starts.
        var here = At(now) with { AnchorSeconds = now, TravelEast = travelled.East, TravelNorth = travelled.North };
        return new WindWeather(here, incoming, now, rampSeconds, false);
    }
}
