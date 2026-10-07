using System.Numerics;
using static OpenFPS.Common.DesignedSoundKit;

namespace OpenFPS.Common;

/// <summary>
/// The teleporter's four sounds, reasoned from what the device would physically do.
///
/// - Charging: a photoflash-style charger whose transformer sings at its switching frequency, which
///   climbs as the capacitor charges: 2 to 10 kHz over two seconds, as the square root of time.
/// - Leaving: about 75 litres of air (a body) rush into where the body was. A monopole's far field is
///   the rate of change of its flow: a small dip while the inflow speeds up, then a sharp compression
///   when it meets and stops, a low thump.
/// - Arriving: the same air shoved away, time reversed: a pop, then a small dip; brighter, because
///   the shove is faster than the inrush.
/// - Ready: two soft notes rising a fourth.
///
/// A body vanishing instantly would leave a vacuum that collapses in about a millisecond, a blast near
/// 170 dB. The teleporter is taken to fade the body over tens of milliseconds; the levels below are set
/// for the game, not computed.
/// </summary>
public static class TeleporterSounds
{
    public const string Prefix = "teleporter:";
    public const string Charge = "charge", Leave = "leave", Arrive = "arrive", Ready = "ready";

    /// <summary>The events, as their synth keys: "teleporter:charge" and so on.</summary>
    public static string Key(string kind) => Prefix + kind;

    public static bool TryParseKey(string? key, out string kind)
    {
        kind = "";
        if (string.IsNullOrEmpty(key) || !key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        kind = key[Prefix.Length..];
        return kind is Charge or Leave or Arrive or Ready;
    }

    /// <summary>How long the charge takes, seconds.</summary>
    public const float ChargeSeconds = 2f;

    /// <summary>Peak dB SPL at a metre. A flash charger is heard across a quiet room; the thump and the
    /// pop are as loud as a door shut hard and a hand clap.</summary>
    public static float LevelDb(string kind) => kind switch
    {
        Charge => 62f,
        Leave => 92f,
        Arrive => 96f,
        _ => 56f,
    };

    public static float Seconds(string kind) => kind switch
    {
        Charge => ChargeSeconds + 0.05f,
        Leave => 0.45f,
        Arrive => 0.35f,
        _ => 0.35f,
    };

    /// <summary>One of the four as a world sound at <paramref name="at"/>. The charge and the ready tone
    /// are on the body that carries the device (<see cref="TransientSound.OnBody"/>, at the hip), so they
    /// follow it; the leave and the arrive are where the body was and where it appears.</summary>
    public static TransientSound Sound(string kind, Vector3 at)
    {
        bool carried = kind is Charge or Ready;
        return new TransientSound
        {
            Character = SoundCharacter.Knock,
            Position = at + (carried ? new Vector3(0f, 1.0f, 0f) : new Vector3(0f, 0.9f, 0f)),
            OnBody = carried,
            BodyOffset = new Vector3(0.2f, 1.0f, 0.1f),
            LevelDb = LevelDb(kind),
            SynthKey = Key(kind),
            DecaySeconds = Seconds(kind) + 0.1f,
            Noisiness = kind is Leave or Arrive ? 1f : 0f,
            Hz = kind == Charge ? 6000f : 1000f,
            ExtentMetres = kind is Leave or Arrive ? 0.5f : 0f,
        };
    }

    public static float[] Render(string kind, int sr, int seed) => kind switch
    {
        Charge => RenderCharge(sr, seed),
        Leave => RenderLeave(sr, seed),
        Arrive => RenderArrive(sr, seed),
        _ => RenderReady(sr),
    };

    /// <summary>The charger's whine, from 2 to 10 kHz as the square root of time, with its second
    /// harmonic where it fits, a little jitter in the switching, and a faint 120 Hz buzz.</summary>
    public static float[] RenderCharge(int sr, int seed)
    {
        var rng = new Random(seed);
        float len = Seconds(Charge);
        var y = Buffer(len, sr);
        double phase = 0, humPhase = 0;
        float jitter = 0f;
        for (int i = 0; i < y.Length; i++)
        {
            float t = i / (float)sr;
            float x = Math.Clamp(t / ChargeSeconds, 0f, 1f);
            if (i % 96 == 0) jitter = 0.004f * White(rng);
            float hz = (2000f + 8000f * MathF.Sqrt(x)) * (1f + jitter);
            phase += hz / sr;
            humPhase += 120.0 / sr;
            double p = 2 * Math.PI * phase;
            float tone = (float)Math.Sin(p);
            if (2f * hz < 0.45f * sr) tone += 0.25f * (float)Math.Sin(2 * p);
            float hum = (float)(Math.Sin(2 * Math.PI * humPhase) + 0.5 * Math.Sin(4 * Math.PI * humPhase) + 0.25 * Math.Sin(6 * Math.PI * humPhase));
            // A little louder as it charges, and stopping when it is done.
            float env = MathF.Min(1f, t / 0.03f) * (0.6f + 0.4f * x) * (t > ChargeSeconds ? MathF.Max(0f, 1f - (t - ChargeSeconds) / 0.03f) : 1f);
            y[i] += env * (0.6f * tone + 0.05f * hum);
        }
        return Finish(y, sr, fadeOutMs: 5f);
    }

    /// <summary>The inrush: a faint rush of air quickening for <paramref name="fillSeconds"/>, a
    /// shallow dip in pressure while it does, and the thump when it meets and stops.</summary>
    public static float[] RenderLeave(int sr, int seed, float fillSeconds = 0.06f)
    {
        var rng = new Random(seed);
        var y = Buffer(Seconds(Leave), sr);
        int start = (int)(0.01f * sr), fill = (int)(fillSeconds * sr);
        // The flow into the hole grows at a constant rate (a constant dip) and then stops all at once:
        // a compression whose width is how long the meeting takes, about three milliseconds.
        float dip = -0.08f;
        for (int i = 0; i < fill && start + i < y.Length; i++) y[start + i] += dip * MathF.Sin(MathF.PI * MathF.Min(1f, i / (0.15f * fill)) * 0.5f);
        int meet = start + fill;
        AddPulse(y, sr, meet / (float)sr, 0.003f, 1f);
        AddRing(y, sr, meet / (float)sr + 0.0015f, 55f, 0.35f, 0.25f, 0.002f);
        AddRing(y, sr, meet / (float)sr + 0.0015f, 110f, 0.12f, 0.12f, 0.002f);
        // The rush: turbulence in the inflow, louder as it goes faster.
        var bp = Biquad.BandPass(450f, 0.8f, sr);
        for (int i = 0; i < fill && start + i < y.Length; i++)
        {
            float x = i / (float)fill;
            y[start + i] += 0.12f * x * x * bp.Run(White(rng));
        }
        return Finish(y, sr, fadeInMs: 2f, fadeOutMs: 40f);
    }

    /// <summary>The shove: a compression all at once (a pop, a millisecond wide), the outflow slowing
    /// with a shallow dip, and the air it pushed rushing away, brighter than the inrush.</summary>
    public static float[] RenderArrive(int sr, int seed, float pushSeconds = 0.04f)
    {
        var rng = new Random(seed);
        var y = Buffer(Seconds(Arrive), sr);
        float at = 0.005f;
        AddPulse(y, sr, at, 0.0012f, 1f);
        int start = (int)((at + 0.0015f) * sr), push = (int)(pushSeconds * sr);
        for (int i = 0; i < push && start + i < y.Length; i++)
            y[start + i] += -0.12f * MathF.Sin(MathF.PI * i / push);
        AddRing(y, sr, at, 95f, 0.25f, 0.12f, 0.001f);
        var bp = Biquad.BandPass(1400f, 0.7f, sr);
        for (int i = 0; i < (int)(0.12f * sr) && start + i < y.Length; i++)
        {
            float t = i / (float)sr;
            y[start + i] += 0.25f * MathF.Exp(-t / 0.03f) * bp.Run(White(rng));
        }
        return Finish(y, sr, fadeInMs: 0.2f, fadeOutMs: 30f);
    }

    /// <summary>Ready: two soft notes, E6 and A6, each a sine with a little of its octave.</summary>
    public static float[] RenderReady(int sr)
    {
        var y = Buffer(Seconds(Ready), sr);
        void Note(float at, float hz, float length, float amp)
        {
            int a = (int)(at * sr), n = (int)(length * sr);
            for (int i = 0; i < n && a + i < y.Length; i++)
            {
                float t = i / (float)sr;
                float env = MathF.Min(1f, t / 0.006f) * MathF.Exp(-t / (length * 0.35f));
                y[a + i] += amp * env * (MathF.Sin(2f * MathF.PI * hz * t) + 0.15f * MathF.Sin(4f * MathF.PI * hz * t));
            }
        }
        Note(0.005f, 1318.51f, 0.12f, 0.7f);
        Note(0.10f, 1760f, 0.24f, 0.8f);
        return Finish(y, sr, fadeOutMs: 15f);
    }

    /// <summary>A raised-cosine pressure pulse <paramref name="width"/> seconds wide centred at
    /// <paramref name="at"/>.</summary>
    private static void AddPulse(float[] y, int sr, float at, float width, float amplitude)
    {
        int c = (int)(at * sr), h = Math.Max(1, (int)(width * sr * 0.5f));
        for (int i = -h; i <= h; i++)
        {
            int k = c + i;
            if (k < 0 || k >= y.Length) continue;
            y[k] += amplitude * 0.5f * (1f + MathF.Cos(MathF.PI * i / h));
        }
    }
}
