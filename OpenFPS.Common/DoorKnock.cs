namespace OpenFPS.Common;

/// <summary>
/// Knuckles on a wooden door, made as the car door is: each knock a burst of noise in every octave band
/// at the band's level and dry decay, no resonators (they were "an instrument, too tonal" on the car door).
///
/// Fitted to Cody's "Heavy Door Knocks" recording (measured 2026-09-29, 30 knocks, 0-120 ms after each):
/// the energy is in the door, 63-500 Hz, 1 kHz about 8 dB under it and the knuckle's crack at 2-8 kHz
/// 19-31 dB under; the middle dies inside 50 ms, 125-250 Hz rings a little longer. The recording's slow
/// tail is its room, which the game adds itself. Knocks come about a quarter second apart, the last softer.
/// </summary>
public static class DoorKnock
{
    public const string KeyPrefix = "knock:";
    public static string Key(int knocks) => KeyPrefix + Math.Clamp(knocks, 1, 8);

    public static bool TryParseKey(string? key, out int knocks)
    {
        knocks = 0;
        return key != null && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(KeyPrefix.Length), out knocks) && knocks is >= 1 and <= 8;
    }

    /// <summary>A knock's level, dB SPL at a metre: a firm knock on a solid door.</summary>
    public const float LevelDb = 80f;

    // Per band (the car door's octaves, 30 Hz to 16 kHz): level against the loudest, dB, and dry T60, s.
    private static readonly float[] Shape = { -3.6f, 0.0f, -8.5f, -11.3f, -14.2f, -36.2f, -30.2f, -41.5f, -54.2f };
    private static readonly float[] T60 = { 0.15f, 0.20f, 0.25f, 0.18f, 0.12f, 0.08f, 0.06f, 0.05f, 0.04f };

    public static float[] Render(int knocks, int sampleRate, int seed)
    {
        knocks = Math.Clamp(knocks, 1, 8);
        var rng = new Random(seed);
        float spacing = 0.24f;
        float length = spacing * (knocks - 1) + 0.4f;
        int n = (int)(length * sampleRate);
        var bands = CarDoor.BandNoise(n, sampleRate, rng);
        var outp = new float[n];
        float at = 0.005f;
        for (int k = 0; k < knocks; k++)
        {
            float db = (k == knocks - 1 && knocks > 1 ? -2f : 0f) + ((float)rng.NextDouble() - 0.5f) * 3f;
            for (int b = 0; b < Shape.Length; b++)
            {
                float jitter = ((float)rng.NextDouble() - 0.5f) * 2f;
                CarDoor.AddHit(outp, bands[b], sampleRate, at, T60[b] * (0.9f + 0.2f * (float)rng.NextDouble()),
                               MathF.Pow(10f, (Shape[b] + db + jitter) / 20f));
            }
            at += spacing * (0.85f + 0.3f * (float)rng.NextDouble());
        }
        float peak = 1e-9f;
        foreach (var v in outp) peak = MathF.Max(peak, MathF.Abs(v));
        for (int i = 0; i < n; i++) outp[i] /= peak;
        return outp;
    }
}
