using OpenFPS.Common;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Lightning, on the server: when and where the storm flashes. The thunder is the client's to work
/// out (OpenFPS.Client.AudioEngine.Core.Thunder), from the strike this hands it, for wherever each
/// listener is standing; all the server decides is the flash.
///
/// It reads the weather and nothing else: a Storm has a cell drifting with the wind and flashing a
/// few times a minute, heavy Rain now and then makes one too, and anything else is quiet. The
/// physics of it is in <see cref="LightningSchedule"/> and <see cref="LightningPhysics"/>.
/// </summary>
public sealed class LightningSystem
{
    private readonly LightningSchedule _schedule;
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
        _schedule = new LightningSchedule(seed ?? Random.Shared.Next());
    }

    /// <summary>The storm as it stands, for diagnostics.</summary>
    public LightningSchedule Schedule => _schedule;

    /// <summary>Moves the storm on and hands every flash in this step to <paramref name="strike"/>.</summary>
    public void Update(float dt, WeatherType scenario, in WorldEnvironmentComponent env, Action<LightningStrike> strike)
    {
        _strikes.Clear();
        _schedule.Advance(dt, new StormSky(scenario, env.PrecipitationIntensity, env.WindVelocity), _strikes);
        foreach (var s in _strikes)
        {
            Log.Information("Lightning: {Kind} at ({X:F0}, {Z:F0}) m from the map centre, {Strokes} stroke(s), {Energy:0} J/m",
                            s.Kind == FlashKind.CloudToGround ? "ground flash" : "cloud flash", s.Centre.X, s.Centre.Z, s.Strokes, s.EnergyPerMetre);
            strike(s);
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
