using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Renders a <see cref="TransientSound"/>: four physical characters and a handful of numbers, no case
/// per object. Rendered at 1.0 peak, at the source; level, place and occlusion come afterwards from
/// the ordinary emitter path.
/// </summary>
public static class TransientSynth
{
    public const int SampleRate = 48000;

    /// <summary>The longest a render may be, whatever its decay claims: a stuck value would otherwise
    /// be a memory leak with a sound.</summary>
    private const float MaxSeconds = 3.0f;

    /// <summary>
    /// Renders one sound to mono float PCM. <paramref name="seed"/> travels with the event, so every
    /// client hears the same door, and two events from one door are not identical.
    /// </summary>
    public static float[] Render(TransientSound sound, int seed)
    {
        float seconds = Math.Clamp(sound.DecaySeconds, 0.005f, MaxSeconds);
        int samples = Math.Max(16, (int)(seconds * SampleRate));
        var buffer = new float[samples];
        var rng = new Random(seed);

        // A thing is never struck in quite the same place twice.
        float hz = MathF.Max(20f, sound.Hz * (0.97f + 0.06f * (float)rng.NextDouble()));

        switch (sound.Character)
        {
            case SoundCharacter.Ring: RenderRing(buffer, hz, seconds, sound.Noisiness, rng); break;
            case SoundCharacter.Hiss: RenderHiss(buffer, hz, rng); break;
            case SoundCharacter.Scrape: RenderScrape(buffer, hz, rng); break;
            default: RenderKnock(buffer, hz, seconds, sound.Noisiness, rng); break;
        }

        Normalize(buffer);
        return buffer;
    }

    /// <summary>A blow: a contact burst over up to five inharmonic modes, the high ones dying first.</summary>
    private static void RenderKnock(float[] buffer, float hz, float seconds, float noisiness, Random rng)
    {
        // Many modes, not one: a single struck resonance was heard as a cork, twice. See
        // docs/CLIENT_NOTES.md, "An impact is not one resonance".
        Span<float> ratios = stackalloc float[] { 1f, 1.71f, 2.63f, 4.07f, 6.2f };
        Span<float> gains = stackalloc float[] { 1f, 0.72f, 0.5f, 0.31f, 0.17f };

        float noise = Math.Clamp(noisiness, 0f, 1f);
        float q = 2.5f + 24f * (1f - noise);
        Span<Resonator> modes = stackalloc Resonator[5];
        int used = 0;
        for (int m = 0; m < 5; m++)
        {
            float f = hz * ratios[m];
            if (f > SampleRate * 0.45f) break;
            modes[m] = new Resonator(f, q);
            used++;
        }
        if (used == 0) { modes[0] = new Resonator(hz, q); used = 1; }

        float k = 6.9f / (seconds * SampleRate);          // 60 dB over the whole length
        // The contact: a broadband burst under 2 ms, what says two things touched.
        float contact = MathF.Max(1f, 0.0018f * SampleRate);

        // Only as bright as the thing is small: flat to 20 kHz, a car meeting a wall had a third of
        // its energy above 4 kHz.
        float bright = 1f - MathF.Exp(-2f * MathF.PI * MathF.Min(hz * 6f, SampleRate * 0.45f) / SampleRate);
        float lp = 0f;

        for (int i = 0; i < buffer.Length; i++)
        {
            float raw = i == 0
                ? 1f
                : (float)(rng.NextDouble() * 2.0 - 1.0) * MathF.Exp(-i / contact);
            lp += bright * (raw - lp);

            float v = 0f;
            for (int m = 0; m < used; m++)
            {
                // Higher modes radiate more readily: a struck thing gets duller as it dies.
                v += modes[m].Process(raw) * gains[m] * MathF.Exp(-k * i * (1f + m * 0.55f));
            }
            buffer[i] = v + lp * noise * 0.6f * MathF.Exp(-k * i * 3f);
        }
    }

    /// <summary>A decaying tone with three slightly inharmonic partials (two is a beat, four a chord;
    /// a plate's overtones are not neat multiples).</summary>
    private static void RenderRing(float[] buffer, float hz, float seconds, float noisiness, Random rng)
    {
        Span<float> partials = stackalloc float[] { 1f, 2.39f, 4.11f };
        Span<float> gains = stackalloc float[] { 1f, 0.42f, 0.18f };
        Span<float> phases = stackalloc float[3];
        for (int p = 0; p < 3; p++) phases[p] = (float)(rng.NextDouble() * Math.Tau);

        float k = 6.9f / (seconds * SampleRate);
        float twoPiOverSr = MathF.Tau / SampleRate;
        for (int i = 0; i < buffer.Length; i++)
        {
            float v = 0f;
            for (int p = 0; p < 3; p++)
            {
                v += gains[p] * MathF.Sin(phases[p]) * MathF.Exp(-k * i * (1f + p * 0.8f));
                phases[p] += twoPiOverSr * hz * partials[p];
            }
            if (noisiness > 0f) v += (float)(rng.NextDouble() * 2.0 - 1.0) * noisiness * MathF.Exp(-k * i * 8f);
            buffer[i] = v;
        }
    }

    /// <summary>Air moving: noise in a wide band around <paramref name="hz"/> (a third of it to eight
    /// times it), swelling in and dying away.</summary>
    private static void RenderHiss(float[] buffer, float hz, Random rng)
    {
        // A broad band, not a resonance, and no transient: one resonator with a sharp attack was heard
        // as "random banging" over six sessions. See docs/CLIENT_NOTES.md, "A breath is turbulence".
        int n = buffer.Length;
        float lowCut = MathF.Max(60f, hz / 3f);
        float highCut = MathF.Min(SampleRate * 0.45f, hz * 8f);
        float aHigh = 1f - MathF.Exp(-MathF.Tau * highCut / SampleRate);
        float aLow = 1f - MathF.Exp(-MathF.Tau * lowCut / SampleRate);
        float lp1 = 0f, lp2 = 0f, hp = 0f;

        // A third of its length rising on a raised cosine: no corner for the ear to hear as an onset.
        int attack = Math.Max(1, (int)(n * 0.35f));
        int fall = Math.Max(1, n - attack);

        for (int i = 0; i < n; i++)
        {
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            lp1 += aHigh * (noise - lp1);
            lp2 += aHigh * (lp1 - lp2);
            hp += aLow * (lp2 - hp);
            float band = lp2 - hp;

            float envelope = i < attack
                ? 0.5f * (1f - MathF.Cos(MathF.PI * i / attack))
                : MathF.Exp(-3f * (i - attack) / fall);
            buffer[i] = band * envelope;
        }
    }

    /// <summary>Stick-slip, as a tyre at its limit or a bow on a string. The slips are irregular: a
    /// periodic one is a buzzer.</summary>
    private static void RenderScrape(float[] buffer, float hz, Random rng)
    {
        var filter = new Resonator(hz, q: 14f);
        float slipsPerSecond = MathF.Max(8f, hz * 0.06f);
        float phase = 0f;
        int attack = Math.Max(1, buffer.Length / 20);
        int release = Math.Max(1, buffer.Length / 6);
        for (int i = 0; i < buffer.Length; i++)
        {
            phase += slipsPerSecond / SampleRate * (0.7f + 0.6f * (float)rng.NextDouble());
            bool slip = phase >= 1f;
            if (slip) phase -= 1f;

            float excite = slip ? (float)(rng.NextDouble() * 2.0 - 1.0)
                                : (float)(rng.NextDouble() * 2.0 - 1.0) * 0.12f;
            float envelope = i < attack ? i / (float)attack
                           : i > buffer.Length - release ? (buffer.Length - i) / (float)release
                           : 1f;
            buffer[i] = filter.Process(excite) * envelope;
        }
    }

    /// <summary>Scales the loudest sample to just under full scale; level is applied later.</summary>
    private static void Normalize(float[] buffer)
    {
        float peak = 0f;
        foreach (float v in buffer) peak = MathF.Max(peak, MathF.Abs(v));
        if (peak < 1e-6f) return;
        float gain = 0.98f / peak;
        for (int i = 0; i < buffer.Length; i++) buffer[i] *= gain;
    }

    /// <summary>16-bit mono, which is what FMOD wants handed to it in memory.</summary>
    public static byte[] ToPcm16(float[] buffer)
    {
        var bytes = new byte[buffer.Length * 2];
        for (int i = 0; i < buffer.Length; i++)
        {
            short s = (short)Math.Clamp(buffer[i] * 32767f, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>A two-pole resonant band-pass.</summary>
    private struct Resonator
    {
        private readonly float _a1, _a2, _gain;
        private float _z1, _z2;

        public Resonator(float hz, float q)
        {
            float r = MathF.Exp(-MathF.PI * MathF.Max(1f, hz / MathF.Max(0.2f, q)) / SampleRate);
            float theta = MathF.Tau * Math.Clamp(hz, 20f, SampleRate * 0.45f) / SampleRate;
            _a1 = 2f * r * MathF.Cos(theta);
            _a2 = -r * r;
            _gain = (1f - r) * MathF.Sqrt(1f - 2f * r * MathF.Cos(2f * theta) + r * r);
            _z1 = _z2 = 0f;
        }

        public float Process(float x)
        {
            float y = _gain * x + _a1 * _z1 + _a2 * _z2;
            _z2 = _z1;
            _z1 = y;
            return y;
        }
    }
}
