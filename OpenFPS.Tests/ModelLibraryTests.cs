using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The models are data, and this is the proof.
///
/// The claim `ModelLibrary` makes is that a train, a horn, a whistle, a bell and an air system can
/// leave C# and live in a file an author writes — so the test is the round trip: every built-in
/// model must survive being written out and read back with not one number changed. If a spec ever
/// grows a field the serializer cannot carry (a computed property, an interface, a tuple), this is
/// what catches it, and it catches it for every model at once rather than for the one somebody
/// happened to try.
/// </summary>
public class ModelLibraryTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    public ModelLibraryTests(ITestOutputHelper o) { _o = o; ModelLibrary.Clear(); }
    public void Dispose() => ModelLibrary.Clear();

    [Fact]
    public void EveryBuiltInModelSurvivesTheRoundTrip()
    {
        int checked_ = 0;
        foreach (string kind in ModelLibrary.AllKinds)
        {
            foreach (string id in ModelLibrary.Ids(kind).ToList())
            {
                object spec = kind switch
                {
                    ModelLibrary.Kinds.Train => ModelLibrary.Train(id),
                    ModelLibrary.Kinds.RailVehicle => ModelLibrary.RailVehicle(id),
                    ModelLibrary.Kinds.Track => ModelLibrary.Track(id),
                    ModelLibrary.Kinds.Horn => ModelLibrary.Horn(id),
                    ModelLibrary.Kinds.Whistle => ModelLibrary.Whistle(id),
                    ModelLibrary.Kinds.Bell => ModelLibrary.Bell(id),
                    ModelLibrary.Kinds.Air => ModelLibrary.Air(id),
                    ModelLibrary.Kinds.SmallMachine => ModelLibrary.SmallMachine(id),
                    ModelLibrary.Kinds.Water => ModelLibrary.Water(id),
                    ModelLibrary.Kinds.Fire => ModelLibrary.Fire(id),
                    ModelLibrary.Kinds.Foliage => ModelLibrary.Foliage(id),
                    ModelLibrary.Kinds.Flow => ModelLibrary.Flow(id),
                    ModelLibrary.Kinds.Shore => ModelLibrary.Shore(id),
                    ModelLibrary.Kinds.Engine => ModelLibrary.Get<EngineProfile>(kind, id),
                    ModelLibrary.Kinds.Vehicle => ModelLibrary.Get<VehicleSpec>(kind, id),
                    _ => throw new InvalidOperationException($"no accessor for kind '{kind}'"),
                };
                bool ok = ModelLibrary.RoundTrips(kind, spec, out string before, out string after);
                if (!ok)
                {
                    _o.WriteLine($"{kind}:{id} DID NOT round trip");
                    _o.WriteLine("before: " + before);
                    _o.WriteLine("after:  " + after);
                }
                Assert.True(ok, $"{kind}:{id} did not survive being written out and read back");

                // The text agreeing is not enough: a field the writer skips is missing from both
                // sides of it. What the model READ BACK reports must be what the library's does,
                // every property, the ones worked out from the others included.
                // An engine's "worked out from the rest" is NaN, which JSON writes as a named literal.
                var back = JsonSerializer.Deserialize(before, spec.GetType(),
                    new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals })!;
                var lost = Readings(spec).Except(Readings(back)).ToList();
                foreach (var l in lost) _o.WriteLine($"{kind}:{id} lost {l}");
                Assert.True(lost.Count == 0, $"{kind}:{id} read back differently: {string.Join("; ", lost.Take(4))}");
                checked_++;
            }
        }
        _o.WriteLine($"{checked_} models across {ModelLibrary.AllKinds.Count()} kinds");
        Assert.True(checked_ >= 25, $"only {checked_} models — did a family stop being registered?");
    }

    /// <summary>
    /// Every public property of a model, as "path=value" lines, down through its records and lists.
    /// Computed properties too: they are what the synthesis reads, so they are what has to survive.
    /// </summary>
    private static IEnumerable<string> Readings(object? o, string path = "", int depth = 0)
    {
        if (o == null) { yield return path + "=null"; yield break; }
        var t = o.GetType();
        if (t.IsPrimitive || t.IsEnum || o is string || o is decimal || o is Vector3)
        {
            yield return path + "=" + Convert.ToString(o, CultureInfo.InvariantCulture);
            yield break;
        }
        if (depth > 8) yield break;
        if (o is IEnumerable list)
        {
            int i = 0;
            foreach (var e in list)
                foreach (var r in Readings(e, $"{path}[{i++}]", depth + 1)) yield return r;
            yield break;
        }
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            foreach (var r in Readings(p.GetValue(o), path + "." + p.Name, depth + 1)) yield return r;
        }
    }

    [Fact]
    public void AnAuthoredModelOverridesTheBuiltInOfTheSameName()
    {
        // The whole point of the library: a map replaces the crossing bell without editing the
        // library, and everything that asks for "crossing_gong" gets the author's.
        var stock = ModelLibrary.Bell("crossing_gong");
        var mine = stock with { Name = "a much bigger gong", DiameterMetres = 0.40f, StrikesPerSecond = 1.4f };
        ModelLibrary.Add(ModelLibrary.Kinds.Bell, "crossing_gong", mine);

        var got = ModelLibrary.Bell("crossing_gong");
        Assert.Equal(0.40f, got.DiameterMetres);
        Assert.Equal("a much bigger gong", got.Name);
        _o.WriteLine($"stock {stock.DiameterMetres * 1000f:F0} mm -> authored {got.DiameterMetres * 1000f:F0} mm");

        // And it is still listed once, not twice.
        Assert.Single(ModelLibrary.Ids(ModelLibrary.Kinds.Bell).Where(
            i => i.Equals("crossing_gong", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A road vehicle's air horn comes from the library, as a train's does: a horn an author writes
    /// is the one a bus or a lorry blows, at its own level. Both looked air horns up among the
    /// built-in presets only, so an authored horn never reached the road: its honk was 110 dB, the
    /// level for a horn nobody knows, and its voice could not be made at all.
    /// </summary>
    [Fact]
    public void AnAuthoredHornReachesARoadVehicle()
    {
        var mine = ChimeHornSpec.BusAirHorn with { Name = "a bus horn somebody wrote", ReferenceDb = 121f };
        ModelLibrary.Add(ModelLibrary.Kinds.Horn, "authored_bus_horn", mine);

        Assert.Equal(121f, Honk.LevelDb("air:authored_bus_horn"));
        var voice = new HornVoiceState("air:authored_bus_horn", new[] { 0.3f }, 44100, 1);
        var stock = new HornVoiceState("air:bus_horn", new[] { 0.3f }, 44100, 1);
        // Full scale is the horn's level plus the shared headroom, so it shows whose level it took.
        Assert.Equal(121f - ChimeHornSpec.BusAirHorn.ReferenceDb,
                     20f * MathF.Log10(voice.PascalsAtFullScale / stock.PascalsAtFullScale), 3);
        var block = new float[512];
        double e = 0;
        for (int i = 0; i < 40; i++) { voice.Render(block); foreach (float x in block) e += x * x; }
        Assert.True(e > 0, "the authored horn made no sound");
    }

    [Fact]
    public void AModelLoadsFromAFileTheWayAMapAuthorWouldWriteOne()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Three lines of a horn, by hand — not an export, not a copy of the library.
            File.WriteAllText(Path.Combine(dir, "k1.json"), """
            {
              "kind": "horn",
              "id": "single_chime",
              "spec": {
                "Name": "one bell, and nothing to hide behind",
                "Bells": [ { "LengthMetres": 0.62, "MouthDiameterMetres": 0.12 } ],
                "ReferenceDb": 134
              }
            }
            """);
            int loaded = ModelLibrary.Load(dir);
            Assert.Equal(1, loaded);

            var horn = ModelLibrary.Horn("single_chime");
            Assert.Single(horn.Bells);
            // The note was never written down: it is c/2L of what the author DID write.
            float expect = 343f / (2f * (0.62f + 0.6f * 0.06f));
            _o.WriteLine($"620 mm bell -> {horn.Bells[0].Hz:F0} Hz (c/2L says {expect:F0})");
            Assert.InRange(horn.Bells[0].Hz, expect * 0.99f, expect * 1.01f);
            // And everything the author did not write kept its default.
            Assert.Equal(ChimeHornSpec.NathanK5LA.ReedRatio, horn.ReedRatio);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ABadFileIsNamedAndSteppedOverRatherThanFatal()
    {
        // One broken model in an author's folder must not take the rest of the map's sounds with it.
        string dir = Path.Combine(Path.GetTempPath(), "openfps-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ this is not json");
            File.WriteAllText(Path.Combine(dir, "wrong-kind.json"), """{ "kind": "submarine", "id": "x", "spec": {} }""");
            File.WriteAllText(Path.Combine(dir, "good.json"), """
            { "kind": "track", "id": "branch_line",
              "spec": { "Name": "a branch line", "RailKgPerMetre": 45, "JointSpacingMetres": 18.29 } }
            """);
            int loaded = ModelLibrary.Load(dir);
            Assert.Equal(1, loaded);
            Assert.True(ModelLibrary.Knows(ModelLibrary.Kinds.Track, "branch_line"));
            var t = ModelLibrary.Track("branch_line");
            // Sixty-foot rail, so the bangs come further apart than on thirty-nine foot.
            Assert.True(t.JointSpacingMetres > TrackSpec.JointedTimber.JointSpacingMetres);
            _o.WriteLine($"branch line: {t.JointSpacingMetres:F2} m rails, pinned-pinned {t.PinnedPinnedHz:F0} Hz");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExportWritesTheWholeLibraryInTheFormTheLoaderReads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            int written = ModelLibrary.Export(dir);
            _o.WriteLine($"exported {written} models to {dir}");
            Assert.True(written >= 25);

            // And the export reads back as itself — which is the only way an author can trust it as
            // something to copy from.
            ModelLibrary.Clear();
            int loaded = ModelLibrary.Load(dir);
            Assert.Equal(written, loaded);
            // A Genesis and six coaches, still, after a trip through a file.
            var amtrak = ModelLibrary.Train("amtrak");
            Assert.Equal(1, amtrak.Consist[0].Count);
            Assert.Equal(6, amtrak.Consist[^1].Count);
            Assert.Equal(0.915f, amtrak.Consist[^1].Vehicle.Wheels.DiameterMetres);
            Assert.Equal(5, ModelLibrary.Train("steam").Consist[^1].Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void AConsistIsWritableByHand()
    {
        // It used to be an array of tuples, which serializes to a row of empty objects, so a train
        // was the one model an author could not write. A named pair fixes that and costs nothing.
        var amtrak = TrainProfile.AmtrakDiesel;
        var (vehicle, count) = amtrak.Consist[1];
        Assert.Equal(6, count);
        Assert.Equal(TrainProfile.PassengerCoach.Name, vehicle.Name);
        Assert.True(ModelLibrary.RoundTrips(ModelLibrary.Kinds.Train, amtrak, out string json, out _));
        Assert.Contains("\"Vehicle\"", json);
        Assert.Contains("\"Count\": 6", json);
        _o.WriteLine(json[..Math.Min(400, json.Length)]);
    }
}
