using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --scope-sounds [out=DIR]: the scope's guidance loops (ScopeSounds) played as the mixer plays them,
/// resampled at the rate closeness asks for, 20 ms fades, at the game's gain (interface 0.5 times the
/// scope's 0.7): each state, an approach onto a body, the breath held and let go; and the M700's report,
/// bolt, reload and dry fire, peak-normalised. Each file's peak, RMS and largest sample step against
/// the tone's own, which is where a click shows.
/// </summary>
public static class ScopeSoundsSpike
{
    private static readonly int Sr = ScopeSounds.SampleRate;
    private const float GameGain = 0.5f * 0.7f;

    public static int Run(string[] args)
    {
        string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..]
                     ?? LabPaths.InRepo("inbox", "scope-2026-10-04");
        Directory.CreateDirectory(dir);
        var pulse = ScopeSounds.RenderPulse();
        var steady = ScopeSounds.RenderSteady();

        var files = new List<(string Name, float[] Pcm, int Rate, string What)>
        {
            ("guidance-1-far.wav", Play(pulse, _ => ScopeSounds.PulseRate(0f), 3f), Sr, "pulsing, a target at the edge of the view"),
            ("guidance-2-half.wav", Play(pulse, _ => ScopeSounds.PulseRate(0.5f), 3f), Sr, "pulsing, half way in"),
            ("guidance-3-near.wav", Play(pulse, _ => ScopeSounds.PulseRate(0.9f), 3f), Sr, "pulsing, almost on"),
            ("guidance-4-on.wav", Play(steady, _ => 1f, 3f), Sr, "the held note, on a body"),
            ("guidance-5-approach.wav", Approach(pulse, steady), Sr, "edge of the view to on the body over 5 s, then held 1.5 s, then off it"),
            ("breath-in.wav", Gain(ScopeSounds.RenderBreath(true)), Sr, "numpad 0 pressed"),
            ("breath-out.wav", Gain(ScopeSounds.RenderBreath(false)), Sr, "numpad 0 let go"),
            ("breath-hold.wav", Hold(), Sr, "in, five seconds held, out"),
        };

        var m700 = WeaponRegistry.M700;
        int tsr = TransientSynth.SampleRate;
        files.Add(("m700-report.wav", Normalise(WeaponSynth.MuzzleBlast(WeaponProfile.From(m700), 1)), WeaponSynth.SampleRate, "the report at the muzzle"));
        files.Add(("m700-bolt-cycle.wav", Normalise(WeaponHandling.Render(new HandlingSpec("m700", false, 0, false, IsCycle: true), tsr, 1)), tsr, "the bolt worked after a shot"));
        files.Add(("m700-reload-5.wav", Normalise(WeaponHandling.Render(new HandlingSpec("m700", true, 5, true), tsr, 1)), tsr, "five rounds pressed into the box"));
        files.Add(("m700-dryfire.wav", Normalise(WeaponHandling.Render(new HandlingSpec("m700", false, 0, false), tsr, 1)), tsr, "the striker on an empty chamber"));

        // The largest step a clean tone makes at the scope's level: the held note's own, at the game's gain.
        float toneStep = MaxStep(Gain(steady));
        Console.WriteLine($"Writing to {dir}\n");
        Console.WriteLine($"  {"file",-26} {"secs",5} {"peak dBFS",9} {"RMS dBFS",9} {"max step",9}  what");
        foreach (var (name, pcm, rate, what) in files)
        {
            WriteWav(Path.Combine(dir, name), pcm, rate);
            float peak = pcm.Max(MathF.Abs);
            double rms = Math.Sqrt(pcm.Sum(v => (double)v * v) / pcm.Length);
            float step = MaxStep(pcm);
            Console.WriteLine($"  {name,-26} {pcm.Length / (float)rate,5:F2} {Db(peak),9:F1} {Db((float)rms),9:F1} {step / MathF.Max(1e-9f, peak),9:F3}  {what}");
        }
        Console.WriteLine($"\n  The held note's own largest step at game level is {toneStep:F4} ({toneStep / Gain(steady).Max(MathF.Abs):F3} of its peak).");
        return 0;
    }

    private static float Db(float x) => 20f * MathF.Log10(MathF.Max(1e-9f, x));

    private static float MaxStep(float[] x)
    {
        float m = 0f;
        for (int i = 1; i < x.Length; i++) m = MathF.Max(m, MathF.Abs(x[i] - x[i - 1]));
        return m;
    }

    private static float[] Gain(float[] x) => x.Select(v => v * GameGain).ToArray();

    private static float[] Normalise(float[] x)
    {
        float peak = x.Max(MathF.Abs);
        float k = MathF.Pow(10f, -1f / 20f) / MathF.Max(1e-9f, peak);   // -1 dBFS
        return x.Select(v => v * k).ToArray();
    }

    /// <summary>A loop played at a rate that can change as it plays, faded in and out over 20 ms, at
    /// the game's gain: what the mixer does with a looping channel and setPitch.</summary>
    private static float[] Play(float[] loop, Func<float, float> rateAt, float seconds)
    {
        int n = (int)(seconds * Sr);
        var y = new float[n];
        double pos = 0;
        int fade = Sr / 50;
        for (int i = 0; i < n; i++)
        {
            int a = (int)pos % loop.Length, b = (a + 1) % loop.Length;
            float f = (float)(pos - Math.Floor(pos));
            float env = MathF.Min(1f, MathF.Min(i / (float)fade, (n - 1 - i) / (float)fade));
            y[i] = (loop[a] + f * (loop[b] - loop[a])) * env * GameGain;
            pos += rateAt(i / (float)Sr);
        }
        return y;
    }

    /// <summary>
    /// The crosshair drawn from the edge of the view onto a body and off again: pulses quicken, the held
    /// note crossfades in, holds, and goes. The rate steps every tenth of a second, as the game polls it.
    /// </summary>
    private static float[] Approach(float[] pulse, float[] steady)
    {
        float Closeness(float t) => MathF.Min(1f, MathF.Floor(t * 10f) / 10f / 5f);
        var a = Play(pulse, t => ScopeSounds.PulseRate(Closeness(t)), 5f);
        var b = Play(steady, _ => 1f, 1.5f);
        var c = Play(pulse, _ => ScopeSounds.PulseRate(0.8f), 1.5f);
        return a.Concat(b).Concat(c).ToArray();
    }

    private static float[] Hold()
    {
        var inhale = Gain(ScopeSounds.RenderBreath(true));
        var exhale = Gain(ScopeSounds.RenderBreath(false));
        var gap = new float[5 * Sr - inhale.Length];
        return inhale.Concat(gap).Concat(exhale).ToArray();
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int bytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(bytes);
        foreach (float v in samples) w.Write((short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue));
    }
}
