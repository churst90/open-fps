using OpenFPS.Client.AudioEngine.Core.Stove;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The gas hob (docs/GAS_HOB.md): its injectors against the manufacturers' tables, its state on the wire and
/// the interact key, and the hob itself: a knob turned to full lights after a tick or two, a knob left low
/// never lights, a slow light burns more gas, a flame turned off goes out and its safety valve shuts later.
/// </summary>
public class GasHobTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _o;
    public GasHobTests(ITestOutputHelper o) => _o = o;

    private static float[] Run(GasHobSynth h, float seconds, Action<GasHobSynth, float>? each = null)
    {
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
        {
            if (each != null && i % 480 == 0) each(h, i / (float)Rate);
            x[i] = h.Next();
        }
        return x;
    }

    private static double Db(double pa) => 20 * Math.Log10(Math.Max(1e-12, pa) / 20e-6);

    private static double Leq(float[] x, float from, float to)
    {
        int a = (int)(from * Rate), b = (int)(to * Rate);
        double e = 0;
        for (int i = a; i < b; i++) e += (double)x[i] * x[i];
        return Db(Math.Sqrt(e / (b - a)));
    }

    [Fact]
    public void TheInjectorsPassTheGasTheManualsSay()
    {
        var hob = GasHobSpec.FourBurnerNatural;
        // Whirlpool AKT 300: 286 l/h for the rapid burner, 157 l/h for the semi-rapid, G20 at 20 mbar.
        Assert.InRange(hob.FullFlow(hob.Burners[0]) * 3.6e6f, 278f, 294f);
        Assert.InRange(hob.FullFlow(hob.Burners[2]) * 3.6e6f, 152f, 162f);
        // Gross ratings: 3.00, 1.65 and 1.00 kW.
        Assert.InRange(hob.RatedKw(hob.Burners[0]), 2.9f, 3.1f);
        Assert.InRange(hob.RatedKw(hob.Burners[2]), 1.6f, 1.7f);
        Assert.InRange(hob.RatedKw(hob.Burners[1]), 0.9f, 1.05f);
        // The propane set gives the same heat through smaller holes.
        var lpg = GasHobSpec.FourBurnerPropane;
        Assert.InRange(lpg.RatedKw(lpg.Burners[0]) / hob.RatedKw(hob.Burners[0]), 0.95f, 1.05f);
        Assert.True(lpg.Burners[0].InjectorMm < hob.Burners[0].InjectorMm);
    }

    [Fact]
    public void ASparkLightsOnlyWhatIsFlammable()
    {
        var gas = FuelGas.Methane;
        float stoich = gas.FractionAt(1f);
        Assert.InRange(stoich, 0.090f, 0.100f);
        Assert.True(gas.IgnitionEnergyMj(stoich) < 0.5f);
        Assert.True(gas.IgnitionEnergyMj(0.03f) == float.PositiveInfinity);
        Assert.True(gas.IgnitionEnergyMj(0.051f) < 15f, "a 15 mJ spark lights at the lean limit");
        Assert.Equal(0f, gas.BurningVelocity(0.2f));
        Assert.InRange(gas.BurningVelocity(gas.FractionAt(1.075f)), 0.36f, 0.38f);
    }

    [Fact]
    public void TheKeyCarriesTheSettingsAndWhenTheyChanged()
    {
        Assert.True(HobKey.TryParse("stove:hob4", out var bare));
        Assert.Equal("hob4", bare.Preset);
        Assert.False(bare.AnyOn);
        var k = new HobKey("hob4", "0000", "3000", 1234.5);
        Assert.True(HobKey.TryParse(k.Format(), out var back));
        Assert.Equal(k, back);
        Assert.Equal(3, back.Setting(0));
        Assert.True(back.AnyOn);
        Assert.False(HobKey.TryParse("stove:hob4/0000>300@1", out _));
        Assert.False(HobKey.TryParse("stove:hob4/0000>3009@1", out _));
        Assert.Equal("hob4", GasHobSpec.ByName(k.Format()).Burners.Length == 4 ? "hob4" : "");
    }

    [Fact]
    public void TheInteractKeyLightsEachBurnerInTurnThenTurnsThemAllOff()
    {
        string key = "stove:hob4";
        var lines = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            lines.Add(HobControls.Press(key, 100 + i, out key));
            _o.WriteLine($"{lines[^1]}  {key}");
        }
        Assert.Equal("You light the front left burner.", lines[0]);
        Assert.Equal("You light the back right burner.", lines[3]);
        Assert.Equal("You turn every burner off.", lines[4]);
        Assert.True(HobKey.TryParse(key, out var last));
        Assert.Equal("3333", last.From);
        Assert.Equal("0000", last.To);
        Assert.Equal(104.0, last.At, 2);
        Assert.Equal("You light the burner.", HobControls.Press("stove:hob1", 1, out _));
    }

    [Fact]
    public void TurnedToFullItLightsAfterATickOrTwoAndTheSparksStopWhenTheKnobIsLetGo()
    {
        var h = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 7);
        h.Begin("0000", "3000", 0f);
        var x = Run(h, 9f);
        Assert.True(h.IsLit(0));
        Assert.False(h.IsLit(1));
        Assert.InRange(h.FailedSparksBeforeLight(0), 0, 2);
        Assert.InRange(h.LastLightUpJoules(0), 50f, 600f);
        int sparks = h.Sparks;
        Run(h, 2f);
        Assert.Equal(sparks, h.Sparks);
        Assert.False(h.HandBusy);
        // The module's rate: 50 Hz over twelve cycles.
        Assert.InRange(sparks, 12, 20);
        _o.WriteLine($"{sparks} sparks, {h.FailedSparksBeforeLight(0)} failed, light-up {h.LastLightUpJoules(0):F0} J, flame {Leq(x, 7f, 9f):F1} dB");
    }

    [Fact]
    public void AKnobBarelyOpenNeverLights()
    {
        // A trickle (30°, 7 % of full): the ports' jets do not reach the electrode, and the gas drifts off
        // leaner than the lean limit. A hob is lit on full. (Turned to low it passes full on the way, and
        // lights there.)
        var h = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 3);
        h.Begin("0000", "0000", 0f);
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Push, Burner = 0, Seconds = 0.1f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = 0, Degrees = 30f, Seconds = 0.6f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Wait, Seconds = 8f });
        Run(h, 9f);
        Assert.False(h.IsLit(0));
        Assert.True(h.Sparks > 30);
        // Pushed on to full, the next spark or two light it.
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = 0, Degrees = GasHobSpec.FullDegrees, Seconds = 0.3f });
        Run(h, 1.5f);
        Assert.True(h.IsLit(0));
    }

    [Fact]
    public void ASlowLightFailsWhileTheGasGathersAndBurnsMoreOfItAtOnce()
    {
        var quick = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 7);
        quick.Begin("0000", "3000", 0f);
        Run(quick, 6f);
        var slow = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 7);
        slow.Begin("0000", "0000", 0f);
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Push, Burner = 0, Seconds = 0.15f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = 0, Degrees = 36f, Seconds = 0.9f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Wait, Seconds = 2.6f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = 0, Degrees = GasHobSpec.FullDegrees, Seconds = 0.4f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.WaitLit, Burner = 0, Hold = 3f, Seconds = 10f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Release, Burner = 0, Seconds = 0.1f });
        Run(slow, 10f);
        Assert.True(slow.IsLit(0));
        Assert.True(slow.FailedSparksBeforeLight(0) >= 6, $"{slow.FailedSparksBeforeLight(0)} failed sparks");
        Assert.True(slow.LastLightUpJoules(0) > 2f * quick.LastLightUpJoules(0),
            $"slow {slow.LastLightUpJoules(0):F0} J against quick {quick.LastLightUpJoules(0):F0} J");
    }

    [Fact]
    public void TurnedOffTheFlameGoesOutAndTheSafetyValveShutsLater()
    {
        var h = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 5);
        h.Begin("3000", "3000", 0f);
        var on = Run(h, 2f);
        Assert.True(h.IsLit(0));
        h.Change("3000", "0000");
        var off = Run(h, 3f);
        Assert.False(h.IsLit(0));
        Assert.True(Leq(off, 2f, 3f) < Leq(on, 0.5f, 2f) - 30, "out within a second");
        // The armature drops when the thermocouple has cooled: a click well after the flame.
        var later = Run(h, 25f);
        int loud = Array.FindIndex(later, v => Math.Abs(v) > 20e-6 * Math.Pow(10, 35 / 20.0));
        Assert.InRange(loud / (float)Rate, 8f, 22f);
        Assert.True(h.Idle);
    }

    [Fact]
    public void AVoiceMadeLateHearsTheHobAsItIs()
    {
        // Silent catch-up: a client arriving 30 s after a burner was lit hears it burning, the knob let go.
        var late = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 9);
        late.Begin("0000", "3000", 30f);
        Assert.True(late.IsLit(0));
        Assert.False(late.HandBusy);
        Assert.Equal(GasHobSpec.FullDegrees, late.KnobDegrees(0), 1);
        var x = Run(late, 1f);
        Assert.InRange(Leq(x, 0.2f, 1f), 40, 46);
        // Turned off a minute ago: nothing.
        var gone = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 9);
        gone.Begin("3000", "0000", 60f);
        Assert.False(gone.IsLit(0));
        Assert.True(gone.Idle);
    }

    [Fact]
    public void TheSameHobRendersTheSameAndAllocatesNothing()
    {
        var a = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 11);
        var b = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 11);
        a.Begin("0000", "3300", 0f);
        b.Begin("0000", "3300", 0f);
        Run(a, 0.5f); Run(b, 0.5f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float diff = 0f;
        for (int i = 0; i < 8 * Rate; i++) diff = MathF.Max(diff, MathF.Abs(a.Next() - b.Next()));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0f, diff);
        Assert.Equal(0, allocated);
        Assert.True(a.IsLit(0));
    }

    [Fact]
    public void EveryBurnerOnFullIsTheDeclaredLevel()
    {
        foreach (var (name, spec) in new[] { ("hob4", GasHobSpec.FourBurnerNatural), ("hob4_propane", GasHobSpec.FourBurnerPropane), ("hob1", GasHobSpec.SingleBurner) })
        {
            var h = new GasHobSynth(spec, Rate, 13);
            string all = new('3', spec.Burners.Length);
            h.Begin(all, all, 0f);
            var x = Run(h, 6f);
            double leq = Leq(x, 1f, 6f);
            _o.WriteLine($"{name}: {leq:F1} dB against {spec.SourceLevelDb:F1} declared");
            Assert.InRange(leq, spec.SourceLevelDb - 1.0, spec.SourceLevelDb + 1.0);
        }
    }

    [Fact]
    public void TheVoiceTakesAKnobTurnedFromItsKey()
    {
        var spec = GasHobSpec.FourBurnerNatural;
        var voice = new StoveVoiceState(spec, new HobKey("hob4", "0000", "0000", WindField.Now() - 5).Format(), Rate, 3);
        var block = new float[1024];
        voice.SetKey(new HobKey("hob4", "0000", "3000", WindField.Now()).Format());
        for (int i = 0; i < 8 * Rate / block.Length; i++) voice.Render(block);
        Assert.True(voice.Hob.IsLit(0));
    }
}
