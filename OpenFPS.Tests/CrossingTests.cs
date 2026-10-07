using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A level crossing's bell arrives at the client as a well-formed emitter. It rang on the server while
/// absent on the client twice: the client's physical-voice lookup dropped a "bell:" id, and the ringing flag
/// lived in the definition, sent once per entity.
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

    /// <summary>A crossing's give-way line is on a road, the crossing on the rails, and the platforms nowhere
    /// near either: a 55 m consist at a platform 20 m from a crossing blocks the road.</summary>
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
    /// Every synthesised sound id the map or its systems produce is one the client can price: LookUpPhysicalLevel
    /// returns null for an unknown prefix and the emitter is dropped silently, as "bell:" was.
    /// </summary>
    [Theory]
    [InlineData("bell:crossing_gong")]
    [InlineData("gate:crossing_gate")]
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
            "gate" => CrossingGateSpec.ByName(preset),
            _ => null,
        };
        Assert.NotNull(spec);
        _o.WriteLine($"{soundId} -> {spec}");
    }

    private static OpenFPS.Server.Repositories.MapData? CityMap()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "maps", "city.json");
        if (!System.IO.File.Exists(path)) return null;
        // The server's options: System.Text.Json binds properties and Vector3's X, Y and Z are fields, so default
        // options read every position as the origin, silently. The server registers a Vector3Converter.
        return System.Text.Json.JsonSerializer.Deserialize<OpenFPS.Server.Repositories.MapData>(
            System.IO.File.ReadAllText(path),
            OpenFPS.Server.Repositories.MapRepository.JsonOptions);
    }
}
