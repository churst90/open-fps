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
    float MechanicalLevel,
    float BrightnessFloorFraction = 0.10f)  // how far the brightness sweep falls
{
    /// <summary>A 5.56 mm-ish service rifle: fast, bright, hard crack, modest body.</summary>
    public static WeaponProfile Rifle => new(
        Name: "rifle",
        MuzzleVelocity: 880f,
        BlastLowHz: 320f,
        BlastDecaySeconds: 0.026f,
        ThumpHz: 78f,
        ThumpDecaySeconds: 0.14f,
        BrightnessHz: 7200f,
        MechanicalDelaySeconds: 0.045f,
        MechanicalLevel: 0.22f);

    /// <summary>A 12 gauge: the biggest body on the list and the lowest thump. A shotgun is mostly
    /// low-frequency energy, which is why it is the one weapon you feel through a wall.</summary>
    public static WeaponProfile Shotgun => new(
        Name: "shotgun",
        MuzzleVelocity: 400f,
        BlastLowHz: 210f,
        BlastDecaySeconds: 0.036f,
        ThumpHz: 58f,
        ThumpDecaySeconds: 0.20f,
        BrightnessHz: 4800f,
        MechanicalDelaySeconds: 0.0f,   // a pump gun does nothing on its own
        MechanicalLevel: 0.0f);

    /// <summary>
    /// The profile for a weapon, taken from the weapon.
    ///
    /// This replaced a lookup by name, which was two sources of truth wearing one name and behaved
    /// exactly as that arrangement always does: the Glock's definition said 375 m/s and the profile it
    /// named said 340, so a crack was scheduled and silence was rendered into it — and because five
    /// weapons shared three profiles, the Glock and the .45 came out identical. There is now one
    /// place a weapon's sound is described, and it is the weapon.
    /// </summary>
    public static WeaponProfile From(WeaponDefinition w) => new(
        Name: w.Id,
        MuzzleVelocity: w.MuzzleVelocity,
        BlastLowHz: w.BlastLowHz,
        BlastDecaySeconds: w.BlastDecaySeconds,
        ThumpHz: w.ThumpHz,
        ThumpDecaySeconds: w.ThumpDecaySeconds,
        BrightnessHz: w.BrightnessHz,
        MechanicalDelaySeconds: w.MechanicalDelaySeconds,
        MechanicalLevel: w.MechanicalLevel,
        BrightnessFloorFraction: w.BrightnessFloorFraction);

    /// <summary>A 9 mm handgun: subsonic-ish, smaller body, no crack worth rendering.</summary>
    public static WeaponProfile Pistol => new(
        Name: "pistol",
        MuzzleVelocity: 340f,
        BlastLowHz: 430f,
        BlastDecaySeconds: 0.019f,
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
    /// The muzzle blast: a blast wave, not a drum.
    ///
    /// The first version of this was built from a resonant band-pass and a sine, and it sounded like
    /// someone hitting a bathtub — because that is what those two things are. A high-Q resonator
    /// ringing at 300 Hz for sixty milliseconds IS a box, and a decaying 68 Hz sine IS a tonal boom.
    /// Neither is what a gun does. A muzzle blast has no resonant frequency and no oscillation in it
    /// at all; it is a pressure discontinuity, and it is over very quickly.
    ///
    /// So the low end here is a FRIEDLANDER WAVE — p(t) = P(1 − t/T)·e^(−t/T) — which is the standard
    /// model of a blast: an instantaneous jump to peak overpressure, a fall through zero into a
    /// shallow negative phase, and back. One excursion, not a cycle, which is why it gives weight
    /// without pitch. T sets where its energy sits, and that is the one number that separates a 12
    /// gauge from a 9 mm at the bottom end.
    ///
    /// Over it sits broadband noise through a low-pass whose cutoff FALLS as the sound decays. That
    /// sweep is most of what makes it read as an explosion rather than a burst of static: a real blast
    /// loses its top end within milliseconds as the shock front degrades, so the sound opens bright
    /// and closes dark. A fixed filter cannot do that and sounds synthetic no matter how it is tuned.
    ///
    /// And it is SHORT — around a tenth of a second. That is not a compromise either. Everything
    /// after the first hundred milliseconds of a gunshot you have ever heard was the place you heard
    /// it in, and this engine builds that itself. A synthesized tail is a second room competing with
    /// the real one, which is the same mistake as shipping a recording with its field still attached.
    /// </summary>
    public static float[] MuzzleBlast(WeaponProfile w, int seed = 1)
    {
        // The blast wave's time constant, from the thump fundamental. Longer than the textbook
        // overpressure duration on purpose: at the muzzle a real blast wave is a millisecond or two,
        // but what a listener twenty metres away experiences as the PUNCH of a gunshot is the larger,
        // slower pressure disturbance behind it. Render only the fast part and the shot is accurate
        // and weightless.
        // A Friedlander wave of time constant T puts its energy around 1/(2*pi*T), NOT around 1/T.
        // Getting that wrong by the factor of 2*pi is what made the first attempt at this thin: with
        // T taken as 1/(1.2*ThumpHz) the AKM's sub-bass landed near THIRTEEN HERTZ — inaudible, and
        // promptly removed by the DC-blocking high-pass in Finish. Two per cent of the rendered energy
        // was below 200 Hz. The synthesis was generating the weight and then throwing it away.
        float fastHz = MathF.Max(20f, w.ThumpHz) * 1.6f;
        float T = 1f / (2f * MathF.PI * fastHz);

        // And a second, slower blast wave under the first — three times the time constant, so its
        // energy sits an octave and a half lower. This is the part you feel rather than hear, and it
        // is why a shotgun reads as bigger than a rifle even when the rifle measures louder. It is
        // safe to lean on now in a way the old model was not: a Friedlander wave has no resonant
        // frequency, so adding low end adds WEIGHT, where adding it to a tuned band-pass added BOOM.
        float subHz = MathF.Max(16f, w.ThumpHz * 0.7f);
        float Tsub = 1f / (2f * MathF.PI * subHz);

        // SIX time constants of the noise, not four. This file already carried a comment saying so and
        // I overrode it: at four the layer is still at 1.8% when the buffer runs out, which is a step
        // in the waveform at the end of every shot that an 8 ms fade can only paper over. The way to
        // keep a blast short is to decay it faster, not to cut it off while it is still sounding.
        float length = MathF.Max(Tsub * 8f, w.BlastDecaySeconds * 6f) + 0.012f;
        int n = (int)(SampleRate * length);
        var buf = new float[n];
        var rng = new Random(seed);

        // Where the noise starts and where it ends up. The sweep is the effect; the endpoints are
        // taste. Starting at the profile's brightness and falling to a tenth of it covers about three
        // and a half octaves, which is roughly what a shock front loses as it decays.
        float startCut = w.BrightnessHz;
        float endCut = MathF.Max(180f, w.BrightnessHz * Math.Clamp(w.BrightnessFloorFraction, 0.02f, 0.9f));

        // A gentle high-pass keeps the noise layer out of the blast wave's way, so the two stack
        // rather than fight. Its corner is the body centre — the profile's one remaining use for it.
        float hpAlpha = 1f - MathF.Exp(-2f * MathF.PI * (w.BlastLowHz * 0.5f) / SampleRate);
        float hpState = 0f;
        float lp = 0f;

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;

            // 1. The blast wave, twice: a fast one for the punch and a slow one for the weight.
            float u = t / T;
            float friedlander = (1f - u) * MathF.Exp(-u);
            float us = t / Tsub;
            float sub = (1f - us) * MathF.Exp(-us);

            // 2. Broadband noise, decaying fast, through a closing low-pass.
            float noiseEnv = MathF.Exp(-t / MathF.Max(0.004f, w.BlastDecaySeconds));
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * noiseEnv;

            // Cutoff falls geometrically, which is how it is heard — as octaves, not as hertz.
            float sweep = MathF.Exp(-t / MathF.Max(0.004f, w.BlastDecaySeconds * 0.8f));
            float cutoff = endCut + (startCut - endCut) * sweep;
            float alpha = 1f - MathF.Exp(-2f * MathF.PI * cutoff / SampleRate);
            lp += alpha * (noise - lp);

            // High-pass the noise so it sits above the blast wave rather than doubling its bottom.
            hpState += hpAlpha * (lp - hpState);
            float shaped = lp - hpState;

            // 3. The very first instant: a hard broadband edge, gone in a millisecond or two. This is
            //    what says "gun" rather than "explosion in the distance".
            float spike = (float)(rng.NextDouble() * 2.0 - 1.0) * MathF.Exp(-t / 0.0012f);

            float sample = friedlander * BlastWaveLevel
                         + sub * SubWeightLevel
                         + shaped * NoiseLayerLevel
                         + spike * 0.5f;

            // Soft clip, driven hard. This is doing real work rather than protecting against overflow:
            // saturation is what lets the low end stay LOUD without simply owning the peak. Peak
            // normalisation happens afterwards, so anything that merely raises the peak makes
            // everything else quieter — the way to make a shot feel bigger is to make more of it sit
            // near the top, which is what saturating it does. It is also what a microphone and an ear
            // both do to something this loud.
            buf[i] = MathF.Tanh(sample * SaturationDrive);
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
            float subHz = MathF.Max(16f, w.ThumpHz * 0.7f);
            float Tsub = 1f / (2f * MathF.PI * subHz);
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

    /// <summary>Weight of the fast Friedlander blast wave — the punch.</summary>
    public const float BlastWaveLevel = 1.45f;
    /// <summary>Weight of the slow blast wave underneath it — the part you feel in your chest. This is
    /// the knob for "I should be able to feel a gun fire".</summary>
    public const float SubWeightLevel = 1.10f;
    /// <summary>How hard the blast is driven into the soft clipper. Higher is denser and louder-feeling
    /// at the same peak; far too high and it stops sounding like air moving.</summary>
    public const float SaturationDrive = 2.1f;
    /// <summary>Weight of the swept broadband noise — the part that sounds like a gunshot rather than
    /// like a drum. This is where the character is.</summary>
    public const float NoiseLayerLevel = 1.9f;

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
