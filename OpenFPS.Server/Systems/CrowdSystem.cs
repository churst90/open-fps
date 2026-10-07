using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Crowds reacting to what goes past them. A crowd is a source, not a bed: it has a place and a size,
/// and it only makes a noise when something happens. It reacts to anything with a velocity that is
/// fast and close enough, not to "a car".
/// </summary>
public static class CrowdSystem
{
    /// <summary>Below this a thing going past is not an event. 25 m/s is 90 km/h.</summary>
    public const float NoticeableSpeed = 25f;

    /// <summary>The least share of a crowd that claps at anything it reacts to.</summary>
    public const float ParticipationFloor = 0.25f;

    /// <summary>When each crowd last reacted, by map and entity id: every map is its own world, and
    /// entity ids repeat between them.</summary>
    private static readonly Dictionary<(string Map, int Id), double> _lastReaction = new();

    public static void Update(string mapId, World world, double now,
                              Action<int, Vector3, CrowdApplause> react)
    {
        var crowds = new List<(int Id, Vector3 At, CrowdComponent C)>();
        world.Query(new QueryDescription().WithAll<Transform, CrowdComponent>(),
            (Entity e, ref Transform t, ref CrowdComponent c) => crowds.Add((e.Id, t.Position, c)));
        if (crowds.Count == 0) return;

        var movers = new List<(Vector3 At, float Speed)>();
        world.Query(new QueryDescription().WithAll<Transform, Velocity>(), (Entity e, ref Transform t, ref Velocity v) =>
        {
            float speed = v.Linear.Length();
            if (speed >= NoticeableSpeed) movers.Add((t.Position, speed));
        });
        if (movers.Count == 0) return;

        foreach (var (id, at, c) in crowds)
        {
            double last = _lastReaction.GetValueOrDefault((mapId, id), double.NegativeInfinity);
            if (now - last < Math.Max(1f, c.CooldownSeconds)) continue;

            float radius = MathF.Max(5f, c.ReactRadiusMetres);
            int near = 0;
            float fastest = 0f;
            foreach (var (mAt, speed) in movers)
            {
                if (Vector3.DistanceSquared(mAt, at) > radius * radius) continue;
                near++;
                fastest = MathF.Max(fastest, speed);
            }
            if (near == 0) continue;

            // More things at once and faster is more of an event; both saturate.
            float density = MathF.Min(1f, near / 6f);
            float pace = MathF.Min(1f, (fastest - NoticeableSpeed) / 45f);
            float intensity = Math.Clamp(0.25f + 0.45f * density + 0.4f * pace, 0f, 1f);

            int clapping = Math.Max(1, (int)(c.People * (ParticipationFloor + (1f - ParticipationFloor) * intensity)));

            _lastReaction[(mapId, id)] = now;
            // Quantised: a rendered crowd is a cached buffer, and unquantised every reaction on the
            // speedway was a fresh render registered with the mixer, ninety a minute, for ever.
            react(id, at, Applause.Quantise(new CrowdApplause(clapping, intensity, 2.0f + 2.5f * intensity)));
        }
    }

    /// <summary>Forgets when each crowd last reacted, for a map torn down or reloaded.</summary>
    public static void Reset() => _lastReaction.Clear();
}
