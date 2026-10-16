using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// What the air does to one sound's way to the ear now (Refraction's law, read with this world's weather): the wind
/// where the way's middle is at this moment, gusts and all, along the way, the air's layers over land or water, and the
/// air's stir. The mixer asks it of every voice in the world, the ground's bounce of every voice that plays one; the
/// lab asks it to measure. Allocates nothing.
/// </summary>
public static class Bending
{
    /// <summary>
    /// One way's bending: its curvature (per metre, negative bent up), where its shadow starts (metres, infinity for
    /// none), what the shadow takes from each band (dB), the stir's swing on each band now (dB, either sign), and how
    /// much higher bending down stands the source and the ear over the ground's bounce (metres).
    /// </summary>
    public readonly record struct Bent(float Curvature, float EdgeMetres, Refraction.Bands ShadowDb, Refraction.Bands SwingDb,
                                       float SourceRaise, float EarRaise)
    {
        public static readonly Bent None = new(0f, float.PositiveInfinity, Refraction.Bands.None, Refraction.Bands.None, 0f, 0f);

        /// <summary>What the air takes from each band, dB: the shadow less the swing (a swing up is a negative take).</summary>
        public Refraction.Bands TakenDb => new(ShadowDb.Low - SwingDb.Low, ShadowDb.Mid - SwingDb.Mid, ShadowDb.High - SwingDb.High);
    }

    /// <summary>The height the wind along the way is read at, m: the server's wind is the ten-metre wind, and the
    /// profile's coefficients are worked out for it (AirLayers).</summary>
    public const float WindHeightMetres = 10f;

    /// <summary>
    /// The variance of the refractive index, μ², over land or open water for a way whose mean height over it is
    /// <paramref name="heightMetres"/>: the surface layer's own eddies (AirLayers.Layer.EddiesAt) against the speed of
    /// sound (Refraction.StirVariance). Zero in still neutral air.
    /// </summary>
    public static float StirVariance(AirLayers layers, bool overWater, float heightMetres, float speedOfSound)
    {
        var e = (overWater ? layers.Water : layers.Land).EddiesAt(heightMetres);
        return Refraction.StirVariance(e.Along, e.Across, e.Up, e.TemperatureK, layers.TemperatureC, speedOfSound);
    }

    /// <summary>
    /// The bending of the way from <paramref name="source"/> to <paramref name="ear"/>, both over ground at
    /// <paramref name="groundY"/>, over open water or land, at <paramref name="seconds"/> on the world's clock.
    /// </summary>
    public static Bent Along(WorldWeather weather, Vector3 source, Vector3 ear, float groundY, bool overWater, double seconds,
                             float speedOfSound)
    {
        float dx = ear.X - source.X, dz = ear.Z - source.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (!(distance > 1f) || !float.IsFinite(distance)) return Bent.None;
        float mx = 0.5f * (source.X + ear.X), mz = 0.5f * (source.Z + ear.Z);
        var wind = WindField.VelocityAt(weather.Wind, mx, WindHeightMetres, mz, seconds);
        float toward = (wind.X * dx + wind.Y * dz) / distance;
        var layers = weather.Layers;
        var (log, linear) = (overWater ? layers.Water : layers.Land).Along(toward);
        float hs = MathF.Max(0.05f, source.Y - groundY), hr = MathF.Max(0.05f, ear.Y - groundY);
        float curvature = Refraction.Curvature(new Refraction.Profile(log, linear), distance, hs, hr, speedOfSound);
        // The stir is the surface layer's at the way's mean height, and so is the eddies' size.
        float height = 0.5f * (hs + hr);
        float stir = StirVariance(layers, overWater, height, speedOfSound);
        var shadow = Refraction.Shadow(distance, hs, hr, curvature, stir, speedOfSound);
        var sigma = Refraction.Flicker(distance, stir, Refraction.OuterScale(height), speedOfSound);
        var swing = Refraction.Bands.None;
        if (sigma.High > 0f)
        {
            float n = WindField.Stir(weather.Wind, mx, mz, seconds);
            swing = new Refraction.Bands(Refraction.SwingDb(sigma.Low, n), Refraction.SwingDb(sigma.Mid, n), Refraction.SwingDb(sigma.High, n));
        }
        var (rs, re) = Refraction.Raise(hs, hr, distance, curvature);
        return new Bent(curvature, Refraction.ShadowEdge(curvature, hs, hr), shadow, swing, rs, re);
    }
}
