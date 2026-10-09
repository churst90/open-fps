using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Common;
using OpenFPS.Common.Hearing;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The DSP callbacks FMOD calls on its mixer thread, run without FMOD through a stand-in for the
/// callback's function table (<see cref="FakeDsp"/>), which answers the two things they ask of it: their
/// state (DspCallback.UserData) and the channel clock (DspCallback.Clock). Four past crashes were in this
/// glue (docs/COVERAGE_2026-09-24.md). What each callback owes the mix: finite samples, nothing allocated
/// on the mixer thread, silence where nothing plays, and the level its own law states. Stages that need
/// the native Steam Audio library are tested on their managed paths only: the bail-outs to silence, the
/// non-finite guard, the capture.
/// </summary>
public class DspCallbackTests
{
    private readonly ITestOutputHelper _o;
    public DspCallbackTests(ITestOutputHelper o) => _o = o;

    private const int Rate = 48000;
    private const int Block = 1024;

    // ── The physical voices: machines, aircraft, signals, rain, rail, water, fire, trees, shores ──

    public static IEnumerable<object[]> PhysicalKinds() => new[]
    {
        "window unit", "mower", "airliner", "siren", "electric horn", "air horn", "crossing bell", "rain",
        "light rail", "fountain", "fountain tap", "fire", "fire place", "tree", "tree place", "creek", "shore",
    }.Select(k => new object[] { k });

    private static readonly Vector3 Somewhere = new(400f, 0f, -300f);

    private static PhysicalVoiceState MakePhysical(string kind) => kind switch
    {
        "window unit" => new MachineVoiceState(SmallMachineSpec.ByName("ac_window"), Rate, entityId: 4242, seed: 11),
        "mower" => new MachineVoiceState(SmallMachineSpec.ByName("mower_push"), Rate, entityId: 77, seed: 3) { TargetGroundSpeed = 0.95f },
        "airliner" => new AircraftVoiceState(AircraftProfile.ByName("airliner"), Rate, seed: 5, lever: 1f),
        "siren" => new SirenVoiceState(SirenSpec.Patrol100W, Rate) { TargetMode = (int)SirenMode.Wail },
        "electric horn" => new HornVoiceState("electric:disc_pair", new[] { 2f }, Rate, 3),
        "air horn" => new HornVoiceState("air:truck_dual", new[] { 2f }, Rate, 3),
        "crossing bell" => new BellVoiceState(ModelLibrary.Bell("crossing_gong"), Rate, 5),
        "rain" => Rain(),
        "light rail" => LightRail(),
        "fountain" => new WaterVoiceState(WaterFeatureSpec.ByName("park_fountain"), Rate, 3, Somewhere),
        "fountain tap" => new WaterTapState(new WaterFeatureVoice("park_fountain/test", WaterFeatureSpec.ByName("park_fountain"), Rate, 1), 0, Rate, Somewhere),
        "fire" => new FireVoiceState(FireSpec.ByName("fire_pit"), Rate, 13, Somewhere),
        "fire place" => new NaturePlaceState(new PlacedNatureVoice("fire:fire_pit", FireSpec.ByName("fire_pit"), 3, Rate, 13, Somewhere) { TargetSpread = 1f }, 0, Rate, Somewhere),
        "tree" => new FoliageVoiceState(FoliageSpec.ParkTree, Rate, 9, Somewhere),
        "tree place" => new NaturePlaceState(new PlacedNatureVoice("foliage:park_tree", FoliageSpec.ParkTree, 1 + FoliageSynth.Boughs, Rate, 9, Somewhere) { TargetSpread = 1f }, 0, Rate, Somewhere),
        "creek" => new NaturePlaceState(new PlacedNatureVoice("flow:creek", RunningWaterSpec.Creek, Rate, 19, Somewhere), 0, Rate, Somewhere),
        "shore" => new NaturePlaceState(new PlacedNatureVoice("shore:sea", ShoreSpec.SeaSand, ShoreSpec.SeaSand.DefaultGeometry, Rate, 23, Somewhere), 0, Rate, Somewhere),
        _ => throw new ArgumentException(kind),
    };

    /// <summary>A light rail set heard through one voice carrying all of it (TrainSlotState), its lanes
    /// rendered inline, from the start of the train's timeline so a twin made later is in step.</summary>
    private static PhysicalVoiceState LightRail()
    {
        var t = new TrainVoiceState("light_rail/test", TrainProfile.ByName("light_rail"), Rate, 7) { Offline = true };
        var field = t.Layout.Where(e => !e.IsSignal).Select(e => e.Index).ToArray();
        t.SetPlan(0, new TrainSlotPlan(field, field.Select(_ => 0.5f).ToArray()));
        return new TrainSlotState(t, 0, OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.SlotLevelDb(t.Layout), Rate, startSample: 0);
    }

    /// <summary>A steel roof in steady rain, on a clock of its own from zero (every rain voice in the game
    /// shares one; two made a moment apart here would otherwise start at two moments of the shared one).</summary>
    private static RainVoiceState Rain()
    {
        var layer = new RainLayer { Kind = RainSurfaceKind.Plate, Material = "Metal", FromBelow = true,
                                    Plate = new RainPlate("Metal", 0.0007f, 1.2f, 1.2f) };
        layer.Add(0, 3f, 1.0f, 1f);
        layer.Add(1, 9f, 1.8f, 1f);
        var feed = new RainFeed { Patch = new RainPatch { Layers = new[] { layer }, ReferenceDistance = 1f },
                                  Falling = new Precipitation(PrecipitationKind.Rain, 10f) };
        var voice = new RainVoiceState(feed, Rate, 9);
        voice.Synth.Clock = 0;
        return voice;
    }

    /// <summary>
    /// A physical voice's callback hands the mixer exactly what the voice's ring holds, the same sample in
    /// every channel, at unity — compared against a twin of the voice read directly — and silence before
    /// the voice is primed. It allocates nothing, and every sample is finite.
    /// </summary>
    [Theory]
    [MemberData(nameof(PhysicalKinds))]
    public void APhysicalVoiceIsItsRingAtUnityInEveryChannel(string kind)
    {
        using var wind = WindField.Hold(WindWeather.Steady(4.5f, 270f, 0f));
        var read = Callback(typeof(MachineProcessor));
        var voice = MakePhysical(kind);
        var twin = MakePhysical(kind);
        using var dsp = new FakeDsp(voice);
        using var output = new Pinned(Block * 2);
        var direct = new float[Block];

        // Not primed: silence, and the twin is taken the same way so the two stay in step.
        Run(read, dsp, IntPtr.Zero, output, Block, 0, 2);
        Assert.All(output.Data, v => Assert.Equal(0f, v));
        twin.Consume(direct);

        long allocated = 0;
        double energy = 0;
        // Eight blocks at least, and on until the voice has made a sound (a shore can be still for a
        // second or two between its waves), as the render pool keeps it: produced, then read.
        int b = 0;
        for (; b < 8 || (energy == 0 && b < 30 * Rate / Block); b++)
        {
            voice.Produce();
            twin.Produce();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run(read, dsp, IntPtr.Zero, output, Block, 0, 2);
            if (b > 0) allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            twin.Consume(direct);
            for (int i = 0; i < Block; i++)
            {
                if (!float.IsFinite(output.Data[2 * i])) Assert.Fail($"{kind}: sample {i} of block {b} is {output.Data[2 * i]}");
                if (output.Data[2 * i] != direct[i] || output.Data[2 * i + 1] != direct[i])
                    Assert.Fail($"{kind}: block {b} sample {i}: the callback wrote {output.Data[2 * i]}/{output.Data[2 * i + 1]}, the voice rendered {direct[i]}");
                energy += direct[i] * (double)direct[i];
            }
        }
        _o.WriteLine($"{kind}: {10 * Math.Log10(energy / (b * Block) + 1e-30):F1} dBFS over {b} blocks, {allocated} bytes allocated");
        Assert.True(energy > 0, $"{kind} rendered nothing: the comparison proved nothing");
        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// NEVER CUT A VOICE (PhysicalVoiceState): told to stop, a voice fades on its envelope and then plays
    /// silence through its callback, and says it has faded so it can be released.
    /// </summary>
    [Fact]
    public void AFadedVoiceEndsInSilenceThroughItsCallback()
    {
        var read = Callback(typeof(MachineProcessor));
        var voice = MakePhysical("window unit");
        using var dsp = new FakeDsp(voice);
        using var output = new Pinned(Block);
        voice.Produce();
        Run(read, dsp, IntPtr.Zero, output, Block, 0, 1);
        Assert.Contains(output.Data, v => v != 0f);
        voice.TargetEnvelope = 0f;
        // The ring holds a lead rendered at full level; the fade starts after it, over 60 ms.
        for (int b = 0; b < Rate / Block * 2; b++)
        {
            voice.Produce();
            Run(read, dsp, IntPtr.Zero, output, Block, 0, 1);
        }
        Assert.True(voice.FadedOut);
        float peak = output.Data.Max(MathF.Abs);
        Assert.True(peak < 1e-6f, $"still {peak} two seconds after the fade began");
    }

    // ── The engines: the car, its front outlet and a wall's reflection of it ──────────────────

    private static EngineVoiceState Car() => new(MachineRegistry.VehicleFor("i4_midsize"), Rate, 7)
    {
        TargetSpeed = 50f / 3.6f, SplitVoices = true,
    };

    /// <summary>
    /// The engine's callback (EngineProcessor), its front outlet's (TapProcessor) and a reflection's
    /// (EchoProcessor) each hand the mixer exactly what their voice renders, the same in every channel,
    /// allocating nothing — against a twin car whose voices are read directly.
    /// </summary>
    [Fact]
    public void AnEngineItsFrontAndItsEchoAreTheirVoicesAtUnity()
    {
        var engineRead = Callback(typeof(EngineProcessor));
        var tapRead = Callback(typeof(TapProcessor));
        var echoRead = Callback(typeof(EchoProcessor));
        var car = Car(); var twin = Car();
        car.PlaceAtSpeed(car.TargetSpeed); twin.PlaceAtSpeed(twin.TargetSpeed);
        var front = new EngineTapState(car) { ChannelRate = 1f };
        var twinFront = new EngineTapState(twin) { ChannelRate = 1f };
        var echo = new EngineEchoState(car) { TargetDelaySeconds = 0.03f, TargetGain = 0.5f, SampleRate = Rate };
        var twinEcho = new EngineEchoState(twin) { TargetDelaySeconds = 0.03f, TargetGain = 0.5f, SampleRate = Rate };
        using var carDsp = new FakeDsp(car);
        using var frontDsp = new FakeDsp(front);
        using var echoDsp = new FakeDsp(echo);
        using var o1 = new Pinned(Block * 2); using var o2 = new Pinned(Block * 2); using var o3 = new Pinned(Block * 2);
        var d1 = new float[Block]; var d2 = new float[Block]; var d3 = new float[Block];

        // Unprimed: silence from the engine's callback.
        Run(engineRead, carDsp, IntPtr.Zero, o1, Block, 0, 2);
        Assert.All(o1.Data, v => Assert.Equal(0f, v));
        twin.Consume(d1);

        long allocated = 0;
        double eEngine = 0, eFront = 0, eEcho = 0;
        for (int b = 0; b < 24; b++)
        {
            car.Produce(); twin.Produce();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run(engineRead, carDsp, IntPtr.Zero, o1, Block, 0, 2);
            Run(tapRead, frontDsp, IntPtr.Zero, o2, Block, 0, 2);
            Run(echoRead, echoDsp, IntPtr.Zero, o3, Block, 0, 2);
            if (b > 0) allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            twin.Consume(d1); twinFront.Render(d2); twinEcho.Render(d3);
            for (int i = 0; i < Block; i++)
                for (int c = 0; c < 2; c++)
                {
                    if (o1.Data[2 * i + c] != d1[i] || o2.Data[2 * i + c] != d2[i] || o3.Data[2 * i + c] != d3[i])
                        Assert.Fail($"block {b} sample {i} channel {c}: engine {o1.Data[2 * i + c]} against {d1[i]}, front {o2.Data[2 * i + c]} against {d2[i]}, echo {o3.Data[2 * i + c]} against {d3[i]}");
                    if (!float.IsFinite(o1.Data[2 * i + c]) || !float.IsFinite(o2.Data[2 * i + c]) || !float.IsFinite(o3.Data[2 * i + c]))
                        Assert.Fail($"block {b} sample {i}: not finite");
                }
            for (int i = 0; i < Block; i++) { eEngine += d1[i] * (double)d1[i]; eFront += d2[i] * (double)d2[i]; eEcho += d3[i] * (double)d3[i]; }
        }
        _o.WriteLine($"engine {Db(eEngine, 24 * Block):F1}, front {Db(eFront, 24 * Block):F1}, echo {Db(eEcho, 24 * Block):F1} dBFS; {allocated} bytes allocated");
        Assert.True(eEngine > 0 && eFront > 0 && eEcho > 0, "a voice rendered nothing: the comparison proved nothing");
        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// A cabin path's tap plays, through its callback, the samples its engine's callback played in the
    /// same block on the mixer's clock — whichever channel started where (the engine's 239 samples into
    /// its parent's time line, the tap's a block later). The engine's callback notes which block went out
    /// when (DspCallback.Clock); the tap's reads it back. A tap on its own clock turned the cancellation
    /// between intake and exhaust at idle into a sum: +6.5 dB (AudioLab --cabin probe=align).
    /// </summary>
    [Fact]
    public void ACabinPathPlaysTheBlockItsEngineCallbackPlayedAtTheSameMoment()
    {
        var engineRead = Callback(typeof(EngineProcessor));
        var tapRead = Callback(typeof(TapProcessor));
        var car = new EngineVoiceState(MachineRegistry.VehicleFor("i4_economy"), Rate, 7)
        {
            TargetSpeed = 60f / 3.6f, CompensateLevel = false, Interior = true, ChannelClockOffset = 239,
        };
        car.PlaceAtSpeed(car.TargetSpeed);
        Assert.NotNull(car.CabinLayout);
        car.SetCabinTapLive(1, true);
        var tap = new EngineTapState(car, 1) { ChannelClockOffset = 239 + Block };
        using var carDsp = new FakeDsp(car);
        using var tapDsp = new FakeDsp(tap);
        using var o1 = new Pinned(Block);
        using var o2 = new Pinned(Block);
        long alignedBefore = EngineTapState.AlignedBlocks;
        int blocks = 12;
        double worst = 0, energy = 0;
        for (int b = 0; b < blocks; b++)
        {
            car.Produce();
            long from = car.Played;
            ulong carClock = (ulong)(10_000 + b * Block);
            FakeDsp.Clock = carClock;
            Run(engineRead, carDsp, IntPtr.Zero, o1, Block, 0, 1);
            FakeDsp.Clock = carClock - Block;                 // the tap's channel started a block later
            Run(tapRead, tapDsp, IntPtr.Zero, o2, Block, 0, 1);
            if (b < 4) continue;                               // the tap's own 60 ms fade-in
            for (int i = 0; i < Block; i++)
            {
                float expect = car.ReadCabinAt(1, from + i);
                worst = Math.Max(worst, Math.Abs(o2.Data[i] - expect));
                energy += expect * (double)expect;
            }
        }
        FakeDsp.Clock = 0;
        _o.WriteLine($"path 1: {Db(energy, (blocks - 4) * Block):F1} dBFS, worst difference from the engine's own block {worst:E2}; "
                   + $"{EngineTapState.AlignedBlocks - alignedBefore} block(s) aligned");
        Assert.True(energy > 0, "the path carried nothing: the comparison proved nothing");
        Assert.Equal(blocks, EngineTapState.AlignedBlocks - alignedBefore);
        Assert.True(worst < 1e-6, $"the tap played {worst:E2} away from the block its engine played");
    }

    // ── The older generators: the synth and the granular voice ──────────────────────────────

    /// <summary>
    /// The synthesiser voice: a sine through its filter, the same in both channels, within full scale,
    /// nothing allocated; and an oscillator at 0 Hz with no LFO is silence.
    /// </summary>
    [Fact]
    public void TheSynthVoiceIsBoundedAndAStoppedOscillatorIsSilent()
    {
        var read = Callback(typeof(SynthProcessor));
        var tone = new SynthVoiceState
        {
            WaveType = OpenFPS.Client.AudioEngine.Data.SynthWaveType.Sine, Frequency = 440f,
            FilterCutoff = 1f, FilterResonance = 0.1f, PulseWidth = 0.9f,
        };
        using var dsp = new FakeDsp(tone);
        using var output = new Pinned(Block * 2);
        long allocated = AllocatedOver(() => Run(read, dsp, IntPtr.Zero, output, Block, 0, 2), 8);
        Assert.Equal(0, allocated);
        Assert.Contains(output.Data, v => v != 0f);
        for (int i = 0; i < Block; i++)
        {
            Assert.InRange(output.Data[2 * i], -1f, 1f);
            Assert.Equal(output.Data[2 * i], output.Data[2 * i + 1]);
        }

        var still = new SynthVoiceState { WaveType = OpenFPS.Client.AudioEngine.Data.SynthWaveType.Sine, Frequency = 0f, FilterCutoff = 1f };
        using var quiet = new FakeDsp(still);
        Run(read, quiet, IntPtr.Zero, output, Block, 0, 2);
        Assert.All(output.Data, v => Assert.Equal(0f, v));
    }

    /// <summary>
    /// The granular voice: grains of a silent recording are silence; grains of a constant one, never
    /// overlapping, are each a Hann window of it at the voice's 0.7 trim, so the output peaks at 0.7 of
    /// the source and never above. Nothing allocated.
    /// </summary>
    [Fact]
    public void AGrainIsAHannWindowOfItsSourceAtTheVoicesTrim()
    {
        var read = Callback(typeof(GranularProcessor));
        GranularVoiceState Voice(float level) => new(Enumerable.Repeat(level, Rate).ToArray(), 1, Rate)
        {
            Position = 0.2f, GrainSizeMs = 50f, Density = 10f, Pitch = 1f,
        };
        using var output = new Pinned(Block * 2);

        using (var silent = new FakeDsp(Voice(0f)))
            for (int b = 0; b < 10; b++)
            {
                Run(read, silent, IntPtr.Zero, output, Block, 0, 2);
                Assert.All(output.Data, v => Assert.Equal(0f, v));
            }

        using var dsp = new FakeDsp(Voice(0.5f));
        float peak = 0f;
        int mismatched = 0;
        long allocated = AllocatedOver(() =>
        {
            Run(read, dsp, IntPtr.Zero, output, Block, 0, 2);
            for (int i = 0; i < Block; i++)
            {
                if (output.Data[2 * i] != output.Data[2 * i + 1]) mismatched++;
                peak = MathF.Max(peak, MathF.Abs(output.Data[2 * i]));
            }
        }, Rate / Block);
        Assert.Equal(0, mismatched);
        _o.WriteLine($"peak {peak:F4} against 0.7 x 0.5 = 0.35");
        Assert.Equal(0, allocated);
        Assert.InRange(peak, 0.34f, 0.35f + 1e-5f);
    }

    // ── The ear: wind at the ears, and each voice's loudness shelves ─────────────────────────

    /// <summary>
    /// The wind at the ears: with nobody listening there is no wind, and with a listener standing in a
    /// steady breeze it is finite, two different ears, and nothing allocated.
    /// </summary>
    [Fact]
    public void TheWindAtTheEarsIsSilentWithNobodyThereAndFiniteWithSomebody()
    {
        using var wind = WindField.Hold(WindWeather.Steady(8f, 270f, 0f));
        var read = Callback(typeof(EarWindProcessor));
        var state = new EarWindState(Rate);
        using var dsp = new FakeDsp(state);
        using var output = new Pinned(Block * 2);
        for (int b = 0; b < 8; b++) Run(read, dsp, IntPtr.Zero, output, Block, 0, 2);
        float idle = output.Data.Max(MathF.Abs);
        _o.WriteLine($"no listener: peak {idle}");
        Assert.True(idle < 1e-6f, $"wind at the ears of nobody: {idle}");

        state.SetListener(new EarWindListener(new Vector3(0f, 1.7f, 0f), 1.7f, Vector2.Zero, 0f, 1f));
        double left = 0, right = 0, diff = 0;
        int bad = 0;
        long allocated = AllocatedOver(() =>
        {
            Run(read, dsp, IntPtr.Zero, output, Block, 0, 2);
            for (int i = 0; i < Block; i++)
            {
                float l = output.Data[2 * i], r = output.Data[2 * i + 1];
                if (!float.IsFinite(l) || !float.IsFinite(r)) bad++;
                left += l * (double)l; right += r * (double)r; diff += (l - r) * (double)(l - r);
            }
        }, 40);
        _o.WriteLine($"wind from the left at 8 m/s: left {Db(left, 40 * Block):F1}, right {Db(right, 40 * Block):F1} dBFS");
        Assert.Equal(0, allocated);
        Assert.Equal(0, bad);
        Assert.True(left > 0 && right > 0, "a listener in an 8 m/s wind heard nothing");
        Assert.True(diff > 0, "both ears heard the same wind");
    }

    /// <summary>
    /// The ear stage (EarProcessor): at 0 dB both shelves are exactly the identity; given +6 dB on a
    /// shelf, a tone well inside it comes out by the shelf's own response at that frequency; told a new
    /// target, it moves at SlewDbPerSecond, not at once; and a channel count that does not match its
    /// output passes nothing through rather than guess. Nothing allocated.
    /// </summary>
    [Fact]
    public void TheEarStageIsTheShelvesItWasGiven()
    {
        var read = Callback(typeof(EarProcessor));
        var state = new EarVoiceState { SampleRate = Rate };
        using var dsp = new FakeDsp(state);
        int n = 4800;
        using var input = new Pinned(n * 2);
        using var output = new Pinned(n * 2);

        // Identity at 0 dB.
        var rnd = new Random(3);
        for (int i = 0; i < input.Data.Length; i++) input.Data[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.5f;
        Run(read, dsp, input.Ptr, output, n, 2, 2);
        for (int i = 0; i < input.Data.Length; i++) Assert.Equal(input.Data[i], output.Data[i], 5);

        // The shelves' gains, on tones.
        foreach (var (hz, low, high) in new[] { (40f, 6f, 0f), (16000f, 0f, 6f), (1000f, 6f, 6f) })
        {
            state.Reset();
            state.Snap(low, high);
            for (int i = 0; i < n; i++)
            {
                float v = 0.25f * MathF.Sin(2f * MathF.PI * hz * i / Rate);
                input.Data[2 * i] = v; input.Data[2 * i + 1] = v;
            }
            double ein = 0, eout = 0;
            for (int b = 0; b < 4; b++)
            {
                Run(read, dsp, input.Ptr, output, n, 2, 2);
                if (b < 2) continue;                                   // the filters settled
                for (int i = 0; i < n * 2; i++) { ein += input.Data[i] * (double)input.Data[i]; eout += output.Data[i] * (double)output.Data[i]; }
            }
            float measured = (float)(10 * Math.Log10(eout / ein));
            float law = LoudnessCompensation.ShelfResponseDb(hz, Rate, LoudnessCompensation.LowShelfHz, low, high: false)
                      + LoudnessCompensation.ShelfResponseDb(hz, Rate, LoudnessCompensation.HighShelfHz, high, high: true);
            _o.WriteLine($"{hz} Hz with shelves {low}/{high} dB: {measured:F2} dB, the shelves say {law:F2}");
            Assert.InRange(measured, law - 0.15f, law + 0.15f);
        }

        // The slew: a new target is approached at 6 dB a second.
        state.Reset();
        Run(read, dsp, input.Ptr, output, n, 2, 2);
        state.SetTarget(6f, -6f);
        Run(read, dsp, input.Ptr, output, n, 2, 2);
        float step = EarVoiceState.SlewDbPerSecond * n / Rate;
        Assert.Equal(step, state.LowDb, 4);
        Assert.Equal(-step, state.HighDb, 4);

        long allocated = AllocatedOver(() => Run(read, dsp, input.Ptr, output, n, 2, 2), 8);
        Assert.Equal(0, allocated);

        // A mismatch between what comes in and what goes out: silence, not a guess.
        Run(read, dsp, input.Ptr, output, n / 2, 2, 1);
        for (int i = 0; i < n / 2; i++) Assert.Equal(0f, output.Data[i]);
    }

    /// <summary>
    /// A recording's copy off a rough wall (EchoWashProcessor): its callback hands the mixer exactly what its
    /// state renders, in each channel of a stereo recording; a clean copy (mirror share 1) is the input to
    /// the bit; a channel count that does not match passes nothing rather than guess. Nothing allocated.
    /// </summary>
    [Fact]
    public void AWallsWashIsItsStatesRender()
    {
        var read = Callback(typeof(EchoWashProcessor));
        var state = new EchoWashState();
        var twin = new EchoWashState();
        state.Configure(0.45f, MathF.Sqrt(0.55f), 5, Rate);
        twin.Configure(0.45f, MathF.Sqrt(0.55f), 5, Rate);
        using var dsp = new FakeDsp(state);
        int n = Block;
        using var input = new Pinned(n * 2);
        using var output = new Pinned(n * 2);
        var rnd = new Random(9);
        for (int i = 0; i < input.Data.Length; i++) input.Data[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.5f;
        var expect = new float[n * 2];
        for (int b = 0; b < 3; b++)
        {
            Run(read, dsp, input.Ptr, output, n, 2, 2);
            twin.Process(input.Data, expect, 2);
            for (int i = 0; i < expect.Length; i++) Assert.Equal(expect[i], output.Data[i]);
        }

        Assert.Equal(0, AllocatedOver(() => Run(read, dsp, input.Ptr, output, n, 2, 2), 8));

        state.Configure(0.45f, 1f, 5, Rate);
        Run(read, dsp, input.Ptr, output, n, 2, 2);
        for (int i = 0; i < input.Data.Length; i++) Assert.Equal(input.Data[i], output.Data[i]);

        Run(read, dsp, input.Ptr, output, n / 2, 2, 1);
        for (int i = 0; i < n / 2; i++) Assert.Equal(0f, output.Data[i]);
    }

    // ── The master bus: the boundary reflections, the limiter, the dither, the capture ───────

    /// <summary>
    /// The near-field boundary stage on the master bus: with no surface near, the mix passes through
    /// untouched (silence is silence); a block with a NaN in it arriving at the master is silence rather
    /// than the end of the game's sound; and nothing allocates.
    /// </summary>
    [Fact]
    public void TheBoundaryStagePassesTheMixAndStopsANaN()
    {
        var read = Callback(typeof(BoundaryProximityProcessor));
        var state = new BoundaryVoiceState(Rate);
        using var dsp = new FakeDsp(state);
        using var input = new Pinned(Block * 2);
        using var output = new Pinned(Block * 2);
        Run(read, dsp, input.Ptr, output, Block, 2, 2);
        Assert.All(output.Data, v => Assert.Equal(0f, v));

        var rnd = new Random(5);
        for (int i = 0; i < input.Data.Length; i++) input.Data[i] = (float)(rnd.NextDouble() * 2 - 1);
        long allocated = AllocatedOver(() => Run(read, dsp, input.Ptr, output, Block, 2, 2), 8);
        Assert.Equal(input.Data, output.Data);
        Assert.Equal(0, allocated);

        input.Data[17] = float.NaN;
        Run(read, dsp, input.Ptr, output, Block, 2, 2);
        Assert.All(output.Data, v => Assert.Equal(0f, v));
        Assert.Equal(1, state.NonFiniteInputReported);
    }

    /// <summary>
    /// The master limiter's callback is its limiter: it writes exactly what a twin TruePeakLimiter makes
    /// of the same input, silence stays silence, and nothing allocates. With no state it passes the mix
    /// through: this unit is on the master, and silencing it would silence the game.
    /// </summary>
    [Fact]
    public void TheMasterLimitersCallbackIsItsLimiter()
    {
        var read = Callback(typeof(MasterLimiter));
        var limiter = (MasterLimiter)Activator.CreateInstance(typeof(MasterLimiter), BindingFlags.NonPublic | BindingFlags.Instance,
                                                              null, new object[] { Rate, 7f }, null)!;
        var twin = new TruePeakLimiter(Rate, TruePeakLimiter.DefaultCeilingDb, 7f);
        using var dsp = new FakeDsp(limiter);
        using var input = new Pinned(Block * 2);
        using var output = new Pinned(Block * 2);
        var direct = new float[Block * 2];

        Run(read, dsp, input.Ptr, output, Block, 2, 2);
        twin.Process(input.Data, direct, 2);
        Assert.All(output.Data, v => Assert.Equal(0f, v));

        long allocated = 0;
        for (int b = 0; b < 16; b++)
        {
            // A tone pushed 6 dB over the ceiling, so the limiter works.
            for (int i = 0; i < Block; i++)
            {
                float v = 1.8f * MathF.Sin(2f * MathF.PI * 220f * (b * Block + i) / Rate);
                input.Data[2 * i] = v; input.Data[2 * i + 1] = v;
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run(read, dsp, input.Ptr, output, Block, 2, 2);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            twin.Process(input.Data, direct, 2);
            Assert.Equal(direct, output.Data);
        }
        Assert.Equal(0, allocated);

        using var none = new FakeDsp(null);
        Run(read, none, input.Ptr, output, Block, 2, 2);
        Assert.Equal(input.Data, output.Data);
    }

    /// <summary>
    /// The dither: what it adds is triangular, at most one sixteen-bit step either way, mean zero and
    /// one step over the square root of six RMS (two independent uniforms of a step each, differenced),
    /// about -98 dBFS RMS — and the same on a signal as on silence. Nothing allocated. With no state, the
    /// mix passes through as it came.
    /// </summary>
    [Fact]
    public void TheDitherIsOneTriangularStep()
    {
        const float step = 1f / 32768f;
        var read = Callback(typeof(MasterDither));
        var dither = new MasterDither();
        using var dsp = new FakeDsp(dither);
        using var input = new Pinned(Block * 2);
        using var output = new Pinned(Block * 2);

        foreach (float level in new[] { 0f, 0.3f })
        {
            for (int i = 0; i < input.Data.Length; i++) input.Data[i] = level * MathF.Sin(i * 0.01f);
            double sum = 0, sumSq = 0;
            float worst = 0f;
            int blocks = 64;
            long allocated = AllocatedOver(() =>
            {
                Run(read, dsp, input.Ptr, output, Block, 2, 2);
                for (int i = 0; i < input.Data.Length; i++)
                {
                    double d = output.Data[i] - (double)input.Data[i];
                    sum += d; sumSq += d * d;
                    worst = MathF.Max(worst, MathF.Abs((float)d));
                }
            }, blocks);
            int count = blocks * Block * 2;
            double rms = Math.Sqrt(sumSq / count), mean = sum / count;
            _o.WriteLine($"on a signal of {level}: dither RMS {20 * Math.Log10(rms):F2} dBFS ({rms / step:F4} steps), mean {mean / step:F5} steps, worst {worst / step:F3} steps");
            Assert.Equal(0, allocated);
            Assert.InRange(rms / step, 1 / Math.Sqrt(6) * 0.98, 1 / Math.Sqrt(6) * 1.02);
            Assert.InRange(mean / step, -0.01, 0.01);
            Assert.True(worst <= step * 1.0001f, $"a dither of {worst / step} steps");
        }

        using var none = new FakeDsp(null);
        Run(read, none, input.Ptr, output, Block, 2, 2);
        Assert.Equal(input.Data, output.Data);
    }

    /// <summary>
    /// The capture tap (MasterTap) passes the mix through untouched — a diagnostic must never be able to
    /// change what is heard — and what it writes to its file is exactly what went past.
    /// </summary>
    [Fact]
    public void TheCaptureTapPassesTheMixAndWritesWhatWentPast()
    {
        var read = Callback(typeof(MasterTap));
        string path = Path.Combine(Path.GetTempPath(), $"openfps-tap-test-{Guid.NewGuid():N}.wav");
        try
        {
            var tap = (MasterTap)Activator.CreateInstance(typeof(MasterTap), BindingFlags.NonPublic | BindingFlags.Instance,
                                                          null, new object[] { path, Rate, true }, null)!;
            using var input = new Pinned(Block * 2);
            using var output = new Pinned(Block * 2);
            var sent = new List<float>();
            using (var dsp = new FakeDsp(tap))
            {
                var rnd = new Random(9);
                for (int b = 0; b < 4; b++)
                {
                    for (int i = 0; i < input.Data.Length; i++) input.Data[i] = (float)(rnd.NextDouble() * 2.4 - 1.2);
                    Run(read, dsp, input.Ptr, output, Block, 2, 2);
                    Assert.Equal(input.Data, output.Data);
                    sent.AddRange(input.Data);
                }
                tap.Dispose();
            }
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(3, BitConverter.ToInt16(bytes, 20));          // IEEE float
            Assert.Equal(2, BitConverter.ToInt16(bytes, 22));          // stereo
            Assert.Equal(Rate, BitConverter.ToInt32(bytes, 24));
            Assert.Equal(sent.Count * 4, BitConverter.ToInt32(bytes, 40));
            for (int i = 0; i < sent.Count; i++) Assert.Equal(sent[i], BitConverter.ToSingle(bytes, 44 + 4 * i));
        }
        finally { File.Delete(path); }
    }

    // ── Steam Audio's stages, where they run without the native library ──────────────────────

    /// <summary>
    /// The binaural stage (SteamAudioDsp) without Steam Audio: a block of a size the effect was not made
    /// for is silence rather than a misuse of the effect; no input is silence; and a block with a NaN in
    /// its input is silence, the voice's input named once and its stage reset. Nothing allocated. (The
    /// HRTF itself needs the native library.)
    /// </summary>
    [Fact]
    public void TheBinauralStageIsSilentWhereItCannotPlaceAndStopsANaN()
    {
        var state = new SteamAudioVoiceState { FrameSize = 512, MonoScratch = new float[512], StereoScratch = new float[1024] };
        using var dsp = new FakeDsp(state);
        using var input = new Pinned(Block);
        using var output = new Pinned(Block * 2);
        using var inArray = new BufferArray(1, input.Ptr);
        using var outArray = new BufferArray(2, output.Ptr);

        RESULT Process(bool idle = false)
        {
            var i = inArray.Array; var o = outArray.Array;
            return SteamAudioDsp.ProcessCallback(ref dsp.State, Block, ref i, ref o, idle, DSP_PROCESS_OPERATION.PROCESS_PERFORM);
        }

        for (int i = 0; i < Block; i++) input.Data[i] = 0.5f * MathF.Sin(i * 0.05f);
        Array.Fill(output.Data, 9f);
        Assert.Equal(RESULT.OK, Process());
        Assert.All(output.Data, v => Assert.Equal(0f, v));          // 1024 against an effect of 512
        long allocated = AllocatedOver(() => Process(), 8);
        Assert.Equal(0, allocated);

        // Idle input is taken as silence.
        Process(idle: true);
        Assert.All(input.Data, v => Assert.Equal(0f, v));

        // No input at all.
        using (var noInput = new BufferArray(1, IntPtr.Zero))
        {
            Array.Fill(output.Data, 9f);
            var i = noInput.Array; var o = outArray.Array;
            SteamAudioDsp.ProcessCallback(ref dsp.State, Block, ref i, ref o, false, DSP_PROCESS_OPERATION.PROCESS_PERFORM);
            Assert.All(output.Data, v => Assert.Equal(0f, v));
        }

        input.Data[3] = float.NaN;
        Array.Fill(output.Data, 9f);
        Process();
        Assert.All(output.Data, v => Assert.Equal(0f, v));
        Assert.Equal(1, state.NonFiniteInputReported);
    }

    /// <summary>
    /// The ambisonic bed without Steam Audio: a bed with no decoder, or a block of the wrong size, is
    /// silence, and the bed says why it bailed. Nothing allocated. (The decode needs the native library.)
    /// </summary>
    [Fact]
    public void AnAmbisonicBedWithNoDecoderIsSilentAndSaysWhy()
    {
        var read = Callback(typeof(AmbisonicBedDsp));
        var bed = new AmbisonicBedState { FrameSize = Block, Channels = 4, Order = 1, SourceSampleRate = Rate, Pcm = Enumerable.Repeat(0.3f, 4 * Rate).ToArray() };
        using var dsp = new FakeDsp(bed);
        using var output = new Pinned(Block * 2);
        Array.Fill(output.Data, 9f);
        Run(read, dsp, IntPtr.Zero, output, Block, 0, 2);
        Assert.All(output.Data, v => Assert.Equal(0f, v));
        Assert.Equal(2, bed.Bailed);                                   // no decode effect
        Run(read, dsp, IntPtr.Zero, output, Block / 2, 0, 2);
        Assert.Equal(1, bed.Bailed);                                   // not the effect's block
        Assert.Equal(0, AllocatedOver(() => Run(read, dsp, IntPtr.Zero, output, Block, 0, 2), 8));
    }

    /// <summary>
    /// The traced reverb stage without a trace: silence, counted as a bail; and a send with a NaN in it
    /// is silence, the input named once. Nothing allocated. (The convolution and the decode need the
    /// native library.)
    /// </summary>
    [Fact]
    public void TheTracedReverbStageWithoutATraceIsSilentAndStopsANaN()
    {
        var read = Callback(typeof(TracedReverbDsp), "Read");
        var stage = new TracedReverbState { FrameSize = Block, SubFrame = 256, SampleRate = Rate, Region = 7 };
        using var dsp = new FakeDsp(stage);
        using var input = new Pinned(Block * 2);
        using var output = new Pinned(Block * 2);
        for (int i = 0; i < input.Data.Length; i++) input.Data[i] = 0.2f;
        Array.Fill(output.Data, 9f);
        Run(read, dsp, input.Ptr, output, Block, 2, 2);
        Assert.All(output.Data, v => Assert.Equal(0f, v));
        Assert.Equal(1, stage.Bailed);
        Assert.Equal(0, AllocatedOver(() => Run(read, dsp, input.Ptr, output, Block, 2, 2), 8));

        input.Data[5] = float.PositiveInfinity;
        Run(read, dsp, input.Ptr, output, Block, 2, 2);
        Assert.All(output.Data, v => Assert.Equal(0f, v));
        Assert.Equal(1, stage.NonFiniteInputReported);
    }

    /// <summary>
    /// A traced echo's capture: the voice passes through untouched, and the copy kept for the echo is
    /// the voice's own block, down to one channel, at the rig's input gain — ramped across the block from
    /// the last block's gain rather than stepped. An idle rig captures nothing. The mix, with no traced
    /// echoes running, passes the voice through. Nothing allocated.
    /// </summary>
    [Fact]
    public void ATracedEchoCapturesItsVoiceAtItsGainAndPassesItThrough()
    {
        var capture = Callback(typeof(TracedEchoDsp), "CaptureRead");
        var mix = Callback(typeof(TracedEchoDsp), "MixRead");
        var rig = new TracedEchoRig { FrameSize = Block, Capture = new float[Block], Slot = -1, InputGain = 2f };
        using var dsp = new FakeDsp(rig);
        using var input = new Pinned(Block * 2);
        using var output = new Pinned(Block * 2);
        for (int i = 0; i < Block; i++) { input.Data[2 * i] = 0.1f; input.Data[2 * i + 1] = 0.3f; }

        // Idle: through, nothing captured.
        Run(capture, dsp, input.Ptr, output, Block, 2, 2);
        Assert.Equal(input.Data, output.Data);
        Assert.False(rig.Fresh);
        Assert.All(rig.Capture, v => Assert.Equal(0f, v));

        // Given a slot: the first block at its own gain, the next ramped to a new one.
        rig.Slot = 0;
        Run(capture, dsp, input.Ptr, output, Block, 2, 2);
        Assert.Equal(input.Data, output.Data);
        Assert.True(rig.Fresh);
        Assert.All(rig.Capture, v => Assert.Equal(0.2f * 2f, v, 6));
        rig.InputGain = 1f;
        Run(capture, dsp, input.Ptr, output, Block, 2, 2);
        Assert.Equal(0.2f * (2f - 1f / Block), rig.Capture[0], 5);
        Assert.Equal(0.2f * 1f, rig.Capture[Block - 1], 5);
        for (int i = 1; i < Block; i++) Assert.True(rig.Capture[i] <= rig.Capture[i - 1] + 1e-7f, "the gain stepped");

        // The mix with nothing traced: the voice through.
        Array.Fill(output.Data, 9f);
        Run(mix, dsp, input.Ptr, output, Block, 2, 2);
        Assert.Equal(input.Data, output.Data);

        Assert.Equal(0, AllocatedOver(() =>
        {
            Run(capture, dsp, input.Ptr, output, Block, 2, 2);
            Run(mix, dsp, input.Ptr, output, Block, 2, 2);
        }, 8));
    }

    // ── Your own voice in the room ───────────────────────────────────────────────────────────

    /// <summary>A surface answering a microphone that has never written anything is silence, and the
    /// read allocates nothing.</summary>
    [Fact]
    public void ARoomAnsweringASilentMicrophoneIsSilent()
    {
        var read = Callback(typeof(OwnVoiceProcessor));
        var tap = new OwnVoiceTap(new OwnVoiceRing(), 0.01f, Rate);
        using var dsp = new FakeDsp(tap);
        using var output = new Pinned(Block * 2);
        Array.Fill(output.Data, 9f);
        float loudest = 0f;
        long allocated = AllocatedOver(() =>
        {
            Run(read, dsp, IntPtr.Zero, output, Block, 0, 2);
            foreach (float v in output.Data) loudest = MathF.Max(loudest, MathF.Abs(v));
        }, 8);
        Assert.Equal(0f, loudest);
        Assert.Equal(0, allocated);
    }

    // ── Every callback, with no state behind it ─────────────────────────────────────────────

    /// <summary>
    /// A callback whose state is gone (a released voice FMOD calls once more) writes silence — or, on
    /// the master, passes the mix through — and does not throw: an exception on the mixer thread aborts
    /// the process. A generator that left its buffer as it found it would replay a stale block.
    /// </summary>
    [Theory]
    [InlineData(typeof(MachineProcessor), "ReadCallback", false)]
    [InlineData(typeof(EngineProcessor), "ReadCallback", false)]
    [InlineData(typeof(TapProcessor), "ReadCallback", false)]
    [InlineData(typeof(EchoProcessor), "ReadCallback", false)]
    [InlineData(typeof(SynthProcessor), "ReadCallback", false)]
    [InlineData(typeof(GranularProcessor), "ReadCallback", false)]
    [InlineData(typeof(EarWindProcessor), "ReadCallback", false)]
    [InlineData(typeof(OwnVoiceProcessor), "ReadCallback", false)]
    [InlineData(typeof(AmbisonicBedDsp), "ReadCallback", false)]
    [InlineData(typeof(TracedReverbDsp), "Read", false)]
    [InlineData(typeof(EarProcessor), "ReadCallback", true)]
    [InlineData(typeof(BoundaryProximityProcessor), "ReadCallback", true)]
    [InlineData(typeof(MasterLimiter), "ReadCallback", true)]
    [InlineData(typeof(MasterDither), "ReadCallback", true)]
    [InlineData(typeof(MasterTap), "ReadCallback", true)]
    [InlineData(typeof(TracedEchoDsp), "CaptureRead", true)]
    [InlineData(typeof(TracedEchoDsp), "MixRead", true)]
    public void ACallbackWithNoStateIsSilentOrPassesTheMixThrough(Type processor, string method, bool passes)
    {
        var read = Callback(processor, method);
        using var dsp = new FakeDsp(null);
        using var input = new Pinned(Block * 2);
        using var output = new Pinned(Block * 2);
        for (int i = 0; i < input.Data.Length; i++) input.Data[i] = 0.25f;
        Array.Fill(output.Data, 9f);
        int outCh = 2;
        var r = read(ref dsp.State, input.Ptr, output.Ptr, Block, 2, ref outCh);
        Assert.Equal(RESULT.OK, r);
        if (passes) Assert.Equal(input.Data, output.Data);
        else Assert.All(output.Data, v => Assert.Equal(0f, v));
    }

    /// <summary>
    /// NOTHING MAY ESCAPE A DSP CALLBACK: an exception that reaches FMOD's native mixer thread aborts the
    /// process. A callback handed the wrong kind of state (its cast throws, as a stale handle's Target
    /// does) answers OK with a silent block, and records the fault for the game thread to log rather than
    /// logging on the mixer thread.
    /// </summary>
    [Theory]
    [InlineData(typeof(MachineProcessor), "MachineProcessor")]
    [InlineData(typeof(EngineProcessor), "EngineProcessor")]
    [InlineData(typeof(TapProcessor), "TapProcessor")]
    [InlineData(typeof(EchoProcessor), "EchoProcessor")]
    [InlineData(typeof(SynthProcessor), "SynthProcessor")]
    [InlineData(typeof(GranularProcessor), "GranularProcessor")]
    [InlineData(typeof(OwnVoiceProcessor), "OwnVoiceProcessor")]
    public void AFaultInACallbackIsASilentBlockAndALineForTheGameThread(Type processor, string name)
    {
        var read = Callback(processor);
        DspFault.TryDrain(out _, out _);                               // whatever earlier tests left
        using var dsp = new FakeDsp("not a voice");
        using var output = new Pinned(Block * 2);
        Array.Fill(output.Data, 9f);
        int outCh = 2;
        var r = read(ref dsp.State, IntPtr.Zero, output.Ptr, Block, 0, ref outCh);
        Assert.Equal(RESULT.OK, r);
        Assert.All(output.Data, v => Assert.Equal(0f, v));
        Assert.True(DspFault.TryDrain(out int count, out string? first));
        Assert.Equal(1, count);
        Assert.StartsWith(name, first);
    }

    // ── The stand-in for FMOD ───────────────────────────────────────────────────────────────

    /// <summary>A processor's read callback, as FMOD is handed it.</summary>
    private static DSP_READ_CALLBACK Callback(Type processor, string method = "ReadCallback")
    {
        var m = processor.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(processor.Name, method);
        return (DSP_READ_CALLBACK)Delegate.CreateDelegate(typeof(DSP_READ_CALLBACK), m);
    }

    private static void Run(DSP_READ_CALLBACK read, FakeDsp dsp, IntPtr input, Pinned output, int frames, int inChannels, int outChannels)
    {
        int outCh = outChannels;
        // No Assert here: it allocates, and this runs inside the allocation measurements.
        if (read(ref dsp.State, input, output.Ptr, (uint)frames, inChannels, ref outCh) != RESULT.OK)
            throw new InvalidOperationException("a callback answered other than OK");
    }

    /// <summary>Bytes this thread allocated over <paramref name="times"/> runs, after one run to warm up
    /// (the JIT, the callback table's cached accessor).</summary>
    private static long AllocatedOver(Action run, int times)
    {
        run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < times; i++) run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static double Db(double energy, int count) => 10 * Math.Log10(energy / count + 1e-30);

    /// <summary>A float buffer at a fixed address, as the mixer's are.</summary>
    private sealed class Pinned : IDisposable
    {
        public readonly float[] Data;
        private GCHandle _pin;
        public Pinned(int length) { Data = new float[length]; _pin = GCHandle.Alloc(Data, GCHandleType.Pinned); }
        public IntPtr Ptr => _pin.AddrOfPinnedObject();
        public void Dispose() { if (_pin.IsAllocated) _pin.Free(); }
    }

    /// <summary>One FMOD buffer array (a process callback's input or output): a channel count, a mask
    /// and a buffer pointer, in unmanaged memory.</summary>
    private sealed class BufferArray : IDisposable
    {
        private readonly IntPtr _channels = Marshal.AllocHGlobal(sizeof(int));
        private readonly IntPtr _mask = Marshal.AllocHGlobal(sizeof(int));
        private readonly IntPtr _buffers = Marshal.AllocHGlobal(IntPtr.Size);

        public BufferArray(int channels, IntPtr buffer)
        {
            Marshal.WriteInt32(_channels, channels);
            Marshal.WriteInt32(_mask, 0);
            Marshal.WriteIntPtr(_buffers, buffer);
        }

        public DSP_BUFFER_ARRAY Array => new()
        {
            numbuffers = 1, buffernumchannels = _channels, bufferchannelmask = _mask, buffers = _buffers,
            speakermode = SPEAKERMODE.DEFAULT,
        };

        public void Dispose()
        {
            Marshal.FreeHGlobal(_channels);
            Marshal.FreeHGlobal(_mask);
            Marshal.FreeHGlobal(_buffers);
        }
    }
}

/// <summary>
/// What FMOD hands a DSP callback, without FMOD: a DSP_STATE whose function table answers the two
/// questions this engine's callbacks ask through it — the unit's user data (the GCHandle of its state,
/// kept here in <c>plugindata</c>) and the channel's clock.
/// </summary>
internal sealed class FakeDsp : IDisposable
{
    private static readonly DSP_GETUSERDATA_FUNC GetUserData = UserDataOf;
    private static readonly DSP_GETCLOCK_FUNC GetClock = ClockOf;
    private static readonly IntPtr Table = MakeTable();

    /// <summary>The clock the next callback reads, samples.</summary>
    public static ulong Clock;

    public DSP_STATE State;
    private GCHandle _handle;

    public FakeDsp(object? target)
    {
        IntPtr user = IntPtr.Zero;
        if (target != null) { _handle = GCHandle.Alloc(target); user = GCHandle.ToIntPtr(_handle); }
        object boxed = new DSP_STATE { plugindata = user };
        typeof(DSP_STATE).GetField("functions_internal", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(boxed, Table);
        State = (DSP_STATE)boxed;
    }

    private static RESULT UserDataOf(ref DSP_STATE state, out IntPtr userdata)
    {
        userdata = state.plugindata;
        return RESULT.OK;
    }

    private static RESULT ClockOf(ref DSP_STATE state, out ulong clock, out uint offset, out uint length)
    {
        clock = Clock; offset = 0; length = 0;
        return RESULT.OK;
    }

    private static IntPtr MakeTable()
    {
        var fns = new DSP_STATE_FUNCTIONS { getuserdata = GetUserData, getclock = GetClock };
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<DSP_STATE_FUNCTIONS>());
        Marshal.StructureToPtr(fns, p, false);
        return p;
    }

    public void Dispose()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
