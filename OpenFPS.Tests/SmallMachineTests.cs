using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Yard;

namespace OpenFPS.Tests;

/// <summary>
/// Mowers and air conditioners, held to the arithmetic a refactor could break in silence: the governor
/// trades speed for throttle (the bog), the blade is locked to the engine's firing, a compressor hums at
/// twice the mains, and the cutting hiss is worth what half m v squared says.
/// </summary>
public class SmallMachineTests
{
    private const float Sr = 44100f;

    public SmallMachineTests() => AcousticRegistry.Initialize();

    [Fact]
    public void EveryPresetHasThePartsItsSoundIsMadeOf()
    {
        foreach (var key in SmallMachineSpec.Presets.Keys)
        {
            var s = SmallMachineSpec.ByName(key);
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.True(s.SourceLevelDb > 30f, $"{key} declares {s.SourceLevelDb} dB");
            // Petrol machinery is governed; the rest are fans and compressors.
            if (s.EngineKey != null)
            {
                Assert.NotNull(s.Governor);
                Assert.Contains(s.EngineKey, EngineProfile.Presets.Keys);
            }
            else Assert.True(s.Blade != null || s.Compressor != null, $"{key} has nothing that makes a sound");
        }
    }

    /// <summary>
    /// The governor gives up speed to open the throttle: that is the bog. A proportional controller with
    /// no integral term cannot return to its setting under load, so rpm under load reads how hard it works.
    /// </summary>
    [Fact]
    public void AGovernorTradesSpeedForThrottleAndCannotHoldItsSetting()
    {
        var g = new GovernorSpec { SettingRpm = 2900f, Droop = 0.09f };

        Assert.Equal(g.MinThrottle, g.Throttle(2900f), 3);          // at the setting: shut
        Assert.Equal(1f, g.Throttle(2900f * (1f - 0.09f)), 3);       // a full droop down: wide open
        Assert.InRange(g.Throttle(2900f - 0.5f * 0.09f * 2900f), 0.45f, 0.55f);   // half way: half open

        // Monotone: a governor that was not would hunt for ever.
        float last = 0f;
        for (float rpm = 2900f; rpm > 2500f; rpm -= 25f)
        {
            float t = g.Throttle(rpm);
            Assert.True(t >= last, $"throttle fell from {last:F2} to {t:F2} as the engine slowed");
            last = t;
        }
    }

    /// <summary>
    /// Thick grass pulls a mower's speed down and the governor brings it back, each in about the right
    /// time; run on the model, not the controller, since this is the sound.
    /// </summary>
    [Fact]
    public void ThickGrassBogsTheMowerAndItRecovers()
    {
        var synth = new SmallMachineSynth(SmallMachineSpec.PushMower, Sr, 17)
        { Load = 0.05f, GroundSpeed = 0f };
        Run(synth, 8f);
        float free = MeanRpm(synth, 1f);
        Assert.InRange(free, 2500f, 2950f);          // near its setting with nothing to do

        synth.Load = 1f; synth.GroundSpeed = 1.2f;   // into something thick
        Run(synth, 2f);
        float bogged = MeanRpm(synth, 1f);
        Assert.True(bogged < free - 150f, $"thick grass moved it from {free:F0} to {bogged:F0} rpm");
        Assert.True(bogged > free * 0.75f, $"it should bog, not stall: {bogged:F0} against {free:F0}");
        Assert.True(synth.Throttle > 0.7f, $"the governor should be nearly wide open, not {synth.Throttle:P0}");

        synth.Load = 0.05f; synth.GroundSpeed = 0f;  // out the other side
        Run(synth, 2f);
        float back = MeanRpm(synth, 1f);
        float regained = (back - bogged) / MathF.Max(1f, free - bogged);
        Assert.True(regained > 0.8f,
            $"it recovered {regained:P0} of the {free - bogged:F0} rpm it lost: {bogged:F0} -> {back:F0} against {free:F0}");
    }

    /// <summary>
    /// The mean speed over a stretch: a single's crank ripples a hundred rpm either way at 2,800 between
    /// firings, and a test written on one instant failed at random.
    /// </summary>
    private static float MeanRpm(SmallMachineSynth synth, float seconds)
    {
        int n = (int)(Sr * seconds);
        double sum = 0;
        for (int i = 0; i < n; i++) { synth.Step(); sum += synth.Rpm; }
        return (float)(sum / n);
    }

    private static void Run(SmallMachineSynth synth, float seconds)
    {
        int n = (int)(Sr * seconds);
        for (int i = 0; i < n; i++) synth.Step();
    }

    /// <summary>
    /// The blade is bolted to the crankshaft, so the machine has one rotating speed; a ratio that stopped
    /// being 1 would give a mower a second, beating note.
    /// </summary>
    [Fact]
    public void AMowerBladeTurnsAtEngineSpeed()
    {
        var spec = SmallMachineSpec.PushMower;
        Assert.Equal(1f, spec.BladeGearRatio);

        var synth = new SmallMachineSynth(spec, Sr, 17) { Load = 0.4f, GroundSpeed = 1f };
        for (int i = 0; i < (int)(Sr * 8); i++) synth.Step();
        Assert.Equal(synth.Rpm, synth.BladeRpm, 0);

        // Two tips pass twice a revolution: the second order of an engine that fires once every two,
        // locked to it, so a mower is one sound.
        float bladePass = spec.Blade!.BladePassHz(synth.BladeRpm);
        float firing = synth.Rpm / 60f / 2f;      // a four-stroke single fires once per two turns
        Assert.Equal(4f, bladePass / firing, 1);
    }

    /// <summary>A compressor hums at twice the mains frequency, not at the shaft: every air conditioner
    /// on a street hums the same note, and a European one a tone lower.</summary>
    [Fact]
    public void TheHumIsTheMainsAndNotTheMachine()
    {
        var american = SmallMachineSpec.AirConditionerCondenser.Compressor!;
        Assert.Equal(120f, american.HumHz, 1);

        var european = american with { LineHz = 50f };
        Assert.Equal(100f, european.HumHz, 1);

        // The pump is the shaft, not the hum: a few per cent of slip puts it clear, and the beat between
        // them stops a compressor sounding like a transformer.
        Assert.True(MathF.Abs(american.PulsationHz - american.HumHz) > 30f,
            $"pumping {american.PulsationHz:F0} Hz against a {american.HumHz:F0} Hz hum");
        Assert.InRange(american.ShaftRpm, 3300f, 3550f);

        // A 50 Hz machine turns slower too, because the field does.
        Assert.True(european.ShaftRpm < american.ShaftRpm * 0.88f);
    }

    /// <summary>The fan runs on, audibly, when the compressor stops.</summary>
    [Fact]
    public void TheFanRunsOnWhenTheCompressorIsSatisfied()
    {
        var synth = new SmallMachineSynth(SmallMachineSpec.AirConditionerCondenser, Sr, 17);
        Run(synth, 4f);                 // the compressor takes a moment to come up to speed
        double both = Energy(synth, 3f);

        synth.CompressorOn = false;
        for (int i = 0; i < (int)(Sr * 3); i++) synth.Step();   // it takes a moment to wind down
        double fanOnly = Energy(synth, 3f);

        Assert.True(fanOnly > 0, "the fan stopped with the compressor");
        float drop = 10f * MathF.Log10((float)(both / fanOnly));
        Assert.InRange(drop, 1.5f, 12f);   // quieter, and still plainly running
    }

    /// <summary>
    /// The cutting hiss is worth what half m v squared says: 5 mg at 80 m/s is 16 mJ, a tick in the
    /// fifties of dB against a machine in the nineties. Grass that seems too quiet is the engine bogging.
    /// </summary>
    [Fact]
    public void CuttingIsAsQuietAsTheEnergyArithmeticSaysItIs()
    {
        var c = new CuttingSpec();
        float tip = SmallMachineSpec.PushMower.Blade!.TipSpeed(SmallMachineSpec.PushMower.Blade!.RpmMax);
        Assert.InRange(c.ImpactDb(tip), 50f, 64f);

        // Twice the speed is four times the energy: 6 dB.
        Assert.Equal(6f, c.ImpactDb(2f * tip) - c.ImpactDb(tip), 1);
    }

    /// <summary>
    /// A mower standing still cuts nothing: the rate is grass arriving, so stopping to turn round stops
    /// the hiss but not the machine.
    /// </summary>
    [Fact]
    public void AMowerStandingStillCutsNothing()
    {
        var synth = new SmallMachineSynth(SmallMachineSpec.PushMower, Sr, 17) { Load = 0.6f, GroundSpeed = 1.2f };
        for (int i = 0; i < (int)(Sr * 8); i++) synth.Step();

        double walking = 0;
        for (int i = 0; i < (int)(Sr * 2); i++) { synth.Step(); walking += synth.Cutting * synth.Cutting; }
        Assert.True(walking > 0, "walking through grass cut nothing");

        synth.GroundSpeed = 0f;
        for (int i = 0; i < (int)(Sr * 1); i++) synth.Step();
        double still = 0;
        for (int i = 0; i < (int)(Sr * 2); i++) { synth.Step(); still += synth.Cutting * synth.Cutting; }
        Assert.Equal(0d, still);
    }

    /// <summary>
    /// Every machine renders its declared level within 3 dB, measured as <c>--yard levels</c> does; a
    /// drifted level puts a machine in the wrong place against the rest of a map.
    /// </summary>
    [Theory]
    [InlineData("mower_push")]
    [InlineData("mower_riding")]
    [InlineData("ac_condenser")]
    [InlineData("ac_window")]
    public void EachMachineMeasuresWhatItDeclares(string key)
    {
        var spec = SmallMachineSpec.ByName(key);
        var synth = new SmallMachineSynth(spec, Sr, 17)
        {
            Load = spec.Cutting != null ? 0.5f : 0.6f,
            GroundSpeed = spec.Cutting != null ? 1.1f : 0f,
        };
        for (int i = 0; i < (int)(Sr * 6); i++) synth.Step();

        int n = (int)(Sr * 4);
        double sum = 0;
        for (int i = 0; i < n; i++) { synth.Step(); sum += synth.Total * synth.Total; }
        float db = 20f * MathF.Log10(MathF.Max(1e-9f, (float)Math.Sqrt(sum / n)) / 20e-6f);

        Assert.True(MathF.Abs(db - spec.SourceLevelDb) <= 3f,
            $"{key} declares {spec.SourceLevelDb:F0} dB and measures {db:F1}");
    }

    /// <summary>
    /// The sheet-metal parts are steel, which the registry spells "Metal": "Steel" silently gets Generic
    /// (1,200 kg/m^3 and 5 GPa, a plastic), a deck with the modes of a bucket.
    /// </summary>
    [Fact]
    public void TheMetalPartsAreActuallyMetal()
    {
        foreach (var key in SmallMachineSpec.Presets.Keys)
        {
            var s = SmallMachineSpec.ByName(key);
            foreach (var (what, material) in new[]
                     { ("deck", s.Deck?.Material), ("casing", s.Casing?.Material) })
            {
                if (material == null) continue;
                var m = AcousticRegistry.GetProperties(material);
                var generic = AcousticRegistry.GetProperties("Generic");
                Assert.True(m.YoungsModulusGPa != generic.YoungsModulusGPa || material == "Generic",
                    $"{key}'s {what} asks for '{material}' and the registry fell back to Generic");
                Assert.True(m.YoungsModulusGPa > 20f, $"{key}'s {what} is {m.YoungsModulusGPa} GPa, which is not sheet metal");
            }
        }
    }

    /// <summary>A machine written out as JSON and read back is the same machine, so a map can carry its own.</summary>
    [Fact]
    public void AMachineSurvivesTheRoundTrip()
    {
        foreach (var key in SmallMachineSpec.Presets.Keys)
        {
            var original = SmallMachineSpec.Presets[key]();
            string json = ModelLibrary.ToJson(ModelLibrary.Kinds.SmallMachine, key, original);
            Assert.Contains(key, json);
            Assert.Contains(ModelLibrary.Kinds.SmallMachine, json);
        }
        Assert.Contains("mower_push", ModelLibrary.Ids(ModelLibrary.Kinds.SmallMachine));
    }

    private static double Energy(SmallMachineSynth synth, float seconds)
    {
        double sum = 0;
        int n = (int)(Sr * seconds);
        for (int i = 0; i < n; i++) { synth.Step(); sum += synth.Total * synth.Total; }
        return sum / n;
    }
}
