using OpenFPS.Physics;

namespace OpenFPS.Common;

/// <summary>
/// The one world time the server and every client agree on, s (WindField.Now: UTC seconds since 2026-01-01):
/// the gusts, a lit fire's age, a hob's key. Not a world's state. A world's weather reads its moment from one
/// (WorldWeather.Clock); an audio world's clock is one (IAudioClock).
/// </summary>
public interface IWorldClock
{
    /// <summary>The world time now, s.</summary>
    double WorldTime { get; }
}

/// <summary>The shared world time alone: a world's weather's clock until whoever holds it gives it its world's.</summary>
public sealed class SharedWorldClock : IWorldClock
{
    public double WorldTime => WindField.Now();
}
