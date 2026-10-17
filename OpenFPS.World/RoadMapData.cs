using System.Numerics;
using System.Text.Json;

namespace OpenFPS.Common;

/// <summary>
/// What a driver's client is told about a map's roads (the MapRoads message): roads, junctions, level
/// crossings and drivable tracks. The client builds the same <see cref="RoadNetwork"/> the server's
/// traffic runs on, so the driving cues and the traffic read one description.
/// </summary>
public sealed class RoadMapData
{
    public List<RoadData> Roads { get; set; } = new();
    public List<JunctionData> Junctions { get; set; } = new();
    public List<CrossingRails> Crossings { get; set; } = new();
    public List<DriveTrack> Tracks { get; set; } = new();

    public bool IsEmpty => Roads.Count == 0 && Crossings.Count == 0 && Tracks.Count == 0;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static RoadMapData? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RoadMapData>(json, Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Where a railway crosses a road, as the rails lie: the middle of the track at the crossing, the
/// direction the rails run, and how far apart the two rails are. Derived by the server from the rail
/// line, never declared (see CrossingSystem).
/// </summary>
public sealed class CrossingRails
{
    public string Name { get; set; } = "";
    public Vector3 Centre { get; set; }
    /// <summary>A unit vector along the rails, in the ground plane.</summary>
    public Vector3 Along { get; set; } = Vector3.UnitZ;
    /// <summary>Rail centre to rail centre, metres.</summary>
    public float RailCentresMetres { get; set; } = StandardRailCentres;
    /// <summary>How far either side of the middle the crossing's planking reaches along the rails,
    /// metres: the road's half-width and a little.</summary>
    public float HalfLengthMetres { get; set; } = 8f;

    /// <summary>Rail head centre to rail head centre: standard gauge (1435 mm between the inside faces)
    /// plus one 72 mm head.</summary>
    public const float StandardRailCentres = 1.435f + 0.072f;

    /// <summary>A point on each rail's line where it crosses the road; the lines run <see cref="Along"/>.</summary>
    public (Vector3 Left, Vector3 Right) RailPoints()
    {
        var across = new Vector3(Along.Z, 0f, -Along.X);
        float h = RailCentresMetres * 0.5f;
        return (Centre - across * h, Centre + across * h);
    }
}

/// <summary>A closed track a vehicle can drive round (a speedway), as the map declares it.</summary>
public sealed class DriveTrack
{
    public string Id { get; set; } = "";
    public List<Vector3> Waypoints { get; set; } = new();
    public float WidthMetres { get; set; } = 15f;
    public float BankingDegrees { get; set; }
}
