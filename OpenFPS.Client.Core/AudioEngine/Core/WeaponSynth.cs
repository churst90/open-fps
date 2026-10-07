using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// What a weapon sounds like, as numbers. Change these and you have a different gun; nothing else in
/// the synthesis is weapon-specific, which is the point — one profile per weapon, not one renderer.
/// </summary>
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
/// Synthesizes the sounds of a shot, as dry mono one-shots for the engine to place.
///
/// Dry and mono is not a compromise, it is the requirement. Every gunshot recording you can find is a
/// few milliseconds of muzzle blast followed by a second of the field it was recorded in, and this
/// engine generates its own field — region reverb, boundary reflections, occlusion, Steam Audio. Feed it
/// a recording with a tail and you hear two rooms at once. Synthesis sidesteps that entirely: what comes
/// out of here has no room in it, so the room it ends up in is the one the player is standing in.
///
/// The report is the one sound here, and it happens at the weapon. A supersonic round's crack happens
/// somewhere else (where its Mach cone meets the listener, see <see cref="Ballistics"/>) and is not
/// built yet; see docs/GUNFIRE.md.
///
/// Deterministic given a seed, so the same shot renders identically on every machine and the tests can
/// assert on the waveform rather than on a description of it.
/// </summary>
public static class WeaponSynth
{
    /// <summary>The rate the engine plays a rendered one-shot at (<see cref="TransientSynth.SampleRate"/>).
    /// This was 44.1 kHz while the engine registered the buffer at 48: every shot played 9 per cent
    /// fast, its positive phase 0.39 ms where 0.42 was written and its spectrum an eighth of an octave
    /// high.</summary>
    public const int SampleRate = TransientSynth.SampleRate;

    /// <summary>
    /// The report, synthesized to the spec measured from real ones (docs/GUNFIRE.md).
    ///
    /// At 20-40 m every rifle and pistol in the NIJ recordings is a pulse with a positive phase of
    /// 0.2-0.5 ms, down 10 dB within about a millisecond, 20 dB within 1.5-3 and 30 dB within 4-6.
    /// Its spectrum peaks at 1 kHz (1-2 kHz for the AK) and falls about 5-6 dB an octave either side
    /// of the peak, and its negative phase is 0.4-1.0 of the positive peak.
    ///
    /// The model, and why each part is there:
    ///  - The far field of a puff of gas is the rate of change of the gas leaving the muzzle, so it
    ///    has no DC: it goes up, through zero and back. As a filter that is a second-order band-pass,
    ///    and critically damped its impulse response is exactly the Friedlander pulse,
    ///    p(t) = (1 - t/T) e^(-t/T). The band-pass's note comes from the positive phase: the first
    ///    zero crossing falls at T = acos(z) / (w0 sqrt(1 - z^2)) for a damping ratio z.
    ///  - The damping is below critical: the gas overshoots ambient and comes back, which is the
    ///    recordings' deep negative phase (a Friedlander's is 0.135 of the peak). It is also the
    ///    peak at 1 kHz, which one pole on pink noise could not make.
    ///  - The turbulent burst behind the shock is the same outflow and radiates through the same
    ///    band-pass. It used to be pink noise straight out, which put as much energy at 125-250 Hz as
    ///    at 1 kHz: 6-12 dB too much low end against the NIJ Glock. The band-pass's lower skirt is
    ///    what the separate high-pass (80 Hz in the game, 250 in the lab's fit) was standing in for.
    ///  - The trail is the gas that leaves after the bubble has rung out: mixing noise from the grown
    ///    plume, broadband, and what holds up the recordings' 125 Hz band (the rifles' above all).
    ///  - Two corners. The gas's turbulence cannot move faster than its eddies (the weapon's corner,
    ///    1.5-4 kHz); the shock front rises in microseconds (<see cref="ShockCornerHz"/>). With one
    ///    corner for both, 8 kHz came out 6-10 dB light.
    ///  - A revolver also blasts at its cylinder gap, earlier and smaller, through the same model.
    /// Nothing after that: what a shot sounds like after its first few milliseconds is the place it
    /// is heard in, and the engine makes that itself.
    ///
    /// Normalised to <see cref="ReportEnergyDb"/>, not to a peak: see there.
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
            // The gap: a small share of the charge's gas, so a shorter and brighter pulse (a blast's
            // duration goes as the cube root of its energy), when the bullet leaves the cylinder.
            Blast(buf, 0, w.PositivePhaseMs * GapPulseScale, w.Damping, w.BurstDecayMs * GapPulseScale,
                  w.TrailDecayMs * GapPulseScale, w.TrailLevel, w.CornerHz / GapPulseScale, w.GapLevel, rng);
        // A short fade over the last tenth, so a buffer that ends on a sample above zero is not a click.
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

        // The bubble's band-pass for the burst, as a biquad (RBJ's, unity at its centre).
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
    /// of the gas, about a tenth.</summary>
    public const float GapPulseScale = 0.46f;
    /// <summary>The shock front's rise, as a corner, Hz: a weak shock tens of metres out rises in
    /// 10-20 microseconds.</summary>
    public const float ShockCornerHz = 12000f;

    /// <summary>
    /// Finishes a rendered layer: removes DC and fades the end to silence.
    ///
    /// Neither is cosmetic. Resonators and one-pole filters settle at an offset rather than at zero —
    /// the raw N-wave came out sitting 0.12 above the line, which is twelve per cent of the headroom
    /// spent on something inaudible and a step in the waveform the moment a voice starts or stops. And
    /// a one-shot whose buffer ends while it is still decaying ends on a step too, which is a click at
    /// the end of every shot.
    /// </summary>
    private static void Finish(float[] buf)
    {
        if (buf.Length == 0) return;

        // Subtract the actual mean first. A one-pole high-pass alone is not enough now that the blast
        // has a real sub-bass layer in it: a Friedlander wave integrates to zero over all time, but
        // driving one through tanh does not — saturation squashes the big positive lobe harder than
        // the shallow negative one and leaves an offset behind that an 18 Hz corner barely touches.
        // Removing the measured mean is exact and, unlike a steeper filter, costs none of the weight
        // the layer was added for.
        float mean = 0f;
        foreach (var v in buf) mean += v;
        mean /= buf.Length;
        if (MathF.Abs(mean) > 1e-6f)
            for (int i = 0; i < buf.Length; i++) buf[i] -= mean;

        // DC block: a one-pole high-pass at about 18 Hz. Below anything a muzzle blast really contains,
        // above the offset the filters leave behind.
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

        // Fade the last few milliseconds to zero so the buffer ends where silence begins.
        int fade = Math.Min(buf.Length, (int)(SampleRate * 0.008f));
        for (int i = 0; i < fade; i++)
        {
            int at = buf.Length - fade + i;
            buf[at] *= 1f - (i / (float)fade);
        }

    }

    /// <summary>
    /// What every report carries into the engine: its energy, as the sum of its squared samples over
    /// the sample rate, in dB (re one second at full scale).
    ///
    /// By ENERGY, not by peak, because that is what a short sound's loudness follows (the ear
    /// integrates over about 100 ms) and what <see cref="Applause"/> normalises a clap by. Scaled to
    /// a peak, a report's level depended on its crest factor: an 18 ms buffer whose energy sits in
    /// 1-2 ms carried 12 dB less than a clap at the same peak, and a brighter or shorter gun came out
    /// quieter than a darker one at the same declared level. Normalised like this every weapon carries
    /// the same energy, and what separates them is their cartridge level (<see cref="Loudness"/>).
    ///
    /// The value is the most the weapons can carry inside full scale: the engine registers a one-shot
    /// as 16-bit PCM, so a sample past 1.0 is clipped there, and the sharpest report here must still
    /// fit (<see cref="ReportPeakCeiling"/>). That is why a shot still carries less than a clap does:
    /// a clap is limited (Applause soft-clips above 0.9) and a report is not.
    /// </summary>
    public const float ReportEnergyDb = -39f;

    /// <summary>The highest a report may peak. A report whose energy would take it past this is held
    /// here instead (and comes out quieter); the tests require that no weapon is.</summary>
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

    /// <summary>Reads a 16-bit mono WAV back to floats. Enough to re-load what the ingest wrote; it
    /// is not a general decoder and says so by returning empty rather than guessing at a format it
    /// does not recognise.</summary>
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
                    // Mono-sum anything wider, so a stereo take does not come back at half length.
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
