using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common.Components;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Common;

/// <summary>
/// One movement step, stateless, so the client's prediction and the server's authority agree exactly.
/// The traps each rule below guards against are in docs/COMMON_NOTES.md, Walking.
/// </summary>
public static class SharedMovementEngine
{
    /// <summary>How far past the surface a resolved collision leaves the player, in metres. Enough that
    /// the next tick's overlap test starts clear of the face; small enough not to be a visible gap.</summary>
    public const float CollisionSkinWidth = 0.001f;

    public struct Collider
    {
        public Vector3 Position;
        public Vector3 Size;
        public Quaternion Rotation;
        public string Material;
    }

    public struct MovementContext
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 InputDirection;
        public float DeltaTime;
        public float GroundHeight;
        public float Gravity;
        public float JumpForce;
        public float Speed;
        public float PlayerRadius;
        public float PlayerHeight;
        public float StepHeight;
        public bool IsJumpRequested;
        public Vector3 MapMin;
        public Vector3 MapMax;
        /// <summary>What the body is: the cylinder of the box path and stage 1, or stage 2's capsule
        /// (docs/GEOMETRY.md 3.1). The triangle path walks a capsule; the box path a cylinder.</summary>
        public OpenFPS.Common.Geometry.BodyShape Body;
        /// <summary>The slope of the ground along the way the body is going, rise over run (positive up);
        /// 0 on the level. See <see cref="GradeAlong"/> and <see cref="GradeSpeed"/>.</summary>
        public float Grade;
        /// <summary>On the world (docs/WORLD_STREAMING.md, stage 2): which of its tiles are there to walk
        /// into, and their size. Null on every other map. The server's and the client's must agree.</summary>
        public System.Func<TileKey, bool>? TileReady;
        public float TileMetres;
    }

    /// <summary>
    /// What held the body back this step, if anything (not a step it climbed): the collider, its outward
    /// normal, and how fast the attempted horizontal motion went into it, small for a body sliding along
    /// a wall. The client's wall bump reads it; nothing in the step depends on it.
    /// </summary>
    public struct Contact
    {
        public bool Blocked;
        /// <summary>Index into the colliders passed to <see cref="Step(MovementContext, ReadOnlySpan{Collider}, out Contact)"/>.</summary>
        public int ColliderIndex;
        public Vector3 Normal;
        public float IntoSpeed;
        /// <summary>Stopped at the edge of a tile of the world that is not built yet.</summary>
        public bool Fenced;
    }

    public static (Vector3 NewPosition, Vector3 NewVelocity, bool IsGrounded) Step(MovementContext ctx, ReadOnlySpan<Collider> nearbyColliders)
        => Step(ctx, nearbyColliders, out _);

    public static (Vector3 NewPosition, Vector3 NewVelocity, bool IsGrounded) Step(MovementContext ctx, ReadOnlySpan<Collider> nearbyColliders, out Contact contact)
        => Step(ctx, new Obstacles(nearbyColliders), out contact);

    /// <summary>
    /// What a body can walk into this step: boxes (things that move, and every static thing on the old
    /// path) and solids of the triangle world (docs/GEOMETRY.md 3.1). An obstacle's index in
    /// <see cref="Contact.ColliderIndex"/> counts the boxes first, then the solids.
    /// </summary>
    public readonly ref struct Obstacles
    {
        public readonly ReadOnlySpan<Collider> Boxes;
        public readonly OpenFPS.Common.Geometry.TriangleWorld? World;
        public readonly ReadOnlySpan<OpenFPS.Common.Geometry.SolidRef> Solids;

        public Obstacles(ReadOnlySpan<Collider> boxes) { Boxes = boxes; World = null; Solids = default; }

        public Obstacles(ReadOnlySpan<Collider> boxes, OpenFPS.Common.Geometry.TriangleWorld? world,
                         ReadOnlySpan<OpenFPS.Common.Geometry.SolidRef> solids)
        {
            Boxes = boxes; World = world; Solids = world == null ? default : solids;
        }

        public int Count => Boxes.Length + Solids.Length;

        /// <summary>How the cylinder whose middle is at <paramref name="centre"/> overlaps obstacle
        /// <paramref name="j"/>, and the way out, in the world.</summary>
        public GeometryUtils.CollisionResult Overlap(int j, Vector3 centre, float radius, float height, bool canGoDown)
        {
            if (j < Boxes.Length)
            {
                var col = Boxes[j];
                Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-col.Position) *
                                         Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(col.Rotation));
                Vector3 localPos = Vector3.Transform(centre, worldToLocal);
                var hit = GeometryUtils.GetCylinderAABBOverlap(-col.Size / 2f, col.Size / 2f, localPos, radius, height, canGoDown);
                if (hit.IsColliding)
                {
                    hit.Normal = Vector3.TransformNormal(hit.Normal, Matrix4x4.CreateFromQuaternion(col.Rotation));
                    hit.Material = col.Material;
                }
                return hit;
            }
            var solid = Solids[j - Boxes.Length];
            var r = OpenFPS.Common.Geometry.SolidContact.CylinderOverlap(World!, solid, centre, radius, height, canGoDown);
            if (r.IsColliding) r.Material = World!.SurfaceOf(solid).Material;
            return r;
        }

        /// <summary>Whether the cylinder shares any volume with obstacle <paramref name="j"/>.</summary>
        public bool Intersects(int j, Vector3 centre, float radius, float height)
        {
            if (j < Boxes.Length)
            {
                var col = Boxes[j];
                Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-col.Position) *
                                         Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(col.Rotation));
                Vector3 localStepPos = Vector3.Transform(centre, worldToLocal);
                return GeometryUtils.AABBIntersectsCylinder(-col.Size / 2f, col.Size / 2f, localStepPos, radius, height);
            }
            return OpenFPS.Common.Geometry.SolidContact.CylinderIntersects(World!, Solids[j - Boxes.Length], centre, radius, height);
        }

        /// <summary>How deep the cylinder is in obstacle <paramref name="j"/> (0 when clear of it).</summary>
        public float Depth(int j, Vector3 centre, float radius, float height)
        {
            if (j < Boxes.Length) return SharedMovementEngine.Depth(Boxes[j], centre, radius, height);
            var r = OpenFPS.Common.Geometry.SolidContact.CylinderOverlap(World!, Solids[j - Boxes.Length], centre, radius, height);
            return r.IsColliding ? r.Penetration : 0f;
        }

        /// <summary>How a capsule body with its feet at <paramref name="feet"/> overlaps obstacle
        /// <paramref name="j"/>, and the way out (SolidContact.CapsuleOverlap): a floor lifts the feet by
        /// the penetration itself.</summary>
        public GeometryUtils.CollisionResult CapsuleOverlap(int j, Vector3 feet, in OpenFPS.Common.Geometry.SolidContact.Capsule body, bool airborne,
                                                           out float depth)
        {
            if (j < Boxes.Length)
            {
                var col = Boxes[j];
                var hit = OpenFPS.Common.Geometry.SolidContact.CapsuleOverlapBox(col.Position, col.Size, col.Rotation, feet, body, airborne, out depth);
                if (hit.IsColliding) hit.Material = col.Material;
                return hit;
            }
            var solid = Solids[j - Boxes.Length];
            var r = OpenFPS.Common.Geometry.SolidContact.CapsuleOverlap(World!, solid, feet, body, airborne, out depth);
            if (r.IsColliding) r.Material = World!.SurfaceOf(solid).Material;
            return r;
        }

        /// <summary>How deep a capsule body is in obstacle <paramref name="j"/> (0 when clear of it).</summary>
        public float CapsuleDepth(int j, Vector3 feet, in OpenFPS.Common.Geometry.SolidContact.Capsule body)
        {
            if (j < Boxes.Length)
            {
                var col = Boxes[j];
                OpenFPS.Common.Geometry.SolidContact.CapsuleOverlapBox(col.Position, col.Size, col.Rotation, feet, body, airborne: true, out float depth);
                return depth;
            }
            return OpenFPS.Common.Geometry.SolidContact.CapsuleDepth(World!, Solids[j - Boxes.Length], feet, body);
        }
    }

    /// <summary>
    /// The slope of the ground along <paramref name="direction"/>, rise over run, from the floors under
    /// the feet and a body's radius ahead and behind: the gentler half when both climb or both fall, else
    /// nothing, so a ramp or a flight reads its pitch and a lone kerb is stepped, not climbed. Static
    /// floors only, the same on the server and every client.
    /// </summary>
    public static float GradeAlong<F>(OpenFPS.Common.Geometry.TriangleWorld world, ref F filter, Vector3 feet, Vector3 direction,
                                      float reach = PlayerRadius) where F : OpenFPS.Common.Geometry.IGeometryFilter
    {
        var d = new Vector3(direction.X, 0f, direction.Z);
        float len = d.Length();
        if (len < 1e-4f) return 0f;
        d /= len;
        float top = feet.Y + StepHeight;
        var layers = OpenFPS.Common.Geometry.GeometryLayers.Ground;
        float here = world.FloorAt(feet.X, feet.Z, top, layers, ref filter, out _);
        float ahead = world.FloorAt(feet.X + d.X * reach, feet.Z + d.Z * reach, top, layers, ref filter, out _);
        float behind = world.FloorAt(feet.X - d.X * reach, feet.Z - d.Z * reach, top + StepHeight, layers, ref filter, out _);
        if (here <= -1000f || ahead <= -1000f || behind <= -1000f) return 0f;
        float up = (ahead - here) / reach, before = (here - behind) / reach;
        if (up > 0f && before > 0f) return MathF.Min(up, before);
        if (up < 0f && before < 0f) return MathF.Max(up, before);
        return 0f;
    }

    /// <summary>
    /// Walking speed on a grade against the level. Up 1 / (1 + 2g): 0.86 on a 1-in-12 ramp, 0.45 on a
    /// stair's pitch (about 0.6). Down 1.05 at a tenth, then 1.05 / (1 + 1.2 (|g| - 0.1)): 0.65 down a
    /// stair. People on stairs go up at a little under half their level speed and down at about two
    /// thirds (Fruin, Pedestrian Planning and Design, 1971); Tobler's hiking law puts the fastest walk on
    /// a slight downhill.
    /// </summary>
    public static float GradeSpeed(float grade)
    {
        if (grade >= 0f) return 1f / (1f + 2f * MathF.Min(grade, 1f));
        float a = MathF.Min(-grade, 1f);
        return a <= 0.1f ? 1f + 0.5f * a : 1.05f / (1f + 1.2f * (a - 0.1f));
    }

    /// <summary>The solids in the movement layer a body could meet this step, sorted by owner so the
    /// server and a client meet them in the same order.</summary>
    public static void GatherSolids<F>(in MovementContext ctx, OpenFPS.Common.Geometry.TriangleWorld world, ref F filter,
                                       List<OpenFPS.Common.Geometry.SolidRef> into)
        where F : OpenFPS.Common.Geometry.IGeometryFilter
    {
        into.Clear();
        // The old grid gather's reach: every 10 m cell within CollisionSearchRadius, at every height. A
        // push out of something big carries the body metres, and PushedDeeperIntoAnything sees only this
        // list; every past fix was heard with this reach.
        int cells = (int)MathF.Ceiling(CollisionSearchRadius / GatherCell);
        float cx = MathF.Floor(ctx.Position.X / GatherCell), cz = MathF.Floor(ctx.Position.Z / GatherCell);
        var min = new Vector3((cx - cells) * GatherCell, float.MinValue, (cz - cells) * GatherCell);
        var max = new Vector3((cx + cells + 1) * GatherCell, float.MaxValue, (cz + cells + 1) * GatherCell);
        // A step longer than that (it never is on foot) reaches as far as it goes.
        float dt = MathF.Max(0f, ctx.DeltaTime);
        float across = ctx.PlayerRadius + ctx.Speed * dt + MathF.Abs(ctx.Velocity.X * dt) + MathF.Abs(ctx.Velocity.Z * dt) + GatherMargin;
        min = new Vector3(MathF.Min(min.X, ctx.Position.X - across), float.MinValue, MathF.Min(min.Z, ctx.Position.Z - across));
        max = new Vector3(MathF.Max(max.X, ctx.Position.X + across), float.MaxValue, MathF.Max(max.Z, ctx.Position.Z + across));
        // The ground's prisms only round the body: they are a couple of metres across, never a push of metres.
        float fall = MathF.Abs(ctx.Velocity.Y * dt) + StepHeight + 1f;
        var tmin = new Vector3(ctx.Position.X - across, ctx.Position.Y - fall, ctx.Position.Z - across);
        var tmax = new Vector3(ctx.Position.X + across, ctx.Position.Y + ctx.PlayerHeight + fall, ctx.Position.Z + across);
        world.Overlapping(min, max, tmin, tmax, OpenFPS.Common.Geometry.GeometryLayers.Movement, ref filter, into);
        // A handful: an insertion sort, and no comparer to allocate.
        for (int i = 1; i < into.Count; i++)
        {
            var x = into[i];
            int ox = world.OwnerOf(x);
            int j = i - 1;
            while (j >= 0 && Before(world, ox, x, into[j])) { into[j + 1] = into[j]; j--; }
            into[j + 1] = x;
        }

        static bool Before(OpenFPS.Common.Geometry.TriangleWorld w, int ox, OpenFPS.Common.Geometry.SolidRef x, OpenFPS.Common.Geometry.SolidRef y)
        {
            int oy = w.OwnerOf(y);
            if (ox != oy) return ox < oy;
            return x.Instance != y.Instance ? x.Instance < y.Instance : x.Solid < y.Solid;
        }
    }

    /// <summary>How much further than the step can reach a solid is still gathered, metres.</summary>
    private const float GatherMargin = 0.5f;

    /// <summary>The cell of the grid the static gather used to walk (SpatialGrid, 10 m).</summary>
    private const float GatherCell = 10f;

    public static (Vector3 NewPosition, Vector3 NewVelocity, bool IsGrounded) Step(MovementContext ctx, Obstacles nearbyColliders, out Contact contact)
    {
        contact = default;
        Vector3 pos = ctx.Position;
        Vector3 vel = ctx.Velocity;
        float dt = ctx.DeltaTime;

        // --- 1. VERTICAL PHYSICS & GROUNDING ---
        // Snap to ground only when close to it and not rising: a missing floor must not teleport the
        // player to the map's bottom.
        bool isGrounded = false;

        // A body walking off a kerb steps down by StepHeight, as it steps up; with only 0.1 m a 12 cm lip
        // landed it, a heavy sound, every half second while standing on the edge. Not for a body already
        // jumping or falling: walking off a roof is still walking off a roof.
        float stepDown = vel.Y > -0.01f && vel.Y < 0.01f ? MathF.Max(0.1f, ctx.StepHeight) : 0.1f;
        if (pos.Y <= ctx.GroundHeight + stepDown && vel.Y <= 0.1f)
        {
            if (ctx.GroundHeight > DefaultGroundCheckLimit) // Valid ground check
            {
                isGrounded = true;
                if (vel.Y < 0) vel.Y = 0;
                pos.Y = ctx.GroundHeight;

                if (ctx.IsJumpRequested)
                {
                    vel.Y = ctx.JumpForce;
                    isGrounded = false;
                }
            }
        }
        
        if (!isGrounded)
        {
            vel.Y -= ctx.Gravity * dt;
        }

        // A fall that would pass through the floor this tick stops on it. Faster than 3 m/s (a drop of
        // about half a metre) a body arrived a tick deep in the floor, and collision pushed it out
        // sideways to the slab's nearest edge: 489 m in one tick off Brandt Court's roof (2026-10-04).
        Vector3 moveDelta;
        bool landed = false;
        if (!isGrounded && vel.Y < 0f && ctx.GroundHeight > DefaultGroundCheckLimit
            && pos.Y >= ctx.GroundHeight && pos.Y + vel.Y * dt <= ctx.GroundHeight)
        {
            landed = true;
        }

        // --- 2. HORIZONTAL MOVEMENT ---
        // The grade's speed only on the ground: a body in the air keeps the speed it has.
        float speed = ctx.Speed;
        if (isGrounded && ctx.Grade != 0f) speed *= GradeSpeed(ctx.Grade);
        Vector3 horizontalVel = ctx.InputDirection * speed;
        vel.X = horizontalVel.X;
        vel.Z = horizontalVel.Z;

        // --- 3. ITERATIVE COLLISION RESOLUTION (SLIDING) ---
        moveDelta = vel * dt;
        if (landed)
        {
            moveDelta.Y = ctx.GroundHeight - pos.Y;
            vel.Y = 0f;
            isGrounded = true;
        }
        Vector3 remainingMove = moveDelta;

        // Foot padding: We lift the collision cylinder bottom slightly to avoid hitting the floor we stand on.
        const float footPadding = 0.15f;
        float collisionHeight = ctx.PlayerHeight - footPadding;
        Vector3 cylinderCenterOffset = new Vector3(0, footPadding + (collisionHeight / 2f), 0);

        // Collide and slide: each pass takes the move, then lifts the body out of the deepest surface
        // it ended up in, along the normal; what survives is the slide. Later passes only depenetrate.
        // The push is applied to the position after the move, where its depth was measured: applied to
        // the one before, it shoved a body against a wall backwards a step every tick.
        bool pushed = false;
        bool capsule = ctx.Body == OpenFPS.Common.Geometry.BodyShape.Capsule;
        var body = new OpenFPS.Common.Geometry.SolidContact.Capsule(ctx.PlayerRadius, footPadding, ctx.PlayerHeight);
        for (int i = 0; i < 3; i++)
        {
            Vector3 nextPos = pos + remainingMove;
            GeometryUtils.CollisionResult bestHit = new() { IsColliding = false };
            int bestIndex = -1;
            float bestDepth = 0f;

            for (int j = 0; j < nearbyColliders.Count; j++)
            {
                // The deepest contact is resolved first. The cylinder's penetration is its depth; the
                // capsule's is how far it moves, which for a floor is a lift, so it ranks by depth.
                float depth = 0f;
                var hit = capsule
                    ? nearbyColliders.CapsuleOverlap(j, nextPos, body, airborne: !isGrounded, out depth)
                    : nearbyColliders.Overlap(j, nextPos + cylinderCenterOffset, ctx.PlayerRadius, collisionHeight, canGoDown: !isGrounded);
                if (!capsule) depth = hit.Penetration;
                if (hit.IsColliding)
                {
                    if (!bestHit.IsColliding || depth > bestDepth)
                    {
                        bestHit = hit;
                        bestIndex = j;
                        bestDepth = depth;
                    }
                }
            }

            if (!bestHit.IsColliding)
            {
                pos = nextPos;
                break;
            }

            // --- 3a. STEP CLIMBING ---
            // Only while actually trying to move. On a depenetration pass there is no motion to climb
            // with, and stepping then would lift a merely-overlapping player into the air.
            bool stepped = false;
            if (isGrounded && bestHit.Penetration > 0.001f && remainingMove.LengthSquared() > 0.000001f)
            {
                Vector3 stepTarget = nextPos + new Vector3(0, ctx.StepHeight, 0);
                bool stepBlocked = false;
                // Whether the body fits up there is asked of the cylinder, whatever the body: the
                // capsule's rounded bottom fits past an edge up to 0.85 m high and walked up a 56 cm ledge
                // (the parity harness, Kestrel Street's steps).
                for (int j = 0; j < nearbyColliders.Count; j++)
                {
                    if (nearbyColliders.Intersects(j, stepTarget + cylinderCenterOffset, ctx.PlayerRadius, collisionHeight))
                    {
                        stepBlocked = true;
                        break;
                    }
                }

                if (!stepBlocked)
                {
                    pos = stepTarget;
                    stepped = true;
                    break; 
                }
            }

            if (!stepped) pushed = true;
            if (!stepped && bestHit.Normal.Y > 0.5f)
            {
                // A floor the body came down into: out the top. The cylinder stops footPadding above the
                // feet, so they go that much further up; the capsule's floor says how far itself.
                pos = nextPos + bestHit.Normal * (bestHit.Penetration + (capsule ? 0f : footPadding));
                remainingMove = Vector3.Zero;
                if (vel.Y < 0f) vel.Y = 0f;
                isGrounded = true;
            }
            else if (!stepped)
            {
                pos = nextPos + bestHit.Normal * (bestHit.Penetration + CollisionSkinWidth);
                remainingMove = Vector3.Zero; // spent: the slide is what survived the push-out

                // Reported, not acted on: the hardest the attempted motion pressed into anything.
                float into = -(vel.X * bestHit.Normal.X + vel.Z * bestHit.Normal.Z);
                if (into > contact.IntoSpeed)
                    contact = new Contact { Blocked = true, ColliderIndex = bestIndex, Normal = bestHit.Normal, IntoSpeed = into };

                float velDot = Vector3.Dot(vel, bestHit.Normal);
                if (velDot < 0)
                {
                    vel.X -= bestHit.Normal.X * velDot;
                    vel.Z -= bestHit.Normal.Z * velDot;
                    // A head against a ceiling stops going up.
                    if (bestHit.Normal.Y < 0f && vel.Y > 0f) vel.Y = 0f;
                }
            }
        }

        // A step whose pushing leaves the body deeper in anything than it began stays where it stood:
        // two things pushing in turn ended odd passes inside the second, and past the middle of a 35 cm
        // parapet a push comes out of its far side, off the roof (Kestrel House, 2026-10-05). A body
        // already inside a wall at the start is still let out the way the passes say.
        if (pushed && (pos.X != ctx.Position.X || pos.Z != ctx.Position.Z)
            && (capsule ? CapsulePushedDeeper(ctx, nearbyColliders, pos, body)
                        : PushedDeeperIntoAnything(ctx, nearbyColliders, pos, cylinderCenterOffset, collisionHeight)))
        {
            pos.X = ctx.Position.X;
            pos.Z = ctx.Position.Z;
            vel.X = 0f;
            vel.Z = 0f;
        }

        // --- 4. MAP BOUNDARY CLAMPING ---
        // The map's edges are solid planes, a body's radius in.
        float minX = ctx.MapMin.X + ctx.PlayerRadius;
        float maxX = ctx.MapMax.X - ctx.PlayerRadius;
        float minZ = ctx.MapMin.Z + ctx.PlayerRadius;
        float maxZ = ctx.MapMax.Z - ctx.PlayerRadius;

        if (pos.X < minX) { pos.X = minX; vel.X = 0; }
        if (pos.X > maxX) { pos.X = maxX; vel.X = 0; }
        if (pos.Z < minZ) { pos.Z = minZ; vel.Z = 0; }
        if (pos.Z > maxZ) { pos.Z = maxZ; vel.Z = 0; }

        // The edge of a tile of the world that is not built yet (docs/WORLD_STREAMING.md, At an edge that
        // is not ready): a wall that is not a wall, a body's radius in, along whichever side it would cross.
        if (ctx.TileReady != null && ctx.TileMetres > 0f && Fence(ctx, ref pos, ref vel)) contact.Fenced = true;

        // --- 5. FINAL POST-STEP GROUND CHECK ---
        if (ctx.GroundHeight > DefaultGroundCheckLimit && pos.Y < ctx.GroundHeight)
        {
            pos.Y = ctx.GroundHeight;
            if (vel.Y < 0) vel.Y = 0;
            isGrounded = true;
        }

        return (pos, vel, isGrounded);
    }

    /// <summary>
    /// Keeps a body in tiles that are there: from the tile it starts in, a move into a neighbour that is not
    /// ready stops a body's radius short of the shared edge, on that axis only, so it slides along the
    /// edge; a body already standing in a tile that is not ready (it went while they stood there) is left
    /// to walk out. True when it stopped anything.
    /// </summary>
    private static bool Fence(in MovementContext ctx, ref Vector3 pos, ref Vector3 vel)
    {
        float t = ctx.TileMetres, r = ctx.PlayerRadius;
        var from = TileKey.Of(ctx.Position, t);
        if (!ctx.TileReady!(from)) return false;
        bool fenced = false;
        float x0 = from.X * t, x1 = x0 + t, z0 = from.Z * t, z1 = z0 + t;
        if (pos.X > x1 - r && !ctx.TileReady(new TileKey(from.X + 1, from.Z))) { pos.X = MathF.Max(ctx.Position.X, x1 - r); vel.X = 0f; fenced = true; }
        if (pos.X < x0 + r && !ctx.TileReady(new TileKey(from.X - 1, from.Z))) { pos.X = MathF.Min(ctx.Position.X, x0 + r); vel.X = 0f; fenced = true; }
        if (pos.Z > z1 - r && !ctx.TileReady(new TileKey(from.X, from.Z + 1))) { pos.Z = MathF.Max(ctx.Position.Z, z1 - r); vel.Z = 0f; fenced = true; }
        if (pos.Z < z0 + r && !ctx.TileReady(new TileKey(from.X, from.Z - 1))) { pos.Z = MathF.Min(ctx.Position.Z, z0 + r); vel.Z = 0f; fenced = true; }
        // Across a corner into a tile that is not there with both its sides open: held on both axes.
        var to = TileKey.Of(pos, t);
        if (to != from && !ctx.TileReady(to))
        {
            pos.X = Math.Clamp(pos.X, MathF.Min(ctx.Position.X, x0 + r), MathF.Max(ctx.Position.X, x1 - r));
            pos.Z = Math.Clamp(pos.Z, MathF.Min(ctx.Position.Z, z0 + r), MathF.Max(ctx.Position.Z, z1 - r));
            vel.X = vel.Z = 0f;
            fenced = true;
        }
        return fenced;
    }

    /// <summary>How much deeper than it began a step may leave the body in anything, metres: float
    /// noise and the skin, not a push.</summary>
    public const float DeeperTolerance = 0.01f;

    /// <summary>
    /// Whether the body at <paramref name="end"/> is further into any collider than it was where the
    /// step began (<see cref="MovementContext.Position"/>), by more than <see cref="DeeperTolerance"/>.
    /// </summary>
    private static bool PushedDeeperIntoAnything(in MovementContext ctx, Obstacles colliders, Vector3 end,
                                                 Vector3 cylinderCenterOffset, float collisionHeight)
    {
        for (int j = 0; j < colliders.Count; j++)
        {
            float there = colliders.Depth(j, end + cylinderCenterOffset, ctx.PlayerRadius, collisionHeight);
            if (there <= DeeperTolerance) continue;
            float before = colliders.Depth(j, ctx.Position + cylinderCenterOffset, ctx.PlayerRadius, collisionHeight);
            if (there > before + DeeperTolerance) return true;
        }
        return false;
    }

    /// <summary><see cref="PushedDeeperIntoAnything"/> for the capsule.</summary>
    private static bool CapsulePushedDeeper(in MovementContext ctx, Obstacles colliders, Vector3 end,
                                            in OpenFPS.Common.Geometry.SolidContact.Capsule body)
    {
        for (int j = 0; j < colliders.Count; j++)
        {
            float there = colliders.CapsuleDepth(j, end, body);
            if (there <= DeeperTolerance) continue;
            float before = colliders.CapsuleDepth(j, ctx.Position, body);
            if (there > before + DeeperTolerance) return true;
        }
        return false;
    }

    private static float Depth(in Collider col, Vector3 centre, float radius, float height)
    {
        var local = Vector3.Transform(centre - col.Position, Quaternion.Inverse(col.Rotation));
        var hit = GeometryUtils.GetCylinderAABBOverlap(-col.Size / 2f, col.Size / 2f, local, radius, height);
        return hit.IsColliding ? hit.Penetration : 0f;
    }

    /// <summary>
    /// How a collider stands for a walking body to meet: a cylinder (a person) upright whatever way its
    /// owner faces, as rays, bullets and sight treat it. With the look pitch in it, a player looking
    /// down tipped a 1.8 m box through whoever stood beside them (Kestrel House roof, 2026-10-05).
    /// </summary>
    public static Quaternion StandingRotation(ColliderShape shape, Quaternion rotation)
        => shape is ColliderShape.Cylinder or ColliderShape.Cone ? Quaternion.Identity : rotation;
}
