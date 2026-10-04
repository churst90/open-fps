using System;
using System.Collections.Generic;
using System.Numerics;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Common;

/// <summary>
/// A state-less, purely mathematical movement engine that calculates physics steps.
/// Ensures 100% deterministic parity between Client prediction and Server authority.
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
    }

    /// <summary>
    /// What held the body back this step, if anything: the collider it pushed into and was stopped
    /// by (not one it climbed as a step), the surface's outward normal, and how fast the attempted
    /// horizontal motion was going INTO it. A body sliding along a wall has a small
    /// <see cref="IntoSpeed"/>; one walking straight at it has all of its speed there. The client's
    /// wall bump reads it; nothing in the step depends on it.
    /// </summary>
    public struct Contact
    {
        public bool Blocked;
        /// <summary>Index into the colliders passed to <see cref="Step(MovementContext, ReadOnlySpan{Collider}, out Contact)"/>.</summary>
        public int ColliderIndex;
        public Vector3 Normal;
        public float IntoSpeed;
    }

    public static (Vector3 NewPosition, Vector3 NewVelocity, bool IsGrounded) Step(MovementContext ctx, ReadOnlySpan<Collider> nearbyColliders)
        => Step(ctx, nearbyColliders, out _);

    public static (Vector3 NewPosition, Vector3 NewVelocity, bool IsGrounded) Step(MovementContext ctx, ReadOnlySpan<Collider> nearbyColliders, out Contact contact)
    {
        contact = default;
        Vector3 pos = ctx.Position;
        Vector3 vel = ctx.Velocity;
        float dt = ctx.DeltaTime;

        // --- 1. VERTICAL PHYSICS & GROUNDING ---
        // Only snap to ground if we are close to it and moving downwards or stationary.
        // This prevents the "Void Snap" where a player is teleported to map bottom if ground is missing.
        bool isGrounded = false;

        // ── How far BELOW you the floor may be and still be the floor you are walking on ─────────
        //
        // A body walking off a kerb does not leave the ground. It steps down — which is the same
        // StepHeight the collision code already uses to step UP, and the asymmetry was a bug: a lip
        // of twelve centimetres put the body in the air for two ticks and then LANDED it, and a
        // landing is a heavy sound. On a map where the made ground sits proud of the dirt beside it,
        // that fires wherever a pavement ends — and, once you are standing on the boundary, the
        // five-point ground probe straddles it and flickers, so it fires again every half second for
        // as long as you stand there. Reported as "walk a few steps, stop, and for like 10 seconds,
        // periodic bangs", and heard as footsteps because a landing plays the footstep bank.
        //
        // Only for a body that is NOT already going up or down: one that has jumped, or is genuinely
        // falling, keeps the old tolerance, so walking off a roof is still walking off a roof.
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

        // ── A fall ends ON the floor, not inside it ──────────────────────────────────────────────
        //
        // The landing above only catches a body that STARTS a tick within a tenth of a metre of the
        // floor. A body falling faster than that — three metres per second at 30 Hz, which is any
        // drop of more than about half a metre — crosses the window between two ticks and arrives a
        // whole tick's fall deep in the floor. Collision then met the floor box from inside it, centre
        // within its footprint, and pushed the body out sideways to the box's NEAREST EDGE: the end of
        // the roof slab, or of the ground box the whole city stands on. Sean walked off the west side
        // of Brandt Court (2026-10-04), fell eighteen metres, and on landing was put 489 m west at the
        // edge of the map in one tick; Cody's drop onto the same roof was put 0.85 m south.
        //
        // The floor under the body is known (GroundHeight is the highest top below it), so a fall
        // that would pass through it this tick stops on it this tick.
        Vector3 moveDelta;
        bool landed = false;
        if (!isGrounded && vel.Y < 0f && ctx.GroundHeight > DefaultGroundCheckLimit
            && pos.Y >= ctx.GroundHeight && pos.Y + vel.Y * dt <= ctx.GroundHeight)
        {
            landed = true;
        }

        // --- 2. HORIZONTAL MOVEMENT ---
        Vector3 horizontalVel = ctx.InputDirection * ctx.Speed;
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

        // Collide and slide. Each pass moves by whatever is left of this frame's motion and then lifts
        // the player back out of the deepest surface they ended up inside. Removing the normal component
        // that way IS the slide — the tangential part of the move survives it — so the remainder is spent
        // and later passes start from a standstill; they exist to depenetrate a second collider that the
        // first push moved us into, and to lift a player who was already overlapping something.
        //
        // The push used to be applied to `pos` — the position BEFORE the move — using a penetration
        // measured at the position after it. The player was outside the wall to begin with, so pressing
        // into one shoved them BACKWARDS by most of a step every tick and the next tick walked them back
        // in: no net movement, 4.5 m/s of path length, footsteps that never stopped, and an acoustic
        // region that flipped back and forth at half the tick rate wherever that straddled a doorway.
        for (int i = 0; i < 3; i++)
        {
            Vector3 nextPos = pos + remainingMove;
            GeometryUtils.CollisionResult bestHit = new() { IsColliding = false };
            int bestIndex = -1;

            for (int j = 0; j < nearbyColliders.Length; j++)
            {
                var col = nearbyColliders[j];
                // Create a robust World-to-Local matrix
                Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-col.Position) * 
                                         Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(col.Rotation));
                
                // Transform current cylinder center to local space
                Vector3 localPos = Vector3.Transform(nextPos + cylinderCenterOffset, worldToLocal);
                
                var hit = GeometryUtils.GetCylinderAABBOverlap(-col.Size/2f, col.Size/2f, localPos, ctx.PlayerRadius, collisionHeight,
                                                              canGoDown: !isGrounded);
                
                if (hit.IsColliding)
                {
                    // Transform normal back to world space
                    hit.Normal = Vector3.TransformNormal(hit.Normal, Matrix4x4.CreateFromQuaternion(col.Rotation));
                    hit.Material = col.Material;

                    if (!bestHit.IsColliding || hit.Penetration > bestHit.Penetration)
                    {
                        bestHit = hit;
                        bestIndex = j;
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
                for (int j = 0; j < nearbyColliders.Length; j++)
                {
                    var col = nearbyColliders[j];
                    Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-col.Position) * 
                                             Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(col.Rotation));
                    Vector3 localStepPos = Vector3.Transform(stepTarget + cylinderCenterOffset, worldToLocal);
                    
                    if (GeometryUtils.AABBIntersectsCylinder(-col.Size/2f, col.Size/2f, localStepPos, ctx.PlayerRadius, collisionHeight))
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

            if (!stepped && bestHit.Normal.Y > 0.5f)
            {
                // A FLOOR the body came down into (see GetCylinderAABBOverlap): out the top, onto it.
                // The cylinder stops footPadding above the feet, so the feet go that much further up
                // to stand on the surface rather than a hand's breadth inside it.
                pos = nextPos + bestHit.Normal * (bestHit.Penetration + footPadding);
                remainingMove = Vector3.Zero;
                if (vel.Y < 0f) vel.Y = 0f;
                isGrounded = true;
            }
            else if (!stepped)
            {
                // Take the move, then come back out along the surface normal by the depth measured
                // THERE, plus a skin width so the next test starts clear of it.
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

        // --- 4. MAP BOUNDARY CLAMPING ---
        // Treat map edges as solid planes. 
        // We use PlayerRadius to ensure the character's volume doesn't clip out.
        float minX = ctx.MapMin.X + ctx.PlayerRadius;
        float maxX = ctx.MapMax.X - ctx.PlayerRadius;
        float minZ = ctx.MapMin.Z + ctx.PlayerRadius;
        float maxZ = ctx.MapMax.Z - ctx.PlayerRadius;

        if (pos.X < minX) { pos.X = minX; vel.X = 0; }
        if (pos.X > maxX) { pos.X = maxX; vel.X = 0; }
        if (pos.Z < minZ) { pos.Z = minZ; vel.Z = 0; }
        if (pos.Z > maxZ) { pos.Z = maxZ; vel.Z = 0; }

        // --- 5. FINAL POST-STEP GROUND CHECK ---
        if (ctx.GroundHeight > DefaultGroundCheckLimit && pos.Y < ctx.GroundHeight)
        {
            pos.Y = ctx.GroundHeight;
            if (vel.Y < 0) vel.Y = 0;
            isGrounded = true;
        }

        return (pos, vel, isGrounded);
    }
}
