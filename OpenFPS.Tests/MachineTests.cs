using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A machine is a parts list.
///
/// The claim being tested is narrow and load-bearing: the vocabulary in <see cref="MachinePart"/> —
/// a model, a profile, an offset, a handful of numbers — can hold everything the built-in vehicle
/// library says, so an author writing a machine in JSON is using the same parts the library is made
/// of rather than a simplified imitation of them. If that is true, taking any preset apart and
/// putting it back together has to return the same vehicle, field for field.
///
/// It is the test that lets the C# factories stop being the only way to describe a car.
/// </summary>
public class MachineTests
{
    public MachineTests() => MachineRegistry.Clear();

    /// <summary>
    /// Every preset, taken apart into parts and reassembled, is the same vehicle.
    ///
    /// Field for field, including the ones nobody would think to check — the panel spans of the
    /// body, the gear ratios, what the tyres squeal at. A missing field here is not a cosmetic loss:
    /// it is a car that sounds different depending on whether it came from C# or from a file.
    /// </summary>
    [Fact]
    public void EveryBuiltInVehicleSurvivesBeingTakenApart()
    {
        foreach (string key in VehicleProfile.Presets.Keys)
        {
            var original = VehicleProfile.ByName(key);
            var rebuilt = MachineRegistry.Assemble(MachineRegistry.Describe(original, key));
            AssertSameVehicle(original, rebuilt, key);
        }
    }

    /// <summary>...and the same again with the parts list written out to JSON and read back, which
    /// is the form an author actually sees.</summary>
    [Fact]
    public void EveryBuiltInVehicleSurvivesARoundTripThroughJson()
    {
        foreach (string key in VehicleProfile.Presets.Keys)
        {
            var original = VehicleProfile.ByName(key);
            string json = MachineRegistry.ToJson(MachineRegistry.Describe(original, key));
            var rebuilt = MachineRegistry.Assemble(MachineRegistry.FromJson(json, key));
            AssertSameVehicle(original, rebuilt, key);
        }
    }

    /// <summary>
    /// Every engine in the library has its own name.
    ///
    /// Which is what makes a vehicle able to say which engine it is holding. A VehicleProfile keeps
    /// an EngineProfile, not the key it was built from, and its own EngineKey is the VEHICLE's key —
    /// the school bus runs a "diesel_bus". The name is the only identity an engine has, and a
    /// duplicate would silently give two cars each other's engine on export.
    /// </summary>
    [Fact]
    public void EveryEngineHasADistinctName()
    {
        var seen = new Dictionary<string, string>();
        foreach (var kv in EngineProfile.Presets)
        {
            string name = kv.Value().Name;
            Assert.False(seen.ContainsKey(name),
                $"Engine presets '{seen.GetValueOrDefault(name)}' and '{kv.Key}' are both called \"{name}\".");
            seen[name] = kv.Key;
        }
    }

    /// <summary>
    /// A machine written from scratch, in the form the doc proposed: parts, with offsets.
    ///
    /// No base, so the parts have to carry the whole car between them. This is the case that proves
    /// a map author can describe a vehicle that does not exist in C#.
    /// </summary>
    [Fact]
    public void AMachineCanBeAssembledFromPartsAlone()
    {
        var def = MachineRegistry.FromJson("""
        {
          "id": "test_pickup",
          "name": "A pickup somebody wrote",
          "parts": [
            { "model": "engine",  "profile": "diesel_cummins" },
            { "model": "exhaust", "at": [0, 0.45, -2.6], "levelDb": 107 },
            { "model": "intake",  "at": [0, 1.1, 1.8] },
            { "model": "tyres",   "profile": "truck_asphalt" },
            { "model": "body",    "profile": "van" },
            { "model": "gearbox", "series": [4.31, 2.33, 1.52, 1.13],
              "settings": { "finalDrive": 3.73, "wheelRadius": 0.38, "upshiftRpm": 2800 } },
            { "model": "chassis", "settings": { "massKg": 2600, "dragArea": 1.2, "sourceLevelDb": 107 } }
          ]
        }
        """);

        var v = MachineRegistry.Assemble(def);

        Assert.Equal("A pickup somebody wrote", v.Name);
        Assert.Equal(EngineProfile.DieselCumminsI6.Name, v.Engine.Name);
        Assert.Equal(-2.6f, v.ExhaustOffsetZ, 3);
        Assert.Equal(0.45f, v.ExhaustHeight, 3);
        Assert.Equal(1.1f, v.IntakeHeight, 3);
        Assert.Equal(1.8f, v.IntakeOffsetZ, 3);
        Assert.Equal(TyreProfile.TruckOnAsphalt.SquealHz, v.Tyres.SquealHz);
        Assert.Equal(VehicleBody.Van.CabinLengthM, v.Body!.CabinLengthM);
        Assert.Equal(new[] { 4.31f, 2.33f, 1.52f, 1.13f }, v.Gearbox.Ratios);
        Assert.Equal(2800f, v.Gearbox.UpshiftRpm);
        // A setting nobody mentioned keeps the profile's value rather than becoming zero — which is
        // what lets a definition be short.
        Assert.Equal(Gearbox.SixSpeedSports.DownshiftRpm, v.Gearbox.DownshiftRpm);
        Assert.Equal(2600f, v.MassKg);
        Assert.Equal(107f, v.SourceLevelDb);
    }

    /// <summary>
    /// "That bus, but with the silencer taken off" — three lines, and everything else unchanged.
    ///
    /// The variation is the common case for an author, and it is the one that was impossible before:
    /// naming a preset got you the preset, and anything else meant C#.
    /// </summary>
    [Fact]
    public void AMachineCanVaryOneThatExists()
    {
        var def = MachineRegistry.FromJson("""
        {
          "id": "loud_bus",
          "name": "School bus, straight pipe",
          "base": "school_bus",
          "parts": [
            { "model": "engine", "profile": "diesel_cummins" },
            { "model": "exhaust", "at": [0, 3.1, 2.0] }
          ]
        }
        """);

        var bus = VehicleProfile.ByName("school_bus");
        var loud = MachineRegistry.Assemble(def);

        // What was said, changed.
        Assert.Equal(EngineProfile.DieselCumminsI6.Name, loud.Engine.Name);
        Assert.Equal(3.1f, loud.ExhaustHeight, 3);
        Assert.Equal(2.0f, loud.ExhaustOffsetZ, 3);
        // Everything that was not said, kept.
        Assert.Equal(bus.MassKg, loud.MassKg);
        Assert.Equal(bus.Gearbox.Ratios, loud.Gearbox.Ratios);
        Assert.Equal(bus.Tyres, loud.Tyres);
        Assert.Equal(bus.Body!.PanelSpansM, loud.Body!.PanelSpansM);
        Assert.Equal(bus.SourceLevelDb, loud.SourceLevelDb);
    }

    /// <summary>
    /// An authored machine of the same name REPLACES the built-in, and that is how a map swaps a car
    /// out of the library without editing the library.
    /// </summary>
    [Fact]
    public void AnAuthoredMachineWinsOverTheBuiltInOfTheSameName()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"openfps-machines-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "v8_sports.json"), """
            { "id": "v8_sports", "base": "v8_sports",
              "parts": [ { "model": "chassis", "settings": { "massKg": 999 } } ] }
            """);
            Assert.Equal(1, MachineRegistry.Load(dir));

            Assert.Equal(999f, MachineRegistry.VehicleFor("v8_sports").MassKg);
            // The library itself is untouched: it is an override, not an edit.
            Assert.NotEqual(999f, VehicleProfile.ByName("v8_sports").MassKg);
        }
        finally { Directory.Delete(dir, true); MachineRegistry.Clear(); }
    }

    /// <summary>One unreadable file does not take the rest of an author's folder with it.</summary>
    [Fact]
    public void ABadMachineFileDoesNotStopTheGoodOnes()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"openfps-machines-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json at all ");
            File.WriteAllText(Path.Combine(dir, "fine.json"), """
            { "id": "fine", "base": "i4_sport", "parts": [] }
            """);
            Assert.Equal(1, MachineRegistry.Load(dir));
            Assert.NotNull(MachineRegistry.Find("fine"));
        }
        finally { Directory.Delete(dir, true); MachineRegistry.Clear(); }
    }

    private static void AssertSameVehicle(VehicleProfile a, VehicleProfile b, string key)
    {
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Engine.Name, b.Engine.Name);
        Assert.Equal(a.Engine.Cylinders, b.Engine.Cylinders);
        Assert.Equal(a.Engine.RedlineRpm, b.Engine.RedlineRpm);
        Assert.Equal(a.MassKg, b.MassKg);
        Assert.Equal(a.DragArea, b.DragArea);
        Assert.Equal(a.RollingResistance, b.RollingResistance);
        Assert.Equal(a.SourceLevelDb, b.SourceLevelDb);
        Assert.Equal(a.ExhaustOffsetZ, b.ExhaustOffsetZ);
        Assert.Equal(a.ExhaustHeight, b.ExhaustHeight);
        Assert.Equal(a.IntakeOffsetZ, b.IntakeOffsetZ);
        Assert.Equal(a.IntakeHeight, b.IntakeHeight);
        Assert.Equal(a.FrontAxleZ, b.FrontAxleZ);
        Assert.Equal(a.LengthMetres, b.LengthMetres);
        Assert.Equal(a.WidthMetres, b.WidthMetres);
        Assert.Equal(a.HeightMetres, b.HeightMetres);
        Assert.Equal(a.RearAxleZ, b.RearAxleZ);
        Assert.Equal(a.Tyres, b.Tyres);
        Assert.Equal(a.Gearbox.Ratios, b.Gearbox.Ratios);
        Assert.Equal(a.Gearbox.FinalDrive, b.Gearbox.FinalDrive);
        Assert.Equal(a.Gearbox.WheelRadiusMetres, b.Gearbox.WheelRadiusMetres);
        Assert.Equal(a.Gearbox.ShiftSeconds, b.Gearbox.ShiftSeconds);
        Assert.Equal(a.Gearbox.UpshiftRpm, b.Gearbox.UpshiftRpm);
        Assert.Equal(a.Gearbox.DownshiftRpm, b.Gearbox.DownshiftRpm);
        Assert.Equal(a.Body?.PanelMaterial, b.Body?.PanelMaterial);
        Assert.Equal(a.Body?.PanelSpansM, b.Body?.PanelSpansM);
        Assert.Equal(a.Body?.PanelThicknessM, b.Body?.PanelThicknessM);
        Assert.Equal(a.Body?.PanelLoss, b.Body?.PanelLoss);
        Assert.Equal(a.Body?.Coupling, b.Body?.Coupling);
        Assert.Equal(a.Body?.CabinLengthM, b.Body?.CabinLengthM);
        Assert.Equal(a.Body?.CabinWidthM, b.Body?.CabinWidthM);
        Assert.Equal(a.Body?.CabinHeightM, b.Body?.CabinHeightM);
        Assert.Equal(a.Body?.CabinAbsorption, b.Body?.CabinAbsorption);
        Assert.Equal(a.Body?.CabinLeak, b.Body?.CabinLeak);
        Assert.Equal(a.Body?.SealLeak, b.Body?.SealLeak);
        Assert.Equal(a.Body?.WindNoiseDbAt110, b.Body?.WindNoiseDbAt110);
        Assert.Equal(a.Body?.SealedBox, b.Body?.SealedBox);
        Assert.Equal(a.Body?.MaxModes, b.Body?.MaxModes);
        Assert.True(true, key);
    }
}
