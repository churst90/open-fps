using System.Numerics;
using OpenFPS.Common.Geometry;
using static OpenFPS.Common.BodyConstants;

namespace OpenFPS.Common;

/// <summary>
/// A body on foot stepped in a physics world (docs/PHYSICS_BOUNDARY.md stage 3): the one gather of what it can
/// walk into, the grade, and <see cref="SharedMovementEngine.Step(SharedMovementEngine.MovementContext, SharedMovementEngine.Obstacles, out SharedMovementEngine.Contact)"/>.
/// The server's MovementSystem and a client's prediction both step through here, each over its own
/// <see cref="IPhysicsWorld"/>, so they gather the same boxes in the same order.
/// </summary>
public static class OnFoot
{
    /// <summary>A caller's lists, kept between steps so a step allocates nothing. One per thread that steps.</summary>
    public sealed class Scratch
    {
        internal readonly List<PhysicsBody> Bodies = new(64);
        internal readonly List<SolidRef> Solids = new(32);
        internal SharedMovementEngine.Collider[] Colliders = new SharedMovementEngine.Collider[16];
        /// <summary>The ground probe's own list (<see cref="Footing.GroundOn(IPhysicsWorld, Vector3, int, IReadOnlyList{int}, List{PhysicsBody}, out string)"/>).</summary>
        public readonly List<PhysicsBody> Ground = new(16);
        /// <summary>The boxes the last step was given, in order.</summary>
        public IReadOnlyList<PhysicsBody> Gathered => Bodies;
    }

    /// <summary>
    /// What a body can walk into this step besides the triangles: every box whose footprint comes within
    /// <see cref="CollisionSearchRadius"/> of it, not its own, by id. One gather for the server and every client.
    /// </summary>
    public static void Gather(IPhysicsWorld world, int own, Vector3 at, List<PhysicsBody> into)
    {
        world.Near(at, CollisionSearchRadius, into);
        for (int i = into.Count - 1; i >= 0; i--)
            if (into[i].Id == own) into.RemoveAt(i);
    }

    /// <summary>One step's outcome: the body after it, and what held it back (none: <see cref="Other"/> is
    /// <see cref="ContactEvent.NoOther"/> and the contact not blocked), with a second thing pressed into (in a
    /// corner, the other wall; <see cref="SecondOther"/> none when there was none).</summary>
    public readonly record struct Result(Vector3 Position, Vector3 Velocity, bool Grounded, SharedMovementEngine.Contact Contact,
                                         int Other, string OtherSurface, int SecondOther, string SecondSurface);

    /// <summary>
    /// One step of the body <paramref name="own"/>. <paramref name="ctx"/> holds everything the caller knows
    /// (where it is, its velocity, the input, the floor under it, its speed, the map's edges and gravity); the
    /// body's shape, the grade and the obstacles are worked out here from the world.
    /// </summary>
    public static Result Step(IPhysicsWorld world, int own, SharedMovementEngine.MovementContext ctx, Scratch scratch)
    {
        var bodies = scratch.Bodies;
        Gather(world, own, ctx.Position, bodies);
        if (scratch.Colliders.Length < bodies.Count) scratch.Colliders = new SharedMovementEngine.Collider[Math.Max(bodies.Count, scratch.Colliders.Length * 2)];
        var colliders = scratch.Colliders;
        for (int i = 0; i < bodies.Count; i++) colliders[i] = bodies[i].Collider;
        var boxes = new ReadOnlySpan<SharedMovementEngine.Collider>(colliders, 0, bodies.Count);

        var obstacles = new SharedMovementEngine.Obstacles(boxes);
        var solids = scratch.Solids;
        solids.Clear();
        var geo = world.Triangles;
        if (geo != null)
        {
            // On triangles the body is the capsule, and the ground has a grade (docs/GEOMETRY.md stage 2); the
            // box path keeps the cylinder on the level.
            var filter = new BodyFilter(world, own);
            ctx.Body = BodyShape.Capsule;
            ctx.Grade = SharedMovementEngine.GradeAlong(geo, ref filter, ctx.Position, ctx.InputDirection);
            SharedMovementEngine.GatherSolids(ctx, geo, ref filter, solids);
            obstacles = new SharedMovementEngine.Obstacles(boxes, geo, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids));
        }
        var (position, velocity, grounded) = SharedMovementEngine.Step(ctx, obstacles, out var contact);

        var (other, surface) = contact.Blocked ? Named(contact.ColliderIndex) : (ContactEvent.NoOther, "Generic");
        var (second, secondSurface) = contact.SecondBlocked ? Named(contact.SecondIndex) : (ContactEvent.NoOther, "Generic");
        return new Result(position, velocity, grounded, contact, other, surface, second, secondSurface);

        // What an obstacle's index names: a box's body, or a solid's owner; its surface.
        (int Id, string Surface) Named(int index)
        {
            string material = "";
            int id = ContactEvent.NoOther;
            if (index >= 0 && index < bodies.Count) { id = bodies[index].Id; material = bodies[index].Material; }
            else if (geo != null && index >= bodies.Count && index - bodies.Count < solids.Count)
            {
                var solid = solids[index - bodies.Count];
                id = geo.OwnerOf(solid);
                material = geo.SurfaceOf(solid).Material;
            }
            return (id, string.IsNullOrEmpty(material) ? "Generic" : material);
        }
    }

    /// <summary>
    /// The contact a step made, if it pressed into something: the body, what it met, its feet where they ended,
    /// the way out of the other thing, how fast the attempted move went into it and how fast the body slid
    /// along it. A body on foot exchanges no momentum with what it meets (stage 4 gives it some): the impulse
    /// is zero.
    /// </summary>
    public static ContactEvent? ContactOf(int own, string ownSurface, in Result step)
    {
        if (!step.Contact.Blocked || step.Other == ContactEvent.NoOther) return null;
        float slide = new Vector2(step.Velocity.X, step.Velocity.Z).Length();
        return new ContactEvent(own, step.Other, step.Position, step.Contact.Normal, 0f, step.Contact.IntoSpeed, slide,
                                string.IsNullOrEmpty(ownSurface) ? "Generic" : ownSurface, step.OtherSurface, ContactPhase.Began);
    }

    /// <summary>The second thing the step pressed into, as <see cref="ContactOf"/> gives the first: in a corner,
    /// the other wall.</summary>
    public static ContactEvent? SecondContactOf(int own, string ownSurface, in Result step)
    {
        if (!step.Contact.SecondBlocked || step.SecondOther == ContactEvent.NoOther) return null;
        float slide = new Vector2(step.Velocity.X, step.Velocity.Z).Length();
        return new ContactEvent(own, step.SecondOther, step.Position, step.Contact.SecondNormal, 0f, step.Contact.SecondIntoSpeed, slide,
                                string.IsNullOrEmpty(ownSurface) ? "Generic" : ownSurface, step.SecondSurface, ContactPhase.Began);
    }
}
