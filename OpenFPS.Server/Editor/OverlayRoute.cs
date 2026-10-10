using System.Numerics;
using System.Text.Json.Serialization;

namespace OpenFPS.Server.Editor;

/// <summary>A station on a railway laid with the editor: where the trains stop, metres round from its first point.</summary>
public sealed class OverlayStation
{
    public string Name { get; set; } = "";
    public float AtMetres { get; set; }
}

/// <summary>A level crossing on a railway laid with the editor: where a road crosses it.</summary>
public sealed class OverlayCrossing
{
    public string Name { get; set; } = "";
    public Vector3 Position { get; set; }
}

/// <summary>
/// A road, a path or a railway laid with the editor (docs/WORLD_EDITOR.md section 18): the line it runs
/// along, on the ground, and what it is. At load it becomes the map's own data, a RoadData for a road and
/// a TrackData (with its stations, crossings and train) for a railway, so traffic and trains use it
/// through the code they always have. What it is made of underfoot is ordinary things the editor placed
/// (<see cref="Pieces"/>), listed with everything else placed.
/// </summary>
public sealed class OverlayRoute
{
    public const string Road = "road", Path = "path", Rail = "railway";
    public const string OnTheGround = "ground", Raised = "raised", Underground = "underground";

    /// <summary>Its id in the map's data: a road's, a track's. Unique on the map.</summary>
    public string Id { get; set; } = "";
    public string Kind { get; set; } = Road;
    public string Name { get; set; } = "";
    /// <summary>Where it runs, in order, at the ground (where a person laying it stood). A railway is a loop:
    /// the last point joins the first.</summary>
    public List<Vector3> Points { get; set; } = new();
    public float WidthMetres { get; set; }
    /// <summary>What its surface is made of: a material the acoustic registry knows ("Asphalt").</summary>
    public string Surface { get; set; } = "Asphalt";
    /// <summary>A railway's level: on the ground, raised on pillars, or underground in a tunnel of its own.</summary>
    public string Level { get; set; } = OnTheGround;
    /// <summary>How high a raised line's deck is over the ground, or how deep an underground one's floor is under it, metres.</summary>
    public float LevelMetres { get; set; }
    /// <summary>A road's speed limit, or a railway's trains' top speed, km/h.</summary>
    public float SpeedKmh { get; set; }
    public List<OverlayStation>? Stations { get; set; }
    public List<OverlayCrossing>? Crossings { get; set; }
    /// <summary>The train that runs a railway (a TrainProfile preset), or null for none.</summary>
    public string? Train { get; set; }
    /// <summary>The things placed for it (the editor's additions): its surface, deck, pillars, tunnel, platforms.</summary>
    public List<int> Pieces { get; set; } = new();
    public string? LaidBy { get; set; }
    public DateTime? LaidAt { get; set; }

    [JsonIgnore] public bool IsRail => Kind == Rail;
}
