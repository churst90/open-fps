using System;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A level crossing's bell has to arrive at the client as a well-formed emitter, and that is a
/// separate question from whether the crossing logic works. It rang correctly on the server for two
/// rounds while being completely absent on the client — once because the client's physical-voice
/// lookup did not know a "bell:" id and dropped it, and once because the flag that says it is
/// ringing lives in the DEFINITION, which is sent once per entity and was never re-sent.
///
/// So this checks the thing the client actually receives.
/// </summary>
public class CrossingTests
{
    private readonly ITestOutputHelper _o;
    public CrossingTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void TheCityDeclaresCrossingsOnItsRailLine()
    {
        var map = CityMap();
        if (map == null) { _o.WriteLine("no city map here; skipped"); return; }
        Assert.NotNull(map.Crossings);
        Assert.NotEmpty(map.Crossings!);
        foreach (var c in map.Crossings!)
            _o.WriteLine($"{c.Name} at {c.Position}, bell '{c.Bell}', clear {c.ClearMetres} m");
    }

    /// <summary>
    /// A crossing's give-way line is on a road, the crossing is on the rails, and the platforms are
    /// nowhere near either. A 55 m consist standing at a platform 20 m from a crossing blocks the
    /// road it is meant to be clear of — which is exactly what happened when the platforms were
    /// placed at arbitrary fractions of the lap before the crossings existed.
    /// </summary>
    [Fact]
    public void PlatformsAreWellClearOfCrossings()
    {
        var map = CityMap();
        if (map?.Crossings == null || map.Tracks == null) { _o.WriteLine("no city map here; skipped"); return; }
        var rail = map.Tracks.FirstOrDefault(t => t.Id == "rail_loop");
        Assert.NotNull(rail);

        var line = new RaceLine(rail!.Waypoints, 0f, 12.5f, 1f, 0.6f, rail.BankingDegrees);
        foreach (var stop in rail.Stops.Where(s => s.Kind == "platform"))
        {
            line.Sample(stop.AtMetres, out var at, out _, out _);
            foreach (var c in map.Crossings!)
            {
                float d = System.Numerics.Vector3.Distance(
                    new System.Numerics.Vector3(at.X, 0f, at.Z),
                    new System.Numerics.Vector3(c.Position.X, 0f, c.Position.Z));
                _o.WriteLine($"platform at {stop.AtMetres:F0} m is {d:F0} m from {c.Name}");
                Assert.True(d > 120f,
                    $"a platform at {stop.AtMetres:F0} m is only {d:F0} m from {c.Name} — "
                  + "a train standing there blocks the crossing it is supposed to clear");
            }
        }
    }

    /// <summary>
    /// Every synthesised sound id the map or its systems produce is one the CLIENT can price.
    ///
    /// The client's LookUpPhysicalLevel returns null for a prefix it does not recognise, and null
    /// means the emitter is dropped by both the voice ranking and the submit path — silently, and
    /// identically to a sound that simply never plays. A new kind of source is therefore invisible
    /// until someone adds a line there, which is what happened to "bell:".
    /// </summary>
    [Theory]
    [InlineData("bell:crossing_gong")]
    [InlineData("machine:ac_window")]
    [InlineData("aircraft:airliner")]
    public void TheClientCanPriceEverySynthPrefix(string soundId)
    {
        AcousticRegistry.Initialize();
        int colon = soundId.IndexOf(':');
        string kind = soundId[..colon], preset = soundId[(colon + 1)..];
        object? spec = kind switch
        {
            "bell" => ModelLibrary.Bell(preset),
            "machine" => SmallMachineSpec.ByName(preset),
            "aircraft" => AircraftProfile.ByName(preset),
            _ => null,
        };
        Assert.NotNull(spec);
        _o.WriteLine($"{soundId} -> {spec}");
    }

    private static OpenFPS.Server.Repositories.MapData? CityMap()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "maps", "city.json");
        if (!System.IO.File.Exists(path)) return null;
        // The SERVER'S options, not fresh ones.
        //
        // System.Text.Json binds PROPERTIES, and Vector3's X, Y and Z are FIELDS — so a map read
        // with default options comes back with every position at the origin, silently. The first
        // run of this reported both crossings at <0,0,0> and a platform "0 m" from one of them,
        // which looked exactly like a map-authoring fault and was a test-harness fault. The server
        // registers a Vector3Converter; read the file the way the thing under test reads it.
        return System.Text.Json.JsonSerializer.Deserialize<OpenFPS.Server.Repositories.MapData>(
            System.IO.File.ReadAllText(path),
            OpenFPS.Server.Repositories.MapRepository.JsonOptions);
    }
}
