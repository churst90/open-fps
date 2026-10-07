using OpenFPS.Common;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>A map that holds its own weather (world editor map settings), and its sky as its players have it.</summary>
public readonly record struct HeldSky(string MapId, WeatherType Weather, WorldEnvironmentComponent State);

/// <summary>
/// When and where the storm flashes, from the weather alone; the client works out the thunder for each
/// listener (OpenFPS.Client.AudioEngine.Core.Thunder). The physics is in <see cref="LightningSchedule"/>
/// and <see cref="LightningPhysics"/>. The server's sky has one storm for every map that follows it; a
/// map that holds a weather has a storm of its own, from that weather, or none.
/// </summary>
public sealed class LightningSystem
{
    private readonly int _seed;
    private readonly LightningSchedule _schedule;
    private readonly Dictionary<string, LightningSchedule> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LightningStrike> _strikes = new();

    /// <summary>
    /// The level a strike's sound is declared at, dB SPL at a metre, for anything that reads a world
    /// sound's level without rendering it (the birds, which go quiet at a bang). A point source this
    /// loud is the model's peak at 1-3 km (about 108-114 dB; --thunder in the AudioLab). The client
    /// does not use it: it works the level out from the channel.
    /// </summary>
    public const float DeclaredLevelDb = 175f;

    public LightningSystem(int? seed = null)
    {
        _seed = seed ?? Random.Shared.Next();
        _schedule = new LightningSchedule(_seed);
    }

    /// <summary>The storm as it stands, for diagnostics.</summary>
    public LightningSchedule Schedule => _schedule;

    /// <summary>
    /// Moves every storm on and hands each flash in this step to <paramref name="strike"/> with the map it
    /// is on: null for the server's sky (every map that does not hold a weather), or the id of a map in
    /// <paramref name="held"/>. A map that stops holding a weather loses its storm.
    /// </summary>
    public void Update(float dt, WeatherType scenario, in WorldEnvironmentComponent env, IReadOnlyList<HeldSky> held,
                       Action<LightningStrike, string?> strike)
    {
        Advance(_schedule, dt, new StormSky(scenario, env.PrecipitationIntensity, env.WindVelocity), null, strike);
        if (_held.Count > 0)
        {
            _gone.Clear();
            foreach (var mapId in _held.Keys)
                if (!Holds(held, mapId)) _gone.Add(mapId);
            foreach (var mapId in _gone) _held.Remove(mapId);
        }
        for (int i = 0; i < held.Count; i++)
        {
            var sky = held[i];
            if (!_held.TryGetValue(sky.MapId, out var schedule))
                _held[sky.MapId] = schedule = new LightningSchedule(_seed ^ StringComparer.OrdinalIgnoreCase.GetHashCode(sky.MapId));
            Advance(schedule, dt, new StormSky(sky.Weather, sky.State.PrecipitationIntensity, sky.State.WindVelocity), sky.MapId, strike);
        }
    }

    private readonly List<string> _gone = new();

    private static bool Holds(IReadOnlyList<HeldSky> held, string mapId)
    {
        for (int i = 0; i < held.Count; i++)
            if (held[i].MapId.Equals(mapId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void Advance(LightningSchedule schedule, float dt, in StormSky sky, string? mapId, Action<LightningStrike, string?> strike)
    {
        _strikes.Clear();
        schedule.Advance(dt, sky, _strikes);
        foreach (var s in _strikes)
        {
            Log.Information("Lightning: {Kind} at ({X:F0}, {Z:F0}) m from the map centre, {Strokes} stroke(s), {Energy:0} J/m, {Sky}",
                            s.Kind == FlashKind.CloudToGround ? "ground flash" : "cloud flash", s.Centre.X, s.Centre.Z, s.Strokes, s.EnergyPerMetre,
                            mapId == null ? "the server's sky" : $"the sky held on {mapId}");
            strike(s, mapId);
        }
    }

    /// <summary>The one sound a strike is on the wire: its key says everything the client needs.</summary>
    public static TransientSound SoundFor(in LightningStrike strike) => new()
    {
        Character = SoundCharacter.Knock,
        Position = strike.Centre,
        LevelDb = DeclaredLevelDb,
        // How long the thunder of a strike this far off can run: the client renders its own length.
        DecaySeconds = 30f,
        Noisiness = 1f,
        SynthKey = strike.Key(),
    };
}
