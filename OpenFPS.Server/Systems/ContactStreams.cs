using System.Runtime.CompilerServices;
using Arch.Core;
using OpenFPS.Physics;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Each map's contacts (<see cref="ContactStream"/>), by its world: the server's tick begins the map's stream
/// before anybody moves and closes it after the last thing that moves, and the movement and the driven cars add
/// to it. By world, because each map is its own world, and a world that goes takes its stream with it.
/// </summary>
public static class ContactStreams
{
    private static readonly ConditionalWeakTable<World, ContactStream> _streams = new();

    /// <summary>The map's stream.</summary>
    public static ContactStream Of(World world) => _streams.GetValue(world, static _ => new ContactStream());
}
