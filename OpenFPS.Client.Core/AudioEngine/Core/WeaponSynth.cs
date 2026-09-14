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
    float BlastLowHz,            // centre of the body resonance: the "size" of the gun
    float BlastDecaySeconds,     // how long the blast body rings
    float ThumpHz,               // the chest-punch fundamental
    float ThumpDecaySeconds,
    float BrightnessHz,          // low-pass on the blast: higher is sharper and more cracking
    float MechanicalDelaySeconds,// when the action is heard after the shot
    float MechanicalLevel)
{
    /// <summary>A 5.56 mm-ish service rifle: fast, bright, hard crack, modest body.</summary>
    public static WeaponProfile Rifle => new(
        Name: "rifle",
        MuzzleVelocity: 880f,
        BlastLowHz: 320f,
        BlastDecaySeconds: 0.055f,
        ThumpHz: 78f,
        ThumpDecaySeconds: 0.14f,
        BrightnessHz: 7200f,
        MechanicalDelaySeconds: 0.045f,
        MechanicalLevel: 0.22f);

    /// <summary>A 9 mm handgun: subsonic-ish, smaller body, no crack worth rendering.</summary>
    public static WeaponProfile Pistol => new(
        Name: "pistol",
        MuzzleVelocity: 340f,
        BlastLowHz: 430f,
        BlastDecaySeconds: 0.040f,
        ThumpHz: 95f,
        ThumpDecaySeconds: 0.09f,
        BrightnessHz: 5200f,
        MechanicalDelaySeconds: 0.055f,
        MechanicalLevel: 0.30f);
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
    /// The muzzle blast: a pressure transient with a body and a thump under it.
    ///
    /// Three layers because a gunshot is three things at once. The transient is what tells you a gun
    /// went off rather than a door slamming; the body is what tells you which gun; the thump is what
    /// makes it feel like it happened near you rather than on a television.
    /// </summary>
    public static float[] MuzzleBlast(WeaponProfile w, int seed = 1)
    {
        // Six time constants, not four: at four the thump is still at 1.8% when the buffer runs out,
        // which the fade-out then has to paper over instead of simply being past.
        float length = MathF.Max(w.BlastDecaySeconds, w.ThumpDecaySeconds) * 6f + 0.02f;
        int n = (int)(SampleRate * length);
        var buf = new float[n];
        var rng = new Random(seed);

        // Band-pass state for the body, and a one-pole low-pass for the overall brightness.
        float bp1 = 0f, bp2 = 0f, lp = 0f;
        float bodyQ = 0.22f;
        float bodyF = 2f * MathF.PI * w.BlastLowHz / SampleRate;
        float brightAlpha = 1f - MathF.Exp(-2f * MathF.PI * w.BrightnessHz / SampleRate);

        double thumpPhase = 0;
        float thumpStep = 2f * MathF.PI * w.ThumpHz / SampleRate;

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);

            // 1. Transient. Near-instant attack and a very fast decay — a millisecond or two. This is
            //    the part a recording captures well and a synthesizer gets right for free.
            float transient = noise * MathF.Exp(-t / 0.0018f);

            // 2. Body: the same noise through a resonant band-pass, decaying over tens of milliseconds.
            //    The resonant frequency is most of what reads as calibre.
            float input = noise * MathF.Exp(-t / w.BlastDecaySeconds);
            bp1 += bodyF * (input - bp1 - bodyQ * bp2);
            bp2 += bodyF * bp1;
            float body = bp2 * 3.2f;

            // 3. Thump: a low sine that falls in pitch slightly as it decays, which is what a real
            //    expanding pressure front does and what stops it sounding like a test tone.
            float thumpEnv = MathF.Exp(-t / w.ThumpDecaySeconds);
            thumpPhase += thumpStep * (0.75f + 0.25f * thumpEnv);
            float thump = (float)Math.Sin(thumpPhase) * thumpEnv * 0.85f;

            float s = transient * 0.9f + body + thump;

            // Overall brightness, and a soft clip so the transient saturates rather than splitting.
            lp += brightAlpha * (s - lp);
            buf[i] = MathF.Tanh(lp * 1.6f);
        }

        return Finish(buf, 0.95f);
    }

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
