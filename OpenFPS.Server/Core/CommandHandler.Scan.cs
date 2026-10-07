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
    /// Named things within 20 m in direct line, not through a wall, said in one line nearest first.
    /// Each is measured to its nearest part, so a long wall beside you is not placed at its middle.
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
            // Carried things are said by saying who carries them (Cody, 2026-10-04: Shift+P read out
            // every gun Sean had).
            if (world.Has<HeldComponent>(e)) return;
            // Somebody dead is said by their body: "body of sean".
            if (world.Has<DeadComponent>(e)) return;
            Vector3 size = world.Has<ColliderComponent>(e) ? world.Get<ColliderComponent>(e).Size : Vector3.Zero;
            bool solid = world.Has<ColliderComponent>(e) && world.Get<ColliderComponent>(e).IsSolid && size.X > 0f;
            if (solid && Vector3.Distance(eye, t.Position) < scanRadius + size.Length()) solids.Add((e, t.Position, size, t.Rotation));

            // A region, a named place or a bare portal is not a thing near you (Cody, 2026-10-04: a scan
            // in a stairwell named it, and the one below, at 0 metres). A door carries a portal and is a thing.
            if (world.Has<RegionComponent>(e) || IsNamedPlace(world, e)) return;
            if (world.Has<PortalComponent>(e) && !world.Has<DoorComponent>(e)) return;

            string? name = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name
                         : world.Has<PlayerComponent>(e) ? world.Get<PlayerComponent>(e).Username : null;
            if (string.IsNullOrWhiteSpace(name)) return;
            // Not the ground ("that's what z is for", Cody, 2026-10-04), nor a ceiling or roof over you;
            // decided by shape, not by name.
            if (IsGroundOrOverhead(t.Position, size, t.Rotation, playerPos, eye.Y)) return;
            Vector3 nearest = NearestPointOf(t.Position, size, t.Rotation, eye);
            // The floor you are standing on is not something near you.
            if (size != Vector3.Zero && nearest.Y <= playerPos.Y + 0.3f && MathF.Abs(nearest.X - playerPos.X) < 0.3f
                && MathF.Abs(nearest.Z - playerPos.Z) < 0.3f) return;
            float dist = Vector3.Distance(eye, nearest);
            if (dist <= scanRadius) results.Add((name!, dist, dist > 0.001f ? Vector3.Normalize(nearest - eye) : Vector3.UnitZ, e));
        });

        // Each name once, at its nearest: a wall is in pieces round its doorways and stairs are a box a
        // step, so five nearest parts were often one wall said five times.
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
    /// Whether a box is a surface you stand on or under rather than a thing: a level slab at most a metre
    /// thick, at least a metre and four thicknesses wide, with its top at or below the eye or its
    /// underside over your head. A slab at head height is a beam and is said; anything narrow is a thing.
    /// </summary>
    internal static bool IsGroundOrOverhead(Vector3 centre, Vector3 size, Quaternion rot, Vector3 feet, float eyeY)
    {
        if (size == Vector3.Zero) return false;
        // Level only: turned about the vertical or not at all.
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
