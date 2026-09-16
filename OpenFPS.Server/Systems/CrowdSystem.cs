using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Systems;

/// <summary>
/// People reacting to what goes past them.
///
/// The crowd is a SOURCE, not a bed. It has a place, so a listener can tell which way the grandstand
/// is; it has a size, so they can tell how full it is; and it only makes a noise when something
/// happens, because a crowd that is always making a noise is an ambience loop wearing a hat.
///
/// What it reacts to is deliberately not "a car": it is anything moving fast enough, close enough, to
/// be worth reacting to. A crowd that knew what a car was would need teaching about the next thing
/// somebody puts on a track.
/// </summary>
public static class CrowdSystem
{
    /// <summary>Below this a thing going past is not an event. 25 m/s is 90 km/h.</summary>
    public const float NoticeableSpeed = 25f;

    /// <summary>How much of the crowd is actually clapping. Even a good pass does not get everybody.</summary>
    public const float ParticipationFloor = 0.25f;

    private static readonly Dictionary<int, double> _lastReaction = new();

    public static void Update(string mapId, World world, Dictionary<int, Entity> lookup, double now,
                              Action<int, Vector3, CrowdApplause> react)
    {
        var crowds = new List<(int Id, Vector3 At, CrowdComponent C)>();
        world.Query(new QueryDescription().WithAll<Transform, CrowdComponent>(),
            (Entity e, ref Transform t, ref CrowdComponent c) => crowds.Add((e.Id, t.Position, c)));
        if (crowds.Count == 0) return;

        // What is going past, and how fast. Anything with a velocity counts — the crowd does not know
        // what a car is and should not have to.
        var movers = new List<(Vector3 At, float Speed)>();
        world.Query(new QueryDescription().WithAll<Transform, Velocity>(), (Entity e, ref Transform t, ref Velocity v) =>
        {
            float speed = v.Linear.Length();
            if (speed >= NoticeableSpeed) movers.Add((t.Position, speed));
        });
        if (movers.Count == 0) return;

        foreach (var (id, at, c) in crowds)
        {
            double last = _lastReaction.GetValueOrDefault(id, double.NegativeInfinity);
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

            // How worked up they are: more things at once and faster is more of an event. Both
            // saturate, because a crowd has a ceiling and it is not very high.
            float density = MathF.Min(1f, near / 6f);
            float pace = MathF.Min(1f, (fastest - NoticeableSpeed) / 45f);
            float intensity = Math.Clamp(0.25f + 0.45f * density + 0.4f * pace, 0f, 1f);

            // ...and how many of them bother. A quiet moment gets a quarter of the stand; something
            // worth watching gets most of it.
            int clapping = Math.Max(1, (int)(c.People * (ParticipationFloor + (1f - ParticipationFloor) * intensity)));

            _lastReaction[id] = now;
            react(id, at, new CrowdApplause(clapping, intensity, 2.0f + 2.5f * intensity));
        }
    }

    /// <summary>Forgets when each crowd last reacted — for a map being torn down or reloaded.</summary>
    public static void Reset() => _lastReaction.Clear();
}
