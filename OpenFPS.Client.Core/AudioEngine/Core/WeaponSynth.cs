using System;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// What a weapon sounds like, as numbers. Change these and you have a different gun; nothing else in
/// the synthesis is weapon-specific, which is the point — one profile per weapon, not one renderer.
/// </summary>
public readonly record struct WeaponProfile(
    string Name,
    float MuzzleVelocity,        // m/s
    float PositivePhaseMs,       // the blast pulse's positive phase
    float BurstDecayMs,          // the turbulent gas behind the shock
    float TrailDecayMs,          // what trails after it
    float CornerHz)              // where the spectrum starts to fall
{
    public static WeaponProfile From(WeaponDefinition w) => new(
        Name: w.Id,
        MuzzleVelocity: w.MuzzleVelocity,
        PositivePhaseMs: w.ReportPositivePhaseMs,
        BurstDecayMs: w.ReportBurstDecayMs,
        TrailDecayMs: w.ReportTrailDecayMs,
        CornerHz: w.ReportCornerHz);

    public static WeaponProfile Rifle => From(WeaponRegistry.Ar15);
    public static WeaponProfile Pistol => From(WeaponRegistry.Glock);
    public static WeaponProfile Shotgun => From(WeaponRegistry.Shotgun);
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
    public const int SampleRate = 44100;

    /// <summary>
    /// The report, synthesized to the spec measured from real ones (docs/GUNFIRE.md).
    ///
    /// At 20-40 m every rifle and pistol in the NIJ recordings is a pulse with a positive phase of
    /// 0.35-0.56 ms, down 10 dB within about a millisecond, 20 dB within 2.5-3.5 and 30 dB within
    /// 4-7, with a spectrum roughly level from 125 Hz to 2 kHz that then falls about 6 dB an octave.
    /// The shot before this one took 18-26 ms to fall 20 dB, five to ten times too long, and that
    /// more than its tone was why it did not sound like a gun.
    ///
    /// So: a Friedlander pulse — p(t) = (1 - t/T) e^(-t/T), the jump, the fall through zero at T and
    /// the shallow negative phase — for the shock; decaying noise for the turbulent gas behind it, a
    /// fast burst and a slower trail; one pole above the corner and one below 80 Hz. Nothing after
    /// that: what a shot sounds like after its first few milliseconds is the place it is heard in,
    /// and the engine makes that itself — the echoes off each surface, the scatter off the ground
    /// and the reverb.
    /// </summary>
    public static float[] MuzzleBlast(WeaponProfile w, int seed = 1)
    {
        float T = MathF.Max(0.05f, w.PositivePhaseMs) * 1e-3f;
        float tau = MathF.Max(0.05f, w.BurstDecayMs) * 1e-3f;
        float trail = MathF.Max(0.1f, w.TrailDecayMs) * 1e-3f;
        int n = (int)((T * 10f + trail * 9f) * SampleRate) + 16;
        var buf = new float[n];
        var rng = new Random(seed);
        // PINK noise for the gas: the recordings' spectrum falls about 3 dB an octave from 125 Hz to
        // 2 kHz before it falls steeply, and white noise piles its energy into the top octaves — a
        // white burst put nine per cent of the shot below 500 Hz, the "burst of white noise" of an
        // earlier listening test. (Paul Kellet's three-pole approximation.)
        float p0 = 0f, p1 = 0f, p2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float shock = (1f - t / T) * MathF.Exp(-t / T);
            float white = (float)(rng.NextDouble() * 2 - 1);
            p0 = 0.99765f * p0 + white * 0.0990460f;
            p1 = 0.96300f * p1 + white * 0.2965164f;
            p2 = 0.57000f * p2 + white * 1.0526913f;
            float pink = (p0 + p1 + p2 + white * 0.1848f) * 0.25f;
            float env = MathF.Exp(-t / tau) + ReportTrailLevel * MathF.Exp(-t / trail);
            buf[i] = shock + pink * ReportBurstLevel * env;
        }
        float lp = 1f - MathF.Exp(-2f * MathF.PI * MathF.Max(200f, w.CornerHz) / SampleRate);
        float hp = 1f - MathF.Exp(-2f * MathF.PI * ReportHighPassHz / SampleRate);
        float l = 0f, h = 0f;
        for (int i = 0; i < n; i++)
        {
            l += lp * (buf[i] - l);
            h += hp * (l - h);
            buf[i] = l - h;
        }
        // A short fade over the last tenth, so a buffer that ends on a sample above zero is not a click.
        int fade = n / 10;
        for (int i = 0; i < fade; i++) buf[n - 1 - i] *= i / (float)fade;
        return Finish(buf, 0.95f);
    }

    /// <summary>The turbulent gas against the shock's peak. From the fit to the recordings.</summary>
    public const float ReportBurstLevel = 2.4f;
    /// <summary>The trail against the burst.</summary>
    public const float ReportTrailLevel = 0.18f;
    /// <summary>Below this the report falls away. Only where the recordings stop saying anything: a
    /// handheld recorder's capsules roll off below about 80 Hz.</summary>
    public const float ReportHighPassHz = 80f;

    /// <summary>
    /// Finishes a rendered layer: removes DC, fades the end to silence, then scales to a peak.
    ///
    /// All three matter and none are cosmetic. Resonators and one-pole filters settle at an offset
    /// rather than at zero — the raw N-wave came out sitting 0.12 above the line, which is twelve per
    /// cent of the headroom spent on something inaudible and a step in the waveform the moment a voice
    /// starts or stops. And a one-shot whose buffer ends while it is still decaying ends on a step too,
    /// which is a click at the end of every shot. Peak scaling is last because the engine owns distance:
    /// a one-shot arrives at full scale and is attenuated by where it is, not by how it was rendered.
    /// </summary>
    private static float[] Finish(float[] buf, float peak)
    {
        if (buf.Length == 0) return buf;

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

        float max = 0f;
        foreach (var s in buf) max = MathF.Max(max, MathF.Abs(s));
        if (max <= 1e-9f) return buf;
        float g = peak / max;
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
