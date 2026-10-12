using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A model sounds the same whatever seed it is given: its seed chooses which noise it draws, never how loud it
/// is or where its energy lies. Each model the mixer seeds from its voice's seed (FmodAudioProvider, with the
/// same arithmetic on the seed as there) is rendered as the voice it had the seed it had before seeds were a
/// hash (its old id, OldVoiceIds), as the same voice with the seed it has now (VoiceRegistry.SeedOf), and as
/// its neighbour, and the three are compared by level in each of nine bands (docs/SPECIAL_CASES_AUDIT.md, P3d
/// step 6). The render fingerprints cannot show this: they render with seeds of their own.
/// </summary>
public class SeedBandLevelTests
{
    private const int Rate = 48000;
    private const int Block = 1024;
    private readonly ITestOutputHelper _o;
    public SeedBandLevelTests(ITestOutputHelper o) => _o = o;

    private static readonly Vector3 Somewhere = new(400f, 0f, -300f);
    private static WorldWeather Weather() => new() { Wind = WindWeather.Steady(4.5f, 270f, 0f) };

    /// <summary>One model as the mixer makes it: what kind of voice it is, how long to listen (a model of rare
    /// events needs longer for its level to settle), and its render from a seed.</summary>
    private sealed record Model(VoiceKind Kind, double Seconds, Func<int, Action<float[]>> Make);

    private static readonly Dictionary<string, Model> Models = new()
    {
        ["engine"] = new(VoiceKind.Engine, 6, seed =>
        {
            var v = new EngineVoiceState(VehicleProfile.V8Muscle, Rate, seed) { TargetSpeed = 15f };
            v.PlaceAtSpeed(15f);
            return b => v.Render(b);
        }),
        ["mower"] = new(VoiceKind.Machine, 12, seed => Of(new MachineVoiceState(SmallMachineSpec.ByName("mower_push"), Rate, seed, unchecked(seed * 31 + 7)) { TargetGroundSpeed = 0.95f })),
        ["airliner"] = new(VoiceKind.Aircraft, 6, seed => Of(new AircraftVoiceState(AircraftProfile.ByName("airliner"), Rate, unchecked(seed * 17 + 3), lever: 1f))),
        ["crossing bell"] = new(VoiceKind.Machine, 8, seed => Of(new BellVoiceState(ModelLibrary.Bell("crossing_gong"), Rate, unchecked(seed * 13 + 5)))),
        ["fountain"] = new(VoiceKind.Machine, 8, seed => Of(new WaterVoiceState(WaterFeatureSpec.ByName("park_fountain"), Rate, unchecked(seed * 37 + 11), Somewhere, Weather()))),
        ["tree"] = new(VoiceKind.Machine, 12, seed => Of(new NaturePlaceState(
            new PlacedNatureVoice("foliage:park_tree", FoliageSpec.ParkTree, ExtendedSources.Layout("foliage:park_tree")?.Length ?? 1, Rate,
                                  unchecked(seed * 43 + 17), Somewhere, Weather()), 0, Rate, Somewhere))),
        ["wood"] = new(VoiceKind.Wood, 12, seed =>
        {
            string key = WoodChorus.KeyFor("pine", 60f, 60f);
            return Of(new NaturePlaceState(
                new PlacedNatureVoice(key, FoliageSpec.ByName("pine"), ExtendedSources.Layout(key)?.Length ?? 1, Rate,
                                      unchecked(seed * 43 + 17), Somewhere, Weather())
                { WindPlaces = ExtendedSources.Layout(key)?[1..], TargetTrees = 9f }, 0, Rate, Somewhere));
        }),
        ["fire"] = new(VoiceKind.Machine, 20, seed => Of(new NaturePlaceState(
            new PlacedNatureVoice("fire:fire_pit", FireSpec.ByName("fire_pit"), ExtendedSources.Layout("fire:fire_pit")?.Length ?? 1, Rate,
                                  unchecked(seed * 41 + 13), Somewhere, Weather()), 0, Rate, Somewhere))),
        ["creek"] = new(VoiceKind.Machine, 12, seed => Of(new NaturePlaceState(
            new PlacedNatureVoice("flow:creek", RunningWaterSpec.Creek, Rate, unchecked(seed * 47 + 19), Somewhere, Weather()), 0, Rate, Somewhere))),
        ["electric horn"] = new(VoiceKind.Horn, 4, seed => Of(new HornVoiceState("electric:disc_pair", new[] { 60f }, Rate, unchecked(seed * 29 + 1)))),
        ["air horn"] = new(VoiceKind.HeldHorn, 4, seed => Of(new HornVoiceState("air:truck_dual", new[] { 60f }, Rate, unchecked(seed * 29 + 1)))),
        // A wall's smear of an engine's echo (EngineEchoState) and of a recording's copy (EchoWashState): the
        // same noise through it under each seed.
        ["wall smear"] = new(VoiceKind.EngineEcho, 4, seed =>
        {
            var smear = new EchoDiffuser(0.6f, seed, Rate);
            var noise = new Random(5);
            return b => { for (int i = 0; i < b.Length; i++) b[i] = smear.Process((float)(noise.NextDouble() * 2 - 1) * 0.25f); };
        }),
    };

    /// <summary>A sea's edge, listened to for five minutes: its waves come in groups (the sum of a wind sea's
    /// components), and forty seconds of it under two seeds are a few waves each, 6.4 dB apart. Over five
    /// minutes they are 0.5 dB apart as a whole and 3.0 dB at 60 to 125 Hz.</summary>
    private static readonly Model Shore = new(VoiceKind.Machine, 300, seed => Of(new NaturePlaceState(
        new PlacedNatureVoice("shore:sea", ShoreSpec.SeaSand, ShoreSpec.SeaSand.DefaultGeometry, Rate, unchecked(seed * 53 + 23), Somewhere, Weather()),
        0, Rate, Somewhere)));

    private static Action<float[]> Of(PhysicalVoiceState voice) => b => voice.Render(b);

    public static IEnumerable<object[]> Names() => Models.Keys.Select(k => new object[] { k });

    /// <summary>The level in each of Spectrum's nine bands, dB, averaged over the render in overlapping
    /// windows, and the level of the whole.</summary>
    private static (double[] Bands, double Whole) Levels(Action<float[]> render, double seconds)
    {
        var block = new float[Block];
        // A second to start in: a spin-up, a first gust, a ring filling.
        for (int b = 0; b < Rate / Block; b++) render(block);
        var x = new float[(int)(seconds * Rate / Block) * Block];
        for (int at = 0; at < x.Length; at += Block)
        {
            render(block);
            block.CopyTo(x, at);
        }
        var energy = new double[Spectrum.BandCount];
        int windows = 0;
        for (int at = 0; at + 4096 <= x.Length; at += 2048, windows++)
        {
            var e = Spectrum.BandEnergy(x.AsSpan(at, 4096), Rate);
            for (int i = 0; i < e.Length; i++) energy[i] += e[i];
        }
        double whole = 0;
        foreach (float v in x)
        {
            if (!float.IsFinite(v)) Assert.Fail("a sample is not finite");
            whole += v * (double)v;
        }
        return (energy.Select(e => 10 * Math.Log10(e / windows + 1e-30)).ToArray(), 10 * Math.Log10(whole / x.Length + 1e-30));
    }

    /// <summary>How far apart two renders are, dB: in the whole, and in the worst of the bands within 30 dB of
    /// the loudest (a band 40 dB down is not heard under the rest, and is a handful of events).</summary>
    private static (double Whole, double WorstBand, int Band) Apart((double[] Bands, double Whole) a, (double[] Bands, double Whole) b)
    {
        double loudest = a.Bands.Max(), worst = 0;
        int band = 0;
        for (int i = 0; i < a.Bands.Length; i++)
        {
            if (a.Bands[i] < loudest - 30) continue;
            double d = Math.Abs(a.Bands[i] - b.Bands[i]);
            if (d > worst) { worst = d; band = i; }
        }
        return (Math.Abs(a.Whole - b.Whole), worst, band);
    }

    private static string Row(string name, (double[] Bands, double Whole) l)
        => $"{name,-16}{l.Whole,7:F1} |" + string.Concat(l.Bands.Select(b => $"{b,7:F1}"));

    /// <summary>The widest any model's level may move between seeds, dB, in the whole and in a band. A band of
    /// few events is a seed's own draw of them: over these lengths a fountain's 125 to 250 Hz, a fire's
    /// crackle (1 to 8 kHz) and a creek's 250 to 500 Hz are up to 3.2 dB apart between two seeds, old or new,
    /// and nothing else is more than 1.2.</summary>
    private const double WholeDb = 1.0, BandDb = 4.0;

    [Theory]
    [MemberData(nameof(Names))]
    public void A_model_is_as_loud_in_every_band_whatever_its_seed(string name) => Compare(name, Models[name]);

    /// <summary>The shore takes minutes to render, so it is asked for: OPENFPS_SEED_BANDS_SHORE=1.</summary>
    [Fact]
    public void A_shore_is_as_loud_in_every_band_whatever_its_seed()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_SEED_BANDS_SHORE") != "1")
        {
            _o.WriteLine("OPENFPS_SEED_BANDS_SHORE is not 1: the shore was not rendered.");
            return;
        }
        Compare("shore", Shore);
    }

    private void Compare(string name, Model model)
    {
        const int owner = 101, neighbour = 102;
        var was = Levels(model.Make(OldVoiceIds.Of(model.Kind, owner)), model.Seconds);
        var now = Levels(model.Make(VoiceRegistry.SeedOf(model.Kind, owner)), model.Seconds);
        var next = Levels(model.Make(VoiceRegistry.SeedOf(model.Kind, neighbour)), model.Seconds);

        _o.WriteLine($"{name}, {model.Kind} of {owner}: seed {OldVoiceIds.Of(model.Kind, owner)} before, {VoiceRegistry.SeedOf(model.Kind, owner)} now; "
                   + $"of {neighbour}: {VoiceRegistry.SeedOf(model.Kind, neighbour)}. {model.Seconds:F0} s, dB:");
        _o.WriteLine($"{"",-16}{"whole",7} |" + string.Concat(Enumerable.Range(0, Spectrum.BandCount).Select(i => $"{Spectrum.BandEdges[i],7:F0}")));
        _o.WriteLine(Row("before", was));
        _o.WriteLine(Row("now", now));
        _o.WriteLine(Row("its neighbour", next));
        var moved = Apart(was, now);
        var between = Apart(now, next);
        _o.WriteLine($"before to now: whole {moved.Whole:F2} dB, worst band {moved.WorstBand:F2} dB ({Spectrum.BandName(moved.Band)}); "
                   + $"now to its neighbour: whole {between.Whole:F2} dB, worst band {between.WorstBand:F2} dB ({Spectrum.BandName(between.Band)})");

        Assert.True(was.Whole > -120, $"{name} rendered nothing: the comparison proved nothing");
        Assert.True(moved.Whole <= WholeDb, $"{name} is {moved.Whole:F2} dB louder or quieter with its new seed");
        Assert.True(moved.WorstBand <= BandDb, $"{name} moved {moved.WorstBand:F2} dB at {Spectrum.BandName(moved.Band)} with its new seed");
        Assert.True(between.Whole <= WholeDb, $"{name} and its neighbour are {between.Whole:F2} dB apart");
        Assert.True(between.WorstBand <= BandDb, $"{name} and its neighbour are {between.WorstBand:F2} dB apart at {Spectrum.BandName(between.Band)}");
    }
}
