using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using Arch.Core;

namespace OpenFPS.Server.Core;

/// <summary>/scan: what is round you, said nearest first.</summary>
public partial class CommandHandler
{
    /// <summary>
    /// What is round you, nearest first: named things within 20 m that you could actually see or hear
    /// directly, not through a wall, and measured to the nearest part of each, so a long wall beside
    /// you is not placed at its middle. Said as one line, closest first; what is behind a wall is not
    /// reported.
    /// </summary>
    private void HandleScan(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        const float scanRadius = 20.0f;
        Vector3 eye = playerPos + new Vector3(0f, 1.6f, 0f);
        var solids = new List<(Entity E, Vector3 Pos, Vector3 Size, Quaternion Rot)>();
        var results = new List<(string Name, float Distance, Vector3 Direction, Entity E)>();

        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (e.Id == session.Entity.Id) return;
            // Somebody's carried things ride on them: yours are not near you, and another player's are
            // said by saying the player (Cody, 2026-10-04: Shift+P read out every gun Sean had).
            if (world.Has<HeldComponent>(e)) return;
            // Somebody dead is said by their body, which lies where they fell: "body of sean".
            if (world.Has<DeadComponent>(e)) return;
            Vector3 size = world.Has<ColliderComponent>(e) ? world.Get<ColliderComponent>(e).Size : Vector3.Zero;
            bool solid = world.Has<ColliderComponent>(e) && world.Get<ColliderComponent>(e).IsSolid && size.X > 0f;
            if (solid && Vector3.Distance(eye, t.Position) < scanRadius + size.Length()) solids.Add((e, t.Position, size, t.Rotation));

            // A place is not a thing near you. The zone you are in was said when you walked into it, and
            // a region or the portal between two is a volume nobody can walk into (Cody, 2026-10-04:
            // a scan in a stairwell named the stairwell, and the one below it, at 0 metres). A door
            // carries a portal too, and a door is very much a thing.
            if (world.Has<RegionComponent>(e) || IsNamedPlace(world, e)) return;
            if (world.Has<PortalComponent>(e) && !world.Has<DoorComponent>(e)) return;

            string? name = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name
                         : world.Has<PlayerComponent>(e) ? world.Get<PlayerComponent>(e).Username : null;
            if (string.IsNullOrWhiteSpace(name)) return;
            // Ground is not announced: "that's what z is for" (Cody, 2026-10-04). Nor is a ceiling or a
            // roof you are under. Decided by shape, not by name.
            if (IsGroundOrOverhead(t.Position, size, t.Rotation, playerPos, eye.Y)) return;
            Vector3 nearest = NearestPointOf(t.Position, size, t.Rotation, eye);
            // The floor you are standing on is not something near you.
            if (size != Vector3.Zero && nearest.Y <= playerPos.Y + 0.3f && MathF.Abs(nearest.X - playerPos.X) < 0.3f
                && MathF.Abs(nearest.Z - playerPos.Z) < 0.3f) return;
            float dist = Vector3.Distance(eye, nearest);
            if (dist <= scanRadius) results.Add((name!, dist, dist > 0.001f ? Vector3.Normalize(nearest - eye) : Vector3.UnitZ, e));
        });

        // Each name once, at its nearest: a wall is built in pieces round its doorways, and a flight of
        // stairs is a box a step, so five nearest parts were often one wall said five times.
        var seen = results.Where(r => !Blocked(eye, r.Direction, r.Distance, r.E, solids))
                          .OrderBy(r => r.Distance)
                          .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                          .Take(5).ToList();
        if (seen.Count == 0)
        {
            Say(reply, "Nothing in sight nearby.");
            return;
        }

        var playerRotation = world.Get<Transform>(session.Entity).Rotation;
        Say(reply, string.Join(". ", seen.Select(r =>
            $"{r.Name}, {GetRelativeDirection(playerRotation, r.Direction)}, {r.Distance:F0} {(MathF.Round(r.Distance) == 1f ? "metre" : "metres")}")) + ".");
    }

    /// <summary>
    /// Whether a box is a surface you stand on or stand under rather than a thing you could walk into:
    /// a thin horizontal slab — no more than a metre thick and at least four times as wide as it is
    /// thick, and a metre wide — with its top at or below the eye (a floor, a road, a pavement, a roof
    /// you are on, a platform you could climb onto), or its underside over your head (a ceiling, a
    /// canopy). A slab at head height is neither, and is said: that is a beam you would walk into.
    /// Anything narrow, however low — a gun on the floor, a step — is a thing.
    /// </summary>
    internal static bool IsGroundOrOverhead(Vector3 centre, Vector3 size, Quaternion rot, Vector3 feet, float eyeY)
    {
        if (size == Vector3.Zero) return false;
        // Only a box that is level: turned about the vertical, or not at all.
        Vector3 up = Vector3.Transform(Vector3.UnitY, rot);
        if (MathF.Abs(up.Y) < 0.95f) return false;
        float thick = size.Y, narrow = MathF.Min(size.X, size.Z);
        if (thick > 1.0f || narrow < 1.0f || narrow < 4f * thick) return false;
        float top = centre.Y + thick * 0.5f, bottom = centre.Y - thick * 0.5f;
        return top <= eyeY || bottom >= feet.Y + PhysicsConstants.PlayerHeight;
    }

    /// <summary>The point of a turned box nearest to another point; the box's centre for a point.</summary>
    private static Vector3 NearestPointOf(Vector3 centre, Vector3 size, Quaternion rot, Vector3 from)
    {
        if (size == Vector3.Zero) return centre;
        var inv = Quaternion.Inverse(rot);
        Vector3 local = Vector3.Transform(from - centre, inv), h = size * 0.5f;
        local = Vector3.Clamp(local, -h, h);
        return centre + Vector3.Transform(local, rot);
    }

    /// <summary>Whether something solid, other than the thing itself, stands between the eye and it.</summary>
    private static bool Blocked(Vector3 eye, Vector3 dir, float dist, Entity target,
                                List<(Entity E, Vector3 Pos, Vector3 Size, Quaternion Rot)> solids)
    {
        foreach (var s in solids)
        {
            if (s.E == target) continue;
            if (GeometryUtils.RayHitsOBB(eye, dir, dist - 0.05f, s.Pos, s.Size, s.Rot, out float d, out _) && d < dist - 0.05f)
                return true;
        }
        return false;
    }
}
