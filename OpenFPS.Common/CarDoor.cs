namespace OpenFPS.Common;

/// <summary>
/// A car door opening or shutting, built from octave bands of noise.
///
/// Fitted to a recording of a car door (approved/car-door, see approved/README.md) and approved by ear on
/// 2026-09-28. The fit and its instruments are in tools/car_door_fit. The first version rang a set of
/// resonators at the door skin's and the cabin's modes, matched the recording's band levels, and was
/// rejected: "it sounds like an instrument... too tonal and not mechanical". A few fixed modes ringing
/// for half a second are a chord. Measured, its strongest narrow peaks stood 22 dB over the spectrum
/// around them 50-140 ms after the slam, against 9 dB in the recording. So nothing here rings at a
/// pitch: every part is noise, given a spectrum (a level per octave band) and a decay per band.
///
/// Shutting: four hits over 75 ms (first touch, the latch's secondary catch, the door seating on its
/// primary catch, a rebound), the cabin answering in the three lowest bands as the seal pushes air
/// into it, and the body settling with a few small dull knocks. Opening: the handle and the rod
/// working the latch, the latch letting go, and the check strap's roller dropping into its detent as
/// the door swings.
///
/// One car's door, for now. Every vehicle uses it until there are recordings of others (a van's
/// sliding door, a truck's cab door) to fit.
/// </summary>
public static class CarDoor
{
    public const string KeyPrefix = "cardoor:";
    public static string Key(bool closing) => KeyPrefix + (closing ? "close" : "open");

    public static bool TryParseKey(string? key, out bool closing)
    {
        closing = false;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string what = key[KeyPrefix.Length..];
        if (what.Equals("close", StringComparison.OrdinalIgnoreCase)) { closing = true; return true; }
        return what.Equals("open", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The octave bands, Hz: 30-60 up to 8-16 kHz.</summary>
    internal static readonly float[] Edges = { 30f, 60f, 120f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
    private const int Bands = 9;

    // ── Shutting, as fitted ──────────────────────────────────────────────────────────────────
    /// <summary>The slam's spectrum, dB per band.</summary>
    private static readonly float[] SlamShape = { 6.0f, -24.0f, -8.59f, -8.66f, -4.28f, -7.92f, -10.42f, -13.01f, -17.35f };
    /// <summary>The four hits: seconds after the first touch, and level, dB.</summary>
    private static readonly (float At, float Db)[] SlamHits = { (0f, 0f), (0.014f, -11.0f), (0.030f, 0f), (0.075f, -13.8f) };
    /// <summary>The skin's decay: T60 = SkinT60 × (500 / f)^SkinExp, no longer than SkinT60Max.</summary>
    private const float SkinT60 = 0.157f, SkinExp = 0.718f, SkinT60Max = 0.294f;
    /// <summary>The cabin: the three lowest bands swell over BoomRise and die away with BoomT60.</summary>
    private const float BoomRise = 0.056f, BoomT60 = 0.604f;
    private static readonly float[] BoomDb = { -11.88f, -12.64f, -10.10f };
    /// <summary>Settling: four knocks, the first SettleAt after the slam, duller as they go up.</summary>
    private static readonly float[] SettleSteps = { 0f, -6f, -10f, -16f };
    private const float SettleAt = 0.12f, SettleDb = -15.0f, SettleTilt = 1.79f;

    // ── Opening, as fitted ───────────────────────────────────────────────────────────────────
    /// <summary>The handle and rod: four ticks starting HandleLead before the latch lets go.</summary>
    private const float HandleLead = 0.103f, HandleDb = -16.0f, HandleT60 = 0.03f;
    private static readonly float[] HandleSteps = { -4f, 0f, 4f, -2f };
    private static readonly float[] HandleShape = { -20f, -4.54f, 10f, -18.31f, 10f, -20f, -8.65f, -20f, -20f };
    /// <summary>The latch letting go: two hits 8 ms apart.</summary>
    private static readonly float[] UnlatchShape = { -30f, -30f, 6f, -30f, -0.09f, -2.98f, -14.85f, -22.44f, -30f };
    private const float UnlatchT60 = 0.157f;
    /// <summary>The check strap's detent, DetentAt after the latch.</summary>
    private const float DetentAt = 0.386f, DetentDb = -0.06f, DetentT60 = 0.081f;
    private static readonly float[] DetentShape = { -9.91f, 7.11f, -10.14f, -18.96f, 10f, -5.86f, -0.66f, -4.31f, -9.87f };

    /// <summary>
    /// Renders one, normalised to a peak of one. The sound starts a few milliseconds before its first
    /// event: shutting, the first touch; opening, the handle.
    /// </summary>
    public static float[] Render(bool closing, int sampleRate, int seed)
    {
        var rng = new Random(seed);
        float sr = sampleRate;
        int n = (int)((closing ? 1.15f : 0.95f) * sr);
        var band = BandNoise(n, sr, rng);
        var outp = new float[n];
        float t0 = closing ? 0.005f : HandleLead + 0.01f;

        if (closing)
        {
            foreach (var (at, db) in SlamHits)
                for (int b = 0; b < Bands; b++)
                    AddHit(outp, band[b], sr, t0 + at, MathF.Min(SkinT60Max, SkinT60 * MathF.Pow(500f / Centre(b), SkinExp)), Gain(db + SlamShape[b]));
            for (int b = 0; b < 3; b++)
                AddSwell(outp, band[b], sr, t0, BoomRise, BoomT60, Gain(BoomDb[b]));
            float tt = SettleAt;
            foreach (float step in SettleSteps)
            {
                for (int b = 0; b < Bands; b++)
                    AddHit(outp, band[b], sr, t0 + tt, MathF.Min(0.15f, 0.08f * MathF.Sqrt(500f / Centre(b))),
                           Gain(step + SettleDb + SlamShape[b] - SettleTilt * b));
                tt += 0.04f + 0.08f * (float)rng.NextDouble();
            }
        }
        else
        {
            float tt = t0 - HandleLead;
            foreach (float step in HandleSteps)
            {
                for (int b = 0; b < Bands; b++)
                    AddHit(outp, band[b], sr, tt, HandleT60, Gain(step + HandleDb + HandleShape[b]));
                tt += 0.015f + 0.02f * (float)rng.NextDouble();
            }
            foreach (var (at, db) in new[] { (0f, 0f), (0.008f, -6f) })
                for (int b = 0; b < Bands; b++)
                    AddHit(outp, band[b], sr, t0 + at, MathF.Min(0.3f, UnlatchT60 * MathF.Pow(500f / Centre(b), 0.4f)), Gain(db + UnlatchShape[b]));
            for (int b = 0; b < Bands; b++)
                AddHit(outp, band[b], sr, t0 + DetentAt, MathF.Min(0.2f, DetentT60 * MathF.Pow(500f / Centre(b), 0.3f)), Gain(DetentDb + DetentShape[b]));
        }

        float peak = 0f;
        foreach (float v in outp) peak = MathF.Max(peak, MathF.Abs(v));
        if (peak > 1e-9f) for (int i = 0; i < n; i++) outp[i] /= peak;
        return outp;
    }

    private static float Centre(int b) => MathF.Sqrt(Edges[b] * Edges[b + 1]);
    private static float Gain(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>A struck thing's envelope on one band: up in under a millisecond, down by T60.</summary>
    internal static void AddHit(float[] outp, float[] band, float sr, float at, float t60, float gain)
    {
        int a = (int)(at * sr);
        float attack = 0.0008f * sr, k = 6.9f / (t60 * sr);
        int end = Math.Min(outp.Length, a + (int)(t60 * sr * 1.2f));
        for (int i = Math.Max(0, a); i < end; i++)
        {
            float t = i - a;
            outp[i] += band[i] * gain * MathF.Min(1f, t / attack) * MathF.Exp(-k * t);
        }
    }

    /// <summary>Air pushed into a closed space: a swell over <paramref name="rise"/>, then a decay.</summary>
    private static void AddSwell(float[] outp, float[] band, float sr, float at, float rise, float t60, float gain)
    {
        int a = (int)(at * sr);
        for (int i = Math.Max(0, a); i < outp.Length; i++)
        {
            float t = (i - a) / sr;
            float r = MathF.Min(1f, t / rise), s = MathF.Sin(0.5f * MathF.PI * r);
            outp[i] += band[i] * gain * s * s * MathF.Exp(-6.9f * MathF.Max(0f, t - rise) / t60);
        }
    }

    /// <summary>One white noise split into the octave bands, each by a third-order Butterworth
    /// band-pass: the filter the fit was made with (scipy.signal.butter(3, band, "band")).</summary>
    internal static float[][] BandNoise(int n, float sr, Random rng)
    {
        // A tenth of a second run through the filters first and thrown away: the 30-60 Hz band takes
        // that long to fill, and a band that starts empty is 10 dB short in the slam's first 50 ms.
        int pre = (int)(0.1f * sr);
        var white = new float[pre + n];
        for (int i = 0; i < white.Length; i++) white[i] = ((float)rng.NextDouble() * 2f - 1f) * 1.7320508f;
        var bands = new float[Bands][];
        for (int b = 0; b < Bands; b++)
        {
            var y = (float[])white.Clone();
            ButterworthBandPass(y, Edges[b], MathF.Min(Edges[b + 1], 0.45f * sr), sr);
            bands[b] = y[pre..];
        }
        return bands;
    }

    /// <summary>
    /// A third-order Butterworth band-pass between two edges, in place: the low-pass prototype's three
    /// poles each become a pair about the (prewarped) centre, and the bilinear transform takes the six
    /// to z, as three biquads with their zeros at z = 1 and z = -1. Unity gain at the centre.
    /// </summary>
    private static void ButterworthBandPass(float[] x, float lo, float hi, float sr)
    {
        double fs2 = 2.0 * sr;
        double w1 = fs2 * Math.Tan(Math.PI * lo / sr), w2 = fs2 * Math.Tan(Math.PI * hi / sr);
        double w0 = Math.Sqrt(w1 * w2), bw = w2 - w1;
        var poles = new List<System.Numerics.Complex>();
        for (int k = 0; k < 3; k++)
        {
            var p = System.Numerics.Complex.FromPolarCoordinates(1.0, Math.PI * (2 * k + 4) / 6.0);   // left half plane
            var pb = p * bw;
            var root = System.Numerics.Complex.Sqrt(pb * pb - 4.0 * w0 * w0);
            foreach (var sPole in new[] { (pb + root) / 2.0, (pb - root) / 2.0 })
            {
                var z = (fs2 + sPole) / (fs2 - sPole);
                if (z.Imaginary > 1e-12) poles.Add(z);
            }
        }
        // Gain at the centre, digital: |H(e^jw)| with w the centre mapped back through the bilinear.
        double wd = 2.0 * Math.Atan(w0 / fs2);
        var ejw = System.Numerics.Complex.FromPolarCoordinates(1.0, wd);
        var h = System.Numerics.Complex.One;
        foreach (var z in poles)
            h *= (1.0 - ejw * ejw) / ((ejw - z) * (ejw - System.Numerics.Complex.Conjugate(z)));
        float gain = (float)(1.0 / h.Magnitude);
        for (int i = 0; i < x.Length; i++) x[i] *= gain;
        foreach (var z in poles)
        {
            float a1 = (float)(-2.0 * z.Real), a2 = (float)(z.Magnitude * z.Magnitude);
            float xa = 0f, xb = 0f, ya = 0f, yb = 0f;
            for (int i = 0; i < x.Length; i++)
            {
                float v = x[i] - xb - a1 * ya - a2 * yb;
                xb = xa; xa = x[i]; yb = ya; ya = v; x[i] = v;
            }
        }
    }
}
