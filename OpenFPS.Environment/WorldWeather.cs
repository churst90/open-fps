using System.Threading;
using OpenFPS.Physics;

namespace OpenFPS.Common;

/// <summary>
/// The weather one audio world hears: the wind over the map, the rain running off to the gutters and
/// drains, and the water over the ground. Every voice of that world reads this one, so a gust is one event
/// for the trees, the fires and the ears of the same world, and two worlds in one process (a test beside a
/// session, a lab comparing two winds) never hear each other's.
///
/// The host fills it with the conditions it knows and it fetches nothing itself: open-fps's client gives it
/// the server's broadcast (the wind ramp, the rain, the ground's water); a lab gives it numbers. Whatever
/// drives the host's weather, a simulation or observations of a real place, reaches the sound the same way.
/// The gusts are read on its world's clock (<see cref="IWorldClock.WorldTime"/>), the one time the server and
/// every client agree on.
/// </summary>
public sealed class WorldWeather
{
    /// <summary>The clock of the world this is the weather of, which whoever holds both gives it (the mixer:
    /// FmodAudioProvider.Clock). One of its own until then, which reads the shared world time.</summary>
    public IWorldClock Clock { get; set; } = new SharedWorldClock();

    /// <summary>The world time now, s (<see cref="IWorldClock.WorldTime"/>): the moment the gusts, a lit fire's
    /// age and the rain's swell are read at.</summary>
    public double Now => Clock.WorldTime;

    /// <summary>The wind: a ramp from the last state of the air to the newest (<see cref="WindWeather.Following"/>).
    /// Swapped whole, so a reader on the mixer thread never sees half of one broadcast and half of the next.</summary>
    public WindWeather Wind
    {
        get => Volatile.Read(ref _wind);
        set => Volatile.Write(ref _wind, value ?? WindWeather.Default);
    }
    private WindWeather _wind = WindWeather.Default;

    /// <summary>The air's layers near the ground (AirLayers): what bends sound over land and over water. Swapped whole, as
    /// the wind is; neutral until the host says otherwise.</summary>
    public AirLayers Layers
    {
        get => Volatile.Read(ref _layers);
        set => Volatile.Write(ref _layers, value ?? AirLayers.Neutral());
    }
    private AirLayers _layers = AirLayers.Neutral();

    /// <summary>The rain landing on roofs and streets on its way to a gutter, a drain or a downpipe. The host
    /// advances it once a frame with the rain falling (<see cref="Runoff.Update"/>).</summary>
    public Runoff Runoff { get; } = new();

    /// <summary>The water over the ground the drainage lines carry, as the server last sent it
    /// (<see cref="GroundWater.Load"/>).</summary>
    public GroundWater Ground { get; } = new();

    /// <summary>The mean wind at ten metres now, m/s. A moderate breeze — Beaufort 3, leaves and small twigs
    /// in constant motion — is 3.4 to 5.4. Setting it (the lab does) makes a steady wind with the present
    /// direction and turbulence.</summary>
    public float MeanSpeed
    {
        get => Wind.At(Now).Speed;
        set { var a = Wind.At(Now); Wind = WindWeather.Steady(value, a.FromDegrees, a.Turbulence); }
    }

    /// <summary>Where it blows FROM now, degrees clockwise from north (the way a forecast says it). Setting it
    /// makes a steady wind.</summary>
    public float FromDegrees
    {
        get => Wind.At(Now).FromDegrees;
        set { var a = Wind.At(Now); Wind = WindWeather.Steady(a.Speed, value, a.Turbulence); }
    }

    /// <summary>Standard deviation of the speed over its mean, now: about 1 / ln(z / z0) (EN 1991-1-4,
    /// turbulence factor 1), 0.24 at ten metres and 0.26 at a park tree's crown with
    /// <see cref="WindField.RoughnessMetres"/>. Not 0.3: a crown's sound goes as the wind to the 3.6, so the
    /// lulls silenced the trees and the gusts sounded like someone turning them up and down. The server's
    /// gustiness moves it a little (<see cref="WindAir.TurbulenceFor"/>). Setting it makes a steady wind.</summary>
    public float Turbulence
    {
        get => Wind.At(Now).Turbulence;
        set { var a = Wind.At(Now); Wind = WindWeather.Steady(a.Speed, a.FromDegrees, value); }
    }

    /// <summary>The wind speed at a place and moment under this weather, m/s (<see cref="WindField.SpeedAt(WindWeather, float, float, float, double)"/>).</summary>
    public float SpeedAt(float x, float heightMetres, float z, double seconds) => WindField.SpeedAt(Wind, x, heightMetres, z, seconds);
}
