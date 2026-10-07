using System.Numerics;
using System.Text.Json;

namespace OpenFPS.Common;

/// <summary>
/// What a driver's client is told about a map's roads (the MapRoads message): the roads and junctions
/// as the map declares them, the level crossings with the line of their rails, and the tracks a
/// vehicle can drive round. The client builds the same <see cref="RoadNetwork"/> the server's traffic
/// runs on from it, so the driving cues and the traffic read one description of the road.
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

    /// <summary>
    /// Standard gauge (1435 mm between the inside faces of the rail heads) plus one 72 mm head: the
    /// distance from the middle of one rail head to the middle of the other.
    /// </summary>
    public const float StandardRailCentres = 1.435f + 0.072f;

    /// <summary>The two rails' lines across the road: a point on each, and the direction they run.</summary>
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
