using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Systems;

/// <summary>
/// The water on each map's roads (RoadWater), advanced every tick from the map's own weather, and the
/// puddles along its kerbs (PuddleField) built once from its roads. Traffic and driven cars read the
/// water under each wheel from here; the broadcast sends the state to every client on the map, which
/// is how the grip the server drives with and the hiss a client hears agree. See docs/WET_ROADS.md.
/// </summary>
public static class RoadWaterSystem
{
    private sealed class MapWater
    {
        public readonly RoadWater Water = new();
        public PuddleField? Field;
        public RoadNetwork? Built;
        public readonly Dictionary<RoadData, int> RoadIndex = new(ReferenceEqualityComparer.Instance);
    }

    private static readonly ConcurrentDictionary<string, MapWater> _maps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Advances one map's water by <paramref name="dt"/> seconds in its weather: the rain landing on it
    /// (mm/h, zero when what falls is snow), and the air that dries it.
    /// </summary>
    public static void Update(string mapId, in WorldEnvironmentComponent env, float rainMmPerHour, RoadNetwork? roads, float dt)
    {
        var m = _maps.GetOrAdd(mapId, _ => new MapWater());
        if (!ReferenceEquals(m.Built, roads))
        {
            m.Field = roads != null ? new PuddleField(roads.Roads) : null;
            m.RoadIndex.Clear();
            if (roads != null) for (int i = 0; i < roads.Roads.Count; i++) m.RoadIndex[roads.Roads[i]] = i;
            m.Built = roads;
        }
        m.Water.Step(rainMmPerHour, Evaporation(env, m.Water.Drainage), dt);
    }

    /// <summary>What the air takes off a wet road on this map now, mm/h: Penman's equation with the
    /// wind brought from the broadcast's height to two metres by the log law (z0 = 0.03 m, 10 m to 2 m:
    /// a factor 0.75), and the sun from the hour, the day and the cloud.</summary>
    public static float Evaporation(in WorldEnvironmentComponent env, RoadDrainageSpec drainage)
    {
        float wind = new Vector2(env.WindVelocity.X, env.WindVelocity.Z).Length() * 0.75f;
        float cloud = RoadWaterLaw.CloudFrom(env.PrecipitationIntensity, env.Humidity);
        float sun = RoadWaterLaw.NetRadiationWm2(env.GameTime, env.DayOfYear, cloud, drainage.LatitudeDegrees);
        return RoadWaterLaw.EvaporationMmPerHour(env.Temperature, env.Humidity, wind, sun);
    }

    /// <summary>A map's water, or null before its first tick.</summary>
    public static RoadWater? WaterOf(string mapId) => _maps.TryGetValue(mapId, out var m) ? m.Water : null;

    /// <summary>A map's puddles, or null for a map without roads.</summary>
    public static PuddleField? FieldOf(string mapId) => _maps.TryGetValue(mapId, out var m) ? m.Field : null;

    /// <summary>
    /// The water under a wheel on a road, mm: <paramref name="along"/> the road's centreline and
    /// <paramref name="lateral"/> to the right of it.
    /// </summary>
    public static float WaterOn(string mapId, RoadData road, float along, float lateral, byte surface)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return 0f;
        float half = road.WidthMetres * 0.5f;
        if (half <= 0f) return m.Water.WaterMm(surface, 2f, float.PositiveInfinity, 0f);
        float puddle = m.Field != null && m.RoadIndex.TryGetValue(road, out int index)
            ? m.Field.PuddleMm(index, along, lateral, m.Water.PuddleFill) : 0f;
        return m.Water.WaterMm(surface, MathF.Abs(lateral), half - MathF.Abs(lateral), half, puddle);
    }

    /// <summary>The water under a wheel at a place in the world, mm: on whichever road is there, or the
    /// surface's own texture off the roads.</summary>
    public static float WaterAt(string mapId, Vector3 p, byte surface)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return 0f;
        return m.Field != null ? m.Field.WaterAt(m.Water, p, surface) : m.Water.WaterMm(surface, 2f, float.PositiveInfinity, 0f);
    }

    /// <summary>Tests: forgets every map.</summary>
    internal static void Reset() => _maps.Clear();
}
