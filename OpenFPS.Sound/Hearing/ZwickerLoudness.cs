namespace OpenFPS.Common.Hearing;

/// <summary>
/// Loudness of a steady sound from its one-third-octave band levels: ISO 532-1:2017, the method for
/// stationary sounds (Zwicker), free or diffuse field.
///
/// A port of the standard's procedure as Zwicker and Fastl published it in BASIC ("Program for
/// calculating loudness according to DIN 45631 (ISO 532 B)", J. Acoust. Soc. Jpn (E) 12, 1, 1991),
/// which the standard's own C code follows: the low bands are weighted down by level along the
/// equal-loudness contours (table A.3), the three lowest critical bands are formed from the bands
/// below 300 Hz, the ear's transmission and threshold give the core loudness of each of 20 critical
/// bands, and the upper slopes (the spread of masking toward high frequencies) are added along the
/// critical-band rate. The sum is the loudness in sone.
///
/// Checked against the standard's annex B.2 test spectrum (83.296 sone) in LoudnessModelTests.
/// Allocation-free: safe on any thread but the mixer's, where nothing this long should run.
/// </summary>
public static class ZwickerLoudness
{
    /// <summary>The 28 one-third-octave centre frequencies the method takes, 25 Hz to 12.5 kHz (ISO 266).</summary>
    public static ReadOnlySpan<float> CentresHz => Centres;
    private static readonly float[] Centres =
    {
        25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f, 200f, 250f, 315f, 400f, 500f, 630f, 800f,
        1000f, 1250f, 1600f, 2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f,
    };

    public const int BandCount = 28;

    // ── The standard's tables (ISO 532-1:2017 annex A; variable names as Zwicker's) ─────────────

    /// <summary>Ranges of band level for the low-frequency correction, dB.</summary>
    private static readonly float[] Rap = { 45f, 55f, 65f, 71f, 80f, 90f, 100f, 120f };

    /// <summary>Reduction of the 11 band levels below 300 Hz within each range of <see cref="Rap"/>, dB.</summary>
    private static readonly float[,] Dll =
    {
        { -32, -24, -16, -10, -5, 0, -7, -3, 0, -2, 0 },
        { -29, -22, -15, -10, -4, 0, -7, -2, 0, -2, 0 },
        { -27, -19, -14, -9, -4, 0, -6, -2, 0, -2, 0 },
        { -25, -17, -12, -9, -3, 0, -5, -2, 0, -2, 0 },
        { -23, -16, -11, -7, -3, 0, -4, -1, 0, -1, 0 },
        { -20, -14, -10, -6, -3, 0, -4, -1, 0, -1, 0 },
        { -18, -12, -9, -6, -2, 0, -3, -1, 0, -1, 0 },
        { -15, -10, -8, -4, -2, 0, -3, -1, 0, -1, 0 },
    };

    /// <summary>Critical-band level at the absolute threshold, without the ear's transmission, dB.</summary>
    private static readonly float[] Ltq = { 30, 18, 12, 8, 7, 6, 5, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3 };

    /// <summary>The ear's transmission (outer and middle ear), dB.</summary>
    private static readonly float[] A0 = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -0.5f, -1.6f, -3.2f, -5.4f, -5.6f, -4f, -1.5f, 2f, 5f, 12f };

    /// <summary>Level difference between a diffuse and a free field, dB.</summary>
    private static readonly float[] Ddf = { 0, 0, 0.5f, 0.9f, 1.2f, 1.6f, 2.3f, 2.8f, 3f, 2f, 0, -1.4f, -2f, -1.9f, -1f, 0.5f, 3f, 4f, 4.3f, 4f };

    /// <summary>From one-third-octave band level to critical-band level, dB.</summary>
    private static readonly float[] Dcb = { -0.25f, -0.6f, -0.8f, -0.8f, -0.5f, 0, 0.5f, 1.1f, 1.5f, 1.7f, 1.8f, 1.8f, 1.7f, 1.6f, 1.4f, 1.2f, 0.8f, 0.5f, 0, -0.5f };

    /// <summary>Upper limits of the approximated critical bands, Bark.</summary>
    private static readonly float[] Zup = { 0.9f, 1.8f, 2.8f, 3.5f, 4.4f, 5.4f, 6.6f, 7.9f, 9.2f, 10.6f, 12.3f, 13.8f, 15.2f, 16.7f, 18.1f, 19.3f, 20.6f, 21.8f, 22.7f, 23.6f, 24.0f };

    /// <summary>Ranges of specific loudness for the steepness of the upper slopes, sone/Bark.</summary>
    private static readonly float[] Rns = { 21.5f, 18f, 15.1f, 11.5f, 9f, 6.1f, 4.4f, 3.1f, 2.13f, 1.36f, 0.82f, 0.42f, 0.30f, 0.22f, 0.15f, 0.10f, 0.035f, 0f };

    /// <summary>Steepness of the upper slopes for each range of <see cref="Rns"/> and critical band group, sone/Bark per Bark.</summary>
    private static readonly float[,] Usl =
    {
        { 13f, 8.2f, 6.3f, 5.5f, 5.5f, 5.5f, 5.5f, 5.5f },
        { 9f, 7.5f, 6f, 5.1f, 4.5f, 4.5f, 4.5f, 4.5f },
        { 7.8f, 6.7f, 5.6f, 4.9f, 4.4f, 3.9f, 3.9f, 3.9f },
        { 6.2f, 5.4f, 4.6f, 4.0f, 3.5f, 3.2f, 3.2f, 3.2f },
        { 4.5f, 3.8f, 3.6f, 3.2f, 2.9f, 2.7f, 2.7f, 2.7f },
        { 3.7f, 3.0f, 2.8f, 2.35f, 2.2f, 2.2f, 2.2f, 2.2f },
        { 2.9f, 2.3f, 2.1f, 1.9f, 1.8f, 1.7f, 1.7f, 1.7f },
        { 2.4f, 1.7f, 1.5f, 1.35f, 1.3f, 1.3f, 1.3f, 1.3f },
        { 1.95f, 1.45f, 1.3f, 1.15f, 1.1f, 1.1f, 1.1f, 1.1f },
        { 1.5f, 1.2f, 0.94f, 0.86f, 0.82f, 0.82f, 0.82f, 0.82f },
        { 0.72f, 0.67f, 0.64f, 0.63f, 0.62f, 0.62f, 0.62f, 0.62f },
        { 0.59f, 0.53f, 0.51f, 0.50f, 0.42f, 0.42f, 0.42f, 0.42f },
        { 0.40f, 0.33f, 0.26f, 0.24f, 0.24f, 0.22f, 0.22f, 0.22f },
        { 0.27f, 0.21f, 0.20f, 0.18f, 0.17f, 0.17f, 0.17f, 0.17f },
        { 0.16f, 0.15f, 0.14f, 0.12f, 0.11f, 0.11f, 0.11f, 0.11f },
        { 0.12f, 0.11f, 0.10f, 0.08f, 0.08f, 0.08f, 0.08f, 0.08f },
        { 0.09f, 0.08f, 0.07f, 0.06f, 0.06f, 0.06f, 0.06f, 0.05f },
        { 0.06f, 0.05f, 0.03f, 0.02f, 0.02f, 0.02f, 0.02f, 0.02f },
    };

    /// <summary>
    /// Loudness, sone, of a steady sound with these 28 one-third-octave band levels (dB SPL, 25 Hz to
    /// 12.5 kHz). Band levels far below hearing may be given as anything under -60.
    /// </summary>
    public static float Sones(ReadOnlySpan<float> bandLevelsDb, bool diffuseField = false)
    {
        if (bandLevelsDb.Length != BandCount) throw new ArgumentException($"{BandCount} band levels expected", nameof(bandLevelsDb));

        // 1. The bands below 300 Hz, weighted down along the equal-loudness contours for their level,
        //    and gathered into the first three critical bands.
        Span<double> ti = stackalloc double[11];
        for (int i = 0; i < 11; i++)
        {
            float level = bandLevelsDb[i];
            int j = 0;
            while (j < 7 && level > Rap[j] - Dll[j, i]) j++;
            ti[i] = Math.Pow(10.0, (level + Dll[j, i]) / 10.0);
        }
        double g0 = ti[0] + ti[1] + ti[2] + ti[3] + ti[4] + ti[5];
        double g1 = ti[6] + ti[7] + ti[8];
        double g2 = ti[9] + ti[10];

        // 2. Core loudness of each critical band.
        Span<float> nm = stackalloc float[21];
        const double s = 0.25;
        for (int i = 0; i < 20; i++)
        {
            double le = i switch
            {
                0 => g0 > 0 ? 10.0 * Math.Log10(g0) : -100.0,
                1 => g1 > 0 ? 10.0 * Math.Log10(g1) : -100.0,
                2 => g2 > 0 ? 10.0 * Math.Log10(g2) : -100.0,
                _ => bandLevelsDb[i + 8],
            };
            le -= A0[i];
            if (diffuseField) le += Ddf[i];
            nm[i] = 0f;
            if (le > Ltq[i])
            {
                le -= Dcb[i];
                double mp1 = 0.0635 * Math.Pow(10.0, 0.025 * Ltq[i]);
                double mp2 = Math.Pow(1.0 - s + s * Math.Pow(10.0, (le - Ltq[i]) / 10.0), 0.25) - 1.0;
                nm[i] = (float)Math.Max(0.0, mp1 * mp2);
            }
        }
        nm[20] = 0f;

        // The lowest critical band: the threshold varies inside it.
        float korry = 0.4f + 0.32f * MathF.Pow(nm[0], 0.2f);
        if (korry <= 1f) nm[0] *= korry;

        // 3. The upper slopes, and the sum along the critical-band rate.
        double n = 0, z1 = 0, n1 = 0, n2 = 0, z2;
        int r = 17;
        for (int i = 0; i < 21; i++)
        {
            double zup = Zup[i] + 0.0001;
            int ig = Math.Min(i - 1, 7);
            int guard = 0;
            while (z1 < zup && guard++ < 64)
            {
                if (n1 <= nm[i])
                {
                    // Unmasked: the band's own core loudness.
                    if (n1 < nm[i])
                    {
                        r = 0;
                        while (r < 17 && Rns[r] > nm[i]) r++;
                    }
                    z2 = zup;
                    n2 = nm[i];
                    n += n2 * (z2 - z1);
                }
                else
                {
                    // Masked, partly or wholly, by the slope falling from the band below.
                    n2 = Rns[r];
                    if (n2 < nm[i]) n2 = nm[i];
                    double usl = Usl[r, Math.Max(ig, 0)];
                    double dz = (n1 - n2) / usl;
                    z2 = z1 + dz;
                    if (z2 > zup)
                    {
                        z2 = zup;
                        dz = z2 - z1;
                        n2 = n1 - dz * usl;
                    }
                    n += dz * (n1 + n2) / 2.0;
                }
                while (r < 17 && n2 <= Rns[r]) r++;
                z1 = z2;
                n1 = n2;
            }
        }
        return (float)Math.Max(0.0, n);
    }

    /// <summary>Loudness level, phon, of a loudness in sone (ISO 532-1 formula: 40 + 10 log2 N above
    /// one sone, 40 (N + 0.0005)^0.35 below).</summary>
    public static float Phons(float sones)
        => sones >= 1f ? 40f + 10f * MathF.Log2(sones) : 40f * MathF.Pow(sones + 0.0005f, 0.35f);

    /// <summary>The inverse of <see cref="Phons"/>.</summary>
    public static float SonesFromPhons(float phons)
        => phons >= 40f ? MathF.Pow(2f, (phons - 40f) / 10f) : MathF.Max(0f, MathF.Pow(MathF.Max(0f, phons) / 40f, 1f / 0.35f) - 0.0005f);
}
