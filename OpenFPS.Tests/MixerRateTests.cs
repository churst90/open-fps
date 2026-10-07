using System.Numerics;
using System.Reflection;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Signals;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The mixer runs at 48 kHz (MixerQuality.DefaultRate; it was 44.1 until 2026-10-06), and everything
/// that renders for it takes the mixer's rate rather than assuming one. A synth that assumed 44.1 would
/// play 9 % sharp and short, or its filters and decays would move; these fail if anything does.
/// </summary>
public class MixerRateTests
{
    private readonly ITestOutputHelper _o;
    public MixerRateTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void TheMixerAsksForFortyEightKilohertz()
    {
        Assert.Equal(48000, MixerQuality.DefaultRate);
        if (Environment.GetEnvironmentVariable("OPENFPS_MIXER_RATE") == null) Assert.Equal(48000, MixerQuality.RequestedRate);
        // Rendered one-shots are made at the mixer's rate, so a stationary one plays with no resampler.
        Assert.Equal(MixerQuality.DefaultRate, TransientSynth.SampleRate);
        Assert.Equal(MixerQuality.MixerRate, UiSounds.SampleRate);
        Assert.Equal(MixerQuality.MixerRate, ScopeSounds.SampleRate);
        Assert.Equal(MixerQuality.DefaultRate, VehicleSynth.SampleRate);
    }

    /// <summary>
    /// No constructor, method or rate constant in the game's audio code defaults to 44,100. DiffuseBranch and
    /// EarDecorrelator lay their filters out in samples counted at 44.1 kHz and scale to the rate given, and say
    /// so by name.
    /// </summary>
    [Fact]
    public void NothingDefaultsToFortyFourOne()
    {
        var allowed = new HashSet<string> { "DiffuseBranch", "EarDecorrelator", "At44k" };
        var found = new List<string>();
        foreach (var asm in new[] { typeof(MixerQuality).Assembly, typeof(Footsteps).Assembly })
            foreach (var t in SafeTypes(asm))
            {
                if (allowed.Contains(t.Name)) continue;
                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                    foreach (var p in m.GetParameters())
                        if (p.HasDefaultValue && IsRateName(p.Name) && Is44k(p.DefaultValue))
                            found.Add($"{t.FullName}.{m.Name}({p.Name} = {p.DefaultValue})");
                foreach (var f in t.GetFields(All))
                    if ((f.IsLiteral || f.IsInitOnly && f.IsStatic) && IsRateName(f.Name) && Is44k(SafeValue(f)))
                        found.Add($"{t.FullName}.{f.Name} = {SafeValue(f)}");
            }
        foreach (var f in found) _o.WriteLine(f);
        Assert.Empty(found);
    }

    private static bool IsRateName(string? n) => n != null && (n.Contains("rate", StringComparison.OrdinalIgnoreCase) || n is "fs" or "sr" or "Fs" or "Sr");
    private static bool Is44k(object? v) => v switch { int i => i == 44100, float f => f == 44100f, double d => d == 44100.0, _ => false };
    private static object? SafeValue(FieldInfo f) { try { return f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null); } catch { return null; } }
    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
    }

    /// <summary>Every physical voice runs at the rate the provider hands it, which is the mixer's.</summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void EveryVoiceRunsAtTheRateItIsGiven(int rate)
    {
        var at = new Vector3(0f, 0f, 5f);
        var voices = new List<(string, float)>
        {
            ("engine", new EngineVoiceState(MachineRegistry.VehicleFor("i4_midsize"), rate, 1).SampleRate),
            ("machine", new MachineVoiceState(SmallMachineSpec.ByName(SmallMachineSpec.Presets.Keys.First()), rate, 1, 2).SampleRate),
            ("siren", new SirenVoiceState(SirenSpec.ByName(SirenSpec.Presets.Keys.First()), rate).SampleRate),
            ("water", new WaterVoiceState(WaterFeatureSpec.ByName(WaterFeatureSpec.Presets.Keys.First()), rate, 1, at).SampleRate),
            ("fire", new FireVoiceState(FireSpec.ByName(FireSpec.Presets.Keys.First()), rate, 1, at).SampleRate),
            ("foliage", new FoliageVoiceState(FoliageSpec.ByName(FoliageSpec.Presets.Keys.First()), rate, 1, at).SampleRate),
        };
        foreach (var (name, r) in voices) Assert.True(r == rate, $"{name} runs at {r} Hz, given {rate}");
    }

    /// <summary>The engine's note is where its speed puts it at either rate: an idle's strongest order is the
    /// same at 44.1 and 48 kHz, and the idle speed within a few per cent (the crank is integrated per sample, so
    /// the idle's small hunt differs).</summary>
    [Fact]
    public void AnEngineIdlesOnTheSameNoteAtEitherRate()
    {
        (float Hz, float Rpm) Peak(int rate)
        {
            var v = new EngineVoiceState(MachineRegistry.VehicleFor("i4_midsize"), rate, 11) { TargetSpeed = 0f, CompensateLevel = false };
            v.PlaceAtSpeed(0f);
            v.Revive();
            var x = new List<float>();
            var block = new float[512];
            double rpm = 0; int n = 0;
            for (int b = 0; b < rate * 5 / 512; b++)
            {
                v.Produce(); v.Consume(block);
                if (b > rate * 2 / 512) { x.AddRange(block); rpm += v.Engine.Rpm; n++; }
            }
            float r = (float)(rpm / n);
            // The strongest engine order, half-orders 0.5 to 6, each at this run's own speed.
            var arr = x.ToArray();
            float bestOrder = 0f; double bestP = -1;
            for (float o = 0.5f; o <= 6f; o += 0.5f)
            {
                double p = Power(arr, rate, o * r / 60f);
                if (p > bestP) { bestP = p; bestOrder = o; }
            }
            return (bestOrder, r);
        }
        var a = Peak(44100); var b = Peak(48000);
        _o.WriteLine($"idle: {a.Rpm:F0} rpm, strongest order {a.Hz:F1} at 44.1 kHz; {b.Rpm:F0} rpm, strongest order {b.Hz:F1} at 48");
        Assert.Equal(a.Hz, b.Hz);
        Assert.InRange(b.Rpm / a.Rpm, 0.97f, 1.03f);
    }

    private static double Power(float[] x, int rate, float f)
    {
        double w = 2 * Math.PI * f / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1));
            double s0 = x[i] * win + c * s1 - s2; s2 = s1; s1 = s0;
        }
        return s1 * s1 + s2 * s2 - c * s1 * s2;
    }

    /// <summary>A siren sweeps through the same notes at the same times at either rate.</summary>
    [Fact]
    public void ASirenSweepsTheSameAtEitherRate()
    {
        var spec = SirenSpec.ByName(SirenSpec.Presets.Keys.First());
        float[] Track(int rate)
        {
            var s = new ElectronicSiren(spec, rate) { Mode = SirenMode.Wail };
            var hz = new float[4];
            for (int i = 0; i < rate * 4; i++)
            {
                s.Step();
                if ((i + 1) % rate == 0) hz[(i + 1) / rate - 1] = s.Hz;
            }
            return hz;
        }
        var a = Track(44100); var b = Track(48000);
        _o.WriteLine($"wail at 1-4 s: {string.Join(", ", a.Select(h => h.ToString("F0")))} Hz at 44.1; {string.Join(", ", b.Select(h => h.ToString("F0")))} at 48");
        for (int k = 0; k < a.Length; k++) Assert.InRange(b[k] / a[k], 0.99f, 1.01f);
    }

    /// <summary>A horn's note is the same note at either rate.</summary>
    [Fact]
    public void AHornIsTheSameNoteAtEitherRate()
    {
        var spec = ChimeHornSpec.NathanK5LA;
        float Peak(int rate)
        {
            var h = new ChimeHorn(spec, rate, 11);
            var x = new float[rate * 2];
            for (int i = 0; i < x.Length; i++) { h.Blowing = true; h.Step(); x[i] = h.Out; }
            return StrongestHz(x.AsSpan(rate).ToArray(), rate, 150f, 800f, 0.25f);
        }
        float a = Peak(44100), b = Peak(48000);
        _o.WriteLine($"horn's strongest line: {a:F2} Hz at 44.1 kHz, {b:F2} Hz at 48");
        Assert.InRange(b / a, 0.995f, 1.005f);
    }

    /// <summary>The scope's held note is 661.5 Hz at any rate, in whole cycles so it loops without a seam.</summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void TheScopeNoteLoopsInWholeCyclesAtAnyRate(int rate)
    {
        int n = ScopeSounds.SteadyLoopSamples(rate);
        double cycles = n * (double)ScopeSounds.SteadyHz / rate;
        Assert.Equal(Math.Round(cycles), cycles, 6);
        Assert.InRange(n / (double)rate, 0.04, 0.7);
        if (rate == 44100) Assert.Equal(2000, n);
    }

    /// <summary>Strongest line between lo and hi, by a Goertzel scan.</summary>
    private static float StrongestHz(float[] x, int rate, float lo, float hi, float step)
    {
        float best = lo; double bestP = -1;
        for (float f = lo; f <= hi; f += step)
        {
            double w = 2 * Math.PI * f / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1));
                double s0 = x[i] * win + c * s1 - s2; s2 = s1; s1 = s0;
            }
            double p = s1 * s1 + s2 * s2 - c * s1 * s2;
            if (p > bestP) { bestP = p; best = f; }
        }
        return best;
    }
}
