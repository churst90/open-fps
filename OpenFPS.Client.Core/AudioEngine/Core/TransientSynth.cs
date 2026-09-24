using System;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Renders a <see cref="TransientSound"/> — four short DSPs, and between them every noise in the
/// game that happens and stops.
///
/// There is no case here for doors, or for glass, or for gunfire. There are four physical characters
/// and a handful of numbers, which is the whole reason for describing sounds that way: adding a ball
/// that bounces, or a bucket somebody kicks over, needs nothing in this file at all. A sample library
/// would need a recording of each, at each size, of each material, struck at each force.
///
/// Everything is rendered at a level of 1.0 peak and positioned, attenuated and occluded afterwards
/// by the ordinary emitter path — so what comes out of here is the sound AT THE SOURCE, and how loud
/// it ends up is somebody else's business. That split is what stops a distant door needing its own
/// quiet recording.
/// </summary>
public static class TransientSynth
{
    public const int SampleRate = 48000;

    /// <summary>Nothing is allowed to be longer than this, however long its decay claims to be. A
    /// stuck value producing a minute-long buffer would be a memory leak with a sound.</summary>
    private const float MaxSeconds = 3.0f;

    /// <summary>
    /// Renders one sound to mono float PCM.
    ///
    /// <paramref name="seed"/> travels with the event, so two clients hearing the same door hear the
    /// same door — and two events from the same door do NOT sound identical, which is what keeps
    /// repeated events from reading as a recording being replayed.
    /// </summary>
    public static float[] Render(TransientSound sound, int seed)
    {
        float seconds = Math.Clamp(sound.DecaySeconds, 0.005f, MaxSeconds);
        int samples = Math.Max(16, (int)(seconds * SampleRate));
        var buffer = new float[samples];
        var rng = new Random(seed);

        // A little variation on the note itself. Real repeated events differ slightly because the
        // thing is never struck in quite the same place twice.
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

    /// <summary>
    /// A blow. Noise through a resonance, over almost before it starts.
    ///
    /// The resonance is what stops every impact in the game sounding like the same click: a latch is
    /// a small hard thing and sits high, a car door closing on its frame is large and sits low, and
    /// both are this function with a different number.
    /// </summary>
    private static void RenderKnock(float[] buffer, float hz, float seconds, float noisiness, Random rng)
    {
        // AN IMPACT IS NOT ONE RESONANCE.
        //
        // A single pole struck by an impulse is a cork coming out of a bottle — and that is what
        // listeners called this, twice, about two completely different sounds: a door shutting and a
        // car hitting a wall. Both were one resonance, so both were a cork.
        //
        // A struck object answers on MANY modes at once. They are inharmonically spaced, because a
        // plate or a panel or a car wing is not a string, and the high ones die first because they
        // radiate faster. That spread is the whole difference between "something was struck" and "a
        // note was played", and no amount of moving the one note around will produce it.
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
        // The contact itself: a broadband burst a millisecond or two long, under everything else.
        // This is the part that says two things TOUCHED, as opposed to one thing rang.
        float contact = MathF.Max(1f, 0.0018f * SampleRate);

        // ...and it has to be as bright as the thing is SMALL. Unfiltered, the burst is flat to
        // twenty kilohertz, so a fifteen-hundred-kilo car meeting a wall came out with a third of
        // its energy above 4 kHz — the sound of a tiny hard tap laid over a low crunch. Tying the
        // cutoff to the sound's own pitch keeps a latch bright and lets a crash be the low,
        // gravelly thing it is.
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
                // Higher modes decay faster: they radiate more readily, which is why a struck thing
                // gets duller as it dies rather than simply quieter.
                v += modes[m].Process(raw) * gains[m] * MathF.Exp(-k * i * (1f + m * 0.55f));
            }
            buffer[i] = v + lp * noise * 0.6f * MathF.Exp(-k * i * 3f);
        }
    }

    /// <summary>
    /// Something still moving after it was hit: a decaying tone, with its own overtones.
    ///
    /// Three partials, because two is a beat and four is a chord. Slightly inharmonic, because a
    /// plate is not a string — its overtones do not fall on neat multiples, and that inharmonicity is
    /// most of the difference between a struck panel and a synthesiser pretending to be one.
    /// </summary>
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
                // Higher partials die first, as they do on anything real.
                v += gains[p] * MathF.Sin(phases[p]) * MathF.Exp(-k * i * (1f + p * 0.8f));
                phases[p] += twoPiOverSr * hz * partials[p];
            }
            if (noisiness > 0f) v += (float)(rng.NextDouble() * 2.0 - 1.0) * noisiness * MathF.Exp(-k * i * 8f);
            buffer[i] = v;
        }
    }

    /// <summary>Air moving: band-limited noise with a soft edge on both ends, so it arrives and
    /// leaves rather than switching on.</summary>
    private static void RenderHiss(float[] buffer, float hz, Random rng)
    {
        // ── TURBULENCE IS NOT A RESONANCE, AND A BREATH HAS NO TRANSIENT ────────────────────────
        //
        // This was noise through ONE resonator at Q 0.9, with an attack a twelfth of its own length.
        // Both are wrong and both are audible. A resonance that narrow puts everything in one band:
        // measured, an exhale came out peaking at 250-1000 Hz with 2-4 kHz twenty-three decibels down
        // and 4-8 kHz thirty-two down — and the top is where a breath LIVES, it is the whole of what
        // makes "hhh" sound like air rather than like a thud. And a sharp attack on a 300 ms noise
        // burst is a transient, which is the difference between a breath and a soft bang.
        //
        // It was reported as exactly that, over six sessions: "random banging... it is 2 different
        // bangs so it makes me think it's breathing in and out... I don't hear the breathing either".
        // Two bangs, because an inhale and an exhale are different; no breathing, because neither of
        // them sounded like one.
        //
        // What turbulence actually is: a broad band, not a peak. Air tearing past a narrow opening
        // radiates over decades, rolling off gently either side of a centre set by the aperture and
        // the flow. So the noise is shaped by a wide band — two poles down at the top, one up at the
        // bottom — around `hz` rather than resonated at it, which leaves the 2-6 kHz that carries the
        // character. An inhale is drawn through a narrower opening, so its `hz` is higher and the
        // whole band moves up with it: brighter, by the same arithmetic, with nothing added.
        int n = buffer.Length;
        float lowCut = MathF.Max(60f, hz / 3f);
        float highCut = MathF.Min(SampleRate * 0.45f, hz * 8f);
        float aHigh = 1f - MathF.Exp(-MathF.Tau * highCut / SampleRate);
        float aLow = 1f - MathF.Exp(-MathF.Tau * lowCut / SampleRate);
        float lp1 = 0f, lp2 = 0f, hp = 0f;

        // ...and it SWELLS. A breath is a flow that starts, peaks and stops — a third of its length
        // rising, on a raised cosine so there is no corner anywhere in it for an ear to hear as an
        // onset. That single change is most of the difference between air and a knock.
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

    /// <summary>
    /// Stick-slip: something gripping, letting go, and gripping again, many times a second.
    ///
    /// The same process as a tyre at its limit and a bow on a string. What makes it a groan rather
    /// than a tone is that the slips are irregular — a perfectly periodic one is a buzzer.
    /// </summary>
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

    /// <summary>Scales a buffer so its loudest sample is just under full scale. Level is applied
    /// later, by the thing that knows how far away the listener is.</summary>
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

    /// <summary>A two-pole resonant band-pass. The one filter all four characters are built on.</summary>
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
