using System;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// What a weapon sounds like, as numbers. Change these and you have a different gun; nothing else in
/// the synthesis is weapon-specific, which is the point — one profile per weapon, not one renderer.
/// </summary>
public readonly record struct WeaponProfile(
    string Name,
    float MuzzleVelocity,        // m/s. Decides whether there is a crack at all, and how tight it is.
    float PositivePhaseMs,       // the blast pulse's positive phase
    float BurstDecayMs,          // the turbulent gas behind the shock
    float TrailDecayMs,          // what trails after it
    float CornerHz,              // where the spectrum starts to fall
    float MechanicalDelaySeconds,// when the action is heard after the shot
    float MechanicalLevel)
{
    public static WeaponProfile From(WeaponDefinition w) => new(
        Name: w.Id,
        MuzzleVelocity: w.MuzzleVelocity,
        PositivePhaseMs: w.ReportPositivePhaseMs,
        BurstDecayMs: w.ReportBurstDecayMs,
        TrailDecayMs: w.ReportTrailDecayMs,
        CornerHz: w.ReportCornerHz,
        MechanicalDelaySeconds: w.MechanicalDelaySeconds,
        MechanicalLevel: w.MechanicalLevel);

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
/// There are three separate sounds here and they belong at different PLACES as well as different times:
///
///   * <see cref="MuzzleBlast"/> happens at the weapon.
///   * <see cref="SupersonicCrack"/> happens at the LISTENER, as the round passes them, and arrives
///     first. See <see cref="Ballistics"/> — the gap between the two is a distance cue.
///   * <see cref="MechanicalAction"/> happens at the weapon, just after.
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
    /// The crack of a supersonic round passing the listener.
    ///
    /// This is not a quieter gunshot, it is a different sound entirely: an N-wave, a near-instantaneous
    /// pressure step up and back down, lasting a fraction of a millisecond. That is why it reads as a
    /// whip rather than a bang, and why it is so easy to localize — almost all its energy is high and
    /// broadband, which is exactly what the ears use to place a sound.
    ///
    /// It is rendered AT THE LISTENER, so it should be played unspatialized or at a point just off the
    /// listener's position, never at the shooter's.
    /// </summary>
    public static float[] SupersonicCrack(WeaponProfile w, float missDistance,
                                          float speedOfSound = AudioPhysics.SpeedOfSound, int seed = 2)
    {
        if (!Ballistics.MakesCrack(w.MuzzleVelocity, missDistance, speedOfSound))
            return Array.Empty<float>();

        float nWave = Ballistics.CrackDurationSeconds(missDistance, w.MuzzleVelocity, speedOfSound);
        // The N-wave itself is the crack; what follows is the ground and the air smearing it out.
        float tail = 0.045f + missDistance * 0.0025f;
        int n = (int)(SampleRate * (nWave + tail));
        if (n < 8) return Array.Empty<float>();

        var buf = new float[n];
        var rng = new Random(seed);
        int nWaveSamples = Math.Max(2, (int)(nWave * SampleRate));

        float lp = 0f;
        // A near miss keeps everything; a distant one has lost its highs to the air on the way over.
        float cutoff = MathHelper.Lerp(11000f, 2600f, Math.Clamp(missDistance / Ballistics.MaxCrackMissDistance, 0f, 1f));
        float alpha = 1f - MathF.Exp(-2f * MathF.PI * cutoff / SampleRate);

        for (int i = 0; i < n; i++)
        {
            float s;
            if (i < nWaveSamples)
            {
                // The N: a linear ramp from +1 down through zero to −1. That shape is the whole
                // character — it is why a crack sounds like a tear and not like a click.
                float u = i / (float)nWaveSamples;
                s = 1f - 2f * u;
            }
            else
            {
                float t = (i - nWaveSamples) / (float)SampleRate;
                // Short decay relative to the buffer — a fifth of it, so the tail is over rather than
                // merely quiet by the end. A crack that trails off slowly stops sounding like a whip
                // and starts sounding like a firework, which is both wrong and much harder to place.
                s = (float)(rng.NextDouble() * 2.0 - 1.0) * MathF.Exp(-t / (tail * 0.18f)) * 0.5f;
            }
            lp += alpha * (s - lp);
            buf[i] = lp;
        }

        return Finish(buf, 0.9f);
    }

    /// <summary>
    /// The action: bolt, extractor, casing. Quiet, close, and the part that tells a listener a weapon
    /// was re-cocked rather than fired again — which in a game played by ear is information.
    /// </summary>
    public static float[] MechanicalAction(WeaponProfile w, int seed = 3)
    {
        int n = (int)(SampleRate * 0.16f);
        var buf = new float[n];
        var rng = new Random(seed);

        // Two metallic clicks a few tens of milliseconds apart, each a short burst through a high
        // resonance, plus a little ring.
        AddClick(buf, 0.000f, 1900f, 0.012f, 1.0f, rng);
        AddClick(buf, 0.038f, 3100f, 0.020f, 0.7f, rng);
        AddClick(buf, 0.085f, 1200f, 0.035f, 0.4f, rng);

        return Finish(buf, w.MechanicalLevel);
    }

    private static void AddClick(float[] buf, float atSeconds, float resonanceHz, float decay, float level, Random rng)
    {
        int start = (int)(atSeconds * SampleRate);
        float f = 2f * MathF.PI * resonanceHz / SampleRate;
        float bp1 = 0f, bp2 = 0f;
        for (int i = start; i < buf.Length; i++)
        {
            float t = (i - start) / (float)SampleRate;
            float env = MathF.Exp(-t / decay);
            if (env < 1e-4f) break;
            float input = (float)(rng.NextDouble() * 2.0 - 1.0) * env;
            bp1 += f * (input - bp1 - 0.08f * bp2);
            bp2 += f * bp1;
            buf[i] += bp2 * 3f * level;
        }
    }

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

    /// <summary>
    /// How a weapon's blast is built when there IS a real recording of it.
    ///
    /// The balance here was wrong and the recordings are why. When every take in the drop was clipped,
    /// limited flat and 34 dB down at 4 kHz, synthesis had to carry the sound and the recording was a
    /// thin veneer on top — synth at 1.0, recording at 0.45. With 96 kHz takes that are unclipped and
    /// unlimited, that is exactly backwards: the recording is the better sound and the synthesizer was
    /// drowning it, which is why replacing four of the five firing sounds changed almost nothing.
    ///
    /// Measuring the real thing also retired most of the reason for the synthesis. A 7.62 blast three
    /// metres from the muzzle decays TWENTY-SEVEN DECIBELS IN THIRTY MILLISECONDS — it is genuinely
    /// over by then, and what follows is the desert it was recorded in. My synthesized layer ran for
    /// a hundred and twenty to two hundred and forty milliseconds, so it was not reinforcing the blast,
    /// it was appending an invented tail to it. A tail is a room, and this engine builds its own.
    ///
    /// What is left for synthesis is one narrow job: the bottom octave. A handheld recorder's internal
    /// capsules roll off below about 80 Hz, and the measurement shows it — the real take sits 8.6 dB
    /// down at 35-80 Hz where the synthesized blast wave peaks. So a little of the Friedlander goes
    /// back underneath, matched to the recording's own length, to restore what the microphone could
    /// not hear. That is reinforcement. Everything else the recording does better.
    /// </summary>
    /// <param name="recorded">The ingested transient. Empty falls back to full synthesis.</param>
    /// <param name="subLevel">How much low-end reinforcement to add, 0 to 1.</param>
    public static float[] CompositeBlast(WeaponProfile w, float[] recorded, int seed = 1,
                                        float recordedBlend = 1f, float subLevel = SubReinforcementLevel)
    {
        // No recording of this weapon — synthesize the whole thing. A blend of zero means the same
        // thing said deliberately: the shotgun HAS a file, but it is twelve milliseconds of a
        // borrowed single-shot rifle take, and twelve milliseconds of the wrong gun is worse than a
        // synthesized one of the right gun.
        float blend = Math.Clamp(recordedBlend, 0f, 1f);
        if (recorded == null || recorded.Length == 0 || blend <= 0.001f) return MuzzleBlast(w, seed);

        var buf = new float[recorded.Length];
        for (int i = 0; i < recorded.Length; i++) buf[i] = recorded[i] * blend;

        // The bottom octave the microphone missed: one Friedlander excursion, no tail of its own, and
        // no longer than the blast it is sitting under.
        float sub = Math.Clamp(subLevel, 0f, 1f);
        if (sub > 0.001f)
        {
            float Tsub = 1f / (2f * MathF.PI * SubReinforcementHz);
            for (int i = 0; i < buf.Length; i++)
            {
                float t = i / (float)SampleRate;
                float u = t / Tsub;
                buf[i] += (1f - u) * MathF.Exp(-u) * sub;
            }
        }

        return Finish(buf, 0.97f);
    }

    /// <summary>
    /// How much low-frequency reinforcement goes under a recorded blast.
    ///
    /// Restoring what the recorder could not capture, not adding weight that was never there. A real
    /// 7.62 at three metres peaks in the 160-400 Hz band, not in the sub — the cinematic sub-bass boom
    /// is a convention rather than a measurement. Turn this up if a shot feels weightless on your
    /// system; past about 0.5 it stops sounding like the gun in the recording.
    /// </summary>
    public const float SubReinforcementLevel = 0.25f;
    /// <summary>The bottom octave a handheld recorder rolls off below, which the reinforcement restores.</summary>
    public const float SubReinforcementHz = 60f;


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
