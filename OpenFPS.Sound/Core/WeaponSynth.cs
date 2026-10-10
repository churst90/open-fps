using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>What a weapon's report sounds like, as numbers; nothing else in the synthesis is
/// weapon-specific.</summary>
public readonly record struct WeaponProfile(
    string Name,
    float MuzzleVelocity,        // m/s
    float PositivePhaseMs,       // the shock's positive phase, which sets the gas bubble's note
    float BurstDecayMs,          // the turbulent gas behind the shock
    float TrailDecayMs,          // what trails after it
    float CornerHz,              // the gas's eddies: one pole above it
    float Damping = 1f,          // the gas bubble's damping ratio: 1 is a Friedlander pulse
    float TrailLevel = 0.1f,     // the trail against the burst
    float GapLeadMs = 0f,        // a revolver's cylinder gap: how far its blast leads the muzzle's
    float GapLevel = 0f)         // and how strong it is against the muzzle's
{
    public static WeaponProfile From(WeaponDefinition w) => new(
        Name: w.Id,
        MuzzleVelocity: w.MuzzleVelocity,
        PositivePhaseMs: w.ReportPositivePhaseMs,
        BurstDecayMs: w.ReportBurstDecayMs,
        TrailDecayMs: w.ReportTrailDecayMs,
        CornerHz: w.ReportCornerHz,
        Damping: w.ReportDamping,
        TrailLevel: w.ReportTrailLevel,
        GapLeadMs: w.CylinderGapLeadMs,
        GapLevel: w.CylinderGapLevel);

    public static WeaponProfile Rifle => From(WeaponRegistry.Ar15);
    public static WeaponProfile Pistol => From(WeaponRegistry.Glock);
}

/// <summary>
/// The report of a shot, as a dry mono one-shot: the engine makes the room, and a recording's own
/// tail would be a second room. A supersonic round's crack is elsewhere (<see cref="Ballistics"/>).
/// Deterministic given a seed, so tests can assert on the waveform. See docs/GUNFIRE.md.
/// </summary>
public static class WeaponSynth
{
    /// <summary>The rate the engine plays a rendered one-shot at. Rendered at 44.1 kHz and played at
    /// 48, every shot was 9 per cent fast and an eighth of an octave high.</summary>
    public const int SampleRate = TransientSynth.SampleRate;

    /// <summary>
    /// The report, synthesized to the spec measured from the NIJ recordings (docs/GUNFIRE.md, "The
    /// bubble model"): at 20-40 m a pulse with a 0.2-0.5 ms positive phase, down 10 dB within about
    /// a millisecond, peaking at 1 kHz, with a negative phase 0.4-1.0 of the peak.
    ///
    /// The gas outflow through a second-order band-pass (critically damped, the Friedlander pulse
    /// p(t) = (1 - t/T) e^(-t/T); the first zero crossing at T = acos(z) / (w0 sqrt(1 - z^2))). Damped
    /// below critical for the deep negative phase and the 1 kHz peak. The turbulent burst goes through
    /// the same band-pass (pink noise straight out was 6-12 dB too heavy at 125-250 Hz against the
    /// Glock); the trail does not. Two corners, the eddies' and the shock's rise: with one, 8 kHz came
    /// out 6-10 dB light. A revolver also blasts at its cylinder gap. Normalised to
    /// <see cref="ReportEnergyDb"/>, not to a peak.
    /// </summary>
    public static float[] MuzzleBlast(WeaponProfile w, int seed = 1)
    {
        float T = MathF.Max(0.05f, w.PositivePhaseMs) * 1e-3f;
        float trail = MathF.Max(0.1f, w.TrailDecayMs) * 1e-3f;
        float lead = w.GapLevel > 0f ? MathF.Max(0f, w.GapLeadMs) * 1e-3f : 0f;
        int n = (int)((lead + T * 10f + trail * 9f) * SampleRate) + 16;
        var buf = new float[n];
        var rng = new Random(seed);
        Blast(buf, (int)MathF.Round(lead * SampleRate), w.PositivePhaseMs, w.Damping, w.BurstDecayMs,
              w.TrailDecayMs, w.TrailLevel, w.CornerHz, 1f, rng);
        if (lead > 0f)
            // The gap: a small share of the gas, so a shorter, brighter pulse (duration goes as the
            // cube root of energy).
            Blast(buf, 0, w.PositivePhaseMs * GapPulseScale, w.Damping, w.BurstDecayMs * GapPulseScale,
                  w.TrailDecayMs * GapPulseScale, w.TrailLevel, w.CornerHz / GapPulseScale, w.GapLevel, rng);
        // A fade over the last tenth: a buffer ending above zero is a click.
        int fade = n / 10;
        for (int i = 0; i < fade; i++) buf[n - 1 - i] *= i / (float)fade;
        Finish(buf);
        return ToEnergy(buf, ReportEnergyDb);
    }

    /// <summary>One blast added into <paramref name="buf"/> from <paramref name="start"/>.</summary>
    private static void Blast(float[] buf, int start, float positivePhaseMs, float damping, float burstMs,
                              float trailMs, float trailLevel, float cornerHz, float level, Random rng)
    {
        float T = MathF.Max(0.05f, positivePhaseMs) * 1e-3f;
        float tau = MathF.Max(0.05f, burstMs) * 1e-3f;
        float trail = MathF.Max(0.1f, trailMs) * 1e-3f;
        float z = Math.Clamp(damping, 0.05f, 1f);
        float w0 = BubbleRadPerSecond(T, z);
        bool critical = z >= 0.999f;
        float wd = critical ? 0f : w0 * MathF.Sqrt(1f - z * z);
        float k = critical ? 0f : z / MathF.Sqrt(1f - z * z);

        // The bubble's band-pass for the burst (RBJ, unity at its centre).
        float wn = w0 / SampleRate, q = 1f / (2f * z);
        float alpha = MathF.Sin(wn) / (2f * q), a0 = 1f + alpha;
        float b0 = alpha / a0, b2 = -alpha / a0, a1 = -2f * MathF.Cos(wn) / a0, a2 = (1f - alpha) / a0;
        float x1 = 0f, x2 = 0f, y1 = 0f, y2 = 0f;
        float p0 = 0f, p1 = 0f, p2 = 0f;               // Paul Kellet's pink
        float eddy = 1f - MathF.Exp(-2f * MathF.PI * MathF.Max(200f, cornerHz) / SampleRate);
        float rise = 1f - MathF.Exp(-2f * MathF.PI * ShockCornerHz / SampleRate);
        float gasOut = 0f, shockOut = 0f;
        for (int i = start; i < buf.Length; i++)
        {
            float t = (i - start) / (float)SampleRate;
            float shock = critical
                ? (1f - w0 * t) * MathF.Exp(-w0 * t)
                : MathF.Exp(-z * w0 * t) * (MathF.Cos(wd * t) - k * MathF.Sin(wd * t));
            float white = (float)(rng.NextDouble() * 2 - 1);
            p0 = 0.99765f * p0 + white * 0.0990460f;
            p1 = 0.96300f * p1 + white * 0.2965164f;
            p2 = 0.57000f * p2 + white * 1.0526913f;
            float pink = (p0 + p1 + p2 + white * 0.1848f) * 0.25f * ReportBurstLevel;
            float burst = pink * MathF.Exp(-t / tau);
            float y = b0 * burst + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = burst; y2 = y1; y1 = y;
            gasOut += eddy * (y + pink * trailLevel * MathF.Exp(-t / trail) - gasOut);
            shockOut += rise * (shock - shockOut);
            buf[i] += level * (shockOut + gasOut);
        }
    }

    /// <summary>The bubble's natural frequency, rad/s, for the positive phase it has to make.</summary>
    public static float BubbleRadPerSecond(float positivePhaseSeconds, float damping)
    {
        float z = Math.Clamp(damping, 0.05f, 1f);
        if (z >= 0.999f) return 1f / positivePhaseSeconds;
        return MathF.Acos(z) / (positivePhaseSeconds * MathF.Sqrt(1f - z * z));
    }

    /// <summary>The turbulent burst against the shock's peak, before the bubble's band-pass takes its
    /// share. Fitted with the weapons' values against the NIJ takes (`--gun-fit grid`).</summary>
    public const float ReportBurstLevel = 4f;
    /// <summary>A cylinder gap's pulse against the muzzle's, in duration: the cube root of its share
    /// of the gas (about a tenth).</summary>
    public const float GapPulseScale = 0.46f;
    /// <summary>The shock front's rise, as a corner, Hz: a weak shock tens of metres out rises in
    /// 10-20 microseconds.</summary>
    public const float ShockCornerHz = 12000f;

    /// <summary>
    /// Removes DC and fades the end to silence. Filters settle at an offset (the raw N-wave once sat
    /// 0.12 above the line: headroom spent on nothing, and a step when a voice starts or stops), and a
    /// buffer that ends while still decaying clicks.
    /// </summary>
    private static void Finish(float[] buf)
    {
        if (buf.Length == 0) return;

        // The measured mean first: exact, where an 18 Hz corner barely touches an offset.
        float mean = 0f;
        foreach (var v in buf) mean += v;
        mean /= buf.Length;
        if (MathF.Abs(mean) > 1e-6f)
            for (int i = 0; i < buf.Length; i++) buf[i] -= mean;

        // DC block at about 18 Hz, below anything a muzzle blast contains.
        float r = 1f - 2f * MathF.PI * 18f / SampleRate;
        float lastIn = 0f, lastOut = 0f;
        for (int i = 0; i < buf.Length; i++)
        {
            float x = buf[i];
            float y = x - lastIn + r * lastOut;
            lastIn = x;
            lastOut = y;
            buf[i] = y;
        }

        int fade = Math.Min(buf.Length, (int)(SampleRate * 0.008f));
        for (int i = 0; i < fade; i++)
        {
            int at = buf.Length - fade + i;
            buf[at] *= 1f - (i / (float)fade);
        }

    }

    /// <summary>
    /// The energy every report carries, dB re one second at full scale. By energy, not peak, as a
    /// short sound's loudness follows it (the ear integrates about 100 ms) and as
    /// <see cref="Applause"/> normalises a clap: scaled to a peak, a report's level followed its crest
    /// factor (12 dB under a clap at the same peak), and the cartridge level (<see cref="Loudness"/>)
    /// is what separates weapons. The most the sharpest report can carry inside full scale (16-bit PCM;
    /// <see cref="ReportPeakCeiling"/>); a clap carries more because Applause soft-clips and this does not.
    /// </summary>
    public const float ReportEnergyDb = -39f;

    /// <summary>The highest a report may peak; one whose energy would pass it is held here, quieter.
    /// The tests require that no weapon is.</summary>
    public const float ReportPeakCeiling = 0.98f;

    private static float[] ToEnergy(float[] buf, float energyDb)
    {
        double e = 0;
        float max = 0f;
        foreach (var v in buf) { e += (double)v * v; max = MathF.Max(max, MathF.Abs(v)); }
        if (e <= 1e-30 || max <= 1e-9f) return buf;
        float g = MathF.Sqrt((float)(Math.Pow(10, energyDb / 10.0) * SampleRate / e));
        g = MathF.Min(g, ReportPeakCeiling / max);
        for (int i = 0; i < buf.Length; i++) buf[i] *= g;
        return buf;
    }

    /// <summary>Reads a 16-bit WAV back to mono floats; empty for any other format. Not a general
    /// decoder.</summary>
    public static float[] ReadWav16Mono(byte[] wav)
    {
        if (wav == null || wav.Length < 44) return Array.Empty<float>();
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return Array.Empty<float>();

        int pos = 12;
        short channels = 1, bits = 16;
        while (pos + 8 <= wav.Length)
        {
            string id = $"{(char)wav[pos]}{(char)wav[pos + 1]}{(char)wav[pos + 2]}{(char)wav[pos + 3]}";
            int size = BitConverter.ToInt32(wav, pos + 4);
            if (size < 0 || pos + 8 + size > wav.Length) size = wav.Length - pos - 8;

            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(wav, pos + 10);
                bits = BitConverter.ToInt16(wav, pos + 22);
            }
            else if (id == "data")
            {
                if (bits != 16 || channels < 1) return Array.Empty<float>();
                int frames = size / 2 / channels;
                var outp = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    int acc = 0;
                    for (int ch = 0; ch < channels; ch++)
                        acc += BitConverter.ToInt16(wav, pos + 8 + (i * channels + ch) * 2);
                    outp[i] = acc / (float)channels / 32768f;
                }
                return outp;
            }
            pos += 8 + size + (size & 1);
        }
        return Array.Empty<float>();
    }

    /// <summary>16-bit mono WAV, for auditioning a profile outside the game.</summary>
    public static byte[] ToWav16(float[] samples, int sampleRate = SampleRate)
    {
        int dataBytes = samples.Length * 2;
        var bytes = new byte[44 + dataBytes];
        void Str(int at, string s) { for (int i = 0; i < s.Length; i++) bytes[at + i] = (byte)s[i]; }
        void I32(int at, int v) => BitConverter.GetBytes(v).CopyTo(bytes, at);
        void I16(int at, short v) => BitConverter.GetBytes(v).CopyTo(bytes, at);

        Str(0, "RIFF"); I32(4, 36 + dataBytes); Str(8, "WAVE");
        Str(12, "fmt "); I32(16, 16); I16(20, 1); I16(22, 1);
        I32(24, sampleRate); I32(28, sampleRate * 2); I16(32, 2); I16(34, 16);
        Str(36, "data"); I32(40, dataBytes);

        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            I16(44 + i * 2, v);
        }
        return bytes;
    }
}
